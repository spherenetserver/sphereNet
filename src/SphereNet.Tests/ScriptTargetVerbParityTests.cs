using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using ExecArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// The script TARGET verb family against Source-X OV_TARGET (CObjBase.cpp:2691),
/// SetTargMode (CClientMsg.cpp:1644), OnTarg_Use_Item (CClientTarg.cpp:1667) and
/// OnTarg_Obj_Function (CClientTarg.cpp:89).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptTargetVerbParityTests : IDisposable
{
    private readonly ScriptRuntimeStack _stack = ScriptTestBootstrap.CreateRuntimeStack();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"targ-verbs-{Guid.NewGuid():N}.scp");
    private readonly GameWorld _world;
    private readonly GameClient _client;
    private readonly Character _player;
    private readonly Item _tool;
    private readonly Item _robe;

    private const string Script = """
        [EVENTS e_test_player]
        ON=@TARGON_CANCEL
        TAG.CANCEL_ARGS=<ARGS>
        TAG.CANCEL_COUNT=<EVAL <TAG0.CANCEL_COUNT> + 1>

        [TYPEDEF e_test_tool]
        ON=@TARGON_ITEM
        TAG.ITEM_HIT=<EVAL <TAG0.ITEM_HIT> + 1>
        TAG.PRV=<SRC.TARGPRV.UID>
        RETURN 1
        ON=@TARGON_GROUND
        TAG.G_N1=<ARGN1>
        TAG.G_N2=<ARGN2>
        TAG.G_N3=<ARGN3>
        RETURN 1
        ON=@TARGON_CANCEL
        TAG.ITEM_CANCEL=<EVAL <TAG0.ITEM_CANCEL> + 1>

        [FUNCTION f_test_pick]
        SRC.TAG.FUNC_RAN=<ARGS>
        """;

    public ScriptTargetVerbParityTests()
    {
        File.WriteAllText(_path, Script);
        // r_GetRef, as the server wires it: SRC.TARGPRV.x dereferences the head.
        _stack.Interpreter.ResolveObjectRef = (obj, head) => (obj as ObjBase)?.ResolveScriptRefHead(head);
        _stack.Resources.LoadResourceFile(_path);
        var logs = LoggerFactory.Create(_ => { });
        _world = TestHarness.CreateWorld();
        _client = TestHarness.CreateClient(logs, _world, new AccountManager(logs), 19711);

        _player = _world.CreateCharacter();
        _player.IsPlayer = true;
        _world.PlaceCharacter(_player, new Point3D(100, 100, 0, 0));
        _player.Events.Add(_stack.Resources.ResolveDefName("e_test_player"));
        _tool = _world.CreateItem();
        _world.PlaceItem(_tool, new Point3D(100, 101, 0, 0));
        _tool.Events.Add(_stack.Resources.ResolveDefName("e_test_tool"));
        _robe = _world.CreateItem();
        _world.PlaceItem(_robe, new Point3D(101, 100, 0, 0));

        _client.SetEngines(triggerDispatcher: _stack.Dispatcher);
        TestHarness.AttachCharacter(_client, _player);
    }

    public void Dispose()
    {
        File.Delete(_path);
        _stack.LoggerFactory.Dispose();
    }

    private bool Run(IScriptObjTarget on, string verb, string args = "")
    {
        // The engine statics are reset between the constructor and the test body
        // (ResetEngineStatics), so they are pointed at this world here.
        ObjBase.ResolveWorld = () => _world;
        ObjBase.ResolveClientConsole = _ => _client;
        return _client.TryExecuteScriptCommand(on.Obj, verb, args, new ExecArgs(_player));
    }

    private readonly record struct IScriptObjTarget(SphereNet.Core.Interfaces.IScriptObj Obj);
    private IScriptObjTarget Tool => new(_tool);
    private IScriptObjTarget Player => new(_player);

    private List<byte[]> Sent() =>
        TestHarness.GetQueuedPackets(_client.NetState).Select(p => p.Span.ToArray()).ToList();

    private string Tag(ObjBase obj, string key) => obj.TryGetTag(key, out var v) ? v ?? "" : "";

    private static bool ContainsUnicode(byte[] packet, string text)
    {
        byte[] needle = Encoding.BigEndianUnicode.GetBytes(text);
        return packet.AsSpan().IndexOf(needle) >= 0;
    }

    // C. The argument of a plain TARGET is the prompt, printed as a system line.
    [Fact]
    public void TargetPrintsItsPromptWithTheBarkPrefixTakenOff()
    {
        TestHarness.ClearQueuedPackets(_client.NetState);
        Assert.True(Run(Tool, "TARGET", "@,,1,1 Pick an empty hatchery"));

        var speech = Sent().Where(p => p[0] == 0xAE).ToList();
        Assert.Contains(speech, p => ContainsUnicode(p, "Pick an empty hatchery"));
        Assert.DoesNotContain(speech, p => ContainsUnicode(p, "@,,1,1"));
        Assert.Contains(Sent(), p => p[0] == 0x6C);
    }

    // D. A new cursor replaces the open one and fires the old one's cancel.
    [Fact]
    public void OpeningACursorOverAnOpenOneCancelsTheOldOne()
    {
        Assert.True(Run(Player, "TARGETF", "f_test_pick first"));
        uint firstId = _client.ActiveTargetCursorId;

        Assert.True(Run(Tool, "TARGET"));

        Assert.Equal("f_test_pick first", Tag(_player, "CANCEL_ARGS"));
        Assert.NotEqual(firstId, _client.ActiveTargetCursorId);
        Assert.Equal(_tool.Uid, _client.Targets.ItemUid);
        Assert.Null(_client.Targets.Function);

        // ...and the other way round: the used item hears its own cancel.
        Assert.True(Run(Player, "TARGETF", "f_test_pick second"));
        Assert.Equal("1", Tag(_tool, "ITEM_CANCEL"));
        Assert.Equal("f_test_pick", _client.Targets.Function);
    }

    // E. + F(TARGPRV). @TargOn_* get ARGN1 = the static id, ARGN2/3 = 0; TARGPRV
    // is the used item.
    [Fact]
    public void UsedItemTriggersGetTheTileIdAndTargPrvIsTheItem()
    {
        Assert.True(Run(Tool, "TARGETG"));
        _client.HandleTargetResponse(1, _client.ActiveTargetCursorId, 0, 300, 310, 5, 0x1234);
        Assert.Equal(0x1234.ToString(), Tag(_tool, "G_N1"));
        Assert.Equal("0", Tag(_tool, "G_N2"));
        Assert.Equal("0", Tag(_tool, "G_N3"));

        Assert.True(Run(Tool, "TARGET"));
        _client.HandleTargetResponse(0, _client.ActiveTargetCursorId, _robe.Uid.Value, 101, 100, 0, 0);
        Assert.True(ScriptNumber.TryParseToken(Tag(_tool, "PRV"), out long prv));
        Assert.Equal(_tool.Uid.Value, (uint)prv);
    }

    // F. The character's @Targon_Cancel belongs to TARGETF cursors only.
    [Fact]
    public void CharTargonCancelFiresForAFunctionCursorOnlyWithItsLine()
    {
        Assert.True(Run(Tool, "TARGET"));
        _client.HandleTargetResponse(0, _client.ActiveTargetCursorId, 0xFFFFFFFF, 0, 0, 0, 0);
        Assert.Equal("", Tag(_player, "CANCEL_COUNT"));
        Assert.Equal("1", Tag(_tool, "ITEM_CANCEL"));

        Assert.True(Run(Player, "TARGETF", "f_test_pick a b"));
        _client.HandleTargetResponse(0, _client.ActiveTargetCursorId, 0xFFFFFFFF, 0, 0, 0, 0);
        Assert.Equal("1", Tag(_player, "CANCEL_COUNT"));
        Assert.Equal("f_test_pick a b", Tag(_player, "CANCEL_ARGS"));
    }

    // G. TARGETF run from an item only runs the function.
    [Fact]
    public void TargetFunctionFromAnItemRunsOnlyTheFunction()
    {
        Assert.True(Run(Tool, "TARGETF", "f_test_pick hello"));
        _client.HandleTargetResponse(0, _client.ActiveTargetCursorId, _robe.Uid.Value, 101, 100, 0, 0);

        Assert.Equal("hello", Tag(_player, "FUNC_RAN"));
        Assert.Equal("", Tag(_tool, "ITEM_HIT"));
    }

    // H. The modifier letters, in any order.
    [Theory]
    [InlineData("TARGETW", false, 0, 1)]
    [InlineData("TARGETGW", false, 1, 1)]
    [InlineData("TARGETWG", false, 1, 1)]
    [InlineData("TARGETFW", true, 0, 1)]
    [InlineData("TARGETWF", true, 0, 1)]
    [InlineData("TARGETFGW", true, 1, 1)]
    [InlineData("TARGETGWF", true, 1, 1)]
    [InlineData("TARGETFG", true, 1, 0)]
    public void ModifierLettersSetTheCursorInAnyOrder(string verb, bool function, byte type, byte flags)
    {
        TestHarness.ClearQueuedPackets(_client.NetState);
        Assert.True(Run(Tool, verb, function ? "f_test_pick" : ""));

        var cursor = Sent().Last(p => p[0] == 0x6C);
        Assert.Equal(type, cursor[1]);
        Assert.Equal(flags, cursor[6]);
        Assert.Equal(function ? "f_test_pick" : null, _client.Targets.Function);
        Assert.Equal(!function, _client.Targets.ItemUid == _tool.Uid);
        Assert.Equal(type == 1, _client.Targets.AllowGround);
    }

    [Fact]
    public void TargetMWithAMultiIdRaisesThePlacementPreview()
    {
        TestHarness.ClearQueuedPackets(_client.NetState);
        Assert.True(Run(Tool, "TARGETM", "0x4001,0x21"));
        Assert.Contains(Sent(), p => p[0] == 0x99);
        Assert.Equal(_tool.Uid, _client.Targets.ItemUid);

        TestHarness.ClearQueuedPackets(_client.NetState);
        Assert.True(Run(Tool, "TARGETFM", "f_test_pick,0x4001,0"));
        Assert.Contains(Sent(), p => p[0] == 0x99);
        Assert.Equal("f_test_pick", _client.Targets.Function);
    }

    [Fact]
    public void TargetCloseIsStillItsOwnVerb()
    {
        Assert.True(Run(Tool, "TARGET"));
        Assert.True(Run(Tool, "TARGETCLOSE"));
        Assert.False(_client.Targets.CursorActive);
    }
}
