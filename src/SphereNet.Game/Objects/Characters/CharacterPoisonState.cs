using SphereNet.Core.Enums;
using SphereNet.Core.Types;

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

    /// <summary>Strength on the Sphere memory scale (ARGN2 / ARGO.MOREY).</summary>
    public int Strength { get; init; }

    /// <summary>Applier UID for ARGO.LINK; invalid when unattributed.</summary>
    public Core.Types.Serial SourceUid { get; init; }

    /// <summary>Damage this tick, post-resist (LOCAL.EFFECT). Script value
    /// is applied as-is — no second resist pass.</summary>
    public int Damage { get; set; }

    /// <summary>Delay until the next tick in ms (LOCAL.DELAY, seconds in script).</summary>
    public int DelayMs { get; set; }

    /// <summary>Ticks left including this one (LOCAL.CHARGES; the engine
    /// auto-decrements by one after the tick, Source-X parity).</summary>
    public int Charges { get; set; }
}

/// <summary>
/// Poison runtime state extracted from Character (decomposition slice 2):
/// level, tick schedule and the poisoner for kill attribution. Character
/// keeps thin delegating members (ApplyPoison/CurePoison/ProcessPoisonTick,
/// PoisonLevel, IsPoisoned), so the public API and behaviour are unchanged —
/// the logic moved verbatim. Static hooks (BroadcastNearby, ResolveCharByUid,
/// OnLifecycleKill) stay on Character and are invoked with the owner.
/// </summary>
public sealed class CharacterPoisonState
{
    private readonly Character _owner;

    private byte _level; // 0=none, 1=lesser, 2=normal, 3=greater, 4=deadly, 5=lethal
    private long _nextTick;
    private int _ticksRemaining;
    private Serial _source = Serial.Invalid; // who applied the poison (for kill attribution)

    public CharacterPoisonState(Character owner)
    {
        _owner = owner;
    }

    public byte Level { get => _level; set => _level = value; }
    public bool IsPoisoned => _level > 0;
    public Serial Source => _source;

    /// <summary>Ticks left in the current poison (0 when not poisoned).</summary>
    public int TicksRemaining => _ticksRemaining;

    /// <summary>Milliseconds until the next tick, clamped to ≥ 0 — for save as
    /// remaining time (the absolute tick resets on restart).</summary>
    public long RemainingTickMs => _nextTick > 0 ? Math.Max(0, _nextTick - Environment.TickCount64) : 0;

    /// <summary>Restore a persisted poison (world load): re-arm the level, remaining
    /// ticks, next-tick time and poisoner exactly, without the fresh-poison reset that
    /// <see cref="Apply"/> does. Ignored when the level or tick count is out of range.</summary>
    public void Restore(byte level, int ticksRemaining, long remainingMs, Serial source)
    {
        if (level == 0 || level > 5 || ticksRemaining <= 0)
            return;
        _level = level;
        _ticksRemaining = ticksRemaining;
        _nextTick = Environment.TickCount64 + Math.Max(0, remainingMs);
        _source = source;
        _owner.SetStatFlag(StatFlag.Poisoned);
    }

    /// <summary>Apply poison. Level: 1=lesser .. 5=lethal. A weaker poison
    /// never downgrades an active stronger one.</summary>
    public void Apply(byte level, Serial source)
    {
        if (level == 0 || level > 5) return;
        if (_owner.IsDead) return;
        if (level <= _level && _ticksRemaining > 0)
            return;
        _level = _ticksRemaining > 0 ? Math.Max(_level, level) : level;
        _ticksRemaining = _level switch
        {
            1 => 5, 2 => 8, 3 => 12, 4 => 16, _ => 20
        };
        _nextTick = Environment.TickCount64 + GetTickInterval();
        if (source.IsValid) _source = source;
        _owner.SetStatFlag(StatFlag.Poisoned);
    }

    public void Cure()
    {
        _level = 0;
        _ticksRemaining = 0;
        _nextTick = 0;
        _source = Serial.Invalid;
        _owner.ClearStatFlag(StatFlag.Poisoned);
    }

    private int GetTickInterval() => _level switch
    {
        1 => 4000, 2 => 3000, 3 => 2500, 4 => 2000, _ => 1500
    };

    private int GetDamage() => _level switch
    {
        1 => Random.Shared.Next(2, 5),
        2 => Random.Shared.Next(3, 8),
        3 => Random.Shared.Next(5, 12),
        4 => Random.Shared.Next(8, 20),
        _ => Random.Shared.Next(12, 30)
    };

    // Engine level → Sphere memory-strength scale (ARGO.MOREY / ARGN2).
    // Midpoints of the reference poison bands: <200 lesser, <400 normal,
    // <800 greater, <1000 deadly, else lethal — so scripts that band on
    // MOREY (the documented @SpellEffectTick pattern) classify each level
    // back to itself.
    private int GetStrength() => _level switch
    {
        1 => 100, 2 => 300, 3 => 600, 4 => 900, _ => 1000
    };

    /// <summary>Process a poison tick. Returns damage dealt, 0 if no tick.</summary>
    public int ProcessTick(long now)
    {
        if (_level == 0 || _ticksRemaining <= 0) return 0;
        if (_owner.IsDead) { Cure(); return 0; }
        if (now < _nextTick) return 0;

        _nextTick = now + GetTickInterval();

        int damage = GetDamage();
        // Apply poison resist
        int resistPct = Math.Clamp(_owner.ResPoison, (short)0, (short)80);
        damage = damage * (100 - resistPct) / 100;
        damage = Math.Max(1, damage);

        // @SpellEffectTick bridge (Source-X SPELLFLAG_TICK): the script sees
        // the pending tick and may rewrite damage/delay/charges or destroy
        // the effect (RETURN 1 → cure, no tick). Charges are seeded BEFORE
        // the auto-decrement below, matching the Source-X contract where
        // local.charges drops by one at the end of the trigger execution.
        var tickHook = Character.OnSpellEffectTick;
        if (tickHook != null)
        {
            var ctx = new SpellEffectTickContext
            {
                SpellId = (int)SpellType.Poison,
                Level = _level,
                Strength = GetStrength(),
                SourceUid = _source,
                Damage = damage,
                DelayMs = GetTickInterval(),
                Charges = _ticksRemaining,
            };
            if (!tickHook(_owner, ctx))
            {
                Cure();
                return 0;
            }
            damage = Math.Max(0, ctx.Damage);
            _ticksRemaining = Math.Max(1, ctx.Charges);
            if (ctx.DelayMs > 0)
                _nextTick = now + ctx.DelayMs;
        }
        _ticksRemaining--;

        // A script can zero the tick (reference handler maps sub-lesser
        // strength to 0 damage): keep the schedule, skip the application.
        if (damage > 0)
        {
            _owner.Hits = (short)Math.Max(0, _owner.Hits - damage);

            // Credit the damage to the poisoner so murder count / karma-fame / loot
            // rights resolve correctly on a poison kill (otherwise it is anonymous).
            var poisoner = _source.IsValid ? Character.ResolveCharByUid?.Invoke(_source) : null;
            if (_source.IsValid)
                _owner.RecordAttack(_source, damage);

            // Show the poison damage to nearby clients and refresh the health bar —
            // without this the victim's HP silently drains with no visual feedback.
            Character.BroadcastNearby?.Invoke(_owner.Position, 18,
                new SphereNet.Network.Packets.Outgoing.PacketDamage(
                    _owner.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue)), 0);
            Character.BroadcastNearby?.Invoke(_owner.Position, 18,
                new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                    _owner.Uid.Value, _owner.MaxHits, _owner.Hits), 0);

            if (_owner.Hits <= 0 && !_owner.IsDead)
            {
                if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(_owner, poisoner);
                else _owner.Kill();
            }
        }

        if (_ticksRemaining <= 0)
            Cure();

        return damage;
    }
}
