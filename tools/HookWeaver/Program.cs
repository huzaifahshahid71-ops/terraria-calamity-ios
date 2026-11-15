var root = Environment.GetEnvironmentVariable("PORT_KIT_ROOT") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
Directory.CreateDirectory(Path.Combine(root, "metadata"));
string refs = Path.Combine(root, "inputs/loader-references");
string[] mods = { Path.Combine(root, "inputs/mods/CalamityMod/CalamityMod.dll"), Path.Combine(root, "inputs/mods/CalamityModMusic/CalamityModMusic.dll") };
switch (args.FirstOrDefault()) {
    case "weave": return Weave.Run(refs, Path.Combine(root, "metadata/hook-targets.json"), Path.Combine(root, "tools/ManagedHooks/bin/Release/net8.0/ManagedHooks.dll"), Path.Combine(root, "build/woven"),
        new[] { "Terraria.ModLoader.LocalizationLoader::ExtractLocalizationFiles" });
    case "weave-ios": return Weave.Run(refs, Path.Combine(root, "metadata/hook-targets.json"), Path.Combine(root, "tools/ManagedHooks/bin/Release/net8.0/ManagedHooks.dll"), Path.Combine(root, "build/woven-ios"),
        new[] { "Terraria.ModLoader.LocalizationLoader::ExtractLocalizationFiles" }, ios: true);
    case "osscan": OsScan.Run(args[1]); break;
    case "consolescan": ConsoleScan.Run(args[1], args.Length > 2 ? args[2] : "System.Console"); break;
    case "pinvokes": PInvokeScan.Run(args[1], args[2]); break;
    case "tml": TmlAudit.Run(refs); break;
    case "audit": Audit.Run(refs, mods, Path.Combine(root, "metadata/hook-targets.json")); break;
    default: Console.Error.WriteLine("usage: HookWeaver audit"); return 2;
}
return 0;
