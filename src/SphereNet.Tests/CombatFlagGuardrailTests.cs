using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using Xunit;

namespace SphereNet.Tests;

// Combat wave C6 — the CombatFlags contract guardrail. Two failure modes are
// pinned: (1) the enum drifting from Source-X COMBATFLAGS_TYPE numeric values,
// and (2) a flag existing in the enum with NO wired behavior — the leak the
// C-wave audit found four of (DClickSelfUnmounts, AllowHitFromShip,
// NoPetDesert, AttackNoAggreived). Adding a new member fails this test until
// it is consciously classified below WITH its behavior site.
public class CombatFlagGuardrailTests
{
    // Source-X CServerConfig.h COMBATFLAGS_TYPE, name and value pinned.
    private static readonly (CombatFlags Flag, uint Value)[] SourceXFlags =
    [
        (CombatFlags.NoDirChange,        0x1),
        (CombatFlags.FaceCombat,         0x2),
        (CombatFlags.PreHit,             0x4),
        (CombatFlags.ElementalEngine,    0x8),
        (CombatFlags.DClickSelfUnmounts, 0x20),
        (CombatFlags.AllowHitFromShip,   0x40),
        (CombatFlags.NoPetDesert,        0x80),
        (CombatFlags.ArcheryCanMove,     0x100),
        (CombatFlags.StayInRange,        0x200),
        (CombatFlags.StackArmor,         0x1000),
        (CombatFlags.NoPoisonHit,        0x2000),
        (CombatFlags.Slayer,             0x4000),
        (CombatFlags.SwingNoRange,       0x8000),
        (CombatFlags.AnimHitSmooth,      0x10000),
        (CombatFlags.FirstHitInstant,    0x20000),
        (CombatFlags.NpcBonusDamage,     0x40000),
        (CombatFlags.ParalyzeCanSwing,   0x80000),
        (CombatFlags.AttackNoAggreived,  0x100000),
    ];

    // Where each flag's behavior lives. A flag with no entry here is an
    // "enum var ama davranış yok" leak — wire the behavior (with a test)
    // before classifying it.
    private static readonly Dictionary<CombatFlags, string> BehaviorSite = new()
    {
        [CombatFlags.NoDirChange]        = "ClientCombatHandler.TrySwingAt (no auto-face)",
        [CombatFlags.FaceCombat]         = "CombatHelper.IsFacingTarget",
        [CombatFlags.PreHit]             = "CombatHelper.GetSwingHitDelayMs (atomic hit)",
        [CombatFlags.ElementalEngine]    = "CombatEngine.ResolveAttack armor branch",
        [CombatFlags.DClickSelfUnmounts] = "CombatHelper.DClickSelfKeepsMount (wave C3)",
        [CombatFlags.AllowHitFromShip]   = "CombatHelper.IsCombatBlockedByRegion ship gate (wave C3)",
        [CombatFlags.NoPetDesert]        = "ClientCombatHandler.HandleAttack own-pet desert (wave C3)",
        [CombatFlags.ArcheryCanMove]     = "CombatHelper.ValidateSwingPrep settle delay",
        [CombatFlags.StayInRange]        = "CombatHelper.EvaluateHitTime (out of reach -> miss)",
        [CombatFlags.StackArmor]         = "CombatEngine.CalcArmorDefenseForRegion",
        [CombatFlags.NoPoisonHit]        = "CombatEngine.ResolveAttack poison blocks",
        [CombatFlags.Slayer]             = "CombatEngine.ApplySlayerDamage (wave C4)",
        [CombatFlags.SwingNoRange]       = "CombatHelper swing window + @HitCheck Recoil_NoRange",
        [CombatFlags.AnimHitSmooth]      = "CombatHelper.GetSwingAnimDelay",
        [CombatFlags.FirstHitInstant]    = "ClientCombatHandler.HandleAttack initial swing delay",
        [CombatFlags.NpcBonusDamage]     = "CombatEngine.ResolveAttack damage increase gate",
        [CombatFlags.ParalyzeCanSwing]   = "ClientCombatHandler.TrySwingAt freeze gate",
        [CombatFlags.AttackNoAggreived]  = "ClientCombatHandler/SpellEngine criminal gates (wave C3)",
    };

    [Fact]
    public void CombatFlags_MatchSourceXValues_Exactly()
    {
        var members = Enum.GetValues<CombatFlags>()
            .Where(f => f != CombatFlags.None)
            .ToList();

        Assert.Equal(SourceXFlags.Length, members.Count);
        foreach (var (flag, value) in SourceXFlags)
            Assert.Equal(value, (uint)flag);
    }

    [Fact]
    public void EveryCombatFlag_HasAWiredBehaviorSite()
    {
        foreach (var flag in Enum.GetValues<CombatFlags>())
        {
            if (flag == CombatFlags.None) continue;
            Assert.True(BehaviorSite.TryGetValue(flag, out var site) && !string.IsNullOrWhiteSpace(site),
                $"CombatFlags.{flag} has no behavior site registered — wire the behavior " +
                "(with a test) before adding it to the guardrail table.");
        }
    }

    [Fact]
    public void AosOnHitSurface_IsPinned_WithSourceXDeferredNamesExcluded()
    {
        // The 14 wired AOS on-hit properties (wave C5).
        Assert.Equal(14, AosOnHitProperties.All.Length);
        Assert.Equal(14, AosOnHitProperties.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Unimplemented in Source-X too — deliberately NOT part of the
        // surface. If one is added, implement the behavior first.
        foreach (var deferred in new[] { "HITLOWERATK", "HITLOWERDEF", "HITCURSE", "HITFATIGUE" })
            Assert.False(AosOnHitProperties.Contains(deferred),
                $"{deferred} joined AosOnHitProperties without a behavior implementation.");
    }
}
