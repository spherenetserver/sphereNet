using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
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

    // --- Create-character packet length boundaries ---
    // 0x00 is ALWAYS 104 bytes; the 106-byte form is a distinct opcode (0xF8).
    // Regression guard: a modern client must not cause 0x00 to be read as 106.
    [Theory]
    [InlineData(0u)]            // version unknown
    [InlineData(60_001_007u)]   // 6.0.1.7
    [InlineData(70_009_000u)]   // 7.0.9 (HS)
    [InlineData(70_018_000u)]   // 7.0.18
    [InlineData(70_020_000u)]   // 7.0.20
    [InlineData(70_029_000u)]   // 7.0.29
    [InlineData(70_030_000u)]   // 7.0.30
    [InlineData(70_061_000u)]   // 7.0.61
    public void CreateCharacter_0x00_IsAlways104Bytes(uint version)
    {
        var state = new NetState(NullLogger<NetState>.Instance) { ClientVersionNumber = version };
        Assert.Equal(104, PacketDefinitions.GetPacketLength(0x00, state));
    }

    [Theory]
    [InlineData(70_016_000u)]   // 7.0.16 (NewCharacterCreation)
    [InlineData(70_018_000u)]   // 7.0.18
    [InlineData(70_030_000u)]   // 7.0.30
    public void CreateCharacterHS_0xF8_IsAlways106Bytes(uint version)
    {
        var state = new NetState(NullLogger<NetState>.Instance) { ClientVersionNumber = version };
        Assert.Equal(106, PacketDefinitions.GetPacketLength(0xF8, state));
    }

    [Fact]
    public void CreateCharacter_ModernEra_StillReads0x00As104()
    {
        var state = new NetState(NullLogger<NetState>.Instance) { ClientEra = ClientEra.Modern };
        Assert.Equal(104, PacketDefinitions.GetPacketLength(0x00, state));
    }

    // --- Drop-item (0x08) grid-index boundary ---
    // 14 bytes pre-6.0.1.7, 15 bytes 6.0.1.7+ (extra grid-index byte).
    [Theory]
    [InlineData(0u, 14)]              // version unknown → Sphere56x baseline
    [InlineData(60_000_000u, 14)]     // 6.0.0 (pre grid index)
    [InlineData(60_001_007u, 15)]     // 6.0.1.7 (grid index added)
    [InlineData(70_009_000u, 15)]     // 7.0.9
    public void DropItem_0x08_GridIndexLengthByEra(uint version, int expected)
    {
        var state = new NetState(NullLogger<NetState>.Instance) { ClientVersionNumber = version };
        Assert.Equal(expected, PacketDefinitions.GetPacketLength(0x08, state));
    }

    [Fact]
    public void DropItem_0x08_ModernEra_Uses15Bytes()
    {
        var state = new NetState(NullLogger<NetState>.Instance) { ClientEra = ClientEra.Modern };
        Assert.Equal(15, PacketDefinitions.GetPacketLength(0x08, state));
    }
}
