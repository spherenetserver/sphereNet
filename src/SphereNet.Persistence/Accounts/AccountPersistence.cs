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
    /// its <c>.tmp</c> sibling and becomes the live snapshot only when
    /// <see cref="Commit"/> runs.
    ///
    /// Rendering and publishing are separate steps because the accounts and the world
    /// are one save. A background save wrote the account file while the world was
    /// still being encoded on the writer thread, so a world write that then failed
    /// published the NEW accounts beside the PREVIOUS world: a character created in
    /// the save that was lost keeps its slot in an account whose character the world
    /// has never heard of (review work item D01).</summary>
    public sealed class StagedAccountSnapshot
    {
        internal StagedAccountSnapshot(string dir, string tmpPath, string finalPath,
            SaveFormat fmt, int count, int skipped)
        {
            Dir = dir; TmpPath = tmpPath; FinalPath = finalPath;
            Format = fmt; Count = count; Skipped = skipped;
        }

        internal string Dir { get; }
        internal string TmpPath { get; }
        internal SaveFormat Format { get; }

        /// <summary>Where the snapshot lands when it is published.</summary>
        public string FinalPath { get; }
        /// <summary>Accounts written into the staged file.</summary>
        public int Count { get; }
        /// <summary>Accounts left out because their name cannot be written back.</summary>
        public int Skipped { get; }
    }

    /// <summary>What a load found: the accounts, and the world generation the file
    /// says it belongs to (null for a file written without a stamp).</summary>
    public readonly record struct AccountLoadResult(int Count, long? Generation, string? Path);

    /// <summary>Write every account into <c>sphereaccu.{ext}</c> and publish it.
    /// Stale files in the other formats are removed so changing SaveFormat doesn't
    /// leave two snapshots next to each other.</summary>
    /// <param name="generation">The world generation these accounts belong to, stamped
    /// into the file so a later boot can tell whether the world beside it is the same
    /// save. 0 leaves the file unstamped.</param>
    public static int Save(AccountManager accounts, string dir, SaveFormat fmt, ILogger? log = null,
        long generation = 0)
        => Commit(Stage(accounts, dir, fmt, log, generation), log);

    /// <summary>Phase 1: render the account file to its <c>.tmp</c> sibling. Reads live
    /// account state, so main-thread only; touches no live file.</summary>
    public static StagedAccountSnapshot Stage(AccountManager accounts, string dir, SaveFormat fmt,
        ILogger? log = null, long generation = 0)
    {
        Directory.CreateDirectory(dir);

        // Source-X Account_SaveAll "looks for changes FIRST" (CAccount.cpp:133): what
        // an administrator typed into sphereacct.scp is folded in before the write.
        ApplyChangesFile(accounts, dir, log);

        string ext = SaveIO.ExtensionFor(fmt);
        string finalPath = Path.Combine(dir, BaseName + ext);
        string tmpPath = finalPath + ".tmp";

        int count = 0, skipped = 0;
        using (var w = SaveIO.OpenWriter(tmpPath, fmt))
        {
            w.WriteHeaderComment("SphereNet Account File");
            w.WriteHeaderComment($"Saved at {DateTime.UtcNow:u}");

            // The same stamp every world shard carries. Without it the only way to
            // notice that the world had been rolled back to an older generation while
            // the accounts stayed ahead was a player reporting a character that no
            // longer exists.
            if (generation != 0)
            {
                w.BeginRecord(SaveIO.SaveIdSection);
                w.WriteProperty(SaveIO.GenerationProperty, generation.ToString());
                w.EndRecord();
            }

            foreach (var acc in accounts.GetAllAccounts())
            {
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

        return new StagedAccountSnapshot(dir, tmpPath, finalPath, fmt, count, skipped);
    }

    /// <summary>Phase 2: publish a staged snapshot - promote the <c>.tmp</c>, name it
    /// in the manifest and drop the files it supersedes. Safe on any thread.</summary>
    public static int Commit(StagedAccountSnapshot staged, ILogger? log = null)
    {
        string dir = staged.Dir, finalPath = staged.FinalPath;
        int count = staged.Count, skipped = staged.Skipped;
        SaveFormat fmt = staged.Format;

        // Atomic promote: .tmp → final (overwrite).
        File.Move(staged.TmpPath, finalPath, overwrite: true);

        // Name the active snapshot before removing anything. If the process dies
        // between these two steps the manifest still points at a file that exists,
        // and a stale file that survives deletion can no longer shadow the current
        // one on the next load.
        bool manifestOk = WriteManifest(dir, Path.GetFileName(finalPath), fmt, log);

        // Drop stale files in other formats so the directory shows only one
        // canonical account snapshot. Also cleans up any ancient .bak left
        // by pre-refactor code.
        string[] knownExts = { ".scp", ".scp.gz", ".sbin", ".sbin.gz" };
        var staleLeftBehind = new List<string>();
        foreach (string otherExt in knownExts)
        {
            string candidate = Path.Combine(dir, BaseName + otherExt);
            if (!candidate.Equals(finalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                try { File.Delete(candidate); }
                catch (Exception ex)
                {
                    log?.LogWarning(ex, "Could not remove stale account file {File}", candidate);
                    staleLeftBehind.Add(candidate);
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
    /// write it belonged to failed. Leaves the live account file untouched.</summary>
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
        long? generation = null;
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
        return new AccountLoadResult(count, generation, path);
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
            w.WriteProperty("PLEVEL", acc.PrivLevel.ToString());
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

        foreach (var tag in acc.Tags.GetAll())
            w.WriteProperty("TAG." + tag.Key, tag.Value);

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
                    acc.PrivLevel = NormalizePrivLevel(pl);
                else if (Enum.TryParse<SphereNet.Core.Enums.PrivLevel>(val, true, out var plv))
                    acc.PrivLevel = plv;
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
                    acc.SetTag(upper[4..], val);
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
