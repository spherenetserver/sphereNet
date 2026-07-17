using System;
using System.IO;
using SphereNet.Core.Configuration;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 268 — config-driven multi-entry login server list (0xA8). Source-X
/// send.cpp:3289 lists this shard first then config-defined extra shards (cap 32);
/// SphereNet previously hardcoded a single "SphereNet"/127.0.0.1 entry that ignored
/// config. The 0xA0 select now honors the list index for the 0x8C relay target.
/// </summary>
public sealed class SourceXWave268Tests
{
    [Fact]
    public void PacketServerList_WritesAllEntriesWithSequentialIndices()
    {
        var servers = new[]
        {
            new ServerListEntry("First", 0x7F000001u, 2593),
            new ServerListEntry("Second", 0x01020304u, 2594),
            new ServerListEntry("Third", 0x05060708u, 2595),
        };

        byte[] d = new PacketServerList(servers).Build().Data;

        Assert.Equal(0xA8, d[0]);
        Assert.Equal(0x5D, d[3]);                 // system info flag
        Assert.Equal(3, (d[4] << 8) | d[5]);      // count

        // Each entry is index(2) + name(32) + pct(1) + tz(1) + ip(4) = 40 bytes,
        // first entry at offset 6.
        Assert.Equal(0, (d[6] << 8) | d[7]);
        Assert.Equal(1, (d[46] << 8) | d[47]);
        Assert.Equal(2, (d[86] << 8) | d[87]);

        // Entry 0 IP sits after index(2)+name(32)+pct(1)+tz(1) = offset 6+36 = 42.
        uint ip0 = (uint)((d[42] << 24) | (d[43] << 16) | (d[44] << 8) | d[45]);
        Assert.Equal(0x7F000001u, ip0);
    }

    [Fact]
    public void PacketServerList_CapsAtThirtyTwoEntries()
    {
        var many = new ServerListEntry[40];
        for (int i = 0; i < many.Length; i++)
            many[i] = new ServerListEntry($"S{i}", 0x7F000001u, 2593);

        byte[] d = new PacketServerList(many).Build().Data;
        Assert.Equal(32, (d[4] << 8) | d[5]); // Source-X MAX_SERVERS_LIST
    }

    [Fact]
    public void SphereConfig_ParsesServerListEntries()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_srvlist_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, """
            [SPHERE]
            ServName=Main Shard
            SERVERLIST=Second Shard,10.0.0.5,2594; Third Shard,10.0.0.6,2595
            """);
        try
        {
            var parser = new IniParser();
            parser.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(parser);

            Assert.Equal(2, config.ServerList.Count);
            Assert.Equal("Second Shard", config.ServerList[0].Name);
            Assert.Equal("10.0.0.5", config.ServerList[0].Ip);
            Assert.Equal(2594, config.ServerList[0].Port);
            Assert.Equal("Third Shard", config.ServerList[1].Name);
            Assert.Equal(2595, config.ServerList[1].Port);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void SphereConfig_NoServerList_IsEmpty()
    {
        Assert.Empty(new SphereConfig().ServerList);
    }
}
