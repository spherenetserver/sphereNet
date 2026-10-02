using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A [DEFMESSAGE] value is read with CScriptKey::GetArgStr, which drops the
/// double quotes around it (CServerConfig.cpp:3423). Packs write their messages quoted,
/// and blank one out with "" - that went out to players as two quote marks.</summary>
public sealed class DefMessageQuoteTests
{
    [Fact]
    public void QuotedMessagesLoseTheirQuotes_AndEmptyQuotesAreTheEmptyMessage()
    {
        var resources = new ResourceHolder(NullLogger<ResourceHolder>.Instance);
        string file = Path.Combine(Path.GetTempPath(), $"defmsg_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file,
            "[DEFMESSAGE messages]\r\n" +
            "server_worldsave\t\t\"\"\r\n" +
            "repair_not\t\t\"The item is not repairable\"\r\n" +
            "plain_msg=Unquoted text stays\r\n");
        try { resources.LoadResourceFile(file); }
        finally { File.Delete(file); }

        Assert.True(resources.TryGetDefMessage("server_worldsave", out var blank));
        Assert.Equal("", blank);
        Assert.True(resources.TryGetDefMessage("repair_not", out var quoted));
        Assert.Equal("The item is not repairable", quoted);
        Assert.True(resources.TryGetDefMessage("plain_msg", out var plain));
        Assert.Equal("Unquoted text stays", plain);
    }
}
