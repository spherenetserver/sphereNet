using System;
using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Naming, timing and asking about delayed work (review 12W).
///
/// TIMERF STOP and ISTIMERF match a PATTERN against the whole queued command,
/// arguments included, with Str_Match semantics - a plain name is an exact match, '*'
/// and '?' are the wildcards (CTimedFunctionHandler.cpp:19/34, sstring.cpp:1750).
/// ISTIMERF answers with the FIRST match, and jobs are appended in the order they were
/// queued (:111). The delay in front of a command is an expression that the reader
/// consumes in full before the command begins (CObjBase.cpp:2777,
/// CExpression.cpp:794/1256).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DelayedCallParity12WTests
{
    private static Item NewObject(GameWorld world)
    {
        var item = world.CreateItem();
        item.BaseId = 0x1F03;
        world.PlaceItem(item, new Point3D(120, 120, 0, 0));
        return item;
    }

    private static Item WithThreeJobs(GameWorld world)
    {
        var obj = NewObject(world);
        Assert.True(obj.AddTimerF(60_000, "f_job", ""));
        Assert.True(obj.AddTimerF(60_000, "f_job_extra", ""));
        Assert.True(obj.AddTimerF(60_000, "f_job", "alpha"));
        return obj;
    }

    // ================================================================ 12W-1

    [Theory]
    [InlineData("f_job", 2)]          // an exact name stops only the job with no args
    [InlineData("f_job*", 0)]         // the wildcard takes the family
    [InlineData("f_jo?", 2)]          // '?' is one character: f_job only
    [InlineData("f_job alpha", 2)]    // arguments take part in the match
    [InlineData("f_nothing", 3)]
    public void StopMatchesTheWholeCommandRatherThanItsStart(string pattern, int remaining)
    {
        var world = TestHarness.CreateWorld();
        var obj = WithThreeJobs(world);

        obj.ClearTimerF(pattern);

        Assert.Equal(remaining, obj.TimerFEntries.Count);
    }

    [Fact]
    public void ClearWithNoPatternStillTakesEverything()
    {
        var world = TestHarness.CreateWorld();
        var obj = WithThreeJobs(world);

        obj.ClearTimerF(null);

        Assert.Empty(obj.TimerFEntries);
    }

    [Theory]
    [InlineData("f_job_extra*", true)]
    [InlineData("f_job_extra beta", true)]
    [InlineData("f_job", false)]           // a different job's name answers nothing
    [InlineData("f_job_extra", false)]     // ...and neither does this one, it has args
    public void AskingAboutAJobMatchesTheWholeCommandToo(string pattern, bool found)
    {
        var world = TestHarness.CreateWorld();
        var obj = NewObject(world);
        Assert.True(obj.AddTimerF(60_000, "f_job_extra", "beta"));

        long remaining = obj.GetTimerFRemaining(pattern, Environment.TickCount64);

        if (found)
            Assert.InRange(remaining, 55_000, 60_000);
        else
            Assert.Equal(0, remaining);
    }

    [Theory]
    [InlineData("f_job", "f_job", true)]
    [InlineData("f_job", "f_job_extra", false)]
    [InlineData("f_job*", "f_job_extra", true)]
    [InlineData("f_jo?", "f_job", true)]
    [InlineData("f_jo?", "f_jo", false)]
    [InlineData("F_JOB", "f_job", true)]                 // case independent
    [InlineData("f_job[0-9]", "f_job7", true)]
    [InlineData("f_job[0-9]", "f_jobx", false)]
    [InlineData("f_job[!0-9]", "f_jobx", true)]
    [InlineData("*alpha", "f_job alpha", true)]
    public void ThePatternLanguageIsTheReferenceOne(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, SpherePattern.Matches(pattern, text));
    }

    // ================================================================ 12W-3

    [Fact]
    public void AskingAboutTwoJobsOfTheSameNameAnswersWithTheFirstOne()
    {
        var world = TestHarness.CreateWorld();
        var obj = NewObject(world);
        Assert.True(obj.AddTimerF(60_000, "f_same", ""));
        Assert.True(obj.AddTimerF(10_000, "f_same", ""));

        long remaining = obj.GetTimerFRemaining("f_same", Environment.TickCount64);

        // The one queued first, not the one due soonest.
        Assert.InRange(remaining, 55_000, 60_000);
    }

    [Fact]
    public void AJobThatIsAlreadyDueAnswersZeroRatherThanTheNextOne()
    {
        var world = TestHarness.CreateWorld();
        var obj = NewObject(world);
        Assert.True(obj.AddTimerF(0, "f_same", ""));
        Assert.True(obj.AddTimerF(10_000, "f_same", ""));

        Assert.Equal(0, obj.GetTimerFRemaining("f_same", Environment.TickCount64));
    }

    // ================================================================ 12W-2

    [Theory]
    [InlineData("2, f_done", 2_000)]
    [InlineData("2 f_done", 2_000)]          // the space form, no comma
    [InlineData("1+1, f_done", 2_000)]
    [InlineData("2-1, f_done", 1_000)]
    [InlineData("2*3, f_done", 6_000)]       // multiplication used to schedule nothing
    [InlineData("(1+1), f_done", 2_000)]     // ...and so did a parenthesis
    [InlineData("1 + 1, f_done", 2_000)]     // spaces around the operator: still 2
    [InlineData("010, f_done", 16_000)]      // Sphere hex
    public void TheDelayIsAnExpressionAndTheCommandBeginsWhereItEnds(string line, long expectedMs)
    {
        var world = TestHarness.CreateWorld();
        var obj = NewObject(world);

        Assert.True(obj.TryExecuteCommand("TIMERF", line, new TestConsole()));

        var entry = Assert.Single(obj.TimerFEntries);
        Assert.Equal("f_done", entry.FunctionName);
        Assert.Equal("", entry.Args);
        long delay = entry.DueTickMs - Environment.TickCount64;
        Assert.InRange(delay, expectedMs - 1_000, expectedMs);
    }

    [Fact]
    public void AnArgumentAfterTheFunctionNameIsStillItsArgument()
    {
        var world = TestHarness.CreateWorld();
        var obj = NewObject(world);

        Assert.True(obj.TryExecuteCommand("TIMERF", "2*3, f_done 37", new TestConsole()));

        var entry = Assert.Single(obj.TimerFEntries);
        Assert.Equal("f_done", entry.FunctionName);
        Assert.Equal("37", entry.Args);
    }

    [Theory]
    [InlineData("notanumber, f_done")]
    [InlineData("-1, f_done")]
    [InlineData("5")]                        // a delay and nothing to run
    public void AnUnreadableOrEmptyLineSchedulesNothing(string line)
    {
        var world = TestHarness.CreateWorld();
        var obj = NewObject(world);

        obj.TryExecuteCommand("TIMERF", line, new TestConsole());

        Assert.Empty(obj.TimerFEntries);
    }

    [Theory]
    [InlineData("2*3", 6, 3)]
    [InlineData("(1+1)", 2, 5)]
    [InlineData("1 + 1", 2, 5)]
    [InlineData("2 f_done", 2, 1)]           // the expression ends before the name
    [InlineData("010", 16, 3)]
    [InlineData("7, rest", 7, 1)]
    public void TheExpressionReaderReportsWhereItStopped(string text, long value, int consumed)
    {
        Assert.True(ScriptNumber.TryEvaluatePrefix(text, out long parsed, out int used));
        Assert.Equal(value, parsed);
        Assert.Equal(consumed, used);
    }

    private sealed class TestConsole : Core.Interfaces.ITextConsole
    {
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) { }
    }
}
