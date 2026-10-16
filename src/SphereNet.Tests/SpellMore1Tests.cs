using SphereNet.Core.Enums;
using SphereNet.Game.Objects;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A spell named in MORE1 - a potion's m_itPotion.m_Type, written MORE1=s_poison in a
/// pack's @Create - is the spell's number, the same number s_poison evaluates to in a
/// script. It was parked in a MORE1_DEFNAME tag with MORE1 left at 0, so a pack's
/// skill check IF (&lt;SRC.ACT.MORE1&gt; == s_poison) refused every poison potion.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellMore1Tests
{
    [Fact]
    public void ASpellNamedInMore1IsItsNumber()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"spellmore1-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [SPELL 20]
            DEFNAME=s_poison
            NAME=Poison
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            new SphereNet.Game.Definitions.DefinitionLoader(stack.Resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
            var world = TestHarness.CreateWorld();
            ObjBase.ResolveWorld = () => world;
            var ch = world.CreateCharacter();
            var potion = world.CreateItem();
            potion.ItemType = ItemType.Potion;

            Assert.True(potion.TrySetProperty("MORE1", "s_poison"));
            Assert.Equal(20u, potion.More1);

            stack.Interpreter.Execute([new ScriptKey("TAG.SAME", "<eval (<argo.more1> == s_poison)>")], ch, null,
                new TriggerArgs { Source = ch, Object1 = potion }, new ScriptScope());
            Assert.Equal("1", ch.Tags.Get("SAME"));
        }
        finally { File.Delete(path); }
    }
}
