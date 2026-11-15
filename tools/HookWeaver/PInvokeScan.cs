using Mono.Cecil;
static class PInvokeScan {
    public static void Run(string path, string outDir) {
        var mod = ModuleDefinition.ReadModule(path);
        var byLib = Audit.AllTypes(mod).SelectMany(t => t.Methods).Where(m => m.IsPInvokeImpl && m.PInvokeInfo != null)
            .GroupBy(m => m.PInvokeInfo.Module.Name).ToDictionary(g => g.Key, g => g.Select(m => m.PInvokeInfo.EntryPoint ?? m.Name).Distinct().OrderBy(x => x).ToList());
        Directory.CreateDirectory(outDir);
        foreach (var kv in byLib) { File.WriteAllLines(Path.Combine(outDir, kv.Key + ".txt"), kv.Value); Console.WriteLine($"{kv.Key}: {kv.Value.Count} entry points"); }
    }
}
