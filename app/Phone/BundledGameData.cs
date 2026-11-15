using System.Security.Cryptography;
namespace TerrariaRuntimeProbe;

static class BundledGameData {
    // Keep tModLoader's normal writable mod/save layout. Never delete worlds, players or settings.
    public static void PrepareMods() {
        string source=Path.Combine(AppContext.BaseDirectory,"BundledMods");
        if(!Directory.Exists(source)) return;
        string docs=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string target=Path.Combine(docs,"tml-save","Mods");Directory.CreateDirectory(target);
        foreach(var input in Directory.GetFiles(source,"*.tmod")) {
            string output=Path.Combine(target,Path.GetFileName(input));
            if(File.Exists(output)) {
                using var a=File.OpenRead(input);using var b=File.OpenRead(output);
                if(SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b)))continue;
                // Preserve any previous mod package before installing the fixed pack's matching input.
                File.Copy(output,output+".before-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),false);
            }
            string temp=output+".installing";File.Copy(input,temp,true);File.Move(temp,output,true);
        }
        string enabled=Path.Combine(target,"enabled.json");
        if(!File.Exists(enabled)) File.Copy(Path.Combine(source,"enabled.json"),enabled);
    }
}
