using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field reports from a live pack, each a script that ran and silently did the
/// wrong thing:
///  - a bare &lt;IsDeath&gt; read under a client answered with the FUNCTION's resource
///    index, so "you cannot do this while dead" fired on the living;
///  - &lt;REF1.IsDeath&gt; / &lt;ARGO.f&gt; / &lt;UID.x.f&gt; / &lt;TOPOBJ.f&gt; never called the function
///    on the referenced object (only SRC.f did);
///  - &lt;ARGO.ISMYPET&gt; read 0 for the SRC's own pet;
///  - SRC.POISON &lt;skill&gt; was swallowed by the world-load POISON setter;
///  - a DIALOG in an item's @DClick never reached the clicker's screen.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptReferenceAndClientContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_ref_" + Guid.NewGuid().ToString("N"));
    private readonly ILoggerFactory _logs = LoggerFactory.Create(_ => { });

    public void Dispose()
    {
        _logs.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed record Bench(ScriptRuntimeStack Stack, GameWorld World, GameClient Client, Character Player);

    private static readonly string[] Script =
    [
        "[DEFNAME probe_flags]", "statf_dead 02", "",
        "[CHARDEF 0190]", "DEFNAME=c_man", "NAME=Man", "",
        "[FUNCTION f_isdeath]",
        "IF (<FLAGS>&STATF_DEAD)", "\tRETURN 1", "ELSEIF (<HITS> <= 0)", "\tRETURN 1", "ENDIF", "RETURN 0", "",
        "[FUNCTION f_whoami]", "RETURN <NAME>", "",
        "[ITEMDEF i_probe_dialog_deed]", "ID=014EF", "TYPE=T_SCRIPT", "NAME=Probe Deed", "",
        "ON=@DClick", "DIALOG d_probe_ref", "RETURN 1", "",
        "[DIALOG d_probe_ref]", "0,0", "DTEXT 1 1 0 Probe", "",
        "[DIALOG d_probe_ref BUTTON]", "ON=0", "",
    ];

    private Bench Build()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "probe.scp");
        File.WriteAllLines(file, Script);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.ScpBaseDir = _dir;
        stack.Resources.LoadResourceFile(file);
        new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();
        stack.Interpreter.FunctionLookup = stack.Runner.HasFunction;

        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        stack.Interpreter.ServerPropertyResolver = p => (string?)resolve.Invoke(null, [p]);
        typeof(SphereNet.Server.Program)
            .GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, stack.Resources);

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        typeof(SphereNet.Server.Program)
            .GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, world);
        // The host's r_GetRef bridge, as Program wires it.
        stack.Interpreter.ResolveObjectRef = (obj, head) =>
            head.StartsWith("UID.", StringComparison.OrdinalIgnoreCase)
                ? (uint.TryParse(head[4..], System.Globalization.NumberStyles.HexNumber, null, out uint u)
                    ? world.FindObject(new Serial(u)) : null)
                : (obj as ObjBase)?.ResolveScriptRefHead(head);

        var client = TestHarness.CreateClient(_logs, world, new AccountManager(_logs), 19541);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = 0x190;
        player.Name = "Living";
        player.PrivLevel = PrivLevel.Owner;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        player.TrySetProperty("STR", "80");
        player.TrySetProperty("HITS", "80");
        TestHarness.AttachCharacter(client, player);
        client.SetEngines(commands: new CommandHandler { Resources = stack.Resources },
            triggerDispatcher: stack.Dispatcher);
        ObjBase.ResolveClientConsole = ch => ch == player ? client : null;
        return new Bench(stack, world, client, player);
    }

    private static string Read(Bench b, string expr, ScriptKey[]? before = null, TriggerArgs? args = null)
    {
        var lines = new List<ScriptKey>(before ?? []) { new("TAG.OUT", expr) };
        b.Stack.Interpreter.Execute(lines, b.Player, b.Client,
            args ?? new TriggerArgs { Source = b.Player }, new ScriptScope());
        b.Player.TryGetProperty("TAG.OUT", out string v);
        return v;
    }

    private static Character Dead(Bench b)
    {
        var dead = b.World.CreateCharacter();
        dead.BodyId = 0x190;
        dead.Name = "Corpse";
        b.World.PlaceCharacter(dead, new Point3D(101, 100, 0, 0));
        dead.SetStatFlag(StatFlag.Dead);
        dead.TrySetProperty("HITS", "0");
        return dead;
    }

    [Fact]
    public void ABareFunctionUnderAClientIsCalledNotReadAsItsResourceIndex()
    {
        var b = Build();
        Assert.Equal("0", Read(b, "<f_isdeath>"));
        Assert.Equal("0", Read(b, "<SRC.f_isdeath>"));
    }

    [Fact]
    public void AReferencedFunctionRunsOnTheReferencedObject()
    {
        var b = Build();
        var dead = Dead(b);
        string uid = $"0{dead.Uid.Value:X}";
        var args = new TriggerArgs { Source = b.Player, Object1 = dead };

        Assert.Equal("Corpse", Read(b, "<REF1.f_whoami>", [new("REF1", uid)], args));
        Assert.Equal("Corpse", Read(b, "<ARGO.f_whoami>", null, args));
        Assert.Equal("Corpse", Read(b, $"<UID.{uid}.f_whoami>", null, args));
        Assert.Equal("1", Read(b, "<REF1.f_isdeath>", [new("REF1", uid)], args));
        Assert.Equal("1", Read(b, "<ARGO.f_isdeath>", null, args));
        // The caller's own reads are untouched.
        Assert.Equal("Living", Read(b, "<f_whoami>", null, args));
        Assert.Equal("0", Read(b, "<f_isdeath>", null, args));
    }

    [Fact]
    public void TopObjReachesTheCharacterCarryingTheItem()
    {
        var b = Build();
        var dead = Dead(b);
        var pack = b.World.CreateItem();
        dead.Equip(pack, Layer.Pack);
        var inPack = b.World.CreateItem();
        pack.AddItem(inPack);

        b.Stack.Interpreter.Execute([new ScriptKey("TAG.OUT", "<TOPOBJ.f_isdeath>")], inPack, b.Client,
            new TriggerArgs { Source = b.Player }, new ScriptScope());
        inPack.TryGetProperty("TAG.OUT", out string v);
        Assert.Equal("1", v);
    }

    [Fact]
    public void ArgoIsMyPetAnswersAboutSrc()
    {
        var b = Build();
        var pet = b.World.CreateCharacter();
        pet.BodyId = 0xC8;
        b.World.PlaceCharacter(pet, new Point3D(100, 101, 0, 0));
        var stranger = b.World.CreateCharacter();
        stranger.BodyId = 0xC8;
        b.World.PlaceCharacter(stranger, new Point3D(100, 102, 0, 0));
        Assert.True(pet.TryExecuteCommand("MAKEMYPET", $"0{b.Player.Uid.Value:X}", null!));

        // A player (not in GM mode) asking about its own pet and a wild one.
        b.Player.PrivLevel = PrivLevel.Player;
        Assert.Equal("1", Read(b, "<ARGO.ISMYPET>", null, new TriggerArgs { Source = b.Player, Object1 = pet }));
        Assert.Equal("0", Read(b, "<ARGO.ISMYPET>", null, new TriggerArgs { Source = b.Player, Object1 = stranger }));
    }

    [Fact]
    public void SrcPoisonIsTheVerbNotTheSaveSetter()
    {
        var b = Build();
        var npc = b.World.CreateCharacter();
        b.World.PlaceCharacter(npc, new Point3D(100, 101, 0, 0));

        b.Stack.Interpreter.Execute([new ScriptKey("SRC.POISON", "1000")], npc, b.Client,
            new TriggerArgs { Source = b.Player }, new ScriptScope());

        Assert.True(b.Player.IsStatFlag(StatFlag.Poisoned));
        Assert.InRange(b.Player.PoisonLevel, (byte)4, (byte)5);   // deadly, or lethal 1 in 10
    }

    [Fact]
    public void ADialogInAnItemsDClickOpensOnTheClickersClient()
    {
        var b = Build();
        var rid = b.Stack.Resources.ResolveDefName("i_probe_dialog_deed");
        Assert.True(rid.IsValid);
        var pack = b.World.CreateItem();
        b.Player.Equip(pack, Layer.Pack);
        var deed = b.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(deed, rid.Index));
        pack.AddItem(deed);

        b.Client.HandleDoubleClick(deed.Uid.Value);

        Assert.True(b.Client.Gumps.OpenScriptDialogs.ContainsKey("d_probe_ref"));
    }

    [Fact]
    public void LastEventWalkIsOnTheTimeHiresClock()
    {
        var b = Build();
        b.World.SetGameClockMs(500_000);
        b.Player.LastMoveTick = Environment.TickCount64;
        long walk = long.Parse(Read(b, "<SRC.LASTEVENTWALK>"));
        Assert.InRange(walk, 499_000, 500_000);
    }
}
