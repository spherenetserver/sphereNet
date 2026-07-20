using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// C5: MinCharDeleteTime — a character younger than the configured day count
/// cannot be deleted from char select (Source-X Setup_Delete, 0x85 reason 3);
/// Counsel+ accounts and legacy pre-stamp characters bypass.
/// D2: spells in the unimplemented-school id space with no behaviour at all
/// are refused at cast start instead of consuming mana/reagents and no-oping.
/// </summary>
public sealed class CharDeleteAndSpellGateTests
{
    private static (GameClient Client, SphereNet.Network.State.NetState State, Account Account,
        SphereNet.Game.Objects.Characters.Character Ch, GameWorld World) MakeCharDeleteHarness(
        ILoggerFactory loggerFactory)
    {
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(20_000, 30_000));
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());

        var account = new Account { Name = "tester" };
        account.SetPassword("pw");
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "victim";
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        account.SetCharSlot(0, ch.Uid);
        TestHarness.AttachCharacter(client, null!, account);
        return (client, state, account, ch, world);
    }

    [Fact]
    public void CharDelete_YoungCharacter_RejectedWithNotOldEnough()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var (client, state, _, ch, world) = MakeCharDeleteHarness(loggerFactory);
        int saved = GameClient.ServerMinCharDeleteDays;
        try
        {
            GameClient.ServerMinCharDeleteDays = 7;
            ch.CreatedUtcSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600; // 1 hour old

            client.HandleCharDelete(0, "pw");

            var packets = TestHarness.GetQueuedPackets(state).ToList();
            // 0x85 with reason 3 = "character is not old enough".
            Assert.Contains(packets, p => p.Span[0] == 0x85 && p.Span[1] == 3);
            Assert.NotNull(world.FindChar(ch.Uid)); // still alive
        }
        finally { GameClient.ServerMinCharDeleteDays = saved; }
    }

    [Fact]
    public void CharDelete_OldOrLegacyCharacter_Deletes()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var (client, state, _, ch, world) = MakeCharDeleteHarness(loggerFactory);
        int saved = GameClient.ServerMinCharDeleteDays;
        try
        {
            GameClient.ServerMinCharDeleteDays = 7;
            // Legacy save: no creation stamp → treated as old enough.
            ch.CreatedUtcSeconds = 0;

            client.HandleCharDelete(0, "pw");

            var packets = TestHarness.GetQueuedPackets(state).ToList();
            Assert.Contains(packets, p => p.Span[0] == 0x85 && p.Span[1] == 0); // success
            Assert.Null(world.FindChar(ch.Uid));
        }
        finally { GameClient.ServerMinCharDeleteDays = saved; }
    }

    [Fact]
    public void InertSchoolSpell_Classification()
    {
        // A Bushido spell with no flags, no curves, no scripted stages = inert.
        Assert.True(SpellEngine.IsInertSchoolSpell(new SpellDef { Id = SpellType.Confidence }));

        // Audit finding (wiki/1.txt #3): marker flags alone (good/harm/fx) or
        // a bare effect curve still no-op through the dispatcher — Source-X
        // damages only on SPELLFLAG_DAMAGE — so they are inert too; casting
        // them would just burn mana/reagents.
        Assert.True(SpellEngine.IsInertSchoolSpell(new SpellDef
        { Id = SpellType.Confidence, Flags = SpellFlag.Good }));
        Assert.True(SpellEngine.IsInertSchoolSpell(new SpellDef
        { Id = SpellType.Confidence, Flags = SpellFlag.Harm | SpellFlag.TargChar }));
        Assert.True(SpellEngine.IsInertSchoolSpell(new SpellDef
        { Id = SpellType.Confidence, EffectBase = 5, EffectScale = 20 }));

        // ACTIONABLE flags or scripted stages make the spell castable.
        Assert.False(SpellEngine.IsInertSchoolSpell(new SpellDef
        { Id = SpellType.Confidence, Flags = SpellFlag.Damage | SpellFlag.TargChar }));
        Assert.False(SpellEngine.IsInertSchoolSpell(new SpellDef
        { Id = SpellType.Confidence, Flags = SpellFlag.Heal | SpellFlag.TargChar }));
        Assert.False(SpellEngine.IsInertSchoolSpell(new SpellDef
        { Id = SpellType.Confidence, HasScriptedStages = true }));

        // Natively-handled school spells are always castable.
        Assert.False(SpellEngine.IsInertSchoolSpell(new SpellDef { Id = SpellType.DivineFury }));
        Assert.False(SpellEngine.IsInertSchoolSpell(new SpellDef { Id = SpellType.GiftOfRenewal }));

        // Magery / Necromancy / custom ids never hit the gate.
        Assert.False(SpellEngine.IsInertSchoolSpell(new SpellDef { Id = SpellType.Heal }));
        Assert.False(SpellEngine.IsInertSchoolSpell(new SpellDef { Id = SpellType.Wither }));
        Assert.False(SpellEngine.IsInertSchoolSpell(new SpellDef { Id = SpellType.SummonUndead }));
    }
}
