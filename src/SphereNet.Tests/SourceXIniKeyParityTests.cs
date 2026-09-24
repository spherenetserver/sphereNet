using System.Net;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Chat;
using SphereNet.Game.Clients;
using SphereNet.Game.Guild;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Game.World.Sectors;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CServerConfig keys that SphereNet did not read (ELEM table,
/// CServerConfig.cpp:760-1041): each is read under its reference spelling with the
/// reference default and changes what its reference use site changes.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXIniKeyParityTests : IDisposable
{
    private readonly string _iniPath = Path.Combine(Path.GetTempPath(), $"sphnet_srcx_{Guid.NewGuid():N}.ini");

    public void Dispose()
    {
        try { File.Delete(_iniPath); } catch (IOException) { }
    }

    private SphereConfig Load(string body)
    {
        File.WriteAllText(_iniPath, "[SPHERE]\n" + body);
        var ini = new IniParser();
        ini.Load(_iniPath);
        var cfg = new SphereConfig();
        cfg.LoadFromIni(ini);
        return cfg;
    }

    private static GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Character.ResolveAccountForChar = null;
        return world;
    }

    // ---------------------------------------------------------------- config

    [Fact]
    public void Defaults_AreTheReferenceOnes()
    {
        var cfg = Load("");
        Assert.Equal(0, cfg.CanSeeSamePLevel);
        Assert.Equal(1, cfg.ArriveDepartMsg);
        Assert.Equal(0x2, cfg.AreaFlags);
        Assert.True(cfg.AutoNewbieKeys);
        Assert.True(cfg.AutoShipKeys);
        Assert.Equal(8, cfg.ConnectingMaxIP);
        Assert.Equal(15, cfg.ContextMenuLimit);
        Assert.Equal(5, cfg.MaxConnectRequestsPerIP);
        Assert.Equal(15, cfg.MaxPings);
        Assert.Equal(5000, cfg.TimeoutIncompleteConnMs);
        Assert.Equal(1000, cfg.MediumCanHearGhosts);
        Assert.Equal(32, cfg.MaxCharComplexity);
        Assert.Equal(1024, cfg.MaxSectorComplexity);
        Assert.True(cfg.TradeWindowSnooping);
        Assert.True(cfg.VendorTradeTitle);
        Assert.True(cfg.WopPlayer);
        Assert.False(cfg.WopStaff);
        Assert.Equal(0x3B2, cfg.WopColor);
        Assert.Equal(10, cfg.WopTalkMode);
        Assert.Equal("1323,1624,0", cfg.ZeroPoint);
        Assert.Equal(1, cfg.LevelMode);
        Assert.Equal(100, cfg.ExperienceKoefPVP);
        Assert.Equal(9, cfg.EraLimitGear);
    }

    [Fact]
    public void Keys_AreReadUnderTheReferenceSpelling()
    {
        var cfg = Load(
            "CANSEESAMEPLEVEL=4\nAREAFLAGS=05\nTIMEOUTINCOMPLETECONN=5 * 1000\nTIMERCALL=2\n" +
            "TIMERCALLUNIT=1\nWOPCOLOR=022\nSTATSFLAGS=02\nEXPERIENCEMODE=013\nMAXCOMPLEXITY=10\n" +
            "ZEROPOINT=1000,1000,0\nCHATSTATICCHANNELS=A, B\nSPEECHOTHER=spk_other\n");
        Assert.Equal(4, cfg.CanSeeSamePLevel);
        Assert.Equal(0x5, cfg.AreaFlags);
        Assert.Equal(5000, cfg.TimeoutIncompleteConnMs);
        Assert.Equal(2000, cfg.TimerCallPeriodMs);   // seconds, not minutes
        Assert.Equal(0x22, cfg.WopColor);
        Assert.Equal(0x2, cfg.StatsFlags);
        Assert.Equal(0x13, cfg.ExperienceMode);
        Assert.Equal(10, cfg.MaxCharComplexity);
        Assert.Equal("1000,1000,0", cfg.ZeroPoint);
        Assert.Equal(new[] { "A", "B" }, ChatEngine.ParseStaticChannels(cfg.ChatStaticChannels));
        Assert.Equal("spk_other", cfg.SpeechOther);

        Assert.Equal(120_000, Load("TIMERCALL=2\n").TimerCallPeriodMs);
    }

    // ---------------------------------------------------------------- CANSEESAMEPLEVEL

    [Theory]
    [InlineData(0, PrivLevel.GM, PrivLevel.GM, true)]
    [InlineData(0, PrivLevel.GM, PrivLevel.Admin, false)]
    [InlineData(1, PrivLevel.GM, PrivLevel.GM, false)]
    [InlineData(1, PrivLevel.Admin, PrivLevel.GM, true)]
    [InlineData(4, PrivLevel.Counsel, PrivLevel.Counsel, false)]
    [InlineData(4, PrivLevel.GM, PrivLevel.Owner, true)]
    [InlineData(0, PrivLevel.Player, PrivLevel.Player, false)]
    public void CanSeeSamePLevel_DecidesWhoSeesHiddenStaff(int mode, PrivLevel viewer, PrivLevel target, bool sees)
    {
        var world = World();
        Character.CanSeeSamePLevel = mode;
        var a = world.CreateCharacter();
        a.IsPlayer = true;
        a.PrivLevel = viewer;
        var b = world.CreateCharacter();
        b.IsPlayer = true;
        b.PrivLevel = target;
        b.SetStatFlag(StatFlag.Hidden);

        Assert.Equal(sees, Character.CanSeeHidden(a, b));
    }

    // ---------------------------------------------------------------- STATSFLAGS

    [Fact]
    public void StatsFlags_DenyMaxWrites()
    {
        var world = World();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Str = 60;
        var npc = world.CreateCharacter();
        npc.Str = 70;

        Character.StatsFlags = 0x2; // players only
        Assert.True(player.TrySetProperty("MAXHITS", "200"));
        Assert.True(npc.TrySetProperty("MAXHITS", "200"));
        Assert.Equal(60, player.BaseMaxHits);
        Assert.Equal(200, npc.BaseMaxHits);
    }

    // ---------------------------------------------------------------- MEDIUMCANHEARGHOSTS

    [Fact]
    public void MediumCanHearGhosts_IsTheSpiritSpeakThreshold()
    {
        var world = World();
        var medium = world.CreateCharacter();
        medium.IsPlayer = true;
        medium.PrivLevel = PrivLevel.Player;
        medium.SetSkill(SkillType.SpiritSpeak, 800);

        Assert.False(GhostSpeech.HearsGhostClearly(medium));
        GhostSpeech.MediumCanHearGhosts = 800;
        Assert.True(GhostSpeech.HearsGhostClearly(medium));
    }

    // ---------------------------------------------------------------- SUPPRESSCAPITALS

    [Fact]
    public void SuppressCapitals_LowersShoutedLinesButTheFirstLetter()
    {
        Assert.Equal("HELLO THERE", SpeechEngine.ApplyCapitalsSuppression("HELLO THERE"));
        SpeechEngine.SuppressCapitals = true;
        Assert.Equal("Hello there", SpeechEngine.ApplyCapitalsSuppression("HELLO THERE"));
        Assert.Equal("HELLO", SpeechEngine.ApplyCapitalsSuppression("HELLO"));        // 5 chars or fewer
        Assert.Equal("Hello World", SpeechEngine.ApplyCapitalsSuppression("Hello World")); // not 75% capitals
    }

    // ---------------------------------------------------------------- WOP*

    private static (SpellEngine Engine, Character Caster, List<string> Spoken) SpellSetup(GameWorld world, PrivLevel priv)
    {
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Heal, Name = "Heal",
            Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
            ManaCost = 0, CastTimeBase = 1, Runes = "In Mani",
        });
        var engine = new SpellEngine(world, registry);
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = priv;
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var spoken = new List<string>();
        engine.OnSpellWords = (_, w) => spoken.Add(w);
        return (engine, caster, spoken);
    }

    [Fact]
    public void WopPlayerAndStaff_DecideWhoSpeaksThePowerWords()
    {
        // CastPreparation.Words: null = the spell's own mantra, "" = silent.
        var world = World();
        var (engine, player, _) = SpellSetup(world, PrivLevel.Player);
        Assert.Null(engine.PrepareCast(player, SpellType.Heal)!.Words);
        SpellEngine.WopPlayer = false;
        Assert.Equal("", engine.PrepareCast(player, SpellType.Heal)!.Words);

        var (engine2, gm, spoken) = SpellSetup(World(), PrivLevel.GM);
        Assert.True(engine2.CastStart(gm, SpellType.Heal, gm.Uid, gm.Position) > 0);
        Assert.Empty(spoken);  // WOPSTAFF defaults off
        gm.ClearCastState();
        SpellEngine.WopStaff = true;
        Assert.True(engine2.CastStart(gm, SpellType.Heal, gm.Uid, gm.Position) > 0);
        Assert.Equal(new[] { "In Mani" }, spoken);
    }

    [Fact]
    public void WopColorAndTalkMode_FallBackAsTheReferenceDoes()
    {
        var world = World();
        var caster = world.CreateCharacter();
        caster.SpeechColor = 0x55;
        Assert.Equal(0x3B2, SpellEngine.DefaultWopHue(caster));  // WOPCOLOR > 0 wins
        SpellEngine.WopColor = 0;
        Assert.Equal(0x55, SpellEngine.DefaultWopHue(caster));
        Assert.Equal(10, SpellEngine.EffectiveWopTalkMode);
        SpellEngine.WopTalkMode = 0x0F;                            // TALKMODE_COMMAND is out of range
        Assert.Equal(10, SpellEngine.EffectiveWopTalkMode);
        SpellEngine.WopTalkMode = 9;
        Assert.Equal(9, SpellEngine.EffectiveWopTalkMode);
    }

    // ---------------------------------------------------------------- DISTANCEFORMULA

    [Fact]
    public void DistanceFormula_BendsGameDistanceButNotSight()
    {
        var a = new Point3D(0, 0, 0);
        var b = new Point3D(3, 4, 20);
        Assert.Equal(4, a.GetDistanceTo(b));
        Point3D.DistanceFormula = 1;
        Assert.Equal(5, a.GetDistanceTo(b));
        Point3D.DistanceFormula = 2;
        Assert.Equal(21, a.GetDistanceTo(b));   // sqrt(9+16+400) = 20.6
        Assert.Equal(4, a.GetDistSight(b));
    }

    // ---------------------------------------------------------------- MOUNTHEIGHT

    [Fact]
    public void MountHeight_OnlyAffectsRidersBelowGm()
    {
        var world = World();
        var rider = world.CreateCharacter();
        rider.IsPlayer = true;
        rider.PrivLevel = PrivLevel.Player;
        rider.SetStatFlag(StatFlag.Hovering);

        Assert.False(WalkCheck.RiderNeedsHeadroom(rider));
        WalkCheck.MountHeight = true;
        Assert.True(WalkCheck.RiderNeedsHeadroom(rider));
        rider.PrivLevel = PrivLevel.GM;
        Assert.False(WalkCheck.RiderNeedsHeadroom(rider));
        Assert.Equal(21, WalkCheck.MountedClearance);
    }

    // ---------------------------------------------------------------- CHARTAGS / VENDORTRADETITLE

    [Fact]
    public void ClickName_TradeTitleAndCharTags()
    {
        var world = World();
        var vendor = world.CreateCharacter();
        vendor.IsPlayer = false;
        vendor.NpcBrain = NpcBrainType.Vendor;
        vendor.BodyId = 0x0190;
        vendor.Name = "Bob";
        vendor.Title = "the smith";

        Assert.Equal(" the smith", ClientInventoryHandler.CharNameSuffix(vendor, allShow: false));
        GameClient.VendorTradeTitle = false;
        Assert.Equal("", ClientInventoryHandler.CharNameSuffix(vendor, allShow: false));

        GameClient.CharTags = true;
        vendor.SetStatFlag(StatFlag.Invul);
        Assert.Equal(" [npc] [invul]", ClientInventoryHandler.CharNameSuffix(vendor, allShow: false));
    }

    // ---------------------------------------------------------------- CONTEXTMENULIMIT

    [Fact]
    public void ContextMenuLimit_DropsTheEntriesPastIt()
    {
        var entries = Enumerable.Range(0, 20).Select(i => ((ushort)i, (uint)(3000000 + i), (ushort)0)).ToArray();
        PacketContextMenu.EntryLimit = 4;
        var data = new PacketContextMenu(1, entries).Build().Data;
        Assert.Equal(4, data[11]);
    }

    // ---------------------------------------------------------------- VERBOSEITEMBOUNCE / ZEROPOINT

    [Fact]
    public void ItemBounceMessage_NamesWhereTheItemWent()
    {
        Assert.Equal("You put the dagger in your pack.", GameClient.ItemBounceMessage("dagger", onGround: false));
        // DEFMSG_MSG_FEET ends in its own period inside "You put the %s %s." - upstream
        // prints both (CCharAct.cpp:3205).
        Assert.Equal("You put the dagger at your feet. It is too heavy..", GameClient.ItemBounceMessage("dagger", onGround: true));
    }

    [Fact]
    public void ZeroPoint_MovesTheSextantOrigin()
    {
        Assert.StartsWith("0° 0'S, 0° 0'E", ClientItemUseHandler.FormatSextant(new Point3D(1323, 1624, 0)));
        GameClient.SetSextantZeroPoint("1000,1000,0");
        Assert.StartsWith("0° 0'S, 0° 0'E", ClientItemUseHandler.FormatSextant(new Point3D(1000, 1000, 0)));
        GameClient.SetSextantZeroPoint("garbage");
        Assert.Equal(1000, GameClient.SextantZeroX);
    }

    // ---------------------------------------------------------------- MAXCOMPLEXITY / MAXSECTORCOMPLEXITY

    [Fact]
    public void SectorComplexity_WarnsPastTheLimits()
    {
        var world = World();
        var warnings = new List<string>();
        Sector.ComplexityWarning = warnings.Add;
        Sector.MaxCharComplexity = 2;
        for (int i = 0; i < 3; i++)
        {
            var npc = world.CreateCharacter();
            world.PlaceCharacter(npc, new Point3D(10, 10, 0, 0));
        }

        Assert.Equal(1, world.CheckSectorComplexity());
        Assert.Contains(warnings, w => w.StartsWith("3 chars at 0,0,0,0"));
        Assert.Equal(3, world.GetSector(new Point3D(10, 10, 0, 0))!.GetCharComplexity());
    }

    // ---------------------------------------------------------------- AREAFLAGS

    [Fact]
    public void AreaFlags_RoomsInheritFromTheirArea()
    {
        var world = World();
        var area = new Region { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        area.AddRect(0, 0, 100, 100);
        area.SetTag("CITY", "1");
        world.AddRegion(area);
        var room = new Room { Name = "inn", MapIndex = 0 };
        room.AddRect(10, 10, 20, 20);
        world.AddRoom(room);

        world.ApplyRoomInheritance(0x2);   // the default: flags only

        Assert.True(room.IsFlag(RegionFlag.Guarded));
        Assert.False(room.TryGetTag("CITY", out _));

        world.ApplyRoomInheritance(0x4);
        Assert.True(room.TryGetTag("CITY", out string? city));
        Assert.Equal("1", city);
    }

    // ---------------------------------------------------------------- MAXSHIPSGUILD / ERALIMIT*

    [Fact]
    public void MaxShipsGuild_IsANewStonesShipCap()
    {
        GuildManager.DefaultMaxShips = 3;
        var guild = new GuildManager().CreateGuild(new Serial(0x40001234), "g", new Serial(0x10));
        Assert.Equal(3, guild.MaxShips);
    }

    [Fact]
    public void EraLimits_UnsetChardefTakesTheIniValue()
    {
        var def = new CharDef(ResourceId.Invalid);
        Assert.Equal(9, def.EraLimitGear);
        Assert.False(def.HasEraLimitGear);
        CharDef.DefaultEraLimitGear = 3;
        Assert.Equal(3, def.EraLimitGear);
        def.EraLimitGear = 5;
        Assert.True(def.HasEraLimitGear);
        Assert.Equal(5, def.EraLimitGear);
    }

    // ---------------------------------------------------------------- AUTOPRIVFLAGS / GUESTSMAX

    [Fact]
    public void AutoPrivFlags_SeedANewAccount()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        AccountManager.DefaultPrivFlags = 0x40;
        var acc = new AccountManager(lf).CreateAccount("privtest" + Guid.NewGuid().ToString("N")[..6], "pw");
        Assert.NotNull(acc);
        Assert.Equal(0x40u, acc!.Priv);
    }

    private static List<byte[]> GuestLogin(AccountManager accounts)
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(50_000, 60_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        client.HandleLoginRequest("GUEST", "whatever");
        return TestHarness.GetQueuedPackets(state).Select(p => p.Span.ToArray()).ToList();
    }

    [Fact]
    public void GuestsMax_GatesGuestLogins()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var accounts = new AccountManager(lf);

        var refused = GuestLogin(accounts);
        Assert.Contains(refused, p => p[0] == 0x82 && p[1] == 0x02);   // MaxGuests -> Blocked

        GameClient.GuestsMax = 1;
        var accepted = GuestLogin(accounts);
        Assert.DoesNotContain(accepted, p => p[0] == 0x82);
        var guest = accounts.FindAccount("GUEST0");
        Assert.NotNull(guest);
        Assert.Equal(PrivLevel.Guest, guest!.PrivLevel);

        GameClient.AccountInUse = a => a == guest;   // the only slot is taken
        Assert.Contains(GuestLogin(accounts), p => p[0] == 0x82);
    }

    // ---------------------------------------------------------------- network gates

    [Fact]
    public void IpHistory_RefusesPastMaxConnectRequestsAndMaxPings()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var mgr = new NetworkManager(4, lf);
        var ip = IPAddress.Parse("10.1.2.3");

        for (int i = 1; i < 5; i++)
            Assert.False(mgr.RejectByIpHistory(ip, out _));
        Assert.True(mgr.RejectByIpHistory(ip, out string reason));   // the 5th request
        Assert.Contains("MaxConnectRequestsPerIP", reason);

        var mgr2 = new NetworkManager(4, lf) { MaxConnectRequestsPerIP = 0, MaxPings = 2 };
        Assert.False(mgr2.RejectByIpHistory(ip, out _));
        Assert.False(mgr2.RejectByIpHistory(ip, out _));
        Assert.True(mgr2.RejectByIpHistory(ip, out reason));
        Assert.Contains("MAXPINGS", reason);

        // Loopback (panel, bot harness) is never counted.
        for (int i = 0; i < 20; i++)
            Assert.False(mgr.RejectByIpHistory(IPAddress.Loopback, out _));
    }

    [Fact]
    public void IpHistory_IsForgottenAfterTheTtl()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var mgr = new NetworkManager(4, lf) { MaxConnectRequestsPerIP = 2, NetHistoryTtlSeconds = 1 };
        var ip = IPAddress.Parse("10.9.9.9");
        Assert.False(mgr.RejectByIpHistory(ip, out _));
        mgr.DecayIpHistory(0, force: true);   // ttl 1 -> 0
        mgr.DecayIpHistory(0, force: true);   // ttl 0 -> -1: forgotten
        Assert.False(mgr.RejectByIpHistory(ip, out _));
    }

    [Fact]
    public void StatusPings_AreAnsweredOrRefused()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var mgr = new NetworkManager(4, lf) { StatusStringProvider = k => k == 0x22 ? "uog" : "cuo" };
        var state = TestHarness.CreateActiveNetState(lf, 1);

        Assert.True(mgr.TryAnswerStatusPing(state, [0x7F]));
        Assert.True(state.IsClosing);
        Assert.Contains(TestHarness.GetQueuedPackets(state), p => p.Span.SequenceEqual("uog"u8));

        var state2 = TestHarness.CreateActiveNetState(lf, 2);
        mgr.CUOStatus = false;
        Assert.True(mgr.TryAnswerStatusPing(state2, [0xF1, 0x00, 0x04, 0xFF]));
        Assert.Empty(TestHarness.GetQueuedPackets(state2));

        Assert.False(mgr.TryAnswerStatusPing(state2, [0x01, 0x02, 0x03, 0x04]));   // a seed
    }
}
