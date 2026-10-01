using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The runtime OWNER verb and the shared ownership operation behind it.
///
/// <list type="bullet">
/// <item>CHV_OWNER (CChar.cpp:4791): with an argument, the ARGUMENT character becomes the
/// pet of the character the line runs on (<c>pChar-&gt;NPC_PetSetOwner(this)</c>); with
/// none, the character the line runs on becomes SRC's pet
/// (<c>NPC_PetSetOwner(pCharSrc)</c>). <c>&lt;OWNER&gt;</c> stays a read of the owner
/// (CHR_OWNER ref, CChar.cpp:2256), and no save writes an OWNER key for a character.</item>
/// <item>NPC_PetSetOwner (CCharNPCPet.cpp:601) refuses a player and refuses making a
/// creature its own owner.</item>
/// <item>A transfer clears the previous owner first - FollowersUpdate(-slots) on the OLD
/// owner (NPC_PetClearOwners, :594) - and only then adds to the new one (:630).</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PetOwnerVerbParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_ownerverb_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class CharConsole(Character ch) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => ch.PrivLevel;
        public string GetName() => ch.Name;
        public IScriptObj? GetSourceChar() => ch;
        public void SysMessage(string text) { }
    }

    private static GameWorld NewWorld() => TestHarness.CreateWorld();

    private static Character Being(GameWorld world, int x, bool player)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.BodyId = player ? (ushort)0x190 : (ushort)0xC9;
        ch.Name = player ? $"player{x}" : $"creature{x}";
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static void Run(ScriptInterpreter interp, Character target, Character src, params ScriptKey[] lines) =>
        interp.Execute(lines, target, new CharConsole(src), new TriggerArgs { Source = src }, new ScriptScope());

    private static string Hex(Character ch) => $"0{ch.Uid.Value:X}";

    // --- finding 1: the OWNER verb ---------------------------------------------

    [Fact]
    public void OwnerWithAnArgumentMakesTheArgumentThePetOfTheRunner()
    {
        var world = NewWorld();
        var interp = ScriptTestBootstrap.CreateRuntimeStack().Interpreter;
        var player = Being(world, 100, player: true);
        var creature = Being(world, 101, player: false);

        Run(interp, player, player, new ScriptKey("OWNER", Hex(creature)));

        Assert.True(creature.HasOwner(player.Uid));
        Assert.True(creature.IsStatFlag(StatFlag.Pet));
        Assert.False(player.IsStatFlag(StatFlag.Pet));
        Assert.False(player.OwnerSerial.IsValid);
    }

    [Fact]
    public void OwnerWithoutAnArgumentMakesSrcTheOwner()
    {
        var world = NewWorld();
        var interp = ScriptTestBootstrap.CreateRuntimeStack().Interpreter;
        var player = Being(world, 100, player: true);
        var creature = Being(world, 101, player: false);

        Run(interp, creature, player, new ScriptKey("OWNER", ""));

        Assert.True(creature.HasOwner(player.Uid));
        Assert.True(creature.IsStatFlag(StatFlag.Pet));
        Assert.False(player.IsStatFlag(StatFlag.Pet));
    }

    [Fact]
    public void SrcOwnerWithTheCreatureUidMakesItSrcsPet()
    {
        var world = NewWorld();
        var interp = ScriptTestBootstrap.CreateRuntimeStack().Interpreter;
        var player = Being(world, 100, player: true);
        var creature = Being(world, 101, player: false);

        // The NPC-trigger form: the line runs on the creature, SRC.OWNER <UID>.
        Run(interp, creature, player, new ScriptKey("SRC.OWNER", Hex(creature)));

        Assert.True(creature.HasOwner(player.Uid));
        Assert.False(player.IsStatFlag(StatFlag.Pet));
        Assert.False(player.OwnerSerial.IsValid);
    }

    [Fact]
    public void OwnerReadStillAnswersTheOwnerUid()
    {
        var world = NewWorld();
        var interp = ScriptTestBootstrap.CreateRuntimeStack().Interpreter;
        var player = Being(world, 100, player: true);
        var creature = Being(world, 101, player: false);

        Run(interp, player, player, new ScriptKey("OWNER", Hex(creature)));

        Assert.True(creature.TryGetProperty("OWNER", out string read));
        Assert.Equal(Hex(player), read);
    }

    [Fact]
    public void AnOwnerArgumentNamingNoCharacterChangesNothing()
    {
        var world = NewWorld();
        var interp = ScriptTestBootstrap.CreateRuntimeStack().Interpreter;
        var player = Being(world, 100, player: true);

        Run(interp, player, player, new ScriptKey("OWNER", "0"));

        Assert.False(player.IsStatFlag(StatFlag.Pet));
        Assert.False(player.OwnerSerial.IsValid);
    }

    [Fact]
    public void AnOwnedPetSurvivesASaveCycle()
    {
        var world = NewWorld();
        var interp = ScriptTestBootstrap.CreateRuntimeStack().Interpreter;
        var player = Being(world, 100, player: true);
        var creature = Being(world, 101, player: false);
        Run(interp, player, player, new ScriptKey("OWNER", Hex(creature)));

        Directory.CreateDirectory(_dir);
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, _dir);
        var reloaded = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, _dir);

        var backPet = reloaded.FindChar(creature.Uid)!;
        var backOwner = reloaded.FindChar(player.Uid)!;
        Assert.True(backPet.HasOwner(player.Uid));
        Assert.Equal(player.Uid, backPet.NpcMaster);
        Assert.True(backPet.IsStatFlag(StatFlag.Pet));
        Assert.False(backOwner.IsStatFlag(StatFlag.Pet));
        Assert.False(backOwner.OwnerSerial.IsValid);
    }

    [Fact]
    public void ARawOwnerKeyInACharacterRecordStillLoadsAsTheOwner()
    {
        // No writer emits it, but a hand-edited or foreign record that carries the key
        // keeps loading the way it always did: as the raw owner, not as the verb.
        var world = NewWorld();
        var player = Being(world, 100, player: true);
        var creature = Being(world, 101, player: false);

        Directory.CreateDirectory(_dir);
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, _dir);
        bool injected = false;
        foreach (string file in Directory.EnumerateFiles(_dir, "*.scp"))
        {
            var lines = File.ReadAllLines(file).ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("SERIAL=", StringComparison.OrdinalIgnoreCase) ||
                    !uint.TryParse(line["SERIAL=".Length..], System.Globalization.NumberStyles.HexNumber,
                        null, out uint serial) || serial != creature.Uid.Value)
                    continue;
                // At the end of the record, after its FLAGS line.
                int end = i + 1;
                while (end < lines.Count && lines[end].Trim().Length > 0 && !lines[end].TrimStart().StartsWith('['))
                    end++;
                lines.Insert(end, $"OWNER={Hex(player)}");
                injected = true;
                break;
            }
            if (injected) { File.WriteAllLines(file, lines); break; }
        }
        Assert.True(injected);

        var reloaded = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, _dir);

        var backPet = reloaded.FindChar(creature.Uid)!;
        Assert.True(backPet.HasOwner(player.Uid));
        Assert.True(backPet.IsStatFlag(StatFlag.Pet));
        Assert.False(reloaded.FindChar(player.Uid)!.IsStatFlag(StatFlag.Pet));
    }

    // --- finding 2: NPC_PetSetOwner preconditions --------------------------------

    [Fact]
    public void OwnershipOfAPlayerIsRefused()
    {
        var world = NewWorld();
        var owner = Being(world, 100, player: true);
        var victim = Being(world, 101, player: true);

        Assert.False(victim.TryAssignOwnership(owner, owner));

        Assert.False(victim.IsStatFlag(StatFlag.Pet));
        Assert.False(victim.OwnerSerial.IsValid);
    }

    [Fact]
    public void OwnershipBySelfIsRefused()
    {
        var world = NewWorld();
        var creature = Being(world, 101, player: false);

        Assert.False(creature.TryAssignOwnership(creature, creature));

        Assert.False(creature.IsStatFlag(StatFlag.Pet));
        Assert.False(creature.OwnerSerial.IsValid);
    }

    [Fact]
    public void MakeMyPetOnAPlayerGrantsNoPetFlag()
    {
        var world = NewWorld();
        var owner = Being(world, 100, player: true);
        var victim = Being(world, 101, player: true);

        victim.TryExecuteCommand("MAKEMYPET", Hex(owner), null!);

        Assert.False(victim.IsStatFlag(StatFlag.Pet));
        Assert.False(victim.OwnerSerial.IsValid);
    }

    [Fact]
    public void MakeMyPetNamingItselfGrantsNoPetFlag()
    {
        var world = NewWorld();
        var creature = Being(world, 101, player: false);

        creature.TryExecuteCommand("MAKEMYPET", Hex(creature), null!);

        Assert.False(creature.IsStatFlag(StatFlag.Pet));
        Assert.False(creature.OwnerSerial.IsValid);
    }

    [Fact]
    public void ReassigningTheSameOwnerStillSucceeds()
    {
        // Stable retrieval and figurine restore hand the pet back to the owner it has.
        var world = NewWorld();
        var owner = Being(world, 100, player: true);
        var creature = Being(world, 101, player: false);
        Assert.True(creature.TryAssignOwnership(owner, owner));

        Assert.True(creature.TryAssignOwnership(owner, owner, enforceFollowerCap: true));
        Assert.True(creature.HasOwner(owner.Uid));
    }

    // --- finding 3: the transfer's follower notifications -------------------------

    private static List<string> RecordFollowers(Func<Character, bool, bool>? veto = null)
    {
        var log = new List<string>();
        GameClient.ServerOptionFlags |= OptionFlags.PetSlots;
        Character.OnFollowersUpdate = (owner, _, adding, slots) =>
        {
            log.Add($"{owner.Name}:{(adding ? "add" : "remove")}:{slots}");
            return veto?.Invoke(owner, adding) ?? false;
        };
        return log;
    }

    [Fact]
    public void ATransferNotifiesTheOldOwnerBeforeTheNewOne()
    {
        var world = NewWorld();
        var oldOwner = Being(world, 100, player: true);
        var newOwner = Being(world, 102, player: true);
        var creature = Being(world, 101, player: false);
        Assert.True(creature.TryAssignOwnership(oldOwner, oldOwner));
        var log = RecordFollowers();

        Assert.True(creature.TryAssignOwnership(newOwner, newOwner));

        Assert.Equal(new[] { "player100:remove:1", "player102:add:1" }, log);
        Assert.True(creature.HasOwner(newOwner.Uid));
        Assert.Equal(0, oldOwner.CurFollower);
        Assert.Equal(1, newOwner.CurFollower);
    }

    [Fact]
    public void TheOwnerVerbTransferNotifiesBothOwnersInOrder()
    {
        var world = NewWorld();
        var interp = ScriptTestBootstrap.CreateRuntimeStack().Interpreter;
        var oldOwner = Being(world, 100, player: true);
        var newOwner = Being(world, 102, player: true);
        var creature = Being(world, 101, player: false);
        Assert.True(creature.TryAssignOwnership(oldOwner, oldOwner));
        var log = RecordFollowers();

        Run(interp, newOwner, newOwner, new ScriptKey("OWNER", Hex(creature)));

        Assert.Equal(new[] { "player100:remove:1", "player102:add:1" }, log);
        Assert.True(creature.HasOwner(newOwner.Uid));
    }

    [Fact]
    public void AFollowersUpdateReturnOneDoesNotStopATransfer()
    {
        // NPC_PetSetOwner ignores FollowersUpdate's result (CCharNPCPet.cpp:597, :633).
        var world = NewWorld();
        var oldOwner = Being(world, 100, player: true);
        var newOwner = Being(world, 102, player: true);
        var creature = Being(world, 101, player: false);
        Assert.True(creature.TryAssignOwnership(oldOwner, oldOwner));
        var log = RecordFollowers((owner, adding) => adding && owner == newOwner);

        Assert.True(creature.TryAssignOwnership(newOwner, newOwner));

        Assert.True(creature.HasOwner(newOwner.Uid));
        Assert.Equal(0, oldOwner.CurFollower);
        Assert.Equal(1, newOwner.CurFollower);
        Assert.Equal(new[] { "player100:remove:1", "player102:add:1" }, log);
    }

    [Fact]
    public void AFirstOwnerHearsOnlyTheAddition()
    {
        var world = NewWorld();
        var owner = Being(world, 100, player: true);
        var creature = Being(world, 101, player: false);
        var log = RecordFollowers();

        Assert.True(creature.TryAssignOwnership(owner, owner));
        Assert.True(creature.TryAssignOwnership(owner, owner));   // same owner: silent

        Assert.Equal(new[] { "player100:add:1" }, log);
    }
}
