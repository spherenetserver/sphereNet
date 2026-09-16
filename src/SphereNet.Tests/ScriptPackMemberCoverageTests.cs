using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every member a real pack calls on an object is one the engine answers.
///
/// A pack states what it needs by writing SRC.FOO. If nothing answers FOO the
/// line is a silent no-op - no error, no log, the behaviour simply never happens -
/// which is how a stuck timer can fail to freeze anybody and nobody finds out.
///
/// Names are gathered STATICALLY (a member name is unambiguous) and resolved
/// DYNAMICALLY by asking the engine itself: TryGetProperty for reads,
/// TryExecuteCommand for verbs, and the acting client verb table - the chain the
/// interpreter walks - plus the pack FUNCTION blocks. Grepping the C# for the name
/// instead makes the measurement lie: CTAG0, FINDID and ISEVENT are all answered
/// and none appears as a quoted literal anywhere in the engine.
/// </summary>
public sealed class ScriptPackMemberCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
    ];

    /// <summary>Object references a pack writes a member on. SERV is deliberately
    /// absent: its members resolve through the host server-property table, which a
    /// bare world cannot reach.</summary>
    private static readonly Regex MemberCall = new(
        @"\b(?:SRC|TOPOBJ|NEW|ACT|CONT|ARGO|LINK|TARG|I)\.([A-Za-z_][A-Za-z0-9_]*)(\.?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>CultureInvariant is load-bearing: .NET IgnoreCase follows the CURRENT
    /// culture, and in Turkish the lower case of I is not i. On this machine the
    /// pattern FUNCTION did not match the lowercase spelling, so a quarter of the
    /// pack own function definitions went uncounted - which surfaced as a list of
    /// gaps that were not gaps.</summary>
    private static readonly Regex FunctionHeader = new(
        @"^\s*\[\s*FUNCTION\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Members a real pack calls that nothing answers and no FUNCTION
    /// defines. Each is a line that does nothing at all. The assertions below fail
    /// when a name joins this set AND when one leaves it.</summary>
    private static readonly HashSet<string> KnownUnanswered =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ARMOR", "ATTACKER", "CANMAKE", "CANMAKESKILL",
            "DAM", "DEBUG", "DETAIL", "DISTANCE",
            "FLAGSIL", "FUNC_DIALOGCLOSEALL", "FUNC_EMOTE_BONUS", "FUNC_GetChar_List",
            "FUNC_WARMODE", "F_HOUSE_NEAR_DOOR", "FlagEkle", "Func_NoGold_Msg",
            "Func_Server_All_Entities_PageBild_Char", "Func_Server_All_Entities_PageBild_Item", "HEARALL", "HouseDesign",
            "ISARMOR", "ISDISS", "ISINSAFE", "ISJAIL",
            "ISNEARTYPE", "ISNOMOVERFLAGS", "ISWEAPON", "LOG",
            "MOREM", "MOVETO", "NOTICE", "PAGE",
            "RESTEST", "SECTOR", "SKILLMENU", "SYSMESSSYSMESSAGELOC",
            "SendGMPage", "TARGPRV", "UOSOFT_CLIENT_LOGOUT", "VIRTUAL",
            "WEBPAGE", "abbrev", "align", "dmore2",
            "masteruid", "nototitle", "sys_red",
        };

    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => null;
    }

    /// <summary>Resolve a repo-relative pack root. Path.GetFullPath would resolve it
    /// against the TEST BIN directory, where no pack exists.</summary>
    private static string ResolveRoot(string root)
    {
        if (Path.IsPathRooted(root)) return root;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "oldSphere")))
            dir = dir.Parent;
        return dir == null ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(dir.FullName, root));
    }

    private static List<string> PackFiles(ITestOutputHelper? log = null)
    {
        var all = new List<string>();
        foreach (string root in PackRoots)
        {
            string full = ResolveRoot(root);
            if (!Directory.Exists(full)) { log?.WriteLine($"root missing: {full}"); continue; }
            int before = all.Count;
            try { all.AddRange(Directory.EnumerateFiles(full, "*.scp", SearchOption.AllDirectories)); }
            catch (Exception ex) { log?.WriteLine($"root unreadable: {full} ({ex.GetType().Name})"); }
            log?.WriteLine($"root {full}: {all.Count - before} files");
        }
        return all;
    }

    private static HashSet<string> PackFunctions(IEnumerable<string> files)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (IOException) { continue; }
            foreach (Match m in FunctionHeader.Matches(text))
                set.Add(m.Groups[1].Value);
        }
        return set;
    }

    /// <summary>Ask the ENGINE, on throwaway objects, the way the interpreter does.
    /// A verb runs for real, so the world is disposable; a throw counts as answered,
    /// because reaching a handler is the opposite of the silent no-op being sought.
    /// </summary>
    private static bool EngineAnswers(GameWorld world, ITextConsole console,
        string member, bool dotted)
    {
        // A dotted member is two different shapes and the suffix decides which:
        // FINDID.0 takes an ARGUMENT, while ACT.P dereferences to another object and
        // reads a property off it. One fixed suffix therefore answers only half of
        // them - .PROBE made every reference member look unanswered, including ACT,
        // which the pack uses sixteen hundred times. Any suffix answering is enough
        // to prove the member itself is understood.
        string[] keys = dotted
            ? [member + ".P", member + ".0", member + ".NAME", member + ".PROBE"]
            : [member];
        // A plausible argument: a key that needs one and gets none can refuse for
        // that reason alone and be counted as unanswered when it is not.
        const string arg = "1";

        foreach (string key in keys)
        {
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            // A reference member can only answer when it points at something. A
            // script writes src.act.p straight after SERV.NEWITEM, so probing with
            // ACT unset asks a question no engine could answer and reports the
            // sixteen hundred uses of ACT as a gap.
            var acted = world.CreateItem();
            acted.BaseId = 0x0EED;
            world.PlaceItem(acted, new Point3D(102, 100, 0, 0));
            try { ch.TrySetProperty("ACT", $"0{acted.Uid.Value:X}"); } catch { }
            try { if (ch.TryGetProperty(key, out _)) return true; } catch { return true; }
            try { if (ch.TryExecuteCommand(key, arg, console)) return true; } catch { return true; }

            var it = world.CreateItem();
            it.BaseId = 0x0EED;
            world.PlaceItem(it, new Point3D(101, 100, 0, 0));
            try { if (it.TryGetProperty(key, out _)) return true; } catch { return true; }
            try { if (it.TryExecuteCommand(key, arg, console)) return true; } catch { return true; }

            // The third link: the acting CLIENT own verbs (WEBLINK, DIALOG, TARGETF,
            // SENDPACKET...). Skipping it reports every console verb as a gap.
            try { if (console.TryExecuteScriptCommand(ch, key, arg, null)) return true; }
            catch { return true; }

            // The fourth: an object REFERENCE head. ACT.P is not a property named
            // "ACT.P" - the interpreter resolves ACT to another object and asks that
            // one for P (Interpreter.ResolveObjectRef -> ResolveRefHead). Asking
            // TryGetProperty alone therefore reports every reference head as a gap.
            int dot = key.IndexOf('.');
            if (dot > 0)
            {
                string head = key[..dot], rest = key[(dot + 1)..];
                try
                {
                    var target = ch.ResolveRefHead(head) ?? it.ResolveRefHead(head);
                    if (target != null && (target.TryGetProperty(rest, out _) ||
                                           target.TryExecuteCommand(rest, arg, console)))
                        return true;
                }
                catch { return true; }
            }
        }

        return false;
    }

    [Fact]
    public void EveryMemberARealPackCallsIsOneTheEngineAnswers()
    {
        var files = PackFiles(outp);
        if (Gate.Missing(outp, "live script pack", files.Count == 0)) return;

        // name -> uses, and whether it was ever written bare / ever written dotted.
        // SRC.P and SRC.P.X are different questions; answering one does not answer
        // the other, so a name used both ways is probed both ways.
        var seen = new Dictionary<string, (int Uses, bool Dotted, bool Bare, string File)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            foreach (Match m in MemberCall.Matches(text))
            {
                string name = m.Groups[1].Value;
                bool dotted = m.Groups[2].Value == ".";
                if (seen.TryGetValue(name, out var prev))
                    seen[name] = (prev.Uses + 1, prev.Dotted || dotted, prev.Bare || !dotted, prev.File);
                else
                    seen[name] = (1, dotted, !dotted, Path.GetFileName(file));
            }
        }

        var functions = PackFunctions(files);
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        // A real client, because its verb table is the one the interpreter reaches
        // through; a stub console answers nothing and would report every client verb
        // as a gap.
        var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 4801);
        var driver = world.CreateCharacter();
        driver.IsPlayer = true;
        driver.PrivLevel = PrivLevel.Owner;
        world.PlaceCharacter(driver, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, driver);

        var unanswered = new List<(string Name, int Uses, string File)>();
        foreach (var (name, info) in seen)
        {
            if (functions.Contains(name)) continue;
            if (info.Bare && EngineAnswers(world, client, name, false)) continue;
            if (info.Dotted && EngineAnswers(world, client, name, true)) continue;
            unanswered.Add((name, info.Uses, info.File));
        }

        outp.WriteLine($"{files.Count} files, {seen.Count} distinct members, " +
                       $"{functions.Count} pack functions, {unanswered.Count} unanswered");
        foreach (var (name, uses, file) in unanswered.OrderByDescending(u => u.Uses))
            outp.WriteLine($"UNANSWERED {name} x{uses} (first: {file})");

        Assert.True(seen.Count >= 100, $"expected a real pack member variety, saw {seen.Count}");
        Assert.True(functions.Count >= 800,
            $"only {functions.Count} pack functions found - the collector is under-counting");

        var surprises = unanswered.Select(u => u.Name)
            .Where(n => !KnownUnanswered.Contains(n)).ToArray();
        Assert.True(surprises.Length == 0,
            "nothing answers these, so every line calling them does nothing: " +
            string.Join(", ", surprises));

        var answeredNow = KnownUnanswered
            .Where(k => !unanswered.Any(u => u.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.True(answeredNow.Length == 0,
            "these are answered now - remove them from KnownUnanswered: " +
            string.Join(", ", answeredNow));
    }
}
