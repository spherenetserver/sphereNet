using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A verb's numeric argument may be a DEFNAME or a Sphere number.
///
/// Upstream runs these through the expression evaluator - Str_ParseCmds over an int64
/// array (CClient.cpp:1438) - so a leading zero means hex and a DEFNAME resolves to its
/// number. The reader here took 0x-prefixed hex and plain decimal only, and refused
/// everything else, which meant the verb did nothing at all.
///
/// That is how the shipped region script lost its music. regiontypes.scp writes
///
///     SRC.MIDILIST=midi_britain1,midi_ForestA,midi_JungleA,...
///
/// and every one of those names failed to parse, so entering a city sent no track. The
/// same reader serves SOUND and ANIM, whose arguments the packs write as names too.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VerbNumericArgumentTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_verb_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed record Bench(SphereNet.Game.World.GameWorld World,
                                SphereNet.Game.Objects.Characters.Character Me,
                                List<ushort> MusicSent);

    /// <summary>Publish the sound defnames the pack ships and hand back a character
    /// whose music packets are captured.</summary>
    private Bench Build()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "d.scp");
        File.WriteAllLines(file, [
            "[DEFNAME sounds_midi]",
            "midi_britain1 9",
            "midi_forest_a 10",
            "snd_probe_hit 0100",
        ]);
        using var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        res.LoadResourceFile(file);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));

        var sent = new List<ushort>();
        SphereNet.Game.Objects.Characters.Character.SendPacketToOwner = (ch, pkt) =>
        {
            var span = pkt.Build().Span;
            if (span.Length >= 3 && span[0] == 0x6D)
                sent.Add((ushort)((span[1] << 8) | span[2]));
        };
        return new Bench(world, me, sent);
    }

    /// <summary>The shipped region line.</summary>
    [Fact]
    public void MidilistAcceptsTheDefnamesThePackWrites()
    {
        var b = Build();
        Assert.True(b.Me.TryExecuteCommand("MIDILIST", "midi_britain1", null!));
        Assert.Equal([9], b.MusicSent);
    }

    /// <summary>A list picks one of them - and it is one of the ones named, not
    /// nothing.</summary>
    [Fact]
    public void AListPicksOneOfTheNamedTracks()
    {
        var b = Build();
        Assert.True(b.Me.TryExecuteCommand("MIDILIST", "midi_britain1,midi_forest_a",
            null!));
        Assert.Single(b.MusicSent);
        Assert.Contains(b.MusicSent[0], new ushort[] { 9, 10 });
    }

    /// <summary>A plain number still works, and so does the leading-zero hex the
    /// language writes.</summary>
    [Theory]
    [InlineData("9", 9)]
    [InlineData("011", 17)]
    [InlineData("0x11", 17)]
    public void NumbersStillRead(string written, int expected)
    {
        var b = Build();
        b.Me.TryExecuteCommand("MIDILIST", written, null!);
        Assert.Equal([(ushort)expected], b.MusicSent);
    }

    /// <summary>A name nothing defines still sends nothing rather than a track 0.</summary>
    [Fact]
    public void AnUnknownNameSendsNothing()
    {
        var b = Build();
        b.Me.TryExecuteCommand("MIDILIST", "midi_no_such_tune", null!);
        Assert.Empty(b.MusicSent);
    }
}
