using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every KEY a real pack ASSIGNS is one something accepts.
///
/// The third shape a script line can take, and the last one unmeasured. The member
/// sweep reads OBJ.NAME, the bare-verb sweep runs NAME on a line of its own, and
/// this one covers NAME=value - which is how a pack sets almost everything, and
/// exactly the shape behind the ore table that came out of the ground colourless
/// (COLOR=&lt;defname&gt; under @Create, accepted by nothing).
///
/// An assignment nothing accepts is the quietest failure of the three: the verb
/// order ends at the property write (CScriptObj.cpp:1481), so the line is consumed,
/// returns success, and changes nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptPackAssignedKeyCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
        "oldSphere/Scripts-X-main",
    ];

    private static readonly Regex SectionHeader = new(@"^\s*\[\s*([A-Za-z_]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TriggerHeader = new(@"^\s*ON\s*=\s*@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Assignment = new(
        @"^[ \t]*([A-Za-z_][A-Za-z0-9_.]*)\s*(\+=|-=|\*=|/=|=)(?!=)(.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FunctionHeader = new(
        @"^\s*\[\s*FUNCTION\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Object references that may PREFIX a key, stripped before the key is
    /// read: SRC.COLOR=1 assigns COLOR, not SRC.</summary>
    private static readonly HashSet<string> RefHeads =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SRC", "I", "TOPOBJ", "NEW", "ACT", "CONT", "ARGO", "LINK", "TARG", "OBJ",
        };

    /// <summary>REF1 through REF100 - the packs number them far past the handful a
    /// list could hold, and an unlisted one made REF99.NAME=x look like a key called
    /// REF99.</summary>
    private static readonly Regex NumberedRef = new(@"^REF\d+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool IsRefHead(string name) =>
        RefHeads.Contains(name) || NumberedRef.IsMatch(name);

    /// <summary>Namespaced STORES rather than object keys. TAG.x, LOCAL.x, VAR.x and
    /// the trigger argument objects take any name by design, so asking whether the
    /// engine "accepts" the name is not a question about coverage.</summary>
    private static readonly HashSet<string> Namespaces =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "TAG", "TAG0", "CTAG", "CTAG0", "DTAG", "LOCAL", "DLOCAL", "VAR", "VAR0",
            "EVAL", "DEF", "DEF0", "ARGN", "ARGN1", "ARGN2", "ARGN3", "ARGS", "ARGTXT",
            "ARGV", "SERV", "ACCOUNT", "LIST", "DB", "LDB", "FILE", "TAGAT", "REGION",
            "RESOURCES", "SKILLMENU", "MENU", "DIALOG",
            // float.x = <floatval ...> is a floating-point LOCAL store,
            // not a key on any object.
            "FLOAT",
        };

    /// <summary>Accepted, but not by an OBJECT. CONTAINER is a create-header key -
    /// it names the container a template's loot is placed into and is read by the
    /// definition loader (DefinitionLoader.cs:389), never written to an instance - so
    /// asking an object whether it accepts CONTAINER asks the wrong thing.</summary>
    private static readonly HashSet<string> NotProbeable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CONTAINER",
        };

    /// <summary>Keys a real pack assigns that nothing accepts. Every line assigning
    /// one is consumed and changes nothing. The assertions below fail when a name
    /// joins this set AND when one leaves it.
    ///
    /// All thirteen are in the reference key tables, so each is a real difference
    /// from the reference. They are listed rather than implemented because in THIS
    /// engine there is nothing on the other side of them: no code reads the value,
    /// and the packs that write them never read them back either (checked - OWNEDBY
    /// and LOWERREQ are read once and twice respectively, the rest not at all).
    ///
    /// Storing them would turn this list green and change nothing that happens in
    /// the game - which is the exact shape of bug these sweeps exist to find, so
    /// doing it to satisfy the sweep would be scoring our own exam. Each becomes
    /// worth implementing the day something consumes it: BREATH when a creature
    /// breathes, RARITY and SELFREPAIR and the BONUSSKILL pair when the item systems
    /// that read them exist, MODAC when armour class has a modifier term, ONAME when a
    /// renamed object must remember what it was.
    ///
    /// Contrast the six stat bonuses that came out of this same sweep and WERE
    /// implemented: CombatEngine had been summing them off worn items all along, so
    /// accepting the write completed a path that already existed.</summary>
    private static readonly HashSet<string> KnownUnaccepted =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "BREATH", "BonusSkill1", "BonusSkill1Amt", "DUPEITEM",
            "MODAC", "Rarity", "RESDISPDNHUE",
            "SelfRepair",
        };

    // ID left on the same terms: an item re-bases onto another ITEMDEF, which is what
    // upstream's SetID does (SetBaseID sets the base and the type, CItem.cpp:2128) and
    // what the pack's decorations and levers ask for in their TIMER, DCLICK, STEP and
    // EQUIP bodies. The consumer is the item itself - what it looks like, what type it
    // is, which definition its keys read from - not a system still to be written.

    // FRUIT left this list when its consumer turned out to already exist: the harvest
    // reads MORE2 as the plant's fruit override, which is where upstream keeps it
    // (m_itCrop.m_ridFruitOverride, CItem.cpp:3404). Only the key to write it was
    // missing, so the palms and crop fields in the pack fell back to their
    // definition's fruit - the condition this note names was met, not worked around.

    // LOWERREQ left this list when the component-property tables were wired up: it is
    // one of upstream's ADDPROP names, so an instance write lands in a tag and reads
    // back, the same as the resists beside it. Nothing consumes it yet - that part of
    // the note above still stands - but the write is no longer refused.

    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => null;
    }

    private static string ResolveRoot(string root)
    {
        if (Path.IsPathRooted(root)) return root;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "oldSphere")))
            dir = dir.Parent;
        return dir == null ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(dir.FullName, root));
    }

    private static List<string> PackFiles()
    {
        var all = new List<string>();
        foreach (string root in PackRoots)
        {
            string full = ResolveRoot(root);
            if (!Directory.Exists(full)) continue;
            try
            {
                all.AddRange(Directory.EnumerateFiles(full, "*.scp", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}_incomplete{Path.DirectorySeparatorChar}",
                                            StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException) { }
        }
        return all;
    }

    private static string StripComment(string line)
    {
        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] != '/' || line[i + 1] != '/') continue;
            if (i > 0 && line[i - 1] == ':') continue;
            return line[..i];
        }
        return line;
    }

    private static HashSet<string> PackFunctions(List<string> files)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (IOException) { continue; }
            foreach (Match m in FunctionHeader.Matches(text))
                names.Add(m.Groups[1].Value);
        }
        return names;
    }

    /// <summary>Does anything accept a write to this key? The property surface of a
    /// character, an item, a multi with a house and a spawner - the four shapes whose
    /// key sets differ - and failing those, the verb tables, because the interpreter
    /// tries a verb BEFORE it falls back to a property write.</summary>
    private static bool EngineAccepts(GameWorld world, ITextConsole console, string key)
    {
        foreach (string value in new[] { "1", "0" })
        {
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            try { if (ch.TrySetProperty(key, value)) return true; } catch { return true; }

            var npc = world.CreateCharacter();
            world.PlaceCharacter(npc, new Point3D(104, 100, 0, 0));
            try { if (npc.TrySetProperty(key, value)) return true; } catch { return true; }

            var it = world.CreateItem();
            it.BaseId = 0x0EED;
            world.PlaceItem(it, new Point3D(101, 100, 0, 0));
            try { if (it.TrySetProperty(key, value)) return true; } catch { return true; }

            var multi = world.CreateItem();
            multi.BaseId = 0x4064;
            multi.ItemType = ItemType.Multi;
            world.PlaceItem(multi, new Point3D(103, 100, 0, 0));
            var house = new SphereNet.Game.Housing.House(multi);
            Item.ResolveHouse = u => u == multi.Uid ? house : null;
            try { if (multi.TrySetProperty(key, value)) return true; } catch { return true; }

            var spawner = world.CreateItem();
            spawner.ItemType = ItemType.SpawnChar;
            world.PlaceItem(spawner, new Point3D(105, 100, 0, 0));
            spawner.InitializeSpawnComponent(world, null);
            try { if (spawner.TrySetProperty(key, value)) return true; } catch { return true; }

            // A verb wins over a property write in the interpreter's order, so a key
            // some verb table owns is accepted even if no property does. The MULTI is
            // asked too: ADDCOOWNER and its neighbours are verbs on a house, and
            // asking only a plain item reported the whole housing surface as missing.
            try { if (ch.TryExecuteCommand(key, value, console, out bool o1) || o1) return true; }
            catch { return true; }
            try { if (it.TryExecuteCommand(key, value, console, out bool o2) || o2) return true; }
            catch { return true; }
            try { if (multi.TryExecuteCommand(key, value, console, out bool o3) || o3) return true; }
            catch { return true; }
            // And the SPAWNER's own table: DELOBJ is written as an assignment
            // (REF1.DelObj=<LINK>) and is a spawner verb, not a property.
            try { if (spawner.TryExecuteCommand(key, value, console, out bool o4) || o4) return true; }
            catch { return true; }
            try { if (console.TryExecuteScriptCommand(ch, key, value, null)) return true; }
            catch { return true; }
        }
        return false;
    }

    [Fact]
    public void EveryKeyARealPackAssignsIsOneSomethingAccepts()
    {
        var files = PackFiles();
        if (Gate.Missing(outp, "live script pack", files.Count == 0)) return;

        var seen = new Dictionary<string, (int Uses, string File)>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }

            bool inBody = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = StripComment(raw).TrimEnd('\r');
                var header = SectionHeader.Match(line);
                if (header.Success)
                {
                    inBody = header.Groups[1].Value.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (TriggerHeader.IsMatch(line)) { inBody = true; continue; }
                if (!inBody || line.Trim().Length == 0) continue;

                var m = Assignment.Match(line);
                if (!m.Success) continue;
                string lhs = m.Groups[1].Value;
                if (lhs.Contains('<') || lhs.Contains('>')) continue;

                var parts = lhs.Split('.').ToList();
                while (parts.Count > 1 && IsRefHead(parts[0]))
                    parts.RemoveAt(0);
                // FINDID.<defname> resolves to an ITEM and the assignment lands on
                // that item, so the key is what follows the pair - not "FINDID".
                while (parts.Count > 2 &&
                       parts[0].Equals("FINDID", StringComparison.OrdinalIgnoreCase))
                {
                    parts.RemoveRange(0, 2);
                    while (parts.Count > 1 && IsRefHead(parts[0]))
                        parts.RemoveAt(0);
                }
                string name = parts[0];
                if (Namespaces.Contains(name) || IsRefHead(name)) continue;

                var prev = seen.TryGetValue(name, out var had) ? had : (Uses: 0, File: Path.GetFileName(file));
                seen[name] = (prev.Uses + 1, prev.File);
            }
        }

        var functions = PackFunctions(files);
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        // A stone answers ABBREV, ALIGN and MASTERUID only when a guild record is
        // behind it; with none, the whole stone surface reads as missing.
        var probeGuild = new SphereNet.Game.Guild.GuildDef(new Serial(1));
        Item.ResolveGuild = _ => probeGuild;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 4803);
        var driver = world.CreateCharacter();
        driver.IsPlayer = true;
        driver.PrivLevel = PrivLevel.Owner;
        world.PlaceCharacter(driver, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, driver);

        var unaccepted = new List<(string Name, int Uses, string File)>();
        foreach (var (name, info) in seen)
        {
            if (functions.Contains(name)) continue;
            if (NotProbeable.Contains(name)) continue;
            if (EngineAccepts(world, client, name)) continue;
            unaccepted.Add((name, info.Uses, info.File));
        }

        outp.WriteLine($"{files.Count} files, {seen.Count} distinct assigned keys, " +
                       $"{unaccepted.Count} unaccepted");
        foreach (var (name, uses, file) in unaccepted.OrderByDescending(u => u.Uses))
            outp.WriteLine($"UNACCEPTED {name} x{uses} (first: {file})");

        Assert.True(seen.Count >= 100, $"expected a real pack key variety, saw {seen.Count}");

        var surprises = unaccepted.Select(u => u.Name)
            .Where(n => !KnownUnaccepted.Contains(n)).OrderBy(n => n).ToList();
        Assert.True(surprises.Count == 0,
            "nothing accepts these, so every line assigning them changes nothing: " +
            string.Join(", ", surprises));

        var fixedUp = KnownUnaccepted
            .Where(n => !unaccepted.Any(u => u.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n).ToList();
        Assert.True(fixedUp.Count == 0,
            "these are accepted now - take them out of the baseline: " + string.Join(", ", fixedUp));
    }
}
