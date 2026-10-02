using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The notoriety byte a criminal and a murderer carry (Source-X Noto_CalcFlag,
/// CCharNotoriety.cpp): a criminal is NOTO_CRIMINAL (4, grey) to everyone - the
/// victim included - and only a murder count past MURDERMINCOUNT (Noto_IsMurderer,
/// m_wMurders &gt; m_iMurderMinCount) makes NOTO_EVIL (6, red). The criminal memory
/// running out takes the character back to NOTO_GOOD (1).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CriminalMurdererDisplayTests
{
    private static GameWorld CreateWorld()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static byte Noto(GameWorld world, Character viewer, Character subject) =>
        GameClient.ComputeNotorietyColor(world, viewer, subject);

    [Fact]
    public void AnAttackerWhoHitsAnInnocent_IsGreyToTheVictimAndToObservers_NotRed()
    {
        var world = CreateWorld();
        var attacker = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101);
        var observer = MakePlayer(world, 102);

        // The victim's aggrieved memory alone already makes the attacker grey to it.
        victim.Memory_AddObjTypes(attacker.Uid, MemoryType.Aggreived);
        Assert.Equal(4, Noto(world, victim, attacker));
        Assert.Equal(1, Noto(world, observer, attacker));

        attacker.MakeCriminal();

        Assert.True(attacker.IsCriminal);
        Assert.Equal(4, Noto(world, victim, attacker));
        Assert.Equal(4, Noto(world, observer, attacker));
        Assert.Equal(4, Noto(world, attacker, attacker));
    }

    [Fact]
    public void OnlyAMurderCountPastTheThreshold_IsRed()
    {
        var world = CreateWorld();
        var subject = MakePlayer(world, 100);
        var observer = MakePlayer(world, 101);

        subject.Kills = (short)Character.MurderMinCount;
        Assert.Equal(1, Noto(world, observer, subject));

        // A criminal within the threshold stays grey.
        subject.MakeCriminal();
        Assert.Equal(4, Noto(world, observer, subject));

        subject.Kills = (short)(Character.MurderMinCount + 1);
        Assert.Equal(6, Noto(world, observer, subject));
    }

    [Fact]
    public void TheCriminalTimerRunningOut_ReturnsToBlue()
    {
        var world = CreateWorld();
        var subject = MakePlayer(world, 100);
        var observer = MakePlayer(world, 101);

        subject.MakeCriminal();
        var memory = subject.CombatState.CriminalMemory;
        Assert.NotNull(memory);
        Assert.Equal(4, Noto(world, observer, subject));

        subject.TickNotorietyDecay(memory!.Timeout + 1);

        Assert.False(subject.IsCriminal);
        Assert.False(subject.IsStatFlag(StatFlag.Criminal));
        Assert.Equal(1, Noto(world, observer, subject));
    }
}
