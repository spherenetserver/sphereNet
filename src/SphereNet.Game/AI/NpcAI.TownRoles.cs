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
    /// <summary>Source-X UO_MAP_VIEW_SIGHT (uofiles_macros.h:17): the distance a
    /// creature looks at without blurring, and the base of the food search.</summary>
    private const int ViewSight = 14;

    /// <summary>
    /// Guard: patrol, attack criminals/murderers in guarded regions.
    /// Source-X NPC_Act_Idle → NPC_LookAround → NPC_LookAtCharGuard, and
    /// NPC_Act_GoHome for a guard that strayed out of guarded ground.
    /// </summary>
    private void ActGuard(Character npc)
    {
        if (npc.FightTarget.IsValid)
        {
            // A guard finishes the fight it is in, inside town or out of it.
            var assigned = _world.FindChar(npc.FightTarget);
            if (assigned != null && assigned.MapIndex == npc.MapIndex && IsAttackable(assigned))
            {
                GuardEngage(npc, assigned);
                return;
            }
            npc.FightTarget = Serial.Invalid;
        }

        if (LookAroundTown(npc))
            return;

        var region = _world.FindRegion(npc.Position);
        bool isGuarded = region?.IsGuarded ?? false;

        // Guards searching for hidden players is a ModernUO behaviour; Source-X
        // guards only see what CanSee lets them see.
        if (isGuarded && HasExtra(npc, NpcAiExtraFlags.DetectHidden))
            TryDetectHidden(npc);

        // NPC_Act_Idle (CCharNPCAct.cpp:1977): a guard out of guarded territory
        // heads home unless OF_GuardOutsideGuardedArea lets it stay.
        bool mayStayOutside = ((OptionFlags)(uint)_config.OptionFlags & OptionFlags.GuardOutsideGuardedArea) != 0;
        if (!isGuarded && !mayStayOutside && GuardGoHome(npc))
            return;

        Wander(npc);
    }

    /// <summary>Source-X NPC_Act_GoHome, guard branch (CCharNPCAct.cpp:1506-1538).
    /// A post inside a guarded area: teleport back to it. A post outside one: the
    /// guard has no guard post and is removed (upstream makes it conjured and
    /// drops its hit points to zero, which kills it without a corpse). No post at
    /// all: NPC_Act_Idle never starts the go-home (:1979), so the guard stays.
    /// Returns true when the tick was spent.</summary>
    private bool GuardGoHome(Character npc)
    {
        if (!TryResolveHome(npc, out Point3D home, out _))
            return false;

        var homeArea = _world.FindRegion(home);
        if (homeArea != null && homeArea.IsGuarded)
        {
            if (npc.MapIndex != home.Map || npc.Position != home)
            {
                _world.MoveCharacter(npc, home);
                OnNpcTeleport?.Invoke(npc);
            }
            return true;
        }

        npc.SetStatFlag(StatFlag.Conjured);
        _world.DeleteObject(npc);
        return true;
    }

    /// <summary>Source-X sm_szSpeakGuardJeer (CCharNPCAct.cpp:710).</summary>
    private static readonly string[] s_guardJeerMsgs =
    [
        Msg.NpcGuardThreat1, Msg.NpcGuardThreat2, Msg.NpcGuardThreat3,
        Msg.NpcGuardThreat4, Msg.NpcGuardThreat5,
    ];

    /// <summary>Source-X sm_szSpeakGuardStrike (CCharNPCAct.cpp:730).</summary>
    private static readonly string[] s_guardStrikeMsgs =
    [
        Msg.NpcGuardStrike1, Msg.NpcGuardStrike2, Msg.NpcGuardStrike3,
        Msg.NpcGuardStrike4, Msg.NpcGuardStrike5,
    ];

    /// <summary>Source-X NPC_LookAtCharGuard (CCharNPCAct.cpp:698). A guard cares
    /// about the criminal and the evil only - a murderer is evil, and so is an
    /// evil-karma creature. Outside guarded ground it only jeers, one time in ten.
    /// Inside, from more than a step away it teleports next to the target when
    /// GUARDSINSTANTKILL is on or it knows any Magery; with GUARDSINSTANTKILL the
    /// target is also left at 1 hit point and struck at once. A new target (or a
    /// guard not yet at war) gets one of the strike lines and the attack.
    /// <paramref name="fromTrigger"/> is CallGuards' call, which skips the
    /// dead/invulnerable/statue/jailed filter.</summary>
    public bool GuardLookAtChar(Character guard, Character target, bool fromTrigger)
    {
        if (target == guard || target.IsDeleted)
            return false;
        // PRIV_JAILED is part of the filter too (CCharNPCAct.cpp:705).
        bool unfit = target.IsStatFlag(StatFlag.Invul) || target.IsDead ||
                     (CharDefHelper.GetCanFlags(target) & CanFlags.C_Statue) != 0 ||
                     (target.IsPlayer && target.IsJailed);
        if ((unfit && !fromTrigger) || !NotoIsCriminal(target))
            return false;

        var targetArea = _world.FindRegion(target.Position);
        if (targetArea == null || !targetArea.IsGuarded)
        {
            if (_rand.Next(10) != 0)
                return false;
            OnNpcSay?.Invoke(guard, ServerMessages.GetFormatted(
                s_guardJeerMsgs[_rand.Next(s_guardJeerMsgs.Length)], target.GetName()));
            FaceToward(guard, target);
            return false;
        }

        bool strikeNow = false;
        if (guard.MapIndex == target.MapIndex && guard.Position.GetDistanceTo(target.Position) > 1)
        {
            if (_config.GuardsInstantKill || guard.GetSkill(SkillType.Magery) > 0)
            {
                _world.MoveCharacter(guard, target.Position);
                OnNpcTeleport?.Invoke(guard);
            }

            // Stat_SetVal(STAT_STR, 1) + Fight_Hit (CCharNPCAct.cpp:745-749): the
            // target is left at one hit point and the guard swings right away.
            if (_config.GuardsInstantKill)
            {
                target.Hits = 1;
                strikeNow = true;
            }
        }

        if (!guard.IsStatFlag(StatFlag.War) || guard.FightTarget != target.Uid)
        {
            OnNpcSay?.Invoke(guard, ServerMessages.Get(s_guardStrikeMsgs[_rand.Next(s_guardStrikeMsgs.Length)]));
            GuardStartFight(guard, target);
        }
        if (strikeNow)
        {
            guard.NextAttackTime = 0;
            TrySwingAttack(guard, target);
        }
        return true;
    }

    /// <summary>Fight_Attack for a guard: the target, the fight memory and war.</summary>
    private void GuardStartFight(Character guard, Character target)
    {
        if (guard.FightTarget == target.Uid && guard.IsStatFlag(StatFlag.War))
            return;
        guard.FightTarget = target.Uid;
        guard.Memory_Fight_Start(target);
        guard.SetStatFlag(StatFlag.War);
        guard.NextNpcActionTime = 0;
        OnWakeNpc?.Invoke(guard);
    }

    private static void FaceToward(Character npc, Character target)
    {
        if (npc.MapIndex == target.MapIndex && npc.Position != target.Position)
            npc.Direction = npc.Position.GetDirectionTo(target.Position);
    }

    /// <summary>The guard's fight once engaged: the held swing, then melee or the
    /// chase. Killing is the ordinary hit's job - the instant-kill setting only
    /// changes how the guard arrives (<see cref="GuardLookAtChar"/>).</summary>
    private void GuardEngage(Character guard, Character target)
    {
        if (guard.HasPendingHit)
        {
            long now = Environment.TickCount64;
            if (now < guard.SwingHitTime)
                return;
            ResolveNpcHit(guard, now);
            // A blow held for reach (the target stepped away during the swing
            // animation): keep closing in, as ActFight does.
            if (guard.HasPendingHit && guard.MapIndex == target.MapIndex &&
                guard.Position.GetDistanceTo(target.Position) > GetAttackRange(guard))
                MoveToward(guard, target.Position, run: true);
            return;
        }

        if (guard.MapIndex != target.MapIndex) return;
        int dist = guard.Position.GetDistanceTo(target.Position);
        if (dist <= GetAttackRange(guard))
            TrySwingAttack(guard, target);
        else
            MoveToward(guard, target.Position, run: true);
    }

    /// <summary>Next Detect Hidden sweep per NPC uid. Runtime AI state, so it lives
    /// here and not in a TAG (tags are saved and script-visible).</summary>
    private readonly Dictionary<uint, long> _nextDetectHidden = [];

    /// <summary>Periodically try to reveal nearby hidden players. ModernUO behaviour,
    /// only with NPCAIEXTRAS DETECTHIDDEN; chance scales with the NPC's
    /// DetectingHidden vs the target's Hiding/Stealth.</summary>
    private bool TryDetectHidden(Character npc)
    {
        long now = Environment.TickCount64;
        if (_nextDetectHidden.TryGetValue(npc.Uid.Value, out long next) && now < next)
            return false;
        // Smarter NPCs scan more often (8-30s).
        int intervalMs = Math.Clamp(30000 - npc.Int * 100, 8000, 30000);
        _nextDetectHidden[npc.Uid.Value] = now + intervalMs;

        int detectSkill = npc.GetSkill(SkillType.DetectingHidden);
        bool any = false;
        foreach (var ch in _world.GetCharsInRange(npc.Position, 6))
        {
            if (ch == npc || ch.IsDead || ch.IsDeleted) continue;
            if (!ch.IsStatFlag(StatFlag.Hidden) && !ch.IsStatFlag(StatFlag.Invisible)) continue;
            int conceal = Math.Max(ch.GetSkill(SkillType.Hiding), ch.GetSkill(SkillType.Stealth));
            int chance = Math.Clamp((detectSkill - conceal / 2) / 10 + 20, 5, 95); // percent
            if (_rand.Next(100) < chance && ch.ClearHiddenState(RevealFlags.DetectingHidden))
            {
                Character.OnAppearanceChanged?.Invoke(ch); // re-show to nearby clients
                any = true;
            }
        }
        return any;
    }

    /// <summary>Healer: resurrects ghosts (NPC_LookAtChar healer case,
    /// CCharNPCAct.cpp:1092), otherwise behaves as a townsman.</summary>
    private void ActHealer(Character npc)
    {
        TryVendorRestock(npc);

        if (TryFightAssignedTarget(npc))
            return;

        if (LookAroundTown(npc))
            return;

        // NPC_LookAtCharHuman (CCharNPCAct.cpp:803): an evil healer attacks like a
        // monster once no ghost needs it.
        if (NotoIsEvil(npc))
        {
            ActMonster(npc);
            return;
        }

        LookAtNearbyItems(npc);
        // Nothing to look at: the NPC_Act_Idle tail runs on every free tick; its own
        // dice decide between standing, wandering and going home (:1974).
        WanderHome(npc);
    }

    private static readonly string[] s_healerRefuseEvil =
        [Msg.NpcHealerRefEvil1, Msg.NpcHealerRefEvil2, Msg.NpcHealerRefEvil3];
    private static readonly string[] s_healerRefuseCrim =
        [Msg.NpcHealerRefCrim1, Msg.NpcHealerRefCrim2, Msg.NpcHealerRefCrim3];
    private static readonly string[] s_healerRefuseGood =
        [Msg.NpcHealerRefGood1, Msg.NpcHealerRefGood2, Msg.NpcHealerRefGood3];
    private static readonly string[] s_healerRes =
        [Msg.NpcHealerRes1, Msg.NpcHealerRes2, Msg.NpcHealerRes3, Msg.NpcHealerRes4, Msg.NpcHealerRes5];

    /// <summary>Source-X NPC_LookAtCharHealer (CCharNPCAct.cpp:844). Only a ghost
    /// (not stone, not a statue, not a bonded pet) interests a healer - and a
    /// healer sees ghosts whatever their war mode (CanSee, CCharStatus.cpp:1184).
    /// Beyond 3 tiles it calls the ghost closer one time in five. The refusals go
    /// by the ghost's notoriety as the healer sees it: a non-criminal healer
    /// refuses a criminal, a good healer anyone neutral or worse, a neutral or
    /// evil healer anyone good - each spoken one time in five. Otherwise a
    /// resurrect line, the area-cast animation and the Resurrection spell effect,
    /// with a failure line if the ghost is still a ghost afterwards.</summary>
    private bool HealerLookAtChar(Character npc, Character ghost)
    {
        if (!ghost.IsDead || ghost.IsStatFlag(StatFlag.Stone) ||
            (CharDefHelper.GetCanFlags(ghost) & CanFlags.C_Statue) != 0 ||
            (!ghost.IsPlayer && ghost.IsBonded))
            return false;

        FaceToward(npc, ghost);

        int dist = npc.Position.GetDistanceTo(ghost.Position);
        if (dist > 3)
        {
            if (_rand.Next(5) != 0)
                return false;
            OnNpcSay?.Invoke(npc, ServerMessages.Get(Msg.NpcHealerRange));
            return true;
        }

        bool imEvil = NotoIsEvil(npc);
        bool imNeutral = NotoIsNeutral(npc);
        byte notoThem = SphereNet.Game.Clients.GameClient.ComputeNotoriety(_world, npc, ghost);
        const byte NotoGood = 1, NotoNeutral = 3, NotoCriminal = 4;

        string[]? refusal = null;
        if (!HasCriminalFlag(npc) && notoThem == NotoCriminal)
            refusal = s_healerRefuseCrim;
        else if (!imNeutral && !imEvil && notoThem >= NotoNeutral)
            refusal = s_healerRefuseEvil;
        else if ((imNeutral || imEvil) && notoThem == NotoGood)
            refusal = s_healerRefuseGood;
        if (refusal != null)
        {
            string line = refusal[_rand.Next(refusal.Length)];
            if (_rand.Next(5) != 0)
                return false;
            OnNpcSay?.Invoke(npc, ServerMessages.Get(line));
            return true;
        }

        OnNpcSay?.Invoke(npc, ServerMessages.Get(s_healerRes[_rand.Next(s_healerRes.Length)]));
        OnHealerAction?.Invoke(npc, ghost, true);
        if (ghost.IsDead)
            OnNpcSay?.Invoke(npc, ServerMessages.Get(_rand.Next(2) == 0 ? Msg.NpcHealerFail1 : Msg.NpcHealerFail2));
        return true;
    }

    /// <summary>Callback: healer resurrects a ghost. Parameters: healer, target,
    /// isResurrect. Program.cs plays the area-cast animation and applies the
    /// Resurrection spell effect (OnSpellEffect(SPELL_Resurrection, healer, 1000),
    /// CCharNPCAct.cpp:927-928), so @SpellEffect may refuse it.</summary>
    public Action<Character, Character, bool>? OnHealerAction { get; set; }

    /// <summary>Callback: vendor needs restocking. Program.cs fires @NPCRestock trigger.</summary>
    public Action<Character>? OnVendorRestock { get; set; }

    private const int VendorRestockIntervalMs = 10 * 60 * 1000; // 10 minutes

    /// <summary>The vendor layers NPC_Vendor_Restock empties before a restock
    /// (CChar::sm_VendorLayers, CCharNPCAct_Vendor.cpp:15).</summary>
    private static readonly Layer[] s_vendorLayers =
        [Layer.VendorStock, Layer.VendorExtra, Layer.VendorBuy];

    /// <summary>Source-X NPC_Vendor_Restock, run from every NPC tick of an
    /// NPC_IsVendor brain (CCharNPCAct.cpp:2397) - healer, banker, vendor and
    /// stable master alike (CCharNPC::IsVendor, CCharNPC.cpp:251) - and never for
    /// a pet (CCharNPCAct_Vendor.cpp:41). The interval comes from the region's
    /// RestockVendors tag in tenths of a second, a NoRestock tag on the region or
    /// the NPC suppresses it, and a due restock EMPTIES the vendor containers first
    /// (:83-93) before @NPCRestock refills them.</summary>
    private void TryVendorRestock(Character npc)
    {
        if (!Trade.VendorEngine.IsVendorBrain(npc.NpcBrain) ||
            npc.IsStatFlag(StatFlag.Pet) || npc.NpcMaster.IsValid ||
            Trade.VendorEngine.HasRealStock(npc))
            return;

        var vendorRegion = _world.FindRegion(npc.Position);
        long intervalMs = VendorRestockIntervalMs;
        if (vendorRegion != null && vendorRegion.TryGetTag("RESTOCKVENDORS", out string? rv) && rv != null &&
            long.TryParse(rv, out long tenths) && tenths > 0)
            intervalMs = Math.Clamp(tenths, 1, 365L * 24 * 60 * 60 * 10) * 100;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (npc.TryGetTag("RESTOCK_TIME", out string? rtStr) && long.TryParse(rtStr, out long lastRestock)
            && lastRestock > now - intervalMs)
            return;
        if (npc.TryGetTag("NORESTOCK", out _) ||
            (vendorRegion != null && vendorRegion.TryGetTag("NORESTOCK", out _)))
            return;

        foreach (var layer in s_vendorLayers)
        {
            var cont = npc.GetEquippedItem(layer);
            if (cont == null) continue;
            foreach (var old in _world.GetContainerContents(cont.Uid).ToList())
                _world.RemoveItem(old);
        }

        OnVendorRestock?.Invoke(npc);
        npc.SetTag("RESTOCK_TIME", now.ToString());
    }

    /// <summary>Vendor/Banker/Stable: stay near home, barely move, periodic
    /// restock. Defends itself when attacked.</summary>
    private void ActVendor(Character npc)
    {
        TryVendorRestock(npc);

        if (TryFightAssignedTarget(npc))
            return;

        if (NotoIsEvil(npc))
        {
            ActMonster(npc);
            return;
        }

        if (LookAroundTown(npc))
            return;

        LookAtNearbyItems(npc);

        if (!TryResolveHome(npc, out Point3D home, out _))
            return;

        if (npc.MapIndex != home.Map)
        {
            _world.MoveCharacter(npc, home);
            OnNpcTeleport?.Invoke(npc);
            return;
        }

        // Shard rule (deliberate, ignores MOREZ/HOMEDIST): service NPCs stay
        // glued to their post — they kept strolling out of their buildings.
        // Pull back beyond 1 tile, wander only while standing at home, so the
        // drift never exceeds ~2 tiles.
        int dist = npc.Position.GetDistanceTo(home);
        if (dist > 1)
        {
            MoveToward(npc, home);
            return;
        }

        if (dist == 0 && _rand.Next(100) < 3)
            Wander(npc);
    }

    /// <summary>Animal: an evil-karma animal hunts like a monster
    /// (NPC_LookAtCharHuman → NPC_LookAtCharMonster, CCharNPCAct.cpp:803; an
    /// animal is evil at -800 karma or less), any other one looks around as a
    /// townsman and wanders. It fights back when attacked.</summary>
    private void ActAnimal(Character npc)
    {
        if (TryFightAssignedTarget(npc))
            return;

        if (NotoIsEvil(npc))
        {
            ActMonster(npc);
            return;
        }

        // Timid animals back off from the nearest threat until they reach a safe
        // distance (ModernUO Backoff state) - NPCAIEXTRAS ANIMALBACKOFF only.
        // A threat is anyone in war mode or actively targeting this animal.
        if (HasExtra(npc, NpcAiExtraFlags.AnimalBackoff))
        {
            const int threatRange = 8;
            Character? threat = null;
            int nearest = int.MaxValue;
            foreach (var ch in _world.GetCharsInRange(npc.Position, threatRange))
            {
                if (ch == npc || ch.IsDead || ch.IsDeleted || !IsAttackable(ch)) continue;
                if (!ch.IsStatFlag(StatFlag.War) && ch.FightTarget != npc.Uid) continue;
                if (!_world.CanSeeLOS(npc.Position, ch.Position)) continue;
                int d = npc.Position.GetDistanceTo(ch.Position);
                if (d < nearest) { nearest = d; threat = ch; }
            }
            if (threat != null)
            {
                MoveAway(npc, threat.Position);
                return;
            }
        }

        if (LookAroundTown(npc))
            return;

        LookAtNearbyItems(npc);
        LookAroundIdleSound(npc);

        WanderHome(npc); // NPC_Act_Idle tail every free tick (:1974)
    }

    /// <summary>Source-X Food_CanEat (CCharStatus.cpp:888-907).</summary>
    public bool NpcCanEat(Character npc, Item item) => NpcFoodQty(npc, item) > 0;

    /// <summary>Source-X Food_CanEat (CCharStatus.cpp:888-907): the quantity of the
    /// first FOODTYPE entry the item matches - how much the creature wants of it,
    /// and how much it eats at a time (NPC_Food, CCharNPCAct.cpp:2516/:2540). A
    /// creature with no FOODTYPE, or whose FOODTYPE matches nothing, eats nothing.
    /// Entries are itemdefs or t_* typedefs, "qty name" or "name [qty]"; a bare
    /// name counts 1 (CResourceQty::Load).</summary>
    public int NpcFoodQty(Character npc, Item item)
    {
        var def = Definitions.DefinitionLoader.GetCharDef(npc.CharDefIndex);
        string? diet = def != null && !string.IsNullOrWhiteSpace(def.FoodTypeRaw)
            ? def.FoodTypeRaw
            : (npc.TryGetTag("FOODTYPE", out string? tagDiet) ? tagDiet : null);
        if (string.IsNullOrWhiteSpace(diet))
            return 0;

        var resources = Definitions.DefinitionLoader.StaticResources;
        int itemDefIndex = resources != null
            ? Definitions.ItemDefHelper.ResolveInstanceDefIndex(item, resources)
            : 0;
        var itemDef = Definitions.DefinitionLoader.GetItemDef(itemDefIndex)
            ?? Definitions.DefinitionLoader.GetItemDef(item.BaseId);

        foreach (var part in diet.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            string name;
            int qty = 1;
            if (tokens.Length >= 2 && int.TryParse(tokens[0], out int leadQty))
            {
                name = tokens[1];
                qty = leadQty;
            }
            else
            {
                name = tokens[0];
                if (tokens.Length >= 2 && int.TryParse(tokens[1], out int trailQty))
                    qty = trailQty;
            }
            var rid = resources?.ResolveDefName(name) ?? ResourceId.Invalid;
            if (!rid.IsValid)
            {
                // A t_* name with no [TYPEDEF] section behind it still names the
                // built-in item type it spells (IT_FOOD for t_food).
                if (TryParseItemTypeName(name, out var builtIn) && builtIn == item.ItemType)
                    return Math.Max(0, qty);
                continue;
            }
            bool match =
                (rid.Type == ResType.ItemDef &&
                 (rid.Index == itemDefIndex || rid.Index == item.BaseId ||
                  Definitions.DefinitionLoader.GetItemDef(rid.Index)?.DispIndex == item.BaseId)) ||
                (rid.Type == ResType.TypeDef && !string.IsNullOrWhiteSpace(itemDef?.TypeRaw) &&
                 resources!.ResolveDefName(itemDef.TypeRaw.Trim()) == rid) ||
                // A typedef entry matches the item's live type (FindResourceMatch ->
                // IsType, CItem.cpp:6072), so a TYPE set on the instance counts too.
                (rid.Type == ResType.TypeDef && rid.Index == (int)item.ItemType);
            if (match)
                return Math.Max(0, qty);
        }
        return 0;
    }

    private static bool TryParseItemTypeName(string name, out ItemType type)
    {
        type = ItemType.Normal;
        if (!name.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            return false;
        string token = name[2..].Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(token, true, out type) && !int.TryParse(token, out _);
    }

    /// <summary>Whether the chardef's FOODTYPE names t_grass
    /// (m_FoodType.ContainsResourceID(RES_TYPEDEF, IT_GRASS), CCharNPCAct.cpp:2616).</summary>
    private static bool DietAcceptsGrass(Character npc)
    {
        var def = Definitions.DefinitionLoader.GetCharDef(npc.CharDefIndex);
        var resources = Definitions.DefinitionLoader.StaticResources;
        if (def == null || resources == null || string.IsNullOrWhiteSpace(def.FoodTypeRaw))
            return false;
        foreach (var part in def.FoodTypeRaw.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            string name = tokens.Length >= 2 && int.TryParse(tokens[0], out _) ? tokens[1] : tokens[0];
            var rid = resources.ResolveDefName(name);
            if (rid.IsValid && rid.Type == ResType.TypeDef && rid.Index == (int)ItemType.Grass)
                return true;
        }
        return false;
    }

    /// <summary>Callback: an NPC eats. Parameters: eater, food, units offered;
    /// returns the units eaten. Program.cs routes it through EatEngine with the
    /// trigger dispatcher so @Eat runs; unwired, EatEngine runs without it.</summary>
    public Func<Character, Item, int, int>? OnNpcEat { get; set; }

    /// <summary>Callback: EatAnim's stat half (CCharAct.cpp:3455-3486) for a bite
    /// that is not a food stack - grazed grass. Parameters: eater, the bite, the
    /// food it restores. Program.cs routes it through EatEngine with the trigger
    /// dispatcher so @Eat runs.</summary>
    public Action<Character, Item, int>? OnNpcEatAnim { get; set; }

    /// <summary>Callback: an NPC emote line (Speak with TALKMODE_EMOTE).</summary>
    public Action<Character, string>? OnNpcEmote { get; set; }

    /// <summary>Callback: an NPC animation (UpdateAnimate).</summary>
    public Action<Character, AnimationType>? OnNpcAnimate { get; set; }

    /// <summary>Callback: an NPC plays a sound by id (Sound).</summary>
    public Action<Character, ushort>? OnNpcPlaySound { get; set; }

    private static readonly ushort[] s_eatSounds = [0x03A, 0x03B, 0x03C];

    /// <summary>Source-X EatAnim's show (CCharAct.cpp:3436): an eating sound, the
    /// eat animation unless mounted, and the "eats some" emote.</summary>
    private void ShowNpcEating(Character npc, string foodName)
    {
        OnNpcPlaySound?.Invoke(npc, s_eatSounds[_rand.Next(s_eatSounds.Length)]);
        if (!npc.IsMounted)
            OnNpcAnimate?.Invoke(npc, AnimationType.Eat);
        OnNpcEmote?.Invoke(npc, ServerMessages.GetFormatted(Msg.MsgEatsome, foodName));
    }

    /// <summary>Use_EatQty / ConsumeAmount + EatAnim for an NPC: eat up to
    /// <paramref name="qty"/> units of <paramref name="food"/> and spend what was
    /// eaten.</summary>
    private void NpcEat(Character npc, Item food, int qty)
    {
        string name = food.GetName();
        int eaten = OnNpcEat?.Invoke(npc, food, qty)
            ?? NPCs.EatEngine.Eat(npc, food, null, qty);
        if (eaten <= 0)
            return;
        ShowNpcEating(npc, name);
        if (food.Amount > eaten)
            food.Amount -= (ushort)eaten;
        else
            _world.RemoveItem(food);
    }

    /// <summary>The NPC food pass. NPC_AI_INTFOOD runs NPC_Act_Food from the idle
    /// action (CCharNPCAct.cpp:1963) and, when it acted, takes the tick (returns
    /// true); plain NPC_AI_FOOD runs NPC_Food after every tick for every brain,
    /// pets included (CCharAct.cpp:5955), without taking the tick.</summary>
    internal bool RunFoodAI(Character npc)
    {
        if (npc.IsDead)
            return false;
        var flags = GetNpcFlags(npc);
        if (flags.HasFlag(NpcAIFlags.IntFood))
        {
            // Only an idle NPC gets to NPC_Act_Idle; a following pet never does.
            if (npc.NpcMaster.IsValid || npc.FightTarget.IsValid)
                return false;
            return SeekFood(npc, intelligent: true);
        }
        if (flags.HasFlag(NpcAIFlags.Food))
            SeekFood(npc, intelligent: false);
        return false;
    }

    /// <summary>Source-X NPC_Food (CCharNPCAct.cpp:2485) and, with
    /// <paramref name="intelligent"/>, NPC_Act_Food (:1761). Only a creature with
    /// under 10 food AND at most 40% of its food maximum looks for a meal: its own
    /// food first (IT_FOOD in the pack), then the nearest edible it can see on the
    /// ground within UO_MAP_VIEW_SIGHT * hunger - eaten on the spot when adjacent,
    /// walked to otherwise - and failing both, grass (animals; a plain-FOOD human
    /// only when starving).</summary>
    private bool SeekFood(Character npc, bool intelligent)
    {
        int food = npc.Food;
        int level = FoodLevelPercent(npc);
        if (food >= 10 || level > 40)
            return false;

        var pack = npc.Backpack;
        if (pack != null)
        {
            foreach (var it in pack.Contents)
            {
                if (it.IsDeleted || it.ItemType != ItemType.Food)
                    continue;
                // Use_EatQty(pFood, Food_CanEat(pFood)) - the FOODTYPE quantity
                // (CCharNPCAct.cpp:2516-2519).
                int packQty = NpcFoodQty(npc, it);
                if (packQty <= 0)
                    continue;
                NpcEat(npc, it, packQty);
                return true;
            }
        }

        TryResolveHome(npc, out _, out int homeDist);
        int searchRange = Math.Min(ViewSight * (100 - level) / 100, homeDist);
        Item? meal = null;
        int mealQty = 1;
        int best = int.MaxValue;
        foreach (var it in _world.GetItemsInRange(npc.Position, searchRange))
        {
            if (it.IsDeleted || !it.IsOnGround) continue;
            if ((it.Attributes & (ObjAttributes.Move_Never | ObjAttributes.Static |
                    ObjAttributes.LockedDown | ObjAttributes.Secure)) != 0)
                continue;
            if (intelligent ? (it.Z > npc.Z + 10 || it.Z < npc.Z - 1)
                            : (it.Z < npc.Z || it.Z > npc.Z + 8))
                continue;
            int itQty = NpcFoodQty(npc, it);
            if (itQty <= 0) continue;
            if (!_world.CanSeeLOS(npc.Position, it.Position)) continue;
            int d = npc.Position.GetDistanceTo(it.Position);
            if (d < best) { best = d; meal = it; mealQty = itQty; }
        }

        if (meal != null)
        {
            if (best <= 1)
            {
                // ConsumeAmount(Food_CanEat) - the FOODTYPE quantity (:2560).
                NpcEat(npc, meal, mealQty);
                return true;
            }
            // Only an NPC that is idle, wandering, walking or fleeing heads for it
            // (Skill_Start(NPCACT_GOTO), CCharNPCAct.cpp:2575-2596); SphereNet's
            // idle wanderer carries no action at all. A pet stays with its master.
            var act = (NpcAction)npc.Action;
            bool free = npc.Action == SkillType.None || act is NpcAction.Stay or NpcAction.GoTo
                or NpcAction.Wander or NpcAction.Looking or NpcAction.GoHome
                or NpcAction.Napping or NpcAction.Flee;
            if (free && !npc.NpcMaster.IsValid)
            {
                npc.ActP = meal.Position;
                npc.Action = (SkillType)NpcAction.GoTo;
                return true;
            }
            return false;
        }

        bool animal = npc.NpcBrain == NpcBrainType.Animal;
        bool searchGrass = animal ||
            (!intelligent && food == 0 && !animal && npc.NpcBrain is not
                (NpcBrainType.Monster or NpcBrainType.Dragon or NpcBrainType.Berserk));
        return searchGrass && TryGraze(npc);
    }

    /// <summary>Grazing (CCharNPCAct.cpp:2610-2630): a creature whose FOODTYPE takes
    /// t_grass eats the grass it stands on. Source-X finds the grass through the
    /// region's natural resources; SphereNet has no grass resource bits, so the
    /// land tile under the creature has to be a grass tile. The bite is 15 of the
    /// resource, a tenth of it food (EatAnim(pResBit, uiEaten / 10)).</summary>
    private bool TryGraze(Character npc)
    {
        if (!DietAcceptsGrass(npc))
            return false;
        var mapData = _world.MapData;
        if (mapData == null)
            return false;
        var cell = mapData.GetTerrainTile(npc.MapIndex, npc.X, npc.Y);
        if (cell.Z != npc.Z)
            return false;
        var land = mapData.GetLandTileData(cell.TileId);
        if (string.IsNullOrEmpty(land.Name) ||
            !land.Name.Contains("grass", StringComparison.OrdinalIgnoreCase))
            return false;

        // The bite is the resource bit named DEFMSG_NPC_EAT_GRASS; EatAnim shows it
        // and raises the stats through @Eat, food AND stamina (CCharNPCAct.cpp:2619-2622,
        // CCharAct.cpp:3436-3486).
        var bite = _world.CreateItem();
        bite.ItemType = ItemType.Grass;
        bite.Name = ServerMessages.Get(Msg.NpcEatGrass);
        try
        {
            ShowNpcEating(npc, bite.GetName());
            if (OnNpcEatAnim != null)
                OnNpcEatAnim(npc, bite, 15 / 10);
            else
                NPCs.EatEngine.EatAnim(npc, bite, null, 15 / 10);
        }
        finally
        {
            _world.DeleteObject(bite);
        }
        return true;
    }

    /// <summary>Callback: an NPC saw a crime and calls the guards (Source-X
    /// CallGuards, CCharFight.cpp:215). Parameters: witness, criminal. Returns
    /// whether a guard was actually called, which is when the witness speaks.</summary>
    public Func<Character, Character, bool>? OnWitnessCrime { get; set; }

    /// <summary>Human: an evil human hunts like a monster (NPC_LookAtCharHuman,
    /// CCharNPCAct.cpp:803); any other looks around for crime, notices items and
    /// wanders. It defends itself when attacked.</summary>
    private void ActHuman(Character npc)
    {
        if (TryFightAssignedTarget(npc))
            return;

        if (NotoIsEvil(npc))
        {
            ActMonster(npc);
            return;
        }

        if (LookAroundTown(npc))
            return;

        LookAtNearbyItems(npc);

        WanderHome(npc); // NPC_Act_Idle tail every free tick (:1974)
    }

    /// <summary>Source-X NPC_LookAround + NPC_LookAtChar (CCharNPCAct.cpp:1116,
    /// 1005) for the town brains: every character in sight - a far one only now
    /// and then (the UO_MAP_VIEW_SIGHT blur, halved under 50 INT) - with line of
    /// sight gets @NPCLookAtChar, then the brain's own look: a guard's, a healer's
    /// (ghosts) and then a townsman's, everyone else a townsman's. With
    /// <paramref name="asHuman"/> (a monster that is not hunting) only the
    /// townsman's look runs. True when something new was started.</summary>
    private bool LookAroundTown(Character npc, bool asHuman = false)
    {
        int range = GetNpcSight(npc);
        if ((_world.GetSector(npc.Position)?.GetCharComplexity() ?? 0) > World.Sectors.Sector.MaxCharComplexity / 2)
            range /= 4;
        int blur = npc.Int < 50 ? ViewSight / 2 : ViewSight;
        int rand = _rand.Next(Math.Max(1, range * 2));
        bool hooked = OnNpcLookAtChar != null;
        bool healer = !asHuman && npc.NpcBrain == NpcBrainType.Healer;
        bool guard = !asHuman && npc.NpcBrain == NpcBrainType.Guard;

        foreach (var ch in _world.GetCharsInRange(npc.Position, range))
        {
            if (ch == npc || ch.IsDeleted || ch.MapIndex != npc.MapIndex)
                continue;
            // CanSee: only a healer sees ghosts (CCharStatus.cpp:1184); the hidden
            // and the invisible are not seen.
            if (ch.IsDead ? !healer
                          : ch.IsStatFlag(StatFlag.Hidden) || ch.IsStatFlag(StatFlag.Invisible) ||
                            ch.IsStatFlag(StatFlag.Insubstantial))
                continue;
            int dist = npc.Position.GetDistanceTo(ch.Position);
            if (dist > blur && dist > 0 && rand % dist != 0)
                continue;
            // Without a script watching, skip the LOS raycast for someone the brain
            // would ignore anyway.
            if (!hooked && !MightInterest(npc, ch, healer, guard))
                continue;
            if (!_world.CanSeeLOS(npc.Position, ch.Position))
                continue;

            if (LookAtCharBrain(npc, ch, asHuman))
            {
                EmitSound(npc, CreatureSoundType.Notice);
                return true;
            }
        }
        return false;
    }

    /// <summary>NPC_LookAtChar for one character the NPC already sees:
    /// @NPCLookAtChar, then the brain's look (see <see cref="LookAroundTown"/>).
    /// Monster brains pick their targets by motivation instead
    /// (<see cref="FindBestTarget"/>), so they have no look here.</summary>
    private bool LookAtCharBrain(Character npc, Character ch, bool asHuman)
    {
        var look = FireLookAtChar(npc, ch);
        if (look == TriggerResult.True)
            return true;
        if (look == TriggerResult.False)
            return false;

        if (asHuman)
            return HumanLookAtChar(npc, ch);
        return npc.NpcBrain switch
        {
            NpcBrainType.Guard => GuardLookAtChar(npc, ch, fromTrigger: false),
            NpcBrainType.Healer => HealerLookAtChar(npc, ch) || HumanLookAtChar(npc, ch),
            NpcBrainType.Monster or NpcBrainType.Dragon or NpcBrainType.Berserk => false,
            _ => HumanLookAtChar(npc, ch),
        };
    }

    /// <summary>NPC_LookAtChar(pSrc, 1) from the speech path (NPC_OnHear,
    /// CCharNPCAct.cpp:362-366): a healer - or anyone with 100.0 Spirit Speak -
    /// looks at whoever spoke to it, which is how a ghost asking a healer gets
    /// resurrected. True when that started something.</summary>
    public bool LookAtChar(Character npc, Character ch)
    {
        if (ch == npc || ch.IsDeleted || npc.IsDead || npc.IsPlayer || ch.MapIndex != npc.MapIndex)
            return false;
        if (ch.IsDead && npc.NpcBrain != NpcBrainType.Healer)
            return false; // CanSee: only a healer sees a ghost (CCharStatus.cpp:1184)
        if (!_world.CanSeeLOS(npc.Position, ch.Position))
            return false;
        return LookAtCharBrain(npc, ch, asHuman: false);
    }

    /// <summary>A cheap pre-filter mirroring the looks' first tests.</summary>
    private bool MightInterest(Character npc, Character ch, bool healer, bool guard)
    {
        if (ch.IsDead)
            return healer;
        return HasCriminalFlag(ch) || NotoIsEvil(ch) || (guard && NotoIsCriminal(ch));
    }

    /// <summary>Source-X NPC_LookAtCharHuman for a townsman that is not evil
    /// (CCharNPCAct.cpp:809-841): someone with the criminal flag - or someone evil,
    /// when GUARDSONMURDERERS is on - seen inside guarded ground. A guard handles
    /// it itself; anyone else who can speak, one time in three, calls the guards
    /// (and yells only when a guard was called), then runs 20 steps unless it is at
    /// war.</summary>
    private bool HumanLookAtChar(Character npc, Character ch)
    {
        if (npc.IsDead || ch.IsDead || (CharDefHelper.GetCanFlags(ch) & CanFlags.C_Statue) != 0)
            return false;
        if (NotoIsEvil(npc))
            return false; // evil: the monster look, which the brain runs itself
        bool criminal = HasCriminalFlag(ch);
        if (!(NotoIsEvil(ch) && _config.GuardsOnMurderers) && !criminal)
            return false;

        var area = _world.FindRegion(npc.Position);
        if (area == null || !area.IsGuarded)
            return false;
        if (npc.NpcBrain == NpcBrainType.Guard)
            return GuardLookAtChar(npc, ch, fromTrigger: false);
        if (!NpcCanSpeak(npc) || _rand.Next(3) != 0)
            return false;

        if (OnWitnessCrime?.Invoke(npc, ch) == true)
            OnNpcSay?.Invoke(npc, ServerMessages.Get(criminal ? Msg.NpcGenericSeecrim : Msg.NpcGenericSeemons));
        if (npc.IsStatFlag(StatFlag.War))
            return false;

        // Run away like a coward: NPCACT_FLEE from the criminal for 20 steps.
        npc.Act = ch.Uid;
        npc.FleeStepsMax = 20;
        npc.FleeStepsCurrent = 0;
        npc.Action = (SkillType)NpcAction.Flee;
        return true;
    }

    /// <summary>Fire @NPCSeeNewPlayer where Source-X does (NPC_LookAtChar,
    /// CCharNPCAct.cpp:1041-1058): an idle NPC - not at war, doing nothing or
    /// wandering, and not a berserker - that sees a player it has no MEMORY_SPEAK
    /// of. The per-NPC scan only runs when a script hooks the trigger.</summary>
    private void LookForNewPlayers(Character npc)
    {
        if (Character.OnNpcSeeNewPlayer == null || _rand.Next(4) != 0) return;
        if (npc.NpcBrain == NpcBrainType.Berserk || npc.IsStatFlag(StatFlag.War) ||
            npc.FightTarget.IsValid)
            return;
        if (npc.Action != SkillType.None && (NpcAction)npc.Action != NpcAction.Wander)
            return;

        foreach (var ch in _world.GetCharsInRange(npc.Position, GetNpcSight(npc)))
        {
            if (!ch.IsPlayer || ch.IsDeleted) continue;
            if (ch.IsDead && npc.NpcBrain != NpcBrainType.Healer) continue;
            if (!_world.CanSeeLOS(npc.Position, ch.Position)) continue;
            npc.SeeNewPlayer(ch); // fires @NPCSeeNewPlayer on a first sighting
        }
    }

    /// <summary>Next allowed item-scan tick per NPC uid — the widened native
    /// scan ran a sector item query on every eighth act across thousands of
    /// NPCs and showed up as live apply-phase spikes; a 10-15 s per-NPC
    /// cadence matches the reference's leisurely look-around.</summary>
    private readonly Dictionary<uint, long> _nextItemScan = [];

    /// <summary>Source-X NPC_LookAround's item half + NPC_LookAtItem
    /// (CCharNPCAct.cpp:1188-1215, 940): an NPC with hands and more than 10 INT
    /// looks at the items it can see. @NPCLookAtItem may take an item over
    /// (RETURN 1), skip it (RETURN 0) or change the want; an item wanted more than
    /// a d100, or an unlooted corpse for an NPC_AI_LOOTING creature, starts
    /// NPC_Act_Looting - which only a looting monster ever acts on.</summary>
    private bool LookAtNearbyItems(Character npc)
    {
        long scanNow = Environment.TickCount64;
        if (_nextItemScan.TryGetValue(npc.Uid.Value, out long nextScan) && scanNow < nextScan)
            return false;
        _nextItemScan[npc.Uid.Value] = scanNow + 10_000 + _rand.Next(5_001);

        if ((CharDefHelper.GetCanFlags(npc) & CanFlags.C_UseHands) == 0 || npc.Int <= 10)
            return false;

        bool looter = GetNpcFlags(npc).HasFlag(NpcAIFlags.Looting);

        // Sight-driven look range (Source-X NPC_LookAround), clamped for the
        // per-tick item sweep cost.
        int lookRange = Math.Clamp(GetNpcSight(npc), 3, 8);
        foreach (var item in _world.GetItemsInRange(npc.Position, lookRange))
        {
            if (item.IsDeleted || item.ContainedIn.IsValid) continue;
            // No coveting through walls (Source-X CanSee in NPC_LookAtItem).
            if (!_world.CanSeeLOS(npc.Position, item.Position)) continue;
            int dist = npc.Position.GetDistanceTo(item.Position);
            int want = GetWantScore(npc, item);
            if (OnNpcLookAtItem != null && !IsLookAtItemExcluded(item))
            {
                var decision = OnNpcLookAtItem(npc, item, dist, want);
                if (decision.Handled)
                    return true;
                if (decision.Ignore)
                    continue;
                want = decision.Want;
            }

            if (want > _rand.Next(100))
            {
                ActLooting(npc, item);
                return true;
            }
            if (item.ItemType == ItemType.Corpse && looter && npc.Memory_FindObj(item.Uid) == null)
            {
                ActLooting(npc, item);
                return true;
            }
        }
        return false;
    }

    /// <summary>Callback: an NPC looted an item. Parameters: npc, the item, whether
    /// it came out of a corpse. Program.cs sends the rummage emote and animation.</summary>
    public Action<Character, Item, bool>? OnNpcLooted { get; set; }

    /// <summary>Source-X NPC_Act_Looting (CCharNPCAct.cpp:1593): only a MONSTER
    /// brain with NPC_AI_LOOTING and hands, that is neither conjured nor a pet nor
    /// DEATH_NOCORPSE, outside guarded or safe ground, loots. It walks up to the
    /// item, takes a random piece of a corpse, remembers what it cannot take
    /// (NPC_LootMemory), lets @NPCSeeWantItem refuse, and bounces the piece into
    /// its pack - or at its feet when it has none; no pack is ever made for it.</summary>
    private void ActLooting(Character npc, Item item)
    {
        if (IsProtectedGround(npc.Position))
            return;
        if (!GetNpcFlags(npc).HasFlag(NpcAIFlags.Looting))
            return;
        if (npc.NpcBrain != NpcBrainType.Monster ||
            (CharDefHelper.GetCanFlags(npc) & CanFlags.C_UseHands) == 0 ||
            npc.IsStatFlag(StatFlag.Conjured) || npc.IsStatFlag(StatFlag.Pet) ||
            npc.NpcMaster.IsValid || HasNoCorpseFlag(npc))
            return;
        if (item.IsDeleted)
            return;

        if (npc.Position.GetDistanceTo(item.Position) > 2)
        {
            MoveToward(npc, item.Position);
            return;
        }

        bool fromCorpse = item.ItemType == ItemType.Corpse;
        Item piece = item;
        if (fromCorpse && item.Contents.Count > 0)
            piece = item.Contents[_rand.Next(item.Contents.Count)];

        if (!ItemMoveRules.CanMove(npc, piece, out _) || !npc.CanCarry(piece))
        {
            NpcLootMemory(npc, piece);
            return;
        }

        if (OnNpcSeeWantItem?.Invoke(npc, piece) == true)
            return;

        if (fromCorpse && piece != item)
            item.RemoveItem(piece);
        var pack = npc.Backpack;
        if (pack == null || !pack.TryAddItem(piece))
        {
            if (piece.ContainedIn.IsValid || piece != item)
                _world.PlaceItem(piece, npc.Position);
        }
        OnNpcLooted?.Invoke(npc, piece, fromCorpse && piece != item);
    }

    /// <summary>DEATHFLAGS &amp; DEATH_NOCORPSE (0x02) on the NPC.</summary>
    private static bool HasNoCorpseFlag(Character npc)
    {
        if (!npc.TryGetTag("DEATHFLAGS", out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        raw = raw.Trim();
        bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || (raw.Length > 1 && raw[0] == '0');
        var digits = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        return (hex ? int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out int v)
                    : int.TryParse(digits, out v)) && (v & 0x02) != 0;
    }

    /// <summary>Guarded or SAFE region — no scavenging/looting here
    /// (Source-X region checks in the NPC item passes).</summary>
    private bool IsProtectedGround(Point3D pos)
    {
        var region = _world.FindRegion(pos);
        return region != null &&
               (region.IsGuarded || region.IsFlag(RegionFlag.Safe));
    }

    /// <summary>Source-X NPC_WantThisItem (CCharNPCStatus.cpp:628): nothing the
    /// NPC may not move; a DESIRES match scores the entry's OWN qty (bare
    /// defname = 1); an edible-for-this-creature item scores 100 minus the food
    /// level; an NPC_IsVendor brain (healer included) always wants gold at 100.</summary>
    public int GetWantScore(Character npc, Item item)
    {
        if (!ItemMoveRules.CanMove(npc, item, out _))
            return 0;

        var def = Definitions.DefinitionLoader.GetCharDef(npc.CharDefIndex);
        var resources = Definitions.DefinitionLoader.StaticResources;
        if (def != null && resources != null)
        {
            int itemDefIndex = Definitions.ItemDefHelper.ResolveInstanceDefIndex(item, resources);
            var itemDef = Definitions.DefinitionLoader.GetItemDef(itemDefIndex)
                ?? Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            for (int i = 0; i < def.Desires.Count; i++)
            {
                var desire = def.Desires[i];
                int qty = i < def.DesireQtys.Count ? def.DesireQtys[i] : 1;
                if (desire.Type == ResType.ItemDef)
                {
                    var desiredDef = Definitions.DefinitionLoader.GetItemDef(desire.Index);
                    if (desire.Index == itemDefIndex || desire.Index == item.BaseId ||
                        desiredDef?.DispIndex == item.BaseId)
                        return qty;
                }
                else if (desire.Type == ResType.TypeDef && !string.IsNullOrWhiteSpace(itemDef?.TypeRaw))
                {
                    var typeRid = resources.ResolveDefName(itemDef.TypeRaw.Trim());
                    if (typeRid == desire)
                        return qty;
                }
            }
        }

        // Hunger: Food_CanEat + Food_GetLevelPercent.
        if (NpcCanEat(npc, item))
        {
            int foodPercent = FoodLevelPercent(npc);
            if (foodPercent < 100)
                return 100 - foodPercent;
        }

        // Vendors always want money.
        if (Trade.VendorEngine.IsVendorBrain(npc.NpcBrain) && item.ItemType == ItemType.Gold)
            return 100;

        return 0;
    }

    /// <summary>Items @NPCLookAtItem never fires for (Source-X gates the
    /// trigger on ATTR_MOVE_NEVER | ATTR_LOCKEDDOWN | ATTR_SECURE).</summary>
    private static bool IsLookAtItemExcluded(Item item) =>
        (item.Attributes & (ObjAttributes.Move_Never | ObjAttributes.LockedDown |
            ObjAttributes.Secure)) != 0;
}
