using Mono.Cecil; using Mono.Cecil.Cil;
static class TmlAudit {
    public static void Run(string refs) {
        var res = new DefaultAssemblyResolver(); res.AddSearchDirectory(refs);
        var tml = AssemblyDefinition.ReadAssembly(Path.Combine(refs, "tModLoader.dll"), new ReaderParameters { AssemblyResolver = res });
        var apis = new[] { "MonoMod.RuntimeDetour.Hook::.ctor", "MonoMod.RuntimeDetour.ILHook::.ctor", "MonoMod.RuntimeDetour.HookGen.HookEndpointManager::", "MonoMod.RuntimeDetour.DetourManager", "MonoMod.RuntimeDetour.NativeHook", "MonoMod.Core." };
        foreach (var m in Audit.AllTypes(tml.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions)
                if (i.Operand is MethodReference r) { var a = r.DeclaringType.FullName + "::" + r.Name; if (apis.Any(x => a.StartsWith(x))) Console.WriteLine($"{m.DeclaringType.FullName}::{m.Name} -> {a}"); }
    }
}
