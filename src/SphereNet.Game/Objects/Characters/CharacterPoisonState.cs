using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Objects.Characters;

/// <summary>
/// Mutable contract between a periodic spell-effect tick and the
/// @SpellEffectTick script bridge (Source-X SPELLFLAG_TICK). The engine
/// seeds it with the pending tick; the wiring maps it to the script LOCAL
/// pool and writes script overrides back before the tick applies.
/// </summary>
public sealed class SpellEffectTickContext
{
    /// <summary>Spell id the effect belongs to (ARGN1).</summary>
    public int SpellId { get; init; }
    /// <summary>Engine effect level (poison: 1=lesser .. 5=lethal).</summary>
    public byte Level { get; init; }
    /// <summary>ARGN2: upstream's iLevel at the trigger - for poison the 0-4 level
    /// under either formula (the non-OSI branch bands the strength first). The raw
    /// strength is the memory's MOREY. Read back after the trigger, as upstream
    /// reads ARGN2 back into iLevel (CCharSpell.cpp:2011).</summary>
    public int Strength { get; set; }
    /// <summary>Applier UID for ARGO.LINK; invalid when unattributed.</summary>
    public Core.Types.Serial SourceUid { get; init; }
    /// <summary>The effect's memory item itself - upstream's ARGO (pItem).</summary>
    public Item? Memory { get; init; }
    /// <summary>Damage this tick (LOCAL.EFFECT), before resistance: upstream feeds
    /// it to OnTakeDamage, which applies the poison resist.</summary>
    public int Damage { get; set; }
    /// <summary>Delay until the next tick in ms (LOCAL.DELAY, seconds in script).</summary>
    public int DelayMs { get; set; }
    /// <summary>Ticks left including this one (LOCAL.CHARGES; the engine
    /// auto-decrements by one after the tick, Source-X parity).</summary>
    public int Charges { get; set; }
}

/// <summary>
/// Poison, the way Source-X keeps it: one IT_SPELL memory of SPELL_Poison worn on
/// LAYER_FLAG_Poison (CChar::SetPoison, CCharAct.cpp:4175). The item IS the
/// poison - MOREX the spell, MOREY the level (OSI 0-4) or strength (0-1000),
/// MORE2 the ticks left, LINK the poisoner, its timer the next tick - so a script
/// reads it with FINDLAYER, a save stores it as an ordinary equipped item, and
/// deleting it by any road (cure, death, dispel, REMOVE) is the cure.
/// STATF_POISONED follows the item on and off the layer (Spell_Effect_Add /
/// Spell_Effect_Remove, CCharSpell.cpp:1134/583).
/// </summary>
public sealed class CharacterPoisonState
{
    /// <summary>Source-X ITEMID_RHAND_POINT_NW, the memory graphic when the spell
    /// def names no rune item.</summary>
    private const ushort FallbackMemoryId = 0x2053;
    private const int HueTextDef = 0x03B2;

    private readonly Character _owner;

    /// <summary>[SPELL] @EffectAdd for the poison memory the moment it is worn
    /// (Source-X SetPoison -> LayerAdd -> Spell_Effect_Add, CCharAct.cpp:4195 /
    /// CCharSpell.cpp:1000): args are the owner, the memory (ARGO) and the poisoner
    /// (SRC). RETURN 1 deletes the memory - no poison. Installed only while a script
    /// hooks the stage.</summary>
    public static Func<Character, Item, Character?, TriggerResult>? OnSpellEffectAdd { get; set; }

    /// <summary>A poison read from a pre-item save (the old POISON= record), made
    /// into its memory on the first tick after load - creating an item while the
    /// world file is still being read could take a uid a later item owns.</summary>
    private (byte Level, int Ticks, long RemainingMs, Serial Source)? _pendingRestore;

    public CharacterPoisonState(Character owner)
    {
        _owner = owner;
    }

    private static bool OsiFormulas =>
        (Character.MagicFlags & (int)MagicConfigFlags.OsiFormulas) != 0;

    /// <summary>The poison memory on LAYER_FLAG_Poison, if one is worn.</summary>
    public Item? Memory
    {
        get
        {
            var item = _owner.GetEquippedItem(Layer.FlagPoison);
            return item != null && !item.IsDeleted ? item : null;
        }
    }

    /// <summary>STATF_POISONED - what every upstream check reads.</summary>
    public bool IsPoisoned => _owner.IsStatFlag(StatFlag.Poisoned);

    /// <summary>The poison on SphereNet's 1 (lesser) .. 5 (lethal) scale, read off the
    /// memory: the OSI level + 1, or the strength banded the way the non-OSI tick
    /// bands it. 0 when no poison is worn.</summary>
    public byte Level
    {
        get
        {
            var mem = Memory;
            if (mem == null) return 0;
            int raw = mem.MoreP.Y;
            if (OsiFormulas)
                return (byte)Math.Clamp(raw + 1, 1, 5);
            return raw < 200 ? (byte)1 : raw < 400 ? (byte)2 : raw < 800 ? (byte)3 : raw < 1000 ? (byte)4 : (byte)5;
        }
        set
        {
            if (value == 0) Cure(false);
            else Apply(value, Serial.Invalid);
        }
    }

    /// <summary>The poisoner (the memory's LINK).</summary>
    public Serial Source => Memory?.Link ?? Serial.Invalid;

    /// <summary>Ticks left (the memory's MORE2).</summary>
    public int TicksRemaining => (int)(Memory?.More2 ?? 0);

    /// <summary>Seconds to the next tick, for the buff icon (GetTimerSAdjusted).</summary>
    public ushort RemainingDurationSeconds
    {
        get
        {
            var mem = Memory;
            if (mem == null || mem.Timeout <= 0) return 0;
            long ms = Math.Max(0, mem.Timeout - Environment.TickCount64);
            return (ushort)Math.Clamp((ms + 999) / 1000, 1, ushort.MaxValue);
        }
    }

    // ------------------------------------------------------------------ apply

    /// <summary>Source-X CChar::SetPoison(iSkill, iHits, pCharSrc), CCharAct.cpp:4175.
    /// A new poison replaces whatever poison was there (Spell_Effect_Create deletes
    /// the previous memory on the layer).</summary>
    public bool SetPoison(int skill, int hits, Character? source)
    {
        if (_owner.IsDead)
            return false;

        // Release if paralyzed, unless the poison spell says not to.
        var def = Character.ResolveSpellDef?.Invoke(SpellType.Poison);
        if (def == null || !def.IsFlag(SpellFlag.NoUnparalyze))
            Character.BreakParalyzeHook?.Invoke(_owner);

        long durationMs = (1 + Random.Shared.Next(2)) * 1000L;   // 1-2 s to the first tick
        var mem = CreateMemory(skill, durationMs, source);
        if (mem == null)
            return false;

        if (!OsiFormulas)
        {
            mem.More2 = (uint)Math.Max(0, hits);
        }
        else
        {
            int level = 0;
            int dist = source == null ? int.MaxValue
                : source == _owner ? 0
                : source.Position.Map != _owner.Position.Map ? int.MaxValue
                : source.Position.GetDistanceTo(_owner.Position);
            if (dist <= SphereNet.Network.State.NetState.MapViewSizeMax)
            {
                if (skill >= 1000) level = 3 + (Random.Shared.Next(10) == 0 ? 1 : 0);
                else if (skill > 850) level = 2;
                else if (skill > 650) level = 1;
                else level = 0;
                if (dist >= 4)
                    level = Math.Max(0, level - dist / 2);
            }
            mem.MoreP = new Point3D(mem.MoreP.X, (short)level, mem.MoreP.Z, mem.MoreP.Map);
            mem.More2 = level switch { 4 => 8, 3 => 6, 2 => 6, 1 => 3, _ => 3 };

            // Evil Omen: the next poison lands one level higher.
            if (_owner.ConsumeEvilOmen())
                mem.MoreP = new Point3D(mem.MoreP.X, (short)(level + 1), mem.MoreP.Z, mem.MoreP.Map);
        }

        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, false, 0, null);
        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, true, (ushort)Math.Min(mem.More2, ushort.MaxValue), null);
        Character.SendOwnerMessage?.Invoke(_owner, ServerMessages.Get(Msg.JustBeenPoisoned));
        _owner.SetStatFlag(StatFlag.Poisoned);
        Character.OnHealthBarStatusChanged?.Invoke(_owner);
        return true;
    }

    /// <summary>Poison of a known SphereNet level (1 lesser .. 5 lethal) - the GM
    /// command and the POISONLEVEL key, which name a level rather than a skill. The
    /// memory is written the way SetPoison would have written that level.</summary>
    public void Apply(byte level, Serial source)
    {
        if (level == 0 || _owner.IsDead)
            return;
        level = Math.Clamp(level, (byte)1, (byte)5);
        var src = source.IsValid ? Character.ResolveCharByUid?.Invoke(source) : null;
        int strength = level switch { 1 => 100, 2 => 300, 3 => 600, 4 => 900, _ => 1000 };
        var mem = CreateMemory(strength, (1 + Random.Shared.Next(2)) * 1000L, src, source);
        if (mem == null)
            return;
        if (OsiFormulas)
        {
            int osi = level - 1;
            mem.MoreP = new Point3D(mem.MoreP.X, (short)osi, mem.MoreP.Z, mem.MoreP.Map);
            mem.More2 = osi switch { 4 => 8, 3 => 6, 2 => 6, 1 => 3, _ => 3 };
        }
        else
        {
            mem.More2 = (uint)(strength / 50);
        }
        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, false, 0, null);
        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, true, (ushort)Math.Min(mem.More2, ushort.MaxValue), null);
        Character.SendOwnerMessage?.Invoke(_owner, ServerMessages.Get(Msg.JustBeenPoisoned));
        _owner.SetStatFlag(StatFlag.Poisoned);
        Character.OnHealthBarStatusChanged?.Invoke(_owner);
    }

    /// <summary>The poison half of Spell_Effect_Create (CCharSpell.cpp:2040): drop the
    /// previous memory on the layer, make the IT_SPELL memory and wear it.</summary>
    private Item? CreateMemory(int strength, long firstTickMs, Character? source, Serial? sourceUid = null)
    {
        var world = ObjBase.ResolveWorld?.Invoke();
        if (world == null)
            return null;

        var prev = Memory;
        if (prev != null)
        {
            // A TIMER=-1 memory lasts until cast again: casting again only removes it.
            bool toggle = prev.Timeout == -1;
            world.DeleteObject(prev);
            if (toggle)
                return null;
        }

        var def = Character.ResolveSpellDef?.Invoke(SpellType.Poison);
        var mem = world.CreateItem();
        mem.BaseId = def?.RuneItemId is > 0 ? def.RuneItemId : FallbackMemoryId;
        mem.Name = def?.Name is { Length: > 0 } n ? n : "Poison";
        mem.SetAttr(def != null ? ObjAttributes.Newbie | ObjAttributes.Magic : ObjAttributes.Newbie);
        mem.ItemType = ItemType.Spell;
        mem.MoreP = new Point3D((short)SpellType.Poison, (short)Math.Clamp(strength, 0, short.MaxValue), 0, 0);
        mem.More2 = 1;
        Serial link = source?.Uid ?? sourceUid ?? Serial.Invalid;
        if (link.IsValid)
            mem.Link = link;
        world.LastNewObject = mem.Uid;
        if (!_owner.Equip(mem, Layer.FlagPoison))
        {
            world.DeleteObject(mem);
            return null;
        }
        mem.SetTimeout(Environment.TickCount64 + Math.Max(1, firstTickMs));

        // Worn: Spell_Effect_Add runs [SPELL] @EffectAdd with ARGO = this memory and
        // SRC = the poisoner. RETURN 1 deletes it (CCharSpell.cpp:1006-1010).
        var addHook = OnSpellEffectAdd;
        if (addHook != null)
        {
            var caster = source ?? (link.IsValid ? Character.ResolveCharByUid?.Invoke(link) : null);
            if (addHook(_owner, mem, caster) == TriggerResult.True || mem.IsDeleted)
            {
                if (!mem.IsDeleted)
                    world.DeleteObject(mem);
                return null;
            }
        }
        return mem;
    }

    // ------------------------------------------------------------------- cure

    /// <summary>Source-X CChar::SetPoisonCure (CCharAct.cpp:4152): delete the poison
    /// memory; removing it clears STATF_POISONED on the way out.</summary>
    public void Cure(bool extra)
    {
        _pendingRestore = null;
        var mem = Memory;
        if (mem != null)
        {
            var world = ObjBase.ResolveWorld?.Invoke();
            if (world != null) world.DeleteObject(mem);
            else _owner.Unequip(Layer.FlagPoison);
        }
        else if (_owner.IsStatFlag(StatFlag.Poisoned))
        {
            OnEffectRemoved();
        }
    }

    public void Cure() => Cure(false);

    /// <summary>Source-X Calc_CurePoisonChance (CResourceCalc.cpp:601).</summary>
    public static bool CureChance(Item? poison, int cureLevel, bool isGm)
    {
        if (poison == null)
            return false;
        if (isGm)
            return true;
        int poisonLevel = poison.MoreP.Y;
        if (poison.TryGetTag("OVERRIDE.CUREPOISONCHANCE", out string? over) &&
            ScriptNumber.TryParseToken(over, out long overChance))
            return Random.Shared.Next(100) <= overChance;

        if (!OsiFormulas)
        {
            int chance = SphereNet.Game.Skills.SkillEngine.CalcSCurve(cureLevel - poisonLevel, 100);
            return Random.Shared.Next(1000) <= chance;
        }
        if (poisonLevel == 0)   // Lesser Poison is always cured.
            return true;
        int cure;
        if (cureLevel < 410)
            cure = poisonLevel switch { 1 => 35, 2 => 15, 3 => 10, _ => 5 };
        else if (cureLevel < 1010)
            cure = poisonLevel switch { 1 => 95, 2 => 45, 3 => 25, _ => 15 };
        else
            cure = poisonLevel switch { 1 => 100, 2 => 75, 3 => 45, _ => 25 };
        return Random.Shared.Next(100) <= cure;
    }

    // ---------------------------------------------------- effect add / remove

    /// <summary>Spell_Effect_Add for LAYER_FLAG_Poison (CCharSpell.cpp:1134): the
    /// memory went onto the layer.</summary>
    internal void OnEffectAdded()
    {
        _owner.SetStatFlag(StatFlag.Poisoned);
        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, false, 0, null);
        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, true, 2, null);
        Character.OnHealthBarStatusChanged?.Invoke(_owner);
    }

    /// <summary>Spell_Effect_Remove for LAYER_FLAG_Poison (CCharSpell.cpp:583): the
    /// memory came off the layer, however it came off.</summary>
    internal void OnEffectRemoved()
    {
        _owner.ClearStatFlag(StatFlag.Poisoned);
        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, false, 0, null);
        Character.OnHealthBarStatusChanged?.Invoke(_owner);
    }

    // ------------------------------------------------------------------- tick

    /// <summary>Restore a poison saved by the pre-item format (POISON=level|ticks|ms|src).
    /// Made into its memory on the next tick; see <see cref="_pendingRestore"/>.</summary>
    public void Restore(byte level, int ticksRemaining, long remainingMs, Serial source)
    {
        if (level == 0 || level > 5 || ticksRemaining <= 0)
            return;
        _pendingRestore = (level, ticksRemaining, Math.Max(0, remainingMs), source);
        _owner.SetStatFlag(StatFlag.Poisoned);
    }

    /// <summary>Character tick: materialise a restored legacy poison. Returns the damage
    /// of a tick that fell due on the memory, for callers driving the clock by hand.</summary>
    public int ProcessTick(long now)
    {
        MaterializeRestore();
        var mem = Memory;
        if (mem == null || mem.Timeout <= 0 || now < mem.Timeout)
            return 0;
        return EquipTick(mem);
    }

    /// <summary>Turn a poison restored from the old POISON= record into its memory.</summary>
    public void MaterializeRestore()
    {
        if (_pendingRestore is { } r)
        {
            _pendingRestore = null;
            _owner.ClearStatFlag(StatFlag.Poisoned);
            Apply(r.Level, r.Source);
            var restored = Memory;
            if (restored != null)
            {
                restored.More2 = (uint)r.Ticks;
                restored.SetTimeout(Environment.TickCount64 + Math.Max(1, r.RemainingMs));
            }
        }
    }

    /// <summary>Source-X CChar::Spell_Equip_OnTick, SPELL_Poison (CCharSpell.cpp:1806):
    /// one tick of the worn poison. Deletes the memory when the poison is spent.
    /// Returns the damage dealt.</summary>
    public int EquipTick(Item mem)
    {
        if (mem.IsDeleted)
            return 0;
        mem.SetTimeout(0);

        int charges = (int)mem.More2;
        int level = mem.MoreP.Y;
        int effect;
        long delaySeconds;
        int maxHits = Math.Max(1, (int)_owner.MaxHits);

        if (charges <= 0)
        {
            DeleteMemory(mem);
            return 0;
        }

        if (OsiFormulas)
        {
            // m_spelllevel = level of the poison, 0-4.
            level = Math.Clamp(level, 0, 4);
            (int lo, int hi, delaySeconds) = level switch
            {
                4 => (16, 33, 5L),
                3 => (15, 30, 5L),
                2 => (7, 15, 4L),
                1 => (5, 10, 3L),
                _ => (4, 7, 2L),
            };
            effect = maxHits * Random.Shared.Next(lo, hi + 1) / 100;

            string you = ServerMessages.Get(OsiSelf[level]);
            string them = ServerMessages.Get(OsiOther[level]);
            EmoteToOthers(ServerMessages.GetFormatted(Msg.MsgEmote5, _owner.GetName(), them));
            Character.SendOwnerMessage?.Invoke(_owner, ServerMessages.GetFormatted(Msg.SpellYoufeel, you));
        }
        else
        {
            // m_spelllevel = strength of the poison, 0-1000.
            if (level < 50)
            {
                DeleteMemory(mem);
                return 0;
            }
            int band = level < 200 ? 0 : level < 400 ? 1 : level < 800 ? 2 : level < 1000 ? 3 : 4;
            mem.MoreP = new Point3D(mem.MoreP.X, (short)(level - 50), mem.MoreP.Z, mem.MoreP.Map); // gets weaker too
            level = band;
            effect = maxHits * (level * 2) / 100;
            delaySeconds = 5 + Random.Shared.Next(4);

            string looks = ServerMessages.GetFormatted(Msg.SpellLooks, ServerMessages.Get(Looks[level]));
            if ((Character.EmoteFlags & EmoteFlagPoison) != 0)
                Character.SendOwnerMessage?.Invoke(_owner, ServerMessages.GetFormatted(Msg.MsgEmote6, looks));
            else
                EmoteToOthers(ServerMessages.GetFormatted(Msg.MsgEmote5, _owner.GetName(), looks));
            Character.SendOwnerMessage?.Invoke(_owner,
                ServerMessages.GetFormatted(Msg.SpellYoufeel, ServerMessages.Get(Looks[level])));
        }

        effect = Math.Max(PoisonMin[Math.Clamp(level, 0, 4)], effect);

        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, false, 0, null);
        Character.OnClientBuffChanged?.Invoke(_owner, BuffIcon.Poison, true, (ushort)Math.Max(1, delaySeconds), null);

        // @SpellEffectTick / [SPELL] @EffectTick: RETURN 1 destroys the memory.
        var tickHook = Character.OnSpellEffectTick;
        if (tickHook != null)
        {
            var ctx = new SpellEffectTickContext
            {
                SpellId = (int)SpellType.Poison,
                Level = Level,
                Strength = Math.Clamp(level, 0, 4),
                SourceUid = mem.Link,
                Memory = mem,
                Damage = effect,
                DelayMs = (int)(delaySeconds * 1000),
                Charges = charges,
            };
            if (!tickHook(_owner, ctx))
            {
                DeleteMemory(mem);
                return 0;
            }
            if (mem.IsDeleted)
                return 0;
            effect = ctx.Damage;
            charges = ctx.Charges;
            delaySeconds = Math.Max(0, ctx.DelayMs / 1000L);
            if (ctx.DelayMs > 0 && delaySeconds == 0) delaySeconds = 1;
        }

        int dealt = 0;
        if (effect > 0)
        {
            var poisoner = mem.Link.IsValid ? Character.ResolveCharByUid?.Invoke(mem.Link) : null;
            if (poisoner == null)
            {
                // A memory that belongs to no creature fails the victim's skill.
                int aborted = _owner.ClearActiveSkillPending();
                if (aborted >= 0)
                    Character.ActiveSkillAborted?.Invoke(_owner, aborted);
            }
            dealt = Combat.CombatEngine.ApplyScriptDamage(_owner, effect,
                Combat.DamageType.Magic | Combat.DamageType.Poison |
                Combat.DamageType.NoDisturb | Combat.DamageType.NoReveal,
                poisoner, poisonPercent: 100);
            if (dealt > 0 && Combat.CombatEngine.OnDirectCharacterDamageApplied == null &&
                _owner.Hits <= 0 && !_owner.IsDead)
            {
                if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(_owner, poisoner);
                else _owner.Kill();
            }
        }

        if (mem.IsDeleted)
            return dealt;
        mem.More2 = (uint)Math.Max(0, charges);
        // Total number of ticks to come back here.
        if (charges - 1 > 0)
        {
            mem.More2 = (uint)(charges - 1);
            mem.SetTimeout(Environment.TickCount64 + Math.Max(1, delaySeconds) * 1000L);
        }
        else
        {
            DeleteMemory(mem);
        }
        return dealt;
    }

    private void DeleteMemory(Item mem)
    {
        var world = ObjBase.ResolveWorld?.Invoke();
        if (world != null) world.DeleteObject(mem);
        else if (_owner.GetEquippedItem(Layer.FlagPoison) == mem) _owner.Unequip(Layer.FlagPoison);
    }

    /// <summary>Upstream's Emote/Emote2 with the owner's client excluded: bystanders
    /// read "*You see NAME ...*", the victim gets the "You feel" line instead.</summary>
    private void EmoteToOthers(string text)
    {
        Character.BroadcastNearby?.Invoke(_owner.Position, 18,
            new SphereNet.Network.Packets.Outgoing.PacketSpeechOut(
                _owner.Uid.Value, _owner.BodyId, 2, HueTextDef, 3, _owner.GetName(), text),
            _owner.Uid.Value);
    }

    /// <summary>EMOTEF_POISON: only the affected character sees the poison emote.</summary>
    public const int EmoteFlagPoison = 0x02;

    private static readonly int[] PoisonMin = [2, 4, 6, 8, 10];

    private static readonly string[] OsiSelf =
    [
        Msg.SpellOsipoisonLesser, Msg.SpellOsipoisonStandard, Msg.SpellOsipoisonGreater,
        Msg.SpellOsipoisonDeadly, Msg.SpellOsipoisonLethal,
    ];

    private static readonly string[] OsiOther =
    [
        Msg.SpellOsipoisonLesser1, Msg.SpellOsipoisonStandard1, Msg.SpellOsipoisonGreater1,
        Msg.SpellOsipoisonDeadly1, Msg.SpellOsipoisonLethal1,
    ];

    private static readonly string[] Looks =
    [
        Msg.SpellPoison1, Msg.SpellPoison2, Msg.SpellPoison3, Msg.SpellPoison4, Msg.SpellPoison5,
    ];
}
