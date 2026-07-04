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
        if (npc.TryGetTag("PREV_PET_MODE", out string? prev) &&
            int.TryParse(prev, out int prevMode) &&
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

    /// <summary>Pet follow gives up beyond this distance on the same map
    /// (reference parity: UO_MAP_VIEW_RADAR = 36); it resumes when the owner
    /// comes back in range. 0 disables the leash.</summary>
    public static int PetFollowMaxDistance { get; set; } = 36;

    /// <summary>Default hireling pay interval (ms) when HIRE_PERIOD isn't set.</summary>
    private const long DefaultHirePeriodMs = 30 * 60 * 1000; // 30 minutes


    /// <summary>
    /// Pet behavior — follows PetAIMode from owner speech commands.
    /// </summary>
    private void ActPet(Character npc)
    {
        if (npc.TickPetOwnershipTimers(Environment.TickCount64))
        {
            _world.DeleteObject(npc);
            npc.Delete();
            return;
        }

        var master = npc.ResolveControllerCharacter() ?? npc.ResolveOwnerCharacter();
        if (master == null || master.IsDead)
        {
            if (npc.IsSummoned)
            {
                _world.DeleteObject(npc);
                npc.Delete();
                return;
            }

            // Owner gone — uncontrolled pets idle instead of following stale state.
            Wander(npc);
            return;
        }

        // Hireling upkeep (Source-X NPC_CheckHirelingStatus): the day wage
        // comes from the CHARDEF (HIREDAYWAGE; legacy HIRE_WAGE tag overrides)
        // and drains the NPC's own PREPAID balance — funded when the player
        // hands it gold (NPC_OnHirePay) — a period-fraction at a time. It
        // never touches the master's live bank; on depletion it speaks the
        // wage-cost/time-up messages and deserts.
        uint dayWage = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.HireDayWage ?? 0;
        if (npc.TryGetTag("HIRE_WAGE", out string? wageStr) &&
            uint.TryParse(wageStr, out uint tagWage) && tagWage > 0)
            dayWage = tagWage;
        if (dayWage > 0)
        {
            long nowMs = Environment.TickCount64;
            long period = npc.TryGetTag("HIRE_PERIOD", out string? ps) &&
                long.TryParse(ps, out long p) && p > 0 ? p : DefaultHirePeriodMs;
            long nextPay = npc.TryGetTag("HIRE_NEXT_PAY", out string? np) &&
                long.TryParse(np, out long n) ? n : 0;
            if (nextPay == 0)
                npc.SetTag("HIRE_NEXT_PAY", (nowMs + period).ToString());
            else if (nowMs >= nextPay)
            {
                long periodWage = Math.Max(1, (long)dayWage * period / 86_400_000L);
                long balance = npc.TryGetTag("HIRE_BALANCE", out string? bs) &&
                    long.TryParse(bs, out long bal) ? bal : 0;
                if (balance >= periodWage)
                {
                    npc.SetTag("HIRE_BALANCE", (balance - periodWage).ToString());
                    npc.SetTag("HIRE_NEXT_PAY", (nowMs + period).ToString());
                }
                else
                {
                    OnNpcSay?.Invoke(npc, ServerMessages.GetFormatted(Msg.NpcPetWageCost, dayWage.ToString()));
                    OnNpcSay?.Invoke(npc, ServerMessages.Get(Msg.NpcPetHireTimeup));
                    npc.RemoveTag("HIRE_NEXT_PAY");
                    npc.RemoveTag("HIRE_BALANCE");
                    npc.ClearOwnership(clearFriends: true);
                    npc.PetAIMode = PetAIMode.Stay;
                    return;
                }
            }
        }

        // Self-defense (Source-X Memory_FightStart): an attacked pet fights
        // back regardless of its order state. Follow/Come/Stay never read
        // FightTarget, so a pet under attack just stood there while the
        // aggressor beat it down. Explicit orders clear FightTarget, so the
        // fight ends when the owner calls the pet off.
        if (npc.PetAIMode is PetAIMode.Follow or PetAIMode.Come or PetAIMode.Stay &&
            npc.FightTarget.IsValid)
        {
            var aggressor = _world.FindChar(npc.FightTarget);
            if (aggressor != null && !aggressor.IsDead && !aggressor.IsDeleted &&
                IsAttackable(aggressor))
            {
                ActFight(npc, aggressor, 50);
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
                        _world.MoveCharacter(npc, goPos);
                        FinishGoOrder(npc);
                        break;
                    }
                    int goDist = npc.Position.GetDistanceTo(goPos);
                    if (goDist > 1)
                        MoveToward(npc, goPos, run: goDist > 3);
                    else
                        FinishGoOrder(npc);
                    break;
                }

                Character followTarget = ResolvePetTargetCharacter(npc, "FOLLOW_TARGET") ?? master;
                // Source-X @NPCActFollow RETURN 1 = GIVE UP following entirely
                // (NPC_Act_Follow returns false), not a one-tick pause.
                if (OnNpcActFollow?.Invoke(npc, followTarget) == true)
                {
                    npc.PetAIMode = PetAIMode.Stay;
                    break;
                }
                if (npc.MapIndex != followTarget.MapIndex)
                {
                    _world.MoveCharacter(npc, followTarget.Position);
                    break;
                }
                int dist = npc.Position.GetDistanceTo(followTarget.Position);
                bool leashed = PetFollowMaxDistance > 0 && dist > PetFollowMaxDistance;
                if (dist > 2 && !leashed)
                    MoveToward(npc, followTarget.Position, run: dist > 3);
                break;
            }
            case PetAIMode.Guard:
            {
                Character guardTarget = ResolvePetTargetCharacter(npc, "GUARD_TARGET") ?? master;
                if (npc.FightTarget.IsValid)
                {
                    var current = _world.FindChar(npc.FightTarget);
                    if (current != null && !current.IsDead && !current.IsDeleted && IsAttackable(current))
                    {
                        ActFight(npc, current, 50);
                        return;
                    }
                    npc.FightTarget = Serial.Invalid;
                }
                // Master'ın saldırdığı hedefe otomatik katıl
                if (master.FightTarget.IsValid)
                {
                    var masterTarget = _world.FindChar(master.FightTarget);
                    if (masterTarget != null && !masterTarget.IsDead && IsAttackable(masterTarget) && masterTarget != npc)
                    {
                        npc.FightTarget = masterTarget.Uid;
                        ActFight(npc, masterTarget, 50);
                        return;
                    }
                }
                foreach (var ch in _world.GetCharsInRange(guardTarget.Position, 6))
                {
                    if (ch == npc || ch == guardTarget || ch.IsDead || !IsAttackable(ch)) continue;
                    if (ch.FightTarget == guardTarget.Uid)
                    {
                        npc.FightTarget = ch.Uid;
                        ActFight(npc, ch, 50);
                        return;
                    }
                }
                int guardDist = npc.Position.GetDistanceTo(guardTarget.Position);
                if (guardDist > 3 &&
                    (PetFollowMaxDistance <= 0 || guardDist <= PetFollowMaxDistance))
                    MoveToward(npc, guardTarget.Position, run: true);
                break;
            }
            case PetAIMode.Attack:
            {
                Character? target = ResolvePetTargetCharacter(npc, "ATTACK_TARGET");
                if (target == null && master.FightTarget.IsValid)
                    target = _world.FindChar(master.FightTarget);
                if (target == null && npc.FightTarget.IsValid)
                    target = _world.FindChar(npc.FightTarget);
                if (target != null && !target.IsDead && IsAttackable(target))
                {
                    npc.FightTarget = target.Uid;
                    int motivation = GetAttackMotivation(npc, target);
                    ActFight(npc, target, Math.Max(motivation, 50));
                    return;
                }
                // Target dead/gone — revert to the mode the pet was in before the
                // attack order (Guard/Follow), instead of trailing the master.
                npc.FightTarget = Serial.Invalid;
                npc.RemoveTag("ATTACK_TARGET");
                PetAIMode revertMode = PetAIMode.Follow;
                if (npc.TryGetTag("PREV_PET_MODE", out string? prevTag) &&
                    int.TryParse(prevTag, out int prevVal) &&
                    Enum.IsDefined(typeof(PetAIMode), prevVal) &&
                    (PetAIMode)prevVal != PetAIMode.Attack)
                    revertMode = (PetAIMode)prevVal;
                npc.RemoveTag("PREV_PET_MODE");
                npc.PetAIMode = revertMode;
                int d = npc.Position.GetDistanceTo(master.Position);
                if (d > 2 && (PetFollowMaxDistance <= 0 || d <= PetFollowMaxDistance))
                    MoveToward(npc, master.Position, run: d > 3);
                break;
            }
            case PetAIMode.Stay:
            case PetAIMode.Stop:
                // Stay in place
                break;
        }
    }

    private Character? ResolvePetTargetCharacter(Character npc, string tagName)
    {
        if (!npc.TryGetTag(tagName, out string? uidText) || string.IsNullOrWhiteSpace(uidText))
            return null;
        if (!uint.TryParse(uidText, out uint uid))
            return null;
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
