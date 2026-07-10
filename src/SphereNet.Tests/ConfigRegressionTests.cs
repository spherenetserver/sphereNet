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
            SpeedScaleFactor=-5
            StateRecordMoveScanMs=10
            StateRecordSnapshotMs=10
            MacroMaxSteps=1000
            MacroMaxLoopMinutes=0
            OptionFlags=0x0080
            SpeechSelf=spk_player
            SpeechPet=spk_pet
            EventsPet=e_npc_generic_event
            EventsPlayer=e_player_generic_event,e_player_crafting_event
            EventsRegion=e_region_generic_event
            EventsItem=ei_generic_event
            AdvancedLos=3
            ColorNotoGood=059
            ColorNotoGoodNPC=05a
            ColorNotoGuildSame=03f
            ColorNotoNeutral=03b2
            ColorNotoCriminal=03b3
            ColorNotoGuildWar=090
            ColorNotoEvil=022
            ColorNotoInvul=035
            ColorNotoInvulGameMaster=0b
            ColorNotoDefault=03b4
            ColorInvisItem=1000
            ColorInvis=04001
            ColorInvisSpell=04002
            ColorHidden=04003
            PetsInheritNotoriety=07a
            ChatFlags=0x10
            GenericSounds=0
            HearAll=1
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
            Assert.Equal(1, config.SpeedScaleFactor);
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
            Assert.Equal(3, config.AdvancedLos);
            Assert.Equal(0x0059, config.ColorNotoGood);
            Assert.Equal(0x005A, config.ColorNotoGoodNpc);
            Assert.Equal(0x003F, config.ColorNotoGuildSame);
            Assert.Equal(0x03B2, config.ColorNotoNeutral);
            Assert.Equal(0x03B3, config.ColorNotoCriminal);
            Assert.Equal(0x0090, config.ColorNotoGuildWar);
            Assert.Equal(0x0022, config.ColorNotoEvil);
            Assert.Equal(0x0035, config.ColorNotoInvul);
            Assert.Equal(0x000B, config.ColorNotoInvulGameMaster);
            Assert.Equal(0x03B4, config.ColorNotoDefault);
            Assert.Equal(0x1000, config.ColorInvisItem);
            Assert.Equal(0x4001, config.ColorInvis);
            Assert.Equal(0x4002, config.ColorInvisSpell);
            Assert.Equal(0x4003, config.ColorHidden);
            Assert.Equal(0x07A, config.PetsInheritNotoriety);
            Assert.Equal(0x10, config.ChatFlags);
            Assert.False(config.GenericSounds);
            Assert.True((config.LogMask & SphereConfig.LogMaskPlayerSpeak) != 0);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // Case-insensitive INI keys mean COLORNOTOGOOD=0 maps to ColorNotoGood. A literal 0
    // would paint the click name label black; the loader must fall back to the built-in
    // colour instead. Invisibility hues keep an explicit 0 (it means "no tint").
    [Fact]
    public void SphereConfig_NotoHueZero_FallsBackToDefaultButInvisKeepsZero()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_cfg_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, """
            [SPHERE]
            COLORNOTOGOOD=0
            COLORNOTOEVIL=0
            COLORNOTOCRIMINAL=0
            COLORNOTODEFAULT=0
            ColorInvis=0
            """);

        try
        {
            var parser = new IniParser();
            parser.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(parser);

            // Noto hues: 0 → built-in default (never black).
            Assert.Equal(0x0059, config.ColorNotoGood);
            Assert.Equal(0x0022, config.ColorNotoEvil);
            Assert.Equal(0x03B2, config.ColorNotoCriminal);
            Assert.Equal(0x03B2, config.ColorNotoDefault);

            // Invisibility hue: 0 stays 0 (explicit "no tint").
            Assert.Equal(0, config.ColorInvis);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
