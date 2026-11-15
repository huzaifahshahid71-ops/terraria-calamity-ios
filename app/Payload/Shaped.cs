// Terraria/tML-shaped stand-ins: a game type, tML-style generated On_/IL_ event types, and a mod that subscribes.
// The weaver in Checks.cs rewrites Player methods into dispatch stubs and the On_/IL_ add/remove accessors into registry calls.
using System;
using MonoMod.Cil;
using Mono.Cecil.Cil;

public struct Vec { public float X, Y; public Vec(float x, float y) { X = x; Y = y; } }

public class Player {
    private int statLife = 100;
    public int StatLife => statLife;
    public virtual int Hurt(int amount, ref int taken, out bool killed) {
        try { if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount)); taken = amount * 2; }
        catch (ArgumentOutOfRangeException) { taken = 0; }
        statLife -= taken; killed = statLife <= 0; return statLife;
    }
    public Vec Move(Vec v) => new Vec(v.X + 1, v.Y * 2);
    public int Update(int frames) { int s = 0; for (int i = 0; i < frames; i++) s += i % 7; return s; }
    public T Pick<T>(T a, T b) => statLife > 50 ? a : b;
}
public class Hero : Player { public override int Hurt(int amount, ref int taken, out bool killed) => base.Hurt(amount + 1, ref taken, out killed); }

public static class On_Player {
    public delegate int orig_Hurt(Player self, int amount, ref int taken, out bool killed);
    public delegate int hook_Hurt(orig_Hurt orig, Player self, int amount, ref int taken, out bool killed);
    public delegate Vec orig_Move(Player self, Vec v);
    public delegate Vec hook_Move(orig_Move orig, Player self, Vec v);
    public delegate int orig_Update(Player self, int frames);
    public delegate int hook_Update(orig_Update orig, Player self, int frames);
    public static event hook_Hurt Hurt { add => throw new NotSupportedException("native hook path"); remove => throw new NotSupportedException(); }
    public static event hook_Move Move { add => throw new NotSupportedException("native hook path"); remove => throw new NotSupportedException(); }
    public static event hook_Update Update { add => throw new NotSupportedException("native hook path"); remove => throw new NotSupportedException(); }
}
public static class IL_Player {
    public static event ILContext.Manipulator Hurt { add => throw new NotSupportedException("native hook path"); remove => throw new NotSupportedException(); }
    public static event ILContext.Manipulator Update { add => throw new NotSupportedException("native hook path"); remove => throw new NotSupportedException(); }
}

public static class FakeMod {
    static On_Player.hook_Hurt? hurt; static On_Player.hook_Move? move; static On_Player.hook_Update? update;
    static ILContext.Manipulator? ilHurt, ilUpdate;
    public static void LoadOn() {
        hurt = (On_Player.orig_Hurt orig, Player self, int amount, ref int taken, out bool killed) => orig(self, amount * 10, ref taken, out killed) - 1;
        move = (orig, self, v) => { var r = orig(self, v); r.X += 100; return r; };
        update = (orig, self, frames) => orig(self, frames);
        On_Player.Hurt += hurt; On_Player.Move += move; On_Player.Update += update;
    }
    public static void LoadIL(int bonus) {
        ilHurt = il => { var c = new ILCursor(il); if (!c.TryGotoNext(i => i.MatchLdcI4(2))) throw new Exception("Hurt: ldc.i4.2 not found"); c.Next!.OpCode = OpCodes.Ldc_I4_3; c.Next.Operand = null; };
        ilUpdate = il => {
            var c = new ILCursor(il);
            if (!c.TryGotoNext(i => i.MatchLdcI4(7))) throw new Exception("Update: ldc.i4.7 not found");
            c.Next!.OpCode = OpCodes.Ldc_I4_5; c.Next.Operand = null;
            if (!c.TryGotoNext(MoveType.Before, i => i.MatchRet())) throw new Exception("Update: ret not found");
            c.EmitDelegate<Func<int, int>>(s => s + bonus); // closure capture, like Calamity's EmitDelegate usage
        };
        IL_Player.Hurt += ilHurt; IL_Player.Update += ilUpdate;
    }
    public static void Unload() {
        On_Player.Hurt -= hurt; On_Player.Move -= move; On_Player.Update -= update;
        IL_Player.Hurt -= ilHurt; IL_Player.Update -= ilUpdate;
    }
}
