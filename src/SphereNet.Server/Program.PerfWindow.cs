using System.Text.Json;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Diagnostics;

namespace SphereNet.Server;

/// <summary>
/// PERF MARK / PERF REPORT / PERF STOP: a measurement window for an external load
/// runner (src/SphereNet.LoadTest). MARK opens a fresh window, REPORT logs one
/// "[perf_window] {json}" line describing exactly the ticks, loop iterations, NPC
/// budget, GC work and saves since MARK, and STOP closes it. The verbs come in on the
/// console queue, which the main loop drains, so the window is only ever touched by
/// the thread that records into it.
/// </summary>
public static partial class Program
{
    private static PerfMeasurementWindow? _perfWindow;

    private static readonly JsonSerializerOptions s_perfJson = new() { WriteIndented = false };

    /// <summary>The worker count the multicore tick is actually using.</summary>
    private static int EffectiveMulticoreWorkerCount =>
        _config.MulticoreWorkerCount > 0
            ? _config.MulticoreWorkerCount
            : Math.Max(1, Environment.ProcessorCount - 1);

    private static void HandlePerfCommand(string sub, Action<string> output)
    {
        switch (sub.Trim().ToUpperInvariant())
        {
            case "MARK":
                _perfWindow = new PerfMeasurementWindow(_tickCounter);
                _log.LogInformation("[perf_window] mark tick={Tick} workers={Workers}",
                    _tickCounter, EffectiveMulticoreWorkerCount);
                output("Perf window opened.");
                break;

            case "REPORT":
            {
                if (_perfWindow == null)
                {
                    _log.LogInformation("[perf_window] {Json}", "{\"error\":\"no window; send PERF MARK first\"}");
                    output("No perf window open (PERF MARK).");
                    break;
                }
                var (chars, items, _) = _world.GetStats();
                var report = _perfWindow.BuildReport(_tickCounter, EffectiveMulticoreWorkerCount,
                    _multicoreRuntimeEnabled, _clients.Values.Count(c => c.IsPlaying), chars, items);
                string json = JsonSerializer.Serialize(report, s_perfJson);
                _log.LogInformation("[perf_window] {Json}", json);
                output(json);
                break;
            }

            case "STOP":
                _perfWindow = null;
                output("Perf window closed.");
                break;

            default:
                output("Usage: PERF MARK | PERF REPORT | PERF STOP");
                break;
        }
    }
}
