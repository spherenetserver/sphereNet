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
        bool setDisplayId = true, bool setName = true)
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

        if (!string.IsNullOrWhiteSpace(def.DefName))
            item.SetTag("ITEMDEF", def.DefName);
        if (defIndex != item.BaseId)
            item.SetTag("SCRIPTDEF", defIndex.ToString());

        return true;
    }
}
