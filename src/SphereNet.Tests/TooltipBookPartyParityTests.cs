using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Books, the profile packet, the built-in property list, party forwarding, the pet
/// rename entry and the information skills, each against the reference line it
/// follows (cited on the test).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TooltipBookPartyParityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_tbp_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly ILoggerFactory Lf = LoggerFactory.Create(_ => { });

    private static GameWorld World()
    {
        var world = new GameWorld(Lf);
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient Client, Character Actor) NewClient(GameWorld world, int id, short x = 100)
    {
        var client = TestHarness.CreateClient(Lf, world, new AccountManager(Lf), id);
        client.NetState.ClientVersionNumber = 70_020_000;
        var actor = world.CreateCharacter();
        actor.BodyId = 0x0190;
        actor.IsPlayer = true;
        actor.Name = "Actor" + id;
        world.PlaceCharacter(actor, new Point3D(x, 100, 0, 0));
        TestHarness.AttachCharacter(client, actor);
        return (client, actor);
    }

    private static List<byte[]> Sent(GameClient client, byte opcode) =>
        TestHarness.GetQueuedPackets(client.NetState)
            .Where(p => p.Span.Length > 0 && p.Span[0] == opcode)
            .Select(p => p.Span.ToArray()).ToList();

    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] FixedAscii(string text, int width)
    {
        var bytes = new byte[width];
        Encoding.ASCII.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    // ------------------------------------------------------------------ books

    /// <summary>receive.cpp:1719-1721: the client's "writable" byte (always 0) is
    /// skipped, so the edit is not dropped at the parser.</summary>
    [Fact]
    public void BookHeaderEdit_ClientWritableByteIsNotTrusted()
    {
        var state = TestHarness.CreateActiveNetState(Lf, 1);
        (string Title, string Author)? seen = null;
        state.BookHeaderHandler = (_, _, _, title, author) => seen = (title, author);

        var wire = new List<byte>();
        wire.AddRange(U32(0x40001234));
        wire.Add(0);                       // writable, as the client sends it
        wire.Add(1);                       // unknown
        wire.AddRange(U16(16));
        wire.AddRange(FixedAscii("My Diary", 60));
        wire.AddRange(FixedAscii("Me", 30));
        new PacketBookHeader().OnReceive(new PacketBuffer(wire.ToArray()), state);

        Assert.Equal(("My Diary", "Me"), seen);
    }

    /// <summary>receive.cpp:3549-3551: unknown(1), writable(1), pages(2) precede the
    /// length-prefixed strings.</summary>
    [Fact]
    public void NewBookHeaderEdit_FieldsAreAligned()
    {
        var state = TestHarness.CreateActiveNetState(Lf, 2);
        (uint Serial, string Title, string Author)? seen = null;
        state.BookHeaderHandler = (_, serial, _, title, author) => seen = (serial, title, author);

        var wire = new List<byte>();
        wire.AddRange(U32(0x40001234));
        wire.Add(0);                       // unknown
        wire.Add(0);                       // writable
        wire.AddRange(U16(16));            // pages
        wire.AddRange(U16(6));
        wire.AddRange(Encoding.ASCII.GetBytes("Title\0"));
        wire.AddRange(U16(5));
        wire.AddRange(Encoding.ASCII.GetBytes("Auth\0"));
        new PacketNewBookHeader().OnReceive(new PacketBuffer(wire.ToArray()), state);

        Assert.Equal((0x40001234u, "Title", "Auth"), seen);
    }

    /// <summary>Event_Book_Title (CClientEvent.cpp:171) decides by the book alone.</summary>
    [Fact]
    public void BookHeaderEdit_WritableBookIsRetitledWhateverTheFlag()
    {
        var world = World();
        var (client, actor) = NewClient(world, 5301);
        var book = world.CreateItem();
        book.ItemType = ItemType.Book;
        world.PlaceItem(book, actor.Position);

        client.HandleBookHeader(book.Uid.Value, false, "Field Notes", "Actor");

        Assert.Equal("Field Notes", book.Name);
        Assert.True(book.TryGetTag("BOOK_AUTHOR", out string? author));
        Assert.Equal("Actor", author);
    }

    private ResourceHolder LoadScript(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "t.scp");
        File.WriteAllLines(file, lines);
        var resources = new ResourceHolder(Lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        return resources;
    }

    /// <summary>send.cpp:2883-2910 and :1688-1697: a book whose MORE1 names a [BOOK]
    /// resource shows that resource's title, author and pages, read-only.</summary>
    [Fact]
    public void SystemBook_OpensFromItsResourceAndIsReadOnly()
    {
        LoadScript(
            "[BOOK b_probe]",
            "PAGES=2",
            "TITLE=Probe Tome",
            "AUTHOR=Tester",
            "",
            "[BOOK b_probe 1]",
            "first line",
            "second line",
            "",
            "[BOOK b_probe 2]",
            "last page");

        var world = World();
        var (client, actor) = NewClient(world, 5302);
        var book = world.CreateItem();
        book.ItemType = ItemType.Book;
        book.SetTag("MORE1_DEFNAME", "b_probe");
        world.PlaceItem(book, actor.Position);

        Assert.Equal("b_probe", GameClient.SystemBookName(book));
        Assert.Equal(("Probe Tome", "Tester", 2), GameClient.ReadSystemBookHeader("b_probe"));
        Assert.Equal(new[] { "first line", "second line" }, GameClient.ReadSystemBookPage("b_probe", 1));
        Assert.False(client.IsBookWritableFor(book));

        TestHarness.ClearQueuedPackets(client.NetState);
        client.OpenBook(book, writable: true);

        var header = Assert.Single(Sent(client, 0x93));
        Assert.Equal(0, header[5]);                                   // not writable
        Assert.Equal(2, (header[7] << 8) | header[8]);                // pages
        Assert.Equal("Probe Tome", Encoding.ASCII.GetString(header, 9, 60).TrimEnd('\0'));
        Assert.Equal("Tester", Encoding.ASCII.GetString(header, 69, 30).TrimEnd('\0'));
        Assert.Equal("Probe Tome", book.Name);
        string pages = Encoding.ASCII.GetString(Assert.Single(Sent(client, 0x66)));
        Assert.Contains("second line", pages);
        Assert.Contains("last page", pages);

        // Neither a page nor the header can be written.
        client.HandleBookPage(book.Uid.Value, [((ushort)1, new[] { "scribble" })]);
        client.HandleBookHeader(book.Uid.Value, true, "Mine", "Me");
        Assert.False(book.TryGetTag("PAGE_1", out _));
        Assert.Equal("Probe Tome", book.Name);
    }

    /// <summary>send.cpp:2920: a user book with no author says DEFMSG_BOOK_AUTHOR_UNKNOWN.</summary>
    [Fact]
    public void UserBook_WithoutAuthorSaysUnknown()
    {
        var world = World();
        var (client, actor) = NewClient(world, 5303);
        var book = world.CreateItem();
        book.ItemType = ItemType.Book;
        world.PlaceItem(book, actor.Position);

        TestHarness.ClearQueuedPackets(client.NetState);
        client.OpenBook(book, writable: false);

        var header = Assert.Single(Sent(client, 0x93));
        Assert.Equal(ServerMessages.Get(Msg.BookAuthorUnknown),
            Encoding.ASCII.GetString(header, 69, 30).TrimEnd('\0'));
    }

    // ---------------------------------------------------------------- profile

    /// <summary>send.cpp:3828-3841: uid, ASCII name, unicode title line, unicode body.</summary>
    [Fact]
    public void ProfilePacket_IsNameThenTitleThenBody()
    {
        var span = new PacketProfileResponse(0x00001234, "Bob", "T", "hi").Build().Span.ToArray();

        Assert.Equal(0xB8, span[0]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x12, 0x34 }, span[3..7]);
        Assert.Equal(new byte[] { (byte)'B', (byte)'o', (byte)'b', 0 }, span[7..11]);
        Assert.Equal(new byte[] { 0, (byte)'T', 0, 0 }, span[11..15]);
        Assert.Equal(new byte[] { 0, (byte)'h', 0, (byte)'i', 0, 0 }, span[15..21]);
        Assert.Equal(21, span.Length);
        Assert.Equal(21, (span[1] << 8) | span[2]);
    }

    /// <summary>Event_Profile (CClientEvent.cpp:1609-1621): the text is the
    /// character's PROFILE; a line feed refuses it; a GM below the owner's plevel
    /// cannot write it.</summary>
    [Fact]
    public void ProfileWrite_GoesToProfileUnderTheReferenceRules()
    {
        var world = World();
        var (client, actor) = NewClient(world, 5304);

        client.HandleProfileRequest(1, actor.Uid.Value, "I am here");
        Assert.Equal("I am here", actor.Profile);
        Assert.False(actor.TryGetTag("PROFILE_BIO", out _));

        client.HandleProfileRequest(1, actor.Uid.Value, "two\nlines");
        Assert.Equal("I am here", actor.Profile);

        var other = world.CreateCharacter();
        other.IsPlayer = true;
        other.PrivLevel = PrivLevel.Admin;
        world.PlaceCharacter(other, actor.Position);
        client.HandleProfileRequest(1, other.Uid.Value, "not mine");
        Assert.Equal("", other.Profile);
        actor.PrivLevel = PrivLevel.GM;                 // GM, but below the Admin
        client.HandleProfileRequest(1, other.Uid.Value, "still not mine");
        Assert.Equal("", other.Profile);
        actor.PrivLevel = PrivLevel.Admin;
        client.HandleProfileRequest(1, other.Uid.Value, "staff note");
        Assert.Equal("staff note", other.Profile);
    }

    /// <summary>A text an earlier build left in the tag is still shown, and the
    /// answer is laid out name / "noto, trade" / body.</summary>
    [Fact]
    public void ProfileRead_FallsBackToTheOldTagOnce()
    {
        var world = World();
        var (client, actor) = NewClient(world, 5305);
        actor.SetTag("PROFILE_BIO", "old text");
        Assert.Equal("old text", ClientInventoryHandler.ReadProfileText(actor));

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleProfileRequest(0, actor.Uid.Value);
        var packet = Assert.Single(Sent(client, 0xB8));
        Assert.Equal(actor.Name, Encoding.ASCII.GetString(packet, 7, actor.Name.Length));
        Assert.Contains("old text", Encoding.BigEndianUnicode.GetString(packet, 7 + actor.Name.Length + 1,
            packet.Length - 8 - actor.Name.Length));

        actor.Profile = "new text";
        Assert.Equal("new text", ClientInventoryHandler.ReadProfileText(actor));
    }

    // --------------------------------------------------------------- tooltips

    private static List<(uint ClilocId, string Args)> ItemLines(Item item, GameWorld world,
        bool featureMl = true, bool viewerIsGm = false)
    {
        var props = new List<(uint ClilocId, string Args)>();
        DefaultTooltipData.AppendItemData(item, world, featureMl, elementalEngine: false,
            viewerIsGm, isMultiAt: null, props);
        return props;
    }

    /// <summary>CClientMsg_AOSTooltip.cpp:530-577, in order.</summary>
    [Fact]
    public void WeaponTooltip_ReadsTheItemsOwnRatings()
    {
        DefinitionLoader.SetItemDef(0x13B9, new ItemDef(ResourceId.Invalid)
        {
            DispIndex = 0x13B9, Type = ItemType.WeaponSword, Name = "probe blade",
            AttackMin = 3, AttackMax = 15, Speed = 40, ReqStr = 25, Layer = Layer.OneHanded,
        });
        var world = World();
        var sword = world.CreateItem();
        sword.BaseId = 0x13B9;
        sword.ItemType = ItemType.WeaponSword;
        sword.SetAttr(ObjAttributes.Move_Never);
        sword.HitsMax = 50;
        sword.HitsCur = 31;
        sword.MoreP = new Point3D(0, 0, 20, 0);          // poison coat
        world.PlaceItem(sword, new Point3D(100, 100, 0, 0));

        Assert.Equal(new (uint, string)[]
        {
            (1017383, ""),          // poisoned
            (1061168, "3\t15"),     // weapon damage
            (1061167, "40"),        // weapon speed
            (1061170, "25"),        // strength requirement
            (1061824, ""),          // one-handed weapon
            (1061172, ""),          // skill required: swordsmanship
            (1060639, "31\t50"),    // durability
        }, ItemLines(sword, world).ToArray());
    }

    /// <summary>CClientMsg_AOSTooltip.cpp:484-518: 1060658 "Armor: N", not the
    /// physical-resist line.</summary>
    [Fact]
    public void ArmorTooltip_UsesTheArmorLine()
    {
        DefinitionLoader.SetItemDef(0x1415, new ItemDef(ResourceId.Invalid)
        {
            DispIndex = 0x1415, Type = ItemType.Armor, Name = "probe plate",
            DefenseMin = 30, DefenseMax = 40, ReqStr = 60,
        });
        var world = World();
        var plate = world.CreateItem();
        plate.BaseId = 0x1415;
        plate.ItemType = ItemType.Armor;
        plate.SetAttr(ObjAttributes.Move_Never);
        plate.SetTag("LOWERREQ", "10");
        plate.HitsMax = 40;
        plate.HitsCur = 40;
        world.PlaceItem(plate, new Point3D(100, 100, 0, 0));

        Assert.Equal(new (uint, string)[]
        {
            (1060658, $"{ServerMessages.Get(Msg.TooltipTagArmor)}\t30"),
            (1061170, "50"),
            (1060639, "40\t40"),
        }, ItemLines(plate, world).ToArray());
    }

    /// <summary>CClientMsg_AOSTooltip.cpp:396-473: weight, maker, exceptional, uses,
    /// then "Locked" alone for a locked container and a contents line otherwise.</summary>
    [Fact]
    public void CommonItemLines_FollowTheReferenceOrder()
    {
        var world = World();
        var smith = world.CreateCharacter();
        smith.Name = "Smith";
        world.PlaceCharacter(smith, new Point3D(100, 100, 0, 0));

        var locked = world.CreateItem();
        locked.ItemType = ItemType.ContainerLocked;
        locked.SetAttr(ObjAttributes.Move_Never | ObjAttributes.Exceptional);
        locked.Crafter = smith.Uid;
        locked.UsesRemaining = 7;
        world.PlaceItem(locked, new Point3D(100, 100, 0, 0));
        Assert.Equal(new (uint, string)[]
        {
            (1050043, "Smith"),     // crafted by
            (1060636, ""),          // exceptional
            (1060584, "7"),         // uses remaining
            (3005142, ""),          // Locked
        }, ItemLines(locked, world).ToArray());

        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.SetAttr(ObjAttributes.Move_Never);
        world.PlaceItem(chest, new Point3D(100, 100, 0, 0));
        Assert.Equal((1073841u, $"0\t{Item.MaxContainerItems}\t0"), Assert.Single(ItemLines(chest, world)));

        chest.ModMaxWeight = 400;
        Assert.Equal((1072241u, $"0\t{Item.MaxContainerItems}\t0\t400"), Assert.Single(ItemLines(chest, world)));
        Assert.Equal((1050044u, "0\t0"), Assert.Single(ItemLines(chest, world, featureMl: false)));

        // A movable item leads with its weight under FEATURE_ML_UPDATE.
        chest.ClearAttr(ObjAttributes.Move_Never);
        chest.SetAttr(ObjAttributes.Move_Always);
        Assert.Contains(ItemLines(chest, world)[0].ClilocId, new uint[] { 1072788, 1072789 });
    }

    /// <summary>CClientMsg_AOSTooltip.cpp:580-639: wand charges, rune destination,
    /// spell count.</summary>
    [Fact]
    public void WandRuneAndSpellbookLines()
    {
        var world = World();
        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        wand.SetAttr(ObjAttributes.Move_Never);
        wand.More2 = 12;
        world.PlaceItem(wand, new Point3D(100, 100, 0, 0));
        Assert.Equal((1054132u, "12"), Assert.Single(ItemLines(wand, world)));

        var rune = world.CreateItem();
        rune.ItemType = ItemType.Rune;
        rune.SetAttr(ObjAttributes.Move_Never);
        world.PlaceItem(rune, new Point3D(100, 100, 0, 0));
        Assert.Empty(ItemLines(rune, world));                      // blank rune
        rune.SetRuneMark(new Point3D(1500, 1600, 0, 0));
        Assert.Equal(
            (1060805u, $"{ServerMessages.Get(Msg.RuneTo)} {ServerMessages.Get(Msg.RuneLocationUnk)}"),
            Assert.Single(ItemLines(rune, world)));
        rune.SetRuneMark(new Point3D(1500, 1600, 0, 1));
        Assert.Equal(1060806u, Assert.Single(ItemLines(rune, world)).ClilocId);

        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.SetAttr(ObjAttributes.Move_Never);
        world.PlaceItem(book, new Point3D(100, 100, 0, 0));
        Assert.Empty(ItemLines(book, world));
        book.More1 = 0b1011;
        Assert.Equal((1042886u, "3"), Assert.Single(ItemLines(book, world)));
    }

    /// <summary>CClientMsg_AOSTooltip.cpp:253-352.</summary>
    [Fact]
    public void CharacterTooltip_HasPrefixNameSuffixAndStatusLines()
    {
        var world = World();
        var hero = world.CreateCharacter();
        hero.IsPlayer = true;
        hero.Name = "Hero";
        world.PlaceCharacter(hero, new Point3D(100, 100, 0, 0));

        var props = new List<(uint ClilocId, string Args)>();
        DefaultTooltipData.AppendCharName(hero, PrivLevel.Player, props);
        DefaultTooltipData.AppendCharData(hero, featureMl: true, props);
        Assert.Equal((1050045u, " \tHero\t "), Assert.Single(props));

        // Fame above 9900 is "Lord " (Noto_GetFameTitle); a staff member shows its line.
        hero.Fame = 10000;
        hero.SetTag("NAME.SUFFIX", " the Bold");
        hero.PrivLevel = PrivLevel.GM;
        hero.PrivShow = true;
        props.Clear();
        DefaultTooltipData.AppendCharName(hero, PrivLevel.Player, props);
        DefaultTooltipData.AppendCharData(hero, featureMl: true, props);
        Assert.Equal((1050045u, $"{PaperdollText.GetFameTitle(hero)}\tHero\t the Bold"), props[0]);
        Assert.NotEqual(" ", PaperdollText.GetFameTitle(hero));
        Assert.Contains((1018085u, ""), props);

        var pet = world.CreateCharacter();
        pet.Name = "Rex";
        world.PlaceCharacter(pet, new Point3D(100, 100, 0, 0));
        pet.SetStatFlag(StatFlag.Pet);
        props.Clear();
        DefaultTooltipData.AppendCharData(pet, featureMl: true, props);
        Assert.Equal((502006u, ""), Assert.Single(props));          // (tame)

        pet.SetStatFlag(StatFlag.Conjured);
        props.Clear();
        DefaultTooltipData.AppendCharData(pet, featureMl: true, props);
        Assert.Equal((1049646u, ""), Assert.Single(props));         // (summoned)
    }

    /// <summary>The built list a client is actually sent: a character's first line is
    /// the three-part name.</summary>
    [Fact]
    public void CharacterTooltip_IsWhatTheClientIsSent()
    {
        var world = World();
        var (client, _) = NewClient(world, 5306);
        var npc = world.CreateCharacter();
        npc.Name = "Guard";
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        client.SkillUse.SendAosTooltip(npc, requested: true);

        var props = npc.TooltipCache?.Properties ?? [];
        Assert.Equal((1050045u, " \tGuard\t "), (props[0].ClilocId, props[0].Args));
    }

    // ------------------------------------------------------------------ party

    private static byte[] PartyData(byte cmd, uint serial, string? text = null)
    {
        var data = new List<byte> { cmd };
        data.AddRange(U32(serial));
        if (text != null)
        {
            data.AddRange(Encoding.BigEndianUnicode.GetBytes(text));
            data.AddRange(new byte[] { 0, 0 });
        }
        return data.ToArray();
    }

    /// <summary>CClientMsg.cpp:2248-2252 / :2264-2268 with CParty.cpp:96-112: mana and
    /// stamina go to the other members as a percentage.</summary>
    [Fact]
    public void PartyMembersAreSentManaAndStamina()
    {
        var world = World();
        var parties = new PartyManager();
        var (client, actor) = NewClient(world, 5307);
        var (_, mate) = NewClient(world, 5308, x: 101);
        Assert.True(parties.AcceptInvite(actor.Uid, mate.Uid));
        client.SetEngines(partyManager: parties);

        var forwarded = new List<(Serial To, byte[] Bytes)>();
        client.SendToChar = (to, packet) => forwarded.Add((to, packet.Build().Span.ToArray()));

        actor.MaxMana = 50;
        actor.Mana = 25;
        actor.MaxStam = 80;
        actor.Stam = 20;
        client.Combat.TickStatUpdate();

        var mana = Assert.Single(forwarded, f => f.Bytes[0] == 0xA2);
        Assert.Equal(mate.Uid, mana.To);
        Assert.Equal(100, (mana.Bytes[5] << 8) | mana.Bytes[6]);
        Assert.Equal(actor.Mana * 100 / Math.Max((int)actor.MaxMana, 1), (mana.Bytes[7] << 8) | mana.Bytes[8]);
        var stam = Assert.Single(forwarded, f => f.Bytes[0] == 0xA3);
        Assert.Equal(mate.Uid, stam.To);
        Assert.Equal(100, (stam.Bytes[5] << 8) | stam.Bytes[6]);
    }

    /// <summary>CParty.cpp:242-277: a tell reaches its target only; one to a
    /// non-member is dropped without a word.</summary>
    [Fact]
    public void PartyTell_GoesToTheTargetOnly()
    {
        var world = World();
        var parties = new PartyManager();
        var (client, actor) = NewClient(world, 5309);
        var (_, mate) = NewClient(world, 5310, x: 101);
        var (_, stranger) = NewClient(world, 5311, x: 102);
        Assert.True(parties.AcceptInvite(actor.Uid, mate.Uid));
        client.SetEngines(partyManager: parties);
        var forwarded = new List<Serial>();
        client.SendToChar = (to, _) => forwarded.Add(to);

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleExtendedCommand(0x0006, PartyData(3, mate.Uid.Value, "psst"));
        Assert.Equal(new[] { mate.Uid }, forwarded);
        Assert.Empty(TestHarness.GetQueuedPackets(client.NetState));   // no echo to the sender

        forwarded.Clear();
        client.HandleExtendedCommand(0x0006, PartyData(3, stranger.Uid.Value, "psst"));
        Assert.Empty(forwarded);
        Assert.Empty(TestHarness.GetQueuedPackets(client.NetState));   // and no refusal either
    }

    /// <summary>CParty.cpp:74-83 and receive.cpp:2703: the loot choice sets
    /// PARTY_CANLOOTME and is answered inside a party only.</summary>
    [Fact]
    public void PartyLootToggle_SetsTheKeyInsideAPartyOnly()
    {
        var world = World();
        var parties = new PartyManager();
        var (client, actor) = NewClient(world, 5312);
        client.SetEngines(partyManager: parties);
        client.SendToChar = (_, _) => { };

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleExtendedCommand(0x0006, [6, 1]);
        Assert.False(actor.TryGetTag("PARTY_CANLOOTME", out _));
        Assert.Empty(TestHarness.GetQueuedPackets(client.NetState));

        var (_, mate) = NewClient(world, 5313, x: 101);
        Assert.True(parties.AcceptInvite(actor.Uid, mate.Uid));
        client.HandleExtendedCommand(0x0006, [6, 1]);
        Assert.True(actor.TryGetTag("PARTY_CANLOOTME", out string? flag));
        Assert.Equal("1", flag);
        Assert.True(parties.FindParty(actor.Uid)!.GetLootFlag(actor.Uid));
        Assert.NotEmpty(TestHarness.GetQueuedPackets(client.NetState));

        client.HandleExtendedCommand(0x0006, [6, 0]);
        Assert.False(actor.TryGetTag("PARTY_CANLOOTME", out _));
    }

    // ----------------------------------------------------------- context menu

    private static List<(ushort Tag, uint Cliloc)> Menu(GameClient client, uint uid)
    {
        TestHarness.ClearQueuedPackets(client.NetState);
        client.WorldFeatures.SendContextMenu(uid);
        var result = new List<(ushort, uint)>();
        foreach (var span in Sent(client, 0xBF))
        {
            if (span.Length < 13 || span[3] != 0x00 || span[4] != 0x14) continue;
            bool newFormat = span[6] == 2;
            int count = span[11];
            for (int i = 0; i < count; i++)
            {
                if (newFormat)
                {
                    int at = 12 + i * 8;
                    uint cliloc = (uint)((span[at] << 24) | (span[at + 1] << 16) | (span[at + 2] << 8) | span[at + 3]);
                    result.Add(((ushort)((span[at + 4] << 8) | span[at + 5]), cliloc));
                }
                else
                {
                    int at = 12 + i * 6;
                    result.Add(((ushort)((span[at] << 8) | span[at + 1]),
                        3000000u + (uint)((span[at + 2] << 8) | span[at + 3])));
                }
            }
        }
        return result;
    }

    /// <summary>CClientEvent.cpp:2669-2670 (POPUP_PETRENAME, cliloc 1115557) and
    /// :2575 (nothing for a dead requester).</summary>
    [Fact]
    public void OwnedPetMenu_OffersRename_AndTheDeadGetNoMenu()
    {
        var world = World();
        var (client, actor) = NewClient(world, 5314);
        var pet = world.CreateCharacter();
        pet.Name = "Rex";
        pet.BodyId = 0x00D9;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(actor, actor, summoned: false, enforceFollowerCap: false));

        var menu = Menu(client, pet.Uid.Value);
        Assert.Contains(((ushort)919, 1115557u), menu);
        int stay = menu.FindIndex(e => e.Tag == 137);
        Assert.Equal(919, menu[stay + 1].Tag);

        actor.Kill();
        Assert.True(actor.IsDead);
        Assert.Empty(Menu(client, pet.Uid.Value));
    }

    // ------------------------------------------------------------ info skills

    /// <summary>CItem.cpp:5774-5790, :4917-4937 and :4897-4915.</summary>
    [Fact]
    public void ArmsLore_UsesTheStatusMessagesTopDamageAndBaseDefense()
    {
        var world = World();
        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        sword.SetAttackRating(5, 20);
        sword.HitsMax = 30;
        world.PlaceItem(sword, new Point3D(100, 100, 0, 0));
        Assert.Equal(20, sword.GetWeaponAttack());
        sword.ModAr = 2;
        Assert.Equal(22, sword.GetWeaponAttack());

        var plate = world.CreateItem();
        plate.ItemType = ItemType.Armor;
        Assert.True(plate.TrySetProperty("ARMOR", "30,40"));
        world.PlaceItem(plate, new Point3D(100, 100, 0, 0));
        Assert.Equal(30, plate.GetArmorDefense());

        (int Cur, string Key)[] bands =
        [
            (31, Msg.ItemstatusPerfect), (30, Msg.ItemstatusFull), (16, "itemstatus_scratched"),
            (11, "itemstatus_wellworn"), (4, "itemstatus_badly"), (3, "itemstatus_fall_apart"),
        ];
        foreach (var (cur, key) in bands)
        {
            sword.HitsCur = cur;
            Assert.Equal(ServerMessages.Get(key), sword.GetArmorRepairDesc());
        }
    }

    /// <summary>CItem.cpp:1774-1902: the article is always there; "identified" adds
    /// the blessed / magic prefix and the spell with its charges.</summary>
    [Fact]
    public void GetNameFull_ArticleAlways_DetailsWhenIdentified()
    {
        Character.ResolveSpellDef = s => s == SpellType.Fireball ? new SpellDef { Name = "Fireball" } : null;
        DefinitionLoader.SetItemDef(0x13B9, new ItemDef(ResourceId.Invalid)
        {
            DispIndex = 0x13B9, Type = ItemType.WeaponSword, Name = "iron sword",
        });
        DefinitionLoader.SetItemDef(0x0F0E, new ItemDef(ResourceId.Invalid)
        {
            DispIndex = 0x0F0E, Type = ItemType.Normal, Name = "empty bottle",
        });
        var world = World();

        var sword = world.CreateItem();
        sword.BaseId = 0x13B9;
        sword.ItemType = ItemType.WeaponSword;
        world.PlaceItem(sword, new Point3D(100, 100, 0, 0));
        Assert.Equal("an iron sword", sword.GetNameFull(false));
        Assert.Equal("an iron sword", sword.GetNameFull(true));

        sword.SetAttr(ObjAttributes.Blessed | ObjAttributes.Magic);
        sword.MoreP = new Point3D((short)SpellType.Fireball, 50, 0, 0);
        sword.More2 = 9;
        Assert.Equal("an iron sword", sword.GetNameFull(false));
        Assert.Equal(
            $"a {ServerMessages.Get(Msg.ItemtitleBlessed)}iron sword of Fireball (9 {ServerMessages.Get(Msg.ItemtitleCharges)})",
            sword.GetNameFull(true));

        var bottle = world.CreateItem();
        bottle.BaseId = 0x0F0E;
        world.PlaceItem(bottle, new Point3D(100, 100, 0, 0));
        bottle.SetAttr(ObjAttributes.Magic);
        Assert.Equal($"a {ServerMessages.Get(Msg.ItemtitleMagic)}empty bottle", bottle.GetNameFull(true));
        bottle.Amount = 5;
        Assert.StartsWith("5 ", bottle.GetNameFull(false));
    }

    /// <summary>CCharBase.cpp:54-69 and CCharStatus.cpp:611-657.</summary>
    [Fact]
    public void AnimalLore_TradeNameAndPronounFollowTheReference()
    {
        var def = new CharDef(ResourceId.Invalid) { Name = "#NAMES_HUMANMALE the Carpenter" };
        DefinitionLoader.SetCharDef(0x0190, def);
        var world = World();
        var man = world.CreateCharacter();
        man.BodyId = 0x0190;
        man.CharDefIndex = 0x0190;
        man.NpcBrain = NpcBrainType.Monster;        // the brain no longer decides
        world.PlaceCharacter(man, new Point3D(100, 100, 0, 0));

        Assert.Equal("Carpenter", man.GetTradeName());
        Assert.Equal(ServerMessages.Get(Msg.PronounHe), man.GetPronoun());
        Assert.Equal(ServerMessages.Get(Msg.PossesspronounHis), man.GetPossessPronoun());

        man.BodyId = 0x025E;                        // elf woman
        Assert.Equal(ServerMessages.Get(Msg.PronounShe), man.GetPronoun());
        Assert.Equal(ServerMessages.Get(Msg.PossesspronounHer), man.GetPossessPronoun());

        man.BodyId = 0x00C8;                        // a horse
        man.NpcBrain = NpcBrainType.Human;
        Assert.Equal(ServerMessages.Get(Msg.PronounIt), man.GetPronoun());
        Assert.Equal(ServerMessages.Get(Msg.PossesspronounIts), man.GetPossessPronoun());
    }

    // ------------------------------------------------- bulletin board / names

    /// <summary>receive.cpp:1232-1233: a message rolls off once the board holds more
    /// than 32, not at 32; :1279-1283: someone else's message stays.</summary>
    [Fact]
    public void BulletinBoard_RollsOffPast32_AndKeepsOthersMessages()
    {
        var world = World();
        var (client, actor) = NewClient(world, 5315);
        var board = world.CreateItem();
        board.ItemType = ItemType.BBoard;
        world.PlaceItem(board, actor.Position);

        for (int i = 0; i < 33; i++)
            client.HandleBulletinBoardPost(board.Uid.Value, 0, $"post {i}", ["body"]);
        Assert.Equal(33, board.ContentCount);
        Assert.Equal("post 0", board.Contents[0].Name);

        client.HandleBulletinBoardPost(board.Uid.Value, 0, "post 33", ["body"]);
        Assert.Equal(33, board.ContentCount);
        Assert.DoesNotContain(board.Contents, m => m.Name == "post 0");

        var (otherClient, _) = NewClient(world, 5316);
        var theirs = board.Contents[0];
        otherClient.HandleBulletinBoardDelete(board.Uid.Value, theirs.Uid.Value);
        Assert.False(theirs.IsDeleted);
        Assert.Contains(theirs, board.Contents);
    }

    /// <summary>receive.cpp:1784-1793: every serial in the packet is answered, and
    /// only for what the requester can see.</summary>
    [Fact]
    public void AllNames_AnswersEverySerialItCanSee()
    {
        var state = TestHarness.CreateActiveNetState(Lf, 3);
        var asked = new List<uint>();
        state.AllNamesRequestHandler = (_, serial) => asked.Add(serial);
        var wire = new List<byte>();
        wire.AddRange(U32(0x11));
        wire.AddRange(U32(0x22));
        wire.AddRange(U32(0x33));
        new PacketAllNamesReq().OnReceive(new PacketBuffer(wire.ToArray()), state);
        Assert.Equal(new uint[] { 0x11, 0x22, 0x33 }, asked);

        var world = World();
        var (client, actor) = NewClient(world, 5317);
        var near = world.CreateCharacter();
        near.Name = "Near";
        world.PlaceCharacter(near, new Point3D(101, 100, 0, 0));
        var hidden = world.CreateCharacter();
        hidden.Name = "Hidden";
        world.PlaceCharacter(hidden, new Point3D(102, 100, 0, 0));
        hidden.SetStatFlag(StatFlag.Invisible);
        Assert.True(actor.CanSee(near));

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleAllNamesRequest(near.Uid.Value);
        Assert.Single(Sent(client, 0x98));

        Assert.False(actor.CanSee(hidden));
        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleAllNamesRequest(hidden.Uid.Value);
        Assert.Empty(Sent(client, 0x98));
    }
}
