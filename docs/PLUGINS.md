# Plugin bridge

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

## How it behaves

- The game loop never waits on a plugin. Events go into a queue and are written to the
  plugin from a background thread; with no plugin connected, nothing is built at all.
- A plugin that stops reading loses events, and one that stays behind is disconnected.
  Connect again and send `status` or `players` to catch up.
- Commands run on the main loop at the start of an iteration, as a panel action does,
  and every command except `ping`, `status` and `players` is logged as `Plugin audit`.
- A plugin cannot cancel or change an event; it is told about it after the fact.
- At most 8 plugins can be connected at once.

## Protocol

Every message, in both directions, is one JSON object on one line, ended by `\n`
(UTF-8). A line may be at most 64 KB.

### Connecting

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

### Events

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

### Events from scripts

A script sends its own event with

```
SERV.PLUGINEVENT boss_killed,<SRC.NAME> killed the dragon
```

which reaches plugins subscribed to `custom.boss_killed` (or `custom.*`) as
`{"text":"Alice killed the dragon"}`. The name may hold letters, digits, `_` and `.`,
up to 64 characters. This is how a shard decides what is worth announcing.

### Commands

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

## Example

`tools/plugins/discord_webhook.py` posts server start/stop, failed saves, deaths and
`custom.*` script events to a Discord webhook. It needs only Python 3:

```
python discord_webhook.py --port 2599 --token change-me --webhook https://discord.com/api/webhooks/...
```

A minimal client in any language is the same three steps: connect, send the auth line,
then read lines and react to the ones whose `event` it cares about.
