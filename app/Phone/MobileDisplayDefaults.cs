using System.Text.Json;
namespace TerrariaRuntimeProbe;

static class MobileDisplayDefaults {
    public static void Prepare() {
        string docs=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string marker=Path.Combine(docs,"mobile-display-v1.json");
        if(File.Exists(marker)) return; // Normal graphics preferences remain user-adjustable afterwards.
        string config=Path.Combine(docs,"tml-save","config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        using var previous=JsonDocument.Parse(File.Exists(config)?File.ReadAllText(config):"{}");
        var bounds=UIScreen.MainScreen.Bounds;
        double w=(double)bounds.Width,h=(double)bounds.Height,aspect=Math.Max(w,h)/Math.Min(w,h);
        int height=720, width=Math.Clamp((int)Math.Round(height*aspect),1280,1920);
        string backup=config+".before-mobile-layout";
        if(File.Exists(config)&&!File.Exists(backup)) File.Copy(config,backup,false);
        string temp=config+".mobile-installing";
        using(var file=File.Create(temp)) using(var writer=new Utf8JsonWriter(file,new JsonWriterOptions{Indented=true})) {
            var changed=new HashSet<string>{"DisplayWidth","DisplayHeight","Fullscreen","WindowMaximized","WindowBorderless","UIScale","ResetDefaultUIScale"};
            writer.WriteStartObject();
            foreach(var property in previous.RootElement.EnumerateObject()) if(!changed.Contains(property.Name)) property.WriteTo(writer);
            writer.WriteNumber("DisplayWidth",width);writer.WriteNumber("DisplayHeight",height);
            writer.WriteBoolean("Fullscreen",false);writer.WriteBoolean("WindowMaximized",false);writer.WriteBoolean("WindowBorderless",false);
            writer.WriteNumber("UIScale",1.0);writer.WriteBoolean("ResetDefaultUIScale",false);writer.WriteEndObject();
        }
        File.Move(temp,config,true);
        File.WriteAllText(marker,$"{{\"width\":{width},\"height\":{height}}}");
    }
}
