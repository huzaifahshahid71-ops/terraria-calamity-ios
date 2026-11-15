// iOS memory: mods request every autoloaded texture with AssetRequestMode.AsyncLoad at startup (Calamity: 9,205 images,
// ~1.9 GB decoded). This patch lets the app defer those loads until the asset is first used, the way vanilla Terraria
// already loads its own item/NPC/projectile textures lazily. ReLogic only gains two hook fields and three prologues:
//   AssetRepository.TPDefer (Func<AssetRepository,object,bool>): called by LoadAsset<T> for AsyncLoad; true = skip the load.
//   AssetRepository.TPTouch (Action<object>): called by Asset<T>.Value / IsLoaded while State == Loading (deferred assets
//   are parked in Loading so vanilla's LoadItem/LoadNPC never re-request them from the wrong repository).
// Asset<T>.State and Asset<T>.Wait setters become public so the app-side loader can reset/replace them.
// With both fields null (or TP_LAZY_TEXTURES=0 in the app) behaviour is identical to the original.
using Mono.Cecil;
using Mono.Cecil.Cil;

static class LazyAssets {
    public static List<string> Apply(ModuleDefinition relogic) {
        var log = new List<string>();
        var repo = relogic.GetType("ReLogic.Content.AssetRepository");
        var asset = relogic.GetType("ReLogic.Content.Asset`1");
        var objRef = relogic.TypeSystem.Object; var boolRef = relogic.TypeSystem.Boolean;
        // Reference Func/Action through ReLogic's own System.Runtime reference (not the weaver's System.Private.CoreLib),
        // so compilers and both runtimes resolve them like every other BCL type in ReLogic.
        var sysRuntime = relogic.AssemblyReferences.Single(a => a.Name == "System.Runtime");
        TypeReference Open(string name, int arity) { var t = new TypeReference("System", name, relogic, sysRuntime); for (int i = 0; i < arity; i++) t.GenericParameters.Add(new GenericParameter(t)); return t; }
        var funcOpen = Open("Func`3", 3); var actOpen = Open("Action`1", 1);
        var funcT = new GenericInstanceType(funcOpen); funcT.GenericArguments.Add(repo); funcT.GenericArguments.Add(objRef); funcT.GenericArguments.Add(boolRef);
        var actT = new GenericInstanceType(actOpen); actT.GenericArguments.Add(objRef);
        var defer = new FieldDefinition("TPDefer", FieldAttributes.Public | FieldAttributes.Static, funcT); repo.Fields.Add(defer);
        var touch = new FieldDefinition("TPTouch", FieldAttributes.Public | FieldAttributes.Static, actT); repo.Fields.Add(touch);
        // Invoke methods on the generic instances (declared on the open type with generic-parameter signatures).
        var fInvoke = new MethodReference("Invoke", funcOpen.GenericParameters[2], funcT) { HasThis = true };
        fInvoke.Parameters.Add(new ParameterDefinition(funcOpen.GenericParameters[0]));
        fInvoke.Parameters.Add(new ParameterDefinition(funcOpen.GenericParameters[1]));
        var aInvoke = new MethodReference("Invoke", relogic.TypeSystem.Void, actT) { HasThis = true };
        aInvoke.Parameters.Add(new ParameterDefinition(actOpen.GenericParameters[0]));
        log.Add("AssetRepository: + static TPDefer, TPTouch");

        // LoadAsset<T>(Asset<T> asset, AssetRequestMode mode): if (mode == AsyncLoad && TPDefer != null && TPDefer(this, asset)) return;
        var load = repo.Methods.Single(m => m.Name == "LoadAsset" && m.Parameters.Count == 2);
        {
            var il = load.Body.GetILProcessor(); var first = load.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_2)); il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_2)); il.InsertBefore(first, il.Create(OpCodes.Bne_Un, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, defer)); il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, defer)); il.InsertBefore(first, il.Create(OpCodes.Ldarg_0)); il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, fInvoke)); il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
        }
        log.Add("AssetRepository.LoadAsset<T>: AsyncLoad may be deferred (TPDefer)");

        // Asset<T>.get_Value / get_IsLoaded: if (State == Loading && TPTouch != null) TPTouch(this);
        var selfT = new GenericInstanceType(asset); selfT.GenericArguments.Add(asset.GenericParameters[0]);
        var getState = new MethodReference("get_State", asset.Properties.Single(p => p.Name == "State").PropertyType, selfT) { HasThis = true };
        foreach (var name in new[] { "get_Value", "get_IsLoaded" }) {
            var m = asset.Methods.Single(x => x.Name == name);
            var il = m.Body.GetILProcessor(); var first = m.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0)); il.InsertBefore(first, il.Create(OpCodes.Call, getState));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1)); il.InsertBefore(first, il.Create(OpCodes.Bne_Un, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, touch)); il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, touch)); il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, aInvoke));
            log.Add($"Asset<T>.{name}: deferred asset loads on first use (TPTouch)");
        }
        foreach (var p in new[] { "State", "Wait" }) {
            var setter = asset.Properties.Single(x => x.Name == p).SetMethod;
            setter.Attributes = (setter.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
            log.Add($"Asset<T>.{p} setter -> public");
        }
        return log;
    }
}
