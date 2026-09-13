using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The no-data state, made visible (port plan İŞ-49 / PLAN-003).
///
/// A good part of this suite measures real data: the live script pack, the 56T
/// save, the .mul tables, the Source-X reference tree. None of it can be
/// committed, so those tests return early when it is absent - and xUnit 2 has no
/// runtime skip, so they report as PASSED. A run on a machine with no data is
/// indistinguishable from a run that measured everything, and "3636 green" then
/// means less than it appears to.
///
/// This does not invent a skip status. It records every gate the run evaluated -
/// which test, which resource, present or not - and writes the tally next to the
/// TRX, so the number of tests that did not measure anything is a fact the run
/// reports rather than a fact the reader has to guess.
///
/// Usage keeps the original shape of the check:
///
///     if (Gate.Missing(_out, "live script pack", !Directory.Exists(pack))) return;
///
/// The report is rewritten on every call rather than at the end of the run: xUnit
/// 2 has no assembly teardown, and a report that only appears on a clean exit is
/// missing exactly when a run dies partway.
/// </summary>
public static class Gate
{
    public sealed record Observation(string Test, string Resource, bool Present, string File);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Observation> Seen = new(StringComparer.Ordinal);

    /// <summary>True when the data is absent, so the caller returns. Records the
    /// decision either way - a gate that keeps passing is as much a fact as one
    /// that keeps failing.</summary>
    public static bool Missing(
        ITestOutputHelper? output,
        string resource,
        bool missing,
        [CallerMemberName] string test = "",
        [CallerFilePath] string file = "")
    {
        Record(test, resource, !missing, file);
        if (missing)
            output?.WriteLine($"NO-DATA: {resource} - this test measured nothing");
        return missing;
    }

    /// <summary>The same gate for a value that is null when the data is absent.
    /// Separate from the boolean form so the compiler keeps its null-state
    /// analysis: without [NotNullWhen] every gated test would need a ! after it.</summary>
    public static bool MissingValue<T>(
        ITestOutputHelper? output,
        string resource,
        [NotNullWhen(false)] T? value,
        [CallerMemberName] string test = "",
        [CallerFilePath] string file = "") where T : class
    {
        Record(test, resource, value != null, file);
        if (value == null)
            output?.WriteLine($"NO-DATA: {resource} - this test measured nothing");
        return value == null;
    }

    /// <summary>The inverse, for call sites that read better as a positive.</summary>
    public static bool Present(
        ITestOutputHelper? output,
        string resource,
        bool present,
        [CallerMemberName] string test = "",
        [CallerFilePath] string file = "")
        => !Missing(output, resource, !present, test, file);

    private static void Record(string test, string resource, bool present, string file)
    {
        string shortFile = Path.GetFileNameWithoutExtension(file);
        string key = $"{shortFile}.{test}|{resource}";
        lock (Sync)
        {
            // A theory hits the same gate once per case; keep the pessimistic
            // answer so a partially-available resource cannot read as present.
            if (Seen.TryGetValue(key, out var prior) && !prior.Present)
                return;
            Seen[key] = new Observation($"{shortFile}.{test}", resource, present, shortFile);
            WriteReport();
        }
    }

    public static IReadOnlyCollection<Observation> Observations
    {
        get { lock (Sync) return Seen.Values.ToArray(); }
    }

    /// <summary>Where the report goes. `dotnet test` drops its TRX in the test
    /// project's TestResults, so the tally lands beside it.</summary>
    public static string? ReportDirectory()
    {
        string? forced = Environment.GetEnvironmentVariable("SPHERENET_TEST_REPORT_DIR");
        if (!string.IsNullOrWhiteSpace(forced)) return forced;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SphereNet.Tests.csproj")))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, "TestResults");
    }

    private static void WriteReport()
    {
        string? dir = ReportDirectory();
        if (dir == null) return;

        try
        {
            Directory.CreateDirectory(dir);
            var rows = Seen.Values.OrderBy(o => o.Test, StringComparer.Ordinal)
                                  .ThenBy(o => o.Resource, StringComparer.Ordinal)
                                  .ToArray();
            int missing = rows.Count(o => !o.Present);

            var sb = new StringBuilder();
            sb.Append("# Data gates\n\n")
              .Append(CultureInfo.InvariantCulture, $"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n\n")
              .Append(CultureInfo.InvariantCulture,
                      $"**{rows.Length} gate(s) evaluated, {missing} found no data.**\n\n")
              .Append("A gate with no data means the test ran, measured nothing and still\n")
              .Append("reported as passing. See docs/VERI_KAPILARI_TR.md.\n\n")
              .Append("| Test | Resource | Data |\n|---|---|---|\n");

            foreach (var o in rows)
                sb.Append(CultureInfo.InvariantCulture,
                          $"| {o.Test} | {o.Resource} | {(o.Present ? "present" : "**MISSING**")} |\n");

            File.WriteAllText(Path.Combine(dir, "data-gates.md"), sb.ToString());

            var csv = new StringBuilder("test,resource,present\n");
            foreach (var o in rows)
                csv.Append(CultureInfo.InvariantCulture,
                           $"{o.Test},{o.Resource.Replace(',', ';')},{(o.Present ? 1 : 0)}\n");
            File.WriteAllText(Path.Combine(dir, "data-gates.csv"), csv.ToString());
        }
        catch (IOException)
        {
            // The report is a convenience, never a reason to fail a run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
