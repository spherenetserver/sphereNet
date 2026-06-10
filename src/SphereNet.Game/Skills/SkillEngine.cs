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
    // Random.Shared is thread-safe; a plain shared Random instance is not and
    // can corrupt/return 0 under concurrent skill use on the multicore tick.
    private static Random _rand => Random.Shared;

    /// <summary>Callback fired when a skill gain occurs. Args: (Character, SkillType, newValue).</summary>
    public static Action<Character, SkillType, int>? OnSkillGain { get; set; }

    /// <summary>Callback fired when a stat gain occurs. Args: (Character, statIndex: 0=STR 1=DEX 2=INT, newValue).</summary>
    public static Action<Character, int, int>? OnStatGain { get; set; }

    /// <summary>Skill variance for S-curve calculation (reference SKILL_VARIANCE = 150,
    /// i.e. 15.0 skill points per bell-curve halving period).</summary>
    private const int SkillVariance = 150;

    /// <summary>Active skill duration from [SKILL] DELAY (a tenths-of-a-second
    /// curve across skill 0-100.0 → ms; reference Skill_GetTimeout).
    /// <paramref name="skillValue"/> is the user's skill in tenths. 0 = instant.</summary>
    public static int GetSkillDelayMs(SkillType skill, int skillValue = 0)
    {
        var def = DefinitionLoader.GetSkillDef((int)skill);
        if (def == null || def.Delay.IsEmpty) return 0;
        int tenths = def.Delay.GetLinear(skillValue);
        return tenths <= 0 ? 0 : tenths * 100;
    }

    /// <summary>Interval between @SkillStroke firings during a delayed skill.</summary>
    public static int GetSkillStrokeIntervalMs(SkillType skill, int skillValue = 0)
    {
        int delay = GetSkillDelayMs(skill, skillValue);
        if (delay <= 0) return 0;
        return Math.Clamp(delay / 5, 500, 2000);
    }

    /// <summary>
    /// Check if a skill use succeeds. Maps to Skill_CheckSuccess.
    /// Does NOT award experience.
    /// </summary>
    public static bool CheckSuccess(Character ch, SkillType skill, int difficulty, bool useBellCurve = true)
    {
        if (ch.PrivLevel >= PrivLevel.GM && skill != SkillType.Parrying)
            return true;

        if (difficulty < 0)
            return false;

        int iDiff = difficulty * 10; // scale 0-100 to 0-1000
        int skillVal = GetAdjustedSkill(ch, skill);

        int successChance;
        if (useBellCurve)
        {
            successChance = CalcSCurve(skillVal - iDiff, SkillVariance);
        }
        else
        {
            successChance = iDiff;
        }

        return successChance >= _rand.Next(1000);
    }

    /// <summary>
    /// Quick skill use: check + experience in one call.
    /// Maps to Skill_UseQuick in Source-X.
    /// </summary>
    public static bool UseQuick(Character ch, SkillType skill, int difficulty, bool allowGain = true, bool useBellCurve = true)
    {
        // @SkillUseQuick (Source-X) — fires before the check resolves so a script
        // can cancel the quick use (RETURN 1) before any roll or experience gain.
        if (Character.OnSkillUseQuick != null && Character.OnSkillUseQuick(ch, (int)skill, difficulty))
            return false;

        bool success = CheckSuccess(ch, skill, difficulty, useBellCurve);

        if (allowGain)
        {
            int expDiff = success ? difficulty : -difficulty;
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
        // GMs auto-succeed every check (see CheckSuccess); they must not also
        // accrue skill/stat gains from that free success.
        if (ch.PrivLevel >= PrivLevel.GM) return;
        if ((int)skill < 0 || (int)skill >= (int)SkillType.Qty)
            return;

        // Scale and clamp difficulty
        int iDiff = Math.Clamp(difficulty * 10, 1, 1000);

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
        int chance = def != null && !def.AdvRate.IsEmpty
            ? def.AdvRate.GetChancePercent(currentSkill)
            : CalcAdvanceRate(currentSkill);

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
    public static int GetSkillDisplayCap(Character ch, SkillType skill) => ResolveSkillCap(ch, skill);

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
        int classMax = ResolveSkillCap(ch, skill);

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
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.SkillSumMax > 0 ? cls.SkillSumMax : SkillSumMaxOverride;
    }

    /// <summary>
    /// Calculate advance rate. Higher skill = lower chance.
    /// Maps to ADV_RATE curve in Source-X SkillDef.
    /// </summary>
    private static int CalcAdvanceRate(int skillLevel)
    {
        // Simple inverse: easier to gain at low skill, harder at high
        if (skillLevel >= 1000) return 0;
        return Math.Max(1, (1000 - skillLevel) / 5);
    }

    /// <summary>
    /// Stat-adjusted skill value (reference Skill_GetAdjusted): the raw skill
    /// plus BONUS_STATS percent of the BONUS_STR/INT/DEX-weighted stats.
    /// </summary>
    public static int GetAdjustedSkill(Character ch, SkillType skill)
    {
        int baseVal = ch.GetSkill(skill);
        var def = DefinitionLoader.GetSkillDef((int)skill);
        if (def == null || def.BonusStats <= 0)
            return baseVal;

        int str = Math.Max(0, (int)ch.Str);
        int intl = Math.Max(0, (int)ch.Int);
        int dex = Math.Max(0, (int)ch.Dex);
        int pureBonus = def.BonusStr * str + def.BonusInt * intl + def.BonusDex * dex;
        return baseVal + def.BonusStats * pureBonus / 10000;
    }

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
        if (value < 0)
            value = -value;

        int chance = 500;
        while (value > variance && chance != 0)
        {
            value -= variance;
            chance /= 2;
        }

        return chance - (chance / 2) * value / variance;
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
        int statSum = ch.Str + ch.Dex + ch.Int + 1;
        int cap = ResolveStatSumCap(ch);
        if (statSum <= cap)
            return true;

        int sumMax = cap + cap / 4;
        int chanceForLoss = CalcSCurve(sumMax - statSum, Math.Max(1, (sumMax - cap) / 4));
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
            case 0: ch.Str--; ch.MaxHits = (short)Math.Max(1, ch.MaxHits - 1); OnStatGain?.Invoke(ch, 0, ch.Str); break;
            case 1: ch.Dex--; ch.MaxStam = (short)Math.Max(1, ch.MaxStam - 1); OnStatGain?.Invoke(ch, 1, ch.Dex); break;
            case 2: ch.Int--; ch.MaxMana = (short)Math.Max(1, ch.MaxMana - 1); OnStatGain?.Invoke(ch, 2, ch.Int); break;
        }
        return true;
    }

    private static int ResolveSkillCap(Character ch, SkillType skill)
    {
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
