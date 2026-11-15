using Mono.Cecil; using Mono.Cecil.Cil;
static class OsScan {
    public static void Run(string path) {
        var mod = ModuleDefinition.ReadModule(path);
        var hits = new SortedDictionary<string, SortedSet<string>>();
        foreach (var m in Audit.AllTypes(mod).SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions)
                if (i.Operand is MethodReference r) {
                    var n = r.DeclaringType.FullName + "::" + r.Name;
                    if (n.StartsWith("System.Runtime.InteropServices.RuntimeInformation::IsOSPlatform") || (n.StartsWith("System.OperatingSystem::Is") && !n.Contains("VersionAtLeast")))
                        { if (!hits.TryGetValue(m.DeclaringType.FullName + "::" + m.Name, out var s)) hits[m.DeclaringType.FullName + "::" + m.Name] = s = new(); s.Add(r.Name); }
                }
        foreach (var kv in hits) Console.WriteLine($"{kv.Key}  [{string.Join(",", kv.Value)}]");
        Console.WriteLine($"{hits.Count} methods");
    }
}
