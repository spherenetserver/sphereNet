using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Objects.Characters;

public partial class Character
{
    /// <summary>CREID_GIANT_SPIDER: walks through webs.</summary>
    private const ushort WebWalkerBody = 0x001C;

    /// <summary>ITEMID_WEB1_1 (uofiles_enums_itemid.h:373): the graphic of the stuck hold.</summary>
    private const ushort StuckHoldId = 0x0EE3;

    /// <summary>Source-X CChar::Use_Item_Web (CCharUse.cpp:621-701): walking into, or
    /// double-clicking, a web. The web takes the character's STR as damage; while it
    /// holds, the character wears an IT_EQ_STUCK item on LAYER_FLAG_Stuck whose 2-10 s
    /// timer is both the freeze and the wait before the next try.
    /// Returns true while the character is held in place.</summary>
    public bool UseItemWeb(Item web)
    {
        bool topLevel = !web.ContainedIn.IsValid && !web.IsEquipped;
        if (BodyId == WebWalkerBody || web.IsDeleted || !topLevel ||
            IsStatFlag(StatFlag.Dead | StatFlag.Insubstantial) || IsGmMode)
            return false;   // just walk through it

        // m_itWeb.m_wHitsCur is MORE1.
        if (web.More1 == 0)
            web.More1 = (uint)(60 + Random.Shared.Next(250));
        else if (web.More1 > int.MaxValue)
            web.More1 = int.MaxValue;

        var flag = GetEquippedItem(Layer.FlagStuck);
        if (flag is { IsDeleted: true })
            flag = null;
        if (CanMoveWebItem(web))
        {
            flag?.Delete();
            return false;
        }

        // Don't allow me to try to damage it too often.
        if (flag != null && flag.Timeout > 0)
            return true;

        int str = CombatEngine.EffectiveStr(this);
        int dmg = ItemDamageEngine.OnTakeDamage(web, str, this, DamageType.HitBlunt);
        bool released = dmg is 2 or ItemDamageEngine.Destroyed ||
            Position != web.Position;   // 0 / 1 / other: still held only on the web's spot
        if (released)
        {
            flag?.Delete();
            return false;
        }

        if (flag == null)
        {
            var world = ResolveWorld?.Invoke();
            if (world == null)
                return false;
            flag = world.CreateItem();
            flag.BaseId = StuckHoldId;
            flag.SetAttr(ObjAttributes.Decay);
            flag.ItemType = ItemType.EqStuck;
            flag.Link = web.Uid;

            // 2 s minimum, 10 s maximum, longer the weaker the char and the stronger
            // the web.
            long weakness = (100L - Math.Min(100, str)) * (long)web.More1 / 10;
            long seconds = Math.Min(10L, 2L + weakness);
            flag.SetTimeout(Environment.TickCount64 + seconds * 1000L);
            if (!Equip(flag, Layer.FlagStuck))
            {
                world.DeleteObject(flag);
                return false;
            }
        }
        else
        {
            ResolveClientConsole?.Invoke(this)?.SysMessage(
                ServerMessages.GetFormatted(Msg.ItemuseSwebStuck, web.GetName()));
        }
        return true;
    }

    /// <summary>CChar::CanMoveItem(pItem, false) for an item lying on the ground
    /// (CCharStatus.cpp:1588-1618, 1687).</summary>
    private bool CanMoveWebItem(Item item)
    {
        if (IsGmMode)
            return true;
        if ((item.IsAttr(ObjAttributes.Move_Never) || item.IsAttr(ObjAttributes.LockedDown)) &&
            !item.IsAttr(ObjAttributes.Move_Always))
            return false;
        if (IsStatFlag(StatFlag.Stone | StatFlag.Freeze | StatFlag.Insubstantial | StatFlag.Dead | StatFlag.Sleeping) ||
            (Definitions.CharDefHelper.GetCanFlags(this) & CanFlags.C_Statue) != 0)
            return false;
        return item.IsMovableType;
    }
}
