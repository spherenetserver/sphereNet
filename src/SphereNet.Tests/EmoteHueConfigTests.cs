using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

/// <summary>An engine emote takes its hue from COLOREMOTE: Sphere 0.56b's red 0x22 by
/// default (CObjBase.cpp:358), 0x3B2 for the Source-X grey (CObjBase.cpp:671). The
/// notoriety name hues default to 0.56b's as well (CResource.cpp:210-216).</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class EmoteHueConfigTests
{
    [Fact]
    public void TheColourDefaultsAre56bAndTheIniSetsThem()
    {
        var defaults = new SphereConfig();
        Assert.Equal(0x0022, defaults.ColorEmote);
        Assert.Equal(0x0063, defaults.ColorNotoGood);
        Assert.Equal(0x0026, defaults.ColorNotoEvil);
        Assert.Equal(0x03B2, defaults.ColorNotoCriminal);
        Assert.Equal(0x0044, defaults.ColorNotoGuildSame);
        Assert.Equal(0x002B, defaults.ColorNotoGuildWar);

        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_emote_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, "[SPHERE]\nColorEmote=03b2\n");
        try
        {
            var ini = new IniParser();
            ini.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(ini);
            Assert.Equal(0x03B2, config.ColorEmote);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void AnEmoteGoesOutInTheConfiguredHue()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Name = "Gazer";
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        byte[]? sent = null;
        var saved = Character.BroadcastNearby;
        Character.BroadcastNearby = (_, _, packet, _) => sent = packet.Build().Span.ToArray();
        ObjBase.EmoteHue = 0x03B2;
        try
        {
            ch.EmoteObject("looks ill");
        }
        finally
        {
            Character.BroadcastNearby = saved;
            ObjBase.EmoteHue = 0x0022;
        }
        Assert.NotNull(sent);
        Assert.Equal(0x1C, sent![0]);                 // ASCII speech
        Assert.Equal(2, sent[9]);                     // TALKMODE_EMOTE
        Assert.Equal(0x03B2, (sent[10] << 8) | sent[11]);
    }
}
