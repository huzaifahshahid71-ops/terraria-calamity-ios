// Game (client) mode: FNA on iOS via SDL2's UIKit backend (SDL_UIKitRunApp) + FNA3D OpenGL ES, then the real tModLoader client.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using ObjCRuntime;
namespace TerrariaRuntimeProbe;

public static class GameBoot {
    static string Docs => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string FlagPath => Path.Combine(Docs, "tml-client-mode");
    public static string StatusPath => Path.Combine(Docs, "game-boot-status.txt");
    public static bool HasBundledContent => File.Exists(Path.Combine(AppContext.BaseDirectory, "Content", "Images", "ChestStack_0.xnb"));
    public static string RunDir => Path.Combine(Docs, HasBundledContent ? "client-run" : "tml-run");
    public static string ContentDir => Path.Combine(RunDir, "Content");
    static SDL2.SDL.SDL_main_func? mainFunc; // keep alive
    static string[] launchArgs = Array.Empty<string>();
    public static bool InGameMode { get; private set; }

    // LiveContainer (Strata) connects the guest scene through the Info.plist scene delegate (our SceneDelegate),
    // not through SDL's app-delegate configuration; forward the scene to SDL so it can attach windows and start main.
    [DllImport("SDL2")] static extern void SDL_UIKit_SceneDidConnect(IntPtr scene);
    [DllImport("SDL2")] static extern int TP_TakeZoomSteps();
    public static void SceneDidConnect(IntPtr scene) { Status("scene connected -> SDL_UIKit_SceneDidConnect"); SDL_UIKit_SceneDidConnect(scene); }

    public static void Status(string s) { try { File.AppendAllText(StatusPath, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n"); } catch { } }

    public static bool ClientModeRequested() {
        try { return !File.Exists(Path.Combine(Docs, "tml-show-diagnostics")) && (HasBundledContent || Directory.Exists(Path.Combine(ContentDir, "Images"))); } catch { return false; }
    }

    public static void Run(string[] args) {
        InGameMode = true;
        try { File.Delete(StatusPath); } catch { }
        MobileDisplayDefaults.Prepare();
        BundledGameData.PrepareMods(); // Content is read directly from the bundle; saves remain in Documents.
        Status($"game mode: runtime={RuntimeInformation.FrameworkDescription} content={(HasBundledContent || Directory.Exists(Path.Combine(ContentDir, "Images")) ? "present" : "MISSING")}");
        var fw = Path.Combine(AppContext.BaseDirectory, "Frameworks");
        var map = new Dictionary<string, string> {
            ["SDL2"] = Path.Combine(fw, "SDL2.framework", "SDL2"), ["FNA3D"] = Path.Combine(fw, "FNA3D.framework", "FNA3D"),
            ["FAudio"] = Path.Combine(fw, "FAudio.framework", "FAudio"), ["libtheorafile"] = Path.Combine(fw, "libtheorafile.framework", "libtheorafile"),
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) => {
            var key = map.Keys.FirstOrDefault(k => name == k || name.StartsWith("lib" + k) || name.StartsWith(k + "."));
            if (key == null) { Status($"native unresolved: {name} (from {asm.GetName().Name})"); return IntPtr.Zero; }
            var h = NativeLibrary.Load(map[key]); Status($"native {name} -> {key}.framework (from {asm.GetName().Name})"); return h;
        };
        AssemblyLoadContext.Default.Resolving += (ctx, name) => {
            foreach (var c in AssemblyLoadContext.All) foreach (var a in c.Assemblies) if (a.GetName().Name == name.Name) return a;
            var p = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"); return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        Directory.CreateDirectory(RunDir);
        Environment.CurrentDirectory = RunDir;
        Environment.SetEnvironmentVariable("TML_IOS_VANILLA_DIR", HasBundledContent ? AppContext.BaseDirectory : RunDir);          // tML GOG content path = <dir>/Content
        Environment.SetEnvironmentVariable("FNA3D_FORCE_DRIVER", "OpenGL");          // OpenGL ES via SDL (Metal/MoltenVK later)
        Environment.SetEnvironmentVariable("FNA3D_OPENGL_FORCE_ES3", "1");
        Environment.SetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI", "1");
        try { // v34: full-screen intro - Main..ctor applies this before the splash draws (IosPatches 23)
            using var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Docs, "tml-save", "config.json")));
            int w = cfg.RootElement.GetProperty("DisplayWidth").GetInt32(), h = cfg.RootElement.GetProperty("DisplayHeight").GetInt32();
            Environment.SetEnvironmentVariable("TP_INITIAL_DISPLAY", $"{w}x{h}"); Status($"initial display {w}x{h}");
        } catch (Exception e) { Status("initial display: " + e.Message); }
        var save = Path.Combine(Docs, "tml-save"); var tsave = Path.Combine(Docs, "terraria-save"); Directory.CreateDirectory(tsave);
        var a = new List<string> { "-tmlsavedirectory", save, "-savedirectory", tsave };
        var extra = Path.Combine(Docs, "tml-client-args.txt");
        if (File.Exists(extra)) a.AddRange(File.ReadAllText(extra).Replace("{Docs}", Docs).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        launchArgs = a.ToArray();
        try {
            var consoleOut = new StreamWriter(Path.Combine(Docs, "tml-client-console.txt"), append: false) { AutoFlush = true };
            Console.SetOut(consoleOut); Console.SetError(consoleOut);
        } catch { }
        SDL2.SDL.SDL_SetHint("SDL_IOS_ORIENTATIONS", "LandscapeLeft LandscapeRight");
        mainFunc = SdlMain;
        Status("SDL_UIKitRunApp");
        SDL2.SDL.SDL_UIKitRunApp(0, IntPtr.Zero, mainFunc);
    }

    static Timer? memTimer, zoomTimer;
    // v34: touch zoom buttons (SDL overlay) -> Terraria's game zoom (same range as the in-game slider).
    static void PollZoom() {
        try {
            int n = TP_TakeZoomSteps(); if (n == 0) return;
            Terraria.Main.GameZoomTarget = Math.Clamp(Terraria.Main.GameZoomTarget + n * 0.25f, 1f, 2f);
        } catch { }
    }

    [MonoPInvokeCallback(typeof(SDL2.SDL.SDL_main_func))]
    static int SdlMain(int argc, IntPtr argv) {
        var uiContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        Status("game scheduler: " + (uiContext?.GetType().FullName ?? "none") + " -> no UIKit context; engine queues own asset transfers");
        try {
            Environment.CurrentDirectory = RunDir; // SDL's didFinishLaunching switched it to the (read-only) bundle
            Status($"SDL main entered (cwd reset to {RunDir.Replace(Docs, "Docs")}); loading tModLoader client");
            var tml = Assembly.Load("tModLoader");
            try {
                bool lazy = LazyTextures.Install(Docs);
                Status("lazy mod textures: " + (lazy ? "on" : "off"));
                memTimer = new Timer(_ => Status($"memory gc={GC.GetTotalMemory(false) / 1048576} MB {LazyTextures.Stats} baked-il={ManagedHooks.HookEndpoints.BakedUsed}"), null, 20000, 20000);
                ManagedHooks.HookEndpoints.Log ??= Status;
            } catch (Exception e) { Status("lazy textures install failed: " + e.Message); }
            zoomTimer = new Timer(_ => PollZoom(), null, 1000, 100);
            var launch = tml.GetType("Terraria.Program", true)!.GetMethod("LaunchGame", BindingFlags.Public | BindingFlags.Static)!;
            Status("LaunchGame (client) " + string.Join(" ", launchArgs).Replace(Docs, "Docs"));
            launch.Invoke(null, new object[] { launchArgs, false });
            Status("LaunchGame returned");
        } catch (Exception e) {
            while (e is TargetInvocationException { InnerException: not null }) e = e.InnerException!;
            Status("LaunchGame threw " + e);
        } finally { SynchronizationContext.SetSynchronizationContext(uiContext); }
        return 0;
    }
}
