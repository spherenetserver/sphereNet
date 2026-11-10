using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Objects.Items;

/// <summary>
/// Central gate for "can this character lift/move this item" — the per-item /
/// per-mover flag checks Source-X concentrates in CChar::CanMoveItem
/// (CCharStatus.cpp). The packet pickup path additionally applies housing,
/// distance and looting-crime checks that need world context; this service
/// covers the context-free flag gates (immovable item, dead / frozen mover) so
/// every move entry point — packet pickup, script PICKUP, engine drag — can
/// share one rule set instead of re-deriving them inline. Staff (PrivLevel ≥ GM)
/// bypass every gate.
/// </summary>
public static class ItemMoveRules
{
    /// <summary>Reason a move was denied, so callers can map it to the right
    /// pickup-failed code / system message.</summary>
    public enum MoveDenial
    {
        None = 0,
        MoverDead,      // a ghost can't carry items
        MoverFrozen,    // paralysed / frozen movers can't act
        MoverIncapacitated, // stone / insubstantial / sleeping mover or a statue body
        ItemInTrade,    // item sits in somebody else's trade window (msg_trade_cantmove)
        ItemLayerLocked, // equipped hair / beard / pack / non-visible layer
        ItemImmovable,  // ATTR_MOVE_NEVER (corpses, static furniture)
        ItemCursed,     // ATTR_CURSED/CURSED2 while equipped (Source-X cantmove_cursed)
    }

    /// <summary>
    /// True when <paramref name="mover"/> may lift <paramref name="item"/> on the
    /// flag rules alone. Context checks (distance, housing lockdown, snoop,
    /// looting crime) stay at the call site. GM bypasses.
    /// </summary>
    public static bool CanMove(Character mover, Item item, out MoveDenial denial)
    {
        denial = MoveDenial.None;
        // IsPriv(PRIV_ALLMOVE|PRIV_DEBUG|PRIV_GM) (CCharStatus.cpp:1595).
        if (mover.PrivLevel >= PrivLevel.GM || mover.AllMove) return true;

        if (mover.IsDead) { denial = MoveDenial.MoverDead; return false; }
        if (mover.IsStatFlag(StatFlag.Freeze)) { denial = MoveDenial.MoverFrozen; return false; }
        // CChar::CanMoveItem refuses STATF_STONE|FREEZE|INSUBSTANTIAL|DEAD|SLEEPING and
        // CAN_C_STATUE movers alike (CCharStatus.cpp:1602-1607).
        if (mover.IsStatFlag(StatFlag.Stone) || mover.IsStatFlag(StatFlag.Insubstantial) ||
            mover.IsStatFlag(StatFlag.Sleeping) ||
            (Definitions.CharDefHelper.GetCanFlags(mover) & CanFlags.C_Statue) != 0)
        { denial = MoveDenial.MoverIncapacitated; return false; }
        if (item.IsAttr(ObjAttributes.Move_Never) && !item.IsAttr(ObjAttributes.Move_Always))
        { denial = MoveDenial.ItemImmovable; return false; }

        // On the ground the item's own movability decides (CItem::IsMovable,
        // CItem.cpp:726-735 → IsMovableType :712-724), and ItemPickup refuses a
        // STATIC item or a spell field outright, MOVE_ALWAYS or not
        // (CCharAct.cpp:2976-2979). Only MOVE_NEVER used to be asked, so a world item
        // whose tiledata weight is 255 (forges, signs, doors), a STATIC or INVIS one,
        // or a field could be lifted. LOCKEDDOWN is left to the housing engine, which
        // lets the house's owners lift their own lockdowns.
        if (!item.ContainedIn.IsValid)
        {
            if (item.IsAttr(ObjAttributes.Static) || item.ItemType == ItemType.Spell)
            { denial = MoveDenial.ItemImmovable; return false; }
            if (!item.IsAttr(ObjAttributes.Move_Always))
            {
                if (item.IsAttr(ObjAttributes.Invis))
                { denial = MoveDenial.ItemImmovable; return false; }
                var mapData = Item.ResolveWorld?.Invoke()?.MapData;
                if (mapData != null && mapData.GetItemTileData(item.DispIdFull).Weight == 255)
                { denial = MoveDenial.ItemImmovable; return false; }
            }
        }

        // Equipped or in a container (CCharStatus.cpp:1619-1686).
        if (item.ContainedIn.IsValid)
        {
            // Nothing can be lifted out of a trade window (client limitation,
            // CCharStatus.cpp:1621-1629). Deliberate deviation: the side's own
            // owner may still withdraw their own offer (the trade layer clears both
            // check marks when that happens), so only the OTHER party's window is
            // closed to the mover.
            var trade = FindEnclosingTradeWindow(item);
            if (trade != null && trade.ContainedIn != mover.Uid)
            { denial = MoveDenial.ItemInTrade; return false; }

            // An equipped hair / beard / pack is never lifted, nor is any layer
            // that isn't a visible worn slot (bank box, vendor boxes, trade layer);
            // LAYER_DRAGGING is the one layer that always passes
            // (CCharStatus.cpp:1669-1686; staff already returned above).
            if (item.IsEquipped && item.EquipLayer != Layer.Dragging &&
                item.ItemType != ItemType.EqTradeWindow &&
                (item.EquipLayer is Layer.Hair or Layer.FacialHair or Layer.Pack ||
                 !Item.IsVisibleLayer(item.EquipLayer)))
            { denial = MoveDenial.ItemLayerLocked; return false; }
        }

        // Source-X CChar::CanMoveItem: an equipped cursed item cannot be removed
        // (ATTR_CURSED | ATTR_CURSED2 while on a layer → "appears to be cursed.").
        if (item.IsEquipped &&
            (item.IsAttr(ObjAttributes.Cursed) || item.IsAttr(ObjAttributes.Cursed2)))
        { denial = MoveDenial.ItemCursed; return false; }

        return true;
    }

    /// <summary>The trade-window container <paramref name="item"/> is in (or is),
    /// walking up the container chain like CItemContainer::IsItemInTrade
    /// (CItemContainer.cpp:118); null when it is not inside one.</summary>
    public static Item? FindEnclosingTradeWindow(Item item, int maxDepth = 16)
    {
        var world = Item.ResolveWorld?.Invoke();
        if (world == null) return null;
        var current = item.ContainedIn.IsValid ? world.FindObject(item.ContainedIn) as Item : null;
        for (int i = 0; i < maxDepth && current != null; i++)
        {
            if (current.ItemType == ItemType.EqTradeWindow) return current;
            if (!current.ContainedIn.IsValid) return null;
            current = world.FindObject(current.ContainedIn) as Item;
        }
        return null;
    }
}
