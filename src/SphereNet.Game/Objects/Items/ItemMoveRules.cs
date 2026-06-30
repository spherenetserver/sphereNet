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
        ItemImmovable,  // ATTR_MOVE_NEVER (corpses, static furniture)
    }

    /// <summary>
    /// True when <paramref name="mover"/> may lift <paramref name="item"/> on the
    /// flag rules alone. Context checks (distance, housing lockdown, snoop,
    /// looting crime) stay at the call site. GM bypasses.
    /// </summary>
    public static bool CanMove(Character mover, Item item, out MoveDenial denial)
    {
        denial = MoveDenial.None;
        if (mover.PrivLevel >= PrivLevel.GM) return true;

        if (mover.IsDead) { denial = MoveDenial.MoverDead; return false; }
        if (mover.IsStatFlag(StatFlag.Freeze)) { denial = MoveDenial.MoverFrozen; return false; }
        if (item.IsAttr(ObjAttributes.Move_Never)) { denial = MoveDenial.ItemImmovable; return false; }

        return true;
    }
}
