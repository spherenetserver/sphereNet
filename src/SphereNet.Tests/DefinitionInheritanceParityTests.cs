using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Definition loading against the reference: ID= copies its base when the line is read
/// (CopyBasic), a DUPEITEM stub IS its master, spawn members split on every Str_ParseCmds
/// separator, TWOHANDS only on 1/Y, definition TAGs stay on the definition, paged
/// REGIONTYPEs, [STARTS] v2, and the numeric reading of definition values.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DefinitionInheritanceParityTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (string f in _files)
        {
            try { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); }
            catch (IOException) { }
        }
    }

    private ResourceHolder Load(string contents, bool loadDefs = true)
    {
        string file = Path.Combine(Path.GetTempPath(), $"sphnet_inh_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, contents);
        _files.Add(file);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? ""
        };
        resources.LoadResourceFile(file);
        if (loadDefs)
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        return resources;
    }

    private static int IndexOf(ResourceHolder r, string defname) => r.ResolveDefName(defname).Index;

    private static long Num(string? text) => ScriptNumber.TryParseToken(text, out long v) ? v : -1;

    // ------------------------------------------------------------------ SPAWN

    [Theory]
    [InlineData("c_bird  10", "c_bird", 10)]
    [InlineData("c_bird\t7", "c_bird", 7)]
    [InlineData("c_bird,4", "c_bird", 4)]
    [InlineData("c_bird", "c_bird", 1)]
    [InlineData("0200,9", "0200", 9)]
    [InlineData("10,c_bird", "c_bird", 10)]
    public void SpawnMember_SplitsOnEverySeparator(string arg, string name, int weight)
    {
        var group = new SpawnGroupDef(new ResourceId(ResType.Spawn, 1));
        group.LoadFromKey("ID", arg);
        Assert.Single(group.Members);
        Assert.Equal(name, group.Members[0].CharDefName);
        Assert.Equal(weight, group.Members[0].Weight);
    }

    [Fact]
    public void SpawnSection_WhitespaceWeightedMembersResolve()
    {
        var r = Load("""
            [SPAWN spawn_birds]
            ID=c_bird  10
            ID=c_crow	5
            """, loadDefs: false);
        var group = (SpawnGroupDef)r.GetResource(r.ResolveDefName("spawn_birds"))!;
        Assert.Equal([("c_bird", 10), ("c_crow", 5)], group.Members);
        Assert.Equal(15, group.TotalWeight);
    }

    // ------------------------------------------------------------------ CHARDEF ID=

    [Fact]
    public void CharDefId_CopiesTheBodyDefinitionsBasics_ChildDeclaredFirst()
    {
        var r = Load("""
            [CHARDEF c_ghoul]
            SOUND=0100
            NAME=ghoul
            ID=c_zombie
            DAM=7,9

            [CHARDEF 03]
            DEFNAME=c_zombie
            NAME=zombie
            SOUND=01d7
            DAM=3,5
            ARMOR=10
            FOODTYPE=15 t_meat_raw
            DESIRES=i_gold
            RESOURCES=2 i_ribs_raw
            CAN=MT_WALK
            STR=50
            """);
        var ghoul = DefinitionLoader.GetCharDef(IndexOf(r, "c_ghoul"))!;
        Assert.True(ghoul.HasIdBase);
        Assert.Equal(0x1d7, ghoul.SoundBase);           // written before ID=, overwritten
        Assert.Equal("ghoul", ghoul.Name);              // a name of its own is kept
        Assert.Equal((7, 9), (ghoul.AttackMin, ghoul.AttackMax));  // after ID=, overrides
        Assert.Equal((10, 10), (ghoul.DefenseMin, ghoul.DefenseMax));
        Assert.Equal("15 t_meat_raw", ghoul.FoodTypeRaw);
        Assert.Equal(15, ghoul.MaxFood);
        Assert.Single(ghoul.Desires);
        Assert.Single(ghoul.CarveResources);
        Assert.Equal(0, ghoul.StrMin);                  // stats are not part of CopyBasic
        Assert.Equal((ushort)3, CharDefHelper.ResolveBodyId(ghoul, IndexOf(r, "c_ghoul"), r));
    }

    [Fact]
    public void CharDefId_NamingANamedDefinition_OnlyBorrowsTheBody()
    {
        var r = Load("""
            [CHARDEF 0c8]
            DEFNAME=c_horse_base
            SOUND=0a8

            [CHARDEF c_horse_brown_dk]
            ID=c_horse_base
            SOUND=0aa

            [CHARDEF c_rider_horse]
            ID=c_horse_brown_dk
            """);
        var rider = DefinitionLoader.GetCharDef(IndexOf(r, "c_rider_horse"))!;
        // c_horse_brown_dk is not in the body range: SetDispID refuses to copy it.
        Assert.False(rider.HasIdBase);
        Assert.Equal(0, rider.SoundBase);
        Assert.Equal((ushort)0xc8, CharDefHelper.ResolveBodyId(rider, IndexOf(r, "c_rider_horse"), r));
    }

    // ------------------------------------------------------------------ ITEMDEF ID=

    [Fact]
    public void ItemDefId_CopiesTheBase_AtTheLine()
    {
        var r = Load("""
            [ITEMDEF i_spear_vanq]
            WEIGHT=1
            ID=i_spear
            NAME=spear of vanquishing
            TDATA3=05

            [ITEMDEF 0f62]
            DEFNAME=i_spear
            NAME=spear
            TYPE=t_weapon_fence
            LAYER=2
            DAM=2,36
            ARMOR=1
            WEIGHT=7
            SPEED=46
            VALUE=100
            DUPELIST=0f63
            TDATA1=011
            RESOURCES=12 i_log
            TAG.SECRET=1
            """);
        var vanq = DefinitionLoader.GetItemDef(IndexOf(r, "i_spear_vanq"))!;
        Assert.True(vanq.HasIdBase);
        Assert.Equal(ItemType.WeaponFence, vanq.Type);
        Assert.Equal((Layer)2, vanq.Layer);
        Assert.Equal((2, 36), (vanq.AttackMin, vanq.AttackMax));
        Assert.Equal(70, vanq.Weight);                   // WEIGHT=1 came before ID=
        Assert.Equal(46, vanq.Speed);
        Assert.Equal((ushort)0x0f62, vanq.DispIndex);
        Assert.Equal("0f63", vanq.DupeList);
        Assert.Equal(0x11u, vanq.TData1);
        Assert.Equal(5u, vanq.TData3);                   // after ID=, overrides
        Assert.Equal("spear of vanquishing", vanq.Name);
        // Not part of CopyBasic.
        Assert.Equal(0, vanq.ValueMax);
        Assert.Equal("", vanq.ResourcesRaw);
        Assert.Null(vanq.TagDefs.Get("SECRET"));
        // The base itself is untouched.
        var spear = DefinitionLoader.GetItemDef(0x0f62)!;
        Assert.False(spear.HasIdBase);
        Assert.Equal(100, spear.ValueMax);
    }

    [Fact]
    public void ItemDefId_NumericBase_AndAStubFollowedToItsMaster()
    {
        var r = Load("""
            [ITEMDEF 01bf2]
            DEFNAME=i_ingot_iron
            TYPE=t_ingot
            WEIGHT=.1

            [ITEMDEF i_ingot_agapite]
            ID=01bf2
            NAME=agapite ingot

            [ITEMDEF 07dc]
            DEFNAME=i_lantern_lit
            NAME=lit lantern
            TYPE=t_light_lit
            LAYER=2

            [ITEMDEF 07dd]
            DUPEITEM=07dc

            [ITEMDEF i_lantern_custom]
            ID=07dd
            """);
        var agapite = DefinitionLoader.GetItemDef(IndexOf(r, "i_ingot_agapite"))!;
        Assert.Equal(ItemType.Ingot, agapite.Type);
        Assert.Equal(1, agapite.Weight);
        Assert.Equal((ushort)0x1bf2, agapite.DispIndex);

        var custom = DefinitionLoader.GetItemDef(IndexOf(r, "i_lantern_custom"))!;
        Assert.Equal(ItemType.LightLit, custom.Type);   // the stub's master is the base
        Assert.Equal("lit lantern", custom.Name);
        Assert.Equal((ushort)0x07dd, custom.DispIndex); // ...and the stub's graphic is kept
    }

    [Fact]
    public void NumberedItemDef_IdLine_DoesNotCopy()
    {
        var r = Load("""
            [ITEMDEF 0f62]
            DEFNAME=i_spear
            TYPE=t_weapon_fence
            DAM=2,36

            [ITEMDEF 01000]
            ID=i_spear
            """);
        var numbered = DefinitionLoader.GetItemDef(0x1000)!;
        Assert.False(numbered.HasIdBase);
        Assert.Equal(0, numbered.AttackMax);
    }

    // ------------------------------------------------------------------ DUPEITEM

    [Fact]
    public void DupeItemStub_IsItsMaster_ButKeepsItsGraphic()
    {
        var r = Load("""
            [ITEMDEF 07dc]
            DEFNAME=i_lantern_lit
            NAME=lit lantern
            TYPE=t_light_lit
            VALUE=12
            RESOURCES=2 i_ingot_iron
            TEVENTS=t_lantern_events
            TAG.FUEL=50
            CATEGORY=Lights

            [ITEMDEF 07dd]
            DUPEITEM=07dc

            [ITEMDEF 07de]
            NAME=own stub name
            DUPEITEM=07dc

            [TYPEDEF t_lantern_events]
            ON=@DClick
            RETURN 1
            """);
        var master = DefinitionLoader.GetItemDef(0x07dc)!;
        var stub = DefinitionLoader.GetItemDef(0x07dd)!;
        Assert.Equal(0x07dc, stub.DupeMasterIndex);
        Assert.Equal("lit lantern", stub.Name);
        Assert.Equal(ItemType.LightLit, stub.Type);
        Assert.Equal((12, 12), (stub.ValueMin, stub.ValueMax));
        Assert.Equal(master.ResourcesRaw, stub.ResourcesRaw);
        Assert.Equal(master.Events, stub.Events);
        Assert.Equal("50", stub.TagDefs.Get("FUEL"));
        Assert.Equal("Lights", stub.BaseDefs.Get("CATEGORY"));
        Assert.Equal((ushort)0x07dc, stub.DupItemId);
        Assert.Equal(0, master.DupeMasterIndex);
        Assert.Equal(0x07dc, ResourceMatchIndexFor(0x07dd));
        // A property the pack wrote on a stub itself is kept; the rest is the master's.
        var named = DefinitionLoader.GetItemDef(0x07de)!;
        Assert.Equal("own stub name", named.Name);
        Assert.Equal(ItemType.LightLit, named.Type);
    }

    private static int ResourceMatchIndexFor(ushort graphic)
    {
        var item = new Item { BaseId = graphic };
        return ResourceMatch.ItemDefIndexOf(item);
    }

    [Fact]
    public void DupeItemChain_IsNotFollowed()
    {
        Load("""
            [ITEMDEF 0100]
            NAME=first
            TYPE=t_container

            [ITEMDEF 0101]
            DUPEITEM=0100

            [ITEMDEF 0102]
            NAME=third
            DUPEITEM=0101
            """);
        // MakeDupeReplacement refuses a master that is itself a stub ("circle").
        var third = DefinitionLoader.GetItemDef(0x0102)!;
        Assert.Equal(0, third.DupeMasterIndex);
        Assert.Equal("third", third.Name);
        Assert.Equal(0x0100, DefinitionLoader.GetItemDef(0x0101)!.DupeMasterIndex);
    }

    // ------------------------------------------------------------------ TWOHANDS

    [Theory]
    [InlineData("N", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("Y", true)]
    [InlineData("yes", true)]
    public void TwoHands_OnlyOneOrYes(string arg, bool twoHanded)
    {
        var def = new ItemDef(new ResourceId(ResType.ItemDef, 0x13b9));
        def.LoadFromKey("LAYER", "1");
        def.LoadFromKey("TWOHANDS", arg);
        Assert.Equal(twoHanded, def.TwoHands);
        Assert.Equal(twoHanded ? Layer.TwoHanded : Layer.OneHanded, def.Layer);
    }

    // ------------------------------------------------------------------ weapon DAM

    [Fact]
    public void WeaponDamage_ReadsTheWeaponsOwnDefinition()
    {
        Load("""
            [ITEMDEF 020E]
            DEFNAME=i_bone_machete
            TYPE=t_weapon_sword
            DAM=13,15

            [ITEMDEF 020F]
            TYPE=t_weapon_sword
            DAM=3,5
            """);
        Assert.Equal((13, 15), CombatEngine.WeaponDamageFromDefinition(0x020E));
        Assert.Equal((3, 5), CombatEngine.WeaponDamageFromDefinition(0x020F));
        Assert.Null(CombatEngine.WeaponDamageFromDefinition(0x0210));
    }

    // ------------------------------------------------------------------ numbers

    [Fact]
    public void DefinitionNumbers_ReadAsTheExpressionReaderDoes()
    {
        var r = Load("""
            [ITEMDEF 0100]
            DAM=1.5,3
            TDATA3=190,95
            WEIGHT=.1

            [ITEMDEF 0101]
            WEIGHT=0.2
            ARMOR=4 8

            [CHARDEF 0102]
            DEFNAME=c_test_num
            DAM=1.5
            """);
        var a = DefinitionLoader.GetItemDef(0x100)!;
        Assert.Equal((15, 3), (a.AttackMin, a.AttackMax));
        Assert.Equal(190u, a.TData3);
        Assert.Equal(1, a.Weight);
        var b = DefinitionLoader.GetItemDef(0x101)!;
        Assert.Equal(2, b.Weight);
        Assert.Equal((4, 8), (b.DefenseMin, b.DefenseMax));
        var c = DefinitionLoader.GetCharDef(IndexOf(r, "c_test_num"))!;
        Assert.Equal((15, 15), (c.AttackMin, c.AttackMax));
    }

    [Theory]
    [InlineData("1.5", 15)]
    [InlineData("20.0", 200)]
    [InlineData(".1", 1)]
    [InlineData("190,95", 190)]
    [InlineData("010", 16)]
    [InlineData("0x10", 16)]
    [InlineData("-5", -5)]
    [InlineData("0.2", 2)]
    public void LeadingNumber_FollowsGetSingle(string text, long expected)
    {
        Assert.True(ScriptNumber.TryParseLeadingNumber(text, out long value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TypeDefsNumber_IsUsedForATypeNameTheEngineDoesNotKnow()
    {
        Load("""
            [TYPEDEFS]
            t_chair 40
            t_custom_widget 1500

            [ITEMDEF 0b4f]
            TYPE=t_chair

            [ITEMDEF 0b50]
            TYPE=t_weapon_sword
            """);
        Assert.Equal((ItemType)40, DefinitionLoader.GetItemDef(0x0b4f)!.Type);
        Assert.Equal(ItemType.WeaponSword, DefinitionLoader.GetItemDef(0x0b50)!.Type);
    }

    [Fact]
    public void ItemDefName2_IsASecondName()
    {
        var r = Load("""
            [ITEMDEF 0e75]
            DEFNAME=i_backpack
            DEFNAME2=i_pack_alias
            """);
        Assert.Equal(0x0e75, IndexOf(r, "i_pack_alias"));
    }

    [Fact]
    public void CastNoEquip_IsTheEquipOnCastFlag()
    {
        Load("""
            [ITEMDEF 0a15]
            CASTNOEQUP=1

            [ITEMDEF 0a16]
            """);
        Assert.NotEqual(0UL, (ulong)(DefinitionLoader.GetItemDef(0x0a15)!.Can & CanFlags.I_EquipOnCast));
        Assert.Equal(0UL, (ulong)(DefinitionLoader.GetItemDef(0x0a16)!.Can & CanFlags.I_EquipOnCast));
    }

    [Fact]
    public void UnknownSpellKey_IsReadableAsWritten()
    {
        var spells = new SpellRegistry();
        string file = Path.Combine(Path.GetTempPath(), $"sphnet_inh_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, """
            [SPELL 10]
            NAME=Harm
            FREEZE_TIME=20
            ON=@Effect
            SOMEKEY=1
            """);
        _files.Add(file);
        var r = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
        r.LoadResourceFile(file);
        new DefinitionLoader(r, spells).LoadAll();
        var def = spells.Get((SpellType)10)!;
        Assert.True(def.TryGetProperty("FREEZE_TIME", out string value));
        Assert.Equal("20", value);
        Assert.False(def.TryGetProperty("SOMEKEY", out _));     // trigger body, not the def
    }

    // ------------------------------------------------------------------ CATEGORY / TAGs

    [Fact]
    public void CategoryKeys_AreBaseStrings_AndDescriptionAtCopiesSubsection()
    {
        Load("""
            [ITEMDEF 0e75]
            CATEGORY=Containers
            SUBSECTION=Bags
            DESCRIPTION=@
            """);
        var def = DefinitionLoader.GetItemDef(0x0e75)!;
        Assert.Equal("Containers", def.BaseDefs.Get("CATEGORY"));
        Assert.Equal("Bags", def.BaseDefs.Get("DESCRIPTION"));
        Assert.Null(def.TagDefs.Get("CATEGORY"));
        Assert.Null(def.TagDefs.Get("DESCRIPTION"));
    }

    [Fact]
    public void DefinitionTags_AreReadThroughTheDefinition_NotCopied()
    {
        Load("""
            [ITEMDEF 0e75]
            DEFNAME=i_bag_tagged
            TAG.COLORMARK=7
            OVERRIDE.VALUE=99
            """);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var item = world.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, 0x0e75, fireCreate: false));

        Assert.False(item.Tags.Has("COLORMARK"));            // not stamped
        Assert.True(item.TryGetTag("COLORMARK", out string? mark));
        Assert.Equal(7, Num(mark));
        Assert.True(item.TryGetTag("OVERRIDE.VALUE", out string? ov));
        Assert.Equal("99", ov);
        Assert.True(item.TryGetProperty("TAG.COLORMARK", out string tagRead));
        Assert.Equal(7, Num(tagRead));

        item.SetTag("COLORMARK", "3");                       // the item's own wins
        Assert.True(item.TryGetTag("COLORMARK", out mark));
        Assert.Equal("3", mark);
        Assert.False(item.TryGetTag("SCRIPTDEF", out _));     // routing tags never fall back
    }

    // ------------------------------------------------------------------ save round trip

    [Fact]
    public void SourceXSave_LoadsTheSame_AndANewItemSavesWithoutTheDefinitionTags()
    {
        var r = Load("""
            [ITEMDEF 0e75]
            DEFNAME=i_bag_rt
            NAME=bag
            TYPE=t_container
            TAG.COLORMARK=7

            [ITEMDEF 07dc]
            DEFNAME=i_lantern_rt
            NAME=lit lantern
            TYPE=t_light_lit

            [ITEMDEF 07dd]
            DUPEITEM=07dc

            [ITEMDEF i_spear_rt]
            ID=i_spear_base_rt
            NAME=fine spear

            [ITEMDEF 0f62]
            DEFNAME=i_spear_base_rt
            TYPE=t_weapon_fence
            DAM=2,36
            """);
        string saveDir = Path.Combine(Path.GetTempPath(), $"sphnet_inhsave_{Guid.NewGuid():N}");
        Directory.CreateDirectory(saveDir);
        _files.Add(saveDir);
        // A classic Source-X world file: a bag that carries its definition's tag
        // stamped by an older engine, a lantern stub and a named spear.
        File.WriteAllText(Path.Combine(saveDir, "sphereworld.scp"), """
            [SPHERE]
            VERSION=1.0.0
            [WORLDITEM i_bag_rt]
            SERIAL=040000001
            P=10,10,0,0
            TAG.COLORMARK=7
            TAG.OWNMARK=1

            [WORLDITEM 07dd]
            SERIAL=040000002
            P=11,10,0,0

            [WORLDITEM i_spear_rt]
            SERIAL=040000003
            P=12,10,0,0

            [EOF]
            """);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var loader = new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }));
        loader.ResolveItemDef = defname =>
        {
            var rid = r.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };
        loader.ResolveItemDefFullIndex = defname =>
        {
            var rid = r.ResolveDefName(defname);
            return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
        };
        loader.Load(world, saveDir);

        var bag = world.FindItem(new Serial(0x40000001))!;
        Assert.Equal(ItemType.Container, bag.ItemType);
        Assert.True(bag.Tags.Has("COLORMARK"));              // an old stamped tag still loads
        Assert.True(bag.Tags.Has("OWNMARK"));
        var lantern = world.FindItem(new Serial(0x40000002))!;
        Assert.Equal((ushort)0x07dd, lantern.BaseId);
        Assert.Equal(ItemType.LightLit, lantern.ItemType);   // the stub reads as its master
        Assert.True(lantern.TryGetProperty("NAME", out string lanternName));
        Assert.Equal("lit lantern", lanternName);
        var spear = world.FindItem(new Serial(0x40000003))!;
        Assert.Equal((ushort)0x0f62, spear.BaseId);
        Assert.Equal(ItemType.WeaponFence, spear.ItemType);

        // A new item made from the tagged definition saves without the definition's tag
        // and still reads it after loading again.
        var fresh = world.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(fresh, 0x0e75, fireCreate: false));
        world.PlaceItem(fresh, new Point3D(13, 10, 0, 0));
        string outDir = Path.Combine(Path.GetTempPath(), $"sphnet_inhsave2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        _files.Add(outDir);
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, outDir);
        string saved = string.Join("\n", Directory.EnumerateFiles(outDir, "*.scp").Select(File.ReadAllText));
        string freshRecord = saved[saved.IndexOf($"SERIAL=0{fresh.Uid.Value:X}", StringComparison.OrdinalIgnoreCase)..];
        int next = freshRecord.IndexOf("\n[", StringComparison.Ordinal);
        if (next > 0) freshRecord = freshRecord[..next];
        Assert.DoesNotContain("COLORMARK", freshRecord, StringComparison.OrdinalIgnoreCase);

        var world2 = new GameWorld(LoggerFactory.Create(_ => { }));
        world2.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world2;
        Item.ResolveWorld = () => world2;
        loader.Load(world2, outDir);
        var again = world2.FindItem(fresh.Uid)!;
        Assert.True(again.TryGetTag("COLORMARK", out string? mark));
        Assert.Equal(7, Num(mark));
        Assert.Equal(ItemType.LightLit, world2.FindItem(new Serial(0x40000002))!.ItemType);
        Assert.True(world2.FindItem(new Serial(0x40000001))!.Tags.Has("OWNMARK"));
    }

    // ------------------------------------------------------------------ REGIONTYPE pages

    [Fact]
    public void RegionTypeOnTwoTerrains_IsTwoBlocks()
    {
        var r = Load("""
            [REGIONTYPE r_dungeons t_rock]
            RESOURCES=100.0 mr_nothing
            ON=@RegPeriodic
            SRC.SOUND=021f

            [REGIONTYPE r_dungeons]
            """);
        var rid = r.ResolveDefName("r_dungeons");
        Assert.Equal(ResType.RegionType, rid.Type);
        Assert.Equal(0, rid.Page);                           // the name answers for the plain block
        var pages = DefinitionLoader.GetRegionTypeDefPages(rid.Index);
        var rock = Assert.Single(pages, p => string.Equals(p.ItemTypeFilter, "t_rock", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(rock.Resources);
        Assert.NotNull(r.GetResource(rock.Id));
        Assert.NotEqual(rid, rock.Id);
    }

    [Fact]
    public void AreaRainAndColdChance_ReachEverySectorTheAreaCovers()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        var region = new SphereNet.Game.World.Regions.Region { Name = "wet", MapIndex = 0 };
        region.AddRect(10, 10, 100, 20);
        SphereNet.Server.Program.ApplyAreaWeatherChance(world, region, 70, 150);
        var inside = world.GetSector(new Point3D(90, 15, 0, 0))!;
        Assert.Equal(70, inside.RainChance);
        Assert.Equal(100, inside.ColdChance);               // capped at 100
        var outside = world.GetSector(new Point3D(300, 300, 0, 0))!;
        Assert.NotEqual(70, outside.RainChance);
    }

    // ------------------------------------------------------------------ REGIONFLAGS / COMPONENT

    [Fact]
    public void MultiRegionFlags_AreAnExpression()
    {
        var r = Load("""
            [DEFNAME region_flags]
            region_flag_ship 040
            region_flag_nobuilding 080
            region_antimagic_teleport 010
            """, loadDefs: false);
        Assert.Equal(0xC0u, MultiRegistry.ParseFlagExpression("region_flag_nobuilding|region_flag_ship", r));
        Assert.Equal(0x90u, MultiRegistry.ParseFlagExpression("region_flag_nobuilding|region_antimagic_teleport", r));
        Assert.Equal(0x2080u, MultiRegistry.ParseFlagExpression("02080", r));
    }

    [Fact]
    public void ScriptOnlyMulti_IsBuiltFromItsComponents()
    {
        var r = Load("""
            [DEFNAME region_flags]
            region_flag_nobuilding 080

            [ITEMDEF 0423b]
            DEFNAME=i_statue_part_a
            TYPE=t_normal

            [ITEMDEF 0423c]
            DEFNAME=i_statue_part_b

            [MULTIDEF 023f]
            DEFNAME=m_statue
            NAME=statue
            TYPE=t_multi
            REGIONFLAGS=region_flag_nobuilding
            MULTIREGION=0,0,0,0
            COMPONENT=i_statue_part_a,-1,-1
            COMPONENT=0423c 0 -1 5
            """);
        var registry = new MultiRegistry();
        registry.MergeScriptMetadata(r);
        var def = registry.Get(0x023f)!;
        Assert.True(def.ScriptOnly);
        Assert.Equal(RegionFlag.NoBuild, def.RegionFlags & RegionFlag.NoBuild);
        Assert.Equal(2, def.ScriptComponents.Count);
        Assert.Equal((0x423c, (short)0, (short)-1, (short)5), def.ScriptComponents[1]);
        Assert.True(def.OccupiesOffset(-1, -1));

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var multi = world.CreateItem();
        world.PlaceItem(multi, new Point3D(50, 50, 0, 0));
        var made = MultiRegistry.MaterializeScriptComponents(world, def, multi, multi.Position);
        Assert.Equal(2, made.Count);
        Assert.Equal((ushort)0x423b, made[0].BaseId);
        Assert.Equal(new Point3D(49, 49, 0, 0), made[0].Position);
        Assert.Equal(new Point3D(50, 49, 5, 0), made[1].Position);
        Assert.All(made, i => Assert.Equal(multi.Uid, i.Link));
    }

    [Fact]
    public void ScriptComponent_OnAMulPlaceholderSpot_IsNotMadeTwice()
    {
        var r = Load("""
            [ITEMDEF 06a5]
            DEFNAME=i_door_wood_t
            TYPE=t_door

            [ITEMDEF 0bd2]
            DEFNAME=i_sign_t
            """);
        var def = new MultiDef { Id = 0x64 };
        def.Components.Add(new MultiComponent { TileId = 0x06a5, DeltaX = 0, DeltaY = 3, DeltaZ = 7, Visible = false });
        def.Components.Add(new MultiComponent { TileId = 0x0064, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        Assert.True(MultiRegistry.TryParseScriptComponent("i_door_wood_t,0,3,7", r, out var door));
        Assert.True(MultiRegistry.TryParseScriptComponent("i_sign_t,2,4,5", r, out var sign));
        def.ScriptComponents.Add(door);
        def.ScriptComponents.Add(sign);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var multi = world.CreateItem();
        world.PlaceItem(multi, new Point3D(50, 50, 0, 0));
        var made = MultiRegistry.MaterializeScriptComponents(world, def, multi, multi.Position);
        var only = Assert.Single(made);
        Assert.Equal((ushort)0x0bd2, only.BaseId);
    }

    // ------------------------------------------------------------------ NEWBIE / STARTS / MOONGATES

    [Fact]
    public void NewbieKit_IsFoundByTheSkillsKey()
    {
        var r = Load("""
            [SKILL 16]
            DEFNAME=Skill_EvalInt
            KEY=EvaluatingIntel

            [NEWBIE EVALUATINGINTEL]
            ITEM=i_spellbook

            [NEWBIE 21]
            ITEM=i_lockpick
            """);
        Assert.Equal("EVALUATINGINTEL", SphereNet.Game.Clients.GameClient.FindNewbieSkillSection(r, 16),
            StringComparer.OrdinalIgnoreCase);
        Assert.Null(SphereNet.Game.Clients.GameClient.FindNewbieSkillSection(r, 17));
    }

    [Fact]
    public void Starts_Version2_IsFourLinesAndEachSectionReplacesTheList()
    {
        var r = Load("""
            [STARTS 2]
            Yew
            The Empath Abbey
            633,858,0,0
            1075072
            Minoc
            The Barnacle
            2476,413,15,0
            1075073

            [STARTS 2]
            Britain
            Sweet Dreams Inn
            1496,1628,10,0
            1075074

            [MOONGATES]
            1336,1997,5,0=mg_britain
            1499,3771,5,0=mg_jhelom

            [MOONGATES]
            4467,1283,10,1=mg_moonglow
            """, loadDefs: false);
        var start = Assert.Single(r.Starts);
        Assert.Equal("Britain", start.Area);
        Assert.Equal("Sweet Dreams Inn", start.Name);
        Assert.Equal(new Point3D(1496, 1628, 10, 0), start.Point);
        Assert.Equal(1075074u, start.Cliloc);
        var gate = Assert.Single(r.Moongates);
        Assert.Equal(new Point3D(4467, 1283, 10, 1), gate.Point);
    }

    [Fact]
    public void Starts_Version1_ThreeLines_AndTheLegacySingleLineForm()
    {
        var r = Load("""
            [STARTS]
            Yew
            The Empath Abbey
            633,858,0,0
            Britain=1496,1628,10,0
            """, loadDefs: false);
        Assert.Equal(2, r.Starts.Count);
        Assert.Equal(("Yew", "The Empath Abbey"), (r.Starts[0].Area, r.Starts[0].Name));
        Assert.Equal(new Point3D(633, 858, 0, 0), r.Starts[0].Point);
        Assert.Equal(new Point3D(1496, 1628, 10, 0), r.Starts[1].Point);
    }

    [Fact]
    public void CharList_SendsThePacksStartLocations()
    {
        var cities = new List<(string, string, int, int, int, int, uint)>
        {
            ("Britain", "Sweet Dreams Inn", 1496, 1628, 10, 0, 1075074u),
        };
        var packet = new SphereNet.Network.Packets.Outgoing.PacketCharList(["a"], 1, true, 0, cities).Build();
        byte[] data = packet.Data.ToArray();
        // 0xA9, length(2), char count(1), one char slot (60), city count.
        Assert.Equal(1, data[3 + 1 + 60]);
        string area = System.Text.Encoding.ASCII.GetString(data, 3 + 1 + 60 + 2, 32).TrimEnd('\0');
        Assert.Equal("Britain", area);
    }
}
