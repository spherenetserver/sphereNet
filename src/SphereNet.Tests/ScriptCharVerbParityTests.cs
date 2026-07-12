using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Object-verb parity for Source-X CChar_functions entries that were missing
/// from the C# TryExecuteCommand surface: UNEQUIP / WHERE / SUMMONTO.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class ScriptCharVerbParityTests
{
    private sealed class Console : ITextConsole
    {
        public readonly List<string> Messages = [];
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) => Messages.Add(text);
    }

    private static (GameWorld World, Character Ch) MakeWorldChar()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.Name = "Griswold";
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        return (world, ch);
    }

    [Fact]
    public void Unequip_BouncesEquippedItemIntoBackpack()
    {
        var (world, ch) = MakeWorldChar();
        var blade = world.CreateItem();
        blade.ItemType = ItemType.WeaponSword;
        ch.Equip(blade, Layer.OneHanded);
        Assert.Same(blade, ch.GetEquippedItem(Layer.OneHanded));

        bool ok = ch.TryExecuteCommand("UNEQUIP", "0x" + blade.Uid.Value.ToString("X"), new Console());

        Assert.True(ok);
        Assert.Null(ch.GetEquippedItem(Layer.OneHanded));  // stripped
        Assert.False(blade.IsEquipped);
        Assert.Contains(blade, ch.Backpack!.Contents);      // landed in pack
    }

    [Fact]
    public void Unequip_UnknownUid_ReturnsFalse()
    {
        var (_, ch) = MakeWorldChar();
        Assert.False(ch.TryExecuteCommand("UNEQUIP", "0xDEADBEEF", new Console()));
    }

    [Fact]
    public void Where_ReportsLocationToCaller()
    {
        var (_, ch) = MakeWorldChar();
        var console = new Console();

        Assert.True(ch.TryExecuteCommand("WHERE", "", console));

        Assert.Single(console.Messages);
        Assert.Contains("1000,1000,0", console.Messages[0]);
        Assert.Contains("Griswold", console.Messages[0]);
    }

    [Fact]
    public void SummonTo_ExplicitUid_TeleportsToThatObject()
    {
        var (world, ch) = MakeWorldChar();
        var beacon = world.CreateItem();
        world.PlaceItem(beacon, new Point3D(2000, 1500, 5, 0));

        bool ok = ch.TryExecuteCommand("SUMMONTO", "0x" + beacon.Uid.Value.ToString("X"), new Console());

        Assert.True(ok);
        Assert.Equal(2000, ch.X);
        Assert.Equal(1500, ch.Y);
    }

    [Fact]
    public void NotoGetFlag_UsesResolveHook_ForTargetViewer()
    {
        var (world, ch) = MakeWorldChar();
        var viewer = world.CreateCharacter();
        world.PlaceCharacter(viewer, new Point3D(1001, 1000, 0, 0));

        // Wire the resolver like the server does; assert the property routes the
        // (subject, viewer) pair through it and returns the flag.
        Character.ResolveNotoFlag = (subject, v) =>
            ReferenceEquals(subject, ch) && ReferenceEquals(v, viewer) ? (byte)6 : (byte)0;
        try
        {
            Assert.True(ch.TryGetProperty("NOTOGETFLAG 0x" + viewer.Uid.Value.ToString("X"), out string flag));
            Assert.Equal("6", flag);
        }
        finally { Character.ResolveNotoFlag = null; }
    }

    [Fact]
    public void CanMove_IsWiredAndReturnsBoolean()
    {
        var (_, ch) = MakeWorldChar();
        // The property is wired and safe regardless of whether MapData is loaded;
        // it must resolve to a 0/1 flag, never throw or fall through.
        Assert.True(ch.TryGetProperty("CANMOVE N", out string canMove));
        Assert.Contains(canMove, new[] { "0", "1" });
    }
}
