using System.Buffers.Binary;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class RegionMusicTransitionTests
{
    [Fact]
    public void NpcCrossingStillRunsRegionEnterWithoutPlayerMusic()
    {
        Run((world, _, ch, _, newRegion, music) =>
        {
            ch.IsPlayer = false;
            Assert.True(world.MoveCharacter(ch, new Point3D(101, 100, 0, 0)));
            Assert.True(ch.TryGetTag("SEEN_OLD", out var seen));
            Assert.Equal("midi_old", seen);
            Assert.True(ch.TryGetProperty("REGION", out var current));
            Assert.Equal(newRegion.Uid.ToString(), current);
            Assert.Empty(music);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CrossingUsesOldRegionDuringEnterAndPlaysNewMusicOnce(bool walking, bool sameName)
    {
        Run((world, movement, ch, oldRegion, newRegion, music) =>
        {
            if (sameName) newRegion.Name = oldRegion.Name;
            bool moved = walking ? movement.TryMove(ch, Direction.East, false, 0)
                : world.MoveCharacter(ch, new Point3D(101, 100, 0, 0));
            Assert.True(moved);
            Assert.Equal(new ushort[] { 9 }, music);
            Assert.True(ch.TryGetTag("SEEN_OLD", out var seen));
            Assert.Equal("midi_old", seen);
            Assert.True(ch.TryGetProperty("REGION", out var current));
            Assert.Equal(newRegion.Uid.ToString(), current);
            Assert.True(ch.TryGetProperty("REGION.TAG.MUSIC", out var tune));
            Assert.Equal("midi_city", tune);
            Assert.True(world.MoveCharacter(ch, new Point3D(102, 100, 0, 0)));
            Assert.Single(music);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedCityBoundaryInSameCacheCellStillChangesMusic(bool walking)
    {
        Run((world, movement, ch, oldRegion, _, music) =>
        {
            oldRegion.AddRect(70, 70, 140, 140);
            world.InvalidateRegionCache();
            Assert.Same(oldRegion, world.FindRegion(ch.Position));
            bool moved = walking ? movement.TryMove(ch, Direction.East, false, 0)
                : world.MoveCharacter(ch, new Point3D(101, 100, 0, 0));
            Assert.True(moved);
            Assert.Equal(new ushort[] { 9 }, music);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameMusicOrSuppressedShipMovementDoesNotRestartMusic(bool suppress)
    {
        Run((world, _, ch, _, newRegion, music) =>
        {
            if (!suppress) newRegion.SetTag("MUSIC", "midi_old");
            Assert.True(world.MoveCharacter(ch, new Point3D(101, 100, 0, 0), !suppress));
            Assert.Empty(music);
            Assert.Equal(!suppress, ch.TryGetTag("SEEN_OLD", out _));
        });
    }

    private static void Run(Action<GameWorld, MovementEngine, Character, Region, Region, List<ushort>> test)
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"region_music_{Guid.NewGuid():N}.scp");
        var oldSend = Character.SendPacketToOwner;
        try
        {
            File.WriteAllText(path, """
                [DEFNAME music_test]
                midi_old 8
                midi_city 9

                [REGIONTYPE r_city_music]
                ON=@Enter
                SRC.TAG.SEEN_OLD=<SRC.REGION.TAG.MUSIC>
                IF <SRC.ISPLAYER> && <SRC.REGION>
                 IF STRCMPI("<TAG.MUSIC>","<SRC.REGION.TAG.MUSIC>")
                  IF !(<isempty <TAG.MUSIC>>)
                   SRC.MIDILIST=<TAG.MUSIC>
                  ELSE
                   SRC.MIDILIST=midi_city
                  ENDIF
                 ENDIF
                ENDIF
                """);
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
            var world = TestHarness.CreateWorld();
            ObjBase.ResolveWorld = () => world;
            var map = new MapDataManager("");
            map.AddSyntheticMap(0, 256, 256);
            world.MapData = map;
            var oldRegion = new Region { Name = "Old area" };
            oldRegion.AddRect(80, 80, 100, 120);
            oldRegion.SetTag("MUSIC", "midi_old");
            var newRegion = new Region { Name = "City" };
            newRegion.AddRect(101, 80, 130, 120);
            newRegion.SetTag("MUSIC", "midi_city");
            newRegion.AddEvent(runtime.Resources.ResolveDefName("r_city_music"));
            world.AddRegion(oldRegion);
            world.AddRegion(newRegion);
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Str = 100; ch.Dex = 100; ch.Stam = 100;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            var music = new List<ushort>();
            Character.SendPacketToOwner = (owner, packet) =>
            {
                var bytes = packet.Build().Span;
                if (owner == ch && bytes[0] == 0x6D)
                    music.Add(BinaryPrimitives.ReadUInt16BigEndian(bytes[1..]));
            };
            var movement = new MovementEngine(world, runtime.Dispatcher);
            test(world, movement, ch, oldRegion, newRegion, music);
        }
        finally
        {
            Character.SendPacketToOwner = oldSend;
            File.Delete(path);
        }
    }
}
