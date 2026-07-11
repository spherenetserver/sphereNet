using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXCompatibilityTests
{
    [Fact]
    public void ResourceManifest_UsesTablesOrderAndDoesNotRecursivelyLoadDisabledScripts()
    {
        string root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "items", "not_complete"));
            File.WriteAllText(Path.Combine(root, "spheretables.scp"),
                "[RESOURCES]\nitems/\nitems/not_complete/enabled.scp\n");
            File.WriteAllText(Path.Combine(root, "items", "a.scp"), "[EOF]\n");
            File.WriteAllText(Path.Combine(root, "items", "b.scp"), "[EOF]\n");
            File.WriteAllText(Path.Combine(root, "items", "not_complete", "enabled.scp"), "[EOF]\n");
            File.WriteAllText(Path.Combine(root, "items", "not_complete", "disabled.scp"), "[EOF]\n");

            var files = ScriptResourceManifest.Resolve(root);

            Assert.Equal(new[] { "spheretables.scp", "a.scp", "b.scp", "enabled.scp" },
                files.Select(Path.GetFileName));
            Assert.DoesNotContain(files, path => path.EndsWith("disabled.scp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SourceXPack_ManifestSelectsExactlyTheDeclaredFiles()
    {
        string pack = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "oldSphere", "Scripts-X-main"));
        if (!Directory.Exists(pack))
            return;

        var files = ScriptResourceManifest.Resolve(pack);

        Assert.Equal(756, files.Count);
        Assert.Equal("spheretables.scp", Path.GetFileName(files[0]), ignoreCase: true);
        Assert.DoesNotContain(files, path => path.Replace('\\', '/').Contains("/items/not_complete/"));
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SourceXPack_AllDeclaredFilesParseAndDefinitionsLoad()
    {
        string pack = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "oldSphere", "Scripts-X-main"));
        if (!Directory.Exists(pack))
            return;

        var resources = CreateResources(pack);
        var files = ScriptResourceManifest.Resolve(pack);
        foreach (string file in files)
            Assert.True(resources.LoadResourceFile(file) >= 0, $"Could not load {file}");

        var spells = new SpellRegistry();
        var loader = new DefinitionLoader(resources, spells);
        loader.LoadAll();

        Assert.Equal(756, resources.ScriptFiles.Count);
        Assert.True(resources.ResourceCount > 30_000, $"Only {resources.ResourceCount} resources indexed");
        Assert.True(loader.ItemDefsLoaded > 10_000, $"Only {loader.ItemDefsLoaded} ITEMDEFs loaded");
        Assert.True(loader.CharDefsLoaded > 900, $"Only {loader.CharDefsLoaded} CHARDEFs loaded");
        Assert.True(spells.Count > 60, $"Only {spells.Count} SPELLs loaded");
    }

    [Fact]
    public void ScriptFile_FallsBackToWindows1252WithoutReplacingCharacters()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_ansi_{Guid.NewGuid():N}.scp");
        try
        {
            ScriptFile.ConfigureTextEncoding("AUTO", 1252);
            File.WriteAllBytes(file, Encoding.GetEncoding(1252).GetBytes("[FUNCTION f_ansi]\nTAG.TEXT=café\n"));
            using var script = new ScriptFile { UseCache = false };

            Assert.True(script.Open(file));
            var section = Assert.Single(script.ReadAllSections());
            Assert.Equal("café", Assert.Single(section.Keys).Arg);
        }
        finally
        {
            ScriptFile.ConfigureTextEncoding("AUTO", 1252);
            File.Delete(file);
        }
    }

    [Fact]
    public void ScriptEncodingModes_HonorUtf8AndConfiguredLegacyCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_encoding_{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(file, "[FUNCTION f_utf8]\nTAG.TEXT=İstanbul, şğü\n", new UTF8Encoding(false));
            ScriptFile.ConfigureTextEncoding("UTF8", 1252);
            using (var utf8 = new ScriptFile { UseCache = false })
            {
                Assert.True(utf8.Open(file));
                Assert.Equal("İstanbul, şğü", Assert.Single(Assert.Single(utf8.ReadAllSections()).Keys).Arg);
            }

            File.WriteAllBytes(file, Encoding.GetEncoding(1254).GetBytes(
                "[FUNCTION f_legacy]\nTAG.TEXT=İstanbul, şğü\n"));
            ScriptFile.ConfigureTextEncoding("LEGACY", 1254);
            using (var legacy = new ScriptFile { UseCache = false })
            {
                Assert.True(legacy.Open(file));
                Assert.Equal("İstanbul, şğü", Assert.Single(Assert.Single(legacy.ReadAllSections()).Keys).Arg);
            }

            ScriptFile.ConfigureTextEncoding("UTF8", 1252);
            using var invalidUtf8 = new ScriptFile { UseCache = false };
            Assert.Throws<DecoderFallbackException>(() => invalidUtf8.Open(file));
        }
        finally
        {
            ScriptFile.ConfigureTextEncoding("AUTO", 1252);
            File.Delete(file);
        }
    }

    [Fact]
    public void SphereConfig_LoadsScriptEncodingSettings()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_encoding_cfg_{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllText(file, "[SPHERE]\nScriptEncoding=LEGACY\nScriptLegacyCodePage=1254\n");
            var ini = new IniParser();
            ini.Load(file);
            var config = new SphereConfig();
            config.LoadFromIni(ini);

            Assert.Equal("LEGACY", config.ScriptEncoding);
            Assert.Equal(1254, config.ScriptLegacyCodePage);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ItemDef_StaticLoaderStopsAtFirstTriggerBody()
    {
        string root = CreateTempDirectory();
        try
        {
            string file = Path.Combine(root, "item.scp");
            File.WriteAllText(file, """
                [ITEMDEF i_trigger_boundary]
                DEFNAME=i_trigger_boundary
                ID=0eed
                NAME=Static name
                ON=@Create
                NAME=Trigger must not overwrite static definition
                TYPE=t_container
                """);
            var resources = CreateResources(root);
            resources.LoadResourceFile(file);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

            var rid = resources.ResolveDefName("i_trigger_boundary");
            var def = DefinitionLoader.GetItemDef(rid.Index);
            Assert.NotNull(def);
            Assert.Equal("Static name", def.Name);
            Assert.NotEqual(SphereNet.Core.Enums.ItemType.Container, def.Type);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PlevelBlocksForTheSameLevelAreMergedAcrossFiles()
    {
        string root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "one.scp"), "[PLEVEL 4]\nSAVE\n");
            File.WriteAllText(Path.Combine(root, "two.scp"), "[PLEVEL 4]\nRESYNC\n");
            var resources = CreateResources(root);
            resources.LoadResourceFile(Path.Combine(root, "one.scp"));
            resources.LoadResourceFile(Path.Combine(root, "two.scp"));

            var merged = Assert.Single(resources.GetPlevelCommandSections());
            Assert.Equal(4, merged.Level);
            Assert.Equal(new[] { "SAVE", "RESYNC" }, merged.Commands.Select(k => k.Key));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ResourceHolder CreateResources(string root)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        return new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>()) { ScpBaseDir = root };
    }

    private static string CreateTempDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"spherenet_sourcex_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
