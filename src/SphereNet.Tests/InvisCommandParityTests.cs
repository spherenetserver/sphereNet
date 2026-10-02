using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Staff INVIS is CHV_INVIS (CChar.cpp:4655): STATF_INSUBSTANTIAL through
/// GetArgLLFlag (no argument flips, an argument is evaluated, zero clears), then
/// UpdateMode(true) so every client in range redraws or removes the character at
/// once, the BI_HIDDEN buff and the INVIS ON/OFF line under OF_Command_Sysmsgs.
/// The .INVIS command and the INVIS script verb are the same implementation.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class InvisCommandParityTests
{
    private sealed class Fixture
    {
        public GameWorld World = null!;
        public ILoggerFactory Lf = null!;
        public AccountManager Accounts = null!;
        public CommandHandler Commands = null!;
        public readonly List<GameClient> Clients = new();
        public readonly List<string> SysMessages = new();
        public readonly List<(BuffIcon Icon, bool Add)> Buffs = new();
        private int _nextId = 9300;

        public Character Player(short x, PrivLevel priv = PrivLevel.Player)
        {
            var ch = World.CreateCharacter();
            ch.IsPlayer = true;
            ch.PrivLevel = priv;
            ch.MaxHits = 100; ch.Hits = 100;
            ch.BodyId = 0x0190;
            World.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
            return ch;
        }

        public GameClient Client(Character ch)
        {
            var state = TestHarness.CreateActiveNetState(Lf, _nextId++);
            var client = new GameClient(state, World, Accounts, Lf.CreateLogger<GameClient>());
            TestHarness.AttachCharacter(client, ch);
            Clients.Add(client);
            return client;
        }

        public void Clear()
        {
            foreach (var c in Clients)
                TestHarness.ClearQueuedPackets(c.NetState);
            SysMessages.Clear();
            Buffs.Clear();
        }
    }

    private static Fixture NewFixture(bool commandSysMessages = true)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var f = new Fixture { World = world, Lf = lf, Accounts = new AccountManager(lf) };
        f.Commands = new CommandHandler();
        f.Commands.RegisterDefaults(world);
        f.Commands.OnSysMessage += (_, msg) => f.SysMessages.Add(msg);
        // The Program wiring: every client takes its share of UpdateMode.
        Character.OnUpdateMode = ch =>
        {
            foreach (var c in f.Clients)
                c.RefreshCharacterMode(ch);
        };
        Character.OnClientBuffChanged = (_, icon, add, _, _) => f.Buffs.Add((icon, add));
        GameClient.ServerOptionFlags = commandSysMessages
            ? OptionFlags.FileCommands | OptionFlags.Buffs | OptionFlags.CommandSysMessages
            : OptionFlags.FileCommands | OptionFlags.Buffs;
        return f;
    }

    private static uint Serial(System.ReadOnlySpan<byte> s, int at) =>
        (uint)((s[at] << 24) | (s[at + 1] << 16) | (s[at + 2] << 8) | s[at + 3]);

    /// <summary>The flags byte of the last 0x78 for this serial, or null when none was sent.
    /// 0x78: [id][len:2][serial:4][body:2][x:2][y:2][z][dir][hue:2][flags][noto]...</summary>
    private static byte? LastDrawFlags(NetState state, uint serial)
    {
        byte? flags = null;
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length >= 19 && s[0] == 0x78 && Serial(s, 3) == serial)
                flags = s[17];
        }
        return flags;
    }

    private static ushort? LastDrawHue(NetState state, uint serial)
    {
        ushort? hue = null;
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length >= 19 && s[0] == 0x78 && Serial(s, 3) == serial)
                hue = (ushort)((s[15] << 8) | s[16]);
        }
        return hue;
    }

    private static bool SawDelete(NetState state, uint serial)
    {
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length >= 5 && s[0] == 0x1D && Serial(s, 1) == serial)
                return true;
        }
        return false;
    }

    private static bool Insubstantial(Character ch) => ch.IsStatFlag(StatFlag.Insubstantial);

    // ---- the argument: GetArgLLFlag -----------------------------------------

    [Theory]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("2-1", true)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    [InlineData("00", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("1-1", false)]
    public void InvisCommandArgumentSetsOrClearsTheFlag(string arg, bool expected)
    {
        foreach (bool startOn in new[] { false, true })
        {
            var f = NewFixture();
            var gm = f.Player(100, PrivLevel.Owner);
            if (startOn) gm.SetStatFlag(StatFlag.Insubstantial);

            Assert.Equal(CommandResult.Executed, f.Commands.TryExecute(gm, $"invis {arg}"));

            Assert.Equal(expected, Insubstantial(gm));
            // Staff INVIS is not the spell: the spell's flag is never touched.
            Assert.False(gm.IsStatFlag(StatFlag.Invisible));
        }
    }

    [Fact]
    public void LiveLogCase_InvisOffAfterInvisOnMakesTheGmVisibleAgain()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.Owner);

        f.Commands.TryExecute(gm, "invis on");
        Assert.True(Insubstantial(gm));
        Assert.Equal(new[] { "Invis ON" }, f.SysMessages);

        f.Clear();
        f.Commands.TryExecute(gm, "invis off");
        Assert.False(Insubstantial(gm));
        Assert.False(gm.IsConcealed);
        Assert.Equal(new[] { "Invis OFF" }, f.SysMessages);
    }

    [Fact]
    public void BareInvisToggles()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);

        f.Commands.TryExecute(gm, "invis");
        Assert.True(Insubstantial(gm));
        f.Commands.TryExecute(gm, "invis");
        Assert.False(Insubstantial(gm));
    }

    // ---- UpdateMode: the change is on every screen at once ------------------

    [Fact]
    public void InvisRemovesTheGmFromAPlayerAndRedrawsItGreyForStaffAndSelf()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        var player = f.Player(103);
        var counsel = f.Player(104, PrivLevel.Counsel);
        var gmClient = f.Client(gm);
        var playerClient = f.Client(player);
        var counselClient = f.Client(counsel);
        playerClient.NotifyCharacterAppear(gm);
        counselClient.NotifyCharacterAppear(gm);
        Assert.True(playerClient.HasKnownChar(gm.Uid.Value));
        f.Clear();

        f.Commands.TryExecute(gm, "invis");

        // The player loses sight at once (0x1D), not after its own next step.
        Assert.True(SawDelete(playerClient.NetState, gm.Uid.Value));
        Assert.False(playerClient.HasKnownChar(gm.Uid.Value));
        // A counselor ranks below the GM, so CanSee hides it from them too
        // (CANSEESAMEPLEVEL 0: plevelMe < plevelChar -> false).
        Assert.True(SawDelete(counselClient.NetState, gm.Uid.Value));
        // The GM's own client redraws its body with the grey (CHARMODE_INVIS) bit.
        byte? selfFlags = LastDrawFlags(gmClient.NetState, gm.Uid.Value);
        Assert.NotNull(selfFlags);
        Assert.Equal(0x80, selfFlags!.Value & 0x80);
    }

    [Fact]
    public void HigherStaffSeesTheInsubstantialGmGreyed()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        var admin = f.Player(103, PrivLevel.Admin);
        f.Client(gm);
        var adminClient = f.Client(admin);
        adminClient.NotifyCharacterAppear(gm);
        f.Clear();

        f.Commands.TryExecute(gm, "invis 1");

        Assert.False(SawDelete(adminClient.NetState, gm.Uid.Value));
        byte? flags = LastDrawFlags(adminClient.NetState, gm.Uid.Value);
        Assert.NotNull(flags);
        Assert.Equal(0x80, flags!.Value & 0x80);
        Assert.True(adminClient.HasKnownChar(gm.Uid.Value));
    }

    [Fact]
    public void InvisOffBringsTheGmBackOnThePlayersScreenAtOnce()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        var player = f.Player(103);
        var gmClient = f.Client(gm);
        var playerClient = f.Client(player);
        f.Commands.TryExecute(gm, "invis 1");
        Assert.False(playerClient.HasKnownChar(gm.Uid.Value));
        f.Clear();

        f.Commands.TryExecute(gm, "invis 0");

        byte? flags = LastDrawFlags(playerClient.NetState, gm.Uid.Value);
        Assert.NotNull(flags);
        Assert.Equal(0, flags!.Value & 0x80);
        Assert.True(playerClient.HasKnownChar(gm.Uid.Value));
        byte? selfFlags = LastDrawFlags(gmClient.NetState, gm.Uid.Value);
        Assert.NotNull(selfFlags);
        Assert.Equal(0, selfFlags!.Value & 0x80);
        // And the per-tick view agrees with what was just sent.
        Assert.Contains(gm.Uid.Value, playerClient.BuildViewDelta()!.CurrentChars);
    }

    [Fact]
    public void ColorInvisGivesTheInsubstantialBodyThatHueInsteadOfTheGreyBit()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        var gmClient = f.Client(gm);
        ushort saved = GameClient.ColorInvisHue;
        try
        {
            GameClient.ColorInvisHue = 0x4001;
            f.Commands.TryExecute(gm, "invis 1");
            Assert.Equal((ushort)0x4001, LastDrawHue(gmClient.NetState, gm.Uid.Value));
            Assert.Equal(0, LastDrawFlags(gmClient.NetState, gm.Uid.Value)!.Value & 0x80);
        }
        finally
        {
            GameClient.ColorInvisHue = saved;
        }
    }

    // ---- buff and message ---------------------------------------------------

    [Fact]
    public void InvisRaisesAndDropsTheHiddenBuff()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);

        f.Commands.TryExecute(gm, "invis 1");
        Assert.Contains((BuffIcon.Hidden, true), f.Buffs);

        f.Clear();
        f.Commands.TryExecute(gm, "invis 0");
        Assert.Contains((BuffIcon.Hidden, false), f.Buffs);
    }

    [Fact]
    public void LeavingInvisKeepsTheHiddenBuffWhileStillHidden()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        f.Commands.TryExecute(gm, "invis 1");
        gm.SetStatFlag(StatFlag.Hidden);
        f.Clear();

        f.Commands.TryExecute(gm, "invis 0");

        Assert.DoesNotContain((BuffIcon.Hidden, false), f.Buffs);
    }

    [Fact]
    public void WithoutCommandSysmsgsTheToggleIsSilent()
    {
        var f = NewFixture(commandSysMessages: false);
        var gm = f.Player(100, PrivLevel.GM);

        f.Commands.TryExecute(gm, "invis 1");

        Assert.True(Insubstantial(gm));
        Assert.Empty(f.SysMessages);
    }

    // ---- the script verb is the same code -----------------------------------

    private sealed class CapturingConsole(Character ch) : ITextConsole
    {
        public readonly List<string> Lines = new();
        public PrivLevel GetPrivLevel() => ch.PrivLevel;
        public void SysMessage(string text) => Lines.Add(text);
        public string GetName() => ch.Name;
    }

    [Fact]
    public void ScriptVerbInvisFollowsTheSameRules()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        var player = f.Player(103);
        f.Client(gm);
        var playerClient = f.Client(player);
        playerClient.NotifyCharacterAppear(gm);
        var console = new CapturingConsole(gm);

        Assert.True(gm.TryExecuteCommand("INVIS", "on", console));
        Assert.True(Insubstantial(gm));
        Assert.False(playerClient.HasKnownChar(gm.Uid.Value));
        Assert.Equal(new[] { "Invis ON" }, console.Lines);

        Assert.True(gm.TryExecuteCommand("INVIS", "off", console));
        Assert.False(Insubstantial(gm));
        Assert.True(playerClient.HasKnownChar(gm.Uid.Value));

        Assert.True(gm.TryExecuteCommand("INVIS", "", console));
        Assert.True(Insubstantial(gm));
    }

    // ---- related toggles with the same parse ---------------------------------

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void InvulCommandHonoursItsArgument(string arg, bool expected)
    {
        foreach (bool startOn in new[] { false, true })
        {
            var f = NewFixture();
            var gm = f.Player(100, PrivLevel.GM);
            if (startOn) gm.SetStatFlag(StatFlag.Invul);

            f.Commands.TryExecute(gm, $"invul {arg}");

            Assert.Equal(expected, gm.IsStatFlag(StatFlag.Invul));
        }
    }

    [Fact]
    public void InvulScriptVerbAndPropertyShareTheParse()
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        var console = new CapturingConsole(gm);

        Assert.True(gm.TryExecuteCommand("INVUL", "off", console));
        Assert.False(gm.IsStatFlag(StatFlag.Invul));
        Assert.True(gm.TryExecuteCommand("INVUL", "", console));
        Assert.True(gm.IsStatFlag(StatFlag.Invul));
        Assert.Equal(new[] { "Invulnerability OFF", "Invulnerability ON" }, console.Lines);

        Assert.True(gm.TrySetProperty("INVUL", "off"));
        Assert.False(gm.IsStatFlag(StatFlag.Invul));
        Assert.True(gm.TrySetProperty("INVUL", ""));
        Assert.True(gm.IsStatFlag(StatFlag.Invul));
    }

    [Theory]
    [InlineData("allshow")]
    [InlineData("allmove")]
    public void PrivToggleCommandsReadOffAsOff(string verb)
    {
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        bool Read() => verb == "allshow" ? gm.AllShow : gm.AllMove;

        f.Commands.TryExecute(gm, $"{verb} on");
        Assert.True(Read());
        f.Commands.TryExecute(gm, $"{verb} off");
        Assert.False(Read());
        f.Commands.TryExecute(gm, $"{verb} 1");
        Assert.True(Read());
        f.Commands.TryExecute(gm, $"{verb} 00");
        Assert.False(Read());
        f.Commands.TryExecute(gm, verb);
        Assert.True(Read());
    }

    [Fact]
    public void GmCommandTogglesGmModeNotVisibility()
    {
        // CC_GM is TogPrivFlags(PRIV_GM) (CClient.cpp:836); it never made anyone invisible.
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        Assert.True(gm.IsGmMode);

        f.Commands.TryExecute(gm, "gm");
        Assert.False(gm.IsGmMode);
        Assert.False(gm.IsConcealed);

        f.Commands.TryExecute(gm, "gm on");
        Assert.True(gm.IsGmMode);
        f.Commands.TryExecute(gm, "gm off");
        Assert.False(gm.IsGmMode);
        Assert.False(gm.IsConcealed);
    }

    [Fact]
    public void StaffWalkingWhileInvisIsNotRevealed()
    {
        // INSUBSTANTIAL is outside CheckRevealOnMove (CCharAct.cpp:4844).
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        f.Commands.TryExecute(gm, "invis 1");

        gm.ClearHiddenState();

        Assert.True(Insubstantial(gm));
    }

    [Fact]
    public void SavedFlagsLoadTheSameBitsAndTheOldInvisibleStateClearsOnLoad()
    {
        // FLAGS bits are Source-X's: 0x08 STATF_INVISIBLE, 0x2000 STATF_INSUBSTANTIAL.
        var f = NewFixture();

        // A GM saved by the old .INVIS carries STATF_INVISIBLE without a spell
        // memory; the load/login sweep drops that stray bit, so it comes back visible.
        var oldGm = f.Player(100, PrivLevel.GM);
        Assert.True(oldGm.TrySetProperty("FLAGS", "08"));
        Assert.True(oldGm.IsStatFlag(StatFlag.Invisible));
        oldGm.ClearTransientVisualState();
        Assert.False(oldGm.IsConcealed);

        // A GM saved invisible by Source-X (or now) keeps INSUBSTANTIAL across the
        // sweep, and .INVIS turns it off again.
        var gm = f.Player(101, PrivLevel.GM);
        Assert.True(gm.TrySetProperty("FLAGS", "02000"));
        gm.ClearTransientVisualState();
        Assert.True(Insubstantial(gm));
        f.Commands.TryExecute(gm, "invis");
        Assert.False(gm.IsConcealed);
    }
}
