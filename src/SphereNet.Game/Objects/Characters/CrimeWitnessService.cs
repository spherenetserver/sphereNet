using System;
using System.Collections.Generic;
using SphereNet.Core.Enums;
using SphereNet.Game.World;

namespace SphereNet.Game.Objects.Characters;

/// <summary>
/// Source-X CChar::CheckCrimeSeen. When a covert crime (stealing / snooping) or an
/// overt crime is committed, every character in view range who has line of sight
/// — and, for a covert crime, wins the perception skill contest — notices it:
/// the witness records a per-viewer MEMORY_SAWCRIME (so the criminal shows grey to
/// THEM via Noto_CalcFlag / personal grey), a guarded-region NPC witness calls the
/// guards (global criminal flag), and @SeeCrime / @SeeSnoop fire so a script can
/// react or escalate. A crime no one sees has no consequence — matching Source-X.
/// </summary>
public static class CrimeWitnessService
{
    /// <summary>Witness search radius (Source-X g_Cfg.m_iMapViewSize).</summary>
    public const int WitnessRange = 14;

    /// <summary>Percent chance a witnessed snoop attempt registers as a noticed
    /// crime (Source-X g_Cfg.m_iSnoopCriminal).</summary>
    public static int SnoopCriminalChance { get; set; } = 100;

    /// <summary>
    /// Fires @SeeCrime (or @SeeSnoop when <c>isSnoop</c>) on a witness who noticed a
    /// crime (Source-X OnNoticeCrime). Args: witness, criminal, mark, isSnoop.
    /// Returns null to skip this witness entirely (the script handled it / RETURN 1),
    /// false for a personal grey only, or true to also flag the criminal globally
    /// (call guards). Wired by EngineWiring; null hook → default (personal grey).
    /// </summary>
    public static Func<Character, Character, Character?, bool, bool?>? OnCrimeNoticed;

    /// <summary>
    /// Run the witness check. <paramref name="skillToSee"/> is the perception skill
    /// contested for a covert crime (Stealing / Snooping); pass null for an overt
    /// crime that every witness in line of sight notices. Returns true if at least
    /// one witness noticed.
    /// </summary>
    public static bool CheckCrimeSeen(GameWorld world, Character criminal, Character? mark,
        SkillType? skillToSee, Random rng, bool isSnoop = false)
    {
        // Guards fight for justice — they can't themselves commit a crime.
        if (criminal.NpcBrain == NpcBrainType.Guard) return false;

        bool seen = false;
        bool flagCriminal = false;

        // Snapshot: NoticeCrime stamps memory items, which can mutate the sector.
        var witnesses = new List<Character>(world.GetCharsInRange(criminal.Position, WitnessRange));
        foreach (var witness in witnesses)
        {
            if (witness == criminal || witness == mark || witness.IsDeleted || witness.IsDead)
                continue;
            if (witness.PrivLevel >= PrivLevel.GM)
                continue;
            if (!world.CanSeeLOS(witness.Position, criminal.Position))
                continue;
            if (skillToSee.HasValue && !RollCrimeSeen(criminal, witness, skillToSee.Value, rng))
                continue;

            // @SeeCrime / @SeeSnoop: null = witness ignored, true = call guards.
            bool? decision = OnCrimeNoticed?.Invoke(witness, criminal, mark, isSnoop) ?? false;
            if (decision == null)
                continue;

            seen = true;

            // A snoop only REGISTERS as a noticed crime on the snoop-criminal chance
            // (Source-X) — the witness saw the attempt either way.
            if (isSnoop && rng.Next(100) >= SnoopCriminalChance)
                continue;

            witness.Memory_AddObjTypes(criminal.Uid, MemoryType.SawCrime);

            bool guardedNpc = !witness.IsPlayer &&
                (world.FindRegion(criminal.Position)?.IsFlag(RegionFlag.Guarded) ?? false);
            if (decision == true || guardedNpc)
                flagCriminal = true;
        }

        if (flagCriminal)
            criminal.MakeCriminal();
        return seen;
    }

    /// <summary>Source-X Calc_CrimeSeen: per-mille notice chance based on the
    /// viewer's perception skill minus the criminal's skill in the contested skill,
    /// floored at 1% so a witness can always get lucky.</summary>
    private static bool RollCrimeSeen(Character thief, Character viewer, SkillType skill, Random rng)
    {
        int chance = 1000 + (viewer.GetSkill(skill) - thief.GetSkill(skill));
        if (chance < 10) chance = 10;
        return rng.Next(1000) < chance;
    }
}
