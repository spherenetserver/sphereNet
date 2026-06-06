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
    public void Jail_TimedSentence_FiresWithMinutes_AndFreezesTarget()
    {
        var world = CreateWorld();
        var (cmds, gm, target) = Setup(world);

        int minutes = -1;
        Character? jailed = null;
        Character.OnJailed = (c, m) => { jailed = c; minutes = m; };

        cmds.TryExecute(gm, $"JAIL {target.Uid.Value:X} 5");

        Assert.Same(target, jailed);
        Assert.Equal(5, minutes);
        Assert.True(target.IsStatFlag(StatFlag.Freeze));
        Assert.True(target.TryGetTag("JAIL_RELEASE", out _));
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
