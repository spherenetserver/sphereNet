using System.Collections.Concurrent;

namespace SphereNet.Game.Diagnostics;

public sealed class BotAnomalyScanner
{
    private readonly int _botId;
    private readonly BotWorldModel _world;
    private long _lastScanMs;

    private int _moveRejectWindowStart;
    private int _moveRequestWindowStart;

    public BotAnomalyScanner(int botId, BotWorldModel world)
    {
        _botId = botId;
        _world = world;
    }

    public void Tick(long nowMs, ConcurrentQueue<BotAnomaly> anomalies)
    {
        if (nowMs - _lastScanMs < 7000) return;
        _lastScanMs = nowMs;

        CheckMoveRejectRate(nowMs, anomalies);
        CheckZJump(nowMs, anomalies);
        CheckPositionSnap(nowMs, anomalies);
        CheckPickupRejects(nowMs, anomalies);
        CheckGumpStuck(nowMs, anomalies);
        CheckActionTimeout(nowMs, anomalies);
    }

    private void CheckMoveRejectRate(long nowMs, ConcurrentQueue<BotAnomaly> anomalies)
    {
        int totalRejects = _world.TotalMoveRejects;
        int totalRequests = _world.TotalMoveRequests;

        int windowRejects = totalRejects - _moveRejectWindowStart;
        int windowRequests = totalRequests - _moveRequestWindowStart;

        if (windowRequests >= 10)
        {
            double rate = (double)windowRejects / windowRequests;
            if (rate > 0.30)
            {
                anomalies.Enqueue(new BotAnomaly
                {
                    Type = BotAnomalyType.ExcessiveMoveReject,
                    BotId = _botId,
                    TimestampMs = nowMs,
                    Detail = $"MoveReject rate {rate:P0} ({windowRejects}/{windowRequests})",
                    Severity = rate > 0.60 ? BotAnomalySeverity.Error : BotAnomalySeverity.Warning,
                });
            }

            _moveRejectWindowStart = totalRejects;
            _moveRequestWindowStart = totalRequests;
        }
    }

    private void CheckZJump(long nowMs, ConcurrentQueue<BotAnomaly> anomalies)
    {
        int dz = Math.Abs(_world.Z - _world.PrevZ);
        if (dz > 20)
        {
            anomalies.Enqueue(new BotAnomaly
            {
                Type = BotAnomalyType.ZJump,
                BotId = _botId,
                TimestampMs = nowMs,
                Detail = $"Z jumped {dz} tiles ({_world.PrevZ} -> {_world.Z})",
                Severity = BotAnomalySeverity.Warning,
            });
        }
    }

    private void CheckPositionSnap(long nowMs, ConcurrentQueue<BotAnomaly> anomalies)
    {
        int dx = Math.Abs(_world.X - _world.PrevX);
        int dy = Math.Abs(_world.Y - _world.PrevY);
        int dist = Math.Max(dx, dy);

        if (dist > 10 && _world.PrevX != 0)
        {
            anomalies.Enqueue(new BotAnomaly
            {
                Type = BotAnomalyType.PositionSnap,
                BotId = _botId,
                TimestampMs = nowMs,
                Detail = $"Position snapped {dist} tiles ({_world.PrevX},{_world.PrevY} -> {_world.X},{_world.Y})",
                Severity = BotAnomalySeverity.Warning,
            });
        }
    }

    private void CheckPickupRejects(long nowMs, ConcurrentQueue<BotAnomaly> anomalies)
    {
        if (_world.ConsecutivePickupRejects >= 3)
        {
            anomalies.Enqueue(new BotAnomaly
            {
                Type = BotAnomalyType.ItemPickupReject,
                BotId = _botId,
                TimestampMs = nowMs,
                Detail = $"Consecutive pickup rejects: {_world.ConsecutivePickupRejects}",
                Severity = BotAnomalySeverity.Warning,
            });
        }
    }

    private void CheckGumpStuck(long nowMs, ConcurrentQueue<BotAnomaly> anomalies)
    {
        if (_world.ActiveGump is { IsOpen: true } &&
            nowMs - _world.ActiveGumpReceivedMs > 30_000)
        {
            anomalies.Enqueue(new BotAnomaly
            {
                Type = BotAnomalyType.GumpStuck,
                BotId = _botId,
                TimestampMs = nowMs,
                Detail = $"Gump {_world.ActiveGump.GumpId:X8} open for {(nowMs - _world.ActiveGumpReceivedMs) / 1000}s",
                Severity = BotAnomalySeverity.Error,
            });
        }
    }

    private void CheckActionTimeout(long nowMs, ConcurrentQueue<BotAnomaly> anomalies)
    {
        if (_world.LastActionResult == BotActionResult.TimedOut &&
            nowMs - _world.LastActionTimeMs < 15_000)
        {
            anomalies.Enqueue(new BotAnomaly
            {
                Type = BotAnomalyType.PacketTimeout,
                BotId = _botId,
                TimestampMs = nowMs,
                Detail = "Action timed out waiting for server response",
                Severity = BotAnomalySeverity.Warning,
            });
        }
    }
}

public sealed class BotAnomaly
{
    public BotAnomalyType Type { get; init; }
    public int BotId { get; init; }
    public long TimestampMs { get; init; }
    public string Detail { get; init; } = string.Empty;
    public BotAnomalySeverity Severity { get; init; }
}

public enum BotAnomalyType
{
    ExcessiveMoveReject,
    ZJump,
    PositionSnap,
    SendQueueGrowth,
    PacketTimeout,
    GumpStuck,
    ItemPickupReject,
    UnexpectedDisconnect,
}

public enum BotAnomalySeverity
{
    Warning,
    Error,
    Critical,
}
