using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXCastTimingWave224Tests
{
    [Fact]
    public void FasterCasting_AggregatesCharacterAndEquippedItems()
    {
        var world = TestHarness.CreateWorld();
        var caster = world.CreateCharacter();
        caster.TrySetProperty(SpellCastingProperties.FasterCasting, "2");
        var ring = world.CreateItem();
        ring.TrySetProperty(SpellCastingProperties.FasterCasting, "1");
        caster.Equip(ring, Layer.Ring);
        var def = new SpellDef { Id = SpellType.Heal, CastTimeBase = 20 };

        Assert.Equal(3, SpellEngine.GetCastingPropertyValue(
            caster, SpellCastingProperties.FasterCasting));
        Assert.Equal(14, SpellEngine.CalculateCastTimeTenths(caster, def, 0));
        Assert.True(caster.TryGetProperty(SpellCastingProperties.FasterCasting, out string aggregate));
        Assert.Equal("3", aggregate);
    }

    [Fact]
    public void FasterCasting_FloorsCastTimeAtOneTenth()
    {
        var world = TestHarness.CreateWorld();
        var caster = world.CreateCharacter();
        caster.TrySetProperty(SpellCastingProperties.FasterCasting, "100");
        var def = new SpellDef { Id = SpellType.Heal, CastTimeBase = 5 };

        Assert.Equal(1, SpellEngine.CalculateCastTimeTenths(caster, def, 0));
    }

    [Fact]
    public void CastingProperties_AreAcceptedByCharacterItemAndDefinitionSurfaces()
    {
        var world = TestHarness.CreateWorld();
        var caster = world.CreateCharacter();
        var item = world.CreateItem();
        Assert.True(caster.TrySetProperty(SpellCastingProperties.FasterCastRecovery, "4"));
        Assert.True(item.TrySetProperty(SpellCastingProperties.FasterCastRecovery, "3"));
        Assert.True(item.TryGetProperty(SpellCastingProperties.FasterCastRecovery, out string itemValue));
        Assert.Equal("3", itemValue);

        var charDef = new CharDef(ResourceId.Invalid);
        charDef.LoadFromKey(SpellCastingProperties.FasterCasting, "2");
        var itemDef = new ItemDef(ResourceId.Invalid);
        itemDef.LoadFromKey(SpellCastingProperties.FasterCastRecovery, "5");

        Assert.Equal("2", charDef.TagDefs.Get(SpellCastingProperties.FasterCasting));
        Assert.Equal("5", itemDef.TagDefs.Get(SpellCastingProperties.FasterCastRecovery));
    }

    [Fact]
    public void CompletedCast_DoesNotCreateSyntheticPostCastCooldown()
    {
        var world = TestHarness.CreateWorld();
        var spells = new SpellRegistry();
        var def = new SpellDef
        {
            Id = SpellType.Heal,
            Flags = SpellFlag.Good | SpellFlag.Heal,
            CastTimeBase = 10,
        };
        def.SkillReq[SkillType.Magery] = 0;
        spells.Register(def);
        var engine = new SpellEngine(world, spells);
        var caster = world.CreateCharacter();
        caster.Mana = caster.MaxMana = 100;
        caster.Hits = caster.MaxHits = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        engine.CastDone(caster);
        Assert.False(caster.IsCasting);
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
    }
}
