namespace SphereNet.Network.Packets;

/// <summary>
/// Outbound packet priority, mirroring Source-X PacketSend priority levels.
/// Higher values flush first. FlushOutput drains Highest → Idle so that
/// latency-critical traffic (movement ack/reject, auth) never waits behind
/// bulk world/UI updates.
/// </summary>
public enum PacketPriority
{
    Idle = 0,    // ambience, tooltips, weather/music — delay-tolerant
    Low = 1,     // status bars, gumps, animations, target cursors
    Normal = 2,  // world objects, mobiles, speech/sound/effects, combat (default)
    High = 3,    // login/stream control, pickup reject, time sync
    Highest = 4, // movement ack/reject, auth denial — never delayed

    Count = 5,
}
