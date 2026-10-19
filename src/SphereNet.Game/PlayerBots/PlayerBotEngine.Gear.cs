using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Gearing up again. A bot that came back from death with nothing - its corpse
/// looted, rotted or out of reach - would walk about in the resurrection robe for
/// good. In a guarded town it gets a new kit, as a player would buy one: its
/// archetype's @Create is run on a stand-in and what that wears, and the supplies in
/// its pack (not the gold), go to the bot; the robe goes, the stand-in is deleted.
/// </summary>
public sealed partial class PlayerBotEngine
{
    // What is worn and counts as gear: everything but hair, beard, pack, the robe
    // a resurrection leaves, the mount and the bank and vendor boxes.
    private static readonly Layer[] GearLayers =
    [
        Layer.OneHanded, Layer.TwoHanded, Layer.Shoes, Layer.Pants, Layer.Shirt, Layer.Helm, Layer.Gloves,
        Layer.Ring, Layer.Neck, Layer.Waist, Layer.Chest, Layer.Bracelet, Layer.Tunic, Layer.Earrings,
        Layer.Arms, Layer.Cape, Layer.Skirt, Layer.Legs,
    ];

    /// <summary>True when the bot wears nothing of its own (at most a robe).</summary>
    internal static bool IsBare(Objects.Characters.Character ch)
    {
        foreach (var layer in GearLayers)
            if (ch.GetEquippedItem(layer) is { IsDeleted: false })
                return false;
        return true;
    }

    private bool CheckGear(BotMind mind, long now)
    {
        if (now < mind.NextGearCheckMs) return false;
        mind.NextGearCheckMs = now + 30_000;
        var ch = mind.Char;
        if (ch.IsDead || mind.Mode is BotMode.Fight or BotMode.Flee) return false;
        if (ShedResRobe(ch)) return true;
        if (mind.Mode != BotMode.Linger || mind.Scene != null || !IsBare(ch)) return false;
        if (_world.FindRegion(ch.Position)?.IsGuarded != true) return false;
        if (!ch.TryGetTag(ArchetypeTag, out string? arch) || arch == null) return false;
        int defIndex = ResolveCharDef(arch);
        if (defIndex < 0) return false;

        var standIn = CreateFromDef(defIndex);
        try
        {
            if (ch.GetEquippedItem(Layer.Robe) is { } robe)
            {
                ch.Unequip(Layer.Robe);
                _world.DeleteObject(robe);
            }
            foreach (var layer in GearLayers.Append(Layer.Robe))
            {
                if (standIn.GetEquippedItem(layer) is not { } item || ch.GetEquippedItem(layer) != null) continue;
                standIn.Unequip(layer);
                ch.Equip(item, layer);
            }
            EnsureBackpack(ch);
            if (standIn.Backpack is { } from && ch.Backpack is { } to)
                foreach (Item item in _world.GetContainerContents(from.Uid).ToList())
                    if (!item.IsDeleted && item.ItemType != ItemType.Gold)
                        to.TryAddItem(item);
        }
        finally
        {
            _world.DeleteObject(standIn);
        }
        WindowRegears++;
        OnAppeared?.Invoke(ch);
        Speak(mind, "gear_up", fallback: null);
        return true;
    }

    public long WindowRegears { get; private set; }
    public long WindowRobesShed { get; private set; }

    /// <summary>The name a resurrection robe is given (DEFMSG_SPELL_RES_ROBENAME).</summary>
    public string ResRobeName { get; set; } = "";

    private bool IsResRobe(Item? robe) =>
        robe != null && ResRobeName.Length > 0 && string.Equals(robe.Name, ResRobeName, StringComparison.Ordinal);

    /// <summary>Take off and discard the robe a resurrection put on, if that is what
    /// the bot wears.</summary>
    private bool DropResRobe(Objects.Characters.Character ch)
    {
        var robe = ch.GetEquippedItem(Layer.Robe);
        if (!IsResRobe(robe)) return false;
        ch.Unequip(Layer.Robe);
        _world.DeleteObject(robe!);
        return true;
    }

    /// <summary>A bot that has its own things back takes off the plain robe it was
    /// raised in, as a player does, and puts on what the robe kept it from wearing:
    /// pieces in its pack marked with the layer they came off (EQUIPLAYER). A bot with
    /// nothing of its own keeps the robe until it gears up again.</summary>
    private bool ShedResRobe(Objects.Characters.Character ch)
    {
        if (!IsResRobe(ch.GetEquippedItem(Layer.Robe))) return false;
        var marked = new List<(Item Item, Layer Layer)>();
        if (ch.Backpack is { } pack)
            foreach (Item item in _world.GetContainerContents(pack.Uid))
                if (!item.IsDeleted && item.TryGetTag("EQUIPLAYER", out string? text) &&
                    ScriptNumber.TryParseByte(text, out byte b) && ((Layer)b == Layer.Robe || GearLayers.Contains((Layer)b)))
                    marked.Add((item, (Layer)b));
        if (IsBare(ch) && marked.Count == 0) return false;
        DropResRobe(ch);
        foreach (var (item, layer) in marked)
        {
            if (ch.GetEquippedItem(layer) != null) continue;
            ch.Backpack!.RemoveItem(item);
            if (ch.Equip(item, layer)) item.RemoveTag("EQUIPLAYER");
            else ch.Backpack.TryAddItem(item);
        }
        WindowRobesShed++;
        OnAppeared?.Invoke(ch);
        return true;
    }
}
