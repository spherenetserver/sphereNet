using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>The graphic an item made from an itemdef shows. A DUPELIST member - a
/// door's other facings, 06a7..06b3 under [ITEMDEF 06a5] - carries its base in
/// DUPEITEM but is its own graphic, as upstream keeps a dupe's id. Taking DUPEITEM
/// first made every worldgen door face the base's one way.</summary>
public sealed class ItemCreateGraphicTests
{
    private static ItemDef Def(int index, ushort disp = 0, ushort dupe = 0) =>
        new(new ResourceId(ResType.ItemDef, index)) { DispIndex = disp, DupItemId = dupe };

    [Fact]
    public void ADupelistMemberKeepsItsOwnGraphic() =>
        Assert.Equal(0x06A7, ItemDefHelper.CreateGraphic(Def(0x06A7, dupe: 0x06A5), 0x06A7));

    [Fact]
    public void AnExplicitIdWins() =>
        Assert.Equal(0x0EED, ItemDefHelper.CreateGraphic(Def(0x40001234, disp: 0x0EED, dupe: 0x06A5), 0x40001234));

    [Fact]
    public void ANamedDefFallsBackToItsDupeItem() =>
        Assert.Equal(0x06A5, ItemDefHelper.CreateGraphic(Def(0x40001234, dupe: 0x06A5), 0x40001234));

    [Fact]
    public void AnUnknownNamedDefHasNoGraphic() =>
        Assert.Equal(0, ItemDefHelper.CreateGraphic(null, 0x40001234));
}
