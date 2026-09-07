using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.Objects.Items;

/// <summary>
/// Where an object a SCRIPT just created ends up, when the script named a parent for
/// it. Source-X routes the parent field of NEWITEM through CItem::LoadSetContainer
/// (CItem.cpp:2516): an item parent takes the object as content, a character parent
/// wears it at the layer its definition declares, and the layer machinery
/// (CChar::LayerAdd, CCharAct.cpp:251 -> CanEquipLayer, CCharStatus.cpp:320) bounces
/// into the pack whatever the slot refuses.
///
/// The fourth field of NEWITEM chooses BETWEEN TWO PATHS (CScriptObj.cpp:1360): with
/// it set, and only for a character parent, the object goes through CChar::ItemEquip
/// (CCharAct.cpp:3278), which derives the layer itself - so the strength requirement
/// applies - and lets @EquipTest refuse the item. Without it, the load-style path
/// above is used and no strength test is made, because CanEquipLayer only reaches
/// CanEquipStr when it has to work the layer out for itself (CCharStatus.cpp:326).
/// </summary>
public static class ScriptItemPlacement
{
    /// <summary>What actually happened to the object.</summary>
    public enum Outcome
    {
        /// <summary>Nowhere: the parent could not take it. The caller owns the
        /// cleanup, and the script must not be told the placement worked.</summary>
        Refused,
        InContainer,
        Equipped,
        /// <summary>Bounced into the wearer's pack - the slot refused it
        /// (ItemBounce, CCharAct.cpp:279).</summary>
        InPack,
        /// <summary>The pack could not take it either, so it lies at their feet.</summary>
        OnGround,
    }

    /// <param name="equipTestVeto">@EquipTest, for the flag=1 path: true means the
    /// script refused the item. Null when the caller has no trigger dispatcher.</param>
    /// <param name="onEquipped">@Equip, fired once the item is actually worn.</param>
    public static Outcome Place(
        GameWorld world, Item item, Serial parentUid, ItemDef? def, bool triggerEquip,
        Func<Item, Character, bool>? equipTestVeto = null,
        Action<Item, Character>? onEquipped = null)
    {
        if (world == null || item == null || item.IsDeleted)
            return Outcome.Refused;

        if (world.FindItem(parentUid) is { } container)
        {
            // A refusal is REPORTED rather than swallowed. Upstream's ContentAdd has
            // no capacity ceiling on this path, but the classic client cannot render
            // more than 255 items in one container, so the cap stays and the caller
            // learns the object never arrived - it used to be left parented to
            // nothing at 0,0,0 while the script was handed a valid uid.
            return container.TryAddItem(item) ? Outcome.InContainer : Outcome.Refused;
        }

        if (world.FindChar(parentUid) is not { } owner)
            return Outcome.Refused;

        Layer layer = def?.Layer is { } l and > Layer.None and < Layer.Qty ? l : Layer.None;

        if (triggerEquip)
        {
            if (layer == Layer.None || !owner.CanEquip(item, layer, out _))
                return Bounce(world, owner, item);
            if (equipTestVeto?.Invoke(item, owner) == true || item.IsDeleted)
                return item.IsDeleted ? Outcome.Refused : Bounce(world, owner, item);
            if (!owner.Equip(item, layer))
                return Bounce(world, owner, item);
            onEquipped?.Invoke(item, owner);
            return Outcome.Equipped;
        }

        // LoadSetContainer with LAYER_NONE: the layer comes from the definition, and
        // a definition that names none leaves the object for the pack.
        if (layer == Layer.None)
            return Bounce(world, owner, item);

        // An occupied pack layer is refused outright (CanEquipLayer, :455) - the
        // reference does not displace someone's backpack. An EMPTY one takes the
        // container, which is how a starting pack is scripted onto a character; the
        // old code excluded the pack layer from equipping altogether and so built a
        // SECOND, empty backpack and hid the scripted one inside it.
        if (layer == Layer.Pack && owner.Backpack != null)
            return Bounce(world, owner, item);

        // The strength gate belongs to the flag=1 path only, so a TooWeak verdict is
        // not a refusal here; every other verdict is.
        if (!owner.CanEquip(item, layer, out var denial) &&
            denial != Character.EquipDenial.TooWeak)
            return Bounce(world, owner, item);

        return owner.Equip(item, layer) ? Outcome.Equipped : Bounce(world, owner, item);
    }

    /// <summary>ItemBounce (CCharAct.cpp:279): into their pack, creating one when
    /// they have none (GetPackSafe), and at their feet when even that refuses.</summary>
    private static Outcome Bounce(GameWorld world, Character owner, Item item)
    {
        var pack = owner.Backpack;
        if (pack == null)
        {
            pack = world.CreateItem();
            pack.BaseId = 0x0E75;
            pack.ItemType = ItemType.Container;
            pack.Name = "Backpack";
            if (!owner.Equip(pack, Layer.Pack))
            {
                world.RemoveItem(pack);
                pack = null;
            }
        }

        if (pack != null && pack.TryAddItem(item))
            return Outcome.InPack;

        return world.PlaceItemWithDecay(item, owner.Position)
            ? Outcome.OnGround
            : Outcome.Refused;
    }
}
