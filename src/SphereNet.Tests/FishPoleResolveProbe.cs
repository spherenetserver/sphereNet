using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class FishPoleResolveProbe
{
    private readonly ITestOutputHelper _out;
    public FishPoleResolveProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void FishingPole_And_FlipVariant_ResolveToFishPole()
    {
        // In-repo reference pack.
        string[] roots =
        {
            Path.Combine(FindRepo(), "oldSphere", "Scripts-X-main"),
        };
        string? scripts = null;
        foreach (var r in roots) if (Directory.Exists(r)) { scripts = r; break; }
        if (scripts == null) { _out.WriteLine("script pack not present"); return; }

        var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (var f in ScriptResourceManifest.Resolve(scripts))
            res.LoadResourceFile(f);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        var rid = res.ResolveDefName("i_fishing_pole");
        _out.WriteLine($"i_fishing_pole rid valid={rid.IsValid} type={rid.Type} index=0x{rid.Index:X}");
        Assert.True(rid.IsValid && rid.Type == ResType.ItemDef);

        var master = DefinitionLoader.GetItemDef(rid.Index);
        _out.WriteLine($"master def: index=0x{rid.Index:X} type={master?.Type} layer={master?.Layer} disp=0x{master?.DispIndex:X}");
        Assert.NotNull(master);
        Assert.Equal(ItemType.FishPole, master!.Type);

        // The flip/dupe graphics from the pack (0x0dbf master, 0x0dc0 dupe).
        foreach (ushort gfx in new ushort[] { 0x0dbf, 0x0dc0 })
        {
            var d = DefinitionLoader.GetItemDef(gfx);
            _out.WriteLine($"gfx 0x{gfx:X}: def={(d == null ? "null" : $"type={d.Type} layer={d.Layer} dupeItem=0x{d.DupItemId:X}")}");
            Assert.NotNull(d);
            Assert.Equal(ItemType.FishPole, d!.Type);
        }
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SphereNet.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
