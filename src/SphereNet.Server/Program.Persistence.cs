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
    private static void SaveAccountsToDisk()
    {
        try
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string accDir = ResolvePath(basePath, _config.AccountDir);
            SphereNet.Persistence.Accounts.AccountPersistence.Save(
                _accounts, accDir, _saver.Format,
                _loggerFactory.CreateLogger("AccountPersistence"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Account save failed");
        }
    }

    private static void RequestSaveOnMainLoop()
    {
        _mainLoopActions.Enqueue(PerformSave);
    }

    private static void RequestSaveFormatChangeOnMainLoop(string fmtName, int shards)
    {
        _mainLoopActions.Enqueue(() => HandleSaveFormatChange(fmtName, shards));
    }

    /// <summary>Console/IPC/panel RESPAWN: top every spawner in the world up to its
    /// max. Queued onto the main loop because it mutates world/sector state.</summary>
    private static void RequestRespawnOnMainLoop()
    {
        _mainLoopActions.Enqueue(() =>
        {
            int n = _world.RespawnAllSpawners();
            _log.LogInformation("[respawn] topped up {Count} spawners", n);
        });
    }

    /// <summary>Console/telnet RESPAWN FULL: kill every spawner child, then refill
    /// fresh — clears NPCs materialized broken by an older build.</summary>
    private static void RequestRespawnResetOnMainLoop()
    {
        _mainLoopActions.Enqueue(() =>
        {
            // Guard against double-queuing (the 47s synchronous freeze made
            // operators re-issue the command mid-run).
            if (_respawnResetQueue != null)
            {
                _log.LogInformation("[respawn_full] already in progress ({Done}/{Total} spawners)",
                    _respawnResetIndex, _respawnResetQueue.Count);
                return;
            }

            var queue = new List<SphereNet.Game.Objects.Items.Item>();
            foreach (var obj in _world.GetAllObjects())
            {
                if (obj is SphereNet.Game.Objects.Items.Item item && !item.IsDeleted &&
                    (item.SpawnChar != null || item.SpawnItem != null))
                    queue.Add(item);
            }
            _respawnResetQueue = queue;
            _respawnResetIndex = 0;
            _respawnLegitChildren = [];
            _log.LogInformation("[respawn_full] queued {Count} spawners; resetting ~25ms per tick", queue.Count);
        });
    }

    private static List<SphereNet.Game.Objects.Items.Item>? _respawnResetQueue;
    private static int _respawnResetIndex;
    private static HashSet<uint> _respawnLegitChildren = [];

    /// <summary>Advance the incremental RESPAWN FULL: reset spawners inside a
    /// ~25ms budget per tick; when the queue drains, run the orphan sweep and
    /// log the summary. Called from post-tick maintenance.</summary>
    private static void ProcessRespawnResetChunk()
    {
        var queue = _respawnResetQueue;
        if (queue == null) return;

        long start = Stopwatch.GetTimestamp();
        while (_respawnResetIndex < queue.Count)
        {
            var item = queue[_respawnResetIndex++];
            if (!item.IsDeleted)
                _world.ResetSpawner(item, _respawnLegitChildren);
            if (ToMicroseconds(Stopwatch.GetTimestamp() - start) > 25_000)
                break;
        }

        if (_respawnResetIndex >= queue.Count)
        {
            int orphans = _world.SweepOrphanedSpawnChildren(_respawnLegitChildren);
            _log.LogInformation(
                "[respawn_full] reset {Count} spawners, swept {Orphans} orphaned spawn children",
                queue.Count, orphans);
            _respawnResetQueue = null;
            _respawnLegitChildren = [];
        }
    }

    /// <summary>Console/IPC/panel RESTOCK: fire @NPCRestock on every vendor so each
    /// rebuilds its stock (Source-X global RESTOCK). Main-loop only.</summary>
    private static void RequestRestockOnMainLoop()
    {
        _mainLoopActions.Enqueue(() =>
        {
            if (_triggerDispatcher == null) return;
            int n = 0;
            foreach (var obj in _world.GetAllObjects())
            {
                if (obj is not SphereNet.Game.Objects.Characters.Character ch) continue;
                if (ch.IsPlayer || ch.IsDeleted) continue;
                if (ch.NpcBrain != SphereNet.Core.Enums.NpcBrainType.Vendor) continue;
                _triggerDispatcher.FireCharTrigger(ch,
                    SphereNet.Core.Enums.CharTrigger.NPCRestock,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch });
                ch.RemoveTag("RESTOCK_TIME"); // let ActVendor restock again on its timer too
                n++;
            }
            _log.LogInformation("[restock] restocked {Count} vendors", n);
        });
    }

    private static object GetRuntimeMetrics()
    {
        var tick = GetTickTelemetrySnapshot();
        var mapStats = _world.GetMapStats();
        return new
        {
            tick.SampleCount,
            tick.AvgMs,
            tick.MaxMs,
            tick.P50Ms,
            tick.P95Ms,
            tick.P99Ms,
            tick.MaxSinceStartMs,
            tick.MulticoreEnabled,
            SlowTickCount = _slowTickCount,
            LastSlowTickDominantPhase = _lastSlowTickDominantPhase,
            SaveCount = _saveCount,
            Maps = mapStats,
            Telemetry = new
            {
                SnapshotMs = _telemetrySnapshotUs / 1000.0,
                WorldTickMs = _telemetryWorldTickUs / 1000.0,
                PostApplyMs = _telemetryPostApplyUs / 1000.0,
                ComputeMs = _telemetryComputeUs / 1000.0,
                ApplyMs = _telemetryApplyUs / 1000.0,
                FlushMs = _telemetryFlushUs / 1000.0,
                NpcBuildMs = _telemetryNpcBuildUs / 1000.0,
                ClientStateMs = _telemetryClientStateUs / 1000.0,
                NpcApplyMs = _telemetryNpcApplyUs / 1000.0,
                ViewBuildMs = _telemetryViewBuildUs / 1000.0
            }
        };
    }

    private static void PerformSave()
    {
        // Source-X f_onserver_save can veto the save with RETURN 1.
        if (_systemHooks.DispatchServer("save", _serverHookContext))
        {
            _log.LogInformation("World save cancelled by f_onserver_save");
            return;
        }

        // Source-X DEFMSG_WORLDSAVE_S behaviour: tell every online player a
        // save is happening so they don't blame momentary lag on the server
        // crashing. We use the world-event hue (0x0040, light red) which
        // matches the colour OSI/Source-X uses for global system events.
        const ushort SaveHue = 0x0040;
        BroadcastToAllPlayers(ServerMessages.Get("worldsave_started"), SaveHue);

        // E2: only one save may be in flight. A periodic save landing while the
        // previous background write is still running is skipped (it re-fires on
        // the next SavePeriod); Source-X likewise never overlaps saves.
        if (_backgroundSaveTask is { IsCompleted: false })
        {
            _log.LogWarning("World save skipped: previous background save still writing");
            return;
        }

        _log.LogInformation("Saving world...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _housingEngine?.SerializeAllToTags();
            _shipEngine?.SerializeAllToTags();
            _guildManager?.SerializeAllToTags(_world);
            _spellEngine.RevertAllForSave();
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string sp = ResolvePath(basePath, _config.WorldSaveDir);

            if (_config.SaveBackgroundMinutes > 0)
            {
                // Background mode (sphere.ini SAVEBACKGROUND > 0): the world walk
                // (Prepare) stays on the main thread — the only phase that reads
                // live objects — and the expensive shard/encode/write phase moves
                // to a worker. Completion side effects run back on the main loop
                // via CompleteBackgroundSave (polled next to the auto-save timer).
                SphereNet.Persistence.Save.WorldSaver.PreparedWorldSave prepared;
                try
                {
                    prepared = _saver.Prepare(_world);
                }
                finally
                {
                    _spellEngine.ReapplyAllAfterSave();
                }
                SaveAccountsToDisk();
                // Dedicated BELOW-NORMAL thread, and shard writes stay sequential
                // on it (SequentialShardWrites): a pool Task at normal priority
                // fanning out parallel gzip starved small VDS boxes — the main
                // loop showed 100-400ms yield/net_in stalls for the whole write
                // window. Low priority lets the game loop win the CPU.
                _saver.SequentialShardWrites = true;
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var writer = new Thread(() =>
                {
                    try { completion.SetResult(_saver.WritePrepared(prepared, sp)); }
                    catch (Exception ex) { completion.SetException(ex); }
                })
                {
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                    Name = "world-save-writer"
                };
                writer.Start();
                _backgroundSaveTask = completion.Task;
                _backgroundSaveStopwatch = sw;
                _log.LogInformation("World snapshot captured in {Secs:F2}s; writing in background...",
                    sw.Elapsed.TotalSeconds);
                return; // completion handled by CompleteBackgroundSave
            }

            try
            {
                _saver.Save(_world, sp);
            }
            finally
            {
                _spellEngine.ReapplyAllAfterSave();
            }
            SaveAccountsToDisk();
            FinishSaveSuccess(sw);
        }
        catch (Exception ex)
        {
            FinishSaveFailure(sw, ex.Message);
        }
    }

    private static Task<bool>? _backgroundSaveTask;
    private static System.Diagnostics.Stopwatch? _backgroundSaveStopwatch;

    /// <summary>Main-loop poll: when the background write finishes, run the
    /// completion side effects (hooks, counters, broadcast) on the main thread.</summary>
    private static void CompleteBackgroundSave()
    {
        if (_backgroundSaveTask is not { IsCompleted: true } task)
            return;
        var sw = _backgroundSaveStopwatch ?? System.Diagnostics.Stopwatch.StartNew();
        _backgroundSaveTask = null;
        _backgroundSaveStopwatch = null;

        if (task is { IsCompletedSuccessfully: true, Result: true })
            FinishSaveSuccess(sw);
        else
            FinishSaveFailure(sw, task.Exception?.GetBaseException().Message ?? "background write failed");
    }

    /// <summary>Block until an in-flight background save lands (shutdown path —
    /// the final shutdown save must not race the periodic one).</summary>
    private static void WaitForBackgroundSave()
    {
        if (_backgroundSaveTask is { } task)
        {
            try { task.Wait(TimeSpan.FromMinutes(5)); } catch { /* surfaced below */ }
            CompleteBackgroundSave();
        }
    }

    private static void FinishSaveSuccess(System.Diagnostics.Stopwatch sw)
    {
        const ushort SaveHue = 0x0040;
        _saveCount++;
        _systemHooks.DispatchServer("save_ok", _serverHookContext);
        sw.Stop();
        double secs = sw.Elapsed.TotalSeconds;
        _log.LogInformation("Save complete. ({Secs:F2} sec)", secs);
        BroadcastToAllPlayers(
            ServerMessages.GetFormatted("worldsave_complete", _saveCount, $"{secs:F2}"),
            SaveHue);
        _systemHooks.DispatchServer("save_finished", _serverHookContext,
            sw.Elapsed.TotalSeconds.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void FinishSaveFailure(System.Diagnostics.Stopwatch sw, string message)
    {
        const ushort SaveHue = 0x0040;
        sw.Stop();
        _systemHooks.DispatchServer("save_fail", _serverHookContext, message);
        _log.LogError("World save failed: {Message}", message);
        BroadcastToAllPlayers(
            ServerMessages.GetFormatted("worldsave_failed", message),
            SaveHue);
        _systemHooks.DispatchServer("save_finished", _serverHookContext,
            sw.Elapsed.TotalSeconds.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Send a sysmessage to every logged-in player. Used for global
    /// events (world save start/complete, shutdown countdown, etc.) where
    /// Source-X uses g_World.Broadcast() / addBarkParse(...,
    /// CCharBase::ALLCHARS, ...).</summary>
    private static void BroadcastToAllPlayers(string text, ushort hue)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var c in _clients.Values)
        {
            if (!c.IsPlaying)
                continue;
            try
            {
                c.SysMessage(text, hue);
            }
            catch
            {
                // Don't let a single dead socket abort the broadcast — a
                // disconnected client during save is normal at server tick
                // boundaries; the connection will be reaped shortly.
            }
        }
    }

    /// <summary>Handle a <c>.SAVEFORMAT</c> request: parse format name, update
    /// the saver, then immediately persist so the user can confirm the new
    /// files land on disk. Invalid format strings are rejected without any
    /// state change so a typo can't nuke the save path.</summary>
    private static void HandleSaveFormatChange(string fmtName, int shards)
    {
        if (!Enum.TryParse<SphereNet.Core.Configuration.SaveFormat>(fmtName, ignoreCase: true, out var fmt))
        {
            _log.LogWarning("SAVEFORMAT: unknown format '{Name}'. Valid: Text, TextGz, Binary, BinaryGz",
                fmtName);
            return;
        }
        _saver.Format = fmt;
        _config.SaveFormat = fmt;
        if (shards >= 1)
        {
            _saver.ShardCount = shards;
            _config.SaveShards = shards;
        }
        _log.LogInformation("SAVEFORMAT: switching to {Format} (shards={Shards}) and saving now",
            fmt, _saver.ShardCount);
        PerformSave();
    }
}
