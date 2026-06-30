using System;
using System.Collections.Generic;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X gathering parity (wiki/11.txt audit). Fishing/Lumberjacking now gate on
// terrain (water / tree). The tiledata-based rejection can only be exercised with
// real MUL data, but these tests lock the important regression: when no map data
// is loaded the guards must stay PERMISSIVE so the gather flow still runs (rather
// than silently rejecting every tile and breaking gathering in data-less setups).
public class CraftGatherParityTests
{
    private sealed class RecordingActiveSink : IActiveSkillSink
    {
        public Character Self { get; }
        public Random Random { get; }
        public GameWorld World { get; }
        public List<(string Channel, string Text)> Log { get; } = new();
        public List<Item> Delivered { get; } = new();
        public Dictionary<ItemType, Item> Pack { get; } = new();

        public RecordingActiveSink(Character self, GameWorld world)
        {
            Self = self; World = world; Random = new Random(1);
        }

        public void SysMessage(string text) => Log.Add(("SYS", text));
        public void ObjectMessage(ObjBase target, string text) => Log.Add(("OBJ", text));
        public void Emote(string text) => Log.Add(("EMOTE", text));
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => Pack.TryGetValue(type, out var i) ? i : null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) => Delivered.Add(item);
    }

    private static void GiveTool(RecordingActiveSink sink, GameWorld world, ItemType toolType)
    {
        var tool = world.CreateItem();
        tool.ItemType = toolType;
        sink.Pack[toolType] = tool;
    }

    private static GameWorld MakeWorld()
    {
        // no MapData (terrain guards stay permissive). Pin the ResolveWorld statics
        // to THIS world so a prior test's stale world can't leak in (the suite
        // shares these statics; without pinning, test order flips behaviour).
        var world = new GameWorld(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar()
    {
        var ch = new Character
        {
            Name = "Hero", IsPlayer = true, NpcBrain = NpcBrainType.Human, BodyId = 0x0190,
            MaxHits = 50, Hits = 50, MaxMana = 50, Mana = 50,
            Position = new Point3D(100, 100, 0, 0),
        };
        ch.SetSkill(SkillType.Fishing, 500);
        ch.SetSkill(SkillType.Lumberjacking, 500);
        return ch;
    }

    [Fact]
    public void Fishing_NoMapData_TerrainGuardStaysPermissive()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world);
        GiveTool(sink, world, ItemType.FishPole);

        ActiveSkillEngine.Fishing(sink, new Point3D(101, 100, 0, 0), null, world);

        // Match the terrain-guard message specifically ("can't fish THERE"); the
        // unrelated RNG gather-fail line ("can't fish from where you are standing")
        // must not be caught by this assertion.
        Assert.DoesNotContain(sink.Log, e => e.Text.Contains("fish there", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sink.Log, e => e.Text.Contains("fishing pole", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Lumberjacking_NoMapData_TerrainGuardStaysPermissive()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world);
        GiveTool(sink, world, ItemType.WeaponAxe);

        ActiveSkillEngine.Lumberjacking(sink, new Point3D(101, 100, 0, 0), null, world);

        Assert.DoesNotContain(sink.Log, e => e.Text.Contains("no tree", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sink.Log, e => e.Text.Contains("axe to chop", StringComparison.OrdinalIgnoreCase));
    }

    // ---- #2: gathering requires a tool ----

    [Fact]
    public void Fishing_WithoutPole_IsRejected()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world); // no pole

        bool ok = ActiveSkillEngine.Fishing(sink, new Point3D(101, 100, 0, 0), null, world);

        Assert.False(ok);
        Assert.Contains(sink.Log, e => e.Text.Contains("fishing pole", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mining_WithoutPickaxe_IsRejected()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world); // no pickaxe

        bool ok = ActiveSkillEngine.Mining(sink, new Point3D(101, 100, 0, 0), null, world);

        Assert.False(ok);
        Assert.Contains(sink.Log, e => e.Text.Contains("pickaxe", StringComparison.OrdinalIgnoreCase));
    }
}
