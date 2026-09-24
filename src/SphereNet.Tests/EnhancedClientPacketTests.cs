using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// Client packets upstream answers and this server did not: the Enhanced Client
/// character creation (0x8D, PacketCreateNew), tip-window paging (0xA7, PacketTipReq ->
/// CClient::Event_Tips), the pre-5.0.9 tooltip request (0xBF 0x10,
/// PacketAosTooltipInfo) and global chat (0xF9, PacketGlobalChatReq) - plus the
/// language field of server-generated 0xAE messages, which upstream leaves empty
/// (CLanguageID(0)) instead of the hard-coded "TRK" it used to carry here.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class EnhancedClientPacketTests
{
    private static NetState NewState(Action<CharCreateInfo>? onCreate = null)
    {
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);
        if (onCreate != null)
            state.CharCreateHandler = (_, info) => onCreate(info);
        return state;
    }

    private static (GameWorld world, GameClient client, Character me) Bench(int id)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Name = "Adventurer";
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (world, client, me);
    }

    private static List<byte[]> Sent(NetState state) =>
        TestHarness.GetQueuedPackets(state).Select(p => p.Span.ToArray()).ToList();

    private static string SpeechText(byte[] p) =>
        Encoding.BigEndianUnicode.GetString(p, 48, p.Length - 50);

    // ==================== registration ====================

    [Theory]
    [InlineData(0x8D)]
    [InlineData(0xA7)]
    [InlineData(0xF9)]
    public void TheOpcodeIsRegistered(int opcode)
    {
        using var network = new NetworkManager(1, TestHarness.CreateLoggerFactory());
        Assert.Contains((byte)opcode, PacketManagerTests.GetRegisteredOpcodesFor(network));
    }

    [Fact]
    public void TheOldTooltipSubcommandIsKnown() =>
        Assert.True(ExtendedCommandRegistry.IsKnown(0x0010));

    // ==================== 0x8D ====================

    /// <summary>The 143 payload bytes after the opcode and the length word, laid out
    /// the way PacketCreateNew::onReceive reads them.</summary>
    private static byte[] CreateNewPayload(byte profession, byte sex, byte race,
        byte str = 20, byte dex = 20, byte intl = 20, byte skill = 25, byte skillVal = 50)
    {
        var p = new List<byte>();
        p.AddRange(new byte[8]);                            // pattern1, pattern2
        var name = new byte[30];
        Encoding.ASCII.GetBytes("Nova").CopyTo(name, 0);
        p.AddRange(name);
        p.AddRange(new byte[30]);                           // unknown
        p.Add(profession);
        p.Add(3);                                           // start location
        p.Add(sex);
        p.Add(race);
        p.Add(str); p.Add(dex); p.Add(intl);
        p.AddRange([0x83, 0xEA]);                           // skin hue
        p.AddRange(new byte[8]);
        for (int i = 0; i < 4; i++) { p.Add((byte)(skill + i)); p.Add(skillVal); }
        p.AddRange(new byte[26]);
        p.AddRange([0x04, 0x4E]);                           // hair hue
        p.AddRange([0x20, 0x3B]);                           // hair id
        p.AddRange(new byte[6]);
        p.AddRange([0x00, 0x21]);                           // shirt hue
        p.AddRange([0x15, 0x17]);                           // shirt id
        p.Add(0);
        p.AddRange([0x00, 0x05]);                           // face hue
        p.AddRange([0x3B, 0x44]);                           // face id
        p.Add(0);
        p.AddRange([0x04, 0x4F]);                           // beard hue
        p.AddRange([0x20, 0x3E]);                           // beard id
        Assert.Equal(146 - 3, p.Count);
        return p.ToArray();
    }

    [Fact]
    public void EnhancedCreationReadsEveryField()
    {
        CharCreateInfo? got = null;
        var state = NewState(i => got = i);

        new PacketCreateCharacterEnhanced().OnReceive(
            new PacketBuffer(CreateNewPayload(profession: 0, sex: 1, race: 2)), state);

        Assert.NotNull(got);
        Assert.Equal("Nova", got!.Name);
        Assert.True(got.Female);
        Assert.Equal(2, got.Race);                  // elf, as a RACE_TYPE
        Assert.Equal(3, got.City);
        Assert.Equal((byte)20, got.Str);
        Assert.Equal(0x83EA, got.SkinHue);
        Assert.Equal(0x203B, got.HairStyle);
        Assert.Equal(0x044E, got.HairHue);
        Assert.Equal(0x203E, got.BeardStyle);
        Assert.Equal(0x044F, got.BeardHue);
        Assert.Equal(0x1517, got.ShirtId);
        Assert.Equal(0x0021, got.ShirtHue);
        Assert.Equal(0x0021, got.PantsHue);         // upstream reuses the shirt hue
        Assert.Equal(0x3B44, got.FaceId);
        Assert.Equal(0x0005, got.FaceHue);
        Assert.Equal(uint.MaxValue, got.ClientFlags);
        Assert.Equal(4, got.Skills.Length);
        Assert.Equal(((byte)25, (byte)50), got.Skills[0]);
        Assert.Equal(((byte)28, (byte)50), got.Skills[3]);
    }

    /// <summary>A profession replaces what the client sent with upstream's table.</summary>
    [Fact]
    public void AProfessionBringsItsFixedStatsAndSkills()
    {
        CharCreateInfo? got = null;
        var state = NewState(i => got = i);

        new PacketCreateCharacterEnhanced().OnReceive(
            new PacketBuffer(CreateNewPayload(profession: 1, sex: 0, race: 1)), state);

        Assert.False(got!.Female);
        Assert.Equal((45, 35, 10), (got.Str, got.Dex, got.Int));
        Assert.Equal(new (byte, byte)[]
        {
            ((byte)SkillType.Swordsmanship, 30), ((byte)SkillType.Tactics, 30),
            ((byte)SkillType.Healing, 30), ((byte)SkillType.Anatomy, 30),
        }, got.Skills);
    }

    [Fact]
    public void TheNinjaProfessionUsesTheNinjaTable()
    {
        CharCreateInfo? got = null;
        new PacketCreateCharacterEnhanced().OnReceive(
            new PacketBuffer(CreateNewPayload(profession: 7, sex: 0, race: 1)), NewState(i => got = i));

        Assert.Equal((40, 30, 10), (got!.Str, got.Dex, got.Int));
        Assert.Equal((byte)SkillType.Ninjitsu, got.Skills[0].Id);
        Assert.Equal((byte)SkillType.Stealth, got.Skills[3].Id);
    }

    /// <summary>The KR client sends the race one lower than SA: upstream subtracts one.</summary>
    [Fact]
    public void AKingdomRebornRaceIsShiftedDownByOne()
    {
        CharCreateInfo? got = null;
        var state = NewState(i => got = i);
        state.ClientTypeFlag = 2;   // Kingdom Reborn

        new PacketCreateCharacterEnhanced().OnReceive(
            new PacketBuffer(CreateNewPayload(profession: 0, sex: 0, race: 3)), state);

        Assert.Equal(2, got!.Race);
    }

    /// <summary>Through the creation path: the face and shirt the packet names are worn.</summary>
    [Fact]
    public void TheCreatedCharacterWearsThePickedFaceAndShirt()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var state = TestHarness.CreateActiveNetState(lf, 19811);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.SetPrivateField(client, "_account", new Account { Name = "ecuser" });
        client.PendingCharCreate = new CharCreateInfo
        {
            Name = "Nova", Race = 1, Str = 30, Dex = 30, Int = 20,
            ShirtId = 0x1517, ShirtHue = 0x0021,
            FaceId = 0x3B44, FaceHue = 0x0005,
        };

        client.HandleCharSelect(-1, "Nova");

        var ch = client.Character;
        Assert.NotNull(ch);
        var face = ch!.GetEquippedItem(Layer.Face);
        Assert.NotNull(face);
        Assert.Equal(0x3B44, face!.BaseId);
        Assert.Equal(0x0005, face.Hue.Value);
        var shirt = ch.GetEquippedItem(Layer.Shirt);
        Assert.NotNull(shirt);
        Assert.Equal(0x1517, shirt!.BaseId);
        Assert.Equal(0x0021, shirt.Hue.Value);
    }

    // ==================== 0xA7 / tips ====================

    [Theory]
    [InlineData(2, true, 2)]    // shown tip context 2 (tip 1), next -> tip 2
    [InlineData(3, false, 1)]   // shown tip context 3 (tip 2), previous -> tip 1
    [InlineData(0, false, 0xFFFE)]  // word arithmetic wraps, as upstream's does
    public void TheTipRequestStepsTheIndexLikeUpstream(int shown, bool forward, int expected)
    {
        ushort asked = 0;
        var state = NewState();
        state.TipRequestHandler = (_, tip) => asked = tip;

        new PacketTipRequest().OnReceive(
            new PacketBuffer([(byte)(shown >> 8), (byte)shown, (byte)(forward ? 1 : 0)]), state);

        Assert.Equal((ushort)expected, asked);
    }

    private static void LoadTips(string text)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"tips-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, text);
        try { stack.Resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
    }

    private static byte[] OnlyScroll(GameClient client)
    {
        var scrolls = Sent(client.NetState).Where(p => p[0] == 0xA6).ToList();
        Assert.Single(scrolls);
        return scrolls[0];
    }

    [Fact]
    public void ATipIsSentAsATipsScrollWithItsNumberPlusOne()
    {
        LoadTips("[TIP 1]\nFirst tip line\n[TIP 2]\nSecond tip line\nand more\n");
        var (_, client, _) = Bench(19812);

        client.HandleTipRequest(2);

        var p = OnlyScroll(client);
        Assert.Equal(0, p[3]);                                        // SCROLL_TYPE_TIPS
        Assert.Equal(3u, (uint)(p[4] << 24 | p[5] << 16 | p[6] << 8 | p[7]));
        string body = Encoding.ASCII.GetString(p, 10, p.Length - 10).TrimEnd('\0');
        Assert.Equal("Second tip line\rand more\r", body);
    }

    [Fact]
    public void TipZeroIsTipOneAndAMissingTipFallsBackToTipOne()
    {
        LoadTips("[TIP 1]\nFirst tip line\n");
        var (_, client, _) = Bench(19813);

        client.HandleTipRequest(0);
        var first = OnlyScroll(client);
        Assert.Equal(2, first[7]);

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleTipRequest(9);
        var fallback = OnlyScroll(client);
        Assert.Equal(2, fallback[7]);
    }

    [Fact]
    public void WithoutTipOneNothingIsSent()
    {
        LoadTips("[TIP 5]\nOrphan\n");
        var (_, client, _) = Bench(19814);

        client.HandleTipRequest(3);

        Assert.DoesNotContain(Sent(client.NetState), p => p[0] == 0xA6);
    }

    // ==================== 0xBF 0x10 ====================

    [Fact]
    public void TheOldTooltipSubcommandAnswersWithATooltip()
    {
        var (world, client, me) = Bench(19815);
        world.ToolTipMode = 1;
        client.NetState.ClientVersionNumber = 40_000_000;
        var other = world.CreateCharacter();
        other.IsPlayer = true;
        other.Name = "someone";
        world.PlaceCharacter(other, new Point3D(101, 100, 0, 0));
        TestHarness.ClearQueuedPackets(client.NetState);

        uint s = other.Uid.Value;
        client.HandleExtendedCommand(0x0010, [(byte)(s >> 24), (byte)(s >> 16), (byte)(s >> 8), (byte)s]);

        Assert.Contains(Sent(client.NetState), p => p[0] == 0xD6 && p.Length > 9 &&
            (uint)(p[5] << 24 | p[6] << 16 | p[7] << 8 | p[8]) == s);
    }

    // ==================== 0xF9 ====================

    [Fact]
    public void TheGlobalChatRequestReadsTheActionAndXml()
    {
        (byte Action, string Xml)? got = null;
        var state = NewState();
        state.GlobalChatHandler = (_, action, xml) => got = (action, xml);

        new PacketGlobalChatRequest().OnReceive(
            new PacketBuffer([0x00, 0x8A, 0x00, (byte)'<', (byte)'x', (byte)'/', (byte)'>', 0x00]), state);

        Assert.Equal(((byte)0x8A, "<x/>"), got);
    }

    [Fact]
    public void WithGlobalChatOffTheRequestIsRefused()
    {
        var (_, client, _) = Bench(19816);
        GameClient.ServerChatFlags = 0;

        client.HandleGlobalChat(PacketGlobalChatOut.ActionStatusToggle, "");

        var sent = Sent(client.NetState);
        Assert.DoesNotContain(sent, p => p[0] == 0xF9);
        Assert.Contains(sent, p => p[0] == 0xAE && SpeechText(p) == "Global Chat is currently unavailable.");
    }

    [Fact]
    public void TheStatusToggleFlipsOnlineAndOffline()
    {
        var (_, client, me) = Bench(19817);
        GameClient.ServerChatFlags = GameClient.ChatFlagGlobalChat;
        client.NetState.ClientVersionNumber = GameClient.MinClientVersionGlobalChat;

        client.SendGlobalChatConnect();
        var connect = Sent(client.NetState).Single(p => p[0] == 0xF9);
        Assert.Equal(PacketGlobalChatOut.ActionConnect, connect[2]);
        Assert.Equal(PacketGlobalChatOut.StanzaInfoQuery, connect[3]);
        string jid = GameClient.FormatGlobalChatJid(me.Name, me.Uid.Value);
        Assert.Contains($"<iq to=\"{jid}\"", Encoding.ASCII.GetString(connect, 4, connect.Length - 5));

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleGlobalChat(PacketGlobalChatOut.ActionStatusToggle, "");
        var on = Sent(client.NetState).Single(p => p[0] == 0xF9);
        Assert.Equal(PacketGlobalChatOut.StanzaPresence, on[3]);
        Assert.Contains("show=\"1\"", Encoding.ASCII.GetString(on));
        Assert.Contains(Sent(client.NetState), p => p[0] == 0xAE && SpeechText(p) == "Global Chat Online");

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleGlobalChat(PacketGlobalChatOut.ActionStatusToggle, "");
        var off = Sent(client.NetState).Single(p => p[0] == 0xF9);
        Assert.Contains("show=\"0\"", Encoding.ASCII.GetString(off));
    }

    [Fact]
    public void TheJidTruncatesTheNameAndPadsTheUid() =>
        Assert.Equal("Advent_0000042@00", GameClient.FormatGlobalChatJid("Adventurer", 42));

    [Fact]
    public void AnOldClientGetsNoGlobalChatStanza()
    {
        var (_, client, _) = Bench(19818);
        GameClient.ServerChatFlags = GameClient.ChatFlagGlobalChat;
        client.NetState.ClientVersionNumber = GameClient.MinClientVersionGlobalChat - 1;

        client.HandleGlobalChat(PacketGlobalChatOut.ActionStatusToggle, "");

        Assert.DoesNotContain(Sent(client.NetState), p => p[0] == 0xF9);
    }

    [Fact]
    public void FriendAddArmsATargetCursorAndSendAndRemoveDoNothing()
    {
        var (_, client, _) = Bench(19819);
        GameClient.ServerChatFlags = GameClient.ChatFlagGlobalChat;

        client.HandleGlobalChat(PacketGlobalChatOut.ActionMessageSend, "<m/>");
        client.HandleGlobalChat(PacketGlobalChatOut.ActionFriendRemove, "<m/>");
        Assert.Empty(Sent(client.NetState));

        client.HandleGlobalChat(PacketGlobalChatOut.ActionFriendAddTarg, "");
        var sent = Sent(client.NetState);
        Assert.Contains(sent, p => p[0] == 0x6C);
        Assert.Contains(sent, p => p[0] == 0xAE &&
            SpeechText(p) == "Target player to request as Global Chat friend.");
    }

    [Fact]
    public void TheOutgoingStanzaHasNoLengthWord()
    {
        var p = new PacketGlobalChatOut(0, PacketGlobalChatOut.ActionConnect,
            PacketGlobalChatOut.StanzaPresence, "<p/>").Build().Span.ToArray();

        const string doc = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><ultima_stanza><p/></ultima_stanza>";
        Assert.Equal(4 + doc.Length + 1, p.Length);
        Assert.Equal([0xF9, 0x00, 0xB9, 0x00], p[..4]);
        Assert.Equal(doc, Encoding.ASCII.GetString(p, 4, doc.Length));
        Assert.Equal(0, p[^1]);
    }

    // ==================== 0xAE language ====================

    private static byte[] LanguageField(byte[] p) => p[14..18];

    [Fact]
    public void ASystemMessageCarriesNoLanguage()
    {
        var (_, client, _) = Bench(19820);

        client.SysMessage("hello");

        var p = Sent(client.NetState).Single(x => x[0] == 0xAE);
        Assert.Equal(new byte[4], LanguageField(p));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("DEU", "DEU")]
    [InlineData("ENUX", "ENU")]
    [InlineData("TR\0\0", "TR")]
    [InlineData(" ab", "")]
    public void TheLanguageIsNormalisedLikeCLanguageIdSet(string? input, string expected) =>
        Assert.Equal(expected, PacketSpeechUnicodeOut.NormalizeLanguage(input));

    /// <summary>A player's unicode speech records the packet's language, and the
    /// relayed speech goes out in it (Event_TalkUNICODE -> m_lang -> SpeakUTF8Ex).</summary>
    [Fact]
    public void PlayerSpeechIsRelayedInTheSpeakersLanguage()
    {
        var (_, client, _) = Bench(19821);
        var state = client.NetState;
        string? spoken = null;
        state.SpeechHandler = (_, _, _, _, text) => spoken = text;

        var payload = new List<byte> { 0x00, 0x03, 0xB2, 0x00, 0x03 };
        payload.AddRange(Encoding.ASCII.GetBytes("DEU\0"));
        payload.AddRange(Encoding.BigEndianUnicode.GetBytes("hallo"));
        payload.AddRange([0, 0]);
        new PacketSpeechUnicode().OnReceive(new PacketBuffer(payload.ToArray()), state);

        Assert.Equal("hallo", spoken);
        Assert.Equal("DEU", state.ClientLanguage);

        TestHarness.ClearQueuedPackets(state);
        client.HandleSpeech(0, 0x03B2, 3, "hallo");
        var p = Sent(state).First(x => x[0] == 0xAE);
        Assert.Equal("DEU\0"u8.ToArray(), LanguageField(p));
    }
}
