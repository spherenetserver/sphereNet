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

    /// <summary>What the node at a tile has to say before a gathering swing starts.
    /// Null when there is no gathering engine wired (unit tests).</summary>
    public GatherResult? ProbeGatherNode(Character ch, SkillType skill, Point3D target) =>
        _gatheringEngine?.ProbeResource(ch, skill, target);

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
            case SkillType.Enticement:       return ActiveSkillEngine.Enticement(sink, target as Character);
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

    // Only the skills UseSkill can still reach have a handler here: the crafting
    // skills (when no craft hook is wired), Camping and Imbuing. Every targeted and
    // information skill returns before this table (see UseSkill) and runs through
    // ActiveSkillEngine / InfoSkillEngine; the targetless stand-ins that used to be
    // registered for them - fixed difficulties, a hardcoded ore, fish, log, a
    // begging purse - were unreachable and not upstream's.
    private void RegisterAll()
    {
        _handlers[SkillType.Inscription] = HandleInscription;
        _handlers[SkillType.Cooking] = HandleCooking;
        _handlers[SkillType.Alchemy] = HandleAlchemy;
        _handlers[SkillType.Tailoring] = HandleTailoring;
        _handlers[SkillType.Blacksmithing] = HandleBlacksmithing;
        _handlers[SkillType.Carpentry] = HandleCarpentry;
        _handlers[SkillType.Tinkering] = HandleTinkering;
        _handlers[SkillType.Cartography] = HandleCartography;
        _handlers[SkillType.Bowcraft] = HandleBowcraft;
        _handlers[SkillType.Camping] = HandleCamping;
        _handlers[SkillType.Imbuing] = HandleImbuing;
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

    /// <summary>ITEMID_CAMPFIRE (uofiles_enums_itemid.h).</summary>
    private const ushort CampfireId = 0x0DE3;

    /// <summary>Source-X CChar::Use_Kindling (CCharUse.cpp:272-296), reached when
    /// kindling is used (ch.Act). The pile has to lie on the ground
    /// (DEFMSG_ITEMUSE_KINDLING_CONT); Camping is rolled against rand(30), a miss
    /// saying DEFMSG_ITEMUSE_KINDLING_FAIL and spending nothing. A success turns THE
    /// PILE ITSELF into a campfire: fixed in place and decaying, burning
    /// (4 + amount) minutes, amount 1 ("all kindling is set to one fire"), sound
    /// 0x226. A fresh fire item, a consumed stick from the pack, a 30-second life
    /// and a bedroll "safe logout" were all invented - Use_BedRoll only rolls the
    /// bedroll up or out (CCharUse.cpp:1534-1570).</summary>
    private bool HandleCamping(Character ch, Point3D? target)
    {
        var kindling = ch.Act.IsValid ? _world.FindItem(ch.Act) : null;
        if (kindling == null || kindling.IsDeleted || kindling.ItemType != ItemType.Kindling)
            return false;

        if (kindling.ContainedIn.IsValid)
        {
            Character.SendOwnerMessage?.Invoke(ch,
                Messages.ServerMessages.Get(Messages.Msg.ItemuseKindlingCont));
            return false;
        }

        if (!SkillEngine.UseQuick(ch, SkillType.Camping, Random.Shared.Next(30)))
        {
            Character.SendOwnerMessage?.Invoke(ch,
                Messages.ServerMessages.Get(Messages.Msg.ItemuseKindlingFail));
            return false;
        }

        int burnSeconds = (4 + kindling.Amount) * 60;
        kindling.BaseId = CampfireId;
        kindling.ItemType = DefinitionLoader.GetItemDef(CampfireId)?.Type is { } t && t != ItemType.Normal
            ? t
            : ItemType.Campfire;
        kindling.SetAttr(ObjAttributes.Move_Never | ObjAttributes.CanDecay);
        kindling.Amount = 1;
        kindling.SetDecayAt(Environment.TickCount64 + burnSeconds * 1000L);
        Item.OnVisualUpdate?.Invoke(kindling);
        var soundPkt = new SphereNet.Network.Packets.Outgoing.PacketSound(0x0226,
            kindling.X, kindling.Y, kindling.Z);
        Character.BroadcastNearby?.Invoke(kindling.Position, 18, soundPkt, 0);
        return true;
    }

    private bool HandleImbuing(Character ch, Point3D? target)
    {
        // Imbuing is recipe-driven. The selected recipe owns the actual
        // skill roll, resources and trigger chain.
        if (OnCraftSkillUsed == null) return false;
        OnCraftSkillUsed.Invoke(ch, SkillType.Imbuing);
        return true;
    }
}
