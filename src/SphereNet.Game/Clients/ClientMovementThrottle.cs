namespace SphereNet.Game.Clients;

/// <summary>
/// Movement throttle state extracted from GameClient (decomposition phase 1
/// — see docs/GAMECLIENT_DECOMPOSITION_TR.md): the walk token bucket, the
/// move-time gate, the violation counter and the queued-movement processor.
/// Pure state relocation — GameClient.Combat operates on these members
/// exactly as it did on the former fields; the refill/consume logic stays
/// at the call sites for now and moves with the Combat handler in phase 3.
/// </summary>
public sealed class ClientMovementThrottle
{
    public long NextMoveTime;
    public int WalkTokens;
    public long WalkTokenLastMs;
    public int ViolationCount;
    public Movement.MovementQueueProcessor? Queue;

    public ClientMovementThrottle(int initialTokens)
    {
        WalkTokens = initialTokens;
    }
}
