using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SpellFlagDefinitionTests
{
    private static SpellRegistry Load(string contents)
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"spell_flags_{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, contents);
            runtime.Resources.LoadResourceFile(path);
            var registry = new SpellRegistry();
            new DefinitionLoader(runtime.Resources, registry).LoadAll();
            return registry;
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(SpellFlag.Area)]
    [InlineData(SpellFlag.Poly)]
    [InlineData(SpellFlag.TargDead)]
    [InlineData(SpellFlag.Damage)]
    [InlineData(SpellFlag.Bless)]
    [InlineData(SpellFlag.Curse)]
    [InlineData(SpellFlag.Heal)]
    [InlineData(SpellFlag.Tick)]
    public void NumericScriptConstantsPreserveHighSpellBits(SpellFlag flag)
    {
        var registry = Load($"""
            [DEFNAME spell_flag_probe]
            custom_flag 0{(ulong)flag:X}
            [SPELL 4]
            FLAGS=custom_flag|spellflag_targ_char
            """);
        Assert.Equal(flag | SpellFlag.TargChar, registry.Get(SpellType.Heal)!.Flags);
    }

    [Fact]
    public void HealWithSourceXDefnameRestoresHitpoints()
    {
        var registry = Load("""
            [DEFNAME spell_flags]
            spellflag_heal 040000000
            spellflag_targ_char 04
            spellflag_good 0200
            [SPELL 4]
            DEFNAME=s_heal
            FLAGS=spellflag_targ_char|spellflag_good|spellflag_heal
            EFFECT=5,20
            """);
        var world = TestHarness.CreateWorld();
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.MaxHits = 100;
        caster.Hits = 40;
        new SpellEngine(world, registry).ApplyDirectEffect(caster, caster, SpellType.Heal, 1000);
        Assert.InRange(caster.Hits, (short)52, (short)59);
    }

    [Fact]
    public void NumericDefValuesPreserveWidthAndFollowRedefinition()
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"numeric_defs_{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, """
                [DEFNAME initial]
                wide_value 0100000000
                signed_value -1
                changed_to_text 040000000
                changed_to_number hello
                [DEFNAME replacement]
                changed_to_text hello
                changed_to_number 020000000
                """);
            runtime.Resources.LoadResourceFile(path);
            Assert.True(runtime.Resources.TryResolveDefNameValue("wide_value", out long wide));
            Assert.Equal(0x100000000L, wide);
            Assert.True(runtime.Resources.TryResolveDefNameValue("signed_value", out long signed));
            Assert.Equal(-1, signed);
            Assert.False(runtime.Resources.TryResolveDefNameValue("changed_to_text", out _));
            Assert.True(runtime.Resources.TryGetDefValue("changed_to_text", out string text));
            Assert.Equal("hello", text);
            Assert.True(runtime.Resources.TryResolveDefNameValue("changed_to_number", out long number));
            Assert.Equal(0x20000000L, number);
            Assert.False(runtime.Resources.TryGetDefValue("changed_to_number", out _));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(SpellType.MagicArrow, SpellFlag.Damage)]
    [InlineData(SpellType.Fireball, SpellFlag.Damage)]
    [InlineData(SpellType.Lightning, SpellFlag.Damage)]
    [InlineData(SpellType.GreaterHeal, SpellFlag.Heal)]
    [InlineData(SpellType.Strength, SpellFlag.Bless)]
    [InlineData(SpellType.Agility, SpellFlag.Bless)]
    [InlineData(SpellType.Cunning, SpellFlag.Bless)]
    [InlineData(SpellType.Bless, SpellFlag.Bless)]
    [InlineData(SpellType.Weaken, SpellFlag.Curse)]
    [InlineData(SpellType.Clumsy, SpellFlag.Curse)]
    [InlineData(SpellType.Feeblemind, SpellFlag.Curse)]
    [InlineData(SpellType.Curse, SpellFlag.Curse)]
    public void ScriptFlagsReachDamageHealAndStatEffectHandlers(SpellType spell, SpellFlag flag)
    {
        var registry = Load($"""
            [DEFNAME spell_flags]
            spellflag_{flag} 0{(ulong)flag:X}
            [SPELL {(int)spell}]
            FLAGS=spellflag_targ_char|spellflag_{flag}
            EFFECT=10,10
            DURATION=60.0
            """);
        var world = TestHarness.CreateWorld();
        var caster = world.CreateCharacter();
        var target = world.CreateCharacter();
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        target.Str = target.Dex = target.Int = 50;
        target.MaxHits = 100;
        target.Hits = 50;
        new SpellEngine(world, registry).ApplyDirectEffect(caster, target, spell, 1000);

        if (flag == SpellFlag.Damage) Assert.True(target.Hits < 50);
        else if (flag == SpellFlag.Heal) Assert.True(target.Hits > 50);
        else
        {
            bool str = spell is SpellType.Strength or SpellType.Weaken or SpellType.Bless or SpellType.Curse;
            bool dex = spell is SpellType.Agility or SpellType.Clumsy or SpellType.Bless or SpellType.Curse;
            bool intl = spell is SpellType.Cunning or SpellType.Feeblemind or SpellType.Bless or SpellType.Curse;
            Assert.Equal(str ? (flag == SpellFlag.Bless ? 1 : -1) : 0, Math.Sign(target.Str - 50));
            Assert.Equal(dex ? (flag == SpellFlag.Bless ? 1 : -1) : 0, Math.Sign(target.Dex - 50));
            Assert.Equal(intl ? (flag == SpellFlag.Bless ? 1 : -1) : 0, Math.Sign(target.Int - 50));
        }
    }
}
