using SphereNet.Core.Configuration;

namespace SphereNet.Tests;

public class ConfigRegressionTests
{
    [Fact]
    public void SphereConfig_LoadFromIni_AppliesOperationalDefaultsAndClamps()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_cfg_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, """
            [SPHERE]
            AdminPassword=change-me
            AccApp=0
            ClientEra=Modern
            SaveShards=99
            SaveShardSizeMb=-5
            ItemDurabilityLossChance=150
            ItemDurabilityLossMin=3
            ItemDurabilityLossMax=1
            StateRecordMoveScanMs=10
            StateRecordSnapshotMs=10
            MacroMaxSteps=1000
            MacroMaxLoopMinutes=0
            OptionFlags=0x0020
            SpeechSelf=spk_player
            SpeechPet=spk_pet
            EventsPet=e_npc_generic_event
            EventsPlayer=e_player_generic_event,e_player_crafting_event
            EventsRegion=e_region_generic_event
            EventsItem=ei_generic_event
            """);

        try
        {
            var parser = new IniParser();
            parser.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(parser);

            Assert.Equal("change-me", config.AdminPassword);
            Assert.Equal(0, config.AccApp);
            Assert.Equal(ClientEra.Modern, config.ClientEra);
            Assert.Equal(16, config.SaveShards);
            Assert.Equal(0, config.SaveShardSizeMb);
            Assert.Equal(100, config.ItemDurabilityLossChance);
            Assert.Equal(3, config.ItemDurabilityLossMin);
            Assert.Equal(3, config.ItemDurabilityLossMax);
            Assert.Equal(500, config.StateRecordMoveScanMs);
            Assert.Equal(5000, config.StateRecordSnapshotMs);
            Assert.Equal(200, config.MacroMaxSteps);
            Assert.Equal(1, config.MacroMaxLoopMinutes);
            Assert.True(config.HasFileCommands);
            Assert.Equal("spk_player", config.SpeechSelf);
            Assert.Equal("spk_pet", config.SpeechPet);
            Assert.Equal("e_npc_generic_event", config.EventsPet);
            Assert.Equal("e_player_generic_event,e_player_crafting_event", config.EventsPlayer);
            Assert.Equal("e_region_generic_event", config.EventsRegion);
            Assert.Equal("ei_generic_event", config.EventsItem);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
