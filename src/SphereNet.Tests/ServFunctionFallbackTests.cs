using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// A SERV read the server cannot answer falls through to a [FUNCTION].
///
/// Upstream, a key SERV does not recognise is tried as a function before anything
/// else: r_GetFunctionIndex on the remaining key, and only then the parent's own
/// table (CServerDef.cpp:509). Here the SERV branch returned "0" when the resolver
/// declined, so resolution ENDED at the server and a pack function written to answer
/// a SERV read could never run - and the failure was invisible, because "0" is a
/// perfectly ordinary answer.
///
/// The live pack's date line is exactly this shape: it reads &lt;SERV.DAYNAME&gt;,
/// which its own [FUNCTION SERV.DAYNAME] exists to answer, and got "0" where the
/// weekday belongs.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ServFunctionFallbackTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_sff_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    /// <summary>Run one line and hand back the TAG it wrote, which is how the value
    /// the expression produced is observed.</summary>
    private string RunAndRead(string functionSection, string line)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "f.scp");
        File.WriteAllText(file, functionSection);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(file);

        int eq = line.IndexOf('=');
        var body = new[] { new SphereNet.Scripting.Parsing.ScriptKey(line[..eq], line[(eq + 1)..]) };

        var item = new Item();
        stack.Interpreter.Execute(body, item, null, new SphereNet.Scripting.Execution.TriggerArgs(),
            new SphereNet.Scripting.Execution.ScriptScope());
        return item.TryGetProperty("TAG.OUT", out string v) ? v : "<unset>";
    }

    /// <summary>The pack's own spelling: a function whose section name carries the
    /// SERV prefix, answering the whole token.</summary>
    [Fact]
    public void AFunctionNamedWithTheServPrefixAnswersTheRead()
    {
        string result = RunAndRead(
            "[FUNCTION SERV.DAYNAME]" + Nl +
            "RETURN Thursday" + Nl,
            "TAG.OUT=<SERV.DAYNAME>");

        Assert.Equal("Thursday", result);
    }

    /// <summary>And the spelling upstream uses: the remainder after the prefix.</summary>
    [Fact]
    public void AFunctionNamedByTheRemainderAnswersTheRead()
    {
        string result = RunAndRead(
            "[FUNCTION f_probe_serv_read]" + Nl +
            "RETURN 42" + Nl,
            "TAG.OUT=<SERV.f_probe_serv_read>");

        Assert.Equal("42", result);
    }

    /// <summary>A SERV name nothing answers still reads back as it did - "0" - so
    /// the change does not move the ground under every existing script.</summary>
    [Fact]
    public void AnUnansweredServReadIsStillZero()
    {
        string result = RunAndRead(
            "[FUNCTION f_probe_unused]" + Nl +
            "RETURN 1" + Nl,
            "TAG.OUT=<SERV.NOTHING_ANSWERS_THIS>");

        Assert.Equal("0", result);
    }
}
