using SphereNet.Core.Enums;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

// Wave S1 (wiki/housing-movement-scriptpack.txt): script-pack visibility —
// unknown def keys are counted instead of vanishing silently — and the
// legacy VISIBLE / SFX verbs.
public class ScriptSurfaceWaveS1Tests
{
    [Fact]
    public void UnknownDefKeys_AreCounted_NotSilentlyDropped()
    {
        UnknownKeyDiagnostics.Clear();
        var charDef = new CharDef(default);
        charDef.LoadFromKey("TOTALLYUNKNOWNKEY", "1");
        charDef.LoadFromKey("TOTALLYUNKNOWNKEY", "2");
        var itemDef = new ItemDef(default);
        itemDef.LoadFromKey("WEIRDPROP", "x");

        Assert.Equal(3, UnknownKeyDiagnostics.TotalDropped);
        Assert.Contains(UnknownKeyDiagnostics.Summary(), s => s.StartsWith("CHARDEF.TOTALLYUNKNOWNKEY x2"));
        Assert.Contains(UnknownKeyDiagnostics.Summary(), s => s.StartsWith("ITEMDEF.WEIRDPROP x1"));

        // Known keys and TAG. lines must NOT count as unknown.
        UnknownKeyDiagnostics.Clear();
        charDef.LoadFromKey("DAMFIRE", "25");
        charDef.LoadFromKey("TAG.CUSTOM", "1");
        Assert.Equal(0, UnknownKeyDiagnostics.TotalDropped);
    }

    [Fact]
    public void VisibleVerb_DropsInvisibilityAndHiding()
    {
        var ch = new SphereNet.Game.Objects.Characters.Character();
        ch.SetStatFlag(StatFlag.Invisible);
        ch.SetStatFlag(StatFlag.Hidden);

        Assert.True(ch.TryExecuteCommand("VISIBLE", "", null!));
        Assert.False(ch.IsStatFlag(StatFlag.Invisible));
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void NewNpcVerb_SpawnsThroughTheHook_AndRecordsLastNewChar()
    {
        var world = TestHarness.CreateWorld();
        var invoker = world.CreateCharacter();
        world.PlaceCharacter(invoker, new SphereNet.Core.Types.Point3D(100, 100, 0, 0));

        SphereNet.Game.Objects.Characters.Character? spawned = null;
        SphereNet.Game.Objects.Characters.Character.SpawnNpcFromScript = (inv, def) =>
        {
            spawned = world.CreateCharacter();
            spawned.Name = def;
            world.PlaceCharacter(spawned, inv.Position);
            world.LastNewChar = spawned.Uid;
            return spawned;
        };

        Assert.True(invoker.TryExecuteCommand("NEWNPC", "c_orc", null!));
        Assert.NotNull(spawned);
        Assert.Equal("c_orc", spawned!.Name);
        Assert.Equal(spawned.Uid, world.LastNewChar);
    }

    [Fact]
    public void SfxVerb_IsASoundAlias()
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        world.PlaceItem(item, new SphereNet.Core.Types.Point3D(100, 100, 0, 0));
        // SOUND is the reference verb; SFX is the legacy 55i alias.
        Assert.True(item.TryExecuteCommand("SFX", "0x0042", null!));
    }
}

