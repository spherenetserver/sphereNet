using SphereNet.Core.Enums;
using SphereNet.Core.Types;
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
            default:
                return false;
        }
    }

    /// <summary>Stop the current action and let the brain decide again
    /// (Skill_Start(SKILL_NONE) -> NPC_Act_Idle).</summary>
    private static void ClearScriptedAction(Character npc)
    {
        npc.Action = SkillType.None;
        npc.FleeStepsCurrent = 0;
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

    /// <summary>NPC_Act_Goto / NPC_Act_Runto (CCharNPCAct.cpp:1665/1712): keep
    /// stepping toward the stored point, and go idle on arrival.</summary>
    private bool RunScriptedWalkTo(Character npc, bool run)
    {
        var dest = npc.ActP;
        if (dest.Map != npc.MapIndex)
        {
            ClearScriptedAction(npc);
            return false;
        }
        if (dest.X == npc.X && dest.Y == npc.Y)
        {
            ClearScriptedAction(npc);   // reached it
            return false;
        }

        MoveToward(npc, dest, run);
        return true;
    }
}
