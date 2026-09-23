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

    /// <summary>ACTIONEFFECT (Source-X m_Act_Effect): a one-shot override a script
    /// writes inside a skill trigger — the amount healed, the mana drained, the
    /// radius of an area skill, the percent of a failed craft's resources lost.
    /// -1 means "no override", and every skill start and cleanup restores it
    /// (CCharSkill.cpp:602/4456) so one attempt cannot steer the next.</summary>
    private int _actionEffect = -1;

    /// <summary>ACTIONEFFECT. A negative write normalises to -1, the way the
    /// reference's own setter does (CChar.cpp:3729).</summary>
    public int ActionEffect
    {
        get => _actionEffect;
        set => _actionEffect = value < 0 ? -1 : value;
    }

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

    internal int? CastDifficulty { get; set; }

    internal bool CastSkillSucceeded { get; set; }
    internal Action<Character>? CastAborted { get; set; }

    public void BeginCast(SpellType spell, Serial targetUid, Point3D targetPos)
    {
        CastDifficulty = null;
        CastSkillSucceeded = false;
        _castingSpell = (int)spell;
        ActArg1 = (int)spell;
        _castTargetUid = targetUid;
        _castTargetPos = targetPos;
        _spellPrecast = false;
        _hasCastTargetPosPending = false;
    }

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

    public void ClearCastState(bool notifyAbort = true)
    {
        var aborted = CastAborted;
        CastAborted = null;
        if (notifyAbort && IsCasting) aborted?.Invoke(this);
        CastSkillSucceeded = false;
        CastDifficulty = null;
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
        // A fresh attempt starts with no script override (Skill_Cleanup, :602).
        _actionEffect = -1;
        _skillDelayEnd = delayEnd;
        _skillStrokeNext = strokeNext;
        _skillStrokeCount = 0;
        SkillStrokesLeft = 0;
        SkillStrokeDelayMs = 0;
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

    /// <summary>Re-arm a continuing native skill without clearing ACTEFFECT or its context.</summary>
    public void ContinueSkillPending(long delayEnd) => _skillDelayEnd = delayEnd;

    public long SkillStrokeNext => _skillStrokeNext;

    public void SetSkillStrokeNext(long tickMs) => _skillStrokeNext = tickMs;

    public int SkillStrokeCount => _skillStrokeCount;

    public int IncrementSkillStrokeCount() => ++_skillStrokeCount;

    /// <summary>Strokes a gathering swing still has to run - Source-X
    /// m_atResource.m_dwStrokeCount. Rolled at SKTRIG_START (mining 2-6, fishing 1-2)
    /// and handed to @SkillStart as LOCAL.GatherStrokeCnt, which a script may rewrite;
    /// every Skill_Stroke plays with a count of one or more, decrements it, and the
    /// count reaching zero IS the success (CCharSkill.cpp:3578/3630-3635).</summary>
    public int SkillStrokesLeft { get; set; }

    /// <summary>Per-stroke re-arm interval in milliseconds for the running gather
    /// swing: the skill DELAY, or what the last @SkillStroke wrote into LOCAL.Delay
    /// (Skill_Stroke, CCharSkill.cpp:3574/3605/3645-3649).</summary>
    public long SkillStrokeDelayMs { get; set; }

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
