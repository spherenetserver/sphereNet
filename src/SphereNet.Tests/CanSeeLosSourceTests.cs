using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// &lt;obj.CANSEELOS&gt; with no argument is "can SRC see this object": CanSee, then
/// CanSeeLOS from the SRC character (OC_CANSEELOS, CObjBase.cpp:1152-1155). It answered
/// 0 because a property read had no SRC, and a pack's taming check
/// IF !(&lt;ARGO.CANSEELOS&gt;) then refused every animal as unreachable.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CanSeeLosSourceTests
{
    private static (ScriptRuntimeStack Stack, SphereNet.Game.Objects.Characters.Character Player,
        SphereNet.Game.Objects.Characters.Character Animal) Bench()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true; // a logged-out character sees nothing (CanSee)
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var animal = world.CreateCharacter();
        world.PlaceCharacter(animal, new Point3D(102, 100, 0, 0));
        return (stack, player, animal);
    }

    [Fact]
    public void WithoutAnArgumentItAnswersForTheLinesSrc()
    {
        var (stack, player, animal) = Bench();
        stack.Interpreter.Execute([new ScriptKey("TAG.LOS", "<argo.canseelos>")], player, null,
            new TriggerArgs { Source = player, Object1 = animal }, new ScriptScope());
        Assert.Equal("1", player.Tags.Get("LOS"));
    }

    [Fact]
    public void WithoutASrcCharacterItAnswersZero()
    {
        var (stack, player, animal) = Bench();
        stack.Interpreter.Execute([new ScriptKey("TAG.LOS", "<argo.canseelos>")], player, null,
            new TriggerArgs { Object1 = animal }, new ScriptScope());
        Assert.Equal("0", player.Tags.Get("LOS") ?? "0");
    }

    [Fact]
    public void ASrcThatCannotSeeTheTargetAnswersZero()
    {
        var (stack, player, animal) = Bench();
        animal.SetStatFlag(SphereNet.Core.Enums.StatFlag.Invisible);
        stack.Interpreter.Execute([new ScriptKey("TAG.LOS", "<argo.canseelos>")], player, null,
            new TriggerArgs { Source = player, Object1 = animal }, new ScriptScope());
        Assert.Equal("0", player.Tags.Get("LOS") ?? "0");
    }

    [Fact]
    public void TheSrcDoesNotLeakOutOfTheRead()
    {
        var (stack, player, animal) = Bench();
        stack.Interpreter.Execute([new ScriptKey("TAG.LOS", "<argo.canseelos>")], player, null,
            new TriggerArgs { Source = player, Object1 = animal }, new ScriptScope());
        Assert.Null(ScriptReadContext.Source);
    }
}
