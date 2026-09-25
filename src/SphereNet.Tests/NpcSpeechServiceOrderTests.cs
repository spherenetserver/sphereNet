using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// NPC_OnHear order (CCharNPCAct.cpp:258-386): the SPEECH blocks answer first; the
// engine's own keyword fallbacks only see what no SPEECH took, and they go by BRAIN,
// never by the NPC's name.
[Collection("DefinitionLoaderSerial")]
public sealed class NpcSpeechServiceOrderTests : IDisposable
{
    private readonly ScriptRuntimeStack _stack = ScriptTestBootstrap.CreateRuntimeStack();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"npcspeech-{Guid.NewGuid():N}.scp");
    private readonly Dictionary<FieldInfo, object?> _saved = new();
    private readonly GameWorld _world;

    public NpcSpeechServiceOrderTests()
    {
        _world = TestHarness.CreateWorld();
        SetServer("_triggerDispatcher", _stack.Dispatcher);
        SetServer("_npcAI", new NpcAI(_world, new SphereConfig()));
        SetServer("_world", _world);
        SetServer("_log", NullLogger.Instance);
        ((Dictionary<uint, long>)typeof(SphereNet.Server.Program)
            .GetField("_lastCallGuards", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!).Clear();
        File.WriteAllText(_path, """
            [SPEECH spk_probe]
            ON=*bank*
            TAG.HEARD=1

            [EVENTS e_probe]
            ON=@NPCHearUnknown
            TAG.UNKNOWN=1
            """);
        _stack.Resources.LoadResourceFile(_path);
    }

    private void SetServer(string name, object value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        _saved.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    public void Dispose()
    {
        foreach (var (field, value) in _saved) field.SetValue(null, value);
        File.Delete(_path);
        _stack.LoggerFactory.Dispose();
    }

    private static void Hear(Character speaker, Character npc, string text) =>
        typeof(SphereNet.Server.Program)
            .GetMethod("OnNpcHearSpeech", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [speaker, npc, text, SphereNet.Game.Speech.TalkMode.Say]);

    private (Character npc, Character speaker) Pair(NpcBrainType brain, string name)
    {
        var npc = _world.CreateCharacter();
        npc.NpcBrain = brain;
        npc.Name = name;
        _world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        npc.Events.Add(_stack.Resources.ResolveDefName("e_probe"));
        var speaker = _world.CreateCharacter();
        speaker.IsPlayer = true;
        speaker.MaxHits = 100;
        speaker.Hits = 10;
        _world.PlaceCharacter(speaker, new Point3D(101, 100, 0, 0));
        return (npc, speaker);
    }

    [Fact]
    public void SpeechBlocks_AnswerBeforeTheBankerKeyword()
    {
        var (banker, speaker) = Pair(NpcBrainType.Banker, "the banker");
        Assert.True(banker.TryExecuteCommand("DSPEECH", "+spk_probe", null!));

        Hear(speaker, banker, "bank");

        Assert.True(banker.TryGetTag("HEARD", out _));
    }

    [Fact]
    public void ANamedBanker_WithAHumanBrain_IsNotABanker()
    {
        var (human, speaker) = Pair(NpcBrainType.Human, "Bob the banker");

        Hear(speaker, human, "bank");

        // Nothing handled the line, so it is unknown speech (NPC_OnHear :369).
        Assert.True(human.TryGetTag("UNKNOWN", out _));
    }

    [Fact]
    public void AHealer_DoesNotHealTheLivingOnRequest()
    {
        var (healer, speaker) = Pair(NpcBrainType.Healer, "healer");

        Hear(speaker, healer, "heal me");

        Assert.Equal(10, speaker.Hits);
        Assert.True(healer.TryGetTag("UNKNOWN", out _));
    }

    [Fact]
    public void CallGuards_SendsAFreeGuard_OncePerSpamWindow()
    {
        // CChar::CallGuards (CCharFight.cpp:215-285): a free guard in sight takes the
        // criminal through NPC_LookAtCharGuard; a second call inside 2.5 s is dropped.
        var town = new SphereNet.Game.World.Regions.Region
        { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        town.AddRect(0, 0, 1000, 1000);
        _world.AddRegion(town);
        var (witness, criminal) = Pair(NpcBrainType.Human, "townsman");
        criminal.SetStatFlag(StatFlag.Criminal);
        var guard = _world.CreateCharacter();
        guard.NpcBrain = NpcBrainType.Guard;
        guard.Karma = 5000;
        guard.Hits = guard.MaxHits = 100;
        _world.PlaceCharacter(guard, new Point3D(105, 100, 0, 0));

        var call = typeof(SphereNet.Server.Program)
            .GetMethod("CallGuards", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)call.Invoke(null, [witness, criminal])!);
        Assert.Equal(criminal.Uid, guard.FightTarget);
        Assert.False(criminal.IsDead); // no lightning, no forced death
        Assert.False((bool)call.Invoke(null, [witness, criminal])!);
    }

    [Fact]
    public void AGuard_HasNoCannedAnswer()
    {
        var (guard, speaker) = Pair(NpcBrainType.Guard, "guard");

        Hear(speaker, guard, "help");

        Assert.True(guard.TryGetTag("UNKNOWN", out _));
    }
}
