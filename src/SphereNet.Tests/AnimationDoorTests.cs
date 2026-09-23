using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// One door for every animation.
///
/// Two corrections have to happen before an animation packet is built, and neither is
/// obvious at a call site: the action id is relative to the body playing it - and to
/// whether that body is in a saddle - and the packet itself depends on the viewer,
/// because a KR/Enhanced client takes the body-agnostic 0xE2 and never sees a 0x6E.
///
/// Both corrections already existed in this engine, in two different helpers, and
/// eleven call sites built the raw packet and got neither. Eating, bowing and crafting
/// in the saddle played the on-foot animation, which ClassicUO draws by taking the
/// rider off the horse and putting them back.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class AnimationDoorTests
{
    private readonly ITestOutputHelper _out;
    public AnimationDoorTests(ITestOutputHelper output) => _out = output;

    private const byte LegacyAnim = 0x6E;

    private static Character Actor(ushort body = 0x0190)
    {
        var ch = new Character { BodyId = body };
        ch.SetUid(new Serial(0x00000100));
        return ch;
    }

    /// <summary>Capture what the shared door would put on the wire.</summary>
    private static List<PacketWriter> Played(Character actor)
    {
        var sent = new List<PacketWriter>();
        GameClient.PlayAnimation(actor, (ushort)AnimationType.Eat,
            18, (_, _, packet, _) => sent.Add(packet), forEachClientInRange: null);
        return sent;
    }

    private static ushort AnimOf(PacketWriter p)
    {
        var buf = p.Build();
        var span = buf.Span;
        Assert.Equal(LegacyAnim, span[0]);
        return (ushort)((span[5] << 8) | span[6]);
    }

    [Fact]
    public void AFootAnimationIsTranslatedForARider()
    {
        var walker = Actor();
        var rider = Actor();
        rider.SetStatFlag(StatFlag.OnHorse);

        ushort onFoot = AnimOf(Played(walker).Single());
        ushort inSaddle = AnimOf(Played(rider).Single());

        _out.WriteLine($"eat on foot {onFoot:X2}, in the saddle {inSaddle:X2}");
        Assert.Equal((ushort)AnimationType.Eat, onFoot);
        Assert.NotEqual(onFoot, inSaddle);
        // Source-X's horseback table rides a bow, a salute or a meal as the crossbow
        // attack (GenerateAnimate, CCharAct.cpp:896-899).
        Assert.Equal((ushort)AnimationType.HorseAttackXBow, inSaddle);
    }

    [Fact]
    public void ANonHumanoidGetsItsOwnFrameSet()
    {
        // A creature has no "eat" frame where a human does; the translator maps it.
        var human = Actor(0x0190);
        var creature = Actor(0x000C);        // a dragon

        ushort humanAnim = AnimOf(Played(human).Single());
        ushort creatureAnim = AnimOf(Played(creature).Single());

        _out.WriteLine($"human {humanAnim:X2}, creature {creatureAnim:X2}");
        Assert.NotEqual(humanAnim, creatureAnim);
    }

    [Fact]
    public void AModernClientGetsTheLegacyPacketAndAnEnhancedOneDoesNot()
    {
        var actor = Actor();
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();

        var classic = TestHarness.CreateClient(lf, world, new SphereNet.Game.Accounts.AccountManager(lf), 17101);
        var enhanced = TestHarness.CreateClient(lf, world, new SphereNet.Game.Accounts.AccountManager(lf), 17102);
        enhanced.NetState.ClientTypeFlag = 3;   // ParsedClientType 3 = Enhanced

        GameClient.PlayAnimation(actor, (ushort)AnimationType.Eat, 18,
            broadcastNearby: null,
            forEachClientInRange: (_, _, _, act) =>
            {
                act(actor, classic);
                act(actor, enhanced);
            });

        byte[] classicOps = TestHarness.GetQueuedPackets(classic.NetState).Select(p => p.Span[0]).ToArray();
        byte[] enhancedOps = TestHarness.GetQueuedPackets(enhanced.NetState).Select(p => p.Span[0]).ToArray();

        _out.WriteLine($"classic got [{string.Join(",", classicOps.Select(o => $"0x{o:X2}"))}], " +
                       $"enhanced got [{string.Join(",", enhancedOps.Select(o => $"0x{o:X2}"))}]");
        Assert.Contains((byte)0x6E, classicOps);
        Assert.Contains((byte)0xE2, enhancedOps);
        Assert.DoesNotContain((byte)0x6E, enhancedOps);
    }

    [Fact]
    public void NoCallSiteBuildsTheRawPacketAnyMore()
    {
        // The guardrail: every animation has to come through the door, or the two
        // corrections above are silently skipped again. The only places allowed to
        // construct the packet are the two dispatchers in GameClient.PacketHelpers.
        string root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs",
                     SearchOption.AllDirectories))
        {
            if (file.Contains("SphereNet.Tests") || file.Contains(Path.Combine("obj", "")))
                continue;
            string text = File.ReadAllText(file);
            if (!text.Contains("PacketAnimation("))
                continue;
            if (Path.GetFileName(file) == "GameClient.PacketHelpers.cs")
                continue;                       // the two dispatchers live here
            if (Path.GetFileName(file) == "ExtendedPackets.cs")
                continue;                       // the packet's own definition
            if (Path.GetFileName(file) == "ActiveSkillEngine.cs" ||
                Path.GetFileName(file) == "Character.cs")
                continue;                       // translate both ways already; see the audit
            offenders.Add(Path.GetFileName(file));
        }

        _out.WriteLine(offenders.Count == 0 ? "no raw call sites" : string.Join(", ", offenders));
        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
