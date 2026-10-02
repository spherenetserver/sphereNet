using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>SENDPACKET reads each token with Exp_GetVal behind a toupper'd size prefix
/// (CClientMsg.cpp:2746-2790): a defname, an expression and a lower-case prefix are all
/// values. Only upper-case prefixes followed by a digit were accepted, so a packet
/// carrying an effect by its ITEMDEF name was rejected whole.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SendPacketTokenParityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_sendpk_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (SphereNet.Game.Clients.GameClient Client, NetState State, Character Ch) Build()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "p.scp");
        File.WriteAllText(file, "[ITEMDEF 036b0]\r\nDEFNAME=i_fx_probe\r\n");
        var lf = TestHarness.CreateLoggerFactory();
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        res.LoadResourceFile(file);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        var state = new NetState(lf.CreateLogger<NetState>()) { Id = 1791 };
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        var client = new SphereNet.Game.Clients.GameClient(state, world, new AccountManager(lf),
            lf.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var ch = world.CreateCharacter();
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);
        return (client, state, ch);
    }

    private static List<byte[]> Sent(NetState state)
    {
        var queues = (Queue<PacketBuffer>[])typeof(NetState)
            .GetField("_queues", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!;
        var all = new List<byte[]>();
        for (int p = queues.Length - 1; p >= 0; p--)
            foreach (var pkt in queues[p])
                all.Add(pkt.Span.ToArray());
        return all;
    }

    [Fact]
    public void DefnamesExpressionsAndLowerCasePrefixesAreValues()
    {
        var (client, state, ch) = Build();
        Assert.True(client.TryExecuteScriptCommand(ch, "SENDPACKET", "0C0 w01a75 Wi_fx_probe b1+1 D0", null));
        var packet = Assert.Single(Sent(state));
        Assert.Equal(new byte[] { 0xC0, 0x1A, 0x75, 0x36, 0xB0, 0x02, 0, 0, 0, 0 }, packet);
    }

    [Fact]
    public void TheNumericFormsAreUnchangedAndAnUnknownNameStillRejects()
    {
        var (client, state, ch) = Build();
        Assert.True(client.TryExecuteScriptCommand(ch, "SENDPACKET", "065 W0203 B01 BYTE:05", null));
        Assert.Equal(new byte[] { 0x65, 0x02, 0x03, 0x01, 0x05 }, Assert.Single(Sent(state)));

        Assert.True(client.TryExecuteScriptCommand(ch, "SENDPACKET", "065 Wi_no_such_name_anywhere", null));
        Assert.Single(Sent(state));
    }
}
