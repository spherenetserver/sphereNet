using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;

namespace SphereNet.Tests;

/// <summary>
/// An NPC hears what it could plausibly hear, and nothing else.
///
/// Reported from a shard: every NPC tried to read every line, with no sense of distance -
/// two players chatting to each other had every vendor in sight attempting to answer, and
/// the keyword and script chain ran for each of them on every line. Wrong, and expensive
/// in the same breath.
///
/// Two things were off against upstream. The radius was 18 where upstream's default is
/// UO_MAP_VIEW_SIGHT, 14 (uofiles_macros.h:17, CServerConfig.cpp:215) - more than twice
/// the area, every NPC in it handed the line to consider. And there was no line-of-sight
/// gate at all, where Event_Talk_Common refuses an NPC that cannot see the speaker
/// (CClientEvent.cpp:1949), so a shopkeeper indoors heard the street through the wall.
///
/// A negative NPCDISTANCEHEAR still means "this far, never mind sight", as upstream.
/// </summary>
public sealed class NpcHearingRangeTests
{
    private const ushort WallGraphic = 0x0080;

    private sealed record Bench(SphereNet.Game.World.GameWorld World, SpeechEngine Speech,
                                List<string> Heard);

    private static Bench Build(int npcDistanceHear = 0)
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(WallGraphic, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 20, Name = "wall" });

        var world = new SphereNet.Game.World.GameWorld(TestHarness.CreateLoggerFactory());
        world.InitMap(0, 512, 512);
        world.MapData = md;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var speech = new SpeechEngine(world) { NpcDistanceHear = npcDistanceHear };
        var heard = new List<string>();
        speech.OnNpcHear += (_, npc, text, _) => heard.Add($"{npc.Name}:{text}");
        return new Bench(world, speech, heard);
    }

    private static SphereNet.Game.Objects.Characters.Character Player(Bench b, int x, int y)
    {
        var ch = b.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "player";
        b.World.PlaceCharacter(ch, new Point3D((short)x, (short)y, 0, 0));
        return ch;
    }

    private static SphereNet.Game.Objects.Characters.Character Npc(Bench b, string name, int x, int y)
    {
        var ch = b.World.CreateCharacter();
        ch.IsPlayer = false;
        ch.Name = name;
        b.World.PlaceCharacter(ch, new Point3D((short)x, (short)y, 0, 0));
        return ch;
    }

    /// <summary>Upstream's radius, not a wider one: 14 in, 15 out.</summary>
    [Fact]
    public void TheDefaultRadiusIsFourteen()
    {
        var b = Build();
        var speaker = Player(b, 100, 100);
        Npc(b, "near", 114, 100);
        Npc(b, "far", 116, 100);

        b.Speech.ProcessSpeech(speaker, "hello", TalkMode.Say);

        Assert.Contains("near:hello", b.Heard);
        Assert.DoesNotContain("far:hello", b.Heard);
    }

    /// <summary>The wall case, which is what the shard actually reported.</summary>
    [Fact]
    public void AnNpcBehindAWallDoesNotHear()
    {
        var b = Build();
        var speaker = Player(b, 100, 100);
        Npc(b, "indoors", 104, 100);

        // A wall between them.
        var wall = b.World.CreateItem();
        wall.BaseId = WallGraphic;
        b.World.PlaceItem(wall, new Point3D((short)102, (short)100, 0, 0));

        b.Speech.ProcessSpeech(speaker, "hello", TalkMode.Say);

        Assert.DoesNotContain("indoors:hello", b.Heard);
    }

    /// <summary>And one in the open still does, or the gate would have silenced the
    /// shard instead of tidying it.</summary>
    [Fact]
    public void AnNpcInTheOpenStillHears()
    {
        var b = Build();
        var speaker = Player(b, 100, 100);
        Npc(b, "outdoors", 104, 100);

        b.Speech.ProcessSpeech(speaker, "hello", TalkMode.Say);

        Assert.Contains("outdoors:hello", b.Heard);
    }

    /// <summary>A negative setting keeps the distance and drops the sight requirement,
    /// as upstream's sign convention says.</summary>
    [Fact]
    public void ANegativeSettingIgnoresSight()
    {
        var b = Build(npcDistanceHear: -10);
        var speaker = Player(b, 100, 100);
        Npc(b, "indoors", 104, 100);
        var wall = b.World.CreateItem();
        wall.BaseId = WallGraphic;
        b.World.PlaceItem(wall, new Point3D((short)102, (short)100, 0, 0));

        b.Speech.ProcessSpeech(speaker, "hello", TalkMode.Say);

        Assert.Contains("indoors:hello", b.Heard);
    }

    /// <summary>Two players talking to each other: the ones who cannot see them are not
    /// handed the line, which is the load half of the report.</summary>
    [Fact]
    public void ShopkeepersBehindWallsAreNotHandedEveryLine()
    {
        var b = Build();
        var speaker = Player(b, 100, 100);
        Player(b, 101, 100);           // the other player, chatting

        for (int i = 0; i < 6; i++)
        {
            Npc(b, "shop" + i, 106, 100 + i);
            var wall = b.World.CreateItem();
            wall.BaseId = WallGraphic;
            b.World.PlaceItem(wall, new Point3D((short)103, (short)(100 + i), 0, 0));
        }

        b.Speech.ProcessSpeech(speaker, "see you later", TalkMode.Say);

        Assert.Empty(b.Heard);
    }
}
