using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>Item-use handler (decomposition phase 3) — the members below
    /// delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientItemUseHandler"/>.</summary>
    internal ClientItemUseHandler ItemUse => _itemUse ??= new ClientItemUseHandler(this);
    private ClientItemUseHandler? _itemUse;

    public void HandleDoubleClick(uint uid) => ItemUse.HandleDoubleClick(uid);

    /// <summary>Source-X CChar::Use_Obj(pObj, fTestTouch): a double-click the engine
    /// makes on the player's behalf. With <paramref name="testTouch"/> false the reach
    /// test is skipped (Telekinesis, CCharSpell.cpp:3135).</summary>
    public void UseObject(uint uid, bool testTouch) => ItemUse.HandleDoubleClick(uid, testTouch);

    /// <summary>Use a step-activated switch this player walked onto.</summary>
    public void UseSteppedSwitch(Item item) => ItemUse.UseSteppedSwitch(item);

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
