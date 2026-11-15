// iOS helpers called from patched tModLoader code (tModLoader references ManagedHooks; not the reverse).
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ManagedHooks;

public static class IosSupport {
    // Fixed-modpack mode: a mod assembly shipped byte-identical (and AOT compiled) in the app is loaded ONCE into the default
    // load context (Mono only attaches AOT code there), and remembered as belonging to its tML ModLoadContext.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Assembly, System.Runtime.Loader.AssemblyLoadContext> owner = new();
    static readonly Dictionary<System.Runtime.Loader.AssemblyLoadContext, List<Assembly>> owned = new();

    public static Assembly LoadModAssembly(System.Runtime.Loader.AssemblyLoadContext ctx, Stream code, Stream? pdb) {
        var ms = new MemoryStream(); code.CopyTo(ms); var bytes = ms.ToArray();
        string? name = null;
        try {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes));
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            name = md.GetString(md.GetAssemblyDefinition().Name);
        } catch (Exception e) { Console.WriteLine("[ios] mod assembly name read failed: " + e.Message); }
        if (name != null) {
            var candidate = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (File.Exists(candidate)) {
                var app = File.ReadAllBytes(candidate);
                if (app.AsSpan().SequenceEqual(bytes)) {
                    Assembly a;
                    try { a = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name)); }
                    catch { a = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate); }
                    lock (owned) { owner.AddOrUpdate(a, ctx); if (!owned.TryGetValue(ctx, out var l)) owned[ctx] = l = new(); if (!l.Contains(a)) l.Add(a); }
                    Console.WriteLine($"[ios] mod assembly {name}: single AOT app copy in default context, owned by mod '{ctx.Name}'");
                    return a;
                }
                Console.WriteLine($"[ios] mod assembly {name}: app copy differs ({app.Length} vs {bytes.Length} bytes) -> interpreted from bytes");
            }
        }
        ms.Position = 0;
        return pdb == null ? ctx.LoadFromStream(ms) : ctx.LoadFromStream(ms, pdb);
    }
    // Replacements for AssemblyLoadContext.GetLoadContext / .Assemblies inside tML's AssemblyManager.
    public static System.Runtime.Loader.AssemblyLoadContext? GetLoadContext(Assembly a) =>
        owner.TryGetValue(a, out var c) ? c : System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a);
    public static IEnumerable<Assembly> GetAssemblies(System.Runtime.Loader.AssemblyLoadContext ctx) {
        lock (owned) if (owned.TryGetValue(ctx, out var l)) return ctx.Assemblies.Concat(l).Distinct().ToList();
        return ctx.Assemblies;
    }

    public static readonly List<string> StaticInitFailures = new();

    // Replacement for Terraria.Program.ForceStaticInitializers: one failing .cctor must not kill the warm-up thread/process.
    public static void ForceStaticInitializersSafe(Assembly assembly) {
        Type[] types;
        try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
        int ok = 0;
        foreach (var t in types) {
            if (t.IsGenericType) continue;
            try { RuntimeHelpers.RunClassConstructor(t.TypeHandle); ok++; }
            catch (Exception e) {
                var inner = e is TypeInitializationException { InnerException: not null } ? e.InnerException! : e;
                var line = $"{t.FullName}: {inner.GetType().Name}: {inner.Message}";
                lock (StaticInitFailures) StaticInitFailures.Add(line);
                Console.WriteLine("[ios] static init failed: " + line);
            }
        }
        Console.WriteLine($"[ios] static initializers: {ok} ok, {StaticInitFailures.Count} failed ({assembly.GetName().Name})");
    }
    // Called from Main..ctor (patch 23). TP_INITIAL_DISPLAY="WxH" is set by the iOS app from config.json; no-op elsewhere.
    public static void ConfigureInitialDisplay(object graphics) {
        try {
            var v = Environment.GetEnvironmentVariable("TP_INITIAL_DISPLAY"); if (string.IsNullOrEmpty(v) || graphics == null) return;
            var p = v.Split('x'); int w = int.Parse(p[0]), h = int.Parse(p[1]);
            var gt = graphics.GetType();
            gt.GetProperty("PreferredBackBufferWidth")?.SetValue(graphics, w); gt.GetProperty("PreferredBackBufferHeight")?.SetValue(graphics, h);
            var main = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "tModLoader")?.GetType("Terraria.Main");
            main?.GetField("screenWidth", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, w);
            main?.GetField("screenHeight", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, h);
            Console.WriteLine($"[ios] initial display {w}x{h}");
        } catch (Exception e) { Console.WriteLine("[ios] initial display failed: " + e.Message); }
    }
    static FieldInfo? sw, sh; static PropertyInfo? gdProp; static int lastW, lastH;
    public static void SyncSplashScreen(object graphics) {
        try {
            if (graphics == null || !OperatingSystem.IsIOS() && Environment.GetEnvironmentVariable("TP_INITIAL_DISPLAY") == null) return;
            gdProp ??= graphics.GetType().GetProperty("GraphicsDevice");
            var gd = gdProp?.GetValue(graphics); if (gd == null) return;
            var pp = gd.GetType().GetProperty("PresentationParameters")!.GetValue(gd)!;
            int w = (int)pp.GetType().GetProperty("BackBufferWidth")!.GetValue(pp)!, h = (int)pp.GetType().GetProperty("BackBufferHeight")!.GetValue(pp)!;
            if (sw == null) { var main = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "tModLoader").GetType("Terraria.Main")!;
                sw = main.GetField("screenWidth", BindingFlags.Public | BindingFlags.Static); sh = main.GetField("screenHeight", BindingFlags.Public | BindingFlags.Static); }
            int cw = (int)sw!.GetValue(null)!, ch = (int)sh!.GetValue(null)!;
            if (w > 0 && h > 0 && (cw != w || ch != h)) { sw.SetValue(null, w); sh!.SetValue(null, h); }
            if (w != lastW || h != lastH) { lastW = w; lastH = h; Console.WriteLine($"[ios] splash backbuffer {w}x{h} (screen was {cw}x{ch})"); }
        } catch (Exception e) { Console.WriteLine("[ios] splash sync failed: " + e.Message); }
    }
}
