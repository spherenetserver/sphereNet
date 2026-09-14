using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// SPELLTIMEOUT — the clock on a spell's target cursor (port plan İŞ-70, PLAN-302).
///
/// An armed cursor with no deadline is not harmless. It survives the rest of the
/// session, so the next thing the player clicks — a door, a friend — answers a spell
/// they cast minutes ago. Upstream stamps a deadline when it opens the cursor
/// (CClientMsg.cpp:1751) and cancels it from the character's own tick
/// (CCharAct.cpp:6061).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellTargetTimeoutTests
{
    private readonly ITestOutputHelper _out;
    public SpellTargetTimeoutTests(ITestOutputHelper output) => _out = output;

    private static (GameClient Client, SphereNet.Game.Objects.Characters.Character Me) Stage()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7011);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.MaxHits = 100; me.Hits = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (client, me);
    }

    [Fact]
    public void ACursorWithNoTimeoutStaysOpen()
    {
        var (client, _) = Stage();
        ClientTargetingHandler.SpellTimeoutSeconds = 0;

        client.SetPendingTarget((_, _, _, _, _) => { });
        client.Targeting.ArmSpellTimeout();
        client.Targeting.TickTargetTimeout();

        _out.WriteLine($"deadline={client.Targets.TimeoutAtMs} active={client.HasPendingTarget}");

        // Zero is upstream's default and means never (CServerConfig.cpp:86). The cursor
        // has to survive an ordinary tick, or every cast would cancel itself.
        Assert.Equal(0, client.Targets.TimeoutAtMs);
        Assert.True(client.HasPendingTarget);
    }

    [Fact]
    public void AnExpiredCursorIsGivenUpOnTheNextTick()
    {
        var (client, _) = Stage();
        ClientTargetingHandler.SpellTimeoutSeconds = 5;

        client.SetPendingTarget((_, _, _, _, _) => { });
        client.Targeting.ArmSpellTimeout();
        Assert.True(client.HasPendingTarget);

        // Reach back past the deadline rather than waiting five seconds for it.
        client.Targets.TimeoutAtMs = Environment.TickCount64 - 1;
        client.Targeting.TickTargetTimeout();

        _out.WriteLine($"after expiry: active={client.HasPendingTarget}");
        Assert.False(client.HasPendingTarget);
        Assert.Equal(0, client.Targets.TimeoutAtMs);
    }

    [Fact]
    public void ACursorInsideItsDeadlineIsLeftAlone()
    {
        var (client, _) = Stage();
        ClientTargetingHandler.SpellTimeoutSeconds = 30;

        client.SetPendingTarget((_, _, _, _, _) => { });
        client.Targeting.ArmSpellTimeout();
        client.Targeting.TickTargetTimeout();

        // The tick runs every server frame, so the common case is "not yet" and it must
        // cost the cursor nothing.
        Assert.True(client.HasPendingTarget);
        Assert.True(client.Targets.TimeoutAtMs > Environment.TickCount64);
    }

    [Fact]
    public void TheCharactersOwnTagBeatsTheServerSetting()
    {
        var (client, me) = Stage();
        ClientTargetingHandler.SpellTimeoutSeconds = 600;
        me.SetTag("SPELLTIMEOUT", "2");

        client.SetPendingTarget((_, _, _, _, _) => { });
        client.Targeting.ArmSpellTimeout();

        long budgetMs = client.Targets.TimeoutAtMs - Environment.TickCount64;
        _out.WriteLine($"server 600s, tag 2s -> {budgetMs} ms");

        // A script gives one spell, or one player, a different patience
        // (CClientUse.cpp:1061). Reading the server value instead would make the tag
        // look supported and behave as though it were not.
        Assert.InRange(budgetMs, 1, 2000);
    }

    [Fact]
    public void AnsweringTheCursorClearsTheDeadlineToo()
    {
        var (client, me) = Stage();
        ClientTargetingHandler.SpellTimeoutSeconds = 30;
        bool answered = false;

        client.SetPendingTarget((_, _, _, _, _) => answered = true);
        client.Targeting.ArmSpellTimeout();
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, me.Uid.Value, 0, 0, 0, 0);

        _out.WriteLine($"answered={answered} deadline={client.Targets.TimeoutAtMs}");

        // A consumed cursor must not leave its clock behind: the next cursor armed
        // without a timeout would inherit the old deadline and cancel itself.
        Assert.True(answered);
        Assert.Equal(0, client.Targets.TimeoutAtMs);
    }
}
