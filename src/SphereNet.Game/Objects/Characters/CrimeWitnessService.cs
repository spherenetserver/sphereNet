using System;
using System.Collections.Generic;
using SphereNet.Core.Enums;
using SphereNet.Game.Messages;
using SphereNet.Game.World;

namespace SphereNet.Game.Objects.Characters;

/// <summary>
/// Source-X CChar::CheckCrimeSeen / CChar::OnNoticeCrime (CCharFight.cpp:24-163).
/// When a crime is committed, every character in view range with line of sight —
/// and, for a covert crime, one who wins the perception roll — notices it:
/// <list type="bullet">
/// <item>a PLAYER witness remembers MEMORY_SAWCRIME (the criminal shows grey to
/// them) and runs @SeeCrime; only an ARGN1 set by the script flags the criminal
/// globally;</item>
/// <item>an NPC witness remembers MEMORY_SAWCRIME and, when it can speak, flags
/// the criminal (Noto_Criminal) in any region; in a guarded region it also yells
/// and calls the guards.</item>
/// </list>
/// A crime no one sees has no consequence.
/// </summary>
public static class CrimeWitnessService
{
    /// <summary>Witness search radius (Source-X g_Cfg.m_iMapViewSize,
    /// CCharFight.cpp:111). Wired from MAPVIEWSIZE.</summary>
    public static int WitnessRange { get; set; } = 18;

    /// <summary>Percent chance a witnessed snoop is treated as a noticed crime
    /// (Source-X g_Cfg.m_iSnoopCriminal, CCharFight.cpp:156).</summary>
    public static int SnoopCriminalChance { get; set; } = 100;

    /// <summary>@SeeSnoop on a witness (CCharFight.cpp:141-151). Args: witness,
    /// criminal, mark. Returns true (RETURN 1) to make the witness ignore the snoop.</summary>
    public static Func<Character, Character, Character?, bool>? OnSeeSnoop;

    /// <summary>@SeeCrime on a PLAYER witness (CCharFight.cpp:45-53). Args: witness,
    /// criminal, mark. Returns the ARGN1 read-back: true = flag the criminal
    /// globally (call the guards).</summary>
    public static Func<Character, Character, Character?, bool>? OnSeeCrime;

    /// <summary>An NPC witness standing in a guarded area yells
    /// DEFMSG_NPC_GENERIC_CRIM (unless it is a guard) and calls the guards on the
    /// criminal (CCharFight.cpp:85-91). Args: witness, criminal.</summary>
    public static Action<Character, Character>? OnNpcCallGuards;

    /// <summary>Show a witness what it noticed, over the criminal and to that witness
    /// alone (pChar->ObjMessage(z, this), CCharFight.cpp:165/172). Args: witness,
    /// criminal, text.</summary>
    public static Action<Character, Character, string>? OnWitnessNotice;

    /// <summary>
    /// Run the witness check. <paramref name="skillToSee"/> is the perception skill
    /// contested for a covert crime (Stealing / Snooping); pass null for an overt
    /// crime (SKILL_NONE) that every witness in line of sight notices. Returns true
    /// if at least one witness noticed.
    /// <paramref name="includeMark"/> also lets the owner of a harmed pet witness
    /// that harm, subject to the same range, privilege and sight checks.
    /// </summary>
    public static bool CheckCrimeSeen(GameWorld world, Character criminal, Character? mark,
        SkillType? skillToSee, Random rng, bool isSnoop = false, Items.Item? item = null, string? action = null,
        bool includeMark = false)
    {
        // Restore the theft victim's perception from 56b. Source-X's unconditional
        // mark exclusion makes its fYour bonus, message and NPC snoop reaction
        // unreachable. Other callers retain the mark exclusion.
        bool includeVictim = includeMark || isSnoop || skillToSee is SkillType.Snooping or SkillType.Stealing;

        // Guards fight for justice — they can't themselves commit a crime.
        if (!criminal.IsPlayer && criminal.NpcBrain == NpcBrainType.Guard) return false;

        bool seen = false;

        // Snapshot: NoticeCrime stamps memory items, which can mutate the sector.
        var witnesses = new List<Character>(world.GetCharsInRange(criminal.Position, WitnessRange));
        foreach (var witness in witnesses)
        {
            bool isVictim = witness == mark;
            if (witness == criminal || (isVictim && !includeVictim) || witness.IsDeleted)
                continue;
            if (witness.PrivLevel >= PrivLevel.GM)
                continue;
            if (!world.CanSeeLOSFor(witness, criminal, Core.Enums.LosFlags.NbWindows))
                continue;
            if (!CalcCrimeSeen(criminal, witness, skillToSee, rng, isVictim))
                continue;

            if (isSnoop)
            {
                // @SeeSnoop RETURN 1: this witness ignores the snoop.
                if (OnSeeSnoop?.Invoke(witness, criminal, mark) == true)
                    continue;
                seen = true;
                // Off chance of being a criminal (CCharFight.cpp:156).
                if (rng.Next(100) < SnoopCriminalChance)
                    OnNoticeCrime(world, witness, criminal, mark);
                if (isVictim)
                {
                    witness.Memory_AddObjTypes(criminal.Uid, MemoryType.IrritatedBy);
                    if (!witness.IsPlayer)
                        NoticeNpcSnoop(witness, criminal, rng);
                }
            }
            else
            {
                seen = true;
                OnNoticeCrime(world, witness, criminal, mark);
            }
            if (item != null && !string.IsNullOrEmpty(action))
            {
                string notice = mark != null
                    ? ServerMessages.GetFormatted(Msg.MsgYounotice2, criminal.Name, action,
                        isVictim ? ServerMessages.Get(Msg.MsgYounoticeYour) : mark.Name,
                        isVictim ? "" : ServerMessages.Get(Msg.MsgYounoticeS), item.Name)
                    : ServerMessages.GetFormatted(Msg.MsgYounotice1, criminal.Name, action, item.Name);
                OnWitnessNotice?.Invoke(witness, criminal, notice);
            }
        }

        return seen;
    }

    // CCharNPCAct.cpp NPC_OnNoticeSnoop: only the snooped NPC speaks and has a
    // one-in-four chance to flee from the thief for twenty steps. This reaction
    // does not depend on the separate SNOOPCRIMINAL roll.
    private static void NoticeNpcSnoop(Character victim, Character thief, Random rng)
    {
        if (AI.NpcAI.NpcCanSpeak(victim))
        {
            string message = rng.Next(4) switch
            {
                0 => Msg.NpcGenericSnooped1,
                1 => Msg.NpcGenericSnooped2,
                2 => Msg.NpcGenericSnooped3,
                _ => Msg.NpcGenericSnooped4
            };
            victim.SayAloud(ServerMessages.Get(message));
        }
        if (rng.Next(4) == 0)
        {
            victim.Act = thief.Uid;
            victim.FleeStepsMax = 20;
            victim.FleeStepsCurrent = 0;
            victim.Action = (SkillType)NpcAction.Flee;
        }
    }

    /// <summary>Source-X CChar::OnNoticeCrime (CCharFight.cpp:24-92):
    /// <paramref name="witness"/> noticed <paramref name="criminal"/> offend
    /// <paramref name="mark"/>.</summary>
    public static void OnNoticeCrime(GameWorld? world, Character witness, Character criminal, Character? mark)
    {
        if (criminal == witness || criminal == mark || criminal.PrivLevel >= PrivLevel.GM ||
            (!criminal.IsPlayer && criminal.NpcBrain == NpcBrainType.Guard))
            return;

        // A mark that is itself criminal/evil to the offender is fair game.
        if (mark != null)
        {
            byte noto = Clients.GameClient.ComputeNotoriety(world, criminal, mark);
            if (noto is 4 or 6) // NOTO_CRIMINAL / NOTO_EVIL
                return;
        }

        if (witness.IsPlayer)
        {
            // Players never call the guards by themselves; only @SeeCrime's ARGN1 does.
            bool makeCriminal = OnSeeCrime?.Invoke(witness, criminal, mark) ?? false;
            witness.Memory_AddObjTypes(criminal.Uid, MemoryType.SawCrime);
            if (makeCriminal)
                criminal.MakeCriminal(mark, fromSawCrime: true); // CCharFight.cpp:57
            return;
        }

        // NPC witness.
        if (witness != mark)
        {
            if (witness.HasOwner(criminal.Uid))
                return; // I won't rat out my master.
            witness.Memory_AddObjTypes(criminal.Uid, MemoryType.SawCrime);
        }
        else
        {
            witness.Memory_AddObjTypes(criminal.Uid, MemoryType.SawCrime);
            // The victim retaliates (OnHarmedBy, CCharFight.cpp:77).
            witness.OnHarmedBy(criminal);
        }

        if (!AI.NpcAI.NpcCanSpeak(witness))
            return; // I can't talk anyhow.

        criminal.MakeCriminal(mark);

        // m_pArea->IsGuarded() (CCharFight.cpp:86): guarded and not SAFE.
        if (world?.FindRegion(witness.Position)?.IsGuarded == true)
            OnNpcCallGuards?.Invoke(witness, criminal);
    }

    /// <summary>Source-X Calc_CrimeSeen (CResourceCalc.cpp:456): the victim gets
    /// chance + chance/3 with a 50/1000 floor; a bystander has a 10/1000 floor.</summary>
    private static bool CalcCrimeSeen(Character thief, Character viewer, SkillType? skill, Random rng,
        bool isVictim)
    {
        if (!skill.HasValue || skill.Value == SkillType.None)
            return true;

        if (viewer.PrivLevel >= PrivLevel.GM || thief.PrivLevel >= PrivLevel.GM)
        {
            if (viewer.PrivLevel < thief.PrivLevel) return false;
            if (viewer.PrivLevel > thief.PrivLevel) return true;
        }

        int chance = skill.Value is SkillType.Snooping or SkillType.Stealing
            ? 1000 + (viewer.GetSkill(skill.Value) - thief.GetSkill(skill.Value))
            : 400 + (viewer.Dex + viewer.Int) * 50;
        if (isVictim) chance += chance / 3;
        chance = Math.Max(isVictim ? 50 : 10, chance);
        return rng.Next(1000) <= chance; // fails only when GetVal(1000) > chance
    }
}
