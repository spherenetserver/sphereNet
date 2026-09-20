using System.Diagnostics;
using System.Text;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class WeatherDispatchCacheTests(ITestOutputHelper output)
{
    [Fact]
    public void MissingRetainedTriggerDoesNotReparseFileOrAllocatePerNpc()
    {
        WithScript("[CHARDEF 01]\nON=@Create\nRETURN 0\n", (path, runtime) =>
        {
            var link = runtime.Resources.GetResource(ResType.CharDef, 1)!;
            var npc = new Character();
            var larger = new StringBuilder("[CHARDEF 01]\nON=@EnvironChange\nTAG.UNLOADED=1\n");
            for (int i = 2; i < 202; i++)
                larger.Append($"[CHARDEF 0{i:X}]\nON=@Create\nRETURN 0\n");
            File.WriteAllText(path, larger.ToString());
            ScriptFile.ClearFileCache();
            // File edits are not live until resync. A retained section is authoritative.
            runtime.Runner.RunTriggerByName(link, "EnvironChange", npc, null, null);
            var timer = Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                runtime.Runner.RunTriggerByName(link, "EnvironChange", npc, null, null);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            timer.Stop();
            output.WriteLine($"1000 absent triggers: {timer.Elapsed.TotalMilliseconds:F3} ms, {allocated} bytes");
            Assert.False(npc.TryGetTag("UNLOADED", out _));
            Assert.True(allocated < 4096, $"Missing-trigger path allocated {allocated} bytes");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnretainedLinkReadsOnlyItsSectionAndCachesHitAndMiss(bool indexed)
    {
        WithScript("[CHARDEF 01]\nON=@Create\nTAG.FOUND=1\n[CHARDEF 02]\nON=@EnvironChange\nTAG.WRONG=1\n",
            (path, runtime) =>
        {
            var link = new ResourceLink(new ResourceId(ResType.CharDef, 1)) { ScriptFilePath = path, ScriptLineNumber = 1 };
            link.SetTriggerActive(1);
            var npc = new Character();
            if (indexed) runtime.Runner.RunTrigger(link, 1, "EnvironChange", npc, null, null);
            else runtime.Runner.RunTriggerByName(link, "EnvironChange", npc, null, null);
            Assert.NotNull(link.StoredKeys);
            Assert.False(npc.TryGetTag("WRONG", out _));
            File.Delete(path);
            ScriptFile.ClearFileCache();
            if (indexed) runtime.Runner.RunTrigger(link, 1, "Create", npc, null, null);
            else runtime.Runner.RunTriggerByName(link, "Create", npc, null, null);
            Assert.True(npc.TryGetTag("FOUND", out var value));
            Assert.Equal("1", value);
        });
    }

    [Fact]
    public void RescanningSameLinkRemovesOldBodiesAndTriggerBits()
    {
        WithScript("[CHARDEF 01]\nON=@EnvironChange\nRETURN 1\n", (path, runtime) =>
        {
            var link = runtime.Resources.GetResource(ResType.CharDef, 1)!;
            link.SetTriggerActive(1);
            File.WriteAllText(path, "[CHARDEF 01]\nON=@Create\nRETURN 0\n");
            using var file = new ScriptFile();
            Assert.True(file.Open(path));
            link.ScanSection(file.ReadNextSection()!, retainKeys: true);
            Assert.False(link.IsTriggerActive(1));
            Assert.False(link.TryGetTriggerBody("EnvironChange", out _));
            Assert.True(link.TryGetTriggerBody("Create", out _));
        });
    }

    [Fact]
    public void NpcEnvironmentGateTracksReloadAndPreservesSnapshotsAndNativeHandlers()
    {
        WithScript("[CHARDEF 01]\nNAME=probe\n", (path, runtime) =>
        {
            var npc = new Character { CharDefIndex = 1 };
            var dispatcher = runtime.Dispatcher;
            dispatcher.BuildUsedTriggerCache();
            Character.OnEnvironChange = dispatcher.FireEnvironChange;
            npc.UpdateEnvironment(20, 0, 1);
            npc.UpdateEnvironment(20, 2, 1); // skipped script dispatch still updates snapshot
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) dispatcher.FireEnvironChange(npc, 20);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            File.WriteAllText(path, "[CHARDEF 01]\nON=@EnvironChange\nTAG.SEEN=<ARGN1>\n");
            runtime.Resources.ResyncAll();
            dispatcher.BuildUsedTriggerCache();
            Assert.True(dispatcher.IsCharTriggerUsed(CharTrigger.EnvironChange));
            npc.UpdateEnvironment(20, 2, 1); // same snapshot must not fire after reload
            Assert.False(npc.TryGetTag("SEEN", out _));
            npc.UpdateEnvironment(21, 2, 1);
            Assert.True(npc.TryGetTag("SEEN", out var seen));
            Assert.Equal("21", seen);

            dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillGain", (_, _) => TriggerResult.Default);
            dispatcher.RegisterItemEvent("EVENTSITEM", "Click", (_, _) => TriggerResult.Default);
            File.WriteAllText(path, "[CHARDEF 01]\nNAME=probe\n");
            runtime.Resources.ResyncAll();
            dispatcher.BuildUsedTriggerCache();
            Assert.False(dispatcher.IsCharTriggerUsed(CharTrigger.EnvironChange));
            Assert.True(dispatcher.IsCharTriggerUsed(CharTrigger.SkillGain));
            Assert.True(dispatcher.IsItemTriggerUsed(ItemTrigger.Click));
            npc.UpdateEnvironment(22, 0, 1);
            Assert.True(npc.TryGetTag("SEEN", out seen));
            Assert.Equal("21", seen);
        });
    }

    private static void WithScript(string text, Action<string, ScriptRuntimeStack> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"weather_dispatch_{Guid.NewGuid():N}.scp");
        var previous = Character.OnEnvironChange;
        try
        {
            File.WriteAllText(path, text);
            var runtime = ScriptTestBootstrap.CreateRuntimeStack();
            runtime.Resources.LoadResourceFile(path);
            test(path, runtime);
        }
        finally
        {
            Character.OnEnvironChange = previous;
            File.Delete(path);
        }
    }
}
