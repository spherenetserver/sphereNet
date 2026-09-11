using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a berserk creature refuses to do, and what a logout stops (port plan İŞ-30 /
/// PLAN-405).
///
/// Source-X guards NPC_PetDesert itself: a berserk brain returns before any of it runs
/// (CCharNPCPet.cpp:906), and the reference states the reason - a berserk summon counts
/// against CURFOLLOWER, so if attacking one made it desert, a player could attack his
/// own to free the slot and summon another at no cost. The live pack's Energy Vortex
/// (c_vortex) and Blade Spirit (c_blade_spirit) are both brain_berserk.
///
/// CChar::ClientDetach stops a ship the leaving char is standing on (CChar.cpp:497-502).
/// Nothing did that here, so logging off mid-voyage left the boat sailing on its own.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PetDesertAndLogoutParityTests
{
    // ---- the berserk guard --------------------------------------------

    private static (GameWorld World, Character Owner, Character Pet) Pet(NpcBrainType brain)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.NpcBrain = brain;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner));

        return (world, owner, pet);
    }

    [Fact]
    public void AnOrdinaryPetCanDesert()
    {
        var (_, _, pet) = Pet(NpcBrainType.Animal);
        Assert.True(pet.CanDesertOwner);
    }

    [Fact]
    public void ABerserkCreatureNeverDeserts()
    {
        var (_, _, pet) = Pet(NpcBrainType.Berserk);
        Assert.False(pet.CanDesertOwner);
    }

    [Fact]
    public void APlayerIsNotSomethingThatDeserts()
    {
        var (world, owner, _) = Pet(NpcBrainType.Animal);
        Assert.False(owner.CanDesertOwner);
        Assert.NotNull(world);
    }

    [Fact]
    public void AStarvingBerserkCreatureKeepsItsOwner()
    {
        // The hunger tick is the second NPC_PetDesert call site (CCharAct.cpp:5791):
        // the berserk creature starves, but it does not go wild.
        var (_, owner, pet) = Pet(NpcBrainType.Berserk);
        pet.Food = 1;
        pet.SetTag("PET_NEXT_LOYALTY_TICK", "1");

        pet.TickPetOwnershipTimers(1_000_000);

        Assert.Equal(0, pet.Food);
        Assert.True(pet.HasOwner(owner.Uid));
    }

    // ---- the logout ----------------------------------------------------

    [Fact]
    public void LoggingOffAboardAMovingShipStopsIt()
    {
        var savedShips = Item.ResolveShipEngine;
        try
        {
            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = new GameWorld(loggerFactory);
            world.InitMap(0, 256, 256);
            ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;

            var registry = new MultiRegistry();
            foreach (ushort id in new ushort[] { 0x4000, 0x4001, 0x4002, 0x4003 })
            {
                var def = new MultiDef { Id = id, Name = "test ship" };
                for (short dx = -2; dx <= 2; dx++)
                    for (short dy = -2; dy <= 2; dy++)
                        def.Components.Add(new MultiComponent
                        { TileId = 0x3E40, DeltaX = dx, DeltaY = dy, DeltaZ = 0, Visible = true });
                def.RecalcBounds();
                registry.Register(def);
            }

            var ships = new ShipEngine(world, registry, null);
            Item.ResolveShipEngine = () => ships;

            var owner = world.CreateCharacter();
            owner.IsPlayer = true;
            world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

            var ship = ships.PlaceShip(owner, 0x4000, new Point3D(120, 120, 0, 0), Direction.North);
            Assert.NotNull(ship);

            var sailor = world.CreateCharacter();
            sailor.IsPlayer = true;
            sailor.IsOnline = true;
            world.PlaceCharacter(sailor, new Point3D(120, 120, 0, 0));
            Assert.Same(ship, ships.FindShipAt(sailor.Position));

            ship!.MovementType = ShipMovementType.Normal;   // under sail

            var accounts = new AccountManager(loggerFactory);
            var client = TestHarness.CreateClient(loggerFactory, world, accounts, 1);
            TestHarness.AttachCharacter(client, sailor);

            client.OnDisconnect();

            Assert.Equal(ShipMovementType.Stop, ship.MovementType);
        }
        finally
        {
            Item.ResolveShipEngine = savedShips;
        }
    }

    [Fact]
    public void LoggingOffAshoreTouchesNoShip()
    {
        var savedShips = Item.ResolveShipEngine;
        try
        {
            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = new GameWorld(loggerFactory);
            world.InitMap(0, 256, 256);
            ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;

            var registry = new MultiRegistry();
            foreach (ushort id in new ushort[] { 0x4000, 0x4001, 0x4002, 0x4003 })
            {
                var def = new MultiDef { Id = id, Name = "test ship" };
                def.Components.Add(new MultiComponent
                { TileId = 0x3E40, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
                def.RecalcBounds();
                registry.Register(def);
            }

            var ships = new ShipEngine(world, registry, null);
            Item.ResolveShipEngine = () => ships;

            var owner = world.CreateCharacter();
            owner.IsPlayer = true;
            world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
            var ship = ships.PlaceShip(owner, 0x4000, new Point3D(120, 120, 0, 0), Direction.North);
            Assert.NotNull(ship);
            ship!.MovementType = ShipMovementType.Normal;

            var walker = world.CreateCharacter();
            walker.IsPlayer = true;
            walker.IsOnline = true;
            world.PlaceCharacter(walker, new Point3D(30, 30, 0, 0));   // nowhere near it

            var accounts = new AccountManager(loggerFactory);
            var client = TestHarness.CreateClient(loggerFactory, world, accounts, 2);
            TestHarness.AttachCharacter(client, walker);

            client.OnDisconnect();

            Assert.Equal(ShipMovementType.Normal, ship.MovementType);
        }
        finally
        {
            Item.ResolveShipEngine = savedShips;
        }
    }
}
