using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;
using TriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class TrypParityTests
{
    private sealed class Console(Character? character, PrivLevel privilege) : ITextConsole
    {
        public readonly List<string> Messages = [];
        public PrivLevel GetPrivLevel() => privilege;
        public string GetName() => "test";
        public IScriptObj? GetSourceChar() => character;
        public void SysMessage(string text) => Messages.Add(text);
    }

    [Theory]
    [InlineData("native", 2, true)]
    [InlineData("native", 3, false)]
    [InlineData("interpreter", 2, true)]
    [InlineData("interpreter", 3, false)]
    [InlineData("timer", 2, true)]
    [InlineData("timer", 3, false)]
    public void PlayerMustTouchTargetAcrossEntryPoints(string route, int distance, bool allowed)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100));
        var item = world.CreateItem();
        item.Name = "original";
        world.PlaceItem(item, new Point3D((short)(100 + distance), 100));
        var console = new Console(player, player.PrivLevel);
        if (route == "native") item.TryExecuteCommand("TRYP", "1 NAME=marked", console);
        else if (route == "interpreter")
            stack.Interpreter.Execute([new ScriptKey("TRYP", "1 NAME=marked")], item, console,
                new TriggerArgs(player), new ScriptScope());
        else
        {
            world.TimerFExpired = new DelayedCallDispatcher(() => stack.Runner, null, ScriptServerConsole.Instance).Run;
            item.TryExecuteCommand("TIMERF", $"0,TRYSRC 0{player.Uid.Value:X} TRYP 1 NAME=marked", console);
            TestHarness.PumpTimerF(world, Environment.TickCount64);
        }
        Assert.Equal(allowed ? "marked" : "original", item.Name);
    }

    [Theory]
    [InlineData(PrivLevel.Counsel, "1", false)]
    [InlineData(PrivLevel.Seer, "1", true)]
    [InlineData(PrivLevel.Player, "4", false)]
    [InlineData(PrivLevel.GM, "04", true)]
    [InlineData(PrivLevel.GM, "2 + 2", true)]
    [InlineData(PrivLevel.Owner, "8", false)]
    public void PrivilegeGateHonorsBoundsExpressionsAndSeerTouchBypass(PrivLevel privilege, string minimum, bool allowed)
    {
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.PrivLevel = privilege;
        world.PlaceCharacter(player, new Point3D(100, 100));
        var item = world.CreateItem();
        item.Name = "original";
        world.PlaceItem(item, new Point3D(150, 100));
        var console = new Console(player, privilege);
        Assert.Equal(allowed, item.ExecuteVerbLine("TRYP", $"{minimum} NAME=marked", console));
        Assert.Equal(allowed ? "marked" : "original", item.Name);
        if (!allowed) Assert.NotEmpty(console.Messages);
    }

    [Theory]
    [InlineData(StatFlag.None, ItemType.Normal, false, true)]
    [InlineData(StatFlag.Dead, ItemType.Normal, false, false)]
    [InlineData(StatFlag.Sleeping, ItemType.Normal, false, false)]
    [InlineData(StatFlag.Stone, ItemType.Normal, false, false)]
    [InlineData(StatFlag.Freeze, ItemType.Normal, false, false)]
    [InlineData(StatFlag.Freeze, ItemType.Normal, true, true)]
    [InlineData(StatFlag.Dead, ItemType.Shrine, false, true)]
    [InlineData(StatFlag.Freeze, ItemType.Telescope, false, true)]
    public void TouchHonorsCharacterStateAndItemExceptions(StatFlag flag, ItemType type, bool usableParalyzed, bool allowed)
    {
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100));
        player.SetStatFlag(flag);
        var item = world.CreateItem();
        item.ItemType = type;
        if (usableParalyzed) item.SetAttr(ObjAttributes.CanUseParalyzed);
        world.PlaceItem(item, new Point3D(101, 100));
        Assert.Equal(allowed, item.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedBankItemRequiresOriginalOpeningPosition(bool moved)
    {
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100));
        var bank = world.CreateItem();
        bank.ItemType = ItemType.EqBankBox;
        bank.MoreP = player.Position;
        Assert.True(player.Equip(bank, Layer.BankBox));
        var bag = world.CreateItem();
        bag.ItemType = ItemType.Container;
        Assert.True(bank.TryAddItem(bag));
        var item = world.CreateItem();
        Assert.True(bag.TryAddItem(item));
        if (moved) world.MoveCharacter(player, new Point3D(101, 100));
        Assert.Equal(!moved, item.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void FrozenCharacterMayTouchOwnContainerButNotOrdinaryItem(bool container, bool allowed)
    {
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100));
        var item = world.CreateItem();
        if (container) item.ItemType = ItemType.Container;
        Assert.True(player.Equip(item, (Layer)30));
        player.SetStatFlag(StatFlag.Freeze);
        Assert.Equal(allowed, item.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(24, 0, false)]
    [InlineData(0, 1, false)]
    public void ReachUsesHeightAndMap(int z, int map, bool allowed)
    {
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100));
        var item = world.CreateItem();
        item.Position = new Point3D(101, 100, (sbyte)z, (byte)map);
        Assert.Equal(allowed, item.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LineOfSightChecksRealWallAndItemCanExemption(bool ignoreLos)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"tryp-los-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, $"[ITEMDEF 01001]\nCAN={(ignoreLos ? "04000" : "0")}\n");
        try
        {
            stack.Resources.LoadResourceFile(path);
            new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();
            var world = TestHarness.CreateWorld();
            var map = new SphereNet.MapData.MapDataManager("");
            map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
            map.SetSyntheticItemTile(0x80, new SphereNet.MapData.Tiles.ItemTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.Wall | SphereNet.MapData.Tiles.TileFlag.Impassable, Height = 20 });
            world.MapData = map;
            var player = world.CreateCharacter();
            player.PrivLevel = PrivLevel.Player;
            world.PlaceCharacter(player, new Point3D(100, 100));
            var wall = world.CreateItem();
            wall.BaseId = 0x80;
            world.PlaceItem(wall, new Point3D(101, 100));
            var item = world.CreateItem();
            ItemDefHelper.ApplyInstanceMetadata(item, 0x1001);
            world.PlaceItem(item, new Point3D(102, 100));
            Assert.False(world.CanSeeLOS(player.Position, item.Position));
            Assert.Equal(ignoreLos, item.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionReceivesOriginalSourceAndRefusalDoesNotRunShadowFunction(bool allowed)
    {
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100));
        var item = world.CreateItem();
        world.PlaceItem(item, player.Position);
        var console = new Console(player, player.PrivLevel);
        var previous = ObjBase.RunScriptFunction;
        bool called = false;
        try
        {
            ObjBase.RunScriptFunction = (target, verb, args, source) =>
            {
                called = true;
                Assert.Same(item, target);
                Assert.Equal("f_probe", verb);
                Assert.Equal("37", args);
                Assert.Same(console, source);
                return true;
            };
            Assert.Equal(allowed, item.ExecuteVerbLine("TRYP", $"{(allowed ? 1 : 4)} f_probe=37", console));
            Assert.Equal(allowed, called);
        }
        finally { ObjBase.RunScriptFunction = previous; }
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void ShipTouchUsesConfiguredPlankRange(int distance, bool allowed)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tryp-config-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, "[SPHERE]\nMAXSHIPPLANKTELEPORT=6\n");
        try
        {
            var ini = new SphereNet.Core.Configuration.IniParser();
            ini.Load(path);
            ScriptTouchAccess.Configuration.LoadFromIni(ini);
            Assert.Equal(6, ScriptTouchAccess.Configuration.MaxShipPlankTeleport);
            var world = TestHarness.CreateWorld();
            var player = world.CreateCharacter();
            player.PrivLevel = PrivLevel.Player;
            world.PlaceCharacter(player, new Point3D(100, 100));
            var plank = world.CreateItem();
            plank.ItemType = ItemType.ShipPlank;
            world.PlaceItem(plank, new Point3D((short)(100 + distance), 100));
            Assert.Equal(allowed, plank.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockedContainerContentsRequireOwnership(bool owned)
    {
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100));
        var pet = world.CreateCharacter();
        if (owned) pet.TrySetProperty("OWNER", $"0{player.Uid.Value:X}");
        world.PlaceCharacter(pet, new Point3D(101, 100));
        var container = world.CreateItem();
        container.ItemType = ItemType.ContainerLocked;
        Assert.True(pet.Equip(container, (Layer)30));
        var item = world.CreateItem();
        Assert.True(container.TryAddItem(item));
        Assert.Equal(owned, item.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistanceExemptionComesFromScriptDefinition(bool ignoreDistance)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"tryp-distance-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, $"[ITEMDEF 01002]\nCAN=0{(uint)(ignoreDistance ? CanFlags.I_DcIgnoreDist : CanFlags.None):X}\n");
        try
        {
            stack.Resources.LoadResourceFile(path);
            new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();
            var world = TestHarness.CreateWorld();
            var player = world.CreateCharacter();
            player.PrivLevel = PrivLevel.Player;
            world.PlaceCharacter(player, new Point3D(100, 100));
            var item = world.CreateItem();
            ItemDefHelper.ApplyInstanceMetadata(item, 0x1002);
            world.PlaceItem(item, new Point3D(110, 100));
            Assert.Equal(ignoreDistance, item.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GhostVisibilityRestrictionDoesNotApplyToLivingSource(bool dead, bool allowed)
    {
        int previous = Character.DeadCannotSeeLiving;
        try
        {
            Character.DeadCannotSeeLiving = 1;
            var world = TestHarness.CreateWorld();
            var player = world.CreateCharacter();
            player.PrivLevel = PrivLevel.Player;
            player.IsPlayer = true;
            if (dead) player.SetStatFlag(StatFlag.Dead);
            world.PlaceCharacter(player, new Point3D(100, 100));
            var npc = world.CreateCharacter();
            world.PlaceCharacter(npc, new Point3D(101, 100));
            Assert.Equal(allowed, npc.TryExecuteCommand("TRYP", "1 NAME=marked", new Console(player, player.PrivLevel)));
        }
        finally { Character.DeadCannotSeeLiving = previous; }
    }
}
