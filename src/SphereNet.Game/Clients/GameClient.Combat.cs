using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Objects;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{

    // ServUO-style fastwalk prevention via time-based throttle + walk buffer.
    public static int WalkBufferMax { get; set; } = 75;
    public static int WalkRegenPerSecond { get; set; } = 25;
    public static int MoveToleranceMs { get; set; } = 80;
    public static int MoveRejectResyncMs { get; set; } = 0;
    public static int WalkZCorrectionThreshold { get; set; } = 0;
    public static int MoveViolationKickThreshold { get; set; }
    public static Func<long> MoveClock { get; set; } = () => Environment.TickCount64;

    // Credit-based movement system (opt-in via MovementCreditEnabled)
    public static bool MovementCreditEnabled { get; set; }
    public static int MovementCreditBaseMs { get; set; } = 200;
    public static int MovementCreditMaxMs { get; set; } = 1400;
    public static int MovementQueueCapacity { get; set; } = 10;

    // Speed hack detection (opt-in)
    public static bool SpeedHackDetectionEnabled { get; set; }
    public static double SpeedHackRateThreshold { get; set; } = 1.5;
    public static int SpeedHackBurstWindow { get; set; } = 3;
    public static int SpeedHackHistorySize { get; set; } = 20;
    public static int SpeedHackCooldownMs { get; set; } = 60_000;

    public static event Action<Objects.Characters.Character, Movement.SpeedVerdict>? OnSpeedHackDetected;

    private long _nextMoveTime;
    private int _walkTokens = WalkBufferMax;
    private long _walkTokenLastMs;
    private int _moveViolationCount;
    private long _moveRejectResyncUntil;
    private sbyte _walkZCorrectionBase;
    private long? _movementBatchNow;

    // Credit-based movement state (active only when MovementCreditEnabled=true)
    private int _movementCreditMs;
    private long _movementCreditLastTick;
    private Movement.MovementQueueProcessor? _movementQueue;
    private Movement.MovementHistory? _movementHistory;
    private Movement.SpeedHackDetector? _speedHackDetector;

    public void HandleMovementBatch(IReadOnlyList<SphereNet.Network.State.MovementStep> steps)
    {
        if (_character == null || steps.Count == 0)
            return;

        long baseNow = MoveClock();
        long virtualNow = baseNow;
        for (int i = 0; i < steps.Count; i++)
        {
            _movementBatchNow = virtualNow;
            var step = steps[i];
            bool accepted = HandleMove(step.Direction, step.Sequence, step.FastWalkKey);
            if (!accepted)
            {
                _logger.LogWarning("[move_batch_stop] char={Char} step={Step}/{Total} seq={Seq} dir=0x{Dir:X2} mode={Mode} pos={X},{Y},{Z}",
                    _character.Name, i + 1, steps.Count, step.Sequence, step.Direction, step.Mode,
                    _character.X, _character.Y, _character.Z);
                break;
            }

            bool running = (step.Direction & 0x80) != 0;
            virtualNow += MovementEngine.GetMoveDelay(_character.IsMounted, running);
        }
        _movementBatchNow = null;
    }

    public bool HandleMove(byte dir, byte seq, uint fastWalkKey)
    {
        if (_character == null) return false;
        // NOTE: do NOT silently drop walk packets when IsDead. Source-X
        // ghosts walk freely; if the server eats the request without
        // sending either 0x22 (MoveAck) or 0x21 (MoveReject) the client's
        // walk sequence stalls and the player ends up frozen on screen
        // even though their ghost body is rendered correctly. The
        // post-death "client cannot move" symptom in the death log was
        // exactly this: 0x2C death status arrived, then every subsequent
        // walk packet from the client was silently swallowed here.

        var direction = (Direction)(dir & 0x07);
        bool running = (dir & 0x80) != 0;

        long now = _movementBatchNow ?? MoveClock();
        _netState.LastActivityTick = now;

        _logger.LogDebug(
            "[move_recv] seq={Seq} dir={Dir} run={Run} expected={Expected} at {X},{Y},{Z}",
            seq, direction, running, _netState.WalkSequence,
            _character.X, _character.Y, _character.Z);

        if (_moveRejectResyncUntil > 0 && now < _moveRejectResyncUntil)
        {
            _logger.LogDebug(
                "[move_reject_resync] seq={Seq} dir=0x{Dir:X2} remaining={Remaining}ms at {X},{Y},{Z}",
                seq, dir, _moveRejectResyncUntil - now, _character.X, _character.Y, _character.Z);
            _netState.Send(new PacketMoveReject(seq, _character.X, _character.Y, _character.Z, CurrentFacingDir()));
            _netState.WalkSequence = 0;
            return false;
        }

        byte expectedSeq = _netState.WalkSequence;

        // After a reject, WalkSequence resets to 0. The client's first
        // move post-reject will be seq 0 or 1. Anything higher is a stale
        // speculative move still in flight from before the rejection —
        // processing it from the corrected position would move the
        // character in unexpected directions (teleportation). Drop them.
        if (expectedSeq == 0 && seq > 1)
        {
            _logger.LogDebug(
                "[move_drop_stale] seq={Seq} dir=0x{Dir:X2} at {X},{Y},{Z}",
                seq, dir, _character.X, _character.Y, _character.Z);
            return true;
        }

        // Strict sequence validation (ServUO-style): reject out-of-order walk packets.
        if (expectedSeq != 0 && seq != expectedSeq)
        {
            _logger.LogWarning("[move_reject] reason=seq_mismatch got={Got} expected={Expected} packet=0x{Packet:X2} batch={Batch} dir=0x{Dir:X2} run={Run} at {X},{Y},{Z}",
                seq, expectedSeq, _netState.LastMovementOpcode, _netState.LastMovementBatchSize, dir, running, _character.X, _character.Y, _character.Z);
            RejectMove(seq, now);
            return false;
        }

        // Fast-walk replay check intentionally dropped: the server never ships
        // the 6-key stack via 0xBF sub 0x01 to the client, so modern clients
        // either send key=0 (skipped by the !=0 guard) or emit locally-generated
        // keys whose rotation we cannot predict. False positives manifested as
        // mid-run "square jumping" rejects. The time-based throttle below is
        // the speedhack barrier.
        _netState.LastFastWalkKey = fastWalkKey;

        // Fastwalk throttle: reject if moving too fast.
        if (_character.PrivLevel < PrivLevel.GM)
        {
            int moveDelay = MovementEngine.GetMoveDelay(_character.IsMounted, running);

            if (MovementCreditEnabled)
            {
                EnsureCreditState();
                if (!MovementCreditSystem.TryConsumeCredit(
                        ref _movementCreditMs, ref _movementCreditLastTick,
                        MovementCreditBaseMs, MovementCreditMaxMs, moveDelay, now))
                {
                    if (_movementQueue!.IsFull || !_movementQueue.Enqueue(dir, seq, fastWalkKey, now))
                    {
                        _moveViolationCount++;
                        _logger.LogWarning("[move_reject] reason=credit_exhausted violations={Violations} credit={Credit}ms delay={Delay}ms queue={Queue} packet=0x{Packet:X2} batch={Batch} seq={Seq} dir=0x{Dir:X2} run={Run} at {X},{Y},{Z}",
                            _moveViolationCount, _movementCreditMs, moveDelay, _movementQueue.Count,
                            _netState.LastMovementOpcode, _netState.LastMovementBatchSize, seq, dir, running, _character.X, _character.Y, _character.Z);
                        RejectMove(seq, now);
                        if (MoveViolationKickThreshold > 0 && _moveViolationCount >= MoveViolationKickThreshold)
                            _netState.MarkClosing();
                        return false;
                    }
                    return false;
                }
            }
            else
            {
                RefillWalkTokens(now);

                string? rejectReason = null;
                if (_nextMoveTime > 0 && now + MoveToleranceMs < _nextMoveTime)
                    rejectReason = "throttle";
                else if (_walkTokens <= 0)
                    rejectReason = "walk_buffer";

                if (rejectReason != null)
                {
                    _moveViolationCount++;
                    _logger.LogWarning("[move_reject] reason={Reason} violations={Violations} ahead={Ahead}ms delay={Delay}ms tokens={Tokens} packet=0x{Packet:X2} batch={Batch} seq={Seq} dir=0x{Dir:X2} run={Run} at {X},{Y},{Z}",
                        rejectReason, _moveViolationCount, Math.Max(0, _nextMoveTime - now), moveDelay, _walkTokens,
                        _netState.LastMovementOpcode, _netState.LastMovementBatchSize, seq, dir, running, _character.X, _character.Y, _character.Z);
                    RejectMove(seq, now);
                    if (MoveViolationKickThreshold > 0 && _moveViolationCount >= MoveViolationKickThreshold)
                        _netState.MarkClosing();
                    return false;
                }
            }
        }

        // Execute the move
        bool moved;
        short oldX = _character.X;
        short oldY = _character.Y;
        sbyte oldZ = _character.Z;
        SphereNet.Game.Movement.WalkCheck.Diagnostic moveDiag = default;
        if (_movement != null)
            moved = _movement.TryMoveDetailed(_character, direction, running, seq, out moveDiag);
        else
        {
            GetDirectionDelta(direction, out short dx, out short dy);
            var newPos = new Point3D((short)(_character.X + dx), (short)(_character.Y + dy), _character.Z, _character.MapIndex);
            _character.Direction = direction;
            _world.MoveCharacter(_character, newPos);
            moved = true;
        }

        if (moved)
        {
            _character.LastMoveTick = now;
            int moveDelay = MovementEngine.GetMoveDelay(_character.IsMounted, running);
            if (!MovementCreditEnabled && _character.PrivLevel < PrivLevel.GM && _walkTokens > 0)
                _walkTokens--;
            _nextMoveTime = now + moveDelay;
            _moveViolationCount = 0;

            _movementHistory?.Record(now, direction, running, _character.IsMounted);
            if (SpeedHackDetectionEnabled && _speedHackDetector != null && _movementHistory != null)
            {
                var verdict = _speedHackDetector.Analyze(_movementHistory, _character.IsMounted, running, now);
                if (verdict == Movement.SpeedVerdict.Violation)
                {
                    _logger.LogWarning("[speed_hack] char={Name} verdict={Verdict} avg={Avg:F0}ms burst={Burst}",
                        _character.Name, verdict,
                        _movementHistory.AverageIntervalMs(5),
                        _movementHistory.CountBurstMoves(moveDelay / 2, 5));
                    OnSpeedHackDetected?.Invoke(_character, verdict);
                }
                else if (verdict == Movement.SpeedVerdict.Kick)
                {
                    _logger.LogWarning("[speed_hack_kick] char={Name}", _character.Name);
                    OnSpeedHackDetected?.Invoke(_character, verdict);
                    _netState.MarkClosing();
                    return true;
                }
            }

            if (expectedSeq == 0)
                _walkZCorrectionBase = oldZ;

            GetDirectionDelta(direction, out short expectedDx, out short expectedDy);
            short expectedX = (short)(oldX + expectedDx);
            short expectedY = (short)(oldY + expectedDy);

            if (_character.X != expectedX || _character.Y != expectedY)
            {
                _logger.LogWarning(
                    "[move_teleport] seq={Seq} dir={Dir} from {OldX},{OldY},{OldZ} expected {ExpX},{ExpY} actual {ActX},{ActY},{ActZ} — telepad/script moved character during CheckLocationEffects, suppressing MoveAck",
                    seq, direction, oldX, oldY, oldZ, expectedX, expectedY,
                    _character.X, _character.Y, _character.Z);
                return true;
            }

            if (WalkZCorrectionThreshold > 0 && Math.Abs(_character.Z - _walkZCorrectionBase) >= WalkZCorrectionThreshold)
            {
                _logger.LogInformation(
                    "[move_z_correct] seq={Seq} dir={Dir} baseZ={BaseZ} newZ={NewZ} accum={Accum} at {X},{Y}",
                    seq, direction, _walkZCorrectionBase, _character.Z,
                    Math.Abs(_character.Z - _walkZCorrectionBase), _character.X, _character.Y);
                _walkZCorrectionBase = _character.Z;
                _netState.Send(new PacketMoveReject(seq, _character.X, _character.Y, _character.Z, CurrentFacingDir()));
                _netState.WalkSequence = 0;
                byte flagsCorr = BuildMobileFlags(_character);
                byte dirCorr = (byte)((byte)_character.Direction | (running ? 0x80 : 0));
                byte notoCorr = GetNotoriety(_character);
                var movePktCorr = new PacketMobileMoving(
                    _character.Uid.Value, _character.BodyId,
                    _character.X, _character.Y, _character.Z, dirCorr,
                    _character.Hue, flagsCorr, notoCorr);
                if (BroadcastMoveNearby != null)
                    BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePktCorr, _character.Uid.Value, _character);
                else
                    BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePktCorr, _character.Uid.Value);
                return true;
            }

            byte notoriety = GetNotoriety(_character);
            int zDelta = _character.Z - oldZ;
            if (Math.Abs(zDelta) > 0)
            {
                _logger.LogInformation(
                    "[move_z_delta] seq={Seq} dir={Dir} run={Run} from {OldX},{OldY},{OldZ} to {NewX},{NewY},{NewZ} dz={Delta} startZ={StartZ} startTop={StartTop} fwdZ={FwdZ} last={Last} ourZ={OurZ} " +
                    "fwdLand=({LZ}/{LC}/{LT}) statics={SC} items={IC} tiles=[{Dump}]",
                    seq, direction, running, oldX, oldY, oldZ, _character.X, _character.Y, _character.Z, zDelta,
                    moveDiag.StartZ, moveDiag.StartTop, moveDiag.ForwardNewZ,
                    moveDiag.FwdReason, moveDiag.ForwardNewZ,
                    moveDiag.FwdLandZ, moveDiag.FwdLandCenter, moveDiag.FwdLandTop,
                    moveDiag.FwdSurfaceCount, moveDiag.FwdItemSurfaceCount,
                    moveDiag.FwdStaticDump);
            }

            _logger.LogDebug(
                "[move_ok] seq={Seq} dir={Dir} run={Run} pos={X},{Y},{Z}",
                seq, direction, running, _character.X, _character.Y, _character.Z);

            _netState.Send(new PacketMoveAck(seq, notoriety));

            byte flags = BuildMobileFlags(_character);
            byte dir77 = (byte)((byte)_character.Direction | (running ? 0x80 : 0));
            var movePacket = new PacketMobileMoving(
                _character.Uid.Value, _character.BodyId,
                _character.X, _character.Y, _character.Z, dir77,
                _character.Hue, flags, notoriety);

            if (BroadcastMoveNearby != null)
                BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
            else
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

            byte nextSeq = (byte)(seq + 1);
            if (nextSeq == 0) nextSeq = 1;
            _netState.WalkSequence = nextSeq;
            return true;
        }
        else
        {
            // Attribute the reject to a specific algorithm stage so walk jams
            // can be traced instead of logged as a vague "collision".
            GetDirectionDelta(direction, out short dxLog, out short dyLog);
            short tgtX = (short)(_character.X + dxLog);
            short tgtY = (short)(_character.Y + dyLog);
            string reason;
            if (moveDiag.MobBlocked) reason = "mob_block";
            else if (!moveDiag.ForwardOk) reason = "forward_blocked";
            else if (moveDiag.DiagonalChecked && (!moveDiag.LeftOk || !moveDiag.RightOk))
                reason = $"diagonal_edge left={moveDiag.LeftOk} right={moveDiag.RightOk}";
            else reason = "unknown";

            _logger.LogWarning(
                "[move_reject] {Reason} packet=0x{Packet:X2} batch={Batch} seq={Seq} dir={Dir} run={Run} from {FromX},{FromY},{FromZ} " +
                "target {TgtX},{TgtY} startZ={StartZ} startTop={StartTop} fwdZ={FwdZ} | " +
                "fwdLand=tile=0x{LandTile:X} ({LZ}/{LC}/{LT}) blocks={LB} consider={CL} | " +
                "statics={ST} impassable={IMP} surfaces={SC} items={IC} mobiles={MC} last={Last} | " +
                "tiles=[{Dump}] mobs=[{MobDump}]",
                reason, _netState.LastMovementOpcode, _netState.LastMovementBatchSize, seq, direction, running, _character.X, _character.Y, _character.Z,
                tgtX, tgtY, moveDiag.StartZ, moveDiag.StartTop, moveDiag.ForwardNewZ,
                moveDiag.FwdLandTileId, moveDiag.FwdLandZ, moveDiag.FwdLandCenter, moveDiag.FwdLandTop,
                moveDiag.FwdLandBlocks, moveDiag.FwdConsiderLand,
                moveDiag.FwdStaticTotal, moveDiag.FwdImpassableCount,
                moveDiag.FwdSurfaceCount, moveDiag.FwdItemSurfaceCount, moveDiag.FwdMobileCount,
                moveDiag.FwdReason, moveDiag.FwdStaticDump, moveDiag.FwdMobileDump);
            RejectMove(seq, now);
            return false;
        }
    }

    private void RejectMove(byte seq, long now, bool redrawSelf = false)
    {
        if (_character == null) return;
        _netState.Send(new PacketMoveReject(seq, _character.X, _character.Y, _character.Z, CurrentFacingDir()));
        if (redrawSelf)
        {
            _netState.Send(new PacketDrawPlayer(
                _character.Uid.Value, _character.BodyId, _character.Hue,
                BuildMobileFlags(_character),
                _character.X, _character.Y, _character.Z, CurrentFacingDir()));
            SendDrawObject(_character);
        }
        _netState.WalkSequence = 0;
        _walkZCorrectionBase = _character.Z;
        int resyncMs = Math.Max(0, MoveRejectResyncMs);
        _moveRejectResyncUntil = resyncMs > 0 ? now + resyncMs : 0;
    }

    private byte CurrentFacingDir()
    {
        return _character == null ? (byte)0 : (byte)((byte)_character.Direction & 0x07);
    }

    private void ResetWalkValidator()
    {
        _nextMoveTime = 0;
        _walkTokens = Math.Max(1, WalkBufferMax);
        _walkTokenLastMs = 0;
        _moveViolationCount = 0;
        _moveRejectResyncUntil = 0;

        _movementCreditMs = MovementCreditMaxMs;
        _movementCreditLastTick = 0;
        _movementQueue?.Clear();
        _movementHistory?.Clear();
        _speedHackDetector?.Reset();
    }

    private void EnsureCreditState()
    {
        _movementQueue ??= new Movement.MovementQueueProcessor(MovementQueueCapacity);
        _movementHistory ??= new Movement.MovementHistory(SpeedHackHistorySize);
        if (SpeedHackDetectionEnabled)
            _speedHackDetector ??= new Movement.SpeedHackDetector(
                SpeedHackRateThreshold, SpeedHackBurstWindow, SpeedHackCooldownMs);
    }

    public void TickMovementQueue(long nowMs)
    {
        if (!MovementCreditEnabled || _movementQueue == null || _movementQueue.Count == 0)
            return;

        if (_movementQueue.TryDequeue(out byte qDir, out byte qSeq, out uint qKey))
        {
            HandleMove(qDir, qSeq, qKey);
        }
    }

    private void RefillWalkTokens(long now)
    {
        int maxTokens = Math.Max(1, WalkBufferMax);
        if (_walkTokenLastMs <= 0)
        {
            _walkTokens = maxTokens;
            _walkTokenLastMs = now;
            return;
        }

        int regen = Math.Max(0, WalkRegenPerSecond);
        if (regen == 0 || _walkTokens >= maxTokens)
        {
            _walkTokenLastMs = now;
            _walkTokens = Math.Min(_walkTokens, maxTokens);
            return;
        }

        long elapsed = Math.Max(0, now - _walkTokenLastMs);
        int add = (int)(elapsed * regen / 1000);
        if (add <= 0)
            return;

        _walkTokens = Math.Min(maxTokens, _walkTokens + add);
        _walkTokenLastMs += add * 1000L / regen;
    }

    // ==================== Speech ====================

    public void HandleSpeech(byte type, ushort hue, ushort font, string text)
    {
        if (_character == null) return;

        if (TryHandleCommandSpeech(text))
            return;

        // Pet commands — "all follow", "all guard", "petname follow" etc.
        if (TryHandlePetCommand(text))
        {
            // Still broadcast the speech so others hear it
        }

        _speech?.ProcessSpeech(_character, text, (TalkMode)type, hue, font);

        // Broadcast speech to nearby clients
        int range = type switch
        {
            8 => 3,  // whisper
            9 => 48, // yell
            _ => 18  // say
        };

        var speechPacket = new PacketSpeechUnicodeOut(
            _character.Uid.Value, _character.BodyId,
            type, hue, font, "TRK", _character.Name, text
        );
        // Send to self first (speaker should see their own message)
        Send(speechPacket);
        // Then broadcast to nearby (excluding self since we already sent)
        BroadcastNearby?.Invoke(_character.Position, range, speechPacket, _character.Uid.Value);
    }

    // ==================== Combat ====================

    public void HandleAttack(uint targetUid)
    {
        if (_character == null || _character.IsDead) return;
        if (!_character.IsInWarMode)
            SetWarMode(true, syncClients: true, preserveTarget: true);

        // Source-X style target clear: attacking 0 resets current fight target.
        if (targetUid == 0 || targetUid == 0xFFFFFFFF)
        {
            _character.FightTarget = Serial.Invalid;
            _character.NextAttackTime = 0;
            return;
        }

        var target = _world.FindChar(new Serial(targetUid));
        if (target == null || target == _character) return;

        if (CombatHelper.IsCombatBlockedByRegion(_world, _character, target))
        {
            SysMessage(ServerMessages.Get("combat_nopvp"));
            return;
        }

        if (_triggerDispatcher != null)
        {
            var attackResult = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.Attack,
                new TriggerArgs { CharSrc = _character, O1 = target });
            if (attackResult == TriggerResult.True)
                return;
        }

        _character.FightTarget = target.Uid;

        // Region PvP enforcement
        if (target.IsPlayer && _character.IsPlayer)
        {
            var region = _world.FindRegion(_character.Position);
            if (region != null && region.IsFlag(Core.Enums.RegionFlag.NoPvP))
            {
                SysMessage(ServerMessages.Get("combat_nopvp"));
                return;
            }
            // Attacking an innocent (neither criminal nor murderer) in a
            // guarded / non-PvP region flags the aggressor criminal. Attacking
            // a red/gray player is self-defense — no flag. Config gate:
            // ATTACKINGISACRIME.
            bool targetIsInnocent = target.IsPlayer && !target.IsCriminal && !target.IsMurderer;
            if (Character.AttackingIsACrimeEnabled && targetIsInnocent &&
                region != null && region.IsFlag(Core.Enums.RegionFlag.Guarded))
            {
                _character.MakeCriminal();
            }

            // Source-X: helping a criminal fight an innocent flags you criminal
            if (Character.HelpingCriminalsIsACrimeEnabled && !_character.IsCriminal &&
                (target.IsCriminal || target.IsMurderer) &&
                target.FightTarget.IsValid)
            {
                var victim = _world.FindChar(target.FightTarget);
                if (victim != null && victim.IsPlayer && !victim.IsCriminal && !victim.IsMurderer)
                    _character.MakeCriminal();
            }
        }

        // Fire @CombatStart — if script blocks, cancel attack
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.CombatStart,
                new TriggerArgs { CharSrc = _character, O1 = target });
            if (result == TriggerResult.True)
                return;
        }

        _character.Memory_Fight_Start(target);
        target.Memory_Fight_Start(_character);

        // Set initial swing delay so the first hit isn't instant (unless COMBATFLAGS allows it)
        if (_character.NextAttackTime == 0)
        {
            bool instantFirst = (Character.CombatFlags & (int)CombatFlags.FirstHitInstant) != 0;
            if (!instantFirst)
            {
                var w = _character.GetEquippedItem(Layer.OneHanded)
                     ?? _character.GetEquippedItem(Layer.TwoHanded);
                _character.BeginEquipSwingWait(Environment.TickCount64, GetSwingDelayMs(_character, w), noWait: false);
            }
            else
            {
                _character.BeginEquipSwingWait(Environment.TickCount64, 0, noWait: true);
            }
        }

        // Range check — only swing now if already close enough
        var atkWeapon = _character.GetEquippedItem(Layer.OneHanded)
                     ?? _character.GetEquippedItem(Layer.TwoHanded);
        int atkMaxRange = CombatHelper.GetWeaponRange(atkWeapon).Max;
        int atkDist = CombatHelper.GetChebyshevDistance(_character, target);
        if (atkDist > atkMaxRange)
            return;

        TrySwingAt(target);
    }

    /// <summary>
    /// Auto-attack tick. Called every server tick — if the player has a
    /// valid FightTarget and the swing timer has elapsed, automatically
    /// performs the next melee/ranged swing. Maps to CChar::Fight_HitTry
    /// in Source-X which runs every tick for any character with a fight
    /// target, giving continuous combat without requiring repeated 0x05
    /// packets from the client.
    /// </summary>
    public void TickCombat()
    {
        if (_character == null || _character.IsDead) return;
        if (!_character.FightTarget.IsValid) return;
        if (!_character.IsInWarMode) return;

        long now = Environment.TickCount64;
        _character.RefreshCombatSwingState(now);
        if (now < _character.NextAttackTime) return;

        var target = _world.FindChar(_character.FightTarget);
        if (target == null || target.IsDead || target.IsDeleted)
        {
            _character.FightTarget = Serial.Invalid;
            return;
        }

        var weapon = _character.GetEquippedItem(Layer.OneHanded)
                  ?? _character.GetEquippedItem(Layer.TwoHanded);
        int maxRange = CombatHelper.GetWeaponRange(weapon).Max;
        int dist = CombatHelper.GetChebyshevDistance(_character, target);
        if (dist > maxRange)
            return;

        TrySwingAt(target);
    }

    private void TrySwingAt(Character target)
    {
        if (_character == null) return;

        long now = Environment.TickCount64;
        _character.RefreshCombatSwingState(now);
        if (now < _character.NextAttackTime)
            return;
        if (_character.CombatSwingState is SwingState.Swinging or SwingState.Equipping)
            return;

        // Source-X CChar::Fight_CanHit gates: dead / paralyzed / sleeping
        // attackers can't swing. Also a STAM<=0 char collapses (CCharAct.cpp
        // OnTick "Stat_GetVal(STAT_DEX) <= 0"), so block the swing entirely
        // and re-check next tick — don't burn the recoil timer.
        if (_character.IsDead) return;

        // Manifest ghost protection: a dead target (peace OR war manifest)
        // is never a valid combat target. Source-X CChar::Fight_IsAttackable
        // returns false on m_pPlayer && IsStatFlag(STATF_DEAD); the
        // translucent manifest is purely cosmetic and exists so plain
        // observers can SEE the ghost without being able to hit it.
        // Without this guard a manifested ghost would take damage and
        // produce a "kill the dead" loop with no corpse / no resurrect.
        if (target == null || target.IsDead)
        {
            _character.FightTarget = Serial.Invalid;
            return;
        }
        if (_character.Stam <= 0)
        {
            _character.NextAttackTime = now + 500;
            return;
        }
        if (_character.IsStatFlag(StatFlag.Freeze) || _character.IsStatFlag(StatFlag.Sleeping))
        {
            _character.NextAttackTime = now + 250;
            return;
        }

        // Spell casting blocks weapon swings (Source-X Skill_Magery /
        // SKTRIG_START path: the cast skill owns m_atFight while it runs).
        // We tolerate the swing finishing the *current* recoil but won't
        // start a new one mid-cast.
        if (_character.IsCasting)
        {
            _character.NextAttackTime = now + 500;
            return;
        }

        var weapon = _character.GetEquippedItem(Layer.OneHanded)
                  ?? _character.GetEquippedItem(Layer.TwoHanded);

        var prep = CombatHelper.ValidateSwingPrep(
            _world, _character, target, weapon, _character.PrivLevel, now, _world.CanSeeLOS);
        switch (prep.Result)
        {
            case CombatHelper.SwingPrepResult.Abort:
                if (prep.MessageKey != null)
                    SysMessage(ServerMessages.Get(prep.MessageKey));
                _character.FightTarget = Serial.Invalid;
                return;
            case CombatHelper.SwingPrepResult.RetryLater:
                if (prep.MessageKey != null)
                    SysMessage(ServerMessages.Get(prep.MessageKey));
                else if (CombatHelper.IsRangedWeapon(weapon))
                    SysMessage("You cannot see that target.");
                _character.NextAttackTime = now + Math.Max(prep.RetryMs, 250);
                return;
        }

        CombatHelper.RevealOnAttack(_character, _character.PrivLevel);

        int swingDelayMs = GetSwingDelayMs(_character, weapon);
        int swingDelayTenths = Math.Max(1, swingDelayMs / 100);
        if (_triggerDispatcher != null)
        {
            var hitTryArgs = new TriggerArgs
            {
                CharSrc = _character,
                O1 = target,
                ItemSrc = weapon,
                N1 = swingDelayTenths,
            };
            if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.HitTry, hitTryArgs) == TriggerResult.True)
                return;
            swingDelayTenths = Math.Max(1, hitTryArgs.N1);
            swingDelayMs = swingDelayTenths * 100;
        }

        FaceTarget(target);

        if (_triggerDispatcher != null)
        {
            var hitCheckArgs = new TriggerArgs { CharSrc = _character, O1 = target, ItemSrc = weapon };
            if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.HitCheck, hitCheckArgs) == TriggerResult.True)
            {
                EmitMissFeedback(target, weapon);
                _triggerDispatcher.FireCharTrigger(_character, CharTrigger.HitMiss,
                    new TriggerArgs { CharSrc = _character, O1 = target });
                return;
            }
        }

        _character.BeginSwingRecoil(now, swingDelayMs);

        // Each swing burns a small bit of stamina (Source-X Fight_Hit -> UpdateStatVal(STAT_DEX, -1)).
        if (_character.Stam > 0)
            _character.Stam = (short)(_character.Stam - 1);

        ushort swingAction = GetSwingAction(_character, weapon);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketAnimation(_character.Uid.Value, swingAction), 0);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketSound(GetSwingSound(weapon), _character.X, _character.Y, _character.Z), 0);

        if (CombatHelper.IsRangedWeapon(weapon))
        {
            var ammoType = weapon!.ItemType == ItemType.WeaponBow ? ItemType.WeaponArrow : ItemType.WeaponBolt;
            if (!HasAmmoInBackpack(ammoType))
            {
                SysMessage(ServerMessages.Get(Msg.CombatArchNoammo));
                return;
            }
            ConsumeAmmoFromBackpack(ammoType);
            EmitRangedProjectile(target, ammoType);
        }

        int damage = CombatEngine.ResolveAttack(
            _character,
            target,
            weapon,
            CombatHelper.ActiveCombatFlags);

        if (damage == 0)
        {
            // Source-X CCharFight Hit_Miss: emit attacker miss + target miss text.
            SysMessage(ServerMessages.GetFormatted(Msg.CombatMisss, target.Name));
            // No simple way yet to message the target client; the overhead packet is enough on the source side.
        }

        if (damage > 0)
        {
            if (_lastCombatNotifyTarget != target.Uid.Value)
            {
                _lastCombatNotifyTarget = target.Uid.Value;
                NpcSpeech(_character, ServerMessages.GetFormatted(Msg.CombatAttacks, target.Name));
            }

            _spellEngine?.TryInterruptFromDamage(target, damage);
            if (target.HasActiveSkillPending())
            {
                int abortedSkill = target.ClearActiveSkillPending();
                if (abortedSkill >= 0)
                    Character.ActiveSkillAborted?.Invoke(target, abortedSkill);
            }

            if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
            {
                target.FightTarget = _character.Uid;
                target.NextNpcActionTime = 0;
                OnWakeNpc?.Invoke(target);
            }

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Hit,
                new TriggerArgs { CharSrc = _character, O1 = target, N1 = damage });
            _triggerDispatcher?.FireCharTrigger(target, CharTrigger.GetHit,
                new TriggerArgs { CharSrc = _character, N1 = damage });

            if (weapon != null)
                _triggerDispatcher?.FireItemTrigger(weapon, ItemTrigger.Hit,
                    new TriggerArgs { CharSrc = _character, ItemSrc = weapon, O1 = target, N1 = damage });
            var shield = target.GetEquippedItem(Layer.TwoHanded);
            if (shield != null)
                _triggerDispatcher?.FireItemTrigger(shield, ItemTrigger.GetHit,
                    new TriggerArgs { CharSrc = _character, ItemSrc = shield, N1 = damage });

            _logger.LogDebug("{Attacker} hit {Target} for {Dmg} damage",
                _character.Name, target.Name, damage);

            ushort hitSound = weapon != null ? (ushort)0x0239 : (ushort)0x0135;
            var hitSoundPacket = new PacketSound(hitSound, target.X, target.Y, target.Z);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, hitSoundPacket, 0);

            ushort getHitAction = target.IsMounted ? (ushort)AnimationType.HorseSlap : (ushort)AnimationType.GetHit;
            var getHitAnim = new PacketAnimation(target.Uid.Value, getHitAction);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, getHitAnim, 0);

            var damagePacket = new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue));
            BroadcastNearby?.Invoke(target.Position, UpdateRange, damagePacket, 0);

            var healthPacket = new PacketUpdateHealth(
                target.Uid.Value, target.MaxHits, target.Hits);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, healthPacket, 0);

            if (target.Hits <= 0 && !target.IsDead && _deathEngine != null)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Kill,
                    new TriggerArgs { CharSrc = _character, O1 = target });
                _triggerDispatcher?.FireCharTrigger(target, CharTrigger.Death,
                    new TriggerArgs { CharSrc = _character });

                _knownChars.Remove(target.Uid.Value);

                var targetPos = target.Position;
                byte targetDir = (byte)((byte)target.Direction & 0x07);
                var corpse = _deathEngine.ProcessDeath(target, _character);
                _character.FightTarget = Serial.Invalid;

                if (corpse != null)
                {
                    uint corpseWireSerial = corpse.Uid.Value;
                    if (corpse.Amount > 1)
                        corpseWireSerial |= 0x80000000u;

                    if (target.IsPlayer)
                    {
                        _knownItems.Add(corpse.Uid.Value);
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                            targetDir);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpsePacket, 0);

                        // Player corpse: send contents + equip map for paperdoll corpse rendering.
                        foreach (var corpseItem in corpse.Contents)
                        {
                            var containerItem = new PacketContainerItem(
                                corpseItem.Uid.Value,
                                corpseItem.DispIdFull,
                                0,
                                corpseItem.Amount,
                                corpseItem.X,
                                corpseItem.Y,
                                corpse.Uid.Value,
                                corpseItem.Hue,
                                useGridIndex: true);
                            BroadcastNearby?.Invoke(targetPos, UpdateRange, containerItem, 0);
                        }

                        var corpseEquipEntries = new List<(byte Layer, uint ItemSerial)>();
                        var usedLayers = new HashSet<byte>();
                        foreach (var item in corpse.Contents)
                        {
                            byte layer = (byte)item.EquipLayer;
                            if (layer == (byte)Layer.None || layer == (byte)Layer.Face || layer == (byte)Layer.Pack)
                                continue;
                            if (!usedLayers.Add(layer))
                                continue;
                            corpseEquipEntries.Add((layer, item.Uid.Value));
                        }

                        var corpseEquip = new PacketCorpseEquipment(corpse.Uid.Value, corpseEquipEntries);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpseEquip, 0);

                        // NOTE: 0xAF DeathAnimation is NOT broadcast here.
                        // OnCharacterDeath below runs a per-observer dispatch
                        // that sends 0xAF to plain players (which remaps the
                        // mobile in ClassicUO so it disappears) and a
                        // 0x1D + 0x78 ghost mobile pair to staff observers
                        // (which avoids the 0xAF serial-remap so staff can
                        // see the ghost without the duplicate-mobile bug
                        // documented in the death plan). A blanket
                        // BroadcastNearby would defeat that distinction.
                    }
                    else
                    {
                        // NPC corpse — matches both Source-X (PacketDeath +
                        // RemoveFromView) and ServUO (DeathAnimation + Delete
                        // -> RemovePacket) reference flow:
                        //   1) 0x1A WorldItem  (corpse appears in world)
                        //   2) 0xAF DeathAnim  (mobile -> corpse transition)
                        //   3) 0x1D DeleteObj  (remove the dead mobile)
                        // Source-X CObjBase::DeletePrepare() calls
                        // RemoveFromView() which broadcasts 0x1D to all in
                        // range, and ServUO's Mobile.Kill() / OnDeath() chain
                        // ends with NPC.Delete() which sends 0x1D as well.
                        // Without 0x1D the dead mobile lingers in client
                        // collections (ClassicUO's 0xAF only re-keys the
                        // mobile under 0x80000000|serial; the visual entity
                        // is still there until the client receives 0x1D).
                        //
                        // 0x89/0x3C (CorpseEquipment/ContainerContent) are
                        // only sent for human-body corpses; sending them for
                        // monster corpses corrupts the client's input state.
                        _knownItems.Add(corpse.Uid.Value);
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                            targetDir);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpsePacket, 0);

                        var dirToKiller = target.Position.GetDirectionTo(_character.Position);
                        uint npcFallDir = (uint)dirToKiller <= 3 ? 1u : 0u;
                        var deathAnim = new PacketDeathAnimation(target.Uid.Value, corpse.Uid.Value, npcFallDir);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, deathAnim, 0);

                        var removeMobile = new PacketDeleteObject(target.Uid.Value);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, removeMobile, 0);
                    }
                }

                // PvP: notify the dying player's own client so it transitions
                // to ghost (body+hue swap, 0x77 broadcast, 0x20 self, 0x2C
                // death status). Without this the killer sees the corpse but
                // the victim's screen freezes with a still-alive paperdoll.
                if (target.IsPlayer && OnCharacterDeathOfOther != null)
                    OnCharacterDeathOfOther.Invoke(target);
            }

            // Reactive armor may have killed the attacker
            if (_character.Hits <= 0 && !_character.IsDead && _deathEngine != null)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Death,
                    new TriggerArgs { CharSrc = target });
                _deathEngine.ProcessDeath(_character, target);
                OnCharacterDeath();
            }
        }
        else
        {
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.HitMiss,
                new TriggerArgs { CharSrc = _character, O1 = target });

            BroadcastNearby?.Invoke(target.Position, UpdateRange,
                new PacketSound(0x0234, target.X, target.Y, target.Z), 0);
        }
    }

    private void EmitMissFeedback(Character target, Item? weapon)
    {
        if (_character == null) return;

        ushort missAction = GetSwingAction(_character, weapon);
        var missAnim = new PacketAnimation(_character.Uid.Value, missAction);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, missAnim, 0);

        ushort missSwingSound = GetSwingSound(weapon);
        var missSwingSoundPacket = new PacketSound(missSwingSound, _character.X, _character.Y, _character.Z);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, missSwingSoundPacket, 0);

        var missSound = new PacketSound(0x0234, target.X, target.Y, target.Z);
        BroadcastNearby?.Invoke(target.Position, UpdateRange, missSound, 0);
    }

    private void EmitRangedProjectile(Character target, ItemType ammoType)
    {
        if (_character == null) return;

        ushort effectId = ammoType == ItemType.WeaponBolt ? (ushort)0x1BFB : (ushort)0x0F3F;
        var projectile = new PacketEffect(
            type: 0,
            srcSerial: _character.Uid.Value,
            dstSerial: target.Uid.Value,
            effectId: effectId,
            srcX: _character.X,
            srcY: _character.Y,
            srcZ: _character.Z,
            dstX: target.X,
            dstY: target.Y,
            dstZ: target.Z,
            speed: 18,
            duration: 1,
            fixedDir: true,
            explode: false);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, projectile, 0);
    }

    /// <summary>
    /// Handle player death — body/hue ghost transition, death effect/sound,
    /// per-observer dispatch (plain players get 0xAF, staff get 0x1D + 0x78
    /// ghost mobile), self ghost render (0x77 + 0x20 + 0x78 self + 0x2C),
    /// and view-cache invalidation. Corpse + corpse equipment are already
    /// broadcast by the kill site (TrySwingAt PvP path or Program.OnNpcKill).
    /// </summary>
    public void OnCharacterDeath()
    {
        if (_character == null) return;

        // ---------------------------------------------------------------
        //   Source-X CChar::Death (CCharAct.cpp) reference order:
        //     1) MakeCorpse + UpdateCanSee(PacketDeath)   ← caller did this
        //     2) SetID(ghost) + SetHue(HUE_DEFAULT)       ← below
        //     3) addPlayerWarMode(off) + addTargCancel    ← below
        //     4) Per-observer dispatch (UpdateCanSee)     ← below
        //     5) PacketDeathMenu(Dead) on own client      ← below
        //
        //   Hue note: 0x4001 (HUE_TRANSLUCENT|1) makes the sprite
        //   see-through, NOT grey. ClassicUO renders the ghost body
        //   (0x192/0x193) as a proper grey shroud when hue == 0
        //   (HUE_DEFAULT). The "transparent ghost" bug from the early
        //   death logs was caused by sending 0x4001 here.
        // ---------------------------------------------------------------

        _spellEngine?.RevertPolymorphOnDeath(_character);

        ushort ghostBody = _character.BodyId == 0x0191 ? (ushort)0x0193 : (ushort)0x0192;
        _character.BodyId = ghostBody;
        _character.OSkin = _character.Hue.Value;
        _character.Hue = Core.Types.Color.Default;

        // pClient->addPlayerWarMode(off). We only need the local
        // state flip + the 0x72 PacketWarMode echo to the dying
        // client — the per-observer dispatch below carries the
        // post-death flags (War=off implicit, Female bit derived
        // from ghost body) through its 0x78 PacketDrawObject. A
        // syncClients=true here would inject an early 0x77 to staff
        // observers that mutates their cached mobile (Hue/Flags
        // updated, Graphic NOT updated) — that intermediate state
        // can leave ClassicUO's animation atlas pointing at the
        // alive body even after the follow-up 0x1D + 0x78. So we
        // suppress the broadcast and rely on per-observer dispatch.
        if (_character.IsInWarMode)
            SetWarMode(false, syncClients: false, preserveTarget: false);
        // The 0x72 echo is mandatory regardless — ClassicUO's input
        // handler latches on it to release the war-mode toggle and
        // unblock the death menu.
        _netState.Send(new PacketWarModeResponse(false));

        // pClient->addTargCancel. CRITICAL: PacketTarget(0,0) with
        // flags=0 (Neutral) does NOT cancel in ClassicUO — it OPENS
        // a brand-new target cursor (TargetManager.SetTargeting:165:
        // `IsTargeting = cursorType < TargetType.Cancel;`). We use
        // flags=3 (Cancel). The _targetCursorActive guard avoids a
        // spurious 0x6C when no cursor was open — that flash was the
        // "ölen karakterde target çıkıyor" symptom.
        if (_targetCursorActive)
        {
            _netState.Send(new PacketTarget(0x00, 0x00000000, flags: 3));
            ClearPendingTargetState();
        }

        // Death particle + sound — single BroadcastNearby with
        // excludeUid=0 reaches everyone in range INCLUDING the dying
        // player (Source-X UpdateCanSee semantic). A redundant
        // _netState.Send afterwards would double-send and produce the
        // duplicate 0x70/0x54 wire-log entries seen in earlier traces.
        var deathEffect = new PacketEffect(
            0x03,
            _character.Uid.Value, 0,
            0x3735,
            _character.X, _character.Y, (short)_character.Z,
            0, 0, 0,
            10, 30, true, false);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, deathEffect, 0);

        var deathSound = new PacketSound(0x01FE, _character.X, _character.Y, _character.Z);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, deathSound, 0);

        // ---------------------------------------------------------------
        //   Per-observer dispatch (mirror of CChar::UpdateCanSee with the
        //   ghost-visibility filter). ClassicUO's 0xAF DisplayDeath remaps
        //   the dying mobile to (serial | 0x80000000) and removes the
        //   original key from world.Mobiles (PacketHandlers.cs:3711). So:
        //
        //     - PLAIN observer  → 0xAF (mobile vanishes via remap, only
        //                         the corpse + death anim remain visible)
        //                       + server-side cache cleanup (no 0x1D
        //                         needed; the slot is already empty).
        //     - STAFF observer  → 0x1D (delete the living-body mobile)
        //                       + 0x78 ghost mobile (fresh spawn under
        //                         the original serial — safe because we
        //                         never sent 0xAF to this observer, so
        //                         no remap collision).
        //                       + cache marked as ghost so the next view-
        //                         delta tick sees no body change.
        //     - SELF (handled below the loop, not inside) — needs a full
        //       0x77 + 0x20 + 0x78 + 0x2C sequence.
        //
        //   This is the correct mapping for "staff sees ghosts, plain
        //   players don't" (the user's confirmed visibility rule) without
        //   triggering the duplicate-mobile bug that the 0xAF + 0x78
        //   combo produced on the same observer.
        // ---------------------------------------------------------------
        byte ghostFlags = BuildMobileFlags(_character);
        byte ghostNoto = GetNotoriety(_character);
        byte ghostDir = (byte)_character.Direction;
        short cx = _character.X, cy = _character.Y;
        sbyte cz = _character.Z;
        uint victimUid = _character.Uid.Value;

        // Find the corpse the kill site just created so we can wire the
        // 0xAF DeathAnimation correctly (plain observers need the
        // corpse serial to anchor the falling-body animation). One tile
        // search covers the corpse — DeathEngine.PlaceItem positions it
        // exactly at the victim's tile.
        uint corpseSerial = 0;
        if (_world != null)
        {
            foreach (var item in _world.GetItemsInRange(_character.Position, 0))
            {
                if (item.ItemType != ItemType.Corpse) continue;
                if (!item.TryGetTag("OWNER_UID", out string? ownerStr)) continue;
                if (!uint.TryParse(ownerStr, out uint ownerUid)) continue;
                if (ownerUid != victimUid) continue;
                corpseSerial = item.Uid.Value;
                break;
            }
        }

        // Source-X g_Rand.GetValFast(2) — 0/1 forward/backward fall.
        uint fallDir = (uint)Random.Shared.Next(2);
        var deathAnim = new PacketDeathAnimation(victimUid, corpseSerial, fallDir);

        var ghostEquipment = BuildEquipmentList(_character);

        // Follow-up 0x77 — even though ClassicUO's 0x78 path already
        // calls CheckGraphicChange() when GetOrCreateMobile spawns a
        // fresh entity (mobile.Graphic == 0 branch), some 4.x builds
        // skip the animation-atlas reset on the freshly-spawned ghost.
        // A redundant 0x77 with the same body re-runs CheckGraphicChange
        // against the now-current 0x192/0x193 graphic and forces the
        // animation cache to drop whatever leftover frames the alive
        // body left behind. Cheap to send, fully eliminates the
        // "staff still sees alive sprite" symptom.
        var ghostMovingBroadcast = new PacketMobileMoving(
            victimUid, ghostBody,
            cx, cy, cz, ghostDir,
            _character.Hue, ghostFlags, ghostNoto);

        ForEachClientInRange?.Invoke(_character.Position, UpdateRange, victimUid,
            (observerCh, observerClient) =>
            {
                bool canSeeGhost = observerCh.AllShow ||
                    observerCh.PrivLevel >= Core.Enums.PrivLevel.Counsel;
                if (canSeeGhost)
                {
                    // Staff path: send 0x78 ghost draw directly on the
                    // existing mobile serial. ClassicUO's DrawObject
                    // handler calls GetOrCreateMobile which returns the
                    // existing entity, updates Graphic to 0x192/0x193,
                    // and runs CheckGraphicChange() to reload the
                    // animation atlas. No 0x1D needed — sending
                    // DeleteObject first destroys the client-side mobile
                    // and the follow-up 0x78 recreates it, but some
                    // ClassicUO builds don't fully reset the animation
                    // cache on delete+recreate, leaving the alive sprite
                    // visible despite the ghost body being set.
                    observerClient.Send(new PacketDrawObject(
                        victimUid, ghostBody,
                        cx, cy, cz, ghostDir,
                        _character.Hue, ghostFlags, ghostNoto,
                        ghostEquipment, observerClient.NetState.SupportsNewMobileIncoming));
                    observerClient.Send(ghostMovingBroadcast);
                    observerClient.UpdateKnownCharRender(victimUid, ghostBody, _character.Hue,
                        ghostDir, cx, cy, cz);
                }
                else
                {
                    observerClient.Send(deathAnim);
                    observerClient.RemoveKnownChar(victimUid, sendDelete: false);
                }
            });

        // ---------------------------------------------------------------
        //   Self updates — make the ghost form actually render on the
        //   dying player's own screen.
        //
        //   ClassicUO graphic-update reality (verified against
        //   PacketHandlers.cs in 4.x):
        //
        //   * 0x77 (UpdateCharacter) — for self does NOT touch
        //     world.Player.Graphic in older builds; only NotorietyFlag
        //     and (sometimes) flags get applied. CheckGraphicChange is
        //     called against the OLD graphic, leaving the male/human
        //     state in place.
        //
        //   * 0x78 (UpdateObject) — for an existing (non-zero-graphic)
        //     mobile, the body update path is gated by
        //     `mobile.Graphic == 0`, i.e. only fresh spawns get a real
        //     graphic switch. For self the existing mobile always has
        //     the alive body cached, so the ghost graphic NEVER lands.
        //
        //   * 0x20 (UpdatePlayer) — the ONLY canonical path that sets
        //     world.Player.Graphic = newGraphic and follows it with a
        //     CheckGraphicChange + animation-atlas reset. This must be
        //     the first body-bearing packet sent to the dying player.
        //
        //   * 0x88 (OpenPaperdoll) — forces the paperdoll gump to
        //     re-render against the now-updated body so the dying
        //     player sees the grey ghost on the paperdoll too.
        //
        //   * 0x2C (DeathScreen) — opens the death menu UI; the client
        //     echoes RequestWarMode(false) in response.
        //
        //   Send order is therefore 0x20 → 0x77 (CheckGraphicChange
        //   re-trigger, harmless if already correct) → 0x88 → 0x2C →
        //   status. The previous order (0x77 → 0x20 → 0x78) left the
        //   ghost graphic stuck on the dying client because 0x78 self
        //   was a no-op and 0x77 was racing 0x20.
        // ---------------------------------------------------------------
        var drawPacket = new PacketDrawPlayer(
            victimUid, ghostBody, _character.Hue,
            ghostFlags, cx, cy, cz, ghostDir);
        _netState.Send(drawPacket);

        var ghostMoving = new PacketMobileMoving(
            victimUid, ghostBody,
            cx, cy, cz, ghostDir,
            _character.Hue, ghostFlags, ghostNoto);
        _netState.Send(ghostMoving);

        _netState.Send(new PacketDeathStatus(PacketDeathStatus.ActionDead));

        SendCharacterStatus(_character);
        SysMessage(ServerMessages.Get("combat_dead"));
    }

    /// <summary>
    /// Handle resurrection — body restore (ghost → human), Source-X
    /// "Resurrect with Corpse" auto re-equip, self redraw (0x77 + 0x20
    /// + 0x78 self), per-observer dispatch (single 0x78 fresh draw,
    /// works for both plain — never had the ghost — and staff — had
    /// the ghost mobile, 0x78 overwrites it), and view-cache resync so
    /// the next BuildViewDelta tick sees the new living body.
    /// </summary>
    public void OnResurrect()
    {
        if (_character == null || !_character.IsDead) return;

        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.Resurrect,
                new TriggerArgs { CharSrc = _character });
            if (result == TriggerResult.True)
                return;
        }

        _character.Resurrect();

        ushort restoredBody = _character.BodyId switch
        {
            0x0193 => (ushort)0x0191,
            0x0192 => (ushort)0x0190,
            _      => _spellEngine?.GetResurrectBody(_character) ?? _character.BodyId,
        };
        _character.BodyId = restoredBody;
        if (_character.OBody != 0 && _character.BodyId == _character.OBody)
            _character.OBody = 0;
        _character.ClearStatFlag(StatFlag.Polymorph);
        _character.Hue = _character.OSkin != 0
            ? new Core.Types.Color(_character.OSkin)
            : Core.Types.Color.Default;
        _character.OSkin = 0;

        // === Source-X "Resurrect with Corpse" — auto re-equip ===
        // If the resurrected character is standing on (or one tile of)
        // their own corpse, every item that was equipped at death goes
        // back to its original slot via the EQUIPLAYER tag, the rest
        // returns to the backpack, and the (now-empty) corpse is
        // deleted. Returns true iff the corpse was found — used only
        // for the SysMessage and for deciding whether to broadcast the
        // 0x1D corpse-delete (the corpse's own decay path will already
        // emit it, but we want it gone NOW so the resurrected player
        // isn't standing on a "ghost" corpse on every observer's
        // screen).
        bool corpseRestored = _deathEngine?.RestoreFromCorpse(_character) ?? false;

        byte resFlags = BuildMobileFlags(_character);
        byte resNoto = GetNotoriety(_character);
        byte resDir = (byte)_character.Direction;
        short cx = _character.X, cy = _character.Y;
        sbyte cz = _character.Z;
        uint uid = _character.Uid.Value;
        var resEquipment = BuildEquipmentList(_character);

        // === Self redraw ===
        // Symmetrical to OnCharacterDeath: 0x20 is the ONLY packet
        // that actually swaps world.Player.Graphic in ClassicUO, so
        // it MUST go first. 0x77 then triggers a redundant
        // CheckGraphicChange (cheap insurance for older builds), 0x78
        // delivers the restored equipment list, and SendPaperdoll
        // forces the gump to re-render against the new (alive) body.
        _netState.Send(new PacketDrawPlayer(
            uid, restoredBody, _character.Hue,
            resFlags, cx, cy, cz, resDir));

        var resMoving = new PacketMobileMoving(
            uid, restoredBody,
            cx, cy, cz, resDir,
            _character.Hue, resFlags, resNoto);
        _netState.Send(resMoving);

        _netState.Send(new PacketDrawObject(
            uid, restoredBody,
            cx, cy, cz, resDir,
            _character.Hue, resFlags, resNoto,
            resEquipment, _netState.SupportsNewMobileIncoming));

        // === Per-observer dispatch ===
        // Plain observer: never saw the ghost (filter dropped it during
        // BuildViewDelta) → 0x78 spawns a brand-new living mobile under
        // the original serial.
        // Staff observer: had the ghost mobile in their world.Mobiles
        // (we sent 0x1D + 0x78 ghost during death and never sent 0xAF
        // so no remap happened) → 0x78 overwrites the body+equipment
        // in-place via UpdateGameObject. Same packet, same outcome,
        // single dispatch path.
        // Either way we update the cache so the next view-delta tick
        // doesn't see a stale ghost-body entry and re-emit a duplicate.
        ForEachClientInRange?.Invoke(_character.Position, UpdateRange, uid,
            (observerCh, observerClient) =>
            {
                observerClient.Send(new PacketDrawObject(
                    uid, restoredBody,
                    cx, cy, cz, resDir,
                    _character.Hue, resFlags, resNoto,
                    resEquipment, observerClient.NetState.SupportsNewMobileIncoming));
                observerClient.UpdateKnownCharRender(uid, restoredBody, _character.Hue,
                    resDir, cx, cy, cz);
            });

        // === Resurrect-with-Corpse: client-side state sync ===
        // RestoreFromCorpse mutated the data layer (Equip + AddItem) but
        // did NOT push any wire updates. Without the broadcasts below,
        // ClassicUO observers don't know that backpack/armor came back —
        // the killer would still see a "naked" resurrected mobile, and
        // the resurrected player would see an empty backpack until they
        // close+reopen it (which forces the 0x3C ContainerContent
        // refresh). Source-X CChar::ContentAdd issues the same packet
        // pair: addObject (0x2E PacketWornItem) for layered gear,
        // addContents (0x25 PacketContainerItem) for backpack/loose
        // contents.
        if (corpseRestored)
        {
            // 1) Broadcast every equipped item (skip layers that wouldn't
            //    appear on a paperdoll: None / Face / Pack — Pack itself
            //    rides on the 0x78 above, its CONTENTS need 0x25 below).
            for (int layerIdx = 1; layerIdx <= (int)Layer.Horse; layerIdx++)
            {
                var layer = (Layer)layerIdx;
                if (layer == Layer.Pack || layer == Layer.Face) continue;
                var equip = _character.GetEquippedItem(layer);
                if (equip == null) continue;

                var wornPacket = new PacketWornItem(
                    equip.Uid.Value, equip.DispIdFull, (byte)layer,
                    uid, equip.Hue);
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, wornPacket, 0);
            }

            // 2) Stream the backpack contents back to the resurrecting
            //    player. We push 0x25 unconditionally (Sphere/ServUO
            //    both do) so even if no gump is currently open, the
            //    drag layer / hot-bar references are valid the moment
            //    a gump is opened. Containers nested inside the
            //    backpack (e.g. a pouch) also need their own contents
            //    pushed — we recurse via FindContentItem semantics.
            var pack = _character.Backpack;
            if (pack != null)
            {
                foreach (var child in _world.GetContainerContents(pack.Uid))
                {
                    _netState.Send(new PacketContainerItem(
                        child.Uid.Value, child.DispIdFull, 0,
                        child.Amount, child.X, child.Y,
                        pack.Uid.Value, child.Hue,
                        _netState.IsClientPost6017));
                }
            }
        }

        // Resurrection visual + sound — anchored fixed effect (0x376A
        // heal particle) and chime (0x0214). BroadcastNearby with
        // excludeUid=0 reaches the resurrected player too, so no extra
        // _netState.Send needed.
        var resEffect = new PacketEffect(
            0x03,
            uid, 0,
            0x376A,
            cx, cy, (short)cz,
            0, 0, 0,
            10, 30, true, false);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, resEffect, 0);

        var resSound = new PacketSound(0x0214, cx, cy, cz);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, resSound, 0);

        SendCharacterStatus(_character);
        SysMessage(ServerMessages.Get(corpseRestored
            ? "combat_resurrected_with_corpse"
            : "combat_resurrected"));
    }

    public void HandleWarMode(bool warMode)
    {
        if (_character == null) return;
        _logger.LogDebug("[war_toggle_request] client={ClientId} char=0x{Char:X8} requested={Requested} current={Current}",
            _netState.Id, _character.Uid.Value, warMode ? "war" : "peace", _character.IsInWarMode ? "war" : "peace");
        // @UserWarmode fires before the state flip so a script can abort
        // the toggle by returning 1. Matches Source-X @UserWarmode in
        // CClient::Event_WalkToggleWarmode.
        var triggerArgs = new TriggerArgs { CharSrc = _character, N1 = warMode ? 1 : 0 };
        if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserWarmode, triggerArgs) == TriggerResult.True)
            return;
        SetWarMode(warMode, syncClients: true, preserveTarget: false);
        SysMessage(warMode ? ServerMessages.Get("combat_warmode_on") : ServerMessages.Get("combat_warmode_off"));
    }

    // ==================== Spell Casting ====================

    public void HandleCastSpell(SpellType spell, uint targetUid)
    {
        if (_character == null || _spellEngine == null) return;

        // Fire @SpellCast — if script blocks, don't cast
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SpellCast,
                new TriggerArgs { CharSrc = _character, N1 = (int)spell });
            if (result == TriggerResult.True)
                return;
        }

        var spellDef = _spellEngine.GetSpellDef(spell);

        // Precast: power words + animation first, target cursor after timer.
        if (targetUid == 0 && spellDef != null && SpellEngine.IsPrecastEnabled(spellDef))
        {
            StartPrecast(spell);
            return;
        }

        // If no explicit target provided, check if the spell needs a target cursor
        if (targetUid == 0)
        {
            bool needsTarget = spellDef != null &&
                (spellDef.IsFlag(SpellFlag.TargChar) || spellDef.IsFlag(SpellFlag.TargObj) ||
                 spellDef.IsFlag(SpellFlag.Area) || spellDef.IsFlag(SpellFlag.Field));

            if (needsTarget)
            {
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (_character == null) return;
                    _character.SetCastTargetPosPending(new Point3D(x, y, z, _character.MapIndex));
                    HandleCastSpell(spell, serial != 0 ? serial : _character.Uid.Value);
                });
                return;
            }

            // Self-buff spell — target self
            targetUid = _character.Uid.Value;
        }

        var targetPos = _character.Position;
        if (_character.TryTakeCastTargetPosPending(out Point3D pendingPos))
            targetPos = pendingPos;
        else
        {
            var targetChar = _world.FindChar(new Serial(targetUid));
            if (targetChar != null)
                targetPos = targetChar.Position;
        }

        int castTime = _spellEngine.CastStart(_character, spell, new Serial(targetUid), targetPos);
        if (castTime > 0)
        {
            _character.SetCastTimerEnd(Environment.TickCount64 + castTime);
        }
        else
        {
            // Fire @SpellFail
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellFail,
                new TriggerArgs { CharSrc = _character, N1 = (int)spell });
            SysMessage(ServerMessages.Get("spell_cant_cast"));
        }
    }

    private void StartPrecast(SpellType spell)
    {
        if (_character == null || _spellEngine == null) return;

        int castTime = _spellEngine.CastStart(_character, spell, _character.Uid, _character.Position);
        if (castTime > 0)
        {
            _character.SpellPrecast = true;
            _character.SetCastTimerEnd(Environment.TickCount64 + castTime);
            return;
        }

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellFail,
            new TriggerArgs { CharSrc = _character, N1 = (int)spell });
        SysMessage(ServerMessages.Get("spell_cant_cast"));
    }

    private void PromptPrecastTarget(SpellType spell, Magic.SpellDef? spellDef)
    {
        if (_character == null || _spellEngine == null) return;

        bool needsTarget = spellDef != null &&
            (spellDef.IsFlag(SpellFlag.TargChar) || spellDef.IsFlag(SpellFlag.TargObj) ||
             spellDef.IsFlag(SpellFlag.Area) || spellDef.IsFlag(SpellFlag.Field));

        if (!needsTarget)
        {
            FinishPrecastCast(spell, _character.Uid.Value, _character.Position);
            return;
        }

        SysMessage(spellDef!.TargetPrompt.Length > 0
            ? spellDef.TargetPrompt
            : "Choose your target.");

        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;
            var targetPos = new Point3D(x, y, z, _character.MapIndex);
            uint uid = serial != 0 ? serial : _character.Uid.Value;
            var targetChar = _world.FindChar(new Serial(uid));
            if (targetChar != null)
                targetPos = targetChar.Position;
            FinishPrecastCast(spell, uid, targetPos);
        });
    }

    private void FinishPrecastCast(SpellType spell, uint targetUid, Point3D targetPos)
    {
        if (_character == null || _spellEngine == null) return;

        _character.UpdateCastTarget(new Serial(targetUid), targetPos);

        var spellDef = _spellEngine.GetSpellDef(spell);
        var targetChar = _world.FindChar(new Serial(targetUid));

        bool castOk = _spellEngine.CastDone(_character);
        if (castOk)
        {
            string spellName = spellDef?.Name ?? spell.ToString();
            SysMessage(ServerMessages.GetFormatted("spell_cast_ok", spellName));
        }
        else
        {
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellFail,
                new TriggerArgs { CharSrc = _character, N1 = (int)spell });
        }
    }

    public void TickSpellCast()
    {
        if (_character == null || _spellEngine == null) return;

        if (_character.IsCastTimerExpired(Environment.TickCount64))
        {
            _character.SetCastTimerEnd(0);

            if (_character.SpellPrecast)
            {
                _character.SpellPrecast = false;
                SpellType preSpell = default;
                if (_character.TryGetCastingSpell(out SpellType pre))
                    preSpell = pre;
                PromptPrecastTarget(preSpell, _spellEngine.GetSpellDef(preSpell));
                return;
            }

            // Retrieve spell ID before CastDone clears state
            int spellId = 0;
            if (_character.TryGetCastingSpell(out SpellType castingSpell))
                spellId = (int)castingSpell;

            // Get spell def + target BEFORE CastDone clears state
            var spellDef = _spellEngine.GetSpellDef((SpellType)spellId);
            uint targetUidRaw = _character.CastTargetUid.Value;
            var targetChar = targetUidRaw != 0 ? _world.FindChar(new Serial(targetUidRaw)) : null;

            bool castOk = _spellEngine.CastDone(_character);

            if (castOk)
            {
                // --- Spell name message ---
                string spellName = spellDef?.Name ?? $"Spell #{spellId}";
                SysMessage(ServerMessages.GetFormatted("spell_cast_ok", spellName));

                // --- Visual effect (0x70) on target ---
                var effectTarget = targetChar ?? _character;
                ushort effectGraphic = spellDef?.EffectId ?? 0;
                if (effectGraphic != 0)
                {
                    // type 3 = effect at location (on char), type 1 = bolt from src to dst
                    byte effectType = (spellDef != null && spellDef.IsFlag(SpellFlag.FxBolt)) ? (byte)1 : (byte)3;
                    var effectPacket = new PacketEffect(
                        effectType,
                        effectType == 1 ? _character.Uid.Value : effectTarget.Uid.Value,
                        effectTarget.Uid.Value,
                        effectGraphic,
                        effectTarget.X, effectTarget.Y, (short)effectTarget.Z,
                        effectTarget.X, effectTarget.Y, (short)effectTarget.Z,
                        10, 30, true, false);
                    _netState.Send(effectPacket);
                    BroadcastNearby?.Invoke(effectTarget.Position, UpdateRange, effectPacket, _character.Uid.Value);
                }

                // --- Buff icon (0xDF) for beneficial spells with duration ---
                if (_netState.SupportsBuffIcon &&
                    spellDef != null && spellDef.IsFlag(SpellFlag.Good) && spellDef.DurationBase > 0)
                {
                    int skillLvl = _character.GetSkill(spellDef.GetPrimarySkill());
                    int durTenths = spellDef.GetDuration(skillLvl);
                    ushort durSec = (ushort)Math.Min(durTenths / 10, ushort.MaxValue);
                    ushort buffIconId = GetBuffIconId((SpellType)spellId);
                    if (buffIconId != 0)
                    {
                        _netState.Send(new PacketBuffIcon(
                            _character.Uid.Value, buffIconId, true, durSec, spellName, ""));
                    }
                }

                // Fire @SpellEffect on caster, @SpellSuccess
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellEffect,
                    new TriggerArgs { CharSrc = _character, N1 = spellId });
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellSuccess,
                    new TriggerArgs { CharSrc = _character, N1 = spellId });

                // Consume scroll if cast was initiated from one
                if (_character.TryGetTag("SCROLL_UID", out string? scrollUidStr))
                {
                    _character.RemoveTag("SCROLL_UID");
                    if (uint.TryParse(scrollUidStr, out uint scrollUid))
                    {
                        var scroll = _world.FindItem(new Serial(scrollUid));
                        if (scroll != null && !scroll.IsDeleted)
                        {
                            if (scroll.Amount > 1)
                                scroll.Amount--;
                            else
                                scroll.Delete();
                        }
                    }
                }
            }
            else
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellFail,
                    new TriggerArgs { CharSrc = _character, N1 = spellId });
            }
        }
    }

    /// <summary>Map spell types to ClassicUO buff icon IDs.</summary>
    private static ushort GetBuffIconId(SpellType spell) => spell switch
    {
        SpellType.ReactiveArmor => 0x03E8,
        SpellType.Protection => 0x03E9,
        SpellType.NightSight => 0x03ED,
        SpellType.MagicReflect => 0x03EC,
        SpellType.Incognito => 0x03EF,
        SpellType.Bless => 0x03EA,
        SpellType.Agility => 0x03EB,
        SpellType.Cunning => 0x03EE,
        SpellType.Strength => 0x03F0,
        SpellType.Invisibility => 0x03F1,
        SpellType.Paralyze => 0x03F2,
        SpellType.Poison => 0x03F3,
        SpellType.Curse => 0x03F6,
        _ => 0,
    };

    /// <summary>
    /// Consolidated client tick: runs combat, spell casting, and stat updates.
    /// Call this once per server tick instead of calling TickCombat, TickSpellCast,
    /// and TickStatUpdate separately. This ensures consistent tick order and
    /// simplifies maintenance (single place to modify if new tick types are added).
    /// </summary>
    public void TickClientState()
    {
        TickMovementQueue(MoveClock());
        TickCombat();
        TickSpellCast();
        TickPendingSkill();
        TickStatUpdate();
    }

    /// <summary>
    /// Detect stat changes (from regen, combat, etc.) and send updates to client.
    /// Called each server tick.
    /// </summary>
    public void TickStatUpdate()
    {
        if (_character == null || !IsPlaying) return;

        bool hitsChanged = _character.Hits != _lastHits;
        bool manaChanged = _character.Mana != _lastMana;
        bool stamChanged = _character.Stam != _lastStam;
        if (hitsChanged || manaChanged || stamChanged)
        {
            long now = Environment.TickCount64;
            if (_lastVitalsPacketTick > 0 && now - _lastVitalsPacketTick < VitalsPacketIntervalMs)
                return;

            _lastHits = _character.Hits;
            _lastMana = _character.Mana;
            _lastStam = _character.Stam;
            _lastVitalsPacketTick = now;

            // Send only changed vital packets (avoid A1/A2/A3 spam).
            if (hitsChanged)
            {
                var healthPacket = new PacketUpdateHealth(
                    _character.Uid.Value, _character.MaxHits, _character.Hits);
                _netState.Send(healthPacket);
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, healthPacket, _character.Uid.Value);
            }
            if (manaChanged)
            {
                _netState.Send(new PacketUpdateMana(
                    _character.Uid.Value, _character.MaxMana, _character.Mana));
            }
            if (stamChanged)
            {
                _netState.Send(new PacketUpdateStamina(
                    _character.Uid.Value, _character.MaxStam, _character.Stam));
            }
        }
    }

    // ==================== Double Click / Item Use ====================
}
