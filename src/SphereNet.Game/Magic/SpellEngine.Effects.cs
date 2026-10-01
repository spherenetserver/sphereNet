using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;

namespace SphereNet.Game.Magic;

/// <summary>
/// The life of a spell effect, the way Source-X keeps it: the effect IS the IT_SPELL
/// memory item worn on its spell layer (CChar::Spell_Effect_Create, CCharSpell.cpp:2040).
/// The item carries the spell (MOREX), its level (MOREY, m_spelllevel), its charges
/// (MORE2, m_spellcharges), the polymorph/resist pair (MORE1L/MORE1H, m_PolyStr /
/// m_PolyDex), the caster (LINK) and its clock (TIMER) - and nothing else does. Adding
/// it runs Spell_Effect_Add (:965), removing it - whatever removes it - runs
/// Spell_Effect_Remove (:541), and its timer runs Spell_Equip_OnTick (:1738). Saved
/// with the world as an ordinary equipped item and loaded back without re-adding,
/// exactly as a classic save is: the character's own record already holds what the
/// effect changed (its MODSTR, its FLAGS, its body), so only the runtime state that
/// is not persisted is put back when a loaded memory is taken into the lifecycle.
/// </summary>
public sealed partial class SpellEngine
{
    /// <summary>The worn spell memories this engine runs - an index for the timer and
    /// lookup passes, not a second record of the effect: everything is read off the
    /// item.</summary>
    private readonly HashSet<Item> _effects = new(ReferenceEqualityComparer.Instance);

    /// <summary>Memories whose Spell_Effect_Add is still running: the native part has
    /// not been applied yet, so a removal that happens now (a hook deleting its ARGO,
    /// an @EffectAdd RETURN 1) has nothing native to take back.</summary>
    private readonly HashSet<Item> _addPending = new(ReferenceEqualityComparer.Instance);

    /// <summary>Set on a memory whose add hook answered RETURN 0: the memory stays worn
    /// but the engine's own effect was never applied (CCharSpell.cpp:996-997,
    /// 1012-1013), so its removal must not take back something that never happened.
    /// A tag, so it is saved and loaded with the memory like anything else on it.</summary>
    internal const string NativeSkippedTag = "SPELLEFFECT_NATIVE_SKIPPED";

    /// <summary>The body the polymorph being created right now takes - Source-X's
    /// m_atMagery.m_uiSummonID, read by the add (:1083).</summary>
    private ushort _pendingPolyBody;

    private static SpellType MemSpell(Item mem) => (SpellType)(ushort)mem.MoreP.X;

    /// <summary>m_spelllevel - a word upstream.</summary>
    private static int MemLevel(Item mem) => (ushort)mem.MoreP.Y;

    private static void SetMemLevel(Item mem, int level) =>
        mem.MoreP = new Point3D(mem.MoreP.X,
            unchecked((short)(ushort)Math.Clamp(level, 0, ushort.MaxValue)), mem.MoreP.Z, mem.MoreP.Map);

    private static short PolyStr(Item mem) => unchecked((short)(mem.More1 & 0xFFFF));
    private static short PolyDex(Item mem) => unchecked((short)(mem.More1 >> 16));

    private static void SetPolyStats(Item mem, short str, short dex) =>
        mem.More1 = (ushort)str | ((uint)(ushort)dex << 16);

    private static short ClampShort(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static bool IsNativeSkipped(Item mem) =>
        mem.TryGetTag(NativeSkippedTag, out string? v) && !string.IsNullOrEmpty(v) && v != "0";

    private Character? OwnerOf(Item mem) =>
        mem.ContainedIn.IsValid ? _world.FindChar(mem.ContainedIn) : null;

    private Character? LinkOf(Item mem) =>
        mem.Link.IsValid ? _world.FindChar(mem.Link) : null;

    private static bool IsWornSpellMemory(Item mem) =>
        !mem.IsDeleted && mem.ItemType == ItemType.Spell && mem.IsSpellMemory;

    /// <summary>The first spell memory on <paramref name="ch"/> that matches - the way
    /// LayerFind answers upstream.</summary>
    private static Item? FindEffect(Character ch, Func<Item, bool> match)
    {
        foreach (var mem in ch.Memories)
        {
            if (IsWornSpellMemory(mem) && match(mem))
                return mem;
        }
        return null;
    }

    /// <summary>Take a worn memory into the lifecycle. Only a spell this engine knows
    /// has an effect to run (Spell_Effect_Add/Remove return at once without a
    /// definition, :549-552).</summary>
    private bool TryRegister(Item mem)
    {
        if (mem.IsDeleted || GetSpellDef(MemSpell(mem)) == null)
            return false;
        _effects.Add(mem);
        return true;
    }

    // ------------------------------------------------------------------ create

    private Item? CreateTimedEffect(Character caster, Character target, SpellDef def, int level) =>
        CreateEffect(caster, target, def, level, EffectDurationTenths(caster, target, def));

    private Item? CreatePolymorphEffect(Character caster, Character target, SpellDef def, int effect, ushort body)
    {
        ushort outer = _pendingPolyBody;
        _pendingPolyBody = body;
        try { return CreateTimedEffect(caster, target, def, Math.Max(0, effect)); }
        finally { _pendingPolyBody = outer; }
    }

    /// <summary>CChar::Spell_Effect_Create (CCharSpell.cpp:2040-2113): drop what is on
    /// the layer already, make the IT_SPELL memory, wear it and run its add.
    /// <paramref name="linkSource"/> is the memory's LINK - the caster, or for Blood
    /// Oath the enemy the pact is with. A memory with no timer (TIMER=-1) lasts until
    /// cast again: casting on its layer only removes it and creates nothing (:2063-2069).
    /// Null when nothing was left on: the toggle, or an add hook that refused.</summary>
    private Item? CreateEffect(Character? linkSource, Character owner, SpellDef def, int level, int durationTenths)
    {
        var spell = def.Id;
        var layer = SpellLayers.ForSpell(spell, def);

        foreach (var prev in owner.Memories.ToArray())
        {
            if (!IsWornSpellMemory(prev) || prev.EquipLayer != layer)
                continue;
            // A spell with no layer of its own shares nothing: it only replaces itself.
            if (layer == Layer.Special && MemSpell(prev) != spell)
                continue;
            if (prev.Timeout <= 0)
            {
                DeleteEffectMemory(prev);
                return null;
            }
            // Different stat spells stack under MAGICF_STACKSTATS (:2072), and a pending
            // Explosion timer is never cut short (:2076).
            if (layer == SpellLayers.Stats && spell != MemSpell(prev) && IsMagicFlag(MagicConfigFlags.StackStats))
                continue;
            if (spell == SpellType.Explosion && layer == SpellLayers.Explosion)
                continue;
            DeleteEffectMemory(prev);
            break;
        }

        var mem = _world.CreateItem();
        mem.BaseId = def.RuneItemId != 0 ? def.RuneItemId : (ushort)0x2053;   // ITEMID_RHAND_POINT_NW
        mem.Name = spell.ToString();
        mem.ItemType = ItemType.Spell;
        mem.SetAttr(ObjAttributes.Newbie | ObjAttributes.Magic);   // dispellable unless MOVE_NEVER
        mem.MoreP = new Point3D((short)spell, 0, 0, 0);
        SetMemLevel(mem, level);
        mem.More2 = 1;
        if (linkSource != null)
            mem.Link = linkSource.Uid;
        _world.LastNewObject = mem.Uid;
        owner.MemoryState.AttachSpellEffect(mem, layer);
        if (durationTenths > 0)
            mem.SetTimeout(Environment.TickCount64 + (long)durationTenths * 100L);
        _effects.Add(mem);

        AddEffect(mem);
        return mem.IsDeleted ? null : mem;
    }

    // --------------------------------------------------------------------- add

    /// <summary>CChar::Spell_Effect_Add (CCharSpell.cpp:965). The character's
    /// @SpellEffectAdd and then the spell's @EffectAdd run FIRST, on the worn memory
    /// (ARGO) with the caster as SRC: RETURN 1 deletes the memory, RETURN 0 keeps it
    /// worn but skips the engine's own effect (:984-1014). Only then is the effect
    /// applied - reading the memory's level as the scripts left it.</summary>
    private void AddEffect(Item mem)
    {
        var spell = MemSpell(mem);
        var def = GetSpellDef(spell);
        var owner = OwnerOf(mem);
        if (def == null || owner == null)
            return;
        var caster = LinkOf(mem);

        _addPending.Add(mem);
        try
        {
            // SphereNet's own @EffectAdd character trigger: a notice, no verdict.
            Character.OnEffectAdd?.Invoke(owner, (int)spell);
            if (!IsLive(mem))
                return;

            var charVerdict = Character.OnSpellEffectAdd?.Invoke(owner, caster, mem, (int)spell)
                ?? TriggerResult.Default;
            if (!IsLive(mem))
                return;
            if (charVerdict == TriggerResult.True)
            {
                DeleteEffectMemory(mem);
                return;
            }
            if (charVerdict == TriggerResult.False)
            {
                mem.SetTag(NativeSkippedTag, "1");
                return;
            }

            if (TriggerDispatcher != null)
            {
                var stageArgs = new TriggerArgs { CharSrc = caster ?? owner, N1 = (int)spell, O1 = mem };
                var stageVerdict = StageVerdict(
                    TriggerDispatcher.FireSpellTrigger(spell, "EffectAdd", owner, stageArgs), stageArgs);
                if (!IsLive(mem))
                    return;
                if (stageVerdict == TriggerResult.True)
                {
                    DeleteEffectMemory(mem);
                    return;
                }
                if (stageVerdict == TriggerResult.False)
                {
                    mem.SetTag(NativeSkippedTag, "1");
                    return;
                }
            }

            NativeAdd(mem, owner, caster, def);
        }
        finally
        {
            _addPending.Remove(mem);
        }
    }

    private bool IsLive(Item mem) => !mem.IsDeleted && _effects.Contains(mem);

    /// <summary>A section stage's RETURN 0 is a verdict of its own (TRIGRET_RET_FALSE),
    /// not the same as a stage that returns nothing - the numeric RETURN tells them apart.</summary>
    private static TriggerResult StageVerdict(TriggerResult result, TriggerArgs args) =>
        result == TriggerResult.Default && args.ReturnNumber == 0 ? TriggerResult.False : result;

    /// <summary>The native half of Spell_Effect_Add (CCharSpell.cpp:1016-1735).</summary>
    private void NativeAdd(Item mem, Character t, Character? caster, SpellDef def)
    {
        var spell = def.Id;
        int level = MemLevel(mem);
        bool osi = IsMagicFlag(MagicConfigFlags.OsiFormulas);
        switch (spell)
        {
            // The stat spells move the MODIFIER, never the base (Stat_AddMod,
            // :1549-1614): OSTR stays what it was, MODSTR carries the spell.
            case SpellType.Strength:
            case SpellType.Agility:
            case SpellType.Cunning:
            case SpellType.Bless:
                if (caster != null && osi)
                    level = 1 + caster.GetSkill(SkillType.EvalInt) / 100;
                SetMemLevel(mem, level);
                level = MemLevel(mem);
                if (spell is SpellType.Strength or SpellType.Bless) t.ModStr = ClampShort(t.ModStr + level);
                if (spell is SpellType.Agility or SpellType.Bless) t.ModDex = ClampShort(t.ModDex + level);
                if (spell is SpellType.Cunning or SpellType.Bless) t.ModInt = ClampShort(t.ModInt + level);
                break;

            // The curses take their penalty off the ADJUSTED stat and stop it at 1
            // (_CheckLimitEffectStat, :1373-1388); Curse limits one shared value by all
            // three stats (:1511-1518).
            case SpellType.Weaken:
            case SpellType.Clumsy:
            case SpellType.Feeblemind:
            case SpellType.Curse:
            case SpellType.MassCurse:
                if (caster != null && osi)
                    level = Math.Max(0, 8 + caster.GetSkill(SkillType.EvalInt) / 100 -
                        t.GetSkill(SkillType.MagicResistance) / 100);
                if (spell is SpellType.Weaken or SpellType.Curse or SpellType.MassCurse)
                    level = LimitPenalty(level, CombatEngine.EffectiveStr(t));
                if (spell is SpellType.Clumsy or SpellType.Curse or SpellType.MassCurse)
                    level = LimitPenalty(level, CombatEngine.EffectiveDex(t));
                if (spell is SpellType.Feeblemind or SpellType.Curse or SpellType.MassCurse)
                    level = LimitPenalty(level, CombatEngine.EffectiveInt(t));
                SetMemLevel(mem, level);
                level = MemLevel(mem);
                if (spell is SpellType.Weaken or SpellType.Curse or SpellType.MassCurse) t.ModStr = ClampShort(t.ModStr - level);
                if (spell is SpellType.Clumsy or SpellType.Curse or SpellType.MassCurse) t.ModDex = ClampShort(t.ModDex - level);
                if (spell is SpellType.Feeblemind or SpellType.Curse or SpellType.MassCurse) t.ModInt = ClampShort(t.ModInt - level);
                break;

            case SpellType.Protection:
            case SpellType.ArchProtection:
            case SpellType.Shield:
            case SpellType.Steelskin:
            case SpellType.Stoneskin:
                t.ProtectionArmor = (int)Math.Min(int.MaxValue, (long)t.ProtectionArmor + level);
                break;
            case SpellType.DivineFury:
                t.ProtectionArmor -= level;
                break;
            case SpellType.Trance:
                t.SetSkill(SkillType.Meditation, (ushort)Math.Min(ushort.MaxValue,
                    t.GetSkill(SkillType.Meditation) + level));
                break;

            case SpellType.Paralyze:
            case SpellType.ParalyzeField:
                t.SetStatFlag(StatFlag.Freeze);
                break;
            case SpellType.Invisibility:
                t.SetStatFlag(StatFlag.Invisible);
                break;
            case SpellType.MagicReflect:
                t.SetStatFlag(StatFlag.Reflection);
                break;
            case SpellType.Stone:
            case SpellType.ParticleForm:
                t.SetStatFlag(StatFlag.Stone);
                break;

            case SpellType.NightSight:
            case SpellType.Light:
                // 0x4E PacketPersonalLight adds brightness on top of global lighting;
                // the light the character had goes on the memory (MOREZ) to come back.
                mem.MoreP = new Point3D(mem.MoreP.X, mem.MoreP.Y,
                    (sbyte)Math.Min((int)t.LightLevel, sbyte.MaxValue), mem.MoreP.Map);
                t.SetStatFlag(StatFlag.NightSight);
                t.LightLevel = PersonalSpellLight;
                OnPersonalLightChanged?.Invoke(t);
                break;

            case SpellType.ReactiveArmor:
                // The share of a blow that comes back is the definition's EFFECT
                // curve at the caster's primary skill, over ten, kept in m_PolyStr
                // (:1405-1412).
                t.SetStatFlag(StatFlag.Reactive);
                if (caster != null)
                    SetPolyStats(mem, ClampShort(Math.Max(0, def.GetEffect(caster.GetSkill(def.GetPrimarySkill())) / 10)),
                        PolyDex(mem));
                t.ReactiveArmorPercent = Math.Max(0, (int)PolyStr(mem));
                break;

            case SpellType.Incognito:
                if (!t.IsStatFlag(StatFlag.Incognito))
                    AddIncognito(mem, t);
                break;

            case SpellType.HorrificBeast:
                t.SetStatFlag(StatFlag.Polymorph);
                t.HorrificBeastActive = true;
                break;
            case SpellType.WraithForm:
                // While active, damaging hits drain the target's mana (see
                // CombatEngine.ApplyAosOnHitEffects).
                t.SetStatFlag(StatFlag.Polymorph);
                t.WraithFormActive = true;
                break;
            case SpellType.LichForm:
                t.SetStatFlag(StatFlag.Polymorph);
                t.LichFormActive = true;
                ApplyLichFormResists(t, +1);
                break;
            case SpellType.VampiricEmbrace:
                t.SetStatFlag(StatFlag.Polymorph);
                t.VampiricEmbraceActive = true;
                ApplyVampiricResists(t, +1);
                break;
            case SpellType.Polymorph:
            case SpellType.Chameleon:
            case SpellType.BeastForm:
            case SpellType.MonsterForm:
            case SpellType.ReaperForm:
            case SpellType.StoneForm:
            {
                // The body the character returns to is OBODY (_iPrev_id), captured
                // here - after a previous form came off - so a recast never records the
                // current form as the body to restore.
                ushort body = _pendingPolyBody;
                if (t.OBody == 0)
                    t.OBody = t.BodyId;
                t.SetStatFlag(StatFlag.Polymorph);
                if (body != 0)
                {
                    t.BodyId = body;
                    if (spell is not (SpellType.ReaperForm or SpellType.StoneForm))
                        ApplyPolymorphStats(t, mem, body);
                    Character.OnAppearanceChanged?.Invoke(t);
                }
                break;
            }

            case SpellType.CurseWeapon:
                t.CurseWeaponLevel = Math.Clamp(level, 1, 100);
                break;
            case SpellType.CorpseSkin:
                ApplyCorpseSkinResists(t, +1);
                break;
            case SpellType.MindRot:
                t.MindRotActive = true;
                break;

            case SpellType.PainSpike:
                // (SS - MR)/100 + 18 against a player, (SS - MR)/10 + 30 against an NPC,
                // dealt as level/10 on each of ten charges (:1295-1311).
                if (caster != null)
                {
                    int ss = caster.GetSkill(SkillType.SpiritSpeak);
                    int mr = t.GetSkill(SkillType.MagicResistance);
                    SetMemLevel(mem, t.IsPlayer ? (ss - mr) / 100 + 18 : (ss - mr) / 10 + 30);
                }
                mem.More2 = 10;
                break;
            case SpellType.Strangle:
                // Power = the caster's Spirit Speak / 100, at least 4; as many charges
                // (:1220-1231).
                if (caster != null)
                    SetMemLevel(mem, Math.Max(4, caster.GetSkill(SkillType.SpiritSpeak) / 100));
                mem.More2 = (uint)MemLevel(mem);
                break;

            case SpellType.BloodOath:
            {
                // The memory is on the caster; its LINK is the enemy. The reflect level
                // is the wearer's own resist (:1312-1313).
                SetMemLevel(mem, t.GetSkill(SkillType.MagicResistance) * 10 / 20 + 10);
                t.BloodOathEnemy = mem.Link;
                t.BloodOathLevel = MemLevel(mem);
                // Two icons on two characters: the enemy carries the curse with the
                // caster's name, the caster the bond with the enemy's name.
                ushort secs = TimerSeconds(mem);
                if (caster != null)
                    RaiseBuffIcon(caster, BuffIcon.BloodOathCurse, secs, [t.GetName(), t.GetName()]);
                RaiseBuffIcon(t, BuffIcon.BloodOathCaster, secs, [caster?.GetName() ?? ""]);
                break;
            }

            case SpellType.ManaDrain:
                // The target loses up to the effect ((400 + EI - MR)/10 under OSI
                // formulas), capped by its mana; it comes back on removal (:1615-1627).
                if (caster != null && osi)
                    level = (400 + caster.GetSkill(SkillType.EvalInt) - t.GetSkill(SkillType.MagicResistance)) / 10;
                level = Math.Clamp(level, 0, Math.Max(0, (int)t.Mana));
                SetMemLevel(mem, level);
                t.Mana = (short)(t.Mana - level);
                break;

            case SpellType.Hallucination:
                // The trip starts NOW, not at the first tick (addChar + addPlayerSee).
                t.SetStatFlag(StatFlag.Hallucinating);
                OnViewRefresh?.Invoke(t);
                break;
        }

        if (spell != SpellType.BloodOath)
        {
            NotifySpellBuff(t, spell, false);
            NotifySpellBuff(t, spell, true, TimerSeconds(mem), MemLevel(mem));
        }
    }

    /// <summary>The personal light level a Night Sight memory holds the character at.</summary>
    private const byte PersonalSpellLight = 30;

    /// <summary>_CheckLimitEffectStat for a penalty: never below 1 on the stat.</summary>
    private static int LimitPenalty(int penalty, int adjustedStat) =>
        Math.Max(0, Math.Min(penalty, adjustedStat - 1));

    /// <summary>Seconds left on a memory's timer, for its buff icon (wTimerEffect).</summary>
    private static ushort TimerSeconds(Item mem)
    {
        long t = mem.Timeout;
        if (t <= 0)
            return 0;
        return (ushort)Math.Clamp((t - Environment.TickCount64 + 999) / 1000, 1, ushort.MaxValue);
    }

    /// <summary>LAYER_SPELL_Incognito (:1155-1195): the memory takes the character's
    /// name, the hair and beard keep their hue on the memory's COLOR.HAIR / COLOR.BEARD
    /// tags; a random race name, skin hue (playable body) and hair hue go on.</summary>
    private void AddIncognito(Item mem, Character t)
    {
        mem.Name = t.Name;
        string? namesList = IncognitoNamesList(t);
        if (namesList != null)
        {
            string picked = Definitions.DefinitionLoader.ResolveNames(namesList);
            if (!string.IsNullOrWhiteSpace(picked) && picked != namesList)
                t.Name = picked;
        }
        if (Clients.PaperdollText.IsPlayableBody(t.BodyId))
        {
            mem.SetTag("COLOR.SKIN", ((ushort)t.Hue).ToString());
            t.Hue = new Color((ushort)(_rand.Next(0x03EA, 0x0422 + 1) | 0x8000));   // HUE_SKIN_LOW..HIGH | HUE_UNDERWEAR
        }
        ushort hairHue = (ushort)_rand.Next(0x044E, 0x04AD + 1);                    // HUE_HAIR_LOW..HIGH
        if (t.GetEquippedItem(Layer.Hair) is { } hair)
        {
            mem.SetTag("COLOR.HAIR", ((ushort)hair.Hue).ToString());
            hair.Hue = new Color(hairHue);
        }
        if (t.GetEquippedItem(Layer.FacialHair) is { } beard)
        {
            mem.SetTag("COLOR.BEARD", ((ushort)beard.Hue).ToString());
            beard.Hue = new Color(hairHue);
        }
        t.SetStatFlag(StatFlag.Incognito);
        Character.OnAppearanceChanged?.Invoke(t);
    }

    private static void RemoveIncognito(Item mem, Character t)
    {
        if (!t.IsStatFlag(StatFlag.Incognito))
            return;
        t.ClearStatFlag(StatFlag.Incognito);
        if (!string.IsNullOrEmpty(mem.Name) && mem.Name != SpellType.Incognito.ToString())
            t.Name = mem.Name;                                          // restore your name (:690)
        if (!t.IsStatFlag(StatFlag.Polymorph) && TagHue(mem, "COLOR.SKIN") is ushort skin)
            t.Hue = new Color(skin);
        if (TagHue(mem, "COLOR.HAIR") is ushort hairHue && t.GetEquippedItem(Layer.Hair) is { } hair)
            hair.Hue = new Color(hairHue);
        if (TagHue(mem, "COLOR.BEARD") is ushort beardHue && t.GetEquippedItem(Layer.FacialHair) is { } beard)
            beard.Hue = new Color(beardHue);
        Character.OnAppearanceChanged?.Invoke(t);

        static ushort? TagHue(Item m, string key) =>
            m.TryGetTag(key, out string? raw) && ScriptNumber.TryParseToken(raw, out long v)
                ? (ushort)v : null;
    }

    // ------------------------------------------------------------------ remove

    /// <summary>Delete a spell memory - the one way an effect ends. Deleting it runs its
    /// Spell_Effect_Remove through the world's delete notice
    /// (<see cref="OnWorldObjectDeleting"/>), exactly as upstream's OnRemoveObj does.</summary>
    private void DeleteEffectMemory(Item mem)
    {
        if (mem.IsDeleted)
            return;
        if (_world.IsRegistered(mem))
        {
            _world.TryDeleteObject(mem, force: true);
            return;
        }
        if (_effects.Contains(mem))
            RemoveEffect(mem);
        OwnerOf(mem)?.MemoryState.DetachSpellEffect(mem);
        mem.ContainedIn = Serial.Invalid;
        mem.Delete();
    }

    /// <summary>CChar::Spell_Effect_Remove (CCharSpell.cpp:541). The memory has left the
    /// character (FINDLAYER no longer sees it) but is still a live object, so the hooks
    /// read it as ARGO; SRC is its LINK. The character's @SpellEffectRemove and then
    /// the spell's @EffectRemove run BEFORE anything is taken back; either answering
    /// RETURN 0 lets the memory go but keeps the engine's own undo from running
    /// (:558-577). Each memory is removed at most once.</summary>
    private void RemoveEffect(Item mem)
    {
        if (!_effects.Remove(mem))
            return;
        if (!mem.IsSpellMemory)
            return;                             // already taken off (and undone) once
        var owner = OwnerOf(mem);
        owner?.MemoryState.DetachSpellEffect(mem);
        mem.IsSpellMemory = false;
        bool nativeApplied = !_addPending.Contains(mem) && !IsNativeSkipped(mem);
        if (owner == null)
            return;
        var spell = MemSpell(mem);
        var def = GetSpellDef(spell);
        if (def == null)
            return;
        var caster = LinkOf(mem);

        bool skipNative = false;
        var charVerdict = Character.OnSpellEffectRemove?.Invoke(owner, caster, mem, (int)spell)
            ?? TriggerResult.Default;
        if (charVerdict == TriggerResult.False)
            skipNative = true;
        else if (TriggerDispatcher != null)
        {
            var stageArgs = new TriggerArgs { CharSrc = caster ?? owner, N1 = (int)spell, O1 = mem };
            if (StageVerdict(TriggerDispatcher.FireSpellTrigger(spell, "EffectRemove", owner, stageArgs),
                    stageArgs) == TriggerResult.False)
                skipNative = true;
        }

        NotifySpellBuff(owner, spell, false);
        if (nativeApplied && !skipNative)
            NativeRemove(mem, owner, def);
    }

    /// <summary>The native half of Spell_Effect_Remove (CCharSpell.cpp:579-959): take
    /// back what the add put on, from the memory's own fields.</summary>
    private void NativeRemove(Item mem, Character t, SpellDef def)
    {
        var spell = def.Id;
        int level = MemLevel(mem);
        switch (spell)
        {
            case SpellType.Strength: t.ModStr = ClampShort(t.ModStr - level); break;
            case SpellType.Agility: t.ModDex = ClampShort(t.ModDex - level); break;
            case SpellType.Cunning: t.ModInt = ClampShort(t.ModInt - level); break;
            case SpellType.Bless:
                t.ModStr = ClampShort(t.ModStr - level);
                t.ModDex = ClampShort(t.ModDex - level);
                t.ModInt = ClampShort(t.ModInt - level);
                break;
            case SpellType.Weaken: t.ModStr = ClampShort(t.ModStr + level); break;
            case SpellType.Clumsy: t.ModDex = ClampShort(t.ModDex + level); break;
            case SpellType.Feeblemind: t.ModInt = ClampShort(t.ModInt + level); break;
            case SpellType.Curse:
            case SpellType.MassCurse:
                t.ModStr = ClampShort(t.ModStr + level);
                t.ModDex = ClampShort(t.ModDex + level);
                t.ModInt = ClampShort(t.ModInt + level);
                break;

            case SpellType.Protection:
            case SpellType.ArchProtection:
            case SpellType.Shield:
            case SpellType.Steelskin:
            case SpellType.Stoneskin:
                t.ProtectionArmor = Math.Max(0, t.ProtectionArmor - level);
                break;
            case SpellType.DivineFury:
                t.ProtectionArmor += level;
                break;
            case SpellType.Trance:
                t.SetSkill(SkillType.Meditation, (ushort)Math.Max(0, t.GetSkill(SkillType.Meditation) - level));
                break;

            case SpellType.Paralyze:
            case SpellType.ParalyzeField:
                t.ClearStatFlag(StatFlag.Freeze);
                break;
            case SpellType.Invisibility:
                t.ClearStatFlag(StatFlag.Invisible);
                break;
            case SpellType.MagicReflect:
                t.ClearStatFlag(StatFlag.Reflection);
                break;
            case SpellType.Stone:
            case SpellType.ParticleForm:
                t.ClearStatFlag(StatFlag.Stone);
                break;
            case SpellType.NightSight:
            case SpellType.Light:
                t.ClearStatFlag(StatFlag.NightSight);
                t.LightLevel = (byte)Math.Max(0, (int)mem.MoreP.Z);
                OnPersonalLightChanged?.Invoke(t);
                break;
            case SpellType.ReactiveArmor:
                t.ClearStatFlag(StatFlag.Reactive);
                t.ReactiveArmorPercent = 0;
                break;
            case SpellType.Incognito:
                RemoveIncognito(mem, t);
                break;

            case SpellType.HorrificBeast:
                t.HorrificBeastActive = false;
                t.ClearStatFlag(StatFlag.Polymorph);
                break;
            case SpellType.WraithForm:
                t.WraithFormActive = false;
                t.ClearStatFlag(StatFlag.Polymorph);
                break;
            case SpellType.LichForm:
                t.LichFormActive = false;
                ApplyLichFormResists(t, -1);
                t.ClearStatFlag(StatFlag.Polymorph);
                break;
            case SpellType.VampiricEmbrace:
                t.VampiricEmbraceActive = false;
                ApplyVampiricResists(t, -1);
                t.ClearStatFlag(StatFlag.Polymorph);
                break;
            case SpellType.Polymorph:
            case SpellType.Chameleon:
            case SpellType.BeastForm:
            case SpellType.MonsterForm:
            case SpellType.ReaperForm:
            case SpellType.StoneForm:
                // SetID(_iPrev_id) and the polymorph stat change off the modifier (:661-670).
                t.ModStr = ClampShort(t.ModStr - PolyStr(mem));
                t.ModDex = ClampShort(t.ModDex - PolyDex(mem));
                if (t.OBody != 0)
                {
                    bool changed = t.BodyId != t.OBody;
                    t.BodyId = t.OBody;
                    t.OBody = 0;
                    if (changed)
                        Character.OnAppearanceChanged?.Invoke(t);
                }
                t.ClearStatFlag(StatFlag.Polymorph);
                break;

            case SpellType.CurseWeapon:
                t.CurseWeaponLevel = 0;
                break;
            case SpellType.CorpseSkin:
                ApplyCorpseSkinResists(t, -1);
                break;
            case SpellType.MindRot:
                t.MindRotActive = false;
                break;
            case SpellType.BloodOath:
            {
                // Both ends of the bond drop their icon when it breaks.
                Character.OnClientBuffChanged?.Invoke(t, BuffIcon.BloodOathCaster, false, 0, null);
                if (mem.Link.IsValid && _world.FindChar(mem.Link) is { } bonded)
                    Character.OnClientBuffChanged?.Invoke(bonded, BuffIcon.BloodOathCurse, false, 0, null);
                t.BloodOathEnemy = Serial.Invalid;
                t.BloodOathLevel = 0;
                break;
            }
            case SpellType.ManaDrain:
                // The drained mana comes back when the memory goes (:874-876).
                if (level > 0 && !t.IsDead)
                    t.Mana = (short)Math.Min(t.Mana + level, t.MaxMana);
                break;
            case SpellType.Hallucination:
                // The whole view is refreshed when the trip ENDS too (:809-817).
                t.ClearStatFlag(StatFlag.Hallucinating);
                OnViewRefresh?.Invoke(t);
                break;
        }
    }

    /// <summary>The runtime half of an effect that a save does not carry - put back when
    /// a loaded memory joins the lifecycle. The character record already holds
    /// everything the add wrote to it (modifiers, flags, body, resists, skills), so it
    /// is not written again: that is what kept a saved +20 from becoming +40.</summary>
    private void Reattach(Item mem, Character t)
    {
        if (IsNativeSkipped(mem))
            return;
        int level = MemLevel(mem);
        switch (MemSpell(mem))
        {
            case SpellType.Protection:
            case SpellType.ArchProtection:
            case SpellType.Shield:
            case SpellType.Steelskin:
            case SpellType.Stoneskin:
                t.ProtectionArmor = (int)Math.Min(int.MaxValue, (long)t.ProtectionArmor + level);
                break;
            case SpellType.DivineFury:
                t.ProtectionArmor -= level;
                break;
            // The character loader drops these flags as stray visual state before its
            // memories are back on (Character.ClearTransientVisualState); the worn
            // memory is what says they are real.
            case SpellType.Paralyze:
            case SpellType.ParalyzeField:
                t.SetStatFlag(StatFlag.Freeze);
                break;
            case SpellType.Invisibility:
                t.SetStatFlag(StatFlag.Invisible);
                break;
            case SpellType.MagicReflect:
                t.SetStatFlag(StatFlag.Reflection);
                break;
            case SpellType.NightSight:
            case SpellType.Light:
                t.SetStatFlag(StatFlag.NightSight);
                t.LightLevel = PersonalSpellLight;
                OnPersonalLightChanged?.Invoke(t);
                break;
            case SpellType.ReactiveArmor:
                t.SetStatFlag(StatFlag.Reactive);
                t.ReactiveArmorPercent = Math.Max(0, (int)PolyStr(mem));
                break;
            case SpellType.HorrificBeast: t.HorrificBeastActive = true; break;
            case SpellType.WraithForm: t.WraithFormActive = true; break;
            case SpellType.LichForm: t.LichFormActive = true; break;
            case SpellType.VampiricEmbrace: t.VampiricEmbraceActive = true; break;
            case SpellType.CurseWeapon: t.CurseWeaponLevel = Math.Clamp(level, 1, 100); break;
            case SpellType.MindRot: t.MindRotActive = true; break;
            case SpellType.BloodOath:
                t.BloodOathEnemy = mem.Link;
                t.BloodOathLevel = level;
                break;
        }
    }

    // -------------------------------------------------------------------- tick

    /// <summary>Run every spell memory whose timer is due. Called from the main loop;
    /// a worn memory's own world timer comes here too (<see cref="HandleMemoryTimer"/>).</summary>
    public void ProcessExpirations(long now)
    {
        if (_effects.Count == 0)
            return;
        List<Item>? due = null;
        foreach (var mem in _effects)
        {
            if (mem.IsDeleted || (mem.Timeout > 0 && now >= mem.Timeout))
                (due ??= []).Add(mem);
        }
        if (due == null)
            return;

        foreach (var mem in due)
        {
            if (!_effects.Contains(mem))
                continue;                       // retired by an earlier callback
            if (mem.IsDeleted)
            {
                _effects.Remove(mem);
                continue;
            }
            OnEffectTimer(mem, now);
        }
    }

    /// <summary>The world timer of a worn spell memory, handed here instead of the
    /// item's own @Timer (CWorldTicker -> CChar::OnTickEquip -> Spell_Equip_OnTick,
    /// CCharAct.cpp:4143). False when the memory is not one this engine runs.</summary>
    public bool HandleMemoryTimer(Item mem)
    {
        if (!_effects.Contains(mem))
        {
            if (!IsWornSpellMemory(mem))
                return false;
            if (!TryRegister(mem))
            {
                // A memory of a spell nobody defines any more: Spell_Equip_OnTick
                // answers false without a definition and the item goes (:1748-1750).
                if (mem.Timeout > 0 && Environment.TickCount64 >= mem.Timeout)
                    DeleteEffectMemory(mem);
                return true;
            }
        }
        OnEffectTimer(mem, Environment.TickCount64);
        return true;
    }

    private void OnEffectTimer(Item mem, long now)
    {
        long due = mem.Timeout;
        if (due <= 0 || now < due)
            return;                             // re-armed or cleared meanwhile
        var owner = OwnerOf(mem);
        if (owner == null || owner.IsDeleted)
        {
            DeleteEffectMemory(mem);
            return;
        }
        mem.SetTimeout(0);
        if (!EquipTick(mem, owner, now) && !mem.IsDeleted)
            DeleteEffectMemory(mem);
    }

    /// <summary>CChar::Spell_Equip_OnTick (CCharSpell.cpp:1738-2038): the memory's timer
    /// ran out. A spell that does not tick ends here; a ticking one does its work -
    /// after @SpellEffectTick and [SPELL] @EffectTick have seen and may have rewritten
    /// it (LOCAL.Effect / Delay / Charges / DamageType, ARGN2 the level) - re-arms
    /// for the next tick and spends a charge. False removes the effect.</summary>
    private bool EquipTick(Item mem, Character t, long now)
    {
        var spell = MemSpell(mem);
        var def = GetSpellDef(spell);
        if (def == null)
            return false;
        int charges = (int)mem.More2;
        int level = MemLevel(mem);
        int effect = 0;
        var damageType = DamageType.None;
        long delaySeconds = 5;      // custom spells' default (:1755)
        bool heal = def.IsFlag(SpellFlag.Heal);
        bool harm = def.IsFlag(SpellFlag.Harm);

        switch (spell)
        {
            case SpellType.Ale:
            case SpellType.Wine:
            case SpellType.Liquor:
                // A chance to sober up quickly; a point of mana and stamina gone; a
                // hiccup, a stumble and a bow now and then (:1759-1780).
                if (_rand.Next(100) < 10)
                    charges--;
                t.Stam = (short)Math.Max(0, t.Stam - 1);
                t.Mana = (short)Math.Max(0, t.Mana - 1);
                if (_rand.Next(3) == 0)
                {
                    OnOverheadEmote?.Invoke(t, "*hic*");
                    if (!t.IsStatFlag(StatFlag.OnHorse))
                    {
                        t.Direction = (Direction)_rand.Next(8);
                        // Through the shared door: the action id is body-relative.
                        SphereNet.Game.Clients.GameClient.PlayAnimation(
                            t, (ushort)AnimationType.Bow, 18,
                            Character.BroadcastNearby, forEachClientInRange: null);
                    }
                }
                heal = harm = false;
                break;
            case SpellType.Regenerate:
                if (charges <= 0 || level <= 0)
                    return false;
                delaySeconds = 2;
                effect = def.GetEffect(level);
                break;
            case SpellType.Hallucination:
                if (charges <= 0 || level <= 0)
                    return false;
                delaySeconds = 15 + _rand.Next(16);
                // The trip sound goes ONLY to the hallucinating client, and the view
                // refresh re-rolls the random hues it sees (:1796-1802).
                OnPlaySoundTo?.Invoke(t, (ushort)(_rand.Next(2) == 0 ? 0x0243 : 0x0244));
                OnViewRefresh?.Invoke(t);
                heal = harm = false;
                break;
            case SpellType.Explosion:
                effect = level;
                damageType = DamageType.Magic | DamageType.Fire;
                harm = true;
                break;
            case SpellType.Strangle:
            {
                // The delay is taken from the ticks done BEFORE this one is spent:
                // 5 s to the first, then 4, 3, 2 and 1 (:1934-1949). The damage scales
                // rand(power-2..power+1) by the victim's fatigue (:1951-1952).
                int done = level - charges;
                delaySeconds = done switch { 0 => 4, 1 => 3, 2 => 2, _ => 1 };
                int spellPower = _rand.Next(level - 2, level + 2);
                int adjustedDex = Math.Max(1, CombatEngine.EffectiveDex(t));
                effect = Math.Max(0, spellPower * (3 - (t.Dex / adjustedDex) * 2));
                damageType = DamageType.Magic | DamageType.Poison | DamageType.NoReveal;
                harm = true;
                break;
            }
            case SpellType.PainSpike:
                // level/10 of direct damage every second; DAMAGE_GOD, so nothing
                // blocks it (:1957-1963).
                effect = level / 10;
                damageType = DamageType.Magic | DamageType.God;
                delaySeconds = 1;
                harm = true;
                break;
            case SpellType.GiftOfRenewal:
                // Spellweaving heal-over-time: the memory's level every 2 s.
                if (charges <= 0)
                    return false;
                effect = level;
                delaySeconds = 2;
                heal = true;
                harm = false;
                break;
            default:
                if (!def.IsFlag(SpellFlag.Tick))
                    return false;
                break;
        }

        var ctx = new SpellEffectTickContext
        {
            SpellId = (int)spell,
            Level = (byte)Math.Clamp(level, 0, byte.MaxValue),
            Strength = level,
            SourceUid = mem.Link,
            Memory = mem,
            Damage = effect,
            DelayMs = (int)Math.Min(int.MaxValue, delaySeconds * 1000),
            Charges = charges,
            DamageType = (int)damageType,
        };
        if (!RunTickStages(t, def, mem, ctx, out bool scriptedHandled))
        {
            DeleteEffectMemory(mem);
            return false;
        }
        if (mem.IsDeleted)
            return false;
        if (scriptedHandled)
            return true;

        level = ctx.Strength;
        effect = ctx.Damage;
        charges = ctx.Charges;
        damageType = (DamageType)unchecked((uint)ctx.DamageType);
        long delayMs = Math.Max(0, (long)ctx.DelayMs);

        if (harm)
        {
            if (damageType != DamageType.None && effect > 0)
                ApplyTickDamage(t, mem, effect, damageType);
        }
        else if (heal && effect > 0 && !t.IsDead)
        {
            t.Hits = (short)Math.Min(t.Hits + effect, t.MaxHits);
            Character.BroadcastNearby?.Invoke(t.Position, 18,
                new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                    t.Uid.Value, t.MaxHits, t.Hits), 0);
        }
        if (mem.IsDeleted)
            return false;

        mem.SetTimeout(now + Math.Max(1, delayMs));
        // Total number of ticks to come back here (:2032-2037).
        mem.More2 = (uint)Math.Max(0, charges - 1);
        return charges - 1 > 0;
    }

    /// <summary>@SpellEffectTick on the character, then [SPELL] @EffectTick (:1974-1999).
    /// RETURN 1 from either destroys the effect (false); RETURN 0 on a SCRIPTED spell
    /// means the script ticked it (<paramref name="scriptedHandled"/>).</summary>
    private bool RunTickStages(Character t, SpellDef def, Item mem, SpellEffectTickContext ctx,
        out bool scriptedHandled)
    {
        scriptedHandled = false;
        bool scripted = def.IsFlag(SpellFlag.Scripted);
        if (Character.OnSpellEffectTick is { } hook)
        {
            // The host bridge runs both stages with the shared LOCAL pool.
            if (!hook(t, ctx))
                return false;
            scriptedHandled = scripted && ctx.ScriptReturnedZero;
            return true;
        }
        if (TriggerDispatcher == null || !TriggerDispatcher.IsTriggerNameUsed("EffectTick"))
            return true;

        var locals = new SphereNet.Scripting.Variables.VarMap();
        locals.SetInt("Charges", ctx.Charges);
        locals.Set("Delay", (ctx.DelayMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        locals.SetInt("DamageType", ctx.DamageType);
        locals.SetInt("Effect", ctx.Damage);
        var args = new TriggerArgs
        {
            CharSrc = t,
            N1 = ctx.SpellId,
            N2 = ctx.Strength,
            O1 = mem,
            Locals = locals,
        };
        var verdict = StageVerdict(TriggerDispatcher.FireSpellTrigger(def.Id, "EffectTick", t, args), args);
        if (verdict == TriggerResult.True)
            return false;
        if (verdict == TriggerResult.False && scripted)
        {
            scriptedHandled = true;
            return true;
        }
        ctx.Strength = ScriptNumber.ToEngineInt(args.N2);
        ctx.Damage = (int)locals.GetInt("Effect", ctx.Damage);
        ctx.Charges = (int)locals.GetInt("Charges", ctx.Charges);
        ctx.DamageType = (int)locals.GetInt("DamageType", ctx.DamageType);
        if (double.TryParse(locals.Get("Delay"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double delaySec) && delaySec >= 0)
            ctx.DelayMs = (int)Math.Min(int.MaxValue, delaySec * 1000);
        return true;
    }

    /// <summary>One harmful tick through the victim's damage entry, with the memory's
    /// LINK as the source and the element split taken from the type (:2005-2027).</summary>
    private void ApplyTickDamage(Character victim, Item mem, int damage, DamageType type)
    {
        var source = LinkOf(mem);
        if (source == null)
        {
            // A memory that belongs to no creature fails the victim's skill (:2012-2016).
            int aborted = victim.ClearActiveSkillPending();
            if (aborted >= 0)
                Character.ActiveSkillAborted?.Invoke(victim, aborted);
        }
        int dealt = CombatEngine.ApplyCharacterDamage(victim, damage, source, type,
            (type & (DamageType.HitBlunt | DamageType.HitPierce | DamageType.HitSlash)) != 0 ? 100 : 0,
            (type & DamageType.Fire) != 0 ? 100 : 0,
            (type & DamageType.Cold) != 0 ? 100 : 0,
            (type & DamageType.Poison) != 0 ? 100 : 0,
            (type & DamageType.Energy) != 0 ? 100 : 0,
            spell: (int)MemSpell(mem), feedback: DamageFeedback.Caller);
        if (dealt <= 0)
            return;
        TryInterruptFromDamage(victim, dealt);

        Character.BroadcastDamageNearby?.Invoke(victim.Position, 18, victim.Uid.Value, dealt, 0);
        Character.BroadcastNearby?.Invoke(victim.Position, 18,
            new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                victim.Uid.Value, victim.MaxHits, victim.Hits), 0);

        if (victim.Hits <= 0 && !victim.IsDead)
        {
            if (OnTargetKilled != null) OnTargetKilled.Invoke(victim, source!);
            else if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(victim, source);
            else victim.Kill();
        }
    }

    /// <summary>A drink while drunk lengthens the LAYER_FLAG_Drunk memory by ten
    /// charges (Spell_Effect_Remove, += 10, Spell_Effect_Add, CCharUse.cpp:1040-1047);
    /// otherwise a new one starts with ten.</summary>
    private void ApplyDrunk(Character caster, Character target, SpellDef def, int effect)
    {
        var drunk = FindEffect(target, m => MemSpell(m) is SpellType.Ale or SpellType.Wine or SpellType.Liquor);
        if (drunk != null && _effects.Contains(drunk))
        {
            drunk.More2 += 10;
            if (MemLevel(drunk) < 500)
                SetMemLevel(drunk, MemLevel(drunk) + effect);
            if (drunk.Timeout <= 0)
                drunk.SetTimeout(Environment.TickCount64 + 5000);
            return;
        }
        var mem = CreateEffect(caster, target, def, effect, 50);
        if (mem != null)
            mem.More2 = 10;
    }

    // ----------------------------------------------------------------- removal

    /// <summary>CChar::Spell_Dispel (CCharSpell.cpp:79-104): every memory worn on
    /// LAYER_SPELL_STATS..LAYER_SPELL_Summon goes, and a lit light on the face layer -
    /// except, at a level of 100 or less, an ATTR_MOVE_NEVER one. Poison,
    /// drunkenness, hallucination, mana drain and the necromancy layers stay.</summary>
    private void SpellDispel(Character ch, int level)
    {
        foreach (var mem in ch.Memories.ToArray())
        {
            if (!IsWornSpellMemory(mem) || !IsDispelLayer(mem.EquipLayer))
                continue;
            if (level <= 100 && mem.IsAttr(ObjAttributes.Move_Never))
                continue;
            DeleteEffectMemory(mem);
        }
        if (ch.GetEquippedItem(Layer.Face) is { ItemType: ItemType.LightLit } faceLight &&
            !(level <= 100 && faceLight.IsAttr(ObjAttributes.Move_Never)))
            _world.DeleteObject(faceLight);
    }

    /// <summary>Delete the matching spell memories of one character - each one's own
    /// removal runs as it goes. A snapshot first: a removal hook can end other effects.</summary>
    private void RemoveMatchingEffects(Character ch, Func<Item, bool> match)
    {
        foreach (var mem in ch.Memories.ToArray())
        {
            if (IsWornSpellMemory(mem) && match(mem))
                DeleteEffectMemory(mem);
        }
    }

    /// <summary>Chivalry Remove Curse: strip only the CURSE-type effects from the
    /// target, leaving beneficial buffs intact.</summary>
    public void StripCurseEffects(Character target) =>
        RemoveMatchingEffects(target, m => IsCurseSpell(MemSpell(m)));

    /// <summary>Spell_Dispel(100) - what death runs (CCharAct.cpp:4397).</summary>
    public void StripDispellableEffects(Character target) => SpellDispel(target, 100);

    /// <summary>Break an active paralyze early (Source-X: the paralyze spell memory is
    /// deleted when the victim takes damage, CCharFight.cpp:805-818).</summary>
    public void BreakParalyze(Character victim)
    {
        victim.ClearStatFlag(StatFlag.Freeze);
        RemoveMatchingEffects(victim, m => MemSpell(m) == SpellType.Paralyze);
    }

    /// <summary>Retire magical invisibility when Reveal, movement, combat or casting
    /// exposes the character (Reveal deletes the Invis memory, CCharAct.cpp:3521-3532).</summary>
    public void BreakInvisibility(Character victim) =>
        RemoveMatchingEffects(victim, m => MemSpell(m) == SpellType.Invisibility);

    /// <summary>Revert polymorph body on death when MAGICF bit 0x0008 is set.</summary>
    public void RevertPolymorphOnDeath(Character ch)
    {
        if (!IsMagicFlag(MagicConfigFlags.PolymorphRevertDeath))
            return;
        var poly = FindEffect(ch, m => MemSpell(m) == SpellType.Polymorph);
        if (poly != null && ch.OBody != 0 && ch.BodyId != ch.OBody)
            DeleteEffectMemory(poly);
    }

    /// <summary>Death's share of the spell effects: Spell_Dispel(100) (CCharAct.cpp:4397).
    /// It honours the dispel layers and ATTR_MOVE_NEVER like any dispel - a necromancy
    /// curse, a poison or a protected memory outlives the death.</summary>
    public void ClearAllEffectsOnDeath(Character ch)
    {
        RevertPolymorphOnDeath(ch);
        SpellDispel(ch, 100);
    }

    /// <summary>Original body for resurrect after polymorph (OBODY, Source-X _iPrev_id).</summary>
    public ushort GetResurrectBody(Character ch) => ch.OBody != 0 ? ch.OBody : ch.BodyId;

    /// <summary>Delete the effect this memory is - the script REMOVE path. True when the
    /// memory was an effect this engine runs.</summary>
    public bool RemoveEffectByMemory(Item memory)
    {
        if (!_effects.Contains(memory) && !(IsWornSpellMemory(memory) && TryRegister(memory)))
            return false;
        DeleteEffectMemory(memory);
        return true;
    }

    // -------------------------------------------------------------- load/login

    /// <summary>Take every worn spell memory in the world into the lifecycle - the
    /// world was just loaded (a save of either server, or a SERV.LOAD import). Nothing
    /// is re-added: the character records already hold what the effects changed.</summary>
    public int RestorePersistedEffectsFromWorld()
    {
        int count = 0;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is Character ch)
                count += RestorePersistedEffects(ch);
        }
        return count;
    }

    /// <summary>Take this character's loaded spell memories into the lifecycle and put
    /// back the runtime state the save does not carry. Idempotent.</summary>
    public int RestorePersistedEffects(Character ch)
    {
        int count = 0;
        foreach (var mem in ch.Memories.ToArray())
        {
            if (!IsWornSpellMemory(mem) || _effects.Contains(mem) || !TryRegister(mem))
                continue;
            Reattach(mem, ch);
            count++;
        }
        return count;
    }

    /// <summary>Rebuild the client's buff bar after login/resync. Removing each
    /// icon first avoids the client retaining a stale countdown across reconnects.</summary>
    public void ResendBuffs(Character ch)
    {
        if (ch.IsStatFlag(StatFlag.Hidden | StatFlag.Insubstantial))
        {
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.Hidden, false, 0, null);
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.Hidden, true, 0, null);
        }
        if (ch.IsStatFlag(StatFlag.Meditation))
        {
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.ActiveMeditation, false, 0, null);
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.ActiveMeditation, true, 0, null);
        }
        if (ch.IsStatFlag(StatFlag.Criminal))
        {
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.CriminalStatus, false, 0, null);
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.CriminalStatus, true, 0, null);
        }
        if (ch.IsPoisoned)
        {
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.Poison, false, 0, null);
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.Poison, true,
                ch.Poison.RemainingDurationSeconds, null);
        }

        foreach (var mem in ch.Memories)
        {
            if (!IsWornSpellMemory(mem) || !_effects.Contains(mem) || IsNativeSkipped(mem))
                continue;
            var spell = MemSpell(mem);
            NotifySpellBuff(ch, spell, false);
            NotifySpellBuff(ch, spell, true, TimerSeconds(mem), MemLevel(mem));
        }
    }
}
