// Build-time IL edits ("baking") for JIT-less iOS.
//
// Mac (TP_BAKE_OUT set): after a target's IL manipulators ran, the edited body is copied into a Cecil copy of the
// woven tModLoader.dll as a static method __mh_baked_{name}_{k}(self?, args...), next to:
//   __mh_bakedcells_{name}_{k}  object[]  - MonoMod reference cells (EmitDelegate/EmitReference objects), filled at runtime
//   __mh_bakedinfo_{name}_{k}   const string "manipulator ids|IL hash|cell count"
// The phone AOT-compiles that method. At runtime the manipulators still run on an ILContext (so closures, cells and side
// effects are real), but when ids + IL hash match, the AOT body is used instead of an interpreted DynamicMethod.
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace ManagedHooks;

static class Baked {
    // ---------- shared ----------
    public static string ManipulatorId(Delegate d) => $"{d.Method.DeclaringType?.FullName}::{d.Method.Name}";
    public static string Ids(IEnumerable<Delegate> il) => string.Join(";", il.Select(ManipulatorId));

    static bool IsCellLoad(Instruction i) => i.OpCode.Code is Code.Call && i.Operand is MethodReference m
        && m.DeclaringType?.FullName == "MonoMod.Utils.DynamicReferenceManager" && m.Name.StartsWith("GetValue", StringComparison.Ordinal);

    // Hash of the final IL shape (opcodes + operand kinds/names), independent of cell ids.
    public static string Hash(Mono.Cecil.Cil.MethodBody body) {
        ulong h = 1469598103934665603UL;
        void Mix(string s) { foreach (var c in s) { h ^= c; h *= 1099511628211UL; } h ^= 0xff; h *= 1099511628211UL; }
        var ins = body.Instructions; var index = new Dictionary<Instruction, int>(ins.Count);
        for (int n = 0; n < ins.Count; n++) index[ins[n]] = n;
        for (int n = 0; n < ins.Count; n++) {
            var i = ins[n]; Mix(i.OpCode.Name);
            switch (i.Operand) {
                case MemberReference m: Mix(m.Name); break;
                case string s: Mix(s); break;
                case Instruction t: Mix(index.TryGetValue(t, out var ti) ? ti.ToString() : "?"); break;
            }
        }
        Mix(body.Variables.Count.ToString()); Mix(body.ExceptionHandlers.Count.ToString());
        return h.ToString("x16");
    }

    // The objects behind the MonoMod reference cells, in IL order. Each cell load is ldc.i4 a; ldc.i4 b; call GetValueT<T>(a,b)
    // (or a single-int variant); evaluating the call reproduces the object.
    public static List<(int start, int count, object? value, TypeReference type)> Cells(Mono.Cecil.Cil.MethodBody body, bool evaluate) {
        var res = new List<(int, int, object?, TypeReference)>(); var ins = body.Instructions;
        for (int n = 0; n < ins.Count; n++) {
            if (!IsCellLoad(ins[n])) continue;
            var mr = (MethodReference)ins[n].Operand; int argc = mr.Parameters.Count;
            if (n < argc) throw new NotSupportedException("cell load without arguments");
            var args = new object[argc];
            for (int a = 0; a < argc; a++) {
                var ld = ins[n - argc + a];
                if (ld.OpCode.Code != Code.Ldc_I4 || ld.Operand is not int v) throw new NotSupportedException("cell load argument " + ld);
                args[a] = v;
            }
            var type = mr is GenericInstanceMethod g ? g.GenericArguments[0] : mr.ReturnType;
            object? value = null;
            if (evaluate) {
                var rt = typeof(MonoMod.Utils.DynamicReferenceManager);
                var m = rt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .First(x => x.Name == mr.Name && x.GetParameters().Length == argc && x.IsGenericMethodDefinition == (mr is GenericInstanceMethod));
                if (m.IsGenericMethodDefinition) m = m.MakeGenericMethod(typeof(object));
                value = m.Invoke(null, args);
            }
            res.Add((n - argc, argc + 1, value, type));
        }
        return res;
    }

    // ---------- runtime (phone) ----------
    static readonly bool Disabled = Environment.GetEnvironmentVariable("TP_BAKED_IL") == "0";
    public static Delegate? TryUse(MethodInfo orig, Type chainType, List<Delegate> il, Mono.Cecil.Cil.MethodBody edited, Action<string>? log) {
        if (Disabled || Baking) return null;
        var t = orig.DeclaringType!; var suffix = orig.Name.Substring("__mh_orig_".Length);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var info = t.GetField("__mh_bakedinfo_" + suffix, all); if (info == null) return null;
        var parts = ((string)info.GetRawConstantValue()!).Split('|');
        string ids = Ids(il), hash = Hash(edited);
        if (parts[0] != ids || parts[1] != hash) { log?.Invoke($"ManagedHooks: baked {t.Name}::{suffix} not used (ids {(parts[0] == ids ? "match" : "differ")}, IL {(parts[1] == hash ? "match" : "differ")})"); return null; }
        var cells = Cells(edited, evaluate: true);
        if (cells.Count != int.Parse(parts[2])) { log?.Invoke($"ManagedHooks: baked {t.Name}::{suffix} cell count differs"); return null; }
        var cf = t.GetField("__mh_bakedcells_" + suffix, all)!; var arr = new object?[cells.Count];
        for (int i = 0; i < cells.Count; i++) arr[i] = Convert(cells[i].value);
        cf.SetValue(null, arr);
        var baked = t.GetMethod("__mh_baked_" + suffix, all)!;
        return baked.CreateDelegate(chainType);
    }

    // ---------- bake (Mac) ----------
    static ModuleDefinition? outModule; static string? outPath; static Timer? flush; static readonly object BakeGate = new();
    static readonly List<string> report = new();
    public static bool Baking => Environment.GetEnvironmentVariable("TP_BAKE_OUT") != null;

    public static void Capture(MethodInfo orig, List<Delegate> il, MethodDefinition edited) {
        lock (BakeGate) {
            try {
                if (outModule == null) {
                    outPath = Environment.GetEnvironmentVariable("TP_BAKE_OUT")!;
                    var src = Environment.GetEnvironmentVariable("TP_BAKE_SRC")!;
                    var res = new DefaultAssemblyResolver(); res.AddSearchDirectory(Path.GetDirectoryName(src)!);
                    foreach (var d in (Environment.GetEnvironmentVariable("TP_BAKE_REFS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)) res.AddSearchDirectory(d);
                    outModule = ModuleDefinition.ReadModule(src, new ReaderParameters { AssemblyResolver = res, ReadingMode = ReadingMode.Immediate });
                }
                var suffix = orig.Name.Substring("__mh_orig_".Length);
                var type = outModule.GetType(orig.DeclaringType!.FullName!.Replace('+', '/')) ?? throw new Exception("type not found " + orig.DeclaringType.FullName);
                Remove(type, suffix);
                var origDef = type.Methods.Single(x => x.Name == orig.Name);
                var cells = Cells(edited.Body, evaluate: false);
                var m2 = Copy(type, origDef, edited, suffix, cells.Count, out var cellField);
                var info = new FieldDefinition("__mh_bakedinfo_" + suffix, Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Literal | Mono.Cecil.FieldAttributes.HasDefault, outModule.TypeSystem.String)
                    { Constant = $"{Ids(il)}|{Hash(edited.Body)}|{cells.Count}" };
                type.Fields.Add(info);
                report.Add($"{type.FullName}::{suffix} il={il.Count} cells={cells.Count} size={m2.Body.Instructions.Count}");
            } catch (Exception e) {
                try { var t = outModule?.GetType(orig.DeclaringType!.FullName!.Replace('+', '/')); if (t != null) Remove(t, orig.Name.Substring("__mh_orig_".Length)); } catch { }
                report.Add($"FAILED {orig.DeclaringType?.FullName}::{orig.Name}: {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            }
            flush?.Dispose(); flush = new Timer(_ => Write(), null, 20000, Timeout.Infinite);
        }
    }

    static void Remove(TypeDefinition type, string suffix) {
        foreach (var h in type.Methods.Where(x => x.Name.StartsWith("__mh_fdi_" + suffix + "_", StringComparison.Ordinal)).ToList()) type.Methods.Remove(h);
        foreach (var name in new[] { "__mh_baked_", "__mh_bakedcells_", "__mh_bakedinfo_" }) {
            var m = type.Methods.FirstOrDefault(x => x.Name == name + suffix); if (m != null) type.Methods.Remove(m);
            var f = type.Fields.FirstOrDefault(x => x.Name == name + suffix); if (f != null) type.Fields.Remove(f);
        }
    }

    // Baked bodies call internal MonoMod helpers (FastDelegateInvokers) and private Calamity members, exactly like the
    // DynamicMethods they replace (skipVisibility). Grant the same through IgnoresAccessChecksToAttribute.
    static void GrantAccess(ModuleDefinition mod) {
        const string ns = "System.Runtime.CompilerServices", n = "IgnoresAccessChecksToAttribute";
        var attr = mod.GetType(ns, n);
        if (attr == null) {
            var baseRef = mod.ImportReference(typeof(Attribute));
            attr = new TypeDefinition(ns, n, Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit, baseRef);
            var ctor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, mod.TypeSystem.Void);
            ctor.Parameters.Add(new ParameterDefinition("assemblyName", Mono.Cecil.ParameterAttributes.None, mod.TypeSystem.String));
            var il = ctor.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, mod.ImportReference(typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!)); il.Emit(OpCodes.Ret);
            attr.Methods.Add(ctor); mod.Types.Add(attr);
        }
        var c = attr.Methods.Single(m => m.IsConstructor);
        var have = mod.Assembly.CustomAttributes.Where(a => a.AttributeType.FullName == attr.FullName).Select(a => (string)a.ConstructorArguments[0].Value).ToHashSet();
        foreach (var r in mod.AssemblyReferences.Select(a => a.Name).Distinct())
            if (have.Add(r)) { var ca = new CustomAttribute(c); ca.ConstructorArguments.Add(new CustomAttributeArgument(mod.TypeSystem.String, r)); mod.Assembly.CustomAttributes.Add(ca); }
    }

    static void Write() {
        lock (BakeGate) {
            if (outModule == null) return;
            try {
                // Re-read the module after writing would be needed for a second write; write to a fresh file each time.
                GrantAccess(outModule);
                outModule.Write(outPath!);
                File.WriteAllLines(outPath + ".report.txt", report);
                File.WriteAllText(outPath + ".done", DateTime.Now.ToString("O"));
            } catch (Exception e) { File.WriteAllText(outPath + ".error.txt", e.ToString()); }
        }
    }

    static MethodDefinition Copy(TypeDefinition type, MethodDefinition origDef, MethodDefinition edited, string suffix, int cellCount, out FieldDefinition cellField) {
        var mod = type.Module;
        cellField = new FieldDefinition("__mh_bakedcells_" + suffix, Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, new ArrayType(mod.TypeSystem.Object));
        type.Fields.Add(cellField);
        var baked = new MethodDefinition("__mh_baked_" + suffix, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig, origDef.ReturnType);
        type.Methods.Add(baked);
        // static field initializer through a static ctor would change type init semantics; initialise lazily instead: the
        // runtime fills the array before the chain delegate exists, so allocate it here via a dedicated helper method.
        var im = new Importer(mod, baked);
        if (origDef.HasThis) baked.Parameters.Add(new ParameterDefinition("self", Mono.Cecil.ParameterAttributes.None, type));
        foreach (var p in origDef.Parameters) baked.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes & ~(Mono.Cecil.ParameterAttributes.HasDefault | Mono.Cecil.ParameterAttributes.HasFieldMarshal), p.ParameterType));
        var src = edited.Body; var dst = baked.Body; dst.InitLocals = src.InitLocals;
        var vars = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var v in src.Variables) { var nv = new VariableDefinition(im.Type(v.VariableType)); vars[v] = nv; dst.Variables.Add(nv); }
        ParameterDefinition MapP(ParameterDefinition p) {
            if (p == src.ThisParameter) return baked.Parameters[0];
            int shift = edited.HasThis ? 1 : 0; return baked.Parameters[p.Index + shift];
        }
        var cells = Cells(src, evaluate: false); var cellStart = new Dictionary<int, (int count, int idx, TypeReference t)>();
        for (int c = 0; c < cells.Count; c++) cellStart[cells[c].start] = (cells[c].count, c, cells[c].type);
        var map = new Dictionary<Instruction, Instruction>(); var list = new List<Instruction>(); var il = dst.GetILProcessor(); int fdiCount = 0;
        var ins = src.Instructions;
        for (int n = 0; n < ins.Count; n++) {
            if (cellStart.TryGetValue(n, out var cell)) {
                // ldsfld cells; ldc.i4 idx; ldelem.ref; castclass T  (first instruction keeps branch targets)
                var first = Instruction.Create(OpCodes.Ldsfld, cellField);
                var seq = new[] { first, Instruction.Create(OpCodes.Ldc_I4, cell.idx), Instruction.Create(OpCodes.Ldelem_Ref), Instruction.Create(OpCodes.Castclass, IsFdi(cell.t) ? mod.ImportReference(typeof(Delegate)) : im.Type(cell.t)) };
                for (int k = 0; k < cell.count; k++) map[ins[n + k]] = first;
                list.AddRange(seq); n += cell.count - 1; continue;
            }
            var i = ins[n]; var o = i.Operand; Instruction ni;
            switch (o) {
                case null: ni = Instruction.Create(i.OpCode); break;
                case Instruction _: case Instruction[] _: ni = Instruction.Create(OpCodes.Nop); ni.OpCode = i.OpCode; ni.Operand = o; break; // fixed below
                case VariableDefinition v: ni = Instruction.Create(OpCodes.Nop); ni.OpCode = i.OpCode; ni.Operand = vars[v]; break;
                case ParameterDefinition p: ni = Instruction.Create(OpCodes.Nop); ni.OpCode = i.OpCode; ni.Operand = MapP(p); break;
                case MethodReference m when m.DeclaringType?.FullName == "MonoMod.Cil.FastDelegateInvokers":
                    ni = Instruction.Create(OpCodes.Call, FdiHelper(type, suffix, fdiCount++, m, im)); break;
                case MethodReference m: ni = Instruction.Create(OpCodes.Nop); ni.OpCode = i.OpCode; ni.Operand = im.Method(m); break;
                case FieldReference f: ni = Instruction.Create(OpCodes.Nop); ni.OpCode = i.OpCode; ni.Operand = im.Field(f); break;
                case TypeReference t: ni = Instruction.Create(OpCodes.Nop); ni.OpCode = i.OpCode; ni.Operand = im.Type(t); break;
                case CallSite: throw new NotSupportedException("calli");
                default: ni = Instruction.Create(OpCodes.Nop); ni.OpCode = i.OpCode; ni.Operand = o; break;
            }
            map[i] = ni; list.Add(ni);
        }
        foreach (var x in list) {
            if (x.Operand is Instruction t) x.Operand = map[t];
            else if (x.Operand is Instruction[] ts) x.Operand = ts.Select(y => map[y]).ToArray();
        }
        foreach (var x in list) dst.Instructions.Add(x);
        foreach (var h in src.ExceptionHandlers) dst.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType) {
            TryStart = map[h.TryStart], TryEnd = h.TryEnd == null ? null : map[h.TryEnd], HandlerStart = map[h.HandlerStart], HandlerEnd = h.HandlerEnd == null ? null : map[h.HandlerEnd],
            FilterStart = h.FilterStart == null ? null : map[h.FilterStart], CatchType = h.CatchType == null ? null : im.Type(h.CatchType) });
        // short branches may now be out of range
        foreach (var x in dst.Instructions) x.OpCode = x.OpCode.Code switch {
            Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue, Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge,
            Code.Bgt_S => OpCodes.Bgt, Code.Ble_S => OpCodes.Ble, Code.Blt_S => OpCodes.Blt, Code.Bne_Un_S => OpCodes.Bne_Un, Code.Bge_Un_S => OpCodes.Bge_Un,
            Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_Un_S => OpCodes.Blt_Un, Code.Leave_S => OpCodes.Leave, _ => x.OpCode };
        // operand normalisation (manipulators often store int in ldc.i4.s)
        foreach (var x in dst.Instructions) {
            if (x.OpCode.Code == Code.Ldc_I4_S && x.Operand is int v) { if (v >= sbyte.MinValue && v <= sbyte.MaxValue) x.Operand = (sbyte)v; else x.OpCode = OpCodes.Ldc_I4; }
            else if (x.OpCode.OperandType == OperandType.InlineI && x.Operand is sbyte sb) x.Operand = (int)sb;
            else if (x.OpCode.OperandType == OperandType.InlineI && x.Operand is byte b) x.Operand = (int)b;
            else if ((x.OpCode.Code is Code.Ldarg_S or Code.Ldarga_S or Code.Starg_S) && x.Operand is ParameterDefinition pd && pd.Index > 250) x.OpCode = x.OpCode.Code switch { Code.Ldarg_S => OpCodes.Ldarg, Code.Ldarga_S => OpCodes.Ldarga, _ => OpCodes.Starg };
            else if ((x.OpCode.Code is Code.Ldloc_S or Code.Ldloca_S or Code.Stloc_S) && x.Operand is VariableDefinition vd && vd.Index > 250) x.OpCode = x.OpCode.Code switch { Code.Ldloc_S => OpCodes.Ldloc, Code.Ldloca_S => OpCodes.Ldloca, _ => OpCodes.Stloc };
        }
        // arguments shifted by one for instance methods: ldarg.0..3 short forms refer to positions, rewrite them explicitly
        foreach (var x in dst.Instructions) {
            int? argIndex = x.OpCode.Code switch { Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3, _ => null };
            if (argIndex is int ai) { x.OpCode = OpCodes.Ldarg; x.Operand = baked.Parameters[ai]; } // edited was already static-shaped? see below
        }
        return baked;
    }

    static bool IsFdi(TypeReference t) => t.FullName.StartsWith("MonoMod.Cil.FastDelegateInvokers", StringComparison.Ordinal);

    // MonoMod's FastDelegateInvokers.InvokeXxx<..>(args..., del) is private (Mono enforces that) and its delegate types are private
    // nested types. Replace each call with a helper in tModLoader: castclass Action/Func<..>; ldarg args; callvirt Invoke.
    // At runtime the cell delegates are re-created as those Action/Func types (TryUse).
    static MethodReference FdiHelper(TypeDefinition type, string suffix, int index, MethodReference m, Importer im) {
        var mod = type.Module; var gm = m as GenericInstanceMethod;
        TypeReference Sub(TypeReference t) => t is GenericParameter gp && gp.Type == GenericParameterType.Method && gm != null ? gm.GenericArguments[gp.Position] : t;
        var ps = m.Parameters.Take(m.Parameters.Count - 1).Select(p => Sub(p.ParameterType)).ToList(); var ret = Sub(m.ReturnType);
        if (ps.Any(p => p.IsByReference || p.ContainsGenericParameter) || ret.ContainsGenericParameter || ps.Count > 15) throw new NotSupportedException("invoker " + m.Name);
        bool isVoid = ret.FullName == "System.Void"; int k = ps.Count;
        var openType = isVoid ? (k == 0 ? typeof(Action) : Type.GetType($"System.Action`{k}")!) : Type.GetType($"System.Func`{k + 1}")!;
        var open = mod.ImportReference(openType);
        TypeReference dt = open;
        if (open.HasGenericParameters) { var g = new GenericInstanceType(open); foreach (var p in ps) g.GenericArguments.Add(im.Type(p)); if (!isVoid) g.GenericArguments.Add(im.Type(ret)); dt = g; }
        var inv = new MethodReference("Invoke", isVoid ? mod.TypeSystem.Void : open.GenericParameters[k], dt) { HasThis = true };
        for (int i = 0; i < k; i++) inv.Parameters.Add(new ParameterDefinition(open.GenericParameters[i]));
        var h = new MethodDefinition($"__mh_fdi_{suffix}_{index}", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig, isVoid ? mod.TypeSystem.Void : im.Type(ret));
        foreach (var p in ps) h.Parameters.Add(new ParameterDefinition(im.Type(p)));
        h.Parameters.Add(new ParameterDefinition("del", Mono.Cecil.ParameterAttributes.None, mod.ImportReference(typeof(Delegate))));
        var il = h.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg, h.Parameters[k]); il.Emit(OpCodes.Castclass, dt);
        for (int i = 0; i < k; i++) il.Emit(OpCodes.Ldarg, h.Parameters[i]);
        il.Emit(OpCodes.Callvirt, inv); il.Emit(OpCodes.Ret);
        type.Methods.Add(h); return h;
    }

    // Runtime: MonoMod-private invoker delegate -> equivalent System.Action/Func (same target and method).
    static object? Convert(object? v) {
        if (v is not Delegate d || d.GetType().DeclaringType?.FullName != "MonoMod.Cil.FastDelegateInvokers") return v;
        var invoke = d.GetType().GetMethod("Invoke")!; var ps = invoke.GetParameters().Select(p => p.ParameterType).ToList();
        Type t = invoke.ReturnType == typeof(void)
            ? (ps.Count == 0 ? typeof(Action) : Type.GetType($"System.Action`{ps.Count}")!.MakeGenericType(ps.ToArray()))
            : Type.GetType($"System.Func`{ps.Count + 1}")!.MakeGenericType(ps.Append(invoke.ReturnType).ToArray());
        return d.Method.CreateDelegate(t, d.Target);
    }

    // Imports references into tModLoader's own module, mapping tModLoader members back to their definitions.
    sealed class Importer {
        readonly ModuleDefinition mod; readonly MethodDefinition owner;
        public Importer(ModuleDefinition mod, MethodDefinition owner) { this.mod = mod; this.owner = owner; }
        bool IsSelf(TypeReference t) {
            var scope = t.Scope;
            string? name = scope switch { AssemblyNameReference a => a.Name, ModuleDefinition m => m.Assembly.Name.Name, ModuleReference r => r.Name, _ => null };
            if (t is TypeDefinition td) name = td.Module.Assembly.Name.Name;
            return name == mod.Assembly.Name.Name || name == mod.Assembly.Name.Name + ".dll";
        }
        public TypeReference Type(TypeReference t) {
            switch (t) {
                case GenericParameter gp: return gp; // only inside signatures; encoded by position
                case GenericInstanceType g: { var n = new GenericInstanceType(Type(g.ElementType)); foreach (var a in g.GenericArguments) n.GenericArguments.Add(Type(a)); return n; }
                case ArrayType a: return a.IsVector ? new ArrayType(Type(a.ElementType)) : new ArrayType(Type(a.ElementType), a.Rank);
                case ByReferenceType b: return new ByReferenceType(Type(b.ElementType));
                case PointerType p: return new PointerType(Type(p.ElementType));
                case PinnedType p: return new PinnedType(Type(p.ElementType));
                case RequiredModifierType r: return new RequiredModifierType(Type(r.ModifierType), Type(r.ElementType));
                case OptionalModifierType o: return new OptionalModifierType(Type(o.ModifierType), Type(o.ElementType));
            }
            if (t.IsNested) {
                var decl = Type(t.DeclaringType);
                if (decl is TypeDefinition dd) return dd.NestedTypes.FirstOrDefault(x => x.Name == t.Name) ?? throw new Exception("nested type not found " + t.FullName);
            }
            if (IsSelf(t)) return mod.GetType(t.FullName) ?? throw new Exception("self type not found " + t.FullName);
            return mod.ImportReference(t);
        }
        public FieldReference Field(FieldReference f) {
            var decl = Type(f.DeclaringType);
            if (decl is TypeDefinition td && td.Module == mod) return td.Fields.FirstOrDefault(x => x.Name == f.Name) ?? throw new Exception("self field not found " + f.FullName);
            if (f.DeclaringType.Scope != null && !IsSelf(f.DeclaringType) && f is not FieldDefinition && decl is not GenericInstanceType) return mod.ImportReference(f);
            return new FieldReference(f.Name, Sig(f.FieldType), decl);
        }
        TypeReference Sig(TypeReference t) => t.ContainsGenericParameter ? SigGeneric(t) : Type(t);
        TypeReference SigGeneric(TypeReference t) => t switch {
            GenericParameter gp => gp,
            GenericInstanceType g => Gi(g),
            ArrayType a => a.IsVector ? new ArrayType(SigGeneric(a.ElementType)) : new ArrayType(SigGeneric(a.ElementType), a.Rank),
            ByReferenceType b => new ByReferenceType(SigGeneric(b.ElementType)),
            PointerType p => new PointerType(SigGeneric(p.ElementType)),
            _ => Type(t) };
        GenericInstanceType Gi(GenericInstanceType g) { var n = new GenericInstanceType(Type(g.ElementType)); foreach (var a in g.GenericArguments) n.GenericArguments.Add(Sig(a)); return n; }
        public MethodReference Method(MethodReference m) {
            if (m.DeclaringType == null) throw new NotSupportedException($"method without declaring type: {m.GetType().Name} {m.Name} {m}");
            if (m is GenericInstanceMethod gm) { var n = new GenericInstanceMethod(Method(gm.ElementMethod)); foreach (var a in gm.GenericArguments) n.GenericArguments.Add(Type(a)); return n; }
            var decl = Type(m.DeclaringType);
            if (decl is TypeDefinition td && td.Module == mod) {
                var hit = td.Methods.Where(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count && x.GenericParameters.Count == m.GenericParameters.Count && x.HasThis == m.HasThis
                    && x.ReturnType.FullName == m.ReturnType.FullName && x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(m.Parameters.Select(p => p.ParameterType.FullName))).ToList();
                if (hit.Count == 1) return hit[0];
                static string N(TypeReference t) { while (t is IModifierType mt) t = mt.ElementType; return t.FullName; }
                hit = td.Methods.Where(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count && x.GenericParameters.Count == m.GenericParameters.Count && x.HasThis == m.HasThis
                    && N(x.ReturnType) == N(m.ReturnType) && x.Parameters.Select(p => N(p.ParameterType)).SequenceEqual(m.Parameters.Select(p => N(p.ParameterType)))).ToList();
                if (hit.Count == 1) return hit[0];
                throw new Exception($"self method not found ({hit.Count}) {m.FullName}");
            }
            if (!IsSelf(m.DeclaringType) && decl is not GenericInstanceType && !m.DeclaringType.ContainsGenericParameter) return mod.ImportReference(m);
            var r = new MethodReference(m.Name, mod.TypeSystem.Void, decl) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention };
            foreach (var g in m.GenericParameters) r.GenericParameters.Add(new GenericParameter(g.Name, r));
            r.ReturnType = Sig(m.ReturnType);
            foreach (var p in m.Parameters) r.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, Sig(p.ParameterType)));
            return r;
        }
    }
}
