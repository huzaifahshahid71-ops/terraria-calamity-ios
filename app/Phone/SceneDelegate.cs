namespace TerrariaRuntimeProbe;
[Register("SceneDelegate")]
public class SceneDelegate : UIResponder, IUIWindowSceneDelegate {
    [Export("window")] public UIWindow? Window {get;set;}
    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions options) {
        if(scene is not UIWindowScene ws) return;
        if(GameBoot.InGameMode){ GameBoot.SceneDidConnect(ws.Handle); return; } // game mode: SDL owns the scene
        Window = new UIWindow(ws) {RootViewController=new ProbeController()}; Window.MakeKeyAndVisible();
    }
}
public class ProbeController : UIViewController {
    UITextView text = new(); UIButton run=UIButton.FromType(UIButtonType.System), share=UIButton.FromType(UIButtonType.System), boot=UIButton.FromType(UIButtonType.System);
    NSTimer? bootTimer;
    UIButton install=UIButton.FromType(UIButtonType.System), game=UIButton.FromType(UIButtonType.System);
    // Boot progress screen
    UIView bootPanel = new(); UILabel phaseLabel = new(), stepLabel = new(), pctLabel = new(), timeLabel = new(), lastLabel = new();
    UIProgressView overallBar = new(UIProgressViewStyle.Default), stepBar = new(UIProgressViewStyle.Bar);
    DateTime bootStarted;
    string ReportPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Terraria-Runtime-Report.json");
    public override void ViewDidLoad() {
        base.ViewDidLoad();View!.BackgroundColor=UIColor.SystemBackground;
        text.Editable=false;text.Font=UIFont.FromName("Menlo",12) ?? UIFont.SystemFontOfSize(12);
        run.SetTitle("Run JIT-disabled runtime checks",UIControlState.Normal);
        share.SetTitle("Share diagnostic report",UIControlState.Normal);
        boot.SetTitle("Boot tML headless server (Calamity)",UIControlState.Normal);
        install.SetTitle("Install game content (Documents/Content.zip)",UIControlState.Normal);
        game.SetTitle("Launch game (tModLoader client) — app will close",UIControlState.Normal);
        install.TouchUpInside+=(_,_)=>{
            try {
                var zip=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Content.zip");
                if(!File.Exists(zip)){text.Text="Documents/Content.zip not found.";return;}
                install.Enabled=false; bootPanel.Hidden=false; phaseLabel.Text="Installing content"; var started=DateTime.Now;
                Task.Run(()=>{
                    try {
                        var dest=GameBoot.ContentDir; if(Directory.Exists(dest)) Directory.Delete(dest,true); Directory.CreateDirectory(dest);
                        using var z=System.IO.Compression.ZipFile.OpenRead(zip); int n=0,total=z.Entries.Count; long bytes=0;
                        foreach(var e in z.Entries){
                            var path=Path.GetFullPath(Path.Combine(GameBoot.RunDir,e.FullName));
                            if(!path.StartsWith(Path.GetFullPath(GameBoot.RunDir)+Path.DirectorySeparatorChar)) throw new IOException("zip entry outside target: "+e.FullName);
                            if(e.FullName.EndsWith("/")){Directory.CreateDirectory(path);continue;}
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.Compression.ZipFileExtensions.ExtractToFile(e,path,true); n++; bytes+=e.Length;
                            if(n%200==0||n==total){ var nn=n; var bb=bytes; BeginInvokeOnMainThread(()=>{ overallBar.SetProgress((float)nn/total,false); stepBar.SetProgress((float)nn/total,false); pctLabel.Text=$"{nn*100/total}%"; stepLabel.Text=$"{nn}/{total} files, {bb/1048576} MB"; timeLabel.Text=$"Elapsed {(DateTime.Now-started):m\\:ss}"; }); }
                        }
                        File.WriteAllText(Path.Combine(GameBoot.RunDir,"content-installed.txt"),$"{n} files {bytes} bytes {DateTime.Now:O}");
                        BeginInvokeOnMainThread(()=>{ phaseLabel.Text="Content installed"; phaseLabel.TextColor=UIColor.SystemGreen; install.Enabled=true; });
                    } catch(Exception ex){ CrashLog.Write("content install",ex); BeginInvokeOnMainThread(()=>{ phaseLabel.Text="Install failed"; phaseLabel.TextColor=UIColor.SystemRed; text.Text=ex.ToString(); install.Enabled=true; }); }
                });
            } catch(Exception ex){ CrashLog.Write("install handler",ex); text.Text=ex.ToString(); }
        };
        game.TouchUpInside+=(_,_)=>{
            try { File.WriteAllText(GameBoot.FlagPath,"1"); text.Text="Game mode armed. Closing; reopen the app to start tModLoader."; NSTimer.CreateScheduledTimer(0.8,_=>Environment.Exit(0)); }
            catch(Exception ex){ CrashLog.Write("game handler",ex); text.Text=ex.ToString(); }
        };
        boot.TouchUpInside+=(_,_)=>{
            try {
                boot.Enabled=false; run.Enabled=false;
                TmlBoot.Start(); bootStarted=DateTime.Now; bootPanel.Hidden=false;
                bootTimer=NSTimer.CreateRepeatingScheduledTimer(1.0,_=>{ try {
                    var st=BootProgress.Read(TmlBoot.ServerLog,bootStarted);
                    phaseLabel.Text=st.Failed?"Failed":st.Phase; phaseLabel.TextColor=st.Failed?UIColor.SystemRed:(st.Done?UIColor.SystemGreen:UIColor.Label);
                    stepLabel.Text=st.Step; pctLabel.Text=$"{st.Overall*100:0}%"; timeLabel.Text="Elapsed "+st.Elapsed;
                    overallBar.SetProgress(st.Overall,true); stepBar.SetProgress(st.StepProgress,true); lastLabel.Text=st.LastLine;
                    text.Text=TmlBoot.Snapshot();
                } catch(Exception ex){ CrashLog.Write("boot timer",ex); } });
            } catch(Exception ex){ CrashLog.Write("Boot handler",ex); text.Text=ex.ToString(); }
        };
        run.TouchUpInside+=async (_,_)=>{
            // async void handler: every statement is guarded so nothing can escape into UIKit.
            try {
                run.Enabled=false;
                Checks.Progress=step=>{
                    // Runs on the worker thread: build the report snapshot here, hand only an immutable string to the UI.
                    string report;
                    try { report=Checks.Report(); File.WriteAllText(ReportPath,report); File.WriteAllText(ReportPath+".stage.txt",step); }
                    catch(Exception ex){ CrashLog.Write("Progress(worker) "+step,ex); report="(report failed: "+ex.GetType().Name+")"; }
                    var shown=step+"\n\n"+report;
                    BeginInvokeOnMainThread(()=>{ try { text.Text=shown; } catch(Exception ex){ CrashLog.Write("Progress(main) "+step,ex); } });
                };
                CalamityHooks.Dirs=new[]{Path.Combine(AppContext.BaseDirectory,"tml"),AppContext.BaseDirectory};
                await Task.Run(Checks.Run);
                var final=Checks.Report();
                File.WriteAllText(ReportPath,final);
                File.WriteAllText(ReportPath+".stage.txt","All checks complete; UI survived");
                text.Text="Checks complete\n\n"+final;
            }
            catch(Exception ex){
                CrashLog.Write("Run handler",ex);
                try { text.Text=ex.ToString(); File.WriteAllText(ReportPath+".error.txt",ex.ToString()); } catch {}
            }
            finally { Checks.Progress=null; run.Enabled=true; }
        };
        share.TouchUpInside+=(_,_)=>{
            try {
                if(!File.Exists(ReportPath)){text.Text="Run the checks first.";return;}
                var items=new List<NSObject>{NSUrl.FromFilename(ReportPath)};
                foreach(var extra in new[]{ReportPath+".stage.txt",ReportPath+".error.txt",CrashLog.PathName,TmlBoot.StatusPath,TmlBoot.ServerLog,GameBoot.StatusPath,Path.Combine(GameBoot.RunDir,"tModLoader-Logs","client.log")}) if(File.Exists(extra)) items.Add(NSUrl.FromFilename(extra));
                var vc=new UIActivityViewController(items.ToArray(),null);
                if(vc.PopoverPresentationController is {} pop){pop.SourceView=share;pop.SourceRect=share.Bounds;}
                PresentViewController(vc,true,null);
            } catch(Exception ex){ CrashLog.Write("Share handler",ex); text.Text=ex.ToString(); }
        };
        var crash=File.Exists(CrashLog.PathName)?"PREVIOUS CRASH LOG:\n"+File.ReadAllText(CrashLog.PathName)+"\n\n":"";
        text.Text=crash+(File.Exists(ReportPath)?"Previous report:\n"+File.ReadAllText(ReportPath):"Terraria runtime feasibility diagnostic\n\nKeep JIT disabled. Tap Run. This contains no game files. If it closes, the last stage, completed checks and any crash exception are saved in Documents.");
        phaseLabel.Font=UIFont.BoldSystemFontOfSize(22); pctLabel.Font=UIFont.MonospacedDigitSystemFontOfSize(22,UIFontWeight.Bold); pctLabel.TextAlignment=UITextAlignment.Right;
        stepLabel.Font=UIFont.SystemFontOfSize(15); stepLabel.TextColor=UIColor.SecondaryLabel; timeLabel.Font=UIFont.MonospacedDigitSystemFontOfSize(13,UIFontWeight.Regular); timeLabel.TextColor=UIColor.SecondaryLabel;
        lastLabel.Font=UIFont.FromName("Menlo",10) ?? UIFont.SystemFontOfSize(10); lastLabel.TextColor=UIColor.TertiaryLabel; lastLabel.Lines=2;
        overallBar.Transform=CoreGraphics.CGAffineTransform.MakeScale(1,3); bootPanel.Hidden=true;
        foreach(var v in new UIView[]{phaseLabel,pctLabel,overallBar,stepLabel,stepBar,timeLabel,lastLabel}){v.TranslatesAutoresizingMaskIntoConstraints=false;bootPanel.AddSubview(v);}
        NSLayoutConstraint.ActivateConstraints(new[]{
            phaseLabel.TopAnchor.ConstraintEqualTo(bootPanel.TopAnchor),phaseLabel.LeadingAnchor.ConstraintEqualTo(bootPanel.LeadingAnchor),
            pctLabel.FirstBaselineAnchor.ConstraintEqualTo(phaseLabel.FirstBaselineAnchor),pctLabel.TrailingAnchor.ConstraintEqualTo(bootPanel.TrailingAnchor),
            overallBar.TopAnchor.ConstraintEqualTo(phaseLabel.BottomAnchor,10),overallBar.LeadingAnchor.ConstraintEqualTo(bootPanel.LeadingAnchor),overallBar.TrailingAnchor.ConstraintEqualTo(bootPanel.TrailingAnchor),
            stepLabel.TopAnchor.ConstraintEqualTo(overallBar.BottomAnchor,12),stepLabel.LeadingAnchor.ConstraintEqualTo(bootPanel.LeadingAnchor),stepLabel.TrailingAnchor.ConstraintEqualTo(bootPanel.TrailingAnchor),
            stepBar.TopAnchor.ConstraintEqualTo(stepLabel.BottomAnchor,6),stepBar.LeadingAnchor.ConstraintEqualTo(bootPanel.LeadingAnchor),stepBar.TrailingAnchor.ConstraintEqualTo(bootPanel.TrailingAnchor),
            timeLabel.TopAnchor.ConstraintEqualTo(stepBar.BottomAnchor,8),timeLabel.LeadingAnchor.ConstraintEqualTo(bootPanel.LeadingAnchor),
            lastLabel.TopAnchor.ConstraintEqualTo(timeLabel.BottomAnchor,6),lastLabel.LeadingAnchor.ConstraintEqualTo(bootPanel.LeadingAnchor),lastLabel.TrailingAnchor.ConstraintEqualTo(bootPanel.TrailingAnchor),
            lastLabel.BottomAnchor.ConstraintEqualTo(bootPanel.BottomAnchor)});
        foreach(var v in new UIView[]{run,share,boot,install,game,bootPanel,text}){v.TranslatesAutoresizingMaskIntoConstraints=false;View.AddSubview(v);}
        var safe=View.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(new[]{run.TopAnchor.ConstraintEqualTo(safe.TopAnchor,16),run.CenterXAnchor.ConstraintEqualTo(safe.CenterXAnchor),share.TopAnchor.ConstraintEqualTo(run.BottomAnchor,8),share.CenterXAnchor.ConstraintEqualTo(safe.CenterXAnchor),boot.TopAnchor.ConstraintEqualTo(share.BottomAnchor,8),boot.CenterXAnchor.ConstraintEqualTo(safe.CenterXAnchor),install.TopAnchor.ConstraintEqualTo(boot.BottomAnchor,8),install.CenterXAnchor.ConstraintEqualTo(safe.CenterXAnchor),game.TopAnchor.ConstraintEqualTo(install.BottomAnchor,8),game.CenterXAnchor.ConstraintEqualTo(safe.CenterXAnchor),bootPanel.TopAnchor.ConstraintEqualTo(game.BottomAnchor,16),bootPanel.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor,16),bootPanel.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor,-16),text.TopAnchor.ConstraintEqualTo(bootPanel.BottomAnchor,12),text.LeftAnchor.ConstraintEqualTo(safe.LeftAnchor,12),text.RightAnchor.ConstraintEqualTo(safe.RightAnchor,-12),text.BottomAnchor.ConstraintEqualTo(safe.BottomAnchor,-12)});
    }
}
