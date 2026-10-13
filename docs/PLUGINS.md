# Plugins

There are two ways to extend the server without changing it, and both see the same
events and send the same commands:

- **The plugin bridge** - a program outside the server, in any language, connected over
  a local TCP socket. A crash in it cannot touch the server. Start here.
- **In-process .NET plugins** - a DLL the server loads at startup (see the end of this
  page). For code that wants to live with the server, written in C#. Only these can
  filter incoming packets and refuse logins.

The panel's **Plugins** page lists both: each bridge client (address, subscriptions,
events dropped, queue) and each DLL plugin (subscriptions, hooks, failures, slow hooks).

## Plugin bridge

The plugin bridge lets a program outside the server follow what happens on the shard
and act on it: post deaths and saves to Discord, collect warnings, back up a save when
it finishes, block an address that keeps failing to log in. The program can be written
in any language that has a TCP socket and a JSON parser.

The bridge is off until `sphere.ini` gives it a port and a token:

```
PLUGINBRIDGEPORT=2599
PLUGINBRIDGETOKEN=change-me
PLUGINBRIDGEADDRESS=127.0.0.1
```

It listens on this machine only by default. See [CONFIG-EN.md](CONFIG-EN.md) for the keys.

### How it behaves

- The game loop never waits on a plugin. Events go into a queue and are written to the
  plugin from a background thread; with no plugin connected, nothing is built at all.
- A plugin that stops reading loses events, and one that stays behind is disconnected.
  Connect again and send `status` or `players` to catch up.
- Commands run on the main loop at the start of an iteration, as a panel action does,
  and every command except `ping`, `status` and `players` is logged as `Plugin audit`.
- A plugin cannot cancel or change an event; it is told about it after the fact.
- At most 8 plugins can be connected at once.

### Protocol

Every message, in both directions, is one JSON object on one line, ended by `\n`
(UTF-8). A line may be at most 64 KB.

#### Connecting

The first line must authenticate, within 5 seconds, and name the events wanted:

```json
{"op":"auth","token":"change-me","subscribe":["player.*","save.completed","custom.*"]}
```

A subscription is `*` (everything), an event name, or a prefix ending in `.*`.
The reply:

```json
{"ok":true,"version":1,"subscribe":["player.*","save.completed","custom.*"]}
```

A wrong token gets `{"ok":false,"error":"bad token"}` and the connection is closed.

#### Events

```json
{"event":"player.login","time":"2026-10-13T18:04:11.52Z","data":{"account":"alice","name":"Alice","serial":4097,"ip":"203.0.113.7"}}
```

| Event | `data` |
|---|---|
| `server.started` | `name`, `port` |
| `server.stopping` | `null` |
| `save.started` | `forced` (a shutdown or forced save) |
| `save.completed` | `ok`, `seconds`, `message` (why it failed) |
| `player.login` | `account`, `name`, `serial`, `ip` |
| `player.logout` | `name`, `serial` |
| `char.death` | `victim`, `killer` (or `null`): each `name`, `serial`, `player`, `map`, `x`, `y` |
| `login.failed` | `account`, `ip`, `reason`: `no_account`, `blocked`, `bad_password`, `too_many_tries` |
| `connection.rejected` | `ip`, `reason`: `filter` (blocked or throttled address), `ip_history`, `ip_limit`, `no_slot` |
| `log` | `level` (`Warning`, `Error`, `Fatal`), `message`, `exception` |
| `crash` | `source`, `terminating`, `type`, `message` |
| `custom.<name>` | `text` |

`server.started` is sent once, when the server opens; a plugin that connects later
asks for `status` instead.

#### Events from scripts

A script sends its own event with

```
SERV.PLUGINEVENT boss_killed,<SRC.NAME> killed the dragon
```

which reaches plugins subscribed to `custom.boss_killed` (or `custom.*`) as
`{"text":"Alice killed the dragon"}`. The name may hold letters, digits, `_` and `.`,
up to 64 characters. This is how a shard decides what is worth announcing.

#### Commands

```json
{"id":1,"op":"players"}
```

The reply carries the same `id`:

```json
{"id":1,"ok":true,"result":[{"account":"alice","name":"Alice","serial":4097,"map":0,"x":1495,"y":1629,"ip":"203.0.113.7"}]}
{"id":2,"ok":false,"error":"unknown op 'foo'"}
```

| `op` | Fields | Result |
|---|---|---|
| `ping` | | `"pong"` |
| `status` | | `name`, `uptimeSeconds`, `players`, `lastSaveOk`, `saveCount` |
| `players` | | the online players, as above |
| `broadcast` | `text` | `true`; the line goes to every player as a server broadcast |
| `kick` | `serial` | whether a playing character had that serial |
| `save` | | `true`; a world save is requested |
| `function` | `name`, `args` | the output lines; a script `[FUNCTION]` or a `SERV` verb/property, checked like the panel's `/api/server/function` |
| `command` | `line` | the output lines of a console command (as typed at the server console) |

`function` and `command` run with server (owner) rights: the token is what protects them.

### Example

`tools/plugins/discord_webhook.py` posts server start/stop, failed saves, deaths and
`custom.*` script events to a Discord webhook. It needs only Python 3:

```
python discord_webhook.py --port 2599 --token change-me --webhook https://discord.com/api/webhooks/...
```

A minimal client in any language is the same three steps: connect, send the auth line,
then read lines and react to the ones whose `event` it cares about.

## In-process .NET plugins

A plugin is a .NET 10 class library with a public class implementing `ISpherePlugin`
from `SphereNet.Plugins.Abstractions`. List it in `sphere.ini`, after the `[SPHERE]`
keys and before `[EOF]`:

```
[PLUGINS]
Welcome=plugins/Welcome/SphereNet.Plugins.Sample.dll
```

The path is relative to the server folder. Plugins load in file order when the server
opens, before `server.started`.

```csharp
using SphereNet.Plugins;

public sealed class WelcomePlugin : ISpherePlugin
{
    public void OnLoad(IPluginHost host)
    {
        host.Subscribe("player.login", e =>
        {
            string name = e.Data.GetProperty("name").GetString() ?? "";
            _ = host.ExecuteAsync("broadcast", new { text = $"Welcome, {name}!" });
        });
    }
}
```

The project is `src/SphereNet.Plugins.Sample`. A plugin project references the
abstractions with `<Private>false</Private>` and `<ExcludeAssets>runtime</ExcludeAssets>`
(the server provides that assembly), and sets `<EnableDynamicLoading>true</EnableDynamicLoading>`
so its own dependencies are copied next to it.

- `Subscribe(pattern, handler)` takes the same patterns as the bridge; `PluginEvent`
  carries `Name`, `Time` and `Data` (the same JSON as the bridge's `data`).
- `ExecuteAsync(op, fields)` runs a bridge command and returns its `result` as JSON;
  an error throws.
- `Logger` writes to the server log under `Plugin.<name>`. These lines are not sent
  back to plugins as `log` events.
- `OnUnload()` runs at shutdown.

### Synchronous hooks

Two hooks run on the game loop itself, so a plugin can stop something before it happens:

```csharp
// Drop a packet: return false. Name the opcodes; with none, every packet is offered.
host.AddIncomingPacketFilter(p => !(p.Opcode == 0xAD && p.Data.Length > 2000), 0xAD);

// Refuse a login whose password was accepted: return a reason, or null to allow.
host.AddLoginCheck(a => banList.Contains(a.Ip) ? "address banned" : null);
```

- `IncomingPacket` carries `Opcode`, `Data` (the whole frame, opcode first; a copy the
  plugin may keep), `Ip` and `Account` (null before login). The filter runs before the
  server handles the packet, before the `PACKETx` script filter and before the ping
  shortcut. Only the listed opcodes are offered, so a filter on one opcode costs the
  others nothing.
- `LoginAttempt` carries `Account` and `Ip`. It runs on the login server and again on
  the game server connection. A refused client is told its account is blocked; the
  reason goes to the server log and to `login.failed` as `plugin: <name>: <reason>`.
- Both return the call's `IDisposable`; dispose it to remove the hook.

Because they hold up the game loop, hooks are watched:

- Each call is timed. One slower than 5 ms is reported (at most once a minute per
  plugin); one slower than 100 ms counts as a failure.
- A hook that throws counts as a failure and the packet or login is let through - a
  broken plugin never locks players out.
- Failures from hooks and event handlers add up; at 10 the plugin is switched off and
  all its hooks removed.

How the server protects itself:

- Each plugin DLL is loaded into its own load context: its dependencies come from its
  own folder and cannot clash with the server's. Only the abstractions and the logging
  interfaces are shared.
- Handlers run one at a time on the plugin's own thread, never on the game loop. A
  plugin that falls more than 4096 events behind loses the newest ones.
- A handler that throws is logged; after 10 failures the plugin is switched off. A
  plugin whose `OnLoad` throws stays off.
- Commands sent from `OnLoad` fail (the main loop is not running yet); do the work when
  `server.started` arrives.

A plugin runs with the server's full rights, inside its process: a plugin that blocks
its own thread only stops itself, but one that exhausts memory or calls
`Environment.Exit` takes the server with it. Prefer the bridge for anything that does
not need to be inside.
