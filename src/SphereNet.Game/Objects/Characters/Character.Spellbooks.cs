using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Objects.Characters;

public partial class Character
{
    internal Item? FindSpellbook(int spell)
    {
        Item? fallback = null;
        bool Consider(Item item)
        {
            int bit = spell - item.SpellbookOffset - 1;
            if (!item.IsSpellbook || bit < 0 || (uint)bit >= item.SpellbookSpellCount) return false;
            fallback = item;
            return item.ContainsSpell(spell);
        }

        // Source-X GetSpellbookLayer picks the first book in hand 1, then hand 2.
        var hand = GetEquippedItem(Layer.OneHanded);
        if (hand?.IsSpellbook != true) hand = GetEquippedItem(Layer.TwoHanded);
        if (hand != null && Consider(hand)) return hand;
        if (Backpack != null)
            foreach (var item in Backpack.Contents)
                if (Consider(item)) return item;
        return fallback;
    }

}
