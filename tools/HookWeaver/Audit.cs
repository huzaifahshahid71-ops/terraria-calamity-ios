// Static audit: which tModLoader/Terraria methods do the supplied mods hook, and through which mechanism.
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Audit {
    public static void Run(string refs, string[] mods, string outPath) {
        var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(refs);
        foreach (var m in mods) resolver.AddSearchDirectory(Path.GetDirectoryName(m)!);
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred };
        var hooks = AssemblyDefinition.ReadAssembly(Path.Combine(refs, "TerrariaHooks.dll"), rp);

        // accessor full name -> (kind, target method)
        var accessorTarget = new Dictionary<string, (string kind, MethodReference target)>();
        int accessorCount = 0, unresolvedAccessor = 0;
        foreach (var t in AllTypes(hooks.MainModule)) {
            var top = Top(t); string kind = top.Name.StartsWith("On_") ? "On" : top.Name.StartsWith("IL_") ? "IL" : "";
            if (kind == "") continue;
            foreach (var m in t.Methods.Where(m => m.Name.StartsWith("add_") && m.HasBody)) {
                accessorCount++;
                var tok = m.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldtoken && i.Operand is MethodReference);
                if (tok == null) { unresolvedAccessor++; continue; }
                accessorTarget[m.FullName] = (kind, (MethodReference)tok.Operand);
            }
        }
        var heCalls = AllTypes(hooks.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Where(i => i.Operand is MethodReference r && (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt))
            .Select(i => ((MethodReference)i.Operand).DeclaringType.FullName + "::" + ((MethodReference)i.Operand).Name).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        var targets = new Dictionary<string, Target>();
        var dynamicSites = new List<object>();
        string[] dynamicApis = { "MonoMod.RuntimeDetour.Hook::.ctor", "MonoMod.RuntimeDetour.ILHook::.ctor", "Terraria.ModLoader.MonoModHooks::Add", "Terraria.ModLoader.MonoModHooks::Modify",
            "MonoMod.RuntimeDetour.HookGen.HookEndpointManager::Add", "MonoMod.RuntimeDetour.HookGen.HookEndpointManager::Modify", "MonoMod.RuntimeDetour.Detour::.ctor", "MonoMod.RuntimeDetour.NativeDetour::.ctor" };
        foreach (var modPath in mods) {
            var mod = AssemblyDefinition.ReadAssembly(modPath, rp); var modName = mod.Name.Name;
            foreach (var caller in AllTypes(mod.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody)) {
                var ins = caller.Body.Instructions;
                for (int k = 0; k < ins.Count; k++) {
                    if (ins[k].Operand is not MethodReference r || (ins[k].OpCode != OpCodes.Call && ins[k].OpCode != OpCodes.Callvirt && ins[k].OpCode != OpCodes.Newobj)) continue;
                    var api = r.DeclaringType.FullName + "::" + r.Name;
                    if (r.DeclaringType.Scope.Name.StartsWith("TerrariaHooks") && (r.Name.StartsWith("add_") || r.Name.StartsWith("remove_"))) {
                        if (r.Name.StartsWith("remove_")) continue;
                        var key = r.Resolve()?.FullName ?? r.FullName;
                        if (!accessorTarget.TryGetValue(key, out var at)) { dynamicSites.Add(new { mod = modName, caller = caller.FullName, api = "UNRESOLVED accessor " + r.FullName }); continue; }
                        var tgt = at.target; var tdef = tgt.Resolve();
                        var id = (tdef?.FullName ?? tgt.FullName);
                        if (!targets.TryGetValue(id, out var T)) targets[id] = T = Describe(tgt, tdef);
                        T.Kinds.Add(at.kind); T.Mods.Add(modName); T.Sites++;
                    } else if (dynamicApis.Any(a => api == a)) {
                        // best-effort: nearest preceding ldtoken / ldstr for the target
                        var hints = ins.Take(k).Reverse().Take(40).Where(i => i.OpCode == OpCodes.Ldtoken || i.OpCode == OpCodes.Ldstr).Take(4).Select(i => i.OpCode + " " + i.Operand).ToArray();
                        dynamicSites.Add(new { mod = modName, caller = caller.FullName, api, hints });
                    }
                }
            }
        }
        var list = targets.Values.OrderBy(t => t.Id).ToList();
        var summary = new {
            terrariaHooksAccessors = accessorCount, unresolvedAccessors = unresolvedAccessor,
            terrariaHooksCalls = heCalls.Where(kv => kv.Key.Contains("HookEndpointManager")).ToDictionary(kv => kv.Key, kv => kv.Value),
            uniqueTargets = list.Count,
            onTargets = list.Count(t => t.Kinds.Contains("On")), ilTargets = list.Count(t => t.Kinds.Contains("IL")),
            byAssembly = list.GroupBy(t => t.Assembly).ToDictionary(g => g.Key, g => g.Count()),
            features = new {
                instance = list.Count(t => !t.Static), staticM = list.Count(t => t.Static), valueTypeSelf = list.Count(t => t.ValueTypeSelf),
                virtualM = list.Count(t => t.Virtual), byRefParams = list.Count(t => t.ByRefParams), genericMethodOrType = list.Count(t => t.Generic),
                ctor = list.Count(t => t.Ctor), unresolved = list.Count(t => t.Unresolved), hasTryCatch = list.Count(t => t.HasEH),
                totalIlBytesOfTargets = list.Sum(t => t.CodeSize)
            },
            dynamicSiteCount = dynamicSites.Count,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(new { summary, targets = list, dynamicSites }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }
    static Target Describe(MethodReference r, MethodDefinition? d) => new Target {
        Id = d?.FullName ?? r.FullName, Assembly = d?.Module.Assembly.Name.Name ?? r.DeclaringType.Scope.Name, Unresolved = d == null,
        Static = d is { IsStatic: true }, Virtual = d is { IsVirtual: true }, Ctor = d is { IsConstructor: true },
        ValueTypeSelf = d != null && !d.IsStatic && d.DeclaringType.IsValueType,
        ByRefParams = r.Parameters.Any(p => p.ParameterType.IsByReference) || r.ReturnType.IsByReference,
        Generic = r.HasGenericParameters || r.DeclaringType.HasGenericParameters || r.DeclaringType is GenericInstanceType,
        HasEH = d is { HasBody: true } && d.Body.HasExceptionHandlers, CodeSize = d is { HasBody: true } ? d.Body.CodeSize : 0,
    };
    public static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m) { foreach (var t in m.Types) foreach (var x in Nest(t)) yield return x; }
    static IEnumerable<TypeDefinition> Nest(TypeDefinition t) { yield return t; foreach (var n in t.NestedTypes) foreach (var x in Nest(n)) yield return x; }
    static TypeDefinition Top(TypeDefinition t) { while (t.DeclaringType != null) t = t.DeclaringType; return t; }
}
public class Target {
    public string Id { get; set; } = ""; public string Assembly { get; set; } = ""; public HashSet<string> Kinds { get; } = new(); public HashSet<string> Mods { get; } = new();
    public int Sites { get; set; } public bool Static { get; set; } public bool Virtual { get; set; } public bool Ctor { get; set; } public bool ValueTypeSelf { get; set; }
    public bool ByRefParams { get; set; } public bool Generic { get; set; } public bool HasEH { get; set; } public bool Unresolved { get; set; } public int CodeSize { get; set; }
}
