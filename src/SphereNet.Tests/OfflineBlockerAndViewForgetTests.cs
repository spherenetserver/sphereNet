using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Two ways a player could lose sight or footing of the world:
/// a logged-out character parked on a tile blocked a player who was not at full
/// stamina (Source-X's world search skips disconnected characters unless AllShow),
/// and a 0x1D the server sent for a refused double-click left the mobile "known",
/// so it went on moving and casting with 0x77s the client threw away.
/// </summary>
public sealed class OfflineBlockerAndViewForgetTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, short x, bool online)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.IsOnline = online;
        ch.BodyId = 0x0190;
        ch.Dex = 50;
        ch.MaxStam = 50;
        ch.Stam = 40; // below full: a shove would be refused
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void ALoggedOutCharacterDoesNotBlockTheTile()
    {
        var world = CreateWorld();
        var walker = Player(world, 100, online: true);
        Player(world, 101, online: false);

        Assert.True(MovementEngine.ShoveCharAtPosition(world, walker,
            new Point3D(101, 100, 0, 0), pathFinding: false, null, out _));
    }

    [Fact]
    public void AnOnlineCharacterStillDoes()
    {
        var world = CreateWorld();
        var walker = Player(world, 100, online: true);
        Player(world, 101, online: true);

        Assert.False(MovementEngine.ShoveCharAtPosition(world, walker,
            new Point3D(101, 100, 0, 0), pathFinding: false, null, out _));
    }

    [Fact]
    public void DeleteAndForgetLetsTheNextDeltaDrawTheMobileAgain()
    {
        var world = CreateWorld();
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7301);
        var me = Player(world, 100, online: true);
        TestHarness.AttachCharacter(client, me);
        var npc = world.CreateCharacter();
        npc.BodyId = 0x000F;
        world.PlaceCharacter(npc, new Point3D(105, 100, 0, 0));

        client.ViewUpdater.NotifyCharacterAppear(npc);
        Assert.True(client.HasKnownChar(npc.Uid.Value));

        client.ViewUpdater.DeleteAndForget(npc.Uid.Value);

        Assert.False(client.HasKnownChar(npc.Uid.Value));
    }
}
