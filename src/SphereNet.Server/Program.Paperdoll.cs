using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Panel;
using SphereNet.Server.Admin;

namespace SphereNet.Server;

public static partial class Program
{
    private static PaperdollRenderer? _paperdollRenderer;

    /// <summary>Worn layers a paperdoll reports: every client-visible equip slot up to
    /// the mount (the same span 0x78 sends). Bank box and vendor containers stay out.</summary>
    private static IEnumerable<(Layer Layer, SphereNet.Game.Objects.Items.Item Item)> PaperdollWornItems(Character ch)
    {
        for (int i = (int)Layer.OneHanded; i <= (int)Layer.Horse; i++)
        {
            var item = ch.GetEquippedItem((Layer)i);
            if (item != null && !item.IsDeleted)
                yield return ((Layer)i, item);
        }
    }

    private static Character? FindPaperdollChar(uint serial)
    {
        if (serial == 0 || new Serial(serial).IsItem)
            return null;
        var ch = _world.FindChar(new Serial(serial));
        return ch == null || ch.IsDeleted ? null : ch;
    }

    /// <summary>Paperdoll info for the panel. Main loop only.</summary>
    private static PaperdollInfo? BuildPaperdollInfo(uint serial)
    {
        var ch = FindPaperdollChar(serial);
        if (ch == null)
            return null;

        var equipment = PaperdollWornItems(ch)
            .Select(e => new PaperdollItemInfo((int)e.Layer, e.Item.Uid.Value,
                e.Item.DispIdFull, e.Item.Hue.Value, e.Item.GetName()))
            .ToList();

        return new PaperdollInfo(
            ch.Uid.Value,
            ch.GetName(),
            ch.Title,
            PaperdollText.Build(ch),
            ch.BodyId,
            PaperdollRenderer.ResolveFemale(ch.BodyId, ch.IsFemale),
            ch.IsPlayer,
            (int)ch.PrivLevel,
            Character.ResolveAccountForChar?.Invoke(ch.Uid)?.Name ?? "",
            _clientsByCharUid.ContainsKey(ch.Uid),
            PaperdollRenderer.IsHumanoidBody(ch.BodyId),
            equipment);
    }

    /// <summary>What the paperdoll picture depends on. Main loop only; the drawing
    /// runs afterwards on the caller's thread.</summary>
    private static PaperdollLook? CapturePaperdollLook(uint serial)
    {
        var ch = FindPaperdollChar(serial);
        if (ch == null || !PaperdollRenderer.IsHumanoidBody(ch.BodyId))
            return null;
        var items = PaperdollWornItems(ch)
            .Select(e => new PaperdollWornItem((byte)e.Layer, e.Item.DispIdFull, e.Item.Hue.Value))
            .ToList();
        return new PaperdollLook(ch.Uid.Value, ch.BodyId, ch.Hue.Value, ch.IsFemale, items);
    }

    /// <summary>Paperdoll PNG: world state read on the main loop, picture drawn (or
    /// served from the look-keyed cache) off it.</summary>
    private static byte[]? GetPaperdollPng(uint serial, bool frame)
    {
        if (_mapData == null)
            return null;
        var look = InvokePanelOnMainLoop(() => CapturePaperdollLook(serial), "paperdoll look");
        if (look == null)
            return null;
        var renderer = LazyInitializer.EnsureInitialized(ref _paperdollRenderer,
            () => new PaperdollRenderer(new MapDataPaperdollArt(_mapData)));
        return renderer.GetPng(look, frame);
    }
}
