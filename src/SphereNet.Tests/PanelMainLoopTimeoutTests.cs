using System.Reflection;

namespace SphereNet.Tests;

/// <summary>
/// A panel request that times out does not leave a fault nobody reads.
///
/// The main-loop bridge faults its completion on timeout, which is the signal to the
/// queued action that nobody is waiting any more - it checks IsCompleted and skips the
/// work. But a faulted Task nobody reads is rethrown by the finalizer thread as an
/// UnobservedTaskException, and that is a PROCESS-level fault: a panel stats request that
/// timed out during a long boot arrived in the crash log as
///
///     FATAL (TaskScheduler.UnobservedTaskException)
///      ---> TimeoutException: Main-loop operation 'stats snapshot' timed out
///
/// with no caller left to name. The timeout itself is a fair thing to happen while a
/// world of 180,000 items is loading; being reported as a fatal fault is not.
///
/// This drives the real bridge, times it out, and then forces the finalizer to run.
/// </summary>
public sealed class PanelMainLoopTimeoutTests
{
    private static readonly Type P = typeof(SphereNet.Server.Program);

    private static FieldInfo Field(string name) =>
        P.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void ATimedOutRequestRaisesNoUnobservedTaskException()
    {
        object? runningBefore = Field("_running").GetValue(null);
        object? threadBefore = Field("_mainLoopThreadId").GetValue(null);

        var unobserved = new List<Exception>();
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            unobserved.Add(e.Exception);
            e.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            // A main loop that exists but is not this thread and never drains its queue:
            // the request has nobody to run it and must time out.
            Field("_running").SetValue(null, true);
            Field("_mainLoopThreadId").SetValue(null, Environment.CurrentManagedThreadId + 1000);

            var invoke = P.GetMethod("InvokePanelOnMainLoop",
                    BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(int));

            var ex = Record.Exception(() =>
                invoke.Invoke(null, [(Func<int>)(() => 7), "probe"]));

            // The caller is told, which is the behaviour the panel relies on.
            Assert.NotNull(ex);
            Assert.IsType<TimeoutException>(ex!.InnerException ?? ex);

            // The queued action still holds the completion, so it cannot be collected
            // while it sits there - and an uncollected Task is never finalized, which
            // would make this test pass whatever the code does. Drain the queue first so
            // the abandoned completion is genuinely unreachable.
            var queue = Field("_mainLoopActions").GetValue(null)!;
            var clear = queue.GetType().GetMethod("Clear")!;
            clear.Invoke(queue, null);

            // And the abandoned completion must not come back as a process fault.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
            Field("_running").SetValue(null, runningBefore);
            Field("_mainLoopThreadId").SetValue(null, threadBefore);
        }
    }
}
