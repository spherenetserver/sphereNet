using SphereNet.Network.Encryption;
using SphereNet.Scripting.Expressions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>CScriptObj functions from CScriptObj_functions.tbl that had no route:
/// RESDEF0 (and RESDEF, which was missing as well), BCRYPTHASH / BCRYPTVALIDATE and
/// SYSCMD / SYSSPAWN (gated by OF_FileCommands, CScriptObj.cpp:953).
///
/// The guild-stone verbs of CItemStone_functions.tbl named alongside them are dead
/// upstream (CItemStone::r_Verb falls to default: return false) and are pinned as such
/// by SourceXVerbInventoryGuardrailTests; the CSFileObjContainer keywords belong to a
/// class Source-X never instantiates, so no script can reach them.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StoneFileScriptKeywordParityTests
{
    private static string Resolve(string expr, string extraScript = "")
    {
        string path = Path.Combine(Path.GetTempPath(), $"resdef-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path,
                "[CHARDEF 0999]\nDEFNAME=c_resdef_probe\n" +
                "[ITEMDEF 0eed]\nDEFNAME=i_resdef_probe\n" +
                "[DEFNAME resdef_consts]\nresdef_const 5\n" +
                extraScript +
                "[EOF]\n");
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(path);
            return stack.Parser.ResolveAngleBrackets(expr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- RESDEF / RESDEF0 ---------------------------------------------------------

    [Fact]
    public void ResDef_AnswersTheResourceUid()
    {
        // UID_F_RESOURCE | (RES_CHARDEF=7 << 20) | 0x999, formatted as Sphere hex.
        Assert.Equal("080700999", Resolve("<RESDEF.c_resdef_probe>"));
        Assert.Equal("080F00EED", Resolve("<RESDEF.i_resdef_probe>"));
        Assert.Equal("080700999", Resolve("<RESDEF0.c_resdef_probe>"));
    }

    [Fact]
    public void ResDef_UnknownNameIsEmpty_ResDef0IsZero()
    {
        Assert.Equal("[]", "[" + Resolve("<RESDEF.no_such_resource>") + "]");
        Assert.Equal("0", Resolve("<RESDEF0.no_such_resource>"));
    }

    [Fact]
    public void ResDef_IgnoresPlainDefNameConstants()
    {
        // [DEFNAME] entries live in m_VarDefs, not m_VarResDefs.
        Assert.Equal("0", Resolve("<RESDEF0.resdef_const>"));
    }

    // --- BCRYPTHASH / BCRYPTVALIDATE ------------------------------------------------

    [Fact]
    public void BCrypt_HashThenValidateRoundTrips()
    {
        ExpressionParser.BCryptHasher = ScriptBCrypt.Hash;
        ExpressionParser.BCryptValidator = ScriptBCrypt.Validate;
        var parser = new ExpressionParser();

        string hash = parser.ResolveAngleBrackets("<BCRYPTHASH 2,4,secret>");
        Assert.StartsWith("$2y$04$", hash);
        Assert.Equal(60, hash.Length);
        Assert.Equal("1", parser.ResolveAngleBrackets($"<BCRYPTVALIDATE secret,{hash}>"));
        Assert.Equal("0", parser.ResolveAngleBrackets($"<BCRYPTVALIDATE wrong,{hash}>"));
    }

    [Fact]
    public void BCrypt_PrefixCodesAndCostClamp()
    {
        ExpressionParser.BCryptHasher = ScriptBCrypt.Hash;
        var parser = new ExpressionParser();

        Assert.StartsWith("$2a$04$", parser.ResolveAngleBrackets("<BCRYPTHASH 0,1,pw>"));   // cost clamped up to 4
        Assert.StartsWith("$2b$04$", parser.ResolveAngleBrackets("<BCRYPTHASH 1,4,pw>"));
        Assert.StartsWith("$2a$04$", parser.ResolveAngleBrackets("<BCRYPTHASH 9,4,pw>"));   // unknown -> $2a$
        // $1$ / _ salts are generated but the blowfish-only crypt_rn cannot hash them.
        Assert.Equal("[]", "[" + parser.ResolveAngleBrackets("<BCRYPTHASH 3,4,pw>") + "]");
        Assert.Equal("[]", "[" + parser.ResolveAngleBrackets("<BCRYPTHASH 4,4,pw>") + "]");
    }

    [Fact]
    public void BCryptValidate_KnownVectorAndMalformedHash()
    {
        // Openwall crypt_blowfish test vector ("U*U", cost 5).
        Assert.True(ScriptBCrypt.Validate("U*U", "$2a$05$CCCCCCCCCCCCCCCCCCCCC.E5YPO9kmyuRGyh0XouQYb4YMJKvyOeW"));
        Assert.False(ScriptBCrypt.Validate("U*U", "$1$notbcrypt"));
        Assert.False(ScriptBCrypt.Validate("U*U", "garbage"));
    }

    // --- SYSCMD / SYSSPAWN ----------------------------------------------------------

    private sealed class LauncherProbe
    {
        public string? Program;
        public IReadOnlyList<string>? Args;
        public bool? Wait;
        public int Calls;

        public int? Launch(string program, IReadOnlyList<string> args, bool wait)
        {
            Calls++;
            Program = program;
            Args = args;
            Wait = wait;
            return wait ? 3 : null;
        }
    }

    [Fact]
    public void SysCmd_RefusedWithoutFileCommands()
    {
        var probe = new LauncherProbe();
        ExpressionParser.SystemProcessLauncher = probe.Launch;
        var parser = new ExpressionParser();

        ExpressionParser.FileCommandsEnabled = null;
        parser.ResolveAngleBrackets("<SYSCMD tool.exe a b>");
        parser.ResolveAngleBrackets("<SYSSPAWN tool.exe a b>");

        ExpressionParser.FileCommandsEnabled = () => false;
        parser.ResolveAngleBrackets("<SYSCMD tool.exe a b>");
        parser.ResolveAngleBrackets("<SYSSPAWN tool.exe a b>");

        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void SysCmd_WaitsAndSysSpawnDoesNot_WhenEnabled()
    {
        var probe = new LauncherProbe();
        ExpressionParser.SystemProcessLauncher = probe.Launch;
        ExpressionParser.FileCommandsEnabled = () => true;
        var parser = new ExpressionParser();

        string ret = parser.ResolveAngleBrackets("<SYSCMD tool.exe first,\"two words\" third>");
        Assert.Equal(1, probe.Calls);
        Assert.Equal("tool.exe", probe.Program);
        Assert.Equal(["first", "two words", "third"], probe.Args);
        Assert.True(probe.Wait);
        // _spawnl on Windows writes nothing; the POSIX build answers the exit status.
        Assert.Equal(OperatingSystem.IsWindows() ? "" : "03", ret);

        ret = parser.ResolveAngleBrackets("<SYSSPAWN tool.exe x>");
        Assert.Equal(2, probe.Calls);
        Assert.False(probe.Wait);
        Assert.Equal(["x"], probe.Args);
        Assert.Equal("", ret);
    }

    [Fact]
    public void SysCmd_KeepsAtMostNineArguments()
    {
        var probe = new LauncherProbe();
        ExpressionParser.SystemProcessLauncher = probe.Launch;
        ExpressionParser.FileCommandsEnabled = () => true;

        new ExpressionParser().ResolveAngleBrackets("<SYSSPAWN p 1 2 3 4 5 6 7 8 9 10>");

        Assert.Equal(9, probe.Args!.Count);
        Assert.Equal("9 10", probe.Args[8]); // the last slot keeps the rest of the line
    }

    [Fact]
    public void ParseCmds_MatchesStrParseSeparators()
    {
        Assert.Equal(["a", "b", "c d"], ExpressionParser.ParseCmds("a, b c d", 3, ", "));
        Assert.Equal(["a", "b=c"], ExpressionParser.ParseCmds("a=b=c", 2, null));
        Assert.Equal(["pw", ""], ExpressionParser.ParseCmds("pw,", 2, ", "));
        Assert.Empty(ExpressionParser.ParseCmds("   ", 3, null));
    }
}
