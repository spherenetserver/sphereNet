using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A targeted skill clicked on the skill list starts only after its target is
/// picked (Source-X CClient::Event_Skill_Use, CClientEvent.cpp:595-724, then
/// CClient::OnTarg_Skill / OnTarg_Skill_Provoke / OnTarg_Skill_Poison,
/// CClientTarg.cpp:1333-1429, each calling CChar::Skill_Start with ACT already
/// set): the click shows PROMPT_MSG and a cursor and runs no @PreStart / @Start;
/// the pick sets ACT, then @SkillPreStart / @PreStart / @SkillStart / @Start run
/// exactly once, and RETURN 1 there means the skill never starts. The 0xBF 0x2E
/// targeted-skill packet sets ACT and calls Skill_Start directly
/// (receive.cpp:3284-3315).
///
/// The reported shape is Sphere 56T custom-version compatibility: an item
/// identification @Start that refuses anything not carried
/// (<c>if (&lt;act.topobj.uid&gt; != &lt;src.uid&gt;) ... return 1</c>) refused the
/// skill on the click itself, before any cursor, because the stage ran with no ACT.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SkillTargetStartOrderTests : IDisposable
{
    private const string Script = """
        [SKILL 1]
        DEFNAME=Skill_Anatomy
        KEY=Anatomy
        PROMPT_MSG=Whom shall I examine?
        DELAY=1.0,2.0
        RANGE=3

        ON=@PreStart
        TAG.PRESTARTS=<EVAL <TAG0.PRESTARTS>+1>

        ON=@Start
        TAG.STARTS=<EVAL <TAG0.STARTS>+1>
        TAG.START_ACT=<ACT.UID>

        [SKILL 3]
        DEFNAME=Skill_Appraise
        KEY=ItemID
        PROMPT_MSG=@1153,,1 What do you wish to appraise?
        DELAY=2.0,3.0
        RANGE=3

        ON=@PreStart
        TAG.PRESTARTS=<EVAL <TAG0.PRESTARTS>+1>

        ON=@Start
        TAG.STARTS=<EVAL <TAG0.STARTS>+1>
        actdiff 0
        if (<act.topobj.uid> != <src.uid>)
            TAG.REFUSED=<EVAL <TAG0.REFUSED>+1>
            return 1
        endif
        TAG.START_ACT=<ACT.UID>

        [SKILL 22]
        DEFNAME=Skill_Provocation
        KEY=Provocation
        PROMPT_MSG=Whom do you wish to incite?
        DELAY=1.0

        ON=@Start
        TAG.STARTS=<EVAL <TAG0.STARTS>+1>
        TAG.START_ACT=<ACT.UID>
        TAG.START_ACTPRV=<ACTPRV>

        [SKILL 30]
        DEFNAME=Skill_Poisoning
        KEY=Poisoning
        PROMPT_MSG=To what do you wish to apply the poison?
        DELAY=1.0

        ON=@Start
        TAG.STARTS=<EVAL <TAG0.STARTS>+1>
        TAG.START_ACT=<ACT.UID>
        TAG.START_ACTPRV=<ACTPRV>
        """;

    private readonly string _scriptPath;
    private readonly ITestOutputHelper _out;

    public SkillTargetStartOrderTests(ITestOutputHelper output)
    {
        _out = output;
        _scriptPath = Path.Combine(Path.GetTempPath(), $"spherenet_skilltarg_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Script);
    }

    public void Dispose()
    {
        try { File.Delete(_scriptPath); } catch { }
    }

    private sealed record Bench(GameWorld World, GameClient Client, Character Player, Item Pack,
        List<string> CharStages);

    private static Bench Build(string scriptPath)
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(scriptPath) ?? ""
        };
        resources.LoadResourceFile(scriptPath);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

        var stages = new List<string>();
        foreach (string trig in new[] { "SkillSelect", "SkillPreStart", "SkillStart" })
        {
            string name = trig;
            dispatcher.RegisterCharEvent("EVENTSPLAYER", trig, (ch, _) =>
            {
                stages.Add($"{name}:{((Character)ch).Act.Value:X}");
                return TriggerResult.Default;
            });
        }

        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7811);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.Player;
        player.Str = 100;
        player.MaxHits = player.Hits = 100;
        foreach (var s in new[] { SkillType.ItemId, SkillType.Anatomy, SkillType.Provocation,
                     SkillType.Poisoning, SkillType.Musicianship })
            player.SetSkill(s, 1000);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        player.Equip(pack, Layer.Pack);
        player.Backpack = pack;

        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);
        return new Bench(world, client, player, pack, stages);
    }

    private static Item MakeItem(GameWorld world, ushort baseId)
    {
        var item = world.CreateItem();
        item.BaseId = baseId;
        item.ItemType = ItemType.Normal;
        return item;
    }

    private static Character MakeNpc(GameWorld world, Point3D at)
    {
        var npc = world.CreateCharacter();
        npc.BodyId = 0x00C9;
        npc.MaxHits = npc.Hits = 50;
        world.PlaceCharacter(npc, at);
        return npc;
    }

    private static void Pick(GameClient client, Serial uid, Point3D at) =>
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, uid.Value,
            (short)at.X, (short)at.Y, (sbyte)at.Z, 0);

    private static uint TagUid(Character ch, string tag)
    {
        Assert.True(ch.TryGetTag(tag, out string? raw) && raw != null, tag);
        Assert.True(ScriptNumber.TryParseToken(raw!, out long value), raw);
        return (uint)value;
    }

    private static int TagInt(Character ch, string tag) =>
        ch.TryGetTag(tag, out string? raw) && ScriptNumber.TryParseInt(raw, out int v) ? v : 0;

    [Fact]
    public void ItemId_ClickOpensTheCursorWithNoStage_StartRunsOnThePickWithActSet()
    {
        var b = Build(_scriptPath);
        var gem = MakeItem(b.World, 0x0F26);
        b.Pack.AddItem(gem);

        b.Client.HandleUseSkill((int)SkillType.ItemId);

        // The click is a prompt and a cursor - nothing has run yet, nothing refused.
        Assert.True(b.Client.HasPendingTarget);
        Assert.Equal(0, TagInt(b.Player, "PRESTARTS"));
        Assert.Equal(0, TagInt(b.Player, "STARTS"));
        Assert.Equal(0, TagInt(b.Player, "REFUSED"));
        Assert.DoesNotContain(b.CharStages, s => s.StartsWith("SkillPreStart") || s.StartsWith("SkillStart"));
        Assert.Contains(b.CharStages, s => s.StartsWith("SkillSelect"));

        Pick(b.Client, gem.Uid, b.Player.Position);

        // One pass through each stage, with ACT the picked item; the skill is running.
        Assert.Equal(1, TagInt(b.Player, "PRESTARTS"));
        Assert.Equal(1, TagInt(b.Player, "STARTS"));
        Assert.Equal(0, TagInt(b.Player, "REFUSED"));
        Assert.Equal(gem.Uid.Value, TagUid(b.Player, "START_ACT"));
        Assert.Equal(gem.Uid, b.Player.Act);
        Assert.Equal(new[] { $"SkillPreStart:{gem.Uid.Value:X}", $"SkillStart:{gem.Uid.Value:X}" },
            b.CharStages.Where(s => !s.StartsWith("SkillSelect")).ToArray());
        Assert.True(b.Player.HasActiveSkillPending());
        Assert.Equal((int)SkillType.ItemId, b.Player.SkillPendingId);
    }

    [Fact]
    public void ItemId_StartRefusingAGroundItem_EndsTheSkillAfterThePick()
    {
        var b = Build(_scriptPath);
        var onGround = MakeItem(b.World, 0x0F26);
        var spot = new Point3D(101, 100, 0, 0);
        b.World.PlaceItem(onGround, spot);

        b.Client.HandleUseSkill((int)SkillType.ItemId);
        Assert.True(b.Client.HasPendingTarget);
        Assert.Equal(0, TagInt(b.Player, "REFUSED"));

        Pick(b.Client, onGround.Uid, spot);

        Assert.Equal(1, TagInt(b.Player, "STARTS"));
        Assert.Equal(1, TagInt(b.Player, "REFUSED"));
        Assert.False(b.Player.HasActiveSkillPending());
        Assert.False(b.Player.TryGetTag("START_ACT", out _));
    }

    [Fact]
    public void Anatomy_StartSeesThePickedCreatureAsAct()
    {
        var b = Build(_scriptPath);
        var npc = MakeNpc(b.World, new Point3D(101, 100, 0, 0));

        b.Client.HandleUseSkill((int)SkillType.Anatomy);
        Assert.True(b.Client.HasPendingTarget);
        Assert.Equal(0, TagInt(b.Player, "STARTS"));

        Pick(b.Client, npc.Uid, npc.Position);

        Assert.Equal(1, TagInt(b.Player, "PRESTARTS"));
        Assert.Equal(1, TagInt(b.Player, "STARTS"));
        Assert.Equal(npc.Uid.Value, TagUid(b.Player, "START_ACT"));
        Assert.True(b.Player.HasActiveSkillPending());
    }

    [Fact]
    public void Anatomy_PickOnTheGround_StartsNothing()
    {
        var b = Build(_scriptPath);

        b.Client.HandleUseSkill((int)SkillType.Anatomy);
        b.Client.HandleTargetResponse(1, b.Client.ActiveTargetCursorId, 0, 102, 100, 0, 0);

        // OnTarg_Skill gives up on a pick naming no object (CClientTarg.cpp:1340).
        Assert.Equal(0, TagInt(b.Player, "STARTS"));
        Assert.False(b.Player.HasActiveSkillPending());
    }

    [Fact]
    public void TargetedSkillPacket_SetsActAndStartsWithNoCursor()
    {
        var b = Build(_scriptPath);
        var gem = MakeItem(b.World, 0x0F26);
        b.Pack.AddItem(gem);

        uint uid = gem.Uid.Value;
        b.Client.HandleExtendedCommand(0x002E, new byte[]
        {
            0, (byte)SkillType.ItemId,
            (byte)(uid >> 24), (byte)(uid >> 16), (byte)(uid >> 8), (byte)uid,
        });

        Assert.False(b.Client.HasPendingTarget);
        Assert.Equal(1, TagInt(b.Player, "STARTS"));
        Assert.Equal(uid, TagUid(b.Player, "START_ACT"));
        // No @SkillSelect on this path (receive.cpp:3307-3308).
        Assert.DoesNotContain(b.CharStages, s => s.StartsWith("SkillSelect"));
    }

    [Fact]
    public void Provocation_StartsAfterTheSecondPick_ActIsTheTargetActPrvTheProvoked()
    {
        var b = Build(_scriptPath);
        var provoked = MakeNpc(b.World, new Point3D(101, 100, 0, 0));
        var victim = MakeNpc(b.World, new Point3D(102, 100, 0, 0));

        b.Client.HandleUseSkill((int)SkillType.Provocation);
        Assert.True(b.Client.HasPendingTarget);
        Assert.Equal(0, TagInt(b.Player, "STARTS"));

        Pick(b.Client, provoked.Uid, provoked.Position);
        // The second cursor (DEFMSG_PROVOKE_SELECT); still nothing started.
        Assert.True(b.Client.HasPendingTarget);
        Assert.Equal(0, TagInt(b.Player, "STARTS"));

        Pick(b.Client, victim.Uid, victim.Position);

        Assert.Equal(1, TagInt(b.Player, "STARTS"));
        Assert.Equal(victim.Uid.Value, TagUid(b.Player, "START_ACT"));
        Assert.Equal(provoked.Uid.Value, TagUid(b.Player, "START_ACTPRV"));
    }

    [Fact]
    public void Provocation_FirstPickNotACreature_IsRefusedWithoutAStage()
    {
        var b = Build(_scriptPath);
        var rock = MakeItem(b.World, 0x1363);
        var spot = new Point3D(101, 100, 0, 0);
        b.World.PlaceItem(rock, spot);

        b.Client.HandleUseSkill((int)SkillType.Provocation);
        Pick(b.Client, rock.Uid, spot);

        Assert.False(b.Client.HasPendingTarget);
        Assert.Equal(0, TagInt(b.Player, "STARTS"));
        Assert.False(b.Player.HasActiveSkillPending());
    }

    [Fact]
    public void Poisoning_FirstPickIsTheThingToPoison_SecondThePoison()
    {
        var b = Build(_scriptPath);
        var blade = MakeItem(b.World, 0x0F51);
        blade.ItemType = ItemType.WeaponSword;
        b.Pack.AddItem(blade);
        var potion = MakeItem(b.World, 0x0F0A);
        potion.ItemType = ItemType.Potion;
        b.Pack.AddItem(potion);

        b.Client.HandleUseSkill((int)SkillType.Poisoning);
        Pick(b.Client, blade.Uid, b.Player.Position);
        Assert.True(b.Client.HasPendingTarget);
        Assert.Equal(0, TagInt(b.Player, "STARTS"));

        Pick(b.Client, potion.Uid, b.Player.Position);

        // Skill_Poisoning: ACTPRV = poison this, ACT = with this poison
        // (CCharSkill.cpp:2162-2163).
        Assert.Equal(1, TagInt(b.Player, "STARTS"));
        Assert.Equal(potion.Uid.Value, TagUid(b.Player, "START_ACT"));
        Assert.Equal(blade.Uid.Value, TagUid(b.Player, "START_ACTPRV"));
    }

    [Fact]
    public void UntargetedSkill_SendsNoGenericUseLine()
    {
        var b = Build(_scriptPath);
        var state = b.Client.NetState;
        TestHarness.ClearQueuedPackets(state);

        // Camping reaches the generic tail of HandleUseSkill; upstream never adds a
        // closing "You use <skill>." / "You fail to use <skill>." line.
        b.Client.HandleUseSkill((int)SkillType.Camping);

        foreach (var packet in TestHarness.GetQueuedPackets(state))
        {
            string text = System.Text.Encoding.BigEndianUnicode.GetString(packet.Span.ToArray());
            Assert.DoesNotContain("You use ", text);
            Assert.DoesNotContain("You fail to use ", text);
        }
    }

    [Fact]
    public void Sphere56TItemIdentification_PromptsFirstAndChecksThePackOnThePick()
    {
        string scripts = Environment.GetEnvironmentVariable("SPHERENET_56T_SCRIPTS") ?? @"C:\56T\scripts";
        string skillFile = Path.Combine(scripts, "sphere_skills.scp");
        if (Gate.Missing(_out, "external script pack", !File.Exists(skillFile))) return;

        var b = Build(skillFile);
        var gem = MakeItem(b.World, 0x0F26);
        b.Pack.AddItem(gem);
        var onGround = MakeItem(b.World, 0x0F26);
        var spot = new Point3D(101, 100, 0, 0);
        b.World.PlaceItem(onGround, spot);

        // The click: a cursor, and the pack's @Start has not run.
        b.Client.HandleUseSkill((int)SkillType.ItemId);
        Assert.True(b.Client.HasPendingTarget);
        Assert.DoesNotContain(b.CharStages, s => s.StartsWith("SkillStart"));

        // A carried item passes the script's check and the skill runs.
        Pick(b.Client, gem.Uid, b.Player.Position);
        Assert.Contains($"SkillStart:{gem.Uid.Value:X}", b.CharStages);
        Assert.True(b.Player.HasActiveSkillPending());

        b.Player.ClearActiveSkillPending();
        b.CharStages.Clear();

        // One on the ground: @Start runs with ACT on it and its RETURN 1 ends the skill.
        b.Client.HandleUseSkill((int)SkillType.ItemId);
        Assert.True(b.Client.HasPendingTarget);
        Pick(b.Client, onGround.Uid, spot);
        Assert.Contains($"SkillStart:{onGround.Uid.Value:X}", b.CharStages);
        Assert.False(b.Player.HasActiveSkillPending());
    }
}
