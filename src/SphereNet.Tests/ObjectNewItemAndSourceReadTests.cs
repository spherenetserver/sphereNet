using System.Reflection;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// What a scripted taming system needed and did not get:
/// <list type="bullet">
/// <item>NEWITEM is a verb of every object (SSV_NEWITEM, CScriptObj.cpp:1341) and NEW a
/// reference every object reaches (SREF_NEW): "ref1.newitem i_memory" then
/// "ref1.new.color ..." gives the animal its pet memory.</item>
/// <item>&lt;obj.DISTANCE&gt; with no argument is the distance to the line's SRC
/// (OC_DISTANCE, CObjBase.cpp:1262).</item>
/// <item>BRAIN is the Sphere 0.56T spelling of NPC.</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ObjectNewItemAndSourceReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_newitem_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<FieldInfo, object?> _saved = new();

    private void SetServer(string name, object? value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!_saved.ContainsKey(field))
            _saved.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    public void Dispose()
    {
        foreach (var (field, value) in _saved)
            field.SetValue(null, value);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (ScriptRuntimeStack Stack, Character Player, Character Animal) Bench()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "defs.scp");
        File.WriteAllLines(file, ["[ITEMDEF 01f14]", "DEFNAME=i_test_memory", "NAME=test memory"]);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.ScpBaseDir = _dir;
        stack.Resources.LoadResourceFile(file);
        new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        SetServer("_world", world);
        SetServer("_resources", stack.Resources);
        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        stack.Interpreter.ServerPropertyResolver = p => (string?)resolve.Invoke(null, [p]);
        // The server's resolver (Program.EngineWiring): NEW, UID.<n>, then the object's heads.
        stack.Interpreter.ResolveObjectRef = (obj, head) =>
            head.Equals("NEW", StringComparison.OrdinalIgnoreCase)
            ? world.FindObject(world.LastNewObject)
            : head.StartsWith("UID.", StringComparison.OrdinalIgnoreCase)
            ? (ScriptNumber.TryParseLong(head[4..], out long u) ? world.FindObject(new Serial((uint)u)) : null)
            : (obj as ObjBase)?.ResolveScriptRefHead(head);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var animal = world.CreateCharacter();
        animal.NpcBrain = SphereNet.Core.Enums.NpcBrainType.Animal;
        world.PlaceCharacter(animal, new Point3D(103, 100, 0, 0));
        return (stack, player, animal);
    }

    private static void Run(ScriptRuntimeStack stack, Character self, Character src, Character argo,
        params (string Key, string Arg)[] lines) =>
        stack.Interpreter.Execute(lines.Select(l => new ScriptKey(l.Key, l.Arg)).ToList(), self, null,
            new TriggerArgs { Source = src, Object1 = argo }, new ScriptScope());

    [Fact]
    public void ARefNewItemCreatesTheItemAndItsNewLinesReachIt()
    {
        var (stack, player, animal) = Bench();
        Run(stack, player, player, animal,
            ("REF1", "<argo.uid>"),
            ("REF1.NEWITEM", "i_test_memory"),
            ("REF1.NEW.COLOR", "0481"),
            ("REF1.NEW.CONT", "<argo.uid>"),
            ("TAG.NEWUID", "<REF1.NEW.UID>"));

        var memory = animal.GetEquippedItem(SphereNet.Core.Enums.Layer.Special) ??
                     ObjBase.ResolveWorld!()!.GetAllObjects().OfType<Item>().FirstOrDefault(i => i.BaseId == 0x1f14);
        Assert.NotNull(memory);
        Assert.Equal(0x0481, memory!.Hue.Value);
        Assert.Equal($"0{memory.Uid.Value:x}", player.Tags.Get("NEWUID"));
        // ACT goes to the character the verb was addressed to.
        Assert.Equal(memory.Uid, animal.Act);
    }

    [Fact]
    public void DistanceWithoutAnArgumentIsToTheLinesSrc()
    {
        var (stack, player, animal) = Bench();
        Run(stack, player, player, animal, ("TAG.D", "<argo.distance>"));
        Assert.Equal("3", player.Tags.Get("D"));
    }

    [Fact]
    public void BrainIsTheSameKeyAsNpc()
    {
        var (stack, player, animal) = Bench();
        Run(stack, player, player, animal, ("TAG.B", "<argo.brain>"), ("TAG.N", "<argo.npc>"));
        Assert.Equal(player.Tags.Get("N"), player.Tags.Get("B"));
        Assert.NotEqual("0", player.Tags.Get("B"));
    }
}
