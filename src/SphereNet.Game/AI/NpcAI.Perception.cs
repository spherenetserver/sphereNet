// Targeting and senses: motivation, hostility, attackability, sight.
// Decomposed from the former single-file NpcAI.cs (see NpcAI.cs core).
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Messages;
using SphereNet.Game.World;

namespace SphereNet.Game.AI;

public sealed partial class NpcAI
{
    /// <summary>Threat weight: how strongly the NPC should prefer a target based
    /// on the damage that target has dealt to it (from the attacker list).
    /// Bounded so a single heavy hitter dominates target choice without
    /// completely ignoring distance/other factors.</summary>
    internal static int GetThreatBonus(Character npc, Character target)
    {
        var attackers = npc.Attackers;
        for (int i = 0; i < attackers.Count; i++)
            if (attackers[i].Uid == target.Uid)
                return Math.Clamp(attackers[i].TotalDamage / 2, 0, 60);
        return 0;
    }

    /// <summary>Source-X NPC_GetAttackContinueMotivation morale: the SIGNED
    /// fight-motivation modifier from relative strength/health. Source-X does
    /// not clamp it — a strong, healthy NPC facing a weak target gains extra
    /// motivation (stronger target lock, more flee resistance); an outmatched
    /// one goes negative toward fleeing. Exposed for tests.</summary>
    internal static int GetMoralePenalty(Character npc, Character target)
    {
        int myHpPct = npc.MaxHits > 0 ? npc.Hits * 100 / npc.MaxHits : 100;
        int targetHpPct = target.MaxHits > 0 ? target.Hits * 100 / target.MaxHits : 100;
        return (npc.Str - target.Str) + (myHpPct - targetHpPct) - (npc.Int / 16);
    }

    /// <summary>Per-creature target-preference bias from the FIGHTMODE tag
    /// (Weakest/Strongest/Evil). Closest is the default (distance already drives
    /// motivation), so no tag = unchanged behavior.</summary>
    private static int FightModeBias(Character npc, Character target)
    {
        if (!npc.TryGetTag("FIGHTMODE", out string? mode) || string.IsNullOrEmpty(mode))
            return 0;
        int hpPct = target.MaxHits > 0 ? target.Hits * 100 / target.MaxHits : 100;
        return mode.Trim().ToLowerInvariant() switch
        {
            "weakest"   => (100 - hpPct) / 2,                                      // favor low HP
            "strongest" => Math.Clamp((target.Str + target.GetSkill(SkillType.Tactics) / 10) / 20, 0, 40),
            "evil"      => (target.IsCriminal || target.IsMurderer) ? 40 : 0,
            _ => 0,
        };
    }

    /// <summary>A target just hid/went invisible. Instead of dropping it
    /// instantly, move to its last known spot for a few ticks and try to Reveal
    /// it if the NPC can (ServUO mage reveal). Returns true while still pursuing,
    /// false to give up.</summary>
    private bool PursueHiddenTarget(Character npc, Character hidden)
    {
        int ticks = 5;
        if (npc.TryGetTag("HIDE_PURSUIT", out string? hp) && int.TryParse(hp, out int v))
            ticks = v;
        if (ticks <= 0) return false;
        npc.SetTag("HIDE_PURSUIT", (ticks - 1).ToString());

        Point3D lastLoc = npc.Position;
        if (npc.TryGetTag("LAST_TGT_LOC", out string? loc) && TryParsePoint(loc, out Point3D p))
            lastLoc = p;

        int dist = npc.Position.GetDistanceTo(lastLoc);
        // Near the last spot — try a Reveal (area around self) if available.
        if (dist <= 3 && npc.NpcSpells.Contains(SpellType.Reveal) && npc.Mana >= npc.Int / 4)
        {
            CastViaTrigger(npc, npc, SpellType.Reveal);
            return true;
        }
        if (dist > 1)
            MoveToward(npc, lastLoc);
        return true;
    }

    /// <summary>Find a wounded/poisoned friendly NPC within range to support.
    /// Allies are non-player creatures the NPC is not hostile toward. Picks the
    /// most-wounded (or a poisoned one).</summary>
    private Character? FindWoundedAlly(Character npc)
    {
        // Source-X builds the friend list only from chars ENGAGED WITH THE
        // SAME ENEMY (shared MEMORY_FIGHT) — a bystander ally not actually
        // fighting doesn't get combat heals.
        Serial sharedEnemy = npc.FightTarget;
        Character? best = null;
        int bestPct = 80; // only consider allies below 80% HP
        Character? poisoned = null;
        foreach (var ch in _world.GetCharsInRange(npc.Position, 8))
        {
            if (ch == npc || ch.IsDead || ch.IsDeleted || ch.IsPlayer) continue;
            if (ch.MaxHits <= 0) continue;
            if (sharedEnemy.IsValid && ch.FightTarget != sharedEnemy) continue;
            if (GetHostilityLevel(npc, ch) >= 0) continue; // not an ally
            int pct = ch.Hits * 100 / ch.MaxHits;
            if (ch.IsPoisoned) poisoned ??= ch;
            if (pct < bestPct)
            {
                bestPct = pct;
                best = ch;
            }
        }
        return best ?? poisoned;
    }

    /// <summary>
    /// Find the best target in sight range by motivation score.
    /// Source-X: NPC_LookAround → NPC_LookAtCharMonster loop.
    /// Only acquires targets within line of sight.
    /// </summary>
    private (Character? target, int motivation) FindBestTarget(Character npc, int sightRange)
    {
        // Rank candidates by motivation WITHOUT line-of-sight first: LOS
        // raycasts against the real static map are the dominant cost under
        // dense combat (one per candidate would be O(crowd) raycasts per NPC).
        // Keep the top 3, then verify LOS lazily on those — so each NPC does at
        // most a few raycasts regardless of crowd size.
        Character? t1 = null, t2 = null, t3 = null;
        int m1 = 0, m2 = 0, m3 = 0;
        foreach (var ch in _world.GetCharsInRange(npc.Position, sightRange))
        {
            if (ch == npc || !IsAttackable(ch)) continue;
            // Never cap by sector insertion order: a dense neutral/allied
            // crowd must not hide a valid enemy. Motivation is cheap; only the
            // best three candidates pay for LOS raycasts below.
            int motivation = GetAttackMotivation(npc, ch);
            if (motivation <= 0) continue;

            if (motivation > m1)
            {
                t3 = t2; m3 = m2; t2 = t1; m2 = m1; t1 = ch; m1 = motivation;
            }
            else if (motivation > m2)
            {
                t3 = t2; m3 = m2; t2 = ch; m2 = motivation;
            }
            else if (motivation > m3)
            {
                t3 = ch; m3 = motivation;
            }
        }

        if (t1 != null && _world.CanSeeLOS(npc.Position, t1.Position) && OnNpcLookAtChar?.Invoke(npc, t1) != true)
            return (t1, m1);
        if (t2 != null && _world.CanSeeLOS(npc.Position, t2.Position) && OnNpcLookAtChar?.Invoke(npc, t2) != true)
            return (t2, m2);
        if (t3 != null && _world.CanSeeLOS(npc.Position, t3.Position) && OnNpcLookAtChar?.Invoke(npc, t3) != true)
            return (t3, m3);

        // The three strongest candidates may all be behind a wall. Do not let
        // that hide a weaker but visible hostile: fall back to a record-best
        // visible scan. LOS is still lazy (only candidates that can improve
        // the current fallback pay for a raycast).
        Character? visibleFallback = null;
        int visibleMotivation = 0;
        foreach (var ch in _world.GetCharsInRange(npc.Position, sightRange))
        {
            if (ch == npc || ch == t1 || ch == t2 || ch == t3 || !IsAttackable(ch))
                continue;
            int motivation = GetAttackMotivation(npc, ch);
            if (motivation <= visibleMotivation)
                continue;
            if (!_world.CanSeeLOS(npc.Position, ch.Position))
                continue;
            if (OnNpcLookAtChar?.Invoke(npc, ch) == true)
                continue;
            visibleFallback = ch;
            visibleMotivation = motivation;
        }
        if (visibleFallback != null)
            return (visibleFallback, visibleMotivation);
        return (null, 0);
    }

    /// <summary>
    /// Source-X: NPC_GetAttackMotivation — computes how much this NPC wants to attack target.
    /// Returns &lt;0 to flee, 0 for no interest, &gt;0 to attack.
    /// </summary>
    private int GetAttackMotivation(Character npc, Character target)
    {
        // Cheap hostility test first. The overwhelming majority of candidates in
        // a crowd are non-hostile (own kind / townsfolk) and return here, so the
        // comparatively costly FindRegion spatial lookup is done only for a
        // genuinely hostile target — this is what keeps dense-crowd target scans
        // from becoming a per-candidate region query.
        // Source-X "element of surprise": a hidden melee NPC holds its ambush
        // until the prey is adjacent — unless it can cast from stealth.
        if (npc.IsStatFlag(StatFlag.Hidden) &&
            npc.Position.GetDistanceTo(target.Position) > 1 &&
            npc.NpcSpells.Count == 0 && FindNpcWand(npc) == null)
            return 0;

        // Source-X checks REGION_FLAG_SAFE FIRST (before hostility) — the old
        // order let a beloved ally inside a SAFE region return a negative
        // (flee) motivation instead of a clean neutral 0.
        var region = _world.FindRegion(target.Position);
        if (region != null && region.IsFlag(RegionFlag.Safe))
            return 0;

        int hostility = GetHostilityLevel(npc, target);
        if (hostility <= 0)
            return hostility;

        if (npc.NpcBrain == NpcBrainType.Guard)
            return 100;
        // Source-X berserk: NPC_GetAttackContinueMotivation adds +80 − dist on
        // top of the 100 hostility, so distance still breaks ties toward the
        // nearer foe (the flat 100 removed that preference).
        if (npc.NpcBrain == NpcBrainType.Berserk)
            return 100 + 80 - npc.Position.GetDistanceTo(target.Position);

        int motivation = hostility;

        // Bonus for current target (Source-X: +8 — the exact hysteresis
        // constant matters for the "better target must beat current" compare)
        if (npc.FightTarget == target.Uid)
            motivation += 8;

        // Distance penalty
        motivation -= npc.Position.GetDistanceTo(target.Position);

        // NOTE: no wounded/caster/healer/retaliation bonuses here — Source-X
        // NPC_GetAttackMotivation has none (motivation is hostility + same-
        // target hysteresis − distance + morale). Retaliation pressure comes
        // from the attacker-list THREAT term below.

        // Threat: stick to whoever has dealt the most damage to us (Source-X
        // NPC_AI_THREAT / NPC_FightFindBestTarget). Global flag, on by default;
        // drives tank/aggro mechanics off the accumulated attacker list.
        if (GetNpcFlags(npc).HasFlag(NpcAIFlags.Threat))
            motivation += GetThreatBonus(npc, target);

        // Optional per-creature FightMode bias (ServUO/ModernUO AcquireFocusMob:
        // Weakest/Strongest/Evil). Default (no tag) leaves distance-based
        // "closest" behavior unchanged.
        motivation += FightModeBias(npc, target);

        // Fear / morale (Source-X NPC_GetAttackContinueMotivation): the will
        // to fight scales with RELATIVE strength and health, not a fixed HP
        // threshold. A creature much weaker or more hurt than its target loses
        // motivation (and flees once total motivation drops below 0); a strong,
        // healthy NPC presses a weak target even while itself below half HP.
        // This is a signed Source-X modifier: it may either reinforce or erode
        // the hostility/threat score.
        if (_config.MonsterFear)
            motivation += GetMoralePenalty(npc, target);

        return motivation;
    }

    /// <summary>
    /// Source-X: NPC_GetHostilityLevelToward — hostility from alignment (karma),
    /// creature ally-group families, brain kinship and fight memories.
    /// 100=extreme hatred, 0=neutral, -100=love.
    /// </summary>
    private int GetHostilityLevel(Character npc, Character target)
    {
        // If target is a pet, evaluate hostility toward its owner (max 8 hops to prevent circular chains)
        var eval = target;
        for (int hops = 0; hops < 8 && eval.OwnerSerial.IsValid; hops++)
        {
            var owner = eval.ResolveOwnerCharacter();
            if (owner == null || owner == npc || owner == eval)
                break;
            eval = owner;
        }
        if (eval != target)
            target = eval;

        int hostility = 0;
        bool memBase = false;

        var npcRegion = _world.FindRegion(npc.Position);
        bool guarded = npcRegion != null && npcRegion.IsFlag(RegionFlag.Guarded);

        if (IsEvilForHostility(npc) && !guarded && target.IsPlayer)
        {
            // An evil creature hates every player outside guarded towns,
            // regardless of the player's karma.
            hostility = 51;
        }
        else if (npc.NpcBrain == NpcBrainType.Berserk)
        {
            hostility = 100; // berserk hates everyone all the time
        }
        else if (!target.IsPlayer && target.NpcBrain != NpcBrainType.Berserk && !_config.MonsterFight)
        {
            // MONSTERFIGHT off: monsters don't hunt other monsters; the low
            // base still lets fight memories flip it for self-defence.
            hostility = -50;
            memBase = true;
        }
        else
        {
            // Alignment: evil hates good karma, the virtuous hate the vile.
            int karmaTarg = target.Karma;
            if (IsEvilForHostility(npc))
            {
                if (karmaTarg > 0)
                    hostility += karmaTarg / 1024;
            }
            else if (npc.Karma > 300 && karmaTarg < -100)
            {
                hostility += -karmaTarg / 1024;
            }
        }

        if (!memBase)
        {
            // BodyId 0 (unset) must never match another unset body as "kin".
            bool bodiesKnown = npc.BodyId != 0 && target.BodyId != 0;
            if (!target.IsPlayer)
            {
                if (bodiesKnown && npc.BodyId == target.BodyId)
                    hostility -= 100;               // never attack my own kind
                else if (bodiesKnown && GetAllyGroup(npc.BodyId) == GetAllyGroup(target.BodyId))
                    hostility -= 50;                // same creature family (orc ↔ orc mage)
                else if (npc.NpcBrain == target.NpcBrain)
                    hostility -= 30;                // my basic kind
            }
            else if (bodiesKnown && !IsPlayableBody(npc.BodyId) &&
                     GetAllyGroup(npc.BodyId) == GetAllyGroup(target.BodyId))
            {
                // Source-X: only a NON-playable-bodied NPC softens toward a
                // player who shares its ally group (human townsfolk don't get
                // this — their friendliness comes from alignment/brain).
                hostility -= 51;
            }
        }

        // Grudges: prior fight/aggression memories push toward attack.
        if (npc.Memory_FindObjTypes(target.Uid,
                MemoryType.Fight | MemoryType.HarmedBy | MemoryType.IrritatedBy |
                MemoryType.SawCrime | MemoryType.Aggreived) != null)
        {
            hostility += 50;
            if (!target.IsPlayer && target.NpcBrain == NpcBrainType.Berserk)
                hostility += 60;
        }

        return hostility;
    }

    /// <summary>Source-X Noto_IsEvil for the hostility model. Monster/Dragon
    /// use karma &lt;= 0 (instead of the reference's &lt; 0) so legacy chardefs
    /// that omit KARMA keep their aggressive default.</summary>
    private static bool IsEvilForHostility(Character npc)
    {
        short karma = npc.Karma;
        return npc.NpcBrain switch
        {
            NpcBrainType.Monster or NpcBrainType.Dragon => karma <= 0,
            NpcBrainType.Berserk => true,
            NpcBrainType.Animal => karma <= -800,
            _ => karma <= -3000,
        };
    }

    /// <summary>Playable-character bodies (Source-X IsPlayableCharacter).</summary>
    internal static bool IsPlayableBody(ushort bodyId) => bodyId is
        0x190 or 0x191 or 0x192 or 0x193 or       // human + ghosts
        0x25D or 0x25E or 0x25F or 0x260 or       // elf + ghosts
        0x29A or 0x29B or 0x2B6 or 0x2B7;         // gargoyle + ghosts

    /// <summary>Source-X NPC_GetAllyGroupType — collapse body ids into creature
    /// families so an orc treats an orc captain/mage as kin. Ids from
    /// uofiles_enums_creid.h; unlisted bodies are their own group.</summary>
    internal static ushort GetAllyGroup(ushort bodyId) => bodyId switch
    {
        0x190 or 0x191 or 0x192 or 0x193 => 0x190, // human
        0x25D or 0x25E or 0x25F or 0x260 => 0x25D, // elf
        0x29A or 0x29B or 0x2B6 or 0x2B7 => 0x29A, // playable gargoyle
        0x01 or 0x53 or 0x87 => 0x01, // ogre
        0x02 or 0x12 => 0x02, // ettin
        0x35 or 0x36 or 0x37 => 0x36, // troll
        0x11 or 0x29 or 0x07 or 0x8A or 0x8B or 0x8C or
            0xB5 or 0xB6 or 0xBD => 0x11, // orc
        0x34 or 0x15 or 0x59 or 0x5A or 0x5C or 0x5D => 0x34, // snake
        0x09 or 0x0A or 0x2B or 0x66 => 0x09, // demon
        0x3C or 0x3D or 0x0C or 0x3B or 0x67 => 0x3C, // dragon/drake
        0x6A or 0xB4 or 0x31E => 0x6A, // wyrm
        0xC5 or 0x58B or 0x58C => 0xC5, // crimson dragon
        0xC6 or 0x589 or 0x58A => 0xC6, // platinum dragon
        0x33A or 0x58D or 0x58E => 0x33A, // stygian dragon
        0x31A or 0x31F => 0x31A, // swamp dragon
        0x508 or 0x50E => 0x508, // dragon turtle
        0x04 or 0x43 or 0x82 or 0x2D2 or 0x2F1 => 0x04, // creature gargoyle
        0x16 or 0x45 or 0x44 or 0x30A => 0x16, // gazer
        0x18 or 0x4F or 0x4E or 0x33E => 0x18, // lich
        0x21 or 0x23 or 0x24 => 0x21, // lizardman
        0x2A or 0x2C or 0x2D or 0x8E or 0x8F => 0x2A, // ratman
        0x1E or 0x49 => 0x1E, // harpy
        0x32 or 0x38 or 0x39 or 0x93 or 0x94 => 0x32, // skeleton
        0x03 or 0x9A or 0x99 => 0x03, // zombie/mummy/ghoul
        0x136 or 0x1A => 0x136, // banshee/spectre
        0x33 or 0x60 or 0x5E => 0x33, // slime
        0x5F or 0x96 or 0x91 or 0x90 => 0x5F, // sea creature
        0x50 or 0x51 => 0x50, // giant toad
        0x46 or 0x47 or 0x48 => 0x47, // terathan
        0x55 or 0x56 or 0x57 or 0x88 or 0x89 => 0x56, // ophidian
        0x2FC or 0x2FD or 0x2FE => 0x2FC, // juka
        0x302 or 0x303 or 0x304 or 0x305 => 0x302, // meer
        0x6B or 0x6C or 0x6D or 0x6E or 0x6F or 0x70 or 0x71 or 0xA6 => 0x6B, // ore elemental
        0x61 or 0x62 or 0x42D => 0x61, // hellhound
        0x7D or 0x7E => 0x7D, // evil mage
        0x30B or 0x30C => 0x30B, // bog creature
        0x30D or 0x30E or 0x30F or 0x327 => 0x30D, // solen
        0x325 or 0x326 or 0x324 or 0x328 => 0x325, // black solen
        0x31C or 0x3E7 or 0x308 => 0x31C, // horde demon
        0x107 or 0x118 or 0x42F or 0x119 or 0x106 => 0x107, // minotaur
        0x105 or 0x104 or 0x111 or 0x110 => 0x105, // effusion/essence
        0xF5 or 0xFD or 0xFF => 0xF5, // yomotsu
        0x317 or 0xA9 => 0x317, // giant/fire beetle
        0x2D3 or 0x2D4 => 0x2D3, // green goblin
        0x4E6 or 0x51D or 0x588 => 0x4E6, // tiger
        0x2CB or 0x1B0 => 0x2CB, // boura
        0x2D6 or 0x2D7 => 0x2D6, // kepetch
        0x2E0 or 0x2E1 => 0x2E0, // spider wolf
        0x2E8 or 0x2E9 => 0x2E8, // vampire
        0x4DC or 0x4DD => 0x4DC, // charybdis
        0x4DE or 0x4DF => 0x4DE, // pumpkin demon
        0x505 or 0x506 or 0x507 or 0x509 or 0x50A or 0x50C or 0x578 => 0x505, // dinosaurs
        0x50D or 0x57A or 0x57B or 0x57C => 0x50D, // myrmidex
        0x57E or 0x57D => 0x57E, // kotl
        0x05 or 0x06 or 0x11B or 0x11A => 0x06, // birds
        0xC8 or 0xCC or 0xE2 or 0xE4 or 0x123 or 0x75 or 0x72 or
            0x76 or 0x77 or 0x78 or 0x79 or 0xB1 or 0xB2 or 0xB3 or 0x580 => 0xC8, // horses
        0xA7 or 0xD4 or 0xD5 => 0xA7, // bears
        0xD8 or 0xE7 or 0xE8 or 0xE9 => 0xE8, // cows/bulls
        0xD2 or 0xDA or 0xDB => 0xDB, // ostards
        0xCF or 0xDF => 0xCF, // sheep
        0xEA or 0xED => 0xED, // deer
        0xCB or 0x122 => 0xCB, // pigs
        0xDC or 0x124 => 0xDC, // llamas
        0xEE or 0xD7 => 0xEE, // rats
        0x63 or 0x64 or 0xE1 => 0x64, // wolves
        0x85 or 0x86 => 0x85, // alligator/komodo
        0xBB or 0xBC => 0xBB, // ridgeback
        _ => bodyId,
    };

    /// <summary>
    /// Source-X: Fight_IsAttackable — checks if a character can be targeted.
    /// </summary>
    private static bool IsAttackable(Character ch)
    {
        if (ch.IsDeleted || ch.IsDead) return false;
        if (ch.IsStatFlag(StatFlag.Invul)) return false;
        if (ch.IsStatFlag(StatFlag.Stone)) return false;
        if (ch.IsStatFlag(StatFlag.Invisible)) return false;
        if (ch.IsStatFlag(StatFlag.Hidden)) return false;
        if (ch.IsStatFlag(StatFlag.Insubstantial)) return false;
        // A ridden mount has no world presence — hostiles must target the
        // RIDER. Targeting the mount produced an invisible one-sided fight
        // (the ridden NPC skips its AI tick, so it could never respond).
        if (ch.IsStatFlag(StatFlag.Ridden)) return false;
        return true;
    }

    /// <summary>
    /// NPC sight range. Source-X uses the per-character GetSight() value.
    /// VISUALRANGE=0 retains the engine's normal 18-tile fallback.
    /// </summary>
    internal static int GetNpcSight(Character npc) =>
        npc.VisualRange > 0 ? npc.VisualRange : 18;

    /// <summary>Source-X: SoundChar — emit a creature sound via callback.</summary>
    private void EmitSound(Character npc, CreatureSoundType type)
    {
        OnNpcSound?.Invoke(npc, type);
    }

    /// <summary>Callback: creature sound. Parameters: npc, sound type.
    /// Program.cs resolves body-specific sound ID and broadcasts 0x54.</summary>
    public Action<Character, CreatureSoundType>? OnNpcSound { get; set; }
}
