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
/// Every VERB a real pack writes as a bare statement is one something owns.
///
/// The member sweep beside this one measures <c>OBJ.NAME</c> - a name written on an
/// object. It cannot see a verb written on a line of its own, and most verbs are:
/// SOUND, UPDATE, REMOVE, TIMERF, EVENTS. That blind spot is not theoretical - it
/// hid eleven of the sixteen NPC action verbs, of which only LEAVE was ever written
/// as a member and so only LEAVE was ever reported.
///
/// Same method as the member sweep, because it is the one that survived calibration:
/// collect STATICALLY (the first token of a statement is unambiguous) and resolve
/// DYNAMICALLY by asking the engine - the object verb tables, the acting client's
/// table, and the pack's own [FUNCTION] blocks, which is the order the interpreter
/// walks.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptPackBareVerbCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
        "oldSphere/Scripts-X-main",
    ];

    /// <summary>Only a [FUNCTION] body and an ON=@Trigger body hold verb lines. A
    /// [DIALOG] body is a different language - button, dhtmlgump, resizepic are gump
    /// layout, not verbs - and a [COMMENT] or [BOOK] body is English prose. Scanning
    /// everything reported eighteen thousand "verbs", most of them words.</summary>
    private static readonly Regex SectionHeader = new(@"^\s*\[\s*([A-Za-z_]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TriggerHeader = new(@"^\s*ON\s*=\s*@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A property assignment is not a verb line.</summary>
    private static readonly Regex Assignment = new(@"^\s*[A-Za-z_][A-Za-z0-9_.\[\]<>]*\s*(\+=|-=|=)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Statement = new(@"^[ \t]*([A-Za-z_][A-Za-z0-9_]*)([ \t]+(\S.*)?)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FunctionHeader = new(
        @"^\s*\[\s*FUNCTION\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Script CONTROL FLOW, which owns its names in the interpreter rather
    /// than in any verb table. A name here is not a gap however it resolves.</summary>
    private static readonly HashSet<string> ControlFlow =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "IF", "ELIF", "ELSE", "ELSEIF", "ENDIF", "FOR", "ENDFOR", "WHILE",
            "ENDWHILE", "ENDWH", "RETURN", "BEGIN", "END", "DORAND", "ENDDO",
            "DOSWITCH", "SWITCH", "ENDSWITCH", "CASE", "BREAK", "CONTINUE", "ON",
            "VERSION", "FORCHARS", "FORITEMS", "FORCLIENTS", "FORCONT", "FOROBJS",
            "FORPLAYERS", "FORCHARLAYER", "FORCHARMEMORYTYPE", "FORCHARMEMORYTYPES",
            "FORCONTID", "FORCONTTYPE", "FORCONTTYPES", "FORINSTANCES", "FORUID",
            "FORITEMSNEARBY", "ENDDORAND",
        };

    /// <summary>Owned by the INTERPRETER rather than by any verb table, so this
    /// probe - which asks the objects and the client - cannot see them.
    ///
    /// CALL, TRY and TRYSRV are script control (ScriptInterpreter :156, :226, :259);
    /// ARGS, ARGN1, ARGN2 and REF1 are the trigger argument objects, written bare to
    /// assign them (:866, :975, :999). Each was confirmed present at those lines
    /// before being listed here.</summary>
    private static readonly HashSet<string> NotProbeable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CALL", "TRY", "TRYSRV", "ARGS", "ARGN1", "ARGN2", "REF1",
        };

    /// <summary>Verbs a real pack writes that nothing owns. Every line writing one
    /// does nothing at all. The assertions below fail when a name joins this set AND
    /// when one leaves it.
    ///
    /// All four belong to the PACKS: FUNC_WARMODE, FUNC_GetChar_List,
    /// f_webdate_build_items and RANDMAGICITEM have no [FUNCTION] block in any of the
    /// three packs and no entry in the reference key tables. They are fixed by writing
    /// the missing function, not here.</summary>
    private static readonly HashSet<string> KnownUnanswered =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FUNC_WARMODE", "FUNC_GetChar_List", "f_webdate_build_items",
            "RANDMAGICITEM",
        };

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
                // _incomplete is the reference distribution's OWN marker for scripts
                // its authors say are not finished. Measuring the engine against them
                // measures work nobody claims is done.
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
            if (i > 0 && line[i - 1] == ':') continue;   // http://
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

    /// <summary>Does anything own this verb NAME? nameOwned, not the return value: a
    /// verb that recognises the name and refuses the probe's argument is not an
    /// unknown name, and the engine keeps the two apart for exactly this reason.</summary>
    private static bool EngineOwns(GameWorld world, ITextConsole console, string verb)
    {
        foreach (string arg in new[] { "", "1" })
        {
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            try { if (ch.TryExecuteCommand(verb, arg, console, out bool chOwned) || chOwned) return true; }
            catch { return true; }

            // An NPC too: a whole verb table belongs to NPCs alone.
            var npc = world.CreateCharacter();
            world.PlaceCharacter(npc, new Point3D(104, 100, 0, 0));
            try { if (npc.TryExecuteCommand(verb, arg, console, out bool npcOwned) || npcOwned) return true; }
            catch { return true; }

            var it = world.CreateItem();
            it.BaseId = 0x0EED;
            world.PlaceItem(it, new Point3D(101, 100, 0, 0));
            try { if (it.TryExecuteCommand(verb, arg, console, out bool itOwned) || itOwned) return true; }
            catch { return true; }

            // A MULTI with a live house behind it: the whole CItemMulti verb surface
            // is gated on the item BEING a house, and a house script writes DELBAN on
            // a bare line inside the multi's own trigger.
            // A SPAWNER. RESET, START, STOP and DELOBJ are gated on the item having
            // a spawn component, and a spawner script writes them bare inside its own
            // trigger - so a plain item refuses them for a reason that has nothing to
            // do with whether the engine implements them.
            var spawner = world.CreateItem();
            spawner.ItemType = ItemType.SpawnChar;
            world.PlaceItem(spawner, new Point3D(105, 100, 0, 0));
            spawner.InitializeSpawnComponent(world, null);
            try { if (spawner.TryExecuteCommand(verb, arg, console, out bool spOwned) || spOwned) return true; }
            catch { return true; }

            var multi = world.CreateItem();
            multi.BaseId = 0x4064;
            multi.ItemType = ItemType.Multi;
            world.PlaceItem(multi, new Point3D(103, 100, 0, 0));
            var house = new SphereNet.Game.Housing.House(multi);
            Item.ResolveHouse = u => u == multi.Uid ? house : null;
            try { if (multi.TryExecuteCommand(verb, arg, console, out bool mOwned) || mOwned) return true; }
            catch { return true; }

            try { if (console.TryExecuteScriptCommand(ch, verb, arg, null)) return true; }
            catch { return true; }

            // And the last link the interpreter walks: a bare line whose name no
            // verb table owns becomes a PROPERTY ASSIGNMENT (CScriptObj.cpp:1481).
            // "ATTR 040" and "MORE2 3" are ordinary Sphere, and asking only the verb
            // tables reported a dozen working lines as gaps.
            try { if (ch.TrySetProperty(verb, arg) || it.TrySetProperty(verb, arg)) return true; }
            catch { return true; }
        }
        return false;
    }

    [Fact]
    public void EveryBareVerbARealPackWritesIsOneSomethingOwns()
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
                if (Assignment.IsMatch(line)) continue;

                var m = Statement.Match(line);
                if (!m.Success) continue;
                string name = m.Groups[1].Value;
                if (ControlFlow.Contains(name)) continue;
                // A [FUNCTION] may emit dialog ROWS - d_admin_main prints its house
                // and event tables that way - so gump layout turns up inside function
                // bodies legitimately. The vocabulary is asked of the engine rather
                // than listed here, so the two cannot drift apart.
                if (SphereNet.Game.Clients.ClientDialogHandler.DialogRenderCommands.Contains(name))
                    continue;

                var prev = seen.TryGetValue(name, out var had) ? had : (Uses: 0, File: Path.GetFileName(file));
                seen[name] = (prev.Uses + 1, prev.File);
            }
        }

        var functions = PackFunctions(files);
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 4802);
        var driver = world.CreateCharacter();
        driver.IsPlayer = true;
        driver.PrivLevel = PrivLevel.Owner;
        world.PlaceCharacter(driver, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, driver);

        var unanswered = new List<(string Name, int Uses, string File)>();
        foreach (var (name, info) in seen)
        {
            if (functions.Contains(name)) continue;
            if (NotProbeable.Contains(name)) continue;
            if (EngineOwns(world, client, name)) continue;
            unanswered.Add((name, info.Uses, info.File));
        }

        outp.WriteLine($"{files.Count} files, {seen.Count} distinct bare verbs, " +
                       $"{functions.Count} pack functions, {unanswered.Count} unowned");
        foreach (var (name, uses, file) in unanswered.OrderByDescending(u => u.Uses))
            outp.WriteLine($"UNOWNED {name} x{uses} (first: {file})");

        Assert.True(seen.Count >= 100, $"expected a real pack verb variety, saw {seen.Count}");

        var surprises = unanswered.Select(u => u.Name)
            .Where(n => !KnownUnanswered.Contains(n)).OrderBy(n => n).ToList();
        Assert.True(surprises.Count == 0,
            "nothing owns these, so every line writing them does nothing: " +
            string.Join(", ", surprises));

        var fixedUp = KnownUnanswered
            .Where(n => !unanswered.Any(u => u.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n).ToList();
        Assert.True(fixedUp.Count == 0,
            "these are owned now - take them out of the baseline: " + string.Join(", ", fixedUp));
    }
}
