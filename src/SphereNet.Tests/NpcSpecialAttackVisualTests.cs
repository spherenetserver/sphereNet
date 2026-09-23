using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

// NPC special-attack visuals against the Source-X reference: the ranged
// projectile art (TDATA4 / AMMOANIM*), ammo-free TDATA3=0 weapons, ID= TDATA
// inheritance, the weapon @Damage / @Hit SRC contract, blood hue, spell bolt
// motion, breath and thrown-object effects.
[Collection("DefinitionLoaderSerial")]
public class NpcSpecialAttackVisualTests : IDisposable
{
    private readonly string _defFile = Path.Combine(Path.GetTempPath(),
        $"spherenet_npcspecial_{Guid.NewGuid():N}.scp");

    public void Dispose()
    {
        DefinitionLoader.ResetForTests();
        if (File.Exists(_defFile)) File.Delete(_defFile);
    }

    private const string BowDefs = """
        [ITEMDEF 0f3f]
        DEFNAME=i_arrow
        TYPE=t_weapon_arrow

        [ITEMDEF 0f42]
        DEFNAME=i_arrow_x

        [ITEMDEF 013b2]
        DEFNAME=i_bow
        TYPE=t_weapon_bow
        TDATA3=i_arrow
        TDATA4=i_arrow_x
        RANGE=1,15

        [ITEMDEF i_bow_power]
        DEFNAME=i_bow_power
        ID=i_bow

        [ITEMDEF i_bow_exp]
        DEFNAME=i_bow_exp
        ID=I_BOW
        RANGE=1,15
        TDATA1=0
        TDATA3=0
        TDATA4=01c1c

        [CHARDEF 0400]
        DEFNAME=c_nobleed
        BLOODCOLOR=-1

        [CHARDEF 0401]
        DEFNAME=c_greenbleed
        BLOODCOLOR=07EB
        """;

    private ResourceHolder LoadDefs(string text)
    {
        File.WriteAllText(_defFile, text);
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        return resources;
    }

    private static ItemDef Def(string name) =>
        DefinitionLoader.GetItemDef(DefinitionLoader.ResolveItemDefIndexByName(name))!;

    private static byte[] Bytes(PacketWriter p) => p.Build().Span.ToArray();

    /// <summary>A weapon made from a named def the way the engine makes one
    /// (ApplyInstanceMetadata): the art's graphic as BaseId, the definition in
    /// the SCRIPTDEF routing tag.</summary>
    private static Item MakeWeapon(GameWorld world, string defName, ItemType type)
    {
        int index = DefinitionLoader.ResolveItemDefIndexByName(defName);
        var def = DefinitionLoader.GetItemDef(index)!;
        var weapon = world.CreateItem();
        weapon.BaseId = def.DispIndex != 0 ? def.DispIndex : (ushort)index;
        weapon.ItemType = type;
        weapon.SetTag("SCRIPTDEF", index.ToString());
        return weapon;
    }

    // ---- ranged weapon ammo / projectile art -------------------------------

    [Fact]
    public void IdAlias_RangedWeapon_InheritsBaseTData_ButAnExplicitZeroWins()
    {
        LoadDefs(BowDefs);

        // Source-X CopyBasic: ID=i_bow copies TDATA3/4, later TDATAn lines override.
        var power = Def("i_bow_power");
        Assert.Equal(ItemType.WeaponBow, power.Type);
        Assert.Equal(0x0F3Fu, power.TData3);
        Assert.Equal(0x0F42u, power.TData4);

        var bomb = Def("i_bow_exp");
        Assert.Equal(ItemType.WeaponBow, bomb.Type);
        Assert.Equal(0u, bomb.TData3);
        Assert.Equal(0x1C1Cu, bomb.TData4);
    }

    [Fact]
    public void AmmoSpec_Tdata3NamesTheAmmo_Tdata4TheFlight_AndTdata3ZeroNeedsNone()
    {
        LoadDefs(BowDefs);

        var power = CombatHelper.ResolveAmmoSpec(Def("i_bow_power"), ItemType.WeaponBow, null);
        Assert.True(power.RequiresAmmo);
        Assert.Equal((ushort)0x0F3F, power.BaseId);
        Assert.Equal((ushort)0x0F42, power.Gfx);

        var bomb = CombatHelper.ResolveAmmoSpec(Def("i_bow_exp"), ItemType.WeaponBow, null);
        Assert.False(bomb.RequiresAmmo);
        Assert.Equal((ushort)0x1C1C, bomb.Gfx);
    }

    [Fact]
    public void RangedProjectile_FliesTheWeaponArt_HuedWhenAmmoAnimHueIsSet()
    {
        LoadDefs(BowDefs);
        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        var target = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(target, new Point3D(105, 100, 0, 0));

        var bow = MakeWeapon(world, "i_bow_exp", ItemType.WeaponBow);
        Assert.Equal((ushort)0x13B2, bow.BaseId); // shares i_bow's art

        var sent = new List<PacketWriter>();
        GameClient.BroadcastRangedProjectile(npc, target, bow, (_, _, p, _) => sent.Add(p));
        var pkt = Assert.Single(sent);
        var b = Bytes(pkt);
        Assert.Equal(0x70, b[0]);
        Assert.Equal(0, b[1]);                          // EFFECT_BOLT
        Assert.Equal(0x1C1C, (b[10] << 8) | b[11]);     // the bomb, not an arrow
        Assert.Equal(0, b[23]);                         // BOLT: loop does not apply
        Assert.Equal(0, b[26]);                         // oneDirection=false
        Assert.Equal(0, b[27]);                         // no explode

        // AMMOANIMHUE on the instance upgrades to the hued 0xC0 packet.
        bow.SetTag("AMMOANIMHUE", "237");
        sent.Clear();
        GameClient.BroadcastRangedProjectile(npc, target, bow, (_, _, p, _) => sent.Add(p));
        var hued = Bytes(Assert.Single(sent));
        Assert.Equal(0xC0, hued[0]);
        // Source-X writes hue - 1 (send.cpp:2051).
        Assert.Equal(236, (hued[28] << 24) | (hued[29] << 16) | (hued[30] << 8) | hued[31]);
    }

    [Fact]
    public void NpcRangedShot_IsEmittedBeforeTheHitResolves()
    {
        LoadDefs(BowDefs);
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereNet.Core.Configuration.SphereConfig());

        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Monster;
        npc.Str = npc.Dex = 100;
        npc.Stam = npc.MaxStam = 100;
        npc.Hits = npc.MaxHits = 100;
        npc.SetSkill(SkillType.Archery, 1000);
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.IsOnline = true;
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(105, 100, 0, 0));
        world.AddOnlinePlayer(target);
        world.OnTick();

        // The pack's bomb bow: TDATA3=0, no ammo anywhere.
        var bow = MakeWeapon(world, "i_bow_exp", ItemType.WeaponBow);
        Assert.Equal((ushort)0x13B2, bow.BaseId); // shares i_bow's art
        npc.Equip(bow, Layer.TwoHanded);

        var order = new List<string>();
        ai.OnNpcRangedShot = (_, _, w) => order.Add($"shot:{w.Uid.Value:X}");
        ai.OnNpcAttack = (_, _, _, _, _) => order.Add("attack");
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = ctx => { order.Add("hit"); return ctx.Damage; };
            npc.FightTarget = target.Uid;
            npc.NextNpcActionTime = 0;
            npc.NextAttackTime = 0;
            for (int i = 0; i < 5 && !order.Contains("attack"); i++)
            {
                ai.OnTickAction(npc);
                npc.NextNpcActionTime = 0;
            }
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
        }

        Assert.Contains("attack", order);
        int shot = order.IndexOf($"shot:{bow.Uid.Value:X}");
        Assert.True(shot >= 0, string.Join(",", order));
        Assert.True(shot < order.IndexOf("attack"));
        if (order.Contains("hit"))
            Assert.True(shot < order.IndexOf("hit"));
    }

    [Fact]
    public void PlayerArcher_WithATdata3ZeroBow_FiresWithoutArrows()
    {
        LoadDefs(BowDefs);
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1471);

        var archer = world.CreateCharacter();
        archer.IsPlayer = true;
        archer.PrivLevel = PrivLevel.GM; // guaranteed hit, no LOS noise
        archer.Str = archer.Dex = 100;
        archer.Stam = archer.MaxStam = 100;
        archer.SetStatFlag(StatFlag.War);
        world.PlaceCharacter(archer, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, archer);
        var sent = new List<PacketWriter>();
        client.BroadcastNearby = (_, _, p, _) => sent.Add(p);

        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(105, 100, 0, 0));

        var bow = MakeWeapon(world, "i_bow_exp", ItemType.WeaponBow);
        Assert.Equal((ushort)0x13B2, bow.BaseId); // shares i_bow's art
        archer.Equip(bow, Layer.TwoHanded);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        archer.Equip(pack, Layer.Pack);

        archer.FightTarget = target.Uid;
        archer.NextAttackTime = 0;
        client.TickCombat();

        Assert.Contains(sent, p =>
        {
            var b = Bytes(p);
            return b[0] == 0x70 && b[1] == 0 && ((b[10] << 8) | b[11]) == 0x1C1C;
        });
    }

    // ---- weapon trigger SRC contract ---------------------------------------

    [Fact]
    public void WeaponDamageTrigger_SeesTheStruckCharAsSrc_EvenWithoutHitpoints()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_wdmg_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_wear_always_probe]
            ON=@Hit
            LOCAL.ItemDamageChance=100

            [EVENTS ei_boom_probe]
            ON=@Damage
            SRC.TAG.BOOMED=<ARGN1>
            """);
        var savedHook = CombatEngine.OnHitDamage;
        var savedDamaged = CombatEngine.OnItemDamaged;
        bool savedEnabled = CombatEngine.DurabilityEnabled;
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);
            CombatEngine.OnHitDamage = ctx => stack.Dispatcher.RunHitDamageTriggers(ctx);
            // Production wiring (Program.EngineWiring OnItemDamaged).
            CombatEngine.OnItemDamaged = (item, dmg, src, type) =>
                stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Damage,
                    new TriggerArgs { CharSrc = src, ItemSrc = item, N1 = dmg, N2 = (long)type })
                == TriggerResult.True;
            CombatEngine.DurabilityEnabled = true;

            var world = TestHarness.CreateWorld();
            var attacker = world.CreateCharacter();
            attacker.IsPlayer = true;
            attacker.PrivLevel = PrivLevel.GM;
            attacker.Str = attacker.Dex = 100;
            world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
            var target = world.CreateCharacter();
            target.Hits = target.MaxHits = 100;
            world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;               // no def, no HITPOINTS
            attacker.Equip(sword, Layer.OneHanded);
            sword.Events.Add(stack.Resources.ResolveDefName("ei_boom_probe"));
            attacker.Events.Add(stack.Resources.ResolveDefName("e_wear_always_probe"));

            int dealt = 0;
            for (int i = 0; i < 20 && dealt <= 0; i++)
            {
                target.Hits = target.MaxHits;
                dealt = CombatEngine.ResolveAttack(attacker, target, sword,
                    CombatHelper.ActiveCombatFlags, -1, -1, 0, out _);
            }
            Assert.True(dealt > 0);

            // SRC = the victim (the pack's bomb bow draws SRC.EFFECT there), ARGN1 = the blow.
            Assert.True(target.TryGetTag("BOOMED", out var boomed), "weapon @Damage did not reach the victim");
            Assert.True(int.Parse(boomed!) > 0);
            Assert.False(attacker.TryGetTag("BOOMED", out _));
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            CombatEngine.OnItemDamaged = savedDamaged;
            CombatEngine.DurabilityEnabled = savedEnabled;
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void WeaponHitTrigger_SrcIsTheVictim_ArgoTheWielder()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_whit_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS ei_hit_probe]
            ON=@Hit
            SRC.TAG.STRUCK=1
            ARGO.TAG.WIELDER=1
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
            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            attacker.Equip(sword, Layer.OneHanded);
            sword.Events.Add(stack.Resources.ResolveDefName("ei_hit_probe"));

            stack.Dispatcher.RunHitDamageTriggers(new HitDamageContext
            {
                Attacker = attacker,
                Target = target,
                Weapon = sword,
                Damage = 10,
                ItemDamageLayer = Layer.Helm,
            });

            Assert.True(target.TryGetTag("STRUCK", out _));
            Assert.True(attacker.TryGetTag("WIELDER", out _));
            Assert.False(attacker.TryGetTag("STRUCK", out _));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ---- blood ----------------------------------------------------------------

    [Fact]
    public void BloodColor_MinusOneBleedsNothing_HexHuesTheBlood()
    {
        LoadDefs(BowDefs);
        Assert.Equal((short)-1, DefinitionLoader.GetCharDef(0x400)!.BloodColor);
        Assert.Equal((short)0x07EB, DefinitionLoader.GetCharDef(0x401)!.BloodColor);

        var world = TestHarness.CreateWorld();
        var dry = world.CreateCharacter();
        dry.CharDefIndex = 0x400;
        world.PlaceCharacter(dry, new Point3D(100, 100, 0, 0));
        var wet = world.CreateCharacter();
        wet.CharDefIndex = 0x401;
        world.PlaceCharacter(wet, new Point3D(120, 100, 0, 0));

        GameClient.EmitBloodSplat(world, dry);
        Assert.Empty(world.GetItemsInRange(dry.Position, 2));

        GameClient.EmitBloodSplat(world, wet);
        var blood = world.GetItemsInRange(wet.Position, 2).ToList();
        Assert.NotEmpty(blood);
        Assert.All(blood, b => Assert.Equal((ushort)0x07EB, b.Hue.Value));

        // An instance BLOODCOLOR=-1 turns a bleeding creature dry too.
        Assert.True(wet.TrySetProperty("BLOODCOLOR", "-1"));
        foreach (var b in blood) world.RemoveItem(b);
        GameClient.EmitBloodSplat(world, wet);
        Assert.Empty(world.GetItemsInRange(wet.Position, 2));
    }

    // ---- spells -------------------------------------------------------------

    [Fact]
    public void SpellFx_BoltFliesFromTheCaster_TargPlaysOnTheTarget()
    {
        var world = TestHarness.CreateWorld();
        var caster = world.CreateCharacter();
        var target = world.CreateCharacter();
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(target, new Point3D(104, 100, 0, 0));

        var bolt = new SpellDef { EffectId = 0x36D4, Flags = SpellFlag.FxBolt | SpellFlag.Harm };
        var b = Bytes(Assert.Single(GameClient.BuildSpellCastFx(caster, target, bolt)));
        Assert.Equal(0, b[1]);                                   // EFFECT_BOLT, not lightning (1)
        Assert.Equal(caster.Uid.Value, (uint)((b[2] << 24) | (b[3] << 16) | (b[4] << 8) | b[5]));
        Assert.Equal(target.Uid.Value, (uint)((b[6] << 24) | (b[7] << 16) | (b[8] << 8) | b[9]));
        Assert.Equal(100, (b[12] << 8) | b[13]);                 // starts at the caster
        Assert.Equal(5, b[22]);                                  // speed
        Assert.Equal(0, b[26]);                                  // rotates along its path
        Assert.Equal(1, b[27]);                                  // harmful bolt explodes

        var targ = new SpellDef { EffectId = 0x376A, Flags = SpellFlag.FxTarg | SpellFlag.Good };
        var t = Bytes(Assert.Single(GameClient.BuildSpellCastFx(caster, target, targ)));
        Assert.Equal(3, t[1]);                                   // EFFECT_OBJ
        Assert.Equal(0, t[22]);
        Assert.Equal(15, t[23]);
        Assert.Equal(1, t[26]);                                  // oneDirection
        Assert.Equal(0, t[27]);                                  // GOOD spell: no explode

        Assert.Empty(GameClient.BuildSpellCastFx(caster, target,
            new SpellDef { EffectId = 0x376A }));
    }

    // ---- breath / throw ---------------------------------------------------------

    [Fact]
    public void Breath_DefaultsToAFireballBolt_TagsOverride()
    {
        var npc = new Character();
        var d = NpcAI.ResolveBreath(npc);
        Assert.Equal((byte)0, d.Motion);
        Assert.Equal((ushort)0x36D4, d.Gfx);
        Assert.Equal(DamageType.Fire, d.DamageType);
        Assert.Equal(100, d.Fire);

        npc.SetTag("BREATH.ANIM", "0379f");
        npc.SetTag("BREATH.HUE", "0480");
        npc.SetTag("BREATH.DAMTYPE", "016"); // DAMAGE_COLD 0x10
        d = NpcAI.ResolveBreath(npc);
        Assert.Equal((ushort)0x379F, d.Gfx);
        Assert.Equal((ushort)0x480, d.Hue);
        Assert.Equal(100, d.Cold);
        Assert.Equal(0, d.Fire);
    }

    [Fact]
    public void ThrownObject_IsARockOrTheThrowObj_NeverADagger()
    {
        var npc = new Character();
        for (int i = 0; i < 50; i++)
        {
            ushort gfx = NpcAI.ResolveThrowGraphic(npc);
            Assert.True(gfx is >= 0x134F and <= 0x136C, $"0x{gfx:X}");
        }
        npc.SetTag("THROWOBJ", "01363");
        Assert.Equal((ushort)0x1363, NpcAI.ResolveThrowGraphic(npc));
    }
}
