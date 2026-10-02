using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using Xunit;

namespace SphereNet.Tests;

/// <summary>JOB is CCharBase::GetTradeName (CCharBase.cpp:252): the definition NAME
/// without its name-list word and "the ". A pack's click line "<NAME>, The <JOB>" on a
/// player vendor read "Leland, The 0" because a character did not answer JOB.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharJobTradeNameTests
{
    [Fact]
    public void ACharacterReadsItsDefinitionsTradeNameAsJob()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string script = Path.Combine(Path.GetTempPath(), $"job_{Guid.NewGuid():N}.scp");
        File.WriteAllText(script,
            "[CHARDEF 0190]\r\nDEFNAME=c_man\r\nNAME=#NAMES_HUMANMALE the Man\r\n\r\n" +
            "[chardef c_player_vendor]\r\ndefname c_player_vendor\r\nname #names_humanmale the Player Vendor\r\nid c_man\r\n\r\n" +
            "[CHARDEF c_plain_name]\r\nNAME=Dragon\r\nID=c_man\r\n");
        try { stack.Resources.LoadResourceFile(script); }
        finally { File.Delete(script); }
        new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        string Job(string defName)
        {
            var ch = world.CreateCharacter();
            ch.CharDefIndex = stack.Resources.ResolveDefName(defName).Index;
            Assert.True(ch.TryGetProperty("JOB", out string job));
            return job;
        }

        Assert.Equal("Player Vendor", Job("c_player_vendor"));
        Assert.Equal("Man", Job("c_man"));
        Assert.Equal("Dragon", Job("c_plain_name"));
    }
}
