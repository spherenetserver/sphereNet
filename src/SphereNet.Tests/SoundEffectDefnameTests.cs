using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Network.Packets;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// SOUND and EFFECT take a DEFNAME, and their numbers are Sphere numbers.
///
/// The event scripts name what they play: "SOUND snd_spell_poison",
/// "EFFECT 0,i_fx_fireball,10,16,0,044,4". Neither id parsed as a number, so the
/// verb returned success and nothing was heard or seen - twenty-seven such SOUND
/// calls and nineteen such EFFECT calls across the packs. The numeric arguments had
/// the second half of the same problem: a leading zero means HEX everywhere else in
/// the language (ScriptNumber.TryParseToken), so the hue written 044 was being read
/// as 44 - an effect in a plausible but wrong colour, which is the kind of thing
/// nobody reports.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SoundEffectDefnameTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_snd_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => null;
    }

    private void LoadDefs()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "defs.scp");
        File.WriteAllText(file,
            "[DEFNAME sounds_probe]" + Nl +
            "snd_probe_poison   517" + Nl + Nl +
            "[ITEMDEF 036b0]" + Nl +
            "DEFNAME=i_fx_probe" + Nl +
            "TYPE=t_normal" + Nl);
        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private static (GameWorld World, Character Ch, List<PacketWriter> Sent) Rig()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var sent = new List<PacketWriter>();
        ObjBase.BroadcastNearby = (_, _, packet, _) => sent.Add(packet);

        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch, sent);
    }

    /// <summary>A sound named by defname is heard. Before this the verb consumed the
    /// line and broadcast nothing at all.</summary>
    [Fact]
    public void ASoundNamedByDefnameIsBroadcast()
    {
        LoadDefs();
        var (_, ch, sent) = Rig();

        Assert.True(ch.TryExecuteCommand("SOUND", "snd_probe_poison", new Console(), out bool owned));
        Assert.True(owned);
        Assert.Single(sent);
    }

    /// <summary>An unknown name still plays nothing - the resolution is a lookup, not
    /// a licence to broadcast whatever number a bad token happens to look like.</summary>
    [Fact]
    public void AnUnknownSoundNameStillPlaysNothing()
    {
        LoadDefs();
        var (_, ch, sent) = Rig();

        ch.TryExecuteCommand("SOUND", "snd_no_such_sound_at_all", new Console(), out _);
        Assert.Empty(sent);
    }

    /// <summary>The numeric form keeps working, and a leading zero is HEX as it is
    /// everywhere else in the language.</summary>
    [Fact]
    public void ANumericSoundStillWorksAndALeadingZeroIsHex()
    {
        LoadDefs();
        var (_, ch, sent) = Rig();

        Assert.True(ch.TryExecuteCommand("SOUND", "517", new Console(), out _));
        Assert.Single(sent);

        // 0205 is 517 in hex - the same sound, written the way a pack writes it.
        sent.Clear();
        Assert.True(ch.TryExecuteCommand("SOUND", "0205", new Console(), out _));
        Assert.Single(sent);
    }

    /// <summary>An effect whose graphic is named by ITEMDEF is drawn.</summary>
    [Fact]
    public void AnEffectGraphicNamedByItemdefIsBroadcast()
    {
        LoadDefs();
        var (_, ch, sent) = Rig();

        Assert.True(ch.TryExecuteCommand("EFFECT", "0,i_fx_probe,10,16,0,044,4",
            new Console(), out bool owned));
        Assert.True(owned);
        Assert.Single(sent);
    }

    /// <summary>The resolution itself: a defname constant answers its number, an
    /// ITEMDEF answers its graphic, and an unknown name answers nothing. This is what
    /// the two verbs above are built on.</summary>
    [Fact]
    public void TheResolverAnswersConstantsDefinitionsAndNothingElse()
    {
        LoadDefs();

        Assert.True(DefinitionLoader.TryGetDefNumber("snd_probe_poison", out int snd));
        Assert.Equal(517, snd);

        Assert.Equal(0x36B0, DefinitionLoader.ResolveItemDefIndexByName("i_fx_probe"));

        Assert.False(DefinitionLoader.TryGetDefNumber("nothing_defines_this", out _));
        Assert.Equal(0, DefinitionLoader.ResolveItemDefIndexByName("nothing_defines_this"));
    }
}
