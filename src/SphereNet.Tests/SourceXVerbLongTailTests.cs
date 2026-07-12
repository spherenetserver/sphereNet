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
/// Source-X verb long-tail coverage (the former KnownPartialOrDeferred set):
/// ObjBase GOAWAKE/GOSLEEP/PROPLIST/SAYUA/EFFECTLOCATION and the Char
/// GOCHARID/GOTYPE/AFK/HEAR/UNDERWEAR/GOCLI surfaces.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class SourceXVerbLongTailTests
{
    private sealed class CapturingConsole : ITextConsole
    {
        public List<string> Lines { get; } = [];
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) => Lines.Add(text);
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar(GameWorld world, string name, ushort body = 0x190)
    {
        var ch = world.CreateCharacter();
        ch.Name = name;
        ch.BodyId = body;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void GoSleepAndGoAwake_FlipObjectSleepState()
    {
        var world = CreateWorld();
        var ch = MakeChar(world, "sleeper");
        var console = new CapturingConsole();

        Assert.True(ch.TryExecuteCommand("GOSLEEP", "", console));
        Assert.True(ch.IsSleeping);
        Assert.True(ch.TryExecuteCommand("GOAWAKE", "", console));
        Assert.False(ch.IsSleeping);
    }

    [Fact]
    public void PropList_DumpsPropertiesToConsole()
    {
        var world = CreateWorld();
        var ch = MakeChar(world, "proptest");
        var console = new CapturingConsole();

        Assert.True(ch.TryExecuteCommand("PROPLIST", "", console));
        Assert.Contains(console.Lines, l => l.StartsWith("NAME=proptest"));
        Assert.Contains(console.Lines, l => l.StartsWith("[PROPLIST]"));
    }

    [Fact]
    public void Underwear_TogglesNudeHueBit()
    {
        var world = CreateWorld();
        var ch = MakeChar(world, "nudist");
        ushort before = ch.Hue.Value;
        var console = new CapturingConsole();

        Assert.True(ch.TryExecuteCommand("UNDERWEAR", "", console));
        Assert.Equal(before ^ 0x8000, ch.Hue.Value);
        Assert.True(ch.TryExecuteCommand("UNDERWEAR", "", console));
        Assert.Equal(before, ch.Hue.Value);
    }

    [Fact]
    public void GoCharId_TeleportsToMatchingBody()
    {
        var world = CreateWorld();
        var gm = MakeChar(world, "gm");
        var orc = MakeChar(world, "an orc", body: 0x11);
        world.MoveCharacter(orc, new Point3D(500, 500, 0, 0));
        var console = new CapturingConsole();

        Assert.True(gm.TryExecuteCommand("GOCHARID", "0x11", console));
        Assert.Equal(orc.Position.X, gm.Position.X);
        Assert.Equal(orc.Position.Y, gm.Position.Y);
    }

    [Fact]
    public void GoType_TeleportsToFirstItemOfType()
    {
        var world = CreateWorld();
        var gm = MakeChar(world, "gm2");
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        item.ItemType = ItemType.Gold;
        world.PlaceItem(item, new Point3D(777, 777, 0, 0));
        var console = new CapturingConsole();

        Assert.True(gm.TryExecuteCommand("GOTYPE", ((int)ItemType.Gold).ToString(), console));
        Assert.Equal(777, gm.Position.X);
    }

    [Fact]
    public void Afk_TogglesWithMessages()
    {
        var world = CreateWorld();
        var ch = MakeChar(world, "afker");
        var console = new CapturingConsole();

        Assert.True(ch.TryExecuteCommand("AFK", "", console));
        Assert.True(ch.IsAfk);
        Assert.Contains(console.Lines, l => l.Contains("AFK"));
        Assert.True(ch.TryExecuteCommand("AFK", "", console));
        Assert.False(ch.IsAfk);
        // Forced ON stays on.
        Assert.True(ch.TryExecuteCommand("AFK", "1", console));
        Assert.True(ch.IsAfk);
        Assert.True(ch.TryExecuteCommand("AFK", "1", console));
        Assert.True(ch.IsAfk);
    }

    [Fact]
    public void Hear_RoutesThroughHostHook()
    {
        var world = CreateWorld();
        var ch = MakeChar(world, "listener");
        var console = new CapturingConsole();

        Character? routedChar = null;
        string? routedText = null;
        Character.OnHearRouted = (c, text, _) => { routedChar = c; routedText = text; };
        try
        {
            Assert.True(ch.TryExecuteCommand("HEAR", "psst over here", console));
            Assert.Same(ch, routedChar);
            Assert.Equal("psst over here", routedText);
        }
        finally
        {
            Character.OnHearRouted = null;
        }
    }

    [Fact]
    public void GoCli_UsesHostResolver()
    {
        var world = CreateWorld();
        var gm = MakeChar(world, "gm3");
        var online = MakeChar(world, "player1");
        world.MoveCharacter(online, new Point3D(321, 321, 0, 0));
        var console = new CapturingConsole();

        Character.FindCharByClientIndex = idx => idx == 0 ? online : null;
        try
        {
            Assert.True(gm.TryExecuteCommand("GOCLI", "0", console));
            Assert.Equal(321, gm.Position.X);
        }
        finally
        {
            Character.FindCharByClientIndex = null;
        }
    }

    [Fact]
    public void BasePropList_UsesDiagnosticLogWhenRequested()
    {
        var world = CreateWorld();
        var ch = MakeChar(world, "logdump");
        var console = new CapturingConsole();
        var logLines = new List<string>();
        ObjBase.DiagnosticLog = logLines.Add;
        try
        {
            Assert.True(ch.TryExecuteCommand("PROPLIST", "log", console));
            Assert.Empty(console.Lines);
            Assert.Contains(logLines, l => l.StartsWith("[PROPLIST]"));
        }
        finally
        {
            ObjBase.DiagnosticLog = null;
        }
    }
}
