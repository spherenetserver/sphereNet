using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ScriptResponseLifecycleTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"responses-{Guid.NewGuid():N}.scp");
        private readonly ILoggerFactory _logs = LoggerFactory.Create(_ => { });
        public readonly GameWorld World = TestHarness.CreateWorld();
        public readonly GameClient Client;
        public readonly Character Player;
        public Fixture(string script = "[FUNCTION f_reply]\nTAG.REPLY=A<ARGS>B\nTAG.NUMBER=<ARGN1>\n", string? eventName = null)
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            File.WriteAllText(_path, script);
            stack.Resources.LoadResourceFile(_path);
            stack.Interpreter.FunctionLookup = stack.Runner.HasFunction;
            Client = TestHarness.CreateClient(_logs, World, new AccountManager(_logs), 19540);
            Player = World.CreateCharacter();
            TestHarness.AttachCharacter(Client, Player);
            if (eventName != null) Player.Events.Add(stack.Resources.ResolveDefName(eventName));
            Client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
        }
        public uint PromptId => BinaryPrimitives.ReadUInt32BigEndian(Packet(0x9A, 0xC2).AsSpan(7));
        public ushort MenuId => BinaryPrimitives.ReadUInt16BigEndian(Packet(0x7C).AsSpan(7));
        public ushort InputContext => BinaryPrimitives.ReadUInt16BigEndian(Packet(0xAB).AsSpan(7));
        public byte[] Packet(params byte[] ids) => TestHarness.GetQueuedPackets(Client.NetState).Last(p => ids.Contains(p.Span[0])).Span.ToArray();
        public string Tag(string key) { Assert.True(Player.TryGetTag(key, out var value)); return value!; }
        public void Dispose() { File.Delete(_path); _logs.Dispose(); }
    }

    [Fact]
    public void UnexpectedPromptCannotRenameAnItem()
    {
        using var f = new Fixture();
        var item = f.World.CreateItem(); item.Name = "Original";
        f.Client.HandlePromptResponse(item.Uid.Value, 7, 1, "Changed");
        Assert.Equal("Original", item.Name);
    }

    [Fact]
    public void StalePromptCannotConsumeTheCurrentSession()
    {
        using var f = new Fixture();
        string? answer = null;
        f.Client.SendPrompt(10, "First", (_, _, _, text) => answer = "wrong");
        f.Client.SendPrompt(11, "Second", (_, _, _, text) => answer = text);
        f.Client.HandlePromptResponse(f.Player.Uid.Value, 10, 0, "");
        f.Client.HandlePromptResponse(999, 11, 1, "wrong");
        Assert.Null(answer);
        f.Client.HandlePromptResponse(f.Player.Uid.Value, 11, 1, "right");
        Assert.Equal("right", answer);
    }

    [Fact]
    public void CallbackCanOpenTheNextPrompt()
    {
        using var f = new Fixture();
        string? answer = null;
        f.Client.SendPrompt(10, "First", (_, _, _, _) =>
            f.Client.SendPrompt(11, "Second", (_, _, _, text) => answer = text));
        f.Client.HandlePromptResponse(f.Player.Uid.Value, 10, 1, "first");
        f.Client.HandlePromptResponse(f.Player.Uid.Value, 11, 1, "second");
        Assert.Equal("second", answer);
    }

    [Theory]
    [InlineData(false, 1u, "42", "A42B", "42")]
    [InlineData(false, 0u, "", "AB", "0")]
    [InlineData(false, 1u, "a|b=c\td", "AabcdB", "0")]
    [InlineData(true, 1u, "Türkçe|[]", "ATürkçe|[]B", "0")]
    public void ScriptPromptRunsOnPlayerWithFreshArguments(bool unicode, uint type, string text, string expected, string number)
    {
        using var f = new Fixture();
        var item = f.World.CreateItem();
        Assert.True(f.Client.TryExecuteScriptCommand(item, unicode ? "PROMPTCONSOLEU" : "PROMPTCONSOLE", "f_reply,Question", null));
        uint id = f.PromptId;
        f.Client.HandlePromptResponse(f.Player.Uid.Value, id, type, text);
        Assert.Equal(expected, f.Tag("REPLY"));
        Assert.Equal(number, f.Tag("NUMBER"));
        Assert.False(item.TryGetTag("REPLY", out _));
        f.Client.HandlePromptResponse(f.Player.Uid.Value, id, 1, "replay");
        Assert.Equal(expected, f.Tag("REPLY"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MenuRunsSelectedOrCancelBlockOnItsSubject(int choice)
    {
        using var f = new Fixture("[MENU m_response]\nQuestion\nON=@Cancel\nTAG.CANCEL=1\nON=0 Choose\nLOCAL.N=7\nIF 0\nTAG.BAD=1\nELSE\nTAG.CHOICE=<LOCAL.N>\nENDIF\nRETURN 1\nTAG.BAD=2\n");
        var item = f.World.CreateItem();
        Assert.True(f.Client.TryExecuteScriptCommand(item, "MENU", "m_response", null));
        ushort id = f.MenuId;
        f.Client.HandleMenuChoice(f.Player.Uid.Value, (ushort)(id ^ 1), 1, 0);
        Assert.False(item.TryGetTag("CHOICE", out _));
        f.Client.HandleMenuChoice(f.Player.Uid.Value, id, (ushort)choice, 0);
        Assert.True(item.TryGetTag(choice == 0 ? "CANCEL" : "CHOICE", out var value));
        Assert.Equal(choice == 0 ? "1" : "7", value);
        Assert.False(item.TryGetTag("BAD", out _));
        Assert.False(f.Player.TryGetTag("CHOICE", out _));
    }

    [Theory]
    [InlineData(" ", false)]
    [InlineData("\t", false)]
    [InlineData(" ", true)]
    public void TargetFunctionReceivesNumbersAndGraphic(string separator, bool ground)
    {
        using var f = new Fixture("[FUNCTION f_target]\nTAG.N1=<ARGN1>\nTAG.N2=<ARGN2>\nTAG.N3=<ARGN3>\nTAG.GRAPHIC=<LOCAL.ID>\n");
        var item = f.World.CreateItem();
        Assert.True(f.Client.TryExecuteScriptCommand(f.Player, ground ? "TARGETFG" : "TARGETF", "f_target" + separator + "42,5,6", null));
        f.Client.HandleTargetResponse(ground ? (byte)1 : (byte)0, f.Client.ActiveTargetCursorId,
            ground ? 0u : item.Uid.Value, 10, 10, 0, 1234);
        Assert.Equal("42", f.Tag("N1"));
        Assert.Equal("5", f.Tag("N2"));
        Assert.Equal("6", f.Tag("N3"));
        Assert.Equal("1234", f.Tag("GRAPHIC"));
    }

    [Theory]
    [InlineData("NAME,20", 1, "#", "#")]
    [InlineData("NAME\t20", 1, "New", "New")]
    [InlineData("NAME 20", 2, "New", "Original")]
    public void InputUsesArgumentSeparatorsAndOnlyAcceptsOk(string args, byte action, string text, string expected)
    {
        using var f = new Fixture();
        var item = f.World.CreateItem(); item.Name = "Original";
        Assert.True(f.Client.TryExecuteScriptCommand(item, "INPDLG", args, null));
        f.Client.HandleGumpTextEntry(item.Uid.Value, f.InputContext, action, text);
        Assert.Equal(expected, item.Name);
    }

    [Fact]
    public void NewInputReplacesTheOldPendingInput()
    {
        using var f = new Fixture();
        var item = f.World.CreateItem(); item.Name = "Original";
        f.Client.SendInputPromptGump(item, "NAME", 20);
        ushort old = f.InputContext;
        f.Client.SendInputPromptGump(item, "NAME", 20);
        ushort current = f.InputContext;
        f.Client.HandleGumpTextEntry(item.Uid.Value, old, 1, "Stale");
        Assert.Equal("Original", item.Name);
        f.Client.HandleGumpTextEntry(item.Uid.Value, current, 1, "Current");
        Assert.Equal("Current", item.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextCommandCanRewriteOrCancelBeforeSkillDispatch(bool cancel)
    {
        using var f = new Fixture("[EVENTS e_ext]\nON=@UserExtCmd\nTAG.TYPE=<ARGN1>\nTAG.RAW=<ARGS>\nARGS=21 0\nARGN1=88\nRETURN " + (cancel ? "1" : "0") + "\n", "e_ext");
        int? skill = null;
        f.Client.HandleTextCommand(0x24, "46 0", id => skill = id);
        Assert.Equal("36", f.Tag("TYPE"));
        Assert.Equal("46 0", f.Tag("RAW"));
        Assert.Equal(cancel ? (int?)null : 21, skill);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(100, 14)]
    public void DoorMacroExposesAndClampsScriptDistance(int requested, int expected)
    {
        using var f = new Fixture("[EVENTS e_ext]\nON=@UserExtCmd\nTAG.DEFAULT=<LOCAL.DoorAutoDist>\nLOCAL.DoorAutoDist=" + requested + "\n", "e_ext");
        string command = "";
        Assert.True(f.Client.PrepareTextCommand(0x58, ref command, out int distance));
        Assert.Equal("1", f.Tag("DEFAULT"));
        Assert.Equal(expected, distance);
    }

    [Theory]
    [InlineData(299, true)]
    [InlineData(300, false)]
    public void ExtendedCommandsRespectSourceXArgumentLimit(int length, bool accepted)
    {
        using var f = new Fixture("[EVENTS e_ext]\nON=@UserExtCmd\nTAG.CALLED=1\n", "e_ext");
        string command = new('x', length);
        Assert.Equal(accepted, f.Client.PrepareTextCommand(0xFE, ref command, out _));
        Assert.Equal(accepted, f.Player.TryGetTag("CALLED", out _));
        if (accepted) Assert.Equal(255, command.Length);
    }

    [Fact]
    public void ExtendedCommandRewrittenArgsReachVirtueTrigger()
    {
        using var f = new Fixture("[EVENTS e_ext]\nON=@UserExtCmd\nARGS=3\nON=@UserVirtueInvoke\nTAG.VIRTUE=<ARGN1>\n", "e_ext");
        f.Client.HandleTextCommand(0xF4, "1");
        Assert.Equal("3", f.Tag("VIRTUE"));
    }

    [Fact]
    public void TextCommandCannotMasqueradeAsSkillLockPacket()
    {
        using var f = new Fixture();
        f.Client.HandleTextCommand(0xF4, "SKILLLOCK 46 2");
        Assert.Equal(0, f.Player.GetSkillLock(SphereNet.Core.Enums.SkillType.Meditation));
    }

    [Theory]
    [InlineData(101, 100, 0, true)]
    [InlineData(98, 100, 0, false)]
    [InlineData(101, 100, 20, false)]
    public void DoorMacroSearchesInFrontAndChecksHeight(short x, short y, sbyte z, bool opens)
    {
        using var f = new Fixture();
        f.World.PlaceCharacter(f.Player, new SphereNet.Core.Types.Point3D(100, 100));
        f.Player.Direction = SphereNet.Core.Enums.Direction.East;
        var door = f.World.CreateItem();
        door.BaseId = 0x0675;
        door.ItemType = SphereNet.Core.Enums.ItemType.Door;
        f.World.PlaceItem(door, new SphereNet.Core.Types.Point3D(x, y, z));
        f.Client.HandleTextCommand(0x58, "");
        Assert.Equal(opens, door.TryGetTag("DOOR_OPEN", out var value) && value == "1");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ScriptDoorRadiusChangesActualSearch(int radius, bool opens)
    {
        using var f = new Fixture("[EVENTS e_ext]\nON=@UserExtCmd\nLOCAL.DoorAutoDist=" + radius + "\n", "e_ext");
        f.World.PlaceCharacter(f.Player, new SphereNet.Core.Types.Point3D(100, 100));
        f.Player.Direction = SphereNet.Core.Enums.Direction.East;
        var door = f.World.CreateItem();
        door.BaseId = 0x0675;
        door.ItemType = SphereNet.Core.Enums.ItemType.Door;
        f.World.PlaceItem(door, new SphereNet.Core.Types.Point3D(102, 100));
        f.Client.HandleTextCommand(0x58, "");
        Assert.Equal(opens, door.TryGetTag("DOOR_OPEN", out var value) && value == "1");
    }

    [Fact]
    public void PromptCanInvokeAPlayerVerb()
    {
        using var f = new Fixture();
        Assert.True(f.Client.TryExecuteScriptCommand(f.Player, "PROMPTCONSOLE", "NAME", null));
        f.Client.HandlePromptResponse(f.Player.Uid.Value, f.PromptId, 1, "New name");
        Assert.Equal("New name", f.Player.Name);
    }

    [Fact]
    public void MenuCanOpenAnotherMenuWithoutLosingIt()
    {
        using var f = new Fixture("[MENU m_first]\nFirst\nON=0 Next\nMENU m_second\n[MENU m_second]\nSecond\nON=0 Finish\nTAG.FINISHED=1\n");
        f.Client.TryExecuteScriptCommand(f.Player, "MENU", "m_first", null);
        f.Client.HandleMenuChoice(f.Player.Uid.Value, f.MenuId, 1, 0);
        Assert.Equal(2, TestHarness.GetQueuedPackets(f.Client.NetState).Count(p => p.Span[0] == 0x7C));
        f.Client.HandleMenuChoice(f.Player.Uid.Value, f.MenuId, 1, 0);
        Assert.Equal("1", f.Tag("FINISHED"));
    }

    [Fact]
    public void DeletedMenuSubjectIsNotUsedForAResponse()
    {
        using var f = new Fixture("[MENU m_deleted]\nQuestion\nON=0 Choose\nSRC.TAG.RAN=1\n");
        var item = f.World.CreateItem();
        f.Client.TryExecuteScriptCommand(item, "MENU", "m_deleted", null);
        ushort id = f.MenuId;
        f.World.DeleteObject(item);
        f.Client.HandleMenuChoice(f.Player.Uid.Value, id, 1, 0);
        Assert.False(f.Player.TryGetTag("RAN", out _));
    }
}
