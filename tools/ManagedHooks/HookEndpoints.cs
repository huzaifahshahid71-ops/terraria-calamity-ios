// Managed replacement for MonoMod.RuntimeDetour.HookGen.HookEndpointManager on JIT-less iOS.
// The weaver redirects TerrariaHooks' HookEndpointManager.Add/Remove/Modify/Unmodify calls here.
// Woven targets expose, in their declaring type:
//   __mh_orig_{name}_{k}   original body (same signature, non-virtual)
//   __mh_bridge_{name}_{k} static (self?, args...) -> orig
//   __mh_chains.__mh_chain_{name}_{k}  static field (nested holder, no .cctor) of delegate type __mh_chains.__mh_d_{name}_{k}
// The original method body is a stub that calls the chain field when non-null.
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using MonoMod.Cil;
using MonoMod.Utils;

namespace ManagedHooks;

public static class HookEndpoints {
    sealed class Slot {
        public required MethodInfo Orig, Bridge; public required FieldInfo Chain; public required Type ChainType;
        public readonly List<Delegate> On = new(); public readonly List<Delegate> IL = new();
        public (DynamicMethodDefinition dmd, ILContext ctx)? Live;
    }
    static readonly object Gate = new();
    static readonly Dictionary<MethodBase, Slot> Slots = new();
    public static Action<string>? Log;
    public static int BakedUsed;

    static HookEndpoints() {
        if (Environment.GetEnvironmentVariable("TP_HOOK_LOG") == "1") Log = s => Console.WriteLine(s);
        // iOS: generate IL-edited bodies as DynamicMethods (no temporary assemblies that could pull a second copy of a mod
        // assembly into the default load context). Proven on device in the runtime diagnostic (v6).
        if (OperatingSystem.IsIOS() && Environment.GetEnvironmentVariable("MONOMOD_DMDType") == null) MonoMod.Switches.SetSwitchValue("DMDType", "dm");
        // MonoMod's Cecil DMD generator (preferred on .NET 10 Mono) resolves references by name from the default context.
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            foreach (var c in AssemblyLoadContext.All) foreach (var a in c.Assemblies) if (a.GetName().Name == name.Name) return a;
            return null;
        };
    }

    static void AddOnce(List<Delegate> list, Delegate d) {
        if (list.Contains(d)) throw new ArgumentException("Delegate has already been applied to this method as a hook!");
        list.Add(d);
    }
    public static void Add<T>(MethodBase method, Delegate hookDelegate) where T : Delegate => Change(method, s => AddOnce(s.On, hookDelegate));
    public static void Remove<T>(MethodBase method, Delegate hookDelegate) where T : Delegate => Change(method, s => s.On.Remove(hookDelegate));
    public static void Modify<T>(MethodBase method, Delegate callback) where T : Delegate => Change(method, s => AddOnce(s.IL, callback));
    public static void Unmodify<T>(MethodBase method, Delegate callback) where T : Delegate => Change(method, s => s.IL.Remove(callback));
    // Untyped forms for MonoModHooks.Add/Modify-style callers.
    public static void AddUntyped(MethodBase method, Delegate hook) => Change(method, s => AddOnce(s.On, hook));
    public static void ModifyUntyped(MethodBase method, Delegate manipulator) => Change(method, s => AddOnce(s.IL, manipulator));
    public static bool IsWoven(MethodBase method) { lock (Gate) return TryFind(method) != null; }
    public static void Clear() { lock (Gate) foreach (var s in Slots.Values) { s.On.Clear(); s.IL.Clear(); Rebuild(s); } }

    static void Change(MethodBase method, Action<Slot> edit) {
        if (method == null) throw new ArgumentNullException(nameof(method));
        lock (Gate) {
            var slot = TryFind(method) ?? throw new NotSupportedException(
                $"ManagedHooks: {method.DeclaringType?.FullName}::{method} was not prewoven; native detours are unavailable on this runtime. Add it to the weave list.");
            edit(slot); Rebuild(slot);
        }
    }

    static Slot? TryFind(MethodBase m) {
        if (Slots.TryGetValue(m, out var s)) return s;
        var t = m.DeclaringType!; var want = m.GetParameters().Select(p => p.ParameterType).ToArray();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var o in t.GetMethods(all)) {
            if (!o.Name.StartsWith("__mh_orig_" + m.Name + "_", StringComparison.Ordinal) || o.IsStatic != m.IsStatic) continue;
            if (!o.GetParameters().Select(p => p.ParameterType).SequenceEqual(want)) continue;
            var k = o.Name.Substring(("__mh_orig_" + m.Name + "_").Length);
            if (k.Contains('_')) continue; // a method whose own name begins with m.Name + "_"
            var holder = t.GetNestedType("__mh_chains", BindingFlags.Public | BindingFlags.NonPublic)!;
            var chain = holder.GetField($"__mh_chain_{m.Name}_{k}", all)!;
            var bridge = t.GetMethod($"__mh_bridge_{m.Name}_{k}", all)!;
            return Slots[m] = new Slot { Orig = o, Bridge = bridge, Chain = chain, ChainType = chain.FieldType };
        }
        return null;
    }

    static void Rebuild(Slot s) {
        // Unhook, then release the previous DMD/ILContext BEFORE building a new one: MonoMod shares the cached Cecil
        // definition while a DMD is alive, so a live predecessor would make the new edit run on already-edited IL.
        s.Chain.SetValue(null, null);
        if (s.Live is { } old) { old.ctx.Dispose(); old.dmd.Dispose(); s.Live = null; }
        if (s.On.Count == 0 && s.IL.Count == 0) return;
        Delegate cur; bool usedBaked = false;
        if (s.IL.Count > 0) {
            var dmd = new DynamicMethodDefinition(s.Orig);
            var ctx = new ILContext(dmd.Definition); // kept alive: owns EmitDelegate closure cells
            foreach (var m in s.IL) ctx.Invoke((ILContext.Manipulator)m);
            NormalizeOperands(dmd.Definition.Body);
            if (Baked.Baking) Baked.Capture(s.Orig, s.IL, dmd.Definition);
            Delegate? baked = null;
            try { baked = Baked.TryUse(s.Orig, s.ChainType, s.IL, dmd.Definition.Body, Log); } catch (Exception e) { Log?.Invoke($"ManagedHooks: baked {s.Orig.Name} failed: {e.Message}"); }
            cur = baked ?? dmd.Generate().CreateDelegate(s.ChainType); s.Live = (dmd, ctx);
            if (baked != null) { BakedUsed++; usedBaked = true; }
        } else cur = s.Bridge.CreateDelegate(s.ChainType);
        foreach (var h in s.On) cur = Link(s, h, cur);
        s.Chain.SetValue(null, cur);
        Log?.Invoke($"ManagedHooks: {s.Orig.DeclaringType}::{s.Orig.Name} on={s.On.Count} il={s.IL.Count}{(usedBaked ? " baked" : "")}");
    }

    // Manipulators often set ldc.i4.s operands to an int; the DynamicMethod generator tolerates it, Cecil's writer does not.
    static void NormalizeOperands(Mono.Cecil.Cil.MethodBody body) {
        foreach (var i in body.Instructions) {
            if (i.OpCode.OperandType == Mono.Cecil.Cil.OperandType.ShortInlineI && i.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_S && i.Operand is int v) {
                if (v >= sbyte.MinValue && v <= sbyte.MaxValue) i.Operand = (sbyte)v; else i.OpCode = Mono.Cecil.Cil.OpCodes.Ldc_I4;
            } else if (i.OpCode.OperandType == Mono.Cecil.Cil.OperandType.InlineI && i.Operand is sbyte sb) i.Operand = (int)sb;
            else if (i.OpCode.OperandType == Mono.Cecil.Cil.OperandType.InlineI && i.Operand is byte b) i.Operand = (int)b;
        }
    }

    // Build a chain-typed delegate that calls hook(orig, args...), with orig = 'next' viewed as the hook's orig delegate type.
    static Delegate Link(Slot s, Delegate hook, Delegate next) {
        var hookInvoke = hook.GetType().GetMethod("Invoke")!;
        var hp = hookInvoke.GetParameters();
        var origType = hp[0].ParameterType;
        var origView = origType == next.GetType() ? next : Delegate.CreateDelegate(origType, next, next.GetType().GetMethod("Invoke")!);
        var chainInvoke = s.ChainType.GetMethod("Invoke")!;
        var cp = chainInvoke.GetParameters().Select(p => p.ParameterType).ToArray();
        var dm = new DynamicMethod("__mh_link", chainInvoke.ReturnType, new[] { typeof(object[]) }.Concat(cp).ToArray(), typeof(HookEndpoints).Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Castclass, hook.GetType());
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldelem_Ref); il.Emit(OpCodes.Castclass, origType);
        for (int i = 0; i < cp.Length; i++) il.Emit(OpCodes.Ldarg, (short)(i + 1));
        il.Emit(OpCodes.Callvirt, hookInvoke); il.Emit(OpCodes.Ret);
        return dm.CreateDelegate(s.ChainType, new object[] { hook, origView });
    }
}
