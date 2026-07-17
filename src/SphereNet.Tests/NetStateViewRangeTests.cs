using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// C8 (config contract): the default map view range for a new connection now comes
/// from config (sphere.ini MapViewSize) instead of a hardcoded 18. A client may still
/// request its own via 0xC8.
/// </summary>
public sealed class NetStateViewRangeTests
{
    [Fact]
    public void NewConnection_UsesConfigDefaultViewRange()
    {
        byte original = NetState.DefaultViewRange;
        try
        {
            NetState.DefaultViewRange = 12;
            var state = new NetState(NullLogger<NetState>.Instance);
            Assert.Equal((byte)12, state.ViewRange);
        }
        finally
        {
            NetState.DefaultViewRange = original; // restore global default for other tests
        }
    }
}
