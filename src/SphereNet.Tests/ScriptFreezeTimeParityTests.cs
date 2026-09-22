using System.Reflection;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ScriptFreezeTimeParityTests
{
    private static void Advance(GameWorld world, long milliseconds)
    {
        var field = typeof(GameWorld).GetField("_lastClockUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long last = (long)field.GetValue(world)!;
        typeof(GameWorld).GetMethod("AdvanceWorldClock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(world, [last + milliseconds]);
    }

    [Theory]
    [InlineData("1.0", 10, "abort")]
    [InlineData("1.0", 10, "success")]
    [InlineData("1.0", 10, "fail")]
    [InlineData("1.0", 10, "death")]
    [InlineData("1.0", 10, "precast")]
    [InlineData("3.5", 35, "abort")]
    [InlineData("0.0", 0, "abort")]
    public void PackFreezetimeExpressionUsesSpellIdAndTenths(string freeze, int tenths, string terminal)
    {
        string path = Path.Combine(Path.GetTempPath(), $"freezetime-{Guid.NewGuid():N}.scp");
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var world = TestHarness.CreateWorld();
        world.SetWorldClockMinutes(100);
        var field = typeof(SphereNet.Server.Program).GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        var resourcesField = typeof(SphereNet.Server.Program).GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousResources = resourcesField.GetValue(null);
        var resolver = typeof(SphereNet.Server.Program).GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            field.SetValue(null, world);
            resourcesField.SetValue(null, stack.Resources);
            stack.Interpreter.ServerPropertyResolver = key => (string?)resolver.Invoke(null, [key]);
            File.WriteAllText(path, "[DEFNAME freeze]\nSPELL_4_FREEZETIME=" + freeze + "\n" + """
                [SKILL 25]
                KEY=Magery
                FLAGS=skf_magic
                ON=@Start
                LOCAL.FREEZETIME = <DDEF.SPELL_<DACTARG1>_FREEZETIME>
                IF (<LOCAL.FREEZETIME>)
                    SRC.TAG0.NOMOVETILL=<EVAL <SERV.TIME> + (<dLOCAL.FREEZETIME>)>
                ENDIF
                ON=@Success
                SRC.TAG0.NOMOVETILL=
                ON=@Fail
                SRC.TAG0.NOMOVETILL=
                ON=@Abort
                SRC.TAG0.NOMOVETILL=
                """);
            stack.Resources.LoadResourceFile(path);
            var caster = world.CreateCharacter(); caster.IsPlayer = true; caster.PrivLevel = PrivLevel.GM;
            world.PlaceCharacter(caster, new Point3D(100, 100));
            caster.MaxMana = caster.Mana = 100;
            Character.MagicFlags = 0;
            var spells = new SpellRegistry();
            spells.Register(new SpellDef { Id = SpellType.Heal, ManaCost = 0, CastTimeBase = 50, Flags = SpellFlag.Heal });
            var engine = new SpellEngine(world, spells) { TriggerDispatcher = stack.Dispatcher };
            Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
            Assert.Equal(4, caster.ActArg1);
            var movement = new MovementEngine(world) { SpellEngine = engine };
            if (tenths > 0)
            {
                Assert.True(caster.TryGetTag("NOMOVETILL", out var deadline));
                Assert.Equal(world.GameClockMs / 100 + tenths, long.Parse(deadline!));
                Assert.False(movement.TryMove(caster, Direction.East, false, 0));
                Advance(world, tenths * 100 - 1);
                Assert.False(movement.TryMove(caster, Direction.East, false, 0));
                Advance(world, tenths * 100);
            }
            Assert.True(movement.TryMove(caster, Direction.East, false, 0));
            Assert.True(caster.IsCasting);
            if (terminal == "death") caster.Kill();
            else if (terminal == "success") Assert.True(engine.CastDone(caster));
            else if (terminal == "precast")
            {
                Assert.True(engine.CompletePrecastSkill(caster));
                Assert.True(caster.IsCasting);
                Assert.False(caster.TryGetTag("NOMOVETILL", out var tag) && !string.IsNullOrEmpty(tag));
                Assert.True(engine.CastDone(caster));
            }
            else if (terminal == "fail")
            {
                caster.PrivLevel = PrivLevel.Player;
                caster.CastDifficulty = 100000;
                Character.SpellbookRequiredEnabled = false;
                Assert.False(engine.CastDone(caster));
            }
            else engine.CancelCast(caster);
            Assert.False(caster.TryGetTag("NOMOVETILL", out var remaining) && !string.IsNullOrEmpty(remaining));
        }
        finally { field.SetValue(null, previous); resourcesField.SetValue(null, previousResources); File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorldTimeIncludesFractionsAcrossMinuteBoundary(bool crossMinute)
    {
        var world = TestHarness.CreateWorld(); world.SetWorldClockMinutes(100);
        long start = world.GameClockMs;
        long elapsed = crossMinute ? world.GameMinuteLengthMs + 1234 : 1234;
        Advance(world, elapsed);
        Assert.Equal(start + elapsed, world.GameClockMs);
        var field = typeof(SphereNet.Server.Program).GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try
        {
            field.SetValue(null, world);
            var method = typeof(SphereNet.Server.Program).GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(((start + elapsed) / 100).ToString(), method.Invoke(null, ["TIME"]));
            Assert.Equal((start + elapsed).ToString(), method.Invoke(null, ["TIMEHIRES"]));
        }
        finally { field.SetValue(null, previous); }
    }

    [Fact]
    public void SaveRestoresSubMinuteWorldClock()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld(); world.SetGameClockMs(1234567);
        string directory = Path.Combine(Path.GetTempPath(), $"freeze-clock-{Guid.NewGuid():N}");
        try
        {
            new SphereNet.Persistence.Save.WorldSaver(logs).Save(world, directory);
            var restored = TestHarness.CreateWorld();
            new SphereNet.Persistence.Load.WorldLoader(logs).Load(restored, directory);
            Assert.Equal(world.GameClockMs, restored.GameClockMs);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("fail")]
    [InlineData("abort")]
    [InlineData("veto")]
    public void CastSkillStagesCarrySpellAndHonorSuccessVeto(string outcome)
    {
        var world = TestHarness.CreateWorld(); var caster = world.CreateCharacter();
        world.PlaceCharacter(caster, new Point3D(100, 100));
        caster.IsPlayer = true;
        caster.PrivLevel = outcome == "fail" ? PrivLevel.Player : PrivLevel.GM;
        caster.MaxMana = caster.Mana = 100;
        var spells = new SpellRegistry();
        spells.Register(new SpellDef { Id = SpellType.Heal, ManaCost = 10, CastTimeBase = 5, Flags = SpellFlag.Heal });
        var triggers = new TriggerDispatcher();
        var engine = new SpellEngine(world, spells) { TriggerDispatcher = triggers };
        var stages = new List<string>();
        foreach (string stage in new[] { "Start", "Success", "Fail", "Abort" })
            triggers.RegisterCharEvent("EVENTSPLAYER", "Skill" + stage, (_, args) =>
            {
                stages.Add($"{args.N1}:{caster.ActArg1}:{args.Locals?.GetInt("spell")}");
                stages.Add(stage);
                return outcome == "veto" && stage == "Success" ? TriggerResult.True : TriggerResult.Default;
            });
        caster.MaxMana = caster.Mana = 100;
        Character.SpellbookRequiredEnabled = false;
        Character.ReagentsRequiredEnabled = false;
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        if (outcome == "abort") engine.CancelCast(caster);
        else
        {
            if (outcome == "fail") caster.CastDifficulty = 100000;
            Assert.Equal(outcome == "success", engine.CastDone(caster));
        }
        Assert.All(stages.Where(s => s.Contains(':')), s => Assert.Equal("25:4:4", s));
        stages.RemoveAll(s => s.Contains(':'));
        Assert.Equal(outcome switch {
            "success" => new[] { "Start", "Success" },
            "fail" => new[] { "Start", "Fail" },
            "veto" => new[] { "Start", "Success", "Abort" },
            _ => new[] { "Start", "Abort" }
        }, stages);
        Assert.False(caster.IsCasting);
        if (outcome == "veto") Assert.Equal(100, caster.Mana);
    }
}
