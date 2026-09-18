using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// A statement whose KEY is built from brackets writes to the key those brackets name.
///
/// Upstream handles this explicitly: when a line's key contains '&lt;' it rejoins the
/// key and the raw argument, parses the whole line, and re-splits the key from the
/// result (CScriptObj.cpp:2506). That is what lets a loop write to a member it picked
/// at runtime.
///
/// The packs use it to walk the skill table: SRC.&lt;SERV.SKILL.&lt;n&gt;.NAME&gt; -= 0.1
/// lowers the skill the loop is on. If the key is not expanded the write lands on a
/// member literally named "&lt;SERV.SKILL...&gt;", which nothing ever reads back - the
/// line looks like it worked and changed nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BracketedStatementKeyTests
{
    /// <summary>Run the lines against a fresh item and hand back one of its tags.</summary>
    private static string Run(string readBack, params string[] lines)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();

        // Build the lines with the parser the loader uses, not a hand-rolled split -
        // the separator rules (quotes, brackets, ++ / --) live in there.
        var body = new List<ScriptKey>();
        foreach (string l in lines)
        {
            var k = new ScriptKey();
            k.Parse(l.AsSpan());
            body.Add(k);
        }

        var item = new Item();
        stack.Interpreter.Execute(body, item, null, new TriggerArgs(), new ScriptScope());
        item.TryGetProperty(readBack, out string v);
        return v;
    }

    /// <summary>The plain form: the key names itself through another tag.</summary>
    [Fact]
    public void TheKeyIsExpandedBeforeTheWrite()
        => Assert.Equal("7", Run("TAG.CHOSEN", "TAG.SEL=CHOSEN", "TAG.<TAG.SEL>=7"));

    /// <summary>A key that only exists once the brackets are expanded: nothing can
    /// read it back unless the expansion happened before the write.</summary>
    [Fact]
    public void TheWriteLandsOnTheExpandedNameOnly()
    {
        Assert.Equal("7", Run("TAG.CHOSEN", "TAG.SEL=CHOSEN", "TAG.<TAG.SEL>=7"));
        Assert.NotEqual("7", Run("TAG.SEL", "TAG.SEL=CHOSEN", "TAG.<TAG.SEL>=7"));
    }

    /// <summary>The compound form, which is how the skill loop is written.</summary>
    [Fact]
    public void ACompoundAssignmentExpandsItsKeyToo()
        => Assert.Equal("10", Run("TAG.CHOSEN",
            "TAG.SEL=CHOSEN", "TAG.CHOSEN=7", "TAG.<TAG.SEL> +=3"));

    /// <summary>Two levels, the shape the pack actually writes - the index is itself
    /// a bracket.</summary>
    [Fact]
    public void TheNameMayItselfBeBuiltFromABracket()
        => Assert.Equal("4", Run("TAG.CHOSEN",
            "TAG.IDX=SEL", "TAG.SEL=CHOSEN", "TAG.<TAG.<TAG.IDX>>=4"));

}
