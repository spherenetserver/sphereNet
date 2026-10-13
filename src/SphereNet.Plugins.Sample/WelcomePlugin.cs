using Microsoft.Extensions.Logging;

namespace SphereNet.Plugins.Sample;

/// <summary>Greets every player who enters the world with a server broadcast.</summary>
public sealed class WelcomePlugin : ISpherePlugin
{
    private IPluginHost _host = null!;

    public void OnLoad(IPluginHost host)
    {
        _host = host;
        host.Subscribe("player.login", e =>
        {
            string name = e.Data.GetProperty("name").GetString() ?? "";
            _host.ExecuteAsync("broadcast", new { text = $"Welcome, {name}!" })
                .ContinueWith(t => _host.Logger.LogWarning(t.Exception, "Welcome broadcast failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
        });
        host.Logger.LogInformation("Welcome plugin loaded");
    }
}
