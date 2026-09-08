using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SphereNet.Game.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every trigger a real script pack hooks is one this engine actually fires
/// (port plan PLAN-206).
///
/// A pack states what it expects the engine to do by hooking names. A name nothing
/// fires is a silent no-op: no error, no log, the behaviour simply never happens, and
/// the shard finds out months later. The check is driven from the PACK side and from
/// the reference's own trigger table rather than from our enum - measuring our coverage
/// with our own enum only proves the enum agrees with itself.
///
/// The pack directories are optional, so this is CI-safe: with none of them present the
/// test asserts what it still can (that the dispatchable set is sane) and skips the rest.
/// </summary>
public sealed class ScriptPackTriggerCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        @"C:\56T\scripts",
        "oldSphere/scripts",
        "oldSphere/Scripts-X-main",
    ];

    /// <summary>Hooks a pack writes that the reference does not have either
    /// (checked against its own tables/triggers.tbl). They are dead script upstream
    /// too - a shard wrote them against a trigger that never existed - so firing them
    /// would be inventing behaviour, not porting it.</summary>
    private static readonly HashSet<string> NotInTheReference =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "TameAbort", "SpellStart", "PartyJoin",
            "npcmount", "NPCDisMount", "move", "skilluse", "statgain",
        };

    /// <summary>Triggers the reference HAS and this engine does not fire yet. A pack
    /// hooking one of these is asking for behaviour that silently never happens, so
    /// each entry is a recorded gap, not an excuse - and the test below fails when an
    /// entry stops being unserved, so implementing one forces its removal from here.
    /// </summary>
    private static readonly HashSet<string> KnownGaps =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ResourceFound",            // CWorldMap.cpp:159 - fires on the REGIONRESOURCE def
            "RegionResourceFound",      // CWorldMap.cpp:157 - fires on the character
            "RegionResourceGather",     // CCharSkill.cpp:1035 - fires on the gatherer
            "HitReactive",              // CCharFight.cpp:965 - reactive armour reflection
        };

    private static readonly Regex HookLine =
        new(@"^\s*ON\s*=\s*@(\w+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static IEnumerable<string> PackFiles()
    {
        foreach (string root in PackRoots)
        {
            string full = Path.IsPathRooted(root) ? root : Path.GetFullPath(root);
            if (!Directory.Exists(full)) continue;
            foreach (string f in Directory.EnumerateFiles(full, "*.scp", SearchOption.AllDirectories))
                yield return f;
        }
    }

    [Fact]
    public void TheDispatchableSetCoversTheFamiliesThatDoNotGoThroughAnEnum()
    {
        var names = TriggerDispatcher.DispatchableTriggerNames;

        // Plain char and item triggers.
        Assert.Contains("Create", names);
        Assert.Contains("DClick", names);
        // The cross-fired mirrors, which no CharTrigger/ItemTrigger call site names.
        Assert.Contains("itemDClick", names);
        Assert.Contains("charDeath", names);
        // [SKILL n] section stages - fired under the short name, not "SkillStart".
        Assert.Contains("PreStart", names);
        Assert.Contains("Stroke", names);
        // Region and [SPELL n] section stages.
        Assert.Contains("RegPeriodic", names);
        Assert.Contains("CliPeriodic", names);
        Assert.Contains("Effect", names);
    }

    [Fact]
    public void EveryHookARealPackWritesIsOneTheEngineFires()
    {
        var files = PackFiles().ToList();
        if (files.Count == 0)
        {
            outp.WriteLine("no script pack present - nothing to measure");
            return;
        }

        var names = TriggerDispatcher.DispatchableTriggerNames;
        var dead = new Dictionary<string, (int Uses, string FirstFile)>(StringComparer.OrdinalIgnoreCase);
        var stillMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int hookCount = 0;
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }

            foreach (Match m in HookLine.Matches(text))
            {
                string hook = m.Groups[1].Value;
                hookCount++;
                distinct.Add(hook);
                if (names.Contains(hook) || NotInTheReference.Contains(hook)) continue;
                if (KnownGaps.Contains(hook)) { stillMissing.Add(hook); continue; }

                var prev = dead.TryGetValue(hook, out var seen) ? seen : (0, Path.GetFileName(file));
                dead[hook] = (prev.Item1 + 1, prev.Item2);
            }
        }

        outp.WriteLine($"{files.Count} script files, {hookCount} hooks, {distinct.Count} distinct trigger names");
        foreach (var (hook, info) in dead.OrderByDescending(d => d.Value.Uses))
            outp.WriteLine($"  UNSERVED @{hook} x{info.Uses} (first in {info.FirstFile})");

        Assert.True(dead.Count == 0,
            "a script pack hooks trigger(s) nothing fires: " +
            string.Join(", ", dead.OrderByDescending(d => d.Value.Uses)
                                  .Select(d => $"@{d.Key} x{d.Value.Uses} ({d.Value.FirstFile})")));

        // A gap that is no longer a gap must leave the list, or the list stops meaning
        // anything. Only checked against packs that actually hook the name.
        var fixedNow = KnownGaps.Where(g => distinct.Contains(g) && !stillMissing.Contains(g)).ToList();
        Assert.True(fixedNow.Count == 0,
            "these are served now and must be removed from KnownGaps: " + string.Join(", ", fixedNow));
    }
}
