using Mono.Cecil; using Mono.Cecil.Cil;
static class ConsoleScan {
    public static void Run(string path, string typeName = "System.Console") {
        var mod = ModuleDefinition.ReadModule(path);
        var c = new SortedDictionary<string,int>();
        foreach (var m in Audit.AllTypes(mod).SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions)
                if (i.Operand is MethodReference r && r.DeclaringType.FullName == typeName) { var k = r.Name + "(" + string.Join(",", r.Parameters.Select(p => p.ParameterType.Name)) + ")"; c[k] = c.GetValueOrDefault(k) + 1; if (typeName != "System.Console") Console.WriteLine($"   {m.DeclaringType.FullName}::{m.Name} -> {k}"); }
        foreach (var kv in c) Console.WriteLine($"{kv.Value,4} {kv.Key}");
    }
}
