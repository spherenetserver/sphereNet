using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SphereNet.Tests;

public class SourceXVerbInventoryGuardrailTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static readonly Regex TableVerb = new(@"ADD\([^,]+,\s*""([^""]+)""\)", RegexOptions.Compiled);

    private static readonly Dictionary<string, string[]> ExpectedSourceXVerbs = new()
    {
        ["CObjBase_functions.tbl"] =
        [
            "ADDCLILOC", "BASEPROPLIST", "BASETAGLIST", "CLICK", "CLILOCLIST", "DAMAGE",
            "DCLICK", "DIALOG", "DIALOGCLOSE", "EDIT", "EFFECT", "EFFECTLOCATION", "EMOTE",
            "FIX", "FLIP", "GOAWAKE", "GOSLEEP", "INFO", "INPDLG", "MENU", "MESSAGE",
            "MESSAGEUA", "MOVE", "MOVENEAR", "MOVETO", "MSG", "NUDGEDOWN", "NUDGEUP", "P",
            "PROMPTCONSOLE", "PROMPTCONSOLEU", "PROPLIST", "REMOVE", "REMOVECLILOC",
            "REMOVEFROMVIEW", "REPLACECLILOC", "RESENDTOOLTIP", "SAY", "SAYU", "SAYUA",
            "SDIALOG", "SOUND", "SPELLEFFECT", "TAGLIST", "TARGET", "TIMERF", "TIMERFMS",
            "TRIGGER", "TRY", "TRYP", "TRYSRC", "TRYSRV", "UID", "UPDATE", "UPDATEX",
            "USEITEM", "Z"
        ],
        ["CChar_functions.tbl"] =
        [
            "AFK", "ALLSKILLS", "ANIM", "ATTACK", "BANK", "BARK", "BOUNCE", "BOW", "CONSUME",
            "CONTROL", "CRIMINAL", "CURE", "DESTROY", "DISCONNECT", "DROP", "DUPE", "EQUIP",
            "EQUIPARMOR", "EQUIPHALO", "EQUIPWEAPON", "FACE", "FIXWEIGHT", "FORGIVE", "GO",
            "GOCHAR", "GOCHARID", "GOCLI", "GOITEMID", "GONAME", "GOSOCK", "GOTYPE", "GOUID",
            "HEAR", "HUNGRY", "INVIS", "INVUL", "JAIL", "KILL", "MAKEITEM", "MOUNT",
            "NEWBIESKILL", "NEWGOLD", "NEWLOOT", "NOTOCLEAR", "NOTOUPDATE", "OWNER", "PACK",
            "POISON", "POLY", "PRIVSET", "REMOVE", "RESURRECT", "REVEAL", "SALUTE", "SKILL",
            "SKILLGAIN", "SLEEP", "SMSG", "SMSGL", "SMSGLEX", "SMSGU", "SUICIDE", "SUMMONCAGE",
            "SUMMONTO", "SYSMESSAGE", "SYSMESSAGEF", "SYSMESSAGELOC", "SYSMESSAGELOCEX",
            "SYSMESSAGEUA", "TARGETCLOSE", "UNDERWEAR", "UNEQUIP", "WAKE", "WHERE"
        ],
        ["CItem_functions.tbl"] =
        [
            "BOUNCE", "CARVECORPSE", "CONSUME", "CONTCONSUME", "DECAY", "DESTROY", "DROP",
            "DUPE", "EQUIP", "REPAIR", "SMELT", "UNEQUIP", "USE", "USEDOOR"
        ],
        ["CClient_functions.tbl"] =
        [
            "ADD", "ADDBUFF", "ADDCHAR", "ADDCONTEXTENTRY", "ADDITEM", "ARROWQUEST",
            "BADSPAWN", "BANKSELF", "CAST", "CHANGEFACE", "CHARLIST", "CLEARCTAGS",
            "CLOSEPAPERDOLL", "CLOSEPROFILE", "CLOSESTATUS", "CODEXOFWISDOM", "CTAGLIST",
            "DYE", "EVERBTARG", "EXTRACT", "FLUSH", "GMPAGE", "GOTARG", "INFO", "INFORMATION",
            "LAST", "LINK", "MAPWAYPOINT", "MENU", "MIDILIST", "NUDGE", "NUKE", "NUKECHAR",
            "OPENPAPERDOLL", "OPENTRADEWINDOW", "REMOVEBUFF", "REPAIR", "RESEND", "SAVE",
            "SCROLL", "SELF", "SENDPACKET", "SHOWSKILLS", "SKILLMENU", "SKILLSELECT",
            "SKILLUPDATE", "SMSG", "SMSGL", "SMSGLEX", "SMSGU", "SUMMON", "SYSMESSAGE",
            "SYSMESSAGEF", "SYSMESSAGELOC", "SYSMESSAGELOCEX", "SYSMESSAGEUA", "TELE", "TILE",
            "UNEXTRACT", "VERSION", "WEBLINK"
        ],
        ["CServer.cpp"] =
        [
            "ACCOUNT", "ACCOUNTS", "ALLCLIENTS", "B", "BLOCKIP", "CALCCRYPT", "CHARS",
            "CLEARLISTS", "CONSOLE", "EXPORT", "GARBAGE", "GMPAGES", "HEARALL", "IMPORT",
            "INFORMATION", "ITEMS", "LOAD", "LOG", "PRINTLISTS", "RESPAWN", "RESTOCK",
            "RESTORE", "RESYNC", "SAVE", "SAVECOUNT", "SAVESTATICS", "SECURE", "SHRINKMEM",
            "SHUTDOWN", "TIME", "UNBLOCKIP", "VARLIST"
        ]
    };

    private static readonly Dictionary<string, string[]> KnownPartialOrDeferred = new()
    {
        ["CObjBase_functions.tbl"] =
        [
            "ADDCLILOC", "BASEPROPLIST", "BASETAGLIST", "CLILOCLIST", "DIALOGCLOSE",
            "EDIT", "EFFECTLOCATION", "GOAWAKE", "GOSLEEP", "PROMPTCONSOLE",
            "PROMPTCONSOLEU", "PROPLIST", "REMOVECLILOC", "REPLACECLILOC", "SAYUA",
            "SPELLEFFECT"
        ],
        ["CChar_functions.tbl"] =
        [
            "AFK", "GOCHARID", "GOCLI", "GOSOCK", "GOTYPE", "HEAR", "NEWBIESKILL",
            "NOTOCLEAR", "NOTOUPDATE", "TARGETCLOSE", "UNDERWEAR"
        ],
        ["CClient_functions.tbl"] =
        [
            "ADDCONTEXTENTRY", "BADSPAWN", "CHANGEFACE", "CHARLIST", "CLOSEPROFILE",
            "CLOSESTATUS", "CODEXOFWISDOM", "DYE", "EVERBTARG", "EXTRACT", "GOTARG",
            "LAST", "LINK", "MAPWAYPOINT", "NUDGE", "NUKE", "NUKECHAR", "REPAIR",
            "SCROLL", "SHOWSKILLS", "SKILLUPDATE", "SUMMON", "TILE", "UNEXTRACT"
        ],
        ["CServer.cpp"] =
        [
            "BLOCKIP", "CALCCRYPT", "CONSOLE", "IMPORT", "SAVESTATICS", "UNBLOCKIP"
        ]
    };

    [Fact]
    public void SourceXVerbSurfaces_MatchPinnedInventory()
    {
        foreach (var (surface, expected) in ExpectedSourceXVerbs)
        {
            var actual = surface.EndsWith(".tbl", StringComparison.Ordinal)
                ? ReadFunctionTable(surface)
                : ReadServerVerbs();

            Assert.Equal(
                expected.OrderBy(x => x, StringComparer.Ordinal),
                actual.OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void KnownBacklog_OnlyReferencesPinnedSourceXVerbs()
    {
        foreach (var (surface, verbs) in KnownPartialOrDeferred)
        {
            var upstream = ExpectedSourceXVerbs[surface].ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(verbs, verb => Assert.Contains(verb, upstream));
        }
    }

    private static List<string> ReadFunctionTable(string fileName)
    {
        string path = Path.Combine(RepoRoot(), "oldSphere", "Source-X-full", "src", "tables", fileName);
        return File.ReadLines(path)
            .Select(line => TableVerb.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    private static List<string> ReadServerVerbs()
    {
        string path = Path.Combine(RepoRoot(), "oldSphere", "Source-X-full", "src", "game", "CServer.cpp");
        var verbs = new List<string>();
        bool inTable = false;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Contains("CServer::sm_szVerbKeys", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }
            if (!inTable)
                continue;
            if (line.Contains("nullptr", StringComparison.Ordinal))
                break;

            var match = Regex.Match(line, @"""([A-Z0-9_]+)""");
            if (match.Success && !match.Groups[1].Value.Equals("CRASH", StringComparison.Ordinal))
                verbs.Add(match.Groups[1].Value);
        }
        return verbs;
    }
}
