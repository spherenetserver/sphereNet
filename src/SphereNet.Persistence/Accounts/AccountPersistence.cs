using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Formats;

namespace SphereNet.Persistence.Accounts;

/// <summary>
/// Account file save/load. Uses the same <see cref="ISaveWriter"/> /
/// <see cref="ISaveReader"/> abstraction as the world saver so format
/// selection (text / gzip / binary) applies uniformly to every persistent
/// file in the save directory. Written as <c>sphereaccu</c> + the format
/// extension; the loader auto-detects by probing all known extensions.
/// </summary>
public static class AccountPersistence
{
    private const string BaseName = "sphereaccu";

    /// <summary>An account file that has been written but not published: it sits in
    /// a staging file of its own and becomes the live snapshot only when
    /// <see cref="Commit"/> runs.
    ///
    /// Rendering and publishing are separate steps because the accounts and the world
    /// are one save. A background save wrote the account file while the world was
    /// still being encoded on the writer thread, so a world write that then failed
    /// published the NEW accounts beside the PREVIOUS world: a character created in
    /// the save that was lost keeps its slot in an account whose character the world
    /// has never heard of (review work item D01).
    ///
    /// Each staged snapshot is its own transaction. Its staging file name is unique,
    /// so an ordinary account save that runs while a world write is still in flight
    /// cannot consume it, and discarding one transaction cannot delete another's file.
    /// It also remembers its place in the order of stages: a snapshot rendered before
    /// another one was published is older than the file on disk, and publishing it
    /// as rendered would put back account state that was already replaced.</summary>
    public sealed class StagedAccountSnapshot
    {
        internal StagedAccountSnapshot(string dir, string tmpPath, string finalPath,
            SaveFormat fmt, int count, int skipped, AccountManager accounts, long generation,
            int backupLevels, long? unresolvedGeneration, long sequence, ChangesFileStamp changesStamp)
        {
            Dir = dir; TmpPath = tmpPath; FinalPath = finalPath;
            Format = fmt; Count = count; Skipped = skipped;
            Accounts = accounts; Generation = generation; BackupLevels = backupLevels;
            UnresolvedGeneration = unresolvedGeneration; Sequence = sequence; ChangesStamp = changesStamp;
        }

        internal string Dir { get; }
        internal string TmpPath { get; }
        internal SaveFormat Format { get; }
        internal AccountManager Accounts { get; }
        internal int BackupLevels { get; }
        internal long? UnresolvedGeneration { get; }
        internal long Sequence { get; }
        internal ChangesFileStamp ChangesStamp { get; }

        /// <summary>Where the snapshot lands when it is published.</summary>
        public string FinalPath { get; }
        /// <summary>The world generation stamped into the file (0 = unstamped).</summary>
        public long Generation { get; }
        /// <summary>Accounts written into the staged file.</summary>
        public int Count { get; }
        /// <summary>Accounts left out because their name cannot be written back.</summary>
        public int Skipped { get; }
    }

    /// <summary>What a load found: the accounts, and the world generation the file
    /// says it belongs to (null for a file written without a stamp).</summary>
    public readonly record struct AccountLoadResult(int Count, long? Generation, string? Path)
    {
        /// <summary>The account generation of a world/account mismatch that has not been
        /// resolved yet, carried forward by every save until it is (null when none).</summary>
        public long? UnresolvedGeneration { get; init; }
    }

    /// <summary>SAVEID property naming an unresolved world/account mismatch: the
    /// generation the account file had when a world from another generation loaded
    /// beside it and no account snapshot of the world's generation existed.</summary>
    public const string UnresolvedGenerationProperty = "UNRESOLVEDGEN";

    /// <summary>Order of stages across the process, and per directory the newest stage
    /// that has been published. A stage older than what was published is re-rendered
    /// before it is published.</summary>
    private static long s_stageSequence;
    private static readonly object s_commitGate = new();
    private static readonly Dictionary<string, long> s_lastPublished = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marker of the staging file name. Deliberately NOT <c>.tmp</c>: the
    /// world writer sweeps every <c>*.tmp</c> of its directory when a write fails, and
    /// the account directory may be the world directory.</summary>
    private const string StagingMarker = ".stage-";

    internal readonly record struct ChangesFileStamp(bool Exists, long Length, DateTime WrittenUtc);

    /// <summary>Write every account into <c>sphereaccu.{ext}</c> and publish it.
    /// Stale files in the other formats are removed so changing SaveFormat doesn't
    /// leave two snapshots next to each other.</summary>
    /// <param name="generation">The world generation these accounts belong to, stamped
    /// into the file so a later boot can tell whether the world beside it is the same
    /// save. 0 leaves the file unstamped.</param>
    /// <param name="backupLevels">How many previous generations of the account file to
    /// keep as <c>.bakN</c> (the world's BackupLevels). 0 keeps none.</param>
    /// <param name="unresolvedGeneration">An unresolved world/account mismatch to carry
    /// in the file (see <see cref="UnresolvedGenerationProperty"/>).</param>
    public static int Save(AccountManager accounts, string dir, SaveFormat fmt, ILogger? log = null,
        long generation = 0, int backupLevels = 0, long? unresolvedGeneration = null)
        => Commit(Stage(accounts, dir, fmt, log, generation, backupLevels, unresolvedGeneration), log);

    /// <summary>Phase 1: render the account file to a staging file of its own. Reads
    /// live account state, so main-thread only; touches no live file.</summary>
    public static StagedAccountSnapshot Stage(AccountManager accounts, string dir, SaveFormat fmt,
        ILogger? log = null, long generation = 0, int backupLevels = 0, long? unresolvedGeneration = null)
    {
        Directory.CreateDirectory(dir);

        // Source-X Account_SaveAll "looks for changes FIRST" (CAccount.cpp:133): what
        // an administrator typed into sphereacct.scp is folded in before the write.
        ApplyChangesFile(accounts, dir, log);
        var changesStamp = StampChangesFile(dir);

        string ext = SaveIO.ExtensionFor(fmt);
        string finalPath = Path.Combine(dir, BaseName + ext);
        long sequence = Interlocked.Increment(ref s_stageSequence);
        string tmpPath = $"{finalPath}{StagingMarker}{sequence}-{Guid.NewGuid():N}";

        int count = 0, skipped = 0;
        try
        {
            using var w = SaveIO.OpenWriter(tmpPath, fmt);
            w.WriteHeaderComment("SphereNet Account File");
            w.WriteHeaderComment($"Saved at {DateTime.UtcNow:u}");

            // The same stamp every world shard carries. Without it the only way to
            // notice that the world had been rolled back to an older generation while
            // the accounts stayed ahead was a player reporting a character that no
            // longer exists.
            if (generation != 0 || unresolvedGeneration != null)
            {
                w.BeginRecord(SaveIO.SaveIdSection);
                if (generation != 0)
                    w.WriteProperty(SaveIO.GenerationProperty, generation.ToString());
                if (unresolvedGeneration != null)
                    w.WriteProperty(UnresolvedGenerationProperty, unresolvedGeneration.Value.ToString());
                w.EndRecord();
            }

            foreach (var acc in accounts.GetAllAccounts())
            {
                // An engine-internal account (the load-test bots) lives for the
                // session only.
                if (acc.IsEngineInternal)
                    continue;
                // Last line of defence. CreateAccount rejects names that cannot
                // round-trip, but a file written before that gate existed could
                // still hold one. Skipping the single bad record keeps every other
                // account (and every password/ban change) reaching disk instead of
                // aborting the whole write on a section-name exception.
                if (!AccountNameValidator.IsWritable(acc.Name))
                {
                    log?.LogError(
                        "An account name cannot be written as a save section and was skipped; " +
                        "rename it to persist that account again.");
                    skipped++;
                    continue;
                }
                WriteAccount(w, acc);
                count++;
            }
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }

        return new StagedAccountSnapshot(dir, tmpPath, finalPath, fmt, count, skipped, accounts,
            generation, backupLevels, unresolvedGeneration, sequence, changesStamp);
    }

    /// <summary>Phase 2: publish a staged snapshot - promote its staging file, name it
    /// in the manifest, keep the generation it supersedes as a backup and drop the
    /// files it supersedes.
    ///
    /// Publishes are serialised. A snapshot that is no longer the newest account state
    /// on disk - another snapshot staged after it was published first, the changes file
    /// was edited, or its staging file is gone - is re-rendered from the account state
    /// it was staged from, under the same generation, before it is published: the
    /// generation it was staged for is still the world it belongs to, and the account
    /// changes saved in between are newer than it. Re-rendering reads live account
    /// state, so call this on the thread that owns the accounts whenever ordinary
    /// account saves can interleave with a staged one.</summary>
    public static int Commit(StagedAccountSnapshot staged, ILogger? log = null)
    {
        lock (s_commitGate)
        {
            string key = Path.GetFullPath(staged.Dir);
            if (IsSuperseded(staged, key, out string why))
            {
                log?.LogInformation(
                    "Staged account snapshot for generation {Generation} is older than the account state on disk ({Why}); " +
                    "re-rendering it from the current accounts before publishing", staged.Generation, why);
                Discard(staged, log);
                staged = Stage(staged.Accounts, staged.Dir, staged.Format, log, staged.Generation,
                    staged.BackupLevels, staged.UnresolvedGeneration);
            }

            int published = Publish(staged, log);
            s_lastPublished[key] = staged.Sequence;
            return published;
        }
    }

    private static bool IsSuperseded(StagedAccountSnapshot staged, string key, out string why)
    {
        if (s_lastPublished.TryGetValue(key, out long last) && last > staged.Sequence)
        {
            why = "a later account save was published first";
            return true;
        }
        if (!File.Exists(staged.TmpPath))
        {
            why = "its staging file is gone";
            return true;
        }
        if (StampChangesFile(staged.Dir) != staged.ChangesStamp)
        {
            why = $"{ChangesFileName} changed since it was staged";
            return true;
        }
        why = string.Empty;
        return false;
    }

    private static int Publish(StagedAccountSnapshot staged, ILogger? log)
    {
        string dir = staged.Dir, finalPath = staged.FinalPath;
        int count = staged.Count, skipped = staged.Skipped;
        SaveFormat fmt = staged.Format;
        int levels = Math.Clamp(staged.BackupLevels, 0, 32);

        // Source-X writes sphereaccu through the same backup rotation as the world
        // (CAccounts::Account_SaveAll -> CWorld::OpenScriptBackup), so a world that
        // recovers from a backup has the account file of that save beside it. Here
        // the chain advances once per world generation: ordinary account saves inside
        // one generation overwrite the live file, and the first save of a new
        // generation moves the previous one to .bak1. A backup therefore always holds
        // the last account state of its generation, and it is found by its stamp.
        string? live = ActiveSnapshotPath(dir);
        long? liveGeneration = live != null ? ReadGenerationStamp(live) : null;
        long? newGeneration = staged.Generation != 0 ? staged.Generation : null;
        bool newGenerationStarts = live != null && liveGeneration != newGeneration;
        if (levels > 0 && newGenerationStarts &&
            live!.Equals(finalPath, StringComparison.OrdinalIgnoreCase))
        {
            RotateBackups(finalPath, levels, log);
        }

        // Atomic promote: staging file → final (overwrite).
        File.Move(staged.TmpPath, finalPath, overwrite: true);

        // Name the active snapshot before removing anything. If the process dies
        // between these two steps the manifest still points at a file that exists,
        // and a stale file that survives deletion can no longer shadow the current
        // one on the next load.
        bool manifestOk = WriteManifest(dir, Path.GetFileName(finalPath), fmt, log);

        // Drop stale files in other formats so the directory shows only one
        // canonical account snapshot. Also cleans up any ancient .bak left
        // by pre-refactor code. A superseded live file of another format (SaveFormat
        // changed) that holds the previous generation is kept in its own backup chain
        // instead of being deleted.
        string[] knownExts = { ".scp", ".scp.gz", ".sbin", ".sbin.gz" };
        var staleLeftBehind = new List<string>();
        foreach (string otherExt in knownExts)
        {
            string candidate = Path.Combine(dir, BaseName + otherExt);
            if (!candidate.Equals(finalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                bool retired = levels > 0 && newGenerationStarts &&
                    candidate.Equals(live, StringComparison.OrdinalIgnoreCase) &&
                    RetireToBackup(candidate, levels, log);
                if (!retired)
                {
                    try { File.Delete(candidate); }
                    catch (Exception ex)
                    {
                        log?.LogWarning(ex, "Could not remove stale account file {File}", candidate);
                        staleLeftBehind.Add(candidate);
                    }
                }
            }

            string stale = Path.Combine(dir, BaseName + otherExt + ".bak");
            if (File.Exists(stale))
            {
                try { File.Delete(stale); } catch { /* best effort */ }
            }
        }

        // Naming the active snapshot and removing the ones it supersedes are two
        // halves of one commit. If BOTH fail, the next load has no way to tell the
        // new file from the stale one and the extension probe can pick the old
        // snapshot - silently rolling back every account change. Report that rather
        // than returning a success count.
        if (!manifestOk && staleLeftBehind.Count > 0)
        {
            throw new IOException(
                $"Account snapshot committed to '{finalPath}', but the manifest could not be " +
                $"written and {staleLeftBehind.Count} superseded file(s) could not be removed. " +
                "The next load could select the stale snapshot.");
        }

        // ...and once they are in the main file, the changes file is emptied back to
        // its header (Account_LoadAll(true, true), CAccount.cpp:156).
        ClearChangesFile(dir, log);

        if (skipped > 0)
            log?.LogWarning("{Skipped} account(s) skipped on save: unwritable name", skipped);
        log?.LogInformation("Saved {Count} accounts to {Path}", count, finalPath);
        return count;
    }

    /// <summary>Shift <c>path.bak1..N</c> up one level and copy the live file into
    /// <c>.bak1</c>. The live file itself stays until the new one replaces it.</summary>
    private static void RotateBackups(string path, int levels, ILogger? log)
    {
        try
        {
            ShiftBackupChain(path, levels);
            File.Copy(path, path + ".bak1", overwrite: true);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not keep the previous account generation as a backup of {File}", path);
        }
    }

    /// <summary>Move a superseded live file of another format into its own chain.
    /// Returns false when it could not be preserved, so the caller removes it.</summary>
    private static bool RetireToBackup(string path, int levels, ILogger? log)
    {
        try
        {
            ShiftBackupChain(path, levels);
            File.Move(path, path + ".bak1", overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not keep the superseded account file {File} as a backup", path);
            return false;
        }
    }

    private static void ShiftBackupChain(string path, int levels)
    {
        string oldest = $"{path}.bak{levels}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = levels - 1; i >= 1; i--)
        {
            string src = $"{path}.bak{i}";
            if (File.Exists(src)) File.Move(src, $"{path}.bak{i + 1}", overwrite: true);
        }
    }

    /// <summary>The live account file: the manifest target, or the first known
    /// extension present. Null when there is none.</summary>
    private static string? ActiveSnapshotPath(string dir)
    {
        string? active = ReadManifestTarget(dir);
        if (active != null)
        {
            string path = Path.Combine(dir, active);
            if (File.Exists(path)) return path;
        }
        foreach (string ext in new[] { ".sbin.gz", ".sbin", ".scp.gz", ".scp" })
        {
            string path = Path.Combine(dir, BaseName + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The world generation an account file names, or null when it carries
    /// no stamp or cannot be read. Reads only the leading SAVEID record.</summary>
    public static long? ReadGenerationStamp(string path)
    {
        try
        {
            using var reader = SaveIO.OpenReader(path);
            if (!reader.NextRecord(out string section) ||
                !section.Equals(SaveIO.SaveIdSection, StringComparison.OrdinalIgnoreCase))
                return null;
            long? generation = null;
            while (reader.NextProperty(out string key, out string value))
            {
                if (key.Equals(SaveIO.GenerationProperty, StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(value, out long gen))
                    generation = gen;
            }
            return generation;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The account file - live or a <c>.bakN</c> of any format - stamped with
    /// <paramref name="generation"/>, newest first; null when none is.</summary>
    public static string? FindSnapshotForGeneration(string dir, long generation)
    {
        var candidates = new List<string>();
        string? live = ActiveSnapshotPath(dir);
        if (live != null) candidates.Add(live);
        string[] exts = { ".sbin.gz", ".sbin", ".scp.gz", ".scp" };
        foreach (string ext in exts)
        {
            string path = Path.Combine(dir, BaseName + ext);
            if (File.Exists(path) && !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                candidates.Add(path);
        }
        // Probe past a lowered BackupLevels, as the world loader does.
        for (int level = 1; level <= 32; level++)
        {
            foreach (string ext in exts)
            {
                string path = Path.Combine(dir, $"{BaseName}{ext}.bak{level}");
                if (File.Exists(path)) candidates.Add(path);
            }
        }
        foreach (string path in candidates)
        {
            if (ReadGenerationStamp(path) == generation)
                return path;
        }
        return null;
    }

    /// <summary>Remove staging files that no transaction will ever publish - left by a
    /// process that stopped between staging and publishing. Boot-time only.</summary>
    public static int RemoveAbandonedStagingFiles(string dir, ILogger? log = null)
    {
        int removed = 0;
        if (!Directory.Exists(dir)) return 0;
        foreach (string file in Directory.GetFiles(dir, BaseName + ".*" + StagingMarker + "*"))
        {
            try { File.Delete(file); removed++; }
            catch (Exception ex) { log?.LogWarning(ex, "Could not remove abandoned account staging file {File}", file); }
        }
        return removed;
    }

    private static ChangesFileStamp StampChangesFile(string dir)
    {
        var info = new FileInfo(Path.Combine(dir, ChangesFileName));
        return info.Exists
            ? new ChangesFileStamp(true, info.Length, info.LastWriteTimeUtc)
            : new ChangesFileStamp(false, 0, default);
    }

    // ---- world rollback ----------------------------------------------------

    /// <summary>What <see cref="ReconcileWithWorldGeneration"/> did.</summary>
    public enum AccountReconcileOutcome
    {
        /// <summary>The account file and the world are the same generation, or one of
        /// them is unstamped and nothing can be concluded.</summary>
        NotNeeded,
        /// <summary>The account snapshot of the world's generation was found and the
        /// world-linked account state was taken from it.</summary>
        Restored,
        /// <summary>No account snapshot of the world's generation exists. The accounts
        /// are left as loaded and the mismatch stays open.</summary>
        NoSnapshot,
    }

    public sealed record AccountReconcileResult(
        AccountReconcileOutcome Outcome,
        long? AccountGeneration,
        long? WorldGeneration,
        string? SnapshotPath,
        string? PreservedPath,
        int SlotsChanged,
        IReadOnlyList<string> AccountsCreatedSince,
        IReadOnlyList<string> AccountsDeletedSince,
        IReadOnlyList<string> AccountsWithNewerTags);

    /// <summary>Bring the loaded accounts back to the world generation that is about
    /// to be materialised, when the world comes from another generation than the
    /// account file (a world recovered from a backup). Call it after the account file
    /// is loaded and before the world links its characters into account slots.
    ///
    /// The policy splits the account by what it points at:
    /// <list type="bullet">
    /// <item>Character slots and LASTCHARUID name world objects, so they are taken from
    /// the account snapshot of the world's own generation. An account that did not
    /// exist in that generation keeps no slot: every character it made is in the lost
    /// world. The world then links its own characters on top, as on every boot.</item>
    /// <item>Everything else - password, privilege level, PRIV flags, ban, jail, connect
    /// records, TAGs - keeps its newest value. Those are account decisions, mostly an
    /// administrator's, and they cannot contradict the world; rolling a password or a
    /// ban back would undo a security change. Accounts created since are kept, accounts
    /// deleted since stay deleted. TAGs are kept but every account whose TAGs differ
    /// from the generation snapshot is named, because a script may have recorded
    /// something there that only the lost world explains.</item>
    /// </list>
    /// The pre-recovery account file is copied aside (never rotated or deleted) so the
    /// operator can still choose the other side. Without a snapshot of the world's
    /// generation nothing is changed and the outcome says so.</summary>
    public static AccountReconcileResult ReconcileWithWorldGeneration(AccountManager accounts, string dir,
        AccountLoadResult loaded, long? worldGeneration, ILogger? log = null)
    {
        var none = Array.Empty<string>();
        if (worldGeneration == null || loaded.Generation == null || loaded.Generation == worldGeneration)
            return new AccountReconcileResult(AccountReconcileOutcome.NotNeeded, loaded.Generation,
                worldGeneration, null, null, 0, none, none, none);

        string? preserved = PreserveSnapshot(loaded.Path, loaded.Generation.Value, log);
        string? snapshotPath = FindSnapshotForGeneration(dir, worldGeneration.Value);
        if (snapshotPath == null)
        {
            log?.LogError(
                "No account snapshot of world generation {WorldGen} exists (the account file is generation " +
                "{AccountGen}); the accounts are left as loaded. The pre-recovery account file is kept as {Preserved}.",
                worldGeneration, loaded.Generation, preserved ?? "(could not be copied)");
            return new AccountReconcileResult(AccountReconcileOutcome.NoSnapshot, loaded.Generation,
                worldGeneration, null, preserved, 0, none, none, none);
        }

        var then = new AccountManager(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
        {
            Md5Passwords = accounts.Md5Passwords,
        };
        LoadFile(then, snapshotPath, null);

        int slotsChanged = 0;
        var created = new List<string>();
        var newerTags = new List<string>();
        foreach (var acc in accounts.GetAllAccounts())
        {
            var old = then.FindAccount(acc.Name);
            if (old == null) created.Add(acc.Name);
            for (int i = 0; i < 7; i++)
            {
                var slot = old?.GetCharSlot(i) ?? Serial.Invalid;
                if (acc.GetCharSlot(i) != slot)
                {
                    acc.SetCharSlot(i, slot);
                    slotsChanged++;
                }
            }
            acc.LastCharUid = old?.LastCharUid ?? Serial.Invalid;
            if (old != null && !SameTags(acc, old))
                newerTags.Add(acc.Name);
        }
        var deleted = then.GetAllAccounts()
            .Where(a => accounts.FindAccount(a.Name) == null)
            .Select(a => a.Name)
            .ToList();

        log?.LogWarning(
            "The world loaded generation {WorldGen} while the account file is generation {AccountGen}. Character " +
            "slots were restored from {Snapshot} ({Slots} slot change(s)); passwords, privileges, bans and other " +
            "account fields keep their newest values. Accounts created since: {Created}. Accounts deleted since: " +
            "{Deleted}. Accounts whose TAGs differ from that generation (kept as newest, review them): {Tags}. " +
            "The pre-recovery account file is kept as {Preserved}.",
            worldGeneration, loaded.Generation, snapshotPath, slotsChanged,
            created.Count == 0 ? "none" : string.Join(", ", created),
            deleted.Count == 0 ? "none" : string.Join(", ", deleted),
            newerTags.Count == 0 ? "none" : string.Join(", ", newerTags),
            preserved ?? "(could not be copied)");

        return new AccountReconcileResult(AccountReconcileOutcome.Restored, loaded.Generation, worldGeneration,
            snapshotPath, preserved, slotsChanged, created, deleted, newerTags);
    }

    private static bool SameTags(Account a, Account b)
    {
        var left = a.Tags.GetAll().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var right = b.Tags.GetAll().ToList();
        if (left.Count != right.Count) return false;
        foreach (var kv in right)
        {
            if (!left.TryGetValue(kv.Key, out string? v) || v != kv.Value) return false;
        }
        return true;
    }

    /// <summary>Copy the account file that loaded to
    /// <c>sphereaccu.preserved-{generation}{ext}</c>, outside every rotation, unless a
    /// copy of that generation is already there. Returns its path, or null.</summary>
    private static string? PreserveSnapshot(string? path, long generation, ILogger? log)
    {
        if (path == null || !File.Exists(path)) return null;
        string ext = SaveIO.ExtensionFor(SaveIO.FormatFromPath(path));
        string target = Path.Combine(Path.GetDirectoryName(path)!, $"{BaseName}.preserved-{generation}{ext}");
        try
        {
            if (!File.Exists(target)) File.Copy(path, target);
            return target;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not keep a copy of the account file {File}", path);
            return null;
        }
    }

    /// <summary>Source-X's hand-edit file (SPHERE_FILE "acct"). The main file is written
    /// by the server and rewritten on every save; changes go here and are merged.</summary>
    public const string ChangesFileName = "sphereacct.scp";

    private const string ChangesHeader =
        "// Accounts are periodically moved to the sphereaccu.scp file.\n" +
        "// All account changes should be made here.\n" +
        "// Use the /ACCOUNT UPDATE command to force accounts to update.\n";

    /// <summary>Source-X Account_LoadAll(fChanges=true) (CAccount.cpp:69/26): each section
    /// of sphereacct.scp updates the account of that name, or creates it. It was never
    /// read, so an account added or edited by hand in that file did not exist.</summary>
    public static int ApplyChangesFile(AccountManager accounts, string dir, ILogger? log = null)
    {
        string path = Path.Combine(dir, ChangesFileName);
        if (!File.Exists(path))
            return 0;
        int count = 0;
        try
        {
            using var reader = SaveIO.OpenReader(path);
            while (reader.NextRecord(out string section))
            {
                string? name = ExtractAccountName(section);
                if (name == null || section.Equals(SaveIO.SaveIdSection, StringComparison.OrdinalIgnoreCase))
                {
                    while (reader.NextProperty(out _, out _)) { }
                    continue;
                }
                var account = accounts.FindAccount(name);
                bool isNew = account == null;
                account ??= new Account { Name = name, UseMd5Passwords = accounts.Md5Passwords };
                while (reader.NextProperty(out string key, out string value))
                    ApplyProperty(account, key, value);
                if (isNew)
                    accounts.AddLoaded(account);
                count++;
            }
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Could not read the account changes file {Path}", path);
            return count;
        }
        if (count > 0)
            log?.LogInformation("Applied {Count} account change(s) from {Path}", count, path);
        return count;
    }

    private static void ClearChangesFile(string dir, ILogger? log)
    {
        string path = Path.Combine(dir, ChangesFileName);
        try { File.WriteAllText(path, ChangesHeader); }
        catch (Exception ex) { log?.LogWarning(ex, "Could not reset the account changes file {Path}", path); }
    }

    /// <summary>Throw away a staged snapshot that will never be published - the world
    /// write it belonged to failed. Leaves the live account file untouched, and removes
    /// only this transaction's own staging file.</summary>
    public static void Discard(StagedAccountSnapshot staged, ILogger? log = null)
    {
        try
        {
            if (File.Exists(staged.TmpPath)) File.Delete(staged.TmpPath);
        }
        catch (Exception ex)
        {
            // A staged file nobody promotes is inert - the loader only ever reads the
            // manifest target or a known extension - so this is worth a line, not a
            // failure.
            log?.LogWarning(ex, "Could not remove the unpublished account file {Path}", staged.TmpPath);
        }
    }

    /// <summary>Load accounts from <paramref name="dir"/>. The manifest names the
    /// active snapshot; without one (first run, or a directory written by an older
    /// build) the known extensions are probed as before.</summary>
    public static int Load(AccountManager accounts, string dir, ILogger? log = null)
        => LoadSnapshot(accounts, dir, log).Count;

    /// <summary>Load accounts and report which world generation the file names, so
    /// the caller can check it against the world that actually loaded (D01).</summary>
    public static AccountLoadResult LoadSnapshot(AccountManager accounts, string dir, ILogger? log = null)
    {
        var result = LoadMainSnapshot(accounts, dir, log);
        // Source-X loads the changes file right after the main one (CAccount.cpp:122).
        ApplyChangesFile(accounts, dir, log);
        return result;
    }

    private static AccountLoadResult LoadMainSnapshot(AccountManager accounts, string dir, ILogger? log)
    {
        string? active = ReadManifestTarget(dir);
        if (active != null)
        {
            string path = Path.Combine(dir, active);
            if (File.Exists(path))
                return LoadFile(accounts, path, log);

            // A restored backup can leave the manifest pointing at a file that is
            // gone. Probing is still better than loading nothing.
            log?.LogWarning(
                "Account manifest names {File}, which is missing; falling back to a format probe", active);
        }

        // Priority order: prefer compressed binary (most-specific) when two
        // snapshots somehow co-exist, fall back to classic .scp last.
        string[] exts = { ".sbin.gz", ".sbin", ".scp.gz", ".scp" };
        foreach (string ext in exts)
        {
            string path = Path.Combine(dir, BaseName + ext);
            if (File.Exists(path))
                return LoadFile(accounts, path, log);
        }
        log?.LogWarning("No account file found in {Dir}", dir);
        return new AccountLoadResult(0, null, null);
    }

    /// <summary>Record which file is the live account snapshot. Written through a
    /// .tmp + rename so a torn write cannot leave an unreadable manifest. Returns
    /// false when the manifest could not be updated; the caller decides whether the
    /// extension probe alone is still enough to pick the right file.</summary>
    private static bool WriteManifest(string dir, string fileName, SaveFormat fmt, ILogger? log)
    {
        string path = ShardManifest.PathFor(dir, BaseName);
        string tmp = path + ".tmp";
        try
        {
            var manifest = new ShardManifest { Format = fmt, ShardCount = 1 };
            manifest.Files.Add(fileName);
            manifest.Save(tmp);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not write the account manifest {Path}", path);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }

            // A manifest left naming the PREVIOUS file is worse than none at all:
            // the loader would trust it over the snapshot just committed.
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>The file name the manifest declares active, or null when there is
    /// no usable manifest.</summary>
    private static string? ReadManifestTarget(string dir)
    {
        var manifest = ShardManifest.TryLoad(ShardManifest.PathFor(dir, BaseName));
        if (manifest == null || manifest.Files.Count == 0) return null;

        // The manifest is hand-editable by design, so treat it as untrusted: it may
        // only name an account file sitting directly in this directory.
        string name = manifest.Files[0];
        if (!name.StartsWith(BaseName, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(name).Equals(name, StringComparison.Ordinal))
            return null;

        return name;
    }

    private static AccountLoadResult LoadFile(AccountManager accounts, string path, ILogger? log)
    {
        int count = 0;
        long? generation = null, unresolved = null;
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            // The generation stamp, read before the account-name rules get a look at
            // the section: SAVEID is reserved, so it can never be mistaken for one.
            if (section.Equals(SaveIO.SaveIdSection, StringComparison.OrdinalIgnoreCase))
            {
                while (reader.NextProperty(out string stampKey, out string stampVal))
                {
                    if (stampKey.Equals(SaveIO.GenerationProperty, StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(stampVal, out long gen))
                        generation = gen;
                    else if (stampKey.Equals(UnresolvedGenerationProperty, StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(stampVal, out long ugen))
                        unresolved = ugen;
                }
                continue;
            }

            string? name = ExtractAccountName(section);
            if (name == null)
            {
                while (reader.NextProperty(out _, out _)) { /* skip */ }
                continue;
            }

            var account = new Account
            {
                Name = name,
                UseMd5Passwords = accounts.Md5Passwords,
            };
            while (reader.NextProperty(out string key, out string value))
                ApplyProperty(account, key, value);

            accounts.AddLoaded(account);
            count++;
        }

        log?.LogInformation("Loaded {Count} accounts from {Path}", count, path);
        return new AccountLoadResult(count, generation, path) { UnresolvedGeneration = unresolved };
    }

    private static string? ExtractAccountName(string section)
    {
        if (section.StartsWith("ACCOUNT ", StringComparison.OrdinalIgnoreCase))
            return section[8..].Trim();
        if (section.Equals("EOF", StringComparison.OrdinalIgnoreCase))
            return null;
        // Sphere/Source-X bare format: [username] — accept any non-keyword section.
        // The reserved set lives in AccountNameValidator so the writer's admission
        // check and this reader's skip list cannot drift apart.
        if (section.Length > 0 && !AccountNameValidator.IsReservedSection(section))
            return section;
        return null;
    }

    private static void WriteAccount(ISaveWriter w, Account acc)
    {
        w.BeginRecord(acc.Name);
        if (acc.PrivLevel > SphereNet.Core.Enums.PrivLevel.Player)
            w.WriteProperty("PLEVEL", acc.ExtendedPlevelName ?? acc.PrivLevel.ToString());
        if (acc.Priv != 0) w.WriteProperty("PRIV", $"0{acc.Priv:x}");
        if (acc.ResDisp != 0) w.WriteProperty("RESDISP", acc.ResDisp.ToString());
        w.WriteProperty("PASSWORD", acc.PasswordHash ?? string.Empty);
        if (acc.TotalConnectTime != 0) w.WriteProperty("TOTALCONNECTTIME", acc.TotalConnectTime.ToString());
        if (acc.LastConnectTime != 0) w.WriteProperty("LASTCONNECTTIME", acc.LastConnectTime.ToString());
        if (acc.LastCharUid.IsValid) w.WriteProperty("LASTCHARUID", $"0{acc.LastCharUid.Value:x}");

        for (int i = 0; i < 7; i++)
        {
            var charUid = acc.GetCharSlot(i);
            if (charUid.IsValid)
                w.WriteProperty($"CHARUID{i}", $"0{charUid.Value:x}");
        }

        if (acc.FirstConnectDate != default)
            w.WriteProperty("FIRSTCONNECTDATE", Account.FormatConnectDate(acc.FirstConnectDate));
        if (acc.LastLogin != default)
            w.WriteProperty("LASTCONNECTDATE", Account.FormatConnectDate(acc.LastLogin));
        if (!string.IsNullOrEmpty(acc.FirstIp)) w.WriteProperty("FIRSTIP", acc.FirstIp);
        if (!string.IsNullOrEmpty(acc.LastIp)) w.WriteProperty("LASTIP", acc.LastIp);
        if (!string.IsNullOrEmpty(acc.ChatName)) w.WriteProperty("CHATNAME", acc.ChatName);
        if (!string.IsNullOrEmpty(acc.Lang)) w.WriteProperty("LANG", acc.Lang);
        if (acc.MaxChars != 7) w.WriteProperty("MAXCHARS", acc.MaxChars.ToString());
        if (acc.IsBanned) w.WriteProperty("BANNED", "1");
        if (acc.Guest) w.WriteProperty("GUEST", "1");
        if (acc.Jail) w.WriteProperty("JAIL", "1");

        // CAccount::r_Write -> m_TagDefs.r_WritePrefix(s, "TAG") (CAccount.cpp:1613):
        // a number var bare, a string var quoted.
        foreach (var tag in acc.Tags.GetAll())
            w.WriteProperty("TAG." + tag.Key, acc.Tags.GetSaveText(tag.Key)!);

        w.EndRecord();
    }

    private static void ApplyProperty(Account acc, string key, string val)
    {
        string upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "PASSWORD":
                if (val.StartsWith("SHA256:", StringComparison.Ordinal))
                    acc.PasswordHash = val;
                else if (val.Length == 32 && val.All(c => "0123456789abcdefABCDEF".Contains(c)))
                    acc.PasswordHash = val;
                else if (!string.IsNullOrEmpty(val))
                    acc.SetPassword(val);
                break;
            case "PLEVEL":
                if (int.TryParse(val, out int pl))
                {
                    acc.PrivLevel = NormalizePrivLevel(pl);
                    if (pl > (int)Core.Enums.PrivLevel.Owner) acc.ExtendedPlevelName = val;
                }
                else if (Enum.TryParse<SphereNet.Core.Enums.PrivLevel>(val, true, out var plv))
                    acc.PrivLevel = plv;
                else if (val.Equals("Founder", StringComparison.OrdinalIgnoreCase)
                    || val.Equals("Root", StringComparison.OrdinalIgnoreCase))
                {
                    // Sphere 56T custom-version compatibility: levels 8/9 above Owner.
                    acc.PrivLevel = Core.Enums.PrivLevel.Owner;
                    acc.ExtendedPlevelName = val;
                }
                break;
            case "LASTCONNECTDATE":
                if (DateTime.TryParse(val, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var lcd))
                    acc.LastLogin = DateTime.SpecifyKind(lcd, DateTimeKind.Local).ToUniversalTime();
                break;
            case "LASTCONNECTTIME":
                if (uint.TryParse(val, out uint lct))
                    acc.LastConnectTime = lct;
                break;
            case "MAXHOUSES":
                acc.SetTag("MaxHouses", val);
                break;
            case "LASTIP": acc.LastIp = val; break;
            case "TOTALCONNECTTIME":
                if (uint.TryParse(val, out uint ct))
                    acc.TotalConnectTime = ct;
                break;
            case "BANNED":
                acc.IsBanned = val == "1";
                break;
            case "CHATNAME": acc.ChatName = val; break;
            case "FIRSTCONNECTDATE":
                if (DateTime.TryParse(val, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var fcd))
                    acc.FirstConnectDate = DateTime.SpecifyKind(fcd, DateTimeKind.Local).ToUniversalTime();
                break;
            case "FIRSTIP": acc.FirstIp = val; break;
            case "LASTCHARUID":
                if (TryParseHexOrDec(val, out uint lcuid))
                    acc.LastCharUid = new Serial(lcuid);
                break;
            case "MAXCHARS":
                if (int.TryParse(val, out int mc)) acc.MaxChars = mc;
                break;
            case "GUEST": acc.Guest = val == "1"; break;
            case "JAIL": acc.Jail = val == "1"; break;
            case "LANG": acc.Lang = val; break;
            case "PRIV":
                if (TryParseHexOrDec(val, out uint pv)) acc.Priv = pv;
                break;
            case "RESDISP":
                if (byte.TryParse(val, out byte rd)) acc.ResDisp = rd;
                break;
            default:
                if (upper.StartsWith("TAG.", StringComparison.Ordinal) && upper.Length > 4)
                {
                    // AC_TAG: GetArgStr strips the quote pair, and a quoted value is a
                    // string var. An unquoted simple number is a number var that keeps
                    // its text exactly as written, so it goes back out the same. The
                    // key keeps its written case.
                    string text = SphereNet.Scripting.Variables.VarMap.UnquoteSaveValue(val, out bool quoted);
                    if (quoted)
                    {
                        acc.Tags.SetStr(key[4..], true, text);
                    }
                    else
                    {
                        acc.Tags.Set(key[4..], text);
                        acc.Tags.ApplyLoadedForm(key[4..], false, text);
                    }
                }
                else if (upper == "CHARUID")
                {
                    // Sphere bare CHARUID=serial (no index) — append to next free slot
                    if (TryParseHexOrDec(val, out uint cs))
                    {
                        int free = acc.FindFreeSlot();
                        if (free >= 0) acc.SetCharSlot(free, new Serial(cs));
                    }
                }
                else if (upper.StartsWith("CHARUID", StringComparison.Ordinal) && upper.Length == 8
                    && int.TryParse(upper.AsSpan(7), out int slotIdx))
                {
                    if (TryParseHexOrDec(val, out uint charSerial))
                        acc.SetCharSlot(slotIdx, new Serial(charSerial));
                }
                break;
        }
    }

    private static Core.Enums.PrivLevel NormalizePrivLevel(int value)
    {
        if (value < (int)Core.Enums.PrivLevel.Guest) value = (int)Core.Enums.PrivLevel.Guest;
        if (value > (int)Core.Enums.PrivLevel.Owner) value = (int)Core.Enums.PrivLevel.Owner;
        return (Core.Enums.PrivLevel)value;
    }

    private static bool TryParseHexOrDec(string val, out uint result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (val.StartsWith('0') && val.Length > 1 && !val.Contains('.'))
            return uint.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);
        return uint.TryParse(val, out result);
    }
}
