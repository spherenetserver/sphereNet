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
        var world = CreateWorld();
        var source = CreateCharacter(world, 100);
        var target = CreateCharacter(world, 101);
        target.ResPhysical = 50;
        target.ResFire = 0;
        int applied = 0;
        Character? appliedSource = null;
        CombatEngine.OnDirectCharacterDamageApplied = (_, src, damage) =>
        {
            applied = damage;
            appliedSource = src;
        };

        string sourceUid = "0" + source.Uid.Value.ToString("X");
        Assert.True(target.TryExecuteCommand(
            "DAMAGE", $"20,0x9,{sourceUid},50,50,0,0,0", new Console()));

        Assert.Equal((short)85, target.Hits);
        Assert.Equal(15, applied);
        Assert.Same(source, appliedSource);
        Assert.Contains(target.Attackers, record => record.Uid == source.Uid && record.TotalDamage == 15);
    }

    [Fact]
    public void DamageVerb_GetHitHookCanRewriteOrCancelDamage()
    {
        var world = CreateWorld();
        var target = CreateCharacter(world, 100);
        CombatEngine.OnDirectDamage = ctx =>
        {
            ctx.FirePercent = 100;
            return 10;
        };
        target.ResFire = 50;

        Assert.True(target.TryExecuteCommand("DAMAGE", "40,0x8", new Console()));
        Assert.Equal((short)95, target.Hits);

        CombatEngine.OnDirectDamage = ctx =>
        {
            ctx.Cancelled = true;
            return 0;
        };
        Assert.True(target.TryExecuteCommand("DAMAGE", "40,0x8", new Console()));
        Assert.Equal((short)95, target.Hits);
    }

    [Fact]
    public void DamageVerb_ReducesAndBreaksItemDurability()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        item.HitsMax = 10;
        item.HitsCur = 10;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        Item? broken = null;
        CombatEngine.BreakOnZeroHits = true;
        CombatEngine.OnItemBroken = candidate => broken = candidate;

        Assert.True(item.TryExecuteCommand("DAMAGE", "4,0x1000", new Console()));
        Assert.Equal(6, item.HitsCur);
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
        CombatEngine.OnItemDamaged = (_, _) => true;

        Assert.True(item.TryExecuteCommand("DAMAGE", "9", new Console()));

        Assert.Equal(10, item.HitsCur);
    }
}
