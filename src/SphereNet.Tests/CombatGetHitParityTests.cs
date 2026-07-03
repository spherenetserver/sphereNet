using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

// Combat wave C1 — the Source-X @GetHit armor-damage contract
// (CChar::OnTakeDamage, CCharFight.cpp:750): LOCAL.ItemDamageLayer /
// ItemDamageChance / DamagePercent* locals, the item @GetHit fired on the
// script-chosen worn piece, and the elemental split edge cases.
public class CombatGetHitParityTests
{
    private static Character MakeChar()
    {
        var ch = new Character();
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 50; ch.Hits = 50;
        return ch;
    }

    [Fact]
    public void GetHit_SeedsArmorDamageLocals_AndItemGetHitFiresOnScriptChosenLayer()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_gethit_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_gethit_probe]
            ON=@GetHit
            TAG.SEENLAYER=<LOCAL.ItemDamageLayer>
            TAG.SEENCHANCE=<LOCAL.ItemDamageChance>
            LOCAL.ItemDamageLayer=13

            [EVENTS e_armor_gethit_probe]
            ON=@GetHit
            TAG.ARMORHIT=<ARGN1>
            ARGN1=5
            LOCAL.ItemDamageChance=0
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
            target.Events.Add(stack.Resources.ResolveDefName("e_gethit_probe"));

            // Armor on the CHEST (layer 13) — the char script redirects the
            // armor-damage roll there, away from the engine-seeded Helm.
            var chest = world.CreateItem();
            chest.Events.Add(stack.Resources.ResolveDefName("e_armor_gethit_probe"));
            target.Equip(chest, Layer.Chest);

            var ctx = new HitDamageContext
            {
                Attacker = attacker,
                Target = target,
                Damage = 10,
                ItemDamageLayer = Layer.Helm,
            };
            int dmg = stack.Dispatcher.RunHitDamageTriggers(ctx);

            // The char @GetHit saw the engine-seeded roll...
            Assert.True(target.TryGetTag("SEENLAYER", out var l) && l == ((int)Layer.Helm).ToString());
            Assert.True(target.TryGetTag("SEENCHANCE", out var c) && c == "25");
            // ...its layer redirect routed the item @GetHit onto the chest piece
            // (previously only a Layer.TwoHanded shield ever got item @GetHit)...
            Assert.True(chest.TryGetTag("ARMORHIT", out var a) && a == "10");
            // ...whose ARGN1 write threads into the final damage, and the locals
            // write back into the context for the engine's durability roll.
            Assert.Equal(5, dmg);
            Assert.Equal(Layer.Chest, ctx.ItemDamageLayer);
            Assert.Equal(0, ctx.ItemDamageChance);
            Assert.False(ctx.Cancelled);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetHit_ElementalSplit_ExposedAsDamagePercentLocals()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_gethit_elem_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_gethit_elem_probe]
            ON=@GetHit
            TAG.PCTPHYS=<LOCAL.DamagePercentPhysical>
            TAG.PCTFIRE=<LOCAL.DamagePercentFire>
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
            target.Events.Add(stack.Resources.ResolveDefName("e_gethit_elem_probe"));

            var ctx = new HitDamageContext
            {
                Attacker = attacker,
                Target = target,
                Damage = 10,
                ItemDamageLayer = Layer.Helm,
                Elemental = true,
                DamPercentPhysical = 75,
                DamPercentFire = 25,
            };
            stack.Dispatcher.RunHitDamageTriggers(ctx);

            Assert.True(target.TryGetTag("PCTPHYS", out var p) && p == "75");
            Assert.True(target.TryGetTag("PCTFIRE", out var f) && f == "25");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetHit_Return1_CancelsHitAndMarksContext()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_gethit_ret1_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_gethit_veto]
            ON=@GetHit
            RETURN 1
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
            target.Events.Add(stack.Resources.ResolveDefName("e_gethit_veto"));

            var ctx = new HitDamageContext
            {
                Attacker = attacker,
                Target = target,
                Damage = 10,
                ItemDamageLayer = Layer.Helm,
            };
            int dmg = stack.Dispatcher.RunHitDamageTriggers(ctx);

            // RETURN 1 cancels the hit; Cancelled makes CombatEngine skip the
            // durability roll too (Source-X returns 0 before it).
            Assert.Equal(0, dmg);
            Assert.True(ctx.Cancelled);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ---- Elemental split edge cases (Source-X CCharFight.cpp:714-730) ----

    [Fact]
    public void ApplyElementalDamageSplit_FullResist_AllowsZeroDamage()
    {
        var attacker = MakeChar();
        var target = MakeChar();
        attacker.DamPhysical = 0; // pure-fire hitter
        attacker.DamFire = 100;
        target.ResFire = 100;

        // Source-X's elemental formula lets a fully-resisted hit deal 0; the
        // old Math.Max(1, total) floor forced 1 damage through a 100% resist.
        Assert.Equal(0, CombatEngine.ApplyElementalDamageSplit(attacker, target, 20, null));
    }

    [Fact]
    public void ApplyElementalDamageSplit_UnsetPhysical_IsTheRemainderOfTheSplit()
    {
        var attacker = MakeChar();
        var target = MakeChar();
        // DAMPHYSICAL zeroed with DAMFIRE=25: Source-X assumes physical is the
        // remaining 75% (iDmgPhysical = 100 - elemental sum), not a 100%-fire hit.
        attacker.DamPhysical = 0;
        attacker.DamFire = 25;
        target.ResFire = 100;
        target.ResPhysical = 0;

        Assert.Equal(75, CombatEngine.ApplyElementalDamageSplit(attacker, target, 100, null));
    }

    // ---- Durability wear via the context roll (ResolveAttack tail) ----

    [Fact]
    public void ResolveAttack_WearsTheContextLayerItem_HonoringChanceAndCancel()
    {
        var savedHook = CombatEngine.OnHitDamage;
        bool savedEnabled = CombatEngine.DurabilityEnabled;
        int savedChance = CombatEngine.DurabilityLossChance;
        int savedMin = CombatEngine.DurabilityLossMin;
        int savedMax = CombatEngine.DurabilityLossMax;
        bool savedBreak = CombatEngine.BreakOnZeroHits;
        try
        {
            CombatEngine.DurabilityEnabled = true;
            CombatEngine.DurabilityLossChance = 100;
            CombatEngine.DurabilityLossMin = 1;
            CombatEngine.DurabilityLossMax = 1;
            CombatEngine.BreakOnZeroHits = false;

            var attacker = MakeChar();
            var target = MakeChar();
            var chest = new SphereNet.Game.Objects.Items.Item { HitsMax = 50, HitsCur = 50 };
            target.Equip(chest, Layer.Chest);

            int ResolveHit()
            {
                for (int i = 0; i < 400; i++)
                {
                    target.Hits = target.MaxHits;
                    int d = CombatEngine.ResolveAttack(attacker, target, null);
                    if (d >= 0) return d;
                }
                return -1;
            }

            // ItemDamageChance=100 on a fixed layer: the chest wears every hit.
            CombatEngine.OnHitDamage = ctx =>
            {
                ctx.ItemDamageLayer = Layer.Chest;
                ctx.ItemDamageChance = 100;
                return ctx.Damage;
            };
            Assert.True(ResolveHit() >= 0);
            Assert.Equal(49, chest.HitsCur);

            // ItemDamageChance=0: no wear even on a landed hit.
            CombatEngine.OnHitDamage = ctx =>
            {
                ctx.ItemDamageLayer = Layer.Chest;
                ctx.ItemDamageChance = 0;
                return ctx.Damage;
            };
            Assert.True(ResolveHit() >= 0);
            Assert.Equal(49, chest.HitsCur);

            // A cancelled hit (trigger RETURN 1) skips the durability roll —
            // Source-X returns before it.
            CombatEngine.OnHitDamage = ctx =>
            {
                ctx.ItemDamageLayer = Layer.Chest;
                ctx.ItemDamageChance = 100;
                ctx.Cancelled = true;
                return 0;
            };
            Assert.True(ResolveHit() >= 0);
            Assert.Equal(49, chest.HitsCur);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            CombatEngine.DurabilityEnabled = savedEnabled;
            CombatEngine.DurabilityLossChance = savedChance;
            CombatEngine.DurabilityLossMin = savedMin;
            CombatEngine.DurabilityLossMax = savedMax;
            CombatEngine.BreakOnZeroHits = savedBreak;
        }
    }
}

