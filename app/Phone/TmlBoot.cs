// Headless tModLoader boot on iOS: real Terraria.Program.LaunchGame in dedicated-server mode (FNA_PLATFORM_BACKEND=NONE),
// woven + iOS-patched tModLoader from the app bundle, mods from Documents/tml-save/Mods. Progress: Documents/tml-boot-status.txt
// plus tML's own log at Documents/tml-run/tModLoader-Logs/server.log.
using System.Reflection;
using System.Runtime.Loader;
namespace TerrariaRuntimeProbe;

public static class TmlBoot {
    static string Docs => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string StatusPath => Path.Combine(Docs, "tml-boot-status.txt");
    public static string RunDir => Path.Combine(Docs, "tml-run");
    public static string ServerLog => Path.Combine(RunDir, "tModLoader-Logs", "server.log");
    static string Override => Path.Combine(Docs, "tml-override");
    static string Bundle => Path.Combine(AppContext.BaseDirectory, "tml");
    static readonly object Gate = new();
    static bool started;
    public static void Status(string s) { try { lock (Gate) File.AppendAllText(StatusPath, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n"); } catch { } }

    public static void Start() {
        lock (Gate) { if (started) return; started = true; }
        try { File.Delete(StatusPath); } catch { }
        var save = Path.Combine(Docs, "tml-save"); var tsave = Path.Combine(Docs, "terraria-save");
        Directory.CreateDirectory(RunDir); Directory.CreateDirectory(tsave); Directory.CreateDirectory(Path.Combine(save, "Mods"));
        Status($"mods present: {string.Join(", ", Directory.GetFiles(Path.Combine(save, "Mods")).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length))}");
        var dirs = File.Exists(Path.Combine(Docs, "tml-use-override")) ? new[] { Override, AppContext.BaseDirectory, Bundle } : new[] { AppContext.BaseDirectory, Bundle };
        AssemblyLoadContext.Default.Resolving += (ctx, name) => {
            // Prefer an assembly already loaded in ANY context (tML's per-mod contexts load mods from .tmod); never load a second copy.
            foreach (var c in AssemblyLoadContext.All) foreach (var a in c.Assemblies)
                if (a.GetName().Name == name.Name) { Status($"resolve {name.Name} -> already loaded in '{c.Name}'"); return a; }
            foreach (var d in dirs) { var p = Path.Combine(d, name.Name + ".dll"); if (File.Exists(p)) { Status($"resolve {name.Name} -> {p.Replace(Docs, "Docs").Replace(AppContext.BaseDirectory, "App/")}"); return ctx.LoadFromAssemblyPath(p); } }
            Status($"resolve FAILED {name}"); return null;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Status("UNHANDLED " + e.ExceptionObject);
        var t = new Thread(() => {
            try {
                Environment.CurrentDirectory = RunDir;
                Environment.SetEnvironmentVariable("FNA_PLATFORM_BACKEND", "NONE");
                var consoleOut = new StreamWriter(Path.Combine(Docs, "tml-console.txt"), append: false) { AutoFlush = true };
                Console.SetOut(consoleOut); Console.SetError(consoleOut); Console.SetIn(new StringReader(""));
                Status($"cwd={RunDir.Replace(Docs, "Docs")} runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} dynamicCodeCompiled={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled}");
                // v12: tModLoader is an AOT-compiled app assembly; load it by name so the AOT image is used.
                // Override DLLs (interpreted, no AOT) are used only when Documents/tml-use-override exists.
                bool useOverride = File.Exists(Path.Combine(Docs, "tml-use-override")) && File.Exists(Path.Combine(Override, "tModLoader.dll"));
                var tml = useOverride ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Override, "tModLoader.dll")) : Assembly.Load("tModLoader");
                Status($"loaded {tml.FullName} from {(tml.Location ?? "").Replace(Docs, "Docs").Replace(AppContext.BaseDirectory, "App/")} (override={useOverride})");
                var launch = tml.GetType("Terraria.Program", true)!.GetMethod("LaunchGame", BindingFlags.Public | BindingFlags.Static)!;
                var args = new List<string> { "-server", "-nosteam", "-tmlsavedirectory", save, "-savedirectory", tsave };
                var extra = Path.Combine(Docs, "tml-args.txt"); // optional extra launch args, e.g. -autocreate 1 -world <path> -worldname X
                if (File.Exists(extra)) args.AddRange(File.ReadAllText(extra).Replace("{Docs}", Docs).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                Status("LaunchGame " + string.Join(" ", args).Replace(Docs, "Docs"));
                launch.Invoke(null, new object[] { args.ToArray(), false });
                Status("LaunchGame returned");
            } catch (Exception e) {
                while (e is TargetInvocationException { InnerException: not null }) e = e.InnerException!;
                Status("LaunchGame threw " + e);
            }
        }, 256 * 1024 * 1024) { Name = "tML Boot", IsBackground = true };
        t.Start();
        Status("boot thread started (256 MB stack)");
        // Every 30 s: which load context holds each copy of tModLoader/Calamity (detects duplicate loads).
        new Thread(() => {
            for (int i = 0; i < 40; i++) {
                Thread.Sleep(30000);
                try {
                    var rows = AssemblyLoadContext.All.SelectMany(c => c.Assemblies.Select(a => (c, a)))
                        .Where(x => x.a.GetName().Name is "tModLoader" or "CalamityMod" or "CalamityModMusic" or "TerrariaHooks" or "ManagedHooks")
                        .Select(x => $"{x.a.GetName().Name}@'{x.c.Name}'({(string.IsNullOrEmpty(x.a.Location) ? "bytes" : "file")})");
                    Status("assemblies: " + string.Join(", ", rows));
                } catch (Exception e) { Status("assembly report failed " + e.GetType().Name); }
            }
        }) { IsBackground = true, Name = "asm report" }.Start();
    }

    public static string Snapshot() {
        string Tail(string p, int n) { try { if (!File.Exists(p)) return "(none)"; var l = File.ReadAllLines(p); return string.Join("\n", l.Skip(Math.Max(0, l.Length - n))); } catch (Exception e) { return "(" + e.GetType().Name + ")"; } }
        return "STATUS\n" + Tail(StatusPath, 14) + "\n\nSERVER LOG\n" + Tail(ServerLog, 30);
    }
}
