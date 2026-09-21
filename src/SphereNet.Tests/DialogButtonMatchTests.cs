using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogButtonMatchTests
{
    [Theory]
    [InlineData("16", 16, true)]
    [InlineData("010", 16, true)]
    [InlineData("8+8", 16, true)]
    [InlineData("(8 + 8)", 16, true)]
    [InlineData("button_min", 16, true)]
    [InlineData("button_min,button_max", 18, true)]
    [InlineData("(8 + 8), (10 + 10)", 20, true)]
    [InlineData("16 , 20", 18, true)]
    [InlineData("16 20", 21, false)]
    [InlineData("20,16", 18, false)]
    [InlineData("0FFFFFFFF", -1, true)]
    [InlineData("0FFFFFFFE,0FFFFFFFF", -1, true)]
    [InlineData("-1,1", 0, false)]
    [InlineData("16,20,999", 18, true)]
    public void ButtonSelectorsFollowSourceXNumericSemantics(string selector, int button, bool matches)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"button-match-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[DEFNAME button_test]\nbutton_min=16\nbutton_max=20\n[DIALOG d_match BUTTON]\nON={selector}\nTAG.MATCHED=1\n");
            stack.Resources.LoadResourceFile(path);
            Assert.True(stack.Resources.TryGetDialogButton("d_match", out var section));
            var world = TestHarness.CreateWorld();
            var subject = world.CreateItem();
            Assert.Equal(matches, stack.Runner.TryRunDialogButton(section, button, subject, null, new TriggerArgs()));
            Assert.Equal(matches, subject.TryGetTag("MATCHED", out _));
        }
        finally { File.Delete(path); }
    }
}
