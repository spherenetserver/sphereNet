using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Behavior matrix locking the single active-skill stroke model:
//
//   engine (ActiveSkillEngine.*)  — stateless, one call resolves SUCCESS/FAIL.
//   client (GameClient.TickPendingSkill) — wraps DELAY skills with the per-tick
//                                          @SkillStroke loop and completion.
//
// SkillDelayTests already covers single-stroke order, target-cancel and the
// initial-state movement/damage interrupts. This file closes the remaining gaps
// PARITY.md flagged: multi-stroke loop ordering, interrupt DURING the loop, and
// the engine's synchronous pre-check short-circuit. The stroke clock is wall
// time (Environment.TickCount64); tests drive it deterministically by setting the
// pending-skill timing fields directly (BeginSkillPending / SetSkillStrokeNext),
// the same seam the existing interrupt tests use — no sleeps.
public class ActiveSkillStrokeMatrixTests
{
    private const long Far = long.MaxValue / 2;
    private static readonly int HidingId = (int)SkillType.Hiding;

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient client, Character player, List<string> order) Setup(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new SphereNet.Game.Accounts.AccountManager(lf), 1401);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.SetSkill(SkillType.Hiding, 1000);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var dispatcher = new TriggerDispatcher();
        var order = new List<string>();
        void Rec(string trig, Func<TriggerArgs, string> fmt) =>
            dispatcher.RegisterCharEvent("EVENTSPLAYER", trig, (_, a) => { order.Add(fmt(a)); return TriggerResult.Default; });
        Rec("SkillStroke", a => $"stroke:{a.N1}:{a.N2}");
        Rec("SkillSuccess", a => $"success:{a.N1}");
        Rec("SkillFail", a => $"fail:{a.N1}");
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);
        return (client, player, order);
    }

    [Fact]
    public void StrokeLoop_FiresStrokesInOrder_ThenCompletes()
    {
        var world = CreateWorld();
        var (client, player, order) = Setup(world);

        // Delay far in the future so completion does not fire while strokes run.
        player.BeginSkillPending(HidingId, delayEnd: Far, strokeNext: 0, Serial.Invalid, null);

        for (int k = 1; k <= 3; k++)
        {
            player.SetSkillStrokeNext(0);   // make the next stroke due now
            client.TickPendingSkill();
            Assert.True(player.HasActiveSkillPending()); // loop still active
        }

        // Force completion: delay in the past, next stroke parked in the future so
        // no extra stroke fires on the completing tick.
        player.BeginSkillPending(HidingId, delayEnd: 1, strokeNext: Far, Serial.Invalid, null);
        client.TickPendingSkill();

        Assert.False(player.HasActiveSkillPending());
        Assert.Equal(
            [$"stroke:{HidingId}:1", $"stroke:{HidingId}:2", $"stroke:{HidingId}:3"],
            order.Take(3));
        // Completion fires exactly one SUCCESS or FAIL, after the strokes.
        Assert.Single(order.Skip(3));
        Assert.Matches($@"^(success|fail):{HidingId}$", order[^1]);
    }

    [Fact]
    public void MovementDuringStrokeLoop_AbortsAfterStrokes_NoSuccessOrFail()
    {
        var oldAbort = Character.ActiveSkillAborted;
        try
        {
            var world = CreateWorld();
            var (client, player, order) = Setup(world);

            player.BeginSkillPending(HidingId, delayEnd: Far, strokeNext: 0, Serial.Invalid, null);
            for (int k = 1; k <= 2; k++)
            {
                player.SetSkillStrokeNext(0);
                client.TickPendingSkill();
            }

            int aborted = -1;
            Character.ActiveSkillAborted = (_, id) => aborted = id;

            var movement = new MovementEngine(world);
            Assert.True(movement.TryMove(player, Direction.East, running: false, sequence: 1));

            Assert.False(player.HasActiveSkillPending());      // loop cancelled
            Assert.Equal(HidingId, aborted);                   // @SkillAbort path
            Assert.Equal(2, order.Count(o => o.StartsWith("stroke"))); // both strokes fired
            Assert.DoesNotContain(order, o => o.StartsWith("success") || o.StartsWith("fail"));
        }
        finally
        {
            Character.ActiveSkillAborted = oldAbort;
        }
    }

    [Fact]
    public void Engine_PreCheck_ShortCircuitsBeforeRoll_InOneCall()
    {
        var world = CreateWorld();
        var player = world.CreateCharacter();
        player.SetSkill(SkillType.Hiding, 1000);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var sink = new RecordingSink(player, world);

        // Carrying a light fails the Hiding pre-check synchronously: no Hidden flag,
        // no roll — the whole stage machine resolves in the single engine call.
        player.SetTag("LIGHT_CARRIED", "1");
        Assert.False(ActiveSkillEngine.Hiding(sink));
        Assert.False(player.IsStatFlag(StatFlag.Hidden));
    }

    private sealed class RecordingSink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public Random Random { get; } = new(1);
        public GameWorld World { get; } = world;
        public void SysMessage(string text) { }
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public SphereNet.Game.Objects.Items.Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(SphereNet.Game.Objects.Items.Item item, ushort amount = 1) { }
        public void DeliverItem(SphereNet.Game.Objects.Items.Item item) { }
    }
}
