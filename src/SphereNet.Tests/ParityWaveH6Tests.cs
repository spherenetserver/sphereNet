using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H6 (wiki/hedef.txt long tail):
//   * custom-house commit materializes INTERACTIVE fixtures (doors/containers)
//     as real items (Source-X CItemMultiCustom::CommitChanges) — recommit
//     replaces the previous set
//   * char verbs EQUIPHALO / EQUIPARMOR / EQUIPWEAPON (CChar::r_Verb)
//   * client verb CAST begins a spell cast from script
public class ParityWaveH6Tests
{
    private sealed class NullConsole : SphereNet.Core.Interfaces.ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "test";
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static void LoadDefs(string body)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_h6defs_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, body);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        File.Delete(tempFile);
    }

    [Fact]
    public void CustomHouseCommit_MaterializesDoorFixture_AndRecommitReplaces()
    {
        LoadDefs("""
            [ITEMDEF 06a5]
            DEFNAME=i_door_wood
            TYPE=t_door
            """);

        var world = CreateWorld();
        var housing = new HousingEngine(world, new MultiRegistry());
        var engine = new CustomHousingEngine(world, housing);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(1500, 1500, 0, 0));

        var multi = world.CreateItem();
        multi.BaseId = 0x4064;
        multi.ItemType = ItemType.MultiCustom;
        world.PlaceItem(multi, new Point3D(1500, 1500, 0, 0));

        engine.Begin(owner, multi);
        engine.Build(owner, 0x06A5, 2, 3); // a door tile
        engine.Build(owner, 0x0064, 1, 1); // a plain wall — stays virtual
        Assert.NotNull(engine.Commit(owner));

        // The door became a REAL item at the absolute position, linked to the
        // multi (the house key opens it); the wall did not.
        var fixtures = world.GetItemsInRange(new Point3D(1502, 1503, 0, 0), 0)
            .Where(i => i.BaseId == 0x06A5).ToList();
        var door = Assert.Single(fixtures);
        Assert.Equal(ItemType.Door, door.ItemType);
        Assert.Equal(multi.Uid, door.Link);
        Assert.DoesNotContain(world.GetItemsInRange(new Point3D(1501, 1501, 0, 0), 0),
            i => i.BaseId == 0x0064);

        // Recommit with the door moved: the old fixture is torn down.
        engine.Begin(owner, multi);
        engine.Build(owner, 0x06A5, -2, -2);
        Assert.NotNull(engine.Commit(owner));

        Assert.True(door.IsDeleted);
        Assert.Single(world.GetItemsInRange(new Point3D(1498, 1498, 0, 0), 0),
            i => i.BaseId == 0x06A5);
    }

    [Fact]
    public void EquipHalo_EquipsTimedLightSource()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.True(ch.TryExecuteCommand("EQUIPHALO", "30", new NullConsole()));

        var halo = ch.GetEquippedItem(Layer.TwoHanded);
        Assert.NotNull(halo);
        Assert.Equal(0x1647, halo!.BaseId);
        Assert.True(halo.Timeout > 0); // 30s lifetime armed
    }

    [Fact]
    public void EquipArmorAndWeapon_PullBestFromPack()
    {
        LoadDefs("""
            [ITEMDEF 01f15]
            DEFNAME=i_cloak_test
            LAYER=13

            [ITEMDEF 0f5e]
            DEFNAME=i_sword_test
            LAYER=1
            """);

        var world = CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);

        var tunic = world.CreateItem();
        tunic.BaseId = 0x1F15;
        tunic.SetTag("ARMOR", "10");
        pack.AddItem(tunic);

        var weakSword = world.CreateItem();
        weakSword.BaseId = 0x0F5E;
        weakSword.SetTag("DAM", "3");
        pack.AddItem(weakSword);
        var strongSword = world.CreateItem();
        strongSword.BaseId = 0x0F5E;
        strongSword.SetTag("DAM", "9");
        pack.AddItem(strongSword);

        Assert.True(ch.TryExecuteCommand("EQUIPARMOR", "", new NullConsole()));
        Assert.Same(tunic, ch.GetEquippedItem((Layer)13));

        Assert.True(ch.TryExecuteCommand("EQUIPWEAPON", "", new NullConsole()));
        Assert.Same(strongSword, ch.GetEquippedItem(Layer.OneHanded)); // best DAM wins
        Assert.Equal(pack.Uid, weakSword.ContainedIn);                 // loser stays packed
    }

    [Fact]
    public void CastClientVerb_BeginsSpellTargeting()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1801);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, caster);

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = (SpellType)1, // Clumsy
            Name = "Clumsy",
            Flags = SpellFlag.TargChar,
            ManaCost = 0,
            CastTimeBase = 1,
        });
        client.SetEngines(spellEngine: new SpellEngine(world, registry));

        Assert.True(client.TryExecuteScriptCommand(caster, "CAST", "1", null));
        Assert.True(client.HasPendingTarget); // targeted spell opened the cursor
    }
}
