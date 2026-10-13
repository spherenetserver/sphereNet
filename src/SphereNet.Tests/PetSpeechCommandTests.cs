using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Spoken pet commands and command-prefix lines against Source-X
/// CClient::Event_Talk_Common (CClientEvent.cpp:1973-2000), NPC_OnHearName
/// (CCharNPCStatus.cpp:369), NPC_IsOwnedBy (:449) and Event_Command
/// (CClientEvent.cpp:1058): a line no pet of yours answers is plain speech with no
/// "Sorry", a pet is named by its whole name, staff own NPCs only in GM mode, and a
/// player's prefixed line that is no command they may use is said normally.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PetSpeechCommandTests
{
    private sealed record Bench(GameWorld World, GameClient Client, Character Player);

    private static Bench Setup(PrivLevel plevel = PrivLevel.Player)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "speaker";
        player.PrivLevel = plevel;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 16101);
        TestHarness.AttachCharacter(client, player);
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);
        client.SetEngines(commands: commands, skillHandlers: new SkillHandlers(world));
        TestHarness.ClearQueuedPackets(client.NetState);
        return new Bench(world, client, player);
    }

    private static Character Pet(GameWorld world, string name, Character? owner, int x = 101)
    {
        var pet = world.CreateCharacter();
        pet.Name = name;
        pet.BodyId = 0x00C8;
        pet.NpcBrain = NpcBrainType.Animal;
        world.PlaceCharacter(pet, new Point3D((short)x, 100, 0, 0));
        if (owner != null)
            Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));
        return pet;
    }

    [Theory]
    [InlineData("all stop")]
    [InlineData("ok stop")]
    [InlineData("lets go")]
    public void ALineNoPetOfYoursAnswersIsSilent(string line)
    {
        var b = Setup();

        Assert.False(b.Client.TryHandlePetCommand(line));
        Assert.Empty(TestHarness.GetQueuedPackets(b.Client.NetState));
    }

    [Fact]
    public void APetIsNamedByItsWholeNameNotByAPrefix()
    {
        var b = Setup();
        Pet(b.World, "xena", b.Player);

        Assert.False(b.Client.TryHandlePetCommand("x stop"));
        Assert.True(b.Client.TryHandlePetCommand("xena stop"));
        Assert.True(b.Client.TryHandlePetCommand("hey xena stop"));
    }

    [Fact]
    public void AStrangersPetIgnoresTheCommandWithoutAWord()
    {
        var b = Setup();
        var stranger = b.World.CreateCharacter();
        stranger.IsPlayer = true;
        b.World.PlaceCharacter(stranger, new Point3D(105, 100, 0, 0));
        Pet(b.World, "xena", stranger);

        Assert.False(b.Client.TryHandlePetCommand("xena stop"));
        Assert.Empty(TestHarness.GetQueuedPackets(b.Client.NetState));
    }

    [Fact]
    public void StaffOwnAnNpcOnlyInGmMode()
    {
        var b = Setup(PrivLevel.GM);
        var pet = Pet(b.World, "xena", owner: null);

        Assert.True(pet.CanAcceptPetCommandFrom(b.Player));

        Assert.True(b.Player.TrySetProperty("GM", "0"));
        Assert.False(b.Player.IsGmMode);
        Assert.False(pet.CanAcceptPetCommandFrom(b.Player));
    }

    [Fact]
    public void ANamedPetCommandIsNotHeardByAnyNpc()
    {
        var b = Setup();
        var speech = new SpeechEngine(b.World);
        var npc = b.World.CreateCharacter();
        npc.IsPlayer = false;
        b.World.PlaceCharacter(npc, new Point3D(102, 100, 0, 0));
        var heard = new List<Character>();
        speech.OnNpcHear += (_, listener, _, _) => heard.Add(listener);

        speech.ProcessSpeech(b.Player, "xena stop", TalkMode.Say, 0, 3, out _, _ => true);
        Assert.Empty(heard);

        speech.ProcessSpeech(b.Player, "hello", TalkMode.Say, 0, 3, out _, _ => false);
        Assert.Contains(npc, heard);
    }

    [Theory]
    [InlineData("/hi")]
    [InlineData(".zzz_no_such_command")]
    public void APlayersPrefixedLineThatIsNoCommandIsSaid(string line)
    {
        var b = Setup();

        Assert.False(b.Client.TryHandleCommandSpeech(line));
        Assert.Empty(TestHarness.GetQueuedPackets(b.Client.NetState));
    }

    [Fact]
    public void StaffStillGetThePrivilegeMessage()
    {
        var b = Setup(PrivLevel.GM);

        Assert.True(b.Client.TryHandleCommandSpeech(".zzz_no_such_command"));
        Assert.NotEmpty(TestHarness.GetQueuedPackets(b.Client.NetState));
    }
}
