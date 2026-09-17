using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// An ITEMDEF key the parser has no case for still reaches the engine.
///
/// The parser's default branch puts an unrecognised key in the definition's TagDefs
/// (ItemDef.cs:207) and the suit aggregations read def tags as well as instance tags
/// (CombatEngine.GetItemNumProperty), so the AOS families a pack writes on its gear -
/// the five resists, the five damage types, the three regens - arrive even though no
/// case label names them. That distinction matters: "the parser does not know this
/// key" is not the same as "the value is lost", and a coverage measurement that
/// conflates the two would send someone to implement a path that already works.
///
/// These tests pin the working half, so the day the fallback changes shape the
/// resists on every piece of scripted armour do not go quietly to zero.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemDefTagFallbackTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_idt_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private static (GameWorld World, Character Ch, Item Worn) Equip(string defBody)
    {
        string dir = Path.Combine(Path.GetTempPath(), "spn_idt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "d.scp");
        File.WriteAllText(file,
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_armour" + Nl +
            "TYPE=t_armor" + Nl +
            "LAYER=13" + Nl +
            defBody);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var worn = world.CreateItem();
        worn.BaseId = 0x13BB;
        ch.Equip(worn, Layer.Ring);

        try { Directory.Delete(dir, true); } catch (IOException) { }
        return (world, ch, worn);
    }

    /// <summary>A resist written on the DEFINITION reaches the wearer, through the
    /// def-tag fallback rather than through a parser case.</summary>
    [Theory]
    [InlineData("RESFIRE", DamageType.Fire)]
    [InlineData("RESCOLD", DamageType.Cold)]
    [InlineData("RESPOISON", DamageType.Poison)]
    [InlineData("RESENERGY", DamageType.Energy)]
    public void AResistOnTheDefinitionReachesTheWearer(string key, DamageType type)
    {
        var bare = Equip("");
        int before = CombatEngine.EffectiveResist(bare.Ch, type);

        var armed = Equip(key + "=7" + Nl);
        Assert.Equal(before + 7, CombatEngine.EffectiveResist(armed.Ch, type));
    }

    /// <summary>The value really travels as a def TAG - which is what makes the
    /// aggregation find it, and what a script reads back.</summary>
    [Fact]
    public void AnUnparsedKeyIsReadableAsADefinitionTag()
    {
        Equip("RESFIRE=7" + Nl + "SELFREPAIR=3" + Nl);
        var def = DefinitionLoader.GetItemDef(0x13BB);
        Assert.NotNull(def);
        Assert.Equal("7", def!.TagDefs.Get("RESFIRE"));
        // One with no engine consumer travels the same way: the pack gets back what
        // it wrote, and nothing else happens.
        Assert.Equal("3", def.TagDefs.Get("SELFREPAIR"));
    }
}
