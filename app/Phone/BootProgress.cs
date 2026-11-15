// Parses tML's own server.log into a progress model for the boot screen (no tML changes needed).
using System.Text.RegularExpressions;
namespace TerrariaRuntimeProbe;

public record BootState(string Phase, string Step, float Overall, float StepProgress, string Elapsed, bool Done, bool Failed, string LastLine);

public static class BootProgress {
    static readonly string[] ModStages = { "Finding Mods", "Sandboxing", "Constructing Mods", "Adding Content", "Resizing", "Configuring Content", "Finalizing Content", "Adding Recipes", "Mod Load Completed" };
    static readonly Regex Status = new(@"\[StatusText\]: (?:(\d+(?:\.\d+)?)% - )?([^-\r\n]*?)(?: - (\d+(?:\.\d+)?)[%.]?)?\s*$", RegexOptions.Compiled);
    static readonly Regex Time = new(@"^\[(\d\d):(\d\d):(\d\d)\.(\d{3})\]", RegexOptions.Compiled);

    public static BootState Read(string serverLog, DateTime started) {
        string[] lines; try { lines = File.Exists(serverLog) ? File.ReadAllLines(serverLog) : Array.Empty<string>(); } catch { lines = Array.Empty<string>(); }
        int modStage = -1; float world = -1; string step = "Starting tModLoader…"; bool worldPhase = false, loadingWorld = false, done = false, failed = false; string last = "";
        foreach (var l in lines) {
            if (l.Contains("/FATAL]") || l.Contains("Main engine crash")) { failed = true; last = l; }
            for (int i = 0; i < ModStages.Length; i++) if (l.Contains("[tML]: " + ModStages[i])) { modStage = Math.Max(modStage, i); step = l[(l.IndexOf("[tML]: ") + 7)..].Trim(); }
            if (l.Contains("Creating world") || l.Contains("Generating World")) { worldPhase = true; step = "Generating world"; }
            if (l.Contains("Loading World:") || l.Contains("Loading world data")) { loadingWorld = true; step = "Loading world"; }
            var m = Status.Match(l);
            if (m.Success && worldPhase && m.Groups[1].Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) { world = p; step = m.Groups[2].Value.Trim(); }
            if (l.Contains("Saving world data")) step = "Saving world";
            if (l.Contains("Settling liquids")) step = "Settling liquids";
            if (l.Contains("Listening on port")) step = "Starting server";
            if (l.Contains("Server started")) { done = true; step = "Server started"; }
            if (l.Length > 0 && !l.StartsWith(" ") && !l.StartsWith("\t")) last = l;
        }
        // Overall: mod load 0–30 %, world (generate or load) 30–97 %, server start 100 %.
        float modFrac = modStage < 0 ? 0 : (modStage + 1f) / ModStages.Length;
        float overall = modFrac * 0.30f;
        string phase = modStage < ModStages.Length - 1 ? "Loading mods" : "World";
        float stepProg = modFrac;
        if (modStage == ModStages.Length - 1) {
            if (worldPhase && world >= 0) { overall = 0.30f + world / 100f * 0.67f; stepProg = world / 100f; phase = "Generating world"; }
            else if (loadingWorld) { overall = 0.60f; stepProg = 0.5f; phase = "Loading world"; }
        }
        if (done) { overall = 1; stepProg = 1; phase = "Server running"; }
        var el = DateTime.Now - started;
        last = Time.Replace(last, "").Trim();
        if (last.Length > 140) last = last[..140];
        return new BootState(phase, step, Math.Clamp(overall, 0, 1), Math.Clamp(stepProg, 0, 1), $"{(int)el.TotalMinutes}:{el.Seconds:00}", done, failed, last);
    }
}
