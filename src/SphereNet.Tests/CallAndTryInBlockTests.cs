using Microsoft.Extensions.Logging;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// CALL and the TRY family work wherever they are written.
///
/// Both are single statements rather than blocks, and both lived only in Execute's
/// switch. Inside an IF, or as the line a DORAND picked, they fell through to
/// ExecuteLine - which does not know either - and did nothing at all, silently: a
/// CALL that never happens looks exactly like a function that did nothing.
///
/// The shipped packs write 84 of them inside an IF, most in the admin and crafting
/// dialogs. A bare function name always worked, which is why the hole was easy to
/// miss - "f_something" inside an IF runs, "CALL f_something" did not.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CallAndTryInBlockTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_ct_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private string Run(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "f.scp");
        File.WriteAllText(file,
            "[FUNCTION f_probe_mark]" + Nl +
            "TAG.X=<eval <tag.x>+1>" + Nl);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(file);

        var body = new List<ScriptKey>();
        foreach (string l in lines)
        {
            int eq = l.IndexOf('=');
            int sp = l.IndexOf(' ');
            if (eq > 0 && (sp < 0 || eq < sp)) body.Add(new ScriptKey(l[..eq], l[(eq + 1)..]));
            else if (sp > 0) body.Add(new ScriptKey(l[..sp], l[(sp + 1)..]));
            else body.Add(new ScriptKey(l, ""));
        }

        var item = new Item();
        stack.Interpreter.Execute(body, item, null, new TriggerArgs(), new ScriptScope());
        item.TryGetProperty("TAG.X", out string v);
        return v;
    }

    /// <summary>Top level and inside an IF have to agree.</summary>
    [Fact]
    public void ACallInsideAnIfRuns()
    {
        Assert.Equal("1", Run("TAG.X=0", "CALL f_probe_mark"));
        Assert.Equal("1", Run("TAG.X=0", "IF (1)", "CALL f_probe_mark", "ENDIF"));
    }

    /// <summary>And as the line a DORAND picks.</summary>
    [Fact]
    public void ACallAsThePickedLineRuns()
    {
        Assert.Equal("1", Run("TAG.X=0", "DORAND 1", "CALL f_probe_mark", "ENDDO"));
        Assert.Equal("1", Run("TAG.X=0", "DOSWITCH 0", "CALL f_probe_mark", "ENDDO"));
    }

    /// <summary>A loop body already went through Execute, so this always worked - it
    /// is the control case that says the harness is honest.</summary>
    [Fact]
    public void ACallInsideALoopStillRunsEveryPass()
    {
        Assert.Equal("3", Run("TAG.X=0", "FOR 1 3", "CALL f_probe_mark", "ENDFOR"));
    }

    /// <summary>TRY runs the rest of its line, inside an IF as well as outside.</summary>
    [Fact]
    public void ATryInsideAnIfRuns()
    {
        Assert.Equal("5", Run("TAG.X=0", "TRY TAG.X=5"));
        Assert.Equal("5", Run("TAG.X=0", "IF (1)", "TRY TAG.X=5", "ENDIF"));
    }

    /// <summary>An untaken branch runs neither.</summary>
    [Fact]
    public void AnUntakenBranchRunsNeither()
    {
        Assert.Equal("0", Run("TAG.X=0", "IF (0)", "CALL f_probe_mark", "ENDIF"));
        Assert.Equal("0", Run("TAG.X=0", "IF (0)", "TRY TAG.X=5", "ENDIF"));
    }
}
