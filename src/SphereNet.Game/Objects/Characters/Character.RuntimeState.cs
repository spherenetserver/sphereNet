using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Game.Objects.Characters;

public partial class Character
{
    // Stat locks: 0=up, 1=down, 2=locked (UO client convention)
    private readonly byte[] _statLocks = new byte[3];

    // Spell cast runtime (Source-X m_Act_Spell / cast timer)
    private int _castingSpell = -1;
    private long _castTimerEnd;
    private bool _spellPrecast;
    private Serial _castTargetUid = Serial.Invalid;
    private Point3D _castTargetPos;
    private bool _hasCastTargetPosPending;
    private Point3D _castTargetPosPending;

    // Delayed active skill runtime
    private int _skillPendingId = -1;
    private long _skillDelayEnd;
    private long _skillStrokeNext;
    private int _skillStrokeCount;
    private Serial _skillPendingTarget = Serial.Invalid;
    private bool _hasSkillPendingPoint;
    private bool _skillPendingIsInfo;
    private Point3D _skillPendingPoint;
    private readonly List<string> _pendingSpellEffectRecords = [];

    public byte GetStatLock(int statIdx)
    {
        MigrateStatLockFromTags();
        return statIdx >= 0 && statIdx < _statLocks.Length ? _statLocks[statIdx] : (byte)0;
    }

    public void SetStatLock(int statIdx, byte lockState)
    {
        if (statIdx >= 0 && statIdx < _statLocks.Length)
        {
            _statLocks[statIdx] = lockState;
            RemoveTag($"STATLOCK.{statIdx}");
        }
    }

    /// <summary>One-time import from legacy TAG.STATLOCK.* saves.</summary>
    public void MigrateStatLockFromTags()
    {
        for (int i = 0; i < _statLocks.Length; i++)
        {
            if (!TryGetTag($"STATLOCK.{i}", out string? val) || !byte.TryParse(val, out byte sl))
                continue;
            _statLocks[i] = sl;
            RemoveTag($"STATLOCK.{i}");
        }
    }

    public bool IsCasting => _castingSpell >= 0;

    public bool TryGetCastingSpell(out SpellType spell)
    {
        if (_castingSpell < 0)
        {
            spell = default;
            return false;
        }

        spell = (SpellType)_castingSpell;
        return true;
    }

    public void BeginCast(SpellType spell, Serial targetUid, Point3D targetPos)
    {
        _castingSpell = (int)spell;
        _castTargetUid = targetUid;
        _castTargetPos = targetPos;
        _spellPrecast = false;
        _hasCastTargetPosPending = false;
    }

    // Cast recovery (FCR-style): the earliest tick at which the next spell may
    // begin. Prevents gap-less spell spam.
    private long _nextCastReadyMs;
    public bool IsCastOnRecovery(long nowMs) => _nextCastReadyMs > nowMs;
    public void SetCastRecovery(long readyAtMs) => _nextCastReadyMs = readyAtMs;

    public void SetCastTimerEnd(long tickMs) => _castTimerEnd = tickMs;

    public long CastTimerEnd => _castTimerEnd;

    public bool IsCastTimerActive(long nowMs) =>
        _castingSpell >= 0 && _castTimerEnd > 0 && nowMs < _castTimerEnd;

    public bool IsCastTimerExpired(long nowMs) =>
        _castTimerEnd > 0 && nowMs >= _castTimerEnd;

    public bool SpellPrecast
    {
        get => _spellPrecast;
        set => _spellPrecast = value;
    }

    public Serial CastTargetUid => _castTargetUid;

    public Point3D CastTargetPos => _castTargetPos;

    public void UpdateCastTarget(Serial targetUid, Point3D targetPos)
    {
        _castTargetUid = targetUid;
        _castTargetPos = targetPos;
    }

    public void SetCastTargetPosPending(Point3D pos)
    {
        _castTargetPosPending = pos;
        _hasCastTargetPosPending = true;
    }

    public bool TryTakeCastTargetPosPending(out Point3D pos)
    {
        if (!_hasCastTargetPosPending)
        {
            pos = default;
            return false;
        }

        pos = _castTargetPosPending;
        _hasCastTargetPosPending = false;
        return true;
    }

    public void ClearCastState()
    {
        _castingSpell = -1;
        _castTimerEnd = 0;
        _spellPrecast = false;
        _castTargetUid = Serial.Invalid;
        _castTargetPos = default;
        _hasCastTargetPosPending = false;
    }

    public void BeginSkillPending(int skillId, long delayEnd, long strokeNext, Serial targetUid,
        Point3D? point, bool isInfo = false)
    {
        _skillPendingId = skillId;
        _skillDelayEnd = delayEnd;
        _skillStrokeNext = strokeNext;
        _skillStrokeCount = 0;
        _skillPendingTarget = targetUid;
        _skillPendingIsInfo = isInfo;
        if (point.HasValue)
        {
            _skillPendingPoint = point.Value;
            _hasSkillPendingPoint = true;
        }
        else
        {
            _hasSkillPendingPoint = false;
        }
    }

    public int SkillPendingId => _skillPendingId;

    public long SkillDelayEnd => _skillDelayEnd;

    public long SkillStrokeNext => _skillStrokeNext;

    public void SetSkillStrokeNext(long tickMs) => _skillStrokeNext = tickMs;

    public int SkillStrokeCount => _skillStrokeCount;

    public int IncrementSkillStrokeCount() => ++_skillStrokeCount;

    public void ResetSkillStrokeCount() => _skillStrokeCount = 0;

    public Serial SkillPendingTarget => _skillPendingTarget;

    public bool SkillPendingIsInfo => _skillPendingIsInfo;

    public bool TryGetSkillPendingPoint(out Point3D point)
    {
        if (!_hasSkillPendingPoint)
        {
            point = default;
            return false;
        }

        point = _skillPendingPoint;
        return true;
    }

    public IReadOnlyList<string> PendingSpellEffectRecords => _pendingSpellEffectRecords;

    public void AddPendingSpellEffectRecord(string record)
    {
        if (!string.IsNullOrWhiteSpace(record))
            _pendingSpellEffectRecords.Add(record);
    }

    public void ClearPendingSpellEffectRecords() => _pendingSpellEffectRecords.Clear();
}
