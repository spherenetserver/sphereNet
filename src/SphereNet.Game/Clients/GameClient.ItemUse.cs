using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>Item-use handler (decomposition phase 3) — the members below
    /// delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientItemUseHandler"/>.</summary>
    internal ClientItemUseHandler ItemUse => _itemUse ??= new ClientItemUseHandler(this);
    private ClientItemUseHandler? _itemUse;

    public void HandleDoubleClick(uint uid) => ItemUse.HandleDoubleClick(uid);

    public void OpenVendorBuy(Character vendor) => ItemUse.OpenVendorBuy(vendor);

    public void OpenVendorSell(Character vendor) => ItemUse.OpenVendorSell(vendor);

    // Cross-partial bridges (Combat speech path, WorldFeatures context menu)
    // until those partials get their own phase-3 conversion. The pet-command
    // bridge also keeps the test-harness reflection entry point stable.
    internal bool TryHandlePetCommand(string text) => ItemUse.TryHandlePetCommand(text);

    internal bool HasAmmoInBackpack(Core.Enums.ItemType ammo) => ItemUse.HasAmmoInBackpack(ammo);

    internal void ConsumeAmmoFromBackpack(Core.Enums.ItemType ammo) => ItemUse.ConsumeAmmoFromBackpack(ammo);

    internal void HandleVendorInteraction(Character vendor) => ItemUse.HandleVendorInteraction(vendor);
}
