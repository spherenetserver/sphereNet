using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// Script READING parity with Source-X (CServerConfig::LoadResourceSection +
/// CExpression): special sections ([OBSCENE], [GLOBALS]/[LIST], [FAME]/[KARMA]/
/// [NOTOTITLES], [RUNES], [TYPEDEFS], [BOOK], nested [RESOURCES]) and the
/// intrinsic/number-literal alignments.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class ScriptReadingParityTests
{
    private static ResourceHolder LoadScript(string contents, out string tempFile)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_readparity_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);

        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    private static ResourceHolder LoadScript(string contents) => LoadScript(contents, out _);

    // ---- [OBSCENE] + ISOBSCENE ----

    [Fact]
    public void ObsceneSection_LoadsAndMatchesLikeSourceX()
    {
        var resources = LoadScript("""
            [OBSCENE]
            badword
            evil*phrase

            [EOF]
            """);

        Assert.Equal(2, resources.ObsceneWords.Count);
        // Source-X wraps each word as *word* — substring match, case-insensitive.
        Assert.True(resources.IsObscene("you are a BADWORD indeed"));
        Assert.True(resources.IsObscene("evilishphrase"));      // inner * wildcard
        Assert.False(resources.IsObscene("perfectly clean"));
        Assert.False(resources.IsObscene(""));
    }

    [Fact]
    public void IsObsceneIntrinsic_UsesWiredChecker()
    {
        var parser = new ExpressionParser();
        Assert.Equal("0", parser.EvaluateStr("<ISOBSCENE anything>")); // no list wired

        ExpressionParser.ObsceneChecker = text => text.Contains("bad", StringComparison.OrdinalIgnoreCase);
        try
        {
            Assert.Equal("1", parser.EvaluateStr("<ISOBSCENE this is bad>"));
            Assert.Equal("0", parser.EvaluateStr("<ISOBSCENE this is fine>"));
            Assert.Equal("1", parser.EvaluateStr("<ISOBSCENE(really bad)>"));
        }
        finally
        {
            ExpressionParser.ObsceneChecker = null;
        }
    }

    // ---- [GLOBALS]/[WORLDVARS] + [LIST]/[WORLDLISTS] ----

    [Fact]
    public void GlobalsAndListSections_AreCollectedForWorldSeeding()
    {
        var resources = LoadScript("""
            [GLOBALS]
            quest_stage=3
            VAR.legacy_name=hello

            [LIST spawn_bosses]
            ELEM=c_dragon
            ELEM=c_lich

            [WORLDLISTS towns]
            ELEM=britain

            [EOF]
            """);

        Assert.Equal(2, resources.ScriptGlobalVars.Count);
        Assert.Contains(resources.ScriptGlobalVars, kv => kv.Key == "quest_stage" && kv.Value == "3");
        // "VAR." legacy prefix is stripped (Source-X RES_WORLDVARS compat).
        Assert.Contains(resources.ScriptGlobalVars, kv => kv.Key == "legacy_name" && kv.Value == "hello");

        Assert.Equal(2, resources.ScriptGlobalLists.Count);
        var bosses = resources.ScriptGlobalLists.First(l => l.Name == "spawn_bosses");
        Assert.Equal(["c_dragon", "c_lich"], bosses.Elements);
        var towns = resources.ScriptGlobalLists.First(l => l.Name == "towns");
        Assert.Equal(["britain"], towns.Elements);
    }

    // ---- [FAME]/[KARMA]/[NOTOTITLES] ----

    [Fact]
    public void FameKarmaAndNotoTitles_LoadAndResolve()
    {
        var resources = LoadScript("""
            [FAME]
            <none>
            Famous

            [KARMA]
            Wicked
            Kind

            [NOTOTITLES]
            9900 5000
            1000 5000
            Outcast,Outcast f
            Rude
            Notorious
            Anonymous
            Known
            Famed
            Good
            Nice
            Great Lord,Great Lady

            [EOF]
            """);

        Assert.Equal(2, resources.FameTitles.Count);
        Assert.Equal("", resources.FameTitles[0]); // '<' line → empty slot
        Assert.Equal("Famous", resources.FameTitles[1]);
        Assert.Equal(["Wicked", "Kind"], resources.KarmaTitles);

        Assert.Equal([9900, 5000], resources.NotoKarmaLevels);
        Assert.Equal([1000, 5000], resources.NotoFameLevels);
        Assert.Equal(9, resources.NotoTitles.Count); // (2+1)*(2+1)

        // karma 10000 (>=9900 → i=0), fame 500 (<=1000 → j=0) → slot 0
        Assert.Equal("Outcast", resources.GetNotoTitle(10000, 500, female: false));
        Assert.Equal("Outcast f", resources.GetNotoTitle(10000, 500, female: true));
        // karma -5000 (below both → i=2), fame 9000 (above both → j=2) → slot 8
        Assert.Equal("Great Lord", resources.GetNotoTitle(-5000, 9000, female: false));
        Assert.Equal("Great Lady", resources.GetNotoTitle(-5000, 9000, female: true));
    }

    // ---- [RUNES] + SpellDef letter decode ----

    [Fact]
    public void RunesSection_DecodesSpellLetterForm()
    {
        var resources = LoadScript("""
            [RUNES]
            An
            Bet
            Corp
            Des
            Ex
            Flam
            Grav
            Hur
            In

            [EOF]
            """);

        Assert.Equal("An", resources.GetRune('a'));
        Assert.Equal("In", resources.GetRune('I'));
        Assert.Equal("?", resources.GetRune('Z')); // out of table

        SpellDef.RuneWordResolver = ch => resources.Runes.Count > 0 ? resources.GetRune(ch) : null;
        try
        {
            var spell = new SpellDef { Runes = "AIF" };
            Assert.Equal("An In Flam", spell.GetPowerWords());

            // Word form (contains space) is spoken verbatim — no decode.
            var wordSpell = new SpellDef { Runes = "In Mani" };
            Assert.Equal("In Mani", wordSpell.GetPowerWords());
        }
        finally
        {
            SpellDef.RuneWordResolver = null;
        }
    }

    // ---- [TYPEDEFS] bulk declarations ----

    [Fact]
    public void TypeDefsBulkSection_RegistersTypeNames()
    {
        var resources = LoadScript("""
            [TYPEDEFS]
            t_custom_forge 250
            t_custom_anvil 0x100

            [EOF]
            """);

        var forge = resources.ResolveDefName("t_custom_forge");
        Assert.True(forge.IsValid);
        Assert.Equal(ResType.TypeDef, forge.Type);
        Assert.Equal(250, forge.Index);

        var anvil = resources.ResolveDefName("t_custom_anvil");
        Assert.Equal(0x100, anvil.Index);
    }

    // ---- [BOOK] retention ----

    [Fact]
    public void BookSections_AreRetainedWithPages()
    {
        var resources = LoadScript("""
            [BOOK b_test_tome]
            PAGES=2
            TITLE=The Test Tome
            AUTHOR=Fable

            [BOOK b_test_tome 1]
            First page line one.
            First page line two.

            [BOOK b_test_tome 2]
            Second page.

            [EOF]
            """);

        var title = resources.GetBookSection("b_test_tome", 0);
        Assert.NotNull(title);
        Assert.Contains(title!.StoredKeys!, k => k.Key.Equals("TITLE", StringComparison.OrdinalIgnoreCase));

        var page1 = resources.GetBookSection("b_test_tome", 1);
        Assert.NotNull(page1);
        Assert.Equal(2, page1!.StoredKeys!.Count);

        Assert.Null(resources.GetBookSection("b_test_tome", 9));
    }

    // ---- nested [RESOURCES] ----

    [Fact]
    public void NestedResourcesSection_QueuesAndLoadsReferencedFile()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        string dir = Path.Combine(Path.GetTempPath(), $"spherenet_nestedres_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string extra = Path.Combine(dir, "extra_defs.scp");
            File.WriteAllText(extra, """
                [DEFNAME nested_test_defs]
                nested_test_value 1234

                [EOF]
                """);
            string main = Path.Combine(dir, "main.scp");
            File.WriteAllText(main, """
                [RESOURCES]
                extra_defs

                [EOF]
                """);

            var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
            {
                ScpBaseDir = dir
            };
            resources.LoadResourceFile(main);

            // main.scp's [RESOURCES] queued extra_defs (extension appended).
            var pending = resources.DrainPendingResourceFiles();
            string queued = Assert.Single(pending);
            Assert.Equal(Path.GetFullPath(extra), queued);

            resources.LoadResourceFile(queued);
            Assert.True(resources.TryResolveDefNameValue("nested_test_value", out long v));
            Assert.Equal(1234, v);

            // Already-known files are not queued twice.
            resources.LoadResourceFile(main);
            Assert.Empty(resources.DrainPendingResourceFiles());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Manifest_ProcessesAllResourcesSectionsAndUnlistedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"spherenet_manifest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "spheretables.scp"), """
                [RESOURCES]
                first

                [RESOURCES]
                second.scp

                [EOF]
                """);
            File.WriteAllText(Path.Combine(dir, "first.scp"), "[EOF]\n");
            File.WriteAllText(Path.Combine(dir, "second.scp"), "[EOF]\n");
            File.WriteAllText(Path.Combine(dir, "unlisted.scp"), "[EOF]\n");

            var files = ScriptResourceManifest.Resolve(dir).Select(Path.GetFileName).ToList();

            // Table first, then both sections' entries in order, then the
            // unlisted top-level file (Source-X AddResourceDir sweep).
            Assert.Equal(["spheretables.scp", "first.scp", "second.scp", "unlisted.scp"], files);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- expression intrinsic / literal alignments ----

    [Fact]
    public void TrigIntrinsics_UseSourceXRadianSemantics()
    {
        var parser = new ExpressionParser();
        // Source-X: (llong)cos((double)val) — radians, truncated.
        Assert.Equal("1", parser.EvaluateStr("<COS 0>"));
        Assert.Equal("0", parser.EvaluateStr("<SIN 0>"));
        Assert.Equal("0", parser.EvaluateStr("<SIN 1>"));   // sin(1 rad)=0.84 → 0
        Assert.Equal("1", parser.EvaluateStr("<TAN(1)>"));  // tan(1 rad)=1.55 → 1
        Assert.Equal("0", parser.EvaluateStr("<ARCSIN 0>"));
        Assert.Equal("1", parser.EvaluateStr("<ARCCOS 0>")); // acos(0)=1.57 → 1
        Assert.Equal("0", parser.EvaluateStr("<ARCTAN 0>"));
    }

    [Fact]
    public void StrAsciiAndCaseSensitiveIndexOf()
    {
        var parser = new ExpressionParser();
        Assert.Equal("65", parser.EvaluateStr("<STRASCII Abc>"));
        Assert.Equal("0", parser.EvaluateStr("<STRASCII()>"));
        // Source-X Str_IndexOf is case-SENSITIVE.
        Assert.Equal("-1", parser.EvaluateStr("<STRINDEXOF(Hello,hello)>"));
        Assert.Equal("2", parser.EvaluateStr("<STRINDEXOF(ababc,abc)>"));
    }

    [Fact]
    public void IdIntrinsic_MasksResourceTypeBits()
    {
        var parser = new ExpressionParser();
        // Source-X ResGetIndex: low 20 bits only.
        Assert.Equal("291", parser.EvaluateStr("<ID(0x2100123)>")); // 0x123
        Assert.Equal("100", parser.EvaluateStr("<ID(100)>"));
    }

    [Fact]
    public void RandBell_IsSourceXLogCurve()
    {
        var parser = new ExpressionParser();
        // Calc_GetBellCurve: diff 0 → 500, diff == variance → 250.
        Assert.Equal("500", parser.EvaluateStr("<RANDBELL(0,100)>"));
        Assert.Equal("250", parser.EvaluateStr("<RANDBELL(100,100)>"));
        Assert.Equal("250", parser.EvaluateStr("<RANDBELL(-100,100)>"));
        Assert.Equal("500", parser.EvaluateStr("<RANDBELL(5,0)>")); // variance<=0 → 500
    }

    [Fact]
    public void BraceRange_OddPairCountIsErrorZero()
    {
        var parser = new ExpressionParser();
        // Source-X GetRangeNumber: >2 args must be even; odd → error, 0.
        Assert.Equal(0, parser.Evaluate("{1 2 3}"));
        // Even weighted form still picks a listed value.
        long v = parser.Evaluate("{5 1 9 1}");
        Assert.True(v == 5 || v == 9);
    }

    [Fact]
    public void NumberLiterals_FollowSourceXDotRules()
    {
        var parser = new ExpressionParser();
        // '0' followed by '.' → decimal path; dots are grouping separators.
        Assert.Equal(5, parser.Evaluate("0.5"));
        Assert.Equal(100000, parser.Evaluate("100.000"));
        Assert.Equal(5, parser.Evaluate(".5"));   // legacy leading dot skipped
        Assert.Equal(0x1A, parser.Evaluate("01a")); // leading zero → hex, unchanged
        // Dotted decimals now parse fully → numeric for TryEvaluate.
        Assert.True(parser.TryEvaluate("1.5", out long dotted));
        Assert.Equal(15, dotted);
    }

    // ---- ScriptKey lexer parity (Source-X Str_Parse "=, \t") ----

    [Fact]
    public void ScriptKey_CommaIsAKeyArgSeparator()
    {
        var key = new SphereNet.Scripting.Parsing.ScriptKey();
        key.Parse("KEY,VALUE");
        Assert.Equal("KEY", key.Key);
        Assert.Equal("VALUE", key.Arg);
        // The raw line still carries the original separator.
        Assert.Equal("KEY,VALUE", key.RawLine);
    }

    [Fact]
    public void ScriptKey_QuotesAndBracketsGuardSeparators()
    {
        var key = new SphereNet.Scripting.Parsing.ScriptKey();
        key.Parse("\"quoted key\"=x");
        Assert.Equal("\"quoted key\"", key.Key); // space inside quotes ignored
        Assert.Equal("x", key.Arg);

        key = new SphereNet.Scripting.Parsing.ScriptKey();
        key.Parse("TAG.RANGE={1 5},done");
        Assert.Equal("TAG.RANGE", key.Key); // '=' wins; comma inside...
        Assert.Equal("{1 5},done", key.Arg);
    }

    [Fact]
    public void ScriptKey_FloatCompoundAssignmentUsesFloatVal()
    {
        var key = new SphereNet.Scripting.Parsing.ScriptKey();
        key.Parse("FLOAT.SPEED += 1.5");
        Assert.Equal("FLOAT.SPEED", key.Key);
        Assert.StartsWith("<FLOATVAL <FLOAT.SPEED>+", key.Arg);

        key = new SphereNet.Scripting.Parsing.ScriptKey();
        key.Parse("COUNT += 2");
        Assert.Equal("<EVAL <COUNT>+2>", key.Arg);
    }

    [Fact]
    public void ScriptSection_HeaderAcceptsEqualsAndCommaSeparators()
    {
        var (name, arg) = SphereNet.Scripting.Parsing.ScriptSection.ParseHeader("ITEMDEF=i_test");
        Assert.Equal("ITEMDEF", name);
        Assert.Equal("i_test", arg);

        (name, arg) = SphereNet.Scripting.Parsing.ScriptSection.ParseHeader("CHARDEF,c_test");
        Assert.Equal("CHARDEF", name);
        Assert.Equal("c_test", arg);

        (name, arg) = SphereNet.Scripting.Parsing.ScriptSection.ParseHeader("FUNCTION f_test");
        Assert.Equal("FUNCTION", name);
        Assert.Equal("f_test", arg);
    }

    [Fact]
    public void RawSections_PreserveCommaLines()
    {
        var resources = LoadScript("""
            [NAMES human_test]
            John Smith
            Mary, the Baker

            [MOONGATES]
            1000,2000,0,0
            Britain=1496,1628,10

            [EOF]
            """);

        // Multi-word / comma names survive via RawLine.
        string? name = resources.GetRandomName("human_test");
        Assert.NotNull(name);
        Assert.True(name == "John Smith" || name == "Mary, the Baker");

        Assert.Equal(2, resources.Moongates.Count);
        Assert.Equal(1000, resources.Moongates[0].Point.X); // bare coordinate line
        Assert.Equal("Britain", resources.Moongates[1].Name);
        Assert.Equal(1496, resources.Moongates[1].Point.X);
    }

    // ---- Sphere right-fold expression semantics (Source-X GetVal/GetValMath) ----

    [Fact]
    public void Expressions_FoldRightToLeftWithoutPrecedence()
    {
        var parser = new ExpressionParser();
        // Sphere has NO operator precedence: rhs of every operator is the
        // fully-folded remainder.
        Assert.Equal(8, parser.Evaluate("2*3+1"));    // 2*(3+1)
        Assert.Equal(7, parser.Evaluate("1+2*3"));    // 1+(2*3)
        Assert.Equal(1, parser.Evaluate("10/2*5"));   // 10/(2*5)
        Assert.Equal(9, parser.Evaluate("(1+2)*3"));  // parens override
        Assert.Equal(2, parser.Evaluate("5 - 3"));
        Assert.Equal(6, parser.Evaluate("1 - 2 - -7")); // 1+(-2+7) — '-' as sign of rhs
    }

    [Fact]
    public void Expressions_DivideByZeroKeepsLeftValue()
    {
        var parser = new ExpressionParser();
        // Source-X logs the error and leaves the left operand unchanged.
        Assert.Equal(5, parser.Evaluate("5/0"));
        Assert.Equal(7, parser.Evaluate("7%0"));
    }

    [Fact]
    public void Conditionals_SplitAtTopLevelLogicalOperators()
    {
        var parser = new ExpressionParser();
        // Plain right-fold would compute "2 == (2 && (2 == 2))" = 2 == 1 = false.
        // The Source-X conditional splitter evaluates (2==2) && (2==2) = true.
        Assert.True(parser.EvaluateConditional("2 == 2 && 2 == 2"));
        Assert.False(parser.EvaluateConditional("2 == 3 && 2 == 2"));
        Assert.True(parser.EvaluateConditional("1 == 0 || 0 == 0"));
        Assert.False(parser.EvaluateConditional("1 == 0 || 0 == 1"));
        // Left-to-right with EQUAL precedence (Sphere), short-circuiting.
        Assert.True(parser.EvaluateConditional("(1 || 0) && 1"));
        Assert.True(parser.EvaluateConditional("!(1 == 2)"));
        Assert.False(parser.EvaluateConditional("!(1 == 1) && 1"));
        // Parenthesized groups recurse through the splitter.
        Assert.True(parser.EvaluateConditional("(2 == 2 && 3 == 3) || 0"));
        // The raw fold is still reachable through Evaluate for <EVAL> contexts.
        Assert.Equal(0, parser.Evaluate("2 == 2 && 2 == 2"));
    }

    // ---- Second-pass audit fixes (Source-X re-verification) ----

    [Fact]
    public void ScriptKey_WhitespaceSeparatorConsumesFollowingEquals()
    {
        // Source-X Str_Parse: a whitespace separator swallows one following
        // separator char, so "name = value" doesn't leak the '=' into Arg.
        var key = new SphereNet.Scripting.Parsing.ScriptKey();
        key.Parse("brain_animal = 1");
        Assert.Equal("brain_animal", key.Key);
        Assert.Equal("1", key.Arg);

        key = new SphereNet.Scripting.Parsing.ScriptKey();
        key.Parse("KEY , VAL");
        Assert.Equal("KEY", key.Key);
        Assert.Equal("VAL", key.Arg);
    }

    [Fact]
    public void DefNames_SpacedEqualsAndQuotedValuesParseLikeSourceX()
    {
        var resources = LoadScript("""
            [DEFNAME test_constants]
            brain_animal = 1
            quoted_text="hello world"
            quoted_num="5"

            [EOF]
            """);

        Assert.True(resources.TryResolveDefNameValue("brain_animal", out long brain));
        Assert.Equal(1, brain);
        // GetArgStr strips the quote pair — text stored unquoted, quoted
        // numerics still numeric.
        Assert.True(resources.TryGetDefValue("quoted_text", out string txt));
        Assert.Equal("hello world", txt);
        Assert.True(resources.TryResolveDefNameValue("quoted_num", out long qn));
        Assert.Equal(5, qn);
    }

    [Fact]
    public void LeadingZeroHex_SignExtends32BitValues()
    {
        var parser = new ExpressionParser();
        // Source-X GetSingle: ≤8 significant nibbles reinterpret as int32.
        Assert.Equal(-1, parser.Evaluate("0ffffffff"));
        Assert.Equal(-2147483648, parser.Evaluate("080000000"));
        Assert.Equal(65535, parser.Evaluate("0ffff"));
        // 9-16 nibbles = int64 two's complement.
        Assert.Equal(-1, parser.Evaluate("0ffffffffffffffff"));
        // Decimal overflow → -1 like Source-X.
        Assert.Equal(-1, parser.Evaluate("99999999999999999999"));
    }

    [Fact]
    public void Qval_NestedAndShortForms()
    {
        var parser = new ExpressionParser();
        // Nested QVAL must not donate its '?'/':' to the outer split.
        Assert.Equal("MATCH", parser.EvaluateStr("<QVAL <QVAL 5?1:0>==1 ? MATCH : NOMATCH>"));
        // Numeric 3-way form works with 3-4 args (missing branches default 0).
        Assert.Equal("10", parser.EvaluateStr("<QVAL(3,5,10)>"));
        Assert.Equal("0", parser.EvaluateStr("<QVAL(5,3,10)>"));
        Assert.Equal("7", parser.EvaluateStr("<QVAL(4,4,10,7)>"));
    }

    [Fact]
    public void StrFuncs_ArgsSplitBeforeResolution()
    {
        var parser = new ExpressionParser
        {
            VariableResolver = name => name.Equals("COORD", StringComparison.OrdinalIgnoreCase) ? "1,2" : null
        };
        // <COORD> resolves to "1,2" — the comma must not corrupt the arg split.
        Assert.Equal("0", parser.EvaluateStr("<STRCMP(<COORD>,1,2)>"));
        Assert.Equal("3", parser.EvaluateStr("<STRLEN <COORD>>"));
        // Nested parens inside args survive.
        Assert.Equal("0", parser.EvaluateStr("<STRCMP((a,b),(a,b))>"));
    }

    [Fact]
    public void ConditionalSplitter_UnmatchedAngleDoesNotSwallowLogicalOps()
    {
        var parser = new ExpressionParser();
        // "a<b || c" with no closing '>' — '<' is a comparison, the '||'
        // split must still happen: (1<1)=false || 1 → true.
        Assert.True(parser.EvaluateConditional("1<1 || 1"));
        Assert.False(parser.EvaluateConditional("1<1 && 1"));
    }

    [Fact]
    public void GlobalListsAndVars_MergeAndUnquoteLikeSourceX()
    {
        var resources = LoadScript("""
            [LIST bosses]
            ELEM=c_dragon

            [LIST bosses]
            ELEM="c_lich"

            [GLOBALS]
            motd="welcome all"

            [EOF]
            """);

        // Second [LIST bosses] block appends to the SAME list (AddList).
        var entry = Assert.Single(resources.ScriptGlobalLists, l => l.Name == "bosses");
        Assert.Equal(["c_dragon", "c_lich"], entry.Elements);
        // Quoted values are stored de-quoted.
        Assert.Contains(resources.ScriptGlobalVars, kv => kv.Key == "motd" && kv.Value == "welcome all");
    }

    [Fact]
    public void TypeDefsBulk_ResolvableThroughDefTextTable()
    {
        var resources = LoadScript("""
            [TYPEDEFS]
            t_custom_thing 77

            [EOF]
            """);

        // <DEF.t_custom_thing> reads go through the def-text table.
        Assert.True(resources.TryGetDefValue("t_custom_thing", out string v));
        Assert.Equal("77", v);
    }

    [Fact]
    public void Rand_Supports64BitRanges()
    {
        var parser = new ExpressionParser();
        long big = 5_000_000_000;
        long v = parser.Evaluate($"<EVAL <RAND({big},{big})>>".AsSpan()) ;
        Assert.Equal(big, v);
    }
}
