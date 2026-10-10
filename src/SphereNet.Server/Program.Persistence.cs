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

    /// <summary>What the boot-time account load found: the generation the account file
    /// names (null for a classic file or one written by an older build) and any
    /// mismatch an earlier boot left unresolved.</summary>
    private static SphereNet.Persistence.Accounts.AccountPersistence.AccountLoadResult _loadedAccounts;

    /// <summary>What reconciling the accounts with the world generation that loaded
    /// did, or null when the world load never chose a generation.</summary>
    private static SphereNet.Persistence.Accounts.AccountPersistence.AccountReconcileResult? _accountReconcile;

    /// <summary>The account generation of a world/account mismatch that is still
    /// open. Every account save carries it in the file, so a later boot reports it
    /// again instead of the next save quietly stamping both sides alike.</summary>
    private static long? _unresolvedAccountGeneration;

    private static string AccountDirPath()
        => ResolvePath(AppDomain.CurrentDomain.BaseDirectory, _config.AccountDir);

    /// <summary>WorldLoader.GenerationSelected: the world is about to materialise
    /// <paramref name="worldGeneration"/>. When that is not the generation the account
    /// file names - a world recovered from a .bakN - take the character slots from the
    /// account snapshot of the same generation, which the account backup chain keeps
    /// (AccountPersistence.ReconcileWithWorldGeneration documents what is restored and
    /// what keeps its newest value).</summary>
    private static void ReconcileAccountsWithWorldGeneration(long? worldGeneration)
    {
        _accountReconcile = SphereNet.Persistence.Accounts.AccountPersistence.ReconcileWithWorldGeneration(
            _accounts, AccountDirPath(), _loadedAccounts, worldGeneration,
            _loggerFactory.CreateLogger("AccountPersistence"));
    }

    /// <summary>Do the world on disk and the account file on disk come from the same
    /// save? They must, because the world holds the characters the account slots name.
    ///
    /// They can disagree in one direction that matters. Recovery loads the newest
    /// generation it can read, so a world whose current files are unreadable comes
    /// back from a .bakN while the account file stays at the newest generation. The
    /// account file keeps a backup chain of its own, one entry per world generation,
    /// and the world load has already switched the character slots to the snapshot of
    /// its generation (<see cref="ReconcileAccountsWithWorldGeneration"/>). When no
    /// such snapshot exists the mismatch is left open: it is named here at every boot,
    /// carried in the account file by every save, and closed only once no account slot
    /// names a character this world does not hold.</summary>
    private static void VerifyAccountGenerationAgainstWorld()
    {
        long? worldGen = _loader.LoadedGeneration;
        _worldGeneration = worldGen ?? 0;
        _unresolvedAccountGeneration = _loadedAccounts.UnresolvedGeneration;

        var outcome = _accountReconcile?.Outcome
            ?? SphereNet.Persistence.Accounts.AccountPersistence.AccountReconcileOutcome.NotNeeded;
        if (outcome == SphereNet.Persistence.Accounts.AccountPersistence.AccountReconcileOutcome.NoSnapshot)
        {
            _unresolvedAccountGeneration = _loadedAccounts.Generation;
            _log.LogError(
                "The account file was written for save generation {AccountGen} but the world that loaded is " +
                "generation {WorldGen}, and no account snapshot of that generation exists to restore. " +
                "Character slots may name characters this world does not hold; see the world audit below. " +
                "The pre-recovery account file is kept as {Preserved}.",
                _loadedAccounts.Generation, worldGen,
                _accountReconcile?.PreservedPath ?? "(not copied)");
        }

        if (_unresolvedAccountGeneration == null) return;

        int dangling = CountAccountSlotsOutsideWorld();
        if (dangling == 0)
        {
            _log.LogInformation(
                "The world/account generation mismatch from account generation {AccountGen} is resolved: every " +
                "account slot names a character this world holds", _unresolvedAccountGeneration);
            _unresolvedAccountGeneration = null;
            return;
        }

        _log.LogError(
            "World/account generation mismatch from account generation {AccountGen} is UNRESOLVED: {Count} " +
            "account slot(s) name characters this world does not hold. Saves keep the mismatch recorded in the " +
            "account file until every such slot is cleared (for example CHARUIDn=0 in {ChangesFile}) or the " +
            "matching world generation is restored.",
            _unresolvedAccountGeneration, dangling,
            SphereNet.Persistence.Accounts.AccountPersistence.ChangesFileName);
    }

    private static int CountAccountSlotsOutsideWorld()
    {
        int dangling = 0;
        foreach (var acc in _accounts.GetAllAccounts())
        {
            for (int i = 0; i < 7; i++)
            {
                var uid = acc.GetCharSlot(i);
                if (uid.IsValid && _world.FindChar(uid) == null)
                    dangling++;
            }
        }
        return dangling;
    }

    /// <summary>Every world save names a mismatch that is still open, so it does not
    /// vanish from the log after the boot that found it.</summary>
    private static void WarnUnresolvedAccountGeneration()
    {
        if (_unresolvedAccountGeneration != null)
            _log.LogError(
                "World/account generation mismatch from account generation {AccountGen} is still unresolved; " +
                "this save keeps it recorded in the account file", _unresolvedAccountGeneration);
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
                _loggerFactory.CreateLogger("AccountPersistence"), generation,
                _saver.BackupLevels, _unresolvedAccountGeneration);
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
            WarnUnresolvedAccountGeneration();
            _stagedAccounts = SphereNet.Persistence.Accounts.AccountPersistence.Stage(
                _accounts, AccountDirPath(), _saver.Format,
                _loggerFactory.CreateLogger("AccountPersistence"), generation,
                _saver.BackupLevels, _unresolvedAccountGeneration);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Account snapshot could not be written");
            _stagedAccountsError = ex.Message;
        }
    }

    /// <summary>Publish the staged account file. Returns the failure message, or null
    /// when there was nothing staged or it landed. Runs on the main loop: an account
    /// save published since the snapshot was staged makes it older than the file on
    /// disk, and the commit then re-renders it from the live accounts under the same
    /// generation, so no account change saved in between is undone.</summary>
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
        _mainLoopActions.Enqueue(() => PerformSave());
    }

    private static void RequestSaveFormatChangeOnMainLoop(string fmtName, int shards)
    {
        _mainLoopActions.Enqueue(() => HandleSaveFormatChange(fmtName, shards));
    }

    /// <summary>Console/IPC/panel RESPAWN: Source-X's server RESPAWN verb, which
    /// brings every dead NPC that has a home back to it (CServer.cpp:2144 ->
    /// CWorld::RespawnDeadNPCs). Queued onto the main loop because it mutates
    /// world/sector state.</summary>
    private static void RequestRespawnOnMainLoop()
    {
        _mainLoopActions.Enqueue(RespawnDeadNpcsNow);
    }

    private static void RespawnDeadNpcsNow()
    {
        int n = _world.RespawnDeadNpcs();
        _log.LogInformation("[respawn] brought back {Count} dead NPCs", n);
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
                // Never a player vendor: its stock is the owner's goods
                // (NPC_Vendor_Restock returns for a pet, CCharNPCAct_Vendor.cpp:41).
                if (VendorEngine.HasRealStock(ch)) continue;
                _triggerDispatcher.FireVendorRestock(ch,
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
            },
            // Main-loop share of the last save (see RecordSaveMainThreadTelemetry).
            Save = new
            {
                Background = _saveTelemetryBackground,
                PrepMs = _saveTelemetryPrepMs,
                CaptureMs = _saveTelemetryCaptureMs,
                AccountStageMs = _saveTelemetryAccountStageMs,
                MainThreadMs = _saveTelemetryMainThreadMs,
                MaxMainThreadMs = _saveTelemetryMaxMainThreadMs,
            },
            NpcBudget = GetNpcBudgetMetrics(),
        };
    }

    private static void PerformSave(bool forceImmediate = false)
    {
        // Source-X CWorld::Save (CWorld.cpp:1179-1204): f_onserver_save on the server
        // object with ARGN1 = the save is forced (writable - read back as the forced
        // flag) and ARGN2 = the save stage, 0 as a save starts here; RETURN 1 vetoes it.
        var saveArgs = new SphereNet.Scripting.Execution.TriggerArgs
        {
            Number1 = forceImmediate ? 1 : 0,
            Number2 = 0,
        };
        if (_systemHooks.RunServerFunction("f_onserver_save", _serverHookContext, saveArgs, out long? saveRet) &&
            saveRet == 1)
        {
            _log.LogInformation("World save cancelled by f_onserver_save");
            return;
        }
        forceImmediate = saveArgs.Number1 != 0;

        // CWorld::SaveForce (CWorld.cpp:944): the one announcement a save makes is
        // DEFMSG_SERVER_WORLDSAVE, through CWorldComm::Broadcast. A pack that blanks
        // the message (server_worldsave "") announces its saves itself, and an empty
        // text sends nothing (BroadcastToAllPlayers). Upstream says nothing when the
        // save ends; only a failure is broadcast.
        const ushort SaveHue = 0x0040;
        BroadcastToAllPlayers(ServerMessages.Get(Msg.ServerWorldsave), SaveHue);

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
            // Spell effects need nothing here: each is its worn memory item and the
            // character record holds the state it is in, as the classic save does.
            double prepSecs = sw.Elapsed.TotalSeconds;
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string sp = ResolvePath(basePath, _config.WorldSaveDir);

            // A forced save (SaveTry(fForceImmediate)) is written at once, never in
            // the background.
            if (_config.SaveBackgroundMinutes > 0 && !forceImmediate)
            {
                // Background mode (sphere.ini SAVEBACKGROUND > 0): the world walk
                // (Prepare) stays on the main thread — the only phase that reads
                // live objects — and the expensive shard/encode/write phase moves
                // to a worker. Completion side effects run back on the main loop
                // via CompleteBackgroundSave (polled next to the auto-save timer).
                // Prepare and the account staging both run on the main loop and
                // the loop waits for them: they are timed separately so the stall a
                // background save still causes is visible in the log and in the
                // runtime metrics (Save.CaptureMs / AccountStageMs / MainThreadMs).
                long captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var prepared = _saver.Prepare(_world);
                long captureEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                // The accounts are rendered HERE, next to the world walk, so the file
                // describes the same instant the snapshot does - but it is not
                // published until the world write commits. It used to be written
                // outright at this point, so a world write that then failed left the
                // new accounts beside the previous world: a character created in the
                // lost save keeps its slot, and the world has never heard of it
                // (review work item D01).
                StageAccounts(prepared.Generation);
                double accountStageMs = System.Diagnostics.Stopwatch.GetElapsedTime(captureEnd).TotalMilliseconds;
                _backgroundSaveGeneration = prepared.Generation;
                // Dedicated BELOW-NORMAL thread, and shard writes stay sequential
                // on it (SequentialShardWrites): a pool Task at normal priority
                // fanning out parallel gzip starved small VDS boxes — the main
                // loop showed 100-400ms yield/net_in stalls for the whole write
                // window. Low priority lets the game loop win the CPU.
                _saver.SequentialShardWrites = true;
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                //
                // The thread lowers its OWN priority as its first act instead of being
                // created below normal: Thread.Start does not return until the new
                // thread is running, and a below-normal thread on a busy host (every
                // core taken by the loop, the workers and the clients) is the last
                // thing the scheduler runs. A real-socket load run caught Start
                // holding the main loop for ~360 ms that way - longer than the whole
                // capture - and every client's moves waited behind it.
                var writer = new Thread(() =>
                {
                    try { Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal; }
                    catch (Exception) { /* a priority change is best effort */ }
                    try { completion.SetResult(_saver.WritePrepared(prepared, sp)); }
                    catch (Exception ex) { completion.SetException(ex); }
                })
                {
                    IsBackground = true,
                    Name = "world-save-writer"
                };
                writer.Start();
                // Recorded after the writer is running, so the main-loop share includes
                // everything the loop waited for, the thread start among it.
                RecordSaveMainThreadTelemetry(
                    background: true,
                    prepMs: prepSecs * 1000.0,
                    captureMs: System.Diagnostics.Stopwatch.GetElapsedTime(captureStart, captureEnd).TotalMilliseconds,
                    accountStageMs: accountStageMs,
                    mainThreadMs: sw.Elapsed.TotalMilliseconds);
                _backgroundSaveTask = completion.Task;
                _backgroundSaveStopwatch = sw;
                _log.LogInformation(
                    "World snapshot captured in {Secs:F2}s (main loop: prep={PrepMs:F1}ms capture={CaptureMs:F1}ms accounts_stage={AcctMs:F1}ms); writing in background...",
                    sw.Elapsed.TotalSeconds, _saveTelemetryPrepMs, _saveTelemetryCaptureMs,
                    _saveTelemetryAccountStageMs);
                return; // completion handled by CompleteBackgroundSave
            }

            // Synchronous mode is a first-class choice — keep it as fast as it
            // can be: parallel shard writes stay ON here (they shorten the one
            // stall the operator opted into), and a prior background save must
            // not leave its sequential-writes flag sticky on this path.
            _saver.SequentialShardWrites = false;
            long t0 = sw.ElapsedMilliseconds;
            bool worldOk = _saver.Save(_world, sp);
            double worldSecs = (sw.ElapsedMilliseconds - t0) / 1000.0;
            double preAccounts = sw.Elapsed.TotalSeconds;
            // Only after the world has committed, and stamped with the generation it
            // committed as: an account file written beside a world write that failed
            // belongs to a save that does not exist.
            string? accountError = null;
            if (worldOk)
            {
                _worldGeneration = _saver.LastGeneration;
                WarnUnresolvedAccountGeneration();
                accountError = TrySaveAccounts(_worldGeneration);
            }
            // Phase breakdown so a slow sync save names its own cost
            // (prep = housing/ship/guild tag serialize + spell revert;
            // tail = spell reapply + account file + GC pressure).
            _log.LogInformation(
                "Save phases: prep={Prep:F2}s world={World:F2}s tail={Tail:F2}s accounts={Acct:F2}s",
                prepSecs, worldSecs, preAccounts - prepSecs - worldSecs,
                sw.Elapsed.TotalSeconds - preAccounts);
            // Synchronous mode: the whole save is main-loop time; capture is inside
            // the world phase and not separable here.
            RecordSaveMainThreadTelemetry(
                background: false,
                prepMs: prepSecs * 1000.0,
                captureMs: worldSecs * 1000.0,
                accountStageMs: (sw.Elapsed.TotalSeconds - preAccounts) * 1000.0,
                mainThreadMs: sw.Elapsed.TotalMilliseconds);
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
        var preSave = Stopwatch.StartNew();
        // Objects created and never placed are deleted, not saved (Source-X
        // CWorld::SaveStage stage -1 -> GarbageCollection_NewObjs). Written out they
        // came back at 0,0 on map 0 - a spawner a script could not place turned into
        // a live spawner at the map corner.
        world.CollectUnplacedNewItems();
        if (forceGarbageCollect)
        {
            var cleanup = Stopwatch.StartNew();
            var result = world.GarbageCollection(message => log.LogWarning("{Reason}", message));
            cleanup.Stop();
            log.LogInformation(
                "Pre-save world cleanup: checked={Checked} fixed={Fixed} deleted={Deleted} duration={Ms:F1}ms (excluded from save timer)",
                result.Checked, result.Fixed, result.Deleted, cleanup.Elapsed.TotalMilliseconds);
        }
        // loop_stall still measures the entire main-loop job, including cleanup; the
        // completion line adds it to the pause the world actually felt.
        _saveTelemetryPreSaveMs = preSave.Elapsed.TotalMilliseconds;
        return Stopwatch.StartNew();
    }

    // Main-loop save cost of the most recent save (telemetry only). In background
    // mode capture = WorldSaver.Prepare and accounts_stage = StageAccounts; in
    // synchronous mode capture is the whole world write and accounts_stage the
    // account write, both of which also run on the main loop.
    private static bool _saveTelemetryBackground;
    private static double _saveTelemetryPrepMs;
    private static double _saveTelemetryCaptureMs;
    private static double _saveTelemetryAccountStageMs;
    private static double _saveTelemetryMainThreadMs;
    /// <summary>Main-loop time before the save timer starts: unplaced-object collection
    /// and the pre-save world cleanup.</summary>
    private static double _saveTelemetryPreSaveMs;
    private static double _saveTelemetryMaxMainThreadMs;

    private static void RecordSaveMainThreadTelemetry(bool background, double prepMs,
        double captureMs, double accountStageMs, double mainThreadMs)
    {
        _saveTelemetryBackground = background;
        _saveTelemetryPrepMs = prepMs;
        _saveTelemetryCaptureMs = captureMs;
        _saveTelemetryAccountStageMs = accountStageMs;
        _saveTelemetryMainThreadMs = mainThreadMs;
        if (mainThreadMs > _saveTelemetryMaxMainThreadMs)
            _saveTelemetryMaxMainThreadMs = mainThreadMs;
        _perfWindow?.RecordSave(captureMs, mainThreadMs);
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
        // The world landed but its .bak1 did not: say so instead of "Save complete",
        // so a shard does not run on without a backup unnoticed.
        string? backupError = _saver.LastBackupError;
        if (backupError != null)
        {
            _log.LogError("World saved, but its backup could not be written: {Error}", backupError);
            BroadcastToAllPlayers(
                ServerMessages.GetFormatted("worldsave_backup_failed", backupError),
                SaveHue);
        }
        else
        {
            // What players feel is the time the main loop stood still - the pre-save
            // cleanup plus, in background mode, the snapshot; the whole save in
            // synchronous mode. The total is how long the file write took and freezes
            // nobody, so it comes second.
            double pausedSecs = (_saveTelemetryPreSaveMs + _saveTelemetryMainThreadMs) / 1000.0;
            if (_saveTelemetryBackground)
                _log.LogInformation(
                    "Save complete. Server paused {Paused:F2} sec (cleanup {Cleanup:F0} ms, snapshot {Snapshot:F0} ms); background write finished in {Secs:F2} sec.",
                    pausedSecs, _saveTelemetryPreSaveMs, _saveTelemetryMainThreadMs, secs);
            else
                _log.LogInformation(
                    "Save complete. Server paused {Paused:F2} sec (synchronous save, cleanup {Cleanup:F0} ms).",
                    pausedSecs, _saveTelemetryPreSaveMs);
        }
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
