using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Engine gaps found auditing the live systems/ script pack — all Source-X-
/// present (pack-custom tokens skipped per the maintainer rule). Covers the
/// per-char dynamic SPEECH list (Source-X CChar m_Speech / DSPEECH + ISDSPEECH)
/// and the TARGPRV round-trip. The SERV.GUILDS/GUILDSTONES iteration (#55) and
/// GUILD/TOWN keyword forwarding (#54) run in the server property resolver /
/// stone-flow paths exercised by TownStoneTests.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class SystemsPackParityTests
{
    private static GameWorld World()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (TriggerDispatcher Dispatcher, ResourceHolder Resources) Dispatcher(string scriptText)
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_sys_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, scriptText);
        resources.LoadResourceFile(tmp);

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        return (new TriggerDispatcher { Resources = resources, Runner = runner }, resources);
    }

    [Fact]
    public void DSpeech_AppendRemove_AndIsDSpeechMembership()
    {
        var world = World();
        var ch = world.CreateCharacter();

        // Not a member until added.
        Assert.True(ch.TryGetProperty("ISDSPEECH.spk_townspeech", out string before));
        Assert.Equal("0", before);

        // DSPEECH +<x> appends.
        Assert.True(ch.TryExecuteCommand("DSPEECH", "+spk_townspeech", null!));
        Assert.True(ch.TryGetProperty("ISDSPEECH.spk_townspeech", out string added));
        Assert.Equal("1", added);

        // Idempotent — no duplicate entry.
        Assert.True(ch.TryExecuteCommand("DSPEECH", "+spk_townspeech", null!));
        Assert.Single(ch.DSpeech);

        // A second fragment coexists.
        Assert.True(ch.TryExecuteCommand("DSPEECH", "+spk_guildspeech", null!));
        Assert.Equal(2, ch.DSpeech.Count);

        // DSPEECH -<x> removes just that one.
        Assert.True(ch.TryExecuteCommand("DSPEECH", "-spk_townspeech", null!));
        Assert.True(ch.TryGetProperty("ISDSPEECH.spk_townspeech", out string removed));
        Assert.Equal("0", removed);
        Assert.True(ch.TryGetProperty("ISDSPEECH.spk_guildspeech", out string kept));
        Assert.Equal("1", kept);
    }

    [Fact]
    public void DSpeech_BareSingleValue_Appends_MultiValue_Replaces()
    {
        var world = World();
        var ch = world.CreateCharacter();

        // Successive bare single-value assigns accumulate (save/load convention).
        Assert.True(ch.TrySetProperty("DSPEECH", "spk_townspeech"));
        Assert.True(ch.TrySetProperty("DSPEECH", "spk_guildspeech"));
        Assert.Equal(2, ch.DSpeech.Count);

        // A multi-value assignment replaces the whole list.
        Assert.True(ch.TrySetProperty("DSPEECH", "spk_a spk_b"));
        Assert.Equal(2, ch.DSpeech.Count);
        Assert.True(ch.TryGetProperty("ISDSPEECH.spk_a", out string a));
        Assert.Equal("1", a);
        Assert.True(ch.TryGetProperty("ISDSPEECH.spk_townspeech", out string gone));
        Assert.Equal("0", gone);
    }

    [Fact]
    public void DSpeech_FiresWhenCharHearsSpeech_BeforeCharDefSpeech()
    {
        var (dispatcher, _) = Dispatcher("""
            [SPEECH spk_townspeech]
            ON=*hail*
            TAG.HEARD_TOWN=1
            RETURN 1
            """);

        var hearer = new Character { IsPlayer = false };
        var speaker = new Character { IsPlayer = true };

        // No per-char list yet — the townsfolk block does not fire.
        var miss = dispatcher.FireSpeechTrigger(hearer, speaker, "hail there", 0);
        Assert.NotEqual(TriggerResult.True, miss);

        // Attach the town speech fragment to this char, as the join flow does.
        Assert.True(hearer.TryExecuteCommand("DSPEECH", "+spk_townspeech", null!));

        var hit = dispatcher.FireSpeechTrigger(hearer, speaker, "hail there", 0);
        Assert.Equal(TriggerResult.True, hit);
        Assert.True(hearer.TryGetProperty("TAG.HEARD_TOWN", out string heard));
        Assert.Equal("1", heard);
    }

    [Fact]
    public void Targprv_RoundTripsAsTag()
    {
        var world = World();
        var ch = world.CreateCharacter();

        Assert.True(ch.TrySetProperty("TARGPRV", "3"));
        Assert.True(ch.TryGetProperty("TAG.TARGPRV", out string stored));
        Assert.Equal("3", stored);
    }
}
