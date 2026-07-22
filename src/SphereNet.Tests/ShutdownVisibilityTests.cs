using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 14 / M4 — the main game loop's shutdown flag is written from several threads
/// (Console.CancelKeyPress signal handler, telnet accept thread, WinForms UI
/// thread, IPC/panel callback) and read every iteration of the tight
/// <c>while (_running)</c> loop on the main thread. A non-volatile read can be
/// hoisted/cached by the JIT, so a shutdown request from another thread may not be
/// observed in bounded time. This locks in the volatile barrier so any regression
/// (dropping the modifier) fails the build's test gate rather than silently
/// re-introducing a loop that ignores CancelKeyPress / telnet / panel / delayed
/// SERV.SHUTDOWN requests.
/// </summary>
public sealed class ShutdownVisibilityTests
{
    [Fact]
    public void MainLoopRunningFlag_IsVolatile_SoCrossThreadShutdownIsVisible()
    {
        // Fully qualify: SphereNet.Host also defines a top-level `Program`, which
        // would otherwise shadow this one on an unqualified reference.
        FieldInfo? field = typeof(SphereNet.Server.Program).GetField(
            "_running", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Contains(typeof(IsVolatile), field!.GetRequiredCustomModifiers());
    }
}
