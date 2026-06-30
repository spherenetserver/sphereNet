using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Tests;

public class CombatHelperTests
{
    [Fact]
    public void ResolveAmmoSpec_UsesWeaponDefThenFallsBack()
    {
        // No weapon def → legacy defaults: arrows for bows, bolts for crossbows.
        var bow = CombatHelper.ResolveAmmoSpec(null, ItemType.WeaponBow, null);
        Assert.Equal((ushort)0, bow.BaseId);
        Assert.Equal(ItemType.WeaponArrow, bow.FallbackType);
        Assert.Equal((ushort)0x0F3F, bow.Gfx);

        var xbow = CombatHelper.ResolveAmmoSpec(null, ItemType.WeaponXBow, null);
        Assert.Equal(ItemType.WeaponBolt, xbow.FallbackType);
        Assert.Equal((ushort)0x1BFB, xbow.Gfx);

        // AMMOTYPE resolves to a specific ammo baseid; AMMOANIM sets the graphic.
        var def = new ItemDef(ResourceId.Invalid) { AmmoType = "i_custom", AmmoAnim = 0x1234 };
        var spec = CombatHelper.ResolveAmmoSpec(def, ItemType.WeaponBow,
            name => name == "i_custom" ? (ushort)0xABCD : (ushort)0);
        Assert.Equal((ushort)0xABCD, spec.BaseId);
        Assert.Equal((ushort)0x1234, spec.Gfx);

        // An AMMOTYPE that fails to resolve keeps the type-based fallback.
        var def2 = new ItemDef(ResourceId.Invalid) { AmmoType = "i_missing" };
        var spec2 = CombatHelper.ResolveAmmoSpec(def2, ItemType.WeaponBow, _ => 0);
        Assert.Equal((ushort)0, spec2.BaseId);
        Assert.Equal((ushort)0x0F3F, spec2.Gfx);
    }

    [Fact]
    public void GetWeaponRange_RangedWithoutDef_UsesIniDefaults()
    {
        Character.ArcheryMinDist = 2;
        Character.ArcheryMaxDist = 11;

        var bow = new Item { ItemType = ItemType.WeaponBow, BaseId = 0x13B2 };

        var (min, max) = CombatHelper.GetWeaponRange(bow);
        Assert.Equal(2, min);
        Assert.Equal(11, max);
    }

    [Fact]
    public void IsCombatBlockedByRegion_BlocksSafeRegion()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var target = MakeChar(world, 101, 100);

        var safe = new Region { Name = "safe_test", Flags = RegionFlag.Safe, MapIndex = 0 };
        safe.AddRect(90, 90, 110, 110);
        world.AddRegion(safe);

        Assert.True(CombatHelper.IsCombatBlockedByRegion(world, attacker, target));
    }

    [Fact]
    public void ValidateSwingPrep_RangedTooClose_RetriesWithoutAbort()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var target = MakeChar(world, 101, 100);

        Character.ArcheryMinDist = 2;
        var bow = new Item { ItemType = ItemType.WeaponBow, BaseId = 0x13B2 };

        var prep = CombatHelper.ValidateSwingPrep(
            world, attacker, target, bow, PrivLevel.Player, Environment.TickCount64, (_, _) => true);

        Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, prep.Result);
        Assert.Equal(Msg.CombatArchTooclose, prep.MessageKey);
    }

    [Fact]
    public void ValidateSwingPrep_ShieldAndBow_Aborts()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var target = MakeChar(world, 105, 100);

        Character.ArcheryMinDist = 1;
        Character.ArcheryMaxDist = 12;
        var bow = new Item { ItemType = ItemType.WeaponBow, BaseId = 0x13B2 };
        var shield = new Item { ItemType = ItemType.Shield, BaseId = 0x1B76 };
        attacker.Equip(bow, Layer.OneHanded);
        attacker.Equip(shield, Layer.TwoHanded);

        var prep = CombatHelper.ValidateSwingPrep(
            world, attacker, target, bow, PrivLevel.Player, Environment.TickCount64, (_, _) => true);

        Assert.Equal(CombatHelper.SwingPrepResult.Abort, prep.Result);
        Assert.Equal(Msg.ItemuseBowShield, prep.MessageKey);
    }

    [Fact]
    public void ValidateSwingPrep_ArcheryMovementDelay_BlocksUntilElapsed()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var target = MakeChar(world, 108, 100);

        Character.ArcheryMinDist = 1;
        Character.ArcheryMaxDist = 12;
        Character.CombatArcheryMovementDelay = 500;
        attacker.LastMoveTick = Environment.TickCount64;

        var bow = new Item { ItemType = ItemType.WeaponBow, BaseId = 0x13B2 };
        var prep = CombatHelper.ValidateSwingPrep(
            world, attacker, target, bow, PrivLevel.Player, Environment.TickCount64, (_, _) => true);

        Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, prep.Result);
        Assert.True(prep.RetryMs > 0);
    }

    [Fact]
    public void RevealOnAttack_ClearsHiddenAndInvisible()
    {
        var attacker = new Character();
        attacker.SetStatFlag(StatFlag.Hidden);
        attacker.SetStatFlag(StatFlag.Invisible);

        CombatHelper.RevealOnAttack(attacker, PrivLevel.Player);

        Assert.False(attacker.IsStatFlag(StatFlag.Hidden));
        Assert.False(attacker.IsStatFlag(StatFlag.Invisible));
    }

    [Fact]
    public void ComputeNotoriety_Murderer_IsRed()
    {
        var world = TestHarness.CreateWorld();
        var viewer = MakeChar(world, 100, 100);
        var murderer = MakeChar(world, 200, 200);
        // Source-X: red appears when murders EXCEED the threshold (6th kill at default 5).
        murderer.Kills = (short)(Character.MurderMinCount + 1);

        Assert.Equal(6, GameClient.ComputeNotoriety(world, viewer, murderer));
    }

    private static Character MakeChar(GameWorld world, short x, short y)
    {
        var ch = new Character
        {
            Name = $"C{x}_{y}",
            IsPlayer = true,
        };
        ch.MaxHits = 100;
        ch.Hits = 100;
        ch.MaxStam = 100;
        ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }
}
