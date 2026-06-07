using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

public class CharDefCanFlagsTests
{
    [Fact]
    public void LoadFromKey_Can_ParsesSymbolicMovementFlags()
    {
        var def = new CharDef(ResourceId.Invalid);
        def.LoadFromKey("CAN", "MT_RUN|MT_WALK");

        // Regression: symbolic CAN= used to fail numeric parse and leave None,
        // stripping every such creature of its movement capabilities.
        Assert.True((def.Can & CanFlags.C_Run) != 0);
        Assert.True((def.Can & CanFlags.C_Walk) != 0);
        Assert.Equal(CanFlags.None, def.Can & CanFlags.C_Fly);
    }

    [Fact]
    public void LoadFromKey_Can_ParsesNumericAndMixedForms()
    {
        var hex = new CharDef(ResourceId.Invalid);
        hex.LoadFromKey("CAN", "0x2004"); // C_Run | C_Walk
        Assert.True((hex.Can & CanFlags.C_Run) != 0 && (hex.Can & CanFlags.C_Walk) != 0);

        var fly = new CharDef(ResourceId.Invalid);
        fly.LoadFromKey("CAN", "MT_FLY|MT_WALK");
        Assert.True((fly.Can & CanFlags.C_Fly) != 0 && (fly.Can & CanFlags.C_Walk) != 0);
        Assert.Equal(CanFlags.None, fly.Can & CanFlags.C_Run);
    }

    [Fact]
    public void LoadFromKey_Can_PrefersDefNameResolverOverFallbackMap()
    {
        // In production the resolver reads the script's own [DEFNAME can_flags]
        // values; verify the resolver result wins over the built-in fallback.
        var prev = CharDef.DefNameResolver;
        try
        {
            CharDef.DefNameResolver = name =>
                name.Equals("MT_RUN", System.StringComparison.OrdinalIgnoreCase) ? 0x2000L : null;

            var def = new CharDef(ResourceId.Invalid);
            def.LoadFromKey("CAN", "MT_RUN|MT_WALK");

            Assert.True((def.Can & CanFlags.C_Run) != 0);  // via resolver
            Assert.True((def.Can & CanFlags.C_Walk) != 0); // via fallback map
        }
        finally
        {
            CharDef.DefNameResolver = prev;
        }
    }

    [Fact]
    public void LoadFromKey_SkillRanges_ParseBraceRangesAndAliases()
    {
        var def = new CharDef(ResourceId.Invalid);

        def.LoadFromKey("WRESTLING", "{70 90}");
        def.LoadFromKey("EVALUATINGINTEL", "50,80");

        Assert.Equal((70, 90), def.SkillRanges[SkillType.Wrestling]);
        Assert.Equal((50, 80), def.SkillRanges[SkillType.EvalInt]);
    }

    [Fact]
    public void LoadFromKey_BreathAndThrowProperties_AreRuntimeTags()
    {
        var def = new CharDef(ResourceId.Invalid);

        def.LoadFromKey("BREATH.DAM", "45");
        def.LoadFromKey("THROWOBJ", "0x0F51");
        def.LoadFromKey("THROWDAM", "5,12");

        Assert.Equal("45", def.TagDefs.Get("BREATH.DAM"));
        Assert.Equal("0x0F51", def.TagDefs.Get("THROWOBJ"));
        Assert.Equal("5,12", def.TagDefs.Get("THROWDAM"));
    }

    [Fact]
    public void ApplyNpcDefinitionSkillsAndTags_CopiesCombatRuntimeData()
    {
        var def = new CharDef(ResourceId.Invalid);
        def.LoadFromKey("WRESTLING", "75");
        def.LoadFromKey("TACTICS", "80");
        def.LoadFromKey("BREATH.DAM", "45");

        var npc = new Character { IsPlayer = false };

        CharDefHelper.ApplyNpcDefinitionSkills(npc, def);
        CharDefHelper.ApplyNpcDefinitionTags(npc, def);

        Assert.Equal(75, npc.GetSkill(SkillType.Wrestling));
        Assert.Equal(80, npc.GetSkill(SkillType.Tactics));
        Assert.True(npc.TryGetTag("BREATH.DAM", out var breathDam));
        Assert.Equal("45", breathDam);
    }
}
