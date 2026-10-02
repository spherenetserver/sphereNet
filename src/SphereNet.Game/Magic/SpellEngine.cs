using System.Globalization;
using System.Text;
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
public sealed partial class SpellEngine
{
    private readonly GameWorld _world;
    private readonly SpellRegistry _spells;
    private readonly Random _rand = new();

    public SpellEngine(GameWorld world, SpellRegistry spells)
    {
        _world = world;
        _spells = spells;
        _world.ObjectDeleting += OnWorldObjectDeleting;
        _world.WeaponWornChanged += OnWeaponWornChanged;
        _world.SpellMemoryTakingOff += OnSpellMemoryTakingOff;
    }

    /// <summary>A worn spell memory leaving its wearer without being deleted - dropped,
    /// put in a container, given to another character. Upstream that is the same
    /// CChar::OnRemoveObj -> Spell_Effect_Remove as a deletion (CCharAct.cpp:560): the
    /// hooks and the undo run once, and the item lives on wherever it goes.</summary>
    private void OnSpellMemoryTakingOff(Character wearer, Item mem)
    {
        if (_effects.Contains(mem) || (mem.IsSpellMemory && !mem.IsDeleted && TryRegister(mem)))
            RemoveEffect(mem);
    }

    /// <summary>Deleting a worn IT_SPELL memory - by any road: its timer, a dispel, a
    /// script's REMOVE, a GM remove, the wearer's own deletion - is what ends its
    /// effect. Upstream unequips the item on its way out and CChar::OnRemoveObj
    /// runs Spell_Effect_Remove (CCharAct.cpp:560); this is that call.</summary>
    private void OnWorldObjectDeleting(Objects.ObjBase obj)
    {
        if (obj is Item { ItemType: ItemType.Spell } mem &&
            (_effects.Contains(mem) || (mem.IsSpellMemory && !mem.IsDeleted && TryRegister(mem))))
            RemoveEffect(mem);
    }

    /// <summary>Callback to play a sound at a location.</summary>
    public Action<Point3D, ushort>? OnPlaySound { get; set; }

    /// <summary>Source-X CClientMsg::SysMessage hook for the active caster.
    /// Program.cs wires this to the owning GameClient so spell-specific
    /// failure/success messages (recall blank rune, gate already there,
    /// poison resisted, etc.) reach only the caster, matching upstream.</summary>
    public Action<Character, string>? OnSysMessage { get; set; }

    /// <summary>Notify viewers after a cast bounces a held item to its final
    /// location. Runs for each hand even if freeing the other hand later fails.</summary>
    public Action<Character, Item>? OnCastItemUnequipped { get; set; }

    /// <summary>Free one hand for a cast — the port of Source-X Spell_Unequip
    /// (CCharSpell.cpp:2827). An item that may stay on (a spellbook, a wand, or one
    /// flagged CAN_I_EQUIPONCAST) is left alone; anything else is bounced into the
    /// pack. False means the cast cannot start: frozen hands under
    /// MAGICF_NOCASTFROZENHANDS or MAGICF_CASTPARALYZED, an item that cannot be
    /// moved at all, or a bounce with nowhere to go.</summary>
    private bool TrySpellUnequip(Character caster, Layer layer)
    {
        var held = caster.GetEquippedItem(layer);
        if (held == null)
            return true;

        var magicFlags = (MagicConfigFlags)Character.MagicFlags;
        bool frozen = caster.IsStatFlag(StatFlag.Freeze);

        if (magicFlags.HasFlag(MagicConfigFlags.NoCastFrozenHands) && frozen)
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellTryFrozenhands));
            return false;
        }

        // Kept in hand: the book you cast from, a wand, or an item the pack
        // flagged CAN_I_EQUIPONCAST. SPELLCHANNELING does not bypass frozen
        // hands under CASTPARALYZED (Source-X Spell_Unequip).
        bool staysEquipped =
            held.ItemType is ItemType.Spellbook or ItemType.SpellbookNecro or
                ItemType.SpellbookPala or ItemType.SpellbookExtra or
                ItemType.SpellbookBushido or ItemType.SpellbookNinjitsu or
                ItemType.SpellbookArcanist or ItemType.SpellbookMystic or
                ItemType.SpellbookMastery or ItemType.Wand ||
            HasEquipOnCast(held);

        if (magicFlags.HasFlag(MagicConfigFlags.CastParalyzed))
        {
            if (staysEquipped)
                return true;
            if (frozen)
            {
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellTryFrozenhands));
                return false;
            }
        }

        if (!ItemMoveRules.CanMove(caster, held, out _))
            return false;
        if (staysEquipped || HasSpellChanneling(held))
            return true;

        var pack = caster.Backpack;
        if (pack == null || pack.IsDeleted)
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellTryBusyhands));
            return false;
        }

        caster.Unequip(layer);
        if (!pack.TryAddItem(held))
        {
            // Nowhere to put it: upstream drops it at the feet rather than
            // destroying it, and only then gives up on the cast.
            held.ContainedIn = Serial.Invalid;
            _world.PlaceItemWithDecay(held, caster.Position);
        }
        OnCastItemUnequipped?.Invoke(caster, held);
        return true;
    }

    /// <summary>CAN_I_EQUIPONCAST on the item's definition (Source-X CBase.h:61)
    /// - the pack's way of saying a held item survives a cast with EQUIPPEDCAST
    /// off.</summary>
    private static bool HasEquipOnCast(Item item)
    {
        return item.TryGetProperty("CAN", out string can) &&
            Core.Types.ScriptNumber.TryParseToken(can, out long flags) &&
            ((ulong)flags & (ulong)CanFlags.I_EquipOnCast) != 0;
    }

    private static bool HasSpellChanneling(Item item)
    {
        // Use the existing AOS property storage, with instance override before
        // the full script definition (the graphic alone may name another def).
        string? raw = item.TryGetTag("SPELLCHANNELING", out string? own)
            ? own
            : Definitions.DefinitionLoader.GetItemDef(
                Definitions.ItemDefHelper.ResolveInstanceDefIndex(item))?.TagDefs.Get("SPELLCHANNELING");
        return raw != null && Core.Types.ScriptNumber.TryParseToken(raw, out long value) && value != 0;
    }

    /// <summary>Callback fired when a spell is interrupted. Args: (Character caster, string reason).</summary>
    public Action<Character, string>? OnSpellInterrupt { get; set; }

    /// <summary>Fired when a cast RESOLVES at completion — for ANY caster (player,
    /// NPC, direct) — so the @SpellSuccess / @SpellEffect / @SpellFail triggers run
    /// from one engine point instead of only the client path. Args: caster, spell,
    /// success. Wired to fire the char triggers and the per-spell [SPELL] ON= block.
    /// (Cast-START failures and the targeting cursor's @SpellSelect/@SpellCast/
    /// @SpellTargetCancel stay on the client UI flow — NPCs gate casts via @NPCActCast.)</summary>
    public Action<Character, SpellType, bool>? OnCastResolved { get; set; }

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

    /// <summary>Styled power-words callback (@SpellCast LOCAL.WOPColor /
    /// LOCAL.WOPFont overrides): caster, words, hue (0 = caster default),
    /// font (0 = default). Preferred over <see cref="OnSpellWords"/> when set.</summary>
    public Action<Character, string, ushort, byte>? OnSpellWordsEx { get; set; }

    /// <summary>sphere.ini WOPPLAYER / WOPSTAFF (Source-X m_fWordsOfPowerPlayer,
    /// default 1 / m_fWordsOfPowerStaff, default 0): whether a player / a Counsel+
    /// caster speaks the power words.</summary>
    public static bool WopPlayer { get; set; } = true;
    public static bool WopStaff { get; set; }
    /// <summary>sphere.ini WOPCOLOR (default HUE_TEXT_DEF 0x3B2; above 0 it wins over
    /// the caster's speech hue), WOPFONT (default FONT_NORMAL 3) and WOPTALKMODE
    /// (default TALKMODE_SPELL 10; out-of-range falls back to it), CCharSpell.cpp:3511-3526.</summary>
    public static int WopColor { get; set; } = 0x03B2;
    public static int WopFont { get; set; } = 3;
    public static int WopTalkMode { get; set; } = 10;

    /// <summary>The talk mode the power words go out in (CCharSpell.cpp:3513-3516):
    /// WOPTALKMODE when it names a real mode (SAY..COMMAND exclusive), else SPELL.</summary>
    public static byte EffectiveWopTalkMode =>
        WopTalkMode is > 0 and < 0x0F ? (byte)WopTalkMode : (byte)10;

    /// <summary>The hue the power words go out in when the script did not set
    /// LOCAL.WOPColor: WOPCOLOR above 0, else the caster's own speech hue, else
    /// HUE_TEXT_DEF (CCharSpell.cpp:3518-3525).</summary>
    public static ushort DefaultWopHue(Character caster) =>
        WopColor > 0 ? (ushort)WopColor
        : caster.SpeechColor != 0 ? caster.SpeechColor
        : (ushort)0x03B2;

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

    /// <summary>Play a sound only for this character's client (Source-X
    /// m_pClient-&gt;addSound) — hallucination trip sounds must not broadcast
    /// to bystanders.</summary>
    public Action<Character, ushort>? OnPlaySoundTo { get; set; }

    /// <summary>Re-send the nearby world view to this character's client
    /// (Source-X addChar/addPlayerSee on the hallucination tick — each
    /// refresh re-rolls the random hues).</summary>
    public Action<Character>? OnViewRefresh { get; set; }

    /// <summary>Overhead text spoken by the character, visible to everyone
    /// nearby (Source-X CChar::Speak — the drunk *hic* line).</summary>
    public Action<Character, string>? OnOverheadEmote { get; set; }

    /// <summary>Optional script dispatcher for item spell hooks.</summary>
    public TriggerDispatcher? TriggerDispatcher { get; set; }

    /// <summary>Get a spell definition by type (for flag checks, etc.).</summary>
    public SpellDef? GetSpellDef(SpellType spell) => _spells.Get(spell);

    // Source-X GetScrollSpell resolves through SPELLDEF.SCROLL_ITEM, not MORE.
    public SpellDef? GetScrollSpell(Item scroll) => _spells.GetAll()
        .Where(s => s.Id != SpellType.None && s.ScrollItemId != 0 && s.ScrollItemId == scroll.BaseId)
        .OrderBy(s => s.Id).FirstOrDefault();

    /// <summary>Source-X Spell_CastStart timing: CAST_TIME minus two tenths
    /// per effective FASTERCASTING point, floored at one tenth.</summary>
    public static int CalculateCastTimeTenths(Character caster, SpellDef def, int? skillValue = null)
    {
        if (caster.PrivLevel >= PrivLevel.GM)
            return 1;

        int skill = skillValue ?? caster.GetSkill(def.GetPrimarySkill());
        long wait = def.GetCastTime(skill) - (2L * GetCastingPropertyValue(
            caster, SpellCastingProperties.FasterCasting));
        return (int)Math.Max(1, wait);
    }

    /// <summary>Character property plus equipped item instance/ITEMDEF values.</summary>
    public static int GetCastingPropertyValue(Character caster, string property)
    {
        if (!SpellCastingProperties.Contains(property))
            return 0;
        return SumCharAndEquipProperty(caster, property);
    }

    /// <summary>A character property summed with what the equipped items carry
    /// (instance TAG, else the ITEMDEF's) - the live-scan form of Source-X's
    /// GetPropNum(COMP_PROPS_CHAR, ..., true).</summary>
    internal static int SumCharAndEquipProperty(Character caster, string property)
    {
        long total = caster.Tags.GetInt(property);
        for (int layerIndex = (int)Layer.OneHanded; layerIndex <= (int)Layer.Horse; layerIndex++)
        {
            var item = caster.GetEquippedItem((Layer)layerIndex);
            if (item == null)
                continue;

            if (item.TryGetTag(property, out string? raw) && TryParseInteger(raw, out int instanceValue))
            {
                total += instanceValue;
                continue;
            }

            var itemDef = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (itemDef != null && TryParseInteger(itemDef.TagDefs.Get(property), out int defValue))
                total += defValue;
        }
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    private static bool TryParseInteger(string? raw, out int value) =>
        ScriptNumber.TryParseInt(raw, out value);

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

    private static bool IsWieldingWand(Character caster)
    {
        var weapon = caster.GetEquippedItem(Layer.OneHanded)
                  ?? caster.GetEquippedItem(Layer.TwoHanded);
        return weapon?.ItemType == ItemType.Wand;
    }

    /// <summary>What a cast is being paid for FROM.</summary>
    private enum CastSourceKind { Self, Wand, Scroll }

    private Item? FindTaggedItem(string? raw) =>
        uint.TryParse(raw, out uint uid) ? _world?.FindItem(new Serial(uid)) : null;

    /// <summary>Source-X: "magic items must be on your person to use"
    /// (CCharSpell.cpp:2422) - the source's TOP-LEVEL owner must be the caster.
    /// Deliberately no GM bypass: the reference makes this test BEFORE its PRIV_GM
    /// shortcut (:2429), so a GM cannot cast off someone else's scroll either.</summary>
    private bool IsCastSourceOnPerson(Character caster, Item item)
    {
        var current = item;
        for (int depth = 0; depth < 16; depth++)
        {
            if (!current.ContainedIn.IsValid) return false;   // lying in the world
            if (current.ContainedIn == caster.Uid) return true;
            var parent = _world?.FindItem(current.ContainedIn);
            if (parent == null) return false;
            current = parent;
        }
        return false;
    }

    /// <summary>Re-resolve the cast source at the moment it is needed, the way
    /// Source-X re-reads m_Act_Prv_UID at completion (CCharSpell.cpp:2882) and hands
    /// that same resolved pointer to Spell_CanCast (:3010). Answering from the TAG
    /// alone meant a scroll that had been destroyed, dropped or given away during the
    /// cast time still bought its owner the half-mana, no-reagent discount, and the
    /// consumption then took the scroll out of whoever's pack it had reached.
    ///
    /// Returns false when a source was claimed but is no longer usable: Spell_CanCast
    /// rejects a null source (:2330) and one that is not on the caster (:2422).</summary>
    private bool TryResolveCastSource(Character caster, out CastSourceKind kind, out Item? source)
    {
        source = null;
        kind = CastSourceKind.Self;

        if (caster.TryGetTag("WAND_UID", out string? wandStr))
        {
            kind = CastSourceKind.Wand;
            source = FindTaggedItem(wandStr);
        }
        else if (caster.TryGetTag("SCROLL_UID", out string? scrollStr))
        {
            kind = CastSourceKind.Scroll;
            source = FindTaggedItem(scrollStr);
        }
        else
        {
            // Nothing was recorded, so the caster is spending their own power. NPCs
            // never tag - their AI hands them the wand directly - so the wielded-wand
            // reading survives for them alone. For a PLAYER a wand merely held is not
            // a cast source: reading equipment here let a player swap a wand in during
            // the cast time and finish an ordinary spell at wand prices.
            if (!caster.IsPlayer && IsWieldingWand(caster))
                kind = CastSourceKind.Wand;
            return true;
        }

        return source != null && !source.IsDeleted && IsCastSourceOnPerson(caster, source);
    }

    /// <summary>The spell a wand or scroll casts: MOREX (Source-X m_itWeapon.m_spell,
    /// CItem.h:222).</summary>
    internal static SpellType MagicItemSpell(Item item) => (SpellType)item.MoreP.X;

    /// <summary>Whether a wand can still cast: MORE2 holds its charges (m_spellcharges,
    /// CItem.h:221) and 0 means empty (CCharSpell.cpp:2436).</summary>
    internal static bool WandHasCharge(Item wand) => wand.More2 > 0;

    /// <summary>Spend one wand charge (CCharSpell.cpp:2442): 255 is unlimited, and an
    /// emptied wand keeps its spell - it is simply out of charges.</summary>
    internal static void ConsumeWandCharge(Item wand)
    {
        if (wand.More2 == 0 || wand.More2 == 255)
            return;
        wand.More2--;
    }

    /// <summary>The strength a wand or scroll casts at: its MOREY spell level, or a
    /// random 0-499 when it has none (Spell_CastDone, CCharSpell.cpp:2893-2903).</summary>
    private static int MagicItemSkillLevel(Item item) =>
        item.MoreP.Y > 0 ? item.MoreP.Y : Random.Shared.Next(500);

    /// <summary>Drop the cast-source tags WITHOUT consuming — used when a cast is
    /// interrupted / aborted so a half-started wand or scroll cast does not leak its
    /// tag into the next cast (which would then wrongly consume the charge / scroll).</summary>
    private static void ClearCastSourceTags(Character caster)
    {
        caster.RemoveTag("WAND_UID");
        caster.RemoveTag("SCROLL_UID");
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
        // REVEALFLAGS decides as well: MAGICF_NOREVEALONCAST is the magic-side veto,
        // REVEALF_SPELLCAST the stealth-side one, and upstream honours both.
        caster.ClearHiddenState(RevealFlags.SpellCast);
    }

    /// <summary>Precast mode from sphere.ini MAGICFLAGS bit 0x0002.</summary>
    public static bool IsPrecastEnabled(SpellDef def) =>
        IsMagicFlag(MagicConfigFlags.Precast) && !def.IsFlag(SpellFlag.NoPrecast);

    // No spell is refused for being cast underground: Source-X reads
    // REGION_FLAG_UNDERGROUND only for STATF_INDOORS (CCharAct.cpp:4831) and has
    // no outdoor-only spell list, so the old block here was removed.

    /// <summary>The port of Source-X Spell_CastFail (CCharSpell.cpp:3316-3413): the
    /// mana owed is Calc_SpellManaCost (LOWERMANACOST applied, wand free, scroll
    /// half) times MANALOSSPERCENT; @SpellFail and the [SPELL] @Fail stage see it
    /// as ARGN2 and may change it, and RETURN 1 in either costs nothing.</summary>
    private void ApplyCastResourceLoss(Character caster, SpellDef def, bool wand, bool scroll,
        bool fizzle, bool abort)
    {
        int manaLoss = 0, tithingLoss = 0;
        bool lossEnabled = abort ? Character.ManaLossAbort : Character.ManaLossFail;
        bool reagentLossEnabled = abort ? Character.ReagentLossAbort : Character.ReagentLossFail;
        if (fizzle || abort)
        {
            if (lossEnabled)
                manaLoss = SpellManaCost(caster, def, wand, scroll) * Character.ManaLossPercent / 100;
            // Tithing is lost on the reagent-loss switch, not the mana one (:3327-3341).
            if (reagentLossEnabled)
                tithingLoss = SpellTithingCost(caster, def, wand, scroll);
        }

        // The fail effect: ITEMID_FX_SPELL_FAIL, which the stages may replace
        // (LOCAL.CreateObject1, 0 = none) and hue (LOCAL.EffectColor /
        // LOCAL.EffectRender) - Spell_CastFail, CCharSpell.cpp:3343-3368.
        const ushort SpellFailEffect = 0x3735; // ITEMID_FX_SPELL_FAIL
        ushort failEffect = SpellFailEffect;
        uint effectColor = 0, effectRender = 0;
        if (TriggerDispatcher != null)
        {
            var failArgs = new TriggerArgs
            {
                CharSrc = caster,
                N1 = (int)def.Id,
                N2 = manaLoss,
                Locals = new SphereNet.Scripting.Variables.VarMap(),
            };
            failArgs.Locals.SetInt("CreateObject1", SpellFailEffect);
            failArgs.Locals.SetInt("TithingLoss", tithingLoss);
            if (TriggerDispatcher.FireCharTrigger(caster, CharTrigger.SpellFail, failArgs) == TriggerResult.True)
                return;
            if (TriggerDispatcher.FireSpellTrigger(def.Id, "Fail", caster, failArgs) == TriggerResult.True)
                return;
            manaLoss = (int)Math.Clamp(failArgs.N2, 0, ushort.MaxValue);   // :3360
            tithingLoss = (ushort)failArgs.Locals.GetInt("TithingLoss", 0); // :3361
            effectColor = (uint)failArgs.Locals.GetInt("EffectColor", 0);
            effectRender = (uint)failArgs.Locals.GetInt("EffectRender", 0);
            failEffect = (ushort)(failArgs.Locals.GetInt("CreateObject1", 0) & 0xFFFF);
        }

        // Effect(EFFECT_OBJ, iT1, this, 1, 30, ...) then Sound(SOUND_SPELL_FIZZLE).
        if (failEffect != 0)
        {
            SphereNet.Network.Packets.PacketWriter fx = effectColor != 0 || effectRender != 0
                ? new SphereNet.Network.Packets.Outgoing.PacketEffectHued(3, caster.Uid.Value, caster.Uid.Value,
                    failEffect, caster.X, caster.Y, caster.Z, caster.X, caster.Y, caster.Z,
                    1, 30, false, false, effectColor, effectRender)
                : new SphereNet.Network.Packets.Outgoing.PacketEffect(3, caster.Uid.Value, caster.Uid.Value,
                    failEffect, caster.X, caster.Y, caster.Z, caster.X, caster.Y, caster.Z,
                    1, 30, false, false);
            Character.BroadcastNearby?.Invoke(caster.Position, 18, fx, 0);
        }
        OnPlaySound?.Invoke(caster.Position, 0x5C); // SOUND_SPELL_FIZZLE

        if (caster.PrivLevel >= PrivLevel.GM)
            return;

        // A scroll cast owes no reagents whether it succeeds, fizzles or is aborted:
        // Source-X gates Calc_SpellReagentsConsume on the cast source being the
        // caster themselves, and calls it with that same source on every path
        // (CCharSpell.cpp:3380/3398). The caller passes the RESOLVED source rather
        // than the raw tag, so a scroll that no longer exists cannot buy the exemption.
        bool takeReagents = !wand && !scroll && caster.IsPlayer &&
            Character.ReagentsRequiredEnabled;
        if (fizzle && !Character.ReagentLossFail) takeReagents = false;
        if (abort && !Character.ReagentLossAbort) takeReagents = false;

        if (lossEnabled && manaLoss > 0)
            caster.Mana = (short)Math.Max(0, caster.Mana - manaLoss);

        // Tithing lost with the reagents (:3381-3386, :3399-3404).
        if (reagentLossEnabled && tithingLoss > 0)
            caster.Tithing -= tithingLoss;

        // Calc_SpellReagentsConsume(fTest = false) (:3380/:3398): one LOWERREAGENTCOST
        // roll, then whatever of each reagent is there is spent.
        if (takeReagents)
            MissingReagent(caster, def, test: false);
    }

    /// <summary>A cast that reached its completion but cannot legally resolve.
    /// Source-X returns false from Spell_CastDone, which CCharSkill.cpp:3000 turns
    /// into SKTRIG_ABORT and prices through Spell_CastFail(fAbort = true) - so this
    /// is NOT an unconditional refund, it is the configured abort cost
    /// (MANALOSSABORT / REAGENTLOSSABORT, CCharSpell.cpp:3316).</summary>
    private bool FailCastAtCompletion(Character caster, string? message)
    {
        if (message != null)
            OnSysMessage?.Invoke(caster, message);
        return false;
    }

    public void CancelCast(Character caster)
    {
        if (caster.IsCasting) InterruptCast(caster, null);
    }

    private TriggerResult FireCastSkillTrigger(Character caster, SpellType spell, CharTrigger stage, TriggerArgs? args = null)
    {
        var def = _spells.Get(spell);
        if (def == null) return TriggerResult.Default;
        args ??= new TriggerArgs();
        args.CharSrc = caster;
        args.N1 = (int)def.GetPrimarySkill();
        args.Locals ??= new SphereNet.Scripting.Variables.VarMap();
        args.Locals.SetInt("spell", (int)spell);
        return TriggerDispatcher?.FireCharTrigger(caster, stage, args) ?? TriggerResult.Default;
    }

    private void InterruptCast(Character caster, string? reason)
    {
        SpellDef? def = null;
        if (caster.TryGetCastingSpell(out SpellType spell))
            def = _spells.Get(spell);

        if (def != null && FireCastSkillTrigger(caster, spell, CharTrigger.SkillAbort) != TriggerResult.True)
        {
            TryResolveCastSource(caster, out CastSourceKind kind, out _);
            ApplyCastResourceLoss(caster, def, kind == CastSourceKind.Wand,
                kind == CastSourceKind.Scroll, fizzle: false, abort: true);
        }

        // An aborted wand/scroll cast must not leave its source tag behind, or the
        // next cast would consume the charge/scroll on success.
        ClearCastSourceTags(caster);
        ClearCastState(caster);

        if (reason == null) return;
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
    /// <summary>Whether a creature's casting can be disturbed by a hit the way a
    /// player's is (ini NPCCANFIZZLEONHIT, default false). The host sets it from the
    /// configuration.</summary>
    public static bool NpcCanFizzleOnHit { get; set; }

    /// <summary>How far a polymorph may move a stat, in points (ini MAXPOLYSTATS,
    /// default 150). The host sets it from the configuration.</summary>
    public static int MaxPolyStats { get; set; } = 150;

    /// <summary>Take on the form's own STR and DEX, as far as the setting allows.
    ///
    /// Upstream moves the caster's stats to the creature definition's when
    /// MAGICF_POLYMORPHSTATS is on, by no more than MAXPOLYSTATS points in either
    /// direction, and remembers the change on the spell memory so it comes off with the
    /// form (CCharSpell.cpp:1082). SphereNet changed only the BODY: a mage in a dragon's
    /// shape kept a mage's strength, and the setting that bounds the change had nothing
    /// to bound. A definition that declares no stat leaves that stat alone, exactly as
    /// the reference's <c>if (pCharDef->m_Str)</c> does.</summary>
    private static void ApplyPolymorphStats(Character target, Item memory, ushort bodyId)
    {
        if (!IsMagicFlag(MagicConfigFlags.PolymorphStats))
            return;
        var def = Definitions.DefinitionLoader.GetCharDefByBody(bodyId);
        if (def == null)
            return;

        // The definition's declared value, not a fresh roll: a form has to be the same
        // form every time and has to come off exactly as it went on. The change goes on
        // the stat MODIFIER against the base (Stat_AddMod, CCharSpell.cpp:1097-1123) and
        // is remembered on the memory's m_PolyStr / m_PolyDex (MORE1L / MORE1H).
        int strChange = Change(def.StrMax > 0 ? def.StrMax : Math.Max(0, def.StrMin), target.Str);
        int dexChange = Change(def.DexMax > 0 ? def.DexMax : Math.Max(0, def.DexMin), target.Dex);
        target.ModStr = ClampShort(target.ModStr + strChange);
        target.ModDex = ClampShort(target.ModDex + dexChange);
        SetPolyStats(memory, (short)strChange, (short)dexChange);

        static int Change(int formValue, int baseValue)
        {
            if (formValue <= 0)
                return 0;                       // the form declares none: leave it be
            int change = formValue - baseValue;
            int cap = Math.Max(0, MaxPolyStats);
            if (change > cap) change = cap;
            else if (change < -cap) change = -cap;
            if (change + baseValue < 0) change = -baseValue;
            return change;
        }
    }

    public bool TryInterruptFromDamage(Character caster, int damage, bool breakParalyze = true)
    {
        // Source-X CChar::OnTakeDamage: taking damage removes paralyze (the
        // LAYER_SPELL_Paralyze memory is deleted) unless DAMAGE_NOUNPARALYZE.
        // The shared damage entry (CombatEngine.ApplyCharacterDamage) does that
        // itself with the blow's flags; its callers pass breakParalyze: false. The
        // damage sites that still write hit points directly keep the break here.
        if (breakParalyze && damage > 0 && caster.IsStatFlag(StatFlag.Freeze))
            BreakParalyze(caster);

        if (!caster.IsCasting)
            return false;

        if (IsMagicFlag(MagicConfigFlags.NoInterrupt))
            return false;

        // Reference disturb (OnTakeDamage): only players are disturbed; the
        // chance is the spell's INTERRUPT curve at the caster's skill
        // (per-mille) — the damage amount does not factor in.
        //
        // "Only players" is the DEFAULT, not the rule: NPCCANFIZZLEONHIT makes a
        // creature's casting interruptible too (CCharFight.cpp:881). The setting was
        // not read at all, so a shard asking for it got the default anyway.
        if (!caster.IsPlayer && !NpcCanFizzleOnHit)
            return false;

        int chance = 1000;
        SpellDef? castingDef = null;
        if (caster.TryGetCastingSpell(out SpellType castingSpell))
        {
            castingDef = GetSpellDef(castingSpell);
            if (castingDef != null)
                chance = castingDef.GetInterruptChance(caster.GetSkill(castingDef.GetPrimarySkill()));
        }
        if (chance <= 0)
            return false;

        // A Protection ward cancels the disturb outright with a chance of its level
        // in a thousand - under COMBAT_ELEMENTAL_ENGINE only, and not for a
        // SCRIPTED spell (CCharFight.cpp:890-900).
        if ((Character.CombatFlags & (int)Combat.CombatFlags.ElementalEngine) != 0 &&
            !(castingDef?.IsFlag(SpellFlag.Scripted) ?? false))
        {
            var ward = FindEffect(caster, m => MemSpell(m) is SpellType.Protection or SpellType.ArchProtection);
            if (ward != null && MemLevel(ward) > _rand.Next(1000))
                chance = 0;
        }

        if (chance > 0 && _rand.Next(1000) < chance)
        {
            InterruptCast(caster, "damaged");
            return true;
        }
        return false;
    }

    /// <summary>Whether an in-progress cast forbids this character from moving at
    /// all — the port of Source-X OnFreezeCheck (CCharAct.cpp:4539).
    ///
    /// The reference NEVER interrupts a spell because the caster walked. It picks
    /// one of two answers instead: either the cast roots you (MAGICF_FREEZEONCAST,
    /// or a spell carrying SPELLFLAG_FREEZEONCAST while the global flag is off), or
    /// you walk and keep casting. SPELLFLAG_NOFREEZEONCAST exempts a spell from the
    /// global flag.
    ///
    /// This engine interrupted on ANY step, which is neither answer: on a shard
    /// with MAGICFLAGS=0 — the live one — a caster lost the spell to a single
    /// footfall where the reference would have let them walk.</summary>
    public bool IsMovementFrozenByCast(Character caster)
    {
        if (!caster.IsCasting || caster.PrivLevel >= PrivLevel.GM)
            return false;
        if (!caster.TryGetCastingSpell(out SpellType castingSpell))
            return false;

        var def = GetSpellDef(castingSpell);
        if (def == null)
            return false;

        return IsMagicFlag(MagicConfigFlags.FreezeOnCast)
            ? !def.IsFlag(SpellFlag.NoFreezeOnCast)
            : def.IsFlag(SpellFlag.FreezeOnCast);
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

    /// <summary>One immutable result shared by target selection and cast start.</summary>
    public sealed record CastPreparation(SpellType Spell, int Difficulty, int WaitMs,
        string? Words, ushort Hue, byte Font)
    {
        /// <summary>The caller already ran the start-phase Spell_CanCast (fTest = true)
        /// for this cast, as the client does before its target cursor
        /// (Cmd_Skill_Magery, CClientUse.cpp:1004). CastStart then only re-checks the
        /// caster's state, the way Spell_CastStart does (CCharSpell.cpp:3435-3450).</summary>
        public bool SelectTested { get; init; }
    }

    public CastPreparation? PrepareCast(Character caster, SpellType spell)
    {
        if (caster.IsCasting || caster.IsDead) return null;
        var def = _spells.Get(spell);
        if (def == null) return null;
        caster.ActArg1 = (int)spell;
        // Skill_Start runs both pre-start hooks before Spell_CastStart.
        if (FireCastSkillTrigger(caster, spell, CharTrigger.SkillPreStart) == TriggerResult.True)
        {
            ClearCastSourceTags(caster);
            return null;
        }
        int difficulty = def.GetDifficulty() / 10;
        // WOPSTAFF / WOPPLAYER pick whether the mantra is spoken at all; an
        // insubstantial caster and a wand never speak it (CCharSpell.cpp:3473-3486).
        bool speakWords = caster.PrivLevel >= PrivLevel.Counsel ? WopStaff : WopPlayer;
        if (caster.IsStatFlag(StatFlag.Insubstantial))
            speakWords = false;
        if (TryResolveCastSource(caster, out var kind, out _))
        {
            if (kind == CastSourceKind.Wand) { difficulty = 1; speakWords = false; }
            else if (kind == CastSourceKind.Scroll) difficulty /= 2;
        }
        string words = def.GetPowerWords();
        var locals = new SphereNet.Scripting.Variables.VarMap();
        locals.Set("WOP", speakWords ? words : "");
        locals.SetInt("WOPColor", 0);
        locals.SetInt("WOPFont", 0);
        var args = new TriggerArgs { CharSrc = caster, N1 = (int)spell,
            N2 = difficulty, N3 = CalculateCastTimeTenths(caster, def), Locals = locals };
        if (TriggerDispatcher?.FireCharTrigger(caster, CharTrigger.SpellCast, args) == TriggerResult.True)
            return null;
        if (args.N1 < 0 || args.N1 > ushort.MaxValue || _spells.Get((SpellType)args.N1) == null)
            return null;
        string changedWords = locals.Get("WOP") ?? "";
        return new CastPreparation((SpellType)args.N1,
            (int)Math.Clamp(args.N2, -100000, 100000),
            (int)Math.Clamp(args.N3, 0, int.MaxValue / 100) * 100,
            changedWords == words ? null : changedWords,
            (ushort)Math.Clamp(locals.GetInt("WOPColor"), 0, ushort.MaxValue),
            (byte)Math.Clamp(locals.GetInt("WOPFont"), 0, byte.MaxValue));
    }

    /// <summary>The player-state half of Spell_CanCast / Spell_CastStart
    /// (CCharSpell.cpp:2339-2353, :3438-3450): a non-GM player who is dead, asleep,
    /// turned to stone or a statue cannot cast. Freezing is left to the callers,
    /// which already weigh MAGICF_CASTPARALYZED.</summary>
    private bool CasterStateRefusesCast(Character caster, bool failMsg)
    {
        if (!caster.IsPlayer || caster.PrivLevel >= PrivLevel.GM)
            return false;
        if (caster.IsDead || caster.IsStatFlag(StatFlag.Sleeping) || caster.IsStatFlag(StatFlag.Stone) ||
            (Definitions.CharDefHelper.GetCanFlags(caster) & CanFlags.C_Statue) != 0)
        {
            if (failMsg)
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellTryDead));
            return true;
        }
        if (caster.IsStatFlag(StatFlag.Freeze) && !IsMagicFlag(MagicConfigFlags.CastParalyzed))
        {
            if (failMsg)
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellTryFrozenhands));
            return true;
        }
        return false;
    }

    /// <summary>The start-phase Spell_CanCast (fTest = true, fFailMsg = true) against
    /// the caster's current cast source - what Cmd_Skill_Magery asks before the target
    /// cursor (CClientUse.cpp:1004). <paramref name="spell"/> comes back as the stages
    /// left ARGN1. A refusal drops the wand / scroll tags so they cannot leak into the
    /// next cast.</summary>
    public bool TestCanCast(Character caster, ref SpellType spell)
    {
        if (!TryResolveCastSource(caster, out var kind, out Item? source))
        {
            ClearCastSourceTags(caster);
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellEnchantActivate));
            return false;
        }
        if (SpellCanCast(caster, ref spell, test: true, failMsg: true, kind, source))
            return true;
        ClearCastSourceTags(caster);
        return false;
    }

    /// <summary>The port of Source-X Spell_CanCast (CCharSpell.cpp:2325-2541), shared by
    /// the start phase (<paramref name="test"/> = true: check only) and the completion
    /// (<paramref name="test"/> = false: Spell_CastDone :3010 consumes the wand charge,
    /// scroll, reagents, tithing and mana here).
    ///
    /// [SPELL] @Select and then @SpellSelect see ARGN1 = spell, ARGN2 = the mana cost
    /// (Calc_SpellManaCost with the source), ARGN3 = 0x1 test | 0x2 fail-message,
    /// ARGO = the source (the caster, or the wand / scroll) and LOCAL.TithingUse.
    /// RETURN 1 in either refuses; RETURN 0 in @Select accepts at once, skipping every
    /// check and cost below (:2381). Otherwise ARGN1, ARGN2 and LOCAL.TithingUse are
    /// read back as the spell and the bills (:2398-2406). A GM passes right after the
    /// source's ownership check and before any cost (:2430, :2461).</summary>
    private bool SpellCanCast(Character caster, ref SpellType spell, bool test, bool failMsg,
        CastSourceKind kind, Item? source)
    {
        var def = _spells.Get(spell);
        if (def == null || def.IsFlag(SpellFlag.Disabled))
            return false;
        if (CasterStateRefusesCast(caster, failMsg))
            return false;

        bool wand = kind == CastSourceKind.Wand;
        bool scroll = kind == CastSourceKind.Scroll;
        if (source != null && (source.IsDeleted || !IsCastSourceOnPerson(caster, source)))
        {
            // "magic items must be on your person to use" (:2422).
            if (failMsg)
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellEnchantActivate));
            return false;
        }

        int manaUse = SpellManaCost(caster, def, wand, scroll);
        int tithingUse = SpellTithingCost(caster, def, wand, scroll);
        if (TriggerDispatcher != null)
        {
            var args = new TriggerArgs
            {
                CharSrc = caster,
                N1 = (int)spell,
                N2 = manaUse,
                N3 = (test ? 0x1 : 0) | (failMsg ? 0x2 : 0),
                O1 = source != null ? source : caster,
                Locals = new SphereNet.Scripting.Variables.VarMap(),
            };
            args.Locals.SetInt("TithingUse", tithingUse);

            var selectResult = TriggerDispatcher.FireSpellTrigger(spell, "Select", caster, args);
            if (selectResult == TriggerResult.True)
                return false;
            if (selectResult == TriggerResult.False || args.ReturnNumber == 0)
                return true;

            args.ReturnNumber = null;
            if (TriggerDispatcher.FireCharTrigger(caster, CharTrigger.SpellSelect, args) == TriggerResult.True)
                return false;

            if (args.N1 != (int)spell)
            {
                if (args.N1 <= 0 || args.N1 > ushort.MaxValue)
                    return false;
                var rewritten = _spells.Get((SpellType)args.N1);
                if (rewritten == null)
                    return false;
                spell = (SpellType)args.N1;
                def = rewritten;
            }
            manaUse = (ushort)args.N2;
            tithingUse = (ushort)args.Locals.GetInt("TithingUse", 0);
        }

        if (kind != CastSourceKind.Self)
        {
            // A wand or scroll: the GM pays neither the charge nor the mana (:2430).
            if (caster.PrivLevel >= PrivLevel.GM)
                return true;
            // An NPC wand cast carries no item here; its AI spends the charge itself.
            if (source != null)
            {
                if (wand)
                {
                    if (!WandHasCharge(source))
                    {
                        if (failMsg)
                            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellWandNocharge));
                        return false;
                    }
                    if (!test)
                        ConsumeWandCharge(source);
                }
                else if (!test)
                {
                    if (source.Amount > 1) source.Amount--;
                    else _world?.RemoveItem(source);
                }
            }
        }
        else
        {
            // A raw cast: the GM needs no book, reagents, tithing or mana (:2461).
            if (caster.PrivLevel >= PrivLevel.GM)
                return true;
            if (caster.IsPlayer)
            {
                if (Character.SpellbookRequiredEnabled && !HasSpellInBook(caster, (int)spell))
                {
                    if (failMsg)
                        OnSysMessage?.Invoke(caster, ServerMessages.Get(caster.FindSpellbook((int)spell) == null
                            ? Msg.SpellTryNobook : Msg.SpellTryNotyourbook));
                    return false;
                }

                if (Character.ReagentsRequiredEnabled)
                {
                    int missingReagent = MissingReagent(caster, def, test);
                    if (missingReagent >= 0)
                    {
                        if (failMsg)
                            SendMissingReagentMessage(caster, def, missingReagent);
                        return false;
                    }
                }

                if (caster.Tithing < tithingUse)
                {
                    if (failMsg)
                        OnSysMessage?.Invoke(caster, ServerMessages.GetFormatted(Msg.SpellTryNotithing, tithingUse));
                    return false;
                }
                if (!test && tithingUse > 0)
                    caster.Tithing -= tithingUse;
            }
        }

        // fCheckAntiMagic (:2517): the caster's own area. Only non-GMs reach here.
        if (_world != null && _world.FindRegion(caster.Position) is { } region &&
            RegionBlocksSpell(region, def))
        {
            if (failMsg)
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.Magery6));
            return false;
        }

        if (caster.Mana < manaUse)
        {
            if (failMsg)
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellTryNomana));
            return false;
        }
        if (!test && manaUse > 0)
            caster.Mana = (short)(caster.Mana - manaUse);
        return true;
    }

    /// <summary>
    /// Begin casting a spell. Maps to Spell_CastStart.
    /// Returns cast time in milliseconds, or -1 on failure.
    /// <paramref name="wopOverride"/> comes from @SpellCast LOCAL.WOP:
    /// null = the spell's own power words, "" = silent cast, anything else
    /// replaces the spoken mantra.
    /// </summary>
    public int CastStart(Character caster, SpellType spell, Serial targetUid, Point3D targetPos,
        string? wopOverride = null, ushort wopHue = 0, byte wopFont = 0,
        CastPreparation? preparation = null)
    {
        LastCastRefusal = null;
        if (caster.IsCasting) return RefuseCast("already casting");
        if (caster.IsDead) return RefuseCast("dead");
        if (preparation == null)
        {
            // A direct engine cast (NPC, console, script) runs the start-phase
            // Spell_CanCast before its pre-start hooks, the way NPC_FightCast tests
            // before Skill_Start (CCharNPCAct_Magic.cpp:301).
            if (!TestCanCast(caster, ref spell))
                return RefuseCast("Spell_CanCast (test)");
            preparation = PrepareCast(caster, spell);
            if (preparation == null) return RefuseCast("preparation");
            preparation = preparation with { SelectTested = true };
        }
        spell = preparation.Spell;
        wopOverride ??= preparation.Words;
        if (wopHue == 0) wopHue = preparation.Hue;
        if (wopFont == 0) wopFont = preparation.Font;
        var def = _spells.Get(spell);
        if (def == null || def.IsFlag(SpellFlag.Disabled))
            return RefuseCast("spell undefined or disabled");

        if (IsSpellDisabledByConfig(spell))
            return RefuseCast("spell disabled by config");

        // No "not supported" refusal: Spell_CanCast (CCharSpell.cpp:2325) casts any
        // defined, non-disabled spell, and one with no native case simply does what
        // its script and layer say (OnSpellEffect default, :4148).

        if (caster.IsDead || (Definitions.CharDefHelper.GetCanFlags(caster) & CanFlags.C_Statue) != 0)
            return RefuseCast("dead or statue");
        if (caster.IsCasting)
            return RefuseCast("already casting");
        // Spell_CastStart re-checks the player's state whether or not the start-phase
        // Spell_CanCast already ran (CCharSpell.cpp:3438-3450): asleep or turned to
        // stone is "dead" for casting purposes, and frozen hands refuse unless
        // MAGICF_CASTPARALYZED - for a player out of GM mode only, with the message.
        // A second, unconditional freeze test after it refused NPCs and staff too,
        // and silently.
        if (CasterStateRefusesCast(caster, failMsg: true))
            return RefuseCast("caster state (dead/asleep/stone/frozen)");

        if (!TryResolveCastSource(caster, out var sourceKind, out Item? startSource))
        {
            ClearCastSourceTags(caster);
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellEnchantActivate));
            return RefuseCast("cast source");
        }

        // The start-phase Spell_CanCast (fTest = true): [SPELL] @Select and
        // @SpellSelect, then the source / book / reagent / tithing / anti-magic / mana
        // checks, nothing consumed. A client cast already ran it before its target
        // cursor (Cmd_Skill_Magery, CClientUse.cpp:1004); every other entry point -
        // NPC (CCharNPCAct_Magic.cpp:301), console, script - gets it here.
        if (!preparation.SelectTested)
        {
            var testSpell = spell;
            if (!SpellCanCast(caster, ref testSpell, test: true, failMsg: true, sourceKind, startSource))
            {
                ClearCastSourceTags(caster);
                return RefuseCast("Spell_CanCast");
            }
        }

        // Source-X Spell_CastStart (CCharSpell.cpp:3544): with EQUIPPEDCAST off the
        // caster's HANDS ARE EMPTIED, not the cast refused — Spell_Unequip bounces
        // each held item into the pack and only fails when the item will not go
        // (:2849). Fizzling instead meant a player with a weapon in hand simply
        // could not cast, which is what the live shard does today: its
        // EQUIPPEDCAST is 0.
        if (!Character.EquippedCastEnabled && caster.IsPlayer &&
            caster.PrivLevel < PrivLevel.GM)
        {
            if (!TrySpellUnequip(caster, Layer.OneHanded) ||
                !TrySpellUnequip(caster, Layer.TwoHanded))
                return RefuseCast("Spell_Unequip (hands)");
        }

        RevealOnCast(caster);

        // Store cast state on character
        caster.BeginCast(spell, targetUid, targetPos);
        caster.CastDifficulty = preparation.Difficulty;
        caster.CastAborted = ch => FireCastSkillTrigger(ch, spell, CharTrigger.SkillAbort);

        if (!targetPos.Equals(caster.Position))
        {
            var newDir = caster.Position.GetDirectionTo(targetPos);
            if (newDir != caster.Direction)
            {
                caster.Direction = newDir;
                OnCasterFacingChanged?.Invoke(caster);
            }
        }

        var powerWords = wopOverride ?? def.GetPowerWords();
        if (!string.IsNullOrEmpty(powerWords))
        {
            if (OnSpellWordsEx != null)
                OnSpellWordsEx(caster, powerWords, wopHue, wopFont);
            else
                OnSpellWords?.Invoke(caster, powerWords);
        }

        // The spell's own definition picks the gesture - SPELLFLAG_DIR_ANIM casts
        // directed, everything else casts over an area - and SPELLFLAG_NO_CASTANIM or
        // MAGICF_NOANIM plays none (Spell_CastStart, CCharSpell.cpp:3566-3567). This
        // chose by the TARGET instead: every self-cast went up as an area cast and
        // every targeted one as a directed cast, whatever the spell said, and the two
        // switches that turn the gesture off were never asked.
        if (!def.IsFlag(SpellFlag.NoCastAnim) && !IsMagicFlag(MagicConfigFlags.NoAnimation))
        {
            ushort castAnim = def.IsFlag(SpellFlag.DirAnim)
                ? (ushort)Core.Enums.AnimationType.CastDirected
                : (ushort)Core.Enums.AnimationType.CastArea;
            OnCastAnimation?.Invoke(caster, castAnim);
        }

        // Skill_Start runs after Spell_CastStart; ARGN2 is the skill delay in tenths.
        var skillDef = Definitions.DefinitionLoader.GetSkillDef((int)def.GetPrimarySkill());
        int skillDelay = skillDef?.Delay.GetLinear(caster.GetSkill(def.GetPrimarySkill())) ?? 0;
        var skillArgs = new TriggerArgs { N2 = skillDelay };
        if (FireCastSkillTrigger(caster, spell, CharTrigger.SkillStart, skillArgs) == TriggerResult.True)
        {
            ClearCastSourceTags(caster);
            ClearCastState(caster);
            return RefuseCast("@SkillStart/@Start returned 1");
        }
        int waitMs = skillArgs.N2 > 0
            ? (int)Math.Min(skillArgs.N2, int.MaxValue / 100) * 100 : preparation.WaitMs;
        return Math.Max(1, waitMs);
    }

    /// <summary>Which gate refused the last <see cref="CastStart"/>, or null when it
    /// started. Upstream answers a refused Skill_Start with Skill_Cleanup and nothing
    /// else (CCharSkill.cpp:4434-4519) - no message of its own and no @SpellFail - so
    /// the reason is only for the caller's log.</summary>
    public string? LastCastRefusal { get; private set; }

    private int RefuseCast(string gate)
    {
        LastCastRefusal = gate;
        return -1;
    }

    /// <summary>
    /// Complete a spell cast. Maps to Spell_CastDone.
    /// Called after cast timer expires.
    /// </summary>
    public bool CastDone(Character caster)
    {
        // Capture the spell before the core clears cast state, then fire the
        // shared resolution hook so NPC/direct casts reach @SpellSuccess /
        // @SpellEffect / @SpellFail — the client path no longer fires these.
        bool wasCasting = caster.TryGetCastingSpell(out SpellType resolvedSpell);
        var resolvedDef = wasCasting ? _spells.Get(resolvedSpell) : null;
        int gainDifficulty = caster.CastDifficulty ?? (resolvedDef?.GetDifficulty() / 10 ?? 0);
        var outerSourceItem = _effectSourceItem;
        bool ok, terminalHandled;
        try { ok = CastDoneCore(caster, out terminalHandled); }
        finally { _effectSourceItem = outerSourceItem; }
        if (wasCasting && !ok && !terminalHandled)
        {
            if (caster.IsCasting) CancelCast(caster);
            else FireCastSkillTrigger(caster, resolvedSpell, CharTrigger.SkillAbort);
        }
        if (wasCasting)
            OnCastResolved?.Invoke(caster, resolvedSpell, ok);
        // Source-X Skill_Done grants credit only after the success stage completes.
        if (ok && resolvedDef != null)
            SkillEngine.GainExperience(caster, resolvedDef.GetPrimarySkill(), gainDifficulty);
        return ok;
    }

    // Source-X Skill_Done runs @Success before the precast target cursor.
    public bool CompletePrecastSkill(Character caster)
    {
        if (!caster.TryGetCastingSpell(out var spell)) return false;
        var def = _spells.Get(spell);
        if (def == null || !TryResolveCastSource(caster, out var kind, out _))
        {
            CancelCast(caster);
            return false;
        }
        bool ok = CompleteCastSkill(caster, def, kind);
        if (!ok) OnCastResolved?.Invoke(caster, spell, false);
        return ok;
    }

    private bool CompleteCastSkill(Character caster, SpellDef def, CastSourceKind sourceKind)
    {
        var primarySkill = def.GetPrimarySkill();
        var spell = def.Id;
        bool castWithWand = sourceKind == CastSourceKind.Wand;
        bool castFromScroll = sourceKind == CastSourceKind.Scroll;
        int difficulty = caster.CastDifficulty ?? (castWithWand ? 1 : def.GetDifficulty() / (castFromScroll ? 20 : 10));
        difficulty *= 10;
        if (caster.CastSkillSucceeded) return true;
        // Source-X: skill check at cast completion — fizzle on failure.
        // GetDifficulty() is on the 0-1000 skill scale, but CheckSuccess expects
        // a 0-100 difficulty (it multiplies by 10 internally) — convert here so
        // the bell curve compares like-for-like against the 0-1000 skill value.
        bool fizzled = caster.PrivLevel < PrivLevel.GM &&
            !SkillEngine.CheckSuccess(caster, primarySkill, difficulty / 10);

        if (fizzled)
        {
            bool cancelled = FireCastSkillTrigger(caster, spell, CharTrigger.SkillFail) == TriggerResult.True;
            ApplyCastResourceLoss(caster, def, castWithWand, castFromScroll,
                fizzle: !cancelled, abort: cancelled);
            if (!cancelled)
                SkillEngine.GainExperience(caster, primarySkill, -Math.Abs(difficulty / 10));
            ClearCastSourceTags(caster);
            ClearCastState(caster);
            if (!cancelled) OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
            return false;
        }

        if (FireCastSkillTrigger(caster, spell, CharTrigger.SkillSuccess) == TriggerResult.True)
        {
            CancelCast(caster);
            return false;
        }

        caster.CastSkillSucceeded = true;
        return true;
    }

    private bool CastDoneCore(Character caster, out bool terminalHandled)
    {
        terminalHandled = false;
        if (caster.IsDead || (Definitions.CharDefHelper.GetCanFlags(caster) & CanFlags.C_Statue) != 0)
        {
            return false;
        }

        if (!caster.TryGetCastingSpell(out SpellType spell))
            return false;

        var def = _spells.Get(spell);
        if (def == null)
        {
            return false;
        }

        // Resolve target before consumption so LOS can be checked first
        Serial targetUid = caster.CastTargetUid;
        Point3D targetPos = caster.CastTargetPos;

        // Source-X re-resolves the cast source at completion and refuses the cast if
        // it is gone or no longer on the caster (CCharSpell.cpp:2882 -> :3010).
        if (!TryResolveCastSource(caster, out CastSourceKind sourceKind, out Item? castSource))
            return FailCastAtCompletion(caster,
                ServerMessages.Get(Msg.SpellEnchantActivate));

        if (Character.SpellbookRequiredEnabled && caster.IsPlayer &&
            caster.PrivLevel < PrivLevel.GM && sourceKind == CastSourceKind.Self &&
            !HasSpellInBook(caster, (int)spell))
        {
            string message = ServerMessages.Get(caster.FindSpellbook((int)spell) == null
                ? Msg.SpellTryNobook : Msg.SpellTryNotyourbook);
            OnSysMessage?.Invoke(caster, message);
            return false;
        }

        // LOS check BEFORE consuming resources
        if (_world != null &&
            caster.PrivLevel < PrivLevel.GM &&
            !IsMagicFlag(MagicConfigFlags.NoLos) &&
            (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj) ||
             def.IsFlag(SpellFlag.Area)     || def.IsFlag(SpellFlag.Field) ||
             def.IsFlag(SpellFlag.Summon)))
        {
            int losDist = Math.Max(Math.Abs(caster.X - targetPos.X), Math.Abs(caster.Y - targetPos.Y));
            // Spell_TargCheck casts through windows (LOS_NB_WINDOWS): at the object,
            // or at the point within the caster's view range (CCharSpell.cpp:2764/2787).
            var losObject = targetUid.IsValid ? _world.FindObject(targetUid) : null;
            bool inSight = losObject != null && losObject.GetTopLevelObj().MapIndex == caster.MapIndex
                ? _world.CanSeeLOSFor(caster, losObject, LosFlags.NbWindows)
                : _world.CanSeeLOSFor(caster, caster.Position, targetPos, LosFlags.NbWindows, caster.VisualRange);
            if (losDist > 0 && !inSight)
                return FailCastAtCompletion(caster, "Target not in line of sight.");
        }

        // Source-X opens Spell_CastDone with Spell_TargCheck (CCharSpell.cpp:2878)
        // and only reaches the reagent/mana/charge consumption 130 lines later
        // (:3010). SphereNet re-resolved the target AFTER those costs were taken, so
        // a target that died, walked away or vanished during the cast time was paid
        // for in full and still reported success to @SpellSuccess.
        if (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj))
        {
            var preTarget = _world?.FindChar(targetUid);
            if (preTarget != null)
            {
                // :2740 - a ghost is not a legal target unless the spell says so.
                if (preTarget.IsDead && !def.IsFlag(SpellFlag.TargDead))
                    return FailCastAtCompletion(caster,
                        ServerMessages.Get(Msg.SpellTargDead));
                // Spell_TargCheck has no range of its own, only LOS (:2764), and
                // CanSeeLOS reaches as far as the target's visual range
                // (CCharLOS.cpp:684-687) - not an invented 12 tiles.
                if (preTarget.MapIndex != caster.MapIndex ||
                    caster.Position.GetDistanceTo(preTarget.Position) > preTarget.VisualRange)
                    return FailCastAtCompletion(caster, "Target not in line of sight.");
            }
            // :2728 - "need a target". A spell that may also be aimed at the ground
            // (TARG_XYZ) is allowed to complete without one.
            else if (_world?.FindItem(targetUid) == null && !def.IsFlag(SpellFlag.TargXYZ))
                return FailCastAtCompletion(caster,
                    ServerMessages.Get(Msg.SpellTargObj));
        }

        var primarySkill = def.GetPrimarySkill();
        int skillVal = caster.GetSkill(primarySkill);

        if (!CompleteCastSkill(caster, def, sourceKind))
        {
            terminalHandled = true; // @Fail or @Abort already dispatched.
            return false;
        }

        // An item cast is as strong as the item, not the caster (CCharSpell.cpp:2893).
        // Read before the scroll is consumed.
        Item? levelSource = castSource;
        if (levelSource == null && sourceKind == CastSourceKind.Wand)
            levelSource = caster.GetEquippedItem(Layer.OneHanded) is { ItemType: ItemType.Wand } held ? held : null;
        int skillLevel = sourceKind != CastSourceKind.Self && levelSource != null
            ? MagicItemSkillLevel(levelSource)
            : skillVal;

        // @SpellSuccess / [SPELL] @Success run BEFORE anything is paid for or
        // placed (CCharSpell.cpp:2928-2990): RETURN 1 aborts the cast, ARGN2 is the
        // skill level, and the Duration / AreaRadius / FieldWidth / FieldGauge /
        // CreateObject1/2 / EffectColor locals are read back.
        var stage = FireSpellSuccessStage(caster, def, skillLevel, castSource);
        if (stage == null)
            return false;
        skillLevel = stage.SkillLevel;

        // Source-X builds the summon and weighs it against the follower cap BEFORE
        // the spell is paid for: Spell_Summon_Try (CCharSpell.cpp:3002) creates the
        // chosen creature and refuses it on GetFollowerSlots (:2662), and only then
        // does Spell_CanCast consume (:3010) - deleting the summon again if that
        // consumption cannot be met (:3012). SphereNet summoned in the effect stage
        // instead, long after the mana, reagents and scroll had been taken, so a
        // refused summon was charged for in full. The refusal reports itself, hence
        // no second message here.
        Character? summoned = null;
        if (def.IsFlag(SpellFlag.Summon))
        {
            summoned = PrepareSummon(caster, targetPos, def, spell, skillVal, stage.CreateObject1,
                stage.DurationTenths, stage.FollowerSlotsOverride);
            if (summoned == null)
                return FailCastAtCompletion(caster, null);
        }

        // "Consume the reagents/mana/scroll/charge" (Spell_CastDone, CCharSpell.cpp:3010):
        // the completion re-enters Spell_CanCast with fTest = false, so the player
        // state, a definition disabled mid-cast, [SPELL] @Select / @SpellSelect (with
        // the bill they leave in ARGN2 / LOCAL.TithingUse) and the GM exemption all
        // apply again here, and only then is anything paid. A refusal deletes the
        // summon that was already built (:3012). A charge or scroll is therefore never
        // lost to an interrupted, cancelled or fizzled cast.
        var consumeSpell = spell;
        if (!SpellCanCast(caster, ref consumeSpell, test: false, failMsg: true, sourceKind, castSource))
        {
            DiscardSummon(summoned);
            return false;
        }
        PlaceSummonEffect(caster, summoned, def);
        ClearCastSourceTags(caster);

        // Clear cast state
        ClearCastState(caster);

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
                else if (FireItemSpellEffect(caster, rune, def, skillLevel, castSource) == TriggerResult.True)
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
                if (FireItemSpellEffect(caster, fieldItem, def, skillLevel, castSource) != TriggerResult.True)
                    OnItemRemoved?.Invoke(fieldItem);
            }
            else
            {
                OnSysMessage?.Invoke(caster, "That is not a magical field.");
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // Animate Dead targets a CORPSE item and raises an undead controlled by
        // the caster (reference SPELL_Animate_Dead, CCharSpell.cpp:2608-2632).
        if (spell is SpellType.AnimateDeadAOS or SpellType.AnimateDead)
        {
            var corpse = _world?.FindItem(targetUid);
            if (corpse != null && !corpse.IsDeleted && corpse.ItemType == ItemType.Corpse &&
                CanReachCorpse(caster, corpse))
            {
                // A humanoid corpse raises a zombie; a creature corpse raises its
                // own kind (the corpse stores the original body in Amount).
                ushort corpseBody = corpse.Amount;
                // CCharBase::IsPlayableID (Spell_Summon_Try, CCharSpell.cpp:2620):
                // human, elf AND gargoyle corpses rise as zombies.
                bool humanoid = corpseBody is 0x0190 or 0x0191 or 0x025D or 0x025E or 0x029A or 0x029B;
                ushort body = humanoid ? (ushort)0x0003 : (corpseBody == 0 ? (ushort)0x0003 : corpseBody);
                // Stats/skills come from the raised creature's chardef @Create
                // (SummonCreature applies it) — not flat invented numbers.
                var undead = SummonCreature(caster, corpse.Position, def, skillLevel, bodyId: body);
                PlaceSummonEffect(caster, undead, def);
                if (undead != null)
                    OnItemRemoved?.Invoke(corpse); // the corpse is consumed
            }
            else
            {
                OnSysMessage?.Invoke(caster, "You cannot animate that.");
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // Bone Armor targets a SKELETON corpse and converts it into up to five
        // bone armor pieces at diminishing odds (reference SPELL_Bone_Armor,
        // CCharSpell.cpp:3234-3270). Any carried loot is dumped first.
        if (spell == SpellType.BoneArmor)
        {
            var corpse = _world?.FindItem(targetUid);
            if (corpse == null || corpse.IsDeleted || corpse.ItemType != ItemType.Corpse ||
                !CanReachCorpse(caster, corpse))
            {
                OnSysMessage?.Invoke(caster, "That is not a corpse!");
            }
            else if (corpse.Amount != 0x0032) // CREID_SKELETON only
            {
                OnSysMessage?.Invoke(caster, "The body stirs for a moment.");
            }
            else
            {
                foreach (var child in corpse.Contents.ToArray())
                {
                    corpse.RemoveItem(child);
                    _world!.PlaceItemWithDecay(child, corpse.Position);
                }
                // ITEMID_BONE_ARMS/ARMOR/GLOVES/HELM/LEGS, 1/(2+n) stop chance
                // per piece like the reference loop.
                ReadOnlySpan<ushort> bonePieces = [0x144E, 0x144F, 0x1450, 0x1451, 0x1452];
                int got = 0;
                foreach (ushort pieceId in bonePieces)
                {
                    if (_rand.Next(2 + got) == 0)
                        break;
                    var piece = _world!.CreateItem();
                    piece.BaseId = pieceId;
                    // Run the ITEMDEF @Create metadata (type, armor, durability,
                    // events) like every other scripted create path — a bare
                    // BaseId would leave the piece a graphic-only shell.
                    Definitions.ItemDefHelper.ApplyInstanceMetadata(piece, pieceId);
                    _world.PlaceItemWithDecay(piece, corpse.Position);
                    got++;
                }
                OnItemRemoved?.Invoke(corpse); // the corpse is consumed
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // The wand or scroll rides into every OnSpellEffect this cast makes as its
        // pSourceItem - the target's @SpellEffect ARGO (CCharSpell.cpp:3703).
        _effectSourceItem = castSource is { IsDeleted: false } ? castSource : null;

        // Apply spell effect, in Source-X's order (CCharSpell.cpp:3017-3090): a
        // FIELD spell lays its field and an AREA spell sweeps its radius whatever
        // TARG_ flags it also carries, then summons, then the per-spell cases.
        if (def.IsFlag(SpellFlag.Field))
        {
            // A SCRIPTED field is laid only when it is one of the five with
            // default art (:3025).
            if (!def.IsFlag(SpellFlag.Scripted) || FieldTiles(def.Id).EW != 0)
                CreateField(caster, targetPos, def, skillLevel, stage);
        }
        else if (def.IsFlag(SpellFlag.Area))
        {
            int radius = stage.AreaRadius > 0 ? stage.AreaRadius
                : def.IsFlag(SpellFlag.Scripted) ? 4 : DefaultAreaRadius(def.Id, skillLevel);
            // Centred on the target point only for a spell aimed with TARG_OBJ or
            // TARG_XYZ; otherwise on the caster where they stand now (:3082).
            var center = (def.Flags & (SpellFlag.TargObj | SpellFlag.TargXYZ)) != 0
                ? targetPos : caster.Position;
            ApplyAreaEffect(caster, center, def, skillLevel, radius);
        }
        else if (def.IsFlag(SpellFlag.Summon))
        {
            // Already created above, before the costs were taken - see PrepareSummon.
        }
        else if (def.IsFlag(SpellFlag.Scripted))
        {
            // A SCRIPTED spell never reaches the native cases: a poly spell stops
            // here (:3045), anything else only gets its target's OnSpellEffect,
            // whose triggers do the work (:3049, :3814).
            if (def.IsFlag(SpellFlag.Poly))
                return false;
            var scriptedChar = _world?.FindChar(targetUid);
            if (scriptedChar != null)
                ApplyCharEffect(caster, scriptedChar, def, skillLevel, stage.DurationTenths);
            else if (_world?.FindItem(targetUid) is { } scriptedItem)
                FireItemSpellEffect(caster, scriptedItem, def, skillLevel, castSource);
        }
        else if (spell is SpellType.Recall or SpellType.GateTravel or SpellType.SacredJourney)
        {
            var rune = _world?.FindItem(targetUid);
            if (rune != null && IsItemAccessible(caster, rune))
            {
                if (!RuneTravelHandledByScript(caster, rune, def))
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
                else if (target.MapIndex != caster.MapIndex)
                {
                    OnSysMessage?.Invoke(caster, "That is too far away.");
                }
                else if (spell == SpellType.MindBlast)
                {
                    // SPELL_Mind_Blast (CCharSpell.cpp:3160-3173): half the INT
                    // difference, capped at half the victim's max hit points, is the
                    // SKILL LEVEL the normal effect runs at; a duller caster takes it
                    // himself.
                    var victim = target;
                    int diff = (caster.Int - target.Int) / 2;
                    if (diff < 0) { victim = caster; diff = -diff; }
                    int max = victim.MaxHits / 2;
                    // Rebounding onto the caster is harming oneself, which
                    // OnSpellEffect refuses without MAGICF_CANHARMSELF (:3756).
                    if (victim != caster || IsMagicFlag(MagicConfigFlags.CanHarmSelf))
                        ApplyCharEffect(caster, victim, def, Math.Min(diff, max), stage.DurationTenths);
                }
                else
                {
                    ApplyCharEffect(caster, target, def, skillLevel, stage.DurationTenths);
                }
            }
            else if (def.IsFlag(SpellFlag.TargObj))
            {
                var itemTarget = _world?.FindItem(targetUid);
                if (itemTarget != null && CanSpellReachItem(caster, itemTarget))
                {
                    // Fire the scriptable @SpellEffect first; only apply the
                    // hardcoded item-spell behavior when no script overrode it.
                    if (FireItemSpellEffect(caster, itemTarget, def, skillLevel, castSource) != TriggerResult.True)
                        ApplyItemTargetSpell(caster, itemTarget, def);
                }
            }
        }
        else if (spell == SpellType.CreateFood)
        {
            CreateFood(caster, def, targetPos);
        }
        else
        {
            // Self-buff or ground target. The cast state - its target point
            // included - is already cleared, so the point Teleport aims at is
            // handed down here (Source-X keeps it in m_Act_p, CCharSpell.cpp:3140).
            var prevTargetPos = _effectTargetPos;
            _effectTargetPos = targetPos;
            try
            {
                ApplyCharEffect(caster, caster, def, skillLevel, stage.DurationTenths);
            }
            finally
            {
                _effectTargetPos = prevTargetPos;
            }
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

    /// <summary>What @SpellSuccess / [SPELL] @Success left behind for the rest of
    /// Spell_CastDone (CCharSpell.cpp:2973-2990). Zero means "not set".</summary>
    private sealed record SpellSuccessStage(int SkillLevel, int DurationTenths, int AreaRadius,
        int FieldWidth, int FieldGauge, ushort CreateObject1, ushort CreateObject2, ushort EffectColor,
        int FollowerSlotsOverride = -1);

    /// <summary>Fire @SpellSuccess then [SPELL] @Success with Spell_CastDone's
    /// arguments (CCharSpell.cpp:2928-2971): ARGN1 = spell, ARGN2 = skill level,
    /// ARGO = the wand or scroll, LOCAL.Duration (GetSpellDuration), and for an
    /// AREA / FIELD / SUMMON spell its own locals. Returns null when either
    /// stage answered RETURN 1 - the cast is aborted before anything is paid.</summary>
    private SpellSuccessStage? FireSpellSuccessStage(Character caster, SpellDef def, int skillLevel, Item? source)
    {
        int duration = GetSpellDuration(def, skillLevel, caster, caster);
        bool isField = def.IsFlag(SpellFlag.Field);
        var (fieldEW, fieldNS) = isField ? FieldTiles(def.Id) : ((ushort)0, (ushort)0);
        if (TriggerDispatcher == null)
            return new SpellSuccessStage(skillLevel, duration, 0, 0, 0, 0, 0, 0);

        var locals = new SphereNet.Scripting.Variables.VarMap();
        locals.SetInt("Duration", duration);
        if (def.IsFlag(SpellFlag.Area))
            locals.SetInt("AreaRadius", 0);
        if (isField)
        {
            locals.SetInt("FieldWidth", 0);
            locals.SetInt("FieldGauge", 0);
            locals.SetInt("CreateObject1", fieldEW);
            locals.SetInt("CreateObject2", fieldNS);
        }
        if (def.IsFlag(SpellFlag.Summon))
            locals.SetInt("FollowerSlotsOverride", -1);

        var args = new TriggerArgs
        {
            CharSrc = caster,
            N1 = (int)def.Id,
            N2 = skillLevel,
            O1 = source is { IsDeleted: false } ? source : null,
            Locals = locals,
        };
        if (TriggerDispatcher.FireCharTrigger(caster, CharTrigger.SpellSuccess, args) == TriggerResult.True)
            return null;
        if (TriggerDispatcher.FireSpellTrigger(def.Id, "Success", caster, args) == TriggerResult.True)
            return null;

        static ushort Id(long raw) => (ushort)(raw & 0xFFFF);
        ushort obj1 = Id(locals.GetInt("CreateObject1", 0));
        ushort obj2 = Id(locals.GetInt("CreateObject2", 0));
        // A field keeps its default art unless the script changed it; only a
        // changed id counts as an override (it1test / it2test, :2981-2982).
        if (isField)
        {
            if (obj1 == fieldEW) obj1 = 0;
            if (obj2 == fieldNS) obj2 = 0;
        }
        return new SpellSuccessStage(
            (int)Math.Clamp(args.N2, int.MinValue, int.MaxValue),
            (int)Math.Clamp(locals.GetInt("Duration", duration), 0, int.MaxValue),
            (int)Math.Clamp(locals.GetInt("AreaRadius", 0), 0, 255),
            (int)Math.Clamp(locals.GetInt("FieldWidth", 0), 0, 255),
            (int)Math.Clamp(locals.GetInt("FieldGauge", 0), 0, 255),
            obj1, obj2,
            (ushort)Math.Clamp(locals.GetInt("EffectColor", 0), 0, ushort.MaxValue),
            // LOCAL.FollowerSlotsOverride, narrowed to a short; -1 = the creature's
            // own FOLLOWERSLOTS (CCharSpell.cpp:2994-3001).
            def.IsFlag(SpellFlag.Summon)
                ? unchecked((short)locals.GetInt("FollowerSlotsOverride", -1))
                : -1);
    }

    /// <summary>Source-X CChar::Use_Obj for a spell that acts as a double-click
    /// (Telekinesis). Program.cs routes it through the caster's client with the
    /// reach test off; unwired (headless) the spell does nothing further.</summary>
    public Action<Character, Item>? OnUseObject { get; set; }

    /// <summary>Source-X CChar::CheckCorpseCrime(pCorpse, fLooting=true): flag the
    /// caster criminal (with witnesses) when reaching into this corpse is a crime.
    /// Wired to DeathEngine.</summary>
    public Action<Character, Item>? OnCorpseCrimeCheck { get; set; }

    /// <summary>Hardcoded behavior for item-targeted spells. Runs only when no
    /// @SpellEffect script overrode it.
    ///
    /// Magic Trap and Magic Untrap have no hard-coded effect upstream: Spell_CastDone
    /// hands them to CItem::OnSpellEffect (CCharSpell.cpp:3117-3124), whose switch has
    /// no case for either (CItem.cpp:5663), so what they do is the script's. SphereNet
    /// wrote a TRAPPED tag that nothing ever read.</summary>
    private void ApplyItemTargetSpell(Character caster, Item item, SpellDef def)
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
            case SpellType.Telekinesis:
                ApplyTelekinesis(caster, item);
                break;
        }
    }

    /// <summary>SPELL_Telekin (CCharSpell.cpp:3126): reaching into someone else's
    /// corpse is the looting crime and reveals the caster; then the target is used
    /// as a double-click without the touch test.</summary>
    private void ApplyTelekinesis(Character caster, Item item)
    {
        if (item.GetTopLevelObj() is Item { ItemType: ItemType.Corpse } corpse && !IsCorpseOf(corpse, caster))
        {
            OnCorpseCrimeCheck?.Invoke(caster, corpse);
            caster.ClearHiddenState();
        }
        OnUseObject?.Invoke(caster, item);
    }

    /// <summary>The corpse's owner link (m_uidLink) names this character.</summary>
    private static bool IsCorpseOf(Item corpse, Character ch) =>
        corpse.Link == ch.Uid ||
        (corpse.TryGetTag("OWNER_UID", out string? owner) &&
         ScriptNumber.TryParseUInt(owner, out uint ownerUid) && ownerUid == ch.Uid.Value);

    /// <summary>CItem::OnSpellEffect's script stages (CItem.cpp:5600-5631): the item's
    /// @SpellEffect with ARGN1 = spell, ARGN2 = skill level and ARGO = the wand or
    /// scroll the spell came from, then the [SPELL]'s own @Effect on the item with the
    /// same args. RETURN 1 in either stops the built-in effect; RETURN 0 does too for a
    /// SPELLFLAG_SCRIPTED spell. Returns <see cref="TriggerResult.True"/> when the
    /// built-in effect must not run.</summary>
    private TriggerResult FireItemSpellEffect(Character caster, Item item, SpellDef def,
        int skillLevel = 0, Item? sourceItem = null)
    {
        if (TriggerDispatcher == null)
            return TriggerResult.Default;

        var args = new TriggerArgs
        {
            CharSrc = caster,
            ItemSrc = item,
            O1 = sourceItem is { IsDeleted: false } ? sourceItem : null,
            N1 = (int)def.Id,
            N2 = skillLevel,
        };
        bool scripted = def.IsFlag(SpellFlag.Scripted);
        var result = TriggerDispatcher.FireItemTrigger(item, ItemTrigger.SpellEffect, args);
        if (result == TriggerResult.True || (result == TriggerResult.False && scripted))
            return TriggerResult.True;

        args.ReturnNumber = null;
        var stage = TriggerDispatcher.FireSpellTrigger(def.Id, "Effect", item, args);
        if (stage == TriggerResult.True || (args.ReturnNumber == 0 && scripted))
            return TriggerResult.True;
        return TriggerResult.Default;
    }

    /// <summary>CItem::OnSpellEffect (CItem.cpp:5590-5764) as a field lays it on the
    /// items of its tile (CCharSpell.cpp:2299): @SpellEffect and the spell's @Effect
    /// with their ARGN1/ARGN2 readback, the wand recharge, the caster's anti-magic
    /// area, the per-spell cases a field can reach, a HARM spell's one point of item
    /// damage and the spell's FX. Returns false when the spell did not take.
    /// Magic Lock, Unlock and Mark are targeted at one item and resolved by their own
    /// cast branches; only a script rewriting ARGN1 could bring them here, and then
    /// they do nothing further.</summary>
    private bool ItemOnSpellEffect(Character caster, Item item, SpellDef def, int skillLevel, Item? sourceItem)
    {
        var args = new TriggerArgs
        {
            CharSrc = caster,
            ItemSrc = item,
            O1 = sourceItem is { IsDeleted: false } ? sourceItem : null,
            N1 = (int)def.Id,
            N2 = skillLevel,
            Locals = new SphereNet.Scripting.Variables.VarMap(),
        };
        bool scripted = def.IsFlag(SpellFlag.Scripted);
        if (TriggerDispatcher != null)
        {
            var result = TriggerDispatcher.FireItemTrigger(item, ItemTrigger.SpellEffect, args);
            if (result == TriggerResult.True)
                return false;
            if (result == TriggerResult.False && scripted)
                return true;

            args.ReturnNumber = null;
            var stage = TriggerDispatcher.FireSpellTrigger(def.Id, "Effect", item, args);
            if (stage == TriggerResult.True)
                return false;
            if (stage == TriggerResult.False && scripted)
                return true;
        }
        if (item.IsDeleted)
            return false;

        var spell = (SpellType)(int)args.N1;
        skillLevel = (int)Math.Clamp(args.N2, int.MinValue, int.MaxValue);
        def = _spells.Get(spell) ?? def;

        // Recharge a wand of this spell (or a blank one).
        if (item.ItemType == ItemType.Wand)
        {
            var wandSpell = MagicItemSpell(item);
            if (wandSpell == 0 || wandSpell == spell)
            {
                item.SetAttr(ObjAttributes.Magic);
                if (wandSpell == 0 || caster.IsGmMode)
                {
                    item.MoreP = new Point3D((short)spell, (short)Math.Clamp(skillLevel, short.MinValue, short.MaxValue),
                        item.MoreP.Z, item.MoreP.Map);
                    item.More2 = 0;
                }
                item.More2++;
            }
        }

        if (!caster.IsGmMode && _world?.FindRegion(caster.Position) is { } region &&
            RegionBlocksSpell(region, def))
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellTryAm));
            return false;
        }

        ushort effectId = def.EffectId;
        DamageType damageType = 0;
        switch (spell)
        {
            case SpellType.DispelField:
                if (item.ItemType == ItemType.Spell)
                {
                    if (IsTopLevelItem(item))
                        SendItemEffect(2, effectId, caster, item, 9, 20, false, 0, 0);
                    _world?.RemoveItem(item);
                    return true;
                }
                break;
            case SpellType.Dispel:
            case SpellType.MassDispel:
                if (item.ItemType == ItemType.Spell)
                {
                    if (IsTopLevelItem(item))
                        SendItemEffect(2, effectId, caster, item, 8, 20, false, 0, 0);
                    _world?.RemoveItem(item);
                    return true;
                }
                break;
            case SpellType.Bless:
            case SpellType.Curse:
                return false;
            case SpellType.Lightning:
                SendItemEffect(1, 0, caster, item, 0, 0, false, 0, 0);
                break;
            case SpellType.Explosion:
            case SpellType.Fireball:
            case SpellType.FireBolt:
            case SpellType.FireField:
            case SpellType.Flamestrike:
            case SpellType.MeteorSwarm:
                damageType = DamageType.Fire;
                break;
        }

        // "Potions should explode when hit (etc..)" (:5743).
        if (def.IsFlag(SpellFlag.Harm))
            ItemDamageEngine.OnTakeDamage(item, 1, caster, DamageType.Magic | damageType);

        if (effectId != 0 && !item.IsDeleted)
        {
            bool explode = def.IsFlag(SpellFlag.FxBolt) && !def.IsFlag(SpellFlag.Good);
            uint color = (uint)args.Locals.GetInt("EffectColor", 0);
            uint render = (uint)args.Locals.GetInt("EffectRender", 0);
            if (def.IsFlag(SpellFlag.FxBolt))
                SendItemEffect(0, effectId, caster, item, 5, 1, explode, color, render);
            if (def.IsFlag(SpellFlag.FxTarg))
                SendItemEffect(3, effectId, null, item, 0, 15, explode, color, render);
        }
        return true;
    }

    private static bool IsTopLevelItem(Item item) => !item.ContainedIn.IsValid && !item.IsEquipped;

    /// <summary>CObjBase::Effect on an item: 0 = a bolt from the source to it,
    /// 1 = lightning on it, 2 = at its location, 3 = on the item itself.</summary>
    private static void SendItemEffect(byte type, ushort effectId, Character? source, Item item,
        byte speed, byte loop, bool explode, uint color, uint render)
    {
        var at = item.GetTopLevelPosition();
        uint dst = item.Uid.Value;
        uint src = type == 0 && source != null ? source.Uid.Value : dst;
        var from = type == 0 && source != null ? source.Position : at;
        SphereNet.Network.Packets.PacketWriter fx = color != 0 || render != 0
            ? new SphereNet.Network.Packets.Outgoing.PacketEffectHued(type, src, dst, effectId,
                from.X, from.Y, from.Z, at.X, at.Y, at.Z, speed, loop, true, explode, color, render)
            : new SphereNet.Network.Packets.Outgoing.PacketEffect(type, src, dst, effectId,
                from.X, from.Y, from.Z, at.X, at.Y, at.Z, speed, loop, true, explode);
        Character.BroadcastNearby?.Invoke(at, 18, fx, 0);
    }

    /// <summary>The rune's @SpellEffect before a Recall or Gate Travel (Spell_Recall,
    /// CCharSpell.cpp:380-387): only ARGN1 = the spell is set, and only RETURN 0 means
    /// "handled, do not travel" - a RETURN 1 lets the spell go ahead.</summary>
    private bool RuneTravelHandledByScript(Character caster, Item rune, SpellDef def)
    {
        if (TriggerDispatcher == null)
            return false;
        var args = new TriggerArgs
        {
            CharSrc = caster,
            ItemSrc = rune,
            N1 = def.Id == SpellType.GateTravel ? (int)SpellType.GateTravel : (int)SpellType.Recall,
        };
        return TriggerDispatcher.FireItemTrigger(rune, ItemTrigger.SpellEffect, args) == TriggerResult.False;
    }

    /// <summary>Return true if the caster's backpack holds at least the needed
    /// amount of every reagent the spell requires. Reagents are identified by
    /// BaseId; stacked amounts contribute per-stack.</summary>
    /// <summary>True when the spell's bit is set in any accessible
    /// spellbook (equipped hands or top level of the backpack). Spellbook
    /// content uses the classic 64-bit mask in More1/More2.</summary>
    internal bool HasSpellInBook(Character caster, int spellId)
    {
        var book = caster.FindSpellbook(spellId);
        return book != null && book.ContainsSpell(spellId);
    }

    /// <summary>Whether the caster can pay the spell's reagent cost right now.
    /// Searches the whole reachable pack, sub-bags included: Source-X
    /// CContainer::ContentConsume recurses through every searchable container, and
    /// SphereNet's own gold and crafting counts already do. Looking only at the top
    /// level meant a player who tidied their reagents into a pouch was told they
    /// lacked them.</summary>
    /// <summary>LOWERREAGENTCOST is a percent CHANCE that this cast spends no
    /// reagents at all, not a discount on how many (Source-X
    /// Calc_SpellReagentsConsume, CResourceCalc.cpp:570). The roll wraps the
    /// availability check as well as the spend, so a free cast also cannot be
    /// refused for lacking them — and it is rolled afresh at each of the two
    /// points the reference calls that function from (fTest, then for real).</summary>
    private bool RollsFreeReagents(Character caster) =>
        _rand.Next(100) < GetCastingPropertyValue(
            caster, SpellCastingProperties.LowerReagentCost);

    /// <summary>Calc_SpellTithingCost (CResourceCalc.cpp:581-598): the spell's
    /// TITHINGUSE, owed only by a player casting from their own power while
    /// REAGENTSREQUIRED is on, and waived outright on a LOWERREAGENTCOST roll.</summary>
    private int SpellTithingCost(Character caster, SpellDef def, bool wand, bool scroll)
    {
        if (!Character.ReagentsRequiredEnabled || !caster.IsPlayer || wand || scroll)
            return 0;
        return RollsFreeReagents(caster) ? 0 : def.TithingCost;
    }

    /// <summary>Calc_SpellReagentsConsume's ResourceConsumePart(reagents, 1, 100, fTest)
    /// on the caster (CResourceCalc.cpp:573): the shared resource walk over everything
    /// carried - matched by definition, never reaching into a locked or banked box.
    /// Returns the index of the LAST reagent that fell short, or -1. A real spend takes
    /// what is there of each reagent even when another is short, as upstream does.</summary>
    private int ReagentsConsume(Character caster, SpellDef def, bool test)
    {
        if (def.Reagents.Count == 0)
            return -1;
        var list = def.Reagents
            .Select(kv => new Objects.Items.ResourceMatch.ResourceQty(kv.Key, kv.Value))
            .ToList();
        return Objects.Items.ResourceMatch.ResourceConsumePart(caster, list, 1, 100, test);
    }

    /// <summary>The fTest pass. The LOWERREAGENTCOST roll wraps it (a free cast cannot
    /// be refused for lacking reagents); returns the missing index or -1.</summary>
    private int MissingReagent(Character caster, SpellDef def, bool test)
    {
        if (def.Reagents.Count == 0 || RollsFreeReagents(caster))
            return -1;
        return ReagentsConsume(caster, def, test);
    }

    /// <summary>DEFMSG_SPELL_TRY_NOREGS naming the reagent upstream reports - the last
    /// one found short (CCharSpell.cpp:2495) - or "the reagent" when it has no
    /// definition.</summary>
    private void SendMissingReagentMessage(Character caster, SpellDef def, int missing)
    {
        if (missing < 0 || missing >= def.Reagents.Count)
            return;
        var rid = def.Reagents.Keys.ElementAt(missing);
        string? name = rid.Type == Core.Enums.ResType.ItemDef
            ? Definitions.DefinitionLoader.GetItemDef(rid.Index)?.Name
            : null;
        if (string.IsNullOrWhiteSpace(name))
            name = ServerMessages.Get(Msg.SpellTryThereg);
        OnSysMessage?.Invoke(caster, ServerMessages.GetFormatted(Msg.SpellTryNoregs, name));
    }

    /// <summary>AOS on-hit weapon proc (Source-X Fight_Hit → OnSpellEffect,
    /// CCharFight.cpp:2347-2360): applies the spell's effect directly on the
    /// victim — no cast time, mana or reagents — at the attacker's Magery.</summary>
    public void ApplyOnHitSpell(Character attacker, Character target, SpellType spell)
    {
        var def = GetSpellDef(spell);
        if (def == null)
            return;
        WithSourceItem(null, () => ApplyCharEffect(attacker, target, def, attacker.GetSkill(SkillType.Magery)));
    }

    /// <summary>Source-X character SPELLEFFECT verb: apply a spell immediately
    /// with the supplied skill level, without cast-time/resource checks.</summary>
    public bool ApplyScriptSpellEffect(Character caster, Character target, SpellType spell, int skillLevel)
    {
        var def = GetSpellDef(spell);
        if (def == null) return false;
        WithSourceItem(null, () => ApplyCharEffect(caster, target, def, Math.Clamp(skillLevel, 0, ushort.MaxValue)));
        return true;
    }

    /// <summary>A hit on a reflecting character spends its reflection: the
    /// LAYER_SPELL_Magic_Reflect memory is deleted (CCharSpell.cpp:3786-3803), which runs
    /// the ordinary removal - @SpellEffectRemove / [SPELL] @EffectRemove and the native
    /// flag clear. A flag a script raised without a memory is simply dropped.</summary>
    private void ConsumeMagicReflection(Character character)
    {
        var memory = FindEffect(character, m => MemSpell(m) == SpellType.MagicReflect);
        if (memory != null)
            DeleteEffectMemory(memory);
        else
            character.ClearStatFlag(StatFlag.Reflection);
    }

    /// <summary>Source-X CChar::OnSpellEffect entry for non-cast deliveries —
    /// potions (Use_Drink conveys the MORE1 spell at MORE2 strength), traps,
    /// scripted effects. Applies the spell's effect directly at the given
    /// strength with no cast time, mana, reagents or fizzle.
    /// <paramref name="sourceItem"/> is upstream's pSourceItem - the potion being
    /// drunk - which the target's @SpellEffect sees as ARGO.</summary>
    public void ApplyDirectEffect(Character source, Character target, SpellType spell, int strength,
        Item? sourceItem = null)
    {
        var def = _spells.Get(spell);
        if (def == null) return;
        WithSourceItem(sourceItem, () => ApplyCharEffect(source, target, def, Math.Max(0, strength)));
    }

    /// <summary>The iSkillLevel of the effect being applied (Source-X OnSpellEffect's
    /// argument): the caster's skill, or a potion's quality. Cure and poison read it.</summary>
    private int _effectSkillLevel;

    /// <summary>The ground point of the cast being resolved (Source-X m_Act_p),
    /// or null outside CastDone.</summary>
    private Point3D? _effectTargetPos;

    /// <summary>OnSpellEffect's pSourceItem for the delivery in progress: the wand or
    /// scroll of a cast, the potion of a drink, null for a plain cast or a scripted
    /// effect.</summary>
    private Item? _effectSourceItem;

    private bool WithSourceItem(Item? sourceItem, Func<bool> apply)
    {
        var outer = _effectSourceItem;
        _effectSourceItem = sourceItem;
        try { return apply(); }
        finally { _effectSourceItem = outer; }
    }

    /// <summary>The port of CChar::OnSpellEffect (CCharSpell.cpp:3606). Returns false
    /// when the spell did not take (dead target, a trigger refused it, reflected
    /// away...) - a field touch counts only an effect that landed.
    ///
    /// The order is upstream's: the target's @SpellEffect and the spell's @Effect stage
    /// run FIRST (:3712-3730), their readback - ARGN1 the spell, ARGN2 the level and the
    /// LOCAL contract - is taken (:3732-3740), and only then is the harm checked: harming
    /// oneself (:3750-3759), the aggression notice (:3777) and Magic Reflection
    /// (:3781-3811). A script that refuses the spell therefore neither makes it an attack
    /// nor spends anybody's reflection. A reflected spell re-enters here on the caster
    /// with <paramref name="reflecting"/> set (:3806), so the caster's own hooks see it.</summary>
    private bool ApplyCharEffect(Character caster, Character target, SpellDef def, int skillLevel,
        int durationTenths = 0, bool reflecting = false, bool sourceless = false)
    {
        if (skillLevel < 0)
            return false;                                                     // :3620
        if (target.IsDead && !def.IsFlag(SpellFlag.TargDead))
            return false;                                                     // :3622
        if (def.Id == SpellType.ParalyzeField && target.IsStatFlag(StatFlag.Freeze))
            return false;                                                     // :3624
        if (def.Id == SpellType.PoisonField && target.IsStatFlag(StatFlag.Poisoned))
            return false;                                                     // :3626

        bool harmful = IsHarmfulSpell(def);
        var sourceItem = _effectSourceItem;

        // Randomize potency (Source-X: iSkillLevel/2 + rand(iSkillLevel/2)); the
        // randomized level is what the rest of OnSpellEffect reads (:3631).
        int potency = skillLevel / 2 + _rand.Next(Math.Max(1, skillLevel / 2));
        int effect = def.GetEffect(potency);

        // A cast hands its duration down; a direct effect (SPELLEFFECT, potion,
        // field touch) works it out here at the randomized level (:3634).
        if (durationTenths <= 0)
            durationTenths = GetSpellDuration(def, potency, caster, target);

        // Mind Blast's INT arithmetic belongs to the cast (CastDoneCore); here it
        // is an ordinary damage spell at the level it was handed.
        // Spell damage bonuses exist only under MAGICF_OSIFORMULAS (:3669-3699):
        // EvalInt multiplies, then SDI (15 cap in PvP) + INT/10 + Inscription/100
        // add a percentage.
        if (def.IsFlag(SpellFlag.Damage) && IsMagicFlag(MagicConfigFlags.OsiFormulas))
            effect = ApplyOsiSpellDamageBonus(caster, target, effect);

        // Magic resist percent — computed BEFORE the trigger so a script can
        // read and override it (LOCAL.Resist), applied after. A potion is never
        // resisted (:3649).
        int resistPct = 0;
        bool potion = sourceItem is { ItemType: ItemType.Potion };
        if (def.IsFlag(SpellFlag.Resist) && caster != target && !potion && !sourceless)
            resistPct = CalcMagicResist(target, def, caster);

        // @SpellEffect — Source-X CChar::OnSpellEffect fires this on the
        // AFFECTED char (not the caster) with SRC = caster, ARGN1 = spell,
        // ARGN2 = skill level, ARGO = the source item, and the LOCAL contract
        // (Effect / Resist / Duration / Sound / DamageType / CreateObject1 /
        // Explode). RETURN 1 cancels the effect on this target; ARGN1 / ARGN2 and
        // the LOCALs are read back. The per-spell [SPELL] @EFFECT stage runs right
        // after with the same shared args, as in the reference.
        var spellId = def.Id;
        int level = potency;
        bool scripted = def.IsFlag(SpellFlag.Scripted);
        // LOCAL.DamageType: 0 leaves the spell's own default (CCharSpell.cpp:3734, :3826).
        var scriptDamageType = DamageType.None;
        if (TriggerDispatcher != null)
        {
            var fxLocals = new SphereNet.Scripting.Variables.VarMap();
            fxLocals.SetInt("DamageType", 0);
            fxLocals.SetInt("CreateObject1", def.EffectId);
            fxLocals.SetInt("Explode", 0);
            fxLocals.SetInt("Sound", def.Sound);
            fxLocals.SetInt("Effect", effect);
            fxLocals.SetInt("Resist", resistPct);
            fxLocals.SetInt("Duration", durationTenths);
            var fxArgs = new TriggerArgs
            {
                CharSrc = caster,
                N1 = (int)def.Id,
                N2 = potency,
                O1 = sourceItem,
                Locals = fxLocals,
            };
            // RETURN 1 refuses the effect; RETURN 0 on a SCRIPTED spell means the
            // script did it (:3714-3730).
            var charVerdict = TriggerDispatcher.FireCharTrigger(target, CharTrigger.SpellEffect, fxArgs);
            if (charVerdict == TriggerResult.True)
                return false;
            if (charVerdict == TriggerResult.False && scripted)
                return true;
            fxArgs.ReturnNumber = null;
            var stageVerdict = TriggerDispatcher.FireSpellTrigger(def.Id, "Effect", target, fxArgs);
            if (stageVerdict == TriggerResult.True)
                return false;
            if (scripted && (stageVerdict == TriggerResult.False || fxArgs.ReturnNumber == 0))
                return true;

            // spell = m_iN1 (:3732): a script may turn the effect into another spell.
            long readSpell = fxArgs.N1;
            if (readSpell > 0 && readSpell <= ushort.MaxValue)
                spellId = (SpellType)readSpell;
            level = (int)Math.Clamp(fxArgs.N2, int.MinValue, int.MaxValue);
            effect = (int)fxLocals.GetInt("Effect", effect);
            resistPct = (int)fxLocals.GetInt("Resist", resistPct);
            durationTenths = (int)Math.Clamp(fxLocals.GetInt("Duration", durationTenths), 0, int.MaxValue);
            scriptDamageType = (DamageType)unchecked((uint)fxLocals.GetInt("DamageType"));
        }

        // The definition the native switch dispatches on follows the read-back spell;
        // the flag checks below stay on the definition the effect arrived with, as
        // upstream keeps pSpellDef (:3732-3748).
        var effectDef = spellId == def.Id ? def : GetSpellDef(spellId) ?? def;

        if (harmful && !sourceless)
        {
            if (caster == target)
            {
                // Harming oneself: allowed for a reflected spell (with no disturb) and
                // otherwise only under MAGICF_CANHARMSELF (:3750-3759).
                if (!reflecting && !IsMagicFlag(MagicConfigFlags.CanHarmSelf))
                    return false;
            }
            else
            {
                // The victim learns it was attacked - memory, attacker list, an NPC
                // turns on the caster, and whether harming it was a crime (:3777).
                // The caster is revealed unless the spell is a field.
                if (!target.OnAttackedBy(caster, shouldReveal: !def.IsFlag(SpellFlag.Field)) && !reflecting)
                    return false;

                // Magic Reflect (:3781-3811): only a spell with a direct source
                // bounces. The reflecting side spends its memory; when the caster
                // reflects too (and may), its memory goes as well and the spell lands
                // where it was aimed; otherwise the caster's own reflection may soak
                // it up (MAGICF_DELREFLECTOWN), or the spell comes back on the caster.
                if (target.IsStatFlag(StatFlag.Reflection))
                {
                    ConsumeMagicReflection(target);
                    if (caster.IsStatFlag(StatFlag.Reflection) &&
                        !IsMagicFlag(MagicConfigFlags.NoReflectOwn))
                    {
                        ConsumeMagicReflection(caster);
                    }
                    else
                    {
                        if (caster.IsStatFlag(StatFlag.Reflection) &&
                            IsMagicFlag(MagicConfigFlags.DeleteReflectOwn))
                            ConsumeMagicReflection(caster);
                        else
                            ApplyCharEffect(caster, caster, effectDef, level, durationTenths, reflecting: true);
                        return true;
                    }
                }
            }
        }

        // A SCRIPTED spell does nothing native on a character (:3814).
        if (scripted)
            return true;

        if (spellId is SpellType.Lightning or SpellType.ChainLightning)
            _world.LightFlash(target.Position);                              // :4006-4010

        int prevSkillLevel = _effectSkillLevel;
        int? prevDuration = _effectDurationTenths;
        bool prevReflected = _effectReflected;
        _effectSkillLevel = level;
        _effectDurationTenths = durationTenths;
        _effectReflected = reflecting;
        try
        {
            ApplyCharEffectResolved(caster, target, def, effectDef, effect, resistPct, scriptDamageType);
        }
        finally
        {
            _effectDurationTenths = prevDuration;
            _effectSkillLevel = prevSkillLevel;
            _effectReflected = prevReflected;
        }
        return true;
    }

    /// <summary>MAGICF_OSIFORMULAS spell damage (CCharSpell.cpp:3669-3699): the
    /// effect is multiplied by EvalInt*3/1000 + 1, then raised by a percentage of
    /// INCREASESPELLDAM (at most 15 when both sides are players), INT/10 and
    /// Inscription/100.</summary>
    internal static int ApplyOsiSpellDamageBonus(Character caster, Character target, int effect)
    {
        effect *= caster.GetSkill(SkillType.EvalInt) * 3 / 1000 + 1;
        int bonus = SumCharAndEquipProperty(caster, "INCREASESPELLDAM");
        if (target.IsPlayer && caster.IsPlayer && bonus > 15)
            bonus = 15;
        bonus += caster.Int / 10;
        bonus += caster.GetSkill(SkillType.Inscription) / 100;
        return effect + effect * bonus / 100;
    }

    /// <summary>Post-trigger application: resist subtraction + the per-flag
    /// dispatch. Split out so the @SpellEffect duration override is scoped
    /// with try/finally around every effect it creates. <paramref name="def"/> is the
    /// definition the effect arrived with (its flags gate damage and heal, as upstream
    /// keeps pSpellDef); <paramref name="effectDef"/> is the spell the effect resolved
    /// to after @SpellEffect's ARGN1, which the native switch runs on (:3732, :3873).</summary>
    private void ApplyCharEffectResolved(Character caster, Character target, SpellDef def, SpellDef effectDef,
        int effect, int resistPct, DamageType scriptDamageType = DamageType.None)
    {
        // Sphere custom spells (1000+) with a native char handler dispatch by
        // id FIRST: their pack defs carry marker flags only — Hallucination
        // even carries spellflag_curse — so the generic flag branches would
        // mis-route them. Fire Bolt is NOT in this set: it is a plain damage
        // spell and takes the generic damage path below.
        if (HasNativeCustomCharHandler(effectDef.Id))
        {
            ApplySpecificSpell(caster, target, effectDef, effect);
            return;
        }

        // Damage spells. The damage lands first and the per-spell effect still
        // follows (Source-X's damage block is not an else of its switch, :3817-3873),
        // so a damage-flagged Paralyze Field still paralyzes.
        if (def.IsFlag(SpellFlag.Damage))
        {
            // The resist roll only ever reduces DAMAGE (:3819-3825); a curse's or
            // drain's effect is untouched by it.
            if (resistPct > 0)
                effect = Math.Max(0, effect - effect * resistPct / 100);
            var dmgType = scriptDamageType != DamageType.None
                ? scriptDamageType
                : GetSpellDamageType(effectDef.Id);
            // A spell reflected back onto its own caster does not break that
            // caster's next cast (DAMAGE_NODISTURB, CCharSpell.cpp:3750-3755).
            if (_effectReflected && target == caster)
                dmgType |= DamageType.NoDisturb;
            // The elemental split follows the type (:3857-3868): one element at 100%,
            // anything else physical.
            int splitPhysical = 0, splitFire = 0, splitCold = 0, splitPoison = 0, splitEnergy = 0;
            if ((dmgType & DamageType.Fire) != 0) splitFire = 100;
            else if ((dmgType & DamageType.Cold) != 0) splitCold = 100;
            else if ((dmgType & DamageType.Poison) != 0) splitPoison = 100;
            else if ((dmgType & DamageType.Energy) != 0) splitEnergy = 100;
            else splitPhysical = 100;

            // The spell's damage goes through the victim's damage entry (:3870), like a
            // swing or a script DAMAGE: protection, aggression, armour (the elemental
            // split, or pre-AOS armour halved against magic; MAGICF_IGNOREAR skips it),
            // @GetHit - whose RETURN 1 refuses the damage - unparalyze and
            // COMBAT_SLAYER from the spellbook. No Reactive Armour: that answers
            // physical blows only.
            int damage = CombatEngine.ApplyCharacterDamage(target, Math.Max(0, effect), caster, dmgType,
                splitPhysical, splitFire, splitCold, splitPoison, splitEnergy,
                spell: (int)effectDef.Id, feedback: DamageFeedback.Caller);

            if (damage > 0)
            {
                if ((dmgType & DamageType.NoDisturb) == 0)
                    TryInterruptFromDamage(target, damage, breakParalyze: false);

                // Victim feedback: spell damage used to apply silently — no
                // 0x0B damage number, no health-bar update — while the melee
                // and breath paths broadcast both. Without these the hit is
                // invisible until the victim dies.
                Character.BroadcastDamageNearby?.Invoke(target.Position, 18, target.Uid.Value, damage, 0);
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
            if (target.IsDead || target.IsDeleted)
                return;
        }

        // Heal spells. Noble Sacrifice carries spellflag_heal in the pack but
        // has a NATIVE handler (area heal+cure at the paladin's expense) —
        // the generic branch used to swallow it into a plain caster heal.
        if (def.IsFlag(SpellFlag.Heal) && effectDef.Id != SpellType.NobleSacrifice)
        {
            caster.FlagForHelpingCriminalIfNeeded(target);
            target.Hits = (short)Math.Min(target.Hits + effect, target.MaxHits);
        }
        // Buffs and curses by spell id, as Source-X OnSpellEffect switches on the
        // spell (CCharSpell.cpp:3874). Keying them on SPELLFLAG_BLESS/CURSE lost
        // whatever the pack flags differently: Clumsy carries no curse flag and did
        // nothing, Magic Reflection carries bless and died in the buff switch.
        else if (IsNativeBuff(effectDef.Id))
        {
            ApplyBuff(caster, target, effectDef, effect);
        }
        else if (IsNativeCurse(effectDef.Id))
        {
            ApplyCurse(caster, target, effectDef, effect);
        }
        // Specific spells
        else
        {
            ApplySpecificSpell(caster, target, effectDef, effect);
        }
    }

    private static bool IsNativeBuff(SpellType id) => id is
        SpellType.Strength or SpellType.Agility or SpellType.Cunning or SpellType.Bless or
        SpellType.Protection or SpellType.ArchProtection;

    private static bool IsNativeCurse(SpellType id) => id is
        SpellType.Weaken or SpellType.Clumsy or SpellType.Feeblemind or
        SpellType.Curse or SpellType.MassCurse;

    /// <summary>The radius an AREA spell sweeps when @Success left
    /// LOCAL.AreaRadius at 0 (Spell_CastDone, CCharSpell.cpp:3064-3080).</summary>
    internal static int DefaultAreaRadius(SpellType spell, int skillLevel) => spell switch
    {
        SpellType.ArchCure => 2,
        SpellType.ArchProtection => 3,
        SpellType.MassCurse => 2,
        SpellType.Reveal => 1 + skillLevel / 200,
        SpellType.ChainLightning => 2,
        SpellType.MassDispel => 8,
        SpellType.MeteorSwarm => 2,
        SpellType.Earthquake => 1 + skillLevel / 150,
        SpellType.PoisonStrike => 2,
        SpellType.Wither => 4,
        _ => 4,
    };

    /// <summary>Spell_Area (CCharSpell.cpp:2115): every character within the
    /// radius takes the spell - the caster too, unless it is harmful and
    /// MAGICF_CANHARMSELF is off.</summary>
    private void ApplyAreaEffect(Character caster, Point3D center, SpellDef def, int skillLevel, int radius)
    {
        bool harmful = IsHarmfulSpell(def);
        foreach (var target in _world.GetCharsInRange(center, radius).ToList())
        {
            if (target == caster &&
                ((harmful && !IsMagicFlag(MagicConfigFlags.CanHarmSelf)) || def.IsFlag(SpellFlag.TargNoSelf)))
                continue;
            if (target.IsDead && !def.IsFlag(SpellFlag.TargDead)) continue;

            // Spell_Area hands the duration over in the fReflecting slot, so each
            // target works its own out (:2140) - duration 0 here does the same.
            ApplyCharEffect(caster, target, def, skillLevel);
        }
    }

    /// <summary>Create a multi-tile field wall centred on the target location,
    /// oriented perpendicular to the cast direction (UO-style 5-tile wall).</summary>
    /// <summary>The classic field tile pair per spell (Source-X Spell_CastDone
    /// CreateObject1/CreateObject2 defaults): E/W art when the wall runs
    /// east-west, N/S art when it runs north-south.</summary>
    private static (ushort EW, ushort NS) FieldTiles(SpellType spell) => spell switch
    {
        SpellType.WallOfStone => (0x0080, 0x0080),   // ITEMID_STONE_WALL
        SpellType.FireField => (0x398C, 0x3996),     // ITEMID_FX_FIRE_F_EW/NS
        SpellType.PoisonField => (0x3915, 0x3920),   // ITEMID_FX_POISON_F_EW/NS
        SpellType.ParalyzeField => (0x3967, 0x3979), // ITEMID_FX_PARA_F_EW/NS
        SpellType.EnergyField => (0x3946, 0x3956),   // ITEMID_FX_ENERGY_F_EW/NS
        _ => (0, 0),
    };

    /// <summary>Source-X CChar::Spell_Field: a 5-tile wall perpendicular to
    /// the caster→target axis using the CLASSIC field art per spell (the
    /// pack's EFFECT_ID is the cast FX, not the field tile — Wall of
    /// Stone/Fire/Energy carry EFFECT_ID=0, which used to make the field
    /// items invisible). Duration comes from the spell's DURATION curve;
    /// barrier fields (stone wall, energy) refuse to materialise on top of a
    /// character; each segment records its spell for the typed step effect.</summary>
    private void CreateField(Character caster, Point3D pos, SpellDef def, int skillLevel,
        SpellSuccessStage stage)
    {
        int skill = skillLevel;

        // Orient the field across the caster->target axis (Spell_Field,
        // CCharSpell.cpp:2177-2179): only a strictly wider x offset lays it N-S.
        int dx = Math.Abs(pos.X - caster.X);
        int dy = Math.Abs(pos.Y - caster.Y);
        bool wallRunsNorthSouth = dx > dy;

        var (tileEW, tileNS) = FieldTiles(def.Id);
        ushort tileId = wallRunsNorthSouth
            ? (stage.CreateObject2 != 0 ? stage.CreateObject2 : tileNS)
            : (stage.CreateObject1 != 0 ? stage.CreateObject1 : tileEW);
        if (tileId == 0)
            tileId = def.EffectId; // custom scripted field spells keep EFFECT_ID

        // No duration from @Success: work it out as Spell_Field does (:2303).
        int durTenths = stage.DurationTenths > 0
            ? stage.DurationTenths : GetSpellDuration(def, skill, caster, caster);

        bool isBarrier = tileEW is 0x0080 or 0x3946 or 0x3956;   // stone wall / energy field art (:2277)

        // FieldWidth x FieldGauge, 3 x 1 unless @Success says otherwise (:3027-3031):
        // ix runs along the wall, iy across it (:2181-2185).
        int width = stage.FieldWidth > 0 ? stage.FieldWidth : 3;
        int gauge = stage.FieldGauge > 0 ? stage.FieldGauge : 1;
        int minX = (width - 1) / 2 - (width - 1);
        int maxX = minX + (width - 1);
        int minY = (gauge - 1) / 2 - (gauge - 1);
        int maxY = minY + (gauge - 1);

        Point3D TileAt(int ix, int iy) => wallRunsNorthSouth
            ? new Point3D((short)(pos.X + iy), (short)(pos.Y + ix), pos.Z, pos.Map)
            : new Point3D((short)(pos.X + ix), (short)(pos.Y + iy), pos.Z, pos.Map);

        // MAGICF_NOFIELDSOVERWALLS (:2187-2231): the field stops short of the first
        // blocked tile on each side, and a blocked centre cancels it. Without the
        // flag a field is laid over walls like Source-X does.
        var md = _world.MapData;
        if (IsMagicFlag(MagicConfigFlags.NoFieldsOverWalls) && md != null)
        {
            bool Blocked(int ix)
            {
                for (int iy = minY; iy <= maxY; iy++)
                {
                    var p = TileAt(ix, iy);
                    if (!md.IsPassable(p.Map, p.X, p.Y, p.Z))
                        return true;
                }
                return false;
            }
            if (Blocked(0))
                return;
            for (int ix = -1; ix >= minX; ix--)
                if (Blocked(ix)) { minX = ix + 1; break; }
            for (int ix = 1; ix <= maxX; ix++)
                if (Blocked(ix)) { maxX = ix - 1; break; }
        }

        for (int ix = minX; ix <= maxX; ix++)
        for (int iy = minY; iy <= maxY; iy++)
        {
            var tilePos = TileAt(ix, iy);
            int tx = tilePos.X, ty = tilePos.Y;

            // Direct cast on a creature (CCharSpell.cpp:2251-2284): every active
            // character on the tile not above the caster's plevel. A harmful field
            // is an attack on it (skipped when refused); unless the spell is
            // NOUNPARALYZE it loses its paralysis and its stuck hold; and a stone
            // wall or energy field casts itself on the character instead of
            // being laid on its tile.
            bool goodLoc = true;
            foreach (var ch in _world.GetCharsInRange(tilePos, 0).ToList())
            {
                if (ch.IsDeleted || ch.X != tx || ch.Y != ty || ch.IsLoggedOut)
                    continue;
                if (ch.PrivLevel > caster.PrivLevel)
                    continue;
                if (def.IsFlag(SpellFlag.Harm) && !ch.OnAttackedBy(caster))
                    continue;
                if (!def.IsFlag(SpellFlag.NoUnparalyze))
                {
                    RemoveMatchingEffects(ch, m => MemSpell(m) == SpellType.Paralyze);
                    if (ch.GetEquippedItem(Layer.FlagStuck) is { IsDeleted: false } stuck)
                        stuck.Delete();
                }
                if (isBarrier)
                {
                    WithSourceItem(null, () => ApplyCharEffect(caster, ch, def, skill));
                    goodLoc = false;
                    break;
                }
            }
            if (!goodLoc)
                continue;

            // Direct cast on an item (:2290-2301): MAGICF_OVERRIDEFIELDS deletes the
            // spell items already there; everything else takes the spell.
            foreach (var existing in _world.GetItemsInRange(tilePos, 0).ToList())
            {
                if (existing.IsDeleted || existing.X != tx || existing.Y != ty)
                    continue;
                if (existing.ItemType == ItemType.Spell && IsMagicFlag(MagicConfigFlags.OverrideFields))
                {
                    _world.RemoveItem(existing);
                    continue;
                }
                ItemOnSpellEffect(caster, existing, def, skill, null);
            }

            var fieldItem = _world.CreateItem();
            fieldItem.BaseId = tileId;
            fieldItem.Name = def.Name + " field";
            // A field segment is an IT_SPELL carrying its spell and the caster's
            // level (m_itSpell.m_spell / m_spelllevel), linked back to the caster
            // (:2306-2315); it can never be picked up.
            fieldItem.ItemType = ItemType.Spell;
            fieldItem.SetAttr(ObjAttributes.Move_Never);
            fieldItem.MoreP = new Point3D((short)def.Id, (short)Math.Clamp(skill, 0, short.MaxValue), 0, 0);
            fieldItem.Link = caster.Uid;
            if (stage.EffectColor != 0)
                fieldItem.Hue = new Color(stage.EffectColor);
            fieldItem.SetTag("FIELD_CASTER", caster.Uid.Value.ToString());
            fieldItem.SetTag("FIELD_CASTER_UUID", caster.Uuid.ToString("D"));
            fieldItem.SetTag("FIELD_SPELL", ((int)def.Id).ToString());

            long tileDur = durTenths;
            if (def.IsFlag(SpellFlag.FieldRandomDecay) && tileDur > 1)
                tileDur += _rand.NextInt64(tileDur / 2);                    // :2317
            fieldItem.SetDecayAt(Environment.TickCount64 + tileDur * 100L);
            if (!_world.PlaceItem(fieldItem, tilePos))
                _world.RemoveItem(fieldItem);
        }
    }

    // Harming an innocent player with a field is a crime: the touch runs through
    // ApplyCharEffect, whose harmful branch applies the same notoriety rule.

    /// <summary>Typed field step/stand effect (Source-X: walking into or
    /// standing in a field triggers the field's spell).
    ///
    /// <see cref="FieldTouchResult.NotHandled"/> lets the caller's legacy
    /// flat-damage path run (script-made fields with only FIELD_DAMAGE). The
    /// other two answers both mean "consumed here", and differ only in whether an
    /// effect actually landed — which is what caps the number of spell fields one
    /// step may set off.</summary>
    public FieldTouchResult ApplyFieldTouch(Character ch, Item field)
    {
        if (ch.IsDead) return FieldTouchResult.Handled;
        // A spell-made field says what it is in its tags. An item that is a fire or a
        // spell by TYPE - a fire pit, lava, a script's t_spell - says it the way
        // upstream reads it (CheckLocationEffects, CCharAct.cpp:4974): IT_FIRE burns
        // at its heat level (MOREY), IT_SPELL casts MOREX at level MOREY on behalf of
        // its LINK. Only the tagged kind did anything, so fire pits and scripted
        // fields were harmless to walk through.
        int? spellLevel = null;
        Character? caster = null;
        if (!field.TryGetTag("FIELD_SPELL", out string? fsStr) ||
            !ScriptNumber.TryParseInt(fsStr, out int fsId))
        {
            if (field.ItemType == ItemType.Fire)
                return ApplyHeat(ch, field);
            if (field.ItemType != ItemType.Spell || field.MoreP.X <= 0)
                return FieldTouchResult.NotHandled;
            fsId = field.MoreP.X;
            spellLevel = Math.Clamp((int)field.MoreP.Y, 0, 1000);
            if (field.Link.IsValid)
                caster = _world.FindChar(field.Link);
        }

        if (field.TryGetTag("FIELD_CASTER", out string? cStr) && ScriptNumber.TryParseUInt(cStr, out uint cuid))
            caster = _world.FindChar(new Serial(cuid));

        var spellType = (SpellType)fsId;

        // Source-X runs a field touch through OnSpellEffect, whose harmful branch
        // turns an invulnerable target away BEFORE anything is applied, and answers
        // false so the cap below stays clear for the next field
        // (CCharSpell.cpp:3762). Fire tested its own immunity; poison and paralyze
        // did not, so an invulnerable character took no damage and yet was still
        // poisoned - timer, source UID and status notification and all - and still
        // frozen. Both field defs carry SPELLFLAG_HARM in the reference pack, so
        // the flag is read from the def rather than the type being listed here.
        if (ch.IsStatFlag(StatFlag.Invul) &&
            (_spells.Get(spellType)?.IsFlag(SpellFlag.Harm) ?? false))
            return FieldTouchResult.Handled;

        var def = _spells.Get(spellType);
        if (def == null)
            return FieldTouchResult.NotHandled;

        // The level the field was laid at (m_itSpell.m_spelllevel): MOREY of an
        // engine-made field, the older FIELD_POISON_SKILL tag, else the caster's
        // current skill for a field that recorded none.
        int level = spellLevel
            ?? (field.MoreP.X == fsId ? Math.Clamp((int)field.MoreP.Y, 0, 1000)
            : field.TryGetTag("FIELD_POISON_SKILL", out string? skillStr) && ScriptNumber.TryParseInt(skillStr, out int tagged)
                ? tagged
                : caster?.GetSkill(def.GetPrimarySkill()) ?? 0);

        // A touch is OnSpellEffect(the field's spell, its LINK, its level) - the
        // same effect the spell has when cast (CCharAct.cpp:4994-5006), not a
        // fixed paralyze or a flat burn. It counts toward the one-spell-per-step
        // cap only when it actually landed.
        // A field whose LINK is gone has no pCharSrc upstream: nobody to resist, to
        // reflect onto or to be harming himself.
        bool hit = WithSourceItem(field, () =>
            ApplyCharEffect(caster ?? ch, ch, def, level, sourceless: caster == null));
        return hit ? FieldTouchResult.SpellHit : FieldTouchResult.Handled;
    }


    /// <summary>Upstream IT_FIRE on a location check (CCharAct.cpp:4974): the heat
    /// level (MOREY, 0-1000) rolled between half and full, halved for a flyer, is the
    /// skill a Fire Field effect is taken at - fire damage with no attacker, and the
    /// fire noise when it hurts. Not a spell hit: it does not use up the one spell a
    /// step may set off.</summary>
    private FieldTouchResult ApplyHeat(Character ch, Item fire)
    {
        if (ch.IsStatFlag(StatFlag.Invul) || CombatEngine.IsDamageImmune(ch))
            return FieldTouchResult.Handled;
        int heat = Math.Clamp((int)fire.MoreP.Y, 0, 1000);
        int level = heat / 2 + _rand.Next(heat - heat / 2 + 1);
        if (ch.IsStatFlag(StatFlag.Fly))
            level /= 2;
        int dmg = GetSpellDef(SpellType.FireField)?.GetEffect(level) ?? 0;
        if (dmg <= 0)
            return FieldTouchResult.Handled;
        // OnTakeDamage(effect, nullptr, DAMAGE_FIRE|DAMAGE_GENERAL, 0,100,0,0,0)
        // (CCharAct.cpp:4981): the field's fire goes through the damage entry.
        dmg = CombatEngine.ApplyCharacterDamage(ch, dmg, null,
            DamageType.Fire | DamageType.General, 0, 100, 0, 0, 0,
            spell: (int)SpellType.FireField, feedback: DamageFeedback.Caller);
        if (dmg <= 0)
            return FieldTouchResult.Handled;
        OnPlaySound?.Invoke(ch.Position, 0x015F);
        TryInterruptFromDamage(ch, dmg);
        if (ch.Hits <= 0 && !ch.IsDead)
        {
            if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, null);
            else ch.Kill();
        }
        return FieldTouchResult.Handled;
    }

    /// <summary>Build the summon a completed cast calls for, before any of its cost
    /// is taken. Returns null when it may not be summoned, having already said why.
    ///
    /// Source-X orders it this way deliberately: Spell_Summon_Try runs at
    /// CCharSpell.cpp:3002 and the consumption only at :3010.</summary>
    private Character? PrepareSummon(Character caster, Point3D targetPos, SpellDef def,
        SpellType spell, int skillLevel, ushort createObject1 = 0, int durationTenths = 0,
        int followerSlotsOverride = -1)
    {
        // sm_summon menu pick stashed on the caster (Source-X
        // m_atMagery.m_uiSummonID): the COMPLETED cast conveys the chosen
        // creature — the menu no longer bypasses mana/reagents/cast time.
        string? summonSel = null;
        if (caster.TryGetTag("SUMMON_SELECT", out string? sel) &&
            !string.IsNullOrWhiteSpace(sel))
            summonSel = sel.Trim();
        caster.RemoveTag("SUMMON_SELECT");

        ushort summonBody = spell switch
        {
            SpellType.SummonFamiliar => 0x013D,
            SpellType.BladeSpirit => 0x023E,     // CREID_BLADE_SPIRIT
            SpellType.EnergyVortex => 0x00A4,    // CREID_ENERGY_VORTEX
            SpellType.AirElemental => 0x000D,    // CREID_AIR_ELEM
            SpellType.EarthElemental => 0x000E,
            SpellType.FireElemental => 0x000F,
            SpellType.WaterElemental => 0x0010,
            SpellType.SummonDaemon => 0x0009,    // CREID_DEMON
            SpellType.RisingColossus => 0x033D,  // CREID_RISING_COLOSSUS
            SpellType.AnimatedWeapon => 0x02B4,  // CREID_ANIMATED_WEAPON
            // Sphere custom Summon Undead: 1/15 lich, 4/15 skeleton,
            // else zombie (Source-X CCharSpell.cpp:2591).
            SpellType.SummonUndead => _rand.Next(15) switch
            {
                1 => (ushort)0x0018,             // CREID_LICH
                3 or 5 or 7 or 9 => (ushort)0x0032, // CREID_SKELETON
                _ => (ushort)0x0003,             // CREID_ZOMBIE
            },
            _ => 0,
        };
        // @Success LOCAL.CreateObject1 overrides the creature (Spell_Summon_Try,
        // CCharSpell.cpp:2552, fed from :2987).
        if (createObject1 != 0)
            summonBody = createObject1;
        return SummonCreature(caster, targetPos, def, skillLevel, summonBody, summonSel, durationTenths,
            followerSlotsOverride);
    }

    /// <summary>Take back a summon whose cast could not be paid for after all - the
    /// reference deletes it on the same failure (CCharSpell.cpp:3012).</summary>
    private void DiscardSummon(Character? summoned)
    {
        if (summoned == null) return;
        summoned.ClearOwnership(clearFriends: true);
        _world.DeleteObject(summoned);
    }

    /// <summary>The last step of Spell_Summon_Place (CCharSpell.cpp:368): whatever spell
    /// called it, the creature takes the generic SPELL_Summon effect from its summoner at
    /// the summoner's skill in that spell, for the summoning's duration - its LAYER_SPELL_Summon
    /// memory, with @SpellEffect / @EffectAdd, whose removal (expiry, dispel, REMOVE) makes
    /// the creature vanish (:589). The SUMMON_* tags stay beside it: a save that has only
    /// them keeps its lifetime, and whichever ends first deletes the creature once.</summary>
    private void PlaceSummonEffect(Character caster, Character? creature, SpellDef castDef)
    {
        if (creature == null || creature.IsDeleted)
            return;
        var summonDef = _spells.Get(SpellType.SummonCreature);
        if (summonDef == null)
            return;                                 // OnSpellEffect needs the definition
        int duration = creature.TryGetTag("SUMMON_DURATION", out string? raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? Math.Max(0, d) : 0;
        int skill = caster.GetSkill(castDef.GetPrimarySkill());
        WithSourceItem(null, () => ApplyCharEffect(caster, creature, summonDef, skill, duration));
    }

    /// <summary>Summon a creature at target location.</summary>
    private Character? SummonCreature(Character caster, Point3D pos, SpellDef def, int skillLevel,
        ushort bodyId = 0, string? defName = null, int durationTenths = 0,
        int followerSlotsOverride = -1)
    {
        // MAGICF_SUMMONWALKCHECK (Source-X CCharSpell.cpp:2646): the creature has
        // to be able to STAND where it is called. Without it a summon lands inside
        // a wall or over water and is stuck there. The flag was in the enum and in
        // the ini and nothing read it.
        if (IsMagicFlag(MagicConfigFlags.SummonWalkCheck) &&
            caster.PrivLevel < PrivLevel.GM &&
            _world.MapData is { } summonMap &&
            !summonMap.IsPassable(pos.Map, pos.X, pos.Y, pos.Z))
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.MsgSummonInvalidtarg));
            return null;
        }

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
                return null;
            }
        }

        var creature = _world.CreateCharacter();
        creature.Name = def.Name;
        creature.NpcBrain = NpcBrainType.Monster;
        // A specific creature graphic (necro summons pass one); a conjured summon
        // is dispellable (reference STATF_Conjured).
        if (bodyId != 0)
        {
            creature.BodyId = bodyId;
            creature.BaseId = bodyId;
            // CreateNPC runs NPC_LoadScript, which guesses the brain from the body
            // before the chardef's NPC= applies (GetNPCBrainAuto, CCharNPC.cpp:272).
            creature.NpcBrain = creature.GetNpcBrainAuto();
            creature.SetStatFlag(StatFlag.Conjured);

            // Source-X summons take the creature's own chardef (NPC_LoadScript
            // runs its @Create for stats/skills) — never flat invented numbers.
            var cdef = Definitions.DefinitionLoader.GetCharDef(bodyId);
            if (cdef != null)
            {
                creature.CharDefIndex = bodyId;
                if (!string.IsNullOrWhiteSpace(cdef.Name))
                    creature.Name = Definitions.DefinitionLoader.ResolveNamePool(cdef.Name);
                if (cdef.NpcBrain != NpcBrainType.None)
                    creature.NpcBrain = cdef.NpcBrain;
                Definitions.CharDefHelper.AfterApplyDefName?.Invoke(creature); // chardef @Create
                creature.Hits = creature.MaxHits;
                creature.Stam = creature.MaxStam;
                creature.Mana = creature.MaxMana;
            }
        }

        // A menu pick has to be applied BEFORE the follower cap is weighed. Source-X
        // creates the summon from the chosen id - CreateBasic(m_atMagery.m_uiSummonID),
        // CCharSpell.cpp:2640 - and only then measures it with GetFollowerSlots()
        // (:2662). Applying the pick afterwards meant the cap saw the placeholder's
        // single slot, so a five-slot creature walked through a one-slot allowance and
        // left the owner permanently over their limit.
        if (defName != null)
        {
            var res = Definitions.DefinitionLoader.StaticResources;
            if (res != null &&
                Definitions.CharDefHelper.TryApplyDefName(creature, defName, res, refresh: false, fireCreate: true))
            {
                creature.SetStatFlag(StatFlag.Conjured);
                creature.Hits = creature.MaxHits;
                creature.Stam = creature.MaxStam;
                creature.Mana = creature.MaxMana;
            }
        }

        // The cast's iDuration (GetSpellDuration, then @Success LOCAL.Duration).
        int duration = durationTenths > 0 ? durationTenths : GetSpellDuration(def, skillLevel, caster, caster);
        // @Success LOCAL.FollowerSlotsOverride: the slots the summon is weighed and
        // counted at instead of its own GetFollowerSlots() (Spell_Summon_Try,
        // CCharSpell.cpp:2662). The owner's follower total is a live scan of each
        // pet's slots here, so the override is carried on the summon itself for as
        // long as it serves, rather than added once to a stored counter.
        if (followerSlotsOverride >= 0)
            creature.TrySetProperty("FOLLOWERSLOTS",
                followerSlotsOverride.ToString(CultureInfo.InvariantCulture));
        if (!creature.TryAssignOwnership(caster, caster, summoned: true, enforceFollowerCap: true))
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.PetslotsTrySummon));
            _world.DeleteObject(creature);
            return null;
        }
        creature.SetTag("SUMMON_DURATION", duration.ToString());
        creature.SetTag("SUMMON_MASTER", caster.Uid.Value.ToString());
        creature.SetTag("SUMMON_MASTER_UUID", caster.Uuid.ToString("D"));
        creature.SetTag("SUMMON_EXPIRE_TICK", (Environment.TickCount64 + duration * 100L).ToString());

        if (!_world.PlaceCharacter(creature, pos))
        {
            creature.ClearOwnership(clearFriends: true);
            _world.DeleteObject(creature);
            return null;
        }
        return creature;
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
        // A flat percent check, not the bell curve (Skill_UseQuick(..., true, false),
        // CCharSpell.cpp:3659).
        return SkillEngine.UseQuick(target, SkillType.MagicResistance, chance,
            useBellCurve: false) ? 25 : 0;
    }

    /// <summary>Source-X resist chance (CCharSpell.cpp:3650-3658), in percent:
    /// resist = MR/10; max(resist/5, resist - ((magery-200)/50 + (1 + spell/8)*50)),
    /// the second term floored at 0. The old form worked on the 0-1000 skill and
    /// divided the result by 30, so a GM resister against a first-circle spell got
    /// the wrong figure and then rolled it on the bell curve.</summary>
    internal static int CalcResistChance(int resistSkill, int casterMagery, int spellId)
    {
        int resist = resistSkill / 10;
        int first = resist / 5;
        int threshold = ((casterMagery - 200) / 50) + (1 + spellId / 8) * 50;
        int second = resist >= threshold ? resist - threshold : 0;
        return Math.Max(first, second);
    }

    /// <summary>Get damage type for spell.</summary>
    /// <summary>OnSpellEffect's default damage types (CCharSpell.cpp:3826-3855):
    /// Magic Arrow and the fire spells burn, Harm and Mind Blast freeze, the
    /// lightning family is energy, and anything else is DAMAGE_GENERAL, which the
    /// elemental split counts as physical (:3867).</summary>
    private static DamageType GetSpellDamageType(SpellType spell) => spell switch
    {
        SpellType.MagicArrow or SpellType.Fireball or SpellType.FireField or
        SpellType.Explosion or SpellType.Flamestrike or SpellType.MeteorSwarm or
        SpellType.FireBolt => DamageType.Magic | DamageType.Fire | DamageType.NoReveal,

        SpellType.Harm or SpellType.MindBlast => DamageType.Magic | DamageType.Cold | DamageType.NoReveal,

        SpellType.Lightning or SpellType.EnergyBolt or SpellType.ChainLightning =>
            DamageType.Magic | DamageType.Energy | DamageType.NoReveal,

        _ => DamageType.Magic | DamageType.General | DamageType.NoReveal,
    };

    /// <summary>Strength / Agility / Cunning / Bless and the Protection wards: OnSpellEffect
    /// equips the memory at the spell's EFFECT (CCharSpell.cpp:3884, :3939) and the stat
    /// change itself is the memory's Spell_Effect_Add - after the add hooks, on the
    /// memory's own MOREY (:1549-1614), so a script that rewrites the level is obeyed.</summary>
    private void ApplyBuff(Character caster, Character target, SpellDef def, int effect)
    {
        switch (def.Id)
        {
            case SpellType.Strength:
            case SpellType.Agility:
            case SpellType.Cunning:
            case SpellType.Bless:
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.Protection:
            case SpellType.ArchProtection:
                ApplyProtectionWard(caster, target, def, effect);
                break;
        }
    }

    /// <summary>LAYER_SPELL_Protection: the ward's level adds to AR (CalcArmorDefense,
    /// CCharFight.cpp:553) - it does not set STATF_ARCHERCANMOVE.</summary>
    private void ApplyProtectionWard(Character caster, Character target, SpellDef def, int effect) =>
        CreateTimedEffect(caster, target, def, Math.Max(0, effect));

    /// <summary>Weaken / Clumsy / Feeblemind / Curse / Mass Curse. The previous memory on
    /// the stats layer is removed first (Spell_Effect_Create, :2056-2081) and only then
    /// does the new one's Spell_Effect_Add take its penalty off the ADJUSTED stat, never
    /// below 1 (_CheckLimitEffectStat, :1373-1388) - so a recast lands where the first
    /// cast did, and a stat already lowered by a modifier is not driven to zero.</summary>
    private void ApplyCurse(Character caster, Character target, SpellDef def, int effect)
    {
        switch (def.Id)
        {
            case SpellType.Weaken:
            case SpellType.Clumsy:
            case SpellType.Feeblemind:
            case SpellType.Curse:
            // Mass Curse is the area variant; it is flagged Curse and routes here,
            // so it applies the same all-stat penalty per target.
            case SpellType.MassCurse:
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
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

    /// <summary>A corpse targeted by Animate Dead / Bone Armor must be a
    /// TOP-LEVEL world item (Source-X pCorpse-&gt;IsTopLevel()) on the caster's
    /// map within casting range — a known UID inside a container or across
    /// the world must not be consumable.</summary>
    private static bool CanReachCorpse(Character caster, Item corpse) =>
        !corpse.ContainedIn.IsValid &&
        corpse.Position.Map == caster.MapIndex &&
        caster.Position.GetDistanceTo(corpse.Position) <= 12;

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

        // PRIV_JAILED and Gate Travel: Spell_CreateGate refuses a jailed caster
        // outright, wherever the rune points (CCharSpell.cpp:265-276). Recall goes
        // through Spell_Teleport, which instead sends the prisoner back to the
        // cell - see RedirectJailedTravel below.
        if (def.Id == SpellType.GateTravel &&
            caster.PrivLevel < Core.Enums.PrivLevel.GM && caster.IsJailed)
        {
            SendJailedTravelMessage(caster);
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
            // Source-X anti-magic sub-flags (REGION_ANTIMAGIC_*): RECALL_OUT blocks
            // leaving by recall, RECALL_IN (the Recall flag) blocks arriving by
            // recall/gate, GATE blocks gate travel. The legacy numeric region
            // properties (RECALLOUT/RECALLIN/GATEOUT/GATEIN = 0) still work too.
            if (def.Id == SpellType.Recall)
            {
                if (srcRegion != null && (srcRegion.IsFlag(RegionFlag.RecallOut) ||
                    (srcRegion.TryGetProperty("RECALLOUT", out string? rOut) && rOut == "0")))
                { OnSysMessage?.Invoke(caster, "You cannot recall from here."); return; }
                if (destRegion != null && (destRegion.IsFlag(RegionFlag.Recall) ||
                    (destRegion.TryGetProperty("RECALLIN", out string? rIn) && rIn == "0")))
                { OnSysMessage?.Invoke(caster, "You cannot recall to that location."); return; }
            }
            if (def.Id == SpellType.GateTravel)
            {
                if (srcRegion != null && (srcRegion.IsFlag(RegionFlag.Gate) ||
                    (srcRegion.TryGetProperty("GATEOUT", out string? gOut) && gOut == "0")))
                { OnSysMessage?.Invoke(caster, "You cannot open a gate here."); return; }
                if (destRegion != null && (destRegion.IsFlag(RegionFlag.Recall) || destRegion.IsFlag(RegionFlag.Gate) ||
                    (destRegion.TryGetProperty("GATEIN", out string? gIn) && gIn == "0")))
                { OnSysMessage?.Invoke(caster, "You cannot gate to that location."); return; }
            }
        }

        if (_world.GetSector(dest) == null)
        {
            OnSysMessage?.Invoke(caster, "That location is unreachable.");
            return;
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
            // Spell_Teleport's PRIV_JAILED check runs after its anti-magic checks
            // (CCharSpell.cpp:146-174), so it comes last here too.
            if (!RedirectJailedTravel(caster, ref dest))
                return;
            byte oldMap = caster.MapIndex;
            if (_world.MoveCharacter(caster, dest))
                OnSpellTeleport?.Invoke(caster, dest, oldMap);
            return;
        }

        CreateGate(caster, def, dest);
    }

    private void SendJailedTravelMessage(Character caster) =>
        OnSysMessage?.Invoke(caster, ServerMessages.Get(
            _rand.Next(2) == 0 ? Msg.SpellTeleJailed1 : Msg.SpellTeleJailed2));

    /// <summary>The PRIV_JAILED part of Spell_Teleport (CCharSpell.cpp:146-174): a
    /// jailed non-GM whose destination lies outside the "jail" region is told so and
    /// sent to the anchor of the jail region instead - "jail{JailCell}" when the
    /// account names a cell, else "jail". The move is NOT refused. Returns false only
    /// when that jail region does not exist, where Source-X ends up with an invalid
    /// point (InitPoint) and moves nowhere.</summary>
    private bool RedirectJailedTravel(Character caster, ref Point3D dest)
    {
        if (caster.PrivLevel >= Core.Enums.PrivLevel.GM || !caster.IsJailed)
            return true;
        var jail = _world.FindRegionByName("jail");
        if (jail != null && jail.Contains(dest))
            return true;

        SendJailedTravelMessage(caster);
        int cell = caster.JailCell;
        if (cell != 0)
            jail = _world.FindRegionByName($"jail{cell}");
        if (jail?.RepresentativePoint is not { } cellPoint)
            return false;
        dest = cellPoint;
        return true;
    }

    /// <summary>The port of Spell_CreateGate (CCharSpell.cpp:250-339): always two
    /// linked IT_TELEPAD gates, one here and one at the mark, lasting the DURATION
    /// curve at 0 skill; the art is EFFECT_ID, else blue into a safe region and red
    /// into an unsafe one. A ship is never a destination, and a gate already on
    /// either spot refuses a second.</summary>
    private void CreateGate(Character caster, SpellDef def, Point3D dest)
    {
        var here = caster.Position;
        var srcRegion = _world.FindRegion(here);
        var destRegion = _world.FindRegion(dest);
        if (caster.PrivLevel < PrivLevel.GM)
        {
            if (destRegion != null && destRegion.IsFlag(RegionFlag.Ship))
            {
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGateSomethingblocking));
                return;
            }
            if (HasTelepadAt(here) || HasTelepadAt(dest))
            {
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGateAlreadythere));
                return;
            }
        }

        long durationMs = (long)def.GetDuration(0) * 100L;
        const RegionFlag safeFlags = RegionFlag.Safe | RegionFlag.Guarded | RegionFlag.NoPvP;
        ushort idOrig = def.EffectId, idDest = def.EffectId;
        if (idOrig == 0)
        {
            // ITEMID_MOONGATE_BLUE 0x0F6C / ITEMID_MOONGATE_RED 0x0DDA; each gate
            // shows how safe the place it LEADS to is.
            idOrig = destRegion != null && destRegion.IsFlag(safeFlags) ? (ushort)0x0F6C : (ushort)0x0DDA;
            idDest = srcRegion != null && srcRegion.IsFlag(safeFlags) ? (ushort)0x0F6C : (ushort)0x0DDA;
        }

        Item MakeGate(ushort graphic, Point3D leadsTo)
        {
            var g = _world.CreateItem();
            g.BaseId = graphic;
            g.ItemType = ItemType.Telepad;
            g.Name = "moongate";
            g.SetAttr(ObjAttributes.Move_Never);
            g.More1 = caster.Uid.Value;
            g.MoreP = leadsTo;
            return g;
        }

        var gateOrig = MakeGate(idOrig, dest);
        var gateDest = MakeGate(idDest, here);
        gateOrig.Link = gateDest.Uid;
        gateDest.Link = gateOrig.Uid;
        foreach (var (gate, at) in new[] { (gateOrig, here), (gateDest, dest) })
        {
            if (durationMs > 0)
                gate.SetDecayAt(Environment.TickCount64 + durationMs);
            if (!_world.PlaceItem(gate, at))
                _world.RemoveItem(gate);
            else if (def.Sound > 0)
                OnPlaySound?.Invoke(at, (ushort)def.Sound);
        }

        OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGateOpen));
    }

    private bool HasTelepadAt(Point3D p)
    {
        foreach (var item in _world.GetItemsInRange(p, 0))
        {
            if (!item.IsDeleted && item.ItemType == ItemType.Telepad &&
                item.X == p.X && item.Y == p.Y && item.Position.Map == p.Map)
                return true;
        }
        return false;
    }

    /// <summary>True when the spell sits in the unimplemented-school id space
    /// (201..999: Chivalry/Bushido/Ninjitsu/Spellweaving/Mysticism/masteries)
    /// AND its def provides no behaviour at all, so casting it could only
    /// consume resources and no-op.</summary>
    /// <summary>Flags the effect dispatcher can actually act on. A school
    /// spell whose def carries only marker flags (playeronly, fx, harm, targ)
    /// would cast, consume mana/reagents and silently no-op — Source-X too
    /// damages only on SPELLFLAG_DAMAGE (CCharSpell.cpp:3817), so such a def
    /// is inert there as well.</summary>
    /// <summary>A spell that harms its target. Source-X keys the criminal
    /// marking, Magic Reflect bounce and REGION_ANTIMAGIC_DAMAGE suppression
    /// all off SPELLFLAG_HARM alone (CCharSpell.cpp OnSpellEffect,
    /// CRegion.cpp CheckAntiMagic) — pack spells like Poison, Paralyze and
    /// Mana Vampire carry HARM without DAMAGE/CURSE. Damage/Curse stay in
    /// the union for defs registered without the marker flag.</summary>
    private static bool IsHarmfulSpell(SpellDef def) =>
        (def.Flags & (SpellFlag.Harm | SpellFlag.Damage | SpellFlag.Curse)) != 0;

    /// <summary>Whether a region refuses this spell — the port of Source-X
    /// CRegion::CheckAntiMagic (CRegion.cpp:720). The reference asks this ONE
    /// question of the caster's own area, and its answer covers more than the two
    /// flags this engine was reading:
    /// <list type="bullet">
    /// <item>ANTIMAGIC_ALL refuses everything;</item>
    /// <item>RECALL_IN — <b>and a SHIP region</b> — refuse Mark and Gate Travel,
    /// which is why you cannot mark a rune aboard a boat;</item>
    /// <item>RECALL_OUT refuses Recall, Gate Travel AND Mark;</item>
    /// <item>GATE refuses Gate Travel, TELEPORT refuses Teleport;</item>
    /// <item>DAMAGE refuses anything flagged harmful.</item>
    /// </list>
    /// Mark was ungated here altogether, so a rune could be marked anywhere.</summary>
    public static bool RegionBlocksSpell(World.Regions.Region region, SpellDef def)
    {
        const RegionFlag anyMagicFlag =
            RegionFlag.Ship | RegionFlag.NoMagic | RegionFlag.Recall | RegionFlag.RecallOut |
            RegionFlag.Gate | RegionFlag.NoTeleport | RegionFlag.NoMagicDamage;
        if (!region.IsFlag(anyMagicFlag))
            return false;

        if (region.IsFlag(RegionFlag.NoMagic))
            return true;

        if (region.IsFlag(RegionFlag.Recall | RegionFlag.Ship) &&
            def.Id is SpellType.Mark or SpellType.GateTravel)
            return true;

        if (region.IsFlag(RegionFlag.RecallOut) &&
            def.Id is SpellType.Recall or SpellType.GateTravel or SpellType.Mark)
            return true;

        if (region.IsFlag(RegionFlag.Gate) && def.Id == SpellType.GateTravel)
            return true;

        if (region.IsFlag(RegionFlag.NoTeleport) && def.Id == SpellType.Teleport)
            return true;

        return region.IsFlag(RegionFlag.NoMagicDamage) && IsHarmfulSpell(def);
    }

    private static bool HasActionableFlags(SpellDef def) =>
        // Only flags the engine has a GENERIC handler for. Scripted counts
        // via HasScriptedStages (a bare SCRIPTED flag with no ON= body is
        // dead); Poly and Tick have per-spell handlers only, so a school def
        // carrying just those would still no-op after burning resources.
        (def.Flags & (SpellFlag.Damage | SpellFlag.Heal | SpellFlag.Bless |
                      SpellFlag.Curse | SpellFlag.Field | SpellFlag.Summon |
                      SpellFlag.Area)) != 0 ||
        // A spell LAYER equips a timed memory through the generic fallback.
        def.Layer >= SpellLayers.Stats;

    /// <summary>School spells with a native SphereNet handler (dispatched in
    /// ApplySpecificSpell / the travel route) — always castable.</summary>
    private static bool HasNativeSchoolHandler(SpellType id) => id is
        SpellType.CleanseByFire or SpellType.CloseWounds or SpellType.DispelEvil or
        SpellType.DivineFury or SpellType.HolyLight or SpellType.NobleSacrifice or
        SpellType.RemoveCurse or SpellType.SacredJourney or SpellType.GiftOfRenewal or
        SpellType.ReaperForm or SpellType.StoneForm;

    /// <summary>Sphere custom spells (1000+) whose char effect dispatches by
    /// id in ApplySpecificSpell — their pack defs carry marker flags only
    /// (Hallucination even carries spellflag_curse), so the generic flag
    /// branches would mis-route them.</summary>
    private static bool HasNativeCustomCharHandler(SpellType id) => id is
        SpellType.Light or SpellType.Hallucination or SpellType.Stone or
        SpellType.Shrink or SpellType.Refresh or SpellType.Restore or
        SpellType.Mana or SpellType.Sustenance or SpellType.GenderSwap or
        SpellType.Trance or SpellType.ParticleForm or SpellType.Shield or
        SpellType.Steelskin or SpellType.Stoneskin or SpellType.Regenerate or
        SpellType.Ale or SpellType.Wine or SpellType.Liquor or
        SpellType.Chameleon or SpellType.BeastForm or SpellType.MonsterForm;

    /// <summary>Every Sphere custom spell (1000+) with native engine behavior
    /// — the char handlers above plus the summon/corpse/item routes (Summon
    /// Undead, Animate Dead, Bone Armor) and plain-damage Fire Bolt.
    /// Chameleon / Beast Form / Monster Form ride the reference's
    /// LAYER_SPELL_Polymorph path. Only Enchant and Forget remain refused —
    /// the Source-X reference has no case for either — unless the pack
    /// scripts them.</summary>
    private static bool HasNativeCustomHandler(SpellType id) =>
        HasNativeCustomCharHandler(id) || id is
        SpellType.SummonUndead or SpellType.AnimateDead or
        SpellType.BoneArmor or SpellType.FireBolt;

    internal static bool IsInertSchoolSpell(SpellDef def)
    {
        int id = (int)def.Id;
        if (id < 201)
            return false; // Magery / Necromancy native id space
        if (HasNativeSchoolHandler(def.Id) || HasNativeCustomHandler(def.Id))
            return false;
        if (def.HasScriptedStages)
            return false; // the pack owns the behavior
        return !HasActionableFlags(def);
    }

    private void ApplySpecificSpell(Character caster, Character target, SpellDef def, int effect)
    {
        switch (def.Id)
        {
            case SpellType.Teleport:
            {
                var dest = _effectTargetPos ?? caster.CastTargetPos;
                if (dest.X == 0 && dest.Y == 0) break;
                // Validate the destination like Recall/Gate do: off-map or an
                // impassable tile (wall/water/blocking static) would strand the
                // caster. Recall/Gate guarded this; Teleport did not.
                var teleMd = _world.MapData;
                if (_world.GetSector(dest) == null)
                {
                    OnSysMessage?.Invoke(caster, "That location is unreachable.");
                    break;
                }
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
                // REGION_ANTIMAGIC_TELEPORT: can't teleport into this region.
                if (caster.PrivLevel < Core.Enums.PrivLevel.GM)
                {
                    var teleRegion = _world.FindRegion(dest);
                    if (teleRegion != null && (teleRegion.IsFlag(RegionFlag.NoTeleport) || teleRegion.NoMagic))
                    {
                        OnSysMessage?.Invoke(caster, "You cannot teleport to that location.");
                        break;
                    }
                }
                // PRIV_JAILED: Spell_Teleport sends a prisoner back to the cell
                // (CCharSpell.cpp:146-174; the spell calls it at :3140).
                if (!RedirectJailedTravel(caster, ref dest))
                    break;
                byte oldMap = caster.MapIndex;
                if (_world.MoveCharacter(caster, dest))
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
                // Source-X OnSpellEffect SPELL_Cure (CCharSpell.cpp:3914): the cure can
                // fail against a strong poison (Calc_CurePoisonChance).
                if (target.IsStatFlag(StatFlag.Poisoned))
                {
                    if (CharacterPoisonState.CureChance(target.Poison.Memory, _effectSkillLevel,
                            caster.PrivLevel >= PrivLevel.GM))
                    {
                        target.SetPoisonCure(def.Id == SpellType.ArchCure || _effectSkillLevel > 900);
                        OnSysMessage?.Invoke(caster, ServerMessages.GetFormatted(Msg.HealingCure1,
                            caster == target ? ServerMessages.Get(Msg.HealingYourself) : target.Name));
                        if (caster != target)
                            OnSysMessage?.Invoke(target, ServerMessages.GetFormatted(Msg.HealingCure2, caster.Name));
                    }
                    else
                    {
                        if (caster != target)
                            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.HealingCure3));
                        OnSysMessage?.Invoke(target, ServerMessages.Get(Msg.HealingCure4));
                    }
                }
                break;
            // The memory-backed cases below only equip the effect's IT_SPELL memory
            // (Spell_Effect_Create); what the effect DOES is its Spell_Effect_Add, run on
            // the worn memory after the add hooks (SpellEngine.Effects.cs NativeAdd).
            case SpellType.Paralyze:
            case SpellType.ParalyzeField:   // same LAYER_SPELL_Paralyze effect (:3974-3979)
            case SpellType.Invisibility:
            case SpellType.MagicReflect:
            case SpellType.HorrificBeast:
            case SpellType.WraithForm:
            case SpellType.LichForm:
            case SpellType.VampiricEmbrace:
            case SpellType.CorpseSkin:
            case SpellType.MindRot:
            case SpellType.Stone:
            case SpellType.ParticleForm:
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.Explosion:
                // The delayed second blast is a LAYER_SPELL_Explosion memory made only
                // when the effect has a duration and is not a potion's (:3960-3964) -
                // whatever the definition's LAYER says. A zero duration never leaves a
                // timerless memory behind.
                if (!IsPotionDelivery && EffectDurationTenths(caster, target, def) > 0)
                    CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.Reveal:
                target.ClearHiddenState();
                break;
            case SpellType.Dispel:
            case SpellType.MassDispel:
                // Spell_Dispel (CCharSpell.cpp:79-104) deletes only the memories on
                // LAYER_SPELL_STATS..LAYER_SPELL_Summon - poison, drunkenness,
                // hallucination, mana drain and the necromancy layers stay.
                // The level is 150 from a GM and 50 otherwise (CCharSpell.cpp:3949);
                // at 100 or below a MOVE_NEVER memory stays (:90).
                // A conjured creature is banished first, so MAGICF_DISPELKILLSUMMONS
                // still decides between a kill and a vanish before its summoning
                // memory (layer 41) takes it with the dispel.
                DispelConjured(caster, target);
                if (!target.IsDeleted)
                    SpellDispel(target, caster.PrivLevel >= PrivLevel.GM ? 150 : 50);
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
            case SpellType.PoisonField:     // :3906-3913
            {
                // Source-X OnSpellEffect SPELL_Poison (CCharSpell.cpp:3906): under the
                // OSI formulas the strength is (magery + the caster's poisoning) / 2;
                // SetPoison does the level ladder, distance fall-off and Evil Omen.
                int poisonEffect = effect;
                if (IsMagicFlag(MagicConfigFlags.OsiFormulas))
                    poisonEffect = (_effectSkillLevel + caster.GetSkill(SkillType.Poisoning)) / 2;
                target.SetPoison(poisonEffect, poisonEffect / 50, caster);
                break;
            }
            case SpellType.NightSight:
            case SpellType.Light:
                // A personal light boost for the duration (Night Sight's layer; the
                // Sphere custom Light rides LAYER_FLAG_Potion, :4015-4019).
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.ReactiveArmor:
                // How much comes back is the SPELL DEFINITION's business: the add
                // reads its EFFECT curve at the caster's primary skill (:1411).
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.Protection:
            case SpellType.ArchProtection:
                // Non-Bless custom spell definitions still use the same
                // LAYER_SPELL_Protection-equivalent ward path.
                ApplyProtectionWard(caster, target, def, effect);
                break;
            case SpellType.Incognito:
                // LAYER_SPELL_Incognito (CCharSpell.cpp:1155-1195): a random name
                // from the race's [NAMES] list, a random skin hue for a playable
                // body and one random hair hue for hair and beard alike.
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.CurseWeapon:
                // Necromancy Curse Weapon (:4138): the add needs a weapon in hand and
                // puts a fixed 50 on its HITLEECHLIFE, kept as the memory's level
                // (CCharSpell.cpp:1355-1367).
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.PainSpike:
                // LAYER_SPELL_Pain_Spike (CCharSpell.cpp:1295-1311): the add sets the
                // level from the caster's Spirit Speak and ten charges; the memory
                // ticks once a second (:1957-1963).
            case SpellType.Strangle:
                // Necromancy Strangle (:1220-1231): power = max(4, SpiritSpeak/100)
                // charges, first tick when the 5 s memory timer runs out.
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.BloodOath:
                // Blood Oath is a pact kept on the CASTER, linked to the enemy
                // (pCharSrc->Spell_Effect_Create(..., this), CCharSpell.cpp:4115): a
                // blow the enemy lands on the caster reflects back.
                CreateEffect(target, caster, def, Math.Max(0, effect), EffectDurationTenths(caster, target, def));
                break;
            case SpellType.EvilOmen:
                // Necromancy Evil Omen: an ordinary memory on LAYER_SPELL_Evil_Omen
                // (CCharSpell.cpp:4122-4124). Its presence makes the next harmful
                // effect land harder, and that effect deletes it (CCharFight.cpp:690,
                // CCharAct.cpp:4239, CCharSpell.cpp:3663).
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            // Poison Strike and Wither have no native case in Source-X: they are
            // AREA spells whose [SPELL] @Effect stage deals the damage
            // (OnSpellEffect's commented-out list, CCharSpell.cpp:4142-4147).
            case SpellType.VengefulSpirit:
            {
                // Necromancy Vengeful Spirit (reference SPELL_Vengeful_Spirit):
                // summon a revenant (CREID_REVENANT 0x2EE) that hunts the target,
                // controlled by the caster for a short duration.
                int summonSkill = caster.GetSkill(def.GetPrimarySkill());
                var spawnPos = new Point3D((short)(caster.X + 1), caster.Y, caster.Z, caster.Position.Map);
                // Stats/skills come from the revenant chardef @Create
                // (SummonCreature applies it) — not flat invented numbers.
                var revenant = SummonCreature(caster, spawnPos, def, summonSkill, bodyId: 0x02EE);
                PlaceSummonEffect(caster, revenant, def);
                if (revenant != null && !revenant.IsDeleted && target != caster)
                    revenant.FightTarget = target.Uid;
                break;
            }
            case SpellType.Polymorph:
            case SpellType.Chameleon:
            case SpellType.BeastForm:
            case SpellType.MonsterForm:
            {
                // The body is the menu pick (sm_polymorph / sm_beast_form /
                // sm_monster_form) stashed on the caster - Source-X's
                // m_atMagery.m_uiSummonID, which Spell_Effect_Add SetID()s
                // (CCharSpell.cpp:1083). There is no invented random fallback:
                // without a pick the body stays. The Sphere customs share the
                // reference's LAYER_SPELL_Polymorph path (:4087).
                ushort newBody = 0;
                if (target.TryGetTag("POLY_SELECT", out string? polySel) &&
                    !string.IsNullOrWhiteSpace(polySel))
                {
                    newBody = Character.ResolvePolyBody(polySel);
                    target.RemoveTag("POLY_SELECT");
                }
                CreatePolymorphEffect(caster, target, def, effect, newBody);
                break;
            }
            case SpellType.ReaperForm:
            case SpellType.StoneForm:
            {
                // Source-X OnSpellEffect equips LAYER_SPELL_Polymorph and the
                // effect-add SetID()s the form body. Reaper uses its own
                // CREID_REAPER_FORM 0xE6 (the reference literally assigns
                // CREID_STONE_FORM to both — an obvious copy-paste slip we do
                // not reproduce); Stone Form is CREID_STONE_FORM 0x2C1.
                // The pack ships Reaper Form with DURATION=0.0: a 0-duration
                // memory has no timer, so the form holds until dispel/death/toggle
                // (the Scripts-X @Select FINDID.<RUNE_ITEM>.REMOVE path).
                ushort formBody = def.Id == SpellType.ReaperForm
                    ? (ushort)0x00E6 : (ushort)0x02C1;
                CreatePolymorphEffect(caster, target, def, effect, formBody);
                break;
            }

            case SpellType.ManaDrain:
                // LAYER_SPELL_Mana_Drain (CCharSpell.cpp:1615-1627): the target
                // loses up to the effect ((400 + EI - MR)/10 under OSI formulas) and
                // gets it back when the memory expires (:874-876). The caster
                // gains nothing.
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.ManaVampire:
            {
                // SPELL_Mana_Vamp (CCharSpell.cpp:3981-4004): all the target's mana
                // moves to the caster; under OSI formulas (EI - MR)/10, halved
                // against an NPC, capped by what the target has.
                int max = Math.Max(0, (int)target.Mana);
                int vamp = max;
                if (IsMagicFlag(MagicConfigFlags.OsiFormulas))
                {
                    vamp = (caster.GetSkill(SkillType.EvalInt) - target.GetSkill(SkillType.MagicResistance)) / 10;
                    if (!target.IsPlayer)
                        vamp /= 2;
                    vamp = Math.Clamp(vamp, 0, max);
                }
                target.Mana = (short)(target.Mana - vamp);
                caster.Mana = (short)Math.Min(caster.Mana + vamp, caster.MaxMana);
                break;
            }

            // ---- Chivalry (201-210). Source-X ships this school over the
            // generic script engine (its native surface is buff bookkeeping
            // only); the handlers below give the classic behaviors that the
            // engine's existing primitives can express. Consecrate Weapon and
            // Enemy of One stay script territory (typed-damage combat
            // coupling), like the reference.
            case SpellType.CleanseByFire:
                // Cure the target's poison by holy fire.
                target.CurePoison();
                break;

            case SpellType.DispelEvil:
                // Area dispel around the paladin: strip dispellable effects
                // from and banish conjured creatures near the caster.
                foreach (var ch in _world.GetCharsInRange(caster.Position, 8).ToList())
                {
                    if (ch == caster || ch.IsDead || ch.IsPlayer)
                        continue;
                    bool conjured = ch.IsStatFlag(StatFlag.Conjured);
                    DispelConjured(caster, ch);
                    if (!ch.IsDeleted && (conjured || ch.IsStatFlag(StatFlag.Polymorph)))
                        StripDispellableEffects(ch);
                }
                break;

            case SpellType.DivineFury:
                // Stamina surges back; the paladin drops their guard for the
                // duration (classic -20 defense while the fury lasts). The memory
                // carries the 20 the ward takes off.
                caster.Stam = caster.MaxStam;
                CreateTimedEffect(caster, caster, def, 20);
                break;

            case SpellType.HolyLight:
                // Energy burst around the paladin — strikes the creatures
                // actively fighting the caster.
                foreach (var ch in _world.GetCharsInRange(caster.Position, 3).ToList())
                {
                    if (ch == caster || ch.IsDead || CombatEngine.IsDamageImmune(ch))
                        continue;
                    bool hostile = ch.FightTarget == caster.Uid ||
                                   caster.FightTarget == ch.Uid;
                    if (!hostile)
                        continue;
                    int dmg = CombatEngine.ApplyElementalResist(ch,
                        Math.Max(10, effect), DamageType.Energy);
                    ch.Hits = (short)Math.Max(0, ch.Hits - dmg);
                    ch.RecordAttack(caster.Uid, dmg);
                    if (ch.Hits <= 0 && !ch.IsDead)
                    {
                        if (OnTargetKilled != null) OnTargetKilled.Invoke(ch, caster);
                        else if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, caster);
                        else ch.Kill();
                    }
                }
                break;

            case SpellType.NobleSacrifice:
            {
                // Heal + cure nearby allies at the paladin's own expense.
                // "Ally" is the caster's party plus the caster's own pets —
                // never every bystander (an enemy player standing in range
                // must not receive the heal+cure).
                var casterParty = Character.ResolvePartyManager?.Invoke()?.FindParty(caster.Uid);
                bool sacrificed = false;
                foreach (var ch in _world.GetCharsInRange(caster.Position, 3).ToList())
                {
                    if (ch == caster || ch.IsDead)
                        continue;
                    bool isAlly = ch.OwnerSerial == caster.Uid ||
                                  (casterParty?.IsMember(ch.Uid) ?? false);
                    if (!isAlly)
                        continue;
                    ch.CurePoison();
                    ch.Hits = (short)Math.Min(ch.Hits + Math.Max(10, effect), ch.MaxHits);
                    sacrificed = true;
                }
                if (sacrificed && caster.Hits > 20)
                    caster.Hits -= 20;
                break;
            }

            case SpellType.RemoveCurse:
                StripCurseEffects(target);
                break;

            // ---- Spellweaving: Gift of Renewal — heal-over-time (one heal
            // tick every 2 s for the spell's duration).
            case SpellType.GiftOfRenewal:
            {
                int durTenths = def.GetDuration(caster.GetSkill(def.GetPrimarySkill()));
                var hot = CreateEffect(caster, target, def, Math.Max(5, effect / 3), 20);
                if (hot != null)
                    hot.More2 = (uint)Math.Clamp(durTenths / 20, 5, 15);   // one tick / 2s
                break;
            }

            // ---- Sphere custom spells (reference CCharSpell.cpp OnSpellEffect
            // SPELL_Light..SPELL_Liquor cases; ids remapped to 1000+).
            case SpellType.Hallucination:
            {
                // LAYER_FLAG_Hallucination with rand(30) charges (:4021-4027); the
                // memory then ticks every 15-30 s (Spell_Equip_OnTick :1790-1804).
                var mem = CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                if (mem != null)
                    mem.More2 = (uint)_rand.Next(30);
                break;
            }
            case SpellType.Shrink:
            {
                // Source-X SPELL_Shrink: players are immune; a CONJURED
                // creature is killed outright (NPC_Shrink zeroes its STR);
                // any other NPC is packed into a figurine where it stood.
                if (target.IsPlayer || _world == null)
                    break;
                if (target.IsSummoned || target.IsStatFlag(StatFlag.Conjured))
                {
                    target.Hits = 0;
                    if (OnTargetKilled != null) OnTargetKilled.Invoke(target, caster);
                    else if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(target, caster);
                    else target.Kill();
                    break;
                }
                var shrinkPos = target.Position;
                var figurine = _world.CreateItem();
                figurine.BaseId = 0x2106; // statuette graphic (pet-shrink path)
                if (NPCs.PetFigurine.ShrinkBySpell(caster, target, figurine, _world))
                    _world.PlaceItemWithDecay(figurine, shrinkPos);
                else
                    _world.RemoveItem(figurine);
                break;
            }
            // The effect as it is, no invented floor (UpdateStatVal, :4042-4053).
            case SpellType.Refresh:
                target.Stam = (short)Math.Clamp(target.Stam + effect, 0, target.MaxStam);
                break;
            case SpellType.Restore:
                // Reference: increases both hit points and stamina.
                target.Stam = (short)Math.Clamp(target.Stam + effect, 0, target.MaxStam);
                target.Hits = (short)Math.Clamp(target.Hits + effect, 0, target.MaxHits);
                break;
            case SpellType.Mana:
                target.Mana = (short)Math.Clamp(target.Mana + effect, 0, target.MaxMana);
                break;
            case SpellType.Sustenance:
                // Reference: fills the food meter to its maximum.
                target.Food = 60;
                break;
            case SpellType.GenderSwap:
            {
                ushort swapped = target.BodyId switch
                {
                    0x0190 => (ushort)0x0191, 0x0191 => (ushort)0x0190, // human
                    0x025D => (ushort)0x025E, 0x025E => (ushort)0x025D, // elf
                    0x029A => (ushort)0x029B, 0x029B => (ushort)0x029A, // gargoyle
                    _ => (ushort)0,
                };
                if (swapped != 0)
                {
                    target.BodyId = swapped;
                    Character.OnAppearanceChanged?.Invoke(target);
                }
                break;
            }
            case SpellType.Trance:
                // Source-X SPELL_Trance: a timed Meditation skill bonus, the EFFECT
                // itself (Skill_AddBase +m_spelllevel, :1722-1726).
            case SpellType.Shield:
            case SpellType.Steelskin:
            case SpellType.Stoneskin:
                // Source-X routes these to the Protection ward layer: a timed AR
                // bonus for the spell's duration (:4097-4101).
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            case SpellType.Regenerate:
            {
                // SPELL_Regenerate (CCharSpell.cpp:4103-4112): the duration becomes the
                // number of 2-second heals, carried as the memory's charges; each heal is
                // the EFFECT curve at the memory's level (Spell_Equip_OnTick :1782-1788).
                // The first heal comes after two seconds like the rest.
                int charges = Math.Max(1, EffectDurationTenths(caster, target, def) / 20);
                var hot = CreateEffect(caster, target, def, Math.Max(0, effect), 20);
                if (hot != null)
                    hot.More2 = (uint)charges;
                break;
            }
            case SpellType.Ale:
            case SpellType.Wine:
            case SpellType.Liquor:
                // Use_Drink (CCharUse.cpp:1040-1053): a drink while still drunk
                // lengthens the LAYER_FLAG_Drunk memory by ten more charges; otherwise
                // a fresh one starts with ten. Each 5 s tick drains a point of
                // stamina and mana and may hiccup (Spell_Equip_OnTick :1759-1780).
                ApplyDrunk(caster, target, def, Math.Max(0, effect));
                OnSysMessage?.Invoke(target, "*hic*");
                break;
            case SpellType.SummonCreature:
                // SPELL_Summon on a character (CCharSpell.cpp:3942-3944): the summoning
                // memory on LAYER_SPELL_Summon - what a summoned creature wears for its
                // whole conjured life (Spell_Summon_Place, :368).
                CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
            default:
                // Source-X OnSpellEffect default (CCharSpell.cpp:4148-4151): a spell
                // with no case of its own still equips a timed spell memory when its
                // LAYER is a spell layer (LAYER_SPELL_STATS or above), so @EffectAdd,
                // @EffectRemove, the buff icon and the expiry all run for it.
                if (def.Layer >= SpellLayers.Stats)
                    CreateTimedEffect(caster, target, def, Math.Max(0, effect));
                break;
        }
    }

    /// <summary>SPELL_Create_Food (CCharSpell.cpp:3099-3115): CreateScript of the
    /// DEFFOOD resource (the pack's weighted list of foods), aimed at the target
    /// point for a TARG_OBJ/TARG_XYZ def and otherwise bounced into the caster's
    /// pack with the create-food message. Source-X passes it1test here, which is
    /// only ever set for field spells, so @Success cannot change the food.</summary>
    private void CreateFood(Character caster, SpellDef def, Point3D targetPos)
    {
        int defIndex = ResolveDefFoodIndex();
        if (defIndex == 0)
            return;
        var food = _world.CreateItem();
        var idef = Definitions.DefinitionLoader.GetItemDef(defIndex);
        ushort graphic = Definitions.ItemDefHelper.CreateGraphic(idef, defIndex);
        food.BaseId = graphic != 0 ? graphic : (defIndex <= 0xFFFF ? (ushort)defIndex : (ushort)0);
        Definitions.ItemDefHelper.ApplyInstanceMetadata(food, defIndex);

        if ((def.Flags & (SpellFlag.TargObj | SpellFlag.TargXYZ)) != 0)
        {
            if (!_world.PlaceItemWithDecay(food, targetPos))
                _world.RemoveItem(food);
            return;
        }

        // ItemBounce: the pack, else the caster's feet.
        if (caster.Backpack == null || !caster.Backpack.TryAddItem(food))
        {
            if (!_world.PlaceItemWithDecay(food, caster.Position))
            {
                _world.RemoveItem(food);
                return;
            }
        }
        OnSysMessage?.Invoke(caster, ServerMessages.GetFormatted(Msg.SpellCreateFood, food.GetName()));
    }

    /// <summary>The ITEMDEF DEFFOOD names: a plain defname, or a weighted
    /// <c>{ i_a 1 i_b 1 ... }</c> list rolled once per cast. 0 when unset.</summary>
    private static int ResolveDefFoodIndex()
    {
        var res = Definitions.DefinitionLoader.StaticResources;
        if (res == null)
            return 0;
        string name = "DEFFOOD";
        if (Definitions.DefinitionLoader.TryGetDefValue("DEFFOOD", out string value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            string text = value.Trim();
            if (text.StartsWith('{') && text.EndsWith('}'))
            {
                var tokens = SphereNet.Scripting.Expressions.BraceRange.SplitTokens(text[1..^1]);
                var picks = new List<(string Name, int Weight)>();
                for (int i = 0; i < tokens.Count; i += 2)
                {
                    int weight = i + 1 < tokens.Count && int.TryParse(tokens[i + 1], out int w) ? w : 1;
                    if (weight > 0) picks.Add((tokens[i], weight));
                }
                int total = picks.Sum(p => p.Weight);
                if (total <= 0)
                    return 0;
                int roll = Random.Shared.Next(total);
                foreach (var (pickName, weight) in picks)
                {
                    roll -= weight;
                    if (roll < 0) { name = pickName; break; }
                }
            }
            else
                name = text;
        }
        return Definitions.TemplateEngine.ResolveItemDefIndex(res, name);
    }

    private static void ClearCastState(Character ch) => ch.ClearCastState(notifyAbort: false);

    /// <summary>What this caster actually pays for a spell (Source-X
    /// Calc_SpellManaCost, CResourceCalc.cpp:522). LOWERMANACOST is a PERCENT off
    /// and may be negative, in which case it raises the bill — which is exactly
    /// how the reference expresses Mind Rot: its memory takes 10 off the
    /// character's LOWERMANACOST (CCharSpell.cpp:1351-1354). It is summed off the
    /// character and everything worn, the way the other spell properties are.</summary>
    private static int EffectiveManaCost(Character caster, SpellDef def)
    {
        int cost = def.ManaCost;
        int lower = GetCastingPropertyValue(caster, SpellCastingProperties.LowerManaCost);
        if (lower != 0)
            cost -= cost * lower / 100;
        return Math.Max(0, cost);
    }

    /// <summary>Calc_SpellManaCost with the cast source (CResourceCalc.cpp:522-554):
    /// a wand costs nothing, a scroll half of the LOWERMANACOST-adjusted cost.</summary>
    private static int SpellManaCost(Character caster, SpellDef def, bool wand, bool scroll)
    {
        if (wand) return 0;
        int cost = EffectiveManaCost(caster, def);
        return scroll ? cost / 2 : cost;
    }

    /// <summary>The [NAMES] list Incognito draws from, by race and sex
    /// (CCharSpell.cpp:1164-1169); null for a body that is none of the three.</summary>
    private static string? IncognitoNamesList(Character t)
    {
        bool female = t.IsFemale;
        if (t.IsHuman) return female ? "#NAMES_HUMANFEMALE" : "#NAMES_HUMANMALE";
        if (t.BodyId is 0x025D or 0x025E or 0x025F or 0x0260)
            return female ? "#NAMES_ELF_FEMALE" : "#NAMES_ELF_MALE";
        if (t.IsGargoyle) return female ? "#NAMES_GARGOYLE_FEMALE" : "#NAMES_GARGOYLE_MALE";
        return null;
    }

    /// <summary>The iDuration of the OnSpellEffect being applied (tenths), after
    /// @SpellEffect's LOCAL.Duration; null outside an ApplyCharEffect dispatch.</summary>
    private int? _effectDurationTenths;

    /// <summary>The effect being applied is a Magic Reflect bounce onto its caster
    /// (Source-X OnSpellEffect's fReflecting).</summary>
    private bool _effectReflected;

    /// <summary>The duration an effect created right now lasts, in tenths.</summary>
    private int EffectDurationTenths(Character caster, Character target, SpellDef def) =>
        _effectDurationTenths ?? GetSpellDuration(def, caster.GetSkill(def.GetPrimarySkill()), caster, target);

    /// <summary>The port of CChar::GetSpellDuration (CCharSpell.cpp:4169-4316), in
    /// tenths of a second. With MAGICF_OSIFORMULAS (and always for spells from
    /// Animate Dead AOS on) the listed spells use their fixed OSI/necromancy
    /// formulas in seconds; everything else reads the DURATION curve at the level.
    /// <paramref name="self"/> is the character the effect is on - the one whose
    /// Magic Resistance the necromancy formulas subtract.</summary>
    internal static int GetSpellDuration(SpellDef def, int skillLevel, Character? src, Character self)
    {
        long seconds = -1;
        var spell = def.Id;
        if (src != null && (IsMagicFlag(MagicConfigFlags.OsiFormulas) || (int)spell >= (int)SpellType.AnimateDeadAOS))
        {
            long magery = src.GetSkill(SkillType.Magery);
            long evalInt = src.GetSkill(SkillType.EvalInt);
            long spiritSpeak = src.GetSkill(SkillType.SpiritSpeak);
            long resist = self.GetSkill(SkillType.MagicResistance);
            switch (spell)
            {
                case SpellType.Clumsy: case SpellType.Feeblemind: case SpellType.Weaken:
                case SpellType.Agility: case SpellType.Cunning: case SpellType.Strength:
                case SpellType.Bless: case SpellType.Curse:
                    seconds = 1 + evalInt * 6 / 50; break;
                case SpellType.Protection:
                    seconds = Math.Clamp(magery * 2 / 10, 15, 240); break;
                case SpellType.WallOfStone:
                    seconds = 10; break;
                case SpellType.ArchProtection:
                    seconds = Math.Min(magery * 12 / 100, 144); break;
                case SpellType.FireField:
                    seconds = (15 + magery / 5) / 4; break;
                case SpellType.ManaDrain:
                    seconds = 5; break;
                case SpellType.BladeSpirit:
                    seconds = 120; break;
                case SpellType.Incognito:
                    seconds = Math.Min(1 + magery * 6 / 50, 144); break;
                case SpellType.Paralyze:
                    seconds = 7 + magery / 50; break;
                case SpellType.PoisonField:
                    seconds = 3 + magery / 25; break;
                case SpellType.Invisibility:
                    seconds = magery * 12 / 100; break;
                case SpellType.ParalyzeField:
                    seconds = 3 + magery / 30; break;
                case SpellType.EnergyField:
                    seconds = (15 + magery / 5) / 7; break;
                case SpellType.GateTravel:
                    seconds = 60; break;
                case SpellType.Polymorph:
                    seconds = Math.Min(magery / 10, 120); break;
                case SpellType.EnergyVortex:
                    seconds = 90; break;
                case SpellType.SummonCreature: case SpellType.AirElemental: case SpellType.SummonDaemon:
                case SpellType.EarthElemental: case SpellType.FireElemental: case SpellType.WaterElemental:
                    seconds = magery * 2 / 5; break;
                case SpellType.BloodOath:
                    seconds = 8 + (spiritSpeak - resist) / 80; break;
                case SpellType.CorpseSkin:
                    seconds = 40 + (spiritSpeak - resist) / 25; break;
                case SpellType.CurseWeapon:
                    seconds = 1 + spiritSpeak / 34; break;
                case SpellType.MindRot:
                    seconds = 20 + (spiritSpeak - resist) / 50; break;
                case SpellType.PainSpike:
                    seconds = 1; break;       // timer is 1, but 10 charges
                case SpellType.Strangle:
                    seconds = 5; break;
            }
        }
        if (seconds == -1)
            return def.GetDuration(skillLevel);
        return (int)Math.Clamp(seconds * 10, int.MinValue, int.MaxValue);
    }

    private static void NotifySpellBuff(Character target, SpellType spell, bool add,
        ushort durationSeconds = 0, int magnitude = 0)
    {
        if (!ClientBuffCatalog.TryGet(spell, out var definition))
            return;
        Character.OnClientBuffChanged?.Invoke(target, definition.Icon, add, durationSeconds,
            add ? BuildBuffArgs(definition.Icon, magnitude) : null);
    }

    /// <summary>Raise a buff icon directly, for the effects whose icon does not
    /// follow the one-icon-per-spell-on-the-effect-target rule. Removes first,
    /// like every Source-X addBuff site: a client that still holds the icon
    /// would otherwise keep the old countdown instead of the new one.</summary>
    private static void RaiseBuffIcon(Character target, BuffIcon icon,
        ushort durationSeconds, string[]? args)
    {
        Character.OnClientBuffChanged?.Invoke(target, icon, false, 0, null);
        Character.OnClientBuffChanged?.Invoke(target, icon, true, durationSeconds, args);
    }

    /// <summary>Cliloc formatter arguments for a buff tooltip — Source-X
    /// resendBuffs/Spell_Effect_Add fill a NumBuff array with the effect
    /// magnitude before calling addBuff, and the client interpolates them into
    /// the description cliloc. Without them the tooltip renders its ~1_VAL~
    /// placeholders empty. Argument counts follow the reference exactly:
    /// one per affected stat, plus the four fixed 10s Curse also sends.</summary>
    private static string[]? BuildBuffArgs(BuffIcon icon, int magnitude)
    {
        if (magnitude <= 0)
            return null;
        string value = magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return icon switch
        {
            BuffIcon.Clumsy or BuffIcon.Feeblemind or BuffIcon.Weaken or
            BuffIcon.Strength or BuffIcon.Agility or BuffIcon.Cunning or
            BuffIcon.GiftOfRenewal or BuffIcon.AttuneWeapon or
            BuffIcon.Thunderstorm or BuffIcon.EssenceOfWind or
            BuffIcon.ArcaneEmpowerment or BuffIcon.CorpseSkin => [value],

            // Bless and Mass Curse touch all three base stats (STAT_BASE_QTY).
            BuffIcon.Bless or BuffIcon.MassCurse => [value, value, value],

            // Curse: three stat penalties followed by the four fixed resist
            // penalties Source-X hardcodes to 10.
            BuffIcon.Curse => [value, value, value, "10", "10", "10", "10"],

            _ => null
        };
    }

    /// <summary>Banish a conjured creature (shared by Dispel / Mass Dispel and
    /// Chivalry Dispel Evil): DISPELKILLSUMMONS routes through the kill
    /// pipeline (loot/corpse), otherwise the summon is deleted outright.</summary>
    private void DispelConjured(Character caster, Character target)
    {
        // Already gone: dispelling its summoning memory took it (CCharSpell.cpp:589).
        if (!target.IsStatFlag(StatFlag.Conjured) || target.IsDead || target.IsDeleted)
            return;
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
        }
    }

    /// <summary>Spell_Dispel clears the memories worn on LAYER_SPELL_STATS (32)
    /// through LAYER_SPELL_Summon (41) (CCharSpell.cpp:97) - by the memory's
    /// layer, not by spell, so a spell equipped through its LAYER value (the
    /// generic fallback) is covered too. A conjured creature itself is banished
    /// separately by DispelConjured.</summary>
    private static bool IsDispelLayer(Layer layer) =>
        layer >= SpellLayers.Stats && layer <= SpellLayers.Summon;

    private static bool IsCurseSpell(SpellType s) => s is
        SpellType.Clumsy or SpellType.Feeblemind or SpellType.Weaken or
        SpellType.Curse or SpellType.MassCurse or SpellType.CorpseSkin or
        SpellType.EvilOmen or SpellType.MindRot or SpellType.Strangle or
        SpellType.BloodOath or SpellType.PainSpike;

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
