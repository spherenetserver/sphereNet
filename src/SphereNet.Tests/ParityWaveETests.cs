using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-E (wiki/hedef.txt) — script-surface parity:
//   * OC_DISTANCE — <DISTANCE.uid> / <DISTANCE.x,y> on any object (was missing
//     entirely; measures from the TOP-LEVEL position for contained items)
//   * HITPOINTS / USESCUR aliases (Source-X exposes them as first-class keys)
//   * [SKILL n] resource-section trigger stages fire alongside the char-level
//     @Skill* triggers (Source-X Skill_OnTrigger — never executed before)
//   * item verbs CONSUME / BOUNCE / DECAY (CItem::r_Verb)
public class ParityWaveETests
{
    private sealed class NullConsole : SphereNet.Core.Interfaces.ITextConsole
    {
        public static readonly NullConsole Instance = new();
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "test";
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void Distance_ToUid_AndToPoint()
    {
        var world = CreateWorld();
        var a = world.CreateCharacter();
        world.PlaceCharacter(a, new Point3D(100, 100, 0, 0));
        var b = world.CreateCharacter();
        world.PlaceCharacter(b, new Point3D(105, 103, 0, 0));

        Assert.True(a.TryGetProperty($"DISTANCE.{b.Uid.Value:X}", out string d1));
        Assert.Equal(a.Position.GetDistanceTo(b.Position).ToString(), d1);

        Assert.True(a.TryGetProperty("DISTANCE.110,100", out string d2));
        Assert.Equal("10", d2);
    }

    [Fact]
    public void Distance_ContainedItem_MeasuresFromWearer()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);
        var gem = world.CreateItem();
        pack.AddItem(gem);

        var other = world.CreateCharacter();
        world.PlaceCharacter(other, new Point3D(107, 100, 0, 0));

        // The gem measures from its wearer's map position, not its in-pack slot.
        Assert.True(gem.TryGetProperty($"DISTANCE.{other.Uid.Value:X}", out string d));
        Assert.Equal("7", d);
    }

    [Fact]
    public void HitpointsAndUsesCurAliases_ReadAndWrite()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.MaxHits = 80;
        ch.Hits = 55;
        Assert.True(ch.TryGetProperty("HITPOINTS", out string chHits) && chHits == "55");
        Assert.True(ch.TrySetProperty("HITPOINTS", "60"));
        Assert.Equal(60, ch.Hits);

        var item = world.CreateItem();
        item.HitsCur = 12;
        Assert.True(item.TryGetProperty("HITPOINTS", out string itHits) && itHits == "12");
        Assert.True(item.TrySetProperty("USESCUR", "9"));
        Assert.True(item.TryGetProperty("USESCUR", out string uses) && uses == "9");
        Assert.True(item.TryGetProperty("USESREMAINING", out string usesAlias) && usesAlias == "9");
    }

    [Fact]
    public void SkillSectionTrigger_FiresAlongsideCharTrigger()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-skilltrig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "skill.scp");
        // Skill 1 = Anatomy. The [SKILL] section ON=@Start stage never ran
        // pre-W-E (no FireSkillTrigger existed).
        File.WriteAllText(path, "[SKILL 1]\nON=@Start\nTAG.SKILLSECTION=1\n");
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(path);

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            stack.Dispatcher.FireCharTrigger(ch, CharTrigger.SkillStart,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, N1 = 1 });

            Assert.True(ch.TryGetTag("SKILLSECTION", out var v) && v == "1");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SkillSectionTrigger_Return1_Cancels()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-skilltrigret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "skill.scp");
        File.WriteAllText(path, "[SKILL 1]\nON=@Start\nRETURN 1\n");
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(path);

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            var result = stack.Dispatcher.FireCharTrigger(ch, CharTrigger.SkillStart,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, N1 = 1 });

            Assert.Equal(TriggerResult.True, result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ItemConsumeVerb_ReducesStack_AndDeletesAtZero()
    {
        var world = CreateWorld();
        var stack = world.CreateItem();
        stack.Amount = 10;
        world.PlaceItem(stack, new Point3D(100, 100, 0, 0));

        Assert.True(stack.TryExecuteCommand("CONSUME", "3", NullConsole.Instance));
        Assert.Equal(10 - 3, stack.Amount);

        Assert.True(stack.TryExecuteCommand("CONSUME", "7", NullConsole.Instance));
        Assert.True(stack.IsDeleted);
    }

    [Fact]
    public void ItemBounceVerb_ReturnsEquippedItemToBackpack()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);

        var sword = world.CreateItem();
        owner.Equip(sword, Layer.OneHanded);

        Assert.True(sword.TryExecuteCommand("BOUNCE", "", NullConsole.Instance));
        Assert.Equal(pack.Uid, sword.ContainedIn);
        Assert.False(sword.IsEquipped);
    }
}
