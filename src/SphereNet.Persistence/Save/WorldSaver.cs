using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;

namespace SphereNet.Persistence.Save;

/// <summary>
/// World saver. Maps to CWorld::Save in Source-X.
/// Writes items to <c>sphereworld</c>, characters to <c>spherechars</c>,
/// server state to <c>spheredata</c>. File format (extension) and optional
/// sharding are driven by <see cref="SaveFormat"/> and <c>SaveShards</c>.
/// Classic single-file <c>.scp</c> output is the default and binary-identical
/// to the pre-refactor behaviour.
/// </summary>
public sealed class WorldSaver
{
    private readonly ILogger<WorldSaver> _logger;
    private int _saveIndex;

    /// <summary>Format new saves will be written in. Runtime-changeable via
    /// the migration command; loader auto-detects so mixing formats in a
    /// snapshot dir is safe.</summary>
    public SaveFormat Format { get; set; } = SaveFormat.Text;

    /// <summary>Sharding mode. 0=always single file, 1=rolling (size-based),
    /// 2-16=fixed parallel hash shards. Clamped to [0,16] on config load.</summary>
    public int ShardCount { get; set; } = 1;

    /// <summary>Rolling threshold (bytes). Only consulted when <see cref="ShardCount"/>==1.</summary>
    public long ShardSizeBytes { get; set; }

    /// <summary>Number of per-file backup generations to keep beside the live save.</summary>
    public int BackupLevels { get; set; }

    public Func<ResourceId, string?>? ResolveResourceName { get; set; }

    /// <summary>Resolves an item BaseId (graphic) to its script defname
    /// (e.g. 0x0E75 → "i_backpack"). Used for Source-X compatible section headers.</summary>
    public Func<ushort, string?>? ResolveItemDefName { get; set; }

    /// <summary>Resolves a character CHARDEFINDEX to its script defname
    /// (e.g. hash → "c_man"). Used for Source-X compatible section headers.</summary>
    public Func<int, string?>? ResolveCharDefName { get; set; }

    public WorldSaver(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<WorldSaver>();
    }

    public bool Save(GameWorld world, string savePath)
    {
        _saveIndex++;
        _logger.LogInformation("World save #{Index} starting (format={Format}, shards={Shards})...",
            _saveIndex, Format, ShardCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(savePath);

            var snapshot = CaptureSnapshot(world);

            int itemCount = SaveSharded(snapshot.Items, savePath, "sphereworld", isItems: true);
            int charCount = SaveSharded(snapshot.Characters, savePath, "spherechars", isItems: false);
            SaveServerData(savePath, world);

            _logger.LogInformation("World save #{Index} complete: {Items} items, {Chars} chars in {Elapsed}s",
                _saveIndex, itemCount, charCount, sw.Elapsed.TotalSeconds.ToString("F1"));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "World save #{Index} FAILED", _saveIndex);
            CleanupTmpFiles(savePath);
            return false;
        }
    }

    /// <summary>Write one logical save ("sphereworld" or "spherechars"). Three
    /// code paths, selected by <see cref="ShardCount"/>:
    /// <list type="bullet">
    /// <item><c>0</c> → classic single file, no rolling, no manifest.</item>
    /// <item><c>1</c> → Sphere-style rolling: one file until it fills, then
    /// spill to the next. Small worlds stay single-file + no manifest.</item>
    /// <item><c>2-16</c> → fixed UID-hash parallel shards, always manifest.</item>
    /// </list></summary>
    private WorldSaveSnapshot CaptureSnapshot(GameWorld world)
    {
        var allObjects = world.GetAllObjects().ToArray();
        var items = new List<SaveRecord>();
        var chars = new List<SaveRecord>();
        long now = Environment.TickCount64;

        // Vendor stock is virtual: the container equipped at LAYER 26/27
        // and every item inside it are rebuilt on demand from the SELL
        // template when a player opens the vendor (PopulateVendorStock).
        // Persisting them bloats every save with transient stock items
        // (~20 per vendor), so collect those container UIDs and skip both
        // the containers and their contents below.
        var vendorStock = new HashSet<uint>();
        foreach (var obj in allObjects)
        {
            if (obj is Item it && !it.IsDeleted &&
                (it.EquipLayer == Core.Enums.Layer.VendorStock ||
                 it.EquipLayer == Core.Enums.Layer.VendorExtra))
                vendorStock.Add(it.Uid.Value);
        }

        foreach (var obj in allObjects)
        {
            if (obj is Item item)
            {
                if (item.IsDeleted || item.IsAttr(Core.Enums.ObjAttributes.Static))
                    continue;
                if (vendorStock.Contains(item.Uid.Value) ||
                    (item.ContainedIn.IsValid && vendorStock.Contains(item.ContainedIn.Value)))
                    continue; // virtual vendor stock — never persisted
                items.Add(CaptureItem(item, now));
            }
            else if (obj is Character ch)
            {
                if (ch.IsDeleted)
                    continue;
                chars.Add(CaptureChar(ch, now));
            }
        }

        return new WorldSaveSnapshot(items, chars);
    }

    private int SaveSharded(IReadOnlyList<SaveRecord> records, string savePath, string baseName, bool isItems)
    {
        int shards = Math.Clamp(ShardCount, 0, 16);
        string ext = SaveIO.ExtensionFor(Format);
        long sizeLimit = Math.Max(0, ShardSizeBytes);

        int totalCount;
        List<string> outputFiles;

        if (shards == 0)
        {
            string fileName = baseName + ext;
            string tmp = Path.Combine(savePath, fileName + ".tmp");
            totalCount = WriteOneShard(records, tmp, isItems, shardIndex: 0, shardCount: 1);
            outputFiles = new List<string> { fileName };
        }
        else if (shards == 1)
        {
            outputFiles = new List<string>();
            totalCount = WriteRollingShards(records, savePath, baseName, ext, sizeLimit, isItems, outputFiles);

            // Small worlds that never hit the rolling threshold get promoted
            // to the classic {base}{ext} name so there's no lone .0 suffix
            // and no manifest — identical on-disk layout to SaveShards=0.
            if (outputFiles.Count == 1)
            {
                string oldName = outputFiles[0];
                string newName = baseName + ext;
                if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                {
                    string oldTmp = Path.Combine(savePath, oldName + ".tmp");
                    string newTmp = Path.Combine(savePath, newName + ".tmp");
                    if (File.Exists(newTmp)) File.Delete(newTmp);
                    File.Move(oldTmp, newTmp);
                    outputFiles[0] = newName;
                }
            }
        }
        else
        {
            outputFiles = new List<string>(shards);
            var counts = new int[shards];
            for (int i = 0; i < shards; i++)
                outputFiles.Add($"{baseName}.{i}{ext}");

            var shardBuckets = PartitionRecordsByShard(records, shards);
            var tasks = new Task[shards];
            for (int i = 0; i < shards; i++)
            {
                int shardIdx = i;
                string tmp = Path.Combine(savePath, outputFiles[shardIdx] + ".tmp");
                tasks[i] = Task.Run(() =>
                    counts[shardIdx] = WriteOneShard(shardBuckets[shardIdx], tmp, isItems, shardIdx, shards));
            }
            Task.WaitAll(tasks);

            totalCount = 0;
            foreach (int c in counts) totalCount += c;
        }

        // Atomic commit for every real output file.
        foreach (string name in outputFiles)
            CommitFile(Path.Combine(savePath, name));

        // Manifest: written only when >1 file actually exists — keeps small
        // worlds at a single {base}{ext} file, matching classic Sphere layout.
        string manifestPath = ShardManifest.PathFor(savePath, baseName);
        if (outputFiles.Count > 1)
        {
            var manifest = new ShardManifest
            {
                Format = Format,
                ShardCount = outputFiles.Count,
                Files = outputFiles,
            };
            manifest.Save(manifestPath + ".tmp");
            CommitFile(manifestPath);
        }
        else if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        RemoveStaleSiblings(savePath, baseName, outputFiles);
        return totalCount;
    }

    private static List<SaveRecord>[] PartitionRecordsByShard(IEnumerable<SaveRecord> records, int shards)
    {
        var buckets = new List<SaveRecord>[shards];
        for (int i = 0; i < shards; i++)
            buckets[i] = [];

        foreach (var record in records)
            buckets[ShardManifest.ShardIndexForUid(record.Uid, shards)].Add(record);

        return buckets;
    }

    /// <summary>Sphere-style rolling writer: open <c>{base}.0{ext}</c>, write
    /// records, and when the on-disk file crosses <paramref name="sizeLimit"/>
    /// close it at the next record boundary and open <c>{base}.1{ext}</c>.
    /// The actual file list is appended to <paramref name="outputFiles"/>.
    /// Size is polled on the raw FileStream (compressed bytes for gzip) which
    /// may lag by up to one gzip block — close enough for rolling.</summary>
    private int WriteRollingShards(IEnumerable<SaveRecord> records, string savePath, string baseName,
        string ext, long sizeLimit, bool isItems, List<string> outputFiles)
    {
        int count = 0;
        int errors = 0;
        int fileIdx = 0;
        ISaveWriter? writer = null;
        FileStream? raw = null;

        void OpenNext()
        {
            string name = $"{baseName}.{fileIdx}{ext}";
            outputFiles.Add(name);
            string tmp = Path.Combine(savePath, name + ".tmp");
            writer = SaveIO.OpenWriter(tmp, Format, out raw);
            writer.WriteHeaderComment($"SphereNet {(isItems ? "World Items" : "World Characters")} Save");
            writer.WriteHeaderComment($"Save #{_saveIndex} at {DateTime.UtcNow:u}");
            writer.WriteHeaderComment($"Rolling segment {fileIdx}");
        }

        OpenNext();

        foreach (var record in records)
        {
            try
            {
                WriteRecord(writer!, record);
                count++;
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 5)
                    _logger.LogWarning(ex, "Error saving entity during rolling write");
            }

            if (sizeLimit > 0 && writer != null && writer.WrittenBytes >= sizeLimit)
            {
                writer.Dispose();
                writer = null; raw = null;
                fileIdx++;
                OpenNext();
            }
        }

        writer?.Dispose();

        if (errors > 0)
            _logger.LogWarning("Rolling save {Kind}: {Errors} entities failed",
                isItems ? "items" : "chars", errors);
        return count;
    }

    private int WriteOneShard(IEnumerable<SaveRecord> records, string tmpPath, bool isItems,
        int shardIndex, int shardCount)
    {
        using var writer = SaveIO.OpenWriter(tmpPath, Format);
        writer.WriteHeaderComment($"SphereNet {(isItems ? "World Items" : "World Characters")} Save");
        writer.WriteHeaderComment($"Save #{_saveIndex} at {DateTime.UtcNow:u}");
        if (shardCount > 1)
            writer.WriteHeaderComment($"Shard {shardIndex}/{shardCount}");

        int count = 0;
        int errors = 0;

        foreach (var record in records)
        {
            if (shardCount > 1 && ShardManifest.ShardIndexForUid(record.Uid, shardCount) != shardIndex)
                continue;
            try
            {
                WriteRecord(writer, record);
                count++;
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 5)
                    _logger.LogWarning(ex, "Error saving {Kind} 0x{Serial:X8}",
                        isItems ? "item" : "char", record.Uid);
            }
        }

        if (errors > 0)
            _logger.LogWarning("Save{Kind} shard {Idx}: {Errors} failed",
                isItems ? "Items" : "Chars", shardIndex, errors);
        return count;
    }

    private SaveRecord CaptureItem(Item item, long now)
    {
        using var writer = new SnapshotSaveWriter();
        WriteItem(writer, item, now);
        return writer.ToRecord(item.Uid.Value);
    }

    private SaveRecord CaptureChar(Character ch, long now)
    {
        using var writer = new SnapshotSaveWriter();
        WriteChar(writer, ch, now);
        return writer.ToRecord(ch.Uid.Value);
    }

    private static void WriteRecord(ISaveWriter writer, SaveRecord record)
    {
        writer.BeginRecord(record.Section);
        foreach (var (key, value) in record.Properties)
            writer.WriteProperty(key, value);
        writer.EndRecord();
    }

    /// <summary>Persist an object's pending TIMERF/TIMERFMS entries as remaining-time
    /// (not the absolute due tick, which resets on restart) so a delayed function/verb
    /// survives save-load — Source-X keeps object timers across a world save.</summary>
    private static void WriteTimerF(ISaveWriter w, SphereNet.Game.Objects.ObjBase obj, long now)
    {
        foreach (var t in obj.TimerFEntries)
        {
            long remainingMs = t.DueTickMs - now;
            if (remainingMs < 0) remainingMs = 0;
            // remainingMs | functionName | args — functionName holds no '|'; args may,
            // so the loader splits on the first two delimiters only.
            w.WriteProperty("TIMERF", $"{remainingMs}|{t.FunctionName}|{t.Args}");
        }
    }

    private void WriteItem(ISaveWriter w, Item item, long now)
    {
        EngineTags.StripEphemeral(item);

        string? defname = ResolveItemDefName?.Invoke(item.BaseId);
        w.BeginRecord(defname != null ? $"WORLDITEM {defname}" : "WORLDITEM");
        w.WriteProperty("SERIAL", $"0{item.Uid.Value:X8}");
        w.WriteProperty("UUID", item.Uuid.ToString("D"));
        if (defname == null)
            w.WriteProperty("ID", $"0{item.BaseId:X}");
        w.WriteProperty("NAME", item.Name);
        w.WriteProperty("P", item.Position.ToString());
        if (item.Hue.Value != 0) w.WriteProperty("COLOR", $"0{item.Hue.Value:x}");
        if (item.Amount > 1) w.WriteProperty("AMOUNT", item.Amount.ToString());
        if (item.Direction != 0) w.WriteProperty("DIR", item.Direction.ToString());
        if ((uint)item.Attributes != 0) w.WriteProperty("ATTR", $"0{(uint)item.Attributes:x}");
        if (item.DispIdOverride != 0) w.WriteProperty("DISPID", $"0{item.DispIdOverride:x}");

        if (item.More1 != 0) w.WriteProperty("MORE1", $"0{item.More1:X}");
        if (item.More2 != 0) w.WriteProperty("MORE2", $"0{item.More2:X}");
        if (item.MoreB != 0) w.WriteProperty("MOREB", $"0{item.MoreB:X}");
        if (item.MoreP != Point3D.Zero) w.WriteProperty("MOREP", item.MoreP.ToString());
        if (item.Crafter.IsValid) w.WriteProperty("CRAFTER", $"0{item.Crafter.Value:X}");
        if (item.UsesRemaining != 0) w.WriteProperty("USESREMAINING", item.UsesRemaining.ToString());
        if (item.Link.IsValid) w.WriteProperty("LINK", $"0{item.Link.Value:X}");
        if (item.Price != 0) w.WriteProperty("PRICE", item.Price.ToString());
        if (item.Quality != 50) w.WriteProperty("QUALITY", item.Quality.ToString());

        item.MigrateHitsFromTags();
        if (item.HitsCur > 0) w.WriteProperty("HITS", item.HitsCur.ToString());
        if (item.HitsMax > 0) w.WriteProperty("MAXHITS", item.HitsMax.ToString());

        if (item.TData1 != 0) w.WriteProperty("TDATA1", item.TData1.ToString());
        if (item.TData2 != 0) w.WriteProperty("TDATA2", item.TData2.ToString());
        if (item.TData3 != 0) w.WriteProperty("TDATA3", item.TData3.ToString());
        if (item.TData4 != 0) w.WriteProperty("TDATA4", item.TData4.ToString());

        if (item.ContainedIn.IsValid) w.WriteProperty("CONT", $"0{item.ContainedIn.Value:X8}");
        if (item.EquipLayer != 0) w.WriteProperty("LAYER", ((byte)item.EquipLayer).ToString());
        if (item.ContainerGridIndex != 0) w.WriteProperty("CONTGRID", item.ContainerGridIndex.ToString());

        long timeout = item.Timeout;
        if (timeout > 0)
        {
            long remainingMs = timeout - now;
            if (remainingMs > 0)
                w.WriteProperty("TIMERMS", remainingMs.ToString());
        }

        if (item.DecayTime > 0)
        {
            long remainingSec = (item.DecayTime - now) / 1000;
            if (remainingSec > 0)
                w.WriteProperty("DECAY", remainingSec.ToString());
        }

        WriteTimerF(w, item, now);

        foreach (var r in item.Events)
        {
            if (r.Type != SphereNet.Core.Enums.ResType.Events || r.Index == 0) continue;
            string? name = ResolveResourceName?.Invoke(r);
            if (!string.IsNullOrWhiteSpace(name))
                w.WriteProperty("EVENTS", name!);
        }

        // Spawn items: write ADDOBJ from live SpawnComponent state, not stale tag
        if (item.SpawnChar != null)
        {
            item.SpawnChar.CleanupDead();
            foreach (var uid in item.SpawnChar.SpawnedUids)
                w.WriteProperty("ADDOBJ", $"0{uid.Value:x}");
        }

        item.MigrateRuneFromTags();

        foreach (var (key, val) in item.Tags.GetAll())
        {
            string upper = key.ToUpperInvariant();
            if (upper == "ADDOBJ")
                continue; // already written from SpawnComponent above
            if (EngineTags.IsEphemeral(key))
                continue;
            if (upper is "HITS" or "HITSMAX" or "MAXHITS")
                continue;
            if (upper.StartsWith("RUNE_", StringComparison.Ordinal))
                continue;
            if (upper is "SPAWNID" or "TIMELO" or "TIMEHI" or "MAXDIST"
                or "REGION.FLAGS" or "REGION.EVENTS" or "OWNER" or "HOUSETYPE"
                or "LOCKDOWNSPERCENT" or "BASEVENDORS" or "BASESTORAGE")
            {
                w.WriteProperty(upper, val);
            }
            else if (upper is "ADDCOMP" or "SECURE" or "LOCKITEM")
            {
                foreach (var entry in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    w.WriteProperty(upper, entry);
            }
            else
            {
                w.WriteProperty("TAG." + key, val);
            }
        }

        w.EndRecord();
    }

    private void WriteChar(ISaveWriter w, Character ch, long now)
    {
        EngineTags.StripEphemeral(ch);

        string? defname = ch.CharDefIndex != 0
            ? ResolveCharDefName?.Invoke(ch.CharDefIndex)
            : null;
        if (string.IsNullOrEmpty(defname) && ch.TryGetTag("CHARDEF", out string? tagDef) && !string.IsNullOrEmpty(tagDef))
            defname = tagDef;
        w.BeginRecord(defname != null ? $"WORLDCHAR {defname}" : "WORLDCHAR");
        w.WriteProperty("SERIAL", $"0{ch.Uid.Value:X8}");
        w.WriteProperty("UUID", ch.Uuid.ToString("D"));
        w.WriteProperty("NAME", ch.Name);
        w.WriteProperty("P", ch.Position.ToString());
        w.WriteProperty("BODY", $"0{ch.BodyId:X}");
        // Full-width chardef hash (24-bit). Without this, NPCs reload with
        // CharDefIndex=0 → trigger / brain lookups fall back to BaseId
        // (the truncated body id) and re-introduce the c_alchemist→c_man
        // brain hijack on every restart.
        if (ch.CharDefIndex != 0 && ch.CharDefIndex != ch.BaseId)
            w.WriteProperty("CHARDEFINDEX", $"0{ch.CharDefIndex:X}");
        if (ch.Hue.Value != 0) w.WriteProperty("COLOR", $"0{ch.Hue.Value:x}");
        w.WriteProperty("DIR", ((byte)ch.Direction).ToString());
        w.WriteProperty("STR", ch.Str.ToString());
        w.WriteProperty("DEX", ch.Dex.ToString());
        w.WriteProperty("INT", ch.Int.ToString());
        w.WriteProperty("HITS", ch.Hits.ToString());
        w.WriteProperty("MANA", ch.Mana.ToString());
        w.WriteProperty("STAM", ch.Stam.ToString());
        w.WriteProperty("MAXHITS", ch.MaxHits.ToString());
        w.WriteProperty("MAXMANA", ch.MaxMana.ToString());
        w.WriteProperty("MAXSTAM", ch.MaxStam.ToString());
        w.WriteProperty("OFAME", ch.Fame.ToString());
        w.WriteProperty("OKARMA", ch.Karma.ToString());
        if (ch.Food != 0) w.WriteProperty("OFOOD", ch.Food.ToString());
        var maxFoodTag = ch.Tags.Get("MAXFOOD");
        if (!string.IsNullOrEmpty(maxFoodTag)) w.WriteProperty("MAXFOOD", maxFoodTag);
        if (ch.ResPhysical != 0) w.WriteProperty("RESPHYSICAL", ch.ResPhysical.ToString());
        if (ch.ResFire != 0) w.WriteProperty("RESFIRE", ch.ResFire.ToString());
        if (ch.ResCold != 0) w.WriteProperty("RESCOLD", ch.ResCold.ToString());
        if (ch.ResPoison != 0) w.WriteProperty("RESPOISON", ch.ResPoison.ToString());
        if (ch.ResEnergy != 0) w.WriteProperty("RESENERGY", ch.ResEnergy.ToString());
        if (ch.Kills != 0) w.WriteProperty("KILLS", ch.Kills.ToString());
        if (ch.CriminalTimerRemainingSeconds > 0)
            w.WriteProperty("CRIMINALTIMER", ch.CriminalTimerRemainingSeconds.ToString());
        if (ch.MurderDecayRemainingSeconds > 0)
            w.WriteProperty("MURDERDECAY", ch.MurderDecayRemainingSeconds.ToString());
        if (!string.IsNullOrEmpty(ch.Title)) w.WriteProperty("TITLE", ch.Title);
        w.WriteProperty("FLAGS", $"0{(uint)ch.StatFlags:x}");
        w.WriteProperty("NPC", ((int)ch.NpcBrain).ToString());
        if (ch.NpcSpells.Count > 0)
        {
            foreach (var spell in ch.NpcSpells)
                w.WriteProperty("NPCSPELL", ((int)spell).ToString());
        }

        if (ch.OStr != 0) w.WriteProperty("OSTR", ch.OStr.ToString());
        if (ch.ODex != 0) w.WriteProperty("ODEX", ch.ODex.ToString());
        if (ch.OInt != 0) w.WriteProperty("OINT", ch.OInt.ToString());
        if (ch.OBody != 0) w.WriteProperty("OBODY", $"0{ch.OBody:X}");
        if (ch.OSkin != 0) w.WriteProperty("OSKIN", $"0{ch.OSkin:x}");
        if (ch.Luck != 0) w.WriteProperty("LUCK", ch.Luck.ToString());
        if (ch.Exp != 0) w.WriteProperty("EXP", ch.Exp.ToString());
        if (ch.Level != 0) w.WriteProperty("LEVEL", ch.Level.ToString());
        if (ch.Deaths != 0) w.WriteProperty("DEATHS", ch.Deaths.ToString());
        if (ch.Home.X != 0 || ch.Home.Y != 0)
            w.WriteProperty("HOME", $"{ch.Home.X},{ch.Home.Y},{ch.Home.Z},{ch.Home.Map}");
        if (ch.HomeDist != 10) w.WriteProperty("HOMEDIST", ch.HomeDist.ToString());
        if (ch.ActPri != 0) w.WriteProperty("ACTPRI", ch.ActPri.ToString());
        if (ch.Action != 0) w.WriteProperty("ACTION", ((int)ch.Action).ToString());
        if (ch.Act.IsValid) w.WriteProperty("ACT", $"0{ch.Act.Value:X8}");
        if (ch.ActArg1 != 0) w.WriteProperty("ACTARG1", ch.ActArg1.ToString());
        if (ch.ActArg2 != 0) w.WriteProperty("ACTARG2", ch.ActArg2.ToString());
        if (ch.ActArg3 != 0) w.WriteProperty("ACTARG3", ch.ActArg3.ToString());
        if (ch.ActP.X != 0 || ch.ActP.Y != 0 || ch.ActP.Z != 0 || ch.ActP.Map != 0)
            w.WriteProperty("ACTP", $"{ch.ActP.X},{ch.ActP.Y},{ch.ActP.Z},{ch.ActP.Map}");
        if (ch.ActPrv.IsValid) w.WriteProperty("ACTPRV", $"0{ch.ActPrv.Value:X8}");
        if (ch.ActDiff != 0) w.WriteProperty("ACTDIFF", ch.ActDiff.ToString());
        if (ch.FightTarget.IsValid) w.WriteProperty("FIGHTTARGET", $"0{ch.FightTarget.Value:X8}");
        if (!ch.IsPlayer && ch.PetAIMode != SphereNet.Core.Enums.PetAIMode.Follow)
            w.WriteProperty("PETAI", ((int)ch.PetAIMode).ToString());
        if (ch.FleeStepsCurrent != 0) w.WriteProperty("FLEESTEPS", ch.FleeStepsCurrent.ToString());
        if (ch.FleeStepsMax != 0) w.WriteProperty("FLEESTEPSMAX", ch.FleeStepsMax.ToString());
        if (ch.SpeechColor != 0x0035) w.WriteProperty("SPEECHCOLOR", ch.SpeechColor.ToString());
        if (ch.MaxFollower != 5) w.WriteProperty("MAXFOLLOWER", ch.MaxFollower.ToString());
        if (ch.ResFireMax != 70) w.WriteProperty("RESFIREMAX", ch.ResFireMax.ToString());
        if (ch.ResColdMax != 70) w.WriteProperty("RESCOLDMAX", ch.ResColdMax.ToString());
        if (ch.ResPoisonMax != 70) w.WriteProperty("RESPOISONMAX", ch.ResPoisonMax.ToString());
        if (ch.ResEnergyMax != 70) w.WriteProperty("RESENERGYMAX", ch.ResEnergyMax.ToString());
        if (ch.NightSight) w.WriteProperty("NIGHTSIGHT", "1");
        if (ch.StepStealth != 0) w.WriteProperty("STEPSTEALTH", ch.StepStealth.ToString());
        if (ch.SpeedMode != 0) w.WriteProperty("SPEEDMODE", ch.SpeedMode.ToString());
        if (!string.IsNullOrEmpty(ch.Profile)) w.WriteProperty("PROFILE", ch.Profile);
        if (ch.PFlag != 0) w.WriteProperty("PFLAG", ch.PFlag.ToString());
        if (ch.Tithing != 0) w.WriteProperty("TITHING", ch.Tithing.ToString());
        if (ch.SkillClass != 0) w.WriteProperty("SKILLCLASS", ch.SkillClass.ToString());

        if (ch.IsPlayer) w.WriteProperty("ISPLAYER", "1");

        long chTimeout = ch.Timeout;
        if (chTimeout > 0)
        {
            long chRemainingMs = chTimeout - now;
            if (chRemainingMs > 0)
                w.WriteProperty("TIMERMS", chRemainingMs.ToString());
        }

        var accountTag = ch.Tags.Get("ACCOUNT");
        if (!string.IsNullOrEmpty(accountTag))
            w.WriteProperty("ACCOUNT", accountTag);

        for (int s = 0; s < (int)SphereNet.Core.Enums.SkillType.Qty; s++)
        {
            var skillType = (SphereNet.Core.Enums.SkillType)s;
            ushort val = ch.GetSkill(skillType);
            if (val > 0)
            {
                string skillName = Enum.IsDefined(skillType) ? skillType.ToString() : $"SKILL[{s}]";
                w.WriteProperty(skillName, val.ToString());
            }
        }

        for (int s = 0; s < (int)SphereNet.Core.Enums.SkillType.Qty; s++)
        {
            byte lockVal = ch.GetSkillLock((SphereNet.Core.Enums.SkillType)s);
            if (lockVal != 0)
                w.WriteProperty($"SkillLock[{s}]", lockVal.ToString());
        }

        for (int i = 0; i < 3; i++)
        {
            byte lockVal = ch.GetStatLock(i);
            if (lockVal != 0)
                w.WriteProperty($"StatLock[{i}]", lockVal.ToString());
        }

        for (int layer = 0; layer <= (int)SphereNet.Core.Enums.Layer.Horse; layer++)
        {
            // Skip the virtual vendor stock containers (LAYER 26/27); they
            // and their contents are excluded from CaptureSnapshot and are
            // rebuilt on demand, so persisting the EQUIP reference would
            // dangle to a non-existent item on load.
            if (layer == (int)SphereNet.Core.Enums.Layer.VendorStock ||
                layer == (int)SphereNet.Core.Enums.Layer.VendorExtra)
                continue;
            var equip = ch.GetEquippedItem((SphereNet.Core.Enums.Layer)layer);
            if (equip != null)
                w.WriteProperty($"EQUIP[{layer}]", $"0{equip.Uid.Value:X8}");
        }

        foreach (var r in ch.Events)
        {
            if (r.Type != SphereNet.Core.Enums.ResType.Events || r.Index == 0) continue;
            string? name = ResolveResourceName?.Invoke(r);
            if (!string.IsNullOrWhiteSpace(name))
                w.WriteProperty("EVENTS", name!);
        }

        foreach (var mem in ch.Memories)
        {
            var flags = mem.GetMemoryTypes();
            if (flags == 0) continue;
            // Only persist non-transient memories
            const SphereNet.Core.Enums.MemoryType persistent =
                SphereNet.Core.Enums.MemoryType.IPet |
                SphereNet.Core.Enums.MemoryType.Guard |
                SphereNet.Core.Enums.MemoryType.Guild |
                SphereNet.Core.Enums.MemoryType.Town |
                SphereNet.Core.Enums.MemoryType.Friend;
            if ((flags & persistent) == 0) continue;
            w.WriteProperty("MEMORY", $"0{mem.Link.Value:X8},{(ushort)flags}");
        }

        WriteTimerF(w, ch, now);

        // Active poison (level, remaining ticks + time, poisoner). Saved as remaining
        // time so it resumes after load instead of silently ending on restart.
        if (ch.Poison.IsPoisoned && ch.Poison.TicksRemaining > 0)
        {
            string src = ch.Poison.Source.IsValid ? $"0{ch.Poison.Source.Value:X}" : "0";
            w.WriteProperty("POISON",
                $"{ch.Poison.Level}|{ch.Poison.TicksRemaining}|{ch.Poison.RemainingTickMs}|{src}");
        }

        foreach (var (key, val) in ch.Tags.GetAll())
        {
            string upper = key.ToUpperInvariant();
            if (upper is "ACCOUNT" or "MAXFOOD")
                continue;
            if (EngineTags.IsEphemeral(key))
                continue;
            if (upper.StartsWith("STATLOCK.", StringComparison.Ordinal))
                continue;
            if (upper is "DSPEECH" or "EMOTECOLOR" or "VIRTUALGOLD"
                or "LASTUSED" or "LASTDISCONNECTED" or "NEED" or "SPAWNITEM")
            {
                w.WriteProperty(upper, val);
                continue;
            }
            w.WriteProperty("TAG." + key, val);
        }

        w.EndRecord();
    }

    private void SaveServerData(string savePath, GameWorld world)
    {
        string finalPath = Path.Combine(savePath, "spheredata.scp");
        string tmpPath = finalPath + ".tmp";
        using (var sw = new StreamWriter(tmpPath))
        {
            sw.WriteLine("// SphereNet Server Data Save");
            sw.WriteLine($"// Save #{_saveIndex} at {DateTime.UtcNow:u}");
            sw.WriteLine();
            sw.WriteLine("[SPHERE]");
            sw.WriteLine("VERSION=1");
            sw.WriteLine($"SAVECOUNT={_saveIndex}");
            sw.WriteLine($"TIME={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            sw.WriteLine();

            // GLOBALS
            var globals = world.GetAllGlobalVars().ToList();
            if (globals.Count > 0)
            {
                sw.WriteLine("[GLOBALS]");
                foreach (var (key, val) in globals)
                    sw.WriteLine($"{key}={val}");
                sw.WriteLine();
            }

            // LISTs
            foreach (var (name, list) in world.GetAllGlobalLists())
            {
                if (list.Count == 0) continue;
                sw.WriteLine($"[LIST {name}]");
                foreach (var elem in list)
                    sw.WriteLine($"ELEM={elem}");
                sw.WriteLine();
            }

            // Open static doors
            var openDoors = world.OpenMapStaticDoors;
            if (openDoors.Count > 0)
            {
                sw.WriteLine("[DOORS]");
                foreach (var (map, x, y, z) in openDoors)
                    sw.WriteLine($"OPEN={map},{x},{y},{z}");
                sw.WriteLine();
            }

            sw.WriteLine("[EOF]");
        }
        CommitFile(finalPath);
    }

    /// <summary>Atomic commit: rotate existing final to .bak1..N, then .tmp → final.</summary>
    private void CommitFile(string finalPath)
    {
        string tmpPath = finalPath + ".tmp";
        if (!File.Exists(tmpPath)) return;

        RotateBackups(finalPath);

        File.Move(tmpPath, finalPath, overwrite: true);
    }

    private void RotateBackups(string finalPath)
    {
        int levels = Math.Clamp(BackupLevels, 0, 32);
        if (levels <= 0)
        {
            for (int i = 1; i <= 32; i++)
            {
                string staleGeneration = $"{finalPath}.bak{i}";
                if (File.Exists(staleGeneration))
                {
                    try { File.Delete(staleGeneration); } catch { /* best effort */ }
                }
            }
            return;
        }

        string oldest = $"{finalPath}.bak{levels}";
        if (File.Exists(oldest))
        {
            try { File.Delete(oldest); } catch { /* best effort */ }
        }

        for (int i = levels - 1; i >= 1; i--)
        {
            string src = $"{finalPath}.bak{i}";
            if (!File.Exists(src)) continue;
            string dst = $"{finalPath}.bak{i + 1}";
            try { File.Move(src, dst, overwrite: true); } catch { /* best effort */ }
        }

        string stale = finalPath + ".bak";
        if (File.Exists(stale))
        {
            try { File.Delete(stale); } catch { /* best effort */ }
        }

        if (File.Exists(finalPath))
        {
            try { File.Copy(finalPath, $"{finalPath}.bak1", overwrite: true); } catch { /* best effort */ }
        }
    }

    private void CleanupTmpFiles(string savePath)
    {
        try
        {
            foreach (var tmp in Directory.EnumerateFiles(savePath, "*.tmp"))
            {
                try { File.Delete(tmp); }
                catch { /* best effort */ }
            }
        }
        catch { /* directory may not exist */ }
    }

    /// <summary>Delete files that match the save base but are NOT in the
    /// current output list — prevents orphaned old-format/old-shard files
    /// from shadowing the live snapshot.</summary>
    private void RemoveStaleSiblings(string savePath, string baseName, IEnumerable<string> keepNames)
    {
        var keep = new HashSet<string>(keepNames, StringComparer.OrdinalIgnoreCase);
        // Known save extensions including .bak from prior commits.
        string[] patterns =
        {
            $"{baseName}.scp", $"{baseName}.scp.gz", $"{baseName}.sbin", $"{baseName}.sbin.gz",
        };
        foreach (string p in patterns)
        {
            string full = Path.Combine(savePath, p);
            if (File.Exists(full) && !keep.Contains(p))
            {
                try { File.Delete(full); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove stale save {File}", full); }
            }
        }
        // Shard fragments {base}.{N}.{ext} + any leftover .bak sidecars.
        foreach (string file in Directory.GetFiles(savePath, baseName + ".*"))
        {
            string name = Path.GetFileName(file);
            if (keep.Contains(name)) continue;
            if (name.Equals(baseName + ".manifest", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

            // Plain .bak leftovers from older runs always go. Numbered
            // .bakN files belong to BackupLevels rotation.
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(file); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove stale backup {File}", file); }
                continue;
            }

            // Only touch things that look like shard fragments.
            if (IsShardFragment(name, baseName))
            {
                try { File.Delete(file); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove stale shard {File}", file); }
            }
        }
    }

    private static bool IsShardFragment(string fileName, string baseName)
    {
        // e.g. "sphereworld.3.sbin.gz" → prefix "sphereworld." + digits + known extension
        if (!fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) return false;
        int digitStart = baseName.Length + 1;
        int i = digitStart;
        while (i < fileName.Length && char.IsDigit(fileName[i])) i++;
        if (i == digitStart) return false; // no digits
        string tail = fileName[i..].ToLowerInvariant();
        return tail == ".scp" || tail == ".scp.gz" || tail == ".sbin" || tail == ".sbin.gz";
    }

    private sealed record WorldSaveSnapshot(IReadOnlyList<SaveRecord> Items, IReadOnlyList<SaveRecord> Characters);

    private sealed record SaveRecord(uint Uid, string Section, IReadOnlyList<(string Key, string Value)> Properties);

    private sealed class SnapshotSaveWriter : ISaveWriter
    {
        private readonly List<(string Key, string Value)> _properties = [];
        private string? _section;
        private bool _recordOpen;

        public long WrittenBytes { get; private set; }

        public void BeginRecord(string section)
        {
            if (_recordOpen)
                EndRecord();
            _section = section;
            _properties.Clear();
            _recordOpen = true;
            WrittenBytes += section.Length;
        }

        public void WriteProperty(string key, string value)
        {
            if (!_recordOpen)
                throw new InvalidOperationException("WriteProperty called before BeginRecord");
            _properties.Add((key, value));
            WrittenBytes += key.Length + value.Length + 1;
        }

        public void EndRecord()
        {
            _recordOpen = false;
        }

        public void WriteHeaderComment(string line)
        {
            _ = line;
        }

        public void Flush()
        {
        }

        public SaveRecord ToRecord(uint uid)
        {
            if (string.IsNullOrEmpty(_section))
                throw new InvalidOperationException("Snapshot record was not opened");
            return new SaveRecord(uid, _section, _properties.ToArray());
        }

        public void Dispose()
        {
            EndRecord();
        }
    }
}
