using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Which graphic a dropped item flips to.
///
/// Upstream walks the DUPELIST as a cycle: CItemBase::GetNextFlipID
/// (CItemBase.cpp:885) starts at the def's own display id and, for each entry in
/// order, hands back the next one when the previous matches what the item holds -
/// falling off the end returns to the display id. The drop applies it only to an item
/// that CAN flip, is movable, and is NOT a stackable pile (CCharAct.cpp:3266).
///
/// This engine toggled the low bit of the graphic instead. That is right for a
/// two-entry pair that begins on an even id and wrong for everything else: a pair
/// beginning on an ODD id flips DOWNWARD into whatever the pack has below it - which
/// is a different item's art - and a list longer than two never reaches its later
/// entries.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemFlipCycleTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<int> _defined = [];

    public ItemFlipCycleTests(ITestOutputHelper output) => _out = output;

    private static Dictionary<int, ItemDef> DefTable =>
        (Dictionary<int, ItemDef>)typeof(SphereNet.Game.Definitions.DefinitionLoader)
            .GetField("_itemDefs", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

    public void Dispose()
    {
        foreach (int id in _defined)
            DefTable.Remove(id);
    }

    private void Define(int baseId, Action<ItemDef> shape)
    {
        var def = new ItemDef(new ResourceId(ResType.ItemDef, baseId));
        shape(def);
        DefTable[baseId] = def;
        _defined.Add(baseId);
    }

    private static (GameWorld World, Item It) Spawn(ushort baseId)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var it = world.CreateItem();
        it.BaseId = baseId;
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        return (world, it);
    }

    // ---- the pack's own shapes ------------------------------------------

    [Fact]
    public void AKatanaFlipsToItsOwnSecondArtAndBack()
    {
        // [ITEMDEF 013fe] i_katana, FLIP=1, DUPELIST=013ff - and 01400 is the KRYSS.
        Define(0x13FE, d => { d.Flip = true; d.DupeList = "013ff"; });
        // [ITEMDEF 013ff] with DUPEITEM=013fe - an alias, exactly as the pack
        // writes it: no FLIP and no DUPELIST of its own.
        Define(0x13FF, d => { d.DupItemId = 0x13FE; });
        var (_, katana) = Spawn(0x13FE);

        Assert.True(katana.TryFlipDisplay());
        _out.WriteLine($"13FE -> {katana.BaseId:X4}");
        Assert.Equal((ushort)0x13FF, katana.BaseId);

        Assert.True(katana.TryFlipDisplay());
        _out.WriteLine($"13FF -> {katana.BaseId:X4}");
        Assert.Equal((ushort)0x13FE, katana.BaseId);

        // Whatever it does, it must never become the next def along.
        Assert.NotEqual((ushort)0x1400, katana.BaseId);
    }

    [Fact]
    public void APairThatStartsOnAnOddIdFlipsUpwardNotDownward()
    {
        // The case the low-bit toggle gets backwards. Base 0x0F3F with its pair at
        // 0x0F40: toggling the low bit gives 0x0F3E, which belongs to somebody else.
        Define(0x0F3F, d => { d.Flip = true; d.DupeList = "0f40"; });
        Define(0x0F40, d => { d.DupItemId = 0x0F3F; });
        var (_, it) = Spawn(0x0F3F);

        Assert.True(it.TryFlipDisplay());
        _out.WriteLine($"0F3F -> {it.BaseId:X4} (the low-bit guess would say 0F3E)");
        Assert.Equal((ushort)0x0F40, it.BaseId);
        Assert.NotEqual((ushort)0x0F3E, it.BaseId);
    }

    [Fact]
    public void AListOfThreeVisitsAllOfThemAndComesHome()
    {
        // A leading zero makes a Sphere number hex, which is how the packs write ids.
        Define(0x2000, d => { d.Flip = true; d.DupeList = "02001,02002,02003"; });
        foreach (int alias in new[] { 0x2001, 0x2002, 0x2003 })
            Define(alias, d => { d.DupItemId = 0x2000; });
        var (_, it) = Spawn(0x2000);

        var seen = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            it.TryFlipDisplay();
            seen.Add($"{it.BaseId:X4}");
        }

        _out.WriteLine("cycle: " + string.Join(" -> ", seen));
        Assert.Equal(new[] { "2001", "2002", "2003", "2000" }, seen);
    }

    // ---- what must not flip ---------------------------------------------

    [Fact]
    public void AStackablePileDoesNotFlip()
    {
        // Ore: DUPELIST=019b8,019b9,019ba is an AMOUNT ramp, not a flip cycle, and
        // upstream refuses the flip for anything CAN_I_PILE (CItemBase.h:329).
        Define(0x19B7, d => { d.Flip = true; d.Can = CanFlags.I_Pile; d.DupeList = "019b8,019b9,019ba"; });
        var (_, ore) = Spawn(0x19B7);

        Assert.False(ore.TryFlipDisplay());
        Assert.Equal((ushort)0x19B7, ore.BaseId);
    }

    [Fact]
    public void AnItemWithNoFlipListStaysAsItIs()
    {
        // Upstream returns the display id when the list is empty - no change at all.
        Define(0x1234, d => { d.Flip = true; });
        var (_, it) = Spawn(0x1234);

        Assert.False(it.TryFlipDisplay());
        Assert.Equal((ushort)0x1234, it.BaseId);
    }

    [Fact]
    public void AnExplicitFlipIdStillWins()
    {
        Define(0x3000, d => { d.Flip = true; d.FlipId = 0x3055; });
        var (_, it) = Spawn(0x3000);

        Assert.True(it.TryFlipDisplay());
        Assert.Equal((ushort)0x3055, it.BaseId);
    }

    [Fact]
    public void AnUndefinedGraphicDoesNotFlip()
    {
        var (_, it) = Spawn(0x7777);
        Assert.False(it.TryFlipDisplay());
        Assert.Equal((ushort)0x7777, it.BaseId);
    }
}
