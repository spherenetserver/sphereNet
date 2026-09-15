namespace SphereNet.Game.Diagnostics;

public sealed class BotActionApi
{
    private readonly BotClient _bot;
    private BotWorldModel World => _bot.World;

    private TaskCompletionSource<BotActionResult>? _pendingAction;
    private BotActionWaitKind _waitKind;
    /// <summary>The sequence byte of the move this bot is waiting to hear about. The
    /// server echoes it in both the accept (0x22) and the reject (0x21), and without
    /// comparing it a LATE answer to a move that already timed out completes the NEXT
    /// move as a success - a false positive in the instrument a load report is built
    /// from (review work item D09).</summary>
    private byte _pendingMoveSeq;
    // Guards _waitKind/_pendingAction: the behavior thread sets them (SetWait /
    // WaitResult) while the background receive loop completes them (CompleteXxx).
    private readonly object _waitLock = new();

    public BotActionApi(BotClient bot)
    {
        _bot = bot;
    }

    public async Task<BotActionResult> MoveDirection(byte dir, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        byte seq = _bot.SendMovePacket(dir);
        SetWaitForMove(seq);
        World.TotalMoveRequests++;
        return await WaitResult(1000, ct);
    }

    public async Task<BotActionResult> MoveTo(int targetX, int targetY,
        int timeoutMs = 10000, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        long deadline = Environment.TickCount64 + timeoutMs;
        int stuckCount = 0;

        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            int dx = targetX - World.X;
            int dy = targetY - World.Y;
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) <= 1)
                return BotActionResult.Success;

            byte dir = World.GetDirectionTo((short)targetX, (short)targetY);
            var result = await MoveDirection(dir, ct);
            if (result == BotActionResult.Disconnected) return result;
            if (result == BotActionResult.Rejected)
            {
                stuckCount++;
                if (stuckCount > 10) return BotActionResult.Rejected;
                await Task.Delay(200, ct);
                continue;
            }

            stuckCount = 0;
            await Task.Delay(150, ct);
        }

        return BotActionResult.TimedOut;
    }

    public async Task<BotActionResult> DoubleClick(uint serial,
        int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        SetWait(BotActionWaitKind.ContainerOpen);
        _bot.SendRawPacket(BotPacketBuilder.BuildDoubleClick(serial));
        return await WaitResult(timeoutMs, ct);
    }

    public async Task<BotActionResult> PickUp(uint itemSerial, int amount = 1,
        int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        World.ConsecutivePickupRejects = 0;
        SetWait(BotActionWaitKind.PickUpAck);
        _bot.SendRawPacket(BotPacketBuilder.BuildPickUp(itemSerial, (ushort)amount));
        return await WaitResult(timeoutMs, ct);
    }

    public async Task<BotActionResult> DropToContainer(uint itemSerial, uint containerSerial,
        int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        SetWait(BotActionWaitKind.DropAck);
        _bot.SendRawPacket(BotPacketBuilder.BuildDropToContainer(itemSerial, containerSerial));
        return await WaitResult(timeoutMs, ct);
    }

    public async Task<BotActionResult> DropToWorld(uint itemSerial, int x, int y, int z,
        int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        SetWait(BotActionWaitKind.DropAck);
        _bot.SendRawPacket(BotPacketBuilder.BuildDropToWorld(itemSerial, (short)x, (short)y, (sbyte)z));
        return await WaitResult(timeoutMs, ct);
    }

    public async Task<BotActionResult> OpenBackpack(int timeoutMs = 2000,
        CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;
        uint charUid = World.CharUid;
        return await DoubleClick(charUid, timeoutMs, ct);
    }

    public async Task<BotActionResult> OpenContainer(uint serial, int timeoutMs = 2000,
        CancellationToken ct = default)
    {
        return await DoubleClick(serial, timeoutMs, ct);
    }

    public Task<BotActionResult> Say(string text, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return Task.FromResult(BotActionResult.Disconnected);
        _bot.SendRawPacket(BotPacketBuilder.BuildSpeech(text, 0));
        return Task.FromResult(BotActionResult.Success);
    }

    public Task<BotActionResult> Yell(string text, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return Task.FromResult(BotActionResult.Disconnected);
        _bot.SendRawPacket(BotPacketBuilder.BuildSpeech(text, 1));
        return Task.FromResult(BotActionResult.Success);
    }

    public Task<BotActionResult> Whisper(string text, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return Task.FromResult(BotActionResult.Disconnected);
        _bot.SendRawPacket(BotPacketBuilder.BuildSpeech(text, 2));
        return Task.FromResult(BotActionResult.Success);
    }

    /// <summary>Ask the server to attack. The answer - accepted, out of range, not a
    /// valid target, or nothing at all - arrives later as combat packets, and this
    /// method does not wait for any of it, so it reports <see cref="BotActionResult.Sent"/>
    /// rather than success (review work item D09).</summary>
    public Task<BotActionResult> Attack(uint targetSerial, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return Task.FromResult(BotActionResult.Disconnected);
        _bot.SendRawPacket(BotPacketBuilder.BuildAttackRequest(targetSerial));
        World.TotalRequestsSent++;
        return Task.FromResult(BotActionResult.Sent);
    }

    public Task<BotActionResult> SetWarMode(bool enabled, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return Task.FromResult(BotActionResult.Disconnected);
        World.IsWarMode = enabled;
        _bot.SendRawPacket(BotPacketBuilder.BuildWarMode(enabled));
        World.TotalRequestsSent++;
        return Task.FromResult(BotActionResult.Sent);
    }

    public async Task<BotActionResult> UseSkill(int skillId, int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        SetWait(BotActionWaitKind.SkillResponse);
        _bot.SendRawPacket(BotPacketBuilder.BuildSkillUse((ushort)skillId));
        return await WaitResult(timeoutMs, ct);
    }

    public async Task<BotActionResult> CastSpell(int spellId, uint targetSerial,
        int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        _bot.SendRawPacket(BotPacketBuilder.BuildCastSpell(spellId));

        var targetResult = await WaitForTarget(timeoutMs, ct);
        if (targetResult != BotActionResult.Success) return targetResult;

        return await TargetObject(targetSerial, ct);
    }

    public async Task<BotActionResult> Buy(uint vendorSerial, uint itemSerial, int amount,
        CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        _bot.SendRawPacket(BotPacketBuilder.BuildBuyItems(vendorSerial,
            [(0, itemSerial, (ushort)amount)]));
        await Task.Delay(200, ct);
        return BotActionResult.Success;
    }

    public async Task<BotActionResult> Sell(uint vendorSerial, uint itemSerial, int amount = 1,
        CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        _bot.SendRawPacket(BotPacketBuilder.BuildSellItems(vendorSerial,
            [(itemSerial, (ushort)amount)]));
        await Task.Delay(200, ct);
        return BotActionResult.Success;
    }

    public async Task<BotActionResult> WaitForGump(int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        SetWait(BotActionWaitKind.GumpOpen);
        return await WaitResult(timeoutMs, ct);
    }

    public Task<BotActionResult> RespondGump(uint gumpId, uint serial, int buttonId,
        int[]? switches = null, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return Task.FromResult(BotActionResult.Disconnected);

        _bot.SendRawPacket(BotPacketBuilder.BuildGumpResponse(serial, gumpId, buttonId, switches));
        World.ActiveGump = null;
        World.TotalRequestsSent++;
        return Task.FromResult(BotActionResult.Sent);
    }

    public async Task<BotActionResult> WaitForTarget(int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return BotActionResult.Disconnected;

        if (World.HasPendingTarget)
            return BotActionResult.Success;

        SetWait(BotActionWaitKind.TargetCursor);
        return await WaitResult(timeoutMs, ct);
    }

    public Task<BotActionResult> TargetObject(uint serial, CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return Task.FromResult(BotActionResult.Disconnected);
        if (!World.HasPendingTarget) return Task.FromResult(BotActionResult.InvalidState);

        _bot.SendRawPacket(BotPacketBuilder.BuildTargetObject(World.TargetCursorId, serial));
        World.HasPendingTarget = false;
        World.TotalRequestsSent++;
        return Task.FromResult(BotActionResult.Sent);
    }

    public Task<BotActionResult> TargetLocation(int x, int y, int z,
        CancellationToken ct = default)
    {
        if (_bot.State != BotState.Playing) return Task.FromResult(BotActionResult.Disconnected);
        if (!World.HasPendingTarget) return Task.FromResult(BotActionResult.InvalidState);

        _bot.SendRawPacket(BotPacketBuilder.BuildTargetLocation(
            World.TargetCursorId, (short)x, (short)y, (sbyte)z));
        World.HasPendingTarget = false;
        World.TotalRequestsSent++;
        return Task.FromResult(BotActionResult.Sent);
    }

    // --- Internal completion API (called by BotClient packet handlers) ---

    /// <summary>Answer the move this bot is actually waiting for. Returns false when
    /// the sequence belongs to an earlier move - the caller counts that as a late ack
    /// rather than treating it as this move's outcome.
    ///
    /// There is no answer coming for those earlier moves: the server pins its walk
    /// sequence to 0 on a reject and DROPS the in-flight steps silently (see
    /// RejectStaleMove), which is what the real client's reset is written against. The
    /// bot resets its own sequence the same way, so a timeout is the honest outcome for
    /// a step the server threw away.</summary>
    internal bool CompleteMove(byte sequence, bool success)
    {
        lock (_waitLock)
        {
            if (_waitKind != BotActionWaitKind.MoveAck || sequence != _pendingMoveSeq)
                return false;
            Complete(success ? BotActionResult.Success : BotActionResult.Rejected);
            return true;
        }
    }

    internal void CompleteContainerOpen()
    {
        lock (_waitLock)
            if (_waitKind == BotActionWaitKind.ContainerOpen)
                Complete(BotActionResult.Success);
    }

    internal void CompletePickUp(bool success)
    {
        lock (_waitLock)
            if (_waitKind == BotActionWaitKind.PickUpAck)
                Complete(success ? BotActionResult.Success : BotActionResult.Rejected);
    }

    internal void CompleteDrop()
    {
        lock (_waitLock)
            if (_waitKind == BotActionWaitKind.DropAck)
                Complete(BotActionResult.Success);
    }

    internal void CompleteTarget()
    {
        lock (_waitLock)
            if (_waitKind == BotActionWaitKind.TargetCursor)
                Complete(BotActionResult.Success);
    }

    internal void CompleteGump()
    {
        lock (_waitLock)
            if (_waitKind == BotActionWaitKind.GumpOpen)
                Complete(BotActionResult.Success);
    }

    internal void CompleteSkill()
    {
        lock (_waitLock)
            if (_waitKind == BotActionWaitKind.SkillResponse)
                Complete(BotActionResult.Success);
    }

    internal void CompleteDisconnect()
    {
        lock (_waitLock)
            Complete(BotActionResult.Disconnected);
    }

    // Caller must hold _waitLock.
    private void Complete(BotActionResult result)
    {
        _waitKind = BotActionWaitKind.None;
        _pendingAction?.TrySetResult(result);
    }

    /// <summary>Wait for the answer to one particular move.</summary>
    private void SetWaitForMove(byte sequence)
    {
        lock (_waitLock)
        {
            _pendingMoveSeq = sequence;
            SetWaitLocked(BotActionWaitKind.MoveAck);
        }
    }

    private void SetWait(BotActionWaitKind kind)
    {
        lock (_waitLock)
            SetWaitLocked(kind);
    }

    private void SetWaitLocked(BotActionWaitKind kind)
    {
        _pendingAction?.TrySetResult(BotActionResult.TimedOut);
        _waitKind = kind;
        _pendingAction = new TaskCompletionSource<BotActionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<BotActionResult> WaitResult(int timeoutMs, CancellationToken ct)
    {
        TaskCompletionSource<BotActionResult>? pending;
        lock (_waitLock) pending = _pendingAction;
        if (pending == null) return BotActionResult.None;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            var task = pending.Task;
            var completed = await Task.WhenAny(task,
                Task.Delay(Timeout.Infinite, cts.Token));
            if (completed == task) return task.Result;
        }
        catch (OperationCanceledException) { }

        _waitKind = BotActionWaitKind.None;
        return ct.IsCancellationRequested ? BotActionResult.Disconnected : BotActionResult.TimedOut;
    }
}

internal enum BotActionWaitKind
{
    None,
    MoveAck,
    ContainerOpen,
    PickUpAck,
    DropAck,
    TargetCursor,
    GumpOpen,
    SkillResponse,
}
