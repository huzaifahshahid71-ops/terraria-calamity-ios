// Build-time weave for JIT-less iOS: prewoven dispatch stubs in tModLoader.dll, HookGen/MonoModHooks redirected to ManagedHooks.
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MA = Mono.Cecil.MethodAttributes;

static class Weave {
    public static int Run(string refs, string targetsJson, string managedHooksDll, string outDir, string[] extraTargets, bool ios = false) {
        Directory.CreateDirectory(outDir);
        var res = new DefaultAssemblyResolver(); res.AddSearchDirectory(refs); res.AddSearchDirectory(Path.GetDirectoryName(managedHooksDll)!);
        var rp = new ReaderParameters { AssemblyResolver = res, ReadingMode = ReadingMode.Immediate };
        var tml = AssemblyDefinition.ReadAssembly(Path.Combine(refs, "tModLoader.dll"), rp);
        var hooks = AssemblyDefinition.ReadAssembly(Path.Combine(refs, "TerrariaHooks.dll"), rp);
        var mh = AssemblyDefinition.ReadAssembly(managedHooksDll, rp);
        var endpoints = mh.MainModule.GetType("ManagedHooks.HookEndpoints");

        var ids = JsonDocument.Parse(File.ReadAllText(targetsJson)).RootElement.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("Id").GetString()!).ToHashSet();
        var all = Audit.AllTypes(tml.MainModule).SelectMany(t => t.Methods).ToList();
        var wanted = all.Where(m => ids.Contains(m.FullName)).ToList();
        foreach (var x in extraTargets) {
            var hit = all.Where(m => (m.DeclaringType.FullName + "::" + m.Name) == x).ToList();
            if (hit.Count == 0) throw new Exception("extra target not found: " + x);
            wanted.AddRange(hit);
        }
        wanted = wanted.Distinct().ToList();
        var missing = ids.Count - wanted.Count(m => ids.Contains(m.FullName));
        Console.WriteLine($"targets: {ids.Count} listed, {wanted.Count} to weave (incl. extras), {missing} listed-but-missing");
        if (missing != 0) return 3;

        var report = new List<object>();
        var counters = new Dictionary<string, int>();
        foreach (var m in wanted) {
            if (!m.HasBody) throw new Exception("no body: " + m.FullName);
            if (m.HasGenericParameters || m.DeclaringType.HasGenericParameters) throw new Exception("generic target unsupported yet: " + m.FullName);
            if (!m.IsStatic && m.DeclaringType.IsValueType) throw new Exception("value-type self unsupported yet: " + m.FullName);
            var key = m.DeclaringType.FullName + "::" + m.Name;
            counters.TryGetValue(key, out var k); counters[key] = k + 1;
            WeaveOne(tml.MainModule, m, k);
            report.Add(new { id = m.FullName, k, codeSize = m.DeclaringType.Methods.First(x => x.Name == $"__mh_orig_{m.Name}_{k}").Body.CodeSize });
        }

        // TerrariaHooks: HookEndpointManager.{Add,Remove,Modify,Unmodify}<T> -> ManagedHooks.HookEndpoints.*<T>
        var hm = hooks.MainModule; int redirected = 0;
        var map = new[] { "Add", "Remove", "Modify", "Unmodify" }.ToDictionary(n => n, n => hm.ImportReference(endpoints.Methods.Single(x => x.Name == n && x.HasGenericParameters)));
        foreach (var meth in Audit.AllTypes(hm).SelectMany(t => t.Methods).Where(x => x.HasBody))
            foreach (var ins in meth.Body.Instructions)
                if (ins.Operand is GenericInstanceMethod g && g.ElementMethod.DeclaringType.FullName == "MonoMod.RuntimeDetour.HookGen.HookEndpointManager" && map.TryGetValue(g.Name, out var target)) {
                    var gi = new GenericInstanceMethod(target); gi.GenericArguments.Add(g.GenericArguments[0]); ins.Operand = gi; redirected++;
                }
        Console.WriteLine($"TerrariaHooks: {redirected} HookEndpointManager calls redirected");

        // tML MonoModHooks.Add/Modify -> ManagedHooks untyped entry points
        var mmh = tml.MainModule.GetType("Terraria.ModLoader.MonoModHooks");
        foreach (var (name, to) in new[] { ("Add", "AddUntyped"), ("Modify", "ModifyUntyped") }) {
            var meth = mmh.Methods.Single(x => x.Name == name && x.Parameters.Count == 2);
            var body = meth.Body; body.Instructions.Clear(); body.ExceptionHandlers.Clear(); body.Variables.Clear();
            var il = body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, tml.MainModule.ImportReference(endpoints.Methods.Single(x => x.Name == to))); il.Emit(OpCodes.Ret);
        }
        Console.WriteLine("tModLoader MonoModHooks.Add/Modify redirected");

        if (ios) ModAccess.Apply(tml, res, refs, outDir);
        if (ios) foreach (var line in IosPatches.Apply(tml.MainModule, endpoints, mh.MainModule)) Console.WriteLine("ios patch: " + line);
        tml.Write(Path.Combine(outDir, "tModLoader.dll"));
        hooks.Write(Path.Combine(outDir, "TerrariaHooks.dll"));
        File.Copy(managedHooksDll, Path.Combine(outDir, "ManagedHooks.dll"), true);
        File.WriteAllText(Path.Combine(outDir, "weave-report.json"), JsonSerializer.Serialize(new { woven = report, redirected }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    static void WeaveOne(ModuleDefinition module, MethodDefinition m, int k) {
        var t = m.DeclaringType; var name = m.Name;
        // 1. orig: move the body (not rename) so every existing reference keeps hitting the stub
        var orig = new MethodDefinition($"__mh_orig_{name}_{k}", (m.Attributes & ~(MA.Virtual | MA.NewSlot | MA.Final | MA.Abstract | MA.SpecialName | MA.RTSpecialName)) | MA.HideBySig, m.ReturnType) { HasThis = m.HasThis, ImplAttributes = m.ImplAttributes & ~Mono.Cecil.MethodImplAttributes.AggressiveInlining };
        foreach (var p in m.Parameters) orig.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes & ~(ParameterAttributes.HasDefault | ParameterAttributes.HasFieldMarshal), p.ParameterType));
        var src = m.Body; var dst = orig.Body;
        dst.InitLocals = src.InitLocals; dst.MaxStackSize = src.MaxStackSize;
        foreach (var v in src.Variables) dst.Variables.Add(v);
        foreach (var ins in src.Instructions) {
            if (ins.Operand is ParameterDefinition pd) ins.Operand = pd == src.ThisParameter ? dst.ThisParameter : orig.Parameters[pd.Index];
            dst.Instructions.Add(ins);
        }
        foreach (var eh in src.ExceptionHandlers) dst.ExceptionHandlers.Add(eh);
        t.Methods.Add(orig);
        m.Body = new MethodBody(m);
        m.ImplAttributes &= ~Mono.Cecil.MethodImplAttributes.AggressiveInlining;

        // 2. chain delegate type: (self?, params...) -> ret
        var chainParams = new List<(string, TypeReference, ParameterAttributes)>();
        if (!m.IsStatic) chainParams.Add(("self", t, ParameterAttributes.None));
        foreach (var p in m.Parameters) chainParams.Add((p.Name, p.ParameterType, p.Attributes & (ParameterAttributes.In | ParameterAttributes.Out)));
        var del = new TypeDefinition("", $"__mh_d_{name}_{k}", TypeAttributes.NestedPublic | TypeAttributes.Sealed, module.ImportReference(typeof(MulticastDelegate)));
        var dctor = new MethodDefinition(".ctor", MA.Public | MA.HideBySig | MA.SpecialName | MA.RTSpecialName, module.TypeSystem.Void) { ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime | Mono.Cecil.MethodImplAttributes.Managed };
        dctor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, module.TypeSystem.Object));
        dctor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, module.TypeSystem.IntPtr));
        var invoke = new MethodDefinition("Invoke", MA.Public | MA.HideBySig | MA.NewSlot | MA.Virtual, m.ReturnType) { ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime | Mono.Cecil.MethodImplAttributes.Managed };
        foreach (var (n, ty, a) in chainParams) invoke.Parameters.Add(new ParameterDefinition(n, a, ty));
        // Chain state lives in a nested holder with no static constructor: registering a hook must not run the
        // target type's .cctor (e.g. Terraria.Main), matching native detour behaviour.
        var holder = t.NestedTypes.FirstOrDefault(n => n.Name == "__mh_chains");
        if (holder == null) {
            holder = new TypeDefinition("", "__mh_chains", TypeAttributes.NestedPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
            t.NestedTypes.Add(holder);
        }
        del.Methods.Add(dctor); del.Methods.Add(invoke); holder.NestedTypes.Add(del);

        // 3. chain field + bridge
        var chain = new FieldDefinition($"__mh_chain_{name}_{k}", FieldAttributes.Public | FieldAttributes.Static, del);
        holder.Fields.Add(chain);
        var bridge = new MethodDefinition($"__mh_bridge_{name}_{k}", MA.Public | MA.Static | MA.HideBySig, m.ReturnType);
        foreach (var (n, ty, a) in chainParams) bridge.Parameters.Add(new ParameterDefinition(n, a, ty));
        var bil = bridge.Body.GetILProcessor();
        foreach (var p in bridge.Parameters) bil.Emit(OpCodes.Ldarg, p);
        bil.Emit(OpCodes.Call, orig); bil.Emit(OpCodes.Ret);
        t.Methods.Add(bridge);

        // 4. stub in the original definition
        var il = m.Body.GetILProcessor();
        var viaChain = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Ldsfld, chain); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Brtrue, viaChain); il.Emit(OpCodes.Pop);
        void Args() { if (!m.IsStatic) il.Emit(OpCodes.Ldarg_0); foreach (var p in m.Parameters) il.Emit(OpCodes.Ldarg, p); }
        Args(); il.Emit(OpCodes.Call, orig); il.Emit(OpCodes.Ret);
        il.Append(viaChain); Args(); il.Emit(OpCodes.Callvirt, invoke); il.Emit(OpCodes.Ret);
    }
}
