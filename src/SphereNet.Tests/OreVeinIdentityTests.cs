using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What comes out of a vein that is not iron.
///
/// Field report: mining only ever produced iron ore. The cause is in how a pack writes
/// its ore table. Iron is a numeric def - [ITEMDEF 019b7] - and every other colour is a
/// NAMED def that borrows iron's art and differs only in its definition:
///
///     [ITEMDEF i_ore_copper]
///     ID=i_ore_iron
///     NAME=Copper Ore
///     TDATA1=i_ingot_copper
///     ON=@Create
///        COLOR=color_o_copper
///
/// So all fifteen colours share one graphic. Upstream builds the reaped item from the
/// REAP RESOURCE ID - a definition - through CItem::CreateScript (CCharSkill.cpp:1050),
/// which runs that definition's own @Create (CItem.cpp:404/415). Resolving REAP down to
/// a graphic, as this did, threw the definition away and rebuilt the item from whatever
/// def owns the art: iron. Iron's name, iron's ingot, iron's @Create - from every vein.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class OreVeinIdentityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_ore_{Guid.NewGuid():N}.scp");

    public OreVeinIdentityTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private static readonly Point3D Tile = new(100, 100, 0, 0);

    private sealed record Rig(GameWorld World, GatheringEngine Engine, Character Miner);

    /// <summary>A region whose only vein is the named colour, so what comes out is not
    /// a matter of luck.</summary>
    private Rig Setup(string onlyResource)
    {
        var lf = LoggerFactory.Create(_ => { });
        File.WriteAllText(_defFile, $$"""
            [ITEMDEF 01bef]
            DEFNAME=i_ingot_iron

            [ITEMDEF 01bf0]
            DEFNAME=i_ingot_copper

            [ITEMDEF 01bf1]
            DEFNAME=i_ingot_shadow

            [ITEMDEF 019b7]
            DEFNAME=i_ore_iron
            NAME=Iron Ore
            TYPE=t_ore
            TDATA1=i_ingot_iron
            WEIGHT=2

            [ITEMDEF i_ore_copper]
            ID=i_ore_iron
            NAME=Copper Ore
            TDATA1=i_ingot_copper
            VALUE=5

            [ITEMDEF i_ore_shadow]
            ID=i_ore_iron
            NAME=Shadow Ore
            TDATA1=i_ingot_shadow

            [REGIONRESOURCE mr_iron]
            DEFNAME=mr_iron
            AMOUNT=9,30
            REAP=i_ore_iron
            REAPAMOUNT=1,3
            SKILL=0.0
            REGEN=60*60*10

            [REGIONRESOURCE mr_copper]
            DEFNAME=mr_copper
            AMOUNT=5,20
            REAP=i_ore_copper
            REAPAMOUNT=1,3
            SKILL=0.0
            REGEN=60*60*10

            [REGIONRESOURCE mr_shadow]
            DEFNAME=mr_shadow
            AMOUNT=5,16
            REAP=i_ore_shadow
            REAPAMOUNT=1,3
            SKILL=0.0
            REGEN=60*60*10

            [REGIONTYPE r_ore_test t_rock]
            DEFNAME=r_ore_test
            RESOURCES=100.0 {{onlyResource}}
            """);

        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        TestHarness.AttachLoadedRegionTypes(world);
        var miner = world.CreateCharacter();
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        return new Rig(world, new GatheringEngine(world), miner);
    }

    private static Item? Mine(Rig rig)
    {
        for (int swing = 0; swing < 20; swing++)
        {
            var r = rig.Engine.TryGatherForSink(rig.Miner, SkillType.Mining, Tile);
            if (r.Success && r.Item != null)
                return r.Item;
        }
        return null;
    }

    [Theory]
    [InlineData("mr_copper", "Copper Ore")]
    [InlineData("mr_shadow", "Shadow Ore")]
    public void AColouredVeinYieldsItsOwnOre(string vein, string expectedName)
    {
        var ore = Mine(Setup(vein));

        Assert.NotNull(ore);
        _out.WriteLine($"{vein} -> '{ore!.Name}' graphic {ore.BaseId:X4} " +
                       $"tdata1 {ore.TData1:X} itemdef tag " +
                       $"{(ore.TryGetTag("ITEMDEF", out string? t) ? t : "<none>")}");

        Assert.Equal(expectedName, ore.Name);
        // It still DRAWS as iron ore - that is what the pack asked for with ID=.
        Assert.Equal((ushort)0x19B7, ore.BaseId);
        // ...and it is not iron: the ingot it smelts into is its own.
        Assert.NotEqual(0u, ore.TData1);
        Assert.NotEqual(0x1BEFu, ore.TData1);   // not the iron ingot
    }

    [Fact]
    public void TheDefinitionTravelsWithTheItem()
    {
        // The routing tag is what makes @Create, the tooltip and a later reload read
        // the ore's OWN definition rather than the one that owns the graphic. Without
        // it the colour a pack sets in @Create never reaches the item.
        var ore = Mine(Setup("mr_copper"));

        Assert.NotNull(ore);
        // ApplyInstanceMetadata pins whichever of the two routing tags applies: a
        // named def with no numeric id of its own is reached through SCRIPTDEF.
        bool pinned = ore!.TryGetTag("ITEMDEF", out string? defName) ||
                      ore.TryGetTag("SCRIPTDEF", out defName);
        _out.WriteLine($"routing tag: {defName ?? "<none>"}");
        Assert.True(pinned, "nothing on the item points back at its definition");

        // And the pin resolves to the copper definition, not to iron's.
        int resolved = SphereNet.Game.Definitions.ItemDefHelper.ResolveInstanceDefIndex(ore);
        var def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(resolved);
        Assert.NotNull(def);
        Assert.Equal("Copper Ore", def!.Name);
    }

    [Fact]
    public void IronIsStillIron()
    {
        // The control: a numeric def has no indirection to get wrong, and must keep
        // behaving exactly as it did.
        var ore = Mine(Setup("mr_iron"));

        Assert.NotNull(ore);
        Assert.Equal("Iron Ore", ore!.Name);
        Assert.Equal((ushort)0x19B7, ore.BaseId);
        Assert.Equal(ItemType.Ore, ore.ItemType);
    }

    [Fact]
    public void ACopperVeinIsNotTheIronDefinition()
    {
        // The report in one assertion: this used to come back as iron ore, because the
        // item was rebuilt from the def that owns the GRAPHIC and copper borrows iron's.
        var ore = Mine(Setup("mr_copper"));

        Assert.NotNull(ore);
        var ironDef = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(0x19B7);
        Assert.NotNull(ironDef);
        _out.WriteLine($"mined '{ore!.Name}', the graphic's own def is '{ironDef!.Name}'");
        Assert.NotEqual(ironDef.Name, ore.Name);
        Assert.NotEqual(ironDef.TData1, ore.TData1);
    }
}
