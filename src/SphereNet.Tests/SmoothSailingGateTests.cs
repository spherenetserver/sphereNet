using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Diagnostics;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Which clients are sent the smooth ship move (0xF6). Source-X sails smoothly only
/// for 7.0.9.0 (MINCLIVER_HS) or the Enhanced client, and only while
/// OF_NoSmoothSailing is off (CCMultiMovable.cpp:330); every other client is moved
/// with the ship by ordinary updates. SphereNet broadcast 0xF6 to every client near
/// the hull, and a client older than High Seas cannot parse it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SmoothSailingGateTests
{
    private static GameClient ClientAt(uint packed)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, TestHarness.CreateWorld(), new AccountManager(lf), 9301);
        client.NetState.ClientVersionNumber = ReferenceClientProtocol.ToServerNumber(packed);
        return client;
    }

    [Theory]
    [InlineData(2, 0, 3, false)]
    [InlineData(5, 0, 1, false)]
    [InlineData(7, 0, 8, false)]
    [InlineData(7, 0, 9, true)]
    [InlineData(7, 0, 20, true)]
    public void OnlyHighSeasClientsSailSmoothly(int major, int minor, int rev, bool smooth)
    {
        GameClient.ServerOptionFlags = 0;
        Assert.Equal(smooth, ClientAt(ReferenceClientProtocol.Pack((byte)major, (byte)minor, (byte)rev, 0)).UsesSmoothSailing);
    }

    [Fact]
    public void NoSmoothSailingTurnsItOffForEveryone()
    {
        GameClient.ServerOptionFlags = OptionFlags.NoSmoothSailing;
        Assert.False(ClientAt(ReferenceClientProtocol.Pack(7, 0, 20, 0)).UsesSmoothSailing);
    }
}
