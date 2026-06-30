using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Messages;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;

namespace SphereNet.Game.Magic;

/// <summary>
/// Spell engine. Maps to CChar::Spell_* functions in Source-X CCharSpell.cpp.
/// Handles cast start, cast done, spell effects, damage/heal, resist.
/// </summary>
public sealed class SpellEngine
{
    private readonly GameWorld _world;
    private readonly SpellRegistry _spells;
    private readonly Random _rand = new();

    public SpellEngine(GameWorld world, SpellRegistry spells)
    {
        _world = world;
        _spells = spells;
    }

    /// <summary>Callback to play a sound at a location.</summary>
    public Action<Point3D, ushort>? OnPlaySound { get; set; }

    /// <summary>Source-X CClientMsg::SysMessage hook for the active caster.
    /// Program.cs wires this to the owning GameClient so spell-specific
    /// failure/success messages (recall blank rune, gate already there,
    /// poison resisted, etc.) reach only the caster, matching upstream.</summary>
    public Action<Character, string>? OnSysMessage { get; set; }

    /// <summary>Callback fired when a spell is interrupted. Args: (Character caster, string reason).</summary>
    public Action<Character, string>? OnSpellInterrupt { get; set; }

    /// <summary>Fired when a cast starts after we've turned the caster
    /// to face the target — Program.cs uses this to broadcast a 0x77
    /// MobileMoving so other clients see the new facing while the cast
    /// animation plays. Without this, the caster appears to throw the
    /// spell sideways.</summary>
    public Action<Character>? OnCasterFacingChanged { get; set; }
    public Action<Character, ushort>? OnCastAnimation { get; set; }

    /// <summary>Callback fired to broadcast spell power words as overhead
    /// speech (e.g. "In Lor" for Night Sight). Program.cs wires this to
    /// send a 0x1C speech packet visible to nearby clients.</summary>
    public Action<Character, string>? OnSpellWords { get; set; }

    /// <summary>Callback fired when a CLIENTLESS caster's spell completes —
    /// the player completion path (TickSpellCast) sends its own bolt/impact
    /// effect, but NPC casts have no client and were entirely invisible.
    /// Args: caster, resolved char target (null = location/self), spell def.</summary>
    public Action<Character, Character?, SpellDef>? OnNpcCastFx { get; set; }

    /// <summary>Callback fired when a character's personal light level
    /// changes (e.g. after Night Sight). Program.cs wires this to the
    /// matching GameClient so it can send a fresh 0x4E packet.</summary>
    public Action<Character>? OnPersonalLightChanged { get; set; }

    /// <summary>Callback fired when a spell kills a target. Program.cs wires
    /// this to the DeathEngine pipeline so corpse/loot/triggers are processed
    /// instead of the bare Character.Kill() that skips them.</summary>
    public Action<Character, Character?>? OnTargetKilled { get; set; }

    public Action<Character, Point3D, byte>? OnSpellTeleport { get; set; }

    /// <summary>Remove a world item and broadcast its deletion (used by
    /// Dispel Field to clear a field item early).</summary>
    public Action<Item>? OnItemRemoved { get; set; }

    /// <summary>Optional script dispatcher for item spell hooks.</summary>
    public TriggerDispatcher? TriggerDispatcher { get; set; }

    /// <summary>One entry per active time-limited spell effect. Captures
    /// what was applied (stat deltas, light level, flag) so UndoEffect can
    /// revert exactly those changes when the timer fires. Runtime-only —
    /// effects do not persist across server restart, matching Source-X buff
    /// semantics.</summary>
    private sealed class ActiveSpellEffect
    {
        public required Character Target { get; init; }
        public required SpellType Spell { get; init; }
        public long ExpireTick { get; set; }
        public short StrDelta { get; set; }
        public short DexDelta { get; set; }
        public short IntDelta { get; set; }
        public byte OldLightLevel { get; set; }
        public bool LightChanged { get; set; }
        public StatFlag AppliedFlag { get; set; }
        public ushort OldBodyId { get; set; }
        public bool BodyChanged { get; set; }
        public string? OldName { get; set; }
        public bool NameChanged { get; set; }
    }

    // Disguise names used by Incognito.
    private static readonly string[] s_incognitoNames =
    {
        "Adam", "Brom", "Cyne", "Doran", "Edric", "Faerd", "Gareth", "Halt",
        "Ivar", "Joran", "Kael", "Loric", "Maren", "Nyle", "Oren", "Pael",
        "Quenn", "Roth", "Sael", "Tarl", "Ulric", "Varis", "Wren", "Yorick",
    };

    /// <summary>Active time-limited spell effects. Walked once per world tick
    /// by <see cref="ProcessExpirations"/>; when the tick is reached the
    /// entry is removed and <see cref="UndoEffect"/> reverts its recorded
    /// deltas.</summary>
    private readonly List<ActiveSpellEffect> _activeEffects = [];

    /// <summary>Get a spell definition by type (for flag checks, etc.).</summary>
    public SpellDef? GetSpellDef(SpellType spell) => _spells.Get(spell);

    /// <summary>
    /// Advance an in-progress cast timer. Returns true while still casting.
    /// Used by player TickSpellCast and NPC AI ticks.
    /// </summary>
    public bool TickCastTimer(Character caster)
    {
        if (!caster.IsCasting)
            return false;

        if (caster.IsCastTimerActive(Environment.TickCount64))
            return true;

        caster.SetCastTimerEnd(0);
        CastDone(caster);
        return false;
    }

    private static bool IsMagicFlag(MagicConfigFlags flag) =>
        (Character.MagicFlags & (int)flag) != 0;

    private static bool IsCastingWithWand(Character caster)
    {
        var weapon = caster.GetEquippedItem(Layer.OneHanded)
                  ?? caster.GetEquippedItem(Layer.TwoHanded);
        return weapon?.ItemType == ItemType.Wand;
    }

    private static bool IsSpellDisabledByConfig(SpellType spell)
    {
        return spell switch
        {
            SpellType.Mark when IsMagicFlag(MagicConfigFlags.DisableMark) => true,
            SpellType.Recall when IsMagicFlag(MagicConfigFlags.DisableRecall) => true,
            SpellType.GateTravel when IsMagicFlag(MagicConfigFlags.DisableGate) => true,
            _ => false
        };
    }

    private static void RevealOnCast(Character caster)
    {
        if (IsMagicFlag(MagicConfigFlags.NoRevealOnCast))
            return;
        caster.ClearHiddenState();
    }

    /// <summary>Precast mode from sphere.ini MAGICFLAGS bit 0x0001.</summary>
    public static bool IsPrecastEnabled(SpellDef def) =>
        IsMagicFlag(MagicConfigFlags.Precast) && !def.IsFlag(SpellFlag.NoPrecast);

    private static bool IsOutdoorOnlySpell(SpellType spell) => spell switch
    {
        SpellType.ChainLightning or SpellType.Flamestrike or
        SpellType.MeteorSwarm or SpellType.EnergyVortex or
        SpellType.Earthquake or SpellType.AirElemental or
        SpellType.EarthElemental or SpellType.FireElemental or
        SpellType.WaterElemental => true,
        _ => false,
    };

    private bool CanCastOutdoorSpell(Character caster, SpellDef def, Point3D pos)
    {
        if (!IsOutdoorOnlySpell(def.Id)) return true;
        if (IsMagicFlag(MagicConfigFlags.DungeonOutdoorSpells)) return true;
        if (caster.PrivLevel >= PrivLevel.GM) return true;
        var region = _world.FindRegion(pos);
        return region == null || !region.IsFlag(RegionFlag.Underground);
    }

    private void ApplyCastResourceLoss(Character caster, SpellDef def, bool wand, bool fizzle, bool abort)
    {
        if (caster.PrivLevel >= PrivLevel.GM)
            return;

        bool takeReagents = !wand && Character.ReagentsRequiredEnabled && HasRequiredReagents(caster, def);
        if (fizzle && !Character.ReagentLossFail) takeReagents = false;
        if (abort && !Character.ReagentLossAbort) takeReagents = false;

        bool takeMana = true;
        if (fizzle && !Character.ManaLossFail) takeMana = false;
        if (abort && !Character.ManaLossAbort) takeMana = false;

        if (takeMana && def.ManaCost > 0)
        {
            int cost = Math.Max(0, def.ManaCost * Character.ManaLossPercent / 100);
            caster.Mana = (short)Math.Max(0, caster.Mana - cost);
        }

        if (takeReagents)
            ConsumeReagents(caster, def);
    }

    private void InterruptCast(Character caster, string reason)
    {
        SpellDef? def = null;
        if (caster.TryGetCastingSpell(out SpellType spell))
            def = _spells.Get(spell);

        if (def != null)
            ApplyCastResourceLoss(caster, def, IsCastingWithWand(caster), fizzle: false, abort: true);

        ClearCastState(caster);

        string msg = reason switch
        {
            "damaged" or "moved" or "equip_changed" => ServerMessages.Get(Msg.SpellGenFizzles),
            _ => reason
        };
        OnSpellInterrupt?.Invoke(caster, msg);
        if (caster.IsPlayer)
            OnSysMessage?.Invoke(caster, msg);
    }

    /// <summary>
    /// Check and apply spell interruption from damage.
    /// Call this when a casting character takes damage.
    /// Returns true if the spell was interrupted.
    /// </summary>
    public bool TryInterruptFromDamage(Character caster, int damage)
    {
        if (!caster.IsCasting)
            return false;

        if (IsMagicFlag(MagicConfigFlags.NoInterrupt))
            return false;

        // Reference disturb (OnTakeDamage): only players are disturbed; the
        // chance is the spell's INTERRUPT curve at the caster's skill
        // (per-mille) — the damage amount does not factor in.
        if (!caster.IsPlayer)
            return false;

        int chance = 1000;
        if (caster.TryGetCastingSpell(out SpellType castingSpell))
        {
            var def = GetSpellDef(castingSpell);
            if (def != null)
                chance = def.GetInterruptChance(caster.GetSkill(def.GetPrimarySkill()));
        }
        if (chance <= 0)
            return false;

        // Protection effect dampens the disturb (engine approximation of the
        // reference protection-spell cancel).
        if (caster.IsStatFlag(StatFlag.ArcherCanMove))
            chance /= 2;

        if (_rand.Next(1000) < chance)
        {
            InterruptCast(caster, "damaged");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Check and apply spell interruption from movement.
    /// Call this when a casting character moves.
    /// Returns true if the spell was interrupted.
    /// </summary>
    public bool TryInterruptFromMovement(Character caster)
    {
        if (!caster.IsCasting)
            return false;

        if (caster.PrivLevel >= PrivLevel.GM)
            return false;

        if (IsMagicFlag(MagicConfigFlags.NoInterrupt))
            return false;

        InterruptCast(caster, "moved");
        return true;
    }

    /// <summary>
    /// Check and apply spell interruption from equipment change.
    /// Call this when a casting character equips/unequips an item.
    /// Returns true if the spell was interrupted.
    /// </summary>
    public bool TryInterruptFromEquip(Character caster)
    {
        if (!caster.IsCasting)
            return false;

        if (IsMagicFlag(MagicConfigFlags.NoInterrupt))
            return false;

        InterruptCast(caster, "equip_changed");
        return true;
    }

    /// <summary>
    /// Begin casting a spell. Maps to Spell_CastStart.
    /// Returns cast time in milliseconds, or -1 on failure.
    /// </summary>
    public int CastStart(Character caster, SpellType spell, Serial targetUid, Point3D targetPos)
    {
        var def = _spells.Get(spell);
        if (def == null || def.IsFlag(SpellFlag.Disabled))
            return -1;

        if (IsSpellDisabledByConfig(spell))
            return -1;

        if (caster.IsDead)
            return -1;
        if (caster.IsCasting)
            return -1;
        if (caster.IsStatFlag(StatFlag.Freeze))
            return -1;
        // Cast recovery — block back-to-back spam (non-GM).
        if (caster.PrivLevel < PrivLevel.GM && caster.IsCastOnRecovery(Environment.TickCount64))
            return -1;

        // Region NoMagic check
        if (_world != null)
        {
            var region = _world.FindRegion(caster.Position);
            if (region != null && region.NoMagic && caster.PrivLevel < Core.Enums.PrivLevel.GM)
                return -1;
        }

        // Mana check
        if (caster.Mana < def.ManaCost)
            return -1;

        if (!CanCastOutdoorSpell(caster, def, targetPos))
        {
            OnSysMessage?.Invoke(caster, "That spell does not work here.");
            return -1;
        }

        var primarySkill = def.GetPrimarySkill();
        int skillVal = caster.GetSkill(primarySkill);

        var weapon = caster.GetEquippedItem(Layer.OneHanded);
        var offhand = caster.GetEquippedItem(Layer.TwoHanded);
        bool isWand = weapon?.ItemType == ItemType.Wand;
        bool fromScroll = caster.TryGetTag("SCROLL_UID", out _);
        // A wielded spellbook never blocks casting (reference: casting from
        // the book in hand is the normal flow); wands likewise.
        bool hasBlockingWeapon =
            (weapon != null && weapon.ItemType is not ItemType.Wand and not ItemType.Spellbook) ||
            (offhand != null && offhand.ItemType is not ItemType.Shield and not ItemType.Spellbook);

        if (!Character.EquippedCastEnabled && caster.IsPlayer && hasBlockingWeapon &&
            caster.PrivLevel < PrivLevel.GM)
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
            return -1;
        }

        // Reagent availability check (before starting cast). Wand/scroll/GM skip.
        // NPC casters are exempt — like the reference, monsters cast from innate
        // power and carry no reagents (mirrors the IsPlayer-gated spellbook check
        // below). Without this gate an NPC with an empty pack fails every cast.
        if (caster.IsPlayer && !isWand && !fromScroll && caster.PrivLevel < PrivLevel.GM &&
            Character.ReagentsRequiredEnabled && !HasRequiredReagents(caster, def))
        {
            OnSysMessage?.Invoke(caster, "You lack the reagents to cast that spell.");
            return -1;
        }

        // Spellbook requirement (reference Spell_CanCast): a player casting
        // from memory must have the spell in an accessible spellbook; scroll
        // and wand casts bypass the book. Only the classic 1-64 ids are
        // tracked through the book bit mask (More1/More2).
        if (Character.SpellbookRequiredEnabled && caster.IsPlayer &&
            !isWand && !fromScroll && caster.PrivLevel < PrivLevel.GM &&
            (int)def.Id is >= 1 and <= 64 &&
            !HasSpellInBook(caster, (int)def.Id))
        {
            OnSysMessage?.Invoke(caster, "You don't know that spell.");
            return -1;
        }

        // Cast time
        int castTimeTenths = def.GetCastTime(skillVal);
        if (caster.PrivLevel >= PrivLevel.GM)
            castTimeTenths = 1;

        RevealOnCast(caster);

        // Store cast state on character
        caster.BeginCast(spell, targetUid, targetPos);

        if (!targetPos.Equals(caster.Position))
        {
            var newDir = caster.Position.GetDirectionTo(targetPos);
            if (newDir != caster.Direction)
            {
                caster.Direction = newDir;
                OnCasterFacingChanged?.Invoke(caster);
            }
        }

        var powerWords = def.GetPowerWords();
        if (!string.IsNullOrEmpty(powerWords))
            OnSpellWords?.Invoke(caster, powerWords);

        bool isAreaSpell = targetUid == caster.Uid;
        ushort castAnim = isAreaSpell
            ? (ushort)Core.Enums.AnimationType.CastArea
            : (ushort)Core.Enums.AnimationType.CastDirected;
        OnCastAnimation?.Invoke(caster, castAnim);

        return castTimeTenths * 100;
    }

    /// <summary>
    /// Complete a spell cast. Maps to Spell_CastDone.
    /// Called after cast timer expires.
    /// </summary>
    public bool CastDone(Character caster)
    {
        if (caster.IsDead)
        {
            ClearCastState(caster);
            return false;
        }

        if (!caster.TryGetCastingSpell(out SpellType spell))
            return false;

        var def = _spells.Get(spell);
        if (def == null)
        {
            ClearCastState(caster);
            return false;
        }

        // Resolve target before consumption so LOS can be checked first
        Serial targetUid = caster.CastTargetUid;
        Point3D targetPos = caster.CastTargetPos;

        // LOS check BEFORE consuming resources
        if (_world != null &&
            caster.PrivLevel < PrivLevel.GM &&
            !IsMagicFlag(MagicConfigFlags.NoLos) &&
            (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj) ||
             def.IsFlag(SpellFlag.Area)     || def.IsFlag(SpellFlag.Field) ||
             def.IsFlag(SpellFlag.Summon)))
        {
            int losDist = Math.Max(Math.Abs(caster.X - targetPos.X), Math.Abs(caster.Y - targetPos.Y));
            if (losDist > 0 && !_world.CanSeeLOS(caster.Position, targetPos))
            {
                ClearCastState(caster);
                OnSysMessage?.Invoke(caster, "Target not in line of sight.");
                return false;
            }
        }

        var primarySkill = def.GetPrimarySkill();
        int skillVal = caster.GetSkill(primarySkill);
        int difficulty = def.GetDifficulty();
        bool castWithWand = IsCastingWithWand(caster);
        bool castFromScroll = caster.TryGetTag("SCROLL_UID", out _);
        if (castWithWand) difficulty = 10;          // reference: wand = minimal difficulty
        else if (castFromScroll) difficulty /= 2;   // reference: scroll = half difficulty

        // Cast recovery — a completed cast (success OR fizzle) starts a cooldown
        // before the next one. Higher skill recovers faster (FCR-style).
        if (caster.PrivLevel < PrivLevel.GM)
        {
            int recoveryMs = Math.Max(400, 1500 - skillVal);
            caster.SetCastRecovery(Environment.TickCount64 + recoveryMs);
        }

        // Source-X: skill check at cast completion — fizzle on failure.
        // GetDifficulty() is on the 0-1000 skill scale, but CheckSuccess expects
        // a 0-100 difficulty (it multiplies by 10 internally) — convert here so
        // the bell curve compares like-for-like against the 0-1000 skill value.
        bool fizzled = caster.PrivLevel < PrivLevel.GM &&
            !SkillEngine.CheckSuccess(caster, primarySkill, difficulty / 10);

        // Source-X Spell_CastDone awards the casting skill a gain attempt on every
        // resolved cast, whether it succeeds or fizzles. (GainExperience itself
        // guards GM/dead/locked/safe-region.)
        if (caster.PrivLevel < PrivLevel.GM)
            SkillEngine.GainExperience(caster, primarySkill, difficulty / 10);

        if (fizzled)
        {
            ApplyCastResourceLoss(caster, def, castWithWand, fizzle: true, abort: false);
            ClearCastState(caster);
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
            return false;
        }

        // Consume mana
        if (caster.Mana < def.ManaCost)
        {
            ClearCastState(caster);
            OnSysMessage?.Invoke(caster, "You lack the mana to cast that spell.");
            return false;
        }

        if (caster.IsPlayer && !castWithWand && caster.PrivLevel < PrivLevel.GM &&
            Character.ReagentsRequiredEnabled)
        {
            ConsumeReagents(caster, def);
        }

        int manaCost = Math.Max(0, def.ManaCost * Character.ManaLossPercent / 100);
        if (castWithWand) manaCost = 0;             // reference: wands cost no mana
        else if (castFromScroll) manaCost /= 2;     // reference: scrolls cost half mana
        caster.Mana -= (short)Math.Min(manaCost, caster.Mana);

        // Clear cast state
        ClearCastState(caster);

        int skillLevel = skillVal;

        // Mark targets an item (rune), not a character
        if (spell == SpellType.Mark)
        {
            var rune = _world?.FindItem(targetUid);
            if (rune != null)
            {
                if (!IsItemAccessible(caster, rune))
                {
                    OnSysMessage?.Invoke(caster, "You must target a rune in your pack.");
                }
                else if (FireItemSpellEffect(caster, rune, def) == TriggerResult.True)
                {
                    // Script handled/cancelled the native mark.
                }
                else
                {
                    rune.SetRuneMark(caster.Position);
                    OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellMarkCont));
                }
            }
            else
            {
                OnSysMessage?.Invoke(caster, "You must target a recall rune.");
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // Dispel Field targets a field ITEM (not a character) and removes it.
        if (spell == SpellType.DispelField)
        {
            var fieldItem = _world?.FindItem(targetUid);
            if (fieldItem != null && !fieldItem.IsDeleted &&
                (fieldItem.TryGetTag("FIELD_DAMAGE", out _) || fieldItem.ItemType == ItemType.Spell))
            {
                if (FireItemSpellEffect(caster, fieldItem, def) != TriggerResult.True)
                    OnItemRemoved?.Invoke(fieldItem);
            }
            else
            {
                OnSysMessage?.Invoke(caster, "That is not a magical field.");
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // Apply spell effect
        if (spell is SpellType.Recall or SpellType.GateTravel)
        {
            var rune = _world?.FindItem(targetUid);
            if (rune != null && IsItemAccessible(caster, rune))
            {
                if (FireItemSpellEffect(caster, rune, def) != TriggerResult.True)
                    ApplyRuneTravelSpell(caster, rune, def);
            }
            else
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
        }
        else if (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj))
        {
            var target = _world?.FindChar(targetUid);
            if (target != null)
            {
                if (target.IsDead && !def.IsFlag(SpellFlag.TargDead))
                {
                    OnSysMessage?.Invoke(caster, "That target is dead.");
                }
                else if (target.MapIndex != caster.MapIndex ||
                    caster.Position.GetDistanceTo(target.Position) > 12)
                {
                    OnSysMessage?.Invoke(caster, "That is too far away.");
                }
                else
                {
                    ApplyCharEffect(caster, target, def, skillLevel);
                }
            }
            else if (def.IsFlag(SpellFlag.TargObj))
            {
                var itemTarget = _world?.FindItem(targetUid);
                if (itemTarget != null && CanSpellReachItem(caster, itemTarget))
                {
                    // Fire the scriptable @SpellEffect first; only apply the
                    // hardcoded item-spell behavior when no script overrode it.
                    if (FireItemSpellEffect(caster, itemTarget, def) != TriggerResult.True)
                        ApplyItemTargetSpell(itemTarget, def);
                }
            }
        }
        else if (def.IsFlag(SpellFlag.Area))
        {
            ApplyAreaEffect(caster, targetPos, def, skillLevel);
        }
        else if (def.IsFlag(SpellFlag.Field))
        {
            CreateField(caster, targetPos, def);
        }
        else if (def.IsFlag(SpellFlag.Summon))
        {
            SummonCreature(caster, targetPos, def, skillLevel);
        }
        else
        {
            // Self-buff or ground target
            ApplyCharEffect(caster, caster, def, skillLevel);
        }

        // NPC casters have no client-side completion path (the player's
        // TickSpellCast sends the impact/bolt effect there); emit their
        // completion FX through the host hook so observers see the spell land.
        if (!caster.IsPlayer)
            OnNpcCastFx?.Invoke(caster, _world?.FindChar(targetUid), def);

        // Sound
        if (def.Sound > 0)
            OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);

        return true;
    }

    /// <summary>Hardcoded behavior for item-targeted spells (Magic Lock/Unlock,
    /// Magic Trap/Untrap). Runs only when no @SpellEffect script overrode it.
    /// (Telekinesis remains script-driven via @SpellEffect — its real effect is
    /// a client-routed remote double-click.)</summary>
    private static void ApplyItemTargetSpell(Item item, SpellDef def)
    {
        switch (def.Id)
        {
            case SpellType.MagicLock:
                if (item.ItemType == ItemType.Container) item.ItemType = ItemType.ContainerLocked;
                else if (item.ItemType == ItemType.Door) item.ItemType = ItemType.DoorLocked;
                break;
            case SpellType.Unlock:
                if (item.ItemType == ItemType.ContainerLocked) item.ItemType = ItemType.Container;
                else if (item.ItemType == ItemType.DoorLocked) item.ItemType = ItemType.Door;
                break;
            case SpellType.MagicTrap:
                item.SetTag("TRAPPED", "1");
                break;
            case SpellType.MagicUntrap:
                item.RemoveTag("TRAPPED");
                break;
        }
    }

    private TriggerResult FireItemSpellEffect(Character caster, Item item, SpellDef def)
    {
        if (TriggerDispatcher == null)
            return TriggerResult.Default;

        return TriggerDispatcher.FireItemTrigger(item, ItemTrigger.SpellEffect, new TriggerArgs
        {
            CharSrc = caster,
            ItemSrc = item,
            O1 = caster,
            N1 = (int)def.Id,
            S1 = def.Name,
        });
    }

    /// <summary>Return true if the caster's backpack holds at least the needed
    /// amount of every reagent the spell requires. Reagents are identified by
    /// BaseId; stacked amounts contribute per-stack.</summary>
    /// <summary>True when the spell's bit is set in any accessible
    /// spellbook (equipped hands or top level of the backpack). Spellbook
    /// content uses the classic 64-bit mask in More1/More2.</summary>
    internal bool HasSpellInBook(Character caster, int spellId)
    {
        ulong bit = 1UL << (spellId - 1);
        foreach (var book in EnumerateSpellbooks(caster))
        {
            ulong bits = ((ulong)book.More2 << 32) | book.More1;
            if ((bits & bit) != 0)
                return true;
        }
        return false;
    }

    private IEnumerable<Item> EnumerateSpellbooks(Character caster)
    {
        var oneHand = caster.GetEquippedItem(Layer.OneHanded);
        if (oneHand?.ItemType == ItemType.Spellbook)
            yield return oneHand;
        var twoHand = caster.GetEquippedItem(Layer.TwoHanded);
        if (twoHand?.ItemType == ItemType.Spellbook)
            yield return twoHand;
        if (caster.Backpack != null)
        {
            foreach (var item in caster.Backpack.Contents)
            {
                if (item.ItemType == ItemType.Spellbook)
                    yield return item;
            }
        }
    }

    private bool HasRequiredReagents(Character caster, SpellDef def)
    {
        if (def.Reagents.Count == 0) return true;
        if (caster.Backpack == null) return false;
        foreach (var (regBaseId, needed) in def.Reagents)
        {
            int have = 0;
            foreach (var item in _world.GetContainerContents(caster.Backpack.Uid))
            {
                if (item.IsDeleted) continue;
                if (item.BaseId != regBaseId) continue;
                have += Math.Max(1, (int)item.Amount);
                if (have >= needed) break;
            }
            if (have < needed) return false;
        }
        return true;
    }

    /// <summary>Deduct the spell's reagent cost from the caster's backpack.
    /// Removes stacks that hit zero. Caller must have already verified
    /// availability with HasRequiredReagents.</summary>
    private void ConsumeReagents(Character caster, SpellDef def)
    {
        if (def.Reagents.Count == 0) return;
        if (caster.Backpack == null) return;
        foreach (var (regBaseId, needed) in def.Reagents)
        {
            int remaining = needed;
            // Snapshot — we mutate items/delete, can't iterate live collection.
            var stacks = _world.GetContainerContents(caster.Backpack.Uid).ToList();
            foreach (var item in stacks)
            {
                if (remaining <= 0) break;
                if (item.IsDeleted) continue;
                if (item.BaseId != regBaseId) continue;
                int stackAmt = Math.Max(1, (int)item.Amount);
                int take = Math.Min(remaining, stackAmt);
                ushort newAmt = (ushort)(stackAmt - take);
                item.Amount = newAmt;
                remaining -= take;
                if (newAmt == 0)
                {
                    _world.DeleteObject(item);
                    item.Delete();
                }
            }
        }
    }

    /// <summary>Apply spell effect to a single character target.</summary>
    private void ApplyCharEffect(Character caster, Character target, SpellDef def, int skillLevel)
    {
        // Magic Reflect: the first harmful spell targeted at a mobile with
        // the Reflection flag is bounced back to the caster. Matches
        // Source-X Magic_Reflect and ServUO MagicReflectSpell behaviour:
        // the flag is single-use — consumed as soon as a harmful spell
        // hits, so the reflected spell itself will NOT be re-reflected
        // by the original caster (even if they also have Reflection up).
        bool harmful = def.IsFlag(SpellFlag.Damage) || def.IsFlag(SpellFlag.Curse);

        // A harmful spell on a player who is innocent FROM THE CASTER'S VIEW is a
        // crime (Source-X notoriety) — the caster goes grey, like a melee attack.
        // A target the caster holds SawCrime / HarmedBy of (one who struck first,
        // or whose crime the caster witnessed) is not innocent to them, so the
        // spell is self-defence rather than a crime. Checked before the reflect
        // swap so it credits the real aggressor.
        bool targetInnocentToCaster = !target.IsFlaggedAsCriminal &&
            caster.Memory_FindObjTypes(target.Uid, MemoryType.SawCrime | MemoryType.HarmedBy) == null;
        if (harmful && caster != target && caster.IsPlayer && target.IsPlayer &&
            Character.AttackingIsACrimeEnabled && targetInnocentToCaster)
            caster.MakeCriminal();

        if (harmful && caster != target && target.IsStatFlag(StatFlag.Reflection))
        {
            target.ClearStatFlag(StatFlag.Reflection);
            // Remove the now-consumed flag's expiration entry so UndoEffect
            // doesn't try to clear it again later.
            for (int i = _activeEffects.Count - 1; i >= 0; i--)
            {
                if (_activeEffects[i].Target == target && _activeEffects[i].Spell == SpellType.MagicReflect)
                { _activeEffects.RemoveAt(i); break; }
            }
            (caster, target) = (target, caster);
        }

        int effect = def.GetEffect(skillLevel);

        // Randomize potency (Source-X: iSkillLevel/2 + rand(iSkillLevel/2))
        int potency = skillLevel / 2 + _rand.Next(Math.Max(1, skillLevel / 2));
        effect = def.GetEffect(potency);

        // Mind Blast: damage = (casterINT - targetINT)/2, capped at the victim's
        // STR/2; if the caster is less intelligent the spell rebounds onto them.
        if (def.Id == SpellType.MindBlast)
        {
            int diff = (caster.Int - target.Int) / 2;
            if (diff < 0) { target = caster; diff = -diff; }
            effect = Math.Min(diff, Math.Max(1, target.Str / 2));
        }
        // EvalInt scales offensive spell potency (Source-X spell-damage formula).
        else if (def.IsFlag(SpellFlag.Damage))
        {
            effect += effect * caster.GetSkill(SkillType.EvalInt) / 1000;
        }

        // Magic resist
        if (def.IsFlag(SpellFlag.Resist) && caster != target)
        {
            int resist = CalcMagicResist(target, def, caster);
            effect -= effect * resist / 100;
        }

        // Damage spells
        if (def.IsFlag(SpellFlag.Damage))
        {
            var dmgType = GetSpellDamageType(def.Id);
            int damage = Math.Max(0, effect);
            // Apply elemental resist
            damage = CombatEngine.ApplyElementalResist(target, damage, dmgType);
            if (damage > 0)
            {
                target.Hits -= (short)Math.Min(damage, short.MaxValue);
                target.RecordAttack(caster.Uid, damage);

                // Reactive Armor reflects a quarter of the damage back at the
                // caster, the same as the melee path (previously melee-only).
                if (target.IsStatFlag(StatFlag.Reactive) && caster != target && !caster.IsDead)
                {
                    int reflect = Math.Max(1, damage / 4);
                    caster.Hits -= (short)Math.Min(reflect, short.MaxValue);
                    caster.RecordAttack(target.Uid, reflect);
                }

                TryInterruptFromDamage(target, damage);

                // Victim feedback: spell damage used to apply silently — no
                // 0x0B damage number, no health-bar update — while the melee
                // and breath paths broadcast both. Without these the hit is
                // invisible until the victim dies.
                Character.BroadcastNearby?.Invoke(target.Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketDamage(
                        target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue)), 0);
                Character.BroadcastNearby?.Invoke(target.Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                        target.Uid.Value, target.MaxHits, target.Hits), 0);

                if (target.Hits <= 0 && !target.IsDead)
                {
                    if (OnTargetKilled != null)
                        OnTargetKilled.Invoke(target, caster);
                    else if (Character.OnLifecycleKill != null)
                        Character.OnLifecycleKill(target, caster);
                    else
                        target.Kill();
                }
            }
        }
        // Heal spells
        else if (def.IsFlag(SpellFlag.Heal))
        {
            caster.FlagForHelpingCriminalIfNeeded(target);
            target.Hits = (short)Math.Min(target.Hits + effect, target.MaxHits);
        }
        // Buff/debuff
        else if (def.IsFlag(SpellFlag.Bless))
        {
            ApplyBuff(caster, target, def, effect);
        }
        else if (def.IsFlag(SpellFlag.Curse))
        {
            ApplyCurse(caster, target, def, effect);
        }
        // Specific spells
        else
        {
            ApplySpecificSpell(caster, target, def, effect);
        }
    }

    /// <summary>Apply area effect. Maps to SPELLFLAG_AREA logic.</summary>
    private void ApplyAreaEffect(Character caster, Point3D center, SpellDef def, int skillLevel)
    {
        int range = Math.Min(8, 3 + skillLevel / 300);
        bool harmful = def.IsFlag(SpellFlag.Damage) || def.IsFlag(SpellFlag.Curse);
        foreach (var target in _world.GetCharsInRange(center, range))
        {
            if (target == caster && (harmful || def.IsFlag(SpellFlag.TargNoSelf))) continue;
            if (target.IsDead && !def.IsFlag(SpellFlag.TargDead)) continue;

            ApplyCharEffect(caster, target, def, skillLevel);
        }
    }

    /// <summary>Create a multi-tile field wall centred on the target location,
    /// oriented perpendicular to the cast direction (UO-style 5-tile wall).</summary>
    private void CreateField(Character caster, Point3D pos, SpellDef def)
    {
        int dmg = def.GetEffect(caster.GetSkill(def.GetPrimarySkill()));

        // Orient the wall perpendicular to the caster→target axis.
        int dx = pos.X - caster.X;
        int dy = pos.Y - caster.Y;
        bool wallRunsNorthSouth = Math.Abs(dx) >= Math.Abs(dy); // facing E/W → N-S wall

        for (int offset = -2; offset <= 2; offset++)
        {
            int tx = wallRunsNorthSouth ? pos.X : pos.X + offset;
            int ty = wallRunsNorthSouth ? pos.Y + offset : pos.Y;
            var tilePos = new Point3D((short)tx, (short)ty, pos.Z, pos.Map);

            // Don't lay a field segment on a blocked tile.
            var md = _world.MapData;
            if (md != null && !md.IsPassable(tilePos.Map, tilePos.X, tilePos.Y, tilePos.Z))
                continue;

            var fieldItem = _world.CreateItem();
            fieldItem.BaseId = def.EffectId;
            fieldItem.Name = def.Name + " field";
            fieldItem.SetTag("FIELD_CASTER", caster.Uid.Value.ToString());
            fieldItem.SetTag("FIELD_CASTER_UUID", caster.Uuid.ToString("D"));
            fieldItem.SetTag("FIELD_DAMAGE", dmg.ToString());
            fieldItem.DecayTime = Environment.TickCount64 + 30_000; // 30s duration
            _world.PlaceItem(fieldItem, tilePos);
        }
    }

    /// <summary>Summon a creature at target location.</summary>
    private void SummonCreature(Character caster, Point3D pos, SpellDef def, int skillLevel)
    {
        if (IsMagicFlag(MagicConfigFlags.LimitSummons))
        {
            int activeSummons = 0;
            foreach (var ch in _world.GetCharsInRange(caster.Position, 24))
            {
                if (ch.IsSummoned && ch.OwnerSerial == caster.Uid)
                    activeSummons++;
            }
            if (activeSummons >= 2)
            {
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
                return;
            }
        }

        var creature = _world.CreateCharacter();
        creature.Name = def.Name;
        creature.NpcBrain = NpcBrainType.Monster;

        int duration = def.GetDuration(skillLevel);
        if (!creature.TryAssignOwnership(caster, caster, summoned: true, enforceFollowerCap: true))
        {
            _world.DeleteObject(creature);
            creature.Delete();
            return;
        }
        creature.SetTag("SUMMON_DURATION", duration.ToString());
        creature.SetTag("SUMMON_MASTER", caster.Uid.Value.ToString());
        creature.SetTag("SUMMON_MASTER_UUID", caster.Uuid.ToString("D"));
        creature.SetTag("SUMMON_EXPIRE_TICK", (Environment.TickCount64 + duration * 100L).ToString());

        _world.PlaceCharacter(creature, pos);
    }

    /// <summary>
    /// Calculate magic resistance. Returns resist percentage (0-50).
    /// Graduated scaling: higher MR vs spell difficulty = more reduction.
    /// Also triggers MR skill gain via UseQuick.
    /// </summary>
    private int CalcMagicResist(Character target, SpellDef def, Character caster)
    {
        // Reference resist roll (Spell_CastDone): a quick MagicResistance
        // check against a chance derived from resist skill vs caster magery
        // and the spell id; success absorbs a flat 25% of the effect,
        // failure absorbs nothing.
        int chance = CalcResistChance(
            target.GetSkill(SkillType.MagicResistance),
            caster.GetSkill(SkillType.Magery),
            (int)def.Id);
        return SkillEngine.UseQuick(target, SkillType.MagicResistance, chance) ? 25 : 0;
    }

    /// <summary>Reference resist-chance formula:
    /// max(resist/50, resist - ((magery-200)/50 + (1 + spell/8) * 50)) / 30.</summary>
    internal static int CalcResistChance(int resistSkill, int casterMagery, int spellId)
    {
        int first = resistSkill / 50;
        int second = resistSkill - (((casterMagery - 200) / 50) + (1 + spellId / 8) * 50);
        return Math.Max(first, second) / 30;
    }

    /// <summary>Get damage type for spell.</summary>
    private static DamageType GetSpellDamageType(SpellType spell) => spell switch
    {
        SpellType.Fireball or SpellType.Flamestrike or SpellType.FireField or
        SpellType.MeteorSwarm or SpellType.Explosion => DamageType.Fire,

        SpellType.Lightning or SpellType.ChainLightning or SpellType.EnergyBolt or
        SpellType.EnergyVortex or SpellType.EnergyField => DamageType.Energy,

        SpellType.Harm => DamageType.Cold,
        SpellType.Poison or SpellType.PoisonField => DamageType.Poison,

        _ => DamageType.Magic,
    };

    private void ApplyBuff(Character caster, Character target, SpellDef def, int effect)
    {
        short bonus = (short)Math.Max(1, effect / 5);
        switch (def.Id)
        {
            case SpellType.Strength:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.StrDelta = bonus; target.Str += bonus;
                break;
            }
            case SpellType.Agility:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.DexDelta = bonus; target.Dex += bonus;
                break;
            }
            case SpellType.Cunning:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.IntDelta = bonus; target.Int += bonus;
                break;
            }
            // Arch Protection is the group-wide blessing; it is flagged Bless and
            // routes here, so it applies the same all-stat ward per target.
            case SpellType.Bless:
            case SpellType.ArchProtection:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.StrDelta = bonus; eff.DexDelta = bonus; eff.IntDelta = bonus;
                target.Str += bonus; target.Dex += bonus; target.Int += bonus;
                break;
            }
        }
    }

    private void ApplyCurse(Character caster, Character target, SpellDef def, int effect)
    {
        short penalty = (short)Math.Max(1, effect / 5);
        switch (def.Id)
        {
            case SpellType.Weaken:
            {
                short actual = (short)Math.Min(penalty, target.Str - 1);
                if (actual <= 0) break;
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.StrDelta = (short)-actual; target.Str -= actual;
                break;
            }
            case SpellType.Clumsy:
            {
                short actual = (short)Math.Min(penalty, target.Dex - 1);
                if (actual <= 0) break;
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.DexDelta = (short)-actual; target.Dex -= actual;
                break;
            }
            case SpellType.Feeblemind:
            {
                short actual = (short)Math.Min(penalty, target.Int - 1);
                if (actual <= 0) break;
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.IntDelta = (short)-actual; target.Int -= actual;
                break;
            }
            // Mass Curse is the area variant; it is flagged Curse and routes here,
            // so it applies the same all-stat penalty per target.
            case SpellType.Curse:
            case SpellType.MassCurse:
            {
                short strP = (short)Math.Min(penalty, target.Str - 1);
                short dexP = (short)Math.Min(penalty, target.Dex - 1);
                short intP = (short)Math.Min(penalty, target.Int - 1);
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                if (strP > 0) { eff.StrDelta = (short)-strP; target.Str -= strP; }
                if (dexP > 0) { eff.DexDelta = (short)-dexP; target.Dex -= dexP; }
                if (intP > 0) { eff.IntDelta = (short)-intP; target.Int -= intP; }
                break;
            }
        }
    }

    private bool IsItemAccessible(Character ch, Item item)
    {
        if (ch.PrivLevel >= PrivLevel.GM) return true;
        if (!item.ContainedIn.IsValid)
            return ch.Position.GetDistanceTo(item.Position) <= 2;
        var current = item;
        for (int depth = 0; depth < 16 && current.ContainedIn.IsValid; depth++)
        {
            if (current.ContainedIn == ch.Uid) return true;
            var parent = _world?.FindItem(current.ContainedIn);
            if (parent == null) break;
            current = parent;
        }
        return false;
    }

    private bool CanSpellReachItem(Character caster, Item item)
    {
        if (item.IsDeleted)
            return false;
        if (IsItemAccessible(caster, item))
            return true;
        if (!item.ContainedIn.IsValid &&
            item.Position.Map == caster.MapIndex &&
            caster.Position.GetDistanceTo(item.Position) <= 12)
            return true;
        return false;
    }

    private void ApplyRuneTravelSpell(Character caster, Item rune, SpellDef def)
    {
        if (!rune.TryGetRuneMark(out Point3D dest))
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
            return;
        }

        // A frozen/jailed/paralyzed or dead caster cannot travel out — otherwise
        // a jailed player could recall straight out of jail.
        if (caster.PrivLevel < Core.Enums.PrivLevel.GM &&
            (caster.IsDead || caster.IsStatFlag(StatFlag.Freeze)))
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
            return;
        }

        if (caster.PrivLevel < Core.Enums.PrivLevel.GM)
        {
            var srcRegion = _world.FindRegion(caster.Position);
            var destRegion = _world.FindRegion(dest);
            if (srcRegion != null && srcRegion.NoMagic)
            {
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
                return;
            }
            if (destRegion != null && destRegion.NoMagic)
            {
                OnSysMessage?.Invoke(caster, "That location blocks magic.");
                return;
            }
            if (def.Id == SpellType.Recall)
            {
                if (srcRegion != null && srcRegion.TryGetProperty("RECALLOUT", out string? rOut) && rOut == "0")
                { OnSysMessage?.Invoke(caster, "You cannot recall from here."); return; }
                if (destRegion != null && destRegion.TryGetProperty("RECALLIN", out string? rIn) && rIn == "0")
                { OnSysMessage?.Invoke(caster, "You cannot recall to that location."); return; }
            }
            if (def.Id == SpellType.GateTravel)
            {
                if (srcRegion != null && srcRegion.TryGetProperty("GATEOUT", out string? gOut) && gOut == "0")
                { OnSysMessage?.Invoke(caster, "You cannot open a gate here."); return; }
                if (destRegion != null && destRegion.TryGetProperty("GATEIN", out string? gIn) && gIn == "0")
                { OnSysMessage?.Invoke(caster, "You cannot gate to that location."); return; }
            }
        }

        var md = _world.MapData;
        if (md != null)
        {
            var (mapW, mapH) = md.GetMapSize(dest.Map);
            if (dest.X < 0 || dest.Y < 0 || dest.X >= mapW || dest.Y >= mapH)
            {
                OnSysMessage?.Invoke(caster, "That location is unreachable.");
                return;
            }
            // Don't teleport into a wall/water/impassable static — would leave
            // the traveller stuck. Applies to both Recall and Gate destinations.
            if (caster.PrivLevel < Core.Enums.PrivLevel.GM &&
                !md.IsPassable(dest.Map, dest.X, dest.Y, dest.Z))
            {
                OnSysMessage?.Invoke(caster, "That location is blocked.");
                return;
            }
        }

        if (def.Id == SpellType.Recall)
        {
            byte oldMap = caster.MapIndex;
            _world.MoveCharacter(caster, dest);
            OnSpellTeleport?.Invoke(caster, dest, oldMap);
            return;
        }

        var gate = _world.CreateItem();
        gate.BaseId = 0x0F6C; // moongate graphic
        gate.ItemType = ItemType.Moongate;
        gate.Name = "moongate";
        gate.MoreP = dest;
        gate.DecayTime = Environment.TickCount64 + 30_000;
        _world.PlaceItem(gate, caster.Position);

        if (IsMagicFlag(MagicConfigFlags.GateBothSides))
        {
            var returnGate = _world.CreateItem();
            returnGate.BaseId = 0x0F6C;
            returnGate.ItemType = ItemType.Moongate;
            returnGate.Name = "moongate";
            returnGate.MoreP = caster.Position;
            returnGate.DecayTime = Environment.TickCount64 + 30_000;
            _world.PlaceItem(returnGate, dest);
        }

        OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGateOpen));
    }

    private void ApplySpecificSpell(Character caster, Character target, SpellDef def, int effect)
    {
        switch (def.Id)
        {
            case SpellType.Teleport:
            {
                var dest = caster.CastTargetPos;
                if (dest.X == 0 && dest.Y == 0) break;
                // Validate the destination like Recall/Gate do: off-map or an
                // impassable tile (wall/water/blocking static) would strand the
                // caster. Recall/Gate guarded this; Teleport did not.
                var teleMd = _world.MapData;
                if (teleMd != null)
                {
                    var (mapW, mapH) = teleMd.GetMapSize(dest.Map);
                    if (dest.X < 0 || dest.Y < 0 || dest.X >= mapW || dest.Y >= mapH ||
                        (caster.PrivLevel < Core.Enums.PrivLevel.GM &&
                         !teleMd.IsPassable(dest.Map, dest.X, dest.Y, dest.Z)))
                    {
                        OnSysMessage?.Invoke(caster, "That location is unreachable.");
                        break;
                    }
                }
                byte oldMap = caster.MapIndex;
                _world.MoveCharacter(caster, dest);
                OnSpellTeleport?.Invoke(caster, dest, oldMap);
                break;
            }
            case SpellType.Recall:
                // Recall/Gate use item rune state (MOREP/TryGetRuneMark).
                // A character target cannot be a valid rune.
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
                break;
            case SpellType.Mark:
                // Handled in CastDone before ApplyCharEffect
                break;
            case SpellType.GateTravel:
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
                break;
            case SpellType.Cure:
            case SpellType.ArchCure:
                caster.FlagForHelpingCriminalIfNeeded(target);
                target.CurePoison();
                break;
            case SpellType.Paralyze:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Freeze;
                target.SetStatFlag(StatFlag.Freeze);
                break;
            }
            case SpellType.Invisibility:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Invisible;
                target.SetStatFlag(StatFlag.Invisible);
                break;
            }
            case SpellType.Reveal:
                target.ClearHiddenState();
                break;
            case SpellType.Dispel:
            case SpellType.MassDispel:
                if (target.IsStatFlag(StatFlag.Conjured) && !target.IsDead)
                {
                    if (IsMagicFlag(MagicConfigFlags.DispelKillSummons))
                    {
                        if (OnTargetKilled != null)
                            OnTargetKilled.Invoke(target, caster);
                        else if (Character.OnLifecycleKill != null)
                            Character.OnLifecycleKill(target, caster);
                        else
                            target.Kill();
                    }
                    else
                    {
                        _world.DeleteObject(target);
                        target.Delete();
                    }
                }
                break;
            case SpellType.Resurrection:
                if (target.IsDead)
                {
                    caster.FlagForHelpingCriminalIfNeeded(target);
                    if (Character.OnLifecycleResurrect != null)
                        Character.OnLifecycleResurrect(target);
                    else
                        target.Resurrect();
                }
                break;
            case SpellType.Poison:
            {
                if (target.IsDead) break;
                byte poisonLvl = caster.GetSkill(SkillType.Magery) switch
                {
                    >= 800 => 4, // deadly
                    >= 600 => 3, // greater
                    >= 400 => 2, // normal
                    _ => 1       // lesser
                };
                target.ApplyPoison(poisonLvl, caster.Uid);
                string poisonKey = poisonLvl switch
                {
                    1 => Msg.SpellPoison1,
                    2 => Msg.SpellPoison2,
                    3 => Msg.SpellPoison3,
                    4 => Msg.SpellPoison4,
                    _ => Msg.SpellPoison5
                };
                OnSysMessage?.Invoke(target, ServerMessages.Get(poisonKey));
                break;
            }
            case SpellType.NightSight:
            {
                // 0x4E PacketPersonalLight adds brightness on top of global
                // lighting; higher value = brighter. 30 overrides typical
                // night global (~12). The DURATION script value feeds the
                // expiration timer below; when the timer fires, LightLevel
                // reverts to its pre-cast value and the stat flag is cleared.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.NightSight;
                eff.OldLightLevel = target.LightLevel;
                eff.LightChanged = true;
                target.SetStatFlag(StatFlag.NightSight);
                target.LightLevel = 30;
                OnPersonalLightChanged?.Invoke(target);
                break;
            }
            case SpellType.ReactiveArmor:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Reactive;
                target.SetStatFlag(StatFlag.Reactive);
                break;
            }
            case SpellType.Protection:
            {
                // ArcherCanMove bit is reused as a Protection marker — no
                // dedicated StatFlag.Protection exists yet. CombatEngine
                // checks IsStatFlag(ArcherCanMove) to reduce cast-interrupt
                // chance; documented here so the flag reuse is explicit.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.ArcherCanMove;
                target.SetStatFlag(StatFlag.ArcherCanMove);
                break;
            }
            case SpellType.Incognito:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Incognito;
                // Disguise: hide the real name behind a random alias (restored
                // on expiry). Source-X also randomizes skin/hair; name is the
                // visible part other players key off.
                eff.OldName = target.Name;
                eff.NameChanged = true;
                target.Name = s_incognitoNames[_rand.Next(s_incognitoNames.Length)];
                target.SetStatFlag(StatFlag.Incognito);
                Character.OnAppearanceChanged?.Invoke(target);
                break;
            }
            case SpellType.MagicReflect:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Reflection;
                target.SetStatFlag(StatFlag.Reflection);
                break;
            }
            case SpellType.Polymorph:
            {
                ReadOnlySpan<ushort> forms = [0x0033, 0x0034, 0x0035, 0x0036, 0x0037, 0x0038];
                ushort newBody = forms[_rand.Next(forms.Length)];
                if (target.OBody == 0)
                    target.OBody = target.BodyId;
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.OldBodyId = target.OBody;
                eff.BodyChanged = true;
                target.BodyId = newBody;
                target.SetStatFlag(StatFlag.Polymorph);
                break;
            }
            case SpellType.ManaDrain:
                int drain = Math.Min(target.Mana, (short)effect);
                target.Mana -= (short)drain;
                caster.Mana = (short)Math.Min(caster.Mana + drain, caster.MaxMana);
                break;
            case SpellType.ManaVampire:
                int vamp = Math.Min(target.Mana, (short)effect);
                target.Mana -= (short)vamp;
                caster.Mana = (short)Math.Min(caster.Mana + vamp, caster.MaxMana);
                break;

            case SpellType.CreateFood:
            {
                // Materialize a food item into the caster's pack (was a no-op).
                var food = _world.CreateItem();
                food.BaseId = def.EffectId != 0 ? def.EffectId : (ushort)0x09D0; // apple default
                food.ItemType = ItemType.Food;
                food.Name = "food";
                if (target.Backpack != null)
                    target.Backpack.AddItem(food);
                else
                    _world.PlaceItem(food, target.Position);
                break;
            }
        }
    }

    private static void ClearCastState(Character ch) => ch.ClearCastState();

    /// <summary>Register a spell's expiration with its undo data.
    /// Duration comes from <see cref="SpellDef.GetDuration"/>(caster's
    /// primary skill) — tenths of a second per the CAST_TIME /
    /// DURATION convention. ServUO semantics: duration scales with the
    /// CASTER's skill, not the target's. If the script leaves DURATION
    /// at 0 a 30-second floor kicks in so buffs don't expire instantly
    /// on scripts that forgot the field. Re-casting on the same target
    /// refreshes the timer and merges the delta rather than stacking.</summary>
    private ActiveSpellEffect ScheduleEffectExpiry(Character caster, Character target, SpellType spell, SpellDef def)
    {
        int casterSkill = caster.GetSkill(def.GetPrimarySkill());
        int durationTenths = def.GetDuration(casterSkill);
        if (durationTenths <= 0) durationTenths = 300; // 30s floor
        long expireTick = Environment.TickCount64 + (long)durationTenths * 100L;

        // Refresh on re-cast — revert the previous delta first so the new
        // cast stacks cleanly onto the base value, not on top of the old buff.
        for (int i = 0; i < _activeEffects.Count; i++)
        {
            var existing = _activeEffects[i];
            if (existing.Target == target && existing.Spell == spell)
            {
                RevertDeltas(existing);
                _activeEffects.RemoveAt(i);
                // Source-X re-equips the spell memory on refresh: the old
                // effect's removal is observable before the new add.
                Character.OnSpellEffectRemove?.Invoke(target, (int)spell);
                break;
            }
        }

        var eff = new ActiveSpellEffect { Target = target, Spell = spell, ExpireTick = expireTick };
        _activeEffects.Add(eff);
        // @EffectAdd (Source-X) — a temporary effect was applied to the target.
        Character.OnEffectAdd?.Invoke(target, (int)spell);
        // @SpellEffectAdd (Source-X CCharSpell) — SRC = caster, ARGN1 = spell.
        Character.OnSpellEffectAdd?.Invoke(target, caster, (int)spell);
        return eff;
    }

    /// <summary>Walk the active-effect list once per world tick and undo
    /// any whose expire tick has passed. Called from Program.cs main
    /// loop. Cheap when the list is empty; no-op otherwise.</summary>
    public void ProcessExpirations(long now)
    {
        for (int i = _activeEffects.Count - 1; i >= 0; i--)
        {
            var eff = _activeEffects[i];
            if (eff.Target.IsDeleted)
            {
                _activeEffects.RemoveAt(i);
                continue;
            }
            if (now < eff.ExpireTick) continue;
            _activeEffects.RemoveAt(i);
            RevertDeltas(eff);
            Character.OnSpellEffectRemove?.Invoke(eff.Target, (int)eff.Spell);
        }
    }

    /// <summary>Revert exactly what <see cref="ScheduleEffectExpiry"/>
    /// recorded for this effect — stat deltas subtracted, flag cleared,
    /// light level restored + 0x4E refresh dispatched. Safe to call
    /// even when the effect didn't touch a given field (the delta will
    /// be 0 / flag None / LightChanged false).</summary>
    private void RevertDeltas(ActiveSpellEffect eff)
    {
        var t = eff.Target;
        if (eff.StrDelta != 0) t.Str -= eff.StrDelta;
        if (eff.DexDelta != 0) t.Dex -= eff.DexDelta;
        if (eff.IntDelta != 0) t.Int -= eff.IntDelta;
        if (eff.AppliedFlag != StatFlag.None) t.ClearStatFlag(eff.AppliedFlag);
        if (eff.NameChanged && eff.OldName != null)
        {
            t.Name = eff.OldName;
            Character.OnAppearanceChanged?.Invoke(t);
        }
        if (eff.BodyChanged) t.BodyId = eff.OldBodyId;
        if (eff.BodyChanged && eff.Spell == SpellType.Polymorph)
        {
            t.ClearStatFlag(StatFlag.Polymorph);
            if (t.OBody != 0 && t.BodyId == t.OBody)
                t.OBody = 0;
        }
        if (eff.LightChanged)
        {
            t.LightLevel = eff.OldLightLevel;
            OnPersonalLightChanged?.Invoke(t);
        }
    }

    /// <summary>Revert polymorph body on death when MAGICF bit 0x0008 is set.</summary>
    public void RevertPolymorphOnDeath(Character ch)
    {
        if (!IsMagicFlag(MagicConfigFlags.PolymorphRevertDeath))
            return;

        for (int i = _activeEffects.Count - 1; i >= 0; i--)
        {
            var eff = _activeEffects[i];
            if (eff.Target != ch || !eff.BodyChanged || eff.Spell != SpellType.Polymorph)
                continue;
            RevertDeltas(eff);
            _activeEffects.RemoveAt(i);
            Character.OnSpellEffectRemove?.Invoke(ch, (int)eff.Spell);
            return;
        }
    }

    public void ClearAllEffectsOnDeath(Character ch)
    {
        RevertPolymorphOnDeath(ch);
        for (int i = _activeEffects.Count - 1; i >= 0; i--)
        {
            if (_activeEffects[i].Target != ch) continue;
            var eff = _activeEffects[i];
            RevertDeltas(eff);
            _activeEffects.RemoveAt(i);
            Character.OnSpellEffectRemove?.Invoke(ch, (int)eff.Spell);
        }
    }

    /// <summary>Original body for resurrect after polymorph (OBody or active effect).</summary>
    public ushort GetResurrectBody(Character ch)
    {
        foreach (var eff in _activeEffects)
        {
            if (eff.Target == ch && eff.BodyChanged && eff.OldBodyId != 0)
                return eff.OldBodyId;
        }
        return ch.OBody != 0 ? ch.OBody : ch.BodyId;
    }

    /// <summary>Revert all active buff deltas so character stats are saved
    /// clean (base values only). Call before WorldSaver runs.</summary>
    public void RevertAllForSave()
    {
        foreach (var eff in _activeEffects)
            RevertDeltas(eff);
    }

    /// <summary>Re-apply all active buff deltas after a save completes.
    /// Paired with <see cref="RevertAllForSave"/>.</summary>
    public void ReapplyAllAfterSave()
    {
        foreach (var eff in _activeEffects)
        {
            var t = eff.Target;
            if (eff.StrDelta != 0) t.Str += eff.StrDelta;
            if (eff.DexDelta != 0) t.Dex += eff.DexDelta;
            if (eff.IntDelta != 0) t.Int += eff.IntDelta;
            if (eff.AppliedFlag != StatFlag.None) t.SetStatFlag(eff.AppliedFlag);
            if (eff.LightChanged)
            {
                t.LightLevel = 30;
                OnPersonalLightChanged?.Invoke(t);
            }
        }
    }
}

/// <summary>
/// Registry of all spell definitions. Populated from scripts.
/// </summary>
public sealed class SpellRegistry
{
    private readonly Dictionary<SpellType, SpellDef> _spells = [];

    public void Clear() => _spells.Clear();
    public void Register(SpellDef def) => _spells[def.Id] = def;
    public SpellDef? Get(SpellType id) => _spells.GetValueOrDefault(id);
    public IEnumerable<SpellDef> GetAll() => _spells.Values;
    public int Count => _spells.Count;
}
