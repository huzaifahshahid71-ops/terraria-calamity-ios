// BakeVerify <dir> : JIT-compiles every __mh_baked_* method of <dir>/tModLoader.dll (CoreCLR checks IL validity and member access).
using System.Reflection; using System.Runtime.CompilerServices; using System.Runtime.Loader;
var dir = Path.GetFullPath(args[0]);
var files = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories).GroupBy(f => Path.GetFileNameWithoutExtension(f)).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Length).First());
AssemblyLoadContext.Default.Resolving += (c, n) => files.TryGetValue(n.Name!, out var p) ? c.LoadFromAssemblyPath(p) : null;
var tml = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir, "tModLoader.dll"));
int ok = 0, bad = 0;
Type[] types; try { types = tml.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
foreach (var t in types) foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name.StartsWith("__mh_baked_"))) {
    try { RuntimeHelpers.PrepareMethod(m.MethodHandle); ok++; } catch (Exception e) { bad++; Console.WriteLine($"BAD {t.FullName}::{m.Name}: {e.GetType().Name}: {e.Message}"); }
}
Console.WriteLine($"baked methods: {ok} ok, {bad} bad");
