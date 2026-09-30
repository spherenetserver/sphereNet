using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class SourceXDamageVerbWave220Tests
{
    private sealed class Console : ITextConsole
    {
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) { }
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character CreateCharacter(GameWorld world, short x)
    {
        var ch = world.CreateCharacter();
        ch.MaxHits = 100;
        ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void DamageVerb_AppliesExplicitElementalSplitAndCreditsSource()
    {
        // The split is COMBAT_ELEMENTAL_ENGINE's resist (CCharFight.cpp:717-730).
        Character.CombatFlags = (int)CombatFlags.ElementalEngine;
        var world = CreateWorld();
        var source = CreateCharacter(world, 100);
        var target = CreateCharacter(world, 101);
        target.ResPhysical = 50;
        target.ResFire = 0;
        int applied = 0;
        Character? appliedSource = null;
        CombatEngine.OnDirectCharacterDamageApplied = (_, src, damage, _) =>
        {
            applied = damage;
            appliedSource = src;
        };

        // 0x12 = DAMAGE_HIT_BLUNT|DAMAGE_FIRE (game_macros.h:56,60). This test used to
        // send 0x9 as "physical+fire", but in the reference numbering 0x9 is
        // DAMAGE_GOD|DAMAGE_POISON, which skips armour altogether (below).
        string sourceUid = "0" + source.Uid.Value.ToString("X");
        Assert.True(target.TryExecuteCommand(
            "DAMAGE", $"20,0x12,{sourceUid},50,50,0,0,0", new Console()));

        Assert.Equal((short)85, target.Hits);
        Assert.Equal(15, applied);
        Assert.Same(source, appliedSource);
        Assert.Contains(target.Attackers, record => record.Uid == source.Uid && record.TotalDamage == 15);

        // DAMAGE_GOD (0x1) in 0x9: no armour calculation (CCharFight.cpp:716).
        Assert.True(target.TryExecuteCommand(
            "DAMAGE", $"20,0x9,{sourceUid},50,50,0,0,0", new Console()));
        Assert.Equal((short)65, target.Hits);
    }

    [Fact]
    public void DamageVerb_GetHitHookCanRewriteOrCancelDamage()
    {
        var world = CreateWorld();
        var target = CreateCharacter(world, 100);
        int seen = -1;
        CombatEngine.OnGetHit = ctx =>
        {
            seen = ctx.Damage;
            return 10;
        };
        target.ResFire = 50;

        // @GetHit's ARGN1 is final: Source-X applies it without a second armour pass
        // (CCharFight.cpp:773 onwards). This test used to expect the 10 cut again by
        // the fire resist (5). 010 = DAMAGE_FIRE; pre-AOS armour of 0 leaves 40.
        Assert.True(target.TryExecuteCommand("DAMAGE", "40,010", new Console()));
        Assert.Equal(40, seen);
        Assert.Equal((short)90, target.Hits);

        CombatEngine.OnGetHit = ctx =>
        {
            ctx.Cancelled = true;
            return 0;
        };
        Assert.True(target.TryExecuteCommand("DAMAGE", "40,010", new Console()));
        Assert.Equal((short)90, target.Hits);
    }

    [Fact]
    public void DamageVerb_ReducesAndBreaksItemDurability()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        item.ItemType = SphereNet.Core.Enums.ItemType.Armor;
        item.HitsMax = 3;
        item.HitsCur = 3;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        Item? broken = null;
        CombatEngine.BreakOnZeroHits = true;
        CombatEngine.OnItemBroken = candidate => broken = candidate;

        // CItem::OnTakeDamage wears armour ONE hit point per blow whatever the damage
        // (--m_wHitsCur, CItem.cpp:5930) and destroys it when it is at its last
        // (:5915-5925). This test used to expect the whole blow off the pool (10-4=6).
        Assert.True(item.TryExecuteCommand("DAMAGE", "4,0x1000", new Console()));
        Assert.Equal(2, item.HitsCur);
        Assert.True(item.TryExecuteCommand("DAMAGE", "8,0x1000", new Console()));
        Assert.Equal(1, item.HitsCur);
        Assert.Null(broken);

        Assert.True(item.TryExecuteCommand("DAMAGE", "8,0x1000", new Console()));
        Assert.Equal(0, item.HitsCur);
        Assert.Same(item, broken);
    }

    [Fact]
    public void DamageVerb_ItemDamageTriggerCanCancel()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        item.HitsMax = 10;
        item.HitsCur = 10;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        CombatEngine.OnItemDamaged = (_, _, _, _) => true;

        Assert.True(item.TryExecuteCommand("DAMAGE", "9", new Console()));

        Assert.Equal(10, item.HitsCur);
    }
}
