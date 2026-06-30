using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Ghost speech garbling (Source-X / ServUO MutateSpeech): a dead player's words
/// are scrambled into random o/O for the living who cannot hear the dead, while
/// other ghosts and staff hear them clearly.
/// </summary>
public class GhostSpeechTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Character.ResolveAccountForChar = null; // PrivLevel reads the local field
        return world;
    }

    [Fact]
    public void Garble_PreservesLengthAndSpaces_OnlyGhostChars()
    {
        const string input = "help me please";
        string garbled = GhostSpeech.Garble(input, new Random(12345));

        Assert.Equal(input.Length, garbled.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == ' ')
                Assert.Equal(' ', garbled[i]); // spaces survive at the same offsets
            else
                Assert.True(garbled[i] is 'o' or 'O', $"unexpected char '{garbled[i]}'");
        }
        // The content is scrambled away from the original words.
        Assert.NotEqual(input, garbled);
    }

    [Fact]
    public void Garble_EmptyOrNull_ReturnsInput()
    {
        Assert.Equal("", GhostSpeech.Garble("", new Random(1)));
        Assert.Null(GhostSpeech.Garble(null!, new Random(1)));
    }

    [Fact]
    public void HearsGhostClearly_LivingNonStaff_HearsGarbled()
    {
        var world = CreateWorld();
        var living = world.CreateCharacter();
        living.IsPlayer = true;
        living.PrivLevel = PrivLevel.Player;
        Assert.False(GhostSpeech.HearsGhostClearly(living));
    }

    [Fact]
    public void HearsGhostClearly_DeadStaffOrAllShow_HearClear()
    {
        var world = CreateWorld();

        var ghost = world.CreateCharacter();
        ghost.IsPlayer = true;
        ghost.SetStatFlag(StatFlag.Dead);
        Assert.True(GhostSpeech.HearsGhostClearly(ghost)); // a fellow ghost

        var staff = world.CreateCharacter();
        staff.IsPlayer = true;
        staff.PrivLevel = PrivLevel.Counsel;
        Assert.True(GhostSpeech.HearsGhostClearly(staff)); // staff

        var seer = world.CreateCharacter();
        seer.IsPlayer = true;
        seer.PrivLevel = PrivLevel.Player;
        seer.AllShow = true;
        Assert.True(GhostSpeech.HearsGhostClearly(seer)); // AllShow observer
    }
}
