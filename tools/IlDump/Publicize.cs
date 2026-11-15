// Copies MonoMod.Utils with MonoMod.Cil.FastDelegateInvokers (and its nested delegate types/methods) made public, so AOT-baked
// IL edits (tools/ManagedHooks/Baked.cs) can call it on Mono, which enforces private access. Nothing else changes.
using Mono.Cecil;
static class Publicize {
    public static void Run(string src, string dst) {
        var a = AssemblyDefinition.ReadAssembly(src);
        var t = a.MainModule.GetType("MonoMod.Cil.FastDelegateInvokers") ?? throw new Exception("FastDelegateInvokers not found");
        int n = 0;
        void Pub(TypeDefinition x) {
            x.Attributes = (x.Attributes & ~TypeAttributes.VisibilityMask) | (x.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public); n++;
            foreach (var m in x.Methods) { m.Attributes = (m.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public; n++; }
            foreach (var nt in x.NestedTypes) Pub(nt);
        }
        Pub(t);
        a.Write(dst); Console.WriteLine($"publicized {n} members -> {dst}");
    }
}
