using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Definitions;
using SphereNet.Core.Types;

namespace SphereNet.Game.Skills;

/// <summary>
/// Skill trigger types. Maps exactly to SKTRIG_TYPE in Source-X CSkillDef.h.
/// </summary>
public enum SkillTrigger
{
    Abort = 1,
    Fail = 2,
    Gain = 3,
    PreStart = 4,
    Select = 5,
    Start = 6,
    Stroke = 7,
    Success = 8,
    TargetCancel = 9,
    UseQuick = 10,
    Wait = 11,

    Qty = 12,
}

/// <summary>
/// Skill engine. Maps to CChar::Skill_* functions in Source-X CCharSkill.cpp.
/// Handles skill checks, gain, experience, caps.
/// </summary>
public static class SkillEngine
{
    /// <summary>Number of real client skills. Slots 58..98 are reserved by the
    /// protocol/Source-X enum and must never be trained or dispatched.</summary>
    public const int BaseSkillCount = (int)SkillType.Throwing + 1;

    public static bool IsValidBaseSkill(SkillType skill)
    {
        int index = (int)skill;
        return index >= 0 && index < BaseSkillCount;
    }

    public static SkillFlag GetFlags(SkillType skill) =>
        IsValidBaseSkill(skill)
            ? (SkillFlag)(DefinitionLoader.GetSkillDef((int)skill)?.Flags ?? 0)
            : SkillFlag.Disabled;

    public static bool HasFlag(SkillType skill, SkillFlag flag) =>
        (GetFlags(skill) & flag) != 0;

    public static int GetUseRange(SkillType skill, int fallback)
    {
        int configured = DefinitionLoader.GetSkillDef((int)skill)?.Range ?? 0;
        return configured > 0 ? configured : fallback;
    }

    public static int GetEffect(SkillType skill, int skillValue, int fallback)
    {
        var effect = DefinitionLoader.GetSkillDef((int)skill)?.Effect;
        return effect is { IsEmpty: false } ? effect.GetLinear(skillValue) : fallback;
    }

    // Random.Shared is thread-safe; a plain shared Random instance is not and
    // can corrupt/return 0 under concurrent skill use on the multicore tick.
    private static Random _rand => Random.Shared;

    /// <summary>Callback fired when a skill gain occurs. Args: (Character, SkillType, newValue).</summary>
    public static Action<Character, SkillType, int>? OnSkillGain { get; set; }

    public static Action<Character, SkillType, int>? OnSkillDecrease { get; set; }

    /// <summary>Pre-roll gain hook (Source-X Skill_Experience @SkillGain): fired
    /// BEFORE the gain roll so a script can tune the per-mille gain chance or the
    /// effective skill cap, or cancel the gain attempt by returning true. Installed
    /// only when @SkillGain is actually hooked, so unscripted shards pay nothing.</summary>
    public delegate bool SkillGainCheckHook(Character ch, SkillType skill, ref int chance, ref int skillMax);
    public static SkillGainCheckHook? OnSkillGainCheck { get; set; }

    /// <summary>Callback fired when a stat gain occurs. Args: (Character, statIndex: 0=STR 1=DEX 2=INT, newValue).</summary>
    public static Action<Character, int, int>? OnStatGain { get; set; }

    public static Action<Character, int, int>? OnStatDecrease { get; set; }

    /// <summary>Skill variance for S-curve calculation. Source-X SKILL_VARIANCE = 100
    /// (10.0 skill points per bell-curve halving period).</summary>
    private const int SkillVariance = 100;

    /// <summary>Active skill duration from [SKILL] DELAY (a tenths-of-a-second
    /// curve across skill 0-100.0 → ms; reference Skill_GetTimeout).
    /// <paramref name="skillValue"/> is the user's skill in tenths. 0 = instant.</summary>
    public static int GetSkillDelayMs(SkillType skill, int skillValue = 0)
    {
        if (!IsValidBaseSkill(skill)) return 0;
        var def = DefinitionLoader.GetSkillDef((int)skill);
        if (def == null || def.Delay.IsEmpty) return 0;
        int tenths = def.Delay.GetLinear(skillValue);
        return tenths <= 0 ? 0 : (int)Math.Min(int.MaxValue, (long)tenths * 100L);
    }

    /// <summary>Interval between @SkillStroke firings during a delayed skill.
    /// Source-X Skill_Stroke re-arms with the FULL skill DELAY between strokes
    /// (SetTimeout(delay), CCharSkill.cpp:3649) — the old /5 subdivision
    /// animated gathering five times per swing.</summary>
    public static int GetSkillStrokeIntervalMs(SkillType skill, int skillValue = 0)
    {
        return GetSkillDelayMs(skill, skillValue);
    }

    /// <summary>Stroke count a delayed skill runs before completing — Source-X
    /// rolls it at SKTRIG_START: fishing 1-2 (CCharSkill.cpp:1568), mining and
    /// lumberjacking 2-6 (:1463/:1667). Other delayed skills complete after a
    /// single DELAY period.</summary>
    public static int RollStrokeCount(SkillType skill) => skill switch
    {
        SkillType.Fishing => Random.Shared.Next(2) + 1,
        SkillType.Mining or SkillType.Lumberjacking => Random.Shared.Next(5) + 2,
        _ => 1,
    };

    /// <summary>
    /// Check if a skill use succeeds. Maps to Skill_CheckSuccess.
    /// Does NOT award experience.
    /// </summary>
    public static bool CheckSuccess(Character ch, SkillType skill, int difficulty, bool useBellCurve = true)
    {
        if (!IsValidBaseSkill(skill))
            return false;
        if (ch.PrivLevel >= PrivLevel.GM && skill != SkillType.Parrying)
            return true;

        if (difficulty < 0)
            return false;

        int iDiff = (int)Math.Min(int.MaxValue, (long)difficulty * 10L); // scale 0-100 to 0-1000
        int skillVal = GetAdjustedSkill(ch, skill);

        int successChance;
        if (useBellCurve)
        {
            successChance = CalcSCurve((int)Math.Clamp((long)skillVal - iDiff,
                int.MinValue, int.MaxValue), SkillVariance);
        }
        else
        {
            successChance = iDiff;
        }

        // A zero chance must be a real zero: with ">=" a 0-chance roll still
        // succeeded when the die came up 0 (1-in-1000). That margin was the
        // source of the long-standing intermittent success-curve test
        // failures (craft fail-loss, stealth) and of skill-0 flukes against
        // impossible difficulties in game.
        if (successChance <= 0)
            return false;

        return successChance >= _rand.Next(1000);
    }

    /// <summary>Bell-curve success roll against a raw skill VALUE instead of a
    /// character's stored skill — used by the era-0 combat hit roll, where the
    /// effective weapon skill of an NPC is inferred from its stats rather than
    /// read from the (usually empty) skill table.</summary>
    public static bool CheckSuccessValue(int skillValue, int difficulty)
    {
        if (difficulty < 0)
            return false;
        long scaledDifficulty = Math.Min(int.MaxValue, (long)difficulty * 10L);
        int successChance = CalcSCurve((int)Math.Clamp((long)skillValue - scaledDifficulty,
            int.MinValue, int.MaxValue), SkillVariance);
        if (successChance <= 0)
            return false;
        return successChance >= _rand.Next(1000);
    }

    /// <summary>
    /// Quick skill use: check + experience in one call.
    /// Maps to Skill_UseQuick in Source-X.
    /// </summary>
    public static bool UseQuick(Character ch, SkillType skill, int difficulty, bool allowGain = true, bool useBellCurve = true)
    {
        if (!IsValidBaseSkill(skill) || HasFlag(skill, SkillFlag.Disabled) ||
            HasFlag(skill, SkillFlag.Scripted))
            return false;

        bool success = CheckSuccess(ch, skill, difficulty, useBellCurve);

        // @SkillUseQuick (Source-X) — fires AFTER the roll with ARGN3 = result; a
        // script may flip the result, or cancel the use entirely (RETURN 1) so no
        // experience is gained.
        if (Character.OnSkillUseQuickDetailed != null)
        {
            int outcome = Character.OnSkillUseQuickDetailed(ch, (int)skill, ref difficulty,
                success ? 1 : 0);
            if (outcome < 0) return false;
            success = outcome != 0;
        }
        else if (Character.OnSkillUseQuick != null)
        {
            int outcome = Character.OnSkillUseQuick(ch, (int)skill, difficulty, success ? 1 : 0);
            if (outcome < 0)
                return false;
            success = outcome != 0;
        }

        if (allowGain)
        {
            int gainDifficulty = (int)Math.Clamp((long)difficulty, 0, int.MaxValue);
            int expDiff = success ? gainDifficulty : -gainDifficulty;
            GainExperience(ch, skill, expDiff);
        }

        return success;
    }

    /// <summary>
    /// Award skill experience / potential gain.
    /// Maps to Skill_Experience in Source-X.
    /// </summary>
    public static void GainExperience(Character ch, SkillType skill, int difficulty)
    {
        if (ch.IsDead) return;
        // No priv gate here: Source-X Skill_Experience (CCharSkill.cpp:363)
        // awards gains to GMs too — PRIV_GM only forces the success roll in
        // Skill_CheckSuccess (:526), it does not suppress experience.
        if (!IsValidBaseSkill(skill))
            return;

        // Source-X CChar::Skill_Experience: skills do not advance in safe areas.
        var gainRegion = SphereNet.Game.Objects.ObjBase.ResolveWorld?.Invoke()?.FindRegion(ch.Position);
        if (gainRegion != null && gainRegion.IsFlag(RegionFlag.Safe))
            return;

        // Scale and clamp difficulty. Source-X keeps the sign: a failed use
        // arrives negative and clamps to 1 (CCharSkill.cpp:381-385), so fails
        // still earn the minimum credit instead of Abs()'d full credit.
        int iDiff = (int)Math.Clamp((long)difficulty * 10L, 1L, 1000L);

        int currentSkill = ch.GetSkill(skill);
        int skillMax = GetSkillMax(ch, skill);

        // Lock state: 0=Up (gain), 1=Down (decay only), 2=Locked (no change)
        byte lockState = ch.GetSkillLock(skill);
        if (lockState == 2)
            return;

        // Check total skill cap
        int totalSkill = GetSkillSum(ch);
        int totalMax = GetSkillSumMax(ch);
        if (totalSkill >= totalMax)
            iDiff = 0; // at skill cap

        // Gain radius check (task too easy). Reference semantics: only
        // active when the skill def sets GAINRADIUS (> 0); no invented default.
        var def = DefinitionLoader.GetSkillDef((int)skill);
        int gainRadius = def?.GainRadius ?? 0;
        if (gainRadius > 0 && (iDiff + gainRadius) < Math.Max(50, currentSkill))
            return; // task too easy for gain

        // Advance rate — ADV_RATE curve expresses "uses per 0.1 gain"; the
        // per-mille gain chance is its inverse (reference GetChancePercent).
        // No curve → no gain (CValueDefs.cpp:175 returns 0), never an
        // invented substitute curve.
        int chance = def != null && !def.AdvRate.IsEmpty
            ? def.AdvRate.GetChancePercent(currentSkill)
            : 0;

        // @SkillGain (Source-X Skill_Experience) — fired BEFORE the gain roll so a
        // script can raise/lower the per-mille chance or the effective cap, or
        // cancel the gain attempt (RETURN 1). Only installed when @SkillGain is
        // hooked, so this is a single null check on the common path.
        if (OnSkillGainCheck != null && OnSkillGainCheck(ch, skill, ref chance, ref skillMax))
            return;

        if (chance <= 0)
            return;

        int roll = _rand.Next(1000);

        // Reference structure (Skill_Experience): the block is entered when
        // the used skill can still rise; the decay roll runs before the gain
        // roll and is independent of the total cap, so a capped character
        // erodes a DOWN-locked skill and gains on a later use.
        int skillLevelFixed = Math.Max(50, currentSkill);
        if (skillLevelFixed < skillMax)
        {
            // Slightly higher decay chance than gain chance (reference 3:4).
            if (roll * 3 <= chance * 4)
            {
                TrySkillDecay(ch, skill);
            }

            // Skill gain — only when lock state is Up (0) and not at the cap.
            if (iDiff > 0 && lockState == 0 && roll <= chance)
            {
                ch.SetSkill(skill, (ushort)(currentSkill + 1));
                OnSkillGain?.Invoke(ch, skill, currentSkill + 1);
            }
        }

        // Stat gains
        TryStatGain(ch, skill);
    }

    /// <summary>Configurable total skill cap, defaults to 7000 (700.0). Set from sphere.ini MAXBASESKILL.</summary>
    public static int SkillSumMaxOverride { get; set; } = 7000;

    /// <summary>Stat advance-rate curves from the script [ADVANCE] section
    /// (reference g_Cfg.m_StatAdv): index 0=Str, 1=Dex, 2=Int. Empty curves
    /// disable stat gain, matching the reference.</summary>
    public static ValueCurve[] StatAdvCurves { get; set; } =
        [ValueCurve.Empty, ValueCurve.Empty, ValueCurve.Empty];

    /// <summary>Per-skill cap for the 0x3A skill-list cap field (class caps
    /// and overrides, without the lock clamping used by gain logic).</summary>
    public static int GetSkillDisplayCap(Character ch, SkillType skill) =>
        Math.Clamp(ResolveSkillCap(ch, skill), 0, ushort.MaxValue);

    /// <summary>Per-skill max override table (from SkillClassDef or config).</summary>
    public static Dictionary<SkillType, int> SkillMaxOverrides { get; } = [];

    /// <summary>
    /// Get max skill value for a character.
    /// Default 1000 (100.0) for players, adjustable.
    /// </summary>
    public static int GetSkillMax(Character ch, SkillType skill)
    {
        byte lockState = ch.GetSkillLock(skill);
        int current = ch.GetSkill(skill);
        int classMax = Math.Clamp(ResolveSkillCap(ch, skill), 0, ushort.MaxValue);

        if (lockState == 1) // down
            return Math.Min(current, classMax);
        if (lockState == 2) // locked
            return current < classMax ? current : classMax;

        return classMax;
    }

    /// <summary>Get total of all skill values.</summary>
    public static int GetSkillSum(Character ch)
    {
        int sum = 0;
        for (int i = 0; i < (int)SkillType.Qty; i++)
            sum += ch.GetSkill((SkillType)i);
        return sum;
    }

    /// <summary>Get maximum total skill cap.</summary>
    public static int GetSkillSumMax(Character ch)
    {
        // Source-X Skill_GetSumMax: a per-character OVERRIDE.SKILLSUM tag wins
        // over the class def and the global override.
        if (ch.TryGetTag("OVERRIDE.SKILLSUM", out string? sumStr) &&
            int.TryParse(sumStr, out int sumTag) && sumTag > 0)
            return sumTag;
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.SkillSumMax > 0 ? cls.SkillSumMax : SkillSumMaxOverride;
    }


    /// <summary>
    /// Stat-adjusted skill value (reference Skill_GetAdjusted): the raw skill
    /// plus BONUS_STATS percent of the BONUS_STR/INT/DEX-weighted stats, plus the
    /// per-character <c>SkillMod&lt;n&gt;</c> equipment bonus. The SkillMod term is
    /// the effective-skill layer: @Equip/@UnEquip scripts (and any future first-class
    /// equipment property) maintain it, exactly as reference Skill_GetAdjusted reads
    /// its <c>SkillMod%d</c> key. It is off the hot path — combat/crafting/skill-gain
    /// use the raw base (GetSkill), matching reference Skill_GetBase.
    /// </summary>
    public static int GetAdjustedSkill(Character ch, SkillType skill)
    {
        long adjusted = ch.GetSkill(skill);
        var def = DefinitionLoader.GetSkillDef((int)skill);
        if (def != null && def.BonusStats > 0)
        {
            int str = Combat.CombatEngine.EffectiveStr(ch);
            int intl = Combat.CombatEngine.EffectiveInt(ch);
            int dex = Combat.CombatEngine.EffectiveDex(ch);
            long pureBonus = (long)def.BonusStr * str + (long)def.BonusInt * intl +
                (long)def.BonusDex * dex;
            adjusted += (long)def.BonusStats * pureBonus / 10000L;
        }
        adjusted += GetSkillModBonus(ch, skill);
        return (int)Math.Clamp(adjusted, 0L, int.MaxValue);
    }

    /// <summary>Reference Skill_GetAdjusted uiBonSkill term: the character's
    /// <c>SkillMod&lt;n&gt;</c> key (n = skill index), a signed effective-skill
    /// bonus maintained by equip/unequip scripts. Absent/unparseable → 0.</summary>
    public static int GetSkillModBonus(Character ch, SkillType skill) =>
        ch.TryGetTag($"SkillMod{(int)skill}", out string? s) && int.TryParse(s, out int v) ? v : 0;

    /// <summary>
    /// S-curve for bell-curve success checks. Exact port of the reference
    /// Calc_GetSCurve/Calc_GetBellCurve pair: chance halves for each
    /// variance period of distance from the midpoint, mirrored above it.
    /// </summary>
    internal static int CalcSCurve(int value, int variance)
    {
        int chance = CalcBellCurve(value, variance);
        if (value > 0)
            return 1000 - chance;
        return chance;
    }

    private static int CalcBellCurve(int value, int variance)
    {
        if (variance <= 0)
            return 500;
        long distance = Math.Abs((long)value);

        int chance = 500;
        while (distance > variance && chance != 0)
        {
            distance -= variance;
            chance /= 2;
        }

        return chance - (int)((long)(chance / 2) * distance / variance);
    }

    /// <summary>Decay one DOWN-locked skill by 0.1 (reference
    /// Skill_Decrease): runs on the pre-gain decay roll regardless of the
    /// total cap, opening room for the skill being trained.</summary>
    private static void TrySkillDecay(Character ch, SkillType excludeSkill)
    {
        int count = (int)SkillType.Qty;
        int start = Random.Shared.Next(count);
        for (int n = 0; n < count; n++)
        {
            var sk = (SkillType)((start + n) % count);
            if (sk == excludeSkill) continue;
            if (ch.GetSkillLock(sk) != 1) continue;
            int val = ch.GetSkill(sk);
            if (val > 0)
            {
                ch.SetSkill(sk, (ushort)(val - 1));
                OnSkillDecrease?.Invoke(ch, sk, val - 1);
                return;
            }
        }
    }

    /// <summary>
    /// Try to gain stats from skill use.
    /// Stats gain based on SkillDef stat mapping or hardcoded fallback.
    /// </summary>
    private static int GetStatLock(Character ch, int statIdx) => ch.GetStatLock(statIdx);

    private static void TryStatGain(Character ch, SkillType skill)
    {
        // Reference: stats train only while the used skill's lock is Up.
        if (ch.GetSkillLock(skill) != 0)
            return;

        var def = DefinitionLoader.GetSkillDef((int)skill);
        int statSumMax = ResolveStatSumCap(ch);

        for (int statIdx = 0; statIdx < 3; statIdx++)
        {
            // Polymorphed characters can only train INT (reference parity).
            if (ch.IsStatFlag(StatFlag.Polymorph) && statIdx != 2)
                continue;
            if (GetStatLock(ch, statIdx) != 0)
                continue;

            int statVal = statIdx switch { 0 => ch.Str, 1 => ch.Dex, _ => ch.Int };
            if (statVal <= 0)
                continue;
            if (ch.Str + ch.Dex + ch.Int > statSumMax)
                break;
            int statMax = statIdx switch { 0 => ResolveStrCap(ch), 1 => ResolveDexCap(ch), _ => ResolveIntCap(ch) };
            if (statVal >= statMax)
                continue;

            // STAT_* on the skill def is the ceiling this skill trains the
            // stat toward; no target (or no def) = this skill trains nothing.
            int statTarg = def == null
                ? 0
                : statIdx switch { 0 => def.StatStr, 1 => def.StatDex, _ => def.StatInt };
            if (statVal >= statTarg)
                continue;

            int difficulty = statVal * 1000 / statTarg;
            var adv = StatAdvCurves.Length > statIdx ? StatAdvCurves[statIdx] : ValueCurve.Empty;
            int chance = adv.IsEmpty ? 0 : adv.GetChancePercent(difficulty);
            if (def != null && def.BonusStats > 0)
            {
                int bonusWeight = statIdx switch { 0 => def.BonusStr, 1 => def.BonusDex, _ => def.BonusInt };
                chance = chance * bonusWeight * def.BonusStats / 10000;
            }
            if (chance <= 0)
                continue;

            if (TryStatDecrease(ch, statIdx, def))
            {
                if (chance > _rand.Next(1000))
                {
                    switch (statIdx)
                    {
                        case 0: ch.Str++; ch.MaxHits++; OnStatGain?.Invoke(ch, 0, ch.Str); break;
                        case 1: ch.Dex++; ch.MaxStam++; OnStatGain?.Invoke(ch, 1, ch.Dex); break;
                        case 2: ch.Int++; ch.MaxMana++; OnStatGain?.Invoke(ch, 2, ch.Int); break;
                    }
                    break; // one stat gain per skill use (reference)
                }
            }
        }
    }

    /// <summary>Port of the reference Stat_Decrease: returns true when there
    /// is room for a +1 stat under the total cap; over the cap it may erode
    /// a DOWN-locked stat (the one this skill values least) to make room.</summary>
    private static bool TryStatDecrease(Character ch, int gainStatIdx, SkillDef? def)
    {
        if (!ch.IsPlayer)
            return false;
        int statSum = ch.Str + ch.Dex + ch.Int + 1;
        int cap = ResolveStatSumCap(ch);
        if (statSum <= cap)
            return true;

        int sumMax = cap + cap / 4;
        int chanceForLoss = CalcSCurve(sumMax - cap, Math.Max(1, (sumMax - cap) / 4));
        if (chanceForLoss <= _rand.Next(1000))
            return false;

        int minStat = -1;
        int minVal = gainStatIdx switch { 0 => ResolveStrCap(ch), 1 => ResolveDexCap(ch), _ => ResolveIntCap(ch) };
        for (int i = 0; i < 3; i++)
        {
            if (i == gainStatIdx || GetStatLock(ch, i) != 1)
                continue;
            int val = def != null
                ? i switch { 0 => def.BonusStr, 1 => def.BonusDex, _ => def.BonusInt }
                : i switch { 0 => ch.Str, 1 => ch.Dex, _ => ch.Int };
            if (minVal > val)
            {
                minStat = i;
                minVal = val;
            }
        }

        if (minStat < 0)
            return false;

        int statVal = minStat switch { 0 => ch.Str, 1 => ch.Dex, _ => ch.Int };
        if (statVal <= 10)
            return false;

        switch (minStat)
        {
            case 0: ch.Str--; ch.MaxHits = (short)Math.Max(1, ch.MaxHits - 1); OnStatDecrease?.Invoke(ch, 0, ch.Str); break;
            case 1: ch.Dex--; ch.MaxStam = (short)Math.Max(1, ch.MaxStam - 1); OnStatDecrease?.Invoke(ch, 1, ch.Dex); break;
            case 2: ch.Int--; ch.MaxMana = (short)Math.Max(1, ch.MaxMana - 1); OnStatDecrease?.Invoke(ch, 2, ch.Int); break;
        }
        return true;
    }

    private static int ResolveSkillCap(Character ch, SkillType skill)
    {
        // Source-X Skill_GetMax: a per-character OVERRIDE.SKILLCAP_<n> tag wins
        // over the class def and the global override.
        if (ch.TryGetTag($"OVERRIDE.SKILLCAP_{(int)skill}", out string? capStr) &&
            int.TryParse(capStr, out int capTag) && capTag > 0)
            return capTag;
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        if (cls != null && cls.SkillCaps.TryGetValue(skill, out int classCap) && classCap > 0)
            return classCap;
        if (SkillMaxOverrides.TryGetValue(skill, out int overrideMax))
            return overrideMax;
        return 1000;
    }

    private static int ResolveStatSumCap(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.StatSumMax > 0 ? cls.StatSumMax : 225;
    }

    private static int ResolveStrCap(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.StrMax > 0 ? cls.StrMax : 125;
    }

    private static int ResolveDexCap(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.DexMax > 0 ? cls.DexMax : 125;
    }

    private static int ResolveIntCap(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.IntMax > 0 ? cls.IntMax : 125;
    }

}
