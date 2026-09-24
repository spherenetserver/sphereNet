using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Expression reads the worldgen spawner relies on. A bare resource name -
/// or a [DEFNAME] alias of one - stands for its resource in a numeric context
/// (GetSingle falls back to ResourceGetID), so IF (&lt;LOCAL.SPAWN_ARRAY&gt;) holding
/// "giantserpent,gianttoad" is true; STRARG ends its first argument at a comma as well
/// as at whitespace (CScriptObj.cpp:864).</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ResourceNameExpressionTests
{
    private static Dictionary<string, string> Run(params ScriptKey[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"resname-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path,
                "[CHARDEF c_resname_probe]\nID=c_man\n" +
                "[DEFNAME resname_defs]\nresname_alias {c_resname_probe 1}\n" +
                "[EOF]\n");
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            stack.Interpreter.Execute(lines, ch, null, new TriggerArgs(), new ScriptScope());
            var tags = new Dictionary<string, string>();
            foreach (var k in new[] { "A", "B", "C", "D", "E" })
                if (ch.TryGetTag(k, out string? v) && v != null) tags[k] = v;
            return tags;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AResourceNameIsNonZeroInAnExpression()
    {
        var tags = Run(
            new ScriptKey("LOCAL.LIST", "resname_alias,another_name"),
            new ScriptKey("IF", "(<LOCAL.LIST>)"),
            new ScriptKey("TAG.A", "listed"),
            new ScriptKey("ENDIF", ""),
            new ScriptKey("TAG.B", "<EVAL c_resname_probe>"),
            new ScriptKey("TAG.C", "<EVAL resname_alias>"),
            new ScriptKey("TAG.D", "<EVAL no_such_resource_name>"));

        Assert.Equal("listed", tags["A"]);
        Assert.NotEqual("0", tags["B"]);
        Assert.Equal(tags["B"], tags["C"]);
        Assert.Equal("0", tags["D"]);
    }

    [Fact]
    public void ANonResourceWordStillComparesAsText()
    {
        var tags = Run(
            new ScriptKey("LOCAL.WORD", "healer"),
            new ScriptKey("IF", "(<LOCAL.WORD> == healer)"),
            new ScriptKey("TAG.A", "same"),
            new ScriptKey("ENDIF", ""));

        Assert.Equal("same", tags["A"]);
    }

    [Fact]
    public void ResourceTypeAndIndexAnswerWithoutAClient()
    {
        // A spawner's @Timer runs with no client attached; the worldgen checks every
        // spawn-list member with RESOURCEINDEX there.
        var tags = Run(
            new ScriptKey("TAG.A", "<RESOURCETYPE {c_resname_probe 1}>"),
            new ScriptKey("TAG.B", "<RESOURCEINDEX resname_alias>"),
            new ScriptKey("TAG.C", "<RESOURCEINDEX no_such_resource_name>"));

        Assert.Equal("07", tags["A"]);
        Assert.NotEqual("0", tags["B"]);
        Assert.Equal("0", tags["C"]);
    }

    [Theory]
    [InlineData("a,b,c", "a")]
    [InlineData("a b c", "a")]
    [InlineData("\"quoted rest", "quoted")]
    public void StrArgStopsAtCommaOrSpace(string input, string expected)
    {
        var tags = Run(new ScriptKey("TAG.A", $"<STRARG {input}>"));
        Assert.Equal(expected, tags["A"]);
    }
}
