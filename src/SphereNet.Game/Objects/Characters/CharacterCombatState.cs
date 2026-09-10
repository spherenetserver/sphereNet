using SphereNet.Core.Enums;
using SphereNet.Core.Types;

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

    private long _criminalTimer;       // TickCount64 when criminal flag expires (0 = not criminal)
    private short _kills;              // murder count
    private long _nextMurderDecayTick; // next TickCount64 at which one kill will decay

    public CharacterCombatState(Character owner)
    {
        _owner = owner;
    }

    // --- Notoriety counters ---

    public short Kills { get => _kills; set => _kills = (short)Math.Max(0, (int)value); }

    public bool IsCriminal => _criminalTimer > 0 && Environment.TickCount64 < _criminalTimer;

    // Source-X Noto_IsMurderer: murders must EXCEED the threshold (m_wMurders >
    // m_iMurderMinCount), so with the default 5 the red title appears on the 6th
    // kill, not the 5th. Using >= flagged red one kill too early.
    public bool IsMurderer => _kills > Character.MurderMinCount;

    public int CriminalTimerRemainingSeconds
    {
        get
        {
            if (_criminalTimer <= 0) return 0;
            long remain = _criminalTimer - Environment.TickCount64;
            return remain > 0 ? (int)Math.Min(remain / 1000, int.MaxValue) : 0;
        }
        set => _criminalTimer = value > 0 ? Environment.TickCount64 + value * 1000L : 0;
    }

    /// <summary>Seconds until the next murder count decays off. Persisted so a
    /// murderer's kills keep ageing across save/load instead of restarting the
    /// full decay window every reload (Source-X stores the decay timer).</summary>
    public int MurderDecayRemainingSeconds
    {
        get
        {
            if (_nextMurderDecayTick <= 0) return 0;
            long remain = _nextMurderDecayTick - Environment.TickCount64;
            return remain > 0 ? (int)Math.Min(remain / 1000, int.MaxValue) : 0;
        }
        set => _nextMurderDecayTick = value > 0 ? Environment.TickCount64 + value * 1000L : 0;
    }

    /// <summary>Arm/refresh the criminal timer (duration in ms).</summary>
    public void SetCriminal(long durationMs) =>
        _criminalTimer = durationMs > 0
            ? Environment.TickCount64 + Math.Min(durationMs, long.MaxValue - Environment.TickCount64)
            : 0;

    /// <summary>FORGIVE verb: clear the murder count and the criminal timer.</summary>
    public void Forgive()
    {
        _kills = 0;
        _criminalTimer = 0;
    }

    /// <summary>Clear the criminal timer when expired (per-tick check that
    /// does not touch the stat flag — TickNotorietyDecay handles that).</summary>
    public void ExpireCriminalTimer(long nowMs)
    {
        if (_criminalTimer > 0 && nowMs >= _criminalTimer)
            _criminalTimer = 0;
    }

    /// <summary>Called once per world tick. Clears the expired criminal flag
    /// and decays one kill every MurderDecayTimeSeconds of online time.</summary>
    public void TickNotorietyDecay(long nowMs)
    {
        if (_criminalTimer > 0 && nowMs >= _criminalTimer)
        {
            _criminalTimer = 0;
            if (_owner.IsStatFlag(StatFlag.Criminal))
                _owner.ClearStatFlag(StatFlag.Criminal);
        }

        if (_kills > 0 && Character.MurderDecayTimeSeconds > 0)
        {
            if (_nextMurderDecayTick == 0)
                _nextMurderDecayTick = nowMs + Character.MurderDecayTimeSeconds * 1000L;
            else if (nowMs >= _nextMurderDecayTick)
            {
                _kills--;
                // @MurderDecay may override the seconds until the next decay (ARGN2);
                // 0 / no handler falls back to the configured default interval.
                int nextOverride = Character.OnMurderDecay?.Invoke(_owner, _kills) ?? 0;
                long interval = nextOverride > 0 ? nextOverride : Character.MurderDecayTimeSeconds;
                _nextMurderDecayTick = nowMs + interval * 1000L;
            }
        }
        else
        {
            _nextMurderDecayTick = 0;
        }
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
                // Insertion order is STABLE (Source-X Attacker_Add only ever appends).
                // It has to be: ATTACKER.n is the handle a script holds between two
                // lines, and moving the entry that just took a hit to the end renumbered
                // every other one under it. ATTACKER.LAST is resolved from the last-hit
                // stamp instead (CChar.cpp:2463 walks the list looking for it).
                _attackers[i] = new AttackerRecord(attackerUid, total, now, ignored,
                    _attackers[i].Threat);
                return;
            }
        }
        // First blow from someone not yet on the list: the same add the engagement
        // path takes, so @CombatAdd fires exactly once per participant and can veto
        // or reweight it here too.
        var ctx = new CombatAddContext();
        if (Character.OnCombatAdd != null && !Character.OnCombatAdd(_owner, attackerUid, ctx))
            return;
        _attackers.Add(new AttackerRecord(attackerUid, Math.Min(damage, int.MaxValue), now,
            ctx.Ignore, _owner.IsPlayer ? 0 : ctx.Threat));
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
