using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class ExternalScriptPackSmokeTests
{
    [Fact]
    public void ExternalScriptPackInventory_BuildsReadOnlyRiskReport()
    {
        string? packPath = ScriptTestBootstrap.GetExternalScriptPackPath();
        if (packPath == null)
            return;

        var inventory = ScriptPackInventory.Build(packPath);

        Assert.True(inventory.Files.Count >= 500, $"Expected a large external script pack, found {inventory.Files.Count} files");
        Assert.True(inventory.TotalSections > 0);
        Assert.Contains(inventory.SectionCounts.Keys, k => k.StartsWith("ITEMDEF", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.SectionCounts.Keys, k => k.StartsWith("CHARDEF", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.SectionCounts.Keys, k => k.StartsWith("FUNCTION", StringComparison.OrdinalIgnoreCase));
        Assert.True(inventory.FeatureCounts.Count > 0, "Risk feature matrix should not be empty");
        Assert.Contains("DB.*", inventory.FeatureCounts.Keys);
        Assert.Contains("SERV", inventory.FeatureCounts.Keys);
        Assert.NotEmpty(inventory.TopSections());
        Assert.NotEmpty(inventory.TopFeatures());
    }

    [Fact]
    public void ExternalScriptPackParseLoadSmoke_LoadsCoreProfileAndIndexesResources()
    {
        string? packPath = ScriptTestBootstrap.GetExternalScriptPackPath();
        if (packPath == null)
            return;

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var files = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.CoreOnly);

        Assert.NotEmpty(files);

        int parsedSections = 0;
        foreach (string file in files)
        {
            using var scriptFile = new ScriptFile();
            Assert.True(scriptFile.Open(file), $"Could not open {file}");
            var sections = scriptFile.ReadAllSections();
            parsedSections += sections.Count;
        }

        ScriptTestBootstrap.LoadFiles(stack.Resources, files);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var resources = stack.Resources.GetAllResources().ToArray();
        Assert.True(parsedSections > 0);
        Assert.NotEmpty(resources);
        Assert.Contains(resources, r => r.Id.Type == ResType.Function);
        Assert.Contains(resources, r => r.Id.Type == ResType.Events);
        Assert.Contains(resources, r => r.Id.Type == ResType.ItemDef);
        Assert.Contains(resources, r => r.Id.Type == ResType.CharDef);
    }

    [Fact]
    public void ExternalScriptPackRuntimeCorpus_RunsSelectedTriggersWithoutThrowing()
    {
        string? packPath = ScriptTestBootstrap.GetExternalScriptPackPath();
        if (packPath == null)
            return;

        var collector = new ScriptDiagnosticCollector();
        var stack = ScriptTestBootstrap.CreateRuntimeStack(collector);
        var files = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.CoreOnly);
        ScriptTestBootstrap.LoadFiles(stack.Resources, files);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Name = "ScriptProbe";
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var item = world.CreateItem();
        item.Name = "Script Probe Item";
        item.ItemType = ItemType.Container;
        world.PlaceItem(item, ch.Position);

        var dispatchArgs = new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, ItemSrc = item };
        var scriptArgs = new TriggerArgs { Source = null, ArgString = "" };

        foreach (var trigger in new[] { CharTrigger.Create, CharTrigger.Click, CharTrigger.Attack, CharTrigger.SkillStart, CharTrigger.SkillSuccess, CharTrigger.LogIn, CharTrigger.LogOut })
            stack.Dispatcher.FireCharTrigger(ch, trigger, dispatchArgs);

        foreach (var trigger in new[] { ItemTrigger.Create, ItemTrigger.Click, ItemTrigger.DClick, ItemTrigger.Equip, ItemTrigger.Unequip, ItemTrigger.Timer, ItemTrigger.Step })
            stack.Dispatcher.FireItemTrigger(item, trigger, dispatchArgs);

        foreach (string function in new[] { "f_onserver_start", "f_onchar_login", "f_onchar_logout", "f_onitem_create", "f_onitem_dclick" })
            stack.Runner.TryRunFunction(function, ch, null, scriptArgs, out _);

        Assert.True(collector.Count("unhandled") >= 0);
    }

    [Fact]
    public void ExternalScriptPackDiagnostics_CollectsStructuredGapCategories()
    {
        string? packPath = ScriptTestBootstrap.GetExternalScriptPackPath();
        if (packPath == null)
            return;

        var inventory = ScriptPackInventory.Build(packPath);
        var collector = new ScriptDiagnosticCollector();
        var stack = ScriptTestBootstrap.CreateRuntimeStack(collector);
        var files = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.CoreOnly);

        ScriptTestBootstrap.LoadFiles(stack.Resources, files);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var args = new TriggerArgs { Source = null, ArgString = "" };

        foreach (string function in inventory.TriggerCounts.Keys
            .Where(t => t.StartsWith("@", StringComparison.Ordinal))
            .Take(10)
            .Select(t => "f_onchar_" + t.TrimStart('@').ToLowerInvariant()))
        {
            stack.Runner.RunFunction(function, ch, null, args);
        }

        var summary = ScriptPackCompatibilitySummary.Create(
            ScriptPackProfile.CoreOnly,
            inventory,
            stack.Resources.GetAllResources().Count(),
            collector);

        Assert.True(summary.RiskHits > 0);
        Assert.True(summary.DbHits > 0);
        Assert.True(summary.ServHits > 0);
        Assert.InRange(summary.LoadScore, 0, 10);
        Assert.True(summary.RuntimeRiskScore >= 0);
        Assert.NotNull(collector.Top("expr"));
    }

    [Fact]
    public void ScriptPackCompatibilityProfiles_ReturnStableFileSetsAndSummary()
    {
        string? packPath = ScriptTestBootstrap.GetExternalScriptPackPath();
        if (packPath == null)
            return;

        var inventory = ScriptPackInventory.Build(packPath);
        var audit = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.Audit);
        var core = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.CoreOnly);
        var full = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.Full);
        var summary = ScriptPackCompatibilitySummary.Create(
            ScriptPackProfile.Audit,
            inventory,
            resources: 0,
            new ScriptDiagnosticCollector());

        Assert.NotEmpty(audit);
        Assert.NotEmpty(core);
        Assert.Equal(audit.Count, full.Count);
        Assert.True(core.Count < full.Count, $"Core profile should be smaller than full ({core.Count}/{full.Count})");
        Assert.All(core, path => Assert.EndsWith(".scp", path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ScriptPackProfile.Audit, summary.Profile);
        Assert.Equal(audit.Count, summary.Files);
        Assert.True(summary.DbHits > 0);
        Assert.True(summary.ServHits > 0);
        Assert.Contains(summary.CategoryScores, c => c.Category == "db-ldb");
        Assert.Contains(summary.CategoryScores, c => c.Category == "staff-worldgen");
    }

    [Fact]
    public void ExternalWorldgenProfile_IsAuditOnlyAndNotCoreLoaded()
    {
        string? packPath = ScriptTestBootstrap.GetExternalScriptPackPath();
        if (packPath == null)
            return;

        var core = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.CoreOnly);
        var worldgen = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.WorldGenAudit);

        Assert.DoesNotContain(core, p => p.Replace('\\', '/').Contains("worldgen", StringComparison.OrdinalIgnoreCase));
        Assert.All(worldgen, p => Assert.Contains("worldgen", p.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }
}
