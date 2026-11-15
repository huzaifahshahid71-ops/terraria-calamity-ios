// iOS memory: defer mod textures requested with AsyncLoad until first use (see tools/HookWeaver/LazyAssets.cs).
// Vanilla's own repository (Main.Assets) is untouched; it already loads item/NPC/projectile textures lazily.
// Disable with Documents/tp-lazy-textures-off (any content) or env TP_LAZY_TEXTURES=0.
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;

static class LazyTextures {
    static readonly ConditionalWeakTable<Asset<Texture2D>, AssetRepository> deferred = new();
    [ThreadStatic] static bool bypass;
    static int deferredCount, loadedCount, failedCount;
    public static string Stats => $"lazy textures: deferred={deferredCount} loaded-on-use={loadedCount} failed={failedCount}";

    public static bool Install(string docs) {
        if (Environment.GetEnvironmentVariable("TP_LAZY_TEXTURES") == "0" || File.Exists(Path.Combine(docs, "tp-lazy-textures-off"))) return false;
        AssetRepository.TPDefer = Defer;
        AssetRepository.TPTouch = Touch;
        return true;
    }

    static bool Defer(AssetRepository repo, object a) {
        if (bypass || a is not Asset<Texture2D> asset) return false;
        var vanilla = Terraria.Main.Assets;
        if (vanilla == null || ReferenceEquals(repo, vanilla)) return false;
        lock (deferred) { deferred.AddOrUpdate(asset, repo); }
        asset.State = AssetState.Loading;           // vanilla LoadItem/LoadNPC skip it; Value/IsLoaded call Touch
        asset.Wait = () => Touch(asset);            // Request(ImmediateLoad) and other waiters load it now
        Interlocked.Increment(ref deferredCount);
        return true;
    }

    static readonly System.Reflection.FieldInfo? isLoading = typeof(Terraria.ModLoader.ModLoader).GetField("isLoading", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    static bool ModLoading() { try { return isLoading?.GetValue(null) is true; } catch { return false; } }

    static void Touch(object a) {
        if (a is not Asset<Texture2D> asset) return;
        AssetRepository? repo;
        lock (deferred) { if (!deferred.TryGetValue(asset, out repo)) return; deferred.Remove(asset); }
        // Main thread, or the mod-loading worker (the main thread keeps transferring assets then): load and wait, so
        // load-time code that reads a texture's size gets the real texture. Other worker threads (parallel drawing/update)
        // must not block on the main thread: queue it and return the default texture this once.
        bool main = AssetRepository.IsMainThread || ModLoading();
        asset.Wait = null;
        asset.State = AssetState.NotLoaded;
        bool old = bypass; bypass = true;
        try { repo.Request<Texture2D>(asset.Name, main ? AssetRequestMode.ImmediateLoad : AssetRequestMode.AsyncLoad); Interlocked.Increment(ref loadedCount); }
        catch (Exception e) {
            Interlocked.Increment(ref failedCount);
            try { Console.WriteLine("[ios] lazy texture load failed " + asset.Name + ": " + e.Message); } catch { }
        }
        finally { bypass = old; }
    }
}
