using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.World;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class DefinitionAndSpellRegressionTests
{
    private static ResourceHolder LoadScript(string contents)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_regression_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);

        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void DefinitionLoader_LoadsRegionDefinitionsAndHeaderFilter()
    {
        var resources = LoadScript("""
            [REGIONRESOURCE r_ore_iron]
            DEFNAME=r_ore_iron
            AMOUNT=3,5
            REAP=0x19B9
            REAPAMOUNT=1,2
            SKILL=1.0,100.0

            [REGIONTYPE r_default_rock t_rock]
            DEFNAME=r_default_rock
            RESOURCES=50.0 r_ore_iron
            """);

        var registry = new SpellRegistry();
        new DefinitionLoader(resources, registry).LoadAll();

        var regionTypeId = resources.ResolveDefName("r_default_rock");
        var regionResourceId = resources.ResolveDefName("r_ore_iron");
        var regionType = DefinitionLoader.GetRegionTypeDef(regionTypeId.Index);
        var regionResource = DefinitionLoader.GetRegionResourceDef(regionResourceId.Index);

        Assert.NotNull(regionType);
        Assert.Equal("t_rock", regionType.ItemTypeFilter);
        Assert.Single(regionType.Resources);
        Assert.NotNull(regionResource);
        Assert.Equal(3, regionResource.AmountMin);
        Assert.Equal(5, regionResource.AmountMax);
    }

    [Fact]
    public void DefinitionLoader_LoadAll_RebuildsSpellRegistry()
    {
        var registry = new SpellRegistry();
        var withSpell = LoadScript("""
            [SPELL 20]
            NAME=Teleport
            """);
        new DefinitionLoader(withSpell, registry).LoadAll();
        Assert.Equal(1, registry.Count);

        var withoutSpell = LoadScript("""
            [ITEMDEF 0f6c]
            NAME=Moongate
            """);
        new DefinitionLoader(withoutSpell, registry).LoadAll();

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void CharDefHelper_ResolvesNumericCharDefHeaderAsBody()
    {
        var resources = LoadScript("""
            [CHARDEF 00dc]
            DEFNAME=c_snake
            NAME=snake
            """);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        int idx = CharDefHelper.ResolveDefIndex("c_snake", resources);
        Assert.Equal(0x00DC, idx);
        Assert.Equal(0x00DC, CharDefHelper.ResolveBodyId(idx, resources));

        var ch = new SphereNet.Game.Objects.Characters.Character { IsPlayer = true };
        Assert.True(CharDefHelper.TryApplyDefName(ch, "c_snake", resources, refresh: false));
        Assert.Equal(0x00DC, ch.BodyId);
    }

    [Fact]
    public void CharDefHelper_DoesNotResolveStringHashCharDefAsBody()
    {
        var resources = LoadScript("""
            [CHARDEF c_man_gm]
            DEFNAME=c_man_gm
            NAME=gm
            """);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        int idx = CharDefHelper.ResolveDefIndex("c_man_gm", resources);
        Assert.NotEqual(0, idx);
        Assert.Equal(0, CharDefHelper.ResolveBodyId(idx, resources));
    }

    [Fact]
    public void DefinitionLoader_PreservesRunesWithOrWithoutLeadingDot()
    {
        var resources = LoadScript("""
            [SPELL 5]
            NAME=Magic Arrow
            RUNES=In Por Ylem

            [SPELL 4]
            NAME=Heal
            RUNES=.In Mani
            """);

        var registry = new SpellRegistry();
        new DefinitionLoader(resources, registry).LoadAll();

        Assert.Equal("In Por Ylem", registry.Get(SpellType.MagicArrow)?.Runes);
        Assert.Equal("In Mani", registry.Get(SpellType.Heal)?.Runes);
    }

    [Fact]
    public void CharDef_NpcSpell_ResolvesNamesEnumsAndSpellDefnames()
    {
        var resources = LoadScript("""
            [SPELL 5]
            DEFNAME=s_magic_arrow
            NAME=Magic Arrow

            [CHARDEF c_test_caster]
            ID=0x0190
            NPCSPELL=s_magic_arrow
            NPCSPELL=spell_fireball
            NPCSPELL=Lightning
            """);

        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var charDef = DefinitionLoader.GetCharDef(resources.ResolveDefName("c_test_caster").Index);
        Assert.NotNull(charDef);
        Assert.Contains((int)SpellType.MagicArrow, charDef!.NpcSpells);
        Assert.Contains((int)SpellType.Fireball, charDef.NpcSpells);
        Assert.Contains((int)SpellType.Lightning, charDef.NpcSpells);
    }

    [Fact]
    public void DefinitionLoader_ParsesTemplateSellBuyDiceAmounts()
    {
        var resources = LoadScript("""
            [ITEMDEF i_reagent]
            ID=0f7a
            NAME=Reagent

            [TEMPLATE VENDOR_S_MAGE]
            SELL=i_reagent,{3 7}
            BUY=i_reagent,5
            """);

        var registry = new SpellRegistry();
        new DefinitionLoader(resources, registry).LoadAll();

        var template = DefinitionLoader.GetTemplateDef("VENDOR_S_MAGE");
        Assert.NotNull(template);
        Assert.Equal(2, template!.ItemEntries.Count);
        Assert.All(template.ItemEntries, entry => Assert.Equal("i_reagent", entry.DefName));
        Assert.Contains(template.ItemEntries, entry => entry.Amount == 7);
        Assert.Contains(template.ItemEntries, entry => entry.Amount == 5);
    }

    [Fact]
    public void DefinitionLoader_ParsesSpellFlagsFromDefnamesAndBareHex()
    {
        var resources = LoadScript("""
            [DEFNAME spell_flags]
            spellflag_targ_char=00000002
            spellflag_harm=0x00000020

            [SPELL 5]
            NAME=Magic Arrow
            FLAGS=spellflag_targ_char|spellflag_harm|00000004
            """);

        var registry = new SpellRegistry();
        new DefinitionLoader(resources, registry).LoadAll();

        var spell = registry.Get(SpellType.MagicArrow);
        Assert.NotNull(spell);
        Assert.True(spell!.IsFlag((SpellFlag)0x00000002));
        Assert.True(spell.IsFlag((SpellFlag)0x00000020));
        Assert.True(spell.IsFlag((SpellFlag)0x00000004));
    }

    [Fact]
    public void ScriptCorpusSmoke_LoadsAllFixtureScriptsAndReportsMatrix()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string fixtureDir = Path.Combine(root, "tests", "fixtures", "scripts");
        if (!Directory.Exists(fixtureDir))
            return; // fixture scripts not deployed — skip silently
        var files = Directory.GetFiles(fixtureDir, "*.scp", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(files);

        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        var sectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            using var scriptFile = new ScriptFile();
            Assert.True(scriptFile.Open(path), $"Could not open {path}");
            var sections = scriptFile.ReadAllSections();
            Assert.NotEmpty(sections);
            foreach (var section in sections)
            {
                sectionCounts.TryGetValue(section.Name, out int count);
                sectionCounts[section.Name] = count + 1;
            }

            int loaded = resources.LoadResourceFile(path);
            Assert.True(loaded > 0, $"{Path.GetFileName(path)} did not load any resources");
        }

        var loadedResources = resources.GetAllResources().ToArray();
        var matrix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["sections"] = sectionCounts.Count,
            ["resources"] = loadedResources.Length,
            ["events"] = loadedResources.Count(r => r.Id.Type == ResType.Events),
            ["typedefs"] = loadedResources.Count(r => r.Id.Type == ResType.TypeDef),
            ["functions"] = loadedResources.Count(r => r.Id.Type == ResType.Function),
        };

        Assert.Contains("EVENTS", sectionCounts.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TYPEDEF", sectionCounts.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("FUNCTION", sectionCounts.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.All(matrix, entry => Assert.True(entry.Value > 0, $"Script parity matrix category '{entry.Key}' is empty"));
    }

    [Fact]
    public void SpellEngine_Recall_UsesRuneItemTarget()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Recall,
            Flags = SpellFlag.TargObj,
            ManaCost = 0,
            CastTimeBase = 1
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var rune = world.CreateItem();
        rune.SetRuneMark(new Point3D(200, 210, 5, 0));
        world.PlaceItem(rune, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.Recall, rune.Uid, rune.Position) > 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(200, caster.X);
        Assert.Equal(210, caster.Y);
        Assert.Equal(5, caster.Z);
    }

    [Fact]
    public void SpellEngine_CastStart_EmitsPowerWords()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.MagicArrow,
            Flags = SpellFlag.TargChar,
            ManaCost = 0,
            CastTimeBase = 1
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        string? words = null;
        var engine = new SpellEngine(world, registry);
        engine.OnSpellWords = (_, text) => words = text;

        Assert.True(engine.CastStart(caster, SpellType.MagicArrow, target.Uid, target.Position) > 0);
        Assert.Equal("In Por Ylem", words);
    }

    [Fact]
    public void SpellEngine_CastDone_ClearsStateWhenManaWasSpentBeforeCompletion()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.MagicArrow,
            Flags = SpellFlag.TargChar,
            ManaCost = 10,
            CastTimeBase = 1
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 10;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.MagicArrow, target.Uid, target.Position) > 0);
        caster.Mana = 0;

        Assert.False(engine.CastDone(caster));
        Assert.False(caster.IsCasting);
    }

    [Fact]
    public void ResourceHolder_LoadsStartsGoldAndMoongates()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string scriptPath = Path.Combine(dir, "world.scp");
        File.WriteAllText(scriptPath, """
[STARTS]
Britain=1495,1629,10,0

[STARTSGOLD]
gold=500

[MOONGATES]
Moonglow=4467,1283,5,0
""");

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(scriptPath);

        Assert.Single(resources.Starts);
        Assert.Equal(new Point3D(1495, 1629, 10, 0), resources.Starts[0].Point);
        Assert.Single(resources.StartGold);
        Assert.Equal(500, resources.StartGold[0].Amount);
        Assert.Single(resources.Moongates);
        Assert.Equal("Moonglow", resources.Moongates[0].Name);
    }

    [Fact]
    public void ResourceHolder_RetainsMultiDefKeysForServResolver()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string scriptPath = Path.Combine(dir, "multi.scp");
        File.WriteAllText(scriptPath, """
[MULTIDEF 04000]
DEFNAME=m_test_house
TYPE=t_multi_custom
COMPONENT=01,0,0,0
COMPONENT=02,1,0,0
""");

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(scriptPath);

        var rid = resources.ResolveDefName("m_test_house");
        Assert.True(rid.IsValid);
        Assert.Equal(0x4000, rid.Index);
        var link = resources.GetResource(rid);
        Assert.NotNull(link);
        Assert.NotNull(link!.StoredKeys);
        Assert.Equal(2, link.StoredKeys!.Count(k => k.Key.Equals("COMPONENT", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(link.StoredKeys!, k => k.Key.Equals("TYPE", StringComparison.OrdinalIgnoreCase) && k.Arg == "t_multi_custom");
    }
}
