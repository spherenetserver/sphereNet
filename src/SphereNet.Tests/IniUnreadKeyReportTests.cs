using System;
using System.IO;
using System.Linq;
using SphereNet.Core.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The ini says which of its lines nobody read (port plan İŞ-65 / PLAN-301).
///
/// PLAN-301 asks for the shipped ini to be compared with what the engine actually
/// supports, and - the part that needed building - for an unknown key's behaviour
/// to be VISIBLE.
///
/// It was not. A key this engine does not support and a key the operator
/// misspelled look identical from outside: the line is in the file, the server
/// starts, and nothing happens. The second kind is the expensive one, because the
/// setting looks present. İŞ-47 classified the keys the file documents; this is
/// about the ones it does not - a typo, or a setting carried over from a Source-X
/// build that has it.
///
/// Every read goes through IniParser.GetValue, so marking there answers the
/// question without a second list to maintain: a list would drift from the reader,
/// and then the report would be wrong in the direction that reassures.
/// </summary>
public sealed class IniUnreadKeyReportTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _path;

    public IniUnreadKeyReportTests(ITestOutputHelper output)
    {
        _out = output;
        _path = Path.Combine(Path.GetTempPath(), $"sphnet_ini_{Guid.NewGuid():N}.ini");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private IniParser Parse(string text)
    {
        File.WriteAllText(_path, text);
        var ini = new IniParser();
        ini.Load(_path);
        return ini;
    }

    private static string[] Keys(IniParser ini) =>
        ini.UnreadKeys().Select(k => k.Split('|')[^1]).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    [Fact]
    public void AKeyNobodyAsksForIsReported()
    {
        var ini = Parse("[SPHERE]\nSERVNAME=Test\nWHATEVERTHISIS=1\n");
        ini.GetValue("SPHERE", "SERVNAME");

        string[] unread = Keys(ini);
        _out.WriteLine("unread: " + string.Join(", ", unread));

        Assert.Equal(["WHATEVERTHISIS"], unread);
    }

    [Fact]
    public void AKeyThatWasReadIsNotReportedEvenWhenItIsEmpty()
    {
        var ini = Parse("[SPHERE]\nSERVNAME=\n");
        ini.GetValue("SPHERE", "SERVNAME");

        // Read, not "read and liked". A key whose value is blank was still consulted,
        // and reporting it would train the operator to ignore the list.
        Assert.Empty(Keys(ini));
    }

    [Fact]
    public void AskingForAKeyTheFileDoesNotHaveDoesNotInventOne()
    {
        var ini = Parse("[SPHERE]\nSERVNAME=Test\n");
        ini.GetValue("SPHERE", "SERVNAME");
        ini.GetValue("SPHERE", "NOTINTHEFILE");

        // The report is about lines the FILE carries. A default the engine took for a
        // key nobody wrote is not the operator's problem.
        Assert.Empty(Keys(ini));
    }

    [Fact]
    public void TheMatchIsCaseInsensitiveTheWayTheParserIs()
    {
        var ini = Parse("[SPHERE]\nServName=Test\n");
        ini.GetValue("SPHERE", "SERVNAME");

        // Key lookup ignores case, so the report has to as well - otherwise every
        // key whose spelling differs from the reader's would be listed as unread and
        // the whole thing would be noise.
        Assert.Empty(Keys(ini));
    }

    [Fact]
    public void TheRealConfigLeavesOnlyKeysItDocumentsAsUnsupported()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        string? repoIni = dir == null ? null : Path.Combine(dir.FullName, "config", "sphere.ini");
        if (Gate.MissingValue(_out, "config/sphere.ini", repoIni)) return;
        if (Gate.Missing(_out, "config/sphere.ini", !File.Exists(repoIni))) return;

        var ini = new IniParser();
        ini.Load(repoIni);
        new SphereConfig().LoadFromIni(ini);

        // The Host and the Panel read the same file through their own parsers, so
        // their keys look unread from the game engine's side. Program.cs credits them
        // for exactly this reason; the test models the same startup, or it would hold
        // the report to a claim the server never makes.
        foreach (string owned in new[]
                 {
                     "AppUpdateRepo", "AppUpdateRepoDir", "AppUpdateChannel",
                     "AppUpdateRuntime", "AppUpdateToken", "AppUpdateCheckMinutes",
                     "HostShutdownQuietMs", "HostShutdownTimeoutMs",
                     "HostAutoStart", "HostRestartOnCrash",
                     "AdminPanelAutoFill", "AdminPanelAllowedHosts",
                     "PublicPaperdoll", "PublicPaperdollOrigins",
                     "ScriptPackRepo", "ScriptPackBranch",
                 })
            ini.GetValue("SPHERE", owned);

        string[] unread = Keys(ini);
        _out.WriteLine($"{unread.Length} unread key(s): {string.Join(", ", unread)}");

        // The end-to-end check: run the real config through the real reader and see
        // what is left. Every survivor should be one the file itself marks as
        // unsupported - docs/56T and the ini classification cover why - so a NEW name
        // appearing here is either a typo or a setting that was added to the file and
        // never wired up.
        string text = File.ReadAllText(repoIni);
        var undocumented = unread
            .Where(k => !text.Contains("[UYGULANMADI]", StringComparison.Ordinal) ||
                        !IsMarkedUnsupported(text, k))
            .ToArray();
        foreach (string k in undocumented) _out.WriteLine("  undocumented: " + k);

        Assert.Empty(undocumented);
    }

    /// <summary>Whether the comment block above a key marks it unsupported.</summary>
    private static bool IsMarkedUnsupported(string iniText, string key)
    {
        var lines = iniText.Replace("\r\n", "\n").Split('\n');
        var block = new System.Collections.Generic.List<string>();
        foreach (string raw in lines)
        {
            string t = raw.Trim();
            if (t.StartsWith("//", StringComparison.Ordinal)) { block.Add(t); continue; }
            if (t.Length == 0) { block.Clear(); continue; }
            int eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return block.Any(c => c.Contains("[UYGULANMADI]", StringComparison.Ordinal));
            block.Clear();
        }
        return false;
    }
}
