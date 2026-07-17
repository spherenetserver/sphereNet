using System;
using System.IO;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// B2 (house/ship review): a scripted deed written as "[ITEMDEF i_deed_house] ID=i_deed"
/// with no TYPE of its own must inherit TYPE=t_deed from the referenced base — Source-X's
/// IBC_ID dupes the referenced typed base. Without this the child stays ItemType.Normal
/// and never reaches the deed handler, so house/ship placement never starts.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXDeedInheritanceTests
{
    private static string WriteScript(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_deed_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void IdReference_InheritsBaseType_SoDeedIsNotNormal()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [ITEMDEF i_test_deed_base]
            DEFNAME=i_test_deed_base
            ID=0eec
            TYPE=t_deed

            [ITEMDEF i_test_deed_house]
            ID=i_test_deed_base
            NAME=Deed to a Test House

            [ITEMDEF i_test_plain_ref]
            DEFNAME=i_test_plain_ref
            ID=0f6c

            [ITEMDEF i_test_plain_child]
            ID=i_test_plain_ref
            NAME=Just a graphic copy
            """);
        stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        // Base is a deed.
        int baseIdx = stack.Resources.ResolveDefName("i_test_deed_base").Index;
        var baseDef = Assert.IsType<SphereNet.Scripting.Definitions.ItemDef>(DefinitionLoader.GetItemDef(baseIdx));
        Assert.Equal(ItemType.Deed, baseDef.Type);

        // Child with only `ID=i_test_deed_base` (no TYPE) inherits the deed type.
        int childIdx = stack.Resources.ResolveDefName("i_test_deed_house").Index;
        var childDef = Assert.IsType<SphereNet.Scripting.Definitions.ItemDef>(DefinitionLoader.GetItemDef(childIdx));
        Assert.Equal(ItemType.Deed, childDef.Type);

        // A child referencing a TYPE-less graphic base stays Normal (no over-inheritance).
        int plainIdx = stack.Resources.ResolveDefName("i_test_plain_child").Index;
        var plainDef = Assert.IsType<SphereNet.Scripting.Definitions.ItemDef>(DefinitionLoader.GetItemDef(plainIdx));
        Assert.Equal(ItemType.Normal, plainDef.Type);
    }
}
