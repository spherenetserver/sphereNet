using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Item / object keywords from the Source-X property tables that had no reader or
/// writer: IBC_ALTERITEM, the OC_RECIPE* base-def numbers, OC_PROPSAT / OC_PROPSCOUNT
/// over the object's base defs, OC_CLILOC / OC_CLILOCCOUNT over the built tooltip,
/// OC_TEXTF and OC_CANSEELOSFLAG.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemKeywordParityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_ikp_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    // ---------------------------------------------------------------- ALTERITEM

    [Fact]
    public void AlterItemIsADefinitionStringReadThroughTheItem()
    {
        var def = new ItemDef(ResourceId.Invalid) { DispIndex = 0x0EED };
        def.LoadFromKey("ALTERITEM", "\"i_gold_alt\"");
        DefinitionLoader.SetItemDef(0x0EED, def);
        var world = MakeWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;

        Assert.True(item.TryGetProperty("ALTERITEM", out string v));
        Assert.Equal("i_gold_alt", v);

        // An empty value removes it again; unset reads "" (GetDefStr).
        def.LoadFromKey("ALTERITEM", "");
        Assert.True(item.TryGetProperty("ALTERITEM", out string cleared));
        Assert.Equal("", cleared);
    }

    // ------------------------------------------------------------------ RECIPE*

    [Theory]
    [InlineData("RECIPEALCHEMY")]
    [InlineData("RECIPEBLACKSMITH")]
    [InlineData("RECIPEBOWCRAFT")]
    [InlineData("RECIPECARPENTRY")]
    [InlineData("RECIPECARTOGRAPHY")]
    [InlineData("RECIPECOOKING")]
    [InlineData("RECIPEGLASSBLOWING")]
    [InlineData("RECIPEINSCRIPTION")]
    [InlineData("RECIPEMASONRY")]
    [InlineData("RECIPETAILORING")]
    [InlineData("RECIPETINKERING")]
    public void RecipeKeysReadBackAsHexOnItemsAndChars(string key)
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        var ch = world.CreateCharacter();

        Assert.True(item.TryGetProperty(key, out string unset));
        Assert.Equal("00", unset);

        Assert.True(item.TrySetProperty(key, "10"));
        Assert.True(item.TryGetProperty(key, out string v));
        Assert.Equal("0A", v);

        Assert.True(ch.TrySetProperty(key.ToLowerInvariant(), "0ff"));
        Assert.True(ch.TryGetProperty(key, out string cv));
        Assert.Equal("0FF", cv);

        // Nothing leaks into the TAG map.
        Assert.False(item.TryGetTag(key, out _));
    }

    [Fact]
    public void WritingZeroRemovesARecipe()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        item.TrySetProperty("RECIPECOOKING", "5");
        item.TrySetProperty("RECIPECOOKING", "0");
        Assert.Empty(item.RecipeDefs);
        Assert.True(item.TryGetProperty("RECIPECOOKING", out string v));
        Assert.Equal("00", v);

        // A negative that fits 32 bits reads as its 32-bit pattern.
        item.TrySetProperty("RECIPECOOKING", "-1");
        Assert.True(item.TryGetProperty("RECIPECOOKING", out string neg));
        Assert.Equal("0FFFFFFFF", neg);
    }

    [Fact]
    public void DupeCarriesRecipes()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        item.TrySetProperty("RECIPEALCHEMY", "7");
        var copy = item.CreateDupe(world);
        Assert.Equal(7, copy.GetRecipeDef("RECIPEALCHEMY"));
    }

    [Fact]
    public void RecipesRoundTripThroughASaveAsBareKeys()
    {
        Directory.CreateDirectory(_dir);
        var lf = LoggerFactory.Create(_ => { });
        var src = MakeWorld();
        var ch = src.CreateCharacter();
        ch.BodyId = 0x190;
        src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        ch.TrySetProperty("RECIPETAILORING", "3");
        var item = src.CreateItem();
        item.BaseId = 0x0EED;
        src.PlaceItem(item, new Point3D(1001, 1000, 0, 0));
        item.TrySetProperty("RECIPEALCHEMY", "26");

        Assert.True(new WorldSaver(lf).Save(src, _dir));
        string text = string.Join("\n", Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".scp", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        Assert.Contains("RECIPEALCHEMY=01A", text);
        Assert.DoesNotContain("TAG.RECIPE", text);

        var dst = MakeWorld();
        new WorldLoader(lf).Load(dst, _dir);
        Assert.Equal(3, dst.FindChar(ch.Uid)!.GetRecipeDef("RECIPETAILORING"));
        Assert.Equal(26, dst.FindItem(item.Uid)!.GetRecipeDef("RECIPEALCHEMY"));
    }

    // ------------------------------------------------------- PROPSAT / PROPSCOUNT

    [Fact]
    public void PropsWalkTheItemsOwnBaseDefsInKeyOrder()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        Assert.True(item.TryGetProperty("PROPSCOUNT", out string none));
        Assert.Equal("0", none);

        item.TrySetProperty("RECIPEALCHEMY", "5");
        item.TrySetProperty("ONAME", "heirloom");
        item.TrySetProperty("DROPSOUND", "057");
        item.TrySetProperty("RARITY", "3");
        item.SetTag("NOT_A_DEF", "1"); // a plain TAG is not a base def

        Assert.True(item.TryGetProperty("PROPSCOUNT", out string count));
        Assert.Equal("4", count);
        Assert.True(item.TryGetProperty("PROPSAT.0", out string p0));
        Assert.Equal("DROPSOUND=057", p0);
        Assert.True(item.TryGetProperty("PROPSAT.1.KEY", out string k1));
        Assert.Equal("ONAME", k1);
        Assert.True(item.TryGetProperty("PROPSAT.2.VAL", out string v2));
        Assert.Equal("03", v2);
        Assert.True(item.TryGetProperty("PROPSAT.3", out string p3));
        Assert.Equal("RECIPEALCHEMY=05", p3);

        // Past the end, or an unknown sub-key, stays unresolved (return false).
        Assert.False(item.TryGetProperty("PROPSAT.4", out _));
        Assert.False(item.TryGetProperty("PROPSAT.0.BOGUS", out _));
    }

    [Fact]
    public void PropsOnACharacter()
    {
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        ch.TrySetProperty("ONAME", "Real Name");
        ch.TrySetProperty("RECIPETAILORING", "2");
        Assert.True(ch.TryGetProperty("PROPSCOUNT", out string count));
        Assert.Equal("2", count);
        Assert.True(ch.TryGetProperty("PROPSAT.1", out string p1));
        Assert.Equal("RECIPETAILORING=02", p1);
    }

    // ------------------------------------------------------ CLILOC / CLILOCCOUNT

    [Fact]
    public void ClilocReadsTheBuiltTooltip()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        Assert.True(item.TryGetProperty("CLILOCCOUNT", out string none));
        Assert.Equal("0", none);
        Assert.False(item.TryGetProperty("CLILOC.0", out _));

        item.TooltipCache = new TooltipCacheEntry(1, 1, 0,
            [(1050045u, "a\tb"), (1060658u, "Access\tOwner Only")]);

        Assert.True(item.TryGetProperty("CLILOCCOUNT", out string count));
        Assert.Equal("2", count);
        Assert.True(item.TryGetProperty("CLILOC.1", out string line));
        Assert.Equal("1060658=Access\tOwner Only", line);
        Assert.True(item.TryGetProperty("CLILOC.0.ID", out string id));
        Assert.Equal("1050045", id);
        Assert.True(item.TryGetProperty("CLILOC.0.VAL", out string val));
        Assert.Equal("a\tb", val);
        Assert.False(item.TryGetProperty("CLILOC.2", out _));
    }

    // -------------------------------------------------------------------- TEXTF

    [Fact]
    public void TextFFormatsItsStringArguments()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        Assert.True(item.TryGetProperty("TEXTF \"Hello %s, you have %s gold\",Bob,50", out string v));
        Assert.Equal("Hello Bob, you have 50 gold", v);

        Assert.True(item.TryGetProperty("TEXTF \"[%-4s][%3s][%.2s] 100%%\",ab,c,xyz", out string w));
        Assert.Equal("[ab  ][  c][xy] 100%", w);

        // Only the format and no argument: an error upstream, left unresolved.
        Assert.False(item.TryGetProperty("TEXTF \"no args\"", out _));

        var ch = world.CreateCharacter();
        Assert.True(ch.TryGetProperty("TEXTF \"%s\",x", out string cv));
        Assert.Equal("x", cv);
    }

    // ------------------------------------------------------------ CANSEELOSFLAG

    [Fact]
    public void CanSeeLosFlagTakesFlagsThenAPointOrUid()
    {
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        ch.BodyId = 0x190;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        var near = world.CreateItem();
        near.BaseId = 0x0EED;
        world.PlaceItem(near, new Point3D(1003, 1000, 0, 0));

        Assert.True(ch.TryGetProperty("CANSEELOSFLAG 0, 1002,1000,0", out string pt));
        Assert.Equal("1", pt);
        Assert.True(ch.TryGetProperty($"CANSEELOSFLAG 0 0{near.Uid.Value:X}", out string uid));
        Assert.Equal("1", uid);

        // Past the viewer's sight range, or on another map, is not in sight.
        Assert.True(ch.TryGetProperty("CANSEELOSFLAG 0,1100,1000,0", out string far));
        Assert.Equal("0", far);
        Assert.True(ch.TryGetProperty("CANSEELOSFLAG 0,1002,1000,0,1", out string otherMap));
        Assert.Equal("0", otherMap);

        // Not swallowed by the CANSEELOS / CANSEE prefix reads.
        Assert.True(ch.TryGetProperty("CANSEELOSFLAG", out string bare));
        Assert.Equal("0", bare);
    }

    // ------------------------------------------------------ script end-to-end

    [Fact]
    public void ScriptsWriteAndReadTheKeysInCreate()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "k.scp");
        File.WriteAllLines(file,
        [
            "[ITEMDEF 013bb]", "DEFNAME=i_probe_recipe", "ALTERITEM=i_probe_alt",
            "ON=@Create",
            "RECIPEINSCRIPTION=12",
            "TAG.ALT=<ALTERITEM>",
            "TAG.REC=<RECIPEINSCRIPTION>",
            "TAG.PC=<PROPSCOUNT>",
            "TAG.TF=<TEXTF \"made by %s\",Smith>",
        ]);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.CreateTriggerHook = it => dispatcher.FireItemTrigger(it, ItemTrigger.Create,
            new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });

        var item = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(item, 0x13BB);

        Assert.Equal(12, item.GetRecipeDef("RECIPEINSCRIPTION"));
        Assert.True(item.TryGetTag("ALT", out string? alt));
        Assert.Equal("i_probe_alt", alt);
        Assert.True(item.TryGetTag("REC", out string? rec));
        Assert.Equal("0C", rec);
        Assert.True(item.TryGetTag("PC", out string? pc));
        Assert.Equal("1", pc);
        Assert.True(item.TryGetTag("TF", out string? tf));
        Assert.Equal("made by Smith", tf);
    }
}
