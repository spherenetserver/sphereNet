using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The poison spell end to end, as a player sees it:
/// the health-bar colour rides every character draw (CClient::addChar ->
/// addHealthBarUpdate, CClientMsg.cpp:1193-1194), so an observer who walks up to a
/// poisoned character - or logs in next to one - sees the green bar a 7.0+ client
/// reads only from 0x17/0x16; SetPoisonCure(fExtra) also ends a hallucination
/// (CCharAct.cpp:4160-4165); and the poison tick is the spell's damage
/// (CCharSpell.cpp:2005-2026): SPELLFLAG_HARM and a DamageType are required, and the
/// spell id reaches OnTakeDamage.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PoisonHealthBarAndTickTests
{
    // ------------------------------------------------------------ health bar

    private sealed class Stage
    {
        public GameWorld World = null!;
        public Character Subject = null!;
        public List<(GameClient Client, NetState State, Character Me)> Observers = [];

        public void Tick()
        {
            foreach (var (client, _, _) in Observers)
            {
                client.ViewNeedsRefresh = true;
                var delta = client.BuildViewDelta();
                if (delta != null) client.ApplyViewDelta(delta);
            }
        }
    }

    /// <summary>A poisoned subject, and observers placed out of view range so the
    /// first tick after <see cref="WalkUp"/> is the moment they see it.</summary>
    private static Stage NewStage(params Action<NetState>[] shapes)
    {
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var accounts = new AccountManager(lf);

        var subject = world.CreateCharacter();
        subject.IsPlayer = true;
        subject.IsOnline = true;
        subject.Name = "Subject";
        subject.MaxHits = 100; subject.Hits = 100;
        world.PlaceCharacter(subject, new Point3D(100, 100, 0, 0));

        var stage = new Stage { World = world, Subject = subject };
        int id = 1;
        foreach (var shape in shapes)
        {
            var state = TestHarness.CreateActiveNetState(lf, id++);
            var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
            var me = world.CreateCharacter();
            me.IsPlayer = true;
            me.MaxHits = 100; me.Hits = 100;
            world.PlaceCharacter(me, new Point3D(200, 100, 0, 0));
            shape(state);
            TestHarness.AttachCharacter(client, me);
            stage.Observers.Add((client, state, me));
        }
        stage.Tick();
        return stage;
    }

    private static void WalkUp(Stage stage)
    {
        foreach (var (_, state, me) in stage.Observers)
        {
            stage.World.MoveCharacter(me, new Point3D(102, 100, 0, 0));
            TestHarness.ClearQueuedPackets(state);
        }
        stage.Tick();
    }

    private static uint U32(ReadOnlySpan<byte> s, int at) =>
        (uint)((s[at] << 24) | (s[at + 1] << 16) | (s[at + 2] << 8) | s[at + 3]);

    /// <summary>The 0x17 packets queued for <paramref name="uid"/>: (green, yellow).</summary>
    private static List<(bool Green, bool Yellow)> Bars17(NetState state, uint uid) =>
        TestHarness.GetQueuedPackets(state)
            .Select(p => p.Span.ToArray())
            .Where(b => b.Length >= 15 && b[0] == 0x17 && U32(b, 3) == uid)
            .Select(b => (b[11] != 0, b[14] != 0))
            .ToList();

    private static List<ushort> Bars16(NetState state, uint uid) =>
        TestHarness.GetQueuedPackets(state)
            .Select(p => p.Span.ToArray())
            .Where(b => b.Length >= 12 && b[0] == 0x16 && U32(b, 3) == uid)
            .Select(b => (ushort)((b[9] << 8) | b[10]))
            .ToList();

    private static bool Drawn(NetState state, uint uid) =>
        TestHarness.GetQueuedPackets(state)
            .Any(p => p.Span.Length >= 7 && p.Span[0] == 0x78 && U32(p.Span, 3) == uid);

    [Fact]
    public void ObserverWalkingUpToAPoisonedCharacter_GetsTheGreenBarWithTheDraw()
    {
        var stage = NewStage(
            s => s.ClientVersionNumber = 70_020_000,   // ClassicUO 7.0.20
            s => s.ClientTypeFlag = 3,                 // enhanced client
            s => s.ClientVersionNumber = 50_000_000);  // pre-SA: flag bit only
        stage.Subject.SetStatFlag(StatFlag.Poisoned);
        uint uid = stage.Subject.Uid.Value;

        WalkUp(stage);

        var sa = stage.Observers[0].State;
        Assert.True(Drawn(sa, uid));
        Assert.Equal([(true, false)], Bars17(sa, uid));

        var ec = stage.Observers[1].State;
        Assert.True(Drawn(ec, uid));
        Assert.Equal([(ushort)1], Bars16(ec, uid));        // GreenBar
        Assert.Empty(Bars17(ec, uid));

        var old = stage.Observers[2].State;
        Assert.True(Drawn(old, uid));
        Assert.Empty(Bars17(old, uid));
        Assert.Empty(Bars16(old, uid));
    }

    [Fact]
    public void AnUnchangedCharacter_IsNotRetintedEveryTick()
    {
        var stage = NewStage(s => s.ClientVersionNumber = 70_020_000);
        stage.Subject.SetStatFlag(StatFlag.Poisoned);
        WalkUp(stage);
        var state = stage.Observers[0].State;
        Assert.Single(Bars17(state, stage.Subject.Uid.Value));

        TestHarness.ClearQueuedPackets(state);
        stage.Tick();
        stage.Tick();

        Assert.Empty(Bars17(state, stage.Subject.Uid.Value));
    }

    [Theory]
    [InlineData(StatFlag.Freeze)]
    [InlineData(StatFlag.Stone)]
    public void FrozenOrStone_DrawsTheYellowBar(StatFlag flag)
    {
        // PacketHealthBarUpdate: YellowBar = STATF_FREEZE|STATF_STONE (send.cpp:440).
        var stage = NewStage(s => s.ClientVersionNumber = 70_020_000);
        stage.Subject.SetStatFlag(flag);

        WalkUp(stage);

        Assert.Equal([(false, true)], Bars17(stage.Observers[0].State, stage.Subject.Uid.Value));
    }

    [Fact]
    public void OwnCharacterRedraw_CarriesItsOwnBar()
    {
        // Login and resync draw the player to itself through addChar (CChar::Update,
        // CClient::addReSync), which ends with the bar.
        var stage = NewStage(s => s.ClientVersionNumber = 70_020_000);
        var (client, state, me) = stage.Observers[0];
        me.SetStatFlag(StatFlag.Poisoned);
        TestHarness.ClearQueuedPackets(state);

        client.RefreshCharacterMode(me);

        Assert.Equal([(true, false)], Bars17(state, me.Uid.Value));
    }

    [Fact]
    public void HealthBarGate_IsSourceXCanSendTo()
    {
        var lf = LoggerFactory.Create(_ => { });
        NetState Ns(Action<NetState> shape)
        {
            var s = TestHarness.CreateActiveNetState(lf, 1);
            shape(s);
            return s;
        }

        // MINCLIVER_SA = 7.0.0.0 (sphereproto.h:701) or KR; 0x16 for the enhanced client.
        Assert.True(Ns(s => s.ClientVersionNumber = 70_000_000).SupportsHealthBarStatus);
        Assert.False(Ns(s => s.ClientVersionNumber = 60_017_000).SupportsHealthBarStatus);
        Assert.True(Ns(s => s.ClientTypeFlag = 2).SupportsHealthBarStatus);
        Assert.True(Ns(s => s.ClientTypeFlag = 3).SupportsHealthBarStatusNew);
        Assert.False(Ns(s => s.ClientVersionNumber = 70_020_000).SupportsHealthBarStatusNew);
    }

    // ------------------------------------------------------------ cure

    private static (GameWorld World, SpellEngine Engine, Character Ch) MagicSetup()
    {
        var world = TestHarness.CreateWorld();
        Character.ResolveCharByUid = world.FindChar;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Hallucination,
            Name = "Hallucination",
            Flags = SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Curse,
            EffectBase = 5, EffectScale = 5,
            DurationBase = 1200,
        });
        var engine = new SpellEngine(world, registry);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM;
        ch.BodyId = 0x0190;
        ch.Hits = ch.MaxHits = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        Character.MagicFlags = (int)MagicConfigFlags.CanHarmSelf;
        return (world, engine, ch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetPoisonCureExtra_AlsoEndsTheHallucination(bool extra)
    {
        var (_, engine, ch) = MagicSetup();
        engine.ApplyDirectEffect(ch, ch, SpellType.Hallucination, 500);
        Assert.True(ch.IsStatFlag(StatFlag.Hallucinating));
        ch.Poison.Apply(3, Serial.Invalid);
        Assert.True(ch.IsStatFlag(StatFlag.Poisoned));

        ch.SetPoisonCure(extra);

        Assert.False(ch.IsStatFlag(StatFlag.Poisoned));
        Assert.Null(ch.Poison.Memory);
        Assert.Equal(!extra, ch.IsStatFlag(StatFlag.Hallucinating));
        Assert.Equal(!extra, ch.FindLayer(SpellLayers.FlagHallucination) != null);
    }

    // ------------------------------------------------------------ tick

    private static (Character Victim, Item Memory) PoisonedVictim()
    {
        var world = TestHarness.CreateWorld();
        Character.ResolveCharByUid = world.FindChar;
        Character.MagicFlags = 0;
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.BodyId = 0x0190;
        victim.Str = 100;
        victim.Hits = victim.MaxHits = 100;
        world.PlaceCharacter(victim, new Point3D(100, 100, 0, 0));
        victim.Poison.Apply(5, Serial.Invalid);   // lethal: a non-zero tick
        var mem = victim.Poison.Memory;
        Assert.NotNull(mem);
        return (victim, mem!);
    }

    private static SpellDef PoisonDef(SpellFlag flags) => new()
    {
        Id = SpellType.Poison, Name = "Poison", Flags = flags, DurationBase = 150,
    };

    [Fact]
    public void PoisonTick_DamageCarriesTheSpell()
    {
        var (victim, mem) = PoisonedVictim();
        Character.ResolveSpellDef = _ => PoisonDef(SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Tick);
        var seen = new List<(int Spell, DamageType Type)>();
        CombatEngine.OnGetHit = ctx => { seen.Add((ctx.Spell, ctx.DamageType)); return ctx.Damage; };

        int dealt = victim.Poison.EquipTick(mem);

        Assert.True(dealt > 0);
        var hit = Assert.Single(seen);
        Assert.Equal((int)SpellType.Poison, hit.Spell);                 // LOCAL.Spell = 20
        Assert.Equal(DamageType.Magic | DamageType.Poison | DamageType.NoDisturb | DamageType.NoReveal,
            hit.Type);
    }

    [Fact]
    public void PoisonTick_SpellNoUnparalyze_KeepsTheParalysis()
    {
        var (victim, mem) = PoisonedVictim();
        int breaks = 0;
        Character.BreakParalyzeHook = _ => breaks++;

        Character.ResolveSpellDef = _ => PoisonDef(SpellFlag.Harm | SpellFlag.NoUnparalyze);
        victim.Poison.EquipTick(mem);
        Assert.Equal(0, breaks);

        Character.ResolveSpellDef = _ => PoisonDef(SpellFlag.Harm);
        mem.SetTimeout(0);
        victim.Poison.EquipTick(mem);
        Assert.Equal(1, breaks);
    }

    [Fact]
    public void PoisonTick_WithoutSpellflagHarm_DealsNoDamage()
    {
        var (victim, mem) = PoisonedVictim();
        Character.ResolveSpellDef = _ => PoisonDef(SpellFlag.TargChar | SpellFlag.Tick);
        int getHits = 0;
        CombatEngine.OnGetHit = ctx => { getHits++; return ctx.Damage; };

        Assert.Equal(0, victim.Poison.EquipTick(mem));
        Assert.Equal(0, getHits);
        Assert.Equal(100, victim.Hits);
        Assert.True(victim.IsStatFlag(StatFlag.Poisoned));    // the tick still counts down
    }

    [Fact]
    public void PoisonTick_ScriptZeroedDamageType_DealsNoDamage()
    {
        var (victim, mem) = PoisonedVictim();
        Character.ResolveSpellDef = _ => PoisonDef(SpellFlag.Harm);
        int seededType = -1;
        Character.OnSpellEffectTick = (_, ctx) =>
        {
            seededType = ctx.DamageType;
            ctx.DamageType = 0;
            return true;
        };

        Assert.Equal(0, victim.Poison.EquipTick(mem));
        Assert.Equal((int)(DamageType.Magic | DamageType.Poison | DamageType.NoDisturb | DamageType.NoReveal),
            seededType);
        Assert.Equal(100, victim.Hits);
    }
}
