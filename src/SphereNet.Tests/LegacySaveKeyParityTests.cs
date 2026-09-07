using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Keys a classic save writes that the engine has to understand (port plan İŞ-2 /
/// PLAN-106). The 56T shard dump parks whatever the loader cannot map in a SAVE.*
/// tag; every kind of key parked there is either an engine gap or a deliberate
/// exclusion, and this is where the ones classified as gaps are held to their
/// contract.
///
/// A pack renames a skill slot with the KEY of its [SKILL n] block, and Source-X looks
/// skills up by that key (CSkillDef "KEY="). 56T calls skill 54 Sailormanship, so its
/// characters carry "Sailormanship=70.0" - a name the built-in table does not have.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LegacySaveKeyParityTests : IDisposable
{
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;

    // Skill 54 is Spellweaving to the engine; this pack calls it Sailormanship, the
    // way the 56T pack does.
    private const string Script = """
        [SKILL 54]
        DEFNAME=Skill_Sailormanship
        KEY=Sailormanship
        TITLE=Captain

        [SKILL 1]
        DEFNAME=Skill_Anatomy
        KEY=Anatomy

        [ITEMDEF 014ec]
        DEFNAME=i_map_test
        NAME=Treasure map
        TYPE=t_map

        [ITEMDEF 0fbd]
        DEFNAME=i_book_test
        NAME=Book
        TYPE=t_book

        [ITEMDEF 03eb2]
        NAME=Ship side
        TYPE=t_ship_side_locked

        [MULTIDEF 05b]
        DEFNAME=m_test_ship
        NAME=Test ship
        TYPE=t_ship
        """;

    public LegacySaveKeyParityTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_skl_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Script);
        _resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose() => File.Delete(_scriptPath);

    private GameWorld NewWorld()
    {
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ASkillWrittenUnderThePacksOwnNameIsRead()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();

        // What a classic save line looks like for this shard.
        Assert.True(ch.TrySetProperty("Sailormanship", "70.0"));

        Assert.Equal(700, ch.GetSkill(SkillType.Spellweaving));
    }

    [Fact]
    public void ThatSameNameReadsBackOut()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Spellweaving, 700);

        Assert.True(ch.TryGetProperty("Sailormanship", out string value));
        Assert.Equal("700", value);
    }

    [Fact]
    public void TheEnginesOwnNameStillWorks()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();

        Assert.True(ch.TrySetProperty("Spellweaving", "42.0"));
        Assert.Equal(420, ch.GetSkill(SkillType.Spellweaving));
        Assert.True(ch.TryGetProperty("Spellweaving", out string value));
        Assert.Equal("420", value);
    }

    [Fact]
    public void APackNameThatMatchesNoSkillIsStillNotASkill()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();

        // Unknown key: it must not silently become skill 0.
        Assert.False(ch.TryGetProperty("Sailormanshi", out _));
    }

    [Fact]
    public void ASkillValueUnderThePackNameKeepsItsTenthsScaling()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();

        // The classic file writes tenths with a decimal point; a pack-named skill has
        // to go through the same normalisation the built-in names do.
        Assert.True(ch.TrySetProperty("Sailormanship", "12.5"));
        Assert.Equal(125, ch.GetSkill(SkillType.Spellweaving));
    }

    // ================================================================
    // A classic record carries no TYPE line: the type belongs to the ITEMDEF, and
    // upstream fixes the object's class from it at creation (CreateBase,
    // CItem.cpp:314) before reading a single key. Keys that only a map or a book
    // understands have to be read through that definition.

    [Fact]
    public void AMapTypedByItsDefinitionTakesItsPins()
    {
        var world = NewWorld();
        var map = world.CreateItem();
        map.BaseId = 0x14EC;                    // the itemdef says t_map; the record does not

        Assert.True(map.TrySetProperty("PIN", "150,150"));
        Assert.True(map.TrySetProperty("PIN", "160,170"));

        Assert.True(map.TryGetProperty("PINS", out string count));
        Assert.Equal("2", count);
        Assert.True(map.TryGetProperty("PIN.1", out string first));
        Assert.Equal("150,150", first);
        Assert.True(map.TryGetProperty("PIN.2", out string second));
        Assert.Equal("160,170", second);
    }

    [Fact]
    public void ABookTypedByItsDefinitionTakesItsPages()
    {
        var world = NewWorld();
        var book = world.CreateItem();
        book.BaseId = 0x0FBD;                   // t_book from the definition

        Assert.True(book.TrySetProperty("BODY.0", "first page"));
        Assert.True(book.TrySetProperty("BODY.1", "second page"));
        Assert.True(book.TrySetProperty("TITLE", "A title"));

        Assert.True(book.TryGetProperty("PAGES", out string pages));
        Assert.Equal("2", pages);
        Assert.Equal("A title", book.Name);
    }

    [Fact]
    public void AnInstanceTypeStillWinsOverItsDefinition()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        item.BaseId = 0x14EC;
        item.ItemType = ItemType.Normal;        // not a map any more, whatever the def says
        item.ItemType = ItemType.Container;

        Assert.False(item.TrySetProperty("PIN", "150,150"));
    }

    // ================================================================
    // A multi carries its structure's region inside its own record: upstream strips
    // the "REGION." prefix and hands the rest to the region (SHL_REGION,
    // CItemMulti.cpp:3011). A 56T house writes its owner that way.

    [Fact]
    public void AMultisRegionTagIsReadAndReadBack()
    {
        var world = NewWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;

        Assert.True(multi.TrySetProperty("REGION.TAG.owner", "09191"));

        // The item CARRIES the line - that is what a save writes back - while reading
        // REGION.<key> answers from the region, as it does on any object.
        Assert.True(multi.TryGetTag("REGION.TAG.OWNER", out string? stored));
        Assert.Equal("09191", stored);
        Assert.False(multi.TryGetTag("SAVE.REGION.TAG.owner", out _));
    }

    [Fact]
    public void AMultisRegionTagSurvivesASaveAndLoad()
    {
        var world = NewWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        Assert.True(multi.TrySetProperty("REGION.TAG.owner", "09191"));
        world.PlaceItem(multi, new Point3D(60, 60, 0, 0));

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_rt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, dir);

            var reloaded = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, dir);

            var back = reloaded.FindItem(multi.Uid);
            Assert.NotNull(back);
            Assert.True(back!.TryGetTag("REGION.TAG.OWNER", out string? stored));
            Assert.Equal("09191", stored);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnObjectStandingInTheRegionReadsItsTags()
    {
        var world = NewWorld();
        var region = new SphereNet.Game.World.Regions.Region { Name = "Test house" };
        region.AddRect(50, 50, 70, 70);
        region.SetTag("OWNER", "09191");
        world.AddRegion(region);

        var item = world.CreateItem();
        item.BaseId = 0x1F03;
        world.PlaceItem(item, new Point3D(60, 60, 0, 0));

        // The consumer the stored line exists for: whoever is inside the structure
        // asks the REGION for it.
        Assert.True(item.TryGetProperty("REGION.TAG.OWNER", out string value));
        Assert.Equal("09191", value);
    }

    // ================================================================
    // A classic guild or town stone keeps its whole roster in its own record: ALIGN,
    // ABBREV, CHARTER<n> and one MEMBER line per member, written as
    // uid,title,priv,loyaluid,abbrev,wedeclaredwar,accountgold (CItemStone.cpp:145).

    [Fact]
    public void AClassicStonesRosterBecomesAGuild()
    {
        var world = NewWorld();
        var master = world.CreateCharacter();
        var member = world.CreateCharacter();

        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        stone.Name = "Saints OF Ultima";

        Assert.True(stone.TrySetProperty("ALIGN", "1"));
        Assert.True(stone.TrySetProperty("ABBREV", "SOU"));
        Assert.True(stone.TrySetProperty("CHARTER0", "We stand together"));
        Assert.True(stone.TrySetProperty("MEMBER", $"0{master.Uid.Value:X},Lord,2,0{master.Uid.Value:X},1,0,50"));
        Assert.True(stone.TrySetProperty("MEMBER", $"0{member.Uid.Value:X},,1,0,0,0,0"));

        var guilds = new SphereNet.Game.Guild.GuildManager();
        guilds.DeserializeFromWorld(world);

        var guild = guilds.GetGuild(stone.Uid);
        Assert.NotNull(guild);
        // A classic stone writes no name of its own - the guild is called what the
        // stone is called.
        Assert.Equal("Saints OF Ultima", guild!.Name);
        Assert.Equal("SOU", guild.Abbreviation);
        Assert.Equal(SphereNet.Game.Guild.GuildAlign.Order, guild.Align);
        Assert.Equal("We stand together", guild.Charter);

        Assert.Equal(2, guild.Members.Count);
        var lord = guild.Members.Single(m => m.CharUid == master.Uid);
        Assert.Equal(SphereNet.Game.Guild.GuildPriv.Master, lord.Priv);
        Assert.Equal("Lord", lord.Title);
        Assert.Equal(50, lord.AccountGold);
        Assert.Equal(master.Uid, lord.LoyalTo);
        Assert.True(lord.ShowAbbrev);

        var plain = guild.Members.Single(m => m.CharUid == member.Uid);
        Assert.Equal(SphereNet.Game.Guild.GuildPriv.Member, plain.Priv);
        Assert.False(plain.ShowAbbrev);
    }

    [Fact]
    public void AMemberTitleKeepsItsPunctuation()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        stone.Name = "Punctuation";

        // The engine's own member record is colon-and-comma separated, so a title
        // carrying either has to come back whole.
        Assert.True(stone.TrySetProperty("MEMBER", $"0{ch.Uid.Value:X},The: Bold,1,0,1,0,0"));

        var guilds = new SphereNet.Game.Guild.GuildManager();
        guilds.DeserializeFromWorld(world);

        var guild = guilds.GetGuild(stone.Uid);
        Assert.Equal("The: Bold", Assert.Single(guild!.Members).Title);
    }

    [Fact]
    public void AWarRecordInTheRosterBecomesARelationRatherThanAMember()
    {
        var world = NewWorld();
        var ours = world.CreateCharacter();
        var theirs = world.CreateCharacter();

        var enemyStone = world.CreateItem();
        enemyStone.ItemType = ItemType.StoneGuild;
        enemyStone.Name = "The other guild";
        Assert.True(enemyStone.TrySetProperty("MEMBER", $"0{theirs.Uid.Value:X},,2,0,1,0,0"));

        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        stone.Name = "At war";
        Assert.True(stone.TrySetProperty("MEMBER", $"0{ours.Uid.Value:X},,1,0,1,0,0"));
        // Privilege 100 is STONEPRIV_ENEMY: the same list carries the war records, and
        // the sixth field says WE declared it.
        Assert.True(stone.TrySetProperty("MEMBER", $"0{enemyStone.Uid.Value:X},,100,0,0,1,0"));

        var guilds = new SphereNet.Game.Guild.GuildManager();
        guilds.DeserializeFromWorld(world);

        var guild = guilds.GetGuild(stone.Uid);
        Assert.NotNull(guild);
        Assert.Single(guild!.Members);                 // the enemy is not a member
        var relation = Assert.Single(guild.Relations.Values);
        Assert.Equal(enemyStone.Uid, relation.OtherStoneUid);
        Assert.True(relation.WeDeclaredWar);
        Assert.False(relation.WeDeclaredAlliance);
    }

    [Fact]
    public void AnOrdinaryItemIsNotAStone()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        item.BaseId = 0x1F03;

        // ALIGN on something that is not a stone means nothing to the guild layer.
        Assert.False(item.TrySetProperty("ALIGN", "1"));
    }

    // ================================================================
    // A classic ship names its hold and its planks in its own record and lists no
    // components at all (HATCH / PLANK, CItemShip.cpp:118/208).

    [Fact]
    public void AClassicShipNamesItsHoldAndPlanks()
    {
        var world = NewWorld();
        var hull = world.CreateItem();
        hull.ItemType = ItemType.Ship;
        var hold = world.CreateItem();
        var plankA = world.CreateItem();
        var plankB = world.CreateItem();

        Assert.True(hull.TrySetProperty("HATCH", $"0{hold.Uid.Value:X}"));
        Assert.True(hull.TrySetProperty("PLANK", $"0{plankA.Uid.Value:X}"));
        Assert.True(hull.TrySetProperty("PLANK", $"0{plankB.Uid.Value:X}"));

        Assert.True(hull.TryGetTag("SHIP.HOLD", out string? storedHold));
        Assert.Equal($"0{hold.Uid.Value:X}", storedHold);
        Assert.True(hull.TryGetTag("SHIP.PLANKS", out string? storedPlanks));
        Assert.Equal($"0{plankA.Uid.Value:X},0{plankB.Uid.Value:X}", storedPlanks);
    }

    [Fact]
    public void AClearedUidNamesNothing()
    {
        var world = NewWorld();
        var hull = world.CreateItem();
        hull.ItemType = ItemType.Ship;

        // What seven of the 56T ships carry: the item flag over an index of all ones,
        // which is how a classic save writes a field it has cleared
        // (CUID::IsValidUID, CUID.cpp:32).
        Assert.True(hull.TrySetProperty("HATCH", "04fffffff"));

        Assert.False(hull.TryGetTag("SHIP.HOLD", out _));
    }

    [Theory]
    [InlineData(0x4000BEEFu, true)]
    [InlineData(0u, false)]
    [InlineData(0x0FFFFFFFu, false)]
    [InlineData(0x4FFFFFFFu, false)]
    [InlineData(0xFFFFFFFFu, false)]
    public void TheReferencesUidRuleDecidesWhetherAFieldNamesAnything(uint value, bool names)
    {
        Assert.Equal(names, new Serial(value).NamesAnObject);
    }

    [Fact]
    public void AShipRebuiltFromAClassicRecordHasItsHoldAndPlanks()
    {
        var world = NewWorld();
        var owner = world.CreateCharacter();

        var hull = world.CreateItem();
        hull.ItemType = ItemType.Ship;
        world.PlaceItem(hull, new Point3D(100, 100, 0, 0));
        hull.SetTag("OWNER", $"0{owner.Uid.Value:X}");

        var hold = world.CreateItem();
        hold.ItemType = ItemType.ShipHold;
        var plank = world.CreateItem();
        plank.ItemType = ItemType.ShipPlank;

        Assert.True(hull.TrySetProperty("HATCH", $"0{hold.Uid.Value:X}"));
        Assert.True(hull.TrySetProperty("PLANK", $"0{plank.Uid.Value:X}"));

        var engine = new SphereNet.Game.Ships.ShipEngine(
            world, new SphereNet.Game.Housing.MultiRegistry(), null);
        engine.DeserializeFromWorld();

        var ship = engine.GetShip(hull.Uid);
        Assert.NotNull(ship);
        // The classic record lists no components, so these two uids are the only way
        // the hold and the plank are ever found.
        Assert.Same(hold, ship!.GetHold(world));
        Assert.Equal(1, ship.GetPlankCount(world));
        Assert.Same(plank, ship.GetPlank(0, world));
    }

    // ================================================================
    // Sphere 0.56 kept TWO kill counters; the reference keeps ONE - KILLS, the murder
    // count (CCharPlayer.h:49) - and translates an old key into the field it maps to
    // (CWorldImport.cpp:750).

    [Fact]
    public void TheMurderCountAnOldShardWroteReachesTheOneCounterTheEngineKeeps()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_k_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "spherechars.scp"), """
                [WORLDCHAR c_man]
                SERIAL=0f9e6
                NAME=Killer
                P=100,100,0
                KILLSPLAYER=5
                KILLSNPC=12
                """);

            var world = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }))
                .Load(world, dir);

            var ch = world.FindChar(new Serial(0x0F9E6));
            Assert.NotNull(ch);
            Assert.Equal(5, ch!.Kills);
            // The creature counter has no counterpart in the reference, so it stays
            // script-readable data rather than becoming a second engine field.
            Assert.True(ch.TryGetTag("KILLSNPC", out string? npcKills));
            Assert.Equal("12", npcKills);
            Assert.False(ch.TryGetTag("SAVE.KILLSPLAYER", out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================
    // A structure's record heads with a [MULTIDEF] name and writes neither an ID nor a
    // TYPE line; its parts head with the item id itself. Upstream has no such split -
    // a multi's definition IS its item base (CItemBaseMulti) - so both have to resolve
    // through the definition, or the hull loads as graphic 0, type Normal.

    private SphereNet.Persistence.Load.WorldLoader LoaderWithDefinitions()
    {
        var loader = new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }));
        loader.ResolveItemDef = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };
        loader.ResolveMultiDef = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            if (!rid.IsValid || rid.Type != ResType.MultiDef || rid.Index is < 0 or > ushort.MaxValue)
                return null;
            string typeName = "";
            var link = _resources.GetResource(rid);
            if (link?.StoredKeys != null)
            {
                foreach (var key in link.StoredKeys)
                {
                    if (key.Key.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
                    {
                        typeName = key.Arg.Trim();
                        break;
                    }
                }
            }
            return ((ushort)rid.Index, SphereNet.Scripting.Definitions.ItemDef.ParseTypeName(typeName));
        };
        return loader;
    }

    [Fact]
    public void AStructureTakesItsGraphicAndTypeFromItsMultiDefinition()
    {
        var world = NewWorld();
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_m_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "sphereworld.scp"), """
                [WORLDITEM m_test_ship]
                SERIAL=04000100
                P=100,100,0

                [WORLDITEM 03eb2]
                SERIAL=04000101
                P=101,100,0
                """);

            LoaderWithDefinitions().Load(world, dir);

            var hull = world.FindItem(new Serial(0x04000100));
            Assert.NotNull(hull);
            Assert.Equal(0x5B, hull!.BaseId);              // the multi id it is drawn as
            Assert.Equal(ItemType.Ship, hull.ItemType);    // the TYPE its [MULTIDEF] declares

            // A part heads with the item id itself and writes no ID line.
            var part = world.FindItem(new Serial(0x04000101));
            Assert.NotNull(part);
            Assert.Equal(0x3EB2, part!.BaseId);
            Assert.Equal(ItemType.ShipSideLocked, part.ItemType);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnInstanceTypeInTheRecordStillOutranksTheDefinition()
    {
        var world = NewWorld();
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_m2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // One 56T hull carries exactly this: a ship multi retyped in-game.
            File.WriteAllText(Path.Combine(dir, "sphereworld.scp"), """
                [WORLDITEM m_test_ship]
                SERIAL=04000102
                TYPE=t_multi
                P=102,100,0
                """);

            LoaderWithDefinitions().Load(world, dir);

            var hull = world.FindItem(new Serial(0x04000102));
            Assert.NotNull(hull);
            Assert.Equal(ItemType.Multi, hull!.ItemType);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AShipWithNoOwnerIsStillAShip()
    {
        var world = NewWorld();
        var hull = world.CreateItem();
        hull.ItemType = ItemType.Ship;
        world.PlaceItem(hull, new Point3D(140, 140, 0, 0));

        var engine = new SphereNet.Game.Ships.ShipEngine(
            world, new SphereNet.Game.Housing.MultiRegistry(), null);
        engine.DeserializeFromWorld();

        // Upstream builds a CItemShip from the TYPE and leaves the owner empty when
        // nobody owns it - a guard ship, a scripted decoration.
        var ship = engine.GetShip(hull.Uid);
        Assert.NotNull(ship);
        Assert.False(ship!.Owner.IsValid);
    }

    // ================================================================
    // A structure's region belongs to the structure. Upstream realizes it whenever the
    // multi is put in the world, owner or not (MultiRealizeRegion, CItemMulti.cpp:191).

    private static SphereNet.Game.Housing.MultiRegistry RegistryWithFootprint(ushort id)
    {
        var registry = new SphereNet.Game.Housing.MultiRegistry();
        var def = new SphereNet.Game.Housing.MultiDef { Id = id, Name = "test multi" };
        def.Components.Add(new SphereNet.Game.Housing.MultiComponent
            { TileId = 0x0001, DeltaX = -2, DeltaY = -2, DeltaZ = 0, Visible = true });
        def.Components.Add(new SphereNet.Game.Housing.MultiComponent
            { TileId = 0x0001, DeltaX = 2, DeltaY = 2, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        registry.Register(def);
        return registry;
    }

    [Fact]
    public void AStructureWithNoOwnershipRecordStillHasItsRegion()
    {
        var world = NewWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x7E;
        multi.ItemType = ItemType.Multi;
        multi.Name = "Lonely keep";
        world.PlaceItem(multi, new Point3D(80, 80, 0, 0));

        // What a classic record carries: the region's own flags, events and tags - and
        // no ownership record this engine would recognise.
        Assert.True(multi.TrySetProperty("REGION.EVENTS", "r_house_private"));
        Assert.True(multi.TrySetProperty("REGION.TAG.owner", "09191"));

        var housing = new SphereNet.Game.Housing.HousingEngine(world, RegistryWithFootprint(0x7E));
        housing.DeserializeFromWorld();

        Assert.Equal(0, housing.HouseCount);          // no owner: no house record
        var region = world.FindRegion(multi.Position);
        Assert.NotNull(region);
        Assert.Equal("Lonely keep", region!.Name);
        Assert.True(region.TryGetTag("OWNER", out string? owner));
        Assert.Equal("09191", owner);
        Assert.Single(region.Events);
    }

    [Fact]
    public void RebuildingTwiceDoesNotStackASecondRegionOnTheStructure()
    {
        var world = NewWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x7E;
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(90, 90, 0, 0));

        var housing = new SphereNet.Game.Housing.HousingEngine(world, RegistryWithFootprint(0x7E));
        housing.DeserializeFromWorld();
        var first = world.FindRegion(multi.Position);
        housing.DeserializeFromWorld();
        var second = world.FindRegion(multi.Position);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Uid, second!.Uid);     // the old one is gone, not layered
    }
}
