using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Objects.Characters;

/// <summary>Mutable @CombatAdd arguments: ARGN1 threat, ARGN2 ignore. The engine
/// seeds them, the script may rewrite them, and the engine reads them back
/// (Source-X CCharAttacker.cpp:38/:45).</summary>
public sealed class CombatAddContext
{
    public int Threat { get; set; }
    public bool Ignore { get; set; }
}

/// <summary>Mutable @Attack arguments: ARGN1 threat, ARGN2 ignore, both read back
/// (Source-X CCharFight.cpp:1433/:1437).</summary>
public sealed class AttackTriggerContext
{
    public int Threat { get; set; }
    public bool Ignore { get; set; }
}

/// <summary>One attacker-log entry (Source-X CChar::m_lastAttackers).</summary>
public readonly struct AttackerRecord(Serial uid, int totalDamage, long lastHitTick,
    bool ignored = false, int threat = 0)
{
    public Serial Uid { get; } = uid;
    public int TotalDamage { get; } = totalDamage;
    public long LastHitTick { get; } = lastHitTick;
    /// <summary>Script-set ATTACKER.n.IGNORE flag. A hit from an ignored
    /// attacker fires @HitIgnore on the victim instead of passing silently.</summary>
    public bool Ignored { get; } = ignored;
    /// <summary>How badly this NPC wants to fight this attacker
    /// (Source-X LastAttackers.threat, script key ATTACKER.n.THREAT). It is a
    /// STORED value, not a derived one: a script sets it, @Attack can rewrite it,
    /// and a pet told by its master carries
    /// <see cref="CharacterCombatState.ThreatToldByMaster"/>. Only an NPC keeps
    /// one - the reference refuses the write on a player (CCharAttacker.cpp:205).</summary>
    public int Threat { get; } = threat;
}

/// <summary>
/// Combat-related runtime state extracted from Character: the per-attacker
/// damage log (ATTACKER.*) and the criminal/murder notoriety counters.
/// First slice of the Character decomposition — Character keeps thin
/// delegating members, so the public API and script surface are unchanged.
/// Static hooks (OnCombatAdd, OnHitIgnored, OnMurderDecay, ...) stay on
/// Character; this class invokes them with its owner.
/// </summary>
public sealed class CharacterCombatState
{
    private readonly Character _owner;

    // Per-attacker damage log. Entries accumulate while combat is active;
    // cleared by ClearAttackers (death, resurrect, or script). Insertion
    // order is preserved so ATTACKER.LAST reads the most-recent hit.
    private readonly List<AttackerRecord> _attackers = new();

    private short _kills;              // murder count (Source-X CCharPlayer::m_wMurders)

    // A criminal timer / murder countdown read from a save written before these were
    // worn memories (CRIMINALTIMER= / MURDERDECAY= on the character, in seconds). Made
    // into its memory once the world is loaded (FixNotorietyAfterLoad) or on the
    // character's next tick - creating an item while a save is still being read could
    // take a uid a later record owns.
    private int _pendingCriminalSeconds;
    private int _pendingMurderDecaySeconds;

    /// <summary>The names Spell_Effect_Create gives the two notoriety memories
    /// (CCharSpell.cpp:2089/:2093).</summary>
    public const string CriminalMemoryName = "Criminal Timer";
    public const string MurderMemoryName = "Murder Decay";
    private const ushort FallbackMemoryId = 0x2053; // ITEMID_RHAND_POINT_NW

    public CharacterCombatState(Character owner)
    {
        _owner = owner;
    }

    // --- Notoriety counters ---
    //
    // Source-X keeps both notoriety clocks as worn IT_SPELL memories made by
    // Spell_Effect_Create: the "Criminal Timer" on LAYER_FLAG_Criminal (43), whose
    // presence IS STATF_CRIMINAL (LayerAdd sets the flag, OnRemoveObj clears it,
    // CCharAct.cpp:347/:455), and the "Murder Decay" on LAYER_FLAG_Murders (52), whose
    // timeout ages one murder off (OnTickEquip, CCharAct.cpp:4113). They are ordinary
    // equipped items here too - saved, loaded and ticked as such - so a Source-X save
    // carrying them loads into working clocks and SphereNet writes them the same way.

    public short Kills { get => _kills; set => _kills = (short)Math.Max(0, (int)value); }

    /// <summary>The worn "Criminal Timer" memory, if any.</summary>
    public Item? CriminalMemory => Worn(Layer.FlagCriminal);

    /// <summary>The worn "Murder Decay" memory, if any.</summary>
    public Item? MurderMemory => Worn(Layer.FlagMurders);

    private Item? Worn(Layer layer)
    {
        var item = _owner.GetEquippedItem(layer);
        return item != null && !item.IsDeleted ? item : null;
    }

    public bool IsCriminal => CriminalMemory != null || _pendingCriminalSeconds > 0;

    // Source-X Noto_IsMurderer: murders must EXCEED the threshold (m_wMurders >
    // m_iMurderMinCount), so with the default 5 the red title appears on the 6th
    // kill, not the 5th. Using >= flagged red one kill too early.
    public bool IsMurderer => _kills > Character.MurderMinCount;

    /// <summary>A legacy CRIMINALTIMER= value not yet made into its memory (the saver
    /// writes it back only in that state; a worn memory saves itself).</summary>
    public int PendingCriminalSeconds => _pendingCriminalSeconds;

    /// <summary>A legacy MURDERDECAY= value not yet made into its memory.</summary>
    public int PendingMurderDecaySeconds => _pendingMurderDecaySeconds;

    /// <summary>Seconds left on the criminal memory (its TIMER).</summary>
    public int CriminalTimerRemainingSeconds
    {
        get
        {
            var mem = CriminalMemory;
            return mem != null ? RemainingSeconds(mem) : _pendingCriminalSeconds;
        }
        set
        {
            var mem = CriminalMemory;
            if (value <= 0)
            {
                _pendingCriminalSeconds = 0;
                RemoveMemory(mem);
            }
            else if (mem != null)
            {
                mem.SetTimeout(Environment.TickCount64 + value * 1000L);
            }
            else
            {
                _pendingCriminalSeconds = value;
            }
        }
    }

    /// <summary>Seconds until the next murder count decays off: the murder memory's
    /// TIMER, or - while its owner is offline and the clock is stopped - the balance
    /// it keeps in MORE1 (m_itEqMurderCount.m_dwDecayBalance).</summary>
    public int MurderDecayRemainingSeconds
    {
        get
        {
            var mem = MurderMemory;
            return mem != null ? RemainingSeconds(mem) : _pendingMurderDecaySeconds;
        }
        set
        {
            var mem = MurderMemory;
            if (value <= 0)
            {
                _pendingMurderDecaySeconds = 0;
                RemoveMemory(mem);
            }
            else if (mem != null)
            {
                if (ClockStopped(mem))
                    mem.More1 = (uint)value;
                else
                    mem.SetTimeout(Environment.TickCount64 + value * 1000L);
            }
            else
            {
                _pendingMurderDecaySeconds = value;
            }
        }
    }

    /// <summary>A memory whose timer is not set (no TIMER in the save, or stopped at
    /// logout) - upstream's "timeout -1".</summary>
    private static bool ClockStopped(Item mem) => mem.Timeout <= 0;

    private static int RemainingSeconds(Item mem)
    {
        if (ClockStopped(mem))
            return mem.EquipLayer == Layer.FlagMurders ? (int)Math.Min(mem.More1, int.MaxValue) : 0;
        long remain = mem.Timeout - Environment.TickCount64;
        return remain > 0 ? (int)Math.Min(remain / 1000, int.MaxValue) : 0;
    }

    /// <summary>Arm/refresh the criminal memory (duration in ms) - the
    /// Spell_Effect_Create(SPELL_NONE, LAYER_FLAG_Criminal, ...) of Noto_Criminal
    /// (CCharNotoriety.cpp:420). Zero or less removes it.</summary>
    public void SetCriminal(long durationMs)
    {
        _pendingCriminalSeconds = 0;
        if (durationMs <= 0)
        {
            RemoveMemory(CriminalMemory);
            return;
        }
        CreateFlagMemory(Layer.FlagCriminal, CriminalMemoryName, durationMs);
    }

    /// <summary>Source-X CChar::Noto_Murder (CCharNotoriety.cpp:379-387): the
    /// "Murderer!" notice once the count is past MURDERMINCOUNT, and - while a player
    /// has murders - the murder memory (re)made with the full MURDERDECAYTIME.
    /// Spell_Effect_Create deletes the previous one first, so every call restarts the
    /// countdown. Run after a murder mark and when a player enters the world without
    /// a murder memory.</summary>
    public void NotoMurder()
    {
        if (IsMurderer)
            Character.SendOwnerMessage?.Invoke(_owner,
                SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.MsgMurderer));
        if (_owner.IsPlayer && _kills > 0)
        {
            _pendingMurderDecaySeconds = 0;
            CreateFlagMemory(Layer.FlagMurders, MurderMemoryName, Character.MurderDecayTimeSeconds * 1000L);
        }
    }

    /// <summary>The murder-memory half of CClient::Announce (CClient.cpp:388-401):
    /// entering the world restarts a murder memory's clock from the balance it kept
    /// (SetTimeoutS(MORE1)) or, with none, runs Noto_Murder; leaving stores the time
    /// left in MORE1 and stops the clock (TIMER -1), so murders age by time online.</summary>
    public void OnClientAnnounce(bool arrive)
    {
        MaterializePending();
        var mem = MurderMemory;
        if (mem != null)
        {
            if (arrive)
            {
                mem.SetTimeout(Environment.TickCount64 + (long)mem.More1 * 1000L);
            }
            else if (!ClockStopped(mem))
            {
                mem.More1 = (uint)RemainingSeconds(mem);
                mem.SetTimeout(-1);
            }
        }
        else if (arrive)
        {
            NotoMurder();
        }
    }

    /// <summary>Spell_Effect_Create(SPELL_NONE, layer, GetSpellEffect(SPELL_NONE, 0),
    /// duration) (CCharSpell.cpp:2040-2113) for the two notoriety layers: drop what is
    /// on the layer (a TIMER=-1 one is only removed - casting again toggles it off),
    /// make the IT_SPELL memory and wear it with the duration as its timer.</summary>
    private Item? CreateFlagMemory(Layer layer, string name, long durationMs,
        SphereNet.Game.World.GameWorld? world = null)
    {
        world ??= ObjBase.ResolveWorld?.Invoke();
        if (world == null)
            return null;

        var prev = Worn(layer);
        if (prev != null)
        {
            bool toggle = ClockStopped(prev);
            world.DeleteObject(prev);
            if (toggle)
                return null;
        }

        // The duration travels in tenths of a second (iDurationInTenths).
        long tenthsMs = Math.Max(0, durationMs) / 100 * 100;
        var def = Character.ResolveSpellDef?.Invoke(SpellType.None);
        var mem = world.CreateItem();
        mem.BaseId = def?.RuneItemId is > 0 ? def.RuneItemId : FallbackMemoryId;
        mem.Name = name;
        mem.SetAttr(def != null ? ObjAttributes.Newbie | ObjAttributes.Magic : ObjAttributes.Newbie);
        mem.ItemType = ItemType.Spell;
        mem.MoreP = Point3D.Zero; // MOREX = SPELL_NONE, MOREY = GetSpellEffect(SPELL_NONE, 0) = 0
        mem.More2 = 1;            // m_spellcharges
        world.LastNewObject = mem.Uid;
        if (!_owner.Equip(mem, layer))
        {
            world.DeleteObject(mem);
            return null;
        }
        mem.SetTimeout(Environment.TickCount64 + tenthsMs);
        return mem;
    }

    private void RemoveMemory(Item? mem)
    {
        if (mem == null || mem.IsDeleted)
            return;
        var world = ObjBase.ResolveWorld?.Invoke();
        if (world != null)
            world.DeleteObject(mem);
        else
            _owner.Unequip(mem.EquipLayer);
    }

    /// <summary>FORGIVE verb: clear the murder count and the criminal state.</summary>
    public void Forgive()
    {
        _kills = 0;
        _pendingCriminalSeconds = 0;
        _pendingMurderDecaySeconds = 0;
        RemoveMemory(MurderMemory);
        RemoveMemory(CriminalMemory);
    }

    /// <summary>Make a legacy CRIMINALTIMER= / MURDERDECAY= value into its memory. A
    /// worn memory wins over a legacy value; a murder countdown needs murders, and a
    /// player who is not in the world gets it with the clock stopped and the balance
    /// in MORE1, as CClient::Announce(false) leaves it.</summary>
    public void MaterializePending(SphereNet.Game.World.GameWorld? world = null)
    {
        if (_pendingCriminalSeconds > 0)
        {
            int seconds = _pendingCriminalSeconds;
            _pendingCriminalSeconds = 0;
            if (CriminalMemory == null)
                CreateFlagMemory(Layer.FlagCriminal, CriminalMemoryName, seconds * 1000L, world);
        }
        if (_pendingMurderDecaySeconds > 0)
        {
            int seconds = _pendingMurderDecaySeconds;
            _pendingMurderDecaySeconds = 0;
            if (MurderMemory == null && _owner.IsPlayer && _kills > 0)
            {
                var mem = CreateFlagMemory(Layer.FlagMurders, MurderMemoryName, seconds * 1000L, world);
                if (mem != null && !_owner.IsOnline)
                {
                    mem.More1 = (uint)seconds;
                    mem.SetTimeout(-1);
                }
            }
        }
    }

    /// <summary>The load-time repair upstream runs on every object (FixWeirdness):
    /// legacy timers become memories, a murder memory on a character with no murders
    /// is deleted (CItem.cpp:1214, 0x2235), and STATF_CRIMINAL without a criminal
    /// memory is cleared (CChar.cpp:974-979).</summary>
    public void FixNotorietyAfterLoad(SphereNet.Game.World.GameWorld world)
    {
        MaterializePending(world);
        var murders = MurderMemory;
        if (murders != null && (!_owner.IsPlayer || _kills <= 0))
            world.DeleteObject(murders);
        if (_owner.IsStatFlag(StatFlag.Criminal) && CriminalMemory == null)
            _owner.ClearStatFlag(StatFlag.Criminal);
    }

    /// <summary>Character tick: run a criminal/murder memory that has come due.</summary>
    public void ExpireCriminalTimer(long nowMs) => TickNotorietyDecay(nowMs);

    /// <summary>Run the notoriety memories that have come due by <paramref name="nowMs"/>
    /// (the worn items also tick on their own timers through Item.OnTick; this is the
    /// same step for a caller driving the clock), after making any legacy value into
    /// its memory.</summary>
    public void TickNotorietyDecay(long nowMs)
    {
        MaterializePending();
        var criminal = CriminalMemory;
        if (criminal != null && criminal.Timeout > 0 && nowMs >= criminal.Timeout)
            EquipTick(criminal, nowMs);
        var murders = MurderMemory;
        if (murders != null && murders.Timeout > 0 && nowMs >= murders.Timeout)
            EquipTick(murders, nowMs);
    }

    /// <summary>A notoriety memory's timer came due (CChar::OnTickEquip).
    ///
    /// LAYER_FLAG_Murders (CCharAct.cpp:4113-4134): one murder ages off - @MurderDecay
    /// with ARGN1 = the new count and ARGN2 = the time to the next decay, both read
    /// back - and the memory goes when the count reaches 0, else waits ARGN2 again.
    ///
    /// LAYER_FLAG_Criminal has no case there: it falls to Spell_Equip_OnTick, which for
    /// SPELL_NONE (no tick flag) answers "kill the spell" - the memory is deleted and
    /// its removal clears STATF_CRIMINAL.</summary>
    public void EquipTick(Item mem, long nowMs)
    {
        if (mem.IsDeleted)
            return;
        mem.SetTimeout(0);
        if (mem.EquipLayer == Layer.FlagMurders)
        {
            if (!_owner.IsPlayer || _kills <= 0)
            {
                RemoveMemory(mem);
                return;
            }
            _kills--;
            // @MurderDecay may rewrite the count (the hook writes Kills back) and the
            // seconds to the next decay (0 = MURDERDECAYTIME).
            int nextOverride = Character.OnMurderDecay?.Invoke(_owner, _kills) ?? 0;
            Character.NotoSaveUpdate?.Invoke(_owner);
            if (_kills == 0)
            {
                RemoveMemory(mem);
                return;
            }
            long intervalMs = nextOverride > 0 ? nextOverride * 1000L : Character.MurderDecayTimeSeconds * 1000L;
            mem.SetTimeout(nowMs + Math.Max(0, intervalMs));
            return;
        }
        RemoveMemory(mem);
    }

    // --- Attacker log ---

    /// <summary>Read-only view of the current attacker log. Most recent hit
    /// is at the end of the list (ATTACKER.LAST).</summary>
    public IReadOnlyList<AttackerRecord> Attackers => _attackers;

    /// <summary>Add <paramref name="damage"/> to the running total for
    /// <paramref name="attackerUid"/> and stamp the current tick. Called
    /// from combat / spell damage paths. No-op for self-damage.</summary>
    public void RecordAttack(Serial attackerUid, int damage)
    {
        if (attackerUid == _owner.Uid || attackerUid == Serial.Invalid || damage <= 0)
            return;
        // Being hit lets an idle NPC re-acquire immediately (bypass the
        // target-scan throttle), so retaliation is never delayed.
        _owner.NextNpcReacquireTime = 0;
        long now = Environment.TickCount64;
        for (int i = 0; i < _attackers.Count; i++)
        {
            if (_attackers[i].Uid == attackerUid)
            {
                bool ignored = _attackers[i].Ignored;
                if (ignored && Character.OnHitIgnored != null && Character.OnHitIgnored(_owner, attackerUid))
                    ignored = false; // script un-ignored the attacker
                int total = (int)Math.Min((long)_attackers[i].TotalDamage + damage, int.MaxValue);
                // OnTakeDamage grows amountDone AND threat by the damage for an entry
                // already on the list (CCharFight.cpp:923-927) - so a threat a script
                // set with ATTACKER.n.THREAT keeps rising with every further blow.
                int threat = (int)Math.Clamp((long)_attackers[i].Threat + damage, int.MinValue, int.MaxValue);
                // Insertion order is STABLE (Source-X Attacker_Add only ever appends).
                // It has to be: ATTACKER.n is the handle a script holds between two
                // lines, and moving the entry that just took a hit to the end renumbered
                // every other one under it. ATTACKER.LAST is resolved from the last-hit
                // stamp instead (CChar.cpp:2463 walks the list looking for it).
                _attackers[i] = new AttackerRecord(attackerUid, total, now, ignored, threat);
                return;
            }
        }
        // First blow from someone not yet on the list: the same add the engagement
        // path takes (Attacker_Add, CCharAttacker.cpp:36-55 - reached in Source-X
        // through OnAttackedBy before the damage lands), so @CombatAdd fires exactly
        // once per participant and can veto or reweight it here too. The add's
        // threat weight (0 on a player's own list) then grows by this blow's damage,
        // as every blow does (CCharFight.cpp:923-938).
        var ctx = new CombatAddContext();
        if (Character.OnCombatAdd != null && !Character.OnCombatAdd(_owner, attackerUid, ctx))
            return;
        int baseThreat = _owner.IsPlayer ? 0 : ctx.Threat;
        _attackers.Add(new AttackerRecord(attackerUid, Math.Min(damage, int.MaxValue), now,
            ctx.Ignore, (int)Math.Clamp((long)baseThreat + damage, int.MinValue, int.MaxValue)));
    }

    /// <summary>Set/clear the ATTACKER.n.IGNORE flag for an attacker already
    /// in the log. Returns false when the uid is not an attacker.</summary>
    public bool SetAttackerIgnored(Serial attackerUid, bool ignored)
    {
        int i = IndexOfAttacker(attackerUid);
        if (i < 0) return false;
        var rec = _attackers[i];
        _attackers[i] = new AttackerRecord(rec.Uid, rec.TotalDamage, rec.LastHitTick, ignored, rec.Threat);
        return true;
    }

    /// <summary>Threat an NPC assigns to a target its master pointed it at.
    /// Source-X ATTACKER_THREAT_TOLDBYMASTER (CChar.h:1132): the order adds this
    /// ON TOP of the highest threat the pet already holds, so nothing on the list
    /// can outbid it.</summary>
    public const int ThreatToldByMaster = 1000;

    /// <summary>Index of an attacker in the log, or -1 (Source-X Attacker_GetID).</summary>
    public int IndexOfAttacker(Serial attackerUid)
    {
        for (int i = 0; i < _attackers.Count; i++)
            if (_attackers[i].Uid == attackerUid)
                return i;
        return -1;
    }

    /// <summary>Index of the most recent hit (ATTACKER.LAST), or -1.</summary>
    public int LastAttackerIndex()
    {
        int best = -1;
        for (int i = 0; i < _attackers.Count; i++)
            if (best < 0 || _attackers[i].LastHitTick >= _attackers[best].LastHitTick)
                best = i;
        return best;
    }

    /// <summary>Index of the heaviest damage dealer (ATTACKER.MAX), or -1.</summary>
    public int MaxDamageAttackerIndex()
    {
        int best = -1;
        for (int i = 0; i < _attackers.Count; i++)
            if (best < 0 || _attackers[i].TotalDamage > _attackers[best].TotalDamage)
                best = i;
        return best;
    }

    /// <summary>Highest threat currently on the log (Source-X
    /// Attacker_GetHighestThreat); 0 when the log is empty.</summary>
    public int HighestThreat()
    {
        int high = 0;
        foreach (var rec in _attackers)
            if (rec.Threat > high) high = rec.Threat;
        return high;
    }

    /// <summary>Threat of one entry, clamped at 0 (Source-X Attacker_GetThreat
    /// reports a negative stored value as 0); -1 for an index off the end.</summary>
    public int GetAttackerThreat(int index) =>
        index < 0 || index >= _attackers.Count ? -1 : Math.Max(0, _attackers[index].Threat);

    /// <summary>ATTACKER.n.THREAT=. A PLAYER never keeps one: the reference returns
    /// before the write (CCharAttacker.cpp:205), because threat exists only to steer
    /// the target an NPC picks.</summary>
    public bool SetAttackerThreat(int index, int value)
    {
        if (_owner.IsPlayer || index < 0 || index >= _attackers.Count)
            return false;
        var rec = _attackers[index];
        _attackers[index] = new AttackerRecord(rec.Uid, rec.TotalDamage, rec.LastHitTick, rec.Ignored, value);
        return true;
    }

    /// <summary>ATTACKER.n.DAM= - overwrite the running damage total.</summary>
    public bool SetAttackerDamage(int index, int value)
    {
        if (index < 0 || index >= _attackers.Count) return false;
        var rec = _attackers[index];
        _attackers[index] = new AttackerRecord(rec.Uid, value, rec.LastHitTick, rec.Ignored, rec.Threat);
        return true;
    }

    /// <summary>ATTACKER.n.ELAPSED= - how many SECONDS ago this attacker last hit.
    /// It is kept here as a tick stamp, so the value is applied backwards from now;
    /// that keeps the getter and the setter talking about the same thing.</summary>
    public bool SetAttackerElapsed(int index, long seconds)
    {
        if (index < 0 || index >= _attackers.Count) return false;
        var rec = _attackers[index];
        _attackers[index] = new AttackerRecord(rec.Uid, rec.TotalDamage,
            Environment.TickCount64 - Math.Max(0, seconds) * 1000L, rec.Ignored, rec.Threat);
        return true;
    }

    /// <summary>ATTACKER.n.DELETE / ATTACKER.DELETE &lt;uid&gt; - drop one entry.</summary>
    public bool RemoveAttacker(int index)
    {
        if (index < 0 || index >= _attackers.Count) return false;
        _attackers.RemoveAt(index);
        return true;
    }

    /// <summary>Whether this attacker is flagged ignored; false when unknown.</summary>
    public bool IsAttackerIgnored(Serial attackerUid)
    {
        int i = IndexOfAttacker(attackerUid);
        return i >= 0 && _attackers[i].Ignored;
    }

    /// <summary>Source-X Attacker_Add (CCharAttacker.cpp:11): put
    /// <paramref name="uid"/> on the combat-participant list.
    ///
    /// The list runs BOTH WAYS upstream: Fight_Attack adds the character you
    /// engage, so a target is on your list before it has ever touched you. That is
    /// what lets an NPC pick its next opponent off the list when the current one
    /// dies, instead of looking the world over again. An entry that already exists
    /// is left alone (upstream returns early), so a repeated order cannot re-seed
    /// the threat.
    ///
    /// Returns false when @CombatAdd vetoed the add (RETURN 1, :43).</summary>
    public bool AddAttacker(Serial uid, int threat = 0)
    {
        if (uid == _owner.Uid || uid == Serial.Invalid)
            return true;
        if (IndexOfAttacker(uid) >= 0)
            return true;

        var ctx = new CombatAddContext { Threat = threat, Ignore = false };
        if (Character.OnCombatAdd != null && !Character.OnCombatAdd(_owner, uid, ctx))
            return false;

        // A player never carries threat (CCharAttacker.cpp:205 refuses the write,
        // and the add itself zeroes it at :53).
        _attackers.Add(new AttackerRecord(uid, 0, Environment.TickCount64,
            ctx.Ignore, _owner.IsPlayer ? 0 : ctx.Threat));
        return true;
    }

    /// <summary>Source-X Fight_Attack's engagement contract (CCharFight.cpp:1422
    /// -1450), the part that is about the attacker LIST rather than about war mode
    /// and skills: work out the threat, let @Attack rewrite it (and the ignore
    /// flag) when the target is CHANGING, then commit both to the list.
    ///
    /// Returns false when the engagement must not proceed - @Attack or @CombatAdd
    /// returned 1, or the target ends up flagged ignored.</summary>
    public bool BeginFightWith(Character target, bool toldByMaster)
    {
        // An order from the owner outbids everything already on the list, which is
        // the whole point of the constant (CCharFight.cpp:1425).
        int threat = toldByMaster ? ThreatToldByMaster + HighestThreat() : 0;
        bool ignored = IsAttackerIgnored(target.Uid);

        // Only on a CHANGE of target: re-confirming the current one is an
        // acknowledgement, not a new attack (:1430).
        if (_owner.FightTarget != target.Uid && Character.OnAttackTrigger != null)
        {
            var ctx = new AttackTriggerContext { Threat = threat, Ignore = ignored };
            if (!Character.OnAttackTrigger(_owner, target, ctx))
                return false;
            threat = ctx.Threat;
            ignored = ctx.Ignore;
        }

        SetAttackerIgnored(target.Uid, ignored);   // no-op while it is not on the list
        if (!AddAttacker(target.Uid, threat))
            return false;
        return !IsAttackerIgnored(target.Uid);
    }

    public void ClearAttackers() => _attackers.Clear();

    /// <summary>Re-add a saved attacker entry on world load — no reacquire
    /// bump, no @CombatAdd, no last-hit refresh side effects (unlike
    /// <see cref="RecordAttack"/>); the last-hit tick restarts at load time.</summary>
    public void RestoreAttacker(Serial attackerUid, int totalDamage, bool ignored, int threat = 0)
    {
        if (attackerUid == _owner.Uid || attackerUid == Serial.Invalid || totalDamage <= 0)
            return;
        for (int i = 0; i < _attackers.Count; i++)
            if (_attackers[i].Uid == attackerUid)
                return;
        _attackers.Add(new AttackerRecord(attackerUid, Math.Min(totalDamage, int.MaxValue),
            Environment.TickCount64, ignored, threat));
    }

    /// <summary>Index of <paramref name="uid"/> in the attacker log, or -1.</summary>
    public int GetIndex(Serial uid)
    {
        for (int i = 0; i < _attackers.Count; i++)
            if (_attackers[i].Uid == uid) return i;
        return -1;
    }

    /// <summary>Seconds since the last hit from <paramref name="uid"/>, or -1 when unknown.</summary>
    public long GetElapsedSeconds(Serial uid)
    {
        int idx = GetIndex(uid);
        if (idx < 0) return -1;
        return Math.Max(0L, (Environment.TickCount64 - _attackers[idx].LastHitTick) / 1000L);
    }

    /// <summary>Remove one attacker entry (fight retreat / timeout).</summary>
    public void Delete(Serial uid)
    {
        int idx = GetIndex(uid);
        if (idx >= 0)
        {
            _attackers.RemoveAt(idx);
            Character.OnCombatDelete?.Invoke(_owner, uid);
            if (_attackers.Count == 0)
                Character.OnCombatEnd?.Invoke(_owner);
        }
    }
}
