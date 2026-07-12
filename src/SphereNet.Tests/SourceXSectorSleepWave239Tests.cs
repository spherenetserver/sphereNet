using SphereNet.Core.Enums;
using SphereNet.Game.World.Sectors;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CSector::_CanSleep model: SECF_NoSleep/InstaSleep flags, the
/// clientless timeout measured from the last-client time, and the 8-neighbour
/// adjacency sweep that keeps the ring around an active sector awake.
/// </summary>
public sealed class SourceXSectorSleepWave239Tests
{
    private static Sector NewSector(int x = 5, int y = 5) => new(x, y, 0, 64);

    [Fact]
    public void CanSleep_ClientlessSectorSleepsOnlyAfterTimeout()
    {
        var s = NewSector();
        s.LastClientTimeMs = 0;

        Assert.False(s.CanSleep(nowMs: Sector.SleepDelayMs));          // exactly at delay — not yet
        Assert.True(s.CanSleep(nowMs: Sector.SleepDelayMs + 1));       // past delay — sleeps
    }

    [Fact]
    public void CanSleep_NoSleepFlagNeverSleeps_DisabledDelayNeverSleeps()
    {
        var s = NewSector();
        s.LastClientTimeMs = 0;

        s.Flags = SectorFlag.NoSleep;
        Assert.False(s.CanSleep(nowMs: Sector.SleepDelayMs * 100));

        s.Flags = SectorFlag.None;
        long saved = Sector.SleepDelayMs;
        try
        {
            Sector.SleepDelayMs = 0; // sleeping disabled globally
            Assert.False(s.CanSleep(nowMs: long.MaxValue / 2));
        }
        finally { Sector.SleepDelayMs = saved; }
    }

    [Fact]
    public void CanSleep_InstaSleepFlagSkipsTimeout()
    {
        var s = NewSector();
        s.LastClientTimeMs = 1_000_000; // very recent — timeout not elapsed

        s.Flags = SectorFlag.InstaSleep;
        Assert.True(s.CanSleep(nowMs: 1_000_001)); // sleeps immediately anyway
    }

    [Fact]
    public void CanSleep_PresentClientBlocksSleep_EvenWithInstaSleep()
    {
        var s = NewSector();
        AddOnlinePlayer(s);
        s.Flags = SectorFlag.InstaSleep;

        Assert.False(s.CanSleep(nowMs: Sector.SleepDelayMs + 1)); // ClientCount > 0
    }

    [Fact]
    public void CanSleep_AdjacentActiveSectorKeepsNeighbourAwake()
    {
        var quiet = NewSector(5, 5);
        var busy = NewSector(6, 5);
        quiet.LastClientTimeMs = 0;   // long clientless
        AddOnlinePlayer(busy);        // the neighbour has a client, so it can't sleep

        quiet.GetAdjacentSector = (sx, sy) => (sx == 6 && sy == 5) ? busy : null;

        // With the adjacency sweep the quiet sector stays awake next to the busy one...
        Assert.False(quiet.CanSleep(nowMs: Sector.SleepDelayMs + 1, checkAdjacents: true));
        // ...but the non-recursive check (its own timeout only) lets it sleep.
        Assert.True(quiet.CanSleep(nowMs: Sector.SleepDelayMs + 1, checkAdjacents: false));
    }

    // ClientCount counts characters that are both players and online.
    private static void AddOnlinePlayer(Sector s)
    {
        var ch = new SphereNet.Game.Objects.Characters.Character { IsPlayer = true, IsOnline = true };
        s.AddCharacter(ch);
    }
}
