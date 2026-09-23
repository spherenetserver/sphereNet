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
    /// <summary>The world generation the account file belongs to: the token of the
    /// last world save that committed, or - before this run has saved anything - the
    /// token of the world that loaded. Stamped into the account file so a later boot
    /// can tell a world that was rolled back to an older generation from one the
    /// accounts on disk actually match (review work item D01).</summary>
    private static long _worldGeneration;

    /// <summary>An account snapshot rendered and waiting for the world write it
    /// belongs to, and the error from staging it, if staging is what failed.</summary>
    private static SphereNet.Persistence.Accounts.AccountPersistence.StagedAccountSnapshot? _stagedAccounts;
    private static string? _stagedAccountsError;

    /// <summary>The generation the account file names, read at boot. Null when the
    /// file carries no stamp (a classic account file, or one written by an older
    /// build).</summary>
    private static long? _loadedAccountGeneration;

    private static string AccountDirPath()
        => ResolvePath(AppDomain.CurrentDomain.BaseDirectory, _config.AccountDir);

    /// <summary>Do the world on disk and the account file on disk come from the same
    /// save? They must, because the world holds the characters the account slots name.
    ///
    /// They can disagree in one direction that matters. Recovery loads the newest
    /// generation it can read, so a world whose current files are unreadable comes
    /// back from a .bakN while the account file - a single file, published separately
    /// and never rotated - stays at the newest generation. Every character created
    /// since that backup then has a slot in an account and no character in the world.
    /// That is not repaired here: it is named, at boot, in the log, because the repair
    /// (which of the two to roll back) is the operator's call and the audit below
    /// lists exactly which accounts are affected.</summary>
    private static void VerifyAccountGenerationAgainstWorld()
    {
        long? worldGen = _loader.LoadedGeneration;
        _worldGeneration = worldGen ?? 0;

        if (worldGen == null || _loadedAccountGeneration == null)
        {
            // One of the two is unstamped: a classic Sphere save, a legacy account
            // file, or a directory written before the stamp existed. Nothing can be
            // concluded, and refusing to boot over it would lock out every shard
            // upgrading from an older build.
            return;
        }

        if (_loadedAccountGeneration == worldGen) return;

        _log.LogError(
            "The account file was written for save generation {AccountGen} but the world that loaded is " +
            "generation {WorldGen}. The two are from different saves - typically a world recovered from a " +
            "backup while the account file stayed at the newest save. Character slots may name characters " +
            "this world does not hold; see the world audit below. The next save republishes both as one " +
            "generation, which makes the mismatch permanent, so resolve it before saving.",
            _loadedAccountGeneration, worldGen);
    }

    /// <summary>Account file write outside a world save - a password, a ban, a new
    /// account. Carries the generation of the world currently on disk, because that
    /// is the world these accounts belong to.</summary>
    private static void SaveAccountsToDisk()
    {
        string? error = TrySaveAccounts(_worldGeneration);
        if (error != null)
            _log.LogError("Account save failed: {Message}", error);
    }

    /// <summary>Write and publish the account file in one step. Returns the failure
    /// message, or null when it landed. The message is RETURNED rather than only
    /// logged: a save that lost the account file must not be reported as complete,
    /// and the only way the caller can know is if the failure reaches it (D01).</summary>
    private static string? TrySaveAccounts(long generation)
    {
        try
        {
            SphereNet.Persistence.Accounts.AccountPersistence.Save(
                _accounts, AccountDirPath(), _saver.Format,
                _loggerFactory.CreateLogger("AccountPersistence"), generation);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Account save failed");
            return ex.Message;
        }
    }

    /// <summary>Render the account file next to a world snapshot, but do not publish
    /// it: <see cref="PublishStagedAccounts"/> does that once the world write has
    /// committed. Reads live account state, so main-thread only.</summary>
    private static void StageAccounts(long generation)
    {
        _stagedAccounts = null;
        _stagedAccountsError = null;
        try
        {
            _stagedAccounts = SphereNet.Persistence.Accounts.AccountPersistence.Stage(
                _accounts, AccountDirPath(), _saver.Format,
                _loggerFactory.CreateLogger("AccountPersistence"), generation);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Account snapshot could not be written");
            _stagedAccountsError = ex.Message;
        }
    }

    /// <summary>Publish the staged account file. Returns the failure message, or null
    /// when there was nothing staged or it landed.</summary>
    private static string? PublishStagedAccounts()
    {
        var staged = _stagedAccounts;
        _stagedAccounts = null;
        string? error = _stagedAccountsError;
        _stagedAccountsError = null;
        if (staged == null) return error;

        try
        {
            SphereNet.Persistence.Accounts.AccountPersistence.Commit(
                staged, _loggerFactory.CreateLogger("AccountPersistence"));
            return error;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Account snapshot could not be published");
            return ex.Message;
        }
    }

    /// <summary>Throw away a staged account file whose world write failed. Publishing
    /// it would leave accounts from a save that does not exist beside the world that
    /// survived.</summary>
    private static void DropStagedAccounts()
    {
        var staged = _stagedAccounts;
        _stagedAccounts = null;
        _stagedAccountsError = null;
        if (staged == null) return;
        SphereNet.Persistence.Accounts.AccountPersistence.Discard(
            staged, _loggerFactory.CreateLogger("AccountPersistence"));
        _log.LogWarning(
            "The account snapshot for the failed save was discarded; the account file on disk still " +
            "matches the world on disk");
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
            sw = BeginWorldSave(_world, _config.ForceGarbageCollect, _log);
            _housingEngine?.SerializeAllToTags();
            _shipEngine?.SerializeAllToTags();
            _guildManager?.SerializeAllToTags(_world);
            _spellEngine.RevertAllForSave();
            double prepSecs = sw.Elapsed.TotalSeconds;
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
                // The accounts are rendered HERE, next to the world walk, so the file
                // describes the same instant the snapshot does - but it is not
                // published until the world write commits. It used to be written
                // outright at this point, so a world write that then failed left the
                // new accounts beside the previous world: a character created in the
                // lost save keeps its slot, and the world has never heard of it
                // (review work item D01).
                StageAccounts(prepared.Generation);
                _backgroundSaveGeneration = prepared.Generation;
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

            // Synchronous mode is a first-class choice — keep it as fast as it
            // can be: parallel shard writes stay ON here (they shorten the one
            // stall the operator opted into), and a prior background save must
            // not leave its sequential-writes flag sticky on this path.
            _saver.SequentialShardWrites = false;
            double worldSecs;
            bool worldOk;
            try
            {
                long t0 = sw.ElapsedMilliseconds;
                worldOk = _saver.Save(_world, sp);
                worldSecs = (sw.ElapsedMilliseconds - t0) / 1000.0;
            }
            finally
            {
                _spellEngine.ReapplyAllAfterSave();
            }
            double preAccounts = sw.Elapsed.TotalSeconds;
            // Only after the world has committed, and stamped with the generation it
            // committed as: an account file written beside a world write that failed
            // belongs to a save that does not exist.
            string? accountError = null;
            if (worldOk)
            {
                _worldGeneration = _saver.LastGeneration;
                accountError = TrySaveAccounts(_worldGeneration);
            }
            // Phase breakdown so a slow sync save names its own cost
            // (prep = housing/ship/guild tag serialize + spell revert;
            // tail = spell reapply + account file + GC pressure).
            _log.LogInformation(
                "Save phases: prep={Prep:F2}s world={World:F2}s tail={Tail:F2}s accounts={Acct:F2}s",
                prepSecs, worldSecs, preAccounts - prepSecs - worldSecs,
                sw.Elapsed.TotalSeconds - preAccounts);
            // Broadcast success only when the world write actually committed. A
            // failed write (record encode/IO error) left the previous save intact;
            // report the failure instead of a false "save complete".
            if (!worldOk)
                FinishSaveFailure(sw, "world save failed (record write/commit error); previous save retained — see log");
            else if (accountError != null)
                FinishSavePartial(sw, accountError);
            else
                FinishSaveSuccess(sw);
        }
        catch (Exception ex)
        {
            FinishSaveFailure(sw, ex.Message);
        }
    }

    // Source-X CWorld::SaveTry runs world garbage collection before starting
    // _iSaveTimer. Keep this on the main thread, before snapshot/temporary spell
    // changes, and only after the in-flight-save guard. Never force .NET GC here.
    private static Stopwatch BeginWorldSave(GameWorld world, bool forceGarbageCollect, Microsoft.Extensions.Logging.ILogger log)
    {
        if (forceGarbageCollect)
        {
            var cleanup = Stopwatch.StartNew();
            var result = world.GarbageCollection(message => log.LogWarning("{Reason}", message));
            cleanup.Stop();
            log.LogInformation(
                "Pre-save world cleanup: checked={Checked} fixed={Fixed} deleted={Deleted} duration={Ms:F1}ms (excluded from save timer)",
                result.Checked, result.Fixed, result.Deleted, cleanup.Elapsed.TotalMilliseconds);
        }
        // loop_stall still measures the entire main-loop job, including cleanup.
        return Stopwatch.StartNew();
    }

    private static Task<bool>? _backgroundSaveTask;
    private static System.Diagnostics.Stopwatch? _backgroundSaveStopwatch;
    private static long _backgroundSaveGeneration;

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
        {
            // The world is on disk; now the accounts staged beside it may be
            // published, and the save is only complete if they are.
            _worldGeneration = _backgroundSaveGeneration;
            string? accountError = PublishStagedAccounts();
            if (accountError != null)
                FinishSavePartial(sw, accountError);
            else
                FinishSaveSuccess(sw);
        }
        else
        {
            DropStagedAccounts();
            FinishSaveFailure(sw, task.Exception?.GetBaseException().Message ?? "background write failed");
        }
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

    // What the panel shows as "last save": when it ended, how long it took and
    // whether it landed. Written on the main loop, read by the stats snapshot.
    private static DateTime? _lastSaveUtc;
    private static double _lastSaveSeconds;
    private static bool? _lastSaveOk;

    private static void RecordSaveOutcome(System.Diagnostics.Stopwatch sw, bool ok)
    {
        _lastSaveUtc = DateTime.UtcNow;
        _lastSaveSeconds = sw.Elapsed.TotalSeconds;
        _lastSaveOk = ok;
    }

    private static void FinishSaveSuccess(System.Diagnostics.Stopwatch sw)
    {
        const ushort SaveHue = 0x0040;
        _saveCount++;
        _systemHooks.DispatchServer("save_ok", _serverHookContext);
        sw.Stop();
        double secs = sw.Elapsed.TotalSeconds;
        // Load-profile counter (PLAN-703): save duration and frequency are two of
        // the numbers a soak run compares between snapshots. Both save modes end
        // here, so one call covers synchronous and background alike.
        SphereNet.Game.Diagnostics.LoadProfile.CountSave(sw.ElapsedMilliseconds);
        RecordSaveOutcome(sw, ok: true);
        _log.LogInformation("Save complete. ({Secs:F2} sec)", secs);
        BroadcastToAllPlayers(
            ServerMessages.GetFormatted("worldsave_complete", _saveCount, $"{secs:F2}"),
            SaveHue);
        _systemHooks.DispatchServer("save_finished", _serverHookContext,
            sw.Elapsed.TotalSeconds.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The world generation committed and the account file did not. Counted
    /// as a save because the world really did land, but reported as a failure: a shard
    /// whose accounts silently stopped being written keeps announcing "Save complete"
    /// until a player loses a password change, a ban or a whole character (D01).</summary>
    private static void FinishSavePartial(System.Diagnostics.Stopwatch sw, string message)
    {
        const ushort SaveHue = 0x0040;
        _saveCount++;
        _systemHooks.DispatchServer("save_fail", _serverHookContext, message);
        sw.Stop();
        SphereNet.Game.Diagnostics.LoadProfile.CountSave(sw.ElapsedMilliseconds);
        RecordSaveOutcome(sw, ok: false);
        _log.LogError("World saved, but the account file did not: {Message}", message);
        BroadcastToAllPlayers(
            ServerMessages.GetFormatted("worldsave_partial", message),
            SaveHue);
        _systemHooks.DispatchServer("save_finished", _serverHookContext,
            sw.Elapsed.TotalSeconds.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void FinishSaveFailure(System.Diagnostics.Stopwatch sw, string message)
    {
        const ushort SaveHue = 0x0040;
        sw.Stop();
        RecordSaveOutcome(sw, ok: false);
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

        // Every world broadcast passes f_onserver_broadcast first (CWorldComm::
        // Broadcast, CWorldComm.cpp:228-234): ARGS is the message, RETURN 1 keeps it
        // from being sent, and whatever the script left in ARGS is what goes out.
        // The pack defines the function; nothing ever called it.
        if (_systemHooks != null)
        {
            if (_systemHooks.DispatchServerRewrite("broadcast", _serverHookContext, ref text)
                == SphereNet.Core.Enums.TriggerResult.True)
                return;
            if (string.IsNullOrEmpty(text))
                return;
        }

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
        // The setting is already changed; whether a save happens NOW is a separate
        // question. A background write still running makes PerformSave skip - and the
        // write in flight keeps the format it was prepared with, so the switch reaches
        // disk at the next save rather than this one. Saying "and saving now"
        // regardless told the operator the new format had landed when it had not.
        bool writing = _backgroundSaveTask is { IsCompleted: false };
        if (writing)
        {
            _log.LogInformation(
                "SAVEFORMAT: switching to {Format} (shards={Shards}); a background save is still writing in the " +
                "previous format, so the change takes effect at the next save",
                fmt, _saver.ShardCount);
        }
        else
        {
            _log.LogInformation("SAVEFORMAT: switching to {Format} (shards={Shards}) and saving now",
                fmt, _saver.ShardCount);
        }
        PerformSave();
    }
}
