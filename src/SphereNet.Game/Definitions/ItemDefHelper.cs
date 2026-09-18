using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Definitions;

/// <summary>
/// Central ITEMDEF-to-instance metadata application. Named ITEMDEF resources use
/// a 32-bit resource index while the item sent to the client stores a 16-bit
/// display id; SCRIPTDEF keeps those identities connected for triggers.
/// </summary>
public static class ItemDefHelper
{
    public static int ResolveInstanceDefIndex(Item item, ResourceHolder? resources = null)
    {
        if (item.TryGetTag("SCRIPTDEF", out string? scriptDef) &&
            int.TryParse(scriptDef, out int scriptIndex) && scriptIndex != 0)
            return scriptIndex;

        resources ??= DefinitionLoader.StaticResources;
        if (resources != null && item.TryGetTag("ITEMDEF", out string? defName) &&
            !string.IsNullOrWhiteSpace(defName))
        {
            var rid = resources.ResolveDefName(defName.Trim());
            if (rid.IsValid && rid.Type == ResType.ItemDef)
                return rid.Index;
        }

        return item.BaseId;
    }

    public static bool ApplyInstanceMetadata(Item item, int defIndex,
        bool setDisplayId = true, bool setName = true, bool fireCreate = true)
    {
        var def = DefinitionLoader.GetItemDef(defIndex);
        if (def == null)
            return false;

        if (setDisplayId)
        {
            ushort displayId = def.DispIndex != 0 ? def.DispIndex : def.DupItemId;
            if (displayId == 0 && defIndex is > 0 and <= ushort.MaxValue)
                displayId = (ushort)defIndex;
            if (displayId != 0)
                item.BaseId = displayId;
        }

        if (setName && !string.IsNullOrWhiteSpace(def.Name))
            item.Name = DefinitionLoader.ResolveNames(def.Name);

        item.ItemType = def.Type;
        item.TData1 = def.TData1;
        item.TData2 = def.TData2;
        item.TData3 = def.TData3;
        item.TData4 = def.TData4;

        foreach (var (key, value) in def.TagDefs.GetAll())
            item.SetTag(key, value);

        // Upstream copies the definition's combat ratings onto the instance when it
        // is made (CBase.cpp:416-419), which is what makes them changeable on one
        // item: after this, DAM= and ARMOR= on the object are its own. Stamped before
        // @Create so a magic weapon's script body can change what it inherited.
        if (def.AttackMin != 0 || def.AttackMax != 0)
            item.SetAttackRating(def.AttackMin, def.AttackMax);
        if (def.DefenseMin != 0 || def.DefenseMax != 0)
            item.SetDefenseRating(def.DefenseMin, def.DefenseMax);

        if (!string.IsNullOrWhiteSpace(def.DefName))
            item.SetTag("ITEMDEF", def.DefName);
        if (defIndex != item.BaseId)
            item.SetTag("SCRIPTDEF", defIndex.ToString());

        // Source-X CItem::GenerateScript fires ITRIG_Create on every item it
        // materialises from a def. This is the only hook that runs a magic
        // weapon's @Create body (MOREY/ATTR/HITPOINTS/COLOR), so loot, spawns,
        // NEWITEM, vendor restock, carve parts and .add all get their scripted
        // creation here. The ITEMDEF/SCRIPTDEF routing tags are set above so the
        // dispatcher resolves the right def; the call is a once-per-instance,
        // guarded no-op when no @Create trigger is wired (unit tests) or defined.
        //
        // Re-basing an existing item onto another definition asks for everything above
        // and NOT for this: upstream's SetID re-points the base and the type
        // (CItem.cpp:2128) without running a creation script over an object that is
        // already in the world.
        if (fireCreate)
            item.FireCreateTrigger();

        return true;
    }
}
