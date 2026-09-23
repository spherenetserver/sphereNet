using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field report: a GM teleporting got no flamestrike under him. Upstream runs GO,
/// GOCHAR, GOUID, .TELE and JAIL through CChar::Spell_Teleport, which shows the
/// teleport effect of whoever moved at the old and the new spot
/// (CCharSpell.cpp:178/236). The GO* verbs and the GM commands moved the character
/// with no effect at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TeleportEffectTests
{
    [Fact]
    public void GoShowsTheTeleportFromWhereTheCharacterStood()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));

        var shown = new List<(Character, Point3D)>();
        Character.OnTeleportEffect = (ch, from) => shown.Add((ch, from));

        Assert.True(gm.TryExecuteCommand("GO", "200,210,0", null!, out _));

        var (who, from) = Assert.Single(shown);
        Assert.Same(gm, who);
        Assert.Equal((short)100, from.X);
        Assert.Equal((short)200, gm.X);
        Assert.Equal((short)210, gm.Y);
    }

    [Fact]
    public void AGoThatDoesNotMoveShowsNothing()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var gm = world.CreateCharacter();
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        int shown = 0;
        Character.OnTeleportEffect = (_, _) => shown++;

        Assert.False(gm.TeleportWithEffect(new Point3D(100, 100, 0, 0)));
        Assert.Equal(0, shown);
    }
}
