using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
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
        Assert.False(state.SupportsStygianAbyss);
        Assert.False(state.SupportsHighSeas);
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

    [Fact]
    public void NetState_SA_Client_SupportsStygianAbyss()
    {
        var state = new NetState(NullLogger<NetState>.Instance)
        {
            ClientVersionNumber = 70_000_000 // 7.0.0.0
        };

        Assert.True(state.SupportsStygianAbyss);
        Assert.False(state.SupportsHighSeas);
        Assert.True(state.HasProtocolChanges(ProtocolChanges.StygianAbyss));
    }

    [Fact]
    public void NetState_HS_Client_SupportsHighSeas()
    {
        var state = new NetState(NullLogger<NetState>.Instance)
        {
            ClientVersionNumber = 70_009_000 // 7.0.9.0
        };

        Assert.True(state.SupportsStygianAbyss);
        Assert.True(state.SupportsHighSeas);
    }

    [Fact]
    public void NetState_PreSA_Client_NoStygianAbyss()
    {
        var state = new NetState(NullLogger<NetState>.Instance)
        {
            ClientVersionNumber = 60_014_002 // 6.0.14.2
        };

        Assert.False(state.SupportsStygianAbyss);
        Assert.False(state.SupportsHighSeas);
        Assert.True(state.IsClientPost60142);
    }

    [Fact]
    public void NetState_ProtocolChanges_AutoComputedOnVersionSet()
    {
        var state = new NetState(NullLogger<NetState>.Instance)
        {
            ClientVersionNumber = 70_061_000 // 7.0.61.0
        };

        Assert.Equal(ProtocolChanges.Version70610, state.ProtocolChanges);
        Assert.True(state.SupportsNewMobileIncoming);
        Assert.True(state.SupportsNewSecureTrading);
    }
}
