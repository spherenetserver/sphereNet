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

/// <summary>
/// NPC AI flags. Maps to NPC_AI_* defines in Source-X CServerConfig.h.
/// </summary>
[Flags]
public enum NpcAIFlags : uint
{
    None = 0,
    Path = 0x0001,
    Food = 0x0002,
    Extra = 0x0004,
    AlwaysInt = 0x0008,
    IntFood = 0x0010,
    // 0x0020 is unused in Source-X (gap between INTFOOD and COMBAT) —
    // bit values below are aligned to the NPC_AI_* defines so a raw
    // script NPC.AI integer maps to the correct behaviours.
    Combat = 0x0040,
    VendTime = 0x0080,
    Looting = 0x0100,
    MoveObstacles = 0x0200,
    PersistentPath = 0x0400,
    Threat = 0x0800,
}

/// <summary>Source-X CRESND_TYPE — creature sound categories.</summary>
public enum CreatureSoundType : byte
{
    Idle = 0,
    Notice = 1,
    Hit = 2,
    GetHit = 3,
    Die = 4,
}

/// <summary>
/// NPC AI engine. Maps to CChar::NPC_* functions in Source-X CCharNPCAct.cpp.
/// Handles brain-based decision making and action execution per tick.
/// </summary>
public sealed class NpcAI
{
    public NpcAIFlags Flags { get; set; } =
        NpcAIFlags.Path | NpcAIFlags.Combat | NpcAIFlags.Threat | NpcAIFlags.PersistentPath;

    public Action<Character>? OnNpcFacingChanged { get; set; }

    public enum NpcDecisionType
    {
        None = 0,
        Move = 1,
        Legacy = 2
    }

    public readonly record struct NpcDecision(
        uint NpcUid,
        NpcDecisionType Type,
        Point3D TargetPos,
        Direction Direction,
        long NextActionTick);

    private readonly GameWorld _world;
    private readonly Pathfinder _pathfinder;
    private readonly SphereConfig _config;
    private static Random _rand => Random.Shared;

    // Cached paths per NPC UID — avoids recalculating every tick
    private readonly Dictionary<uint, List<Point3D>> _pathCache = [];
    private readonly Dictionary<uint, int> _pathIndex = [];
    private readonly Dictionary<uint, long> _pathTime = [];
    private const long PathCacheMaxAge = 10_000;
    // Earliest time A* may be recomputed for this NPC. Survives path-cache
    // drops (unlike _pathTime) so a churning crowd — where the cached step is
    // invalidated almost every tick — cannot trigger a full A* recompute every
    // tick. A successful compute allows the next one after PathThrottleMs; a
    // FAILED compute (unreachable target — the search burns the whole node
    // budget before giving up) backs off for PathFailBackoffMs instead. The
    // direct-step fast path above A* still runs every action, so the NPC
    // resumes immediately once the straight line opens.
    private readonly Dictionary<uint, long> _nextPathfindMs = [];
    private const long PathThrottleMs = 750;
    private const long PathFailBackoffMs = 5_000;
    // Last fight target each NPC announced via OnNpcAttackNotify. Mirrors the
    // player path's per-session notify latch: the "*X is attacking Y!*" emote
    // fires once per target, again only when the NPC switches targets.
    private readonly Dictionary<uint, uint> _lastAttackNotify = [];
    // NPC combat-chase A* node budget. Far below the full Pathfinder cap (which
    // is sized for player half-continent .walk): a creature only needs to route
    // around local obstacles toward a target in sight. Each explored node costs
    // ~35µs (per-tile map/static/object walkability checks), so an unreachable
    // target burns the WHOLE budget in the single-threaded apply phase — at
    // 2000 nodes that was a 40-75ms main-loop stall (visible as a ~160ms ping
    // spike). 500 nodes bounds the worst case to ~17ms and still covers any
    // realistic local detour.
    private const int NpcPathMaxNodes = 500;
    /// <summary>How long a target-less NPC waits before re-running the full
    /// acquire scan (ModernUO ReacquireDelay). Reset to 0 on being attacked.</summary>
    private const long ReacquireDelayMs = 1500;
    private long _lastPathPurge;

    public Func<Character, Character, bool>? OnNpcActFight { get; set; }
    public Func<Character, Character, bool>? OnNpcLookAtChar { get; set; }
    public Func<Character, bool>? OnNpcActWander { get; set; }
    public Func<Character, Character, bool>? OnNpcActFollow { get; set; }
    public Func<Character, Character, SpellType, bool>? OnNpcActCast { get; set; }
    public Func<Character, Item, bool>? OnNpcLookAtItem { get; set; }

    public void PurgeStalePaths()
    {
        long now = Environment.TickCount64;
        if (now - _lastPathPurge < 30_000) return;
        _lastPathPurge = now;

        List<uint>? stale = null;
        foreach (var uid in _pathCache.Keys)
        {
            var obj = _world.FindObject(new Core.Types.Serial(uid));
            bool expired = _pathTime.TryGetValue(uid, out long t) && now - t > PathCacheMaxAge;
            if (obj is not Character ch || ch.IsDeleted || ch.IsDead || expired)
                (stale ??= []).Add(uid);
        }
        if (stale != null)
        {
            foreach (var uid in stale)
            {
                _pathCache.Remove(uid);
                _pathIndex.Remove(uid);
                _pathTime.Remove(uid);
                _losFailCounts.Remove(uid);
                _nextPathfindMs.Remove(uid);
                _lastAttackNotify.Remove(uid);
            }
        }

        // Throttle timestamps can outlive the path cache (they survive path
        // drops by design), so sweep them for gone NPCs here as well.
        if (_nextPathfindMs.Count > 0)
        {
            List<uint>? stalePf = null;
            foreach (var uid in _nextPathfindMs.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue;
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (stalePf ??= []).Add(uid);
            }
            if (stalePf != null)
                foreach (var uid in stalePf)
                    _nextPathfindMs.Remove(uid);
        }

        // LOS-fail counters can accumulate for NPCs that never built a path
        // cache entry (target keeps moving but the straight line stays open),
        // so the path-cache sweep above never reaches them. Purge them here too.
        if (_losFailCounts.Count > 0)
        {
            List<uint>? staleLos = null;
            foreach (var uid in _losFailCounts.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue; // already handled above
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (staleLos ??= []).Add(uid);
            }
            if (staleLos != null)
                foreach (var uid in staleLos)
                    _losFailCounts.Remove(uid);
        }

        // Attack-notify latches outlive the path cache the same way (an NPC
        // can fight without ever pathfinding), so sweep them for gone NPCs.
        if (_lastAttackNotify.Count > 0)
        {
            List<uint>? staleNotify = null;
            foreach (var uid in _lastAttackNotify.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue; // already handled above
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (staleNotify ??= []).Add(uid);
            }
            if (staleNotify != null)
                foreach (var uid in staleNotify)
                    _lastAttackNotify.Remove(uid);
        }
    }

    public NpcAI(GameWorld world, SphereConfig config)
    {
        _world = world;
        _config = config;
        _pathfinder = new Pathfinder(world);
    }


    /// <summary>
    /// Main NPC tick action. Maps to NPC_OnTickAction in Source-X.
    /// Called every tick for each living, non-frozen NPC.
    /// </summary>
    public void OnTickAction(Character npc)
    {
        if (npc.IsPlayer || npc.IsDead || npc.IsStatFlag(StatFlag.Ridden)) return;

        long now = Environment.TickCount64;

        if (npc.IsCasting)
        {
            OnNpcTickSpellCast?.Invoke(npc);
            if (npc.IsCasting)
            {
                npc.NextNpcActionTime = now + 250;
                return;
            }
        }

        if (now < npc.NextNpcActionTime)
            return;

        // Active-area gate: no player in view-range → park the NPC for 30-60s.
        // Pets bypass (they live next to their owner by definition). The long
        // park keeps timer wheel churn low on 100K+ NPC worlds; sector wake
        // in Program.cs reschedules NPCs immediately when a player enters.
        if (!npc.NpcMaster.IsValid && !_world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y))
        {
            npc.NextNpcActionTime = now + 30_000 + _rand.Next(0, 30_000);
            return;
        }

        // NPC tick cadence by role, scaled by CharDef.MoveRate.
        // Source-X: MoveRate default=100 (normal speed). Higher = slower.
        // Base delays: combat 400ms, idle wander 1000ms, service 3-5s.
        bool isActive = npc.FightTarget.IsValid ||
            (npc.NpcMaster.IsValid && npc.PetAIMode is PetAIMode.Attack
                or PetAIMode.Follow or PetAIMode.Come or PetAIMode.Guard);
        bool isService = npc.NpcBrain is NpcBrainType.Vendor or NpcBrainType.Banker
            or NpcBrainType.Stable or NpcBrainType.Healer;

        int moveRate = 100;
        var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
        if (charDef != null && charDef.MoveRate > 0)
            moveRate = charDef.MoveRate;

        // Badly-hurt creatures act a little slower (ModernUO BadlyHurtMoveDelay) —
        // natural "wounded" pacing plus a touch of CPU relief on big fights.
        int hurtDelay = (npc.MaxHits > 0 && npc.Hits > 0 && npc.Hits < npc.MaxHits / 3)
            ? (isActive ? 200 : 400)
            : 0;

        // Source-X NPC move cadence (CCharNPCAct move-tick): the step delay
        // scales with DEX and the creature's MoveRate, NOT a flat rate. Running
        // (combat/chase) is ~250ms at high DEX up to ~1.6s at low DEX; walking
        // (idle wander) is ~1s up to ~2.6s. A flat 400ms made ordinary (mid-DEX)
        // creatures chase noticeably faster than Source-X. Pets run a little
        // faster (DEX floored to 75). Clamped to [100ms, 5s] like Source-X.
        if (isActive)
        {
            int dex = npc.Dex;
            if (npc.NpcMaster.IsValid && dex < 75) dex = 75; // pets run faster
            int range = Math.Max(0, 100 - dex * moveRate / 100) / 5;
            int delay = Math.Clamp(250 + _rand.Next(range + 1) * 100, 100, 5000);
            npc.NextNpcActionTime = now + delay + hurtDelay;
        }
        else if (isService)
            npc.NextNpcActionTime = now + 3000 + _rand.Next(0, 2000);
        else
        {
            int range = Math.Max(0, 100 - npc.Dex * moveRate / 100) / 3;
            int delay = Math.Clamp(1000 + _rand.Next(range + 1) * 100, 100, 5000);
            npc.NextNpcActionTime = now + delay + hurtDelay;
        }

        // Atmospheric special trail — giant spiders web the ground, fire
        // elementals leave fire patches, as they move and fight.
        TryDropSpecialTrail(npc);

        // Pet behavior — owned NPCs follow pet AI mode
        if (npc.NpcMaster.IsValid)
        {
            ActPet(npc);
            return;
        }

        // Perceive nearby players for @NPCSeeNewPlayer greetings (free + skipped
        // entirely when no script hooks the trigger).
        LookForNewPlayers(npc);

        // Brain-based behavior
        switch (npc.NpcBrain)
        {
            case NpcBrainType.Guard:
                ActGuard(npc);
                break;
            case NpcBrainType.Monster:
            case NpcBrainType.Dragon:
                ActMonster(npc);
                break;
            case NpcBrainType.Berserk:
                ActBerserk(npc);
                break;
            case NpcBrainType.Healer:
                ActHealer(npc);
                break;
            case NpcBrainType.Vendor:
            case NpcBrainType.Banker:
            case NpcBrainType.Stable:
                ActVendor(npc);
                break;
            case NpcBrainType.Animal:
                ActAnimal(npc);
                break;
            case NpcBrainType.Human:
            default:
                ActHuman(npc);
                break;
        }
    }

    /// <summary>
    /// Build a deterministic AI decision without mutating world state.
    /// Returns null when no action should be applied this tick.
    /// </summary>
    public NpcDecision? BuildDecision(Character npc, long nowTick)
    {
        if (npc.IsPlayer || npc.IsDead || npc.IsDeleted || npc.IsStatFlag(StatFlag.Ridden))
            return null;
        if (nowTick < npc.NextNpcActionTime)
            return null;

        // Active-area gate: no player nearby → park for 30-60s. Returns a None
        // decision so ApplyDecision sets NextNpcActionTime in the sequential phase
        // (no mutation during parallel compute).
        if (!npc.NpcMaster.IsValid && !_world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y))
        {
            long parkTime = nowTick + 30_000 + DeterministicJitter(npc.Uid.Value, nowTick, 30_000);
            return new NpcDecision(npc.Uid.Value, NpcDecisionType.None, npc.Position, npc.Direction, parkTime);
        }

        int spread = (int)((npc.Uid.Value * 2654435761u) % 400);
        long nextAction = nowTick + 600 + spread;

        // All brain types route through Legacy → OnTickAction for full Source-X
        // parity. Without this, service brains (Banker, Stable, Human, Animal)
        // only do deterministic wander and lose their ActVendor/ActHuman/ActAnimal logic.
        // Casting NPCs also need Legacy to run OnNpcTickSpellCast.
        return new NpcDecision(npc.Uid.Value, NpcDecisionType.Legacy, npc.Position, npc.Direction, nextAction);
    }

    /// <summary>
    /// Apply a previously computed decision in a single-threaded phase.
    /// </summary>
    public void ApplyDecision(NpcDecision decision)
    {
        var npc = _world.FindChar(new Serial(decision.NpcUid));
        if (npc == null || npc.IsDeleted || npc.IsDead || npc.IsPlayer)
            return;

        switch (decision.Type)
        {
            case NpcDecisionType.Move:
                npc.NextNpcActionTime = decision.NextActionTick;
                npc.Direction = decision.Direction;
                if (CanNpcMoveTo(npc, decision.TargetPos))
                    _world.MoveCharacter(npc, decision.TargetPos);
                break;
            case NpcDecisionType.Legacy:
                // Let OnTickAction own the cadence update; setting NextNpcActionTime
                // before the legacy call would make the combat brain return early.
                OnTickAction(npc);
                break;
            default:
                npc.NextNpcActionTime = decision.NextActionTick;
                break;
        }
    }

    /// <summary>
    /// Guard: patrol, attack criminals/murderers in guarded regions.
    /// </summary>
    private void ActGuard(Character npc)
    {
        var region = _world.FindRegion(npc.Position);
        bool isGuarded = region?.IsGuarded ?? false;
        if (!isGuarded)
        {
            Wander(npc);
            return;
        }

        if (npc.FightTarget.IsValid)
        {
            var assigned = _world.FindChar(npc.FightTarget);
            if (assigned != null && !assigned.IsDead && !assigned.IsDeleted)
            {
                GuardEngage(npc, assigned);
                return;
            }
            npc.FightTarget = Serial.Invalid;
        }

        // Guards periodically try to reveal hidden players (ModernUO DetectHidden).
        TryDetectHidden(npc);

        bool guardMurderers = _config.GuardsOnMurderers;
        foreach (var target in _world.GetCharsInRange(npc.Position, 12))
        {
            if (target == npc || target.IsDead) continue;
            bool isCriminal = target.IsStatFlag(StatFlag.Criminal) || target.IsCriminal;
            bool isMurderer = guardMurderers && target.IsMurderer;
            if (isCriminal || isMurderer)
            {
                npc.FightTarget = target.Uid;
                GuardEngage(npc, target);
                return;
            }
        }

        Wander(npc);
    }

    /// <summary>Pet follow gives up beyond this distance on the same map
    /// (reference parity: UO_MAP_VIEW_RADAR = 36); it resumes when the owner
    /// comes back in range. 0 disables the leash.</summary>
    public static int PetFollowMaxDistance { get; set; } = 36;

    /// <summary>Idle fidget animation hook — wired by the server to the
    /// body-aware animation broadcast.</summary>
    public Action<Character>? OnNpcFidget { get; set; }

    /// <summary>Open an unlocked closed door for an NPC (state flip +
    /// observer broadcast — wired by the server). Returns true when the
    /// door ended up open so the blocked step can be re-validated.</summary>
    public Func<Character, Item, bool>? OnNpcOpenDoor { get; set; }

    public Action<Character, string>? OnNpcSay { get; set; }
    public Action<Character>? OnGuardLightningStrike { get; set; }
    public Action<Character>? OnNpcTeleport { get; set; }

    /// <summary>
    /// Redirect all idle guard-brain NPCs in range toward a hostile target.
    /// Called by the "Guards!" speech handler so existing patrols respond.
    /// </summary>
    public void AlertGuardsInRange(Point3D center, Character hostile, int range = 14)
    {
        foreach (var npc in _world.GetCharsInRange(center, range))
        {
            if (npc.IsPlayer || npc.IsDead || npc.IsDeleted) continue;
            if (npc.NpcBrain != NpcBrainType.Guard) continue;
            if (npc.FightTarget.IsValid) continue;

            npc.FightTarget = hostile.Uid;
            npc.NextNpcActionTime = 0;
            OnWakeNpc?.Invoke(npc);
        }
    }

    private void GuardEngage(Character guard, Character target)
    {
        if (!guard.TryGetTag("GUARD_YELLED", out _))
        {
            guard.SetTag("GUARD_YELLED", "1");
            OnNpcSay?.Invoke(guard, "Halt, villain! Guards!");
        }

        if (guard.MapIndex != target.MapIndex) return;
        int dist = guard.Position.GetDistanceTo(target.Position);
        if (_config.GuardsInstantKill)
        {
            if (dist > 20) return;
            if (dist > 1)
            {
                _world.MoveCharacter(guard, target.Position);
                OnNpcTeleport?.Invoke(guard);
            }
            OnGuardLightningStrike?.Invoke(target);
            target.Hits = 0;
            guard.FightTarget = Serial.Invalid;
            guard.RemoveTag("GUARD_YELLED");
            OnNpcKill?.Invoke(guard, target);
        }
        else
        {
            if (dist <= GetAttackRange(guard))
                TrySwingAttack(guard, target);
            else
                MoveToward(guard, target.Position);
        }
    }

    /// <summary>
    /// Monster/Dragon: look for targets to attack, fight, or wander.
    /// Source-X: NPC_Act_Idle → NPC_LookAround → NPC_LookAtCharMonster + NPC_Act_Fight.
    /// </summary>
    private void ActMonster(Character npc)
    {
        int sightRange = GetNpcSight(npc);

        // Forced target override (bard provoke / scripted constant focus) takes
        // priority over normal target selection.
        if (TryForcedTarget(npc))
            return;

        // If we have an existing target, check if it's still valid
        if (npc.FightTarget.IsValid)
        {
            var current = _world.FindChar(npc.FightTarget);
            if (current != null && !current.IsDead && !current.IsDeleted && IsAttackable(current))
            {
                // Remember where the target was while we can see it, and clear
                // any hidden-pursuit state.
                npc.SetTag("LAST_TGT_LOC", $"{current.X},{current.Y},{current.Z},{current.MapIndex}");
                npc.RemoveTag("HIDE_PURSUIT");

                int curMotivation = GetAttackMotivation(npc, current);
                if (curMotivation > 0)
                {
                    // Periodically scan for better target (Source-X: NPC_Act_Fight → NPC_LookAround)
                    if (_rand.Next(4) == 0)
                    {
                        var (betterTarget, betterMotivation) = FindBestTarget(npc, sightRange);
                        if (betterTarget != null && !betterTarget.IsDeleted && betterTarget != current && betterMotivation > curMotivation)
                        {
                            npc.FightTarget = betterTarget.Uid;
                            npc.Memory_Fight_Start(betterTarget);
                            current = betterTarget;
                            curMotivation = betterMotivation;
                        }
                    }

                    ActFight(npc, current, curMotivation);
                    return;
                }
                if (curMotivation < 0)
                {
                    // Negative motivation = fear: flee from the target instead of
                    // silently dropping it (Source-X NPC_LookAtChar fear path).
                    // ActFight routes motivation < 0 into ActFlee.
                    ActFight(npc, current, curMotivation);
                    return;
                }
                // motivation == 0: neutral — let go of the target.
            }
            else if (current != null && !current.IsDead && !current.IsDeleted &&
                     (current.IsStatFlag(StatFlag.Hidden) || current.IsStatFlag(StatFlag.Invisible)))
            {
                // Target hid — don't give up instantly (ServUO reveal behavior):
                // try to reveal it, or move to its last known spot for a few ticks.
                if (PursueHiddenTarget(npc, current))
                    return;
            }
            npc.FightTarget = Serial.Invalid;
            npc.RemoveTag("HIDE_PURSUIT");
            npc.RemoveTag("LAST_TGT_LOC");
        }

        // No current target — scan for a new one, but throttle the full-range
        // scan so idle NPCs don't sweep every tick (ModernUO ReacquireDelay).
        // RecordAttack zeroes NextNpcReacquireTime so retaliation is immediate.
        long nowReac = Environment.TickCount64;
        if (nowReac >= npc.NextNpcReacquireTime)
        {
            var (bestTarget, bestMotivation) = FindBestTarget(npc, sightRange);
            if (bestTarget != null && bestMotivation > 0)
            {
                npc.NextNpcReacquireTime = 0;
                npc.FightTarget = bestTarget.Uid;
                npc.Memory_Fight_Start(bestTarget);
                EmitSound(npc, CreatureSoundType.Notice);
                NotifyNearbyAllies(npc, bestTarget);
                ActFight(npc, bestTarget, bestMotivation);
                return;
            }
            // Nothing found — back off the next scan.
            npc.NextNpcReacquireTime = nowReac + ReacquireDelayMs;
        }

        npc.FightTarget = Serial.Invalid;

        // No enemy — looters scavenge nearby corpses (Source-X NPC_AI_LOOTING).
        if ((Flags.HasFlag(NpcAIFlags.Looting) || npc.TryGetTag("LOOTING", out _))
            && TryLoot(npc))
            return;

        if (_rand.Next(8) == 0)
            EmitSound(npc, _rand.Next(2) == 0 ? CreatureSoundType.Idle : CreatureSoundType.Notice);
        WanderHome(npc);
    }

    /// <summary>Looter NPCs walk to a nearby corpse with contents and take one
    /// item into their pack (Source-X NPC_Act_Looting). Empty corpses are
    /// skipped, so no separate loot-memory is needed. Returns true if busy.</summary>
    private bool TryLoot(Character npc)
    {
        if (npc.Backpack == null) return false;

        Item? corpse = null;
        int best = int.MaxValue;
        foreach (var it in _world.GetItemsInRange(npc.Position, 4))
        {
            if (it.IsDeleted || it.ItemType != ItemType.Corpse || it.Contents.Count == 0) continue;
            int d = npc.Position.GetDistanceTo(it.Position);
            if (d < best) { best = d; corpse = it; }
        }
        if (corpse == null) return false;

        if (best > 1)
        {
            MoveToward(npc, corpse.Position);
            return true;
        }

        // Adjacent — grab one random item.
        if (corpse.Contents.Count > 0)
        {
            var loot = corpse.Contents[_rand.Next(corpse.Contents.Count)];
            if (OnNpcLookAtItem?.Invoke(npc, loot) != true) // script may veto/handle
            {
                corpse.RemoveItem(loot);
                npc.Backpack.AddItem(loot);
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
    /// Alert nearby same-type NPCs to join the fight. Source-X parity:
    /// monsters of the same body type rally when one detects a threat.
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
        if (OnNpcActFight?.Invoke(npc, target) == true)
            return;

        // Source-X: flee when motivation < 0 (non-pets only)
        if (!npc.IsStatFlag(StatFlag.Pet) && motivation < 0)
        {
            npc.FleeStepsMax = 20;
            npc.FleeStepsCurrent = 0;
            ActFlee(npc, target);
            return;
        }

        // Already fleeing? Continue.
        if (npc.FleeStepsCurrent > 0 && npc.FleeStepsCurrent < npc.FleeStepsMax)
        {
            ActFlee(npc, target);
            return;
        }
        npc.FleeStepsCurrent = 0;

        // Home leash: a wild (non-pet, masterless) monster that has chased its
        // target too far from home gives up and walks back, instead of trailing
        // a player across the whole map. Pets/summons follow their master and
        // are exempt; NPCs without a home anchor can't leash.
        if (!npc.IsStatFlag(StatFlag.Pet) && !npc.NpcMaster.IsValid &&
            TryResolveHome(npc, out Point3D leashHome, out int leashWander))
        {
            int homeDist = npc.Position.GetDistanceTo(leashHome);
            int leash = Math.Max(leashWander * 2, 18);
            if (homeDist > leash)
            {
                npc.FightTarget = Serial.Invalid;
                ClearLosFailCount(npc);
                // Severely lost — way past the leash radius: teleport straight
                // home (Source-X lost-NPC go-home behaviour). @NPCLostTeleport
                // fires first; RETURN 1 cancels the teleport and the NPC walks
                // back instead. Safe to fire here: ActChase runs in the serial
                // ApplyDecision phase.
                if (homeDist > leash * 3 &&
                    Character.OnNpcLostTeleport?.Invoke(npc) != true)
                {
                    _world.MoveCharacter(npc, leashHome);
                    return;
                }
                MoveToward(npc, leashHome, run: true);
                return;
            }
        }

        // Past every give-up branch (flee, leash) — the NPC is committed to
        // this fight. Announce the engagement once per target so the victim
        // sees the aggression while the NPC is still closing in, not only
        // after the first hit lands.
        if (!_lastAttackNotify.TryGetValue(npc.Uid.Value, out uint lastNotified) ||
            lastNotified != target.Uid.Value)
        {
            _lastAttackNotify[npc.Uid.Value] = target.Uid.Value;
            OnNpcAttackNotify?.Invoke(npc, target);
        }

        int dist = npc.Position.GetDistanceTo(target.Position);
        bool hasLOS = _world.CanSeeLOS(npc.Position, target.Position);

        // No line of sight — pathfind around obstacles to reach target
        if (!hasLOS && dist > 1)
        {
            IncrementLosFailCount(npc);
            int losFails = GetLosFailCount(npc);
            // Stuck for a while — a caster that knows Teleport blinks toward the
            // target instead of giving up (ModernUO OnFailedMove smart-AI).
            if (losFails >= 8 && npc.NpcSpells.Contains(SpellType.Teleport)
                && npc.Mana >= npc.Int / 4 && _rand.Next(3) == 0)
            {
                ClearLosFailCount(npc);
                if (!TryNpcCastSpell(npc, target, SpellType.Teleport))
                    OnNpcCastSpell?.Invoke(npc, target, SpellType.Teleport);
                return;
            }
            if (losFails > 15)
            {
                npc.FightTarget = Serial.Invalid;
                ClearLosFailCount(npc);
                return;
            }
            MoveToward(npc, target.Position, run: true);
            return;
        }
        ClearLosFailCount(npc);

        // HP-based tactical retreat: melee-only NPCs briefly disengage when
        // critically wounded, then re-engage. Casters already kite via spells.
        if (npc.NpcSpells.Count == 0 && npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 4
            && dist <= 1 && _rand.Next(3) == 0)
        {
            MoveAway(npc, target.Position);
            EmitSound(npc, CreatureSoundType.GetHit);
            return;
        }

        // Random idle combat sound (Source-X: Berserk or 1/6 chance)
        if (npc.NpcBrain == NpcBrainType.Berserk || _rand.Next(6) == 0)
            EmitSound(npc, CreatureSoundType.Idle);

        // Dragon breath: fires for Dragon brain, dragon-family bodies, fire-immune
        // monsters, or explicit BREATH.DAM tag. The body check matches the legacy
        // body-derived creature type: script packs routinely keep
        // BRAIN=brain_monster on dragons and still expect the breath attack.
        bool canBreath = npc.NpcBrain == NpcBrainType.Dragon || IsDragonBody(npc.BodyId);
        if (!canBreath)
        {
            var breathDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            canBreath = breathDef != null && (breathDef.Can & CanFlags.C_FireImmune) != 0
                        && npc.NpcBrain is NpcBrainType.Monster or NpcBrainType.Dragon or NpcBrainType.Berserk;
        }
        if (!canBreath)
            canBreath = npc.TryGetTag("BREATH.DAM", out _);

        if (canBreath && dist <= 8 && npc.Stam >= npc.MaxStam / 2 && hasLOS)
        {
            long now = Environment.TickCount64;
            long nextBreath = 0;
            if (npc.TryGetTag("BREATH_CD", out string? cdStr))
                long.TryParse(cdStr, out nextBreath);
            if (now >= nextBreath)
            {
                int breathDmg = GetBreathDamage(npc);
                if (breathDmg > 0)
                {
                    npc.Stam = (short)Math.Max(0, npc.Stam - 10);
                    npc.SetTag("BREATH_CD", (now + 3000).ToString());
                    OnNpcBreath?.Invoke(npc, target, breathDmg);
                    return;
                }
            }
        }

        // Object throwing (Source-X: NPCACT_THROWING, range 2-8, stam >= 50%)
        if (dist >= 2 && dist <= 8 && npc.Stam >= npc.MaxStam / 2
            && hasLOS && npc.TryGetTag("THROWOBJ", out _))
        {
            int throwDmg = Math.Max(1, npc.Dex / 4 + _rand.Next(npc.Dex / 4 + 1));
            int throwMin = 2, throwMax = 8;
            if (npc.TryGetTag("THROWRANGE", out string? trStr) && !string.IsNullOrWhiteSpace(trStr))
            {
                var parts = trStr.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int mn) && int.TryParse(parts[1], out int mx))
                { throwMin = mn; throwMax = mx; }
                else if (int.TryParse(parts[0], out int single))
                    throwMax = single;
            }
            if (npc.TryGetTag("THROWDAM", out string? tdStr) && !string.IsNullOrWhiteSpace(tdStr))
            {
                var parts = tdStr.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int lo) && int.TryParse(parts[1], out int hi))
                    throwDmg = lo + _rand.Next(hi - lo + 1);
                else if (int.TryParse(parts[0], out int flat))
                    throwDmg = flat;
            }
            if (dist >= throwMin && dist <= throwMax)
            {
                npc.Stam = (short)Math.Max(0, npc.Stam - _rand.Next(4, 11));
                OnNpcThrow?.Invoke(npc, target, throwDmg);
                return;
            }
        }

        // NPC spellcasting — requires LOS for ranged spells
        if (hasLOS && TryNpcCastSpell(npc, target, dist))
            return;

        // Melee / ranged
        var weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        var range = CombatHelper.GetWeaponRange(weapon);

        // Ranged kiting (Source-X NPC_FightArchery): when the target has closed
        // inside the weapon's minimum range a ranged attacker cannot fire, so
        // back off to reopen the gap (≈50%) instead of standing locked in melee.
        if (range.Max > 1 && dist < range.Min)
        {
            if (_rand.Next(2) == 0)
                MoveAway(npc, target.Position);
            return;
        }

        if (dist <= range.Max)
        {
            // Surround/flank sidesteps only happen on ticks where the swing
            // did NOT fire (recoil window). Moving in the same tick as a
            // fired swing makes the client cancel the attack animation —
            // the same move-pair conflict class as the pet GO/follow bug.
            bool swung = TrySwingAttack(npc, target);
            if (!swung && dist <= 1 && _rand.Next(3) == 0)
                TrySurroundStep(npc, target);
        }
        else
        {
            // When closing distance, approach from an open flank if possible
            if (dist <= 3)
                MoveTowardFlank(npc, target);
            else
                MoveToward(npc, target.Position, run: true);
        }
    }

    /// <summary>
    /// Step to an open adjacent tile around the target to surround it.
    /// Picks a random unoccupied neighbor of the target that is still in melee range.
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
                if (!occupied)
                    candidates[count++] = (tx, ty);
            }
        }

        if (count == 0) return;

        var pick = candidates[_rand.Next(count)];
        var mapData = _world.MapData;
        sbyte nz = mapData?.GetEffectiveZ(npc.MapIndex, pick.x, pick.y, npc.Z) ?? npc.Z;
        if (Math.Abs(nz - npc.Z) > 12) return;
        var pos = new Point3D(pick.x, pick.y, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, pos)) return;

        // Face the step direction before moving. The 0x77 move packet carries this
        // direction; if it doesn't match the actual tile delta the client can't
        // walk-animate and snaps the NPC ("1-tile teleport" during combat
        // surround steps). The next swing re-faces the target via TrySwingAttack.
        npc.Direction = npc.Position.GetDirectionTo(pos);
        _world.MoveCharacter(npc, pos);
    }

    /// <summary>
    /// Move toward the target but prefer an unoccupied flank direction.
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

                if (!occupied)
                {
                    var approachPos = new Point3D(adjX, adjY, target.Z, target.MapIndex);
                    MoveToward(npc, approachPos, run: true);
                    return;
                }
            }
        }

        // All flanks occupied, just go direct
        MoveToward(npc, target.Position, run: true);
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

    /// <summary>Source-X: NPC_Act_Flee — step-counted retreat with kiting spellcast.</summary>
    private void ActFlee(Character npc, Character target)
    {
        npc.FleeStepsCurrent++;
        if (npc.FleeStepsCurrent >= npc.FleeStepsMax)
        {
            npc.FleeStepsCurrent = 0;
            npc.FightTarget = Serial.Invalid;
            return;
        }

        int dist = npc.Position.GetDistanceTo(target.Position);

        // Kiting: cast a spell while fleeing if mana allows (every 3rd step)
        if (npc.NpcSpells.Count > 0 && npc.Mana >= npc.Int / 3
            && dist >= 2 && dist <= 8
            && npc.FleeStepsCurrent % 3 == 0
            && _world.CanSeeLOS(npc.Position, target.Position))
        {
            var (spell, castTarget) = ChooseBestSpell(npc, target, dist);
            if (spell != SpellType.None && !TryNpcCastSpell(npc, castTarget, spell))
                OnNpcCastSpell?.Invoke(npc, castTarget, spell);
        }

        // Self-heal while fleeing (every 4th step)
        if (npc.NpcSpells.Count > 0 && npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 3
            && npc.FleeStepsCurrent % 4 == 0)
        {
            if (npc.NpcSpells.Contains(SpellType.GreaterHeal))
            {
                if (!TryNpcCastSpell(npc, npc, SpellType.GreaterHeal))
                    OnNpcCastSpell?.Invoke(npc, npc, SpellType.GreaterHeal);
            }
            else if (npc.NpcSpells.Contains(SpellType.Heal))
            {
                if (!TryNpcCastSpell(npc, npc, SpellType.Heal))
                    OnNpcCastSpell?.Invoke(npc, npc, SpellType.Heal);
            }
        }

        // Scout retreat: a creature that can hide vanishes mid-flee once it has
        // some distance, breaking pursuit (ServUO OrcScout guerilla style). The
        // pursuer then loses LOS and falls into hidden-target pursuit.
        if (dist >= 4 && !npc.IsStatFlag(StatFlag.Hidden)
            && npc.GetSkill(SkillType.Hiding) > 0 && _rand.Next(8) == 0)
        {
            npc.SetStatFlag(StatFlag.Hidden);
            npc.FleeStepsCurrent = 0;
            npc.FightTarget = Serial.Invalid;
            return;
        }

        // Pathfinder-based escape: find a direction away from threat
        FleeAway(npc, target.Position);
    }

    /// <summary>Honor a forced combat target: bard Provocation (PROVOKED_TARGET)
    /// or a scripted ConstantFocus (CONSTANT_FOCUS). Returns true if engaged.</summary>
    private bool TryForcedTarget(Character npc)
    {
        Serial uid = Serial.Invalid;
        bool isProvoke = false;
        if (npc.TryGetTag("PROVOKED_TARGET", out string? pt) && uint.TryParse(pt, out uint pu))
        {
            uid = new Serial(pu);
            isProvoke = true;
        }
        else if (npc.TryGetTag("CONSTANT_FOCUS", out string? cf) && uint.TryParse(cf, out uint cu))
        {
            uid = new Serial(cu);
        }
        if (!uid.IsValid) return false;

        var t = _world.FindChar(uid);
        if (t == null || t.IsDead || t.IsDeleted || t == npc || !IsAttackable(t))
        {
            if (isProvoke) npc.RemoveTag("PROVOKED_TARGET"); // expired/dead provoke clears
            return false;
        }
        npc.FightTarget = t.Uid;
        ActFight(npc, t, 100);
        return true;
    }

    private void FleeAway(Character npc, Point3D threat)
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
        sbyte nz = mapData?.GetEffectiveZ(npc.MapIndex, nx, ny, npc.Z) ?? npc.Z;

        if (Math.Abs(nz - npc.Z) <= 12)
        {
            var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
            if (CanNpcMoveTo(npc, newPos))
            {
                var fleeDir = npc.Position.GetDirectionTo(newPos);
                npc.Direction = run ? fleeDir | Direction.Running : fleeDir;
                _world.MoveCharacter(npc, newPos);
                return;
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
                sbyte az = mapData?.GetEffectiveZ(npc.MapIndex, ax, ay, npc.Z) ?? npc.Z;
                if (Math.Abs(az - npc.Z) > 12) continue;
                var altPos = new Point3D(ax, ay, az, npc.MapIndex);
                if (CanNpcMoveTo(npc, altPos))
                {
                    var altFleeDir = npc.Position.GetDirectionTo(altPos);
                    npc.Direction = run ? altFleeDir | Direction.Running : altFleeDir;
                    _world.MoveCharacter(npc, altPos);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Source-X: NPC_FightMagery — attempt to cast a spell at the target.
    /// Requires INT >= 5, spell list on NPC, mana available.
    /// When mana drops below 25% of INT, NPC abandons magic and goes melee.
    /// At melee range with good mana, steps back to gain casting distance.
    /// </summary>
    private bool TryNpcCastSpell(Character npc, Character target, int dist)
    {
        if (npc.Int < 5)
            return false;
        if (npc.NpcSpells.Count == 0)
        {
            // No scripted spell list — derive one from a carried/equipped
            // spellbook (Source-X NPC_AddSpellsFromBook). Tried once per NPC.
            EnsureNpcSpellsFromBook(npc);
            if (npc.NpcSpells.Count == 0)
                return false;
        }

        int mana = npc.Mana;
        int intStat = npc.Int;

        // Mana depleted — switch to melee tactics entirely
        if (mana < intStat / 4)
            return false;

        if (dist > GetNpcSight(npc))
            return false;

        // At melee range with sufficient mana, step back to gain casting distance
        if (dist <= 1 && mana >= intStat / 3)
        {
            MoveAway(npc, target.Position);
            return true;
        }

        // NoMagic region check
        var region = _world.FindRegion(npc.Position);
        if (region != null && region.NoMagic)
            return false;

        // Mana-based cast chance (Source-X formula)
        int chance = mana >= intStat / 2 ? mana : intStat - mana;
        if (_rand.Next(chance + 1) < intStat / 4)
        {
            // Failed chance — kite to maintain distance while mana regens
            if (mana > intStat / 3)
            {
                if (dist < 4)
                    MoveAway(npc, target.Position);
                else if (dist > 8)
                    MoveToward(npc, target.Position, run: true);
                return true;
            }
            return false;
        }

        var (spell, castTarget) = ChooseBestSpell(npc, target, dist);
        if (spell == SpellType.None)
            return false;

        if (TryNpcCastSpell(npc, castTarget, spell))
            return true;

        OnNpcCastSpell?.Invoke(npc, castTarget, spell);
        return true;
    }

    private bool TryNpcCastSpell(Character npc, Character target, SpellType spell) =>
        OnNpcActCast?.Invoke(npc, target, spell) == true;

    /// <summary>
    /// Intelligent spell selection. Priority:
    /// 1. Self-cure if poisoned
    /// 2. Self-heal if HP &lt; 50%
    /// 3. Dispel if target is summoned
    /// 4. Area spells if 3+ enemies nearby
    /// 5. High damage at close range
    /// 6. Ranged damage at distance
    /// 7. Fallback: random non-reflected spell
    /// </summary>
    /// <summary>Pick the best spell AND the character it should be cast on.
    /// Beneficial spells (heal/cure) return the wounded recipient (self or a
    /// hurt ally); harmful spells return the enemy. Previously every spell was
    /// cast on the enemy, so "self-heal" actually healed the enemy.</summary>
    private (SpellType Spell, Character CastTarget) ChooseBestSpell(Character npc, Character target, int dist)
    {
        var spells = npc.NpcSpells;
        bool targetReflects = target.IsStatFlag(StatFlag.Reflection);

        // 1. Self-cure if poisoned (50% chance — don't loop on cure forever)
        if (npc.IsPoisoned && _rand.Next(2) == 0)
        {
            if (spells.Contains(SpellType.Cure)) return (SpellType.Cure, npc);
            if (spells.Contains(SpellType.ArchCure)) return (SpellType.ArchCure, npc);
        }

        // 2. Self-heal if HP < 50% — flat 33% chance at any wound level (aggressive
        //    bias: even a critically-hurt caster still spends ~2/3 of its casts on
        //    offense instead of turtling on heals). Below 25% it prefers GreaterHeal.
        if (npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 2 && _rand.Next(3) == 0)
        {
            if (npc.Hits < npc.MaxHits / 4 && spells.Contains(SpellType.GreaterHeal))
                return (SpellType.GreaterHeal, npc);
            if (spells.Contains(SpellType.Heal)) return (SpellType.Heal, npc);
            if (spells.Contains(SpellType.GreaterHeal)) return (SpellType.GreaterHeal, npc);
        }

        // 2b. Group support — heal/cure a wounded ally (Source-X NPC_FightCast
        //     GOOD-spell path). Only caster NPCs with a heal/cure spell scan.
        bool hasHeal = spells.Contains(SpellType.Heal) || spells.Contains(SpellType.GreaterHeal);
        if ((hasHeal || spells.Contains(SpellType.Cure)) && _rand.Next(2) == 0)
        {
            var ally = FindWoundedAlly(npc);
            if (ally != null)
            {
                if (ally.IsPoisoned && spells.Contains(SpellType.Cure))
                    return (SpellType.Cure, ally);
                if (ally.MaxHits > 0 && ally.Hits < ally.MaxHits / 4 && spells.Contains(SpellType.GreaterHeal))
                    return (SpellType.GreaterHeal, ally);
                if (spells.Contains(SpellType.Heal)) return (SpellType.Heal, ally);
                if (spells.Contains(SpellType.GreaterHeal)) return (SpellType.GreaterHeal, ally);
            }
        }

        // 3. Dispel if target is summoned
        if (target.IsSummoned)
        {
            if (spells.Contains(SpellType.Dispel)) return (SpellType.Dispel, target);
            if (spells.Contains(SpellType.MassDispel)) return (SpellType.MassDispel, target);
        }

        // 3b. Spell combo chain (ServUO/Source-X mage burst): lock the target
        //     down with Paralyze, then unload Explosion/EnergyBolt/Poison while
        //     it can't move. Returns None unless a combo is active/startable.
        var combo = NextComboSpell(npc, target, targetReflects);
        if (combo != SpellType.None) return (combo, target);

        // 4. Area spells if 3+ enemies nearby
        int nearbyEnemies = 0;
        foreach (var ch in _world.GetCharsInRange(target.Position, 3))
        {
            if (ch == npc || ch.IsDead) continue;
            if (ch.IsPlayer || (ch.NpcBrain != npc.NpcBrain && ch.BodyId != npc.BodyId))
                nearbyEnemies++;
        }
        if (nearbyEnemies >= 3)
        {
            if (spells.Contains(SpellType.MeteorSwarm) && !targetReflects)
                return (SpellType.MeteorSwarm, target);
            if (spells.Contains(SpellType.ChainLightning) && !targetReflects)
                return (SpellType.ChainLightning, target);
        }

        // 5. Paralyze a fleeing target to set up damage — but never spam it.
        //    Skip a target that is already frozen, AND enforce a per-caster
        //    cooldown so a target that breaks free instantly (trapped pouch,
        //    expiry) isn't re-locked ahead of the damage spells below. Without
        //    the cooldown the caster loops on Paralyze forever — the locked or
        //    re-locked target stays >4 tiles away, so this rule keeps firing
        //    and no damage is ever dealt. Between locks it falls through to
        //    Poison/direct damage.
        if (dist > 4 && spells.Contains(SpellType.Paralyze) && !targetReflects
            && !target.IsStatFlag(StatFlag.Freeze))
        {
            long now = Environment.TickCount64;
            long paraNext = 0;
            if (npc.TryGetTag("PARA_CD", out string? pcd)) long.TryParse(pcd, out paraNext);
            if (now >= paraNext)
            {
                npc.SetTag("PARA_CD", (now + ParalyzeRecastCooldownMs).ToString());
                return (SpellType.Paralyze, target);
            }
        }

        // 6. Poison if target isn't poisoned yet
        if (!target.IsPoisoned && spells.Contains(SpellType.Poison) && !targetReflects)
        {
            if (_rand.Next(3) == 0) return (SpellType.Poison, target);
        }

        // 7. Damage spells — prefer high damage, avoid if target reflects
        if (!targetReflects)
        {
            if (dist <= 4)
            {
                if (spells.Contains(SpellType.Explosion)) return (SpellType.Explosion, target);
                if (spells.Contains(SpellType.Flamestrike)) return (SpellType.Flamestrike, target);
            }
            if (spells.Contains(SpellType.EnergyBolt)) return (SpellType.EnergyBolt, target);
            if (spells.Contains(SpellType.Lightning)) return (SpellType.Lightning, target);
            if (spells.Contains(SpellType.Fireball)) return (SpellType.Fireball, target);
            if (spells.Contains(SpellType.Harm)) return (SpellType.Harm, target);
            if (spells.Contains(SpellType.MagicArrow)) return (SpellType.MagicArrow, target);
        }

        // 8. Curse/Weaken debuffs (safe against reflect)
        if (spells.Contains(SpellType.Curse) && _rand.Next(4) == 0) return (SpellType.Curse, target);
        if (spells.Contains(SpellType.Weaken) && _rand.Next(4) == 0) return (SpellType.Weaken, target);

        // 9. Fallback: random non-reflected spell (skip heals/cures on enemies)
        int startIdx = _rand.Next(spells.Count);
        for (int i = 0; i < spells.Count; i++)
        {
            var spell = spells[(startIdx + i) % spells.Count];
            if (spell == SpellType.None) continue;
            if (spell is SpellType.Heal or SpellType.GreaterHeal or SpellType.Cure or SpellType.ArchCure)
                continue;
            if (targetReflects && spell is SpellType.Lightning or SpellType.EnergyBolt
                or SpellType.Explosion or SpellType.Flamestrike or SpellType.MagicArrow
                or SpellType.Fireball or SpellType.Harm or SpellType.MeteorSwarm
                or SpellType.ChainLightning)
                continue;
            return (spell, target);
        }

        return (SpellType.None, target);
    }

    /// <summary>Periodically try to reveal nearby hidden players (ModernUO
    /// DetectHidden). Throttled per-NPC (NEXT_DETECT tag); chance scales with
    /// the NPC's DetectingHidden vs the target's Hiding/Stealth.</summary>
    private bool TryDetectHidden(Character npc)
    {
        long now = Environment.TickCount64;
        if (npc.TryGetTag("NEXT_DETECT", out string? nd) && long.TryParse(nd, out long t) && now < t)
            return false;
        // Smarter NPCs scan more often (8-30s).
        int intervalMs = Math.Clamp(30000 - npc.Int * 100, 8000, 30000);
        npc.SetTag("NEXT_DETECT", (now + intervalMs).ToString());

        int detectSkill = npc.GetSkill(SkillType.DetectingHidden);
        bool any = false;
        foreach (var ch in _world.GetCharsInRange(npc.Position, 6))
        {
            if (ch == npc || ch.IsDead || ch.IsDeleted) continue;
            if (!ch.IsStatFlag(StatFlag.Hidden) && !ch.IsStatFlag(StatFlag.Invisible)) continue;
            int conceal = Math.Max(ch.GetSkill(SkillType.Hiding), ch.GetSkill(SkillType.Stealth));
            int chance = Math.Clamp((detectSkill - conceal / 2) / 10 + 20, 5, 95); // percent
            if (_rand.Next(100) < chance && ch.ClearHiddenState())
            {
                Character.OnAppearanceChanged?.Invoke(ch); // re-show to nearby clients
                any = true;
            }
        }
        return any;
    }

    /// <summary>Populate an NPC's spell list from a carried/equipped magery
    /// spellbook (Source-X NPC_AddSpellsFromBook). The book stores a 64-bit
    /// bitmask (More2:More1) where bit i = magery spell (i+1). Tried once.</summary>
    private static void EnsureNpcSpellsFromBook(Character npc)
    {
        if (npc.TryGetTag("SPELLS_LOADED", out _)) return;
        npc.SetTag("SPELLS_LOADED", "1");

        Item? book = FindSpellbook(npc);
        if (book != null)
        {
            ulong bits = ((ulong)book.More2 << 32) | book.More1;
            for (int i = 0; i < 64; i++)
                if ((bits & (1UL << i)) != 0)
                    npc.NpcSpellAdd((SpellType)(i + 1));
        }

        if (npc.NpcSpells.Count == 0)
            AddDefaultSpellsForCasterBody(npc);
    }

    private static void AddDefaultSpellsForCasterBody(Character npc)
    {
        if (!IsLichBody(npc.BodyId))
            return;

        npc.NpcSpellAdd(SpellType.MagicArrow);
        npc.NpcSpellAdd(SpellType.Harm);
        npc.NpcSpellAdd(SpellType.Fireball);
        npc.NpcSpellAdd(SpellType.Poison);
        npc.NpcSpellAdd(SpellType.Curse);
        npc.NpcSpellAdd(SpellType.Lightning);
        npc.NpcSpellAdd(SpellType.MindBlast);
        npc.NpcSpellAdd(SpellType.Paralyze);
        npc.NpcSpellAdd(SpellType.EnergyBolt);
        npc.NpcSpellAdd(SpellType.Explosion);
        npc.NpcSpellAdd(SpellType.Flamestrike);
    }

    private static bool IsLichBody(ushort bodyId) =>
        bodyId is 0x0018 or 0x004E or 0x004F or 0x033E;

    /// <summary>Default hireling pay interval (ms) when HIRE_PERIOD isn't set.</summary>
    private const long DefaultHirePeriodMs = 30 * 60 * 1000; // 30 minutes

    /// <summary>Deduct a hireling's wage from the master's bank box. Returns
    /// false (triggering desertion) if the bank can't cover the wage.</summary>
    private static bool TryPayHireling(Character master, int wage)
    {
        var bank = master.GetEquippedItem(Layer.BankBox);
        if (bank == null) return false;

        long gold = 0;
        foreach (var it in bank.Contents)
            if (it.ItemType == ItemType.Gold) gold += it.Amount;
        if (gold < wage) return false;

        int remaining = wage;
        for (int i = bank.Contents.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var it = bank.Contents[i];
            if (it.ItemType != ItemType.Gold) continue;
            if (it.Amount <= remaining) { remaining -= it.Amount; bank.RemoveItem(it); it.Delete(); }
            else { it.Amount = (ushort)(it.Amount - remaining); remaining = 0; }
        }
        return true;
    }

    private static Item? FindSpellbook(Character npc)
    {
        var held = npc.GetEquippedItem(Layer.OneHanded);
        if (held?.ItemType == ItemType.Spellbook) return held;
        held = npc.GetEquippedItem(Layer.TwoHanded);
        if (held?.ItemType == ItemType.Spellbook) return held;
        var pack = npc.Backpack;
        if (pack != null)
            foreach (var it in pack.Contents)
                if (it.ItemType == ItemType.Spellbook) return it;
        return null;
    }

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

    /// <summary>Source-X NPC_GetAttackContinueMotivation morale: the penalty to
    /// fight-motivation from being weaker / more hurt than the target. Returns a
    /// value ≤ 0 (0 when the NPC is not outmatched), so it only ever pushes the
    /// creature toward fleeing, never toward extra aggression. Exposed for tests.</summary>
    internal static int GetMoralePenalty(Character npc, Character target)
    {
        int myHpPct = npc.MaxHits > 0 ? npc.Hits * 100 / npc.MaxHits : 100;
        int targetHpPct = target.MaxHits > 0 ? target.Hits * 100 / target.MaxHits : 100;
        int morale = (npc.Str - target.Str) + (myHpPct - targetHpPct) - (npc.Int / 16);
        return morale < 0 ? morale : 0;
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
            if (!TryNpcCastSpell(npc, npc, SpellType.Reveal))
                OnNpcCastSpell?.Invoke(npc, npc, SpellType.Reveal);
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
        Character? best = null;
        int bestPct = 80; // only consider allies below 80% HP
        Character? poisoned = null;
        foreach (var ch in _world.GetCharsInRange(npc.Position, 8))
        {
            if (ch == npc || ch.IsDead || ch.IsDeleted || ch.IsPlayer) continue;
            if (ch.MaxHits <= 0) continue;
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

    /// <summary>Spell-combo state machine. Tags COMBO_STEP / COMBO_TARGET track
    /// progress. Step 0 may start a combo (Paralyze) when mana is high and the
    /// target is free; later steps unload damage while the target is locked.
    /// Returns None when no combo applies.</summary>
    private SpellType NextComboSpell(Character npc, Character target, bool targetReflects)
    {
        if (targetReflects) { npc.RemoveTag("COMBO_STEP"); return SpellType.None; }
        var spells = npc.NpcSpells;

        int step = 0;
        if (npc.TryGetTag("COMBO_STEP", out string? s)) int.TryParse(s, out step);

        // Abandon a combo aimed at a different target.
        if (step > 0 && npc.TryGetTag("COMBO_TARGET", out string? ct) &&
            (!uint.TryParse(ct, out uint ctUid) || ctUid != target.Uid.Value))
        {
            npc.RemoveTag("COMBO_STEP");
            step = 0;
        }

        if (step > 0)
        {
            // Advance the chain. Each step falls through to the next available
            // spell so a missing spell doesn't stall the combo.
            npc.RemoveTag("COMBO_STEP");
            if (step <= 1)
            {
                if (spells.Contains(SpellType.Explosion)) { npc.SetTag("COMBO_STEP", "2"); return SpellType.Explosion; }
                step = 2;
            }
            if (step <= 2)
            {
                if (spells.Contains(SpellType.EnergyBolt)) { npc.SetTag("COMBO_STEP", "3"); return SpellType.EnergyBolt; }
                step = 3;
            }
            if (step <= 3 && !target.IsPoisoned && spells.Contains(SpellType.Poison))
                return SpellType.Poison; // final hit, combo ends
            return SpellType.None;
        }

        // Try to START a combo: high mana, target not already locked, and we
        // have Paralyze plus at least one follow-up.
        if (npc.Mana >= npc.Int * 2 / 3 && npc.Mana > 40
            && !target.IsStatFlag(StatFlag.Freeze)
            && spells.Contains(SpellType.Paralyze)
            && (spells.Contains(SpellType.Explosion) || spells.Contains(SpellType.EnergyBolt))
            && _rand.Next(3) == 0)
        {
            npc.SetTag("COMBO_STEP", "1");
            npc.SetTag("COMBO_TARGET", target.Uid.Value.ToString());
            return SpellType.Paralyze;
        }
        return SpellType.None;
    }

    // Special-trail creatures (Source-X NPC Action_StartSpecial): the giant
    // spider lays web on the ground, the fire elemental drops fire patches.
    private const ushort GiantSpiderBody = 0x001C;
    private const ushort FireElementalBody = 0x000F;
    private const ushort DefaultWebId = 0x10D5;   // spider web tile
    private const ushort DefaultFireId = 0x398C;  // fire column tile
    // Below this value a trail tag is read as a bare on/off flag (e.g. "1"),
    // not a graphic override; at or above it the value overrides the tile id.
    private const ushort TrailIdOverrideFloor = 0x0100;

    /// <summary>Giant spiders web the ground and fire elementals leave fire
    /// patches as they act (Source-X NPC Action_StartSpecial). Enabled by the
    /// known giant-spider / fire-elemental body, or by a WEBTRAIL / FIRETRAIL
    /// tag — whose value, when it looks like a tile id, overrides the graphic.
    /// Fire patches carry FIELD_DAMAGE so a creature stepping on one is hurt
    /// (the same field path the fire-field spell uses); webs are an atmospheric
    /// obstacle. At most one drop per few ticks, never stacked on a tile.</summary>
    private void TryDropSpecialTrail(Character npc)
    {
        if (npc.IsDead) return;

        bool hasFireTag = npc.TryGetTag("FIRETRAIL", out string? fireTag);
        bool hasWebTag = npc.TryGetTag("WEBTRAIL", out string? webTag);
        bool fire = hasFireTag || npc.BodyId == FireElementalBody;
        bool web = !fire && (hasWebTag || npc.BodyId == GiantSpiderBody);
        if (!fire && !web) return;

        // ~1 in 4 acting ticks, and never two trails on the same tile.
        if (_rand.Next(4) != 0) return;
        foreach (var existing in _world.GetItemsInRange(npc.Position, 0))
        {
            if (!existing.IsDeleted && existing.TryGetTag("SPECIAL_TRAIL", out _))
                return;
        }

        ushort itemId = fire ? DefaultFireId : DefaultWebId;
        string? tagVal = fire ? fireTag : webTag;
        if (!string.IsNullOrWhiteSpace(tagVal) && TryParseTileId(tagVal, out ushort overrideId)
            && overrideId >= TrailIdOverrideFloor)
            itemId = overrideId;

        var item = _world.CreateItem();
        item.BaseId = itemId;
        item.SetTag("SPECIAL_TRAIL", "1");
        long now = Environment.TickCount64;
        if (fire)
        {
            item.Name = "fire";
            int dmg = Math.Clamp(npc.Str / 25, 2, 15);
            item.SetTag("FIELD_DAMAGE", dmg.ToString());
            item.DecayTime = now + 10_000;
        }
        else
        {
            item.Name = "web";
            item.DecayTime = now + 20_000;
        }
        _world.PlaceItem(item, npc.Position);
    }

    /// <summary>Parse a tile id written as hex (0x10D5) or decimal.</summary>
    private static bool TryParseTileId(string s, out ushort id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(s.AsSpan(2),
                System.Globalization.NumberStyles.HexNumber, null, out id);
        return ushort.TryParse(s, out id);
    }

    /// <summary>Dragon-family bodies (reference CREID list): dragon grey/red,
    /// drakes, wyvern, serpentine/skeletal dragons, shadow/white wyrm, swamp
    /// dragon, ancient wyrm. These breathe regardless of the scripted brain.</summary>
    private static bool IsDragonBody(ushort bodyId) => bodyId switch
    {
        0x000C or 0x003B or 0x003C or 0x003D or 0x003E or
        0x0067 or 0x0068 or 0x006A or 0x00B4 or 0x031A or 0x031E => true,
        _ => false,
    };

    /// <summary>Source-X: BREATH.DAM — defaults to STR*5/100, clamped 1-200.</summary>
    private static int GetBreathDamage(Character npc)
    {
        if (npc.TryGetTag("BREATH.DAM", out string? dmgStr) && int.TryParse(dmgStr, out int custom))
            return Math.Clamp(custom, 1, 500);
        int dmg = npc.Str * 5 / 100;
        return Math.Clamp(dmg, 1, 200);
    }

    /// <summary>
    /// Find the best target in sight range by motivation score.
    /// Source-X: NPC_LookAround → NPC_LookAtCharMonster loop.
    /// Only acquires targets within line of sight.
    /// </summary>
    /// <summary>Cap on candidates fully evaluated (LOS + motivation) per
    /// acquisition. Without it, a dense crowd makes each NPC evaluate every
    /// nearby mobile, turning target acquisition into O(n²) across the area and
    /// stalling the tick. Normal encounters have far fewer than this, so the
    /// cap is invisible in ordinary play; in a crowd, any of the nearest few is
    /// an equally valid target.</summary>
    private const int MaxTargetCandidates = 40;

    /// <summary>Minimum gap between a caster's Paralyze re-casts on its target
    /// (ChooseBestSpell rule 5). Stops the lock-down rule from firing ahead of
    /// the damage spells every tick — including against a target that breaks
    /// free instantly (trapped pouch) — so the caster actually deals damage
    /// between locks instead of looping on Paralyze.</summary>
    private const int ParalyzeRecastCooldownMs = 12000;

    private (Character? target, int motivation) FindBestTarget(Character npc, int sightRange)
    {
        // Rank candidates by motivation WITHOUT line-of-sight first: LOS
        // raycasts against the real static map are the dominant cost under
        // dense combat (one per candidate would be O(crowd) raycasts per NPC).
        // Keep the top 3, then verify LOS lazily on those — so each NPC does at
        // most a few raycasts regardless of crowd size.
        Character? t1 = null, t2 = null, t3 = null;
        int m1 = 0, m2 = 0, m3 = 0;
        int evaluated = 0;

        foreach (var ch in _world.GetCharsInRange(npc.Position, sightRange))
        {
            if (ch == npc || !IsAttackable(ch)) continue;
            // Bound the scan over ALL attackable candidates (not just hostile
            // ones): a monster in a crowd of its own kind would otherwise
            // evaluate every neighbour before finding the few real enemies,
            // making acquisition O(local density). Capping here means each NPC
            // considers its nearest handful — a target farther back in a crowd
            // is some closer NPC's problem, so combat still engages.
            if (++evaluated > MaxTargetCandidates) break;
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
        int hostility = GetHostilityLevel(npc, target);
        if (hostility <= 0)
            return hostility;

        var region = _world.FindRegion(target.Position);
        if (region != null && region.IsFlag(RegionFlag.Safe))
            return 0;

        if (npc.NpcBrain == NpcBrainType.Berserk || npc.NpcBrain == NpcBrainType.Guard)
            return 100;

        int motivation = hostility;

        // Bonus for current target (Source-X: +10)
        if (npc.FightTarget == target.Uid)
            motivation += 10;

        // Distance penalty
        motivation -= npc.Position.GetDistanceTo(target.Position);

        // Prioritize wounded targets (easier kills)
        if (target.MaxHits > 0 && target.Hits < target.MaxHits / 3)
            motivation += 15;

        // Prioritize dangerous targets: casters/healers are high-value
        if (!target.IsPlayer && target.NpcSpells.Count > 0)
            motivation += 10;
        if (!target.IsPlayer && target.NpcBrain == NpcBrainType.Healer)
            motivation += 20;

        // Targets actively attacking us get priority (retaliation)
        if (target.FightTarget == npc.Uid)
            motivation += 15;

        // Threat: stick to whoever has dealt the most damage to us (Source-X
        // NPC_AI_THREAT / NPC_FightFindBestTarget). Global flag, on by default;
        // drives tank/aggro mechanics off the accumulated attacker list.
        if (Flags.HasFlag(NpcAIFlags.Threat))
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
        // Applied as a penalty only, so it never inflates aggression beyond
        // the hostility/threat model above.
        if (_config.MonsterFear)
            motivation += GetMoralePenalty(npc, target);

        return motivation;
    }

    /// <summary>
    /// Source-X: NPC_GetHostilityLevelToward — base hostility by creature type.
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

        // Players and berserk always hostile
        if (target.IsPlayer || npc.NpcBrain == NpcBrainType.Berserk)
            return 100;

        // NPC vs NPC
        if (!target.IsPlayer)
        {
            if (!_config.MonsterFight)
                return 0;

            // Same body type → never attack own kind
            if (npc.BodyId == target.BodyId)
                return -100;

            // Same brain type → mild alliance
            if (npc.NpcBrain == target.NpcBrain)
                return -30;

            return 100;
        }

        return 0;
    }

    /// <summary>
    /// Source-X: Fight_IsAttackable — checks if a character can be targeted.
    /// </summary>
    private static bool IsAttackable(Character ch)
    {
        if (ch.IsDead) return false;
        if (ch.IsStatFlag(StatFlag.Invul)) return false;
        if (ch.IsStatFlag(StatFlag.Stone)) return false;
        if (ch.IsStatFlag(StatFlag.Invisible)) return false;
        if (ch.IsStatFlag(StatFlag.Hidden)) return false;
        if (ch.IsStatFlag(StatFlag.Insubstantial)) return false;
        return true;
    }

    /// <summary>
    /// NPC sight range. Source-X uses a per-NPC GetSight() value (typically 14-16).
    /// We use INT-based range: smarter monsters see further.
    /// </summary>
    private static int GetNpcSight(Character npc)
    {
        int baseRange = 10 + Math.Clamp(npc.Int / 20, 0, 8);
        return Math.Min(baseRange, 18);
    }

    /// <summary>Source-X: SoundChar — emit a creature sound via callback.</summary>
    private void EmitSound(Character npc, CreatureSoundType type)
    {
        OnNpcSound?.Invoke(npc, type);
    }

    /// <summary>Healer: resurrect dead, cure poison, heal wounded. Range 5, refuse criminals/evil.</summary>
    private void ActHealer(Character npc)
    {
        const int healerRange = 5;

        // Priority 1: resurrect dead players in range
        foreach (var ch in _world.GetCharsInRange(npc.Position, healerRange))
        {
            if (ch == npc || !ch.IsDead || !ch.IsPlayer) continue;
            if (ch.IsCriminal || ch.IsMurderer) continue;

            if (!ch.IsInWarMode)
            {
                OnNpcSay?.Invoke(npc, ServerMessages.Get(Msg.NpcHealerManifest));
                continue;
            }

            if (npc.Position.GetDistanceTo(ch.Position) > 2)
            {
                MoveToward(npc, ch.Position);
                return;
            }

            OnHealerAction?.Invoke(npc, ch, true);
            return;
        }

        // Priority 2: cure poisoned allies
        foreach (var ch in _world.GetCharsInRange(npc.Position, healerRange))
        {
            if (ch == npc || ch.IsDead || !ch.IsPoisoned) continue;
            if (ch.IsCriminal || ch.IsMurderer) continue;

            OnHealerCure?.Invoke(npc, ch);
            ch.CurePoison();
            return;
        }

        // Priority 3: heal wounded friendly NPCs/players (HP < 50%)
        foreach (var ch in _world.GetCharsInRange(npc.Position, healerRange))
        {
            if (ch == npc || ch.IsDead) continue;
            if (ch.IsCriminal || ch.IsMurderer) continue;
            if (ch.MaxHits > 0 && ch.Hits < ch.MaxHits / 2)
            {
                int heal = Math.Max(1, npc.Int / 5);
                ch.Hits = (short)Math.Min(ch.Hits + heal, ch.MaxHits);
                OnHealerAction?.Invoke(npc, ch, false);
                return;
            }
        }

        ActHuman(npc);
    }

    /// <summary>Callback: healer performs action. Parameters: healer, target, isResurrect.
    /// Used by Program.cs to broadcast cast animation and sound.</summary>
    public Action<Character, Character, bool>? OnHealerAction { get; set; }

    /// <summary>Callback: healer cures poison. Parameters: healer, target.
    /// Program.cs broadcasts cure animation/sound.</summary>
    public Action<Character, Character>? OnHealerCure { get; set; }

    /// <summary>Callback: vendor needs restocking. Program.cs fires @NPCRestock trigger.</summary>
    public Action<Character>? OnVendorRestock { get; set; }

    private const int VendorRestockIntervalMs = 10 * 60 * 1000; // 10 minutes

    /// <summary>Vendor/Banker/Stable: stay near home, barely move, periodic restock.</summary>
    private void ActVendor(Character npc)
    {
        CheckWitnessCrime(npc);

        // Periodic restock check (vendor brain only)
        if (npc.NpcBrain == NpcBrainType.Vendor)
        {
            long now = Environment.TickCount64;
            if (!npc.TryGetTag("RESTOCK_TIME", out string? rtStr) || !long.TryParse(rtStr, out long lastRestock)
                || now - lastRestock >= VendorRestockIntervalMs)
            {
                OnVendorRestock?.Invoke(npc);
                npc.SetTag("RESTOCK_TIME", now.ToString());
            }
        }

        if (!TryResolveHome(npc, out Point3D home, out _))
            return;

        int dist = npc.Position.GetDistanceTo(home);
        if (dist > 3)
        {
            MoveToward(npc, home);
            return;
        }

        if (_rand.Next(100) < 3)
            Wander(npc);
    }

    /// <summary>Animal: wander, flee from combat.</summary>
    private void ActAnimal(Character npc)
    {
        // Timid animals back off from the nearest threat until they reach a safe
        // distance, instead of only stepping away once (ModernUO Backoff state).
        // A threat is anyone in war mode or actively targeting this animal.
        const int threatRange = 8;
        Character? threat = null;
        int nearest = int.MaxValue;
        foreach (var ch in _world.GetCharsInRange(npc.Position, threatRange))
        {
            if (ch == npc || ch.IsDead || ch.IsDeleted) continue;
            if (!ch.IsStatFlag(StatFlag.War) && ch.FightTarget != npc.Uid) continue;
            int d = npc.Position.GetDistanceTo(ch.Position);
            if (d < nearest) { nearest = d; threat = ch; }
        }
        if (threat != null)
        {
            MoveAway(npc, threat.Position);
            return;
        }

        // Hungry animals/NPCs with IntFood feed: eat from pack or graze
        // (Source-X NPC_Act_Food).
        if ((Flags.HasFlag(NpcAIFlags.IntFood) || npc.TryGetTag("INTFOOD", out _))
            && TryEatFood(npc))
            return;

        if (_rand.Next(12) == 0)
            EmitSound(npc, CreatureSoundType.Idle);
        if (_rand.Next(100) < 20)
            WanderHome(npc);
    }

    /// <summary>Hungry NPC feeds: consume a food item from its pack, otherwise
    /// (animals) graze. Tops up the food meter. Returns true if it fed.</summary>
    private bool TryEatFood(Character npc)
    {
        if (npc.NpcFood >= 50) return false;

        var pack = npc.Backpack;
        if (pack != null)
        {
            foreach (var it in pack.Contents)
            {
                if (it.ItemType is ItemType.Food or ItemType.Fruit or ItemType.Grain or ItemType.FoodRaw)
                {
                    if (it.Amount > 1) it.Amount--;
                    else { pack.RemoveItem(it); it.Delete(); }
                    npc.NpcFood = (ushort)Math.Min(60, npc.NpcFood + 10);
                    EmitSound(npc, CreatureSoundType.Idle);
                    return true;
                }
            }
        }
        // Grazers eat the grass wherever they roam.
        if (npc.NpcBrain == NpcBrainType.Animal && _rand.Next(4) == 0)
        {
            npc.NpcFood = (ushort)Math.Min(60, npc.NpcFood + 5);
            EmitSound(npc, CreatureSoundType.Idle);
            return true;
        }
        return false;
    }

    /// <summary>Callback: NPC witnesses a crime and calls guards. Parameters: witness, criminal.</summary>
    public Action<Character, Character>? OnWitnessCrime { get; set; }

    /// <summary>Human: idle, look around, wander occasionally. Witnesses crimes.</summary>
    private void ActHuman(Character npc)
    {
        CheckWitnessCrime(npc);
        LookAtNearbyItems(npc);

        if (_rand.Next(100) < 10)
            WanderHome(npc);
    }

    /// <summary>Fire @NPCSeeNewPlayer for nearby players this NPC hasn't perceived
    /// recently. Gated on the static hook (null when no script hooks the trigger)
    /// plus a throttle, so the per-NPC range scan only runs when actually needed.</summary>
    private void LookForNewPlayers(Character npc)
    {
        if (Character.OnNpcSeeNewPlayer == null || _rand.Next(4) != 0) return;

        long now = Environment.TickCount64;
        foreach (var ch in _world.GetCharsInRange(npc.Position, 12))
        {
            if (!ch.IsPlayer || ch.IsDead || ch.IsDeleted) continue;
            if (!_world.CanSeeLOS(npc.Position, ch.Position)) continue;
            npc.SeeNewPlayer(ch, now); // fires @NPCSeeNewPlayer on a first sighting
        }
    }

    private void LookAtNearbyItems(Character npc)
    {
        if (OnNpcLookAtItem == null || _rand.Next(8) != 0) return;

        foreach (var item in _world.GetItemsInRange(npc.Position, 3))
        {
            if (item.IsDeleted || item.ContainedIn.IsValid) continue;
            if (OnNpcLookAtItem.Invoke(npc, item))
                return;
        }
    }

    /// <summary>
    /// Crime witness: civilian NPCs in guarded regions report nearby criminals.
    /// Source-X parity: townsfolk yell "Guards!" when they see crime.
    /// </summary>
    private void CheckWitnessCrime(Character npc)
    {
        if (_rand.Next(5) != 0) return;

        var region = _world.FindRegion(npc.Position);
        if (region == null || !region.IsFlag(RegionFlag.Guarded)) return;

        foreach (var ch in _world.GetCharsInRange(npc.Position, 6))
        {
            if (ch == npc || ch.IsDead || ch.IsDeleted) continue;
            if (!ch.IsPlayer) continue;
            if (!ch.IsStatFlag(StatFlag.Criminal) && !ch.IsCriminal && !ch.IsMurderer) continue;
            if (!_world.CanSeeLOS(npc.Position, ch.Position)) continue;

            OnNpcSay?.Invoke(npc, "Guards! A villain!");
            npc.Memory_AddObjTypes(ch.Uid, MemoryType.SawCrime);
            OnWitnessCrime?.Invoke(npc, ch);
            return;
        }
    }

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

        // Hireling wage (Source-X NPC_OnHirePay): a hired NPC (HIRE_WAGE tag) is
        // paid from the master's bank box on a timer; it deserts when unpaid.
        if (npc.TryGetTag("HIRE_WAGE", out string? wageStr) &&
            int.TryParse(wageStr, out int wage) && wage > 0)
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
                if (TryPayHireling(master, wage))
                    npc.SetTag("HIRE_NEXT_PAY", (nowMs + period).ToString());
                else
                {
                    OnNpcSay?.Invoke(npc, "I can no longer be paid. Farewell!");
                    npc.RemoveTag("HIRE_NEXT_PAY");
                    npc.ClearOwnership(clearFriends: true);
                    npc.PetAIMode = PetAIMode.Stay;
                    return;
                }
            }
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
                        npc.RemoveTag("GO_TARGET");
                        npc.PetAIMode = PetAIMode.Stay;
                        break;
                    }
                    int goDist = npc.Position.GetDistanceTo(goPos);
                    if (goDist > 1)
                        MoveToward(npc, goPos, run: goDist > 3);
                    else
                    {
                        npc.RemoveTag("GO_TARGET");
                        npc.PetAIMode = PetAIMode.Stay;
                    }
                    break;
                }

                Character followTarget = ResolvePetTargetCharacter(npc, "FOLLOW_TARGET") ?? master;
                if (OnNpcActFollow?.Invoke(npc, followTarget) == true)
                    break;
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

    /// <summary>
    /// Callback for when an NPC successfully deals damage. Used by Program.cs to broadcast effects.
    /// Parameters: attacker, target, damage dealt
    /// </summary>
    public Action<Character, Character, int>? OnNpcAttack { get; set; }

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

    /// <summary>Callback: NPC casts a spell. Parameters: caster, target, spell.
    /// Program.cs handles SpellEngine.CastStart + broadcast.</summary>
    public Action<Character, Character, SpellType>? OnNpcCastSpell { get; set; }

    /// <summary>Advance an NPC's in-progress spell cast timer. Returns true while still casting.</summary>
    public Func<Character, bool>? OnNpcTickSpellCast { get; set; }

    /// <summary>Callback: dragon breath attack. Parameters: npc, target, damage.</summary>
    public Action<Character, Character, int>? OnNpcBreath { get; set; }

    /// <summary>Callback: NPC throws object. Parameters: npc, target, damage.</summary>
    public Action<Character, Character, int>? OnNpcThrow { get; set; }

    /// <summary>Callback: creature sound. Parameters: npc, sound type.
    /// Program.cs resolves body-specific sound ID and broadcasts 0x54.</summary>
    public Action<Character, CreatureSoundType>? OnNpcSound { get; set; }

    /// <summary>Callback: wake an NPC for immediate action (e.g. retaliation).
    /// Program.cs reschedules the NPC in the timer wheel so it acts next tick.</summary>
    public Action<Character>? OnWakeNpc { get; set; }

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
    ///     <item>Resolve damage and consume one stamina point.</item>
    ///     <item>Set <c>NextAttackTime</c> to the full swing recoil.</item>
    ///   </list>
    /// </summary>
    private bool TrySwingAttack(Character npc, Character target)
    {
        if (npc.IsDead || npc.Hits <= 0 || target.IsDead || target.Hits <= 0)
            return false;

        long now = Environment.TickCount64;
        if (now < npc.NextAttackTime)
            return false;

        // Same gating Source-X applies to player attackers — see
        // GameClient.TrySwingAt for rationale.
        if (npc.Stam <= 0)
        {
            npc.NextAttackTime = now + 1000;
            return false;
        }
        if (npc.IsStatFlag(StatFlag.Freeze) || npc.IsStatFlag(StatFlag.Sleeping))
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

        var prep = CombatHelper.ValidateSwingPrep(
            _world, npc, target, weapon, PrivLevel.Player, now, _world.CanSeeLOS);
        switch (prep.Result)
        {
            case CombatHelper.SwingPrepResult.Abort:
                npc.FightTarget = Serial.Invalid;
                return false;
            case CombatHelper.SwingPrepResult.RetryLater:
                npc.NextAttackTime = now + Math.Max(prep.RetryMs, 250);
                return false;
        }

        int swingDelayMs = SphereNet.Game.Clients.GameClient.GetSwingDelayMs(npc, weapon);
        int stagger = (int)(npc.Uid.Value * 2654435761u % 200);

        var newDir = npc.Position.GetDirectionTo(target.Position);
        if (newDir != npc.Direction)
        {
            npc.Direction = newDir;
            OnNpcFacingChanged?.Invoke(npc);
        }

        npc.NextAttackTime = now + swingDelayMs + stagger;
        npc.BeginSwingRecoil(now, swingDelayMs + stagger);

        if (npc.Stam > 0)
            npc.Stam = (short)(npc.Stam - 1);

        // Source-style owner attribution: pet/summon attacks in guarded towns
        // should criminal-flag the owner when targeting an innocent player.
        if (npc.OwnerSerial.IsValid && target.IsPlayer && Character.AttackingIsACrimeEnabled)
        {
            var owner = npc.ResolveOwnerCharacter();
            if (owner != null && owner.IsPlayer && !owner.IsDead)
            {
                var region = _world.FindRegion(owner.Position);
                bool targetInnocent = !target.IsCriminal && !target.IsMurderer;
                if (targetInnocent && region != null && region.IsFlag(RegionFlag.Guarded))
                    owner.MakeCriminal();
            }
        }

        short hpBefore = npc.Hits;
        int damage = CombatEngine.ResolveAttack(npc, target, weapon, CombatHelper.ActiveCombatFlags);
        OnNpcAttack?.Invoke(npc, target, damage);

        if (damage > 0)
        {
            EmitSound(npc, CreatureSoundType.Hit);
            // The struck target's get-hit vocalization (creature SOUNDGETHIT or a
            // human "oomf") is emitted by the OnNpcAttack hit feedback so it covers
            // both creature and player targets uniformly.

            // Retaliation: NPC targets that aren't already fighting back
            // acquire the attacker as their fight target (Source-X parity).
            if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
            {
                target.FightTarget = npc.Uid;
                target.NextNpcActionTime = 0;
                OnWakeNpc?.Invoke(target);
            }

            if (target.Hits <= 0 && !target.IsDead)
            {
                if (!target.IsPlayer)
                    EmitSound(target, CreatureSoundType.Die);
                OnNpcKill?.Invoke(npc, target);
            }
        }

        // Reactive armor reflect may have killed the attacker
        if (npc.Hits < hpBefore && npc.Hits <= 0 && !npc.IsDead)
        {
            EmitSound(npc, CreatureSoundType.Die);
            OnNpcKill?.Invoke(npc, npc);
        }
        return true;
    }

    // --- Movement helpers ---

    private bool CanNpcEnterTile(Character npc, Point3D pos)
    {
        var mapData = _world.MapData;
        if (mapData == null) return true;

        var terrain = mapData.GetTerrainTile(pos.Map, pos.X, pos.Y);
        var landData = mapData.GetLandTileData(terrain.TileId);

        bool isWater = landData.IsWet;
        if (isWater)
        {
            var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            bool canSwim = charDef != null && (charDef.Can & Core.Enums.CanFlags.C_Swim) != 0;
            if (!canSwim) return false;
        }

        if (IsTileDangerous(npc, pos))
            return false;

        return true;
    }

    private bool IsTileDangerous(Character npc, Point3D pos)
    {
        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            if (!item.TryGetTag("FIELD_DAMAGE", out _))
                continue;
            var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            bool fireImmune = charDef != null && (charDef.Can & CanFlags.C_FireImmune) != 0;
            if (!fireImmune)
                return true;
        }
        return false;
    }

    private bool CanNpcMoveTo(Character npc, Point3D pos)
    {
        if (pos.X < 0 || pos.Y < 0)
            return false;

        if (!CanNpcEnterTile(npc, pos))
            return false;

        bool canPassWalls = CharDefHelper.CanPassWalls(npc);
        if (!canPassWalls)
        {
            var mapData = _world.MapData;
            if (mapData != null && !mapData.IsPassable(pos.Map, pos.X, pos.Y, pos.Z))
                return false;

            foreach (var item in _world.GetItemsInRange(pos, 0))
            {
                if (item.IsStaticBlock)
                    return false;
            }

            foreach (var other in _world.GetCharsInRange(pos, 0))
            {
                if (other == npc || other.IsDeleted || other.IsDead)
                    continue;
                if (other.MapIndex != pos.Map || other.X != pos.X || other.Y != pos.Y)
                    continue;
                if ((other.IsStatFlag(StatFlag.Hidden) || other.IsStatFlag(StatFlag.Invisible))
                    && other.PrivLevel >= PrivLevel.Counsel)
                    continue;
                return false;
            }
        }

        return true;
    }

    private void Wander(Character npc)
    {
        if (OnNpcActWander?.Invoke(npc) == true)
            return;

        // Idle fidget (reference parity: idle NPCs randomly play a fidget
        // animation) — occasionally animate in place instead of stepping so
        // standing NPCs look alive without extra packet pressure.
        if (_rand.Next(8) == 0)
        {
            OnNpcFidget?.Invoke(npc);
            return;
        }

        int dx = _rand.Next(-1, 2);
        int dy = _rand.Next(-1, 2);
        if (dx == 0 && dy == 0) return;

        short nx = (short)(npc.X + dx);
        short ny = (short)(npc.Y + dy);
        var mapData = _world.MapData;
        sbyte nz = mapData?.GetEffectiveZ(npc.MapIndex, nx, ny, npc.Z) ?? npc.Z;
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, newPos))
            return;

        // Face the step direction so the 0x77 move matches the tile delta and the
        // client walk-animates instead of snapping the NPC.
        npc.Direction = npc.Position.GetDirectionTo(newPos);
        _world.MoveCharacter(npc, newPos);
    }

    /// <summary>Wander with home range check. Source-X: m_Home_Dist_Wander.</summary>
    private void WanderHome(Character npc)
    {
        if (!TryResolveHome(npc, out Point3D home, out int homeDist))
        {
            Wander(npc);
            return;
        }

        int curDist = Math.Abs(npc.X - home.X) + Math.Abs(npc.Y - home.Y);
        if (curDist > homeDist)
        {
            MoveToward(npc, home);
            return;
        }
        Wander(npc);
    }

    /// <summary>Home from Character.Home field; legacy TAG.HOME_* fallback.</summary>
    private static bool TryResolveHome(Character npc, out Point3D home, out int wanderDist)
    {
        wanderDist = npc.HomeDist > 0 ? npc.HomeDist : 10;
        if (npc.Home.X != 0 || npc.Home.Y != 0)
        {
            home = new Point3D(npc.Home.X, npc.Home.Y, npc.Home.Z, npc.MapIndex);
            return true;
        }

        if (npc.TryGetTag("HOME_X", out string? hx) && npc.TryGetTag("HOME_Y", out string? hy) &&
            short.TryParse(hx, out short homeX) && short.TryParse(hy, out short homeY))
        {
            sbyte homeZ = npc.Z;
            if (npc.TryGetTag("HOME_Z", out string? hz))
                sbyte.TryParse(hz, out homeZ);
            home = new Point3D(homeX, homeY, homeZ, npc.MapIndex);
            if (npc.TryGetTag("HOME_DIST", out string? hdStr) && int.TryParse(hdStr, out int hd))
                wanderDist = hd;
            return true;
        }

        home = default;
        return false;
    }

    private void MoveToward(Character npc, Point3D target, bool run = false)
    {
        var dir = npc.Position.GetDirectionTo(target);
        GetDirectionDelta(dir, out short dx, out short dy);
        if (run)
            dir |= Direction.Running;

        short nx = (short)(npc.X + dx);
        short ny = (short)(npc.Y + dy);
        var mapData = _world.MapData;
        sbyte nz = mapData?.GetEffectiveZ(npc.MapIndex, nx, ny, npc.Z) ?? npc.Z;
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var directPos = new Point3D(nx, ny, nz, npc.MapIndex);

        bool directBlocked = !CanNpcMoveTo(npc, directPos);

        // Reference parity (NPC door handling in the idle look-at path): a
        // blocked adjacent step may just be a closed door — try to open it
        // (50% per attempt, like the reference) and re-check the tile.
        if (directBlocked && OnNpcOpenDoor != null && _rand.Next(2) == 0)
        {
            var door = FindClosedDoorAt(directPos);
            if (door != null && OnNpcOpenDoor(npc, door))
                directBlocked = !CanNpcMoveTo(npc, directPos);
        }

        if (!directBlocked)
        {
            npc.Direction = dir;
            _world.MoveCharacter(npc, directPos);
            _pathCache.Remove(npc.Uid.Value);
            _pathIndex.Remove(npc.Uid.Value);
            _pathTime.Remove(npc.Uid.Value);
            return;
        }

        if (!Flags.HasFlag(NpcAIFlags.Path))
        {
            npc.Direction = dir;
            return;
        }

        // Direct path blocked — use A* pathfinding
        uint uid = npc.Uid.Value;
        if (!Flags.HasFlag(NpcAIFlags.PersistentPath))
        {
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            _pathTime.Remove(uid);
        }
        if (!_pathCache.TryGetValue(uid, out var path) || path.Count == 0)
        {
            // Throttle full A* recomputes per NPC. In a churning crowd the
            // cached step is blocked nearly every tick, which would otherwise
            // force a fresh A* search every tick for every NPC. Between allowed
            // recomputes just face the target and hold — a closer/unblocked NPC
            // keeps the pressure on, and the path refreshes shortly after.
            long nowMs = Environment.TickCount64;
            if (_nextPathfindMs.TryGetValue(uid, out long nextPf) && nowMs < nextPf)
            {
                npc.Direction = dir;
                return;
            }

            // Calculate new path
            var npcDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            var npcCanFlags = npcDef?.Can ?? Core.Enums.CanFlags.None;
            path = _pathfinder.FindPath(npc.Position, target, npc.MapIndex, npcCanFlags, npc, NpcPathMaxNodes);
            if (path == null || path.Count == 0)
            {
                _nextPathfindMs[uid] = nowMs + PathFailBackoffMs;
                npc.Direction = dir;
                return;
            }
            _nextPathfindMs[uid] = nowMs + PathThrottleMs;
            _pathCache[uid] = path;
            _pathIndex[uid] = 0;
            _pathTime[uid] = Environment.TickCount64;
        }

        int idx = _pathIndex.GetValueOrDefault(uid, 0);
        if (idx >= path.Count)
        {
            // Path exhausted — recalculate
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            _pathTime.Remove(uid);
            return;
        }

        var nextStep = path[idx];

        // Re-validate the cached step before committing it. The path can be up to
        // PathCacheMaxAge old, during which a door may have closed, another mob
        // moved in, or a damage field appeared — walking it blindly would shove
        // the NPC into a blocked/dangerous tile. If it's no longer valid, drop
        // the path and recompute next tick.
        if (!CanNpcMoveTo(npc, nextStep))
        {
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            _pathTime.Remove(uid);
            npc.Direction = npc.Position.GetDirectionTo(nextStep);
            return;
        }

        var stepDir = npc.Position.GetDirectionTo(nextStep);
        npc.Direction = run ? stepDir | Direction.Running : stepDir;
        _world.MoveCharacter(npc, nextStep);
        _pathIndex[uid] = idx + 1;
    }

    /// <summary>Find a closed, unlocked door item on the given tile
    /// (within a reasonable Z window). Internal for tests.</summary>
    internal Item? FindClosedDoorAt(Point3D pos)
    {
        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            if (item.IsDeleted) continue;
            if (Math.Abs(item.Z - pos.Z) > 15) continue;
            if (!DoorHelper.IsDoorItem(item, _world.MapData)) continue;
            if (item.ItemType is ItemType.DoorLocked or ItemType.PortLocked) continue;
            if (item.TryGetTag("DOOR_OPEN", out string? openStr) && openStr == "1") continue;
            return item;
        }
        return null;
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

    private static int GetAttackRange(Character npc, Item? weapon = null)
    {
        weapon ??= npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        return CombatHelper.GetWeaponRange(weapon).Max;
    }

    private void MoveAway(Character npc, Point3D threat)
    {
        var dir = npc.Position.GetDirectionTo(threat);
        GetDirectionDelta(dir, out short dx, out short dy);

        short nx = (short)(npc.X - dx);
        short ny = (short)(npc.Y - dy);
        var mapData = _world.MapData;
        sbyte nz = mapData?.GetEffectiveZ(npc.MapIndex, nx, ny, npc.Z) ?? npc.Z;
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, newPos))
            return;

        npc.Direction = npc.Position.GetDirectionTo(newPos);
        _world.MoveCharacter(npc, newPos);
    }

    private static void GetDirectionDelta(Direction dir, out short dx, out short dy)
    {
        dx = 0; dy = 0;
        switch (dir)
        {
            case Direction.North: dy = -1; break;
            case Direction.NorthEast: dx = 1; dy = -1; break;
            case Direction.East: dx = 1; break;
            case Direction.SouthEast: dx = 1; dy = 1; break;
            case Direction.South: dy = 1; break;
            case Direction.SouthWest: dx = -1; dy = 1; break;
            case Direction.West: dx = -1; break;
            case Direction.NorthWest: dx = -1; dy = -1; break;
        }
    }

    private static int DeterministicJitter(uint uid, long nowTick, int maxExclusive)
    {
        if (maxExclusive <= 0) return 0;
        unchecked
        {
            uint mixed = uid * 2654435761u ^ (uint)nowTick;
            return (int)(mixed % (uint)maxExclusive);
        }
    }

    private static Direction GetDeterministicDirection(uint uid, long nowTick)
    {
        unchecked
        {
            uint mixed = uid * 1103515245u + (uint)nowTick * 12345u;
            return (Direction)(mixed & 0x07);
        }
    }
}
