using Microsoft.Extensions.Logging;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

internal static class ScriptTestBootstrap
{
    public static string? GetExternalScriptPackPath()
    {
        string? configured = Environment.GetEnvironmentVariable("SPHERENET_SCRIPT_PACK");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        return null;
    }

    public static IReadOnlyList<string> GetScriptFiles(string rootPath, ScriptPackProfile profile)
    {
        var all = Directory.GetFiles(rootPath, "*.scp", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return profile switch
        {
            ScriptPackProfile.Audit => all,
            ScriptPackProfile.CoreOnly => all.Where(IsCoreScript).ToArray(),
            ScriptPackProfile.WorldGenAudit => all.Where(IsWorldGenScript).ToArray(),
            ScriptPackProfile.Full => all,
            _ => all,
        };
    }

    public static ScriptRuntimeStack CreateRuntimeStack(ScriptDiagnosticCollector? collector = null)
    {
        var loggerFactory = collector == null
            ? LoggerFactory.Create(_ => { })
            : LoggerFactory.Create(b => b.AddProvider(new CollectingLoggerProvider(collector)));

        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        var parser = new ExpressionParser
        {
            DebugUnresolved = collector != null,
            DiagnosticLogger = collector == null ? null : message => collector.AddUnresolved(message)
        };
        var interpreter = new ScriptInterpreter(parser, loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());

        interpreter.CallFunction = (name, target, source, args) =>
            runner.TryRunFunction(name, target, source, args, out var result)
                ? result
                : SphereNet.Core.Enums.TriggerResult.Default;
        interpreter.ResolveFunctionExpression = (name, rawArgs, target, source, args) =>
            runner.TryEvaluateFunction(name, rawArgs, target, source, args, out var value)
                ? value
                : null;

        var dispatcher = new TriggerDispatcher
        {
            Resources = resources,
            Runner = runner,
            ScriptDebug = collector != null,
        };

        return new ScriptRuntimeStack(loggerFactory, resources, parser, interpreter, runner, dispatcher);
    }

    public static void LoadFiles(ResourceHolder resources, IEnumerable<string> files)
    {
        foreach (string file in files)
            resources.LoadResourceFile(file);
    }

    public static void LoadDefinitions(ResourceHolder resources)
    {
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private static bool IsCoreScript(string path)
    {
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        if (IsWorldGenScript(normalized))
            return false;
        return normalized.Contains("/defnames/") ||
               normalized.Contains("/functions/") ||
               normalized.Contains("/events/") ||
               normalized.Contains("/itemdefs/") ||
               normalized.Contains("/chardefs/") ||
               normalized.EndsWith("/sphere_defs.scp", StringComparison.Ordinal) ||
               normalized.EndsWith("/sphere_msgs.scp", StringComparison.Ordinal) ||
               normalized.EndsWith("/sphere_skills.scp", StringComparison.Ordinal) ||
               normalized.EndsWith("/sphere_spells.scp", StringComparison.Ordinal) ||
               normalized.EndsWith("/sphere_types.scp", StringComparison.Ordinal);
    }

    private static bool IsWorldGenScript(string path)
    {
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/functions/worldgen/") ||
               normalized.Contains("/worldgen/") ||
               normalized.Contains("worldgen");
    }
}

internal enum ScriptPackProfile
{
    Audit,
    CoreOnly,
    WorldGenAudit,
    Full,
}

internal sealed record ScriptRuntimeStack(
    ILoggerFactory LoggerFactory,
    ResourceHolder Resources,
    ExpressionParser Parser,
    ScriptInterpreter Interpreter,
    TriggerRunner Runner,
    TriggerDispatcher Dispatcher);
