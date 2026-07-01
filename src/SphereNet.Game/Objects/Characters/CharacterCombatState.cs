using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Game.Objects.Characters;

/// <summary>One attacker-log entry (Source-X CChar::m_lastAttackers).</summary>
public readonly struct AttackerRecord(Serial uid, int totalDamage, long lastHitTick, bool ignored = false)
{
    public Serial Uid { get; } = uid;
    public int TotalDamage { get; } = totalDamage;
    public long LastHitTick { get; } = lastHitTick;
    /// <summary>Script-set ATTACKER.n.IGNORE flag. A hit from an ignored
    /// attacker fires @HitIgnore on the victim instead of passing silently.</summary>
    public bool Ignored { get; } = ignored;
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

    public short Kills { get => _kills; set => _kills = value; }

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
            return remain > 0 ? (int)(remain / 1000) : 0;
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
            return remain > 0 ? (int)(remain / 1000) : 0;
        }
        set => _nextMurderDecayTick = value > 0 ? Environment.TickCount64 + value * 1000L : 0;
    }

    /// <summary>Arm/refresh the criminal timer (duration in ms).</summary>
    public void SetCriminal(long durationMs) =>
        _criminalTimer = Environment.TickCount64 + durationMs;

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
                _attackers[i] = new AttackerRecord(attackerUid, _attackers[i].TotalDamage + damage, now, ignored);
                // Move this entry to the end so ATTACKER.LAST reflects it
                if (i != _attackers.Count - 1)
                {
                    var rec = _attackers[i];
                    _attackers.RemoveAt(i);
                    _attackers.Add(rec);
                }
                return;
            }
        }
        _attackers.Add(new AttackerRecord(attackerUid, damage, now));
        Character.OnCombatAdd?.Invoke(_owner, attackerUid);
    }

    /// <summary>Set/clear the ATTACKER.n.IGNORE flag for an attacker already
    /// in the log. Returns false when the uid is not an attacker.</summary>
    public bool SetAttackerIgnored(Serial attackerUid, bool ignored)
    {
        for (int i = 0; i < _attackers.Count; i++)
        {
            if (_attackers[i].Uid == attackerUid)
            {
                var rec = _attackers[i];
                _attackers[i] = new AttackerRecord(rec.Uid, rec.TotalDamage, rec.LastHitTick, ignored);
                return true;
            }
        }
        return false;
    }

    public void ClearAttackers() => _attackers.Clear();

    /// <summary>Re-add a saved attacker entry on world load — no reacquire
    /// bump, no @CombatAdd, no last-hit refresh side effects (unlike
    /// <see cref="RecordAttack"/>); the last-hit tick restarts at load time.</summary>
    public void RestoreAttacker(Serial attackerUid, int totalDamage, bool ignored)
    {
        if (attackerUid == _owner.Uid || attackerUid == Serial.Invalid)
            return;
        for (int i = 0; i < _attackers.Count; i++)
            if (_attackers[i].Uid == attackerUid)
                return;
        _attackers.Add(new AttackerRecord(attackerUid, totalDamage, Environment.TickCount64, ignored));
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
