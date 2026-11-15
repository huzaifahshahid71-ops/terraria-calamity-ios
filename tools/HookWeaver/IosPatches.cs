// iOS (Mono interpreter, no native detours, ReLogic reports Linux) startup patches for tModLoader.dll. Each must apply exactly.
using Mono.Cecil;
using Mono.Cecil.Cil;

static class IosPatches {
    public static List<string> Apply(ModuleDefinition tml, TypeDefinition endpoints, ModuleDefinition managedHooks) {
        var log = new List<string>();
        TypeDefinition T(string full) => Audit.AllTypes(tml).Single(t => t.FullName == full);
        MethodDefinition M(TypeDefinition t, string n) => t.Methods.Single(m => m.Name == n);
        MethodReference Imp(Type t, string name, params Type[] ps) => tml.ImportReference(t.GetMethod(name, ps)!);
        void Replace(MethodDefinition m, Action<ILProcessor> emit) {
            var b = m.Body; b.Instructions.Clear(); b.ExceptionHandlers.Clear(); b.Variables.Clear(); emit(b.GetILProcessor());
        }
        // 1. LoggingHooks.Init installs native Hooks (console pipe, stack trace prettifier, web requests, Process.Start)
        Replace(M(T("Terraria.ModLoader.Engine.LoggingHooks"), "Init"), il => il.Emit(OpCodes.Ret));
        log.Add("LoggingHooks.Init -> no-op");
        // 2. AssemblyRedirects..cctor: native Hook on CoreCLR-private ALC.ValidateAssemblyNameWithSimpleName -> null
        var cctor = M(T("Terraria.ModLoader.Core.AssemblyManager/AssemblyRedirects"), ".cctor");
        int n = 0;
        foreach (var i in cctor.Body.Instructions.ToList())
            if (i.OpCode == OpCodes.Newobj && i.Operand is MethodReference r && r.DeclaringType.FullName == "MonoMod.RuntimeDetour.Hook" && r.Parameters.Count == 2) {
                var il = cctor.Body.GetILProcessor();
                il.InsertBefore(i, il.Create(OpCodes.Pop)); il.InsertBefore(i, il.Create(OpCodes.Pop));
                i.OpCode = OpCodes.Ldnull; i.Operand = null; n++;
            }
        if (n != 1) throw new Exception($"AssemblyRedirects hook patch matched {n}");
        log.Add("AssemblyRedirects._hook -> null (no native detour)");
        // 3. MonoModHooks.Initialize subscribes DetourManager events (native backend) -> mark initialized only
        var mmh = T("Terraria.ModLoader.MonoModHooks");
        var isInit = mmh.Fields.Single(f => f.Name == "isInitialized");
        Replace(M(mmh, "Initialize"), il => { il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stsfld, isInit); il.Emit(OpCodes.Ret); });
        log.Add("MonoModHooks.Initialize -> no DetourManager subscriptions");
        // 4. MonoModHooks.Clear: HookEndpointManager.Clear -> ManagedHooks.HookEndpoints.Clear
        n = 0;
        foreach (var i in M(mmh, "Clear").Body.Instructions)
            if (i.Operand is MethodReference r && r.DeclaringType.FullName == "MonoMod.RuntimeDetour.HookGen.HookEndpointManager" && r.Name == "Clear") {
                i.Operand = tml.ImportReference(endpoints.Methods.Single(m => m.Name == "Clear")); n++;
            }
        if (n != 1) throw new Exception($"MonoModHooks.Clear patch matched {n}");
        log.Add("MonoModHooks.Clear -> HookEndpoints.Clear");
        // 5. InstallVerifier.Startup: ReLogic reports iOS as Linux and would check the Linux GOG hash. The supplied Mac GOG
        //    Terraria.exe (MD5 a2182f50...) is verified at build time instead; record GoG and continue.
        var iv = T("Terraria.ModLoader.Engine.InstallVerifier");
        var dp = iv.Fields.Single(f => f.Name == "DistributionPlatform");
        var vep = iv.Fields.Single(f => f.Name == "vanillaExePath");
        Replace(M(iv, "Startup"), il => {
            il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Stsfld, dp);
            // vanillaExePath = Path.Combine(Environment.GetEnvironmentVariable("TML_IOS_VANILLA_DIR") ?? Directory.GetCurrentDirectory(), "Terraria.exe")
            var have = il.Create(OpCodes.Ldstr, "Terraria.exe");
            il.Emit(OpCodes.Ldstr, "TML_IOS_VANILLA_DIR"); il.Emit(OpCodes.Call, Imp(typeof(Environment), "GetEnvironmentVariable", typeof(string)));
            il.Emit(OpCodes.Dup); il.Emit(OpCodes.Brtrue, have); il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Call, Imp(typeof(Directory), "GetCurrentDirectory"));
            il.Append(have); il.Emit(OpCodes.Call, Imp(typeof(Path), "Combine", typeof(string), typeof(string))); il.Emit(OpCodes.Stsfld, vep);
            il.Emit(OpCodes.Ret);
        });
        log.Add("InstallVerifier.Startup -> DistributionPlatform=GoG (hash verified at build time)");
        // 7. InstallVerifier..cctor: IsOSPlatform(Windows/OSX/Linux) are all false on iOS -> FatalExit(UnknownVerificationOS).
        //    Keep the two exe names, mark Steam unsupported, skip steam_api path discovery.
        var ivc = M(iv, ".cctor");
        FieldDefinition F(string n) => iv.Fields.Single(f => f.Name == n);
        Replace(ivc, il => {
            il.Emit(OpCodes.Ldstr, "Terraria.exe"); il.Emit(OpCodes.Stsfld, F("VanillaExe"));
            il.Emit(OpCodes.Ldstr, "Terraria_v1.4.5.8.exe"); il.Emit(OpCodes.Stsfld, F("CheckExe"));
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stsfld, F("IsSteamUnsupported")); il.Emit(OpCodes.Ret);
        });
        log.Add("InstallVerifier..cctor -> exe names only, Steam unsupported (no OS branch / FatalExit)");
        // 6. ModLoadContext: isCollectible true -> false (Mono does not support unloadable load contexts)
        n = 0;
        foreach (var ctor in T("Terraria.ModLoader.Core.AssemblyManager/ModLoadContext").Methods.Where(m => m.IsConstructor && !m.IsStatic))
            foreach (var i in ctor.Body.Instructions)
                if (i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Runtime.Loader.AssemblyLoadContext" && r.Name == ".ctor" && r.Parameters.Count == 2
                    && i.Previous.OpCode == OpCodes.Ldc_I4_1) { i.Previous.OpCode = OpCodes.Ldc_I4_0; n++; }
        if (n != 1) throw new Exception($"ModLoadContext collectible patch matched {n}");
        log.Add("ModLoadContext isCollectible -> false");
        // 8. ErrorReporting.MessageBoxShow uses SDL (absent on iOS); write the message to Documents/tml-fatal.txt instead.
        var er = T("Terraria.ModLoader.Engine.ErrorReporting");
        var mbs = er.Methods.Single(m => m.Name == "MessageBoxShow");
        var corlib = tml.TypeSystem.CoreLibrary;
        Replace(mbs, il => {
            il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Call, Imp(typeof(Environment), "GetFolderPath", typeof(Environment.SpecialFolder)));
            il.Emit(OpCodes.Ldstr, "tml-fatal.txt"); il.Emit(OpCodes.Call, Imp(typeof(Path), "Combine", typeof(string), typeof(string)));
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, "\n----\n"); il.Emit(OpCodes.Call, Imp(typeof(string), "Concat", typeof(string), typeof(string)));
            il.Emit(OpCodes.Call, Imp(typeof(File), "AppendAllText", typeof(string), typeof(string))); il.Emit(OpCodes.Ret);
        });
        log.Add("ErrorReporting.MessageBoxShow -> Documents/tml-fatal.txt (no SDL)");
        // 10. FNAFixes.Init only sets SDL hints/drivers; SDL2 is not loaded in headless server mode -> skip when Main.dedServ.
        var fna = M(T("Terraria.ModLoader.Engine.FNAFixes"), "Init");
        var dedServ = tml.ImportReference(T("Terraria.Main").Fields.Single(f => f.Name == "dedServ"));
        { var ip0 = fna.Body.GetILProcessor(); var f0 = fna.Body.Instructions[0];
          ip0.InsertBefore(f0, ip0.Create(OpCodes.Ldsfld, dedServ)); ip0.InsertBefore(f0, ip0.Create(OpCodes.Brfalse, f0)); ip0.InsertBefore(f0, ip0.Create(OpCodes.Ret)); }
        log.Add("FNAFixes.Init -> skipped when Main.dedServ (no SDL in headless server)");
        // 11. Logging.LogDir "tModLoader-Logs" is resolved by log4net against the (read-only) app bundle base dir -> make it absolute.
        var lcc = M(T("Terraria.ModLoader.Logging"), ".cctor");
        n = 0;
        foreach (var i in lcc.Body.Instructions.ToList())
            if (i.OpCode == OpCodes.Ldstr && (string)i.Operand == "tModLoader-Logs") {
                lcc.Body.GetILProcessor().InsertAfter(i, Instruction.Create(OpCodes.Call, Imp(typeof(Path), "GetFullPath", typeof(string)))); n++;
            }
        if (n != 1) throw new Exception($"LogDir patch matched {n}");
        log.Add("Logging.LogDir -> Path.GetFullPath(\"tModLoader-Logs\") (cwd-relative, not bundle-relative)");
        // 12. iOS has no terminal: Console title/colours/clear/encoding throw PlatformNotSupportedException. Neutralise call sites.
        int pops = 0, nops = 0, consts = 0;
        foreach (var m in Audit.AllTypes(tml).SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions)
                if (i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Console") {
                    switch (r.Name) {
                        case "set_Title": case "set_ForegroundColor": case "set_BackgroundColor": case "set_InputEncoding": case "set_OutputEncoding":
                            i.OpCode = OpCodes.Pop; i.Operand = null; pops++; break;
                        case "Clear": case "ResetColor":
                            i.OpCode = OpCodes.Nop; i.Operand = null; nops++; break;
                        case "get_BackgroundColor": case "get_ForegroundColor":
                            i.OpCode = OpCodes.Ldc_I4_0; i.Operand = null; consts++; break;
                    }
                }
        if (pops == 0 || nops == 0) throw new Exception("console patch matched nothing");
        log.Add($"Console title/colour/clear/encoding call sites neutralised (pop {pops}, nop {nops}, const {consts})");
        // 13. Process memory/process enumeration is unsupported on iOS: memory -> GC.GetTotalMemory(false), GetProcesses -> empty,
        //     Refresh / set_PriorityClass -> discarded. Process.Start etc. are only on desktop/UI paths and are left alone.
        var gcTotal = Imp(typeof(GC), "GetTotalMemory", typeof(bool));
        var processRef = tml.ImportReference(typeof(System.Diagnostics.Process));
        int mem = 0, procs = 0, disc = 0;
        foreach (var m in Audit.AllTypes(tml).SelectMany(t => t.Methods).Where(m => m.HasBody)) {
            var il = m.Body.GetILProcessor();
            foreach (var i in m.Body.Instructions.ToList())
                if (i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Diagnostics.Process") {
                    // Rewrite in place: 'i' becomes the first replacement instruction (branch targets stay valid), the rest follow it.
                    switch (r.Name) {
                        case "get_WorkingSet64": case "get_PrivateMemorySize64": case "get_VirtualMemorySize64": case "get_PagedMemorySize64": {
                            i.OpCode = OpCodes.Pop; i.Operand = null;
                            var a = il.Create(OpCodes.Ldc_I4_0); il.InsertAfter(i, a); il.InsertAfter(a, il.Create(OpCodes.Call, gcTotal)); mem++; break; }
                        case "GetProcesses" when r.Parameters.Count == 0:
                            i.OpCode = OpCodes.Ldc_I4_0; i.Operand = null; il.InsertAfter(i, il.Create(OpCodes.Newarr, processRef)); procs++; break;
                        case "Refresh":
                            i.OpCode = OpCodes.Pop; i.Operand = null; disc++; break;
                        case "set_PriorityClass":
                            i.OpCode = OpCodes.Pop; i.Operand = null; il.InsertAfter(i, il.Create(OpCodes.Pop)); disc++; break;
                    }
                }
        }
        if (mem == 0 || procs == 0) throw new Exception("process patch matched nothing");
        log.Add($"Process memory -> GC.GetTotalMemory ({mem}), GetProcesses -> empty ({procs}), Refresh/PriorityClass discarded ({disc})");
        // 14. AssemblyManager.Unload: ALC.Unload() throws for non-collectible contexts (patch 6) -> skip (no mod hot-reload on iOS)
        n = 0;
        foreach (var i in M(T("Terraria.ModLoader.Core.AssemblyManager"), "Unload").Body.Instructions)
            if (i.Operand is MethodReference r && r.Name == "Unload" && (r.DeclaringType.FullName == "System.Runtime.Loader.AssemblyLoadContext" || r.DeclaringType.Name == "ModLoadContext")) { i.OpCode = OpCodes.Pop; i.Operand = null; n++; }
        if (n != 1) throw new Exception($"ALC unload patch matched {n}");
        log.Add("AssemblyManager.Unload: AssemblyLoadContext.Unload() skipped (non-collectible on Mono)");
        // 15. Main.AddSignalTraps registers POSIX signal handlers (SIGINT/SIGTERM) -> PlatformNotSupported on iOS -> no-op
        Replace(M(T("Terraria.Main"), "AddSignalTraps"), il => il.Emit(OpCodes.Ret));
        log.Add("Main.AddSignalTraps -> no-op (no POSIX signals on iOS)");
        // 16. MonoLaunch.NativePlatformDir throws "Unknown OS" on iOS (hit via MonoLaunch..cctor during force-load) -> "iOS"
        { var npd = T("Terraria.MonoLaunch").Properties.Single(pp => pp.Name == "NativePlatformDir").GetMethod; var ip1 = npd.Body.GetILProcessor(); var f1 = npd.Body.Instructions[0];
          ip1.InsertBefore(f1, ip1.Create(OpCodes.Call, Imp(typeof(OperatingSystem), "IsIOS"))); ip1.InsertBefore(f1, ip1.Create(OpCodes.Brfalse, f1));
          ip1.InsertBefore(f1, ip1.Create(OpCodes.Ldstr, "iOS")); ip1.InsertBefore(f1, ip1.Create(OpCodes.Ret)); }
        log.Add("MonoLaunch.NativePlatformDir -> \"iOS\" when OperatingSystem.IsIOS() (desktop unchanged)");
        // 17. Program.ForceStaticInitializers: any throwing .cctor escapes the background force-load thread and kills the process.
        var safe = tml.ImportReference(managedHooks.GetType("ManagedHooks.IosSupport").Methods.Single(m => m.Name == "ForceStaticInitializersSafe"));
        Replace(T("Terraria.Program").Methods.Single(m => m.Name == "ForceStaticInitializers" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Reflection.Assembly"), il => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, safe); il.Emit(OpCodes.Ret); });
        log.Add("Program.ForceStaticInitializers -> ManagedHooks.IosSupport.ForceStaticInitializersSafe (per-type try/catch, logged)");
        // 18. ModLoadContext.LoadAssembly: LoadFromStream -> IosSupport.LoadModAssembly (use identical AOT-compiled app copy if shipped)
        var lma = tml.ImportReference(managedHooks.GetType("ManagedHooks.IosSupport").Methods.Single(m => m.Name == "LoadModAssembly"));
        n = 0;
        foreach (var i in T("Terraria.ModLoader.Core.AssemblyManager/ModLoadContext").Methods.Single(m => m.Name == "LoadAssembly").Body.Instructions)
            if (i.Operand is MethodReference r && r.Name == "LoadFromStream" && r.Parameters.Count == 2) { i.OpCode = OpCodes.Call; i.Operand = lma; n++; }
        if (n != 1) throw new Exception($"LoadModAssembly patch matched {n}");
        log.Add("ModLoadContext.LoadAssembly: LoadFromStream -> IosSupport.LoadModAssembly (single AOT copy in default context when identical)");
        // 19/20. Mod assemblies may now live in the default context: route GetLoadContext(asm) / ctx.Assemblies through IosSupport.
        var ios = managedHooks.GetType("ManagedHooks.IosSupport");
        var glc = tml.ImportReference(ios.Methods.Single(m => m.Name == "GetLoadContext"));
        var gas = tml.ImportReference(ios.Methods.Single(m => m.Name == "GetAssemblies"));
        int g1 = 0, g2 = 0;
        foreach (var m in Audit.AllTypes(tml).SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions)
                if (i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Runtime.Loader.AssemblyLoadContext") {
                    if (r.Name == "GetLoadContext") { i.OpCode = OpCodes.Call; i.Operand = glc; g1++; }
                    else if (r.Name == "get_Assemblies" && m.DeclaringType.FullName.StartsWith("Terraria.ModLoader.Core.AssemblyManager")) { i.OpCode = OpCodes.Call; i.Operand = gas; g2++; }
                }
        if (g1 != 6 || g2 != 3) throw new Exception($"load-context patches matched GetLoadContext={g1} Assemblies={g2}");
        log.Add($"AssemblyLoadContext.GetLoadContext ({g1}) / ModLoadContext.Assemblies ({g2}) -> IosSupport (mod ownership of default-context assemblies)");
        // 21. FNA resolves relative TitleContainer paths against the app bundle on iOS.
        // Content lives in Documents/tml-run while developing: make both vanilla and override
        // content roots absolute before ContentManager stores them or caches image paths.
        var contentCtor = T("Terraria.ModLoader.Engine.TMLContentManager").Methods.Single(m => m.Name == ".ctor" && m.Parameters.Count == 3);
        var contentIl = contentCtor.Body.GetILProcessor();
        var contentFirst = contentCtor.Body.Instructions[0];
        contentIl.InsertBefore(contentFirst, contentIl.Create(OpCodes.Ldarg_2));
        contentIl.InsertBefore(contentFirst, contentIl.Create(OpCodes.Call, Imp(typeof(Path), "GetFullPath", typeof(string))));
        contentIl.InsertBefore(contentFirst, contentIl.Create(OpCodes.Starg, contentCtor.Parameters[1]));
        log.Add("TMLContentManager rootDirectory -> absolute path (FNA audio/content must not resolve against app bundle)");
        // 22. Desktop FnaIme only toggles a managed flag. iOS additionally needs SDL to
        // show/hide its software keyboard when Terraria enters/leaves a text field.
        var fnaAssembly = tml.AssemblyResolver.Resolve(tml.AssemblyReferences.Single(a => a.Name == "FNA"));
        var textInput = fnaAssembly.MainModule.GetType("Microsoft.Xna.Framework.Input.TextInputEXT");
        var ime = M(T("Terraria.Main"), "HandleIME");
        int imeEnable = 0, imeDisable = 0;
        foreach (var instruction in ime.Body.Instructions.ToArray()) {
            if (instruction.Operand is not MethodReference call || call.DeclaringType.FullName != "ReLogic.Localization.IME.IImeService") continue;
            if (call.Name != "Enable" && call.Name != "Disable") continue;
            var name = call.Name == "Enable" ? "StartTextInput" : "StopTextInput";
            var bridge = tml.ImportReference(textInput.Methods.Single(m => m.Name == name && m.Parameters.Count == 0));
            ime.Body.GetILProcessor().InsertAfter(instruction, Instruction.Create(OpCodes.Call, bridge));
            if (call.Name == "Enable") imeEnable++; else imeDisable++;
        }
        if (imeEnable != 1 || imeDisable != 1) throw new Exception($"iOS IME patch matched enable={imeEnable} disable={imeDisable}");
        log.Add("Main.HandleIME -> FNA StartTextInput/StopTextInput for iOS software keyboard");
        // 23. Initial display size: the splash/intro draws before LoadSettings applies config.json, at the 1152x864 default,
        // which left the intro in part of the screen. Apply the configured size right after Main creates its GraphicsDeviceManager.
        { var ctor = T("Terraria.Main").Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
          var st = ctor.Body.Instructions.Single(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "graphics");
          var cfg = tml.ImportReference(managedHooks.GetType("ManagedHooks.IosSupport").Methods.Single(m => m.Name == "ConfigureInitialDisplay"));
          var ipc = ctor.Body.GetILProcessor(); var ld = ipc.Create(OpCodes.Ldsfld, (FieldReference)st.Operand);
          ipc.InsertAfter(st, ld); ipc.InsertAfter(ld, ipc.Create(OpCodes.Call, cfg)); }
        log.Add("Main..ctor: IosSupport.ConfigureInitialDisplay(graphics) after GraphicsDeviceManager creation (full-screen intro)");
        // 24. The splash draws in back-buffer pixels using Main.screenWidth/Height, which lag behind the iOS window's size changes
        // during startup. Sync them to the real back buffer at the start of every splash frame.
        { var ds = T("Terraria.Main").Methods.Single(m => m.Name == "DrawSplash" && m.Parameters.Count == 1);
          var sync = tml.ImportReference(managedHooks.GetType("ManagedHooks.IosSupport").Methods.Single(m => m.Name == "SyncSplashScreen"));
          var g = T("Terraria.Main").Fields.Single(f => f.Name == "graphics");
          var ipd = ds.Body.GetILProcessor(); var f0 = ds.Body.Instructions[0];
          ipd.InsertBefore(f0, ipd.Create(OpCodes.Ldsfld, g)); ipd.InsertBefore(f0, ipd.Create(OpCodes.Call, sync)); }
        log.Add("Main.DrawSplash: IosSupport.SyncSplashScreen(graphics) (screen size = back buffer during the intro)");
        // 9. Diagnostic: log4net internal debugging on (silent appender failures become visible on the console)
        var l4n = tml.AssemblyResolver.Resolve(tml.AssemblyReferences.Single(a => a.Name == "log4net"));
        var logLog = l4n.MainModule.GetType("log4net.Util.LogLog");
        var setDbg = tml.ImportReference(logLog.Methods.Single(m => m.Name == "set_InternalDebugging"));
        var init = T("Terraria.ModLoader.Logging").Methods.Single(m => m.Name == "Init" && m.Parameters.Count == 1);
        var first = init.Body.Instructions[0]; var ip = init.Body.GetILProcessor();
        ip.InsertBefore(first, ip.Create(OpCodes.Ldc_I4_1)); ip.InsertBefore(first, ip.Create(OpCodes.Call, setDbg));
        log.Add("Logging.Init: log4net LogLog.InternalDebugging = true (diagnostic)");
        return log;
    }
}
