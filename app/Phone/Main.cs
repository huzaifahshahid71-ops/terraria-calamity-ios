using TerrariaRuntimeProbe;

CrashLog.Install();
// Normal launch enters the game directly. The diagnostic UI is opt-in via a Documents flag.
if (GameBoot.ClientModeRequested()) GameBoot.Run(args);
else UIApplication.Main(args, null, typeof(AppDelegate));
