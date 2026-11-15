using System.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using OpCodes = System.Reflection.Emit.OpCodes;

public static class Checks
{
    public record Result(string Name, bool Passed, string Detail);
    // Results is written by the worker thread and read by UI callbacks; all access goes through Gate.
    static readonly object Gate = new();
    static readonly List<Result> Results = new();
    public static Action<string>? Progress;
    static void Add(Result r) { lock(Gate) Results.Add(r); }
    public static Result[] Snapshot() { lock(Gate) return Results.ToArray(); }
    static void Test(string name, Action body) {
        Progress?.Invoke(name);
        try { body(); Add(new(name, true, "Passed")); }
        catch(Exception e) { Add(new(name, false, e.ToString())); }
        Progress?.Invoke(name + " complete");
    }
    static void Test(string name, Func<string> body) {
        Progress?.Invoke(name);
        try { Add(new(name, true, "Passed: " + body())); }
        catch(Exception e) { Add(new(name, false, e.ToString())); }
        Progress?.Invoke(name + " complete");
    }
    static void Equal(int actual, int expected) { if(actual != expected) throw new Exception($"Expected {expected}, got {actual}"); }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static int Target(int x) => x + 1;
    delegate int Original(int x);
    static int Replacement(Original orig, int x) => orig(x) + 10;
    public static void Run() {
        lock(Gate) Results.Clear();
        InstallCrossContextResolver();
        Test("Managed reflection and generic call", () => Equal((int)typeof(Checks).GetMethod(nameof(Identity))!.MakeGenericMethod(typeof(int)).Invoke(null,new object[]{42})!,42));
        Test("Load original-style assembly from stream", () => {
            using var stream = typeof(Checks).Assembly.GetManifestResourceStream("ProbePayload.dll")!;
            var alc = new AssemblyLoadContext("probe-payload", isCollectible:false);
            var asm = alc.LoadFromStream(stream);
            Equal((int)asm.GetType("PayloadEntry")!.GetMethod("Calculate")!.Invoke(null,new object[]{7})!,22);
        });
        Test("Reflection.Emit interpreted dynamic method", () => {
            var method = new DynamicMethod("RuntimeAdd",typeof(int),new[]{typeof(int)});
            var il = method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldc_I4,9);il.Emit(OpCodes.Add);il.Emit(OpCodes.Ret);
            Equal(((Func<int,int>)method.CreateDelegate(typeof(Func<int,int>)))(1),10);
        });
        Test("MonoMod RuntimeDetour hook and undo", () => {
            Equal(Target(2),3);
            using(var hook = new Hook(typeof(Checks).GetMethod(nameof(Target))!, (Original orig,int x)=>Replacement(orig,x))) Equal(Target(2),13);
            Equal(Target(2),3);
        });
        Test("MonoMod ILHook rewrite and undo", () => {
            using(var hook = new ILHook(typeof(Checks).GetMethod(nameof(Target))!, il => {
                var cursor = new ILCursor(il); if(!cursor.TryGotoNext(i=>i.MatchLdcI4(1))) throw new Exception("Constant not found");
                cursor.Next!.Operand=null;cursor.Next.OpCode=Mono.Cecil.Cil.OpCodes.Ldc_I4_5;
            })) Equal(Target(2),7);
            Equal(Target(2),3);
        });
        Test("Rewrite assembly IL before interpreted loading", () => {
            using var input=typeof(Checks).Assembly.GetManifestResourceStream("ProbePayload.dll")!;
            using var assembly=Mono.Cecil.AssemblyDefinition.ReadAssembly(input);
            assembly.Name.Name="ProbePayloadRewritten";
            var type=System.Linq.Enumerable.First(assembly.MainModule.Types,t=>t.Name=="PayloadEntry");
            var method=System.Linq.Enumerable.First(type.Methods,m=>m.Name=="Calculate");
            var instruction=System.Linq.Enumerable.First(method.Body.Instructions,i=>i.OpCode==Mono.Cecil.Cil.OpCodes.Ldc_I4_3);
            instruction.OpCode=Mono.Cecil.Cil.OpCodes.Ldc_I4_5;
            using var output=new MemoryStream();assembly.Write(output);output.Position=0;
            var context=new AssemblyLoadContext("rewritten-payload",isCollectible:false);
            var loaded=context.LoadFromStream(output);
            Equal((int)loaded.GetType("PayloadEntry")!.GetMethod("Calculate")!.Invoke(null,new object[]{7})!,36);
        });
        Test("Managed dispatch hook chain without native detours", () => {
            Func<int,int> original = x=>x+1;
            Func<int,int> dispatch = original;
            Equal(dispatch(2),3);
            var previous=dispatch;dispatch=x=>previous(x)+10;
            Equal(dispatch(2),13);
            var secondPrevious=dispatch;dispatch=x=>secondPrevious(x)*2;
            Equal(dispatch(2),26);
            dispatch=secondPrevious;Equal(dispatch(2),13);
            dispatch=original;Equal(dispatch(2),3);
        });
        Test("Loader private ALC hook target exists", () => {
            if(typeof(AssemblyLoadContext).GetMethod("ValidateAssemblyNameWithSimpleName",BindingFlags.Static|BindingFlags.NonPublic)==null)
                throw new MissingMethodException("Loader assumes a CoreCLR private method absent from this runtime; explicit loader adaptation required.");
        });
        Test("DMD: IL manipulator compiled to interpreted DynamicMethod", () => {
            using var dmd=new MonoMod.Utils.DynamicMethodDefinition(typeof(Checks).GetMethod(nameof(Target))!);
            using(var ctx=new ILContext(dmd.Definition)) ctx.Invoke(SwapOneForFive);
            var generated=dmd.Generate();
            Equal((int)generated.Invoke(null,new object[]{2})!,7);
            Equal(Target(2),3);
        });
        Test("Prewoven dispatch stub with On-style orig chain", () => {
            var type=WovenType.Value;
            var calc=(Func<int,int>)type.GetMethod("Calculate")!.CreateDelegate(typeof(Func<int,int>));
            var field=type.GetField("Hook_Calculate")!;
            var orig=(Func<int,int>)type.GetMethod("orig_Calculate")!.CreateDelegate(typeof(Func<int,int>));
            try {
                Equal(calc(7),22);
                field.SetValue(null,(Func<int,int>)(x=>orig(x)+10)); Equal(calc(7),32);
                var first=(Func<int,int>)field.GetValue(null)!; field.SetValue(null,(Func<int,int>)(x=>first(x)*2)); Equal(calc(7),64);
                field.SetValue(null,first); Equal(calc(7),32);
            } finally { field.SetValue(null,null); }
            Equal(calc(7),22);
        });
        Test("IL edit via DMD routed through prewoven dispatch (file-loaded)", () => {
            var type=WovenType.Value;
            var calc=(Func<int,int>)type.GetMethod("Calculate")!.CreateDelegate(typeof(Func<int,int>));
            var field=type.GetField("Hook_Calculate")!;
            using var dmd=new MonoMod.Utils.DynamicMethodDefinition(type.GetMethod("orig_Calculate")!);
            using(var ctx=new ILContext(dmd.Definition)) ctx.Invoke(il => {
                var c=new ILCursor(il); if(!c.TryGotoNext(i=>i.MatchLdcI4(3))) throw new Exception("Constant 3 not found");
                c.Next!.OpCode=Mono.Cecil.Cil.OpCodes.Ldc_I4_5; c.Next.Operand=null;
            });
            var edited=(Func<int,int>)dmd.Generate().CreateDelegate(typeof(Func<int,int>));
            try {
                field.SetValue(null,edited); Equal(calc(7),36);
                field.SetValue(null,(Func<int,int>)(x=>edited(x)+10)); Equal(calc(7),46);
            } finally { field.SetValue(null,null); }
            Equal(calc(7),22);
        });
        Test("Shaped: weave Player + On_/IL_ accessors, baseline behaviour", () => {
            var w=Shaped.Value; HookRegistry.Bind(w.Player);
            var p=Activator.CreateInstance(w.Player)!;
            var a=new object?[]{5,0,null}; Equal((int)w.Player.GetMethod("Hurt")!.Invoke(p,a)!,90); Equal((int)a[1]!,10); True(!(bool)a[2]!,"killed");
            Equal(UpdateOf(w,Activator.CreateInstance(w.Player)!)(1000),SumMod(1000,7));
            return Time(w,"baseline");
        });
        Test("Shaped On_ hooks: ref/out, struct return, virtual base call", () => {
            var w=Shaped.Value; Mod(w,"LoadOn");
            var p=Activator.CreateInstance(w.Player)!;
            var a=new object?[]{5,0,null}; Equal((int)w.Player.GetMethod("Hurt")!.Invoke(p,a)!,-1); Equal((int)a[1]!,100); True((bool)a[2]!,"killed");
            var h=Activator.CreateInstance(w.Hero)!; a=new object?[]{5,0,null}; Equal((int)w.Hero.GetMethod("Hurt")!.Invoke(h,a)!,-21); Equal((int)a[1]!,120);
            var vec=w.Vec; var v=Activator.CreateInstance(vec,1f,2f)!;
            var r=w.Player.GetMethod("Move")!.Invoke(Activator.CreateInstance(w.Player)!,new[]{v})!;
            True((float)vec.GetField("X")!.GetValue(r)==102f && (float)vec.GetField("Y")!.GetValue(r)==4f,"Move result "+vec.GetField("X")!.GetValue(r)+","+vec.GetField("Y")!.GetValue(r));
            Equal(UpdateOf(w,Activator.CreateInstance(w.Player)!)(1000),SumMod(1000,7));
            return Time(w,"On passthrough on Update");
        });
        Test("Shaped IL_ hooks via DMD: try/catch, private field, EmitDelegate closure", () => {
            var w=Shaped.Value; Mod(w,"LoadIL",1000);
            var p=Activator.CreateInstance(w.Player)!;
            var a=new object?[]{5,0,null}; Equal((int)w.Player.GetMethod("Hurt")!.Invoke(p,a)!,-51); Equal((int)a[1]!,150);
            p=Activator.CreateInstance(w.Player)!; a=new object?[]{-1,0,null}; Equal((int)w.Player.GetMethod("Hurt")!.Invoke(p,a)!,99); Equal((int)a[1]!,0); True(!(bool)a[2]!,"killed after caught exception");
            Equal(UpdateOf(w,Activator.CreateInstance(w.Player)!)(1000),SumMod(1000,5)+1000);
            return Time(w,"On + IL(DMD) on Update");
        });
        Test("Shaped unload restores original behaviour", () => {
            var w=Shaped.Value; Mod(w,"Unload");
            var p=Activator.CreateInstance(w.Player)!;
            var a=new object?[]{5,0,null}; Equal((int)w.Player.GetMethod("Hurt")!.Invoke(p,a)!,90);
            Equal(UpdateOf(w,Activator.CreateInstance(w.Player)!)(1000),SumMod(1000,7));
            True(w.Player.GetField("__chain_Hurt")!.GetValue(null)==null,"chain cleared");
            return "chains cleared";
        });
        Test("DMD on generic method instantiations (int, string)", () => {
            var w=Shaped.Value; var p=Activator.CreateInstance(w.Player)!;
            foreach(var (t,x,y) in new (Type,object,object)[]{(typeof(int),1,2),(typeof(string),"a","b")}) {
                var gm=w.Player.GetMethod("Pick")!.MakeGenericMethod(t);
                True(Equals(gm.Invoke(p,new[]{x,y}),x),"original Pick<"+t.Name+">");
                using var dmd=new MonoMod.Utils.DynamicMethodDefinition(gm);
                using(var ctx=new ILContext(dmd.Definition)) ctx.Invoke(il=>{var c=new ILCursor(il); if(!c.TryGotoNext(i=>i.MatchLdcI4(50))) throw new Exception("50 not found"); c.Next!.OpCode=Mono.Cecil.Cil.OpCodes.Ldc_I4; c.Next.Operand=200;});
                True(Equals(dmd.Generate().Invoke(null,new[]{p,x,y}),y),"edited Pick<"+t.Name+">");
            }
            return "int and string instantiations edited";
        });
        Test("Shaped IL_ hooks with forced DynamicMethod generator (DMDType=dm)", () => {
            var w=Shaped.Value;
            MonoMod.Switches.SetSwitchValue("DMDType","dm");
            try {
                Mod(w,"LoadIL",1000);
                var a=new object?[]{5,0,null}; Equal((int)w.Player.GetMethod("Hurt")!.Invoke(Activator.CreateInstance(w.Player)!,a)!,85); Equal((int)a[1]!,15);
                Equal(UpdateOf(w,Activator.CreateInstance(w.Player)!)(1000),SumMod(1000,5)+1000);
                var t=Time(w,"IL(DMD dm) on Update");
                Mod(w,"Unload");
                return t;
            } finally { MonoMod.Switches.ClearSwitchValue("DMDType"); }
        });
        Test("DMD generator environment", () =>
            $"IsDynamicILAvailable(default prefers DynamicMethod)={MonoMod.Utils.DynamicMethodDefinition.IsDynamicILAvailable}; cross-context resolves={ResolveCount}");
        if(CalamityHooks.Available) Test("Calamity 2.2.4 real On_/IL_ hooks on woven tModLoader (ManagedHooks)", CalamityHooks.Run);
    }
    static int ResolveCount; static bool resolverInstalled;
    // MonoMod's Cecil DMD generator emits a temp assembly whose references are resolved by simple name from the default
    // context; map them to assemblies already loaded in any load context (woven game assembly, mod assemblies).
    static void InstallCrossContextResolver() {
        if(resolverInstalled) return; resolverInstalled=true;
        AssemblyLoadContext.Default.Resolving+=(_,name)=>{
            foreach(var ctx in AssemblyLoadContext.All) foreach(var a in ctx.Assemblies)
                if(a.GetName().Name==name.Name){ Interlocked.Increment(ref ResolveCount); return a; }
            return null;
        };
    }
    static void SwapOneForFive(ILContext il) {
        var c=new ILCursor(il); if(!c.TryGotoNext(i=>i.MatchLdcI4(1))) throw new Exception("Constant not found");
        c.Next!.Operand=null; c.Next.OpCode=Mono.Cecil.Cil.OpCodes.Ldc_I4_5;
    }
    // Build-time-style weave: rename Calculate -> orig_Calculate, add static Func field and a dispatch stub named Calculate.
    static readonly Lazy<Type> WovenType=new(() => {
        using var input=typeof(Checks).Assembly.GetManifestResourceStream("ProbePayload.dll")!;
        using var asm=Mono.Cecil.AssemblyDefinition.ReadAssembly(input);
        asm.Name.Name="ProbePayloadWoven";
        var module=asm.MainModule;
        var type=System.Linq.Enumerable.First(module.Types,t=>t.Name=="PayloadEntry");
        var orig=System.Linq.Enumerable.First(type.Methods,m=>m.Name=="Calculate");
        orig.Name="orig_Calculate";
        var funcRef=module.ImportReference(typeof(Func<int,int>));
        var invoke=module.ImportReference(typeof(Func<int,int>).GetMethod("Invoke"));
        var field=new Mono.Cecil.FieldDefinition("Hook_Calculate",Mono.Cecil.FieldAttributes.Public|Mono.Cecil.FieldAttributes.Static,funcRef);
        type.Fields.Add(field);
        var stub=new Mono.Cecil.MethodDefinition("Calculate",orig.Attributes,orig.ReturnType);
        stub.Parameters.Add(new Mono.Cecil.ParameterDefinition("value",Mono.Cecil.ParameterAttributes.None,orig.Parameters[0].ParameterType));
        var il=stub.Body.GetILProcessor();
        var call=il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldsfld,field); il.Emit(Mono.Cecil.Cil.OpCodes.Dup); il.Emit(Mono.Cecil.Cil.OpCodes.Brtrue,call);
        il.Emit(Mono.Cecil.Cil.OpCodes.Pop); il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0); il.Emit(Mono.Cecil.Cil.OpCodes.Call,orig); il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
        il.Append(call); il.Emit(Mono.Cecil.Cil.OpCodes.Callvirt,invoke); il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
        type.Methods.Add(stub);
        var path=Path.Combine(Path.GetTempPath(),"ProbePayloadWoven-"+Guid.NewGuid().ToString("N")+".dll");
        asm.Write(path);
        return new AssemblyLoadContext("woven-payload",isCollectible:false).LoadFromAssemblyPath(path).GetType("PayloadEntry")!;
    });
    static void True(bool ok,string what){ if(!ok) throw new Exception("Check failed: "+what); }
    static int SumMod(int n,int m){int s=0;for(int i=0;i<n;i++)s+=i%m;return s;}
    public record Woven(Type Player,Type Hero,Type Vec,Type Mod);
    static void Mod(Woven w,string method,params object[] args){
        try { w.Mod.GetMethod(method)!.Invoke(null,args.Length==0?null:args); }
        catch(TargetInvocationException e) when (e.InnerException!=null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
    }
    static Func<int,int> UpdateOf(Woven w,object inst)=>(Func<int,int>)Delegate.CreateDelegate(typeof(Func<int,int>),inst,w.Player.GetMethod("Update")!);
    static string Time(Woven w,string label){
        var f=UpdateOf(w,Activator.CreateInstance(w.Player)!); f(1);
        var sw=System.Diagnostics.Stopwatch.StartNew(); long acc=0; for(int i=0;i<20000;i++) acc+=f(1); var call1=sw.Elapsed.TotalMilliseconds;
        sw.Restart(); for(int i=0;i<2000;i++) acc+=f(100); var call100=sw.Elapsed.TotalMilliseconds;
        return $"{label}: Update(1) {call1*1000/20000:F2} us/call; Update(100) {call100*1000/2000:F2} us/call (acc {acc})";
    }
    // Load-time weave of the Terraria-shaped payload: each hookable Player method X becomes
    //   orig_X (original body, non-virtual) + OrigStatic_X + nested Link_X + static field __chain_X + dispatch stub X;
    // On_Player.add_/remove_X and IL_Player.add_/remove_X are redirected to HookRegistry.
    static readonly Lazy<Woven> Shaped=new(() => {
        using var input=typeof(Checks).Assembly.GetManifestResourceStream("ProbePayload.dll")!;
        using var asm=Mono.Cecil.AssemblyDefinition.ReadAssembly(input);
        asm.Name.Name="ProbePayloadShaped";
        var module=asm.MainModule;
        Mono.Cecil.TypeDefinition T(string n)=>System.Linq.Enumerable.First(module.Types,t=>t.Name==n);
        var player=T("Player"); var on=T("On_Player"); var ilt=T("IL_Player");
        var objCtor=module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes));
        foreach(var name in new[]{"Hurt","Move","Update"}) {
            var m=System.Linq.Enumerable.First(player.Methods,x=>x.Name==name);
            var origDel=System.Linq.Enumerable.First(on.NestedTypes,t=>t.Name=="orig_"+name);
            var hookDel=System.Linq.Enumerable.First(on.NestedTypes,t=>t.Name=="hook_"+name);
            var origInvoke=System.Linq.Enumerable.First(origDel.Methods,x=>x.Name=="Invoke");
            var hookInvoke=System.Linq.Enumerable.First(hookDel.Methods,x=>x.Name=="Invoke");
            // Move the original body into a new non-virtual orig_X; the original definition becomes the stub,
            // so every existing reference (base calls, other types, other assemblies) keeps hitting the dispatcher.
            var orig=new Mono.Cecil.MethodDefinition("orig_"+name,(m.Attributes&~(Mono.Cecil.MethodAttributes.Virtual|Mono.Cecil.MethodAttributes.NewSlot|Mono.Cecil.MethodAttributes.Final|Mono.Cecil.MethodAttributes.Abstract))|Mono.Cecil.MethodAttributes.HideBySig,m.ReturnType){HasThis=m.HasThis};
            foreach(var prm in m.Parameters) orig.Parameters.Add(new Mono.Cecil.ParameterDefinition(prm.Name,prm.Attributes,prm.ParameterType));
            var src=m.Body; var dst=orig.Body;
            dst.InitLocals=src.InitLocals; dst.MaxStackSize=src.MaxStackSize;
            foreach(var v in src.Variables) dst.Variables.Add(v);
            foreach(var ins in src.Instructions) {
                if(ins.Operand is Mono.Cecil.ParameterDefinition pd) ins.Operand=pd==src.ThisParameter?dst.ThisParameter:orig.Parameters[pd.Index];
                dst.Instructions.Add(ins);
            }
            foreach(var eh in src.ExceptionHandlers) dst.ExceptionHandlers.Add(eh);
            player.Methods.Add(orig);
            m.Body=new Mono.Cecil.Cil.MethodBody(m);
            var chain=new Mono.Cecil.FieldDefinition("__chain_"+name,Mono.Cecil.FieldAttributes.Public|Mono.Cecil.FieldAttributes.Static,origDel);
            player.Fields.Add(chain);
            var stub=m;
            var il=stub.Body.GetILProcessor();
            var viaChain=il.Create(Mono.Cecil.Cil.OpCodes.Nop);
            il.Emit(Mono.Cecil.Cil.OpCodes.Ldsfld,chain); il.Emit(Mono.Cecil.Cil.OpCodes.Dup); il.Emit(Mono.Cecil.Cil.OpCodes.Brtrue,viaChain); il.Emit(Mono.Cecil.Cil.OpCodes.Pop);
            il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0); foreach(var prm in stub.Parameters) il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg,prm);
            il.Emit(Mono.Cecil.Cil.OpCodes.Call,orig); il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            il.Append(viaChain); il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0); foreach(var prm in stub.Parameters) il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg,prm);
            il.Emit(Mono.Cecil.Cil.OpCodes.Callvirt,origInvoke); il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            // static bridge so the chain base is a plain static delegate (no open-instance delegate needed)
            var bridge=new Mono.Cecil.MethodDefinition("OrigStatic_"+name,Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static|Mono.Cecil.MethodAttributes.HideBySig,m.ReturnType);
            foreach(var prm in origInvoke.Parameters) bridge.Parameters.Add(new Mono.Cecil.ParameterDefinition(prm.Name,prm.Attributes,prm.ParameterType));
            il=bridge.Body.GetILProcessor(); foreach(var prm in bridge.Parameters) il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg,prm); il.Emit(Mono.Cecil.Cil.OpCodes.Call,orig); il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            player.Methods.Add(bridge);
            // Link_X: binds one hook to the next element of the chain (tML/MonoMod orig semantics)
            var link=new Mono.Cecil.TypeDefinition("","Link_"+name,Mono.Cecil.TypeAttributes.NestedPublic|Mono.Cecil.TypeAttributes.Sealed|Mono.Cecil.TypeAttributes.BeforeFieldInit,module.TypeSystem.Object);
            player.NestedTypes.Add(link);
            var fh=new Mono.Cecil.FieldDefinition("Hook",Mono.Cecil.FieldAttributes.Public,hookDel); var fn=new Mono.Cecil.FieldDefinition("Next",Mono.Cecil.FieldAttributes.Public,origDel);
            link.Fields.Add(fh); link.Fields.Add(fn);
            var ctor=new Mono.Cecil.MethodDefinition(".ctor",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.HideBySig|Mono.Cecil.MethodAttributes.SpecialName|Mono.Cecil.MethodAttributes.RTSpecialName,module.TypeSystem.Void);
            il=ctor.Body.GetILProcessor(); il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0); il.Emit(Mono.Cecil.Cil.OpCodes.Call,objCtor); il.Emit(Mono.Cecil.Cil.OpCodes.Ret); link.Methods.Add(ctor);
            var inv=new Mono.Cecil.MethodDefinition("Invoke",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.HideBySig,m.ReturnType){HasThis=true};
            foreach(var prm in origInvoke.Parameters) inv.Parameters.Add(new Mono.Cecil.ParameterDefinition(prm.Name,prm.Attributes,prm.ParameterType));
            il=inv.Body.GetILProcessor();
            il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0); il.Emit(Mono.Cecil.Cil.OpCodes.Ldfld,fh); il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0); il.Emit(Mono.Cecil.Cil.OpCodes.Ldfld,fn);
            foreach(var prm in inv.Parameters) il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg,prm);
            il.Emit(Mono.Cecil.Cil.OpCodes.Callvirt,hookInvoke); il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            link.Methods.Add(inv);
        }
        void Redirect(Mono.Cecil.TypeDefinition owner,string accessor,string registryMethod){
            foreach(var m in owner.Methods) {
                if(!m.Name.StartsWith(accessor)) continue;
                var body=m.Body; body.Instructions.Clear(); body.ExceptionHandlers.Clear(); body.Variables.Clear();
                var il=body.GetILProcessor();
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0); il.Emit(Mono.Cecil.Cil.OpCodes.Ldstr,m.Name.Substring(accessor.Length));
                il.Emit(Mono.Cecil.Cil.OpCodes.Call,module.ImportReference(typeof(HookRegistry).GetMethod(registryMethod))); il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            }
        }
        Redirect(on,"add_","AddOn"); Redirect(on,"remove_","RemoveOn"); Redirect(ilt,"add_","AddIL"); Redirect(ilt,"remove_","RemoveIL");
        var path=Path.Combine(Path.GetTempPath(),"ProbePayloadShaped-"+Guid.NewGuid().ToString("N")+".dll");
        asm.Write(path);
        var loaded=new AssemblyLoadContext("shaped-payload",isCollectible:false).LoadFromAssemblyPath(path);
        return new Woven(loaded.GetType("Player")!,loaded.GetType("Hero")!,loaded.GetType("Vec")!,loaded.GetType("FakeMod")!);
    });
    public static T Identity<T>(T x)=>x;
    public static string Report() {
        using var memory=new MemoryStream();
        using(var writer=new Utf8JsonWriter(memory,new JsonWriterOptions{Indented=true})) {
            writer.WriteStartObject();writer.WriteString("runtime",System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            writer.WriteBoolean("dynamicCodeSupported",RuntimeFeature.IsDynamicCodeSupported);writer.WriteBoolean("dynamicCodeCompiled",RuntimeFeature.IsDynamicCodeCompiled);
            writer.WriteStartArray("checks");foreach(var result in Snapshot()){writer.WriteStartObject();writer.WriteString("name",result.Name);writer.WriteBoolean("passed",result.Passed);writer.WriteString("detail",result.Detail);writer.WriteEndObject();}
            writer.WriteEndArray();writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }
}

// Managed replacement for MonoMod's native hook backend: composes On_ chains and DMD-compiled IL_ edits
// and publishes the result into the woven __chain_X field. Load-time cost only; no native code patching.
public static class HookRegistry {
    static Type? player;
    static readonly Dictionary<string,List<Delegate>> on=new(), il=new();
    public static void Bind(Type t){ player=t; on.Clear(); il.Clear(); }
    static List<Delegate> Get(Dictionary<string,List<Delegate>> d,string n)=>d.TryGetValue(n,out var l)?l:d[n]=new();
    public static void AddOn(Delegate d,string n){ if(d==null) return; Get(on,n).Add(d); Rebuild(n); }
    public static void RemoveOn(Delegate d,string n){ if(d==null) return; Get(on,n).Remove(d); Rebuild(n); }
    public static void AddIL(Delegate d,string n){ if(d==null) return; Get(il,n).Add(d); Rebuild(n); }
    public static void RemoveIL(Delegate d,string n){ if(d==null) return; Get(il,n).Remove(d); Rebuild(n); }
    static void Rebuild(string n){
        var p=player ?? throw new InvalidOperationException("HookRegistry not bound");
        var origType=p.Assembly.GetType("On_Player")!.GetNestedType("orig_"+n)!;
        var field=p.GetField("__chain_"+n)!;
        var ons=Get(on,n); var ils=Get(il,n);
        if(ons.Count==0 && ils.Count==0){ field.SetValue(null,null); Retire(n,null); return; }
        Delegate cur;
        (MonoMod.Utils.DynamicMethodDefinition, ILContext)? next=null;
        if(ils.Count>0){
            // The ILContext owns EmitDelegate closure cells, so it stays alive until this edit is replaced or removed.
            var dmd=new MonoMod.Utils.DynamicMethodDefinition(p.GetMethod("orig_"+n)!);
            var ctx=new ILContext(dmd.Definition); foreach(var m in ils) ctx.Invoke((ILContext.Manipulator)m);
            cur=dmd.Generate().CreateDelegate(origType); next=(dmd,ctx);
        } else cur=Delegate.CreateDelegate(origType,p.GetMethod("OrigStatic_"+n)!);
        var link=p.GetNestedType("Link_"+n)!;
        foreach(var h in ons){
            var l=Activator.CreateInstance(link)!;
            link.GetField("Hook")!.SetValue(l,h); link.GetField("Next")!.SetValue(l,cur);
            cur=Delegate.CreateDelegate(origType,l,"Invoke");
        }
        field.SetValue(null,cur);
        Retire(n,next);
    }
    static readonly Dictionary<string,(MonoMod.Utils.DynamicMethodDefinition, ILContext)> live=new();
    static void Retire(string n,(MonoMod.Utils.DynamicMethodDefinition, ILContext)? next){
        if(live.Remove(n,out var old)){ old.Item2.Dispose(); old.Item1.Dispose(); }
        if(next is {} x) live[n]=x;
    }
}
