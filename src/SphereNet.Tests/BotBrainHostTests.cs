using SphereNet.BotBrain;
using SphereNet.Game.PlayerBots;

namespace SphereNet.Tests;

public sealed class BotBrainHostTests
{
    private sealed class Brain : IBotBrain, IDisposable
    {
        public int Disposals;
        public bool TryThink(BotThought thought) => true;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    [Fact]
    public async Task ShutdownDuringLoadNeverPublishesTheLateBrain()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var unpublished = new ManualResetEventSlim();
        var brain = new Brain(); int publications = 0;
        var host = new BotBrainHost(() => { entered.Set(); release.Wait(); return brain; },
            b => { if (b != null) Interlocked.Increment(ref publications); else unpublished.Set(); });
        Assert.True(entered.Wait(5000));
        var stop = Task.Run(host.Dispose);
        try
        {
            Assert.True(unpublished.Wait(5000));
            Assert.False(stop.IsCompleted);
        }
        finally { release.Set(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, publications);
        Assert.Equal(1, brain.Disposals);
        host.Dispose(); Assert.Equal(1, brain.Disposals);
    }

    [Fact]
    public void PublishedBrainIsDetachedBeforeDisposal()
    {
        using var ready = new ManualResetEventSlim();
        var brain = new Brain(); IBotBrain? current = null;
        using var host = new BotBrainHost(() => brain, b => { current = b; if (b != null) ready.Set(); });
        Assert.True(ready.Wait(5000));
        host.Dispose();
        Assert.Null(current); Assert.Equal(1, brain.Disposals);
    }
}
