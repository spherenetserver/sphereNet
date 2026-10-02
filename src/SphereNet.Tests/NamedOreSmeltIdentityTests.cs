using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Skills;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Smelting a coloured ore made the IRON ingot (Sphere 56T custom-version
/// compatibility; the same table shape is Source-X's own ore table).
///
/// A pack writes every coloured ore and ingot as a NAMED definition borrowing one
/// shared art:
///
///     [ITEMDEF i_ore_bronze]   ID=i_ore_iron  TDATA1=i_ingot_bronze
///     [ITEMDEF i_ingot_bronze] ID=01bf2
///
/// and smelts with a script that does <c>serv.newitem &lt;tdata1&gt;</c>. Upstream
/// keeps TDATA1 as the resource it names (CItemBase IBC_TDATA1 GetArgDWVal of the
/// defname = its resource uid), so &lt;tdata1&gt; answers the bronze ingot
/// DEFINITION. Resolving the name down to the ingot's GRAPHIC (0x1BF2) handed the
/// script a number every ingot shares, and 0x1BF2 is a DUPEITEM of the iron ingot.
///
/// The sections below are the pack's own, trimmed of display-only keys; the t_ore
/// typedef block is verbatim.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NamedOreSmeltIdentityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_oresmelt_" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _fResources = typeof(SphereNet.Server.Program)
        .GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly FieldInfo _fWorld = typeof(SphereNet.Server.Program)
        .GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object? _oldResources, _oldWorld;

    public NamedOreSmeltIdentityTests(ITestOutputHelper output)
    {
        _out = output;
        _oldResources = _fResources.GetValue(null);
        _oldWorld = _fWorld.GetValue(null);
    }

    public void Dispose()
    {
        _fResources.SetValue(null, _oldResources);
        _fWorld.SetValue(null, _oldWorld);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly string[] PackSections =
    {
        "[DEFNAME ore_smelt_test]",
        "layer_pack\t\t21",
        "normal_skill_gain\t\t6",
        "normal_skill_gain_ph\t\t12",
        "color_o_iron          0",
        "color_o_bronze     06d6",
        "",
        "[FUNCTION f_skill_gain]",
        "return 0",
        "[FUNCTION f_ore_fx]",
        "return 0",
        "[FUNCTION sys_red]",
        "sysmessage <args>",
        "",
        "[ITEMDEF 0e75]",
        "DEFNAME=i_backpack",
        "TYPE=t_container",
        "",
        "[ITEMDEF 0fb1]",
        "DEFNAME=i_forge_test",
        "TYPE=t_forge",
        "",
        "[ITEMDEF 019b7]",
        "DEFNAME=i_ore_iron",
        "NAME=Iron Ore",
        "TYPE=t_ore",
        "SKILLMAKE=mining 30.0",
        "TDATA1=i_ingot_iron",
        "WEIGHT=1.0",
        "VALUE=10",
        "DUPELIST=019b8,019b9,019ba",
        "tag.achi 2500,25000,500000",
        "tag.sub_cat Mining",
        "tag.act_point 1",
        "",
        "[ITEMDEF 019b9]",
        "DUPEITEM=019B7",
        "",
        "[ITEMDEF i_ore_bronze]",
        "ID=i_ore_iron",
        "NAME=Bronze Ore",
        "SKILLMAKE=mining 50.0",
        "TDATA1=i_ingot_bronze",
        "VALUE=60",
        "WEIGHT=1.0",
        "tag.achi 1250,12500,250000",
        "tag.color color_o_bronze",
        "tag.sub_cat Mining",
        "tag.act_point 2",
        "",
        "ON=@Create",
        "COLOR=color_o_bronze",
        "",
        "[ITEMDEF 01bef]",
        "DEFNAME=i_ingot_iron",
        "name Iron Ingot",
        "TYPE=t_ingot",
        "RESOURCES=i_ore_iron",
        "SKILLMAKE=20.0 mining",
        "TDATA1=20.0",
        "TDATA2=50.0",
        "WEIGHT=0.5",
        "DUPELIST=01bf0,01bf1,01bf2,01bf3,01bf4",
        "value 15",
        "",
        "ON=@Create",
        "DISPID=01bf2",
        "",
        "[ITEMDEF 01bf2]",
        "//Iron ingot facing SW",
        "DUPEITEM=01bef",
        "",
        "[ITEMDEF i_ingot_bronze]",
        "NAME=Bronze Ingot",
        "ID=01bf2",
        "RESOURCES=i_ore_bronze",
        "SKILLMAKE=mining 80.0",
        "TDATA1=70.0",
        "TDATA2=100.0",
        "WEIGHT=0.5",
        "VALUE=60",
        "tag.color color_o_bronze",
        "",
        "ON=@Create",
        "COLOR=color_o_bronze",
        "",
        "[REGIONRESOURCE mr_bronze]",
        "AMOUNT=6,9",
        "REAP=i_ore_bronze",
        "REAPAMOUNT=4,5",
        "SKILL=0.0,60.0",
        "REGEN=10*60",
        "",
        "[REGIONTYPE r_ore_smelt_test t_rock]",
        "RESOURCES=100.0 mr_bronze",
        "",
        "[typedef t_ore]",
        "ON=@dclick",
        "ref1=<uid>",
        "if !(<topobj.uid> == <src.uid>)",
        "\tsrc.sys_red Sadece cantanizdaki Ore lari eritebilirsiniz",
        "\treturn 1",
        "elseif !(<SRC.ISNEARTYPE t_forge 2>)",
        "\tsrc.sys_red Etrafinizda Forge bulunmuyor",
        "\treturn 1",
        "elseif <src.isevent.e_etkinlik>",
        "\tlocal.amount <muldiv <r80,90>,<amount>,100>",
        "\tlocal.point <eval <serv.itemdef.<baseid>.tag.act_point>*<dlocal.amount>>",
        "\tref1.remove",
        "\tsrc.f_ore_fx",
        "\tsrc.sys_green <dlocal.amount> <name> eriterek <dlocal.point> puan kazandiniz",
        "\tsrc.tag0.madenci_point += <local.point>",
        "else",
        "\tif <amount> < 10",
        "\t\tif (rand(6) == 1)",
        "\t\t\tlocal.amount <eval <amount>-1>",
        "\t\t\tref1.remove",
        "\t\t\tif <local.amount> > 0",
        "\t\t\t\tserv.newitem <tdata1>,<amount>",
        "\t\t\t\tsrc.sysmessage You put the <new.name> in your pack",
        "\t\t\t\tnew.cont <src.findlayer(layer_pack).uid>",
        "\t\t\telse",
        "\t\t\t\tsrc.sys_red Madeni eritmeye calisirken yaktiniz",
        "\t\t\tendif",
        "\t\telse",
        "\t\t\tref1.remove",
        "\t\t\tserv.newitem <tdata1>,<amount>",
        "\t\t\tsrc.emote smelt <ref1.name>",
        "\t\t\tsrc.sysmessage You put the <new.name> in your pack",
        "\t\t\tnew.cont <src.findlayer(layer_pack).uid>",
        "\t\tendif",
        "\telse",
        "\t\tlocal.skilldiff <eval <eval <eval 1000-<src.mining>>/10>+100>",
        "\t\tlocal.amount <muldiv <r90,95>,<amount>,<dlocal.skilldiff>>",
        "\t\tref1.remove",
        "\t\tif <local.amount> > 0",
        "\t\t\tserv.newitem <tdata1>,<local.amount>",
        "\t\t\tsrc.emote smelt <ref1.name>",
        "\t\t\tsrc.sysmessage You put the <new.name> in your pack",
        "\t\t\tnew.cont <src.findlayer(layer_pack).uid>",
        "\t\tendif",
        "\tendif",
        "\tsrc.f_skill_gain 45,<qval (<serv.var0.power_hour> == 1)? <ddef.normal_skill_gain_ph>:<ddef.normal_skill_gain>>",
        "\tsrc.f_ore_fx",
        "endif",
        "return 1",
        "",
    };

    private sealed record Rig(GameWorld World, ResourceHolder Resources, TriggerDispatcher Dispatcher,
        Character Smith, Item Pack);

    private Rig Setup()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "ore.scp");
        File.WriteAllLines(file, PackSections);

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = TestHarness.CreateWorld();
        var md = new SphereNet.MapData.MapDataManager("");
        md.AddSyntheticMap(0, 6144, 4096);
        foreach (ushort g in new ushort[] { 0x19B7, 0x19B8, 0x19B9, 0x19BA, 0x1BEF, 0x1BF2 })
            md.SetSyntheticItemTile(g, new SphereNet.MapData.Tiles.ItemTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.Generic, Weight = 1 });
        world.MapData = md;
        // The definitions' own @Create runs, as in production (COLOR=color_o_bronze).
        Item.CreateTriggerHook = it => dispatcher.FireItemTrigger(it, ItemTrigger.Create,
            new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });
        TestHarness.AttachLoadedRegionTypes(world);
        _fResources.SetValue(null, resources);
        _fWorld.SetValue(null, world);
        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        interpreter.ServerPropertyResolver = s => (string?)resolve.Invoke(null, [s]);
        interpreter.ResolveObjectRef = (obj, head) =>
            head.Equals("NEW", StringComparison.OrdinalIgnoreCase)
                ? world.FindObject(world.LastNewObject)
                : obj is ObjBase o ? o.ResolveScriptRefHead(head) : null;

        var smith = world.CreateCharacter();
        smith.IsPlayer = true;
        smith.SetSkill(SkillType.Mining, (ushort)1000);
        world.PlaceCharacter(smith, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        smith.Equip(pack, Layer.Pack);

        var forge = world.CreateItem();
        forge.BaseId = 0x0FB1;
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        return new Rig(world, resources, dispatcher, smith, pack);
    }

    private static int DefIndex(ResourceHolder resources, string defname)
    {
        var rid = resources.ResolveDefName(defname);
        Assert.True(rid.IsValid && rid.Type == ResType.ItemDef, $"{defname} did not load");
        return rid.Index;
    }

    private static Item NewItem(Rig rig, string defname, ushort amount)
    {
        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        resolve.Invoke(null, ["_NEWITEM=" + defname + "," + amount]);
        var item = rig.World.FindItem(rig.World.LastNewItem);
        Assert.NotNull(item);
        return item!;
    }

    private static string Read(Item item, string key)
    {
        Assert.True(item.TryGetProperty(key, out string value), $"{key} not readable");
        return value;
    }

    /// <summary>Double-click the ore through the pack's own t_ore typedef and return
    /// what landed in the pack.</summary>
    private List<Item> Smelt(Rig rig, Item ore)
    {
        var before = rig.Pack.Contents.ToHashSet();
        rig.Dispatcher.FireItemTrigger(ore, ItemTrigger.DClick, new SphereNet.Game.Scripting.TriggerArgs { CharSrc = rig.Smith });
        var made = rig.Pack.Contents.Where(i => !before.Contains(i) && !i.IsDeleted).ToList();
        foreach (var it in made)
            _out.WriteLine($"made '{it.GetName()}' x{it.Amount} baseid={Read(it, "BASEID")} graphic={it.BaseId:X4}");
        return made;
    }

    private void AssertBronzeIngots(Rig rig, List<Item> made)
    {
        var ingot = Assert.Single(made);
        Assert.Equal("i_ingot_bronze", Read(ingot, "BASEID"));
        Assert.Equal(DefIndex(rig.Resources, "i_ingot_bronze"), ItemDefHelper.ResolveInstanceDefIndex(ingot));
        Assert.Equal("Bronze Ingot", ingot.Name);
        Assert.Equal((ushort)0x1BF2, ingot.BaseId);   // it still draws as the shared ingot art
    }

    [Fact]
    public void ScriptCreatedBronzeOre_ReadsItsOwnTData1AndBaseId()
    {
        var rig = Setup();
        var ore = NewItem(rig, "i_ore_bronze", 20);

        Assert.Equal("i_ore_bronze", Read(ore, "BASEID"));
        Assert.Equal((ushort)0x19B7, ore.BaseId);
        Assert.Equal(ItemType.Ore, ore.ItemType);
        // <tdata1> must name the bronze ingot definition, not the art it shares with iron.
        string tdata1 = Read(ore, "TDATA1");
        _out.WriteLine($"<tdata1> = {tdata1}");
        var made = NewItem(rig, tdata1, 1);
        Assert.Equal("i_ingot_bronze", Read(made, "BASEID"));

        // Iron is unchanged: its TDATA1 still makes the iron ingot.
        var iron = NewItem(rig, "i_ore_iron", 5);
        var ironIngot = NewItem(rig, Read(iron, "TDATA1"), 1);
        Assert.Equal("i_ingot_iron", Read(ironIngot, "BASEID"));
    }

    [Fact]
    public void ScriptCreatedBronzeOre_SmeltsIntoBronzeIngots()
    {
        var rig = Setup();
        var ore = NewItem(rig, "i_ore_bronze", 20);
        rig.Pack.AddItem(ore);

        AssertBronzeIngots(rig, Smelt(rig, ore));
    }

    [Fact]
    public void MinedBronzeOre_SmeltsIntoBronzeIngots()
    {
        var rig = Setup();
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        var engine = new GatheringEngine(rig.World);
        Item? ore = null;
        for (int swing = 0; swing < 20 && ore == null; swing++)
        {
            var r = engine.TryGatherForSink(rig.Smith, SkillType.Mining, new Point3D(100, 100, 0, 0));
            if (r.Success && r.Item != null) ore = r.Item;
        }
        Assert.NotNull(ore);
        Assert.Equal("i_ore_bronze", Read(ore!, "BASEID"));
        ore!.Amount = 20;
        if (ore.ContainedIn != rig.Pack.Uid) rig.Pack.AddItem(ore);

        AssertBronzeIngots(rig, Smelt(rig, ore));
    }

    /// <summary>A classic save record: the definition only in the header, the pile
    /// graphic in DISPID, the colour its @Create gave it in COLOR.</summary>
    private Item LoadSavedOre(Rig rig, string header, uint serial, bool withDispId,
        params string[] extraLines)
    {
        string saveDir = Path.Combine(_dir, "save_" + serial.ToString("X"));
        Directory.CreateDirectory(saveDir);
        var lines = new List<string>
        {
            "[SPHERE]", "TITLE=Sphere World Script", "VERSION=0.56T-Release", "",
            $"[WORLDITEM {header}]", $"SERIAL=0{serial:x}", "COLOR=06d6",
        };
        if (withDispId) lines.Add("DISPID=019b9");
        lines.AddRange(extraLines);
        lines.AddRange(new[] { "AMOUNT=20", "P=105,96", "", "[EOF]" });
        File.WriteAllLines(Path.Combine(saveDir, "sphereworld.scp"), lines);

        LoadInto(rig, rig.World, saveDir);
        var ore = rig.World.FindItem(new Serial(serial));
        Assert.NotNull(ore);
        return ore!;
    }

    /// <summary>The loader wired the way the server wires it (Program.cs).</summary>
    private static void LoadInto(Rig rig, GameWorld world, string saveDir)
    {
        var resources = rig.Resources;
        var loader = new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }))
        {
            ResolveItemDef = defname =>
            {
                var rid = resources.ResolveDefName(defname);
                if (!rid.IsValid || rid.Type != ResType.ItemDef) return 0;
                return ItemDefHelper.CreateGraphic(DefinitionLoader.GetItemDef(rid.Index), rid.Index);
            },
            ResolveItemDefFullIndex = defname =>
            {
                var rid = resources.ResolveDefName(defname);
                return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
            },
        };
        loader.Load(world, saveDir);
    }

    /// <summary>The saver wired the way the server wires it (Program.cs).</summary>
    private static SphereNet.Persistence.Save.WorldSaver Saver(Rig rig)
    {
        var resources = rig.Resources;
        return new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { }))
        {
            Format = SphereNet.Core.Configuration.SaveFormat.Text,
            ShardCount = 0,
            BackupLevels = 0,
            ResolveItemDefName = baseId => DefinitionLoader.GetItemDef(baseId)?.DefName,
            ResolveItemOwnDefName = item =>
                DefinitionLoader.GetItemDef(ItemDefHelper.ResolveInstanceDefIndex(item, resources))?.DefName,
            ResolveHeaderBaseId = defname =>
            {
                var rid = resources.ResolveDefName(defname);
                if (!rid.IsValid || rid.Type != ResType.ItemDef) return 0;
                return ItemDefHelper.CreateGraphic(DefinitionLoader.GetItemDef(rid.Index), rid.Index);
            },
        };
    }

    /// <summary>The record a save wrote for one item, as text.</summary>
    private static string RecordOf(string saveDir, Serial uid)
    {
        string serialLine = $"SERIAL=0{uid.Value:X8}";
        foreach (string file in Directory.GetFiles(saveDir, "*.scp", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            int at = text.IndexOf(serialLine, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            int start = text.LastIndexOf('[', at);
            int end = text.IndexOf("\n[", at, StringComparison.Ordinal);
            return end < 0 ? text[start..] : text[start..end];
        }
        Assert.Fail($"no record for {uid.Value:X} in {saveDir}");
        return "";
    }

    [Fact]
    public void SaveRoundTrip_WritesTheOwnDefinitionAsTheHeader()
    {
        var rig = Setup();
        var ore = NewItem(rig, "i_ore_bronze", 20);
        rig.World.PlaceItem(ore, new Point3D(105, 96, 0, 0));
        var iron = NewItem(rig, "i_ore_iron", 7);
        rig.World.PlaceItem(iron, new Point3D(106, 96, 0, 0));
        Assert.True(ore.TryGetTag("SCRIPTDEF", out _));   // the in-memory routing tag

        string saveDir = Path.Combine(_dir, "roundtrip");
        Assert.True(Saver(rig).Save(rig.World, saveDir));

        // Written as upstream writes it: the header names the item's own definition,
        // and no engine routing tag is needed beside it.
        string record = RecordOf(saveDir, ore.Uid);
        _out.WriteLine(record);
        Assert.StartsWith("[WORLDITEM i_ore_bronze]", record);
        Assert.DoesNotContain("TAG.SCRIPTDEF", record, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TAG.ITEMDEF", record, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\nID=", record);   // the header's graphic is the item's
        Assert.StartsWith("[WORLDITEM i_ore_iron]", RecordOf(saveDir, iron.Uid));

        // ...and it reads back as bronze.
        var world2 = TestHarness.CreateWorld();
        world2.MapData = rig.World.MapData;
        LoadInto(rig, world2, saveDir);
        var back = world2.FindItem(ore.Uid);
        Assert.NotNull(back);
        Assert.Equal("i_ore_bronze", Read(back!, "BASEID"));
        Assert.Equal("i_ingot_bronze", Read(back!, "TDATA1"));
        Assert.Equal((ushort)0x19B7, back!.BaseId);
        Assert.Equal(ItemType.Ore, back.ItemType);
        Assert.Equal(20, back.Amount);
        var ironBack = world2.FindItem(iron.Uid);
        Assert.Equal("i_ore_iron", Read(ironBack!, "BASEID"));
    }

    [Fact]
    public void OlderSphereNetSave_GraphicHeaderWithScriptDefTag_StillLoadsAsBronze()
    {
        // What this engine wrote before: the header from the graphic's definition and
        // the real one only in TAG.SCRIPTDEF.
        var rig = Setup();
        int bronzeIndex = DefIndex(rig.Resources, "i_ore_bronze");
        var ore = LoadSavedOre(rig, "i_ore_iron", 0x4002090F, withDispId: false,
            $"TAG.SCRIPTDEF={bronzeIndex}");
        Assert.Equal("i_ore_bronze", Read(ore, "BASEID"));
        Assert.Equal("i_ingot_bronze", Read(ore, "TDATA1"));

        // The next save writes it the upstream way.
        string saveDir = Path.Combine(_dir, "resave");
        Assert.True(Saver(rig).Save(rig.World, saveDir));
        string record = RecordOf(saveDir, ore.Uid);
        Assert.StartsWith("[WORLDITEM i_ore_bronze]", record);
        Assert.DoesNotContain("TAG.SCRIPTDEF", record, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveLoadedBronzeOre_SmeltsIntoBronzeIngots()
    {
        var rig = Setup();
        var ore = LoadSavedOre(rig, "i_ore_bronze", 0x4002090C, withDispId: true);
        Assert.Equal("i_ore_bronze", Read(ore, "BASEID"));
        Assert.Equal(ItemType.Ore, ore.ItemType);
        Assert.Equal("i_ingot_bronze", Read(ore, "TDATA1"));
        rig.Pack.AddItem(ore);

        AssertBronzeIngots(rig, Smelt(rig, ore));
    }

    [Fact]
    public void SaveLoadedBronzeOre_ReadsItsOwnDefinitionsTags()
    {
        // The smelt block reads SERV.ITEMDEF.<BASEID>.TAG.ACT_POINT: bronze is worth
        // 2, the iron definition whose graphic it borrows 1.
        var rig = Setup();
        var ore = LoadSavedOre(rig, "i_ore_bronze", 0x4002090D, withDispId: true);
        Assert.True(ScriptNumber.TryParseToken(Read(ore, "TAG.ACT_POINT"), out long own) && own == 2);
        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? viaServ = (string?)resolve.Invoke(null, ["ITEMDEF." + Read(ore, "BASEID") + ".TAG.ACT_POINT"]);
        Assert.True(ScriptNumber.TryParseToken(viaServ ?? "", out long points) && points == 2,
            $"SERV.ITEMDEF.<BASEID>.TAG.ACT_POINT read '{viaServ}'");
    }

    [Fact]
    public void EngineSmeltPath_ResolvesTheBronzeIngot()
    {
        var rig = Setup();
        var ore = NewItem(rig, "i_ore_bronze", 20);
        var method = typeof(SphereNet.Game.Clients.ClientItemUseHandler)
            .GetMethod("ResolveSmeltIngotDefIndex", BindingFlags.Static | BindingFlags.NonPublic)!;
        int index = (int)method.Invoke(null, [ore])!;
        Assert.Equal(DefIndex(rig.Resources, "i_ingot_bronze"), index);

        var iron = NewItem(rig, "i_ore_iron", 5);
        Assert.Equal(0x1BEF, (int)method.Invoke(null, [iron])!);
    }

    [Fact]
    public void BronzeAndIronOreNeverStack()
    {
        var rig = Setup();
        var bronze = NewItem(rig, "i_ore_bronze", 3);
        var iron = NewItem(rig, "i_ore_iron", 3);
        Assert.False(bronze.CanStackWith(iron));
        Assert.False(iron.CanStackWith(bronze));

        // Same colour, same graphic, same TDATA: still two definitions (upstream
        // IsSameType compares GetID, the definition id, CItem.cpp:1296).
        iron.Hue = bronze.Hue;
        iron.TData1 = bronze.TData1;
        Assert.False(iron.CanStackWith(bronze));
        Assert.False(bronze.CanStackWith(iron));

        var bronze2 = NewItem(rig, "i_ore_bronze", 4);
        Assert.Equal((ushort)0x06D6, bronze2.Hue.Value);   // its own @Create coloured it
        Assert.True(bronze.CanStackWith(bronze2));
    }

    [Fact]
    public void SaveLoadedAndFreshBronzeOreStack_IronDoesNot()
    {
        var rig = Setup();
        // A classic record carries the pile graphic in DISPID; upstream IsSameType /
        // Stack never compare the display id (CItem.cpp:1289-1387), so it stacks.
        var loaded = LoadSavedOre(rig, "i_ore_bronze", 0x4002090E, withDispId: true);
        Assert.Equal((ushort)0x19B9, loaded.DispIdOverride);
        var fresh = NewItem(rig, "i_ore_bronze", 4);
        var iron = NewItem(rig, "i_ore_iron", 4);
        iron.Hue = fresh.Hue;

        Assert.True(fresh.CanStackWith(loaded));
        Assert.True(loaded.CanStackWith(fresh));
        Assert.False(iron.CanStackWith(loaded));
        Assert.False(loaded.CanStackWith(iron));
    }
}
