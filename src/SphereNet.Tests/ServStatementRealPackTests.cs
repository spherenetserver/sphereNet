using System.Reflection;
using System.Text.RegularExpressions;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The generic SERV statement/read path against real packs: a Sphere 56T
/// custom-version pack (SPHERENET_56T_SCRIPTS, default C:\56T\scripts) and a Source-X
/// pack (oldSphere/sphere-x/scripts). Each test skips cleanly when its pack is absent.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ServStatementRealPackTests : IDisposable
{
    private static readonly Type P = typeof(SphereNet.Server.Program);
    private readonly ITestOutputHelper _out;
    private readonly Dictionary<string, object?> _savedStatics = new();

    public ServStatementRealPackTests(ITestOutputHelper output)
    {
        _out = output;
        foreach (string name in new[] { "_resources", "_world", "_triggerRunner" })
            _savedStatics[name] = Field(name).GetValue(null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedStatics)
            Field(name).SetValue(null, value);
    }

    private static FieldInfo Field(string name) =>
        P.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static string? Pack56T()
    {
        string dir = Environment.GetEnvironmentVariable("SPHERENET_56T_SCRIPTS") ?? @"C:\56T\scripts";
        return File.Exists(Path.Combine(dir, "sphere_functions.scp")) ? dir : null;
    }

    private static string? PackSourceX()
    {
        string? dir = TestRepo.Optional("oldSphere/sphere-x/scripts");
        return dir != null && Directory.Exists(dir) ? dir : null;
    }

    private sealed record Bench(ScriptRuntimeStack Stack, Character Ch);

    private Bench Load(string root)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.ScpBaseDir = root;
        foreach (string f in Directory.EnumerateFiles(root, "*.scp", SearchOption.AllDirectories))
        {
            try { stack.Resources.LoadResourceFile(f); }
            catch (Exception e) { _out.WriteLine($"load {f}: {e.Message}"); }
        }
        new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Field("_resources").SetValue(null, stack.Resources);
        Field("_world").SetValue(null, world);
        Field("_triggerRunner").SetValue(null, stack.Runner);

        var resolve = P.GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        stack.Interpreter.ServerPropertyResolver = p => (string?)resolve.Invoke(null, [p]);
        stack.Interpreter.FunctionLookup = stack.Runner.HasFunction;
        stack.Interpreter.ServerObject = (IScriptObj)Field("_serverHookContext").GetValue(null)!;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x190;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return new Bench(stack, ch);
    }

    private static string Read(Bench b, string expr)
    {
        b.Ch.Tags.Remove("OUT");
        b.Stack.Interpreter.Execute([new ScriptKey("TAG.OUT", expr)], b.Ch, null,
            new TriggerArgs { Source = b.Ch }, new ScriptScope());
        return b.Ch.Tags.Get("OUT") ?? "";
    }

    /// <summary>Every SERV.f_x statement the 56T pack writes names a [FUNCTION] the
    /// engine finds, so the generic path runs it; and a read through SERV calls the
    /// function rather than answering its resource index.</summary>
    [Fact]
    public void Sphere56TPack_ServFunctionStatementsAndReadsReachThePacksFunctions()
    {
        string? root = Pack56T();
        if (Gate.Missing(_out, "external script pack", root == null)) return;
        var b = Load(root!);

        var called = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var rx = new Regex(@"^\s*serv\.(f_[a-z0-9_]+)", RegexOptions.IgnoreCase);
        foreach (string f in Directory.EnumerateFiles(root!, "*.scp", SearchOption.AllDirectories))
            foreach (string line in File.ReadLines(f))
                if (rx.Match(line) is { Success: true } m)
                    called.Add(m.Groups[1].Value);
        Assert.NotEmpty(called);
        Assert.All(called, name => Assert.True(b.Stack.Runner.HasFunction(name), $"[FUNCTION {name}] missing"));

        Assert.Equal("1,234,567", Read(b, "<SERV.f_decimalseperator 1234567>"));
        Assert.True(b.Stack.Runner.HasFunction("WRITEFILE"), "the pack's own WRITEFILE");
    }

    /// <summary>The 56T pack renames skill slots with KEY=; LOOKUPSKILL answers the
    /// pack's name, and the slot is the one its [SKILL n] header names.</summary>
    [Fact]
    public void Sphere56TPack_LookupSkillAnswersKeyRenamedSkills()
    {
        string? root = Pack56T();
        if (Gate.Missing(_out, "external script pack", root == null)) return;
        string skills = Path.Combine(root!, "sphere_skills.scp");
        if (Gate.Missing(_out, "external script pack", !File.Exists(skills))) return;

        string? header = null, expected = null;
        foreach (string line in File.ReadLines(skills))
        {
            var h = Regex.Match(line, @"^\[SKILL\s+(\d+)\]", RegexOptions.IgnoreCase);
            if (h.Success) header = h.Groups[1].Value;
            else if (header != null && Regex.IsMatch(line, @"^\s*KEY\s*=\s*Farming\s*$", RegexOptions.IgnoreCase))
            { expected = header; break; }
        }
        if (Gate.Missing(_out, "external script pack", expected == null)) return;

        var b = Load(root!);
        Assert.Equal(expected, Read(b, "<SERV.LOOKUPSKILL Farming>"));
        Assert.Equal("0", Read(b, "<SERV.LOOKUPSKILL Alchemy>"));
    }

    /// <summary>A Source-X pack that defines [FUNCTION RTIMESQL] keeps answering
    /// &lt;SERV.RTIMESQL&gt; with it, and its SERV.&lt;name&gt; functions keep answering
    /// their reads, exactly as before the custom-version key existed.</summary>
    [Fact]
    public void SourceXPack_PackFunctionsStillAnswerServReads()
    {
        string? root = PackSourceX();
        if (Gate.Missing(_out, "external script pack", root == null)) return;
        var b = Load(root!);

        if (b.Stack.Runner.HasFunction("RTIMESQL"))
            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"), Read(b, "<SERV.RTIMESQL>"));
        if (b.Stack.Runner.HasFunction("SERV.DAYNAME"))
        {
            string day = Read(b, "<SERV.DAYNAME>");
            Assert.False(string.IsNullOrWhiteSpace(day) || day == "0", $"SERV.DAYNAME answered '{day}'");
        }
        Assert.Equal("0", Read(b, "<SERV.LOOKUPSKILL Alchemy>"));

        // The bare Source-X statements and their SERV spellings land in one store.
        b.Stack.Interpreter.Execute(
        [
            new ScriptKey("VAR0.probe_bare", "3"),
            new ScriptKey("SERV.VAR0.probe_serv", "4"),
        ], b.Ch, null, new TriggerArgs { Source = b.Ch }, new ScriptScope());
        Assert.Equal("3", Read(b, "<EVAL <SERV.VAR0.probe_bare>>"));
        Assert.Equal("4", Read(b, "<EVAL <VAR0.probe_serv>>"));
    }
}
