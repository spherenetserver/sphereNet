using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Character triggers the engine never fired: @PersonalSpace on the one
/// walked into (it ran on the mover and could not refuse), @charShove on the mover,
/// @SeeHidden, @AfkMode and @FollowersUpdate - each where upstream raises it.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharTriggerHookTests
{
    private static GameWorld World()
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, int x, string name)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        ch.Dex = 50;
        ch.MaxStam = 50;
        ch.Stam = ch.MaxStam;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void PersonalSpaceRunsOnTheOneWalkedIntoAndCanRefuse()
    {
        var world = World();
        var mover = Player(world, 100, "mover");
        var blocker = Player(world, 101, "blocker");
        Character? on = null, src = null;
        Character.OnPersonalSpace = (o, s, _) => { on = o; src = s; return true; };

        bool moved = new MovementEngine(world).TryMove(mover, Direction.East, running: false, sequence: 1);

        Assert.False(moved);
        Assert.Same(blocker, on);
        Assert.Same(mover, src);
        Assert.Equal(100, mover.X);
    }

    [Fact]
    public void WithNoScriptTheShoveGoesThrough()
    {
        // The control for the two refusals below: an unhooked shove at full stamina
        // does move, so their refusals are the triggers' doing.
        var world = World();
        var mover = Player(world, 100, "mover");
        Player(world, 101, "blocker");
        Assert.True(new MovementEngine(world).TryMove(mover, Direction.East, running: false, sequence: 1));
    }

    [Fact]
    public void CharShoveCanRefuseTheMover()
    {
        var world = World();
        var mover = Player(world, 100, "mover");
        Player(world, 101, "blocker");
        Character.OnCharShove = (_, _, _) => true;

        Assert.False(new MovementEngine(world).TryMove(mover, Direction.East, running: false, sequence: 1));
    }

    [Fact]
    public void SeeHiddenLetsAPlayerSeeAHiddenPlayer()
    {
        var world = World();
        var viewer = Player(world, 100, "viewer");
        var hidden = Player(world, 101, "hidden");
        hidden.SetStatFlag(StatFlag.Hidden);

        Assert.False(Character.CanSeeHidden(viewer, hidden));
        Character.OnSeeHidden = (_, _, n1) => 0;
        Assert.True(Character.CanSeeHidden(viewer, hidden));
    }

    [Fact]
    public void AfkModeCanRefuseTheSwitch()
    {
        var world = World();
        var ch = Player(world, 100, "afk");
        Character.OnAfkMode = (_, afk, mode) => (true, afk, mode);

        ch.TryExecuteCommand("AFK", "", new Console(), out _);

        Assert.False(ch.IsAfk);
    }

    private sealed class Console : SphereNet.Core.Interfaces.ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "test";
    }

    [Fact]
    public void FollowersUpdateCanRefuseANewPet()
    {
        var world = World();
        var owner = Player(world, 100, "owner");
        var pet = world.CreateCharacter();
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Character.OnFollowersUpdate = (_, _, adding, _) => adding;
        var saved = SphereNet.Game.Clients.GameClient.ServerOptionFlags;
        try
        {
            // Upstream raises it only with OF_PetSlots on (CCharUse.cpp:1236).
            SphereNet.Game.Clients.GameClient.ServerOptionFlags |= OptionFlags.PetSlots;
            Assert.False(pet.TryAssignOwnership(owner));
            Assert.False(pet.HasOwner(owner.Uid));
        }
        finally { SphereNet.Game.Clients.GameClient.ServerOptionFlags = saved; }
    }
}
