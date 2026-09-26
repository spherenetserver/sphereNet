using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the @Jail trigger. The jail system already existed (the GM JAIL command
// teleports to the jail cell, sets the Freeze flag and a JAIL_RELEASE tag, with
// timed release driven from Program); only the trigger was missing. @Jail now
// fires on the jailed character with the sentence length. Character.OnJailed is
// nulled between tests by ResetEngineStatics.
[Collection("DefinitionLoaderSerial")]
public class JailTriggerTests
{
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
        Character? jailed = null;
        Character.OnJailed = (c, m) => { jailed = c; minutes = m; };

        cmds.TryExecute(gm, $"JAIL {target.Uid.Value:X} 5");

        Assert.Same(target, jailed);
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
        Character.OnJailed = (_, m) => { minutes = m; };

        cmds.TryExecute(gm, $"JAIL {target.Uid.Value:X}"); // no duration → indefinite

        Assert.Equal(0, minutes);
    }
}
