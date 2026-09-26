using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies Source-X CChar::Jail (CCharAct.cpp:153-211) and its @Jailed trigger:
// the trigger fires BEFORE anything changes with ARGN1 = set, ARGN2 = cell (SphereNet
// adds ARGN3 = opt-in sentence minutes) and RETURN 1 cancels; the character JAIL
// verb (CHV_JAIL, CChar.cpp:4685) runs the full Jail with its argument as the cell.
// Character.OnJailed is nulled between tests by ResetEngineStatics.
[Collection("DefinitionLoaderSerial")]
public class JailTriggerTests
{
    private sealed class GmConsole(Character gm) : SphereNet.Core.Interfaces.ITextConsole
    {
        public string GetName() => "gm";
        public PrivLevel GetPrivLevel() => PrivLevel.GM;
        public void SysMessage(string text) { }
        public SphereNet.Core.Interfaces.IScriptObj? GetSourceChar() => gm;
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static (CommandHandler cmds, Character gm, Character target) Setup(GameWorld world)
    {
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var cmds = new CommandHandler();
        cmds.RegisterDefaults(world);
        return (cmds, gm, target);
    }

    [Fact]
    public void Jail_TimedSentence_FiresWithMinutes_AndJailsWithoutFreezing()
    {
        var world = CreateWorld();
        var (cmds, gm, target) = Setup(world);

        int minutes = -1;
        Character? jailed = null, jailSrc = null;
        bool? set = null;
        Character.OnJailed = (c, src, s, cell, m) => { jailed = c; jailSrc = src; set = s; minutes = m; return false; };

        cmds.TryExecute(gm, $"JAIL {target.Uid.Value:X} 5");

        Assert.Same(target, jailed);
        Assert.Same(gm, jailSrc);
        Assert.True(set);
        Assert.Equal(5, minutes);
        // Source-X CChar::Jail (CCharAct.cpp:166-191): PRIV_JAILED + teleport to the
        // jail point, no Freeze (the old test asserted one).
        Assert.False(target.IsStatFlag(StatFlag.Freeze));
        Assert.True(target.IsJailed);
        Assert.True(target.TryGetTag("JAIL_RELEASE", out _));
    }

    [Fact]
    public void Jail_SetsAccountPrivJailedAndJailCell_ForgiveClearsOnly()
    {
        var world = CreateWorld();
        var (cmds, gm, target) = Setup(world);
        var account = new SphereNet.Game.Accounts.Account { Name = "jailee" };
        Character.ResolveAccountForChar = uid => uid == target.Uid ? account : null;

        cmds.TryExecute(gm, $"JAIL {target.Uid.Value:X} 0 2");

        Assert.True(account.Jail);
        Assert.True(account.TryGetTag("JailCell", out string cell) && cell == "2");
        var jailedAt = target.Position;

        cmds.TryExecute(gm, $"FORGIVE {target.Uid.Value:X}");

        Assert.False(account.Jail);
        Assert.False(account.TryGetTag("JailCell", out _));
        // Forgiving does not move the character (CCharAct.cpp:193-210).
        Assert.Equal(jailedAt, target.Position);
    }

    [Fact]
    public void Jail_Indefinite_FiresWithZeroMinutes()
    {
        var world = CreateWorld();
        var (cmds, gm, target) = Setup(world);

        int minutes = -1;
        Character.OnJailed = (_, _, _, _, m) => { minutes = m; return false; };

        cmds.TryExecute(gm, $"JAIL {target.Uid.Value:X}"); // no duration → indefinite

        Assert.Equal(0, minutes);
    }

    [Fact]
    public void Jailed_Return1_CancelsBeforeAnythingChanges()
    {
        var world = CreateWorld();
        var (cmds, gm, target) = Setup(world);
        var account = new SphereNet.Game.Accounts.Account { Name = "jailee" };
        Character.ResolveAccountForChar = uid => uid == target.Uid ? account : null;
        var before = target.Position;

        bool? stateAtTrigger = null;
        Character.OnJailed = (c, _, _, _, _) => { stateAtTrigger = c.IsJailed; return true; };

        cmds.TryExecute(gm, $"JAIL {target.Uid.Value:X} 0 1");

        // Fired before PRIV_JAILED was set (CCharAct.cpp:159-164), and cancelled it.
        Assert.False(stateAtTrigger);
        Assert.False(account.Jail);
        Assert.False(target.IsJailed);
        Assert.Equal(before, target.Position);
    }

    [Fact]
    public void JailVerb_RunsFullJail_WithCellArgument()
    {
        var world = CreateWorld();
        var (_, gm, target) = Setup(world);
        var account = new SphereNet.Game.Accounts.Account { Name = "jailee" };
        Character.ResolveAccountForChar = uid => uid == target.Uid ? account : null;

        int trigCell = -1;
        bool? trigSet = null;
        Character.OnJailed = (_, _, s, cell, _) => { trigSet = s; trigCell = cell; return false; };

        // CHV_JAIL: Jail(pSrc, true, GetArgVal()) - the argument is the cell.
        Assert.True(target.TryExecuteCommand("JAIL", "3", new GmConsole(gm)));

        Assert.True(trigSet);
        Assert.Equal(3, trigCell);
        Assert.True(account.Jail);
        Assert.True(account.TryGetTag("JailCell", out string cell) && cell == "3");
        Assert.True(target.IsJailed);
        // Teleported to the jail point (no "jail3" region here -> legacy point).
        Assert.Equal(world.GetJailPoint(3).X, target.Position.X);
        Assert.Equal(world.GetJailPoint(3).Y, target.Position.Y);
    }

    [Fact]
    public void ForgiveVerb_FiresJailedWithSetZero_AndReturn1KeepsPrisoner()
    {
        var world = CreateWorld();
        var (_, gm, target) = Setup(world);
        var account = new SphereNet.Game.Accounts.Account { Name = "jailee" };
        Character.ResolveAccountForChar = uid => uid == target.Uid ? account : null;
        Assert.True(target.TryExecuteCommand("JAIL", "", new GmConsole(gm)));
        Assert.True(account.Jail);

        bool? trigSet = null;
        Character.OnJailed = (_, _, s, _, _) => { trigSet = s; return true; };
        target.TryExecuteCommand("FORGIVE", "", new GmConsole(gm));
        Assert.False(trigSet);
        Assert.True(account.Jail); // RETURN 1 cancelled the pardon

        Character.OnJailed = null;
        target.TryExecuteCommand("FORGIVE", "", new GmConsole(gm));
        Assert.False(account.Jail);
        Assert.False(account.TryGetTag("JailCell", out _));
    }
}
