using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Field-report probe: F_RESTYPES_SETUP printed "Output done in 1784107411
/// seconds" (local.hour read back as 0) and its CREATE TABLE never took
/// effect ('&lt;LOCAL.TABLENAME&gt;' suspected empty). Verifies LOCAL.X survives
/// across lines inside a [FUNCTION] in both spellings the pack uses:
/// "LOCAL.TABLENAME = restypes" (spaced) and "local.hour=&lt;EVAL ...&gt;".
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptLocalScopeProbe
{
    private readonly ITestOutputHelper _out;
    public ScriptLocalScopeProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void FunctionLocals_PersistAcrossLines_BothSpellings()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_localscope_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [FUNCTION f_local_scope_probe]
            LOCAL.TABLENAME = restypes
            local.hour=123
            SRC.TAG.PROBE_A=<LOCAL.TABLENAME>
            SRC.TAG.PROBE_B=<EVAL 500-<local.hour>>
            IF (1)
                LOCAL.INSIDE = restypes
                local.t2=<EVAL 100+23>
                SRC.TAG.PROBE_C=<LOCAL.INSIDE>
                SRC.TAG.PROBE_D=<EVAL 500-<local.t2>>
            ENDIF
            SRC.TAG.PROBE_E=<LOCAL.INSIDE>
            """);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
            resources.LoadResourceFile(tempFile);
            var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
            var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            var args = new SphereNet.Scripting.Execution.TriggerArgs(ch, 0, 0, "");
            runner.RunFunction("f_local_scope_probe", ch, null, args);

            ch.TryGetTag("PROBE_A", out string? a);
            ch.TryGetTag("PROBE_B", out string? b);
            ch.TryGetTag("PROBE_C", out string? c);
            ch.TryGetTag("PROBE_D", out string? d);
            ch.TryGetTag("PROBE_E", out string? e);
            _out.WriteLine($"A='{a}' B='{b}' C='{c}' D='{d}' E='{e}'");

            Assert.Equal("restypes", a);
            Assert.Equal("377", b);
            Assert.Equal("restypes", c); // set inside IF, read inside IF
            Assert.Equal("377", d);      // angle-bracket RHS inside IF
            Assert.Equal("restypes", e); // IF must not open a new local scope
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
