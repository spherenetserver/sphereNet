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

namespace SphereNet.Tests;

/// <summary>
/// Script-surface locals added upstream in Source-X:
///   * @SpellEffect / [SPELL] @Effect LOCAL.IsSpellReflected (read-only) and
///     LOCAL.BypassMagicReflection (&gt;0 skips the reflection check) -
///     CChar::OnSpellEffect, CCharSpell.cpp:3712, :3778-3779, :3810, :3851;
///   * @SpellFail / [SPELL] @Fail LOCAL.Sound (fizzle by default, 0 = silent) -
///     Spell_CastFail, CCharSpell.cpp:3401-3434;
///   * @SkillStart / [SKILL] @Start LOCAL.Effect (m_Act_Effect worked out before the
///     triggers and read back) - Skill_Start, CCharSkill.cpp:4456-4477, :4526;
///   * a summon that dies on placement goes no further - Spell_Summon_Place,
///     CCharSpell.cpp:354-370.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellSkillScriptLocalsUpstreamTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (var path in _temp)
            try { File.Delete(path); } catch { }
    }

    private ScriptRuntimeStack Scripts(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sx-locals-{Guid.NewGuid():N}.scp");
        _temp.Add(path);
        File.WriteAllText(path, text);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(path);
        stack.Dispatcher.BuildUsedTriggerCache();
        return stack;
    }

    private static long Tag(Character ch, string name)
    {
        Assert.True(ch.TryGetTag(name, out string? raw) && raw != null, name);
        return TagValueTestExtensions.SphereNum(raw) ?? throw new InvalidOperationException(raw);
    }

    // ------------------------------------------------------------ reflection

    private static (GameWorld World, SpellEngine Engine, Character Caster, Character Target) ReflectStack()
    {
        var world = TestHarness.CreateWorld();
        Character.MagicFlags = 0;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Fireball,
            Name = "Fireball",
            Flags = SpellFlag.Damage,
            EffectBase = 40,
            EffectScale = 40,
        });
        var caster = world.CreateCharacter();
        caster.Hits = caster.MaxHits = 200;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 200;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (world, new SpellEngine(world, registry), caster, target);
    }

    private const string ReflectProbe = """
        [EVENTS e_reflect_probe]
        ON=@SpellEffect
        TAG.SEEN=<EVAL <TAG0.SEEN>+1>
        TAG.REFLECTED=<LOCAL.IsSpellReflected>
        TAG.BYPASS=<LOCAL.BypassMagicReflection>
        IF (<TAG0.DOBYPASS>)
            LOCAL.BypassMagicReflection=1
        ENDIF
        """;

    [Fact]
    public void ReflectedCopy_SeesIsSpellReflected_OnTheCaster()
    {
        var (_, engine, caster, target) = ReflectStack();
        var stack = Scripts(ReflectProbe);
        engine.TriggerDispatcher = stack.Dispatcher;
        var probe = stack.Resources.ResolveDefName("e_reflect_probe");
        caster.Events.Add(probe);
        target.Events.Add(probe);
        target.SetStatFlag(StatFlag.Reflection);

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);

        Assert.Equal(0, Tag(target, "REFLECTED"));
        Assert.Equal(0, Tag(target, "BYPASS"));
        Assert.Equal(1, Tag(caster, "REFLECTED"));
        Assert.Equal(200, target.Hits);
        Assert.True(caster.Hits < 200);
        Assert.False(target.IsStatFlag(StatFlag.Reflection));
    }

    [Fact]
    public void SpellEffectBypassMagicReflection_LandsOnTheTarget_AndKeepsItsReflection()
    {
        var (_, engine, caster, target) = ReflectStack();
        var stack = Scripts(ReflectProbe);
        engine.TriggerDispatcher = stack.Dispatcher;
        var probe = stack.Resources.ResolveDefName("e_reflect_probe");
        caster.Events.Add(probe);
        target.Events.Add(probe);
        target.SetTag("DOBYPASS", "1");
        target.SetStatFlag(StatFlag.Reflection);

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);

        Assert.True(target.Hits < 200);
        Assert.Equal(200, caster.Hits);
        Assert.True(target.IsStatFlag(StatFlag.Reflection));
        Assert.False(caster.TryGetTag("SEEN", out _));
    }

    [Fact]
    public void SpellStageBypassMagicReflection_AlsoSkipsTheCheck()
    {
        var (_, engine, caster, target) = ReflectStack();
        var stack = Scripts($"[SPELL {(int)SpellType.Fireball}]\nON=@Effect\nLOCAL.BypassMagicReflection=1\n");
        engine.TriggerDispatcher = stack.Dispatcher;
        target.SetStatFlag(StatFlag.Reflection);

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);

        Assert.True(target.Hits < 200);
        Assert.Equal(200, caster.Hits);
        Assert.True(target.IsStatFlag(StatFlag.Reflection));
    }

    // ------------------------------------------------------------ fail sound

    private static (SpellEngine Engine, Character Caster, Character Target, List<ushort> Sounds)
        FailStack(ScriptRuntimeStack? stack)
    {
        Character.SpellbookRequiredEnabled = false;
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Heal, Name = "Heal", Flags = SpellFlag.TargChar | SpellFlag.Heal,
            ManaCost = 0, CastTimeBase = 1, EffectBase = 10, EffectScale = 10,
        });
        var engine = new SpellEngine(world, registry) { TriggerDispatcher = stack?.Dispatcher };
        var sounds = new List<ushort>();
        engine.OnPlaySound = (_, s) => sounds.Add(s);
        Character.BroadcastNearby = (_, _, _, _) => { };

        var caster = world.CreateCharacter();
        caster.Str = 100; caster.MaxHits = caster.Hits = 100;
        caster.Int = 100; caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.MaxHits = target.Hits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (engine, caster, target, sounds);
    }

    private static void Fizzle(SpellEngine engine, Character caster, Character target)
    {
        Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
        target.SetStatFlag(StatFlag.Dead);
        caster.CastSkillSucceeded = true;
        Assert.False(engine.CastDone(caster));
    }

    [Fact]
    public void FailSound_DefaultsToFizzle_AndTheTriggerSeesIt()
    {
        var stack = Scripts("[EVENTS e_fail_probe]\nON=@SpellFail\nTAG.FAILSOUND=<LOCAL.Sound>\n");
        var (engine, caster, target, sounds) = FailStack(stack);
        caster.Events.Add(stack.Resources.ResolveDefName("e_fail_probe"));

        Fizzle(engine, caster, target);

        Assert.Equal(0x5C, Tag(caster, "FAILSOUND"));
        Assert.Equal(new ushort[] { 0x5C }, sounds);
    }

    [Fact]
    public void FailSound_IsReadBack_AndZeroIsSilent()
    {
        var stack = Scripts(
            "[EVENTS e_fail_sound]\nON=@SpellFail\nLOCAL.Sound=<TAG0.FAILSOUND>\n");
        var (engine, caster, target, sounds) = FailStack(stack);
        caster.Events.Add(stack.Resources.ResolveDefName("e_fail_sound"));

        caster.SetTag("FAILSOUND", "0x1F4");
        Fizzle(engine, caster, target);
        Assert.Equal(new ushort[] { 0x1F4 }, sounds);

        sounds.Clear();
        target.ClearStatFlag(StatFlag.Dead);
        caster.SetTag("FAILSOUND", "0");
        Fizzle(engine, caster, target);
        Assert.Empty(sounds);
    }

    [Fact]
    public void FailSound_SpellStageLocalIsReadBack()
    {
        var stack = Scripts($"[SPELL {(int)SpellType.Heal}]\nON=@Fail\nLOCAL.Sound=0\n");
        var (engine, caster, target, sounds) = FailStack(stack);

        Fizzle(engine, caster, target);

        Assert.Empty(sounds);
    }

    // ------------------------------------------------------------ skill start effect

    private const string SkillScript = """
        [SKILL 1]
        DEFNAME=Skill_Anatomy
        KEY=Anatomy
        PROMPT_MSG=Whom shall I examine?
        DELAY=10.0
        EFFECT=10,50
        RANGE=3

        ON=@Start
        TAG.START_LOCAL=<LOCAL.Effect>
        TAG.START_ACT=<ACTIONEFFECT>
        IF (<TAG0.SETEFFECT>)
            ACTIONEFFECT=5
            LOCAL.Effect=<TAG.SETEFFECT>
        ENDIF
        """;

    private (GameClient Client, Character Player, Character Npc) SkillBench()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sx-skilleffect-{Guid.NewGuid():N}.scp");
        _temp.Add(path);
        File.WriteAllText(path, SkillScript);
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(path) ?? ""
        };
        resources.LoadResourceFile(path);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7812);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.Player;
        player.Str = 100;
        player.MaxHits = player.Hits = 100;
        player.SetSkill(SkillType.Anatomy, 1000);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);

        var npc = world.CreateCharacter();
        npc.BodyId = 0x00C9;
        npc.MaxHits = npc.Hits = 50;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
        return (client, player, npc);
    }

    private static void UseAnatomyOn(GameClient client, Character npc)
    {
        client.HandleUseSkill((int)SkillType.Anatomy);
        Assert.True(client.HasPendingTarget);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, npc.Uid.Value,
            (short)npc.X, (short)npc.Y, (sbyte)npc.Z, 0);
    }

    [Fact]
    public void SkillStart_SeesTheEffectCurve_InLocalAndActionEffect_AndTheSkillKeepsIt()
    {
        var (client, player, npc) = SkillBench();

        UseAnatomyOn(client, npc);

        // EFFECT=10,50 at 100.0 skill is 50, worked out before @Start.
        Assert.Equal(50, Tag(player, "START_LOCAL"));
        Assert.Equal(50, Tag(player, "START_ACT"));
        Assert.True(player.HasActiveSkillPending());
        // Scheduling the timer does not wipe what Skill_Start settled on.
        Assert.Equal(50, player.ActionEffect);
    }

    [Fact]
    public void SkillStart_LocalEffectIsReadBack_OverAnActionEffectWrite()
    {
        var (client, player, npc) = SkillBench();
        player.SetTag("SETEFFECT", "77");

        UseAnatomyOn(client, npc);

        Assert.True(player.HasActiveSkillPending());
        Assert.Equal(77, player.ActionEffect);
    }

    [Fact]
    public void StartActionEffect_IsMinusOneWithoutACurve()
    {
        var (_, player, _) = SkillBench();
        Assert.Equal(-1, SkillEngine.GetStartActionEffect(player, SkillType.Healing));
        Assert.Equal(50, SkillEngine.GetStartActionEffect(player, SkillType.Anatomy));
    }

    // ------------------------------------------------------------ summon dies on placement

    private static (GameWorld World, SpellEngine Engine, Character Caster) SummonStack()
    {
        Character.SpellbookRequiredEnabled = false;
        Character.ReagentsRequiredEnabled = false;
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.SummonDaemon, Name = "Summon Daemon",
            Flags = SpellFlag.Summon | SpellFlag.TargXYZ,
            ManaCost = 0, CastTimeBase = 1, DurationBase = 600, DurationScale = 600,
        });
        var engine = new SpellEngine(world, registry);
        var caster = world.CreateCharacter();
        caster.Str = 100; caster.MaxHits = caster.Hits = 100;
        caster.Int = 100; caster.MaxMana = caster.Mana = 100;
        caster.MaxFollower = 5;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        // CharacterPlaced is raised only while somebody is online to see it.
        world.AddOnlinePlayer(caster);
        return (world, engine, caster);
    }

    private static void CastSummon(SpellEngine engine, Character caster)
    {
        Assert.True(engine.CastStart(caster, SpellType.SummonDaemon, Serial.Invalid,
            new Point3D(102, 102, 0, 0)) > 0);
        caster.CastSkillSucceeded = true;
        engine.CastDone(caster);
    }

    [Fact]
    public void SummonKilledOnPlacement_IsNotOwnedOrKept()
    {
        var (world, engine, caster) = SummonStack();
        GameClient.ServerOptionFlags |= OptionFlags.PetSlots;
        Character? placed = null;
        world.CharacterPlaced += ch =>
        {
            if (ch == caster) return;
            placed = ch;
            // A damaging field or trap under the spot: the summon arrives dead.
            ch.Hits = 0;
            ch.SetStatFlag(StatFlag.Dead);
            // Placement comes before the owner (Spell_Summon_Place :352-369).
            Assert.False(ch.OwnerSerial.IsValid);
        };

        CastSummon(engine, caster);

        Assert.NotNull(placed);
        Assert.True(placed!.IsDeleted);
        Assert.Equal(0, caster.CurFollower);
        Assert.DoesNotContain(world.GetAllObjects().OfType<Character>(),
            c => !c.IsDeleted && c.OwnerSerial == caster.Uid);
    }

    [Fact]
    public void SummonThatSurvivesPlacement_IsOwnedAsBefore()
    {
        var (world, engine, caster) = SummonStack();
        GameClient.ServerOptionFlags |= OptionFlags.PetSlots;

        CastSummon(engine, caster);

        var summon = Assert.Single(world.GetAllObjects().OfType<Character>(),
            c => !c.IsDeleted && c.IsSummoned);
        Assert.Equal(caster.Uid, summon.OwnerSerial);
    }
}
