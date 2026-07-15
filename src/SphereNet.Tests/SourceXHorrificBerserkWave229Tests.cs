using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXHorrificBerserkWave229Tests
{
    [Fact]
    public void HorrificBeast_AddsUnarmedRangeAndDamageIncreaseUntilExpiry()
    {
        var (engine, character) = CreateHorrificEffect();
        Character.FeatureAOS = 0x02;

        Assert.True(engine.ApplyScriptSpellEffect(
            character, character, SpellType.HorrificBeast, 1000));
        Assert.Equal((5, 15), CombatEngine.CalcWeaponDamage(character, null));
        Assert.Equal(25, CombatEngine.CalculateDamageIncrease(character));

        engine.ProcessExpirations(long.MaxValue);
        // player fists = c_man DAM 1,4 (no Str-derived unarmed base)
        Assert.Equal((1, 4), CombatEngine.CalcWeaponDamage(character, null));
        Assert.Equal(0, CombatEngine.CalculateDamageIncrease(character));
    }

    [Fact]
    public void HorrificBeastCombatBonus_RequiresAosUpdateBFeature()
    {
        var (engine, character) = CreateHorrificEffect();
        Assert.True(engine.ApplyScriptSpellEffect(
            character, character, SpellType.HorrificBeast, 1000));

        // player fists = c_man DAM 1,4 (no Str-derived unarmed base)
        Assert.Equal((1, 4), CombatEngine.CalcWeaponDamage(character, null));
        Assert.Equal(0, CombatEngine.CalculateDamageIncrease(character));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(19, 0)]
    [InlineData(20, 15)]
    [InlineData(60, 45)]
    [InlineData(80, 60)]
    [InlineData(100, 60)]
    public void GargoyleBerserk_AddsFifteenPerTwentyLostHitsWithSixtyCap(
        int lostHits, int expected)
    {
        var gargoyle = TestHarness.CreateWorld().CreateCharacter();
        gargoyle.BodyId = 0x029A;
        gargoyle.MaxHits = 100;
        gargoyle.Hits = (short)(100 - lostHits);
        Character.RacialFlags = (int)RacialFlags.GargoyleBerserk;

        Assert.Equal(expected, CombatEngine.CalculateDamageIncrease(gargoyle));
    }

    [Fact]
    public void GargoyleBerserk_IsAddedAfterBaseCapAndRequiresGargoyleBody()
    {
        var character = TestHarness.CreateWorld().CreateCharacter();
        character.MaxHits = 100;
        character.Hits = 20;
        character.SetTag("INCREASEDAM", "250");
        Character.RacialFlags = (int)RacialFlags.GargoyleBerserk;

        character.BodyId = 0x0190;
        Assert.Equal(100, CombatEngine.CalculateDamageIncrease(character));

        character.BodyId = 0x02B7;
        Assert.Equal(160, CombatEngine.CalculateDamageIncrease(character));
    }

    [Fact]
    public void SphereConfig_LoadsSourceXRacialFlagsMask()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_racial_{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, "[SPHERE]\nRacialFlags=0x0100\n");
        try
        {
            var ini = new IniParser();
            ini.Load(path);
            var config = new SphereConfig();
            config.LoadFromIni(ini);

            Assert.Equal((int)RacialFlags.GargoyleBerserk, config.RacialFlags);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (SpellEngine Engine, Character Character) CreateHorrificEffect()
    {
        var world = TestHarness.CreateWorld();
        var character = world.CreateCharacter();
        character.IsPlayer = true;
        character.Str = 1;
        character.MaxHits = character.Hits = 1;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.HorrificBeast,
            Flags = SpellFlag.Poly | SpellFlag.Good | SpellFlag.TargChar,
            DurationBase = 600,
            DurationScale = 600,
        });
        return (new SpellEngine(world, registry), character);
    }
}
