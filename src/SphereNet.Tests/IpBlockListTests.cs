using SphereNet.Core.Security;

namespace SphereNet.Tests;

/// <summary>
/// Timed IP blocks (Source-X BLOCKIP decay): a duration-limited block lifts
/// itself once the clock passes its expiry; permanent blocks (duration 0) never do.
/// </summary>
public class IpBlockListTests
{
    private static (IPBlockList List, long[] Now) MakeList()
    {
        long[] now = [1_000_000];
        var list = new IPBlockList { NowMsProvider = () => now[0] };
        return (list, now);
    }

    [Fact]
    public void PermanentBlock_NeverExpires()
    {
        var (list, now) = MakeList();
        Assert.True(list.Add("1.2.3.4"));      // no duration = permanent
        Assert.True(list.IsBlocked("1.2.3.4"));

        now[0] += 10_000_000;                  // far into the future
        Assert.True(list.IsBlocked("1.2.3.4")); // still blocked
    }

    [Fact]
    public void TimedBlock_ExpiresAfterDuration()
    {
        var (list, now) = MakeList();
        list.Add("5.6.7.8", durationSeconds: 30);
        Assert.True(list.IsBlocked("5.6.7.8"));

        now[0] += 29_000;                       // 29s later — still inside the window
        Assert.True(list.IsBlocked("5.6.7.8"));

        now[0] += 2_000;                        // 31s total — past expiry
        Assert.False(list.IsBlocked("5.6.7.8")); // lifted
        Assert.DoesNotContain("5.6.7.8", list.GetAll());
    }

    [Fact]
    public void Reblock_RefreshesExpiry()
    {
        var (list, now) = MakeList();
        list.Add("9.9.9.9", durationSeconds: 10);
        now[0] += 9_000;
        list.Add("9.9.9.9", durationSeconds: 10); // refresh the window
        now[0] += 5_000;                          // 14s from first block, 5s from refresh
        Assert.True(list.IsBlocked("9.9.9.9"));
    }

    [Fact]
    public void Remove_LiftsImmediately()
    {
        var (list, _) = MakeList();
        list.Add("2.2.2.2", durationSeconds: 999);
        Assert.True(list.Remove("2.2.2.2"));
        Assert.False(list.IsBlocked("2.2.2.2"));
    }
}
