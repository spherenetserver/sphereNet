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
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptPackMemberCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
        // The REFERENCE distribution, written against the reference engine: a name
        // it calls and nothing answers is a gap by construction, not a shard's
        // private convention.
        "oldSphere/Scripts-X-main",
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

    /// <summary>Implemented, but this probe cannot demonstrate it.
    ///
    /// A CLIENT verb has no nameOwned signal - the object verb table has one
    /// precisely so "no such verb" and "that verb refused" stay apart, and the
    /// client side does not - so a client verb that validates its argument looks
    /// exactly like an unknown name here. SKILLMENU needs a loaded [SKILLMENU]
    /// section to succeed and there is none in a bare probe world; it is covered by
    /// MoveToAndSkillMenuVerbTests instead. Entries here are excluded from the sweep
    /// so nobody implements them a second time.
    ///
    /// SKILLCHECK needs TWO arguments and this probe can only offer one, so a key
    /// that parses its arguments correctly still refuses. HOUSEDESIGN and TARGPRV are
    /// reference HEADS that resolve to whatever they point at, and in a bare probe
    /// world they point at nothing - which is the correct answer, and indistinguishable
    /// from an unknown head. All three are covered by ReferencePackMemberGapTests.</summary>
    private static readonly HashSet<string> AnsweredButNotProbeable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SKILLMENU", "SKILLCHECK", "HouseDesign", "TARGPRV",
        };

    /// <summary>Members a real pack calls that nothing answers and no FUNCTION
    /// defines. Each is a line that does nothing at all. The assertions below fail
    /// when a name joins this set AND when one leaves it.
    ///
    /// Most of what is left belongs to the PACK rather than the engine. FlagEkle,
    /// FLAGSIL, ISJAIL, ISINSAFE, ISDISS, dmore2 and the FUNC_*/Func_* names have no
    /// definition anywhere - not in this engine, not in the reference tables and not
    /// in any [FUNCTION] block of any of the three packs - and SYSMESSSYSMESSAGELOC
    /// is a typo for SYSMESSAGELOC. f_lich_polymorph is the reference distribution's
    /// own dangling call: e_npcs.scp schedules it and nothing defines it. Those are
    /// fixed by writing the missing function or correcting the call, not here.
    ///
    /// LOCATION, MYNAME, PLACE, NOTICE, REMOVETIMER, VIRTUAL, LOG and
    /// UOSOFT_CLIENT_LOGOUT are names no reference table carries either: shard
    /// conventions that were never defined. (LOG exists upstream, but as a SERV verb -
    /// SERV.LOG works here; the pack writes SRC.LOG, which upstream would refuse too.)
    ///
    /// The engine gaps that remain, and why they are not one-line fixes:
    ///
    /// SendGMPage and F_HOUSE_NEAR_DOOR are pack functions the scripts expect to
    /// exist. (GMPAGEP itself is answered now, by GmPage.)
    ///
    /// "e" is the collector, not the engine: a book's page TEXT contains the English
    /// "i.e.", and prose inside a quoted string is not distinguishable from a member
    /// call by name alone. Comments are stripped; page text cannot be.</summary>
    private static readonly HashSet<string> KnownUnanswered =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FLAGSIL", "FUNC_DIALOGCLOSEALL", "FUNC_EMOTE_BONUS",
            "FUNC_GetChar_List", "FUNC_WARMODE", "F_HOUSE_NEAR_DOOR", "FlagEkle",
            "Func_NoGold_Msg", "Func_Server_All_Entities_PageBild_Char", "Func_Server_All_Entities_PageBild_Item",
            "ISDISS", "ISINSAFE", "ISJAIL",
            "LOCATION", "LOG", "MYNAME",
            "NOTICE", "PLACE", "REMOVETIMER", "SYSMESSSYSMESSAGELOC",
            "SendGMPage", "UOSOFT_CLIENT_LOGOUT", "VIRTUAL",
            "dmore2", "e", "f_lich_polymorph",
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
    /// <summary>Give the probe item a real ITEMDEF behind it.
    ///
    /// A whole family of item members answers only off the definition
    /// (CBaseBaseDef / CItemBase upstream: VALUE, RESOURCES, SKILLMAKE...), so an
    /// instance with nothing behind it refuses them for a reason that has nothing
    /// to do with whether the engine implements them - and the sweep then reports
    /// three implemented members as gaps.</summary>
    /// <summary>Drop the // comments before looking for calls.
    ///
    /// A commented-out line is not a call: the reference pack carries
    /// "// &lt;src.f_combatsys_hitspeed 3.0&gt;" as a note beside the live code, and
    /// English prose in a comment reads "i.e." as a member named e. Both were
    /// reported as gaps nothing answers, which is the sweep measuring itself. The
    /// "://" of a URL is left alone.</summary>
    private static string StripComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            int cut = -1;
            for (int i = 0; i + 1 < line.Length; i++)
            {
                if (line[i] != '/' || line[i + 1] != '/') continue;
                if (i > 0 && line[i - 1] == ':') continue;   // http://
                cut = i;
                break;
            }
            sb.Append(cut < 0 ? line : line[..cut]).Append('\n');
        }
        return sb.ToString();
    }

    private static void LoadProbeItemDef()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spn_probe_def_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "probe.scp");
        File.WriteAllText(file, """
            [ITEMDEF 0eed]
            DEFNAME=i_probe_def
            TYPE=t_normal
            VALUE=10
            WEIGHT=1
            RESOURCES=1 i_probe_def
            SKILLMAKE=Blacksmithing 50.0
            """);
        using var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(
            lf.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>())
        { ScpBaseDir = dir };
        resources.LoadResourceFile(file);
        new SphereNet.Game.Definitions.DefinitionLoader(
            resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static bool EngineAnswers(GameWorld world, ITextConsole console,
        string member, IEnumerable<string> suffixes)
    {
        // Probe with the suffixes the pack ITSELF wrote, not invented ones. A
        // dotted member is several different shapes - FINDID.0 takes an argument,
        // ACT.P dereferences to another object, ARMOR.LO names a half of a range -
        // and guessing the suffix asks a question the engine was never meant to
        // answer: .PROBE reported ACT as missing (sixteen hundred uses) and .P
        // reported ARMOR.LO as missing while the engine answers it. The space form
        // is probed too, because <SRC.CANMAKE i_dagger> reaches the engine as the
        // key "CANMAKE i_dagger" and probing the bare word alone reported CANMAKE
        // as a gap when it is implemented.
        var keyList = new List<string> { member, member + " 1" };
        foreach (string suffix in suffixes)
            keyList.Add(member + "." + suffix);
        string[] keys = [.. keyList];
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
            // nameOwned, not the return value: a verb that recognises the name and
            // then refuses the ARGUMENT is not an unknown name, and the engine keeps
            // the two apart for exactly the reason this probe needs them apart
            // (ObjBase.TryExecuteCommand). Judging by success alone reported MOVETO
            // and SKILLMENU as gaps - both implemented, both simply declining a
            // probe argument of "1".
            try { if (ch.TryExecuteCommand(key, arg, console, out bool chOwned) || chOwned) return true; }
            catch { return true; }

            // An NPC as well as the player. A whole verb table belongs to NPCs
            // alone - upstream's NPC dispatcher refuses on a player and the
            // interpreter keeps looking - so probing only a player reported every
            // one of them as a name nothing answers.
            var npc = world.CreateCharacter();
            world.PlaceCharacter(npc, new Point3D(104, 100, 0, 0));
            try { if (npc.TryGetProperty(key, out _)) return true; } catch { return true; }
            try { if (npc.TryExecuteCommand(key, arg, console, out bool npcOwned) || npcOwned) return true; }
            catch { return true; }

            var it = world.CreateItem();
            it.BaseId = 0x0EED;
            world.PlaceItem(it, new Point3D(101, 100, 0, 0));
            try { if (it.TryGetProperty(key, out _)) return true; } catch { return true; }
            try { if (it.TryExecuteCommand(key, arg, console, out bool itOwned) || itOwned) return true; }
            catch { return true; }

            // A MULTI with a live house behind it. The whole CItemMulti surface -
            // HOUSETYPE, ISOWNER, GETCOOWNERPOS, MOVINGCRATE, DELBAN, REDEED - is
            // gated on the item BEING a house, and refuses on a plain item for a
            // reason that has nothing to do with whether the engine implements it.
            // Probed as a second object rather than by making the one probe item a
            // multi, so the multi gate cannot hide a non-multi answer either.
            var multi = world.CreateItem();
            multi.BaseId = 0x4064;
            multi.ItemType = ItemType.Multi;
            world.PlaceItem(multi, new Point3D(103, 100, 0, 0));
            var house = new SphereNet.Game.Housing.House(multi);
            Item.ResolveHouse = u => u == multi.Uid ? house : null;
            try { if (multi.TryGetProperty(key, out _)) return true; } catch { return true; }
            try { if (multi.TryExecuteCommand(key, arg, console, out bool mOwned) || mOwned) return true; }
            catch { return true; }

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
                    var target = ch.ResolveRefHead(head) ?? it.ResolveRefHead(head) ?? multi.ResolveRefHead(head);
                    if (target != null && (target.TryGetProperty(rest, out _) ||
                                           target.TryExecuteCommand(rest, arg, console, out bool refOwned) ||
                                           refOwned))
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
        var seen = new Dictionary<string, (int Uses, HashSet<string> Suffixes, string File)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            foreach (Match m in MemberCall.Matches(StripComments(text)))
            {
                string name = m.Groups[1].Value;
                string suffix = m.Groups[2].Success ? m.Groups[2].Value : "";
                if (!seen.TryGetValue(name, out var prev))
                {
                    prev = (0, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            Path.GetFileName(file));
                }
                if (suffix.Length > 0 && prev.Suffixes.Count < 12)
                    prev.Suffixes.Add(suffix);
                seen[name] = (prev.Uses + 1, prev.Suffixes, prev.File);
            }
        }

        var functions = PackFunctions(files);
        LoadProbeItemDef();
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        // Some members only answer when the object HAS the thing they describe: a
        // stone with no guild record behind it cannot answer ABBREV, and reporting
        // that as "nothing answers it" would be measuring the probe, not the engine.
        var probeGuild = new SphereNet.Game.Guild.GuildDef(new Core.Types.Serial(1));
        Item.ResolveGuild = _ => probeGuild;

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
            if (AnsweredButNotProbeable.Contains(name)) continue;
            if (EngineAnswers(world, client, name, info.Suffixes)) continue;
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
