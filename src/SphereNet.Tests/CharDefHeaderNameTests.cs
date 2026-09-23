using Microsoft.Extensions.Logging;
using SphereNet.Game.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A [CHARDEF c_name] block with no DEFNAME line is named by its header, as
/// upstream names every resource. The save writes that name into [WORLDCHAR c_name];
/// an empty one produced a bare [WORLDCHAR] that other Sphere servers cannot load.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharDefHeaderNameTests
{
    [Fact]
    public void AHeaderNamedCharDefCarriesItsName()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chardef-header-{Guid.NewGuid():N}.scp");
        using var lf = LoggerFactory.Create(_ => { });
        try
        {
            File.WriteAllText(path,
                "[CHARDEF c_header_named]\nID=c_man\nNAME=header named\n" +
                "[CHARDEF 0123]\nDEFNAME=c_numeric_with_defname\nID=0x190\n" +
                "[EOF]\n");
            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
            { ScpBaseDir = Path.GetDirectoryName(path) ?? "" };
            resources.LoadResourceFile(path);
            new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();

            int named = resources.ResolveDefName("c_header_named").Index;
            Assert.Equal("c_header_named", CharDefHelper.ResolveDefName(named));
            int numeric = resources.ResolveDefName("c_numeric_with_defname").Index;
            Assert.Equal("c_numeric_with_defname", CharDefHelper.ResolveDefName(numeric));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
