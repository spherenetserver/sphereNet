using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Party;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-D (wiki/hedef.txt) — notoriety decision-tree parity with Source-X
// Noto_CalcFlag (CCharNotoriety.cpp):
//   * party/guild resolve BEFORE criminal/murderer — a same-party murderer is
//     GREEN to his fellows (pre-W-D he rendered red)
//   * karma-evil / karma-neutral players (PLAYEREVIL / PLAYERNEUTRAL) render
//     red / grey with zero murders
//   * a murderer viewing himself sees red (fSelfCheck skips guilds only)
//   * incognito is checked before invul
public class ParityWaveDTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void PartyMemberMurderer_ShowsGreenToFellow()
    {
        var saved = Character.ResolvePartyFinder;
        try
        {
            var world = CreateWorld();
            var viewer = MakePlayer(world, 100);
            var murderer = MakePlayer(world, 101);
            murderer.Kills = (short)(Character.MurderMinCount + 1); // red to strangers

            var pm = new PartyManager();
            pm.CreateParty(viewer.Uid);
            pm.ForceAddMember(viewer.Uid, murderer.Uid);
            Character.ResolvePartyFinder = uid => pm.FindParty(uid);

            // Same party wins over the murderer branch (Source-X order).
            Assert.Equal(2, GameClient.ComputeNotoriety(world, viewer, murderer));

            // A stranger still sees red.
            var stranger = MakePlayer(world, 102);
            Assert.Equal(6, GameClient.ComputeNotoriety(world, stranger, murderer));
        }
        finally
        {
            Character.ResolvePartyFinder = saved;
        }
    }

    [Fact]
    public void KarmaThresholds_MakePlayersEvilOrNeutral_WithoutMurders()
    {
        var world = CreateWorld();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);

        subject.Karma = 0;
        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, subject));

        // Below PLAYERNEUTRAL (-2000 default) → grey, zero murders.
        subject.Karma = -2500;
        Assert.Equal(3, GameClient.ComputeNotoriety(world, viewer, subject));

        // Below PLAYEREVIL (-8000 default) → red, zero murders.
        subject.Karma = -8500;
        Assert.Equal(6, GameClient.ComputeNotoriety(world, viewer, subject));
    }

    [Fact]
    public void Murderer_SeesHimselfRed()
    {
        var world = CreateWorld();
        var murderer = MakePlayer(world, 100);
        murderer.Kills = (short)(Character.MurderMinCount + 1);

        // Source-X fSelfCheck skips party/guild but still resolves evil.
        Assert.Equal(6, GameClient.ComputeNotoriety(world, murderer, murderer));
    }

    [Fact]
    public void IncognitoBeatsInvul()
    {
        var world = CreateWorld();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);
        subject.SetStatFlag(StatFlag.Invul);
        subject.SetStatFlag(StatFlag.Incognito);

        // Source-X checks incognito first → neutral, not invul-yellow.
        Assert.Equal(3, GameClient.ComputeNotoriety(world, viewer, subject));
    }
}
