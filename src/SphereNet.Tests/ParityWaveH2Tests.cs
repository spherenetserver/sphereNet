using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H2 (wiki/hedef.txt long tail):
//   * GARBAGE = FixWeirdness world-integrity sweep (Source-X CWorld::
//     GarbageCollection): dangling CONT recovered, phantom equips re-packed,
//     graphicless items deleted, missing decay timers armed
//   * CItemMulti management verbs drive the LIVE House (ADDCOOWNER/LOCKITEM/...)
//   * @SpellCast LOCAL.WOP rewrites/suppresses the spoken power words
public class ParityWaveH2Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void GarbageCollection_RecoversDanglingContainment()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        // Corrupt: flag it contained in a container that does not exist.
        item.ContainedIn = new Serial(0x40FFFFFF);

        var (_, fixedCount, _) = world.GarbageCollection();

        Assert.True(fixedCount >= 1);
        Assert.False(item.ContainedIn.IsValid); // recovered to the ground
        Assert.True(item.DecayTime > 0);        // with a decay window
    }

    [Fact]
    public void GarbageCollection_RepacksPhantomEquip_AndDeletesGraphicless()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);

        // Phantom equip: flagged equipped on the char but absent from the
        // equip table (0x2202).
        var phantom = world.CreateItem();
        phantom.BaseId = 0x1515;
        phantom.IsEquipped = true;
        phantom.EquipLayer = Layer.Cape;
        phantom.ContainedIn = ch.Uid;

        // Graphicless item (0x2103).
        var husk = world.CreateItem();
        world.PlaceItem(husk, new Point3D(101, 100, 0, 0));

        var (_, fixedCount, deleted) = world.GarbageCollection();

        Assert.True(fixedCount >= 1);
        Assert.True(deleted >= 1);
        Assert.False(phantom.IsEquipped);
        Assert.Equal(pack.Uid, phantom.ContainedIn); // recovered into the pack
        Assert.True(husk.IsDeleted);
    }

    [Fact]
    public void GarbageCollection_LeavesHealthyItemsAlone()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        var gem = world.CreateItem();
        gem.BaseId = 0x0F26;
        pack.AddItem(gem);

        var (_, fixedCount, deleted) = world.GarbageCollection();

        Assert.Equal(0, fixedCount);
        Assert.Equal(0, deleted);
        Assert.Equal(pack.Uid, gem.ContainedIn);
        Assert.True(pack.IsEquipped);
    }

    [Fact]
    public void MultiVerbs_DriveLiveHouse()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x4064;
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(1500, 1500, 0, 0));

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(1500, 1500, 0, 0));

        var house = new SphereNet.Game.Housing.House(multi) { Owner = owner.Uid };
        Item.ResolveHouse = uid => uid == multi.Uid ? house : null;
        try
        {
            var friend = world.CreateCharacter();
            friend.IsPlayer = true;

            Assert.True(multi.TryExecuteCommand("ADDCOOWNER", $"0{friend.Uid.Value:X}", null!));
            Assert.Contains(friend.Uid, house.CoOwners);

            Assert.True(multi.TryExecuteCommand("DELCOOWNER", $"0{friend.Uid.Value:X}", null!));
            Assert.DoesNotContain(friend.Uid, house.CoOwners);

            var chair = world.CreateItem();
            chair.BaseId = 0x0B5A;
            world.PlaceItem(chair, new Point3D(1500, 1501, 0, 0));
            Assert.True(multi.TryExecuteCommand("LOCKITEM", $"0{chair.Uid.Value:X}", null!));
            Assert.True(house.IsLockedDown(chair.Uid));
            Assert.True(chair.IsAttr(ObjAttributes.LockedDown));

            Assert.True(multi.TryExecuteCommand("RELEASE", $"0{chair.Uid.Value:X}", null!));
            Assert.False(house.IsLockedDown(chair.Uid));
        }
        finally
        {
            Item.ResolveHouse = null;
        }
    }

    [Fact]
    public void CastStart_WopOverride_RewritesOrSilences()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Heal,
            Name = "Heal",
            Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
            ManaCost = 0,
            CastTimeBase = 1,
            Runes = "In Mani",
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        string? spoken = null;
        engine.OnSpellWords = (_, words) => spoken = words;

        // Default: the spell's own mantra.
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        Assert.Equal("In Mani", spoken);
        caster.ClearCastState();

        // LOCAL.WOP rewrite.
        spoken = null;
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position, "Abra Kadabra") > 0);
        Assert.Equal("Abra Kadabra", spoken);
        caster.ClearCastState();

        // Cleared WOP = silent cast.
        spoken = null;
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position, "") > 0);
        Assert.Null(spoken);
    }
}
