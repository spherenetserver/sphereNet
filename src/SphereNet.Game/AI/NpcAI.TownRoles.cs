// Town-role brains: guard, healer, vendor, animal, human idle behavior.
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
    /// <summary>
    /// Guard: patrol, attack criminals/murderers in guarded regions.
    /// </summary>
    private void ActGuard(Character npc)
    {
        var region = _world.FindRegion(npc.Position);
        bool isGuarded = region?.IsGuarded ?? false;
        if (!isGuarded)
        {
            // A guard may finish an engaged chase outside town first.
            if (npc.FightTarget.IsValid)
            {
                var chased = _world.FindChar(npc.FightTarget);
                if (chased != null && !chased.IsDead && !chased.IsDeleted)
                {
                    GuardEngage(npc, chased);
                    return;
                }
                npc.FightTarget = Serial.Invalid;
            }

            // Source-X NPC_Act_GoHome: a guard outside guarded territory
            // teleports back to its post; with no valid post it despawns
            // (it used to wander the countryside forever here).
            if (npc.Home.X != 0 || npc.Home.Y != 0)
            {
                var post = new Point3D(npc.Home.X, npc.Home.Y, npc.Home.Z, npc.MapIndex);
                if (npc.Position.GetDistanceTo(post) > 1)
                {
                    _world.MoveCharacter(npc, post);
                    OnNpcTeleport?.Invoke(npc);
                }
                return;
            }
            _world.DeleteObject(npc);
            npc.Delete();
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
        // Source-X re-speaks per NEW target — the old boolean tag was cleared
        // only on the instakill branch, so a melee guard yelled exactly once
        // in its lifetime. Key the latch on the target's UID instead.
        string targetUid = target.Uid.Value.ToString();
        if (!guard.TryGetTag("GUARD_YELLED", out string? yelledFor) || yelledFor != targetUid)
        {
            guard.SetTag("GUARD_YELLED", targetUid);
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

        // Source-X NPC_LookAtCharHealer is RESURRECT-ONLY: it early-returns
        // unless the target is dead. The old cure-poison and heal-wounded
        // passes topped up any non-criminal living creature in range —
        // including grey aggressors mid-fight — with no ally/noto/LOS gate.

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

        // Periodic restock check (vendor brain only). Source-X
        // NPC_Vendor_Restock: the interval comes from the region's
        // RestockVendors tag (minutes) and a NoRestock tag — on the region or
        // the NPC — suppresses restock entirely.
        if (npc.NpcBrain == NpcBrainType.Vendor)
        {
            var vendorRegion = _world.FindRegion(npc.Position);
            bool noRestock = npc.TryGetTag("NORESTOCK", out _) ||
                             (vendorRegion != null && vendorRegion.TryGetTag("NORESTOCK", out _));
            long intervalMs = VendorRestockIntervalMs;
            if (vendorRegion != null && vendorRegion.TryGetTag("RESTOCKVENDORS", out string? rv) && rv != null &&
                long.TryParse(rv, out long minutes) && minutes > 0)
                intervalMs = minutes * 60_000;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!noRestock &&
                (!npc.TryGetTag("RESTOCK_TIME", out string? rtStr) || !long.TryParse(rtStr, out long lastRestock)
                 || now - lastRestock >= intervalMs))
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

        // Hungry animals/NPCs feed: eat from pack or graze (Source-X
        // NPC_Food / NPC_Act_Food). Both NPC_AI_FOOD (the basic per-tick
        // search) and NPC_AI_INTFOOD (the smarter variant) route through the
        // same feeding pass here — SphereNet has one implementation, so the
        // basic/intelligent distinction is collapsed.
        var foodFlags = GetNpcFlags(npc);
        if ((foodFlags.HasFlag(NpcAIFlags.Food) || foodFlags.HasFlag(NpcAIFlags.IntFood) ||
             npc.TryGetTag("INTFOOD", out _))
            && TryEatFood(npc))
            return;

        if (_rand.Next(12) == 0)
            EmitSound(npc, CreatureSoundType.Idle);
        if (_rand.Next(100) < 20)
            WanderHome(npc);
    }

    /// <summary>Hungry NPC feeds: consume a food item from its pack, hunt down
    /// edibles lying on the ground (Source-X NPC_Act_Food world search — the
    /// hungrier, the farther it will walk for a meal), otherwise (animals)
    /// graze. Tops up the food meter. Returns true if it acted on food.</summary>
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

        // Ground food: Source-X scans out to sight*(100-food)/100 tiles — a
        // starving creature roams the whole view, a peckish one only sniffs
        // nearby. Adjacent food is eaten on the spot; otherwise walk toward it.
        int searchRange = Math.Clamp(14 * (100 - npc.NpcFood) / 100, 2, 14);
        Item? meal = null;
        int best = int.MaxValue;
        foreach (var it in _world.GetItemsInRange(npc.Position, searchRange))
        {
            if (it.IsDeleted || !it.IsOnGround) continue;
            if (it.ItemType is not (ItemType.Food or ItemType.Fruit or ItemType.Grain or ItemType.FoodRaw))
                continue;
            int d = npc.Position.GetDistanceTo(it.Position);
            if (d < best) { best = d; meal = it; }
        }
        if (meal != null)
        {
            if (best <= 1)
            {
                if (meal.Amount > 1) meal.Amount--;
                else _world.RemoveItem(meal);
                npc.NpcFood = (ushort)Math.Min(60, npc.NpcFood + 10);
                EmitSound(npc, CreatureSoundType.Idle);
            }
            else
            {
                MoveToward(npc, meal.Position);
            }
            return true;
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
            if (IsLookAtItemExcluded(item)) continue;
            int dist = npc.Position.GetDistanceTo(item.Position);
            if (OnNpcLookAtItem.Invoke(npc, item, dist, 0).Handled)
                return;
        }
    }

    /// <summary>Items @NPCLookAtItem never fires for (Source-X gates the
    /// trigger on ATTR_MOVE_NEVER | ATTR_LOCKEDDOWN | ATTR_SECURE).</summary>
    private static bool IsLookAtItemExcluded(Item item) =>
        (item.Attributes & (ObjAttributes.Move_Never | ObjAttributes.LockedDown |
            ObjAttributes.Secure)) != 0;

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
}
