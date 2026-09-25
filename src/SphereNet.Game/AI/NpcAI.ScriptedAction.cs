using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.AI;

/// <summary>
/// The NPC ACTION channel: an action a script started, which outranks whatever the
/// brain would have chosen until it finishes.
///
/// Upstream has one dispatcher for both - Skill_Start(NPCACT_FLEE) parks an action
/// in the same slot a skill uses, and the per-tick handler runs NPC_Act_Flee /
/// NPC_Act_Goto / NPC_Act_Runto before anything else (CCharNPCAct.cpp:2340). This
/// engine decides per tick from the brain instead, so the verbs that start an
/// action - FLEE, LEAVE, GOTO, RUNTO - had nowhere to put one and did nothing at
/// all. Character.Action already carried the value (the SKILL verb writes it, and
/// NpcAction shares the numeric space with SkillType exactly as NPCACT_* shares
/// SKILL_TYPE upstream); nothing read it.
/// </summary>
public sealed partial class NpcAI
{
    /// <summary>Run the action a script started. True when it took this tick, which
    /// keeps the brain from deciding something else on top of it.</summary>
    private bool RunScriptedAction(Character npc)
    {
        switch ((NpcAction)npc.Action)
        {
            case NpcAction.Flee:
                return RunScriptedFlee(npc);
            case NpcAction.GoTo:
                return RunScriptedWalkTo(npc, run: false);
            case NpcAction.RunTo:
                return RunScriptedWalkTo(npc, run: true);
            case NpcAction.FollowTarg:
                return RunScriptedFollowTarg(npc);
            case NpcAction.GuardTarg:
                return RunScriptedGuardTarg(npc);
            case NpcAction.Talk:
            case NpcAction.TalkFollow:
                return RunTalk(npc);
            default:
                return false;
        }
    }

    /// <summary>Stop the current action and let the brain decide again
    /// (Skill_Start(SKILL_NONE) -> NPC_Act_Idle).</summary>
    private void ClearScriptedAction(Character npc)
    {
        npc.Action = SkillType.None;
        npc.FleeStepsCurrent = 0;
        _talkState.Remove(npc.Uid.Value);
    }

    /// <summary>NPC_Act_Flee (CCharNPCAct.cpp:1645): a step-counted retreat that
    /// ends when the budget runs out.
    ///
    /// Who it runs FROM is upstream's rule, not a guess: while the action is FLEE the
    /// fight target wins and the act target stands in for it, and with neither the
    /// action simply ends - "free to do as I wish" (NPC_Act_Follow, :1345-1352). That
    /// matters because a script says I.LEAVE to someone the NPC is talking to, which
    /// is exactly what the act target holds.</summary>
    private bool RunScriptedFlee(Character npc)
    {
        var from = ResolveFleeSubject(npc);
        if (from == null)
        {
            ClearScriptedAction(npc);
            return false;
        }

        if (++npc.FleeStepsCurrent >= npc.FleeStepsMax)
        {
            ClearScriptedAction(npc);
            return false;
        }

        FleeAway(npc, from.Position);
        return true;
    }

    private Character? ResolveFleeSubject(Character npc)
    {
        if (npc.FightTarget.IsValid && _world.FindChar(npc.FightTarget) is { } fighting
            && !fighting.IsDeleted && !fighting.IsDead)
        {
            return fighting;
        }
        if (npc.Act.IsValid && _world.FindChar(npc.Act) is { } acted
            && !acted.IsDeleted && !acted.IsDead)
        {
            return acted;
        }
        return null;
    }

    /// <summary>NPC_Act_Goto / NPC_Act_Runto (CCharNPCAct.cpp:1665-1759): keep
    /// stepping toward the stored point, and go idle on arrival.
    ///
    /// When a step cannot be taken, NPC_AI_PERSISTENTPATH keeps retrying - as runs,
    /// about as many times as the point is tiles away (iDist from 30 down) - before
    /// going idle. Without the flag a playable-bodied creature that is neither frozen
    /// nor stone is teleported onto the point, and anything else goes idle.</summary>
    private bool RunScriptedWalkTo(Character npc, bool run)
    {
        var dest = npc.ActP;
        if (dest.Map != npc.MapIndex)
        {
            ClearScriptedAction(npc);
            return false;
        }

        int result = MoveToward(npc, dest, run);
        if (result == 1)
            return true;
        if (result == 0)
        {
            ClearScriptedAction(npc);   // reached it -> NPC_Act_Idle
            return false;
        }

        if (GetNpcFlags(npc).HasFlag(NpcAIFlags.PersistentPath))
        {
            int iDist = 30;
            while (true)
            {
                int pDist = DistOrMax(dest, npc.Position);
                iDist = iDist > pDist ? pDist : iDist - 1;
                if (iDist <= 0)
                    break;
                result = MoveToward(npc, dest, run: true);
                if (result == 1)
                    return true;
                if (result == 0)
                    break;
            }
            ClearScriptedAction(npc);
            return false;
        }

        if (dest.X >= 0 && dest.Y >= 0 && IsPlayableBody(npc.BodyId) &&
            !npc.IsStatFlag(StatFlag.Freeze) && !npc.IsStatFlag(StatFlag.Stone) &&
            TeleportNpc(npc, dest))
        {
            return true;
        }

        ClearScriptedAction(npc);
        return false;
    }

    /// <summary>NPCACT_FOLLOW_TARG for any NPC (CCharNPCAct.cpp:2320-2326): follow
    /// the act target (ACT); with none the action ends. Pets keep their own order
    /// modes; this is what a script's ACTION=NPCACT_FOLLOW_TARG runs.</summary>
    private bool RunScriptedFollowTarg(Character npc)
    {
        var target = npc.Act.IsValid ? _world.FindChar(npc.Act) : null;
        if (target == null || target.IsDeleted || target == npc)
        {
            ClearScriptedAction(npc);
            return false;
        }
        ActFollow(npc, target);
        return true;
    }

    /// <summary>NPCACT_GUARD_TARG for any NPC: NPC_Act_Guard
    /// (CCharNPCAct.cpp:1291-1310). When the guarded character can be seen and is in
    /// a fight, attack what it is fighting; otherwise follow it.</summary>
    private bool RunScriptedGuardTarg(Character npc)
    {
        var guarded = npc.Act.IsValid ? _world.FindChar(npc.Act) : null;
        if (guarded == null || guarded.IsDeleted || guarded == npc)
        {
            ClearScriptedAction(npc);
            return false;
        }
        ActGuardTarget(npc, guarded);
        return true;
    }

    /// <summary>NPC_Act_Guard body shared by scripted GUARD_TARG and the pet guard
    /// order: attack the guarded character's fight target when it can be seen and
    /// is fighting, else follow it.</summary>
    private void ActGuardTarget(Character npc, Character guarded, Character? master = null)
    {
        if (guarded.MapIndex == npc.MapIndex && CanSeeChar(npc, guarded) &&
            _world.CanSeeLOS(npc.Position, guarded.Position) && guarded.FightTarget.IsValid)
        {
            var foe = _world.FindChar(guarded.FightTarget);
            if (foe != null && foe != npc && !foe.IsDead && !foe.IsDeleted &&
                foe.MapIndex == npc.MapIndex && foe != master && IsAttackable(foe))
            {
                npc.FightTarget = foe.Uid;
                npc.Memory_Fight_Start(foe);
                ActFight(npc, foe, 50);
                return;
            }
        }
        ActFollow(npc, guarded);
    }

    /// <summary>Source-X CanSee as NPC_Act_Follow and NPC_Act_Talk use it: another
    /// map is out of sight, and hidden or invisible characters are unseen unless the
    /// looker is staff (CCharStatus.cpp:1189).</summary>
    private static bool CanSeeChar(Character npc, Character target)
    {
        if (target.MapIndex != npc.MapIndex || target.IsDeleted)
            return false;
        bool concealed = target.IsStatFlag(StatFlag.Hidden) || target.IsStatFlag(StatFlag.Invisible);
        return !concealed || npc.PrivLevel >= PrivLevel.Counsel;
    }

    /// <summary>Source-X NPC_Act_Follow (CCharNPCAct.cpp:1312-1444). False = can't
    /// follow any more. @NPCActFollow sees flee / max distance / move away as
    /// ARGN1..3 (RETURN 1 gives up, a handled call takes no native step). An unseen
    /// target is walked toward at its last known point (ACTP), unless the creature
    /// forgets it (1 in 1 + (100-INT)/20) or is fleeing. Farther than the view radar
    /// gives up; within the distance to keep it is content; a flee steps away,
    /// running past three tiles, and a follow runs in war mode or past three tiles.</summary>
    private bool ActFollow(Character npc, Character target, bool flee = false, int maxDistance = 1,
        bool moveAway = false)
    {
        if ((CharDefHelper.GetCanFlags(npc) & CanFlags.C_NonMover) != 0)
            return npc.FightTarget.IsValid;

        if (OnNpcActFollow != null)
        {
            var args = new FollowTriggerArgs { Flee = flee, MaxDistance = maxDistance, MoveAway = moveAway };
            var answer = OnNpcActFollow(npc, target, args);
            if (answer == FollowTriggerResult.GiveUp)
                return false;
            if (answer == FollowTriggerResult.Handled)
                return true;
            flee = args.Flee;
            maxDistance = args.MaxDistance;
            moveAway = args.MoveAway;
        }

        if (CanSeeChar(npc, target))
        {
            npc.ActP = target.Position;
        }
        else if (flee || RandVal(1 + (100 - npc.Int) / 20) == 0)
        {
            return false;
        }

        var point = npc.ActP;
        int dist = DistOrMax(npc.Position, point);
        int radar = PetFollowMaxDistance > 0 ? PetFollowMaxDistance : short.MaxValue;
        if (dist > radar)
            return false;

        if (moveAway)
        {
            if (dist < maxDistance)
                flee = true;
        }
        else if (flee)
        {
            if (dist >= maxDistance)
                return false;
        }
        else if (dist <= maxDistance)
        {
            return true;
        }

        if (flee)
        {
            var toward = (int)npc.Position.GetDirectionTo(point) & 0x07;
            var away = (Direction)((toward + 4 + 1 - RandVal(3) + 8) % 8);
            GetDirectionDelta(away, out short dx, out short dy);
            var awayPoint = new Point3D((short)(npc.X + dx), (short)(npc.Y + dy), npc.Z, npc.MapIndex);
            return MoveToward(npc, awayPoint, run: dist > 3) < 2;
        }

        return MoveToward(npc, point, run: npc.IsStatFlag(StatFlag.War) || dist > 3) < 2;
    }

    // --- Talk: NPC_ActStart_SpeakTo / NPC_Act_Talk / NPC_OnHear ---

    private sealed class TalkState
    {
        public int WaitCount;
        public int HearUnknown;
    }

    /// <summary>m_atTalk, runtime only.</summary>
    private readonly Dictionary<uint, TalkState> _talkState = [];

    /// <summary>Source-X NPC_ActStart_SpeakTo (CCharNPCAct.cpp:243): the NPC is now
    /// talking to <paramref name="speaker"/> - it waits for twenty ticks, follows
    /// along when the speaker's fame is over 7000, faces them, and takes its next
    /// tick three seconds from now.
    ///
    /// This engine keeps the fight target outside the action slot, so a creature in
    /// a fight is not talked out of it: the talk only starts when it has no fight
    /// target.</summary>
    public void NpcStartSpeakTo(Character npc, Character speaker)
    {
        if (npc.IsPlayer || npc.IsDead || npc.IsDeleted || speaker == npc)
            return;
        if (npc.FightTarget.IsValid)
            return;

        npc.Act = speaker.Uid;
        _talkState[npc.Uid.Value] = new TalkState { WaitCount = 20, HearUnknown = 0 };
        npc.Action = (SkillType)(speaker.Fame > 7000 ? NpcAction.TalkFollow : NpcAction.Talk);
        npc.NextNpcActionTime = Environment.TickCount64 + 3000;
        if (npc.Position.X != speaker.X || npc.Position.Y != speaker.Y)
        {
            npc.Direction = npc.Position.GetDirectionTo(speaker.Position);
            OnNpcFacingChanged?.Invoke(npc);
        }
    }

    /// <summary>The top of Source-X NPC_OnHear (CCharNPCAct.cpp:273-298): an NPC that
    /// was talking to someone else and still is says the "interrupt" line.</summary>
    public void NpcHearBegin(Character npc, Character speaker)
    {
        if (npc.IsPlayer || !NpcCanSpeak(npc))
            return;
        var act = (NpcAction)npc.Action;
        if (act is not (NpcAction.Talk or NpcAction.TalkFollow) || npc.Act == speaker.Uid)
            return;
        var previous = npc.Act.IsValid ? _world.FindChar(npc.Act) : null;
        if (ActTalk(npc) && previous != null)
            OnNpcSay?.Invoke(npc, ServerMessages.GetFormatted(Msg.NpcGenericInterrupt,
                previous.GetName(), speaker.GetName()));
    }

    /// <summary>The tail of Source-X NPC_OnHear (CCharNPCAct.cpp:375-385): a line the
    /// NPC could not make out while talking counts; after more than four (one when
    /// the speaker is over four tiles off) it gives up the conversation.</summary>
    public void NpcHearUnknown(Character npc, Character speaker)
    {
        var act = (NpcAction)npc.Action;
        if (act is not (NpcAction.Talk or NpcAction.TalkFollow))
            return;
        if (!_talkState.TryGetValue(npc.Uid.Value, out var state))
            return;
        state.HearUnknown++;
        int maxUnknown = DistOrMax(npc.Position, speaker.Position) > 4 ? 1 : 4;
        if (state.HearUnknown > maxUnknown)
            ClearScriptedAction(npc);
    }

    /// <summary>NPCACT_TALK / NPCACT_TALK_FOLLOW tick: keep waiting while
    /// <see cref="ActTalk"/> says so, else go idle.</summary>
    private bool RunTalk(Character npc)
    {
        if (ActTalk(npc))
            return true;
        ClearScriptedAction(npc);
        return false;
    }

    /// <summary>Source-X NPC_Act_Talk (CCharNPCAct.cpp:1446-1494). False = do
    /// something else: the partner is gone, 14+ away, beyond HOMEDIST from home or
    /// out of sight, a talk-follow could not close in, or the wait ran out - the last
    /// with one of the "gone" lines.</summary>
    private bool ActTalk(Character npc)
    {
        if (!_talkState.TryGetValue(npc.Uid.Value, out var state))
            return false;
        var partner = npc.Act.IsValid ? _world.FindChar(npc.Act) : null;
        if (partner == null || partner.IsDeleted)
            return false;

        int dist = Dist3D(npc.Position, partner.Position);
        if (dist >= 14)
            return false;
        if (TryResolveHome(npc, out var home, out int homeDist) &&
            Dist3D(home, partner.Position) > homeDist)
            return false;
        if (!CanSeeChar(npc, partner))
            return false;

        if ((NpcAction)npc.Action == NpcAction.TalkFollow && dist > 3 &&
            !ActFollow(npc, partner, false, 4, false))
            return false;

        if (state.WaitCount <= 1)
        {
            if (NpcCanSpeak(npc))
            {
                string key = _rand.Next(2) == 0 ? Msg.NpcGenericGone1 : Msg.NpcGenericGone2;
                OnNpcSay?.Invoke(npc, ServerMessages.GetFormatted(key, partner.GetName()));
            }
            return false;
        }

        state.WaitCount--;
        return true;
    }

    /// <summary>Source-X GetDist3D (CPointBase.cpp:236): the larger of the 2D distance
    /// and the height difference in half player heights.</summary>
    private static int Dist3D(Point3D a, Point3D b)
    {
        int dist = DistOrMax(a, b);
        int dz = Math.Abs(a.Z - b.Z) / 8;
        return Math.Max(dz, dist);
    }
}
