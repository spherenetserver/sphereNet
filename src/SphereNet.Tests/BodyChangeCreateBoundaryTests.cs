using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class BodyChangeCreateBoundaryTests
{
    private static ScriptRuntimeStack Load()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"body-create-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[CHARDEF 0190]\nDEFNAME=c_man\n[CHARDEF 03db]\nDEFNAME=c_man_gm\n");
        try { stack.Resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        return stack;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingCharacterBodyChangeDoesNotRunCreate(bool player)
    {
        var stack = Load();
        var ch = TestHarness.CreateWorld().CreateCharacter();
        ch.IsPlayer = player;
        int calls = 0;
        var previous = CharDefHelper.AfterApplyDefName;
        try
        {
            CharDefHelper.AfterApplyDefName = _ => calls++;
            Assert.True(ch.TrySetProperty("BODY", "c_man_gm"));
            Assert.Equal(0x03DB, ch.BodyId);
            Assert.Equal(0, calls);
        }
        finally { CharDefHelper.AfterApplyDefName = previous; }
    }

    [Fact]
    public void FreshNpcCreateMayChangeBodyWithoutReenteringCreate()
    {
        var stack = Load();
        var ch = TestHarness.CreateWorld().CreateCharacter();
        int calls = 0;
        var previous = CharDefHelper.AfterApplyDefName;
        try
        {
            // Bound the callback so a regression fails an assertion, not the test host.
            CharDefHelper.AfterApplyDefName = target =>
            {
                if (++calls < 3) target.TrySetProperty("BODY", "c_man_gm");
            };
            Assert.True(CharDefHelper.TryApplyDefName(ch, "c_man", stack.Resources,
                refresh: false, fireCreate: true));
            Assert.Equal(1, calls);
            Assert.Equal(0x03DB, ch.BodyId);
        }
        finally { CharDefHelper.AfterApplyDefName = previous; }
    }

    [Fact]
    public void LoginAppearanceRepairDoesNotRunCreate()
    {
        var stack = Load();
        var ch = TestHarness.CreateWorld().CreateCharacter();
        ch.IsPlayer = true;
        ch.CharDefIndex = 0x03DB;
        ch.BodyId = 0x03DB;
        ch.SetTag("CHARDEF", "c_man_gm");
        int calls = 0;
        var previous = CharDefHelper.AfterApplyDefName;
        try
        {
            CharDefHelper.AfterApplyDefName = _ => calls++;
            CharDefHelper.EnsureDisplayBody(ch, stack.Resources);
            Assert.Equal(0x03DB, ch.BodyId);
            Assert.Equal(0, calls);
        }
        finally { CharDefHelper.AfterApplyDefName = previous; }
    }
}
