using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.MapData;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class NpcCanAuditProbeTests
{
    private static void WithCan(string can, Action<SphereNet.Game.World.GameWorld, Character, NpcAI> probe)
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"npc_can_audit_{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[CHARDEF 0cf]\nDEFNAME=c_audit\nCAN={can}\n[CHARDEF 0bba]\nID=c_audit\n");
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
            var world = TestHarness.CreateWorld();
            var map = new MapDataManager("");
            map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
            world.MapData = map;
            var npc = world.CreateCharacter();
            npc.CharDefIndex = 0xcf; npc.BodyId = 0xcf;
            npc.Str = npc.Dex = npc.Int = npc.Hits = npc.Stam = 100;
            npc.Stam = npc.MaxStam;
            world.PlaceCharacter(npc, new(100, 100, 0, 0));
            probe(world, npc, new NpcAI(world, new SphereConfig()) { Flags = NpcAIFlags.None });
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("010004")]
    [InlineData("040004")]
    [InlineData("0")]
    [InlineData("02")]
    public void MovementFlagsBlockIneligibleGroundMovement(string can) => WithCan(can, (w, n, ai) =>
    {
        typeof(NpcAI).GetMethod("MoveToward", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, [n, new Point3D(105, 100, 0, 0), false]);
        Assert.Equal(new Point3D(100, 100, 0, 0), n.Position);
    });

    [Fact]
    public void RunCapableNpcRetainsRunningDirection() => WithCan("02004", (w, n, ai) =>
    {
        typeof(NpcAI).GetMethod("MoveToward", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, [n, new Point3D(105, 100, 0, 0), true]);
        Assert.Equal(0x80, (int)n.Direction & 0x80);
    });

    [Fact]
    public void NoHandsCannotEquipWeapon() => WithCan("04", (w, n, ai) =>
    {
        var item = w.CreateItem(); item.ItemType = ItemType.WeaponSword; item.EquipLayer = Layer.OneHanded;
        Assert.False(n.CanEquip(item, Layer.OneHanded, out _));
    });

    [Fact]
    public void FireImmuneRejectsFireDamage() => WithCan("024", (w, n, ai) =>
        Assert.Equal(0, CombatEngine.ApplyScriptDamage(n, 20, DamageType.Fire)));

    [Fact]
    public void CanMaskChangesEffectiveCapabilities() => WithCan("04", (w, n, ai) =>
    {
        n.TrySetProperty("CANMASK", "010000");
        Assert.True((CharDefHelper.GetCanFlags(n) & CanFlags.C_NonMover) != 0);
    });

    [Fact]
    public void FemaleFlagControlsNpcSex() => WithCan("0804", (w, n, ai) => Assert.True(n.IsFemale));

    [Fact]
    public void WideHexFlagSurvivesPipeLoader() => WithCan("010000|04", (w, n, ai) =>
        Assert.Equal((CanFlags)0x10004, CharDefHelper.GetCanFlags(n)));

    [Fact]
    public void IdInheritsBaseCanFlags() => WithCan("02004", (w, n, ai) =>
        Assert.Equal((CanFlags)0x2004, DefinitionLoader.GetCharDef(0xbba)!.Can));

    [Fact]
    public void LivingGhostCanPassClosedDoor() => WithCan("05", (w, n, ai) =>
    {
        w.MapData!.SetSyntheticItemTile(0x06A5, new SphereNet.MapData.Tiles.ItemTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.Door | SphereNet.MapData.Tiles.TileFlag.Impassable, Height = 20 });
        w.MapData.AddSyntheticStatic(0, 101, 100, 0x06A5, 0);
        Assert.True(w.Standing.CheckMovement(n, n.Position, Direction.East, out _));
    });

    [Fact]
    public void SwimmerCanEnterWater() => WithCan("02", (w, n, ai) =>
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 0x00A8);
        map.SetSyntheticLandTile(0x00A8, new SphereNet.MapData.Tiles.LandTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.Wet | SphereNet.MapData.Tiles.TileFlag.Impassable });
        w.MapData = map;
        Assert.True(w.Standing.CheckMovement(n, n.Position, Direction.East, out _));
    });

    [Fact]
    public void AnimalWithoutMountFlagCannotRide() => WithCan("04", (w, n, ai) =>
    {
        var horse = w.CreateCharacter(); horse.BodyId = 0xc8; horse.Str = horse.Hits = 100;
        Assert.True(horse.TryAssignOwnership(n));
        w.PlaceCharacter(horse, new(101, 100, 0, 0));
        Assert.False(new SphereNet.Game.Mounts.MountEngine(w).TryMount(n, horse, 0x3e9f));
    });

    [Theory]
    [InlineData("04", false)]
    [InlineData("020004", true)]
    public void NoBlockHeightAllowsLowCeiling(string can, bool allowed) => WithCan(can, (w,n,ai) =>
    {
        w.MapData!.SetSyntheticItemTile(0x1000, new SphereNet.MapData.Tiles.ItemTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.Surface, Height = 1 });
        w.MapData.AddSyntheticStatic(0, 101, 100, 0x1000, 8);
        Assert.Equal(allowed, w.Standing.CheckMovement(n, n.Position, Direction.East, out _));
    });

    [Theory]
    [InlineData("04", true)]
    [InlineData("044", false)]
    public void NoIndoorsRejectsCoveredTile(string can, bool allowed) => WithCan(can, (w,n,ai) =>
    {
        w.MapData!.SetSyntheticItemTile(0x1000, new SphereNet.MapData.Tiles.ItemTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.Roof | SphereNet.MapData.Tiles.TileFlag.Surface, Height = 1 });
        w.MapData.AddSyntheticStatic(0, 101, 100, 0x1000, 25);
        Assert.Equal(allowed, w.Standing.CheckMovement(n, n.Position, Direction.East, out _));
    });

    [Fact]
    public void CanMaskTogglesRatherThanOrsAndGodBypassesFireImmunity() => WithCan("024", (w,n,ai) =>
    {
        Assert.True(n.TrySetProperty("CANMASK", "020"));
        Assert.Equal(CanFlags.C_Walk, CharDefHelper.GetCanFlags(n));
        Assert.True(n.TryGetProperty("CAN", out string effectiveCan));
        Assert.Equal("04", effectiveCan);
        Assert.False(n.TryGetTag("CANMASK", out _));
        Assert.Equal(20, CombatEngine.ApplyScriptDamage(n, 20, DamageType.Fire));
        n.CanMask = 0;
        Assert.Equal(20, CombatEngine.ApplyScriptDamage(n, 20, DamageType.Fire | DamageType.God));
    });

    [Fact]
    public void ExistingNpcReadsReloadedScriptAndKeepsOnlyInstanceMask() => WithCan("04", (w,n,ai) =>
    {
        n.CanMask = (ulong)CanFlags.C_Run;
        Assert.Equal(CanFlags.C_Walk | CanFlags.C_Run, CharDefHelper.GetCanFlags(n));
        WithCan("024", (_, _, _) =>
        {
            Assert.Equal(CanFlags.C_Walk | CanFlags.C_FireImmune | CanFlags.C_Run, CharDefHelper.GetCanFlags(n));
            Assert.Equal(0, CombatEngine.ApplyScriptDamage(n, 20, DamageType.Fire));
        });
    });

    [Theory]
    [InlineData("04", false)]
    [InlineData("084", true)]
    public void HoverSurfaceRequiresHoverCapability(string can, bool allowed) => WithCan(can, (w,n,ai) =>
    {
        w.MapData!.SetSyntheticItemTile(0x1000, new SphereNet.MapData.Tiles.ItemTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.HoverOver | SphereNet.MapData.Tiles.TileFlag.Impassable, Height = 1 });
        w.MapData.AddSyntheticStatic(0, 101, 100, 0x1000, 0);
        Assert.Equal(allowed, w.Standing.CheckMovement(n, n.Position, Direction.East, out _));
    });

    [Theory]
    [InlineData("05", false)]
    [InlineData("0c", true)]
    public void PathfindingDistinguishesGhostDoorsFromWalls(string can, bool allowed) => WithCan(can, (w,n,ai) =>
    {
        var wall = w.CreateItem(); wall.ItemType = ItemType.Wall; wall.BaseId = 0x1000;
        w.MapData!.SetSyntheticItemTile(wall.BaseId, new SphereNet.MapData.Tiles.ItemTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.Impassable, Height = 20 });
        w.PlaceItem(wall, new(101,100,0,0));
        Assert.Equal(allowed, new SphereNet.Game.Movement.Pathfinder(w).FindPath(n.Position, wall.Position, 0, self:n) != null);
    });

    [Fact]
    public void StatueViewUsesSourceXFramePacket() => WithCan("040004", (w,n,ai) =>
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var client = TestHarness.CreateClient(logs, w, new SphereNet.Game.Accounts.AccountManager(logs), 8991);
        n.SetTag("STATUE_ANIM", "02"); n.SetTag("STATUE_FRAME", "03");
        TestHarness.ClearQueuedPackets(client.NetState);
        client.SendCharacterView(n);
        var packets = TestHarness.GetQueuedPackets(client.NetState).ToArray();
        var packet = Assert.Single(packets, p => p.Span[0] == 0xBF);
        Assert.Equal(17, packet.Length);
        Assert.Equal(new byte[] { 0xBF,0,17,0,0x19,5 }, packet.Span[..6].ToArray());
        Assert.Equal(n.Uid.Value, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.Span[6..]));
        Assert.Equal(new byte[] { 0,0xFF,1,0,2,0,3 }, packet.Span[10..].ToArray());
    });

    [Fact]
    public void CanInheritanceRespectsScriptOrderAndExplicitZero()
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"npc_can_order_{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, """
                [DEFNAME audit_can]
                custom_can=010004
                [CHARDEF 0bba]
                ID=c_audit_parent
                CAN=0
                [CHARDEF 0bbb]
                CAN=0
                ID=c_audit_parent
                [CHARDEF 0cf]
                DEFNAME=c_audit_parent
                CAN=custom_can|020
                """);
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
            Assert.Equal(CanFlags.None, DefinitionLoader.GetCharDef(0xbba)!.Can);
            Assert.Equal((CanFlags)0x10024, DefinitionLoader.GetCharDef(0xbbb)!.Can);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NonSelectableDoesNotOpenCharacterPaperdoll() => WithCan("080004", (w,n,ai) =>
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var client = TestHarness.CreateClient(logs, w, new SphereNet.Game.Accounts.AccountManager(logs), 8992);
        var player = w.CreateCharacter(); player.IsPlayer = true; player.BodyId = 0x190;
        w.PlaceCharacter(player, new(100,101,0,0));
        TestHarness.AttachCharacter(client, player);
        TestHarness.ClearQueuedPackets(client.NetState);
        client.ItemUse.HandleDoubleClick(n.Uid.Value | 0x80000000);
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0x88);
        n.CanMask = (ulong)CanFlags.C_NonSelectable;
        client.ItemUse.HandleDoubleClick(n.Uid.Value | 0x80000000);
        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0x88);
    });

    [Fact]
    public void NativeMaskSurvivesWorldSaveAndLoad() => WithCan("04", (w,n,ai) =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-can-" + Guid.NewGuid().ToString("N"));
        try
        {
            n.CanMask = 0x10000;
            using var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            var saver = new SphereNet.Persistence.Save.WorldSaver(lf);
            saver.Save(w, dir);
            var restored = TestHarness.CreateWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(restored, dir);
            var loaded = Assert.Single(restored.GetCharsInRange(n.Position, 1));
            Assert.Equal(n.CanMask, loaded.CanMask);
            Assert.False(loaded.TryGetTag("SAVE.CANMASK", out _));
            Assert.Equal(CharDefHelper.GetCanFlags(n), CharDefHelper.GetCanFlags(loaded));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    });
}
