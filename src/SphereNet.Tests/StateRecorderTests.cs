using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Server.Recording;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// E1 (perf review): StateRecorder must not materialize the character roster on every
/// server tick — only when its move-scan (2s) or snapshot (15s) interval is actually
/// due. The caller now passes a provider instead of a pre-built collection, so idle
/// ticks allocate nothing (previously a full ~52K-object array copy ran ~10x/sec).
/// </summary>
public sealed class StateRecorderTests
{
    [Fact]
    public void Tick_InvokesRosterProviderOnlyWhenScanIsDue()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_srec_{Guid.NewGuid():N}.db");
        var rec = new StateRecorder(tmp, NullLogger.Instance, playersOnly: true,
            moveScanMs: 2000, snapshotMs: 15_000);
        rec.Initialize();
        try
        {
            int providerCalls = 0;
            IEnumerable<Character> Provider()
            {
                providerCalls++;
                return Array.Empty<Character>();
            }

            rec.Tick(1000, Provider); // < 2000ms: neither interval due
            Assert.Equal(0, providerCalls);

            rec.Tick(2000, Provider); // move-scan due → roster materialized once
            Assert.Equal(1, providerCalls);

            rec.Tick(2500, Provider); // nothing due again
            Assert.Equal(1, providerCalls);

            rec.Tick(4000, Provider); // move-scan due again
            Assert.Equal(2, providerCalls);
        }
        finally
        {
            rec.Dispose();
            try { File.Delete(tmp); } catch { }
        }
    }
}
