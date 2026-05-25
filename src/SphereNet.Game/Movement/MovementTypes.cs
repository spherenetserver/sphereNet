using SphereNet.Core.Enums;

namespace SphereNet.Game.Movement;

public readonly record struct MovementRecord(
    long TimestampMs,
    Direction Direction,
    bool Running,
    bool Mounted);

public enum SpeedVerdict
{
    Normal,
    Warning,
    Violation,
    Kick
}
