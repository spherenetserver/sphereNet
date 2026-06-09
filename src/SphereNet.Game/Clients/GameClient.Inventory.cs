using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>Inventory/interaction handler (decomposition phase 3) — the
    /// members below delegate so every call site stays unchanged. The logic
    /// lives in <see cref="ClientInventoryHandler"/>.</summary>
    internal ClientInventoryHandler Inventory => _inventory ??= new ClientInventoryHandler(this);
    private ClientInventoryHandler? _inventory;

    public void HandleSingleClick(uint uid) => Inventory.HandleSingleClick(uid);

    public void HandleItemPickup(uint serial, ushort amount) => Inventory.HandleItemPickup(serial, amount);

    public void HandleItemDrop(uint serial, short x, short y, sbyte z, uint containerUid) =>
        Inventory.HandleItemDrop(serial, x, y, z, containerUid);

    /// <summary>Script BOUNCE/DROP verb bridge (Character.OnDragRelease).</summary>
    public bool ReleaseDraggedItem(bool toGround) => Inventory.ReleaseDraggedItem(toGround);

    public void HandleItemEquip(uint serial, byte layer, uint charSerial) =>
        Inventory.HandleItemEquip(serial, layer, charSerial);

    public void HandleProfileRequest(byte mode, uint serial, string bioText = "") =>
        Inventory.HandleProfileRequest(mode, serial, bioText);

    public void HandleStatusRequest(byte type, uint serial) => Inventory.HandleStatusRequest(type, serial);

    /// <summary>Container-chain walk used by the ItemUse partial too; bridges
    /// to the handler until ItemUse's own phase-3 conversion.</summary>
    internal Item? GetTopContainer(Item item) => Inventory.GetTopContainer(item);
}
