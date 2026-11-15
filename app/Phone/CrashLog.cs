namespace TerrariaRuntimeProbe;

// Appends every unhandled/marshaled exception (with thread and source) to Documents/Terraria-Runtime-Crash.txt.
public static class CrashLog {
    static readonly object Gate = new();
    public static string PathName => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Terraria-Runtime-Crash.txt");

    public static void Write(string source, object? error) {
        try {
            var t = Thread.CurrentThread;
            var entry = $"=== {DateTime.UtcNow:O} source={source} thread={t.ManagedThreadId} name={t.Name ?? "-"} main={NSThread.IsMain}\n{error}\n\n";
            lock (Gate) File.AppendAllText(PathName, entry);
        } catch { /* never throw from the crash logger */ }
    }

    public static void Install() {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"AppDomain.UnhandledException terminating={e.IsTerminating}", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("TaskScheduler.UnobservedTaskException", e.Exception); e.SetObserved(); };
        // Managed exceptions crossing into Objective-C (e.g. thrown from a UIKit callback) are reported here first.
        ObjCRuntime.Runtime.MarshalManagedException += (_, e) => Write($"MarshalManagedException mode={e.ExceptionMode}", e.Exception);
        ObjCRuntime.Runtime.MarshalObjectiveCException += (_, e) => Write($"MarshalObjectiveCException mode={e.ExceptionMode}", e.Exception);
    }
}
