using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static void RunMainLoop()
    {
            _log.LogInformation("Type 'help' for commands. Enter commands directly (e.g. save, status, quit).");

            // Do not enqueue the whole saved NPC population at startup. Large
            // worlds can contain 80K+ NPCs; draining that initial wheel while no
            // player is online stalls the login handshake. NPCs are woken when a
            // player activates their sector via WakeNewlyActiveSectorNpcs().
            if (_npcTimerWheel != null)
            {
                int npcCount = 0;
                foreach (var obj in _world.GetAllObjects())
                    if (obj is Character c && !c.IsPlayer && !c.IsDead && !c.IsDeleted)
                        npcCount++;
                _log.LogInformation("NPC timer wheel initialized: {Count} NPCs deferred until sectors become active", npcCount);
            }

            // --- 9. Main Game Loop ---
            _running = true;
            var sw = Stopwatch.StartNew();
            const int TickIntervalMs = 50; // 20 ticks per second
            const int MaxCatchUpTicksPerLoop = 4;
            long nextTickMs = TickIntervalMs;

            while (_running)
            {
                long now = sw.ElapsedMilliseconds;

                // Console input (from WinForms command queue or headless stdin queue)
    #if WINFORMS
                if (_consoleForm != null)
                {
                    while (_consoleForm.CommandQueue.TryDequeue(out string? consoleCmd))
                        HandleConsoleCommand(consoleCmd);
                }
                else
    #endif
                {
                    while (_headlessCommandQueue.TryDequeue(out string? consoleCmd))
                        HandleConsoleCommand(consoleCmd);
                }

                while (_mainLoopActions.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception ex) { _log.LogError(ex, "Main-loop action failed"); }
                }

                // Network I/O runs every iteration for low latency
                _network.CheckNewConnections();
                _network.ProcessAllInput();

                // Fast-path dirty: mark nearby clients for refresh, then run
                // UpdateClientView only for those clients. Gated by HasDirty.
                const bool FastPathViewDeltaEnabled = true;
                if (FastPathViewDeltaEnabled && _world.HasDirty)
                {
                    var dirtyObjects = _world.DrainDirtyObjectsSnapshot();
                    MarkClientsNearDirtyObjects(dirtyObjects);
                    foreach (var client in _clients.Values)
                    {
                        if (client.ViewNeedsRefresh && client.IsPlaying)
                        {
                            client.UpdateClientView();
                            client.ViewNeedsRefresh = false;
                        }
                    }
                }

                // Stress-test batch generation / cleanup — both are cooperative:
                // no-op when queues are empty. Runs every main-loop iteration so
                // long jobs finish quickly without starving the tick.
                if (_stressEngine != null)
                {
                    if (_stressEngine.IsGenerating) _stressEngine.OnTick();
                    if (_stressEngine.IsCleaning)   _stressEngine.TickCleanup();
                }

                // Bot restock — every 3 minutes, refresh bot inventories
                if (now - _lastBotRestockMs > 180_000)
                {
                    _lastBotRestockMs = now;
                    RestockBotCharacters();
                }

                // Replay packet delivery runs every main-loop iteration
                // (~1-15ms) for smooth character movement instead of being
                // batched into the 100ms server tick.
                if (_recordingEngine.HasActiveReplays)
                    TickReplayPackets();

                _network.ProcessAllOutput();
                _network.Tick();

                int catchUpTicks = ComputeDueTickCount(now, ref nextTickMs, TickIntervalMs, MaxCatchUpTicksPerLoop);
                for (int tick = 0; tick < catchUpTicks; tick++)
                {
                    RunServerTick();
                    _network.ProcessAllOutput();
                }

                TickYieldStrategy.Yield(_config.TickSleepMode);
            }

            // --- 10. Shutdown ---
            _log.LogInformation("Shutting down...");
            _systemHooks.DispatchServer("exit", _serverHookContext);

            _log.LogWarning("Auto-save on shutdown is disabled. Use 'save' command before quitting to persist world state.");

            _stateRecorder?.Dispose();
            _telnet?.Dispose();
            _webStatus?.Dispose();
            _network.Dispose();
            _mapData?.Dispose();
            _scriptDb.Close();
            _scriptLdb.Close();
            _scriptFile?.Dispose();

            _log.LogInformation("SphereNet stopped.");
            Log.CloseAndFlush();

    #if WINFORMS
            // Close the WinForms window if still open
            if (_consoleForm != null && !_consoleForm.IsDisposed)
            {
                _consoleForm.BeginInvoke(() => _consoleForm.Close());
            }
    #endif
    }

    private static int ComputeDueTickCount(long nowMs, ref long nextTickMs, int tickIntervalMs, int maxCatchUpTicks)
    {
        int due = 0;
        while (nowMs >= nextTickMs && due < maxCatchUpTicks)
        {
            nextTickMs += tickIntervalMs;
            due++;
        }

        if (due == maxCatchUpTicks && nowMs >= nextTickMs)
            nextTickMs = nowMs + tickIntervalMs;

        return due;
    }

    private static void RunServerTick()
    {
        _tickCounter++;
        long tickStart = Stopwatch.GetTimestamp();
        try
        {
            if (!_multicoreRuntimeEnabled && _multicoreFallbackMs > 0
                && Environment.TickCount64 - _multicoreFallbackMs >= MulticoreRecoveryCooldownMs)
            {
                _multicoreRuntimeEnabled = true;
                _multicoreFallbackMs = 0;
                _log.LogInformation("Multicore mode re-enabled after {Cooldown}s cooldown.", MulticoreRecoveryCooldownMs / 1000);
            }

            if (_multicoreRuntimeEnabled)
                RunMulticoreTick();
            else
                RunSingleThreadTick();
        }
        catch (OperationCanceledException oce)
        {
            _log.LogWarning(oce, "Multicore tick timeout. Falling back to single-thread mode.");
            _multicoreRuntimeEnabled = false;
            _multicoreFallbackMs = Environment.TickCount64;
            RunSingleThreadTick();
        }
        catch (Exception ex)
        {
            if (_multicoreRuntimeEnabled)
            {
                _log.LogWarning(ex, "Multicore tick failure. Falling back to single-thread mode.");
                _multicoreRuntimeEnabled = false;
                _multicoreFallbackMs = Environment.TickCount64;
                RunSingleThreadTick();
            }
            else
            {
                throw;
            }
        }
        finally
        {
            long totalUs = ToMicroseconds(Stopwatch.GetTimestamp() - tickStart);
            if (totalUs > _telemetryMaxTickUs)
                _telemetryMaxTickUs = totalUs;
            TickHistogram.Record((int)(totalUs / 1000));

            long nowMs = Environment.TickCount64;
            if (totalUs > 25_000 && nowMs - _lastSlowTickWarningMs > 10_000)
            {
                _lastSlowTickWarningMs = nowMs;
                _slowTickCount++;
                _lastSlowTickDominantPhase = GetDominantTickPhase();
                _log.LogWarning(
                    "[slow_tick] mode={Mode} tick={Tick} total={TotalMs}ms dominant={DominantPhase} snapshot={SnapshotMs}ms compute={ComputeMs}ms (npc_build={NpcBuildMs}ms client_state={ClientStateMs}ms npc_apply={NpcApplyMs}ms view_build={ViewBuildMs}ms) apply={ApplyMs}ms flush={FlushMs}ms",
                    _multicoreRuntimeEnabled ? "multicore" : "single",
                    _tickCounter,
                    (totalUs / 1000.0).ToString("F1"),
                    _lastSlowTickDominantPhase,
                    (_telemetrySnapshotUs / 1000.0).ToString("F1"),
                    (_telemetryComputeUs / 1000.0).ToString("F1"),
                    (_telemetryNpcBuildUs / 1000.0).ToString("F1"),
                    (_telemetryClientStateUs / 1000.0).ToString("F1"),
                    (_telemetryNpcApplyUs / 1000.0).ToString("F1"),
                    (_telemetryViewBuildUs / 1000.0).ToString("F1"),
                    (_telemetryApplyUs / 1000.0).ToString("F1"),
                    (_telemetryFlushUs / 1000.0).ToString("F1"));
            }

            // Periodic tick stats: log average and max tick time every 30 seconds
            RecordTickTelemetry(totalUs);
            _tickStatsTotalUs += totalUs;
            if (totalUs > _tickStatsMaxUs) _tickStatsMaxUs = totalUs;
            _tickStatsCount++;

            if (nowMs - _lastTickStatsLogMs >= 30_000)
            {
                double avgMs = _tickStatsCount > 0 ? (_tickStatsTotalUs / _tickStatsCount / 1000.0) : 0;
                double maxMs = _tickStatsMaxUs / 1000.0;
                var tickTelemetry = GetTickTelemetrySnapshot();
                int onlinePlayers = _clients.Values.Count(c => c.IsPlaying);
                var (chars, items, _) = _world.GetStats();

                // Include bot stats if bots are active
                if (_botEngine != null && _botEngine.TotalBots > 0)
                {
                    var botStats = _botEngine.GetStats();
                    _log.LogInformation(
                        "[tick_stats] ticks={Count} avg={AvgMs:F1}ms max={MaxMs:F1}ms p50={P50Ms:F1}ms p95={P95Ms:F1}ms p99={P99Ms:F1}ms players={Players} chars={Chars} items={Items} bots={Bots}/{BotTotal} pps_in={PpsIn:F0} pps_out={PpsOut:F0}",
                        _tickStatsCount, avgMs, maxMs, tickTelemetry.P50Ms, tickTelemetry.P95Ms, tickTelemetry.P99Ms, onlinePlayers, chars, items,
                        botStats.ActiveBots, botStats.TotalBots, botStats.PacketsPerSecIn, botStats.PacketsPerSecOut);
                }
                else
                {
                    _log.LogInformation(
                        "[tick_stats] ticks={Count} avg={AvgMs:F1}ms max={MaxMs:F1}ms p50={P50Ms:F1}ms p95={P95Ms:F1}ms p99={P99Ms:F1}ms players={Players} chars={Chars} items={Items}",
                        _tickStatsCount, avgMs, maxMs, tickTelemetry.P50Ms, tickTelemetry.P95Ms, tickTelemetry.P99Ms, onlinePlayers, chars, items);
                }

                _tickStatsTotalUs = 0;
                _tickStatsMaxUs = 0;
                _tickStatsCount = 0;
                _lastTickStatsLogMs = nowMs;
            }
        }
    }

    private static string GetDominantTickPhase()
    {
        var phases = new[]
        {
            ("snapshot", _telemetrySnapshotUs),
            ("compute", _telemetryComputeUs),
            ("npc_build", _telemetryNpcBuildUs),
            ("client_state", _telemetryClientStateUs),
            ("npc_apply", _telemetryNpcApplyUs),
            ("view_build", _telemetryViewBuildUs),
            ("apply", _telemetryApplyUs),
            ("flush", _telemetryFlushUs),
        };
        return phases.OrderByDescending(p => p.Item2).First().Item1;
    }

    private static void RecordTickTelemetry(long totalUs)
    {
        _tickTelemetryWindowUs[_tickTelemetryWriteIndex] = totalUs;
        _tickTelemetryWriteIndex = (_tickTelemetryWriteIndex + 1) % TickTelemetryWindowSize;
        if (_tickTelemetrySampleCount < TickTelemetryWindowSize)
            _tickTelemetrySampleCount++;
    }

    private static TickTelemetrySnapshot GetTickTelemetrySnapshot()
    {
        int count = _tickTelemetrySampleCount;
        if (count == 0)
            return new TickTelemetrySnapshot(false, _multicoreRuntimeEnabled, 0, 0, 0, 0, 0, 0, 0);

        var samples = new long[count];
        Array.Copy(_tickTelemetryWindowUs, samples, count);
        Array.Sort(samples);

        double p50 = Percentile(samples, 0.50) / 1000.0;
        double p95 = Percentile(samples, 0.95) / 1000.0;
        double p99 = Percentile(samples, 0.99) / 1000.0;
        double max = samples[^1] / 1000.0;
        double avg = samples.Average() / 1000.0;
        return new TickTelemetrySnapshot(true, _multicoreRuntimeEnabled, count, avg, max, p50, p95, p99,
            _telemetryMaxTickUs / 1000.0);
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private readonly record struct TickTelemetrySnapshot(
        bool HasSamples,
        bool MulticoreEnabled,
        int SampleCount,
        double AvgMs,
        double MaxMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxSinceStartMs);

    private static void RunSingleThreadTick()
    {
        long p0 = Stopwatch.GetTimestamp();

        _world.OnTick();
        _spellEngine.ProcessExpirations(Environment.TickCount64);

        // Wake NPCs in sectors that just became active (player entered area)
        WakeNewlyActiveSectorNpcs();

        // NPC AI via timer wheel — only reschedule NPCs that remain in
        // active sectors. Sleeping NPCs exit the wheel entirely and get
        // bulk-woken by WakeNewlyActiveSectorNpcs when a player enters.
        {
            long now = Environment.TickCount64;
            var dueNpcs = _npcTimerWheel.Advance(now);
            foreach (var npc in dueNpcs)
            {
                _npcAI.OnTickAction(npc);
                if (!npc.IsDead && !npc.IsDeleted && (npc.NpcMaster.IsValid || _world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y)))
                    _npcTimerWheel.Schedule(npc, npc.NextNpcActionTime);
            }
        }

        _telemetrySnapshotUs = ToMicroseconds(Stopwatch.GetTimestamp() - p0);
        _telemetryComputeUs = 0;
        _telemetryNpcBuildUs = 0;
        _telemetryClientStateUs = 0;
        _telemetryNpcApplyUs = 0;
        _telemetryViewBuildUs = 0;

        long p1 = Stopwatch.GetTimestamp();

        var dirtyObjects = _world.HasDirty ? _world.DrainDirtyObjectsSnapshot() : [];
        MarkClientsNearDirtyObjects(dirtyObjects);

        long rttNow = Environment.TickCount64;
        foreach (var client in _clients.Values)
        {
            client.NetState.SendRttPing(rttNow);
            client.TickClientState();
            if (client.ViewNeedsRefresh)
            {
                client.UpdateClientView();
                client.ViewNeedsRefresh = false;
            }
        }

        if (_recordingEngine.HasActiveReplays)
            TickReplayOverlays();

        _stateRecorder?.Tick(Environment.TickCount64, _world.GetAllObjects().OfType<Character>());

        _macroEngine?.Tick(Environment.TickCount64,
            uid => _clientsByCharUid.GetValueOrDefault(new Serial(uid)),
            FindItemInBackpack,
            (uid, msg) => { if (_world.FindChar(new Serial(uid)) is { } c) SendSysMessage(c, msg); });

        _telemetryApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p2 = Stopwatch.GetTimestamp();
        RunPostTickMaintenance();
        _telemetryFlushUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2);

        MaybeRunDeterminismGuardrail();
    }

    // Below this many work items, Parallel.ForEach is pure overhead
    // (partitioner + worker ramp-up + lambda capture cost more than
    // the actual loop body). Empirically a single 1-vCPU VDS handles
    // 4 ticks/sec at ~5 ms when this threshold short-circuits the
    // 1-client / 0-NPC steady state, vs 50–300 ms when forced through
    // ParallelForEach. Mirrors GameWorld.OnTickParallel sector cutoff.
    private const int ParallelComputeMinBatch = 16;

    private static void RunMulticoreTick()
    {
        int workerCount = _config.MulticoreWorkerCount > 0 ? _config.MulticoreWorkerCount : Environment.ProcessorCount;
        int timeoutMs = Math.Max(100, _config.MulticorePhaseTimeoutMs);
        using var cts = new CancellationTokenSource(timeoutMs);

        long p0 = Stopwatch.GetTimestamp();
        _world.OnTickParallel(workerCount, cts.Token);
        _spellEngine.ProcessExpirations(Environment.TickCount64);

        // Wake NPCs in sectors that just became active (player entered area)
        WakeNewlyActiveSectorNpcs();

        // NPC AI via timer wheel
        var npcSnapshot = _npcTimerWheel.Advance(Environment.TickCount64);

        _reusableClientSnapshot.Clear();
        foreach (var c in _clients.Values)
        {
            if (c.IsPlaying)
                _reusableClientSnapshot.Add(c);
        }
        var clientSnapshot = _reusableClientSnapshot;
        _telemetrySnapshotUs = ToMicroseconds(Stopwatch.GetTimestamp() - p0);

        long p1 = Stopwatch.GetTimestamp();
        long nowTick = Environment.TickCount64;

        // Reuse buffers — both lists are cleared at end of tick. Avoids
        // ConcurrentBag/ConcurrentDictionary allocation churn that was
        // visible as slow_tick spikes on light loads.
        var decisionList = _reusableDecisionList;
        decisionList.Clear();
        if (npcSnapshot.Count >= ParallelComputeMinBatch)
        {
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cts.Token
            };
            Parallel.ForEach(npcSnapshot, po, npc =>
            {
                var decision = _npcAI.BuildDecision(npc, nowTick);
                if (decision.HasValue)
                    lock (decisionList) decisionList.Add(decision.Value);
            });
        }
        else
        {
            foreach (var npc in npcSnapshot)
            {
                var decision = _npcAI.BuildDecision(npc, nowTick);
                if (decision.HasValue)
                    decisionList.Add(decision.Value);
            }
        }
        _telemetryNpcBuildUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p1b = Stopwatch.GetTimestamp();
        long rttNow = Environment.TickCount64;
        foreach (var client in clientSnapshot)
        {
            client.NetState.SendRttPing(rttNow);
            client.TickClientState();
        }
        _telemetryClientStateUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1b);

        long p1c = Stopwatch.GetTimestamp();
        // Parallel.ForEach adds decisions in completion order, which is
        // non-deterministic and differs from the single-thread wheel order.
        // Apply order matters for order-sensitive side effects (ally rally,
        // surround steps, shared-RNG draws), so sort by UID for a stable,
        // reproducible apply order before committing.
        decisionList.Sort(static (a, b) => a.NpcUid.CompareTo(b.NpcUid));

        // Apply NPC decisions — fires CharacterMoved for each NPC move,
        // which immediately notifies nearby clients via OnCharacterMoved.
        foreach (var decision in decisionList)
            _npcAI.ApplyDecision(decision);
        _npcAI.PurgeStalePaths();

        // Mark clients near dirty objects for refresh
        var dirtyObjects = _world.HasDirty ? _world.DrainDirtyObjectsSnapshot() : [];
        MarkClientsNearDirtyObjects(dirtyObjects);
        _telemetryNpcApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1c);

        // View delta: only for clients flagged ViewNeedsRefresh (moved or
        // had nearby objects change). Most clients are idle and skip entirely.
        long p1d = Stopwatch.GetTimestamp();
        var refreshClients = _reusableRefreshClients;
        refreshClients.Clear();
        foreach (var client in clientSnapshot)
        {
            if (client.ViewNeedsRefresh)
                refreshClients.Add(client);
        }

        var clientDeltas = _reusableClientDeltas;
        clientDeltas.Clear();
        if (refreshClients.Count >= ParallelComputeMinBatch)
        {
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cts.Token
            };
            var concurrent = _reusableViewDeltaConcurrent;
            concurrent.Clear();
            Parallel.ForEach(refreshClients, po, client =>
            {
                var delta = client.BuildViewDelta();
                if (delta != null)
                    concurrent[client.NetState.Id] = delta;
            });
            foreach (var kv in concurrent)
                clientDeltas[kv.Key] = kv.Value;
        }
        else
        {
            foreach (var client in refreshClients)
            {
                var delta = client.BuildViewDelta();
                if (delta != null)
                    clientDeltas[client.NetState.Id] = delta;
            }
        }
        _telemetryViewBuildUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1d);
        _telemetryComputeUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p2 = Stopwatch.GetTimestamp();
        bool hasRecordings = _recordingEngine.HasActiveRecordings;
        foreach (var client in refreshClients)
        {
            client.ViewNeedsRefresh = false;
            if (!clientDeltas.TryGetValue(client.NetState.Id, out var delta))
                continue;

            client.ApplyViewDelta(delta);
            client.SyncOpenMapStaticDoors();

            if (hasRecordings && delta.NewChars.Count > 0)
            {
                uint charUid = client.Character?.Uid.Value ?? 0;
                if (charUid != 0 && _recordingEngine.IsRecording(charUid))
                {
                    foreach (var (ch, _) in delta.NewChars)
                    {
                        var equip = new List<(uint, ushort, byte, ushort)>();
                        for (int layer = 1; layer <= (int)Layer.Horse; layer++)
                        {
                            var item = ch.GetEquippedItem((Layer)layer);
                            if (item != null)
                                equip.Add((item.Uid.Value, item.DispIdFull, (byte)layer, item.Hue));
                        }
                        byte flags = 0;
                        if (ch.IsInWarMode) flags |= 0x40;
                        if (ch.IsInvisible) flags |= 0x80;
                        var pkt = new PacketDrawObject(
                            ch.Uid.Value, ch.BodyId, ch.X, ch.Y, ch.Z,
                            (byte)ch.Direction, ch.Hue, flags, 0x01,
                            equip.ToArray());
                        _recordingEngine.CapturePacket(charUid, ch.Position,
                            pkt.Build().Span.ToArray());
                    }
                }
            }
        }
        _telemetryApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2);

        if (_recordingEngine.HasActiveReplays)
            TickReplayOverlays();

        _stateRecorder?.Tick(Environment.TickCount64, _world.GetAllObjects().OfType<Character>());

        _macroEngine?.Tick(Environment.TickCount64,
            uid => _clientsByCharUid.GetValueOrDefault(new Serial(uid)),
            FindItemInBackpack,
            (uid, msg) => { if (_world.FindChar(new Serial(uid)) is { } c) SendSysMessage(c, msg); });

        // Re-schedule only active-sector NPCs (and pets). Sleeping NPCs
        // exit the wheel — WakeNewlyActiveSectorNpcs handles sector transitions.
        if (_npcTimerWheel != null)
        {
            foreach (var npc in npcSnapshot)
            {
                // Match the single-thread reschedule guard: a dead NPC (killed
                // during ApplyDecision) must not be re-queued into the wheel.
                if (!npc.IsDead && !npc.IsDeleted && !npc.IsPlayer &&
                    (npc.NpcMaster.IsValid || _world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y)))
                    _npcTimerWheel.Schedule(npc, npc.NextNpcActionTime);
            }
        }

        long p3 = Stopwatch.GetTimestamp();
        RunPostTickMaintenance();
        _telemetryFlushUs = ToMicroseconds(Stopwatch.GetTimestamp() - p3);

        MaybeRunDeterminismGuardrail();
    }

    private static void WakeNpc(Character npc)
    {
        if (npc.IsPlayer || npc.IsDeleted || npc.IsDead) return;
        npc.NextNpcActionTime = 0;
        _npcTimerWheel?.Remove(npc);
        _npcTimerWheel?.Schedule(npc, Environment.TickCount64 + 100);
    }

    private static void WakeNewlyActiveSectorNpcs()
    {
        if (_npcTimerWheel == null) return;
        var sectors = _world.NewlyActiveSectors;
        if (sectors.Count == 0) return;
        foreach (var sector in sectors)
        {
            foreach (var ch in sector.Characters)
            {
                if (!ch.IsPlayer && !ch.IsDeleted && !ch.IsDead)
                    WakeNpc(ch);
            }
        }
    }

    private static void CleanupSummonedGuards(long now)
    {
        if (_summonedGuardExpiry.Count == 0)
            return;

        foreach (var (uid, expireAt) in _summonedGuardExpiry.ToArray())
        {
            var guard = _world.FindChar(uid);
            if (guard == null || guard.IsDeleted || guard.IsDead)
            {
                _summonedGuardExpiry.Remove(uid);
                continue;
            }

            if (now < expireAt)
                continue;

            var removePacket = new PacketDeleteObject(uid.Value);
            BroadcastNearby(guard.Position, 18, removePacket, 0);
            _world.DeleteObject(guard);
            guard.Delete();
            _summonedGuardExpiry.Remove(uid);
        }
    }

    private static void RunDecayCatchup(long now)
    {
        if (now < _nextDecayCatchupTick)
            return;
        _nextDecayCatchupTick = now + 5000;

        // Sector sleep skips far-away sectors. This catch-up sweep ensures
        // decaying ground items/corpses still expire on time even when
        // nobody is nearby.
        var expired = _world.GetAllObjects()
            .OfType<Item>()
            .Where(it => !it.IsDeleted && it.IsOnGround && it.DecayTime > 0 && it.DecayTime <= now)
            .Take(256)
            .ToList();

        foreach (var item in expired)
        {
            // Drive the normal item decay path first (corpse spill, spawn cleanup).
            _ = item.OnTick();
            if (!item.IsDeleted)
                continue;

            var removePacket = new PacketDeleteObject(item.Uid.Value);
            BroadcastNearby(item.Position, 18, removePacket, 0);
            _world.DeleteObject(item);
            item.Delete();
        }
    }

    private static void RunPostTickMaintenance()
    {
        long now = Environment.TickCount64;
        CleanupSummonedGuards(now);
        RunDecayCatchup(now);

        byte newLight = _world.GlobalLight;
        if (newLight != _lastGlobalLight)
        {
            _lastGlobalLight = newLight;
            var lightPacket = new PacketGlobalLight(newLight);
            foreach (var client in _clients.Values)
            {
                if (client.IsPlaying)
                    client.Send(lightPacket);
            }
        }

        // Weather & season update
        bool seasonChanged = _weatherEngine.OnTick();
        if (seasonChanged)
            BroadcastSeasonChange(playSound: true);

        // Ship movement ticks
        _shipEngine?.OnTickAll();

        // House decay (check every ~120 ticks = ~6s at 50ms tick)
        if (_world.TickCount % 120 == 0 && _housingEngine != null)
        {
            var collapsed = _housingEngine.OnTickDecay();
            foreach (var house in collapsed)
                _log.LogInformation("House 0x{Uid:X} collapsed from decay", house.MultiItem.Uid.Value);
        }

        ProcessIdleTimeout();
        _telnet?.Tick();
        _webStatus?.Tick();
    }

    private static void ProcessIdleTimeout()
    {
        long idleThresholdMs = _config.NetTTL * 1000L;
        if (idleThresholdMs <= 0)
            return;

        long tickNow = Environment.TickCount64;
        foreach (var state in _network.GetActiveStates())
        {
            if (state.LastActivityTick > 0 &&
                tickNow - state.LastActivityTick > idleThresholdMs)
            {
                _log.LogInformation("Idle timeout for connection #{Id} ({Account})",
                    state.Id, state.AccountName);
                state.MarkClosing();
            }
        }
    }

    private static void MaybeRunDeterminismGuardrail()
    {
        if (!_config.MulticoreDeterminismDebug || _tickCounter > 2000)
            return;

        string hash = ComputeDeterminismHash();
        if (_tickCounter == 2000)
        {
            _log.LogInformation("[determinism] hash at tick {Tick}: {Hash}", _tickCounter, hash);

            if (!string.IsNullOrWhiteSpace(_config.MulticoreDeterminismExpectedHash) &&
                !string.Equals(hash, _config.MulticoreDeterminismExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogError(
                    "[determinism] hash mismatch! expected={Expected} actual={Actual}",
                    _config.MulticoreDeterminismExpectedHash,
                    hash);
            }
        }
    }

    private static string ComputeDeterminismHash()
    {
        var sb = new StringBuilder();
        sb.Append("tick:").Append(_tickCounter).Append('\n');
        sb.Append("world:").Append(_world.ComputeStateHash()).Append('\n');
        foreach (var client in _clients.Values.OrderBy(c => c.NetState.Id))
        {
            sb.Append(client.NetState.Id).Append(':');
            if (client.Character != null)
                sb.Append(client.Character.Uid.Value).Append('@').Append(client.Character.X).Append(',').Append(client.Character.Y).Append(',').Append(client.Character.Z);
            sb.Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private static long ToMicroseconds(long stopwatchTicks)
    {
        return (long)(stopwatchTicks * (1_000_000.0 / Stopwatch.Frequency));
    }

    /// <summary>
    /// Parse the [SPHERE] LogFileLevel knob into either a single-value
    /// minimum threshold OR a discrete whitelist set.
    ///   "Warning"                            → minLevel=Warning, whitelist=null
    ///   "Verbose | Warning | Error | Fatal"  → minLevel=Verbose,
    ///                                          whitelist={Verbose, Warning,
    ///                                                     Error, Fatal}
    /// Unknown / empty input falls back to Warning-threshold.  When a
    /// whitelist is returned the caller must wire a Serilog
    /// <c>Filter.ByIncludingOnly</c> against it; the min-level is set to
    /// the lowest whitelisted entry so the sink doesn't pre-drop those
    /// events upstream.
    /// </summary>
}
