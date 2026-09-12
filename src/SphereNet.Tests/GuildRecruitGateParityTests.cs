using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Guild;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Who a stone will take on (port plan İŞ-37 / PLAN-505).
///
/// Source-X gates recruitment in CItemStone::AddRecruit (CItemStone.cpp:1074-1087):
/// only a PLAYER can be a member, and nobody already belonging to another stone of
/// the same kind may be taken on - they must resign the old one first.
///
/// GuildDef.AddRecruit had no gate at all, so a script could enrol an NPC, or put
/// one player in two guilds at once and stamp them with two stone memories. A town
/// and a guild stay independent affiliations (MEMORY_TOWN vs MEMORY_GUILD), so only
/// a stone of the SAME kind blocks.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GuildRecruitGateParityTests
{
    private static (GameWorld World, GuildManager Guilds) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return (world, new GuildManager());
    }

    private static GuildDef Stone(GameWorld world, GuildManager guilds, bool town = false)
    {
        var stone = world.CreateItem();
        stone.ItemType = town ? ItemType.StoneTown : ItemType.StoneGuild;
        stone.BaseId = 0x0ED4;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        return guilds.CreateGuild(stone.Uid, town ? "Britain" : "The Order",
            Serial.Invalid, isTownStone: town);
    }

    private static Character Player(GameWorld world, string name = "someone")
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        world.PlaceCharacter(ch, new Point3D(101, 100, 0, 0));
        return ch;
    }

    private static Character Npc(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = false;
        ch.Name = "a brigand";
        world.PlaceCharacter(ch, new Point3D(102, 100, 0, 0));
        return ch;
    }

    // ---- who is eligible -------------------------------------------------

    [Fact]
    public void APlayerIsTakenOn()
    {
        var (world, guilds) = Setup();
        var guild = Stone(world, guilds);

        Assert.Null(guilds.GetRecruitRefusal(guild, Player(world)));
    }

    [Fact]
    public void AnNpcIsNot()
    {
        var (world, guilds) = Setup();
        var guild = Stone(world, guilds);

        var npc = Npc(world);
        Assert.NotNull(guilds.GetRecruitRefusal(guild, npc));
        Assert.Null(guilds.TryAddRecruit(guild, npc));
        Assert.Null(guild.FindMember(npc.Uid));
    }

    [Fact]
    public void NobodyAtAllIsNot()
    {
        var (world, guilds) = Setup();
        var guild = Stone(world, guilds);

        Assert.NotNull(guilds.GetRecruitRefusal(guild, null));
    }

    // ---- one guild at a time ---------------------------------------------

    [Fact]
    public void AGuildWillNotTakeAnotherGuildsMember()
    {
        var (world, guilds) = Setup();
        var first = Stone(world, guilds);
        var second = Stone(world, guilds);
        var player = Player(world, "Dupre");
        Assert.NotNull(guilds.TryJoinAsMember(first, player));

        string? refusal = guilds.GetRecruitRefusal(second, player);

        Assert.NotNull(refusal);
        Assert.Contains("Dupre", refusal);
        Assert.Null(guilds.TryAddRecruit(second, player));
        Assert.Null(second.FindMember(player.Uid));
    }

    [Fact]
    public void TheSameStoneStillTakesItsOwnMemberBack()
    {
        // The gate is about ANOTHER stone; re-applying to your own is not a clash.
        var (world, guilds) = Setup();
        var guild = Stone(world, guilds);
        var player = Player(world);
        Assert.NotNull(guilds.TryJoinAsMember(guild, player));

        Assert.Null(guilds.GetRecruitRefusal(guild, player));
    }

    [Fact]
    public void ATownAndAGuildAreIndependentAffiliations()
    {
        // MEMORY_TOWN and MEMORY_GUILD are separate in the reference, so a guild
        // member may still be a citizen and the other way round.
        var (world, guilds) = Setup();
        var guild = Stone(world, guilds);
        var town = Stone(world, guilds, town: true);
        var player = Player(world);
        Assert.NotNull(guilds.TryJoinAsMember(guild, player));

        Assert.Null(guilds.GetRecruitRefusal(town, player));
        Assert.NotNull(guilds.TryJoinAsMember(town, player));

        // ...and a second TOWN is still refused.
        var otherTown = Stone(world, guilds, town: true);
        Assert.NotNull(guilds.GetRecruitRefusal(otherTown, player));
    }

    [Fact]
    public void ACandidateSomewhereElseIsNotYetAMemberThere()
    {
        // The reference keys the clash off membership, not off a pending
        // application: an outstanding candidacy does not lock anybody out.
        var (world, guilds) = Setup();
        var first = Stone(world, guilds);
        var second = Stone(world, guilds);
        var player = Player(world);
        Assert.NotNull(guilds.TryAddRecruit(first, player));   // candidate only

        Assert.Null(guilds.GetRecruitRefusal(second, player));
    }

    // ---- through the script verb ------------------------------------------

    [Fact]
    public void TheApplyToJoinVerbAnswersToTheSameGate()
    {
        var (world, guilds) = Setup();
        var first = Stone(world, guilds);
        var second = Stone(world, guilds);
        var player = Player(world);
        Assert.NotNull(guilds.TryJoinAsMember(first, player));

        var savedGuild = Item.ResolveGuild;
        var savedManager = Item.ResolveGuildManager;
        try
        {
            Item.ResolveGuildManager = () => guilds;
            Item.ResolveGuild = uid => guilds.GetGuild(uid);
            var secondStone = world.FindItem(second.StoneUid)!;

            Assert.True(secondStone.TryExecuteCommand("APPLYTOJOIN",
                $"0{player.Uid.Value:X}", null!, out _));

            Assert.Null(second.FindMember(player.Uid));
            // ...and no second stone memory was stamped on the player.
            Assert.Null(player.Memory_FindObj(second.StoneUid));
        }
        finally
        {
            Item.ResolveGuild = savedGuild;
            Item.ResolveGuildManager = savedManager;
        }
    }
}
