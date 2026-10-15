using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A player vendor hired by a script, and the items a GM adds to stock it.
///
/// Upstream's owner of a pet IS the link of its MEMORY_IPET memory (NPC_PetGetOwner,
/// CCharNPCStatus.cpp:471). A vendor deed that hires the Source-X way - an i_memory
/// coloured memory_ipet, linked to the owner - left SphereNet's vendor ownerless, so
/// its owner could not stock it. And .ADD names nothing (CItem::CreateTemplate): the
/// token was stamped as the item's name, so an item without a NAME= line went by its
/// defname ("Setting price of i_gears to 100").
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerVendorOwnershipTests
{
    [Fact]
    public void APetsOwnerIsItsIPetMemorysLink()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        var vendor = world.CreateCharacter();
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

        // As the deed script makes it: SERV.NEWITEM=i_memory, COLOR=memory_ipet,
        // LINK=<owner>, LAYER=layer_special, CONT=<vendor>.
        var memory = world.CreateItem();
        memory.BaseId = 0x2007;
        Assert.True(memory.TrySetProperty("COLOR", ((ushort)MemoryType.IPet).ToString()));
        memory.Link = owner.Uid;
        vendor.Equip(memory, Layer.Special);
        Assert.False(vendor.OwnerSerial.IsValid); // not a pet (STATF_PET) - no owner

        vendor.SetStatFlag(StatFlag.Pet);
        Assert.Equal(owner.Uid, vendor.OwnerSerial);
        Assert.True(vendor.HasOwner(owner.Uid));
    }

    [Fact]
    public void AnAddedItemWithoutANameLineIsNotNamedByItsDefname()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_addname_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, """
            [ITEMDEF 01053]
            DEFNAME=i_gears
            """);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? "",
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 9511);
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, gm);
        client.SetEngines(commands: new SphereNet.Game.Speech.CommandHandler { Resources = resources });

        Assert.True(client.TryAddAtTarget("i_gears", new Point3D(101, 100, 0, 0)));

        var added = world.GetItemsInRange(new Point3D(101, 100, 0, 0), 0).Single(i => i.BaseId == 0x1053);
        Assert.NotEqual("i_gears", added.Name);
        Assert.NotEqual("i_gears", added.GetName());
    }
}
