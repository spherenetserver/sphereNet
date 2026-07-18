using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// S3 (perf/parity): the per-utterance ground-item hear scan is gated on the
/// per-sector listen-item count, mirroring Source-X CSector::m_ListenItems
/// (CClientEvent.cpp:1883 "if (pSector->HasListenItems())"). No comm crystal /
/// multi in earshot and no scripted item @Hear → the scan never runs.
/// </summary>
public sealed class ListenItemSpeechGateTests
{
    private static (GameWorld World, SpeechEngine Speech, Character Speaker, List<Item> Heard)
        MakeWorld()
    {
        var world = TestHarness.CreateWorld();
        var speech = new SpeechEngine(world);
        var speaker = world.CreateCharacter();
        speaker.IsPlayer = true;
        world.PlaceCharacter(speaker, new Point3D(100, 100, 0, 0));

        var heard = new List<Item>();
        speech.OnItemHear = (_, item, _, _) => heard.Add(item);
        return (world, speech, speaker, heard);
    }

    [Fact]
    public void NoListenItemNearby_SkipsItemScanEntirely()
    {
        var (world, speech, speaker, heard) = MakeWorld();

        var mundane = world.CreateItem();
        world.PlaceItem(mundane, new Point3D(102, 100, 0, 0));

        speech.ProcessSpeech(speaker, "hello", TalkMode.Say);
        Assert.Empty(heard);
    }

    [Fact]
    public void CommCrystalNearby_RunsScan_AndTypeChangeRebalancesCounter()
    {
        var (world, speech, speaker, heard) = MakeWorld();

        var crystal = world.CreateItem();
        crystal.ItemType = ItemType.CommCrystal;
        world.PlaceItem(crystal, new Point3D(103, 100, 0, 0));

        speech.ProcessSpeech(speaker, "hello", TalkMode.Say);
        Assert.Contains(crystal, heard);

        // Script retypes the ground crystal → counter rebalances → scan skipped.
        heard.Clear();
        crystal.ItemType = ItemType.Normal;
        speech.ProcessSpeech(speaker, "hello again", TalkMode.Say);
        Assert.Empty(heard);
    }

    [Fact]
    public void DeletingTheOnlyCrystal_ClosesTheGateAgain()
    {
        var (world, speech, speaker, heard) = MakeWorld();

        var crystal = world.CreateItem();
        crystal.ItemType = ItemType.CommCrystal;
        world.PlaceItem(crystal, new Point3D(103, 100, 0, 0));
        Assert.True(world.HasListenItemsInRange(speaker.Position, 18));

        world.DeleteObject(crystal);
        Assert.False(world.HasListenItemsInRange(speaker.Position, 18));

        speech.ProcessSpeech(speaker, "hello", TalkMode.Say);
        Assert.Empty(heard);
    }

    [Fact]
    public void ScriptedItemHear_ForcesFullScanWithoutListenItems()
    {
        var (world, speech, speaker, heard) = MakeWorld();
        speech.ScanAllItemsOnHear = true; // wiring sets this when any item def hooks @Hear

        var mundane = world.CreateItem();
        world.PlaceItem(mundane, new Point3D(102, 100, 0, 0));

        speech.ProcessSpeech(speaker, "hello", TalkMode.Say);
        Assert.Contains(mundane, heard);
    }
}
