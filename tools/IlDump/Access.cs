// Lists non-public members of OTHER assemblies referenced by __mh_baked_* methods (Mono may enforce access on them).
using Mono.Cecil;
static class Access {
    public static void Run(string dll, string dir) {
        var res = new DefaultAssemblyResolver(); res.AddSearchDirectory(dir); foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)) res.AddSearchDirectory(d);
        var m = ModuleDefinition.ReadModule(dll, new ReaderParameters { AssemblyResolver = res });
        var seen = new HashSet<string>();
        bool TypeVisible(TypeDefinition t) { for (; t != null; t = t.DeclaringType) if (!(t.IsPublic || t.IsNestedPublic)) return false; return true; }
        foreach (var t in m.GetTypes()) foreach (var meth in t.Methods.Where(x => x.Name.StartsWith("__mh_baked_") && x.HasBody))
            foreach (var i in meth.Body.Instructions) {
                if (i.Operand is not MemberReference r) continue;
                var dt = r is TypeReference tr ? tr : r.DeclaringType; if (dt == null) continue;
                var scope = dt.GetElementType().Scope?.Name ?? ""; if (scope.StartsWith("tModLoader")) continue;
                string why = "";
                try {
                    var td = dt.GetElementType().Resolve(); if (td != null && !TypeVisible(td)) why += $"type {(td.IsNestedPrivate||td.IsNotPublic?"private/internal":"nonpublic")} ";
                    if (r is MethodReference mr && mr.GetElementMethod().Resolve() is MethodDefinition md && !md.IsPublic) why += md.IsPrivate ? "private method " : "internal/protected method ";
                    if (r is FieldReference fr && fr.Resolve() is FieldDefinition fd && !fd.IsPublic) why += fd.IsPrivate ? "private field " : "internal field ";
                } catch (Exception e) { why += "unresolved " + e.Message; }
                if (why != "" && seen.Add(why + r.FullName)) Console.WriteLine($"{scope}: {why}| {r.FullName}   (in {t.Name}::{meth.Name})");
            }
    }
}
