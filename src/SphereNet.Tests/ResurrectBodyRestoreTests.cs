using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What body a resurrected character comes back in.
///
/// Upstream picks the ghost body FROM the body the character had when they died and
/// puts that same body back afterwards: CChar::Death reads _iPrev_id
/// (CCharAct.cpp:4448) and CChar::Spell_Resurrection does SetID(_iPrev_id)
/// (CCharSpell.cpp:465). _iPrev_id is the OBODY property.
///
/// Nothing recorded it here. The resurrect path had only the ghost body to work back
/// from, so it mapped 0x192/0x193 to the plain human bodies - which is right for a
/// plain human and wrong for everybody else. A staff character on a GM body came back
/// as c_man while its own BODY property still read c_man_gm, and setting BODY again by
/// hand was the only way to get the look back.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ResurrectBodyRestoreTests
{
    private readonly ITestOutputHelper _out;
    public ResurrectBodyRestoreTests(ITestOutputHelper output) => _out = output;

    private static (GameWorld World, SphereNet.Game.Clients.GameClient Client, Character Player) Stage(
        ushort body, int port)
    {
        var world = TestHarness.CreateWorld();
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = body;
        player.MaxHits = 50;
        player.Hits = 50;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return (world, client, player);
    }

    [Theory]
    [InlineData((ushort)0x03DB)]   // a staff body (c_man_gm in the stock pack)
    [InlineData((ushort)0x0025)]   // an elf
    [InlineData((ushort)0x029A)]   // a gargoyle
    [InlineData((ushort)0x0190)]   // and the plain human the old mapping was written for
    public void ACharacterComesBackInTheBodyItDiedIn(ushort body)
    {
        var (_, client, player) = Stage(body, 16110 + body % 50);

        player.Kill();          // the caller kills, then tells the client (Source-X order)
        client.OnCharacterDeath();
        _out.WriteLine($"died as {body:X4} -> ghost {player.BodyId:X4}, obody {player.OBody:X4}");
        Assert.True(player.IsDead);
        Assert.True(player.BodyId is 0x0192 or 0x0193, $"ghost body was {player.BodyId:X4}");

        client.OnResurrect();

        Assert.False(player.IsDead);
        Assert.Equal(body, player.BodyId);
    }

    [Fact]
    public void TheRedrawTheClientGetsCarriesThatSameBody()
    {
        // The property and the picture have to agree: the report was a character whose
        // BODY still said one thing while the screen showed another.
        var (_, client, player) = Stage(0x03DB, 16180);
        player.Kill();
        client.OnCharacterDeath();
        client.OnResurrect();

        var drawPlayer = TestHarness.GetQueuedPackets(client.NetState)
            .LastOrDefault(p => p.Span.Length >= 19 && p.Span[0] == 0x20);
        Assert.NotNull(drawPlayer);
        ushort drawn = (ushort)((drawPlayer!.Span[5] << 8) | drawPlayer.Span[6]);

        _out.WriteLine($"0x20 redraw body {drawn:X4}, char body {player.BodyId:X4}");
        Assert.Equal(player.BodyId, drawn);
        Assert.Equal((ushort)0x03DB, drawn);
    }

    [Fact]
    public void AFemaleGhostIsStillAFemaleGhost()
    {
        var (_, client, player) = Stage(0x0191, 16190);
        player.Kill();
        client.OnCharacterDeath();

        Assert.Equal((ushort)0x0193, player.BodyId);
        client.OnResurrect();
        Assert.Equal((ushort)0x0191, player.BodyId);
    }
}
