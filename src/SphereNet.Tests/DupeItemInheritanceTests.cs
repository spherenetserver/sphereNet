using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DupeItemInheritanceTests
{
    private static ResourceHolder LoadScript(string contents)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_dupe_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    [Fact]
    public void DupeItem_InheritsTypeAndLayerFromMaster()
    {
        // The flip half of a fishing pole (0dc0) carries only DUPEITEM=0dbf.
        // Without inheritance its Type stays t_normal and double-click fishing
        // never dispatches; the master (0dbf) declares TYPE=t_fish_pole.
        var resources = LoadScript("""
            [ITEMDEF 0dbf]
            DEFNAME=i_fishing_pole
            TYPE=t_fish_pole
            LAYER=2
            FLIP=1
            DUPELIST=0dc0

            [ITEMDEF 0dc0]
            DUPEITEM=0dbf
            """);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var master = DefinitionLoader.GetItemDef(0x0dbf);
        var dupe = DefinitionLoader.GetItemDef(0x0dc0);

        Assert.NotNull(master);
        Assert.NotNull(dupe);
        Assert.Equal(ItemType.FishPole, master!.Type);
        // Regression: the dupe now mirrors the master's gameplay type/layer.
        Assert.Equal(ItemType.FishPole, dupe!.Type);
        Assert.Equal((Layer)2, dupe.Layer);
    }
}
