// Applies the real CalamityMod 2.2.4 On_/IL_ registrations to the woven tModLoader (ManagedHooks backend), by reflection only.
// Mirrors tools/CalamityHookTest. Does not run Calamity's Load, content loading or any game code.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

public static class CalamityHooks {
    public static string[] Dirs = Array.Empty<string>();
    static bool resolver;
    public static bool Available => Dirs.Any(d => File.Exists(Path.Combine(d, "CalamityMod.dll")));

    public class LogRecorder : DispatchProxy {
        public static readonly List<string> Lines = new();
        protected override object? Invoke(MethodInfo? m, object?[]? a) {
            if (m == null) return null;
            if (m.ReturnType == typeof(bool)) return true;
            if (a is { Length: > 0 } && (m.Name.StartsWith("Warn") || m.Name.StartsWith("Error") || m.Name.StartsWith("Fatal")))
                lock (Lines) Lines.Add(m.Name + ": " + string.Join(" ", a.Select(x => x?.ToString())));
            return m.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null;
        }
    }
    record Site(string Caller, string Accessor, string AccessorType, string Kind, int Token, string Manip, string Receiver);

    public static string Run() {
        if (!resolver) {
            resolver = true;
            AssemblyLoadContext.Default.Resolving += (ctx, name) => {
                foreach (var c in AssemblyLoadContext.All) foreach (var a in c.Assemblies) if (a.GetName().Name == name.Name) return a;
                foreach (var d in Dirs) { var p = Path.Combine(d, name.Name + ".dll"); if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p); }
                return null;
            };
        }
        var sw = Stopwatch.StartNew(); var phases = new List<string>();
        void Phase(string n) { phases.Add($"{n} {sw.Elapsed.TotalMilliseconds:F0}ms"); }
        string Find(string f) => Dirs.Select(d => Path.Combine(d, f)).First(File.Exists);
        var calPath = Find("CalamityMod.dll");
        var tml = AssemblyLoadContext.Default.LoadFromAssemblyPath(Find("tModLoader.dll"));
        var hooks = AssemblyLoadContext.Default.LoadFromAssemblyPath(Find("TerrariaHooks.dll"));
        AssemblyLoadContext.Default.LoadFromAssemblyPath(Find("ManagedHooks.dll"));
        var cal = AssemblyLoadContext.Default.LoadFromAssemblyPath(calPath);
        Phase("load");
        var sites = Scan(calPath); Phase($"scan({sites.Count} sites)");

        string logger = "recorder";
        try {
            var calType = cal.GetType("CalamityMod.CalamityMod", true)!;
            var inst = RuntimeHelpers.GetUninitializedObject(calType);
            var ilog = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "log4net")?.GetType("log4net.ILog") ?? Assembly.Load("log4net").GetType("log4net.ILog", true)!;
            var proxy = typeof(DispatchProxy).GetMethod("Create", 2, Type.EmptyTypes)!.MakeGenericMethod(ilog, typeof(LogRecorder)).Invoke(null, null);
            calType.BaseType!.GetProperty("Logger", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(inst, proxy);
            calType.GetField("_Instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, inst);
        } catch (Exception e) { logger = "UNAVAILABLE " + Unwrap(e).GetType().Name + ": " + Unwrap(e).Message; }
        Phase("logger");

        var errors = new List<string>(); int ok = 0;
        foreach (var s in sites) {
            try {
                var manip = (MethodInfo)cal.ManifestModule.ResolveMethod(s.Token)!;
                var accType = s.Kind == "Batch" ? cal.GetType(s.AccessorType, true)! : hooks.GetType(s.AccessorType, true)!;
                var add = accType.GetMethod("add_" + s.Accessor, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
                var delType = add.GetParameters()[0].ParameterType;
                object? target = s.Receiver switch {
                    "static" => null,
                    "lambdaCache" => manip.DeclaringType!.GetField("<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null),
                    _ => RuntimeHelpers.GetUninitializedObject(manip.DeclaringType!),
                };
                var del = target == null ? Delegate.CreateDelegate(delType, manip) : Delegate.CreateDelegate(delType, target, manip);
                add.Invoke(null, new object[] { del }); ok++;
            } catch (Exception e) { errors.Add($"{s.Kind} {s.AccessorType}.{s.Accessor}: {Describe(e)}"); }
        }
        Phase("direct sites");
        try { var mm = cal.GetType("CalamityMod.ILEditing.ManipulatorManager", true)!; mm.GetMethod("OnModLoad")!.Invoke(RuntimeHelpers.GetUninitializedObject(mm), null); ok++; }
        catch (Exception e) { errors.Add("Batch ManipulatorManager.OnModLoad: " + Describe(e)); }
        Phase("batched edits (OnModLoad)");

        int woven = 0, active = 0; var inactive = new List<string>();
        // Find holder types via metadata (Mono's GetTypes() throws FileNotFoundException if any tML dependency is absent).
        var holderNames = new List<string>();
        using (var tmod = Mono.Cecil.ModuleDefinition.ReadModule(Find("tModLoader.dll")))
            foreach (var t in All(tmod.Types)) if (t.Name == "__mh_chains") holderNames.Add(TypeName(t));
        var holders = new List<Type>();
        foreach (var n in holderNames) { try { var h = tml.GetType(n, false); if (h != null) holders.Add(h); else inactive.Add("unresolved holder " + n); } catch (Exception e) { inactive.Add("holder " + n + ": " + Unwrap(e).GetType().Name); } }
        foreach (var h in holders)
            foreach (var f in h.GetFields(BindingFlags.Public | BindingFlags.Static)) { woven++; if (f.GetValue(null) != null) active++; else inactive.Add(h.DeclaringType!.Name + "." + f.Name.Replace("__mh_chain_", "")); }
        Phase("verify");

        var summary = $"activeChains={active}/{woven}; sitesOk={ok}/{sites.Count + 1}; calamityIlFailureLogs={LogRecorder.Lines.Count}; logger={logger}; " +
                      $"DMD IsDynamicILAvailable={MonoMod.Utils.DynamicMethodDefinition.IsDynamicILAvailable}\n" +
                      "phases: " + string.Join(", ", phases) + "\ninactive: " + string.Join(", ", inactive) +
                      "\nlogs:\n  " + string.Join("\n  ", LogRecorder.Lines.Take(20)) + "\nerrors:\n  " + string.Join("\n  ", errors.Take(20));
        bool expected = errors.All(e => e.Contains("Main..cctor") || e.Contains("SunkenSeaBiome")); // known harness artifact (game not initialised)
        if (LogRecorder.Lines.Count > 0 || !expected || active < woven - 2) throw new Exception("Calamity hook application incomplete:\n" + summary);
        return summary;
    }
    static Exception Unwrap(Exception e) { while (e is TargetInvocationException { InnerException: not null } || e is TypeInitializationException { InnerException: not null }) e = e.InnerException!; return e; }
    static string Describe(Exception e) { e = Unwrap(e); return e.GetType().Name + ": " + e.Message.Split('\n')[0] + " @ " + string.Join(" < ", (e.StackTrace ?? "").Split('\n').Take(4).Select(x => x.Trim())); }

    static List<Site> Scan(string path) {
        using var mod = Mono.Cecil.ModuleDefinition.ReadModule(path);
        var list = new List<Site>();
        foreach (var t in All(mod.Types)) foreach (var m in t.Methods.Where(m => m.HasBody)) {
            var ins = m.Body.Instructions;
            for (int i = 0; i < ins.Count; i++) {
                if (ins[i].Operand is not Mono.Cecil.MethodReference r || !r.Name.StartsWith("add_")) continue;
                string? kind;
                if (r.DeclaringType.FullName == "CalamityMod.ILEditing.ManipulatorManager" && r.Name == "add_ApplyEdits") kind = "Batch";
                else {
                    if (!r.DeclaringType.Scope.Name.StartsWith("TerrariaHooks")) continue;
                    var top = r.DeclaringType; while (top.DeclaringType != null) top = top.DeclaringType;
                    kind = top.Name.StartsWith("IL_") ? "IL" : top.Name.StartsWith("On_") ? "On" : null; if (kind == null) continue;
                    if (m.DeclaringType.FullName.StartsWith("CalamityMod.ILEditing.ManipulatorManager")) continue;
                }
                int j = i - 1; while (j >= 0 && i - j < 16 && ins[j].OpCode.Code != Mono.Cecil.Cil.Code.Newobj) j--;
                int f = j - 1; while (f >= 0 && j - f < 4 && ins[f].OpCode.Code != Mono.Cecil.Cil.Code.Ldftn && ins[f].OpCode.Code != Mono.Cecil.Cil.Code.Ldvirtftn) f--;
                if (j < 0 || f < 0 || ins[f].Operand is not Mono.Cecil.MethodReference mr) continue;
                var md = mr.Resolve(); var prev = f > 0 ? ins[f - 1] : null;
                string recv = md.IsStatic ? "static" : prev?.OpCode.Code == Mono.Cecil.Cil.Code.Ldsfld && ((Mono.Cecil.FieldReference)prev.Operand).Name == "<>9" ? "lambdaCache" : "instance";
                list.Add(new(m.FullName, r.Name[4..], TypeName(r.DeclaringType), kind, md.MetadataToken.ToInt32(), md.FullName, recv));
            }
        }
        var batchHandlers = list.Where(x => x.Kind == "Batch").Select(x => x.Manip).ToHashSet();
        return list.Where(x => x.Kind == "Batch" || !batchHandlers.Contains(x.Caller)).ToList();
    }
    static string TypeName(Mono.Cecil.TypeReference t) => t.DeclaringType == null ? t.FullName : TypeName(t.DeclaringType) + "+" + t.Name;
    static IEnumerable<Mono.Cecil.TypeDefinition> All(IEnumerable<Mono.Cecil.TypeDefinition> ts) { foreach (var t in ts) { yield return t; foreach (var n in All(t.NestedTypes)) yield return n; } }
}
