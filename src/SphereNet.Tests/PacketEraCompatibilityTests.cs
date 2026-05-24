using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Network.Manager;
using SphereNet.Network.State;

namespace SphereNet.Tests;

public class PacketEraCompatibilityTests
{
    [Fact]
    public void NetState_UnknownClient_DefaultsToSphere56xPacketProfile()
    {
        var state = new NetState(NullLogger<NetState>.Instance);

        Assert.Equal(ClientEra.Sphere56x, state.ClientEra);
        Assert.False(state.IsClientPost6017);
        Assert.False(state.IsClientPost60142);
        Assert.False(state.IsClientPost7090);
        Assert.False(state.SupportsAosTooltip);
        Assert.False(state.SupportsBuffIcon);
    }

    [Fact]
    public void NetState_ModernProfile_AllowsModernFormatsUntilHandshakeArrives()
    {
        var state = new NetState(NullLogger<NetState>.Instance)
        {
            ClientEra = ClientEra.Modern
        };

        Assert.True(state.IsClientPost6017);
        Assert.True(state.IsClientPost7090);
        Assert.True(state.SupportsAosTooltip);
        Assert.True(state.SupportsBuffIcon);
    }

    [Fact]
    public void NetState_ExplicitVersion_OverridesSphere56xUnknownDefault()
    {
        var state = new NetState(NullLogger<NetState>.Instance)
        {
            ClientVersionNumber = 70_009_000
        };

        Assert.True(state.IsClientPost6017);
        Assert.True(state.IsClientPost7090);
        Assert.True(state.SupportsAosTooltip);
        Assert.True(state.SupportsBuffIcon);
    }

    [Fact]
    public void NetworkManager_DefaultClientEra_PropagatesToConnectionSlots()
    {
        var manager = new NetworkManager(2, NullLoggerFactory.Instance)
        {
            DefaultClientEra = ClientEra.Modern
        };

        var state = manager.GetState(0);

        Assert.NotNull(state);
        Assert.Equal(ClientEra.Modern, state!.ClientEra);
        Assert.True(state.IsClientPost6017);
    }
}
