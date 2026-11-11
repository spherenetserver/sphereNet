using System.Text;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Network.Packets;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Magic behaviour pinned to the reference engine's CCharSpell.cpp / CItem.cpp /
/// CClientUse.cpp: the effect-and-sound tail of every OnSpellEffect, what a Recall,
/// a Gate and a Teleport ask of their destination, a new cast aborting the running
/// one, the item side of a spell aimed at an item, Dispel Field and permanent
/// fields, which spells get a target cursor and its prompt, the @SpellCast /
/// @SpellFail contract, and the smaller cast-start rules around them.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MagicParityGapTests
{
    // ---- bench ------------------------------------------------------------------

    private sealed record Fx(byte Op, byte Type, uint Src, uint Dst, ushort Id, byte Speed, byte Loop, bool Explode);

    private sealed class Bench : IDisposable
    {
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _logs = TestHarness.CreateLoggerFactory();
        private readonly Action<Point3D, int, PacketWriter, uint>? _savedBroadcast = Character.BroadcastNearby;
        public readonly GameWorld World = TestHarness.CreateWorld();
        public readonly SpellRegistry Registry = new();
        public readonly TriggerDispatcher Triggers = new();
        public readonly SpellEngine Engine;
        public readonly Character Caster;
        public readonly List<PacketWriter> Sent = [];
        public readonly List<(Point3D At, ushort Id)> Sounds = [];
        public readonly List<string> Messages = [];
        private GameClient? _client;

        public Bench(PrivLevel priv = PrivLevel.Player, params SpellDef[] defs)
        {
            foreach (var d in defs)
                Registry.Register(d);
            Engine = new SpellEngine(World, Registry) { TriggerDispatcher = Triggers };
            Engine.OnPlaySound = (at, id) => Sounds.Add((at, id));
            Engine.OnSysMessage = (_, text) => Messages.Add(text);
            Character.BroadcastNearby = (_, _, p, _) => Sent.Add(p);
            Character.SpellbookRequiredEnabled = false;

            Caster = World.CreateCharacter();
            Caster.IsPlayer = true;
            Caster.IsOnline = true;
            Caster.PrivLevel = priv;
            Caster.Str = 100; Caster.MaxHits = 100; Caster.Hits = 100;
            Caster.Int = 100; Caster.MaxMana = 100; Caster.Mana = 100;
            World.PlaceCharacter(Caster, new Point3D(100, 100, 0, 0));
        }

        public GameClient Client
        {
            get
            {
                if (_client != null) return _client;
                _client = TestHarness.CreateClient(_logs, World, new AccountManager(_logs), 27110);
                TestHarness.AttachCharacter(_client, Caster);
                _client.SetEngines(spellEngine: Engine, triggerDispatcher: Triggers);
                return _client;
            }
        }

        public Character Npc(int x, int y)
        {
            var ch = World.CreateCharacter();
            ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;
            ch.Int = 100; ch.MaxMana = 100; ch.Mana = 100;
            World.PlaceCharacter(ch, new Point3D((short)x, (short)y, 0, 0));
            return ch;
        }

        public Character Player(int x, int y)
        {
            var ch = Npc(x, y);
            ch.IsPlayer = true;
            ch.IsOnline = true;
            ch.PrivLevel = PrivLevel.Player;
            return ch;
        }

        public Region Area(string name, int x, int y, RegionFlag flags)
        {
            var r = new Region { Name = name, MapIndex = 0, P = new Point3D((short)x, (short)y, 0, 0) };
            r.AddRect((short)(x - 3), (short)(y - 3), (short)(x + 3), (short)(y + 3));
            r.Flags = flags;
            World.AddRegion(r);
            return r;
        }

        public Item Rune(Point3D mark)
        {
            var rune = World.CreateItem();
            rune.ItemType = ItemType.Rune;
            rune.SetRuneMark(mark);
            rune.More1 = 10;
            World.PlaceItem(rune, new Point3D((short)(Caster.X + 1), Caster.Y, 0, 0));
            return rune;
        }

        public bool Cast(SpellType spell, Serial uid, Point3D pos)
        {
            Assert.True(Engine.CastStart(Caster, spell, uid, pos) > 0, Engine.LastCastRefusal);
            Caster.CastSkillSucceeded = true;
            return Engine.CastDone(Caster);
        }

        public List<Fx> Effects() => Sent
            .Select(p => p.Build().Span.ToArray())
            .Where(b => b.Length >= 28 && b[0] is 0x70 or 0xC0)
            .Select(b => new Fx(b[0], b[1],
                (uint)(b[2] << 24 | b[3] << 16 | b[4] << 8 | b[5]),
                (uint)(b[6] << 24 | b[7] << 16 | b[8] << 8 | b[9]),
                (ushort)(b[10] << 8 | b[11]), b[22], b[23], b[27] != 0))
            .ToList();

        /// <summary>Whether any packet queued for the client carries this text, as
        /// ASCII or as big-endian UTF-16.</summary>
        public bool ClientGotText(string text)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(text);
            byte[] wide = Encoding.BigEndianUnicode.GetBytes(text);
            foreach (var pkt in TestHarness.GetQueuedPackets(Client.NetState))
            {
                var span = pkt.Span;
                if (span.IndexOf(ascii) >= 0 || span.IndexOf(wide) >= 0)
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            Character.BroadcastNearby = _savedBroadcast;
            _logs.Dispose();
        }
    }

    private static SpellDef Def(SpellType id, SpellFlag flags, ushort effectId = 0, int sound = 0,
        int effect = 0, int durationTenths = 0, ushort mana = 0) => new()
    {
        Id = id,
        Name = id.ToString(),
        Flags = flags,
        ManaCost = mana,
        CastTimeBase = 1,
        EffectId = effectId,
        Sound = sound,
        EffectBase = effect,
        EffectScale = effect,
        DurationBase = durationTenths,
        DurationScale = durationTenths,
    };

    // ---- 1. the effect / sound tail of OnSpellEffect (CCharSpell.cpp:4226-4236) ----

    [Fact]
    public void AnAreaSpellShowsAndSoundsOnEveryVictim()
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.MassCurse, SpellFlag.Area | SpellFlag.Harm | SpellFlag.FxTarg, 0x374A, 0x1FB));
        var near = b.Npc(101, 100);
        var far = b.Npc(102, 101);

        Assert.True(b.Cast(SpellType.MassCurse, b.Caster.Uid, b.Caster.Position));

        var fx = b.Effects().Where(e => e.Id == 0x374A).ToList();
        Assert.Equal(2, fx.Count);
        Assert.All(fx, e => { Assert.Equal(3, e.Type); Assert.Equal(0, e.Speed); Assert.Equal(15, e.Loop); });
        Assert.Contains(fx, e => e.Src == near.Uid.Value);
        Assert.Contains(fx, e => e.Src == far.Uid.Value);
        // Not on the caster: a harmful area spell passes its caster by.
        Assert.DoesNotContain(fx, e => e.Src == b.Caster.Uid.Value);
        Assert.Contains((near.Position, (ushort)0x1FB), b.Sounds);
        Assert.Contains((far.Position, (ushort)0x1FB), b.Sounds);
    }

    [Fact]
    public void ABoltFliesFromTheCasterAndDoesNotExplodeUnlessAScriptSaysSo()
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.MagicArrow, SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.FxBolt, 0x36E4, 0x1E5));
        var target = b.Player(103, 100);

        long seededExplode = -1, seededSound = -1, seededFx = -1;
        bool rewrite = false;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellEffect", (_, a) =>
        {
            seededExplode = a.Locals!.GetInt("Explode", -1);
            seededSound = a.Locals.GetInt("Sound", -1);
            seededFx = a.Locals.GetInt("CreateObject1", -1);
            if (rewrite)
            {
                a.Locals.SetInt("CreateObject1", 0x3709);
                a.Locals.SetInt("Sound", 0x208);
                a.Locals.SetInt("EffectExplode", 1);
                a.Locals.SetInt("EffectColor", 0x21);
            }
            return TriggerResult.Default;
        });

        Assert.True(b.Cast(SpellType.MagicArrow, target.Uid, target.Position));
        var bolt = Assert.Single(b.Effects(), e => e.Id == 0x36E4);
        Assert.Equal(0x70, bolt.Op);
        Assert.Equal(0, bolt.Type);                       // EFFECT_BOLT
        Assert.Equal(b.Caster.Uid.Value, bolt.Src);
        Assert.Equal(target.Uid.Value, bolt.Dst);
        Assert.Equal(5, bolt.Speed);
        // LOCAL.Explode is seeded 1 for a non-GOOD bolt, but the tail reads
        // LOCAL.EffectExplode (CCharSpell.cpp:3773 vs :3805).
        Assert.Equal(1, seededExplode);
        Assert.False(bolt.Explode);
        Assert.Equal(0x1E5, seededSound);
        Assert.Equal(0x36E4, seededFx);
        Assert.Contains((target.Position, (ushort)0x1E5), b.Sounds);

        // The locals are read back: another art, hued, exploding, another sound.
        b.Sent.Clear(); b.Sounds.Clear();
        rewrite = true;
        Assert.True(b.Cast(SpellType.MagicArrow, target.Uid, target.Position));
        var custom = Assert.Single(b.Effects(), e => e.Type == 0);
        Assert.Equal(0xC0, custom.Op);                    // hued effect
        Assert.Equal(0x3709, custom.Id);
        Assert.True(custom.Explode);
        Assert.Contains((target.Position, (ushort)0x208), b.Sounds);
        Assert.DoesNotContain((target.Position, (ushort)0x1E5), b.Sounds);
    }

    [Fact]
    public void ARefusedEffectShowsNothing_AndAReflectedOneShowsOnTheCaster()
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.Curse, SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.FxTarg, 0x374A, 0x1E1));
        var target = b.Npc(103, 100);

        // Invulnerable: the glow of a spell that did not take, never the spell's own.
        target.SetStatFlag(StatFlag.Invul);
        Assert.True(b.Cast(SpellType.Curse, target.Uid, target.Position));
        Assert.DoesNotContain(b.Effects(), e => e.Id == 0x374A);
        Assert.DoesNotContain((target.Position, (ushort)0x1E1), b.Sounds);

        // Reflected: the copy that re-enters on the caster ends with its own tail.
        target.ClearStatFlag(StatFlag.Invul);
        target.SetStatFlag(StatFlag.Reflection);
        b.Sent.Clear(); b.Sounds.Clear();
        Assert.True(b.Cast(SpellType.Curse, target.Uid, target.Position));
        var shown = Assert.Single(b.Effects(), e => e.Id == 0x374A);
        Assert.Equal(b.Caster.Uid.Value, shown.Src);
        Assert.DoesNotContain((target.Position, (ushort)0x1E1), b.Sounds);
    }

    [Fact]
    public void APotionMakesADrinkingSound_NotTheSpells()
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good | SpellFlag.FxTarg, 0x376A, 0x1F2, effect: 5));
        var potion = b.World.CreateItem();
        potion.ItemType = ItemType.Potion;

        b.Engine.ApplyDirectEffect(b.Caster, b.Caster, SpellType.Heal, 500, potion);

        var sound = Assert.Single(b.Sounds);
        Assert.Contains(sound.Id, new ushort[] { 0x030, 0x031 });   // sm_DrinkSounds, :3702-3709
        Assert.Single(b.Effects(), e => e.Id == 0x376A && e.Src == b.Caster.Uid.Value);
    }

    [Fact]
    public void ASingleTargetCastThroughTheClientIsDrawnOnce_AndSaysNothing()
    {
        using var b = new Bench(PrivLevel.GM,
            Def(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good | SpellFlag.FxTarg, 0x376A, 0x1F2, effect: 5));
        var friend = b.Npc(101, 100);
        var client = b.Client;

        client.HandleCastSpell(SpellType.Heal, friend.Uid.Value);
        Assert.True(b.Caster.IsCasting);
        TestHarness.ClearQueuedPackets(client.NetState);
        b.Sent.Clear();
        b.Caster.SetCastTimerEnd(1);
        client.TickSpellCast();

        Assert.False(b.Caster.IsCasting);
        var fx = Assert.Single(b.Effects(), e => e.Id == 0x376A);
        Assert.Equal(friend.Uid.Value, fx.Src);
        // No second copy sent straight to the caster's connection, and no invented
        // "You cast <spell>." line (Spell_CastDone says nothing on success).
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] is 0x70 or 0xC0);
        Assert.False(b.ClientGotText("You cast"));
    }

    // ---- 2. Recall / Gate destination (CCharSpell.cpp:131-144, :285-292) ----

    [Fact]
    public void RecallIsRefusedOnlyByAnAntiTeleportDestination()
    {
        using var b = new Bench(PrivLevel.Player, Def(SpellType.Recall, SpellFlag.TargItem));
        b.Area("keep", 300, 300, RegionFlag.NoTeleport);
        b.Area("temple", 400, 400, RegionFlag.NoMagic | RegionFlag.Recall);
        var start = b.Caster.Position;

        b.Cast(SpellType.Recall, b.Rune(new Point3D(300, 300, 0, 0)).Uid, default);
        Assert.Equal(start, b.Caster.Position);
        Assert.Contains(ServerMessages.Get(Msg.SpellTeleAm), b.Messages);

        // ANTIMAGIC_ALL and RECALL_IN keep a rune from being MARKED there; they do
        // not turn away somebody who already holds one.
        b.Cast(SpellType.Recall, b.Rune(new Point3D(400, 400, 0, 0)).Uid, default);
        Assert.Equal(400, b.Caster.X);
        Assert.Equal(400, b.Caster.Y);
    }

    [Fact]
    public void AGateIsRefusedByAnAntiGateDestinationWithItsOwnMessage()
    {
        using var b = new Bench(PrivLevel.Player, Def(SpellType.GateTravel, SpellFlag.TargItem, durationTenths: 300));
        b.Area("vault", 300, 300, RegionFlag.Gate);
        b.Area("temple", 400, 400, RegionFlag.Recall);

        b.Cast(SpellType.GateTravel, b.Rune(new Point3D(300, 300, 0, 0)).Uid, default);
        Assert.Contains(ServerMessages.Get(Msg.SpellGateAm), b.Messages);
        Assert.DoesNotContain(b.World.GetAllObjects().OfType<Item>(), i => !i.IsDeleted && i.ItemType == ItemType.Telepad);

        // RECALL_IN at the far end does not stop a gate (Spell_CreateGate asks only
        // ANTIMAGIC_ALL | ANTIMAGIC_GATE there).
        b.Cast(SpellType.GateTravel, b.Rune(new Point3D(400, 400, 0, 0)).Uid, default);
        Assert.Equal(2, b.World.GetAllObjects().OfType<Item>().Count(i => !i.IsDeleted && i.ItemType == ItemType.Telepad));
    }

    // ---- 3. a new cast aborts the running one (CCharSkill.cpp:4407-4409) ----

    [Fact]
    public void ANewCastAbortsTheRunningOneAtTheAbortPrice()
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good, mana: 10),
            Def(SpellType.Strength, SpellFlag.TargChar | SpellFlag.Good));
        Character.ManaLossAbort = true;
        Character.ManaLossPercent = 100;
        int aborts = 0;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillAbort", (_, _) => { aborts++; return TriggerResult.Default; });

        Assert.True(b.Engine.CastStart(b.Caster, SpellType.Heal, b.Caster.Uid, b.Caster.Position) > 0);
        Assert.True(b.Engine.CastStart(b.Caster, SpellType.Strength, b.Caster.Uid, b.Caster.Position) > 0,
            b.Engine.LastCastRefusal);

        Assert.True(b.Caster.TryGetCastingSpell(out var running));
        Assert.Equal(SpellType.Strength, running);
        Assert.Equal(1, aborts);
        Assert.Equal(90, b.Caster.Mana);                              // MANALOSSABORT of the Heal
        Assert.Contains(ServerMessages.Get(Msg.SpellGenFizzles), b.Messages);
        Assert.Contains(b.Sounds, s => s.Id == 0x5C);                 // SOUND_SPELL_FIZZLE
    }

    // ---- 4. a spell aimed at an item (CItem.cpp:5642-5657, :5752-5765) ----

    [Fact]
    public void ASpellAimedAtAnItemRechargesAWandDamagesItAndShowsOnIt()
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.Fireball, SpellFlag.TargObj | SpellFlag.Harm | SpellFlag.FxBolt, 0x36D4, 0x15E));
        var wand = b.World.CreateItem();
        wand.ItemType = ItemType.Wand;
        b.World.PlaceItem(wand, new Point3D(101, 100, 0, 0));

        var damaged = new List<(Item Item, int Damage, DamageType Type)>();
        var savedHook = CombatEngine.OnItemDamaged;
        CombatEngine.OnItemDamaged = (item, dmg, _, type) => { damaged.Add((item, dmg, type)); return false; };
        try
        {
            // A blank wand takes the spell and its first charge.
            Assert.True(b.Cast(SpellType.Fireball, wand.Uid, wand.Position));
            Assert.Equal((short)SpellType.Fireball, wand.MoreP.X);
            Assert.Equal(1u, wand.More2);
            Assert.True(wand.IsAttr(ObjAttributes.Magic));

            // A wand of the same spell gains one.
            Assert.True(b.Cast(SpellType.Fireball, wand.Uid, wand.Position));
            Assert.Equal(2u, wand.More2);
        }
        finally { CombatEngine.OnItemDamaged = savedHook; }

        // "Potions should explode when hit": a HARM spell does one point of magic
        // (fire) damage to the item.
        Assert.Equal(2, damaged.Count);
        Assert.All(damaged, d =>
        {
            Assert.Same(wand, d.Item);
            Assert.Equal(1, d.Damage);
            Assert.True((d.Type & DamageType.Magic) != 0 && (d.Type & DamageType.Fire) != 0);
        });

        // The bolt flies at the item.
        var bolts = b.Effects().Where(e => e.Id == 0x36D4).ToList();
        Assert.Equal(2, bolts.Count);
        Assert.All(bolts, e =>
        {
            Assert.Equal(0, e.Type);
            Assert.Equal(b.Caster.Uid.Value, e.Src);
            Assert.Equal(wand.Uid.Value, e.Dst);
            Assert.True(e.Explode);                 // CItem.cpp:5757 keeps the bolt default
        });
    }

    // ---- 5. Dispel Field and permanent fields (CCharSpell.cpp:3192-3202, :2354) ----

    [Fact]
    public void DispelFieldTakesDownACastField_AndLeavesAPermanentOne()
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.FireField, SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Field, durationTenths: 1200),
            Def(SpellType.DispelField, SpellFlag.TargItem));

        Assert.True(b.Cast(SpellType.FireField, b.Caster.Uid, new Point3D(105, 100, 0, 0)));
        var segment = b.World.GetAllObjects().OfType<Item>()
            .First(i => !i.IsDeleted && i.ItemType == ItemType.Spell && i.MoreP.X == (short)SpellType.FireField);
        // ATTR_MAGIC alone: not MOVE_NEVER, yet with its own timer and not liftable.
        Assert.False(segment.IsAttr(ObjAttributes.Move_Never));
        Assert.True(segment.IsAttr(ObjAttributes.Magic));
        Assert.True(segment.DecayTime > Environment.TickCount64);
        Assert.False(ItemMoveRules.CanMove(b.Caster, segment, out _));

        Assert.True(b.Cast(SpellType.DispelField, segment.Uid, segment.Position));
        Assert.True(segment.IsDeleted);

        // A permanent field is ATTR_MOVE_NEVER: "That is not a field!" and the cast fails.
        var wall = b.World.CreateItem();
        wall.ItemType = ItemType.Spell;
        wall.SetAttr(ObjAttributes.Move_Never);
        b.World.PlaceItem(wall, new Point3D(102, 100, 0, 0));
        b.Messages.Clear();
        Assert.False(b.Cast(SpellType.DispelField, wall.Uid, wall.Position));
        Assert.False(wall.IsDeleted);
        Assert.Contains(ServerMessages.Get(Msg.SpellDispellfWt), b.Messages);

        // So is anything that is not an IT_SPELL item.
        var chair = b.World.CreateItem();
        b.World.PlaceItem(chair, new Point3D(102, 101, 0, 0));
        b.Messages.Clear();
        Assert.False(b.Cast(SpellType.DispelField, chair.Uid, chair.Position));
        Assert.False(chair.IsDeleted);
        Assert.Contains(ServerMessages.Get(Msg.SpellDispellfWt), b.Messages);
    }

    // ---- 6 / 13. which spells get a cursor, and its prompt (CClientUse.cpp:1053-1087) ----

    [Fact]
    public void AnAreaSpellWithoutTargetFlagsCastsAtOnce_ATargetedOneGetsACursorAndItsPrompt()
    {
        var prompted = Def(SpellType.Bless, SpellFlag.TargChar | SpellFlag.Good);
        prompted.TargetPrompt = "Whom shall the blessing touch?";
        using var b = new Bench(PrivLevel.GM,
            Def(SpellType.ArchCure, SpellFlag.Area | SpellFlag.Good),
            Def(SpellType.Teleport, SpellFlag.TargXYZ),
            prompted);
        var client = b.Client;

        client.HandleCastSpell(SpellType.ArchCure, 0);
        Assert.False(client.HasPendingTarget);
        Assert.True(b.Caster.TryGetCastingSpell(out var running));
        Assert.Equal(SpellType.ArchCure, running);
        b.Caster.ClearCastState(notifyAbort: false);

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleCastSpell(SpellType.Teleport, 0);
        Assert.True(client.HasPendingTarget);
        Assert.False(b.Caster.IsCasting);
        Assert.True(b.ClientGotText(ServerMessages.Get(Msg.SelectMagicTarget)));   // DEFMSG_SELECT_MAGIC_TARGET

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleCastSpell(SpellType.Bless, 0);
        Assert.True(client.HasPendingTarget);
        Assert.True(b.ClientGotText("Whom shall the blessing touch?"));            // PROMPT_MSG
    }

    // ---- 7. Summon Familiar opens its menu (CClientUse.cpp:1037-1047) ----

    [Fact]
    public void SummonFamiliarAsksItsSkillMenuFirst()
    {
        using var b = new Bench(PrivLevel.GM, Def(SpellType.SummonFamiliar, SpellFlag.Summon, durationTenths: 1200));
        string? menu = null;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "SkillMenu", (_, a) => { menu = a.S1; return TriggerResult.True; });

        b.Client.HandleCastSpell(SpellType.SummonFamiliar, 0);

        Assert.Equal("sm_summon_familiar", menu);
        Assert.False(b.Caster.IsCasting);     // RETURN 1 from @SkillMenu: the script took over
    }

    // ---- 8. Teleport is refused before the cast is paid (CCharSpell.cpp:2837-2862) ----

    [Fact]
    public void TeleportIntoAnAntiTeleportAreaIsRefusedWhenTheCastStarts()
    {
        using var b = new Bench(PrivLevel.Player, Def(SpellType.Teleport, SpellFlag.TargXYZ, mana: 10));
        b.Area("keep", 108, 100, RegionFlag.NoTeleport);

        Assert.Equal(-1, b.Engine.CastStart(b.Caster, SpellType.Teleport, b.Caster.Uid, new Point3D(108, 100, 0, 0)));
        Assert.False(b.Caster.IsCasting);
        Assert.Equal(100, b.Caster.Mana);
        Assert.Contains(ServerMessages.Get(Msg.SpellTeleAm), b.Messages);

        // Elsewhere it goes.
        Assert.True(b.Cast(SpellType.Teleport, b.Caster.Uid, new Point3D(100, 108, 0, 0)));
        Assert.Equal(108, b.Caster.Y);
    }

    // ---- 9. a cast always brings a hiding caster out (CCharSpell.cpp:3625-3628) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACastRevealsAHiddenCaster_TheFlagOnlyDecidesAboutInvisibility(bool revealOnCastFlag)
    {
        using var b = new Bench(PrivLevel.Player, Def(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good));
        Character.ActiveRevealFlags = revealOnCastFlag
            ? Character.ActiveRevealFlags | RevealFlags.SpellCast
            : Character.ActiveRevealFlags & ~RevealFlags.SpellCast;
        b.Caster.SetStatFlag(StatFlag.Hidden);
        b.Caster.SetStatFlag(StatFlag.Invisible);

        Assert.True(b.Engine.CastStart(b.Caster, SpellType.Heal, b.Caster.Uid, b.Caster.Position) > 0);

        Assert.False(b.Caster.IsStatFlag(StatFlag.Hidden));
        Assert.Equal(!revealOnCastFlag, b.Caster.IsStatFlag(StatFlag.Invisible));
    }

    // ---- 10. a creature that cannot speak casts in silence (CCharSpell.cpp:3538-3540) ----

    [Fact]
    public void ACreatureWithoutSpeechSaysNoPowerWords()
    {
        var heal = Def(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good);
        heal.Runes = "In Mani";
        using var b = new Bench(PrivLevel.Player, heal);
        var npc = b.Npc(110, 100);
        var spoken = new List<string>();
        b.Engine.OnSpellWords = (_, words) => spoken.Add(words);

        Assert.True(b.Engine.CastStart(npc, SpellType.Heal, npc.Uid, npc.Position) > 0, b.Engine.LastCastRefusal);
        Assert.Empty(spoken);
        npc.ClearCastState(notifyAbort: false);

        // One with a SPEECH block of its own does (NPC_CanSpeak).
        npc.DSpeech.Add(new ResourceId(ResType.Speech, 1));
        Assert.True(b.Engine.CastStart(npc, SpellType.Heal, npc.Uid, npc.Position) > 0, b.Engine.LastCastRefusal);
        Assert.Equal(["In Mani"], spoken);
    }

    // ---- 11. the travel effect (CCharSpell.cpp:178-198, :236-245, :419-421) ----

    [Fact]
    public void RecallHandsOverItsOwnEffectAndSound_TeleportLeavesTheConfiguredOne()
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.Recall, SpellFlag.TargItem, effectId: 0x3728, sound: 0x1FC),
            Def(SpellType.Teleport, SpellFlag.TargXYZ, effectId: 0x3728, sound: 0x1FE));
        var seen = new List<(Point3D From, ushort Effect, ushort Sound)>();
        b.Engine.OnSpellTeleport = (_, from, effect, sound) => seen.Add((from, effect, sound));
        var start = b.Caster.Position;

        b.Cast(SpellType.Recall, b.Rune(new Point3D(300, 300, 0, 0)).Uid, default);
        Assert.Equal(300, b.Caster.X);
        Assert.Equal((start, (ushort)0x3728, (ushort)0x1FC), Assert.Single(seen));

        seen.Clear();
        var left = b.Caster.Position;
        Assert.True(b.Cast(SpellType.Teleport, b.Caster.Uid, new Point3D(304, 300, 0, 0)));
        Assert.Equal(304, b.Caster.X);
        // "Spell_Teleport(m_Act_p)" passes neither: the host shows the configured pair.
        Assert.Equal((left, (ushort)0, (ushort)0), Assert.Single(seen));
    }

    // ---- 14. the @SpellCast / @SpellFail contract (CCharSpell.cpp:3573-3594, :3403) ----

    [Fact]
    public void SpellCastSeesNumericWopTalkModeAndTheCastItem()
    {
        var heal = Def(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good);
        heal.Runes = "In Mani";
        using var b = new Bench(PrivLevel.Player, heal);
        long wop = -1, talkMode = -1;
        object? argo = "unset";
        bool silence = false;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, a) =>
        {
            wop = a.Locals!.GetInt("WOP", -1);
            talkMode = a.Locals.GetInt("WOPTalkMode", -1);
            argo = a.O1;
            if (silence)
            {
                a.Locals.SetInt("WOP", 0);
                a.Locals.SetInt("WOPTalkMode", 2);
            }
            return TriggerResult.Default;
        });

        var prep = b.Engine.PrepareCast(b.Caster, SpellType.Heal);
        Assert.NotNull(prep);
        Assert.Equal(1, wop);                       // a number, not the mantra's text
        Assert.Equal(10, talkMode);                 // TALKMODE_SPELL
        Assert.Null(argo);                          // a cast from the caster's own power
        Assert.Null(prep!.Words);                   // speak the spell's own words
        Assert.Equal(10, prep.TalkMode);

        silence = true;
        prep = b.Engine.PrepareCast(b.Caster, SpellType.Heal);
        Assert.Equal("", prep!.Words);              // LOCAL.WOP = 0: silent, never "0"
        Assert.Equal(2, prep.TalkMode);

        // A wand cast: ARGO is the wand, and a wand speaks no words (WOP seeded 0).
        var pack = b.World.CreateItem();
        pack.ItemType = ItemType.Container;
        b.Caster.Equip(pack, Layer.Pack);
        var wand = b.World.CreateItem();
        wand.ItemType = ItemType.Wand;
        b.Caster.Backpack!.AddItem(wand);
        b.Caster.SetTag("WAND_UID", wand.Uid.Value.ToString());
        silence = false;
        b.Engine.PrepareCast(b.Caster, SpellType.Heal);
        Assert.Same(wand, argo);
        Assert.Equal(0, wop);
    }

    [Fact]
    public void SpellFailNamesTheCastSourceAsItsObject()
    {
        using var b = new Bench(PrivLevel.Player, Def(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good));
        object? argo = null;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellFail", (_, a) => { argo = a.O1; return TriggerResult.Default; });

        Assert.True(b.Engine.CastStart(b.Caster, SpellType.Heal, b.Caster.Uid, b.Caster.Position) > 0);
        b.Engine.CancelCast(b.Caster);

        Assert.Same(b.Caster, argo);                // m_Act_Prv_UID.ObjFind(): the caster himself
    }

    // ---- 15. Evil Omen halves the resist (CCharSpell.cpp:3728-3733) ----

    [Theory]
    [InlineData(false, 0x02, 25)]
    [InlineData(true, 0x02, 12)]
    [InlineData(true, 0x00, 25)]     // without FEATURE_AOS_UPDATE_B the omen changes nothing
    public void EvilOmenHalvesTheResistOfTheNextSpell(bool omen, int featureAos, int expectedResist)
    {
        using var b = new Bench(PrivLevel.Player,
            Def(SpellType.Curse, SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Resist));
        var target = b.Player(102, 100);
        Character.FeatureAOS = featureAos;
        if (omen)
            target.EvilOmenActive = true;
        long resist = -1;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellEffect", (ch, a) =>
        {
            if (ch == target)
                resist = a.Locals!.GetInt("Resist", -1);
            return TriggerResult.Default;
        });
        var savedQuick = Character.OnSkillUseQuick;
        Character.OnSkillUseQuick = (_, _, _, _) => 1;      // the resist roll succeeds
        try
        {
            Assert.True(b.Cast(SpellType.Curse, target.Uid, target.Position));
        }
        finally { Character.OnSkillUseQuick = savedQuick; }

        Assert.Equal(expectedResist, resist);
    }

    // ---- 16. an untargeted summon appears where the caster stands now (:3040-3041) ----

    [Fact]
    public void AnUntargetedSummonAppearsWhereTheCasterStandsAtCompletion()
    {
        using var b = new Bench(PrivLevel.GM, Def(SpellType.AirElemental, SpellFlag.Summon, durationTenths: 1200));
        var begun = b.Caster.Position;

        Assert.True(b.Engine.CastStart(b.Caster, SpellType.AirElemental, b.Caster.Uid, begun) > 0);
        Assert.True(b.World.MoveCharacter(b.Caster, new Point3D(106, 104, 0, 0)));
        Assert.True(b.Engine.CastDone(b.Caster));

        var summon = Assert.Single(b.World.GetAllObjects().OfType<Character>(), c => c != b.Caster && !c.IsDeleted);
        Assert.Equal(b.Caster.Position, summon.Position);
        Assert.NotEqual(begun, summon.Position);
    }

    // ---- 17. EQUIPPEDCAST off empties every caster's hands (CCharSpell.cpp:3609-3615) ----

    [Theory]
    [InlineData(false)]     // a creature
    [InlineData(true)]      // a GM
    public void WithEquippedCastOffEveryCastersHandsAreEmptied(bool gm)
    {
        using var b = new Bench(gm ? PrivLevel.GM : PrivLevel.Player,
            Def(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good));
        Character.EquippedCastEnabled = false;
        var caster = gm ? b.Caster : b.Npc(110, 100);
        var sword = b.World.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        Assert.True(caster.Equip(sword, Layer.OneHanded));

        Assert.True(b.Engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0, b.Engine.LastCastRefusal);

        Assert.Null(caster.GetEquippedItem(Layer.OneHanded));
        // Bounced into the pack - made on the spot for a caster that had none.
        Assert.NotNull(caster.Backpack);
        Assert.Contains(sword, caster.Backpack!.Contents);
    }
}
