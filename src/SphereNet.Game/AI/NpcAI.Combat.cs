// Fight execution: monster brain, ActFight, swings, flee, flanking.
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
    /// <summary>Source-X NPC_OnTickAction: an engaged fight (the FightTarget
    /// assigned when someone attacks the NPC) continues REGARDLESS of brain —
    /// service NPCs defend themselves instead of ignoring their attacker, and
    /// animals fight back rather than only shying away. Fear still routes to
    /// flee via ActFight (negative motivation). Returns true when this tick
    /// was spent fighting or fleeing the assigned target.</summary>
    private bool TryFightAssignedTarget(Character npc)
    {
        if (!npc.FightTarget.IsValid)
            return false;
        var target = _world.FindChar(npc.FightTarget);
        if (target == null || target.IsDead || target.IsDeleted ||
            target.MapIndex != npc.MapIndex || !IsAttackable(target))
        {
            // Source-X Fight_HitTry (CCharFight.cpp:1503): a target I can no longer
            // hit does not end the fight — the next opponent comes straight off my
            // own attacker list, with the one I just lost excluded (:1508). Dropping
            // to a fresh look-around instead let an NPC that had just killed one of
            // three attackers stand idle among the other two.
            var replacement = FightFindBestTarget(npc, exclude: target);
            if (replacement != null && replacement != target)
            {
                npc.FightTarget = replacement.Uid;
                npc.Memory_Fight_Start(replacement);
                ActFight(npc, replacement, Math.Max(1, GetAttackMotivation(npc, replacement)));
                return true;
            }
            npc.FightTarget = Serial.Invalid;
            return false;
        }
        int motivation = GetAttackMotivation(npc, target);
        if (motivation == 0)
        {
            // Neutral score, but the target was assigned by an actual attack:
            // a service NPC still defends itself at minimum effort rather
            // than standing there taking hits.
            motivation = 1;
        }
        ActFight(npc, target, motivation);
        return true;
    }

    /// <summary>
    /// Monster/Dragon: look for targets to attack, fight, or wander.
    /// Source-X: NPC_Act_Idle → NPC_LookAround → NPC_LookAtCharMonster + NPC_Act_Fight.
    /// </summary>
    private void ActMonster(Character npc)
    {
        int sightRange = GetNpcSight(npc);

        // Forced target override (PROVOKED_TARGET / CONSTANT_FOCUS tags): not a
        // Source-X mechanism - no reference code or script pack sets or reads
        // these - so only under NPCAIEXTRAS CombatExtras.
        if (HasExtra(npc, NpcAiExtraFlags.CombatExtras) && TryForcedTarget(npc))
            return;

        bool hiddenPursuit = HasExtra(npc, NpcAiExtraFlags.HiddenPursuit);

        // If we have an existing target, check if it's still valid
        if (npc.FightTarget.IsValid)
        {
            var current = _world.FindChar(npc.FightTarget);
            if (current != null && !current.IsDead && !current.IsDeleted && IsAttackable(current))
            {
                // HiddenPursuit: remember where the target was while we can see
                // it, and clear any hidden-pursuit state.
                if (hiddenPursuit)
                {
                    npc.SetTag("LAST_TGT_LOC", $"{current.X},{current.Y},{current.Z},{current.MapIndex}");
                    npc.RemoveTag("HIDE_PURSUIT");
                }

                int curMotivation = GetAttackMotivation(npc, current);
                // Source-X NPC_Act_Fight keeps fighting at any motivation that is
                // not negative (only iMotivation < 0 flees, CCharNPCAct_Fight.cpp:
                // 259-278); a neutral score does not end an engaged fight.
                if (curMotivation >= 0)
                {
                    // Mid-fight better-target rescan (NPCAIEXTRAS FightRescan).
                    // Source-X keeps this DISABLED ("probably unnecessary… breaks
                    // the @NPCActFight trigger", CCharNPCAct_Fight.cpp:178-194);
                    // with the extra it runs only under multi-attacker pressure.
                    if (HasExtra(npc, NpcAiExtraFlags.FightRescan) &&
                        npc.Attackers.Count > 1 && _rand.Next(4) == 0)
                    {
                        // Off the ATTACKER LIST, not a fresh look-around: this is the
                        // switch THREAT exists for, and a look-around scores by
                        // distance and hostility, which is exactly what a script
                        // raising an attacker's threat is trying to override.
                        var betterTarget = FightFindBestTarget(npc);
                        if (betterTarget != null && !betterTarget.IsDeleted && betterTarget != current)
                        {
                            npc.FightTarget = betterTarget.Uid;
                            npc.Memory_Fight_Start(betterTarget);
                            current = betterTarget;
                            curMotivation = Math.Max(1, GetAttackMotivation(npc, betterTarget));
                        }
                    }

                    ActFight(npc, current, curMotivation);
                    return;
                }
                // Negative motivation = fear: flee from the target instead of
                // silently dropping it (Source-X NPC_LookAtChar fear path).
                // ActFight routes motivation < 0 into ActFlee.
                ActFight(npc, current, curMotivation);
                return;
            }
            else if (hiddenPursuit && current != null && !current.IsDead && !current.IsDeleted &&
                     (current.IsStatFlag(StatFlag.Hidden) || current.IsStatFlag(StatFlag.Invisible)))
            {
                // NPCAIEXTRAS HiddenPursuit (ServUO reveal behavior): a target that
                // hid is not given up at once - the NPC tries to reveal it, or moves
                // to its last known spot for a few ticks.
                if (PursueHiddenTarget(npc, current))
                    return;
            }
            // Before sweeping the whole sight range, take the next opponent off
            // the attacker list the way the reference does (NPC_FightFindBestTarget).
            var fromList = FightFindBestTarget(npc, exclude: current);
            if (fromList != null && fromList != current)
            {
                npc.FightTarget = fromList.Uid;
                if (hiddenPursuit)
                {
                    npc.RemoveTag("HIDE_PURSUIT");
                    npc.RemoveTag("LAST_TGT_LOC");
                }
                npc.Memory_Fight_Start(fromList);
                ActFight(npc, fromList, Math.Max(1, GetAttackMotivation(npc, fromList)));
                return;
            }

            npc.FightTarget = Serial.Invalid;
            if (hiddenPursuit)
            {
                npc.RemoveTag("HIDE_PURSUIT");
                npc.RemoveTag("LAST_TGT_LOC");
            }
        }

        // No current target — scan for a new one. NPCAIEXTRAS FightRescan throttles
        // the full-range scan so idle NPCs don't sweep every tick (ModernUO
        // ReacquireDelay; RecordAttack zeroes NextNpcReacquireTime so retaliation
        // is immediate). Source-X looks around on every idle tick (NPC_LookAround).
        bool reacquireThrottle = HasExtra(npc, NpcAiExtraFlags.FightRescan);
        long nowReac = Environment.TickCount64;
        if (!reacquireThrottle || nowReac >= npc.NextNpcReacquireTime)
        {
            var (bestTarget, bestMotivation) = FindBestTarget(npc, sightRange);
            if (bestTarget != null && bestMotivation > 0)
            {
                npc.NextNpcReacquireTime = 0;
                npc.FightTarget = bestTarget.Uid;
                npc.Memory_Fight_Start(bestTarget);
                EmitSound(npc, CreatureSoundType.Notice);
                if (HasExtra(npc, NpcAiExtraFlags.AllyRally))
                    NotifyNearbyAllies(npc, bestTarget);
                ActFight(npc, bestTarget, bestMotivation);
                return;
            }
            // Nothing found — back off the next scan.
            if (reacquireThrottle)
                npc.NextNpcReacquireTime = nowReac + ReacquireDelayMs;
        }

        npc.FightTarget = Serial.Invalid;

        // No enemy — looters scavenge nearby corpses (Source-X NPC_AI_LOOTING).
        if ((GetNpcFlags(npc).HasFlag(NpcAIFlags.Looting) || npc.TryGetTag("LOOTING", out _))
            && TryLoot(npc))
            return;

        // Idle monsters notice desirable ground items too (Source-X
        // NPC_LookAtItem runs for every brain, not just humans).
        LookAtNearbyItems(npc);

        if (_rand.Next(8) == 0)
            EmitSound(npc, _rand.Next(2) == 0 ? CreatureSoundType.Idle : CreatureSoundType.Notice);
        WanderHome(npc);
    }

    /// <summary>Source-X CChar::NPC_LootMemory (CCharNPCAct.cpp:1574): remember an item
    /// looked at and discarded (a MEMORY_SPEAK memory linked to it), for as long as
    /// the item itself has left before it decays. A corpse the NPC remembers is not
    /// looted again (NPC_LookAtItem, CCharNPCAct.cpp:974).</summary>
    internal static Item NpcLootMemory(Character npc, Item item)
    {
        var mem = npc.Memory_AddObjTypes(item.Uid, MemoryType.Speak);
        long now = Environment.TickCount64;
        long deadline = item.Timeout > now ? item.Timeout
            : item.DecayTime > now ? item.DecayTime : 0;
        if (deadline > 0)
            mem.SetTimeout(deadline); // forget about it once the item is gone
        return mem;
    }

    /// <summary>Looter NPCs walk to a nearby corpse with contents and take one
    /// item into their pack (Source-X NPC_Act_Looting). Empty corpses are
    /// skipped, and so is a corpse the NPC remembers (NPC_LootMemory).
    /// Returns true if busy.</summary>
    private bool TryLoot(Character npc)
    {
        if (npc.Backpack == null) return false;
        if (npc.Backpack.Contents.Count >= Item.MaxContainerItems) return false;
        // Source-X looting guards: hands required, and never inside guarded
        // or safe territory. (Summons leave no corpse in this engine, so the
        // reference's summon-corpse exclusion has nothing to act on.)
        var lootCan = CharDefHelper.GetCanFlags(npc);
        if ((lootCan & CanFlags.C_UseHands) == 0) return false;
        if (IsProtectedGround(npc.Position)) return false;

        Item? corpse = null;
        int best = int.MaxValue;
        foreach (var it in _world.GetItemsInRange(npc.Position, 4))
        {
            if (it.IsDeleted || it.ItemType != ItemType.Corpse || it.Contents.Count == 0) continue;
            // Already looked at (Memory_FindObj, CCharNPCAct.cpp:974).
            if (npc.Memory_FindObj(it.Uid) != null) continue;
            if (!_world.CanSeeLOS(npc.Position, it.Position)) continue;
            int d = npc.Position.GetDistanceTo(it.Position);
            if (d < best) { best = d; corpse = it; }
        }
        if (corpse == null) return false;

        if (best > 1)
        {
            MoveToward(npc, corpse.Position);
            return true;
        }

        // Adjacent — grab one random item. @NPCLookAtItem sees ARGN1=dist,
        // ARGN2=want (seeded 100 — the engine already decided to loot; a
        // script may lower it below the roll to skip, RETURN 1 to take over
        // or RETURN 0 to leave the piece alone).
        if (corpse.Contents.Count > 0)
        {
            var loot = corpse.Contents[_rand.Next(corpse.Contents.Count)];
            // Out of reach for this creature: remember it and move on
            // (CanMoveItem / CanCarry -> NPC_LootMemory, CCharNPCAct.cpp:1623).
            if (!ItemMoveRules.CanMove(npc, loot, out _) || !npc.CanCarry(loot))
            {
                NpcLootMemory(npc, loot);
                return true;
            }
            int want = 100;
            if (OnNpcLookAtItem != null && !IsLookAtItemExcluded(loot))
            {
                var d = OnNpcLookAtItem(npc, loot, best, 100);
                if (d.Handled || d.Ignore)
                    return true;
                want = d.Want;
            }
            if (want > _rand.Next(100))
            {
                corpse.RemoveItem(loot);
                if (!npc.Backpack.TryAddItem(loot))
                    corpse.TryAddItem(loot);
            }
        }
        return true;
    }

    /// <summary>Berserk: attack nearest visible character (hostile to everyone).</summary>
    private void ActBerserk(Character npc)
    {
        int sightRange = GetNpcSight(npc);

        if (npc.FightTarget.IsValid)
        {
            var current = _world.FindChar(npc.FightTarget);
            if (current != null && !current.IsDead && !current.IsDeleted && IsAttackable(current))
            {
                ActFight(npc, current, 100);
                return;
            }
            npc.FightTarget = Serial.Invalid;
        }

        Character? nearest = null;
        int nearestDist = int.MaxValue;

        foreach (var ch in _world.GetCharsInRange(npc.Position, sightRange))
        {
            if (ch == npc || !IsAttackable(ch)) continue;
            if (!_world.CanSeeLOS(npc.Position, ch.Position)) continue;
            int dist = npc.Position.GetDistanceTo(ch.Position);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = ch;
            }
        }

        if (nearest != null)
        {
            npc.FightTarget = nearest.Uid;
            npc.Memory_Fight_Start(nearest);
            ActFight(npc, nearest, 100);
            return;
        }

        npc.FightTarget = Serial.Invalid;
        if (_rand.Next(6) == 0)
            EmitSound(npc, CreatureSoundType.Idle);
        WanderHome(npc);
    }

    /// <summary>
    /// NPCAIEXTRAS AllyRally: alert nearby NPCs of the same body to join the fight.
    /// Source-X has no such rally - each NPC finds its own targets in
    /// NPC_LookAround - so this runs only with the extra on.
    /// </summary>
    private void NotifyNearbyAllies(Character npc, Character target)
    {
        foreach (var ally in _world.GetCharsInRange(npc.Position, 8))
        {
            if (ally == npc || ally.IsPlayer || ally.IsDead || ally.IsDeleted) continue;
            if (ally.FightTarget.IsValid) continue;
            if (ally.NpcMaster.IsValid) continue;
            if (ally.BodyId != npc.BodyId) continue;
            if (ally.NpcBrain is not (NpcBrainType.Monster or NpcBrainType.Dragon or NpcBrainType.Berserk))
                continue;

            ally.FightTarget = target.Uid;
            ally.NextNpcActionTime = 0;
            OnWakeNpc?.Invoke(ally);
        }
    }

    /// <summary>
    /// Shared fight action. Source-X: NPC_Act_Fight — flee / special / spell / archery / melee.
    /// </summary>
    private void ActFight(Character npc, Character target, int motivation)
    {
        if (npc.MapIndex != target.MapIndex)
        {
            npc.FightTarget = Serial.Invalid;
            return;
        }

        // A committed swing is the NPC's action while its animation plays: a
        // winding-up NPC must not cast, breathe, throw, move or sidestep before
        // the blow lands. Once the animation is over and the blow is still held
        // (the target stepped out of reach), the NPC goes back to its fight AI so
        // it can close in - Source-X's WAR_SWING_READY hold keeps the NPC's AI
        // alive for exactly that (Fight_HitTry, CCharFight.cpp:1614-1626); a new
        // swing cannot start while the held one is pending.
        if (npc.HasPendingHit)
        {
            long pendingNow = Environment.TickCount64;
            if (pendingNow < npc.SwingHitTime)
                return;
            ResolveNpcHit(npc, pendingNow);
            if (npc.HasPendingHit &&
                npc.Position.GetDistanceTo(target.Position) > GetAttackRange(npc))
                MoveToward(npc, target.Position, run: true);
            return;
        }

        ScrubLegacyFightTags(npc);

        // An NPC already running away keeps running (Source-X NPCACT_FLEE is its own
        // skill: while it runs, NPC_Act_Flee is ticked instead of NPC_Act_Fight,
        // CCharNPCAct.cpp:1645-1662), so the step count really reaches its limit.
        if (npc.FleeStepsCurrent > 0 && npc.FleeStepsCurrent < npc.FleeStepsMax &&
            !npc.IsStatFlag(StatFlag.Pet))
        {
            if (!ActFlee(npc, target))
                npc.FleeStepsCurrent = 0; // Skill_Start(SKILL_NONE)
            return;
        }
        npc.FleeStepsCurrent = 0;

        // @NPCActFight (CCharNPCAct_Fight.cpp:223-257): RETURN 1 = the script did the
        // action; RETURN 0 = fSkipHardcoded - keep flee/magery/melee but skip the
        // breath/throw specials; a fall-through may force a skill (LOCAL.skill /
        // LOCAL.spell) and rewrites the distance and motivation (ARGN1 / ARGN2).
        bool skipHardcoded = false;
        if (OnNpcActFight != null)
        {
            int dist0 = npc.Position.GetDistanceTo(target.Position);
            var decision = OnNpcActFight(npc, target, dist0, motivation);
            if (decision.Handled)
                return; // RETURN 1 — script fully handled the fight action
            // LOCAL.skill + LOCAL.spell forced cast (Source-X magic-skill path):
            // a forced spell is cast at the target; a forced non-cast skill yields
            // this action to the script without the engine's default combat.
            if (decision.ForcedSpell != SpellType.None)
            {
                if (CastViaTrigger(npc, target, decision.ForcedSpell))
                    return;
            }
            if (decision.ForcedSkill != SkillType.None)
                return;
            skipHardcoded = decision.SkipHardcoded;
            motivation = decision.Motivation; // ARGN2 readback (may flip to flee)
        }

        // Source-X: flee when motivation < 0 (non-pets only). A flee that cannot even
        // start - the enemy is out of sight, or already out of reach - leaves the
        // fight: war mode off, the attacker forgotten, no target
        // (CCharNPCAct_Fight.cpp:259-278).
        if (!npc.IsStatFlag(StatFlag.Pet) && motivation < 0)
        {
            npc.FleeStepsMax = 20; // Source-X CCharNPCAct.cpp:412 (m_atFlee.m_iStepsMax)
            npc.FleeStepsCurrent = 0;
            if (!ActFlee(npc, target))
            {
                npc.FleeStepsCurrent = 0;
                npc.ClearStatFlag(StatFlag.War);
                npc.Attacker_Delete(target.Uid);
                npc.FightTarget = Serial.Invalid;
            }
            return;
        }

        // Combat pursuit is relative to the target, not the spawn/home radius.
        // Source-X NPC_Act_Follow keeps HOMEDIST in idle/go-home behavior.
        if (!_lastAttackNotify.TryGetValue(npc.Uid.Value, out uint lastNotified) ||
            lastNotified != target.Uid.Value)
        {
            _lastAttackNotify[npc.Uid.Value] = target.Uid.Value;
            OnNpcAttackNotify?.Invoke(npc, target);
        }

        int dist = npc.Position.GetDistanceTo(target.Position);
        bool hasLOS = _world.CanSeeLOS(npc.Position, target.Position);

        // No line of sight: walk round to the target (NPC_Act_Follow's path search).
        if (!hasLOS && dist > 1)
        {
            if (!CanContinueCombatPursuit(npc, target)) return;
            if (HasExtra(npc, NpcAiExtraFlags.LosRecovery) && TryLosRecovery(npc, target))
                return;
            MoveToward(npc, target.Position, run: true);
            return;
        }
        ClearLosFailCount(npc);

        // NPCAIEXTRAS LowHpRetreat: a melee-only NPC briefly disengages when
        // critically wounded, then re-engages.
        if (HasExtra(npc, NpcAiExtraFlags.LowHpRetreat) &&
            npc.NpcSpells.Count == 0 && npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 4
            && dist <= 1 && _rand.Next(3) == 0)
        {
            MoveAway(npc, target.Position);
            EmitSound(npc, CreatureSoundType.GetHit);
            return;
        }

        // NPCAIEXTRAS BandageHeal: an NPC that knows Healing and carries bandages
        // treats itself before anything else.
        if (HasExtra(npc, NpcAiExtraFlags.BandageHeal) && TryBandage(npc, npc))
            return;

        // Random idle combat sound (Source-X: Berserk or 1/6 chance)
        if (npc.NpcBrain == NpcBrainType.Berserk || _rand.Next(6) == 0)
            EmitSound(npc, CreatureSoundType.Idle);

        bool combatExtras = HasExtra(npc, NpcAiExtraFlags.CombatExtras);

        // Breath and throw need FULL stamina (Stat_GetVal(STAT_DEX) >=
        // Stat_GetAdjusted(STAT_DEX), CCharNPCAct_Fight.cpp:282), so these specials
        // fire on the opening exchange / after a rest, not every tick.
        bool fullStam = npc.MaxStam <= 0 || npc.Stam >= npc.MaxStam;
        if (!skipHardcoded && fullStam && TryBreath(npc, target, dist, hasLOS, combatExtras))
            return;
        if (!skipHardcoded && fullStam && TryThrow(npc, target, dist, hasLOS, combatExtras))
            return;

        // NPC spellcasting — requires LOS for ranged spells
        if (hasLOS && TryNpcCastSpell(npc, target, dist))
            return;

        // Melee / ranged
        var weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        var range = GetFightRange(npc, weapon);

        // Ranged kiting (Source-X NPC_FightArchery): when the target has closed
        // inside the weapon's minimum range a ranged attacker cannot fire, so
        // back off to reopen the gap (≈50%) instead of standing locked in melee.
        if (range.Max > 1 && dist < range.Min)
        {
            if (_rand.Next(2) == 0)
                MoveAway(npc, target.Position);
            return;
        }

        bool surround = HasExtra(npc, NpcAiExtraFlags.SurroundFlank);

        // COMBAT_SWING_NORANGE: a swing may start even when out of range.
        if (dist <= range.Max || CombatHelper.SwingIgnoresStartRange())
        {
            // NPCAIEXTRAS SurroundFlank: sidesteps only happen on ticks where the
            // swing did NOT fire (recoil window). Moving in the same tick as a
            // fired swing makes the client cancel the attack animation —
            // the same move-pair conflict class as the pet GO/follow bug.
            bool swung = TrySwingAttack(npc, target);
            if (surround && !swung && dist <= 1 && _rand.Next(3) == 0)
                TrySurroundStep(npc, target);
        }
        else
        {
            if (!CanContinueCombatPursuit(npc, target)) return;
            // NPCAIEXTRAS SurroundFlank: approach from an open flank.
            if (surround && dist <= 3)
                MoveTowardFlank(npc, target);
            else
                MoveToward(npc, target.Position, run: true);
        }
    }

    /// <summary>NPCAIEXTRAS LosRecovery for a target out of sight: switch to a foe on
    /// the attacker list that is in sight; else count the failure - a caster that
    /// knows Teleport blinks toward the target once stuck for a while (ModernUO
    /// OnFailedMove smart-AI), and after 15 failures the target is dropped. True =
    /// this tick was spent.</summary>
    private bool TryLosRecovery(Character npc, Character target)
    {
        var visible = FightFindBestTarget(npc, exclude: target);
        if (visible != null && visible != target && !visible.IsDead &&
            visible.MapIndex == npc.MapIndex && _world.CanSeeLOS(npc.Position, visible.Position))
        {
            ClearLosFailCount(npc);
            npc.FightTarget = visible.Uid;
            npc.Memory_Fight_Start(visible);
            ActFight(npc, visible, Math.Max(1, GetAttackMotivation(npc, visible)));
            return true;
        }

        IncrementLosFailCount(npc);
        int losFails = GetLosFailCount(npc);
        if (losFails >= 8 && npc.NpcSpells.Contains(SpellType.Teleport)
            && npc.Mana >= npc.Int / 4 && _rand.Next(3) == 0)
        {
            ClearLosFailCount(npc);
            CastViaTrigger(npc, target, SpellType.Teleport);
            return true;
        }
        if (losFails > 15)
        {
            npc.FightTarget = Serial.Invalid;
            ClearLosFailCount(npc);
            return true;
        }
        return false;
    }

    /// <summary>NPCACT_BREATH (CCharNPCAct_Fight.cpp:286-295): a DRAGON-brain NPC
    /// breathes at a target 1 to 8 tiles away in sight, on full stamina; the breath
    /// itself costs 10 stamina (Skill_Act_Breath, CCharSkill.cpp:3280). No
    /// cooldown: the stamina gate is what spaces breaths out. NPCAIEXTRAS
    /// CombatExtras widens who breathes - dragon-family bodies (packs that keep
    /// brain_monster on dragons), fire-immune monsters, any BREATH.DAM tag - and
    /// adds a three second cooldown.</summary>
    private bool TryBreath(Character npc, Character target, int dist, bool hasLOS, bool combatExtras)
    {
        bool canBreath = npc.NpcBrain == NpcBrainType.Dragon;
        if (!canBreath && combatExtras)
        {
            canBreath = IsDragonBody(npc.BodyId) ||
                        ((CharDefHelper.GetCanFlags(npc) & CanFlags.C_FireImmune) != 0 &&
                         npc.NpcBrain is NpcBrainType.Monster or NpcBrainType.Berserk) ||
                        npc.TryGetTag("BREATH.DAM", out _);
        }
        // Source-X: iDist >= 1 — no breath onto the overlapping tile.
        if (!canBreath || dist < 1 || dist > 8 || !hasLOS)
            return false;

        long now = Environment.TickCount64;
        if (combatExtras && now < FightMemory(npc).BreathReadyAt)
            return false;
        int breathDmg = GetBreathDamage(npc);
        if (breathDmg <= 0)
            return false;
        npc.Stam = (short)Math.Max(0, npc.Stam - 10);
        if (combatExtras)
            FightMemory(npc).BreathReadyAt = now + BreathCooldownMs;
        OnNpcBreath?.Invoke(npc, target, breathDmg);
        return true;
    }

    /// <summary>CombatExtras breath cooldown (the three seconds Skill_Act_Breath
    /// spends in its start stage, CCharSkill.cpp:3284).</summary>
    private const int BreathCooldownMs = 3000;

    /// <summary>NPCACT_THROWING (CCharNPCAct_Fight.cpp:297-340): within THROWRANGE
    /// (default 2-9) and in sight, an ogre/ettin/cyclops body or a creature with a
    /// THROWOBJ throws - but only while it CARRIES the missile: an IT_AROCK for
    /// the default throwers, an item of the THROWOBJ definition otherwise. Costs
    /// 4 + rand(6) stamina (CCharSkill.cpp:3372). NPCAIEXTRAS CombatExtras keeps
    /// the older wider rule: a THROWOBJ tag alone arms a thrower, and a plain rock
    /// pile counts as a rock.</summary>
    private bool TryThrow(Character npc, Character target, int dist, bool hasLOS, bool combatExtras)
    {
        if (dist < 2 || !hasLOS)
            return false;
        bool throwObjTag = npc.TryGetTag("THROWOBJ", out _);
        bool armed = throwObjTag
            ? combatExtras || CarriesThrowObj(npc)
            : IsRockThrowerBody(npc.BodyId) && HasThrowableRock(npc, acceptPlainRock: combatExtras);
        if (!armed)
            return false;

        // Source-X Skill_Act_Throwing default damage (CCharSkill.cpp:3447).
        int throwDmg = Math.Max(1, npc.Dex / 4 + _rand.Next(npc.Dex / 4 + 1));
        int throwMin = 2, throwMax = 9;
        if (npc.TryGetTag("THROWRANGE", out string? trStr) && !string.IsNullOrWhiteSpace(trStr))
        {
            var parts = trStr.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int mn) && int.TryParse(parts[1], out int mx))
            {
                throwMin = Math.Max(0, Math.Min(mn, mx));
                throwMax = Math.Max(throwMin, Math.Max(mn, mx));
            }
            else if (int.TryParse(parts[0], out int single))
                throwMax = Math.Max(throwMin, single);
        }
        if (npc.TryGetTag("THROWDAM", out string? tdStr) && !string.IsNullOrWhiteSpace(tdStr))
        {
            var parts = tdStr.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int lo) && int.TryParse(parts[1], out int hi))
            {
                int minDamage = Math.Max(0, Math.Min(lo, hi));
                int maxDamage = Math.Max(minDamage, Math.Max(lo, hi));
                throwDmg = minDamage == maxDamage
                    ? minDamage
                    : (int)_rand.NextInt64(minDamage, (long)maxDamage + 1);
            }
            else if (int.TryParse(parts[0], out int flat))
                throwDmg = Math.Max(0, flat);
        }
        if (dist < throwMin || dist > throwMax)
            return false;
        npc.Stam = (short)Math.Max(0, npc.Stam - (4 + _rand.Next(6)));
        OnNpcThrow?.Invoke(npc, target, throwDmg);
        return true;
    }

    /// <summary>Source-X NPC_GetWeaponUseScore (CCharNPCStatus.cpp:671-697): how good
    /// this NPC would be with a weapon - its adjusted weapon skill plus fifty times
    /// the damage roll (Fight_CalcDamage). A non-weapon, or one needing wrestling,
    /// scores 0; <paramref name="weapon"/> null scores bare hands.</summary>
    internal int GetWeaponUseScore(Character npc, Item? weapon)
    {
        SkillType skill;
        if (weapon == null)
        {
            skill = SkillType.Wrestling;
        }
        else
        {
            if (!IsWeaponItemType(weapon.ItemType))
                return 0;
            skill = CombatEngine.GetWeaponSkill(npc, weapon);
            if (skill == SkillType.Wrestling)
                return 0;
        }
        var (lo, hi) = CombatEngine.CalcWeaponDamage(npc, weapon);
        int dmg = hi > lo ? lo + _rand.Next(hi - lo + 1) : lo;
        int skillLevel = SphereNet.Game.Skills.SkillEngine.GetAdjustedSkill(npc, skill);
        return skillLevel + dmg * 50;
    }

    /// <summary>Source-X ItemEquipWeapon (CCharUse.cpp:2049-2077): the pack weapon
    /// with the best <see cref="GetWeaponUseScore"/>, if it beats bare hands; null
    /// when nothing does. Used by the NPC_AI_EXTRA war-mode equip pass.</summary>
    internal Item? FindBestPackWeapon(Character npc)
    {
        var pack = npc.Backpack;
        if (pack == null)
            return null;
        Item? best = null;
        int bestScore = GetWeaponUseScore(npc, null);
        foreach (var it in pack.Contents)
        {
            if (it.IsDeleted) continue;
            int score = GetWeaponUseScore(npc, it);
            if (score > bestScore)
            {
                bestScore = score;
                best = it;
            }
        }
        return best;
    }

    private bool CanContinueCombatPursuit(Character npc, Character target)
    {
        // Source-X NPC_Act_Follow retains the opponent of an immobile NPC.
        if ((CharDefHelper.GetCanFlags(npc) & CanFlags.C_NonMover) != 0)
            return false;
        int radar = _config.MapViewRadar > 0 ? _config.MapViewRadar : Character.MapViewRadarTiles;
        if (npc.Position.GetDistanceTo(target.Position) <= Math.Max(1, radar)) return true;
        npc.FightTarget = Serial.Invalid;
        ClearLosFailCount(npc);
        return false;
    }

    /// <summary>
    /// NPCAIEXTRAS SurroundFlank: step to an open adjacent tile around the target
    /// to surround it. Picks a random unoccupied, unreserved neighbour of the
    /// target that is still in melee range and reserves it for this tick.
    /// </summary>
    private void TrySurroundStep(Character npc, Character target)
    {
        Span<(short x, short y)> candidates = stackalloc (short, short)[8];
        int count = 0;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                short tx = (short)(target.X + dx);
                short ty = (short)(target.Y + dy);
                if (tx == npc.X && ty == npc.Y) continue;

                // A surround step must be a single tile from the NPC's current
                // position. Tiles around the target on the far side are 2 tiles
                // away from the NPC; moving there in one step is not a walk the
                // client can animate, so it teleports the NPC. Only keep tiles
                // adjacent (Chebyshev <= 1) to the NPC.
                if (Math.Abs(tx - npc.X) > 1 || Math.Abs(ty - npc.Y) > 1)
                    continue;

                bool occupied = false;
                foreach (var ch in _world.GetCharsInRange(new Point3D(tx, ty, target.Z, target.MapIndex), 0))
                {
                    if (!ch.IsDead && ch != target) { occupied = true; break; }
                }
                if (!occupied && !IsTileReservedByOther(npc, tx, ty))
                    candidates[count++] = (tx, ty);
            }
        }

        if (count == 0) return;

        var pick = candidates[_rand.Next(count)];
        var mapData = _world.MapData;
        sbyte nz = ResolveNpcStepZ(npc, pick.x, pick.y);
        if (Math.Abs(nz - npc.Z) > 12) return;
        var pos = new Point3D(pick.x, pick.y, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, pos)) return;

        // Face the step direction before moving. The 0x77 move packet carries this
        // direction; if it doesn't match the actual tile delta the client can't
        // walk-animate and snaps the NPC ("1-tile teleport" during combat
        // surround steps). The next swing re-faces the target via TrySwingAttack.
        npc.Direction = npc.Position.GetDirectionTo(pos);
        ReserveTile(npc, pick.x, pick.y);
        _world.MoveCharacter(npc, pos);
    }

    /// <summary>
    /// NPCAIEXTRAS SurroundFlank: move toward the target but prefer an unoccupied,
    /// unreserved flank tile, reserving the one chosen.
    /// </summary>
    private void MoveTowardFlank(Character npc, Character target)
    {
        // Check which sides of the target are already occupied by allies
        var dir = npc.Position.GetDirectionTo(target.Position);
        int dirInt = (int)dir & 0x07;

        // Try clockwise and counter-clockwise rotations to find an open approach
        for (int rot = 0; rot <= 2; rot++)
        {
            foreach (int sign in rot == 0 ? new[] { 0 } : new[] { 1, -1 })
            {
                int tryDir = (dirInt + sign * rot) & 0x07;
                GetDirectionDelta((Direction)tryDir, out short dx, out short dy);
                short adjX = (short)(target.X - dx);
                short adjY = (short)(target.Y - dy);

                bool occupied = false;
                foreach (var ch in _world.GetCharsInRange(
                    new Point3D(adjX, adjY, target.Z, target.MapIndex), 0))
                {
                    if (!ch.IsDead && ch != npc && ch != target) { occupied = true; break; }
                }

                if (!occupied && !IsTileReservedByOther(npc, adjX, adjY))
                {
                    ReserveTile(npc, adjX, adjY);
                    var approachPos = new Point3D(adjX, adjY, target.Z, target.MapIndex);
                    MoveToward(npc, approachPos, run: true);
                    return;
                }
            }
        }

        // All flanks occupied, just go direct
        MoveToward(npc, target.Position, run: true);
    }

    /// <summary>Tiles around a target that a SurroundFlank attacker has claimed for
    /// the current tick (after ModernUO's per-tick move reservation), so two
    /// attackers deciding in the same serial apply phase do not pick the same
    /// tile. Keyed by map and position; an entry lives one tick window and is
    /// never saved.</summary>
    private readonly Dictionary<(byte Map, short X, short Y), (uint Npc, long Until)> _reservedTiles = [];

    private const int TileReservationMs = 250;

    private bool IsTileReservedByOther(Character npc, short x, short y)
    {
        return _reservedTiles.TryGetValue((npc.MapIndex, x, y), out var r) &&
               r.Npc != npc.Uid.Value && r.Until > Environment.TickCount64;
    }

    private void ReserveTile(Character npc, short x, short y)
    {
        long now = Environment.TickCount64;
        if (_reservedTiles.Count > 512)
        {
            foreach (var key in _reservedTiles.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList())
                _reservedTiles.Remove(key);
        }
        _reservedTiles[(npc.MapIndex, x, y)] = (npc.Uid.Value, now + TileReservationMs);
    }

    private readonly Dictionary<uint, int> _losFailCounts = [];

    private void IncrementLosFailCount(Character npc)
    {
        _losFailCounts.TryGetValue(npc.Uid.Value, out int count);
        _losFailCounts[npc.Uid.Value] = count + 1;
    }

    private int GetLosFailCount(Character npc)
    {
        _losFailCounts.TryGetValue(npc.Uid.Value, out int count);
        return count;
    }

    private void ClearLosFailCount(Character npc)
    {
        _losFailCounts.Remove(npc.Uid.Value);
    }

    /// <summary>Source-X NPC_Act_Flee (CCharNPCAct.cpp:1645-1662): a step-counted
    /// retreat. False = the flee failed (NPC_Act_Follow(true, stepsMax) gave up):
    /// the step limit was reached, the enemy is out of sight, it is already
    /// stepsMax tiles away or past the radar (CCharNPCAct.cpp:1382-1420), or no
    /// step away could be taken. NPCAIEXTRAS FleeTactics adds kiting casts, a
    /// self-heal and a scout's vanish while running.</summary>
    private bool ActFlee(Character npc, Character target)
    {
        npc.FleeStepsCurrent++;
        if (npc.FleeStepsCurrent >= npc.FleeStepsMax)
        {
            npc.FleeStepsCurrent = 0;
            npc.FightTarget = Serial.Invalid;
            return false;
        }

        int dist = npc.Position.GetDistanceTo(target.Position);
        int radar = _config.MapViewRadar > 0 ? _config.MapViewRadar : Character.MapViewRadarTiles;
        if (target.MapIndex != npc.MapIndex || !IsAttackable(target) ||
            dist >= npc.FleeStepsMax || dist > radar)
            return false;

        if (HasExtra(npc, NpcAiExtraFlags.FleeTactics) && TryFleeTactics(npc, target, dist))
            return true;

        // Pathfinder-based escape: find a direction away from threat
        return FleeAway(npc, target.Position);
    }

    /// <summary>NPCAIEXTRAS FleeTactics: a fleeing caster casts at its pursuer every
    /// third step and heals itself every fourth; a creature that can hide vanishes
    /// once it has some distance (ServUO OrcScout guerilla style), breaking the
    /// pursuit. True = the step was spent.</summary>
    private bool TryFleeTactics(Character npc, Character target, int dist)
    {
        // Kiting: cast a spell while fleeing if mana allows (every 3rd step)
        if (npc.NpcSpells.Count > 0 && npc.Mana >= npc.Int / 3
            && dist >= 2 && dist <= 8
            && npc.FleeStepsCurrent % 3 == 0
            && _world.CanSeeLOS(npc.Position, target.Position))
        {
            var (spell, castTarget) = ChooseBestSpell(npc, target, dist);
            if (spell != SpellType.None && CastViaTrigger(npc, castTarget, spell))
                return true;
        }

        // Self-heal while fleeing (every 4th step)
        if (npc.NpcSpells.Count > 0 && npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 3
            && npc.FleeStepsCurrent % 4 == 0)
        {
            if (npc.NpcSpells.Contains(SpellType.GreaterHeal))
            {
                if (CastViaTrigger(npc, npc, SpellType.GreaterHeal))
                    return true;
            }
            else if (npc.NpcSpells.Contains(SpellType.Heal))
            {
                if (CastViaTrigger(npc, npc, SpellType.Heal))
                    return true;
            }
        }

        // Scout retreat: vanish mid-flee once there is some distance. The pursuer
        // then loses sight of it.
        if (dist >= 4 && !npc.IsStatFlag(StatFlag.Hidden)
            && npc.GetSkill(SkillType.Hiding) > 0 && _rand.Next(8) == 0)
        {
            npc.SetStatFlag(StatFlag.Hidden);
            npc.FleeStepsCurrent = 0;
            npc.FightTarget = Serial.Invalid;
            return true;
        }
        return false;
    }

    /// <summary>NPCAIEXTRAS CombatExtras forced combat target: TAG.PROVOKED_TARGET or
    /// TAG.CONSTANT_FOCUS names a character to attack. Neither exists in Source-X
    /// (its provocation starts the fight directly) nor in the script packs, so the
    /// tags are read only with the extra on. Returns true if engaged.</summary>
    private static bool TryReadUidTag(Character npc, string name, out uint uid)
    {
        uid = 0;
        if (!npc.TryGetTag(name, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        uid = SphereNet.Game.Objects.ObjBase.ParseHexOrDecUInt(raw);
        return uid != 0;
    }

    private bool TryForcedTarget(Character npc)
    {
        Serial uid = Serial.Invalid;
        bool isProvoke = false;
        if (TryReadUidTag(npc, "PROVOKED_TARGET", out uint pu))
        {
            uid = new Serial(pu);
            isProvoke = true;
        }
        else if (TryReadUidTag(npc, "CONSTANT_FOCUS", out uint cu))
        {
            uid = new Serial(cu);
        }
        if (!uid.IsValid) return false;

        var t = _world.FindChar(uid);
        if (t == null || t.IsDead || t.IsDeleted || t == npc ||
            t.MapIndex != npc.MapIndex || !IsAttackable(t))
        {
            if (isProvoke) npc.RemoveTag("PROVOKED_TARGET"); // expired/dead provoke clears
            return false;
        }
        npc.FightTarget = t.Uid;
        ActFight(npc, t, 100);
        return true;
    }

    private bool FleeAway(Character npc, Point3D threat)
    {
        // Run once there is room; walk while cornered (reference
        // NPC_Act_Follow flee path: NPC_WalkToPoint(iDist > 3)).
        bool run = npc.Position.GetDistanceTo(threat) > 3;
        // Try the direct opposite direction first
        var dir = npc.Position.GetDirectionTo(threat);
        GetDirectionDelta(dir, out short dx, out short dy);

        short nx = (short)(npc.X - dx);
        short ny = (short)(npc.Y - dy);
        var mapData = _world.MapData;
        sbyte nz = ResolveNpcStepZ(npc, nx, ny);

        if (Math.Abs(nz - npc.Z) <= 12)
        {
            var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
            if (CanNpcMoveTo(npc, newPos))
            {
                var fleeDir = npc.Position.GetDirectionTo(newPos);
                npc.Direction = run ? fleeDir | Direction.Running : fleeDir;
                _world.MoveCharacter(npc, newPos);
                return true;
            }
        }

        // Direct blocked — try two diagonal alternatives
        for (int rot = 1; rot <= 2; rot++)
        {
            foreach (int sign in new[] { 1, -1 })
            {
                int altDir = (((int)dir & 0x07) + sign * rot) & 0x07;
                GetDirectionDelta((Direction)altDir, out short adx, out short ady);
                short ax = (short)(npc.X - adx);
                short ay = (short)(npc.Y - ady);
                sbyte az = ResolveNpcStepZ(npc, ax, ay);
                if (Math.Abs(az - npc.Z) > 12) continue;
                var altPos = new Point3D(ax, ay, az, npc.MapIndex);
                if (CanNpcMoveTo(npc, altPos))
                {
                    var altFleeDir = npc.Position.GetDirectionTo(altPos);
                    npc.Direction = run ? altFleeDir | Direction.Running : altFleeDir;
                    _world.MoveCharacter(npc, altPos);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Callback for when an NPC successfully deals damage. Used by Program.cs to broadcast effects.
    /// Parameters: attacker, target, damage dealt
    /// </summary>
    public Action<Character, Character, Item?, int, uint>? OnNpcAttack { get; set; }

    /// <summary>
    /// Callback for an NPC ranged shot leaving the weapon (Source-X post-swing
    /// EFFECT_BOLT, CCharFight.cpp:2001-2007). Invoked BEFORE the hit/miss
    /// resolves, like the reference, so the projectile packet precedes anything the
    /// @Hit / @HitMiss / weapon @Damage scripts emit on the target (an explosion
    /// effect must not play before its bomb is even thrown). Parameters: attacker,
    /// target, weapon.
    /// </summary>
    public Action<Character, Character, Item>? OnNpcRangedShot { get; set; }

    /// <summary>
    /// Callback for when an NPC engages a new fight target (reference
    /// Attacker_Add). Fires before the first swing — at engage, while the NPC
    /// may still be closing in — so Program.cs can broadcast the
    /// "*X is attacking Y!*" emote pair. Parameters: attacker, target.
    /// </summary>
    public Action<Character, Character>? OnNpcAttackNotify { get; set; }

    /// <summary>
    /// Callback for when an NPC kills a target. Used by Program.cs to run DeathEngine + broadcast.
    /// Parameters: killer, victim
    /// </summary>
    public Action<Character, Character>? OnNpcKill { get; set; }

    /// <summary>Callback: dragon breath attack. Parameters: npc, target, damage.</summary>
    public Action<Character, Character, int>? OnNpcBreath { get; set; }

    /// <summary>Callback: NPC throws object. Parameters: npc, target, damage.</summary>
    public Action<Character, Character, int>? OnNpcThrow { get; set; }

    /// <summary>What @HitTry answered for one swing (Source-X CCharFight.cpp:1927-
    /// 1948): RETURN 1 holds the swing; otherwise ARGN1, LOCAL.AnimDelay and
    /// LOCAL.Anim are read back (-1 Anim = the default swing animation).</summary>
    public readonly record struct NpcHitTryOutcome(bool Held, long ArgN1, long AnimDelay, int Anim);

    /// <summary>@HitTry hook, on the player path's contract: ARGN1 = the recoil and
    /// LOCAL.AnimDelay = the swing animation delay, both in tenths (swapped under
    /// COMBAT_ANIM_HIT_SMOOTH, see <see cref="CombatHelper.ToHitTryArgs"/>).
    /// Args: npc, target, weapon, argN1, animDelay.</summary>
    public Func<Character, Character, Item?, int, int, NpcHitTryOutcome>? OnNpcHitTry { get; set; }

    /// <summary>The swing animation, sent as the swing starts - before the blow,
    /// which lands once the animation delay has passed (Source-X READY state,
    /// CCharFight.cpp:1957-1999). Args: npc, target, weapon, the @HitTry
    /// LOCAL.Anim override (-1 = default), the 0x6E delay byte.</summary>
    public Action<Character, Character, Item?, int, byte>? OnNpcSwingStart { get; set; }

    /// <summary>What @HitCheck answered for one swing: the raw RETURN number,
    /// whether that return was true at all, the swing state ARGN1 named, and the
    /// per-swing Recoil_NoRange. The reference's contract is numeric
    /// (CCharFight.cpp:1770-1779), so a plain true/false cannot carry it.</summary>
    public readonly record struct NpcHitCheckOutcome(
        long Return, bool Vetoed, int SwingState, bool SwingNoRange);

    /// <summary>@HitCheck hook fired before range/LoS validation, on the same
    /// contract the player path uses — a script must not see one meaning of
    /// RETURN 1 when it swings and another when its pet does.</summary>
    public Func<Character, Character, Item?, bool, NpcHitCheckOutcome>? OnNpcHitCheck { get; set; }

    /// <summary>
    /// Try to swing attack a target with swing timer throttle.
    /// Uses the same pre-AOS Source-X swing-speed formula as players
    /// (<see cref="SphereNet.Game.Clients.GameClient.GetSwingDelayMs"/>),
    /// gated by the same can-swing checks (dead / sleeping / frozen /
    /// out-of-stamina / mid-cast). Mirrors CChar::Fight_Hit:
    ///   <list type="number">
    ///     <item>Validate state and STAM &gt; 0.</item>
    ///     <item>Turn to face the target before launching the swing
    ///       so the animation plays the right way (UpdateDir).</item>
    ///     <item>Resolve damage after the committed windup.</item>
    ///     <item>Set <c>NextAttackTime</c> to the full swing recoil.</item>
    ///   </list>
    /// </summary>
    private bool TrySwingAttack(Character npc, Character target)
    {
        if (npc == target || npc.MapIndex != target.MapIndex ||
            CombatHelper.IsInvalidSwingParticipant(npc, asTarget: false) ||
            CombatHelper.IsInvalidSwingParticipant(target, asTarget: true))
            return false;

        long now = Environment.TickCount64;
        if (now < npc.NextAttackTime)
            return false;
        // A started swing whose hit hasn't landed yet must not be overwritten;
        // the NPC tick pumps the pending hit (resolved inline for atomic swings).
        if (npc.HasPendingHit)
            return false;

        // Same gating Source-X applies to player attackers — see
        // GameClient.TrySwingAt for rationale.
        if (npc.Stam <= 0)
        {
            npc.NextAttackTime = now + 1000;
            return false;
        }
        // COMBAT_PARALYZE_CANSWING (old-sphere): a paralyzed (Freeze) attacker
        // can keep swinging; sleeping always blocks — on either side, since
        // Source-X Fight_CanHit (CCharFight.cpp:1697) answers SWINGING for
        // STATF_SLEEPING on the attacker AND the target.
        bool paralyzeCanSwing = CombatHelper.IsCombatFlagSet(CombatFlags.ParalyzeCanSwing);
        if ((npc.IsStatFlag(StatFlag.Freeze) && !paralyzeCanSwing) ||
            npc.IsStatFlag(StatFlag.Sleeping) ||
            target.IsStatFlag(StatFlag.Sleeping))
        {
            npc.NextAttackTime = now + 500;
            return false;
        }
        if (npc.IsCasting)
        {
            OnNpcTickSpellCast?.Invoke(npc);
            if (npc.IsCasting)
            {
                npc.NextAttackTime = now + 250;
                return false;
            }
        }

        Item? weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        var effectiveRange = GetFightRange(npc, weapon);
        bool swingNoRange = CombatHelper.SwingIgnoresStartRange();
        int swingDelayMs = SphereNet.Game.Clients.GameClient.GetSwingDelayMs(npc, weapon);

        if (OnNpcHitCheck != null)
        {
            var hitCheck = OnNpcHitCheck(npc, target, weapon, swingNoRange);

            if (hitCheck.Return == -1)
            {
                // WAR_SWING_INVALID: this target is no longer worth swinging at.
                npc.SetCombatSwingState(SwingState.Ready);
                npc.ClearPendingHit();
                npc.FightTarget = Serial.Invalid;
                return true;
            }
            if (hitCheck.Vetoed && hitCheck.Return != -2)
            {
                // RETURN 1: ARGN1 IS the state to take. READY / SWINGING hold,
                // EQUIPPING means the swing is spent — neither is a miss.
                ApplyScriptedSwingState(npc, hitCheck.SwingState, now, swingDelayMs);
                return true;
            }

            swingNoRange = hitCheck.SwingNoRange;
            if (Enum.IsDefined(typeof(SwingState), hitCheck.SwingState))
                npc.SetCombatSwingState((SwingState)hitCheck.SwingState);
        }

        var prep = CombatHelper.ValidateSwingPrep(
            _world, npc, target, weapon, PrivLevel.Player, now, (a, b) => _world.CanSeeLOSFor(npc, a, b),
            ignoreRangeLos: swingNoRange, effectiveRange: effectiveRange);
        switch (prep.Result)
        {
            case CombatHelper.SwingPrepResult.Abort:
                npc.FightTarget = Serial.Invalid;
                return false;
            case CombatHelper.SwingPrepResult.RetryLater:
                npc.NextAttackTime = now + Math.Max(prep.RetryMs, 250);
                return false;
        }

        CombatHelper.RevealOnAttack(npc, PrivLevel.Player);

        int stagger = (int)(npc.Uid.Value * 2654435761u % 200);

        // @HitTry (parity with the player path): Fight_SetDefaultSwingDelays gives
        // the recoil and animation delay, the script may rewrite both or hold the
        // swing (RETURN 1 -> WAR_SWING_READY, look again a tenth later).
        var delays = CombatHelper.GetDefaultSwingDelays(swingDelayMs);
        int animOverride = -1;
        if (OnNpcHitTry != null)
        {
            var (n1, animDelay) = CombatHelper.ToHitTryArgs(delays);
            var hitTry = OnNpcHitTry(npc, target, weapon, n1, animDelay);
            if (hitTry.Held)
            {
                npc.NextAttackTime = now + 100; // held; no recoil burned
                return false;
            }
            delays = CombatHelper.FromHitTryArgs(hitTry.ArgN1, hitTry.AnimDelay);
            animOverride = hitTry.Anim;
        }

        var newDir = npc.Position.GetDirectionTo(target.Position);
        if (!CombatHelper.IsCombatFlagSet(CombatFlags.NoDirChange) && newDir != npc.Direction)
        {
            npc.Direction = newDir;
            OnNpcFacingChanged?.Invoke(npc);
        }

        // Two-phase swing (Source-X READY -> SWINGING): the swing animation goes out
        // now and the blow lands after the animation delay (a second by default)
        // from the NPC tick's pending-hit pump - inline below when there is no
        // delay (PREHIT). The next swing follows the blow by the recoil; the
        // per-NPC stagger only spreads the load of a crowd.
        int hitDelayMs = CombatHelper.GetSwingHitDelayMs(delays);
        int cycleMs = hitDelayMs + delays.RecoilTenths * 100 + stagger;
        npc.BeginSwingWindup(now, hitDelayMs, cycleMs, target.Uid,
            now + Math.Max(cycleMs, swingDelayMs) * 2L,
            weapon != null ? weapon.Uid : Serial.Invalid, swingNoRange,
            effectiveRange.Min, effectiveRange.Max);
        OnNpcSwingStart?.Invoke(npc, target, weapon, animOverride,
            CombatHelper.GetSwingAnimDelay(delays));

        // Source-style owner attribution: a commanded pet/summon attack on an
        // innocent criminal-flags its player owner. Guard response is regional;
        // the criminal flag itself is not.
        if (npc.OwnerSerial.IsValid && target.IsPlayer && Character.AttackingIsACrimeEnabled)
        {
            var owner = npc.ResolveOwnerCharacter();
            if (owner != null && owner.IsPlayer && !owner.IsDead)
            {
                bool targetInnocent = SphereNet.Game.Clients.GameClient.ComputeNotoriety(
                    _world, owner, target) == 1;
                if (targetInnocent &&
                    !CombatHelper.IsCombatFlagSet(CombatFlags.AttackNoAggreived))
                    owner.MakeCriminal();
            }
        }

        if (now >= npc.SwingHitTime)
            ResolveNpcHit(npc, now);
        return true;
    }

    /// <summary>Resolve a started NPC swing's hit (Source-X hit phase). Re-checks
    /// reach/LoS per the combat flags (STAYINRANGE -> miss, SWING_NORANGE -> wait),
    /// fires @HitCheck, runs ResolveAttack and the NPC hit feedback. Called inline
    /// for an atomic swing, or from the NPC tick once the windup elapses.</summary>
    /// <summary>Take the war swing state @HitCheck named (the NPC mirror of the
    /// client handler's own): INVALID drops the target, EQUIPPING spends the
    /// swing, READY / SWINGING wait a tenth of a second (upstream _SetTimeoutD(1))
    /// and look again.</summary>
    private static void ApplyScriptedSwingState(Character npc, int rawState, long now, int swingDelayMs)
    {
        if (!Enum.IsDefined(typeof(SwingState), rawState))
            return;
        switch ((SwingState)rawState)
        {
            case SwingState.Invalid:
                npc.SetCombatSwingState(SwingState.Ready);
                npc.ClearPendingHit();
                npc.FightTarget = Serial.Invalid;
                return;
            case SwingState.Equipping:
            case SwingState.EquippingNoWait:
                npc.SetCombatSwingState(SwingState.Ready);
                npc.NextAttackTime = now +
                    ((SwingState)rawState == SwingState.EquippingNoWait ? 0 : swingDelayMs);
                return;
            default:
                npc.SetCombatSwingState((SwingState)rawState);
                npc.NextAttackTime = now + 100;
                return;
        }
    }

    private static Item? FindNpcAmmo(Character npc, Item weapon)
    {
        // Throwing weapons fire themselves — no pack ammo to find/consume.
        if (CombatHelper.IsThrowingWeapon(weapon)) return null;
        var pack = npc.Backpack;
        if (pack == null) return null;

        var spec = CombatHelper.ResolveAmmoSpec(
            CombatHelper.GetWeaponDef(weapon),
            weapon.ItemType,
            Item.ResolveDefName);
        // A weapon naming no ammo (TDATA3=0) neither looks for nor spends any
        // (Source-X CCharFight.cpp:1864 — pAmmo stays null).
        if (!spec.RequiresAmmo) return null;
        return CombatHelper.FindAmmoInContainer(pack, spec.BaseId, spec.FallbackType);
    }

    private void ConsumeNpcAmmo(Item ammo)
    {
        if (ammo.Amount <= 1)
            _world.RemoveItem(ammo);
        else
            ammo.Amount--;
    }

    private void ResolveNpcHit(Character npc, long now)
    {
        if (!npc.HasPendingHit) return;

        var target = _world.FindChar(npc.PendingHitTarget);
        Serial committedWeaponUid = npc.PendingHitWeapon;
        bool weaponCaptured = npc.PendingHitWeaponCaptured;
        bool swingNoRange = npc.PendingHitSwingNoRange;
        (int Min, int Max)? committedRange =
            npc.PendingHitRangeMin >= 0 && npc.PendingHitRangeMax >= 0
                ? (npc.PendingHitRangeMin, npc.PendingHitRangeMax)
                : null;
        Item? weapon = weaponCaptured
            ? (committedWeaponUid.IsValid ? _world.FindItem(committedWeaponUid) : null)
            : npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        if (weaponCaptured && committedWeaponUid.IsValid && (weapon == null || weapon.IsDeleted))
        {
            npc.ClearPendingHit();
            return;
        }

        switch (CombatHelper.EvaluateHitTime(_world, npc, target, weapon,
            PrivLevel.Player, now, npc.PendingHitDeadline, (a, b) => _world.CanSeeLOSFor(npc, a, b),
            swingNoRange, committedRange))
        {
            case CombatHelper.HitTimeDecision.Wait:
                return; // keep the pending hit; retry next tick
            case CombatHelper.HitTimeDecision.Drop:
                npc.ClearPendingHit();
                return;
            case CombatHelper.HitTimeDecision.Miss:
                // A spent swing (COMBAT_STAYINRANGE target left reach, or the archer
                // walked): Source-X returns WAR_SWING_EQUIPPING out of Fight_Hit
                // before the miss branch (CCharFight.cpp:1857/:1896), so there is no
                // @HitMiss, no miss sound and no ammo to account for.
                npc.ClearPendingHit();
                return;
        }

        npc.ClearPendingHit();
        if (target == null) return;

        Item? ammoStack = weapon != null && CombatHelper.IsRangedWeapon(weapon)
            ? FindNpcAmmo(npc, weapon)
            : null;

        if (weapon != null && CombatHelper.IsRangedWeapon(weapon))
            OnNpcRangedShot?.Invoke(npc, target, weapon);

        short hpBefore = npc.Hits;
        int damage = CombatEngine.ResolveAttack(
            npc, target, weapon, CombatHelper.ActiveCombatFlags,
            -1, -1, ammoStack?.Uid.Value ?? 0, out bool ammoHandled);
        OnNpcAttack?.Invoke(npc, target, weapon, damage, ammoStack?.Uid.Value ?? 0);

        // NPCs remain lenient when templates omit ammo, but a stocked archer
        // consumes the weapon's actual AMMOTYPE (arrow vs bolt/custom). The
        // full combat overload also exposes LOCAL.Arrow and honors a script's
        // LOCAL.ArrowHandled takeover.
        if (ammoStack != null &&
            (damage >= 0 || damage == CombatEngine.AttackResolvedByProc) && !ammoHandled)
            ConsumeNpcAmmo(ammoStack);

        if (damage > 0)
        {
            // The strike sound - Source-X SoundChar(CRESND_HIT): the weapon when
            // armed, the creature's own hit sound when not - and the struck
            // target's get-hit vocalization are both emitted by the OnNpcAttack
            // hit feedback, so they cover creature and player targets uniformly.

            // Retaliation: NPC targets that aren't already fighting back
            // acquire the attacker as their fight target (Source-X parity).
            if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
            {
                target.FightTarget = npc.Uid;
                target.NextNpcActionTime = 0;
                OnWakeNpc?.Invoke(target);
            }

            // The death cry is SoundChar(CRESND_DIE) inside CChar::Death
            // (CCharAct.cpp:4392), played by the death engine for every death.
            if (target.Hits <= 0 && !target.IsDead)
                OnNpcKill?.Invoke(npc, target);
        }

        // Reactive armor reflect may have killed the attacker
        if (npc.Hits < hpBefore && npc.Hits <= 0 && !npc.IsDead)
            OnNpcKill?.Invoke(target, npc);
    }

    private static int GetAttackRange(Character npc, Item? weapon = null)
    {
        weapon ??= npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        return GetFightRange(npc, weapon).Max;
    }

    /// <summary>Source-X Fight_CalcRange: effective reach is max(creature
    /// innate RANGE from the chardef, weapon range). The chardef RANGE keys
    /// were parsed but never read anywhere in the Game layer, so reach
    /// creatures (RANGE=2 serpents etc.) closed to 1 tile and forfeited it.</summary>
    internal static (int Min, int Max) GetFightRange(Character npc, Item? weapon)
    {
        var range = CombatHelper.GetWeaponRange(weapon);
        // A ranged weapon whose ITEMDEF RANGE is the melee default 0,1 counts as
        // "not set" and shoots to sphere.ini ARCHERYMAXDIST (NPC_FightArchery,
        // CCharNPCAct_Fight.cpp:37-38).
        if (weapon != null && CombatHelper.IsRangedWeapon(weapon) &&
            CombatHelper.GetWeaponDef(weapon) is { RangeMin: 0, RangeMax: 1 })
            range = (Math.Min(Character.ArcheryMinDist, Character.ArcheryMaxDist), Math.Max(1, Character.ArcheryMaxDist));
        int innate = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.RangeMax ?? 0;
        return innate > range.Max ? (range.Min, innate) : range;
    }

    /// <summary>Start the Healing skill for an NPC through the engine's skill path
    /// (@SkillStart, the Healing skill with the bandage, @SkillSuccess / @SkillFail).
    /// Args: healer, patient, bandage. False = the start was refused.</summary>
    public Func<Character, Character, Item, bool>? OnNpcBandage { get; set; }

    private readonly Dictionary<uint, long> _bandageBusyUntil = [];

    /// <summary>NPCAIEXTRAS BandageHeal: an NPC with Healing and bandages in its pack
    /// treats <paramref name="patient"/> (itself, or - for the pet code - its owner)
    /// when it is under 78% hit points or poisoned, by starting the Healing skill
    /// through <see cref="OnNpcBandage"/>. While a treatment runs (the skill's
    /// DELAY) no new one starts. True = a treatment started and this tick is
    /// spent.</summary>
    internal bool TryBandage(Character healer, Character patient)
    {
        if (OnNpcBandage == null || healer.IsDead || patient.IsDead || patient.IsDeleted)
            return false;
        if (healer.GetSkill(SkillType.Healing) <= 0 || healer.IsCasting)
            return false;
        if (patient.MaxHits <= 0 || !(patient.IsPoisoned || patient.Hits * 100 < patient.MaxHits * 78))
            return false;
        if (patient != healer && (patient.MapIndex != healer.MapIndex ||
                                  healer.Position.GetDistanceTo(patient.Position) > 2))
            return false;
        long now = Environment.TickCount64;
        if (_bandageBusyUntil.TryGetValue(healer.Uid.Value, out long until) && now < until)
            return false;
        var pack = healer.Backpack;
        var bandage = pack != null ? FindInContents(pack, it => it.ItemType == ItemType.Bandage, 2) : null;
        if (bandage == null)
            return false;
        if (!OnNpcBandage(healer, patient, bandage))
            return false;
        int delay = SphereNet.Game.Skills.SkillEngine.GetSkillDelayMs(SkillType.Healing, healer.GetSkill(SkillType.Healing));
        _bandageBusyUntil[healer.Uid.Value] = now + Math.Max(1000, delay);
        return true;
    }

    /// <summary>The active-skill sink an NPC's skill runs through: pack lookups and
    /// item use act on the world; messages meant for a player's screen go
    /// nowhere.</summary>
    public sealed class NpcSkillSink(Character self, GameWorld world) : SphereNet.Game.Skills.Information.IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random => Random.Shared;
        public void SysMessage(string text) { }
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }

        public Item? FindBackpackItem(ItemType type)
        {
            var pack = Self.Backpack;
            return pack != null ? FindInContents(pack, it => it.ItemType == type, 32) : null;
        }

        public void ConsumeAmount(Item item, ushort amount = 1)
        {
            if (item.Amount > amount)
                item.Amount = (ushort)(item.Amount - amount);
            else
                World.RemoveItem(item);
        }

        public void DeliverItem(Item item)
        {
            var pack = Self.Backpack;
            if (pack == null || !pack.TryAddItem(item))
                World.PlaceItemWithDecay(item, Self.Position);
        }
    }

    private void MoveAway(Character npc, Point3D threat)
    {
        var dir = npc.Position.GetDirectionTo(threat);
        GetDirectionDelta(dir, out short dx, out short dy);

        short nx = (short)(npc.X - dx);
        short ny = (short)(npc.Y - dy);
        var mapData = _world.MapData;
        sbyte nz = ResolveNpcStepZ(npc, nx, ny);
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, newPos))
            return;

        npc.Direction = npc.Position.GetDirectionTo(newPos);
        _world.MoveCharacter(npc, newPos);
    }
}
