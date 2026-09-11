using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Who the follower cap binds (port plan İŞ-28 / PLAN-405).
///
/// The reference keeps the whole slot system behind OF_PETSLOTS - the summon path,
/// the taming path and the pet-memory bookkeeping all ask first (CCharSpell.cpp:2660,
/// CChar.cpp:1257) - and lets a GM past the maximum (fIgnoreMax, CCharUse.cpp:1238).
///
/// Neither was checked here. The flag sat in the OptionFlags enum with nothing reading
/// it, so the cap bound a shard that had switched the system off: the live one runs
/// OPTIONFLAGS=0x2080, which does not include the 0x10 bit.
/// </summary>
public sealed class FollowerCapGateParityTests : IDisposable
{
    private readonly OptionFlags _savedFlags = GameClient.ServerOptionFlags;

    public void Dispose() => GameClient.ServerOptionFlags = _savedFlags;

    private static (GameWorld World, Character Owner, Character Pet) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.PrivLevel = PrivLevel.Player;
        owner.MaxFollower = 0;                 // no room at all
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        return (world, owner, pet);
    }

    // ---- the flag ----------------------------------------------------------

    [Fact]
    public void WithTheFlagOffTheCapDoesNotBind()
    {
        GameClient.ServerOptionFlags = OptionFlags.None;
        var (_, owner, pet) = Setup();

        Assert.True(pet.TryAssignOwnership(owner, owner, enforceFollowerCap: true));
        Assert.True(pet.HasOwner(owner.Uid));
    }

    [Fact]
    public void WithTheFlagOnTheCapBinds()
    {
        GameClient.ServerOptionFlags = OptionFlags.PetSlots;
        var (_, owner, pet) = Setup();

        Assert.False(pet.TryAssignOwnership(owner, owner, enforceFollowerCap: true));
        Assert.False(pet.HasOwner(owner.Uid));
    }

    // ---- the GM exemption ---------------------------------------------------

    [Fact]
    public void AGmIsNeverStoppedByTheMaximum()
    {
        GameClient.ServerOptionFlags = OptionFlags.PetSlots;
        var (_, owner, pet) = Setup();
        owner.PrivLevel = PrivLevel.GM;

        Assert.True(pet.TryAssignOwnership(owner, owner, enforceFollowerCap: true));
    }

    // ---- the same gate on the stable ---------------------------------------

    [Fact]
    public void ClaimingAStabledPetAnswersToTheSameGate()
    {
        GameClient.ServerOptionFlags = OptionFlags.None;
        var (world, owner, pet) = Setup();
        Assert.True(pet.TryAssignOwnership(owner, owner));
        PetStorage.Park(pet, world);

        // The cap is full, but the system is switched off: the pet comes back.
        Assert.True(PetStorage.Unpark(pet, owner, world, owner.Position));

        PetStorage.Park(pet, world);
        GameClient.ServerOptionFlags = OptionFlags.PetSlots;
        Assert.False(PetStorage.Unpark(pet, owner, world, owner.Position));
    }
}
