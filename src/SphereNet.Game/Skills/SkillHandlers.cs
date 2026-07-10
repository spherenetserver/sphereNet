using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Game.Skills;

/// <summary>
/// Per-skill use handlers. Maps to Skill_Start / Skill_Stage for each skill.
/// Each handler returns true if the skill use succeeded.
/// </summary>
public sealed class SkillHandlers
{
    private readonly GameWorld _world;
    private readonly GatheringEngine? _gatheringEngine;
    private readonly Dictionary<SkillType, Func<Character, Point3D?, bool>> _handlers = [];

    /// <summary>Callback to open crafting gump for a character. Set by GameClient.</summary>
    public static Action<Character, SkillType>? OnCraftSkillUsed { get; set; }

    /// <summary>Callback for scripted (custom) skill use. Set by Program.cs to fire trigger chain.</summary>
    public static Func<Character, SkillType, bool>? OnScriptedSkillUse { get; set; }

    public SkillHandlers(GameWorld world, GatheringEngine? gatheringEngine = null)
    {
        _world = world;
        _gatheringEngine = gatheringEngine;
        RegisterAll();
    }

    /// <summary>
    /// Information skills (Anatomy, AnimalLore, ArmsLore, EvalInt, Forensics,
    /// ItemID, TasteID) require a selected target to produce their Source-X
    /// message output. <see cref="GameClient.HandleUseSkill"/> detects these
    /// skills, opens a target cursor, and routes the resolved object here.
    ///
    /// Returns true when the difficulty probe and skill roll succeed. Descriptive
    /// output and mutations run only in the success stage.
    /// </summary>
    public bool UseInfoSkill(IInfoSkillSink sink, SkillType skill, ObjBase? target)
    {
        var ch = sink.Self;
        if (!CanUse(ch, skill)) return false;

        if (target == null || !CanInspectTarget(ch, target, skill))
            return false;

        int level = SkillEngine.GetAdjustedSkill(ch, skill);
        int difficulty = skill switch
        {
            SkillType.Anatomy when target is Character c => InfoSkillEngine.Anatomy(sink, c, level, true),
            SkillType.AnimalLore when target is Character c => InfoSkillEngine.AnimalLore(sink, c, _world, level, true),
            SkillType.ArmsLore when target is Item item => InfoSkillEngine.ArmsLore(sink, item, level, true),
            SkillType.EvalInt when target is Character c => InfoSkillEngine.EvalInt(sink, c, level, true),
            SkillType.Forensics when target is Item corpse => InfoSkillEngine.Forensics(sink, corpse, null, 0, false, false, level, true),
            SkillType.ItemId => InfoSkillEngine.ItemID(sink, target, level, true),
            SkillType.TasteId => InfoSkillEngine.TasteID(sink, target, level, true),
            _ => -1,
        };
        if (difficulty < 0 || !SkillEngine.UseQuick(ch, skill, difficulty))
            return false;

        return skill switch
        {
            SkillType.Anatomy when target is Character c => InfoSkillEngine.Anatomy(sink, c, level) >= 0,
            SkillType.AnimalLore when target is Character c => InfoSkillEngine.AnimalLore(sink, c, _world, level) >= 0,
            SkillType.ArmsLore when target is Item item => InfoSkillEngine.ArmsLore(sink, item, level) >= 0,
            SkillType.EvalInt when target is Character c => InfoSkillEngine.EvalInt(sink, c, level) >= 0,
            SkillType.Forensics when target is Item corpse => RunForensics(sink, corpse, level),
            SkillType.ItemId => InfoSkillEngine.ItemID(sink, target, level) >= 0,
            SkillType.TasteId => InfoSkillEngine.TasteID(sink, target, level) >= 0,
            _ => false,
        };
    }

    private bool RunForensics(IInfoSkillSink sink, Item corpse, int level)
    {
        Serial killerUid = ResolveKillerUid(corpse);
        Character? killer = killerUid.IsValid ? _world.FindChar(killerUid) : null;
        long secs = corpse.TryGetTag("DEATH_TIME", out string? ds) && long.TryParse(ds, out long dt)
            ? Math.Max(0, (Environment.TickCount64 - dt) / 1000)
            : 0;
        bool sleeping = corpse.TryGetTag("CORPSE_SLEEPING", out string? sv) && sv == "1";
        bool carved = corpse.TryGetTag("CORPSE_CARVED", out string? cv) && cv == "1";
        return InfoSkillEngine.Forensics(sink, corpse, killer, secs, sleeping, carved, level) >= 0;
    }

    private bool CanInspectTarget(Character ch, ObjBase target, SkillType skill)
    {
        Point3D position;
        if (target is Character targetChar)
        {
            if (targetChar.IsDeleted) return false;
            position = targetChar.Position;
        }
        else if (target is Item item)
        {
            if (item.IsDeleted) return false;
            var seen = new HashSet<uint>();
            for (int depth = 0; depth < 32 && item.ContainedIn.IsValid; depth++)
            {
                if (!seen.Add(item.Uid.Value)) return false;
                var holder = _world.FindObject(item.ContainedIn);
                if (holder is Character holderChar) { position = holderChar.Position; goto resolved; }
                if (holder is Item parent) { item = parent; continue; }
                return false;
            }
            if (item.ContainedIn.IsValid) return false;
            position = item.Position;
        }
        else
        {
            return false;
        }

    resolved:
        int range = SkillEngine.GetUseRange(skill, 3);
        return position.Map == ch.MapIndex && ch.Position.GetDistanceTo(position) <= range &&
            _world.CanSeeLOS(ch.Position, position);
    }

    private static Serial ResolveKillerUid(Item corpse)
    {
        if ((corpse.TryGetTag("KILLER_UID", out string? kv) ||
             corpse.TryGetTag("CORPSE_KILLER", out kv)) && !string.IsNullOrEmpty(kv))
        {
            if (kv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(kv[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hx))
                return new Serial(hx);
            if (uint.TryParse(kv, out uint dec))
                return new Serial(dec);
            if (uint.TryParse(kv, System.Globalization.NumberStyles.HexNumber, null, out hx))
                return new Serial(hx);
        }
        return Serial.Zero;
    }

    /// <summary>Source-X list of skills that prompt for a target and emit only descriptive text.</summary>
    public static bool IsInfoSkill(SkillType skill) => skill switch
    {
        SkillType.Anatomy or SkillType.AnimalLore or SkillType.ArmsLore or
        SkillType.EvalInt or SkillType.Forensics or SkillType.ItemId or
        SkillType.TasteId => true,
        _ => false,
    };

    /// <summary>Validate a real, enabled skill. Reserved protocol slots never
    /// reach a handler and SKF_DISABLED applies to built-in skills too.</summary>
    public static bool CanUse(Character ch, SkillType skill)
    {
        if (!SkillEngine.IsValidBaseSkill(skill) || ch.IsDead || ch.IsDeleted)
            return false;
        if (ch.IsCasting || ch.IsStatFlag(StatFlag.Sleeping | StatFlag.Freeze | StatFlag.Stone))
            return false;
        return !SkillEngine.HasFlag(skill, SkillFlag.Disabled);
    }

    /// <summary>Skills that may be initiated by the client UseSkill command.
    /// Passive combat/magic/regeneration skills are intentionally excluded.</summary>
    public static bool IsClientUsable(SkillType skill)
    {
        if (!SkillEngine.IsValidBaseSkill(skill) || SkillEngine.HasFlag(skill, SkillFlag.Disabled))
            return false;

        var def = DefinitionLoader.GetSkillDef((int)skill);
        if (def != null)
        {
            var flags = (SkillFlag)def.Flags;
            if ((flags & SkillFlag.Scripted) != 0)
                return (flags & SkillFlag.Selectable) != 0;
        }

        if (IsInfoSkill(skill) || GetActiveSkillTarget(skill) != ActiveSkillTargetKind.Unsupported)
            return true;

        return IsCraftSkill(skill) || skill is SkillType.Camping or SkillType.Cartography;
    }

    public static bool IsCraftSkill(SkillType skill)
    {
        if (SkillEngine.HasFlag(skill, SkillFlag.Craft)) return true;
        return skill is SkillType.Alchemy or SkillType.Blacksmithing or SkillType.Bowcraft or
            SkillType.Carpentry or SkillType.Cartography or SkillType.Cooking or SkillType.Inscription or
            SkillType.Imbuing or SkillType.Tailoring or SkillType.Tinkering;
    }

    /// <summary>
    /// Active skills routed through <see cref="ActiveSkillEngine"/> via the
    /// rich <see cref="IActiveSkillSink"/> path. Returns the kind of target
    /// prompt the client should open.
    /// </summary>
    public static ActiveSkillTargetKind GetActiveSkillTarget(SkillType skill)
    {
        var def = DefinitionLoader.GetSkillDef((int)skill);
        if (def != null && ((SkillFlag)def.Flags & SkillFlag.Scripted) != 0)
        {
            return !string.IsNullOrWhiteSpace(def.PromptMsg) ||
                   !string.IsNullOrWhiteSpace(def.PromptCliloc)
                ? ActiveSkillTargetKind.Object
                : ActiveSkillTargetKind.None;
        }

        var builtIn = skill switch
        {
            SkillType.Hiding or SkillType.Stealth or SkillType.DetectingHidden or
                SkillType.Meditation or SkillType.SpiritSpeak or SkillType.Musicianship or
                SkillType.Peacemaking => ActiveSkillTargetKind.None,
            SkillType.Tracking => ActiveSkillTargetKind.Menu,
            SkillType.Begging or SkillType.Healing or SkillType.Taming or
                SkillType.Herding or SkillType.Veterinary or SkillType.Provocation or
                SkillType.Enticement => ActiveSkillTargetKind.Character,
            SkillType.Stealing or SkillType.Snooping or SkillType.Lockpicking or
                SkillType.RemoveTrap or SkillType.Poisoning => ActiveSkillTargetKind.Item,
            SkillType.Mining or SkillType.Fishing or SkillType.Lumberjacking =>
                ActiveSkillTargetKind.Ground,
            _ => ActiveSkillTargetKind.Unsupported,
        };
        if (builtIn != ActiveSkillTargetKind.Unsupported)
            return builtIn;
        return ActiveSkillTargetKind.Unsupported;
    }

    /// <summary>
    /// Dispatch entry for active skills that takes the rich sink and routes
    /// to the matching <see cref="ActiveSkillEngine"/> method. Falls back to
    /// the legacy <see cref="UseSkill"/> path for unsupported skills.
    /// </summary>
    /// <summary>Resolve a Healing/Veterinary target. A Character heals directly; a
    /// corpse item resurrects its dead owner (Source-X corpse-target resurrection
    /// via Skill_Healing) — the healer must be near the corpse and the owner must
    /// still be dead (a fresh corpse, not a decayed bones pile with no owner link).</summary>
    private Character? ResolveHealTarget(ObjBase? target)
    {
        if (target is Character c) return c;
        if (target is Item corpse && corpse.ItemType == ItemType.Corpse &&
            corpse.TryGetTag("OWNER_UID", out string? o) && uint.TryParse(o, out uint uid))
        {
            var owner = _world.FindChar(new Serial(uid));
            if (owner != null && owner.IsDead && !owner.IsDeleted)
                return owner;
        }
        return null;
    }

    public bool UseActiveSkill(IActiveSkillSink sink, SkillType skill, ObjBase? target, Point3D? point = null)
    {
        var ch = sink.Self;
        if (!CanUse(ch, skill)) return false;
        if (ch.IsStatFlag(StatFlag.Freeze)) return false;

        if (SkillEngine.HasFlag(skill, SkillFlag.Scripted))
        {
            ch.Act = target?.Uid ?? Serial.Invalid;
            if (point.HasValue) ch.ActP = point.Value;
            return UseSkill(ch, skill, point);
        }

        switch (skill)
        {
            case SkillType.Hiding:           return ActiveSkillEngine.Hiding(sink);
            case SkillType.Stealth:          return ActiveSkillEngine.Stealth(sink);
            case SkillType.DetectingHidden:  return ActiveSkillEngine.DetectHidden(sink);
            case SkillType.Meditation:       return ActiveSkillEngine.Meditation(sink);
            case SkillType.SpiritSpeak:      return ActiveSkillEngine.SpiritSpeak(sink);
            case SkillType.Begging:          return ActiveSkillEngine.Begging(sink, target as Character);
            case SkillType.Healing:
                return ActiveSkillEngine.Healing(sink, ResolveHealTarget(target), SkillType.Healing,
                    target as Item);
            case SkillType.Taming:           return ActiveSkillEngine.Taming(sink, target as Character);
            case SkillType.Stealing:         return ActiveSkillEngine.Stealing(sink, target as Item);
            case SkillType.Snooping:         return ActiveSkillEngine.Snooping(sink, target as Item);
            case SkillType.Lockpicking:      return ActiveSkillEngine.Lockpicking(sink, target as Item);
            case SkillType.RemoveTrap:       return ActiveSkillEngine.RemoveTrap(sink, target as Item);
            case SkillType.Poisoning:
                return ActiveSkillEngine.Poisoning(sink, target as Item,
                    ch.ActPrv.IsValid ? _world.FindItem(ch.ActPrv) : null);
            case SkillType.Herding:          return ActiveSkillEngine.Herding(sink, target as Character, point);
            case SkillType.Veterinary:
                // Source-X SKILL_VETERINARY routes to Skill_Healing (bandages,
                // poison cure, pet resurrect) rather than a separate weak path.
                return ActiveSkillEngine.Healing(sink, ResolveHealTarget(target), SkillType.Veterinary,
                    target as Item);
            case SkillType.Tracking:         return ActiveSkillEngine.Tracking(sink, ActiveSkillEngine.TrackingCategory.Animals);
            case SkillType.Mining:           return ActiveSkillEngine.Mining(sink, point ?? ch.Position, _gatheringEngine, _world);
            case SkillType.Fishing:          return ActiveSkillEngine.Fishing(sink, point ?? ch.Position, _gatheringEngine, _world);
            case SkillType.Lumberjacking:    return ActiveSkillEngine.Lumberjacking(sink, point ?? ch.Position, _gatheringEngine, _world);
            case SkillType.Musicianship:     return ActiveSkillEngine.Musicianship(sink);
            case SkillType.Peacemaking:      return ActiveSkillEngine.Peacemaking(sink, target as Character);
            case SkillType.Provocation:
                return ActiveSkillEngine.Provocation(sink,
                    ch.ActPrv.IsValid ? _world.FindChar(ch.ActPrv) : null,
                    target as Character);
            case SkillType.Enticement:       return ActiveSkillEngine.Discordance(sink, target as Character);
            default:
                ch.Act = target?.Uid ?? Serial.Invalid;
                if (point.HasValue) ch.ActP = point.Value;
                return UseSkill(ch, skill, point);
        }
    }

    public enum ActiveSkillTargetKind { None, Character, Item, Object, Menu, Ground, Unsupported }

    public bool UseSkill(Character ch, SkillType skill, Point3D? target = null)
    {
        if (!CanUse(ch, skill)) return false;
        if (ch.IsStatFlag(StatFlag.Freeze)) return false;

        // Check for scripted (custom) skill via SkillDef
        var def = DefinitionLoader.GetSkillDef((int)skill);
        if (def != null && ((SkillFlag)def.Flags & SkillFlag.Scripted) != 0)
        {
            return OnScriptedSkillUse?.Invoke(ch, skill) ?? false;
        }

        if (def != null && ((SkillFlag)def.Flags & SkillFlag.Craft) != 0 && OnCraftSkillUsed != null)
        {
            OnCraftSkillUsed.Invoke(ch, skill);
            return true;
        }

        // Targeted/information skills require the rich sink path for messages,
        // containment checks, LOS, rollback and client synchronization. Do not
        // fall through to the old targetless compatibility handlers.
        if (IsInfoSkill(skill) || GetActiveSkillTarget(skill) != ActiveSkillTargetKind.Unsupported)
            return false;

        if (_handlers.TryGetValue(skill, out var handler))
            return handler(ch, target);

        return false;
    }

    private void RegisterAll()
    {
        _handlers[SkillType.Hiding] = HandleHiding;
        _handlers[SkillType.Stealth] = HandleStealth;
        _handlers[SkillType.DetectingHidden] = HandleDetectHidden;
        _handlers[SkillType.Mining] = HandleMining;
        _handlers[SkillType.Fishing] = HandleFishing;
        _handlers[SkillType.Lumberjacking] = HandleLumberjacking;
        _handlers[SkillType.Taming] = HandleTaming;
        _handlers[SkillType.AnimalLore] = HandleAnimalLore;
        _handlers[SkillType.Healing] = HandleHealing;
        _handlers[SkillType.Anatomy] = HandleAnatomy;
        _handlers[SkillType.Peacemaking] = HandlePeacemaking;
        _handlers[SkillType.Provocation] = HandleProvocation;
        _handlers[SkillType.Musicianship] = HandleMusicianship;
        _handlers[SkillType.Meditation] = HandleMeditation;
        _handlers[SkillType.SpiritSpeak] = HandleSpiritSpeak;
        _handlers[SkillType.Poisoning] = HandlePoisoning;
        _handlers[SkillType.ItemId] = HandleItemID;
        _handlers[SkillType.ArmsLore] = HandleArmsLore;
        _handlers[SkillType.Tracking] = HandleTracking;
        _handlers[SkillType.Forensics] = HandleForensics;
        _handlers[SkillType.Begging] = HandleBegging;
        _handlers[SkillType.Stealing] = HandleStealing;
        _handlers[SkillType.Snooping] = HandleSnooping;
        _handlers[SkillType.Lockpicking] = HandleLockpicking;
        _handlers[SkillType.RemoveTrap] = HandleRemoveTrap;
        _handlers[SkillType.Inscription] = HandleInscription;
        _handlers[SkillType.Cooking] = HandleCooking;
        _handlers[SkillType.Alchemy] = HandleAlchemy;
        _handlers[SkillType.Tailoring] = HandleTailoring;
        _handlers[SkillType.Blacksmithing] = HandleBlacksmithing;
        _handlers[SkillType.Carpentry] = HandleCarpentry;
        _handlers[SkillType.Tinkering] = HandleTinkering;
        _handlers[SkillType.Cartography] = HandleCartography;
        _handlers[SkillType.Bowcraft] = HandleBowcraft;
        _handlers[SkillType.TasteId] = HandleTasteID;
        _handlers[SkillType.EvalInt] = HandleEvalInt;
        _handlers[SkillType.Veterinary] = HandleVeterinary;
        _handlers[SkillType.Herding] = HandleHerding;
        _handlers[SkillType.Camping] = HandleCamping;
        _handlers[SkillType.Imbuing] = HandleImbuing;
    }

    private bool HandleHiding(Character ch, Point3D? target)
    {
        if (ch.IsInWarMode || ch.FightTarget.IsValid) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Hiding, 50);
        if (success)
        {
            ch.SetStatFlag(StatFlag.Hidden);
            ch.SetStatFlag(StatFlag.Invisible);
        }
        return success;
    }

    private bool HandleStealth(Character ch, Point3D? target)
    {
        if (!ch.IsStatFlag(StatFlag.Hidden)) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Stealth, 60);
        if (success)
        {
            int steps = Math.Max(1, ch.GetSkill(SkillType.Stealth) / 100);
            ch.StepStealth = (short)Math.Clamp(steps, 1, 10);
        }
        return success;
    }

    private bool HandleDetectHidden(Character ch, Point3D? target)
    {
        bool success = SkillEngine.UseQuick(ch, SkillType.DetectingHidden, 50);
        if (success)
        {
            foreach (var nearby in _world.GetCharsInRange(ch.Position, 8))
            {
                if (nearby == ch || !nearby.IsStatFlag(StatFlag.Hidden)) continue;
                int detectDiff = ch.GetSkill(SkillType.DetectingHidden) - nearby.GetSkill(SkillType.Hiding);
                if (detectDiff > 0 || Random.Shared.Next(1000) < 300)
                    nearby.ClearHiddenState();
            }
        }
        return success;
    }

    private const int FallbackResAmount = 20;
    private const int FallbackRegenMs = 36_000_000;

    private bool HandleMining(Character ch, Point3D? target)
    {
        if (target == null) return false;

        BroadcastSkillAnimation(ch, (ushort)Core.Enums.AnimationType.Attack1HBash, 0x0125);

        if (_gatheringEngine != null &&
            _gatheringEngine.TryGather(ch, SkillType.Mining, target.Value, out bool regionSuccess, out _, out _))
            return regionSuccess;

        var marker = FindFallbackMarker(target.Value, "Mining");
        if (marker != null && marker.Amount <= 0)
            return false;

        bool success = SkillEngine.UseQuick(ch, SkillType.Mining, 50);
        if (success)
        {
            int amount = Random.Shared.Next(1, 3);
            ConsumeFallbackMarker(target.Value, "Mining", amount, ref marker);
            var ore = _world.CreateItem();
            ore.BaseId = 0x19B9;
            ore.Name = "iron ore";
            ore.Amount = (ushort)amount;
            DeliverLegacyItem(ch, ore);
        }
        return success;
    }

    private bool HandleFishing(Character ch, Point3D? target)
    {
        if (target == null) return false;

        BroadcastSkillAnimation(ch, (ushort)Core.Enums.AnimationType.AttackWeapon, 0x0240);

        if (_gatheringEngine != null &&
            _gatheringEngine.TryGather(ch, SkillType.Fishing, target.Value, out bool regionSuccess, out _, out _))
            return regionSuccess;

        var marker = FindFallbackMarker(target.Value, "Fishing");
        if (marker != null && marker.Amount <= 0)
            return false;

        bool success = SkillEngine.UseQuick(ch, SkillType.Fishing, 40);
        if (success)
        {
            ConsumeFallbackMarker(target.Value, "Fishing", 1, ref marker);
            var fish = _world.CreateItem();
            fish.BaseId = 0x09CC;
            fish.Name = "fish";
            fish.Amount = 1;
            DeliverLegacyItem(ch, fish);
        }
        return success;
    }

    private bool HandleLumberjacking(Character ch, Point3D? target)
    {
        if (target == null) return false;

        BroadcastSkillAnimation(ch, (ushort)Core.Enums.AnimationType.Attack2HSlash, 0x013E);

        if (_gatheringEngine != null &&
            _gatheringEngine.TryGather(ch, SkillType.Lumberjacking, target.Value, out bool regionSuccess, out _, out _))
            return regionSuccess;

        var marker = FindFallbackMarker(target.Value, "Lumberjacking");
        if (marker != null && marker.Amount <= 0)
            return false;

        bool success = SkillEngine.UseQuick(ch, SkillType.Lumberjacking, 50);
        if (success)
        {
            int amount = Random.Shared.Next(1, 5);
            ConsumeFallbackMarker(target.Value, "Lumberjacking", amount, ref marker);
            var logs = _world.CreateItem();
            logs.BaseId = 0x1BDD;
            logs.Name = "logs";
            logs.Amount = (ushort)amount;
            DeliverLegacyItem(ch, logs);
        }
        return success;
    }

    private Item? FindFallbackMarker(Point3D tile, string skillTag)
    {
        foreach (var item in _world.GetItemsInRange(tile, 0))
        {
            if (item.BaseId != 0x1) continue;
            if (!item.TryGetTag("RESOURCE_MARKER", out string? mk) || mk != "1") continue;
            if (!item.TryGetTag("RES_SKILL", out string? st) || st != skillTag) continue;
            if (item.X == tile.X && item.Y == tile.Y) return item;
        }
        return null;
    }

    private void ConsumeFallbackMarker(Point3D tile, string skillTag, int amount, ref Item? marker)
    {
        if (marker == null)
        {
            marker = _world.CreateItem();
            marker.BaseId = 0x1;
            marker.Amount = (ushort)FallbackResAmount;
            marker.SetAttr(ObjAttributes.Invis | ObjAttributes.Move_Never);
            marker.SetTag("RESOURCE_MARKER", "1");
            marker.SetTag("RES_SKILL", skillTag);
            marker.DecayTime = Environment.TickCount64 + FallbackRegenMs;
            _world.PlaceItem(marker, tile);
        }

        int remaining = marker.Amount - amount;
        if (remaining <= 0)
        {
            marker.Amount = 0;
            marker.DecayTime = Environment.TickCount64 + FallbackRegenMs;
        }
        else
        {
            marker.Amount = (ushort)remaining;
        }
    }

    private static void BroadcastSkillAnimation(Character ch, ushort animId, ushort soundId)
    {
        var animPkt = new SphereNet.Network.Packets.Outgoing.PacketAnimation(ch.Uid.Value, animId);
        Character.BroadcastNearby?.Invoke(ch.Position, 18, animPkt, 0);
        var soundPkt = new SphereNet.Network.Packets.Outgoing.PacketSound(soundId, ch.X, ch.Y, ch.Z);
        Character.BroadcastNearby?.Invoke(ch.Position, 18, soundPkt, 0);
    }

    private void DeliverLegacyItem(Character ch, Item item)
    {
        var actual = ch.Backpack?.TryAddItemWithStack(item);
        if (actual == null)
            _world.PlaceItemWithDecay(item, ch.Position);
        else if (actual != item)
            item.Delete();
    }

    private bool HandleTaming(Character ch, Point3D? target)
    {
        if (target == null) return false;
        var npc = FindNearbyNpc(ch, target.Value);
        if (npc == null || npc.NpcBrain == NpcBrainType.Human) return false;
        BroadcastSkillAnimation(ch, (ushort)AnimationType.Bow, 0x0064);
        bool success = SkillEngine.UseQuick(ch, SkillType.Taming, 60);
        if (success)
        {
            npc.NpcBrain = NpcBrainType.Animal;
            success = npc.TryAssignOwnership(ch, ch, summoned: false, enforceFollowerCap: true);
        }
        return success;
    }

    private bool HandleAnimalLore(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.AnimalLore, 30);
    }

    private bool HandleHealing(Character ch, Point3D? target)
    {
        var healTarget = target != null ? FindNearbyChar(ch, target.Value) : ch;
        if (healTarget == null) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Healing, 40);
        if (success)
        {
            int healAmount = ch.GetSkill(SkillType.Healing) / 40 + 3;
            healTarget.Hits = (short)Math.Min(healTarget.Hits + healAmount, healTarget.MaxHits);
        }
        return success;
    }

    private bool HandleAnatomy(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Anatomy, 30);
    }

    private bool HandlePeacemaking(Character ch, Point3D? target)
    {
        BroadcastSkillAnimation(ch, (ushort)AnimationType.Bow, 0x0038);
        if (!SkillEngine.UseQuick(ch, SkillType.Musicianship, 40)) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Peacemaking, 50);
        if (success && target != null)
        {
            var npc = FindNearbyNpc(ch, target.Value);
            if (npc != null)
                npc.ClearStatFlag(StatFlag.War);
        }
        return success;
    }

    private bool HandleProvocation(Character ch, Point3D? target)
    {
        BroadcastSkillAnimation(ch, (ushort)AnimationType.Bow, 0x0038);
        if (!SkillEngine.UseQuick(ch, SkillType.Musicianship, 40)) return false;
        return SkillEngine.UseQuick(ch, SkillType.Provocation, 60);
    }

    private bool HandleMusicianship(Character ch, Point3D? target)
    {
        BroadcastSkillAnimation(ch, (ushort)AnimationType.Bow, 0x0038);
        return SkillEngine.UseQuick(ch, SkillType.Musicianship, 40);
    }

    private bool HandleMeditation(Character ch, Point3D? target)
    {
        if (ch.Mana >= ch.MaxMana) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Meditation, 40);
        if (success)
            ch.SetStatFlag(StatFlag.Meditation);
        return success;
    }

    private bool HandleSpiritSpeak(Character ch, Point3D? target)
    {
        bool success = SkillEngine.UseQuick(ch, SkillType.SpiritSpeak, 50);
        if (success)
            ch.SetStatFlag(StatFlag.SpiritSpeak);
        return success;
    }

    private bool HandlePoisoning(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Poisoning, 50);
    }

    private bool HandleItemID(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.ItemId, 30);
    }

    private bool HandleArmsLore(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.ArmsLore, 30);
    }

    private bool HandleTracking(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Tracking, 50);
    }

    private bool HandleForensics(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Forensics, 30);
    }

    private bool HandleBegging(Character ch, Point3D? target)
    {
        bool success = SkillEngine.UseQuick(ch, SkillType.Begging, 40);
        if (success && ch.Backpack != null)
        {
            var gold = _world.CreateItem();
            gold.BaseId = 0x0EED;
            gold.Name = "Gold";
            gold.ItemType = ItemType.Gold;
            gold.Amount = (ushort)Random.Shared.Next(1, 11); // 1-10, matching ActiveSkillEngine
            ch.Backpack.AddItem(gold);
        }
        return success;
    }

    // Targetless fallback path (UseSkill). Real theft/snoop goes through
    // ActiveSkillEngine.Stealing/Snooping (a targeted item), where the Source-X
    // CheckCrimeSeen witness pipeline runs. Without a victim/item there is nothing
    // to witness, so this fallback no longer flags criminal on its own (the old
    // blind coin flip diverged from the witness model).
    private bool HandleStealing(Character ch, Point3D? target) =>
        SkillEngine.UseQuick(ch, SkillType.Stealing, 60);

    private bool HandleSnooping(Character ch, Point3D? target) =>
        SkillEngine.UseQuick(ch, SkillType.Snooping, 50);

    private bool HandleLockpicking(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Lockpicking, 60);
    }

    private bool HandleRemoveTrap(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.RemoveTrap, 60);
    }

    private bool HandleInscription(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Inscription);
        return true;
    }

    private bool HandleCooking(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Cooking);
        return true;
    }

    private bool HandleAlchemy(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Alchemy);
        return true;
    }

    private bool HandleTailoring(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Tailoring);
        return true;
    }

    private bool HandleBlacksmithing(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Blacksmithing);
        return true;
    }

    private bool HandleCarpentry(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Carpentry);
        return true;
    }

    private bool HandleTinkering(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Tinkering);
        return true;
    }

    private bool HandleCartography(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Cartography);
        return true;
    }

    private bool HandleBowcraft(Character ch, Point3D? target)
    {
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Bowcraft);
        return true;
    }

    private bool HandleTasteID(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.TasteId, 30);
    }

    private bool HandleEvalInt(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.EvalInt, 30);
    }

    private bool HandleVeterinary(Character ch, Point3D? target)
    {
        // Heal animals/pets — requires bandages in backpack
        var targetPt = target ?? ch.Position;
        var animal = FindNearbyNpc(ch, targetPt);
        if (animal == null) return false;

        // Must be an animal (or tamed pet with a master)
        if (animal.NpcBrain != NpcBrainType.Animal && animal.NpcBrain != NpcBrainType.Monster)
            return false;

        BroadcastSkillAnimation(ch, (ushort)AnimationType.Bow, 0x0057);
        bool success = SkillEngine.UseQuick(ch, SkillType.Veterinary, 40);
        if (success)
        {
            int heal = ch.GetSkill(SkillType.Veterinary) / 40 + 3;
            animal.Hits = (short)Math.Min(animal.Hits + heal, animal.MaxHits);
        }
        return success;
    }

    private bool HandleHerding(Character ch, Point3D? target)
    {
        // Direct an animal to a location using a crook
        var targetPt = target ?? ch.Position;
        var animal = FindNearbyNpc(ch, targetPt);
        if (animal == null) return false;

        if (animal.NpcBrain != NpcBrainType.Animal)
            return false;

        bool success = SkillEngine.UseQuick(ch, SkillType.Herding, 40);
        if (success)
        {
            // Move animal toward caster's target direction
            animal.SetTag("HERD_MASTER", ch.Uid.Value.ToString());
            animal.SetTag("HERD_MASTER_UUID", ch.Uuid.ToString("D"));
        }
        return success;
    }

    private bool HandleCamping(Character ch, Point3D? target)
    {
        var usedItem = ch.Act.IsValid ? _world.FindItem(ch.Act) : null;
        if (usedItem?.ItemType == ItemType.Bedroll)
        {
            bool hasOwnFire = _world.GetItemsInRange(ch.Position, 2).Any(item =>
                item.ItemType == ItemType.Campfire &&
                item.TryGetTag("CAMPFIRE_OWNER_UUID", out string? owner) &&
                owner == ch.Uuid.ToString("D"));
            if (!hasOwnFire)
                return false;
            ch.SetTag("CAMPING_SAFE_LOGOUT_UNTIL",
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000).ToString());
            return true;
        }

        Item? kindling = usedItem?.ItemType == ItemType.Kindling
            ? usedItem
            : FindBackpackItemRecursive(ch.Backpack, ItemType.Kindling, 16, []);
        if (kindling == null)
            return false;

        bool success = SkillEngine.UseQuick(ch, SkillType.Camping, Random.Shared.Next(30));
        ConsumeOne(kindling);
        if (!success) return false;

        var campfire = _world.CreateItem();
        campfire.BaseId = 0x0DE3;
        campfire.ItemType = ItemType.Campfire;
        campfire.SetTag("CAMPFIRE_OWNER", ch.Uid.Value.ToString());
        campfire.SetTag("CAMPFIRE_OWNER_UUID", ch.Uuid.ToString("D"));
        if (!_world.PlaceItem(campfire, ch.Position))
        {
            _world.DeleteObject(campfire);
            return false;
        }
        campfire.DecayTime = Environment.TickCount64 + 30_000;
        return success;
    }

    private bool HandleImbuing(Character ch, Point3D? target)
    {
        // Imbuing is recipe-driven. The selected recipe owns the actual
        // skill roll, resources and trigger chain.
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Imbuing);
        return true;
    }

    private Item? FindBackpackItemRecursive(Item? container, ItemType type, int depth,
        HashSet<uint> seen)
    {
        if (container == null || depth < 0 || !seen.Add(container.Uid.Value)) return null;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type) return item;
            var found = FindBackpackItemRecursive(item, type, depth - 1, seen);
            if (found != null) return found;
        }
        return null;
    }

    private void ConsumeOne(Item item)
    {
        if (item.Amount > 1)
        {
            item.Amount--;
            return;
        }
        if (item.ContainedIn.IsValid)
            _world.FindItem(item.ContainedIn)?.RemoveItem(item);
        _world.DeleteObject(item);
    }

    private Character? FindNearbyNpc(Character ch, Point3D target)
    {
        foreach (var c in _world.GetCharsInRange(target, 2))
        {
            if (!c.IsPlayer && !c.IsDeleted && !c.IsDead)
                return c;
        }
        return null;
    }

    private Character? FindNearbyChar(Character ch, Point3D target)
    {
        foreach (var c in _world.GetCharsInRange(target, 2))
        {
            if (!c.IsDeleted && !c.IsDead)
                return c;
        }
        return null;
    }
}
