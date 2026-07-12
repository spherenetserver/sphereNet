using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;

namespace SphereNet.Persistence.Load;

/// <summary>
/// World loader. Maps to CWorld::Load in Source-X.
/// Auto-detects on-disk format via the manifest and file extensions so a
/// savedir can mix classic <c>.scp</c>, gzip, binary, or sharded layouts
/// without loader changes.
/// Handles both SphereNet saves (bare <c>[WORLDITEM]</c>) and classic
/// Sphere/Source-X saves (<c>[WORLDITEM i_backpack]</c> with defname suffix).
/// </summary>
public sealed class WorldLoader
{
    private readonly ILogger<WorldLoader> _logger;
    private int _migratedUuids;

    /// <summary>Resolves a Sphere defname to a base graphic/body ID.
    /// Returns 0 when the defname is unknown.</summary>
    public Func<string, ushort>? ResolveItemDef { get; set; }

    /// <inheritdoc cref="ResolveItemDef"/>
    public Func<string, ushort>? ResolveCharDef { get; set; }

    /// <summary>Applies a WORLDCHAR section defname (c_man, c_banker, …).</summary>
    public Action<Character, string>? ApplyCharDefFromName { get; set; }

    /// <summary>Resolves a loaded CHARDEFINDEX to a client body graphic.</summary>
    public Func<int, ushort>? ResolveBodyFromCharDefIndex { get; set; }

    public WorldLoader(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<WorldLoader>();
    }

    public readonly record struct WorldImportScope(Point3D Center, int Distance, int Flags)
    {
        public bool IncludeItems => (Flags & 1) != 0;
        public bool IncludeChars => (Flags & 2) != 0;

        public bool Contains(Point3D point)
        {
            int distance = Math.Max(0, Distance);
            return point.Map == Center.Map && Center.GetDistanceTo(point) <= distance;
        }
    }

    /// <summary>Load one runtime <c>.scp</c>/<c>.sbin</c> file. This is used by
    /// Source-X-style SERV.LOAD / IMPORT / RESTORE world-ops where a script
    /// injects a bounded object file while the server is running.</summary>
    public (int Items, int Chars) LoadFile(GameWorld world, string path, AccountManager? accounts = null,
        WorldImportScope? scope = null)
    {
        if (!File.Exists(path))
            return (0, 0);

        string loadPath = path;
        string? filteredPath = null;
        if (scope.HasValue)
        {
            filteredPath = Path.Combine(Path.GetTempPath(), $"sphnet_import_{Guid.NewGuid():N}.scp");
            int filteredRecords = WriteScopedRuntimeLoadFile(path, filteredPath, scope.Value);
            _logger.LogInformation("Runtime scoped import filter: {Path} -> {Records} world record(s)",
                Path.GetFileName(path), filteredRecords);
            loadPath = filteredPath;
        }

        int itemCount = 0, charCount = 0;
        var charAccountLinks = new List<(Character Char, string AccountName)>();
        var charEquipLinks = new List<(Character Char, Serial ItemSerial, byte Layer)>();
        var itemContLinks = new List<(Item Item, Serial ContSerial, byte Layer)>();

        world.SuppressDirtyNotify = true;
        try
        {
            itemCount = LoadItemFile(world, loadPath, itemContLinks);
            charCount = LoadCharFile(world, loadPath, charAccountLinks, charEquipLinks);
            LoadDataFile(world, loadPath);
        }
        finally
        {
            world.SuppressDirtyNotify = false;
            world.ConsumeDirtyObjects();
            if (filteredPath != null)
            {
                try { File.Delete(filteredPath); } catch { }
            }
        }

        LinkLoadedAccounts(charAccountLinks, accounts);
        ResolveLoadedObjectLinks(world, itemContLinks, charEquipLinks);
        _logger.LogInformation("Runtime load: {Path} -> {Items} items, {Chars} chars",
            Path.GetFileName(path), itemCount, charCount);
        return (itemCount, charCount);
    }

    /// <summary>Restore one runtime object file, replacing any currently
    /// registered world object whose serial is present in the file. Unlike
    /// <see cref="LoadFile"/>, this matches Source-X's destructive restore
    /// intent for colliding serials while leaving unrelated world state alone.</summary>
    public (int Items, int Chars, int Replaced) RestoreFile(GameWorld world, string path, AccountManager? accounts = null,
        Func<IReadOnlyList<ObjBase>, string, int>? backupWriter = null)
    {
        if (!File.Exists(path))
            return (0, 0, 0);

        var restoreSerials = CollectWorldRecordSerials(path);
        var existing = CollectExistingWorldRecords(world, restoreSerials);
        string? backupPath = null;
        if (backupWriter != null && existing.Count > 0)
        {
            backupPath = Path.Combine(Path.GetTempPath(), $"sphnet_restore_rollback_{Guid.NewGuid():N}.scp");
            int backedUp = backupWriter(existing, backupPath);
            _logger.LogInformation("Runtime restore rollback snapshot: {Count} object(s) -> {Path}",
                backedUp, Path.GetFileName(backupPath));
        }

        int replaced = ReplaceExistingWorldRecords(world, restoreSerials);
        (int items, int chars) loaded;
        try
        {
            loaded = LoadFile(world, path, accounts);
        }
        catch (Exception ex) when (backupPath != null && File.Exists(backupPath))
        {
            TryRollbackRestore(world, backupPath, restoreSerials, accounts, ex);
            throw;
        }
        finally
        {
            if (backupPath != null)
            {
                try { File.Delete(backupPath); } catch { }
            }
        }

        _logger.LogInformation("Runtime restore: {Path} -> {Items} items, {Chars} chars, {Replaced} replaced",
            Path.GetFileName(path), loaded.items, loaded.chars, replaced);
        return (loaded.items, loaded.chars, replaced);
    }

    /// <summary>Load world data from save files.</summary>
    public (int Items, int Chars) Load(GameWorld world, string savePath, AccountManager? accounts = null)
    {
        int itemCount = 0, charCount = 0;

        var charAccountLinks = new List<(Character Char, string AccountName)>();
        var charEquipLinks = new List<(Character Char, Serial ItemSerial, byte Layer)>();
        var itemContLinks = new List<(Item Item, Serial ContSerial, byte Layer)>();

        // Suppress dirty notifications for the whole bulk materialization.
        // Otherwise the dirty set balloons to millions of entries and stalls
        // the fast-path drain during login. Clients get a view resync at
        // login anyway.
        world.SuppressDirtyNotify = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var itemPaths = ResolveSaveFiles(savePath, "sphereworld");
            var charPaths = ResolveSaveFiles(savePath, "spherechars");
            var staticPaths = ResolveSaveFiles(savePath, "spherestatics");
            var multiPaths = ResolveSaveFiles(savePath, "spheremultis");

            var itemSectionPaths = itemPaths
                .Concat(charPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string path in itemSectionPaths)
            {
                int n = LoadItemFile(world, path, itemContLinks);
                itemCount += n;
                _logger.LogInformation("Items: {Path} -> {Count}", Path.GetFileName(path), n);
            }

            foreach (string path in staticPaths)
            {
                int n = LoadItemFile(world, path, itemContLinks);
                itemCount += n;
                _logger.LogInformation("Statics: {Path} -> {Count}", Path.GetFileName(path), n);
            }

            foreach (string path in multiPaths)
            {
                int n = LoadItemFile(world, path, itemContLinks);
                itemCount += n;
                _logger.LogInformation("Multis: {Path} -> {Count}", Path.GetFileName(path), n);
            }

            // Sphere/Source-X saves can mix [WORLDITEM] and [WORLDCHAR]
            // sections in either sphereworld.scp or spherechars.scp. Make a
            // second pass over both logical files to pick up mobiles after the
            // item pass has captured all container/equipment references.
            var charSectionPaths = itemPaths
                .Concat(charPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string path in charSectionPaths)
            {
                int n = LoadCharFile(world, path, charAccountLinks, charEquipLinks);
                if (n > 0)
                {
                    charCount += n;
                    _logger.LogInformation("WorldChars: {Path} -> {Count}", Path.GetFileName(path), n);
                }
            }

            // spheredata.scp — globals, lists, world scripts
            var dataPaths = ResolveSaveFiles(savePath, "spheredata");
            foreach (string path in dataPaths)
                LoadDataFile(world, path);
        }
        finally
        {
            world.SuppressDirtyNotify = false;
            world.ConsumeDirtyObjects();
        }

        // Post-load: link accounts to characters (only if not already linked via CHARUID)
        if (accounts != null)
        {
            int linked = 0;
            foreach (var (ch, accName) in charAccountLinks)
            {
                var acc = accounts.FindAccount(accName);
                if (acc != null)
                {
                    // Any char with a resolved account slot is a player
                    // character. Legacy Sphere saves don't write ISPLAYER
                    // so without this line those chars default to
                    // IsPlayer=false and GetNotoriety treats them as
                    // NPCs (grey overhead name, ignored by notoriety
                    // rules). Account linkage is authoritative proof
                    // that this is a player mobile.
                    ch.IsPlayer = true;

                    bool alreadyLinked = false;
                    for (int i = 0; i < 7; i++)
                    {
                        if (acc.GetCharSlot(i) == ch.Uid)
                        {
                            alreadyLinked = true;
                            break;
                        }
                    }

                    if (!alreadyLinked)
                    {
                        int slot = acc.FindFreeSlot();
                        if (slot >= 0)
                        {
                            acc.SetCharSlot(slot, ch.Uid);
                            linked++;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Orphan player character 0x{Uid:X8} ({Name}) — account '{Account}' not found",
                        ch.Uid.Value, ch.Name, accName);
                }
            }
            _logger.LogInformation("Linked {Count}/{Total} characters to accounts", linked, charAccountLinks.Count);
        }

        // Post-load: resolve container/equipment references
        int containedCount = 0;
        foreach (var (item, contSerial, layer) in itemContLinks)
        {
            var parent = world.FindObject(contSerial);
            if (parent is Character parentChar)
            {
                parentChar.Equip(item, (Layer)layer);
                containedCount++;
            }
            else if (parent is Item parentItem)
            {
                if (parentItem.Uid == item.Uid)
                {
                    _logger.LogWarning("Item {Uid:X8} references itself as container, placing on ground",
                        item.Uid.Value);
                    world.PlaceItem(item, item.Position);
                }
                else
                {
                    parentItem.AddItem(item);
                    containedCount++;
                }
            }
            else
            {
                world.PlaceItem(item, item.Position);
            }
        }

        int equipCount = 0;
        foreach (var (ch, itemSerial, layer) in charEquipLinks)
        {
            var item = world.FindItem(itemSerial);
            if (item == null)
            {
                _logger.LogWarning("Character 0x{CharUid:X8} references missing EQUIP item 0x{ItemUid:X8} on layer {Layer}",
                    ch.Uid.Value, itemSerial.Value, layer);
                continue;
            }

            if (item.ContainedIn.IsValid && world.FindItem(item.ContainedIn) is { } parentItem)
                parentItem.RemoveItem(item);

            if (ch.Equip(item, (Layer)layer))
                equipCount++;
        }

        _logger.LogInformation("World loaded: {Items} items, {Chars} chars, {Contained} contained/equipped in {Elapsed}s",
            itemCount, charCount, containedCount + equipCount, sw.Elapsed.TotalSeconds.ToString("F1"));

        if (_migratedUuids > 0)
            _logger.LogInformation("UUID migration: {Count} objects had no UUID — auto-generated (will persist on next save)",
                _migratedUuids);

        return (itemCount, charCount);
    }

    private void LinkLoadedAccounts(List<(Character Char, string AccountName)> charAccountLinks,
        AccountManager? accounts)
    {
        if (accounts == null)
            return;

        int linked = 0;
        foreach (var (ch, accName) in charAccountLinks)
        {
            var acc = accounts.FindAccount(accName);
            if (acc != null)
            {
                ch.IsPlayer = true;

                bool alreadyLinked = false;
                for (int i = 0; i < 7; i++)
                {
                    if (acc.GetCharSlot(i) == ch.Uid)
                    {
                        alreadyLinked = true;
                        break;
                    }
                }

                if (!alreadyLinked)
                {
                    int slot = acc.FindFreeSlot();
                    if (slot >= 0)
                    {
                        acc.SetCharSlot(slot, ch.Uid);
                        linked++;
                    }
                }
            }
            else
            {
                _logger.LogWarning("Orphan player character 0x{Uid:X8} ({Name}) — account '{Account}' not found",
                    ch.Uid.Value, ch.Name, accName);
            }
        }

        if (charAccountLinks.Count > 0)
            _logger.LogInformation("Linked {Count}/{Total} characters to accounts", linked, charAccountLinks.Count);
    }

    private void ResolveLoadedObjectLinks(GameWorld world,
        List<(Item Item, Serial ContSerial, byte Layer)> itemContLinks,
        List<(Character Char, Serial ItemSerial, byte Layer)> charEquipLinks)
    {
        foreach (var (item, contSerial, layer) in itemContLinks)
        {
            var parent = world.FindObject(contSerial);
            if (parent is Character parentChar)
            {
                parentChar.Equip(item, (Layer)layer);
            }
            else if (parent is Item parentItem)
            {
                if (parentItem.Uid == item.Uid)
                {
                    _logger.LogWarning("Item {Uid:X8} references itself as container, placing on ground",
                        item.Uid.Value);
                    world.PlaceItem(item, item.Position);
                }
                else
                {
                    parentItem.AddItem(item);
                }
            }
            else
            {
                world.PlaceItem(item, item.Position);
            }
        }

        foreach (var (ch, itemSerial, layer) in charEquipLinks)
        {
            var item = world.FindItem(itemSerial);
            if (item == null)
            {
                _logger.LogWarning("Character 0x{CharUid:X8} references missing EQUIP item 0x{ItemUid:X8} on layer {Layer}",
                    ch.Uid.Value, itemSerial.Value, layer);
                continue;
            }

            if (item.ContainedIn.IsValid && world.FindItem(item.ContainedIn) is { } parentItem)
                parentItem.RemoveItem(item);

            ch.Equip(item, (Layer)layer);
        }
    }

    /// <summary>Resolve actual on-disk paths for a logical save name
    /// (e.g. "sphereworld"). Priority: manifest → format probes. Returns an
    /// empty list if nothing exists.</summary>
    private List<string> ResolveSaveFiles(string savePath, string baseName)
    {
        string manifestPath = ShardManifest.PathFor(savePath, baseName);
        var manifest = ShardManifest.TryLoad(manifestPath);
        if (manifest != null && manifest.Files.Count > 0)
        {
            var list = new List<string>(manifest.Files.Count);
            foreach (var name in manifest.Files)
            {
                string full = Path.Combine(savePath, name);
                if (File.Exists(full)) list.Add(full);
                else _logger.LogWarning("Manifest references missing shard {File}", name);
            }
            _logger.LogInformation("Loaded manifest {Base}: format={Format}, shards={Count}",
                baseName, manifest.Format, list.Count);
            return list;
        }

        // No manifest — probe for single-file variants in priority order
        // (most-specific format first so a gzip survives alongside a stale .scp).
        foreach (string ext in new[] { ".sbin.gz", ".sbin", ".scp.gz", ".scp" })
        {
            string candidate = Path.Combine(savePath, baseName + ext);
            if (File.Exists(candidate))
                return new List<string> { candidate };
        }
        return new List<string>();
    }

    private int LoadItemFile(GameWorld world, string path, List<(Item, Serial, byte)> contLinks)
    {
        int count = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            if (!ParseSectionType(section, "WORLDITEM", out string? defname))
            {
                while (reader.NextProperty(out _, out _)) { /* skip */ }
                continue;
            }

            var item = world.CreateItem();
            Serial contSerial = Serial.Invalid;
            byte layer = 0;

            if (defname != null && ResolveItemDef != null)
            {
                ushort baseId = ResolveItemDef(defname);
                if (baseId != 0)
                    item.BaseId = baseId;
                else
                    _logger.LogDebug("Unknown item defname '{DefName}' — BaseId stays default", defname);
            }

            bool hasUuid = false;
            bool hasId = false;
            bool skipItem = false;
            while (reader.NextProperty(out string key, out string val))
            {
                string upper = key.ToUpperInvariant();
                if (upper == "SERIAL")
                {
                    if (TryParseHexOrDec(val, out uint serial))
                    {
                        var oldUid = item.Uid;
                        var newUid = new Serial(serial);
                        if (!world.TryReRegisterObject(item, oldUid, newUid, out var existing))
                        {
                            _logger.LogWarning(
                                "Skipping duplicate item serial 0x{Serial:X8} in {File}: existing={ExistingType}",
                                serial, Path.GetFileName(path), existing?.GetType().Name ?? "unknown");
                            world.DeleteObject(item);
                            skipItem = true;
                        }
                    }
                    continue;
                }
                if (upper == "UUID")
                {
                    if (Guid.TryParse(val, out Guid uuid))
                    {
                        var oldUuid = item.Uuid;
                        item.Uuid = uuid;
                        if (!world.TryReIndexUuid(item, oldUuid, out var existing))
                        {
                            _logger.LogWarning(
                                "Skipping duplicate item UUID {Uuid} for serial 0x{Serial:X8} in {File}: existing=0x{Existing:X8}",
                                uuid, item.Uid.Value, Path.GetFileName(path), existing?.Uid.Value ?? 0);
                            world.DeleteObject(item);
                            skipItem = true;
                        }
                        hasUuid = true;
                    }
                    continue;
                }
                if (upper == "CONT")
                {
                    if (TryParseHexOrDec(val, out uint c))
                        contSerial = new Serial(c);
                    continue;
                }
                if (upper == "LAYER")
                {
                    byte.TryParse(val, out layer);
                    continue;
                }
                if (upper == "CREATE")
                    continue;
                if (skipItem)
                    continue;
                if (upper == "ID")
                    hasId = true;
                ApplyItemProperty(item, key, val);
            }
            if (skipItem)
                continue;

            if (!hasUuid)
                _migratedUuids++;

            if (!hasId && defname != null && item.BaseId == 0)
                _logger.LogWarning("Item 0x{Uid:X} defname='{Def}' has no BaseId after load — will be invisible",
                    item.Uid.Value, defname);

            if (contSerial.IsValid)
                contLinks.Add((item, contSerial, layer));
            else
                world.PlaceItem(item, item.Position);
            count++;

            if (count % 100_000 == 0)
                _logger.LogInformation("  Loading items... {Count} ({Elapsed}s)",
                    count, sw.Elapsed.TotalSeconds.ToString("F1"));
        }
        return count;
    }

    private enum RuntimeWorldRecordKind
    {
        Other,
        Item,
        Character
    }

    private sealed class RuntimeWorldRecord
    {
        public RuntimeWorldRecord(string section, List<(string Key, string Value)> properties,
            RuntimeWorldRecordKind kind)
        {
            Section = section;
            Properties = properties;
            Kind = kind;
        }

        public string Section { get; }
        public List<(string Key, string Value)> Properties { get; }
        public RuntimeWorldRecordKind Kind { get; }
        public Serial Serial { get; set; } = Serial.Invalid;
        public Serial Container { get; set; } = Serial.Invalid;
        public Point3D? Position { get; set; }
    }

    private int WriteScopedRuntimeLoadFile(string sourcePath, string filteredPath, WorldImportScope scope)
    {
        var records = ReadRuntimeWorldRecords(sourcePath);
        var keep = SelectScopedRuntimeRecords(records, scope);

        Directory.CreateDirectory(Path.GetDirectoryName(filteredPath) ?? ".");
        using var fs = new FileStream(filteredPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new TextSaveWriter(fs);
        writer.WriteHeaderComment($"SphereNet scoped import filter from {Path.GetFileName(sourcePath)} at {DateTime.UtcNow:u}");

        int keptWorldRecords = 0;
        foreach (var record in records)
        {
            bool isWorldRecord = record.Kind != RuntimeWorldRecordKind.Other;
            if (isWorldRecord && !keep.Contains(record))
                continue;

            writer.BeginRecord(record.Section);
            foreach (var (key, value) in record.Properties)
                writer.WriteProperty(key, value);
            writer.EndRecord();

            if (isWorldRecord)
                keptWorldRecords++;
        }

        return keptWorldRecords;
    }

    private static List<RuntimeWorldRecord> ReadRuntimeWorldRecords(string path)
    {
        var records = new List<RuntimeWorldRecord>();
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            RuntimeWorldRecordKind kind = RuntimeWorldRecordKind.Other;
            if (ParseSectionType(section, "WORLDITEM", out _))
                kind = RuntimeWorldRecordKind.Item;
            else if (ParseSectionType(section, "WORLDCHAR", out _))
                kind = RuntimeWorldRecordKind.Character;

            var props = new List<(string Key, string Value)>();
            while (reader.NextProperty(out string key, out string val))
                props.Add((key, val));

            var record = new RuntimeWorldRecord(section, props, kind);
            if (kind != RuntimeWorldRecordKind.Other)
                PopulateRuntimeWorldRecordMetadata(record);
            records.Add(record);
        }

        return records;
    }

    private static void PopulateRuntimeWorldRecordMetadata(RuntimeWorldRecord record)
    {
        foreach (var (key, value) in record.Properties)
        {
            if (key.Equals("SERIAL", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHexOrDec(value, out uint serial))
                    record.Serial = new Serial(serial);
            }
            else if (record.Kind == RuntimeWorldRecordKind.Item &&
                     key.Equals("CONT", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHexOrDec(value, out uint cont))
                    record.Container = new Serial(cont);
            }
            else if (key.Equals("P", StringComparison.OrdinalIgnoreCase))
            {
                if (Point3D.TryParse(value.AsSpan(), out var point))
                    record.Position = point;
            }
        }
    }

    private static HashSet<RuntimeWorldRecord> SelectScopedRuntimeRecords(
        IReadOnlyList<RuntimeWorldRecord> records, WorldImportScope scope)
    {
        var keep = new HashSet<RuntimeWorldRecord>();
        var bySerial = new Dictionary<uint, RuntimeWorldRecord>();

        foreach (var record in records)
        {
            if (record.Serial.IsValid && !bySerial.ContainsKey(record.Serial.Value))
                bySerial.Add(record.Serial.Value, record);
        }

        foreach (var record in records)
        {
            if (record.Kind == RuntimeWorldRecordKind.Character)
            {
                if (scope.IncludeChars && record.Position is { } point && scope.Contains(point))
                    keep.Add(record);
            }
            else if (record.Kind == RuntimeWorldRecordKind.Item)
            {
                if (scope.IncludeItems && !record.Container.IsValid &&
                    record.Position is { } point && scope.Contains(point))
                    keep.Add(record);
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var record in records)
            {
                if (record.Kind != RuntimeWorldRecordKind.Item || keep.Contains(record) ||
                    !scope.IncludeItems || !record.Container.IsValid)
                    continue;

                if (bySerial.TryGetValue(record.Container.Value, out var parent) && keep.Contains(parent))
                    changed |= keep.Add(record);
            }
        } while (changed);

        return keep;
    }

    private static List<Serial> CollectWorldRecordSerials(string path)
    {
        var serials = new List<Serial>();
        var seen = new HashSet<uint>();
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            bool isWorldRecord =
                ParseSectionType(section, "WORLDITEM", out _) ||
                ParseSectionType(section, "WORLDCHAR", out _);

            while (reader.NextProperty(out string key, out string val))
            {
                if (!isWorldRecord || !key.Equals("SERIAL", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryParseHexOrDec(val, out uint serial) && serial != 0 && seen.Add(serial))
                    serials.Add(new Serial(serial));
            }
        }

        return serials;
    }

    private int ReplaceExistingWorldRecords(GameWorld world, IReadOnlyList<Serial> restoreSerials)
    {
        int replaced = 0;
        foreach (var serial in restoreSerials)
        {
            var existing = world.FindObject(serial);
            if (existing == null)
                continue;

            world.DeleteObject(existing);
            if (existing is Item item)
                item.Delete();
            else if (existing is Character ch)
                ch.Delete();
            replaced++;
        }

        if (replaced > 0)
            _logger.LogInformation("Runtime restore pre-replaced {Count} existing object(s)", replaced);
        return replaced;
    }

    private static List<ObjBase> CollectExistingWorldRecords(GameWorld world, IReadOnlyList<Serial> restoreSerials)
    {
        var existing = new List<ObjBase>();
        foreach (var serial in restoreSerials)
        {
            var obj = world.FindObject(serial);
            if (obj != null)
                existing.Add(obj);
        }
        return existing;
    }

    private void TryRollbackRestore(GameWorld world, string backupPath, IReadOnlyList<Serial> restoreSerials,
        AccountManager? accounts, Exception originalException)
    {
        _logger.LogWarning(originalException,
            "Runtime restore failed after replace; rolling back {Count} serial(s) from {Backup}",
            restoreSerials.Count, Path.GetFileName(backupPath));

        try
        {
            ReplaceExistingWorldRecords(world, restoreSerials);
            var (items, chars) = LoadFile(world, backupPath, accounts);
            _logger.LogInformation("Runtime restore rollback complete: {Items} item(s), {Chars} char(s)",
                items, chars);
        }
        catch (Exception rollbackEx)
        {
            _logger.LogError(rollbackEx, "Runtime restore rollback failed");
        }
    }

    private int LoadCharFile(GameWorld world, string path, List<(Character, string)> accountLinks,
        List<(Character, Serial, byte)> equipLinks)
    {
        int count = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            if (!ParseSectionType(section, "WORLDCHAR", out string? defname))
            {
                while (reader.NextProperty(out _, out _)) { /* skip */ }
                continue;
            }

            var ch = world.CreateCharacter();
            string? accountName = null;
            bool charHasUuid = false;
            bool skipChar = false;

            if (defname != null && ApplyCharDefFromName != null)
            {
                ApplyCharDefFromName(ch, defname);
            }
            else if (defname != null && ResolveCharDef != null)
            {
                ushort bodyId = ResolveCharDef(defname);
                if (bodyId != 0)
                {
                    ch.BodyId = bodyId;
                    ch.OBody = bodyId;
                    ch.BaseId = bodyId;
                }
                else
                    _logger.LogDebug("Unknown char defname '{DefName}' — BodyId stays default", defname);
            }

            while (reader.NextProperty(out string key, out string val))
            {
                if (key.Equals("SERIAL", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseHexOrDec(val, out uint serial))
                    {
                        var oldUid = ch.Uid;
                        var newUid = new Serial(serial);
                        if (!world.TryReRegisterObject(ch, oldUid, newUid, out var existing))
                        {
                            _logger.LogWarning(
                                "Skipping duplicate character serial 0x{Serial:X8} in {File}: existing={ExistingType}",
                                serial, Path.GetFileName(path), existing?.GetType().Name ?? "unknown");
                            world.DeleteObject(ch);
                            skipChar = true;
                        }
                    }
                    continue;
                }

                if (key.Equals("UUID", StringComparison.OrdinalIgnoreCase))
                {
                    if (Guid.TryParse(val, out Guid uuid))
                    {
                        var oldUuid = ch.Uuid;
                        ch.Uuid = uuid;
                        if (!world.TryReIndexUuid(ch, oldUuid, out var existing))
                        {
                            _logger.LogWarning(
                                "Skipping duplicate character UUID {Uuid} for serial 0x{Serial:X8} in {File}: existing=0x{Existing:X8}",
                                uuid, ch.Uid.Value, Path.GetFileName(path), existing?.Uid.Value ?? 0);
                            world.DeleteObject(ch);
                            skipChar = true;
                        }
                        charHasUuid = true;
                    }
                    continue;
                }

                if (key.Equals("ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("TAG.ACCOUNT", StringComparison.OrdinalIgnoreCase))
                {
                    accountName = val;
                    if (!key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase))
                        ch.SetTag("ACCOUNT", val);
                    continue;
                }

                if (key.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (skipChar)
                    continue;

                if (TryParseEquipProperty(key, val, out var itemSerial, out byte layer))
                {
                    equipLinks.Add((ch, itemSerial, layer));
                    continue;
                }

                ApplyCharProperty(ch, key, val);
            }
            if (skipChar)
                continue;

            CharDefHelper.EnsureDisplayBody(ch, DefinitionLoader.StaticResources);
            ch.ClearTransientVisualState();

            if (!charHasUuid)
                _migratedUuids++;

            world.PlaceCharacter(ch, ch.Position);
            if (!string.IsNullOrEmpty(accountName))
                accountLinks.Add((ch, accountName));
            count++;

            if (count % 50_000 == 0)
                _logger.LogInformation("  Loading chars... {Count} ({Elapsed}s)",
                    count, sw.Elapsed.TotalSeconds.ToString("F1"));
        }
        return count;
    }

    private void ApplyItemProperty(Item item, string key, string val)
    {
        switch (key.ToUpperInvariant())
        {
            case "ID":
                if (TryParseHexOrDec(val, out uint id))
                    item.BaseId = (ushort)id;
                break;
            case "TYPE":
                item.TrySetProperty(key, val);
                break;
            case "AMOUNT":
                if (ushort.TryParse(val, out ushort a))
                    item.Amount = a;
                break;
            case "CONT":
                if (TryParseHexOrDec(val, out uint cont))
                    item.ContainedIn = new Serial(cont);
                break;
            default:
                if (!item.TrySetProperty(key, val))
                    item.SetTag("SAVE." + key, val);
                break;
        }
    }

    private void ApplyCharProperty(Character ch, string key, string val)
    {
        string upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "BODY":
                if (TryParseHexOrDec(val, out uint body))
                {
                    ushort bid = (ushort)body;
                    // Saves sometimes store CHARDEF hash (e.g. 0x03DB) in BODY.
                    if (ch.CharDefIndex != 0 && bid == (ushort)ch.CharDefIndex)
                        break;
                    ch.BodyId = bid;
                }
                break;
            case "CHARDEFINDEX":
                if (TryParseHexOrDec(val, out uint cdi))
                    ch.CharDefIndex = (int)cdi;
                break;
            case "OBODY":
                if (TryParseHexOrDec(val, out uint obody))
                    ch.OBody = (ushort)obody;
                break;
            case "OSKIN":
                if (TryParseHexOrDec(val, out uint oskin))
                    ch.OSkin = (ushort)oskin;
                break;
            case "ISPLAYER":
                ch.IsPlayer = val == "1";
                break;
            case "OSTR":
                if (short.TryParse(val, out short ostr))
                    ch.OStr = ostr;
                break;
            case "ODEX":
                if (short.TryParse(val, out short odex))
                    ch.ODex = odex;
                break;
            case "OINT":
                if (short.TryParse(val, out short oint))
                    ch.OInt = oint;
                break;
            case "SPELLEFFECT":
                ch.AddPendingSpellEffectRecord(val);
                break;
            default:
                if (upper.StartsWith("SKILL[", StringComparison.Ordinal) && upper.Contains(']'))
                {
                    var idx = upper.IndexOf('[');
                    var end = upper.IndexOf(']');
                    if (int.TryParse(upper.AsSpan(idx + 1, end - idx - 1), out int skillIdx)
                        && skillIdx >= 0 && skillIdx < (int)SphereNet.Core.Enums.SkillType.Qty)
                    {
                        var parts = val.Split(',');
                        if (parts.Length >= 1 && ushort.TryParse(parts[0], out ushort sv))
                            ch.SetSkill((SphereNet.Core.Enums.SkillType)skillIdx, sv);
                        if (parts.Length >= 2 && byte.TryParse(parts[1], out byte lockVal))
                            ch.SetSkillLock((SphereNet.Core.Enums.SkillType)skillIdx, lockVal);
                    }
                    break;
                }
                if (upper.StartsWith("EQUIP[", StringComparison.Ordinal))
                {
                    break;
                }
                if (upper == "MEMORY")
                {
                    var parts = val.Split(',');
                    if (parts.Length >= 2 && TryParseHexOrDec(parts[0], out uint mUid) &&
                        ushort.TryParse(parts[1], out ushort mFlags))
                    {
                        var mem = ch.Memory_CreateObj(new Serial(mUid), (SphereNet.Core.Enums.MemoryType)mFlags);
                        // Extended format (W-D): remaining timeout ms (-1 = none)
                        // + original unix creation stamp. Older 2-field saves keep
                        // the fresh timeout CreateObj armed.
                        if (parts.Length >= 3 && long.TryParse(parts[2], out long memRemainMs))
                        {
                            if (memRemainMs < 0)
                                mem.SetTimeout(-1);
                            else
                                mem.SetTimeout(Environment.TickCount64 + Math.Max(1, memRemainMs));
                        }
                        if (parts.Length >= 4 && uint.TryParse(parts[3], out uint memCreated) && memCreated != 0)
                            mem.More1 = memCreated;
                    }
                    break;
                }
                if (upper == "ATTACKER")
                {
                    var parts = val.Split(',');
                    if (parts.Length >= 2 && TryParseHexOrDec(parts[0], out uint aUid) &&
                        int.TryParse(parts[1], out int aDamage))
                    {
                        bool ignored = parts.Length >= 3 && parts[2] == "1";
                        ch.CombatState.RestoreAttacker(new Serial(aUid), aDamage, ignored);
                    }
                    break;
                }
                if (TryResolveSkillName(upper, out SkillType skill))
                {
                    if (ushort.TryParse(val, out ushort sv))
                        ch.SetSkill(skill, sv);
                    break;
                }
                if (!ch.TrySetProperty(key, val))
                    ch.SetTag("SAVE." + key, val);
                break;
        }
    }

    private static bool TryParseEquipProperty(string key, string value, out Serial itemSerial, out byte layer)
    {
        itemSerial = Serial.Invalid;
        layer = 0;

        string upper = key.ToUpperInvariant();
        if (!upper.StartsWith("EQUIP[", StringComparison.Ordinal) || !upper.EndsWith(']'))
            return false;

        if (!byte.TryParse(upper.AsSpan("EQUIP[".Length, upper.Length - "EQUIP[".Length - 1), out layer))
            return false;

        if (!TryParseHexOrDec(value, out uint serial) || serial == 0)
            return false;

        itemSerial = new Serial(serial);
        return true;
    }

    private void LoadDataFile(GameWorld world, string path)
    {
        if (!File.Exists(path)) return;
        using var reader = Formats.SaveIO.OpenReader(path);
        int globals = 0, lists = 0;

        while (reader.NextRecord(out string section))
        {
            string upper = section.ToUpperInvariant();

            if (upper == "GLOBALS")
            {
                while (reader.NextProperty(out string key, out string val))
                {
                    world.SetGlobalVar(key, val);
                    globals++;
                }
            }
            else if (upper.StartsWith("LIST ", StringComparison.OrdinalIgnoreCase))
            {
                string listName = section[5..].Trim();
                var list = world.GetOrCreateList(listName);
                while (reader.NextProperty(out string key, out string val))
                {
                    if (key.Equals("ELEM", StringComparison.OrdinalIgnoreCase))
                        list.Add(val);
                }
                lists++;
            }
            else if (upper.StartsWith("GMPAGE", StringComparison.OrdinalIgnoreCase))
            {
                string account = "", reason = "", handler = "", status = "open";
                long created = 0;
                while (reader.NextProperty(out string key, out string val))
                {
                    switch (key.ToUpperInvariant())
                    {
                        case "ACCOUNT": account = val; break;
                        case "REASON": reason = val; break;
                        case "HANDLER": handler = val; break;
                        case "STATUS": status = val; break;
                        case "TIME": case "CREATED": long.TryParse(val, out created); break;
                    }
                }
                world.AddGmPage(new GameWorld.GmPageRecord(account, reason, handler, status, created));
            }
            else if (upper == "DOORS")
            {
                while (reader.NextProperty(out string key, out string val))
                {
                    if (key.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = val.Split(',');
                        if (parts.Length == 4 &&
                            byte.TryParse(parts[0], out byte dMap) &&
                            short.TryParse(parts[1], out short dX) &&
                            short.TryParse(parts[2], out short dY) &&
                            sbyte.TryParse(parts[3], out sbyte dZ))
                        {
                            world.SetMapStaticDoorOpen(dMap, dX, dY, dZ, true);
                        }
                    }
                }
            }
            else if (upper == "SECTORS")
            {
                while (reader.NextProperty(out string key, out string val))
                {
                    if (!key.Equals("ENV", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parts = val.Split(',');
                    if (parts.Length >= 8 &&
                        int.TryParse(parts[0], out int sMap) &&
                        int.TryParse(parts[1], out int sX) &&
                        int.TryParse(parts[2], out int sY) &&
                        byte.TryParse(parts[3], out byte sWeather) &&
                        byte.TryParse(parts[4], out byte sSeason) &&
                        byte.TryParse(parts[5], out byte sLight) &&
                        short.TryParse(parts[6], out short sRain) &&
                        short.TryParse(parts[7], out short sCold))
                    {
                        var sector = world.GetSector(sMap, sX, sY);
                        if (sector != null)
                        {
                            sector.Weather = sWeather;
                            sector.Season = sSeason;
                            sector.Light = sLight;
                            sector.RainChance = sRain;
                            sector.ColdChance = sCold;
                        }
                    }
                }
            }
            else if (upper.StartsWith("WORLDSCRIPT ", StringComparison.OrdinalIgnoreCase))
            {
                string scriptName = section[12..].Trim();
                while (reader.NextProperty(out string key, out string val))
                {
                    world.SetGlobalVar($"SCRIPT.{scriptName}.{key}", val);
                }
            }
            else if (upper == "SPHERE")
            {
                while (reader.NextProperty(out string key, out string val))
                {
                    if (key.Equals("VERSION", StringComparison.OrdinalIgnoreCase))
                        _logger.LogInformation("Save file version: {Version}", val);
                }
            }
            else
            {
                // TIMERF, EOF — skip properties
                while (reader.NextProperty(out _, out _)) { }
            }
        }

        if (globals > 0 || lists > 0)
            _logger.LogInformation("Data: {Path} -> {Globals} globals, {Lists} lists",
                Path.GetFileName(path), globals, lists);
    }

    /// <summary>Check whether <paramref name="section"/> matches
    /// <paramref name="expectedType"/> (case-insensitive), handling both
    /// SphereNet bare headers (<c>WORLDITEM</c>) and classic Sphere headers
    /// with a defname suffix (<c>WORLDITEM i_backpack</c>).</summary>
    private static bool ParseSectionType(string section, string expectedType, out string? defname)
    {
        defname = null;
        if (section.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
            return true;
        if (section.StartsWith(expectedType, StringComparison.OrdinalIgnoreCase)
            && section.Length > expectedType.Length
            && section[expectedType.Length] == ' ')
        {
            defname = section[(expectedType.Length + 1)..].Trim();
            if (defname.Length == 0) defname = null;
            return true;
        }
        return false;
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

    private static bool TryResolveSkillName(string upper, out SkillType skill)
    {
        if (_sphereSkillNames.TryGetValue(upper, out skill))
            return true;
        if (Enum.TryParse(upper, true, out skill) && Enum.IsDefined(skill))
            return true;
        skill = SkillType.None;
        return false;
    }

    private static readonly Dictionary<string, SkillType> _sphereSkillNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EVALUATINGINTEL"] = SkillType.EvalInt,
        ["EVALUATINGINTELLECT"] = SkillType.EvalInt,
        ["ITEMID"] = SkillType.ItemId,
        ["ITEMIDENTIFICATION"] = SkillType.ItemId,
        ["MACEFIGHTING"] = SkillType.MaceFighting,
        ["ANIMALLORE"] = SkillType.AnimalLore,
        ["ARMSLOREBOWCRAFT"] = SkillType.Bowcraft,
        ["DETECTINGHIDDEN"] = SkillType.DetectingHidden,
        ["MAGICRESISTANCE"] = SkillType.MagicResistance,
        ["SPIRITSPEAK"] = SkillType.SpiritSpeak,
        ["TASTEID"] = SkillType.TasteId,
        ["REMOVETRAP"] = SkillType.RemoveTrap,
    };
}
