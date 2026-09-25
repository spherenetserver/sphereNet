using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using SphereNet.Network.Packets;
using SphereNet.Scripting.Expressions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Semantic depth of the generic object / sector / party / file / script-object
/// keyword surface against Source-X: the names were all recognised, these pin what
/// they DO - the value format, the argument forms and the side effects upstream has
/// (CObjBase.cpp, CSector.cpp, CParty.cpp, CSFileObj.cpp, CScriptObj.cpp).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ObjectScriptSurfaceParityTests : IDisposable
{
    private readonly Action<Character, PacketWriter>? _savedOwnerSend = Character.SendPacketToOwner;
    private readonly Func<PartyManager?>? _savedManager = Character.ResolvePartyManager;
    private readonly Func<Serial, PartyDef?>? _savedFinder = Character.ResolvePartyFinder;

    public void Dispose()
    {
        Character.SendPacketToOwner = _savedOwnerSend;
        Character.ResolvePartyManager = _savedManager;
        Character.ResolvePartyFinder = _savedFinder;
    }

    private sealed class ProbeConsole(Character? ch = null, PrivLevel priv = PrivLevel.Owner) : ITextConsole
    {
        public List<string> Messages { get; } = [];
        public PrivLevel GetPrivLevel() => priv;
        public void SysMessage(string text) => Messages.Add(text);
        public string GetName() => ch?.Name ?? "probe";
        public IScriptObj? GetSourceChar() => ch;
    }

    private static GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Npc(GameWorld world, short x = 100, short y = 100)
    {
        var ch = world.CreateCharacter();
        ch.Name = "npc";
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    private static Item GroundItem(GameWorld world, short x = 100, short y = 100)
    {
        var it = world.CreateItem();
        it.Name = "thing";
        world.PlaceItem(it, new Point3D(x, y, 0, 0));
        return it;
    }

    private static string Read(IScriptObj o, string key)
    {
        Assert.True(o.TryGetProperty(key, out string v), $"{key} did not resolve");
        return v;
    }

    // ---------------------------------------------------------------- sector

    /// <summary>OC_COMPLEXITY hands the key to the top-level object's sector
    /// (CObjBase.cpp:1156). The human speech asks &lt;COMPLEXITY.HIGH&gt; on the NPC.</summary>
    [Fact]
    public void ComplexityOnAnObjectIsItsSectorAnswering()
    {
        var world = World();
        var npc = Npc(world);
        Assert.Equal("1", Read(npc, "COMPLEXITY"));
        Assert.Equal("1", Read(npc, "COMPLEXITY.HIGH"));
        Assert.Equal("0", Read(npc, "COMPLEXITY.MEDIUM"));
        Assert.Equal("0", Read(npc, "COMPLEXITY.LOW"));
        Assert.Equal("0", Read(npc, "COMPLEXITY.SOMETHING"));   // any other title reads 0
    }

    /// <summary>GetCharComplexity counts the ACTIVE characters; a logged-out player
    /// is in m_Chars_Disconnect upstream.</summary>
    [Fact]
    public void ComplexityDoesNotCountLoggedOutPlayers()
    {
        var world = World();
        var npc = Npc(world);
        var offline = Npc(world, 101);
        offline.IsPlayer = true;
        offline.IsOnline = false;
        Assert.Equal("1", Read(npc, "COMPLEXITY"));
    }

    /// <summary>CServerTime::GetTimeMinDesc (CServerTime.cpp:17).</summary>
    [Fact]
    public void LocalTimeIsTheSpokenPhrase()
    {
        Assert.Equal("a quarter past three o'clock in the afternoon", Sector.GetTimeMinDesc(15 * 60 + 20));
        Assert.Equal("half past eight o'clock in the morning", Sector.GetTimeMinDesc(8 * 60 + 40));
        Assert.Equal("a quarter till midnight ", Sector.GetTimeMinDesc(23 * 60 + 50));
        Assert.Equal(" noon ", Sector.GetTimeMinDesc(12 * 60 + 5));
        Assert.Equal(" ten o'clock at night", Sector.GetTimeMinDesc(22 * 60));
    }

    [Fact]
    public void SectorFlagsReadHex()
    {
        var world = World();
        var npc = Npc(world);
        var sector = world.GetSector(npc.Position)!;
        sector.Flags = SectorFlag.NoSleep | SectorFlag.InstaSleep;
        Assert.Equal("03", Read(sector, "FLAGS"));
    }

    /// <summary>ALLITEMS / ALLCHARS / ALLCHARSIDLE run the argument as a verb on each
    /// object (CSector.cpp:512-612), reached through the SECTOR reference head.</summary>
    [Fact]
    public void SectorAllVerbsRunTheLineOnEachObject()
    {
        var world = World();
        var npc = Npc(world);
        var offline = Npc(world, 102);
        offline.IsPlayer = true;
        offline.IsOnline = false;
        var a = GroundItem(world, 103);
        var b = GroundItem(world, 104);
        var console = new ProbeConsole();

        Assert.True(npc.TryExecuteCommand("SECTOR.ALLITEMS", "TAG.MARK 7", console));
        Assert.Equal("7", Read(a, "TAG.MARK"));
        Assert.Equal("7", Read(b, "TAG.MARK"));

        Assert.True(npc.TryExecuteCommand("SECTOR.ALLCHARS", "TAG.ACTIVE 1", console));
        Assert.Equal("1", Read(npc, "TAG.ACTIVE"));
        Assert.False(offline.TryGetTag("ACTIVE", out _));

        Assert.True(npc.TryExecuteCommand("SECTOR.ALLCHARSIDLE", "TAG.IDLE 1", console));
        Assert.Equal("1", Read(offline, "TAG.IDLE"));
        Assert.False(npc.TryGetTag("IDLE", out _));
    }

    // ------------------------------------------------------------ properties

    [Fact]
    public void ColorReadsSphereHex()
    {
        var world = World();
        var it = GroundItem(world);
        it.Hue = new Color(0x0481);
        Assert.Equal("0481", Read(it, "COLOR"));
    }

    [Fact]
    public void GenericObjectKeysAnswerOnEveryObject()
    {
        var world = World();
        var it = GroundItem(world);
        var npc = Npc(world);

        Assert.Equal(Read(it, "UID"), Read(it, "SERIAL"));
        Assert.Equal("0", Read(it, "ISCONT"));
        Assert.Equal("1", Read(npc, "ISCONT"));
        it.ItemType = ItemType.Container;
        Assert.Equal("1", Read(it, "ISCONT"));

        Assert.Equal("0", Read(it, "ISSLEEPING"));
        it.GoSleep();
        Assert.Equal("1", Read(it, "ISSLEEPING"));

        Assert.Equal("0", Read(npc, "CTAGCOUNT"));    // no client
        Assert.Equal("0", Read(it, "CTAGCOUNT"));
    }

    [Fact]
    public void EventsReadsTheListAndTEventIsNotTheInstanceList()
    {
        var world = World();
        var it = GroundItem(world);
        Assert.Equal("", Read(it, "EVENTS"));
        Assert.True(it.TrySetProperty("EVENTS", "+e_probe_events"));
        Assert.NotEqual("", Read(it, "EVENTS"));
        Assert.Equal("1", Read(it, "ISEVENT.e_probe_events"));
        // ISTEVENT asks the DEFINITION's TEVENTS (CObjBase.cpp:1353).
        Assert.Equal("0", Read(it, "ISTEVENT.e_probe_events"));
    }

    /// <summary>IsTypeArmor / IsTypeWeapon of the item's TYPE (CObjBase.cpp:1470/1509),
    /// not "has a rating".</summary>
    [Fact]
    public void IsArmorAndIsWeaponFollowTheItemType()
    {
        var world = World();
        var robe = GroundItem(world);
        robe.ItemType = ItemType.Clothing;
        Assert.Equal("1", Read(robe, "ISARMOR"));
        Assert.Equal("0", Read(robe, "ISWEAPON"));
        var sword = GroundItem(world);
        sword.ItemType = ItemType.WeaponSword;
        Assert.Equal("1", Read(sword, "ISWEAPON"));
    }

    [Fact]
    public void NameWriteIsPublished()
    {
        var world = World();
        var it = GroundItem(world);
        it.ConsumeDirty();
        Assert.True(it.TrySetProperty("NAME", "renamed"));
        Assert.True(it.ConsumeDirty().HasFlag(DirtyFlag.Name));
    }

    // ----------------------------------------------------------------- verbs

    /// <summary>Sound() is heard from the TOP-LEVEL object (CClientMsg.cpp:635).</summary>
    [Fact]
    public void SoundFromAContainedItemPlaysAtItsContainer()
    {
        var world = World();
        var pack = GroundItem(world, 150, 160);
        pack.ItemType = ItemType.Container;
        var inner = world.CreateItem();
        Assert.True(pack.AddItem(inner));
        var origins = new List<Point3D>();
        ObjBase.BroadcastNearby = (p, _, _, _) => origins.Add(p);

        Assert.True(inner.TryExecuteCommand("SOUND", "0x51", new ProbeConsole()));
        Assert.Equal((short)150, Assert.Single(origins).X);
        Assert.Equal((short)160, origins[0].Y);

        Assert.False(inner.TryExecuteCommand("SOUND", "", new ProbeConsole()));   // no argument refuses
    }

    /// <summary>A bolt flies from SRC; -1 swaps the ends (CObjBase.cpp:2279).</summary>
    [Fact]
    public void EffectBoltFliesFromSrcAndMinusOneSwaps()
    {
        var world = World();
        var target = Npc(world);
        var caster = Npc(world, 105);
        var packets = new List<PacketWriter>();
        Character.BroadcastNearby = (_, _, p, _) => packets.Add(p);
        ObjBase.BroadcastNearby = (_, _, p, _) => packets.Add(p);
        var src = new ProbeConsole(caster);

        Assert.True(target.TryExecuteCommand("EFFECT", "0,0x36d4", src));
        var bytes = Assert.Single(packets).Build().Span.ToArray();
        Assert.Equal(caster.Uid.Value, ReadUInt32(bytes, 2));
        Assert.Equal(target.Uid.Value, ReadUInt32(bytes, 6));

        packets.Clear();
        Assert.True(target.TryExecuteCommand("EFFECT", "-1,0x36d4", src));
        bytes = Assert.Single(packets).Build().Span.ToArray();
        Assert.Equal(target.Uid.Value, ReadUInt32(bytes, 2));
        Assert.Equal(caster.Uid.Value, ReadUInt32(bytes, 6));
    }

    private static uint ReadUInt32(byte[] b, int at) =>
        (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    /// <summary>With fewer than three arguments the damage source is SRC
    /// (CObjBase.cpp:2237).</summary>
    [Fact]
    public void DamageDefaultsItsSourceToSrc()
    {
        var world = World();
        var victim = Npc(world);
        victim.MaxHits = 100;
        victim.Hits = 100;
        var attacker = Npc(world, 101);
        Character? seen = null;
        SphereNet.Game.Combat.CombatEngine.OnDirectDamage = ctx => { seen = ctx.Source; return ctx.Damage; };

        Assert.True(victim.TryExecuteCommand("DAMAGE", "5,1", new ProbeConsole(attacker)));
        Assert.Same(attacker, seen);
    }

    [Fact]
    public void AnItemSaysItsLineOutLoud()
    {
        var world = World();
        var it = GroundItem(world);
        var packets = new List<PacketWriter>();
        ObjBase.BroadcastNearby = (_, _, p, _) => packets.Add(p);
        var src = new ProbeConsole();

        Assert.True(it.TryExecuteCommand("SAY", "hello there", src));
        var bytes = Assert.Single(packets).Build().Span.ToArray();
        Assert.Equal(0x1C, bytes[0]);
        Assert.Contains("hello there", Encoding.ASCII.GetString(bytes));
        Assert.Empty(src.Messages);     // not whispered to SRC any more
    }

    /// <summary>CObjBase::Emote (CObjBase.cpp:630): "*You see NAME text*" to others,
    /// "*You text*" to the emoter.</summary>
    [Fact]
    public void EmoteIsFramedByTheDefaultMessages()
    {
        var world = World();
        var npc = Npc(world);
        npc.Name = "Bob";
        var others = new List<PacketWriter>();
        var own = new List<PacketWriter>();
        Character.BroadcastNearby = (_, _, p, _) => others.Add(p);
        Character.SendPacketToOwner = (_, p) => own.Add(p);

        Assert.True(npc.TryExecuteCommand("EMOTE", "waves", new ProbeConsole()));
        Assert.Contains("*You see Bob waves*", Encoding.ASCII.GetString(Assert.Single(others).Build().Span));
        Assert.Contains("*You waves*", Encoding.ASCII.GetString(Assert.Single(own).Build().Span));
    }

    /// <summary>GetDeltaStr (CObjBase.cpp:62): numbers or a direction word + count;
    /// only for top-level objects.</summary>
    [Fact]
    public void MoveTakesDirectionWordsAndWorksOnCharacters()
    {
        var world = World();
        var it = GroundItem(world, 200, 200);
        Assert.True(it.TryExecuteCommand("MOVE", "N 2", new ProbeConsole()));
        Assert.Equal((short)198, it.Position.Y);
        Assert.True(it.TryExecuteCommand("MOVE", "e", new ProbeConsole()));
        Assert.Equal((short)201, it.Position.X);

        var npc = Npc(world, 210, 210);
        Assert.True(npc.TryExecuteCommand("MOVE", "1,-1", new ProbeConsole()));
        Assert.Equal(new Point3D(211, 209, 0, 0), npc.Position);
    }

    [Fact]
    public void NudgeDefaultsToOneAndWorksOnItems()
    {
        var world = World();
        var it = GroundItem(world);
        Assert.True(it.TryExecuteCommand("NUDGEUP", "", new ProbeConsole()));
        Assert.Equal((sbyte)1, it.Position.Z);
        Assert.True(it.TryExecuteCommand("NUDGEDOWN", "3", new ProbeConsole()));
        Assert.Equal((sbyte)-2, it.Position.Z);
    }

    /// <summary>A STOP with no pattern matches nothing (CTimedFunctionHandler.cpp:46);
    /// CLEAR and STOP are prefix matches (CObjBase.cpp:2762).</summary>
    [Fact]
    public void TimerFStopWithoutAPatternStopsNothing()
    {
        var world = World();
        var it = GroundItem(world);
        var console = new ProbeConsole();
        Assert.True(it.TryExecuteCommand("TIMERF", "30,f_probe_timer", console));
        Assert.True(it.GetTimerFRemaining("f_probe_timer", Environment.TickCount64) > 0);

        Assert.True(it.TryExecuteCommand("TIMERF", "STOP", console));
        Assert.True(it.GetTimerFRemaining("f_probe_timer", Environment.TickCount64) > 0);

        Assert.True(it.TryExecuteCommand("TIMERF", "STOP,f_probe_timer", console));
        Assert.Equal(0, it.GetTimerFRemaining("f_probe_timer", Environment.TickCount64));
    }

    /// <summary>CallPersonalTrigger's argument-type forms (CObjBase.cpp:3719).</summary>
    [Fact]
    public void TriggerVerbFillsTheArgumentsItsTypeNames()
    {
        var world = World();
        var it = GroundItem(world);
        var calls = new List<(string Name, TriggerArgs Args)>();
        ObjBase.OnScriptTrigger = (_, name, _, a) => calls.Add((name, a));
        var console = new ProbeConsole();

        Assert.True(it.TryExecuteCommand("TRIGGER", "@probe,1,5,6,7", console));
        Assert.Equal("probe", calls[0].Name);
        Assert.Equal((5L, 6L, 7L), (calls[0].Args.N1, calls[0].Args.N2, calls[0].Args.N3));

        Assert.True(it.TryExecuteCommand("TRIGGER", "probe,2,some text", console));
        Assert.Equal("some text", calls[1].Args.S1);

        Assert.True(it.TryExecuteCommand("TRIGGER", "probe", console));
        Assert.Equal(0L, calls[2].Args.N1);
    }

    [Fact]
    public void UidVerbAndUnknownClickRefuse()
    {
        var world = World();
        var npc = Npc(world);
        var console = new ProbeConsole();
        Assert.False(npc.TryExecuteCommand("UID", "05", console));
        Assert.False(npc.TryExecuteCommand("CLICK", "0FFFFFF0", console));
    }

    [Fact]
    public void TagListUsesTheDumpFormat()
    {
        var world = World();
        var it = GroundItem(world);
        it.SetTag("A", "1");
        var console = new ProbeConsole();
        Assert.True(it.TryExecuteCommand("TAGLIST", "", console));
        Assert.Contains("TAG.A=1", console.Messages);
    }

    [Fact]
    public void TagAtBareIsKeyEqualsValue()
    {
        var world = World();
        var it = GroundItem(world);
        it.SetTag("ONLY", "5");
        int idx = it.Tags.GetAll().Select(p => p.Key).ToList().FindIndex(k => k.Equals("ONLY", StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"{it.Tags.GetAll().ElementAt(idx).Key}=5", Read(it, $"TAGAT.{idx}"));
    }

    [Fact]
    public void DistanceReadsADecimalUidToo()
    {
        var world = World();
        var a = Npc(world, 100, 100);
        var b = Npc(world, 107, 100);
        Assert.Equal("7", Read(a, $"DISTANCE {b.Uid.Value}"));
        Assert.Equal("7", Read(a, $"DISTANCE 0{b.Uid.Value:X}"));
    }

    [Fact]
    public void IsNearTypeTopReadsTheTypeAfterTheSuffix()
    {
        var world = World();
        var npc = Npc(world);
        var forge = GroundItem(world, 101, 100);
        forge.ItemType = ItemType.Forge;
        Assert.Equal("1", Read(npc, "ISNEARTYPE t_forge 2"));
        Assert.Equal("1", Read(npc, "ISNEARTYPETOP t_forge 2"));
        Assert.Equal("0", Read(npc, "ISNEARTYPE t_anvil 2"));
    }

    [Fact]
    public void ItemsResendTheirTooltip()
    {
        var world = World();
        var it = GroundItem(world);
        ObjBase? resent = null;
        ObjBase.ResendTooltipForObject = o => resent = o;
        Assert.True(it.TryExecuteCommand("RESENDTOOLTIP", "", new ProbeConsole()));
        Assert.Same(it, resent);
    }

    /// <summary>A point without z/map is z 0, map 0 (CPointBase.cpp:977).</summary>
    [Fact]
    public void MoveToWithoutZTakesZeroNotTheCurrentZ()
    {
        var world = World();
        var it = world.CreateItem();
        world.PlaceItem(it, new Point3D(100, 100, 20, 0));
        Assert.True(it.TryExecuteCommand("MOVETO", "110,120", new ProbeConsole()));
        Assert.Equal(new Point3D(110, 120, 0, 0), it.Position);
    }

    /// <summary>writeBasicEffectLocation (send.cpp:2095): a stationary effect plays at
    /// the target point on both ends, no object named.</summary>
    [Fact]
    public void EffectLocationPlaysAtThePoint()
    {
        var world = World();
        var it = GroundItem(world, 100, 100);
        var packets = new List<PacketWriter>();
        ObjBase.BroadcastNearby = (_, _, p, _) => packets.Add(p);
        Assert.True(it.TryExecuteCommand("EFFECTLOCATION", "300,310,5,2,0x3709", new ProbeConsole()));
        var b = Assert.Single(packets).Build().Span.ToArray();
        Assert.Equal(0u, ReadUInt32(b, 2));
        Assert.Equal(0u, ReadUInt32(b, 6));
        Assert.Equal(300, (b[12] << 8) | b[13]);     // source x = the point
        Assert.Equal(300, (b[17] << 8) | b[18]);     // target x
    }

    /// <summary>SYSMESSAGELOC: every comma separates an argument, joined with TAB; a
    /// hue that is not a positive number is HUE_TEXT_DEF (CClient.cpp:1643).</summary>
    [Fact]
    public void SysMessageLocJoinsItsArgumentsWithTabs()
    {
        var world = World();
        var ch = Npc(world);
        var sent = new List<PacketWriter>();
        Character.SendPacketToOwner = (_, p) => sent.Add(p);
        Assert.True(ch.TryExecuteCommand("SYSMESSAGELOC", "-1,1156236,90,Magery", new ProbeConsole(ch)));
        var bytes = Assert.Single(sent).Build().Span.ToArray();
        Assert.Equal(0x03B2, (bytes[10] << 8) | bytes[11]);         // hue
        string args = Encoding.Unicode.GetString(bytes, 48, bytes.Length - 48);
        Assert.Contains("90\tMagery", args);

        sent.Clear();
        Assert.True(ch.TryExecuteCommand("SMSGL", "0481,500000", new ProbeConsole(ch)));
        bytes = Assert.Single(sent).Build().Span.ToArray();
        Assert.Equal(0x0481, (bytes[10] << 8) | bytes[11]);
    }

    // --------------------------------------------------------- client / char

    /// <summary>GM toggles GM mode, never the privilege level, and only for a GM
    /// (CClient.cpp:836).</summary>
    [Fact]
    public void GmWriteNeverChangesThePrivilegeLevel()
    {
        var world = World();
        var admin = Npc(world);
        admin.PrivLevel = PrivLevel.Admin;
        Assert.Equal("1", Read(admin, "GM"));
        Assert.True(admin.TrySetProperty("GM", "0"));
        Assert.Equal(PrivLevel.Admin, admin.PrivLevel);
        Assert.Equal("0", Read(admin, "GM"));

        var counsel = Npc(world, 101);
        counsel.PrivLevel = PrivLevel.Counsel;
        Assert.True(counsel.TrySetProperty("GM", "1"));
        Assert.Equal(PrivLevel.Counsel, counsel.PrivLevel);
        Assert.Equal("0", Read(counsel, "GM"));
    }

    /// <summary>TogPrivFlags: no argument flips (CAccount.cpp:745).</summary>
    [Fact]
    public void BarePrivilegeToggleFlips()
    {
        var world = World();
        var gm = Npc(world);
        gm.PrivLevel = PrivLevel.GM;
        var console = new ProbeConsole(gm);
        string before = Read(gm, "DEBUG");
        Assert.True(gm.TryExecuteCommand("DEBUG", "", console));
        Assert.NotEqual(before, Read(gm, "DEBUG"));
        Assert.True(gm.TrySetProperty("DETAIL", "00"));
        Assert.Equal("0", Read(gm, "DETAIL"));
    }

    /// <summary>SYSMESSAGE goes to the TARGET character's client and is eaten when it
    /// has none (CChar.cpp:4392/4936).</summary>
    [Fact]
    public void SysMessageGoesToTheTargetsOwnClient()
    {
        var world = World();
        var player = Npc(world);
        var npc = Npc(world, 101);
        var playerConsole = new ProbeConsole(player);
        var srcConsole = new ProbeConsole(npc);
        ObjBase.ResolveClientConsole = c => ReferenceEquals(c, player) ? playerConsole : null;

        Assert.True(npc.TryExecuteCommand("SYSMESSAGE", "to nobody", srcConsole));
        Assert.Empty(srcConsole.Messages);

        Assert.True(player.TryExecuteCommand("SYSMESSAGE", "to the player", srcConsole));
        Assert.Equal("to the player", Assert.Single(playerConsole.Messages));
        Assert.Empty(srcConsole.Messages);
    }

    // ----------------------------------------------------------------- party

    private static (PartyManager Pm, PartyDef Party, Character Master, Character Member) Party(GameWorld world)
    {
        var pm = new PartyManager();
        Character.ResolvePartyManager = () => pm;
        Character.ResolvePartyFinder = uid => pm.FindParty(uid);
        var master = Npc(world);
        var member = Npc(world, 101);
        var party = pm.CreateParty(master.Uid);
        party.AddMember(member.Uid);
        return (pm, party, master, member);
    }

    [Fact]
    public void PartyTagZeroAndSameParty()
    {
        var world = World();
        var (_, party, master, member) = Party(world);
        Assert.Equal("", Read(master, "PARTY.TAG.NOPE"));
        Assert.Equal("0", Read(master, "PARTY.TAG0.NOPE"));
        Assert.True(master.TrySetProperty("PARTY.TAG0.X", "5"));
        Assert.Equal("5", Read(master, "PARTY.TAG.X"));
        Assert.True(master.TrySetProperty("PARTY.TAG0.X", "0"));
        Assert.False(party.TryGetTag("X", out _));

        // Exp_GetDWVal: decimal works too.
        Assert.Equal("1", Read(master, $"PARTY.ISSAMEPARTYOF {member.Uid.Value}"));
    }

    [Fact]
    public void PartyRemoveMemberRefusesAnOutsiderAndClearTagsMatchesSubstrings()
    {
        var world = World();
        var (pm, party, master, member) = Party(world);
        var outsider = Npc(world, 102);
        var otherParty = pm.CreateParty(outsider.Uid);
        var console = new ProbeConsole(master);

        // ClearKeys(mask) deletes the keys that CONTAIN the mask.
        party.SetTag("OldPlayer", "1");
        party.SetTag("Other", "1");
        Assert.True(master.TryExecuteCommand("PARTY.CLEARTAGS", "player", console));
        Assert.False(party.TryGetTag("OldPlayer", out _));
        Assert.True(party.TryGetTag("Other", out _));

        // Someone else's party member is not pulled out of that party.
        master.TryExecuteCommand("PARTY.REMOVEMEMBER", $"0{outsider.Uid.Value:X}", console);
        Assert.Same(otherParty, pm.FindParty(outsider.Uid));
        Assert.True(otherParty.IsMember(outsider.Uid));
        // "@1" is this party's second member.
        Assert.True(master.TryExecuteCommand("PARTY.REMOVEMEMBER", "@1", console));
        Assert.False(party.IsMember(member.Uid));
    }

    [Fact]
    public void PartySysMessagePicksItsTarget()
    {
        var world = World();
        var (_, _, master, member) = Party(world);
        var sent = new List<Character>();
        Character.SendPacketToOwner = (c, _) => sent.Add(c);
        var console = new ProbeConsole(master);

        Assert.True(master.TryExecuteCommand("PARTY.SYSMESSAGE", "@1 hello", console));
        Assert.Same(member, Assert.Single(sent));

        sent.Clear();
        Assert.True(master.TryExecuteCommand("PARTY.SYSMESSAGE", "everyone hello", console));
        Assert.Equal(2, sent.Count);
    }

    // ------------------------------------------------------------ script obj

    [Theory]
    [InlineData("<ASC hello>", "068 065 06C 06C 06F")]
    [InlineData("<HVAL -1>", "0FFFFFFFF")]
    [InlineData("<HVAL 0>", "00")]
    [InlineData("<FVAL -5>", "-0.5")]
    [InlineData("<FVAL 125>", "12.5")]
    [InlineData("<ISBIT 8,3>", "8")]
    [InlineData("<BETWEEN 0,100,50,200>", "25")]
    [InlineData("<BETWEEN2 0,100,50,200>", "75")]
    [InlineData("<BETWEEN 0,100,500,200>", "100")]
    [InlineData("<BETWEEN 0,100,-1,200>", "0")]
    [InlineData("<ISNUM 0F51>", "1")]
    [InlineData("<ISNUM -12>", "1")]
    [InlineData("<ISNUM +5>", "0")]
    [InlineData("<ISNUM 0x10>", "0")]
    [InlineData("<EXPLODE ;,a;;b>", "a,,b")]
    [InlineData("<EXPLODE ;,a;b;>", "a,b,")]
    [InlineData("<FEVAL 3.9>", "3")]
    [InlineData("<CHR 0141>", "A")]
    public void ScriptObjectFunctionsMatchTheReference(string expr, string want)
        => Assert.Equal(want, new ExpressionParser().EvaluateStr(expr));

    // ------------------------------------------------------------------ file

    private static string TempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sn_fileobj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>MODE.x reads GetArgVal() != 0, and APPEND/CREATE always clear each
    /// other (CSFileObj.cpp:128).</summary>
    [Fact]
    public void FileModeFlagsFollowTheReference()
    {
        Assert.False(ScriptFileHandle.ParseModeValue(""));
        Assert.False(ScriptFileHandle.ParseModeValue("00"));
        Assert.True(ScriptFileHandle.ParseModeValue("1"));

        using var f = new ScriptFileHandle(TempRoot());
        f.ModeCreate = true;
        f.ModeAppend = false;
        Assert.False(f.ModeCreate);
    }

    /// <summary>Read+write opens "a+b": reading starts at the top and every write
    /// lands at the end; write-only truncates (CSFileText.cpp:293).</summary>
    [Fact]
    public void FileOpenModesMatchStdio()
    {
        string root = TempRoot();
        File.WriteAllText(Path.Combine(root, "a.txt"), "first\n");
        using (var f = new ScriptFileHandle(root))
        {
            Assert.True(f.Open("\"a.txt\""));     // quotes are the script's
            Assert.Equal(0, f.Position);
            Assert.Equal("first", f.ReadLine(1));
            Assert.True(f.WriteLine("second"));
            Assert.Equal(f.Length, f.Position);    // flushed: POSITION sees the write
            Assert.Equal(0, f.Seek("-4"));        // a negative offset does not move
            f.Close();
        }
        Assert.StartsWith("first\nsecond", File.ReadAllText(Path.Combine(root, "a.txt")).Replace("\r", ""));

        using (var w = new ScriptFileHandle(root))
        {
            w.ModeRead = false;
            w.ModeAppend = false;
            Assert.True(w.Open("a.txt"));
            Assert.True(w.WriteChr(0xE9));
            w.Close();
        }
        Assert.Equal(new byte[] { 0xE9 }, File.ReadAllBytes(Path.Combine(root, "a.txt")));
        using var probe = new ScriptFileHandle(root);
        Assert.True(probe.FileExistsRelative("\"a.txt\""));
    }
}
