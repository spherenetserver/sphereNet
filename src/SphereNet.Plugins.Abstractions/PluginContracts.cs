using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SphereNet.Plugins;

/// <summary>
/// An in-process plugin: a public class with a parameterless constructor in a DLL that
/// sphere.ini lists under [PLUGINS]. The server creates it once at startup, calls
/// <see cref="OnLoad"/>, and <see cref="OnUnload"/> at shutdown.
///
/// Events and commands are the same as the plugin bridge's (docs/PLUGINS.md), so a
/// plugin can move between running inside the server and running as its own process.
/// </summary>
public interface ISpherePlugin
{
    /// <summary>Subscribe to events here. The main loop is not running yet, so
    /// commands sent from here fail; act on <c>server.started</c> instead.</summary>
    void OnLoad(IPluginHost host);

    /// <summary>The server is shutting down. Keep it short.</summary>
    void OnUnload() { }
}

/// <summary>What the server gives a plugin.</summary>
public interface IPluginHost
{
    /// <summary>The plugin's name in sphere.ini.</summary>
    string PluginName { get; }

    /// <summary>A logger whose lines carry the plugin's name.</summary>
    ILogger Logger { get; }

    /// <summary>Receive the events matching <paramref name="pattern"/>: <c>*</c>, an
    /// event name, or a prefix ending in <c>.*</c>. Handlers run one at a time on the
    /// plugin's own thread, never on the game loop. Dispose the result to stop.</summary>
    IDisposable Subscribe(string pattern, Action<PluginEvent> handler);

    /// <summary>Run a bridge command (<c>players</c>, <c>broadcast</c>, <c>function</c>,
    /// ...) with the given fields, e.g. <c>ExecuteAsync("broadcast", new { text = "Hi" })</c>.
    /// Completes with the command's result, or throws with its error.</summary>
    Task<JsonElement> ExecuteAsync(string op, object? fields = null, CancellationToken cancellationToken = default);

    /// <summary>Look at incoming client packets before the server handles them; return
    /// false to drop one. Pass the opcodes to filter (none = every packet). Runs
    /// synchronously on the game loop: keep it fast and do not block. A filter that
    /// throws lets the packet through and counts as a plugin failure.</summary>
    IDisposable AddIncomingPacketFilter(Func<IncomingPacket, bool> filter, params byte[] opcodes);

    /// <summary>Approve each login after its password checked out: return null to
    /// allow it, or a reason to refuse it (the client is told the account is blocked;
    /// the reason is logged and sent to plugins with login.failed). Runs synchronously
    /// on the game loop; a check that throws allows the login.</summary>
    IDisposable AddLoginCheck(Func<LoginAttempt, string?> check);
}

/// <summary>A client packet about to be handled. <see cref="Data"/> is the whole
/// frame, opcode first.</summary>
public sealed record IncomingPacket(byte Opcode, byte[] Data, string Ip, string? Account);

/// <summary>A login whose password was accepted.</summary>
public sealed record LoginAttempt(string Account, string Ip);

/// <summary>One server event, as the bridge sends it.</summary>
public sealed record PluginEvent(string Name, DateTime Time, JsonElement Data);

/// <summary>The plugin API version this assembly describes.</summary>
public static class PluginApi
{
    /// <summary>Raised when the contract changes in a way an older plugin would notice.
/// 2: incoming packet filters and login checks.</summary>
    public const int Version = 2;
}
