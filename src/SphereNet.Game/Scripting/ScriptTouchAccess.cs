using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Scripting;

/// <summary>Touch checks for low-privilege TRYP sources (CCharStatus.cpp:1294).
/// Operates on world objects so offline characters and timer calls use the same rules.</summary>
public static class ScriptTouchAccess
{
    public static SphereConfig Configuration { get; set; } = new();

    public static bool CanTouch(Character source, ObjBase target)
    {
        var world = ObjBase.ResolveWorld?.Invoke();
        var top = target.GetTopLevelObj();
        if (world == null || source.IsDeleted || target.IsDeleted || top.IsDeleted ||
            source.MapIndex != top.Position.Map) return false;
        int distance = Math.Max(source.Position.GetDistanceTo(top.Position), Math.Abs(source.Z - top.Position.Z) / 8);
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
            bool deathImmune = item.ItemType is ItemType.Shrine or ItemType.Telescope;
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
                if (!container.IsSearchableContainer)
                {
                    if (container.ItemType == ItemType.EqTradeWindow)
                    {
                        if (container.GetTopLevelObj() != source &&
                            world.FindItem(container.Link)?.GetTopLevelObj() != source) return false;
                    }
                    else
                    {
                        if (container.GetTopLevelObj() is not Character owner ||
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
        return (ignoreLos || visible && world.CanSeeLOS(source.Position, top.Position)) &&
            (ignoreDistance || distance <= 2);
    }

    private static int VisualRange(ObjBase top) => top is Character ch ? ch.VisualRange :
        top is Item { BaseId: >= 0x4000 } && Configuration.MapViewRadar > 0
            ? Configuration.MapViewRadar : Configuration.MapViewSize;
}
