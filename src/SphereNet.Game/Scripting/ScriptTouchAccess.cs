using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Scripting;

/// <summary>CChar::CanTouch (CCharStatus.cpp:1293-1430): the touch test behind TRYP,
/// a double-click, a pick-up, a mount and the skills that reach for something.
/// Operates on world objects so offline characters and timer calls use the same rules.</summary>
public static class ScriptTouchAccess
{
    public static SphereConfig Configuration { get; set; } = new();

    public static bool CanTouch(Character source, ObjBase target) => CanTouch(source, target, null);

    /// <param name="world">The world the objects live in; the ambient one when null.
    /// A client handler passes its own, so the test does not depend on the static
    /// resolver being wired.</param>
    public static bool CanTouch(Character source, ObjBase target, World.GameWorld? world)
    {
        world ??= ObjBase.ResolveWorld?.Invoke();
        if (world == null || source.IsDeleted || target.IsDeleted) return false;
        ObjBase top = TopOf(world, target);
        // IsPriv(PRIV_GM) - GM mode, not the privilege level alone: immune to the
        // dead / frozen refusals (:1312), ranked against a character it reaches onto
        // (:1373-1374), let into any container (CanSeeInContainer) and excused the
        // line-of-sight and distance tests (:1410-1411).
        bool gm = source.IsGmMode;
        if (top.IsDeleted) return false;
        bool sameMap = source.MapIndex == top.Position.Map;
        if (!sameMap && !gm) return false;
        int distance = sameMap
            ? Math.Max(source.Position.GetDistanceTo(top.Position), Math.Abs(source.Z - top.Position.Z) / 8)
            : int.MaxValue;
        var item = target as Item;
        if (item != null)
        {
            if (item.ItemType == ItemType.SignGump)
                return distance <= VisualRange(top);
            if (item.ItemType == ItemType.ArcheryButte)
            {
                var weapon = source.GetEquippedItem(Layer.TwoHanded) ?? source.GetEquippedItem(Layer.OneHanded);
                if (Combat.CombatHelper.IsRangedWeapon(weapon))
                    return distance <= Combat.CombatHelper.GetWeaponRange(weapon).Max;
            }
            if (item.ItemType is ItemType.ShipPlank or ItemType.ShipSide or ItemType.ShipSideLocked or ItemType.Rope &&
                !source.IsStatFlag(StatFlag.Sleeping | StatFlag.Freeze | StatFlag.Stone))
                return distance <= Configuration.MaxShipPlankTeleport;
            bool deathImmune = gm || item.ItemType is ItemType.Shrine or ItemType.Telescope;
            bool freezeImmune = top == source && item.ItemType is ItemType.Container or ItemType.ContainerLocked;
            if (!deathImmune &&
                (source.IsStatFlag(StatFlag.Dead | StatFlag.Sleeping | StatFlag.Stone) ||
                 source.IsStatFlag(StatFlag.Freeze) && !freezeImmune && !item.IsAttr(ObjAttributes.CanUseParalyzed)))
                return false;
        }

        var other = top as Character;
        if (top != source)
        {
            if (other != null)
            {
                if (gm)
                    return source.PrivLevel > other.PrivLevel || source.PrivLevel == PrivLevel.Owner;
                if (other.IsDead && source.Action is SkillType.Healing or SkillType.Veterinary) return true;
                if (other.IsStatFlag(StatFlag.Dead | StatFlag.Stone) ||
                    (CharDefHelper.GetCanFlags(source) & CanFlags.C_Statue) != 0) return false;
            }
            int depth = 0;
            for (var nested = item; nested != null && nested.ContainedIn.IsValid;)
            {
                if (++depth > 64) return false;
                var parent = world.FindObject(nested.ContainedIn);
                if (parent is not Item container) break;
                if (!container.IsSearchableContainer && !gm)
                {
                    if (container.ItemType == ItemType.EqTradeWindow)
                    {
                        if (TopOf(world, container) != source &&
                            (world.FindItem(container.Link) is not { } linked || TopOf(world, linked) != source)) return false;
                    }
                    else
                    {
                        if (TopOf(world, container) is not Character owner ||
                            owner != source && !owner.HasOwner(source.Uid)) return false;
                        if (container.ItemType is ItemType.EqBankBox or ItemType.EqVendorBox &&
                            container.MoreP != source.Position) return false;
                    }
                }
                nested = container;
            }
        }
        else
        {
            // Own-bank access expires on movement, including items nested in bags.
            int depth = 0;
            for (var nested = item; nested != null; nested = world.FindItem(nested.ContainedIn))
            {
                if (++depth > 64) return false;
                if ((nested.ItemType == ItemType.EqBankBox || nested.EquipLayer == Layer.BankBox) &&
                    nested.MoreP != source.Position) return false;
            }
        }

        if (gm)
            return true;

        var sourceCan = CharDefHelper.GetCanFlags(source);
        var targetCan = other != null ? CharDefHelper.GetCanFlags(other) : CanFlags.None;
        // Source-X uses the outermost item after walking external containers.
        var flagItem = top != source && top is Character ? null : top as Item ?? item;
        var itemCan = flagItem != null
            ? DefinitionLoader.GetItemDef(ItemDefHelper.ResolveInstanceDefIndex(flagItem))?.Can ?? CanFlags.None
            : CanFlags.None;
        bool ignoreLos = ((sourceCan | targetCan) & CanFlags.C_DcIgnoreLOS) != 0 || (itemCan & CanFlags.I_DcIgnoreLOS) != 0;
        bool ignoreDistance = ((sourceCan | targetCan) & CanFlags.C_DcIgnoreDist) != 0 || (itemCan & CanFlags.I_DcIgnoreDist) != 0;
        bool visible = top == source || distance <= VisualRange(top) &&
            (top is not Item visibleItem || !visibleItem.IsAttr(ObjAttributes.Invis)) &&
            (other == null || (!source.IsDead || source.CanSeeAsDead(other)) &&
                (!other.IsPlayer || other.IsOnline || other.IsClientLingering || source.AllShow) &&
                (!other.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible | StatFlag.Insubstantial) ||
                 source.AllShow || source.PrivLevel >= PrivLevel.Counsel && source.PrivLevel >= other.PrivLevel));
        // What I carry needs no line of sight to myself (CCharStatus.cpp:1400-1412).
        return (ignoreLos || visible && (top == source || world.CanSeeLOSFor(source, top))) &&
            (ignoreDistance || distance <= 2);
    }

    /// <summary>GetTopLevelObj through the given world rather than the ambient one.</summary>
    private static ObjBase TopOf(World.GameWorld world, ObjBase obj)
    {
        for (int depth = 0; depth < 64 && obj is Item held && held.ContainedIn.IsValid; depth++)
        {
            var parent = world.FindObject(held.ContainedIn);
            if (parent == null) break;
            obj = parent;
        }
        return obj;
    }

    private static int VisualRange(ObjBase top) => top is Character ch ? ch.VisualRange :
        top is Item { BaseId: >= 0x4000 } && Configuration.MapViewRadar > 0
            ? Configuration.MapViewRadar : Configuration.MapViewSize;
}
