// Pet brain: ActPet order modes, hireling wages, pet helpers.
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
    /// <summary>GO-order arrival: Source-X NPCACT_GOTO falls back to Act_Idle,
    /// which re-evaluates and typically resumes the previous order — restore
    /// the mode saved when the order was issued instead of parking in Stay.</summary>
    private static void FinishGoOrder(Character npc)
    {
        npc.RemoveTag("GO_TARGET");
        // PetAIMode is byte-backed, and Enum.IsDefined THROWS when handed a boxed Int32
        // for such an enum rather than answering false. Every GO order issued by the
        // real command - which does record the previous mode - therefore blew up here,
        // leaving the pet stuck in Come with its GO_TARGET already deleted.
        if (npc.TryGetTag("PREV_PET_MODE", out string? prev) &&
            byte.TryParse(prev, out byte prevMode) &&
            Enum.IsDefined(typeof(PetAIMode), prevMode))
        {
            npc.PetAIMode = (PetAIMode)prevMode;
            npc.RemoveTag("PREV_PET_MODE");
        }
        else
        {
            npc.PetAIMode = PetAIMode.Stay;
        }
    }

    /// <summary>Where a follower should head for. A target it can see gives its own
    /// position, and that position is remembered; one it cannot gives the last place it
    /// was seen, and null once there is nothing remembered to walk to.
    ///
    /// Hidden and Invisible are visibility rules of their own - a geometric line of
    /// sight would not answer them - and staff-level concealment stays visible to the
    /// creature that owns the follower, as it is elsewhere in the engine.</summary>
    private static Point3D? ResolveFollowPoint(Character npc, Character target)
    {
        bool concealed = target.IsStatFlag(StatFlag.Hidden) || target.IsStatFlag(StatFlag.Invisible);
        if (!concealed || npc.PrivLevel >= PrivLevel.Counsel)
        {
            npc.SetTag(FollowLastSeenTag, $"{target.X},{target.Y},{target.Z},{target.MapIndex}");
            return target.Position;
        }

        if (!npc.TryGetTag(FollowLastSeenTag, out string? raw) || !TryParsePoint(raw, out var seen))
            return null;
        return seen.Map == npc.MapIndex ? seen : null;
    }

    private const string FollowLastSeenTag = "FOLLOW_LAST_SEEN";

    /// <summary>How close a follower tries to get by default: Source-X NPC_Act_Follow's
    /// maxDistance = 1 (CChar.h:1341). @NPCActFollow ARGN2 can change it.</summary>
    private const int PetFollowDistance = 1;

    /// <summary>Pet follow gives up beyond this distance on the same map: the
    /// MAPVIEWRADAR setting, whose default is UO_MAP_VIEW_RADAR = 31
    /// (NPC_Act_Follow, CCharNPCAct.cpp:1401; uofiles_macros.h:22). It resumes when
    /// the owner comes back in range. 0 disables the leash.</summary>
    public const int DefaultMapViewRadar = 31;
    public static int PetFollowMaxDistance { get; set; } = DefaultMapViewRadar;

    /// <summary>LOSTNPCTELEPORT — how far an NPC may stray from home before it is put
    /// back rather than asked to walk (Source-X m_iLostNPCTeleport, default 50 tiles;
    /// 0 disables). A backstop, not a leash: the creature's own wander range has to be
    /// exceeded as well.</summary>
    public static int LostNpcTeleport { get; set; } = 50;

    private static bool IsValidPetEnemy(
        Character npc, Character master, Character? target, Character? protectedTarget = null) =>
        target != null && target != npc && target != master && target != protectedTarget &&
        !target.IsDead && !target.IsDeleted && target.MapIndex == npc.MapIndex &&
        !npc.IsFriendOf(target.Uid) && IsAttackable(target);


    /// <summary>
    /// Pet behavior — follows PetAIMode from owner speech commands.
    /// </summary>
    private void ActPet(Character npc)
    {
        if (npc.TickPetOwnershipTimers(Environment.TickCount64))
        {
            _world.DeleteObject(npc);
            return;
        }

        var master = npc.ResolveControllerCharacter() ?? npc.ResolveOwnerCharacter();
        if (master == null || master.IsDead)
        {
            if (npc.IsSummoned)
            {
                _world.DeleteObject(npc);
                return;
            }

            // Owner gone — uncontrolled pets idle instead of following stale state.
            Wander(npc);
            return;
        }

        // NPCAIEXTRAS BandageHeal: a pet (or hireling) with Healing and bandages
        // treats its owner, then itself (not in Source-X; ModernUO HealOwner).
        if (HasExtra(npc, NpcAiExtraFlags.BandageHeal) &&
            (TryBandage(npc, master) || TryBandage(npc, npc)))
            return;

        // Hireling wages are charged on the food tick (Source-X OnTickFood ->
        // NPC_CheckHirelingStatus, CCharAct.cpp:5755), inside
        // Character.TickPetOwnershipTimers above.

        // Self-defense (Source-X Memory_FightStart): an attacked pet fights
        // back regardless of its order state. Follow/Come/Stay never read
        // FightTarget, so a pet under attack just stood there while the
        // aggressor beat it down. Explicit orders clear FightTarget, so the
        // fight ends when the owner calls the pet off.
        if (npc.PetAIMode is PetAIMode.Follow or PetAIMode.Come or PetAIMode.Stay &&
            npc.FightTarget.IsValid)
        {
            var aggressor = _world.FindChar(npc.FightTarget);
            if (IsValidPetEnemy(npc, master, aggressor))
            {
                ActFight(npc, aggressor!, 50);
                return;
            }
            npc.FightTarget = Serial.Invalid;
        }

        switch (npc.PetAIMode)
        {
            case PetAIMode.Follow:
            case PetAIMode.Come:
            {
                // "all go" — an explicit GO order overrides following entirely
                // (Source-X NPCACT_GOTO): the pet walks to the ordered spot and
                // stays there instead of returning to the owner. Running the
                // follow step and the GO step in the same tick made the two
                // moves cancel out, so the pet oscillated between the owner
                // and the goal without ever arriving.
                if (npc.TryGetTag("GO_TARGET", out string? goTag) &&
                    TryParsePoint(goTag, out Point3D goPos))
                {
                    if (npc.MapIndex != goPos.Map)
                    {
                        // Pet AI must not teleport between facets. Travel
                        // systems may transfer followers explicitly; a stale
                        // cross-map GO order is simply completed here.
                        FinishGoOrder(npc);
                        break;
                    }
                    // Walk onto the tile itself. Source-X keeps stepping until there
                    // is no direction left to take (NPC_WalkToPoint, CCharNPCAct.cpp:437)
                    // - being one tile short is not arrival, and treating it as such
                    // meant a GO to an adjacent tile never moved the pet at all.
                    int goDist = npc.Position.GetDistanceTo(goPos);
                    if (goDist == 0)
                    {
                        FinishGoOrder(npc);
                        break;
                    }

                    var beforeGoStep = npc.Position;
                    MoveToward(npc, goPos, run: goDist > 3);

                    // The last tile can simply be unreachable - occupied, blocked or
                    // walled off. Standing beside it and getting nowhere ends the
                    // order rather than retrying it forever.
                    if (goDist == 1 && npc.Position == beforeGoStep)
                        FinishGoOrder(npc);
                    break;
                }

                Character followTarget = ResolvePetTargetCharacter(npc, "FOLLOW_TARGET") ?? master;

                // Source-X @NPCActFollow: RETURN 1 gives up following entirely,
                // RETURN 0 means the script handled this call, and falling through
                // carries on with the arguments the script may have rewritten.
                var followArgs = new FollowTriggerArgs { MaxDistance = PetFollowDistance };
                if (OnNpcActFollow != null)
                {
                    var followed = OnNpcActFollow(npc, followTarget, followArgs);
                    if (followed == FollowTriggerResult.GiveUp)
                    {
                        npc.PetAIMode = PetAIMode.Stay;
                        break;
                    }
                    if (followed == FollowTriggerResult.Handled)
                        break;
                }
                if (npc.MapIndex != followTarget.MapIndex)
                {
                    break;
                }
                // Source-X only takes the follow point from a target it can SEE
                // (NPC_Act_Follow, CCharNPCAct.cpp:1386; CanSee, CCharStatus.cpp:1189).
                // SphereNet read the position unconditionally, so a pet tracked an
                // owner who had just hidden - across the map, to wherever they went.
                // A hidden target leaves the last place it WAS seen standing, which is
                // what the pet walks to.
                var followPoint = ResolveFollowPoint(npc, followTarget);
                if (followPoint == null)
                    break;

                int dist = npc.Position.GetDistanceTo(followPoint.Value);
                bool leashed = PetFollowMaxDistance > 0 && dist > PetFollowMaxDistance;
                int keep = Math.Max(0, followArgs.MaxDistance);
                if (dist > keep && !leashed)
                {
                    // Runs in war mode or past three tiles (NPC_Act_Follow, :1440).
                    MoveToward(npc, followPoint.Value, run: npc.IsStatFlag(StatFlag.War) || dist > 3);
                    if (dist > keep + 2 && HasExtra(npc, NpcAiExtraFlags.PetKeepPace))
                        KeepOwnerPace(npc, followTarget);
                }
                break;
            }
            case PetAIMode.Guard:
            {
                Character guardTarget = ResolvePetTargetCharacter(npc, "GUARD_TARGET") ?? master;
                if (guardTarget.MapIndex != npc.MapIndex)
                    break;
                if (npc.FightTarget.IsValid)
                {
                    var current = _world.FindChar(npc.FightTarget);
                    if (IsValidPetEnemy(npc, master, current, guardTarget))
                    {
                        ActFight(npc, current!, 50);
                        return;
                    }
                    npc.FightTarget = Serial.Invalid;
                }
                // Source-X NPC_Act_Guard (CCharNPCAct.cpp:1291): attack what the
                // guarded character is fighting when it can be seen, else follow it
                // (to distance 1). Nothing scans for other attackers.
                var guardFoe = guardTarget.FightTarget.IsValid ? _world.FindChar(guardTarget.FightTarget) : null;
                if (IsValidPetEnemy(npc, master, guardFoe, guardTarget) &&
                    CanSeeChar(npc, guardTarget) && _world.CanSeeLOS(npc.Position, guardTarget.Position))
                {
                    bool newFight = npc.FightTarget != guardFoe!.Uid;
                    npc.FightTarget = guardFoe.Uid;
                    if (newFight)
                        NpcAttackCrimeCheck(npc, guardFoe);
                    ActFight(npc, guardFoe, 50);
                    return;
                }
                ActFollow(npc, guardTarget);
                break;
            }
            case PetAIMode.Attack:
            {
                Character? target = ResolvePetTargetCharacter(npc, "ATTACK_TARGET");
                if (target == null && master.FightTarget.IsValid)
                    target = _world.FindChar(master.FightTarget);
                if (target == null && npc.FightTarget.IsValid)
                    target = _world.FindChar(npc.FightTarget);
                if (IsValidPetEnemy(npc, master, target))
                {
                    bool newFight = npc.FightTarget != target!.Uid;
                    npc.FightTarget = target.Uid;
                    if (newFight)
                        NpcAttackCrimeCheck(npc, target);
                    int motivation = GetAttackMotivation(npc, target);
                    ActFight(npc, target, Math.Max(motivation, 50));
                    return;
                }
                // Target dead/gone — revert to the mode the pet was in before the
                // attack order (Guard/Follow), instead of trailing the master.
                npc.FightTarget = Serial.Invalid;
                npc.RemoveTag("ATTACK_TARGET");
                PetAIMode revertMode = PetAIMode.Follow;
                // byte, not int: Enum.IsDefined throws for a boxed Int32 on this
                // byte-backed enum (see FinishGoOrder).
                if (npc.TryGetTag("PREV_PET_MODE", out string? prevTag) &&
                    byte.TryParse(prevTag, out byte prevVal) &&
                    Enum.IsDefined(typeof(PetAIMode), prevVal) &&
                    (PetAIMode)prevVal != PetAIMode.Attack)
                    revertMode = (PetAIMode)prevVal;
                npc.RemoveTag("PREV_PET_MODE");
                npc.PetAIMode = revertMode;
                int d = npc.Position.GetDistanceTo(master.Position);
                if (d > PetFollowDistance && (PetFollowMaxDistance <= 0 || d <= PetFollowMaxDistance))
                    MoveToward(npc, master.Position, run: npc.IsStatFlag(StatFlag.War) || d > 3);
                break;
            }
            case PetAIMode.Stay:
            case PetAIMode.Stop:
                // Stay in place
                break;
        }
    }

    /// <summary>NPCAIEXTRAS PetKeepPace: a following pet that has fallen behind takes
    /// its next step at its owner's pace - about 200 ms on foot, 100 ms mounted -
    /// instead of its own DEX-driven delay.</summary>
    private void KeepOwnerPace(Character npc, Character owner)
    {
        if (_stepDelayAppliedFor != npc.Uid.Value)
            return;   // no step was taken
        int pace = owner.IsMounted ? 100 : 200;
        npc.NextNpcActionTime = Math.Min(npc.NextNpcActionTime, Environment.TickCount64 + pace);
    }

    private Character? ResolvePetTargetCharacter(Character npc, string tagName)
    {
        if (!npc.TryGetTag(tagName, out string? uidText) || string.IsNullOrWhiteSpace(uidText))
            return null;
        uint uid = SphereNet.Game.Objects.ObjBase.ParseHexOrDecUInt(uidText);
        if (uid == 0)
        {
            npc.RemoveTag(tagName);
            return null;
        }
        var target = _world.FindChar(new Serial(uid));
        if (target == null || target.IsDeleted || target.IsDead)
        {
            npc.RemoveTag(tagName);
            return null;
        }
        return target;
    }

    private static bool TryParsePoint(string? raw, out Point3D pos)
    {
        pos = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return false;
        if (!short.TryParse(parts[0], out short x) ||
            !short.TryParse(parts[1], out short y) ||
            !sbyte.TryParse(parts[2], out sbyte z) ||
            !byte.TryParse(parts[3], out byte map))
            return false;
        pos = new Point3D(x, y, z, map);
        return true;
    }
}
