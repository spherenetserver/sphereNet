using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every deadline an object carries is written through exactly one door.
///
/// This is the cheap half of moving timers onto a single time-sorted due list, the
/// way upstream runs them (CWorldTicker's `_vTimedObjsTimeouts`): there, a deadline
/// has to be REGISTERED when it changes, and the object is removed from the list
/// when it fires. A field that a dozen call sites assign directly cannot make that
/// move safely — the one assignment that forgets to register produces a timer that
/// never fires, in a shard where nothing reports it.
///
/// So the invariant is pinned now, while it is cheap, rather than discovered later
/// as a bug report about a door that never closed:
///
///   * `Timeout`  — mutated only by ObjBase.SetTimeout (which already registers
///                  world-level timers for off-ground and CAN=O_NOSLEEP items).
///   * `DecayTime` — mutated only inside Item, through AssignDecay; everyone else
///                  calls SetDecayTime / SetDecayAt / ClearDecay.
///
/// Tests are scanned too. A test that pokes the field directly would keep passing
/// while the engine path it stands for had stopped registering anything.
/// </summary>
public sealed class DeadlineWriteGateGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public DeadlineWriteGateGuardrailTests(ITestOutputHelper output) => _out = output;

    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir;
    }

    /// <summary>The line with its trailing comment removed: a sentence ABOUT the
    /// forbidden shape is not the forbidden shape, and this file is full of them.</summary>
    private static string Code(string line)
    {
        int i = line.IndexOf("//", StringComparison.Ordinal);
        return i < 0 ? line : line[..i];
    }

    private static IEnumerable<string> SourceFiles(DirectoryInfo root) =>
        Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                             !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void TheDecayDeadlineIsWrittenOnlyInsideItem()
    {
        var root = RepoRoot();
        if (Gate.MissingValue(_out, "engine source", root)) return;

        // Something.DecayTime = ... — the shape that bypasses the door. `==` is
        // excluded by requiring a single '='.
        var write = new Regex(@"\.DecayTime\s*=(?!=)");
        string owner = Path.Combine("SphereNet.Game", "Objects", "Items", "Item.cs");

        var offenders = new List<string>();
        foreach (string path in SourceFiles(root!))
        {
            if (path.EndsWith(owner, StringComparison.OrdinalIgnoreCase))
                continue;
            int line = 0;
            foreach (string text in File.ReadLines(path))
            {
                line++;
                if (write.IsMatch(Code(text)))
                    offenders.Add($"{Path.GetFileName(path)}:{line}  {text.Trim()}");
            }
        }

        foreach (string o in offenders) _out.WriteLine(o);
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} direct write(s) to DecayTime outside Item.cs. " +
            "Use SetDecayTime (a duration), SetDecayAt (an absolute deadline) or " +
            "ClearDecay, so the deadline can later be registered in one place.");
    }

    [Fact]
    public void TheTimerDeadlineIsWrittenOnlyInsideObjBase()
    {
        var root = RepoRoot();
        if (Gate.MissingValue(_out, "engine source", root)) return;

        var write = new Regex(@"(?<![\w.])_timeout\s*=(?!=)");
        string owner = Path.Combine("SphereNet.Game", "Objects", "ObjBase.cs");

        var offenders = new List<string>();
        foreach (string path in SourceFiles(root!))
        {
            if (path.EndsWith(owner, StringComparison.OrdinalIgnoreCase))
                continue;
            int line = 0;
            foreach (string text in File.ReadLines(path))
            {
                line++;
                if (write.IsMatch(Code(text)))
                    offenders.Add($"{Path.GetFileName(path)}:{line}  {text.Trim()}");
            }
        }

        foreach (string o in offenders) _out.WriteLine(o);
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} write(s) to the timer deadline outside ObjBase.cs. " +
            "SetTimeout is the door: it is what registers an armed timer with the " +
            "world-level pump for items no sector tick list covers.");
    }

    [Fact]
    public void TheDoorsAreStillThere()
    {
        var root = RepoRoot();
        if (Gate.MissingValue(_out, "engine source", root)) return;

        string item = File.ReadAllText(Path.Combine(root!.FullName, "src",
            "SphereNet.Game", "Objects", "Items", "Item.cs"));
        string objBase = File.ReadAllText(Path.Combine(root.FullName, "src",
            "SphereNet.Game", "Objects", "ObjBase.cs"));

        // A guardrail that only forbids writes would pass just as happily if someone
        // deleted the property and the door with it.
        Assert.Contains("public long DecayTime { get; private set; }", item);
        Assert.Contains("private void AssignDecay(long deadlineMs)", item);
        Assert.Contains("public void SetDecayAt(long deadlineMs)", item);
        Assert.Contains("public void ClearDecay()", item);
        Assert.Contains("public long Timeout => _timeout;", objBase);
        Assert.Contains("public void SetTimeout(long timeoutMs)", objBase);

        _out.WriteLine("both deadline doors present; writes funnel through them");
    }
}
