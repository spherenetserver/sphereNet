using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Combat wave C7 — the deferred tail: @Hit LOCAL.Arrow (hit-path ammo
// takeover, Source-X CCharFight.cpp:2158/2183) and the COMBAT_SLAYER magic
// path (spellbook-first slayer source on spell damage, CCharFight.cpp:824).
public class CombatWaveC7TailTests
{
    // ---- @Hit LOCAL.Arrow ----

    [Fact]
    public void HitTriggers_SeeTheAmmoUid_AndArrowHandledCopiesBack()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_hitarrow_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_hit_arrow_probe]
            ON=@Hit
            TAG.GOTARROW=<LOCAL.Arrow>
            LOCAL.ArrowHandled=1
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var attacker = world.CreateCharacter();
            var target = world.CreateCharacter();
            world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
            world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
            attacker.Events.Add(stack.Resources.ResolveDefName("e_hit_arrow_probe"));

            var ctx = new HitDamageContext
            {
                Attacker = attacker,
                Target = target,
                Damage = 10,
                ItemDamageLayer = Layer.Helm,
                AmmoUid = 0x40001234,
            };
            stack.Dispatcher.RunHitDamageTriggers(ctx);

            Assert.True(attacker.TryGetTag("GOTARROW", out var got));
            Assert.Equal(0x40001234u.ToString(), got);
            Assert.True(ctx.ArrowHandled);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void RangedHit_ArrowHandled_LeavesTheAmmoStackAlone()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_hitammo_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_hit_ammo_takeover]
            ON=@Hit
            LOCAL.ArrowHandled=1
            """);
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);
            CombatEngine.OnHitDamage = ctx => stack.Dispatcher.RunHitDamageTriggers(ctx);

            var lf = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1430);

            var archer = world.CreateCharacter();
            archer.IsPlayer = true;
            archer.PrivLevel = PrivLevel.GM; // era-0 roll: always hits
            archer.Str = archer.Dex = 100;
            archer.Stam = archer.MaxStam = 100;
            archer.SetStatFlag(StatFlag.War);
            world.PlaceCharacter(archer, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, archer);
            client.SetEngines(triggerDispatcher: stack.Dispatcher);
            client.BroadcastNearby = (_, _, _, _) => { };

            var target = world.CreateCharacter();
            target.Hits = target.MaxHits = 100;
            world.PlaceCharacter(target, new Point3D(105, 100, 0, 0));

            var bow = world.CreateItem();
            bow.ItemType = ItemType.WeaponBow;
            bow.BaseId = 0x13B2;
            archer.Equip(bow, Layer.TwoHanded);

            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            archer.Equip(pack, Layer.Pack);
            var arrows = world.CreateItem();
            arrows.ItemType = ItemType.WeaponArrow;
            arrows.Amount = 50;
            pack.AddItem(arrows);

            archer.FightTarget = target.Uid;
            archer.NextAttackTime = 0;

            // With the @Hit takeover a landed shot consumes NOTHING.
            archer.Events.Add(stack.Resources.ResolveDefName("e_hit_ammo_takeover"));
            client.TickCombat();
            Assert.Equal(50, arrows.Amount);

            // Without it the landed shot spends one arrow.
            archer.Events.Clear();
            target.Hits = target.MaxHits;
            archer.NextAttackTime = 0;
            archer.SetCombatSwingState(SwingState.Ready);
            client.TickCombat();
            Assert.Equal(49, arrows.Amount);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            File.Delete(tempFile);
        }
    }

    // ---- COMBAT_SLAYER on the magic path ----

    [Fact]
    public void SpellDamage_ScalesWithTheEquippedSpellbookSlayer()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            var world = new GameWorld(LoggerFactory.Create(_ => { }));
            world.InitMap(0, 6144, 4096);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Fireball,
                Name = "Fireball",
                Flags = SpellFlag.TargChar | SpellFlag.Damage | SpellFlag.Harm,
                ManaCost = 0,
                CastTimeBase = 1,
                EffectBase = 10,
                EffectScale = 0,
            });
            var engine = new SpellEngine(world, registry);

            var caster = world.CreateCharacter();
            caster.IsPlayer = true;
            caster.PrivLevel = PrivLevel.GM;
            caster.MaxMana = caster.Mana = 100;
            world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

            var npc = world.CreateCharacter();
            npc.MaxHits = 100;
            world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
            npc.SetTag("FACTION_GROUP", "0x10");   // Undead
            npc.SetTag("FACTION_SPECIES", "5");

            var book = world.CreateItem();
            book.ItemType = ItemType.Spellbook;
            book.SetTag("SLAYER_GROUP", "0x10");
            book.SetTag("SLAYER_SPECIES", "5");
            caster.Equip(book, Layer.OneHanded);

            int CastOnce()
            {
                npc.Hits = 100;
                caster.BeginCast(SpellType.Fireball, npc.Uid, npc.Position);
                Assert.True(engine.CastDone(caster));
                return 100 - npc.Hits;
            }

            // Baseline without COMBAT_SLAYER.
            Character.CombatFlags = 0;
            int baseline = CastOnce();
            Assert.True(baseline > 0);

            // Lesser slayer spellbook vs its species: x3 under COMBAT_SLAYER.
            Character.CombatFlags = (int)CombatFlags.Slayer;
            Assert.Equal(baseline * 3, CastOnce());
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }
}
