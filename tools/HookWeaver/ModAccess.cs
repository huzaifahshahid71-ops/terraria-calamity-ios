// Apply the desktop Publicizer access surface to the DERIVED runtime assemblies.
// Original game and mod packages remain unchanged. Only members/types referenced by
// the fixed mod set are widened; no gameplay method body is changed here.
using Mono.Cecil;
static class ModAccess {
    public static void Apply(AssemblyDefinition tml, DefaultAssemblyResolver resolver, string refs, string outDir) {
        var modules = new Dictionary<string,ModuleDefinition> { ["tModLoader"]=tml.MainModule };
        foreach(var name in new[]{"FNA","ReLogic"})
            modules[name]=resolver.Resolve(tml.MainModule.AssemblyReferences.Single(a=>a.Name==name)).MainModule;
        var changed=new HashSet<string>();
        void MakeTypePublic(TypeDefinition type) {
            if(type.DeclaringType!=null) {MakeTypePublic(type.DeclaringType);if(!type.IsNestedPublic){type.IsNestedPublic=true;changed.Add(type.Module.Assembly.Name.Name+" type "+type.FullName);}}
            else if(!type.IsPublic){type.IsPublic=true;changed.Add(type.Module.Assembly.Name.Name+" type "+type.FullName);}
        }
        ModuleDefinition? Destination(TypeReference t) {
            t=t.GetElementType();
            if(t.Scope is AssemblyNameReference a && modules.TryGetValue(a.Name,out var module))return module;
            return null;
        }
        var root=Path.GetFullPath(Path.Combine(refs,"../.."));
        foreach(var name in new[]{"CalamityMod","CalamityModMusic"}) {
            using var mod=AssemblyDefinition.ReadAssembly(Path.Combine(root,"inputs/mods",name,name+".dll"),new ReaderParameters{AssemblyResolver=resolver});
            foreach(var reference in mod.MainModule.GetTypeReferences()) {
                var module=Destination(reference);if(module==null)continue;
                var type=Audit.AllTypes(module).SingleOrDefault(t=>t.FullName==reference.FullName);
                if(type!=null)MakeTypePublic(type);
            }
            foreach(var reference in mod.MainModule.GetMemberReferences()) {
                // Array constructors/Get/Set are supplied by the CLR, not private members of the element assembly.
                if(reference.DeclaringType is ArrayType)continue;
                var module=Destination(reference.DeclaringType);if(module==null)continue;
                if(reference is FieldReference field) {
                    var definition=field.Resolve() ?? throw new InvalidOperationException("Unresolved field: "+field.FullName);
                    if(!modules.TryGetValue(definition.Module.Assembly.Name.Name,out var target))continue;
                    var type=Audit.AllTypes(target).Single(t=>t.FullName==definition.DeclaringType.FullName);
                    var member=type.Fields.Single(f=>f.FullName==definition.FullName);MakeTypePublic(type);
                    if(!member.IsPublic){member.IsPublic=true;changed.Add(target.Assembly.Name.Name+" field "+member.FullName);}
                } else if(reference is MethodReference method) {
                    var definition=method.Resolve() ?? throw new InvalidOperationException("Unresolved method: "+method.FullName);
                    if(!modules.TryGetValue(definition.Module.Assembly.Name.Name,out var target))continue;
                    var type=Audit.AllTypes(target).Single(t=>t.FullName==definition.DeclaringType.FullName);
                    var member=type.Methods.Single(m=>m.FullName==definition.FullName);MakeTypePublic(type);
                    if(!member.IsPublic){member.IsPublic=true;changed.Add(target.Assembly.Name.Name+" method "+member.FullName);}
                }
            }
        }
        foreach(var line in LazyAssets.Apply(modules["ReLogic"])){Console.WriteLine("lazy assets: "+line);changed.Add("ReLogic lazy "+line);}
        foreach(var pair in modules.Where(x=>x.Key!="tModLoader"))
            if(changed.Any(x=>x.StartsWith(pair.Key+" ")))pair.Value.Write(Path.Combine(outDir,pair.Key+".dll"));
        File.WriteAllLines(Path.Combine(outDir,"mod-access-report.txt"),changed.Order());
        Console.WriteLine($"Mod Publicizer surface: {changed.Count} referenced private/internal members or types widened in derived runtime assemblies");
    }
}
