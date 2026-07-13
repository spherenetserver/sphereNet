using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A SPELL's RUNE_ITEM/SCROLL_ITEM/EFFECT_ID that names a resource must resolve to
/// the ItemDef's display graphic, not (ushort)ResolveDefName(...).Index. Name-keyed
/// rune/scroll itemdefs ([ITEMDEF i_rune_xxx]) hash to a synthetic index above
/// 0xFFFF, so the raw cast gave every necro/AOS spell a corrupt graphic — including
/// the graphic of the new IT_SPELL memory item (AttachSpellMemory).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellRuneItemGraphicTests
{
    private readonly ITestOutputHelper _out;
    public SpellRuneItemGraphicTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void SpellRuneItem_NameKeyedItemDef_ResolvesRealGraphic_NotTruncatedHash()
    {
        const string scripts = @"C:\sphereNetServer\scripts";
        if (!Directory.Exists(scripts)) { _out.WriteLine("no scripts"); return; }

        var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (var f in ScriptResourceManifest.Resolve(scripts)) res.LoadResourceFile(f);
        var spells = new SpellRegistry();
        new DefinitionLoader(res, spells).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        // The necro rune item is name-keyed (synthetic index) — the failing case.
        var runeRid = res.ResolveDefName("i_rune_animate_dead");
        if (!runeRid.IsValid || runeRid.Type != ResType.ItemDef)
        {
            _out.WriteLine("pack has no i_rune_animate_dead — skipping");
            return;
        }
        Assert.True(runeRid.Index > 0xFFFF, "test relies on a synthetic-index rune itemdef");
        ushort truncated = (ushort)runeRid.Index; // the old buggy value

        // The real display graphic the rune item would render with.
        var probe = world.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(probe, runeRid.Index));
        ushort realGraphic = probe.BaseId;
        _out.WriteLine($"i_rune_animate_dead: index=0x{runeRid.Index:X} truncated=0x{truncated:X} realGraphic=0x{realGraphic:X}");
        Assert.True(realGraphic != 0 && realGraphic <= 0xFFFF);

        var spell = spells.Get(SpellType.AnimateDeadAOS);
        if (spell == null || spell.RuneItemId == 0)
        {
            _out.WriteLine("pack does not define AnimateDeadAOS RUNE_ITEM — skipping spell assertion");
            return;
        }
        _out.WriteLine($"AnimateDeadAOS RuneItemId=0x{spell.RuneItemId:X}");
        // The fix: the spell carries the real rune graphic, not the truncated hash.
        Assert.Equal(realGraphic, spell.RuneItemId);
        Assert.NotEqual(truncated, spell.RuneItemId);
    }
}
