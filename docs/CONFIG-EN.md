# SphereNet Configuration Reference (sphere.ini)

Every key of `config/sphere.ini`, the counterpart of Source-X `CServerConfig`. The ini
itself carries one summary line per key; this file is the full description. The Turkish
version is [CONFIG-TR.md](CONFIG-TR.md).

## Syntax

- `KEY=VALUE`. Key names are case-insensitive.
- Comments start with `//`. Anything after `//` on a line is a comment too, so a value
  can never contain `//` (e.g. no `https://` in a URL).

#### Numbers

Three rules, depending on how the key is read:

1. Most integer keys are read as **decimal**; hexadecimal needs the `0x` prefix (`0x20`).
   A Sphere-style leading zero is **not** hex here: `0200` = two hundred, and a value
   written with `|` (e.g. `01|010`) cannot be read and the default is kept.
   `COMBATFLAGS`, `MAGICFLAGS`, `OPTIONFLAGS`, `CHATFLAGS`, `RACIALFLAGS`, `EMOTEFLAGS`,
   `LOGMASK` and `FEATURE*` belong to this group.
2. Flag keys (`REVEALFLAGS`, `AREAFLAGS`, `AUTOPRIVFLAGS`, `STATSFLAGS`,
   `EXPERIENCEMODE`, `COMBATPARRYINGERA`) follow the Sphere rule: a **leading zero means
   hex** (`010` = 16) and several values are OR'ed with `|` (`01|02|010`).
   `MINCHARDELETETIME`, `REGEN3` and `TIMEOUTINCOMPLETECONN` use the same rule and may be
   written as a product (`7*24*60*60`, `5*1000`).
3. `NPCAI`, `PETSINHERITNOTORIETY`, `ERALIMIT*`, `TELEPORTEFFECT*`, `TELEPORTSOUND*`,
   `WOPCOLOR`, `WOPFONT`, `WOPTALKMODE`: a `0x` prefix or a leading zero means hex,
   anything else is decimal. `COLOR*` keys are **always** hex, with or without `0x`
   (`22` = `0x22`).

#### Boolean

`1`, `true` or `yes` = on; anything else (including `0` and `2`) = off.

## Status tags

Each key's line in `sphere.ini` ends with one of these:

- `[WORKING]` - used by the engine.
- `[READ ONLY]` - loaded into the config but does not change game behaviour (at most a
  script can read it back with `SERV.<KEY>`).
- `[NOT IMPLEMENTED]` - not even loaded; a placeholder kept for Source-X ini
  compatibility. The server lists such keys at startup as "not read by this engine".

"Default" below is the engine's own value, used when the key is absent. The active value
in the shipped `sphere.ini` may differ from it on purpose.

## DEBUG & LOGGING

### DEBUGPACKETS

Dump packet traffic to the console/log and lower the console level to Debug. Can also be toggled from the panel at run time.

- **Type:** boolean (`0`/`1`)
- **Default:** `0`

### SCRIPTDEBUG

Log script execution traces. Can also be toggled from the panel.

- **Type:** boolean (`0`/`1`)
- **Default:** `0`

### DEBUGPACKETOPCODES

Optional packet debug filter: hex opcodes, comma separated. Empty logs every packet (except the `0x73` ping).

- **Type:** string (e.g. `22,34,BF` = resync/status/extended only)
- **Default:** `""` (all)

### DEBUGPACKETCATEGORIES

Which kinds of packet the debug log keeps.

- **Type:** any of `player`, `npc`, `item`, `packet`, comma separated; empty = all
- **Default:** `""`
- **Note:** A crowded street sends far more about creatures and their gear than about the watching player; on a debug run that traffic is most of the log and most of its cost. `player,packet` keeps the client's own conversation with the server readable and drops the rest before it is formatted.

### LOGFILELEVEL

Filter for the rolling log file `logs/spherenet-*.log`. The console level is independent (it follows `DebugPackets`); this only affects what reaches the file, so routine INF messages (commands, script loads, connections) stay off the disk.

- **Type:** two syntaxes:
  1. A single level = minimum threshold; that level and above are written.
     ```
     LogFileLevel=Warning      -> WRN, ERR, FTL
     LogFileLevel=Error        -> ERR + FTL only
     LogFileLevel=Information  -> same as the console
     ```
  2. A `|` or comma separated whitelist = ONLY the listed levels are written.
     ```
     LogFileLevel=Verbose | Warning | Error | Fatal
     LogFileLevel=Error, Fatal
     ```
  Level names (case-insensitive): `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`.
- **Default:** `Warning` (only WRN/ERR/FTL reach the disk)
- **Note:** Implemented as a Serilog file sink filter.

## PERFORMANCE & MULTICORE

The multicore tick pipeline is always on: Build (parallel) -> Apply (serial). On an error or timeout it falls back to a single thread automatically; there is no switch for it.

### TICKSLEEPMODE

How the main loop yields the CPU between ticks.

- **Type:** integer (0-3)

  | Value | Mode | Behavior |
  |---|---|---|
  | `0` | spin | Lowest latency (<1ms), highest CPU; for dedicated servers. |
  | `1` | sleep | Minimal CPU, ~2ms latency; for low-end/shared machines. |
  | `2` | hybrid | Balanced, ~1ms latency, moderate CPU. |
  | `3` | adaptive | Sleeps out the slack until the next tick in capped slices: near-zero idle CPU with bounded input latency; compare the `[tick_stats]` lpt gauge before/after switching. |

- **Default:** `3`
- **Note:** On Windows the server asks for a 1 ms system timer and opts out of power throttling, so the sleeps of modes 1 and 3 last ~1-2 ms instead of a 15.6 ms timer tick, even while the server window is hidden.

### ASYNCRECEIVE

How connections receive data.

- **Type:** boolean (0-1)
  - `1` = completions: every connection keeps a receive outstanding; data is collected off the main loop and the loop's sleep ends as soon as it arrives (no per-connection polling, lower input latency).
  - `0` = polled: the main loop asks the OS each pass which connections have data (one `Socket.Select` over all of them). Fallback.
- **Default:** `1`
- **Note:** Not Source-X `USEASYNCNETWORK`, which is about sending.

### SLOWTICKWARNMS / LOOPSTALLWARNMS

Diagnostic thresholds (ms). A server tick slower than `SlowTickWarnMs` logs `[slow_tick]`; a main-loop iteration slower than `LoopStallWarnMs` logs `[loop_stall]`. `0` disables that warning.

- **Type:** integer (ms)
- **Default:** `50` / `100` (half / all of the 100ms tick budget)

### SLOWPACKETWARNMS

A single inbound packet handler slower than this logs `[slow_packet]` with the opcode and handler name; names the culprit when a `[loop_stall]` comes from `net_in`.

- **Type:** integer (ms, `0` = off)
- **Default:** `20`

### MULTICOREWORKERCOUNT

Number of worker threads for the parallel phases.

- **Type:** integer (`0` = automatic, ProcessorCount)
- **Default:** `0`
- **Note:** Applied as the `Parallel.ForEach` MaxDegreeOfParallelism.

### MULTICOREPHASETIMEOUTMS

Timeout of one multicore phase (ms).

- **Type:** integer (ms, minimum 100)
- **Default:** `5000`
- **Note:** When exceeded the runtime flag is cleared and the tick falls back to a single thread. Below 100 a startup warning is printed. The timeout is cooperative: it is checked between sectors, world tick phases and NPC/view phases; work already running is not cut short, so a tick can overrun it by that work's length.

### MAXNPCSPERTICK

Due NPC actions processed per server tick; the rest are deferred to the next tick (`[npc_budget]` log line).

- **Type:** integer (minimum 1)
- **Default:** `500`

### NPCPATHPRESTAGEPERTICK

Prestage A* path searches accepted per tick; other chasers are deferred and rotated in on later ticks.

- **Type:** integer (minimum 0)
- **Default:** `8`

### PLAYERBOTS

Server-driven player characters: ordinary player characters (saved with the world, resumed where they stood after a restart) that the server walks between banks, shops, inns and healers, fights with and talks through. They are online but not clients: FORCLIENTS, SECTOR.CLIENTS and the login player count leave them out.

- **Type:** boolean
- **Default:** `0`

### PLAYERBOTCOUNT

Population the server fills up to, one new bot at a time; bots already in the save are adopted first. Lowering it logs the surplus out (they stay in the save).

- **Type:** integer (minimum 0)
- **Default:** `50`

### PLAYERBOTDATADIR

Bot data directory: `nav/` (road network and destinations), `chat/<language>/`, `archetypes.txt`, and `scripts/*.scp` (archetype CHARDEFs, loaded with the pack when PLAYERBOTS is on).

- **Type:** path
- **Default:** `playerbots/`

### PLAYERBOTLANGUAGE

Chat language folder under `chat/` (`en`, `tr`). A category the chosen language lacks falls back to `en`.

- **Type:** string
- **Default:** `en`

### PLAYERBOTREDRESPAWN

Region (AREADEF `NAME` or defname) whose anchor point a dead murderer bot is raised at. Any other dead bot is raised at the nearest town healer.

- **Type:** string
- **Default:** `Delucia`

### PLAYERBOTRECLAIMRANGE

A raised bot whose corpse lies within this many tiles walks back and takes its things off it; a farther corpse has what is still on it handed back to the bot. Until then the corpse can be looted like any other.

- **Type:** integer (minimum 0)
- **Default:** `40`

### PLAYERBOTGRAVITY

Percent of a bot's town trips that go to a place within 30 tiles of a connected player, so bots gather where players are. `0` turns it off.

- **Type:** integer (0-100)
- **Default:** `40`

### PLAYERBOTCROWD

Bots standing within sight of one player beyond which no more are drawn toward that player.

- **Type:** integer (minimum 0)
- **Default:** `14`

### PLAYERBOTCHATPACE

Mean seconds between two bot scenes (a line, a trade haggle, news and its reaction) played near each connected player; each wait is half to one and a half of it. Bots nobody hears say nothing.

- **Type:** integer (minimum 4)
- **Default:** `30`

### PLAYERBOTNAMEPREFIX

Put before every bot's name as players see it; quote it to keep a trailing space. Empty for none. The bot's own name is kept in `PLAYERBOT.NAME`, so a changed prefix replaces the old one the next time the bot comes online.

- **Type:** string
- **Default:** `"[Bot] "`

### PLAYERBOTPKPERCENT

Percent of fighter bots that are player killers, decided once per bot (`PLAYERBOT.ROLE`). A player killer keeps out of guarded towns, waits about dungeon mouths, moongates and the roads, and sets on a lone player or bot it rates itself against, then goes through the body. Crime, murder counts, guards and the murderer's resurrection town follow the normal rules. `0` for none.

- **Type:** integer (0-100)
- **Default:** `5`

### PLAYERBOTTHIEFPERCENT

Percent of the other bots that are thieves, decided once per bot. A thief works guarded town crowds: it steps up to someone, opens their pack, steals with the Stealing skill, then hides and walks off, every few minutes. Getting caught follows the normal criminal rules. `0` for none.

- **Type:** integer (0-100)
- **Default:** `5`

### MULTICOREDETERMINISMDEBUG

Determinism check mode: a SHA-256 hash of the world is computed and logged at the end of each tick (first 2000 ticks).

- **Type:** boolean (`0`/`1`)
- **Default:** `0`

### MULTICOREDETERMINISMEXPECTEDHASH

Expected determinism hash; a mismatch logs a warning. Empty = no comparison.

- **Type:** string (SHA-256 hex)
- **Default:** `""`

## SERVER IDENTITY, PANEL & HOST

### SERVNAME

Name shown in the server list and on the status page (used by the 0xA8 server list, the web status page, the telnet console and the panel).

- **Type:** string
- **Default:** `"SphereNet"`

### SERVPORT

TCP port the game server listens on.

- **Type:** integer (1-65535)
- **Default:** `2593`
- **Note:** Standard UO port is 2593. Telnet console: `SERVPORT+1`, web status: `SERVPORT+2`, panel (when `ADMINPANELPORT=0`): `SERVPORT+3`.

### SERVIP

IP address announced to the client in the login server list (`0xA8`) and in the relay to the game server (`0x8C`). Also readable by scripts as `SERV.SERVIP`.

- **Type:** string (IPv4)
- **Default:** `"0.0.0.0"` (= the local address the connection came in on, else `127.0.0.1`)
- **Note:** Does NOT change the listening address; the socket always listens on `0.0.0.0:SERVPORT`. Behind NAT/a router put the public IP here.

### SERVERLIST

Extra shards listed on the login screen IN ADDITION to this one.

- **Type:** string: `;` separated `name,ip,port` entries (no port = `SERVPORT`)
- **Default:** `""` (only this shard)
- **Note:** Counterpart of the Source-X `[SERVERS]` block; a malformed entry is skipped silently.
- **Example:** `SERVERLIST=Test Shard,192.168.1.10,2593;Event Shard,192.168.1.11,2594`

### URL

The shard's website; given to scripts as `SERV.URL`.

- **Type:** string
- **Default:** `"www.spherenetserver.com"`
- **Note:** `//` starts a comment in this file, so `https://` cannot be written; the script side adds the scheme when needed. Source-X also only returns the value through `SERV.URL`.
- **Example:** `URL=www.myshard.com`

### ADMINEMAIL

Administrator e-mail address; given to scripts as `SERV.ADMINEMAIL`.

- **Type:** string
- **Default:** `""`
- **Note:** Only read back through `SERV.ADMINEMAIL`; the engine does not use it.

### ADMINPASSWORD

Password of the telnet admin console and the web panel (used by the telnet console, the admin host policy, SphereNet.Host and the panel).

- **Type:** string
- **Default:** `"1234"` when the key is ABSENT; an empty value disables the telnet admin console.
- **Note:** This file leaves it empty; the panel setup wizard hashes the chosen password and writes it here.

### ADMINPANELAUTOFILL

Pre-fill `ADMINPASSWORD` on the panel login screen (`/api/auth/local-hint`).

- **Type:** boolean (`0`/`1`)
- **Default:** `0` (off)
- **Note:** Only for requests from localhost and only while `ADMINPASSWORD` is plain text; once the setup wizard hashes it, auto-fill stops. It hands the password to the browser, so enable it for local development only.

### ADMINPANELPORT

Port the web panel listens on (used by SphereNet.Host and the in-process admin panel).

- **Type:** integer
- **Default:** `0` (= `SERVPORT+3`)

### ADMINPANELALLOWEDHOSTS

Host names the panel answers to (comma separated), checked against the request's Host header.

- **Type:** string
- **Default:** `""` (only `localhost`, `127.0.0.1`, `::1`)
- **Note:** The panel listens on localhost only. Behind a reverse proxy put the public name the proxy forwards here (e.g. `panel.myshard.com`). A request with a Host not on the list gets 400: this stops a malicious page from pointing its own domain at 127.0.0.1 and using the panel (DNS rebinding). `*` disables the check.

### PLUGINBRIDGEPORT

TCP port of the plugin bridge, which lets programs outside the server (in any language) receive server events and send commands. The protocol is in [PLUGINS.md](PLUGINS.md).

- **Type:** integer (`0` = off)
- **Default:** `0`

### PLUGINBRIDGETOKEN

The secret a plugin sends in its first message. Anyone holding it can run console commands through the bridge, so treat it like the admin password.

- **Type:** string
- **Default:** `""`
- **Note:** With no token the bridge does not start, even with a port set. The value cannot contain `//` (the rest of the line would be a comment).

### PLUGINBRIDGEADDRESS

The address the plugin bridge listens on.

- **Type:** IP address
- **Default:** `127.0.0.1` (this machine only)
- **Note:** Any other address makes the bridge reachable from the network, and a warning is logged at startup. Keep it on loopback unless the plugin runs on another machine behind a firewall.

### PUBLICPAPERDOLL

Serve character paperdoll image and info without login: `/public/paperdoll/<serial>.png` and `/public/paperdoll/<serial>.json`.

- **Type:** boolean (`0`/`1`)
- **Default:** `0` (off; both addresses answer 404)
- **Note:** The serial may be hex (`0x..` or leading `0`) or decimal. Only player characters are shown; staff (Counselor and up), NPCs, deleted and unknown serials get the same 404. The JSON has no plevel/account/IP. `Cache-Control max-age=60`; 60 requests per minute per IP. The panel listens on localhost only: outside access goes through a reverse proxy whose Host name must be in `ADMINPANELALLOWEDHOSTS`.

### PUBLICPAPERDOLLORIGINS

Web sites (CORS) allowed to read the public paperdoll from a browser (fetch/XHR), comma separated; sent as `Access-Control-Allow-Origin`.

- **Type:** string
- **Default:** `""` (no CORS header; an `<img>` tag still works)
- **Note:** Write the domain only (plus `:port` if needed), e.g. `www.myshard.com`. `//` starts a comment here so `https://` cannot be written; http and https are both accepted. `*` allows every site.

### SCRIPTPACKREPO

GitHub script pack (`owner/name`) the "Install" button on the panel's Scripts page downloads (`/api/scripts/download`).

- **Type:** string
- **Default:** `Sphereserver/Scripts-X` (official Source-X pack)
- **Note:** Packs with files at the repo root or under `scripts/` are both supported. Top-level folders starting with `_` (`_incomplete` etc.) are not installed.

### SCRIPTPACKBRANCH

Branch to download (`/api/scripts/download`).

- **Type:** string
- **Default:** `main`

### HOSTAUTOSTART

SphereNet.Host starts the game server by itself when it starts.

- **Type:** boolean (`0`/`1`)
- **Default:** `1`
- **Note:** With `0` the server is started with "Start" in the panel. `1` is recommended so the shard does not stay down after an update or a restart.

### HOSTRESTARTONCRASH

Host restarts the server if it crashes.

- **Type:** boolean (`0`/`1`)
- **Default:** `1`
- **Note:** Waits 5, 10, 20 s... up to 2 min. Stops after 5 crashes in 10 minutes (so a server that always crashes at startup does not loop forever). A normal exit (code 0) and Stop from the panel are not crashes.

### HOSTSHUTDOWNQUIETMS

If a shutting-down server prints nothing for this long (ms), the Host treats it as stuck and terminates it.

- **Type:** integer (ms, clamped to 1000-600000)
- **Default:** `20000`
- **Note:** A large world's shutdown save can take long on slow storage; a kill there loses everything since the last periodic save.

### HOSTSHUTDOWNTIMEOUTMS

Absolute ceiling for a shutdown (ms); past it the process is terminated.

- **Type:** integer (ms, clamped to 1000-3600000)
- **Default:** `180000`

## UPDATES FROM THE PANEL (Updates page)

There are two separate update paths; do not mix them up:

- `APPUPDATE*` (below) -> the panel's "Check for update" flow. Downloads the READY binary built by GitHub Actions. The machine needs NO git/SDK/Node. Updates the server from the panel with one click.
- `APPUPDATEREPODIR` (at the bottom) -> `update.cmd`'s build-from-source flow. Needs git + .NET SDK + Node on the machine and runs while the server is STOPPED.

### APPUPDATEREPO

GitHub repository (`owner/name`) updates are pulled from. Read by SphereNet.Host and served by the panel under `/api/update/*`.

- **Type:** string
- **Default:** `""` (empty = the panel update system is off and the page is hidden)
- **Note:** A public repo needs NO token; release assets are downloaded anonymously.

### APPUPDATECHANNEL

Release tag the packages hang on.

- **Type:** string
- **Default:** `nightly`
- **Note:** `nightly` is a rolling prerelease updated on every main commit (see `.github/workflows/release.yml`). To get only hand-tagged stable releases, write that tag here.

### APPUPDATERUNTIME

Platform (RID) of the package to download; decides the asset name `spherenet-<RID>.zip`.

- **Type:** string
- **Default:** `win-x64`
- **Note:** CI currently builds the win-x64 package only.

### APPUPDATECHECKMINUTES

Background check interval for a new version (minutes).

- **Type:** integer (minutes)
- **Default:** `15`
- **Note:** `0` = no automatic check (only "Check for update" in the panel). The check NEVER applies an update by itself; it only shows a badge.

### APPUPDATETOKEN

Optional GitHub token.

- **Type:** string
- **Default:** `""` (empty)
- **Note:** NOT needed for a public repo; leave it empty. Only needed if the repository is made private (`contents:read` is enough).

### APPUPDATEREPODIR

Source (git) folder for `update.cmd` (one-command update). Read ONLY by `update.cmd`/`update.ps1`; SphereNet.Host does not use it. If this working folder holds only published binaries (e.g. `C:\sphereNet`), point this at a git clone: `update.cmd` runs `git pull` + `build.ps1` in that clone and copies the produced binaries here. That machine needs git + .NET SDK + Node/npm. A relative path is resolved against `sphere.ini`. May stay empty when the working folder is the repo.

- **Type:** string (folder path)
- **Default:** `""`
- **Example:** `APPUPDATEREPODIR=D:\Projeler\Yunus\sphereNet`

## CLIENT & CONNECTION

### CLIENTERA

Default client compatibility profile, used until the client reports its version.

- **Type:** `Sphere56x` | `Modern` (or `0` | `1`)
  - `Sphere56x`: an unknown client uses the old Sphere 56x packet formats.
  - `Modern`: modern formats (ClassicUO/7.x) are assumed until the version arrives.
- **Default:** `Modern`

### CLIENTVERSION

Expected client version string.

- **Type:** string (`major.minor.revision.patch`)
  - `"4.0.2"` - T2A era client
  - `"5.0.9.1"` - AOS era client
  - `"7.0.96.5"` - last classic client
- **Default:** `"4.0.2"`
- **Note:** Only logged at startup. Each connection's client version comes from what the client itself reports and is not compared with this value.

### CLIENTMAX

Maximum number of clients connected at the same time.

- **Type:** integer (0 or less = 256)
- **Default:** `256`
- **Note:** Every connection takes a NetState slot; higher = more memory.

### CLIENTMAXIP

Maximum connections from a single IP address.

- **Type:** integer (`0` = unlimited)
- **Default:** `16` (same as Source-X)
- **Note:** The server counts the open connections of the IP on every accept and refuses at the limit. If more than 16 people play from one address (an internet cafe), raise it, or the 17th is refused though legitimate. A reconnecting player's old connection may be counted until it is collected, so keep the limit a little above the player count.

### CLIENTLINGER

How long a player's character stays in the world after the connection drops without a safe logout (seconds).

- **Type:** integer (seconds, `0` = leave at once)
- **Default:** `600` (same as Source-X)
- **Note:** Linkdead time - a player reconnecting within it gets the character back. Dead characters and safe logouts (inn/house) do not wait. Source-X reads the key in seconds too.

### CONNECTINGMAX

Maximum clients in the pre-login (connecting) stage.

- **Type:** integer
- **Default:** `32`
- **Note:** Only read back through `SERV.CONNECTINGMAX`; no separate "connecting" limit is enforced (the per-IP one is `CONNECTINGMAXIP`).

### CONNECTINGMAXIP

Maximum not-yet-in-game connections per IP.

- **Type:** integer (`0` = unlimited)
- **Default:** `8`
- **Note:** An IP past this is refused (Source-X `CNetworkManager.cpp:159`). Loopback is exempt.

### USECRYPT

Accept encrypted (Blowfish/Twofish) login.

- **Type:** boolean (0/1)
  - `0` = accept unencrypted clients only
  - `1` = accept encrypted clients (standard UO)
- **Default:** `1`
- **Note:** Drives the crypto selection (keys from `sphereCrypt.ini`).

### USENOCRYPT

Accept unencrypted connections alongside encrypted ones.

- **Type:** boolean (0/1)
- **Default:** `0`
- **Note:** Works together with `USECRYPT`.

### COLORNOTO*

Hue of the overhead name label per notoriety (single click).

- **Type:** hex hue (ALWAYS hex, with or without `0x`: `22` = red, `59` = blue, `3b2` = grey, `90` = orange)
- **Default:** the values below
- **Note:** `0` = use the built-in default. Hue 0 never means "black": writing 0 applies the default colour. Leave these commented unless you want custom colours; the defaults are already coloured.

| Key | Default | Meaning |
|---|---|---|
| `COLORNOTOGOOD` | `063` | blue (innocent) |
| `COLORNOTOGOODNPC` | `063` | blue (innocent NPC) |
| `COLORNOTOGUILDSAME` | `044` | green (ally) |
| `COLORNOTONEUTRAL` | `03b2` | grey (can be attacked) |
| `COLORNOTOCRIMINAL` | `03b2` | grey (criminal) |
| `COLORNOTOGUILDWAR` | `02b` | orange (enemy guild) |
| `COLORNOTOEVIL` | `026` | red (murderer) |
| `COLORNOTOINVUL` | `035` | yellow (invulnerable) |
| `COLORNOTOINVULGAMEMASTER` | `0b` | purple (GM) |
| `COLORNOTODEFAULT` | `03b2` | grey (anything else) |

The defaults above are Sphere 0.56b's (`CResource.cpp:210-216`). Source-X ships:

```
COLORNOTOGOOD=059  COLORNOTOGOODNPC=059  COLORNOTOGUILDSAME=03f
COLORNOTOGUILDWAR=090  COLORNOTOEVIL=022
```

### COLOREMOTE

Hue of an engine emote ("*You see NAME looks deathly sick*").

- **Type:** hex hue
- **Default:** `022` (red, Sphere 0.56b `HUE_RED`)
- **Note:** Source-X sends emotes grey, `03b2` (`HUE_TEXT_DEF`). SphereNet setting.

### COLORINVISITEM

Hue in which a GM sees an invisible item.

- **Type:** hex hue (`0` = default item colour, `1000` = invisible grey)
- **Default:** `1000`
- **Note:** Only read back through `SERV.COLORINVISITEM`; item drawing does not use it.

### COLORINVIS / COLORINVISSPELL / COLORHIDDEN

A non-zero value switches off the client's grey overlay (mobile flag `0x80`) for a character in that state: GM invisibility / the invisibility spell or potion / hidden (Source-X `CCharStatus.cpp:687`). The hue itself is not applied to drawing in SphereNet.

- **Type:** hex hue
- **Default:** `0` (grey overlay used)

### PETSINHERITNOTORIETY

Which notoriety statuses a pet inherits from its owner.

- **Type:** flags (`0x` prefix or leading zero = hex)

| Flag | Name | Meaning |
|---|---|---|
| `001` | NotoGood | blue |
| `002` | NotoGuildSame | green ally |
| `004` | NotoNeutral | grey |
| `008` | NotoCriminal | grey criminal |
| `010` | NotoGuildWar | orange |
| `020` | NotoEvil | red |
| `040` | NotoInvul | yellow |

- **Example:** `07a` shows the pet as criminal/warring/evil/allied/invul whenever its owner is, whatever the pet's own notoriety.
- **Default:** `0`
- **Note:** Only read back through `SERV.PETSINHERITNOTORIETY`; pet notoriety does not use it.

## EXPANSION FLAGS (EXPANSION SUPPORT)

Each flag enables expansion-specific features and client packets. `0` = off, hex bitmask = on. All `FEATURE*` keys are OR'ed and sent to the client at login in the `0xB9` PacketFeatureEnable packet (filled from the config at startup).

### FEATURET2A

The Second Age (1998).

- **Type:** hex/integer - Source-X `FEATURE_T2A` capability mask (update/chat). Not a raw `0xB9` bit; mapped to protocol bits by the client's ResDisp.
- **Default:** `0x1`
- **Note:** Sent in `0xB9`.

### FEATURELBR

Lord Blackthorn's Revenge (2002).

- **Type:** hex/integer - Source-X `FEATURE_LBR` mask (update/sound).
- **Default:** `0`
- **Note:** Sent in `0xB9`.

### FEATUREAOS

Age of Shadows (2003).

- **Type:** hex/integer - Source-X `FEATURE_AOS` mask; includes the update-A, update-B, popup and tooltip sub-features.
- **Default:** `0`
- **Note:** Sent in `0xB9`; bit `0x02` (update-B) also turns on the AOS combat systems.

### FEATURESE

Samurai Empire (2004).

- **Type:** hex/integer - Source-X `FEATURE_SE` mask.
- **Default:** `0`
- **Note:** Sent in `0xB9`; bit `0x02` enables the SE parry formula (`COMBATPARRYINGERA 0x02`).

### FEATUREML

Mondain's Legacy (2005).

- **Type:** hex/integer - Source-X `FEATURE_ML` mask.
- **Default:** `0`
- **Note:** Sent in `0xB9`.

### FEATUREKR

Kingdom Reborn client support (2007).

- **Type:** hex/integer
- **Default:** `0`
- **Note:** The KR client is no longer in use.

### FEATURESA

Stygian Abyss (2009).

- **Type:** hex/integer - Source-X `FEATURE_SA` mask.
- **Default:** `0`
- **Note:** Sent in `0xB9`.

### FEATURETOL

Time of Legends (2015).

- **Type:** hex/integer - Source-X `FEATURE_TOL` mask.
- **Default:** `0`
- **Note:** Sent in `0xB9`, including the virtual gold bit.

### FEATUREEXTRA

Custom extra expansion bits.

- **Type:** hex/integer
- **Default:** `0`
- **Note:** Included in the FeatureFlags OR.

### RACIALFLAGS

Racial abilities (Source-X `RACIALF_*`).

- **Type:** hex/integer (`0x` prefix; a leading zero is NOT hex)

| Flag | Ability |
|---|---|
| `0x0001` | Human: Strong Back (+60 carry) |
| `0x0002` | Human: Tough (+2 HP regen) |
| `0x0004` | Human: Workhorse (more resources) |
| `0x0008` | Human: Jack of Trades (20.0 floor) |
| `0x0010` | Elf: Night Sight |
| `0x0020` | Elf: harder to track |
| `0x0040` | Elf: Wisdom (+20 mana) |
| `0x0080` | Gargoyle: flying |
| `0x0100` | Gargoyle: Berserk |
| `0x0200` | Gargoyle: Deadly Aim (20.0 floor) |
| `0x0400` | Gargoyle: Mystic Insight (30.0) |

- **Default:** `0`

### ERALIMITGEAR / ERALIMITLOOT / ERALIMITPROPS

Expansion limit: which era's gear, loot and item properties are used (RESDISP number).

- **Type:** integer (0-9; `9` = ToL = "latest")
- **Default:** `9`
- **Note:** A CHARDEF that does not set its own `ERALIMIT*` takes this value.

### DECIMALVARIABLES

How a number TAG/VAR/LOCAL/CTAG reads back in a script and is written to the save: `0` = Sphere hex (10 reads `"0A"`), `1` = decimal.

- **Type:** boolean (0/1)
- **Default:** `0` (Source-X `CServerConfig m_fDecimalVariables`)

### DISPLAYELEMENTALRESISTANCE

Show the elemental resists in the status packet even when `COMBATFLAGS` ELEMENTALENGINE (`0x8`) is off.

- **Type:** boolean (0/1)
- **Default:** `0`
- **Note:** Applies to the status packet (`0x11`).

### OPTIONFLAGS

Extra server option flags (Source-X `OF_*`).

- **Type:** hex/integer bit flags (`0x` prefix required)
  - `0x0000080` OF_FileCommands - enable `FILE.*` in scripts (`FILE.OPEN`, `FILE.READLINE`, `FILE.WRITELINE` ...); this turns on the FILE script object
  - `0x0002000` OF_Buffs - native buff/debuff icons
  - Others (`SphereNet.Core.Enums.OptionFlags`):

| Flag | Name |
|---|---|
| `0x1` | NoDClickTarget |
| `0x2` | NoSmoothSailing |
| `0x4` | ScaleDamageByDurability |
| `0x8` | CommandSysMessages |
| `0x10` | PetSlots |
| `0x20` | OsiMultiSight |
| `0x40` | ItemsAutoName |
| `0x100` | NoItemNaming |
| `0x200` | NoHouseMuteSpeech |
| `0x400` | NoContextMenuLos |
| `0x800` | MapBoundarySailing |
| `0x1000` | FloodProtection |
| `0x4000` | NoPrefix |
| `0x8000` | DyeType |
| `0x10000` | DrinkIsFood |
| `0x20000` | NoDClickTurn |
| `0x40000` | NoPaperdollTradeTitle |
| `0x80000` | NoTargetTurn |
| `0x100000` | StatAllowValueOverMax |
| `0x200000` | GuardOutsideGuardedArea |
| `0x400000` | OverweightNoDropCarriedItem |
| `0x800000` | AllowContainerInsideContainer |
| `0x1000000` | VendorStockLimit |
| `0x2000000` | EnableGuildAlignNotoriety |
| `0x4000000` | NoDClickEquip |
| `0x8000000` | PetBehaviorOwnerNeutral |
| `0x10000000` | NpcMovementOldStyle |

- **Default:** `0`
- **Note:** Define `OPTIONFLAGS` only once; the ini reader keeps the last value.

## MAP CONFIGURATION

Format: `MAP<id>=width,height,sectorSize,mulReadIndex,clientMapId`

- `width`/`height` - map size in tiles, must match the .mul (multiple of 8)
- `sectorSize` - sector grid size, a power of two (usually 64)
- `mulReadIndex` - which `map<N>.mul` / `statics<N>.mul` is read
- `clientMapId` - map id sent to the client

Standard UO maps:

| Key | Map | Size |
|---|---|---|
| `MAP0` | Felucca | 6144x4096 |
| `MAP1` | Trammel | 6144x4096 |
| `MAP2` | Ilshenar | 2304x1600 |
| `MAP3` | Malas | 2560x2048 |
| `MAP4` | Tokuno | 1448x1448 |
| `MAP5` | Ter Mur | 1280x4096 |

### MAP0 / MAP1 / MAP2 / MAP3 / MAP4 / MAP5

Map definitions in the format above. Applied by the world sector setup and map data initialisation.

7168 = the full map0 width including the T2A/Lost Lands (6144 cuts the Lost Lands off; mortechUO teleport defs use x > 6144).

- **Default:** `MAP0=6144,4096,64,0,0` (when no MAPn is given)
- **Note:** `MAP0`..`MAP255` are read. An empty value (`MAPn=`) disables the map; a zero or missing field keeps its default; an invalid size or sector size is reported at startup and the default kept.

### USEMAPDIFFS

Apply the `mapdif*`/`stadif*` patch files over the maps.

- **Type:** boolean (0/1)
- **Default:** `0`

## FILE PATHS

Absolute paths are used as they are. Relative paths are first looked up from the working directory (CWD); if not found there, from the server executable's directory.

### MULFILES

Path of the UO client data files (`map*.mul`, `statics*.mul`, `tiledata.mul` ...).

- **Type:** string (directory)
- **Default:** `""` (empty = look for a `mul` folder next to the working/server directory)
- **Note:** The server refuses to start if the folder cannot be found.

### SCPFILES

Path of the server script files (`.scp`). The script loader scans this folder.

- **Type:** string (directory)
- **Default:** `"scripts/"`
- **Note:** Source-X manifest root: `spheretables.scp` decides the exact load order; folders/files commented out there are deliberately not loaded.

### SCRIPTENCODING

Text encoding of the scripts: `AUTO`, `UTF8` or `LEGACY`. `AUTO` tries UTF-8 first and falls back to `SCRIPTLEGACYCODEPAGE` when invalid.

- **Type:** `AUTO` | `UTF8` | `LEGACY` (anything else = `AUTO` + warning)
- **Default:** `AUTO`

### SCRIPTLEGACYCODEPAGE

Windows code page used by `LEGACY` and by the `AUTO` fallback.

- **Type:** integer (code page; `1254` for old Turkish scripts)
- **Default:** `1252`

### LOG

Log output directory. Used by the Serilog file sink and the Host crash log.

- **Type:** string (directory)
- **Default:** `"logs/"`

### WORLDSAVE

Directory of the world save files (`sphereworld.scp`, `spheredata.scp` ...).

- **Type:** string (directory)
- **Default:** `"save/"`

### ACCTFILES

Directory of the account files (`sphereaccu.scp`).

- **Type:** string (directory)
- **Default:** `"accounts/"`

## SAVE SETTINGS

### SAVEPERIOD

Automatic save interval (minutes).

- **Type:** integer (minutes, `0` = no automatic save)
- **Default:** `20` (same as Source-X)

### FORCEGARBAGECOLLECT

Check/clean up world objects before every new save (Source-X). `0` skips the full scan. Not the .NET GC. The cleanup time is logged separately; the save timer starts after it.

- **Type:** boolean (0/1)
- **Default:** `1`

### SAVEONSHUTDOWN

Save the world on a clean shutdown, so changes since the last periodic save survive a planned stop.

- **Type:** boolean (0/1)
- **Default:** `1`

### SAVESECTORSPERTICK

Sectors saved per tick in a Source-X background save.

- **Type:** integer
- **Default:** `1`
- **Note:** Only read back through `SERV.SAVESECTORSPERTICK`. SphereNet's background save uses a snapshot + worker model (see `SAVEBACKGROUND`); Source-X's per-tick sector staging is not used.

### SAVESTEPMAXCOMPLEXITY

Source-X: the most object complexity one background-save step processes.

- **Type:** integer
- **Default:** `500`
- **Note:** Only read back through `SERV.SAVESTEPMAXCOMPLEXITY`; the snapshot-based save has no step staging.

### SAVEBACKGROUND

Enable background saving.

- **Type:** integer (in Source-X a duration in minutes; in SphereNet any value above 0 means "on")
  - `0` = fully synchronous save (the world pauses for the whole save, ~1s+)
  - `>0` = background save: the main loop only captures an immutable world snapshot (~0.2-0.4s); sharding/compression/writing run on a low-priority worker thread. Saves never overlap, a shutdown save waits for the one in flight, atomic temp+commit is kept.
- **Default:** `0`

### BACKUPLEVELS

Number of backup save generations kept.

- **Type:** integer (`0` = no backup)
- **Default:** `10`
- **Note:** Creates `save_1/`, `save_2/` ...

### SAVEFORMAT

Save file format.

- **Type:** `Text` | `TextGz` | `Binary` | `BinaryGz` (or 0-3)
  - `Text` = classic Sphere .scp text (human readable, large)
  - `TextGz` = `.scp.gz` - the same text, gzipped (~85% smaller)
  - `Binary` = `.sbin` - tag-stream binary (same (key,value) semantics, no schema)
  - `BinaryGz` = `.sbin.gz` - binary + gzip (smallest)
- **Default:** `Text` (unknown value = `Text`)
- **Note:** The loader detects the format from the file extension, so switching does not break older snapshots. Can also be changed at run time with the `.SAVEFORMAT` command.

### SAVESHARDS

Sharding mode.

- **Type:** integer (0-16)
  - `0` = always a single file, rolling off (classic Sphere)
  - `1` = size-based rolling: 1, 2, 3... files by `SAVESHARDSIZEMB`
  - `2-16` = fixed parallel hash shards (N files, each written on its own task)
- **Default:** `3`

### SAVESHARDSIZEMB

Maximum size per file in rolling mode (MB).

- **Type:** integer (MB)
- **Default:** `75` (BinaryGz: ~4-6M items or ~900K-1.8M NPCs per shard)
- **Note:** Only with `SAVESHARDS=1`. Starts with one file and opens `{base}.1.{ext}`, `{base}.2.{ext}` past the limit; small worlds stay in one file (classic Sphere behaviour).

## World Timing

### GAMEMINUTELENGTH

Real-world length of one in-game minute (seconds). Drives the world clock (day/night).

- **Type:** integer (seconds, at least 1)
- **Default:** `20` (same as Source-X)
- **Note:** At 8 a full in-game day (24h) = 8*60*24 = 11520 s, about 3.2 real hours.

### SERVERTICKMS

World-loop tick interval (milliseconds).

- **Type:** integer (ms, clamped to 20-250)
- **Default:** `100` (10 ticks per second, as Source-X)
- **Note:** Network I/O is not gated by this; it runs on every main-loop pass. A script reading `<TICKPERIOD>` gets the ticks per second (`1000/SERVERTICKMS`).

### TICKPERIOD

Server ticks per SECOND, the unit Source-X reports it in (`<SERV.TICKPERIOD>` = 10). Only used when SERVERTICKMS is absent, so it is commented out in the ini: 10 means a 100 ms tick.

- **Type:** integer (ticks per second)
- **Default:** `10`
- **Note:** Takes effect only when SERVERTICKMS is absent.

### SECTORSLEEP

How long (minutes) a sector keeps ticking after the last player leaves it before it goes to sleep.

- **Type:** integer (minutes, `0` = never sleep)
- **Default:** `10` (same as Source-X); this pack uses `1`
- **Note:** A sleeping sector runs no NPC AI or regeneration; item TIMERs fall to the 3-minute maintenance sweep (decay has its own 5 s catch-up pass). The sector the player stands in is stamped with this delay, not the 5x5 window, so the trail a traveller leaves is one sector wide.
- **Note:** `0` is NOT Source-X's meaning. Source-X ticks every sector and leaves the decision to `_CanSleep`; here the tick set is built from the 5x5 window and only sectors awake last tick are asked. So `0` means: a sector once woken never sleeps again, and a sector nobody came near still never ticks. The awake set only grows (measured: 32 -> 137 sectors while one player walks eight areas) and keeps growing. For "almost never sleep" use a small value, not `0`.

### TIMERCALL

Interval of the server "timer" hook (Source-X `f_onserver_timer`).

- **Type:** integer (minutes; seconds when `TIMERCALLUNIT=1`)
- **Default:** `0` = off
- **Note:** The interval (in its own unit) is passed as `ARGN1` (Source-X CWorld.cpp:1831).

### TIMERCALLUNIT

Unit of TIMERCALL.

- `0` = minutes
- `1` = seconds

- **Type:** boolean (0/1)
- **Default:** `0`

### MAPCACHETIME

How long map data blocks stay cached in memory (seconds).

- **Type:** integer (seconds, `0` = no cache)
- **Default:** `120`
- **Note:** Not implemented: the key is not defined in SphereConfig, so it has no effect.

### ZEROPOINT

Origin of sextant coordinates (Lord British's throne). Used for sextant/map coordinates.

- **Type:** string `"x,y[,z[,m]]"`
- **Default:** `1323,1624,0`
- **Note:** A malformed value keeps the current origin.

## View Range & Distance

### MAPVIEWSIZE

Default client view range (tile radius). Objects beyond it are not sent to the client. A client may request its own range with `0xC8` (up to MAPVIEWSIZEMAX). Also used as the combat range while `MAPVIEWRADAR=0`.

- **Type:** integer (tiles, clamped to 1-255)
- **Default:** `18`

### MAPVIEWSIZEMAX

Largest view range a client may request with `0xBF.0x1C` / `0xC8`.

- **Type:** integer (clamped to 5-255)
- **Default:** `24` (same as Source-X)

### MAPVIEWRADAR

Distance (tiles) within which combat memory and NPC pursuit stay valid; a target farther than this is dropped from combat.

- **Type:** integer (tiles)
- **Default:** `31` (UO_MAP_VIEW_RADAR, as Source-X); `0` = MAPVIEWSIZE
- **Note:** `0` is a SphereNet extension meaning MAPVIEWSIZE.

### DISTANCEWHISPER / DISTANCETALK / DISTANCEYELL

Range (tiles) at which a whisper, normal speech and a yell are heard.

- **Type:** integer (tiles)
- **Default:** `3` / `18` / `31` (same as Source-X)

### MAXCOMPLEXITY

Characters a sector may hold before it counts as "too complex".

- **Type:** integer
- **Default:** `32`
- **Note:** Past it a console warning is printed; in a sector past half of it NPCs skip their look-around scan.

### MAXSECTORCOMPLEXITY

Ground items a sector may hold before it warns ("Sector too complex!").

- **Type:** integer
- **Default:** `1024`

## Stats & Skills

### MAXBASESKILL

Upper bound of the random base value Source-X gives every skill of a NEW character before the chosen skills are set (`200` = 0-20.0).

- **Type:** integer (tenths)
- **Default:** `200`
- **Note:** This is NOT a skill cap. The total cap comes from the character's SKILLCLASS SKILLSUMMAX. SphereNet does not roll the random start skills.
- **Note:** Read only: the value is only read back through `SERV.MAXBASESKILL`.

### MAXPOLYSTATS

How far a polymorph may move a stat, in points. The form's own STR/DEX replace the caster's but never by more than this in either direction (Source-X CCharSpell.cpp:1082). Only read while MAGICFLAGS POLYMORPHSTATS is on.

- **Type:** integer (points, 0 or more)
- **Default:** `150`

### MAXFAME

Maximum fame a character can have (fame change cap).

- **Type:** integer
- **Default:** `10000`

### MAXKARMA

Maximum (best) karma.

- **Type:** integer
- **Default:** `10000`

### MINKARMA

Minimum (worst) karma.

- **Type:** integer
- **Default:** `-10000`

### STATSFLAGS

Bit flags controlling stat behaviour (Source-X CServerConfig.h:496, CCharStat.cpp:233).

| Value | Meaning |
|---|---|
| `01` | MAXHITS/MAXMANA/MAXSTAM writes are refused for everyone |
| `02` | ... for players only |
| `04` | ... for NPCs only |

- **Type:** flags (Sphere rule)
- **Default:** `0`
- **Note:** A refused write sends the pool back to the base stat (STR/INT/DEX).

### EXPERIENCESYSTEM

Source-X experience (EXP) system. While off, no combat or craft experience is given at all.

- **Type:** boolean (0/1)
- **Default:** `0`

### EXPERIENCEMODE

Experience gain rules (Source-X `EXP_MODE_*`). `0` freezes experience entirely.

| Value | Name | Meaning |
|---|---|---|
| `01` | RAISE_COMBAT | gain experience in combat |
| `02` | RAISE_CRAFT | gain experience in crafting |
| `04` | ALLOW_DOWN | experience may go down |
| `08` | DOWN_NOLEVEL | a decrease never drops below the current level |
| `010` | AUTOSET_EXP | give EXP/LEVEL to NPCs whose @Create sets none |

- **Type:** flags (Sphere rule)
- **Default:** `0`

### EXPERIENCEKOEFPVP / EXPERIENCEKOEFPVM

Percent of the experience gained from player-vs-player and player-vs-monster kills (kill experience calculation).

- **Type:** integer (%)
- **Default:** `100`

### LEVELSYSTEM

Level (LEVEL) calculation from experience.

- **Type:** boolean (0/1)
- **Default:** `0`

### LEVELMODE

- `0` = linear (each LEVELNEXTAT experience gives a level)
- `1` = doubling (needs `LEVELNEXTAT * (level+1)`)

- **Type:** integer (0/1)
- **Default:** `1`

### LEVELNEXTAT

Experience needed for the next level (see LEVELMODE).

- **Type:** integer (0 or more)
- **Default:** `0` (no level is computed)

## Regeneration

In SECONDS: the time it takes to regain one point (same unit and order as Source-X: REGEN0=STR/hits, REGEN1=INT/mana, REGEN2=DEX/stamina, REGEN3=food). `REGEN0=40` = one HP every 40 seconds. A character's own REGENHITS/REGENMANA/... overrides these; hunger and meditation also affect them.

### REGEN0

Hit point regeneration interval (seconds).

- **Type:** integer (seconds)
- **Default:** `40`

### REGEN1

Mana regeneration interval (seconds). Meditation speeds it up.

- **Type:** integer (seconds)
- **Default:** `20`

### REGEN2

Stamina regeneration interval (seconds).

- **Type:** integer (seconds)
- **Default:** `10`

### REGEN3

Hunger: time for the food value to drop one point (seconds).

- **Type:** integer (seconds)
- **Default:** `3600` (= 1 hour)
- **Note:** May be written as a product, as the Source-X reference ini does (`60*60*24`).

## Combat

### COMBATFLAGS

Bit flags controlling combat (Source-X `COMBAT_*`).

| Value | Name | Meaning |
|---|---|---|
| `0x00001` | NODIRCHANGE | do not turn while attacking |
| `0x00002` | FACECOMBAT | face the target while attacking |
| `0x00004` | PREHIT | first swing without delay (not with SWING_NORANGE) |
| `0x00008` | ELEMENTALENGINE | elemental damage/resist system |
| `0x00020` | DCLICKSELF_UNMOUNTS | double-click self in combat dismounts |
| `0x00040` | ALLOWHITFROMSHIP | can be hit from a ship |
| `0x00080` | NOPETDESERT | pets do not desert their owner |
| `0x00100` | ARCHERYCANMOVE | archers can shoot while moving |
| `0x00200` | STAYINRANGE | a swing requires staying in range |
| `0x01000` | STACKARMOR | armour on the same layer stacks |
| `0x02000` | NOPOISONHIT | poisoned weapons do not poison on hit |
| `0x04000` | SLAYER | slayer system |
| `0x08000` | SWING_NORANGE | a swing completes out of range |
| `0x10000` | ANIM_HIT_SMOOTH | hit animation spread over the swing |
| `0x20000` | FIRSTHIT_INSTANT | first hit instant |
| `0x40000` | NPC_BONUSDAMAGE | NPCs get damage bonuses too |
| `0x80000` | PARALYZE_CANSWING | keep attacking while paralysed |
| `0x100000` | ATTACK_NOAGGREIVED | attacking a murderer is not a crime |

- **Type:** hex/integer (`0x` prefix required; a Source-X style `040000` is read as DECIMAL here)
- **Default:** `0`
- **Note:** PREHIT and SWING_NORANGE cannot coexist; SWING_NORANGE is cleared with a startup warning.

### COMBATDAMAGEERA

Damage formula era (same values as Source-X).

- `0` = Sphere custom
- `1` = pre-AOS
- `2` = AOS

- **Type:** integer (0-2)
- **Default:** `0`

### COMBATHITCHANCEERA

Hit chance formula era (same values as Source-X).

- `0` = Sphere custom (scaled by Hit/Defense Chance Increase; sleeping or frozen target is hit at least 80%)
- `1` = pre-AOS (not compatible with Hit Chance Increase)
- `2` = AOS

- **Type:** integer (0-2)
- **Default:** `0`

### COMBATHITCHANCELEGACYERA0

SphereNet key. Restores the 0.56 / older Source-X era-0 hit roll: the chance is a random draw `rand(chance)`, a sleeping or frozen target draws `rand(10)`, and HCI/DCI are ignored. `0` = current Source-X formula (the computed chance is used).

- **Type:** boolean (0/1)
- **Default:** `0`

### COMBATSPEEDERA

Swing speed formula era (same values as Source-X).

- `0` = Sphere custom (uses DEX; recommended SPEEDSCALEFACTOR 15000)
- `1` = pre-AOS (uses STAM; recommended 15000)
- `2` = AOS (Swing Speed Increase; recommended 40000)
- `3` = SE (recommended 80000)
- `4` = ML (does not use SPEEDSCALEFACTOR)

- **Type:** integer (0-4)
- **Default:** `0`

### COMBATPARRYINGERA

Parry behaviour flags (Source-X `PARRYERA_*`).

| Value | Name | Meaning |
|---|---|---|
| `0x01` | PRESEFORMULA | pre-SE parry chance (no Bushido); not with 0x02 |
| `0x02` | SEFORMULA | SE formula (Bushido counts; needs FEATURESE 0x02) |
| `0x10` | SHIELDBLOCK | parry with a shield |
| `0x20` | ONEHANDBLOCK | parry with a one-handed weapon, no shield |
| `0x40` | TWOHANDBLOCK | parry with two-handed weapons |
| `0x80` | ARSCALING | shield AR scales with Parrying |

- **Type:** flags, Sphere rule (leading zero = hex, `|` ORs values, e.g. `01|010`)
- **Default:** `01|010` (PRESEFORMULA | SHIELDBLOCK, Source-X default)
- **Note:** CAUTION: `0` opens no parry path at all - nobody can parry.

### COMBATARCHERYMOVEMENTDELAY

Delay after moving before an archer can shoot, in TENTHS of a second (`10` = 1 s), as Source-X reads it (internally ms = value * 100).

- **Type:** integer (tenths of a second; `0` = no delay)
- **Default:** `10` (Source-X; SE 0.25 s / AOS 0.5 s / pre-AOS 1.0 s)
- **Note:** COMBATFLAGS ARCHERYCANMOVE (`0x100`) or the character's ARCHERCANMOVE flag removes the delay.

### COMBATMELEEMOVEMENTDELAY

Delay after moving before a melee swing may start (ms). SphereNet-specific; Source-X has no counterpart.

- **Type:** integer (ms, `0` = no delay)
- **Default:** `0`

### ATTACKERTIMEOUT

Time after which inactive combat memory (the attacker list) is cleared.

- **Type:** integer (seconds, `0` = never)
- **Default:** `30` (Source-X default; its reference ini 300)

### ARCHERYMINDIST

Minimum range for archery attacks (tiles), when the weapon's own RANGE does not say otherwise.

- **Type:** integer (tiles)
- **Default:** `2` (same as Source-X)

### ARCHERYMAXDIST

Maximum range for archery attacks (tiles), when the weapon's own RANGE does not say otherwise.

- **Type:** integer (tiles)
- **Default:** `15` (same as Source-X)

### SPELLTIMEOUT

Time a player has to pick a spell target (seconds). An expired cursor is cancelled and the client is told to close it too. A cursor without a timeout is not harmless: it stays armed for the rest of the session, and the next thing the player clicks - a door, a friend - answers a spell cast minutes ago. The character's own `TAG.SPELLTIMEOUT` overrides it (CClientUse.cpp:1061).

- **Type:** integer (seconds)
- **Default:** `0` = no timeout

### MAGICUNLOCKDOOR

How hard a MAGICALLY locked door is to pick. The reference ini calls it "lockpicking skill needed", but the code uses it as a one-in-N CHANCE rolled before the skill is consulted at all (CItem.cpp:5428). Ordinary locks are not affected.

- `0` = such a door needs its key and cannot be picked
- `-1` = no special rule; a magic door picks like any other

- **Type:** integer
- **Default:** `900`

### MEDITATIONMOVEMENTABORT

Does taking a step cancel meditation? The surprising half is the default: the reference LETS a meditating character walk; only with this key set does the skill fail (CCharAct.cpp:2495). Before 2026-09-14 SphereNet cancelled it unconditionally - stricter than the reference.

- **Type:** boolean (0/1)
- **Default:** `0`

### NORESROBE

Do NOT give a robe to a resurrected player. A separate setting from the ghost's death shroud (CCharSpell.cpp:503); tying the two together would mean a shard wanting visible ghosts must resurrect people naked.

- **Type:** boolean (0/1)
- **Default:** `0` (the robe is given)

### HITSUPDATERATE

Interval of HP bar updates to other clients (tenths of a second).

- **Type:** integer
- **Default:** `30` (= 3.0 s)
- **Note:** Not implemented: the key is not defined in SphereConfig, so it has no effect.

### SPEEDSCALEFACTOR

Divisor of the swing speed formula (see COMBATSPEEDERA).

- **Type:** integer (at least 1)
- **Default:** `15000`

## Magic

### MAGICFLAGS

Bit flags controlling magic.

Bits `0x1`-`0x10000` are Source-X `MAGICF_*` (SphereNet.Core.Enums.MagicConfigFlags):

| Value | Name |
|---|---|
| `0x0000001` | NODIRCHANGE |
| `0x0000002` | PRECAST |
| `0x0000004` | IGNOREAR |
| `0x0000008` | CANHARMSELF |
| `0x0000010` | STACKSTATS |
| `0x0000020` | FREEZEONCAST |
| `0x0000040` | SUMMONWALKCHECK |
| `0x0000080` | NOFIELDSOVERWALLS |
| `0x0000100` | NOANIM |
| `0x0000200` | OSIFORMULAS |
| `0x0000400` | NOCASTFROZENHANDS |
| `0x0000800` | POLYMORPHSTATS |
| `0x0001000` | OVERRIDEFIELDS |
| `0x0002000` | CASTPARALYZED |
| `0x0004000` | NOREFLECTOWN |
| `0x0008000` | DELREFLECTOWN |
| `0x0010000` | NOINTERRUPT |

SphereNet additions:

| Value | Meaning |
|---|---|
| `0x0020000` | no LOS needed to the target |
| `0x0040000` | polymorph reverts on death |
| `0x0080000` | outdoor spells work in dungeons |
| `0x0100000` | gate disabled |
| `0x0200000` | recall disabled |
| `0x0400000` | mark disabled |
| `0x0800000` | summon limit |
| `0x1000000` | gate opens on both sides |
| `0x2000000` | no reveal on cast |
| `0x4000000` | dispel kills summons |

- **Type:** hex/integer (`0x` prefix required)
- **Default:** `0`

### REAGENTSREQUIRED

Casting requires reagents.

- `0` = no reagents needed (free casting)
- `1` = reagents are consumed on cast (standard UO)

- **Type:** boolean (0/1)
- **Default:** `0` (same as Source-X)
- **Note:** Wands, scrolls and GM+ bypass it.

### SPELLBOOKREQUIRED

Casting requires a spellbook holding the spell.

- **Type:** boolean (0/1)
- **Default:** `1`

### EQUIPPEDCAST

Allow casting with a weapon equipped.

- `0` = the weapon must be removed to cast (classic UO)
- `1` = casting with a weapon equipped is allowed

- **Type:** boolean (0/1)
- **Default:** `1` (same as Source-X)

### REAGENTLOSSABORT

Lose the reagents when a cast is aborted.

- **Type:** boolean (0/1)
- **Default:** `0`

### REAGENTLOSSFAIL

Lose the reagents on a fizzle.

- **Type:** boolean (0/1)
- **Default:** `0`

### MANALOSSABORT

Lose mana when a cast is aborted.

- **Type:** boolean (0/1)
- **Default:** `0`

### MANALOSSFAIL

Lose mana on a fizzle.

- **Type:** boolean (0/1)
- **Default:** `0`

### MANALOSSPERCENT

Percent of the mana cost actually spent (`50` = half cost, `0` = free).

- **Type:** integer (%)
- **Default:** `50` (same as Source-X)

### WOPPLAYER / WOPSTAFF

Players / staff (Counselor+) speak the Words of Power while casting.

- **Type:** boolean (0/1)
- **Default:** `WOPPLAYER=1`, `WOPSTAFF=0`

### WOPCOLOR / WOPFONT / WOPTALKMODE

- **WOPCOLOR** - Hue of the Words of Power (`0` = the speaker's own hue).
- **WOPFONT** - Font of the Words of Power (`0` = default font 3).
- **WOPTALKMODE** - Talk mode of the Words of Power (`10` = TALKMODE_SPELL; values outside 1-14 become 10).

- **Type:** integer (`0x` prefix or leading zero = hex)
- **Default:** `WOPCOLOR=0x03B2`, `WOPFONT=3`, `WOPTALKMODE=10`
- **Note:** A spell's own @SpellCast locals (`WOPColor`/`WOPFont`) override these.

## NPC

### NPCAI

AI flags applied to every NPC (Source-X `NPC_AI_*`); a character's own `OVERRIDE.NPCAI` overrides it.

| Flag | Name | Meaning |
|---|---|---|
| `0x0001` | PATH | pathfinding (A*) |
| `0x0002` | FOOD | NPCs look for food |
| `0x0004` | EXTRA | extra behaviours |
| `0x0008` | ALWAYSINT | every NPC counts as intelligent |
| `0x0010` | INTFOOD | intelligent NPCs look for food |
| `0x0040` | COMBAT | advanced combat decisions |
| `0x0080` | VENDTIME | vendors keep business hours |
| `0x0100` | LOOTING | monsters pick up loot |
| `0x0200` | MOVEOBSTACLES | push obstacles |
| `0x0400` | PERSISTENTPATH | keep a found path |
| `0x0800` | THREAT | pick targets from a threat list |

- **Type:** hex/integer (`0x` prefix or leading zero = hex)
- **Default:** `0x0C41` (PATH | COMBAT | PERSISTENTPATH | THREAT) when the key is absent; bare Source-X default is `0`.
- **Note:** CAUTION: this pack writes `0`, so pathfinding and threat tracking are off along with everything else (bare Source-X behaviour).

### NPCAIEXTRAS

Optional NPC behaviours that Source-X does NOT have. `0` = none: the NPCs behave as in Source-X. Add the bits you want (`|` ORs them).

| Flag | Name | Meaning |
|---|---|---|
| `01` | SMARTCASTER | spells chosen by role (cure, heal, dispel, debuff, area, damage, summon) from the spell list and spell flags |
| `02` | LOWHPRETREAT | a badly hurt melee NPC steps back |
| `04` | ALLYRALLY | nearby NPCs of the same kind join a fight |
| `08` | SURROUNDFLANK | attackers spread around the target |
| `010` | FLEETACTICS | a fleeing NPC casts, heals or hides |
| `020` | HIDDENPURSUIT | a target that hides is followed and revealed |
| `040` | LOSRECOVERY | out of sight: switch to a visible foe or blink |
| `080` | DETECTHIDDEN | guards and Detect Hidden NPCs search for hidden players |
| `0100` | ANIMALBACKOFF | timid animals back away from anyone in war mode |
| `0200` | FIGHTRESCAN | fighting NPCs re-score targets now and then |
| `0400` | HURTSLOWDOWN | badly hurt NPCs move more slowly |
| `0800` | PETKEEPPACE | a following pet keeps up with a running or mounted owner |
| `01000` | RETURNHOME | strays are sent home (through `@NPCLostTeleport`) when their sector sleeps or the walk home keeps failing |
| `02000` | BANDAGEHEAL | NPCs with Healing and bandages treat themselves (pets: their owner too) |
| `04000` | IDLEFLAVOR | cosmetic idle animations |
| `08000` | COMBATEXTRAS | other combat additions beyond Source-X |

- **Type:** flags, Sphere rule (leading zero = hex, `|` ORs values)
- **Default:** `0`
- **Note:** `TAG.OVERRIDE.NPCAIEXT` on a character overrides this mask for that NPC.

### NPCHEALTHRESHOLD

HP percent at which an NPC decides to heal itself; also handed to `@NPCActCast` as `LOCAL.HealThreshold`.

- **Type:** integer (%, `0` = 30)
- **Default:** `30`

### NPCWANDERLOOKAROUNDCHANCE

Chance that an idle wandering NPC scans around on a step.

- **Type:** integer (%)
- **Default:** `30` (same as Source-X)
- **Note:** Source-X raises the value to at least 100 before rolling 0-99 (CCharNPCAct.cpp:1247), so this scan never runs there either; the wander AI here matches that.
- **Note:** Only read back through `SERV.NPCWANDERLOOKAROUNDCHANCE`.

### NPCTRAINCOST

Gold per 1.0 skill point when training from an NPC.

- **Type:** integer (gold)
- **Default:** `30`
- **Note:** Only read back through `SERV.NPCTRAINCOST`; the training price comes from `TRAINSKILLCOST`.

### TRAINSKILLCOST

Gold per 0.1 skill point in NPC training.

- **Type:** integer (gold, 0 or more)
- **Default:** `1`

### NPCTRAINMAX / TRAINSKILLMAX

Highest skill value (tenths) an NPC can train a player to.

- **Type:** integer (0 or more)
- **Default:** `420` (= 42.0)
- **Note:** The older SphereNet name `TRAINSKILLMAX` is accepted too; this one wins when both are present. The trainer's `OVERRIDE.TRAINSKILLMAX` tag overrides it.

### REVEALFLAGS

Which actions break concealment.

| Flag | Name | Meaning |
|---|---|---|
| `0001` | DETECTINGHIDDEN | Detecting Hidden really reveals |
| `0002` | LOOTINGSELF | reveal when looting your own corpse |
| `0004` | LOOTINGOTHERS | reveal when looting another's corpse |
| `0008` | SPEAK | reveal when speaking (`TAG.OVERRIDE.NOREVEALSPEAK` exempts) |
| `0010` | SPELLCAST | reveal when a spell starts |
| `0020` | OSILIKEPERSONALSPACE | INVERTED: do NOT reveal when someone steps on you |
| `0040` | SNOOPING | INVERTED: do NOT reveal when starting to snoop |
| `0080` | STEALING | INVERTED: do NOT reveal when starting to steal |
| `0100` | STEALING_SUCCESS | reveal if a theft SUCCEEDS |
| `0200` | STEALING_FAIL | reveal if a theft FAILS |
| `0400` | ONHORSE | stealth steps on a horse reveal |

- **Type:** flags (combined with `|`; a LEADING ZERO IS HEX: `010` = 16)
- **Default:** `01|02|04|08|010|040|080|0200` (what the reference ini ships)
- **Note:** CAUTION: three flags (`0020`/`0040`/`0080`) work OPPOSITE to their name; reading them all the same way turns half of a shard's stealth rules upside down.

### HITSHUNGERLOSS

HP a starving character loses on each food tick.

- **Type:** integer (a FLAT amount)
- **Default:** `0` = off
- **Note:** The reference ini says "% of hits"; the code gives a flat amount (DAMAGE_FIXED, CCharAct.cpp:5788) — the code is followed. The reference ini ships this line commented out. Staff, sleepers and the dead go hungry at no cost.

### SKILLPRACTICEMAX

Highest skill a training dummy or archery butte can raise.

- **Type:** integer (tenths: `300` = 30.0)
- **Default:** `300`

### WOOLGROWTHTIME

Time for a shorn sheep's wool to grow back (MINUTES).

- **Type:** integer (minutes)
- **Default:** `30`

### OVERSKILLMULTIPLY

How far past their class limit a player's skills/stats may drift before being pulled back to it AT LOGIN. A repair pass, not a cap: the limit is already enforced on the gain path; this catches a hand-edited save, a script that overshot, or a class limit lowered on a live shard (CChar.cpp:997). Staff are exempt.

- **Type:** integer (multiplier; 0 or less = check off)
- **Default:** `2`

### NPCTRAINPERCENT / TRAINSKILLPERCENT

Percent of an NPC's own skill it can teach.

- **Type:** integer (0-100)
- **Default:** `30`
- **Note:** The older SphereNet name `TRAINSKILLPERCENT` is accepted too; this one wins when both are present.

### NPCDISTANCEHEAR

Distance (tiles) at which an NPC "hears" speech.

- **Type:** integer (tiles)
- **Default:** `14` (Source-X `UO_MAP_VIEW_SIGHT`)
- **Note:** A negative value uses its magnitude as the radius and drops the line of sight requirement.

### NPCHEARTHROUGHWALLS

Words an NPC hears even when it cannot see the speaker (comma separated). Empty = upstream behaviour: the sight test applies to every keyword. Default "bank": a banker inside its building answers from the street, which is what a player expects of a bank, while "buy" and the rest stay behind the wall so a street full of shopkeepers does not answer two people chatting. A deliberate deviation from upstream.

Applies to the NPC keyword sight test in speech handling.

- **Type:** string (comma separated words)
- **Default:** `bank`

### PAYFROMPACKONLY

Where a character's money is taken from when paying.

- `0` = from everything the character CARRIES; the bank box counts too because it is worn, so banked gold is spendable and shown on the status bar. The pack is spent first, so loose change goes before the bank is touched.
- `1` = the backpack only; banked gold cannot be spent.

- **Type:** boolean (0/1)
- **Default:** `0` (same as upstream)

### NPCSKILLSAVE

While the world is READ, NPC skills below this value are dropped; despite its name it is not a save key (Source-X FixWeirdness, CChar.cpp:1030). Player skills are untouched; `0` keeps everything.

- **Type:** integer (tenths: `100` = 10.0)
- **Default:** `10` (= 1.0)
- **Note:** This pack uses `100`: NPC skills below 10.0 are dropped.

### MOVERATE

Default move rate of all NPCs, as a percent. HIGHER IS SLOWER: the tick interval is multiplied by it (CCharNPCAct.cpp:639). A CHARDEF's own `MOVERATE` overrides it; this is what definitions without one start from (CCharBase.cpp:37). The one place to slow every creature.

- **Type:** integer (%)
- **Default:** `100`

### MONSTERFEAR

Monsters flee at low HP.

- **Type:** boolean (0/1)
- **Default:** `0` (same as Source-X)

### MONSTERFIGHT

Monsters fight each other (NPCs of other kinds). While off a non-berserk NPC does not pick a non-player target.

- **Type:** boolean (0/1)
- **Default:** `0`

### NPCCANFIZZLEONHIT

Can an NPC's spell be disturbed by a hit, like a player's? While off only players' spells are disturbed (Source-X CCharFight.cpp:881).

- **Type:** boolean (0/1)
- **Default:** `1` (Source-X: `0`)

### NPCNOFAMETITLE

NPCs never get the Lord/Lady fame title.

- **Type:** boolean (0/1)
- **Default:** `0` (the Source-X reference ini writes `1`)

### VENDORTRADETITLE

Add the vendor's trade ("the blacksmith") to the name shown on a single click (the single-click label).

- **Type:** boolean (0/1)
- **Default:** `1`

### ALLOWBUYSELLAGENT

Accept a buy/sell confirmed faster than the list could be read (buy/sell agent programs). Applies to vendor buy/sell.

- **Type:** boolean (0/1)
- **Default:** `0` (refused)

## GLOBAL SCRIPT HOOKS

### SPEECHSELF

SPEECH block(s) evaluated when a player speaks. Matching `[SPEECH]` patterns run before the built-in player keywords.

- **Type:** comma-separated SPEECH defnames
- **Default:** `""` (Source-X example: `spk_player`)

### SPEECHPET

SPEECH block(s) evaluated when an NPC/pet hears speech. Runs after the built-in service keywords, before the CHARDEF speech chain finishes.

- **Type:** comma-separated SPEECH defnames
- **Default:** `""` (Source-X example: `spk_pet`)

### EVENTSPET

Global EVENTS applied to all non-player characters.

Trigger chain: `EVENTS -> TEVENTS -> CHARDEF -> EventsPet`.

- **Type:** comma-separated EVENTS defnames
- **Default:** `""` (Source-X example: `e_npc_generic_event`)

### EVENTSNPC / EVENTSMONSTER / EVENTSANIMAL / EVENTSMOUNTABLE / EVENTSSHOP

Sphere 56T custom version: per-kind NPC EVENTS lists, run after `EventsPet`.

- `EventsNpc` - every NPC
- `EventsMonster` - monster, berserk, dragon brain
- `EventsAnimal` - animal brain
- `EventsMountable` - a body that can be ridden
- `EventsShop` - vendors

A 56T pack makes monster loot in `EventsMonster` (`e_npc_monster @DeathCorpse`).

- **Type:** comma-separated EVENTS defnames
- **Default:** `""` (off). Source-X has only `EventsPet`.

### EVENTSPLAYER

Global EVENTS applied to all player characters.

Trigger chain: `EVENTS -> TEVENTS -> CHARDEF -> EventsPlayer`.

- **Type:** comma-separated EVENTS defnames
- **Default:** `""` (Source-X example: `e_player_generic_event,e_player_crafting_event`)

### EVENTSREGION

Optional global EVENTS added to region and room event dispatch.

- **Type:** comma-separated EVENTS defnames
- **Default:** `""` (off)

### EVENTSITEM

Optional global EVENTS applied to all items.

- **Type:** comma-separated EVENTS defnames
- **Default:** `""` (off)

### COMMANDTRIGGER

Global function called before every command (Source-X `f_oncommand`); `ARGS` = the command line, `RETURN 1` consumes the command. Lets a pack add or override commands.

- **Type:** string (FUNCTION name)
- **Default:** `""` (off)
- **Example:** `CommandTrigger=f_oncommand`

## CRIME & NOTORIETY

### CRIMINALTIMER

How long a character stays criminal (grey) after a crime (MINUTES, the same unit as Source-X). The flag is cleared when it expires.

- **Type:** integer (minutes)
- **Default:** `3`

### MURDERMINCOUNT

Murder count at which a player turns red (murderer). Every "innocent" kill adds one; red means kills > this value.

- **Type:** integer
- **Default:** `5`

### MURDERDECAYTIME

Time for one murder count to decay (seconds). One kill is removed per period.

- **Type:** integer (seconds)
- **Default:** `28800` (= 8 real hours)

### PLAYEREVIL / PLAYERNEUTRAL

- `PLAYEREVIL` - Karma below which a player with no murders shows red (evil).
- `PLAYERNEUTRAL` - Karma below which such a player shows grey (neutral).

Used for notoriety.

- **Type:** integer (karma)
- **Default:** `-8000` / `-2000` (same as Source-X)

### LOOTINGISACRIME

Looting another player's corpse is a crime.

- **Type:** boolean (0/1)
- **Default:** `1`

### ATTACKINGISACRIME

Attacking an innocent player is a crime. Attacking an innocent target in a guarded area makes the attacker criminal; a red/grey target counts as self-defence.

- **Type:** boolean (0/1)
- **Default:** `1`

### HELPINGCRIMINALSISACRIME

Helping a criminal (healing/buffing) is a crime.

- **Type:** boolean (0/1)
- **Default:** `1` (same as Source-X)

### GUARDLINGER

How long a summoned guard stays (MINUTES). Source-X reads this key as minutes (CServerConfig.cpp:1292: value * 60 * MSECS_PER_SEC), so `GUARDLINGER=3` in an old ini means three minutes there as well. A summoned guard is removed when it expires.

- **Type:** integer (minutes)
- **Default:** `3` (Source-X CServerConfig.cpp:173)

### GUARDSINSTANTKILL

Guards kill criminals instantly instead of fighting.

- `0` = guards fight normally (strong players can kill them)
- `1` = guards kill the target instantly (Source-X default)

- **Type:** boolean (0/1)
- **Default:** `1`

### GUARDSONMURDERERS

Guards attack murderers (red) on sight; this covers murderers and their pets.

- **Type:** boolean (0/1)
- **Default:** `1`

### SNOOPCRIMINAL

Percent chance (0-100) that a witnessed snoop counts as a crime (Source-X `m_iSnoopCriminal`).

- **Type:** number (0-100)
- **Default:** `100`

### TRADEWINDOWSNOOPING

Opening a container inside a trade window counts as snooping.

- **Type:** boolean (0/1)
- **Default:** `1` (`0` = opened without a snoop check)

### NOTOTIMEOUT

Expiry of a notoriety query flag (seconds).

- **Type:** integer (seconds)
- **Default:** `30`
- **Note:** Only read back through `SERV.NOTOTIMEOUT`; notoriety has no expiry tracking.

## DEATH & RESURRECTION

### PACKETDEATHANIMATION

Send the 0x2C death-screen packet to a dying client. The ghost view is refreshed either way.

- **Type:** integer (`0` = do not send, `1` = send)
- **Default:** `1` (same as Source-X)

### CORPSENPCDECAY

NPC corpse decay time (minutes).

- **Type:** integer (minutes)
- **Default:** `7`

### CORPSEPLAYERDECAY

Player corpse decay time (minutes). When the corpse decays the items inside drop to the ground.

- **Type:** integer (minutes)
- **Default:** `7`

### HITPOINTPERCENTONREZ

Percent of max HP given back on resurrection.

- **Type:** integer (%)
- **Default:** `10`

### DEADCANNOTSEELIVING

Ghosts cannot see the living (Source-X mode).

- `0` = ghosts see everything (standard)
- `1` = a ghost cannot see living NPCs
- `2` = as 1, and NPCs cannot see living players they do not belong to

- **Type:** integer (0-2; an old "true/yes" counts as 1)
- **Default:** `0`

## Container limits

### CONTAINERMAXITEMS

Maximum items in a single container (backpack, chest, bag...). Past the limit, a dropped item bounces back to the backpack with "That container is full."

- **Type:** integer
- **Default:** `255` (Source-X `MAX_ITEMS_CONT`)

### BANKMAXITEMS

Maximum items in the bank box. Checked when an item is dropped on the bank box (the bank box layer).

- **Type:** integer
- **Default:** `1000` (same as Source-X)

### BANKMAXWEIGHT

Maximum weight in the bank box (stones). The total weight is checked on a bank drop.

- **Type:** integer (stones)
- **Default:** `1000` (same as Source-X)

### CONTAINERMAXWEIGHT

Weight an ordinary container (chest, bag) holds (stones). Checked when dropping into a container.

- **Type:** integer (stones)
- **Default:** `0` = no limit of its own
- **Note:** The container's own `MODMAXWEIGHT` always comes first; this is only an optional global used when the container says nothing. The reference has no such global (`CItemContainer.cpp:906`). A player's own backpack is bound by carry weight + `BACKPACKOVERLOAD` instead.

### BACKPACKOVERLOAD

How far over their carry weight a player may load their own backpack (stones). Not an on/off switch: the pack holds what its owner can carry plus this (Source-X `CItemContainer.cpp:907`). The reference ini's own note: raising it BUYS `STAMINALOSSOVERWEIGHT` - every step past the limit burns stamina.

- **Type:** integer (stones; below zero = no limit of its own)
- **Default:** `40`

**The cost of a load** (Source-X `CCharAct.cpp:4787-4829`): the following four settings are paid ONLY on a committed step: a pathfinding probe, a check-only call and a GM pay nothing.

### STAMINALOSSATWEIGHT

Load percent at which a step starts costing stamina. Not a threshold but the MIDPOINT of an S-curve: at this load half the steps cost a point, the chance falls fast below it and climbs fast above it (variance 10). `200` = effect off, because past 100% the overweight branch takes over.

- **Type:** integer (%)
- **Default:** `150`

### STAMINALOSSOVERWEIGHT

Stamina cost of each step taken OVER the carry weight. This branch is not a chance but CERTAIN: paid every step and growing - +1 per 5 stones over the limit, a third of the total when mounted.

- **Type:** integer
- **Default:** `5`

### RUNNINGPENALTY

Points added to the load percent while flying/hovering. The name says running, the code checks `STATF_FLY | STATF_HOVERING` (`CCharAct.cpp:4799`) - the reference's own comment there calls the flat addition a FIXME. The behaviour is matched, not the name: running on foot costs nothing extra.

- **Type:** integer (`0` = no penalty)
- **Default:** `50`

### RUNNINGPENALTYOVERWEIGHT

Percent uplift of the overweight cost while flying/hovering.

DELIBERATE DIVERGENCE: the reference's key table ties this name to its NEIGHBOUR's field (`CServerConfig.cpp:981-982`), so setting it there moves `RUNNINGPENALTY` and the overweight uplift never leaves its default. That is a copy-paste slip in a table, not a contract: the consumer reads the field by name (`CCharAct.cpp:4823`). The key is wired to the field it names.

- **Type:** integer (%, `0` = no uplift)
- **Default:** `100` (the reference ini ships `10`)

### DRAGWEIGHTMAX

Load percent above which a player may not pick an item up. It is measured on the load AFTER the lift (`CCharAct.cpp:2932`), so the item that would cross the line is the one refused. The escape hatch matters as much as the rule: an item in the lifter's OWN pack is not refused but drops at their feet - otherwise an overloaded player could never put anything down.

- **Type:** integer (%; `0` or below = no limit)
- **Default:** `300`

### MAXITEMCOMPLEXITY

How many copies one `DUPE` line may make of a TOP-LEVEL item; the ceiling on what may be piled on one square (Source-X `CItem.cpp:3636`). A copy inside a container is not bound by it.

- **Type:** integer
- **Default:** `25`

### ITEMSMAXAMOUNT

Maximum amount of a stackable item (gold, reagents...). Applies to stack merging and `AMOUNT` writes.

- **Type:** integer
- **Default:** `60000`

### ALLOWNEWBTRANSFER

Newbie (`ATTR_NEWBIE`) items keep the flag when handed to an NPC, and an owner may take newbie items back off their pet.

- **Type:** boolean (`0`/`1`)
- **Default:** `0`

### VERBOSEITEMBOUNCE

Tell the player where a bounced item went (the `msg_itemplace` message).

- **Type:** boolean (`0`/`1`)
- **Default:** `0`

## Housing & ships

### MAXHOUSESPLAYER

Maximum houses one character may own. The character limit is checked on house placement.

- **Type:** integer (`0` = no houses)
- **Default:** `1`

### MAXHOUSESACCOUNT

Maximum houses per account (all characters together). Every character's houses on the account are counted.

- **Type:** integer
- **Default:** `1`

### MAXHOUSESGUILD

Houses a guild may OWN; a stone that does not set its own `MAXHOUSES` takes this value (Source-X `CItemStone.cpp:24`).

- **Type:** integer (`0` = a guild owns no house)
- **Default:** `1`

### MAXSHIPSPLAYER

Maximum ships one character may own.

- **Type:** integer (negative = unlimited)
- **Default:** `1`

### MAXSHIPSACCOUNT

Maximum ships per account.

- **Type:** integer (negative = unlimited)
- **Default:** `1`

### MAXSHIPSGUILD

Ships a guild may own; a stone that does not set its own value takes this one.

- **Type:** integer (0 or more)
- **Default:** `1`

### MAXSHIPPLANKTELEPORT

Farthest distance (tiles) a ship plank looks along its facing for a shore landing spot.

- **Type:** integer (tiles)
- **Default:** `18`

### AUTOHOUSEKEYS

Generate a key when a house is placed and check keys at its doors. While off no key is handed out and the doors answer to the house privilege alone.

- **Type:** boolean (`0`/`1`)
- **Default:** `1`

### AUTOSHIPKEYS

Generate a key when a ship is placed.

- **Type:** boolean (`0`/`1`)
- **Default:** `1`

### AUTONEWBIEKEYS

Generated house and ship keys are newbie (not dropped on death).

- **Type:** boolean (`0`/`1`)
- **Default:** `1`

## Accounts

### ACCAPP

Create unknown accounts automatically at login.

- **Type:** integer (Source-X `ACCAPP_*` values; SphereNet treats every non-zero value as "on")
  - `0` = only existing accounts can log in (recommended for production)
  - non-0 = an account is created on the first login attempt
- **Default:** `2` when the key is absent (on; a startup warning is printed)

### MAXCHARSPERACCOUNT

Maximum character slots per account. Used by the account manager and the client character list.

- **Type:** integer (clamped to 1-7)
- **Default:** `5` (same as Source-X)
- **Note:** UO clients support at most 7 character slots.

### MINCHARDELETETIME

Minimum age a character must reach before it can be deleted (SECONDS, the same unit as Source-X). Prevents accidental deletion. Counselor and higher accounts do not wait. The char-select delete request checks this age.

- **Type:** integer (seconds; may be written as a product: `7*24*60*60`)
- **Default:** `7*24*60*60` (= 7 days); `0` = delete at once

### MD5PASSWORDS

Store account passwords as an MD5 hash.

- **Type:** boolean (`0`/`1`)
  - `0` = plain-text passwords (insecure but compatible with every tool)
  - `1` = unsalted MD5 digest (weak: fast to brute-force, same password = same digest)
- **Default:** `0` (Source-X); with `0` a startup warning is printed
- **Note:** A shard whose account file already holds MD5 digests must keep `1`. Neither value is strong storage (Source-X offers no salted form).

### AUTORESDISP

Set the account's resolution/display type from the client version. The client version/era is turned into ResDisp and stored on the account.

- **Type:** boolean (`0`/`1`)
- **Default:** `1`

### AUTOPRIVFLAGS

PRIV flags a newly created account starts with (Source-X `PRIV_*`).

- **Type:** flags (Sphere rule)
- **Default:** `0` (reference ini example: `040`)

### GUESTSMAX

Number of `GUESTn` guest accounts a "GUEST" login may use.

- **Type:** integer (0 or more)
- **Default:** `0` (guest login off)

### CLIENTLOGINMAXTRIES

Failed login attempts on one account from one address within 15 s before the address is banned for a while.

- **Type:** integer (0 or more)
- **Default:** `0` (off)

### CLIENTLOGINTEMPBAN

Length of that temporary ban (MINUTES).

- **Type:** integer (minutes)
- **Default:** `3`

### PACKET<n> / OUTPACKET<n>

Script packet filters (x = opcode in DECIMAL, 0..254).

```
PACKET114=f_filter_war        runs f_filter_war for every incoming 0x72 packet
OUTPACKET28=f_filter_speech   runs f_filter_speech for every outgoing 0x1C packet
```

The function runs on the server object with:

- `ARGN1` = opcode
- `ARGS` = client IP
- `ARGO` = the client
- `LOCAL.CONNECTIONTYPE`
- `LOCAL.NUM` (length)
- `LOCAL.STR` (bytes as text)
- `LOCAL.ACCOUNT`, `LOCAL.CHAR`
- `LOCAL.0` .. `LOCAL.<NUM-1>` (every byte)

`RETURN 1` drops the packet: the server does not handle it / does not send it. Function name at most 30 characters. Unregistered opcodes are filtered too.

- **Default:** none
- **Note:** Matches Source-X `CClient::xPacketFilter` / `xOutPacketFilter`.

## Network

### USEASYNCNETWORK

Use asynchronous socket I/O.

- **Type:** integer (0-2)
  - `0` = synchronous I/O
  - `1` = async with one thread per client
  - `2` = async with a thread pool
- **Default:** `0`
- **Note:** Not read by the server config. The network runs on the main loop with non-blocking sockets (Poll -> Receive -> Process -> Send); there is no async networking. The resync mechanism (packet `0x22`, `.RESYNC`) exists.

### NETWORKTHREADS

Number of dedicated network I/O threads.

- **Type:** integer (`0` = the main thread handles the network)
- **Default:** `0`
- **Note:** Only read back through `SERV.NETWORKTHREADS`; a deliberate no-op (the network runs on the main loop). Input is received and dispatched serially on the main loop. Only the output flush runs in parallel (across all cores) once 128 or more connections are active, regardless of this value.

### NETWORKTHREADPRIORITY

Priority of network threads (0=Normal .. 4=RealTime).

- **Type:** integer (0-4)
- **Default:** `0`
- **Note:** Not defined in the server config; it has no effect.

### MAXPACKETSPERTICK

Maximum packets SENT to a client per tick, per queue (Source-X: an output limit, `packet.h` `NETWORK_MAXPACKETS`).

- **Type:** integer
- **Default:** `50` (same as Source-X)
- **Note:** Only read back through `SERV.MAXPACKETSPERTICK`. Inbound packets are never capped: every packet a client sends is processed in the same pass and no connection is dropped for packet count, as in Source-X. Input volume is limited by `MAXSIZECLIENTIN` (bytes per 10 s). The send path drains whatever the socket accepts and does not apply this value. The old `FLOODDETECTIONCOUNT` and `FLOODDETECTIONWINDOWMS` keys are retired; an ini that still has them loads normally and the server notes once that they are ignored.

### MAXSIZECLIENTIN

Maximum bytes the server may receive from one client in 10 seconds. Over it, `f_onclient_exceed_network_quota` runs with `ARGN1=2`:

- `RETURN 0` = keep + log
- `RETURN 1` = keep silently
- anything else = disconnect + log

- **Type:** integer (bytes, `0` = disabled)
- **Default:** `10000` when the key is absent

### MAXSIZECLIENTOUT

Maximum bytes the server may send to one client in 10 seconds. Over it, `f_onclient_exceed_network_quota` runs with `ARGN1=1`.

- **Type:** integer (bytes, `0` = disabled)
- **Default:** `80000` when the key is absent

### MAXSIZEPERTICK

Total network I/O limit per tick (bytes).

- **Type:** integer (bytes, `0` = unlimited)
- **Default:** `0`
- **Note:** Not defined in the server config; it has no effect.

### MAXQUEUESIZE

Packets a priority queue holds before the next packet for it is moved down one level (Normal to Low, Low to Idle; Idle has no limit), as Source-X `CNetworkOutput::QueuePacketTransaction` does. It never drops a packet and never drops the connection.

- **Type:** integer (`0` = no limit)
- **Default:** `75` (the Source-X sample sphere.ini ships `500`)
- **Note:** The queues normally empty on every flush, and while they do a demoted packet still leaves in send order. Only when the client cannot take a burst (its socket is full) can a later packet into an emptied queue overtake an earlier, demoted one; a small value makes that likelier, so keep it well above a login or teleport burst.
- **Note:** The send queue has no other depth limit: a burst (a crowded screen seen with ALLSHOW) stays queued and drains as the client reads it. Past 1024 queued packets / 256 KB unsent, shared speech and sound broadcasts are shed first. A client is dropped only when its socket accepts nothing for 60 seconds while data is pending, or when 64 MB is left unsent.

### NETTTL

Lifetime of idle connections (seconds); a connection idle this long is closed. It is also the lifetime of an IP's connection history.

- **Type:** integer (seconds; below 10 = startup warning)
- **Default:** `300`

### MAXCONNECTREQUESTSPERIP

Connection attempts an IP may make while its connection history lives. Does not decay; it resets when the IP logs in successfully or the history expires (`NETTTL` after the last connection closed). Each login is two connections (login + game).

- **Type:** integer (`0` = off)
- **Default:** `50` (Source-X built-in 5, reference ini 50)

### MAXPINGS

Limit of connection attempts counted like pings; decays by one every `max(30, NETTTL/5)` seconds.

- **Type:** integer
- **Default:** `15`

### TIMEOUTINCOMPLETECONN

How long a connection that has not identified itself (no login packet yet) may wait before it is closed (ms).

- **Type:** integer (ms; may be written as a product: `5*1000`)
- **Default:** `5000` (`0` = off)

### DEADSOCKETTIME

Source-X: time before a dead socket is closed (MINUTES in Source-X, default 5).

- **Type:** integer
- **Default:** `300`
- **Note:** Only read back through `SERV.DEADSOCKETTIME`; idle connections are closed by `NETTTL`.

### RTTPINGINTERVALMS

Interval at which the server sends unsolicited `0x73` pings to the client (ms).

- **Type:** integer (ms, `0` = off)
- **Default:** `0`
- **Note:** CAUTION: ClassicUO feeds every incoming `0x73` into its own ping meter, so server pings make the client show a meaningless (huge) ping. The server-side RTT is not used by anything; keep it off unless a client that ignores unsolicited pings is in use.

### CUOSTATUS / UOGSTATUS

Answer ConnectUO / UOGateway status queries.

- **Type:** boolean (`0`/`1`)
- **Default:** `1`

### USEHTTP

Serve the web status page (on `SERVPORT+2`).

- **Type:** integer (`0` = off, any other value = on)
- **Default:** `2` (Source-X)

### USEEXTRABUFFER

Allocate an extra output buffer per connection.

- **Type:** boolean (`0`/`1`)
- **Default:** `0`
- **Note:** Not defined in the server config; it has no effect.

### USEPACKETPRIORITY

Send outgoing packets by priority. Off (the Source-X default), every packet goes to one queue and leaves in the order it was sent. On, each packet gets the priority Source-X gives it in `send.cpp`, and higher priorities leave first: movement ack/reject Highest; player start, drag cancel and the speed mode High; status, health bars, gumps, container open, vendor lists, maps, books, target cursors and the like Low; game time, weather, music, ping and tooltips Idle; everything else Normal. Packets that travel together share a queue (vendor list with the container open, a map with its pins, a book with its gump), so their order holds.

A few packets have a fixed priority either way: login denied `0x82` and kick `0x26` Highest, time sync `0xF2` High, the property list `0xD6` Idle.

- **Type:** boolean (`0`/`1`)
- **Default:** `0`
- **Note:** The `[PACKETPRIORITY]` section moves single opcodes while this is on; see below.

### [PLUGINS]

A section of its own (after the `[SPHERE]` keys, before `[EOF]`) listing in-process .NET plugins as `name=path`, the path relative to the server folder. They load in file order when the server opens. See [PLUGINS.md](PLUGINS.md).

```
[PLUGINS]
Welcome=plugins/Welcome/SphereNet.Plugins.Sample.dll
```

- **Note:** A missing file or a plugin whose `OnLoad` fails is logged and skipped; the server still starts.

### [PACKETPRIORITY]

A section of its own (after the `[SPHERE]` keys, before `[EOF]`) that overrides the priority of single packets while `USEPACKETPRIORITY=1`. A key is an opcode in hex (`0x6C`) or an extended `0xBF` subcommand (`0xBF.0x26`); a value is `Idle`, `Low`, `Normal`, `High`, `Highest` or `0`-`4`. Opcodes not listed keep the built-in table.

The shipped `sphere.ini` ends with the whole table as commented lines, one per packet with its built-in priority and packet name (`// 0x24=Low     // PacketOpenContainer`). To change one, uncomment `[PACKETPRIORITY]` and that line. A line left commented follows the engine's value, so a later correction of the table still reaches the shard; a test keeps the list equal to the table.

```
[PACKETPRIORITY]
0x6C=High
0x54=Low
0xBF.0x26=High
```

- **Note:** A bad line is skipped with a warning at startup. The fixed-priority packets above cannot be moved. Moving a member of a packet sequence (`0x3C`, `0x74`, `0x9E`, `0x24`, `0xD4`, `0x90`, `0xF5`, `0x56`) away from its partners is allowed but warned about: the client then receives the sequence out of order (for example a shop gump that opens before its prices arrive).

## Commands, staff & administration

### COMMANDPREFIX

Prefix character of in-game GM commands (only the first character counts).

- **Type:** string (single character)
- **Default:** `"."`
- **Example:** with `.` typing `.ADD 0x0F51` in game creates an item.

### DEFAULTCOMMANDLEVEL

Default PrivLevel of new accounts (applies to newly created accounts).

- **Type:** integer (0-7)

  | Value | Level |
  |---|---|
  | `0` | Guest |
  | `1` | Player |
  | `2` | Counselor |
  | `3` | Seer |
  | `4` | GM |
  | `5` | Dev |
  | `6` | Admin |
  | `7` | Owner |

- **Default:** `1` (Player; above Player a startup warning is printed)

### COMMANDLOG

Lowest privilege level whose commands are logged. Applies to in-game commands and the telnet console.

- **Type:** integer (PrivLevel; `-1` = none)
- **Default:** `0` (everyone's commands)

### CANSEESAMEPLEVEL

How staff see hidden (invisible/hidden) staff.

- **Type:** integer
  - `0` = equal or higher level sees
  - `1` = only a strictly higher level sees
  - `2`-`7` = that level and above see everyone
- **Default:** `0`

### EXPERIMENTAL

Source-X experimental feature flags.

- **Type:** hex/integer
- **Default:** `0`

Bits SphereNet reads:

| Bit | Name | Effect |
|---|---|---|
| `0x0000040` | `EF_Item_Strict_Comparison` | Boards/logs and leather/hides no longer stand in for each other in resource matching. |
| `0x0000400` | `EF_Script_Profiler` | Time every script function and trigger. The console's `P` prints the report (`P#` also writes `profiler_dump.txt`), `E` clears it. See PERFORMANCE.md. |
| `0x0002000` | `EF_DamageTools` | Tools take wear damage when used. |
| `0x0080000` | `EF_WalkBypassMonsters` | Every mobile is sent with the "ignore mobiles" mode bit (`0x10`), so players can step over NPCs without full stamina; normally the client allows this to staff only. WARNING: the server walk check is unchanged, so it can cost resources and cause movement flicker on clients. |

Other bits are only read back through `SERV.EXPERIMENTAL`.

### TELNETLOG

Log telnet console commands.

- **Type:** boolean (`0`/`1`)
- **Default:** `1`

### SECURE

Source-X secure mode. In SphereNet the freeze monitor only runs while this is on.

- **Type:** boolean (`0`/`1`)
- **Default:** `1`

### FREEZERESTARTTIME

If the main loop makes no progress for this long the server is considered frozen and exits with code 3 (the Host restarts it).

- **Type:** integer (seconds, `0` = monitor off)
- **Default:** `60`
- **Note:** The monitor does not run while `SECURE=0`.

### LOGMASK

Source-X log category mask.

- **Type:** hex/integer (`0x` prefix required; the reference ini's `0fff00` can NOT be read)
- **Default:** `0x3F00`
- **Note:** SphereNet filters its logs with `LOGFILELEVEL`; this mask is kept only for `SERV.LOGMASK` and the HEARALL bit (`0x2000`).

### HEARALL

Turns the player-speech bit (`0x2000`) of `LOGMASK` on/off.

- **Type:** boolean (`0`/`1`)
- **Default:** the bit in `LOGMASK` (on in the default mask)
- **Note:** The in-game `PRIV_HEARALL` (a staff member hearing every conversation) is separate and is a character flag. This ini key only affects the `SERV.HEARALL` read/toggle. No speech logging depends on this bit.

### SENTRYDSN

DSN of the Sentry project errors are reported to (used at program startup by the Sentry SDK).

- **Type:** string
- **Default:** `""` (off)
- **Note:** `//` starts a comment anywhere in a line (as in Source-X), so write the DSN WITHOUT its scheme: `SENTRYDSN=key@o1.ingest.sentry.io/123` - `https://` is added automatically.

## Speech, chat & messages

### CHATFLAGS

Chat system flags (Source-X `CHATF_*`).

- **Type:** hex/integer (`0x` prefix required)

  | Bit | Name | Effect |
  |---|---|---|
  | `0x01` | AUTOJOIN | auto-join the first static channel |
  | `0x02` | CHANNELCREATION | players may create channels |
  | `0x04` | CHANNELMODERATION | channel moderation |
  | `0x08` | CUSTOMNAMES | custom chat names |
  | `0x10` | GLOBALCHAT | global chat (clients 7.0.62.2+) |

- **Default:** `0`
- **Note:** SphereNet applies only `0x10` GLOBALCHAT; the other bits are only read back through `SERV.CHATFLAGS`.

### CHATSTATICCHANNELS

Chat channels that always exist (comma separated).

- **Type:** string
- **Default:** `"General, Help, Trade, Looking For Group"`

### ARRIVEDEPARTMSG

Player arrival/departure announcement, on login/logout.

- **Type:** integer
  - `0` = off
  - `1` = announce to players
  - `2` = staff only
- **Default:** `1`

### CHARTAGS

Add tags such as `[npc]`, `[tame]` to a character's name on single click (the single-click label).

- **Type:** boolean (`0`/`1`)
- **Default:** `0` (a GM with AllShow sees them anyway)

### SUPPRESSCAPITALS

Lower-case a player's all-caps speech (longer than 5 letters).

- **Type:** boolean (`0`/`1`)
- **Default:** `0`

### SPEECHOTHER

A SPEECH list Source-X reads and keeps but never uses.

- **Type:** string
- **Default:** `""`
- **Note:** Only read back through `SERV.SPEECHOTHER` (Source-X does not use it either).

### MEDIUMCANHEARGHOSTS

Spirit Speak value (tenths: `1000` = 100.0) at which the living understand ghost speech.

- **Type:** integer
- **Default:** `1000`

### EMOTEFLAGS

Who sees the automatic emotes (Source-X `EMOTEF_*`).

- **Type:** hex/integer (`0x` prefix required)

  | Bit | Name | Effect |
  |---|---|---|
  | `0x01` | ATTACKER | "%s is attacking %s!" only to the attacked character |
  | `0x02` | POISON | the poison emote only to the poisoned character |
  | `0x04` | DESTROY | the item-destroy emote only to its owner |
  | `0x08` | HIDE_EAT_PLAYER | no emote while players eat |
  | `0x10` | HIDE_EAT_NPC | no emote while NPCs eat |

- **Default:** `0`
- **Note:** SphereNet applies only `0x02` POISON.

### CONTEXTMENULIMIT

Most entries sent in one context menu.

- **Type:** integer (0-255)
- **Default:** `15`

## SCRIPTING & WORLD RULES

### MAXLOOPTIMES

Most iterations of a single script loop (`WHILE`/`FOR`...).

- **Type:** integer (`0` = unlimited)
- **Default:** `100000` (the Source-X reference ini ships `10000`)

### AREAFLAGS

What ROOMDEFs inherit from the AREA around them (Source-X `AREAF_*`). Applied once after the world loads.

- **Type:** flags (Sphere rule)
  - `01` = EVENTS
  - `02` = FLAGS
  - `04` = TAGs
- **Default:** `02`

### ADVANCEDLOS

Who uses the newer line-of-sight algorithm. A BIT MASK, not a quality level (Source-X `CServerConfig.h:474-476`, `CCharLOS.cpp:17`).

- **Type:** integer bit mask (0-3)
  - `0x00` DISABLED
  - `0x01` PLAYER
  - `0x02` NPC
  - `0x03` both
- **Default:** `0`
- **Note:** With the bit off that kind of viewer uses the old method: walking square by square at foot level (Source-X `CanSeeLOS`, default); with it on, a ray at eye level (`CanSeeLOS_New`).

### DISTANCEFORMULA

Distance calculation method (Source-X `CServerConfig.h:479-481`, `CPointBase.cpp:151+`).

- **Type:** integer (0-2)
  - `0` = no diagonal, no Z: a SQUARE area around the object (UO standard)
  - `1` = diagonal counted, no Z: a CIRCULAR area
  - `2` = like 1, but every distance check also counts Z
- **Default:** `0`
- **Note:** The client's view range (`GetDistSight`) always stays square.

### FLIPDROPPEDITEMS

An item dropped on the ground turns to its flipped graphic. Never applied to an item that cannot be moved (Source-X `CCharAct.cpp:3266`).

- **Type:** boolean (0/1)
- **Default:** `1`

### CANUNDRESSPETS

Let an owner take off what their pet is WEARING. Most of the rule is OWNERSHIP anyway: an owner can always reach into the pet's PACK; this key only says whether they may also strip what it wears (`CCharAct.cpp:2957`). Neither is open to anyone else. Before 2026-09-14 this gate read "unless another PLAYER", so every NPC in the world was open to everyone, a stranger's dragon's armour included.

- **Type:** boolean (0/1)
- **Default:** `1`

### CANPETSDRINKPOTION

A pet drinks a potion dropped on it. While off, the bottle goes into the pet's pack like any other gift (`CCharNPCAct.cpp:2145`). The way to heal a wounded animal in the middle of a fight.

- **Type:** boolean (0/1)
- **Default:** `1`

### VENDORMARKUP

A vendor's profit margin in percent, when nothing more specific says otherwise. Order: the vendor's own TAG -> the region's TAG -> this (`CCharNPCStatus.cpp:366`). `1` = no markup. `0` = shopping is FREE (the reference ini's own warning).

- **Type:** integer (%)
- **Default:** `15`

### VENDORMAXSELL

Most units of one line a vendor sells in a single transaction. The cap is applied when the list is BUILT (`send.cpp:1249`), not after the click: the client never offers a number the shard does not allow.

- **Type:** integer (0 or below = no limit)
- **Default:** `255`

### LOSTNPCTELEPORT

An NPC that strays this far from home is teleported back (tiles). A safety belt, not a leash: the distance must beat both this and the creature's own wander range (`CCharNPCAct.cpp:1550`), or a wide-roaming spawn would be dragged home by a narrow global. `@NPCLostTeleport` can veto it.

- **Type:** integer (tiles, `0` = off)
- **Default:** `50`

### NPCSHOVENPC

Can an NPC push past another NPC? Players shove creatures; creatures hold each other (`CCharAct.cpp:4624`). A single creature can be exempted with `TAG.OVERRIDE.SHOVE`. Without the rule a guard walks right through the crowd it should be stuck behind.

- **Type:** boolean (0/1)
- **Default:** `0`

### TOOLTIPMODE

Tooltip (AOS property) display mode. Drives the `0xDC` revision and `0xD6` property list flow.

- **Type:** integer (0-2)
  - `0` = off (no tooltips sent)
  - `1` = send revision/hash; full tooltip when changed or requested
  - `2` = always send the full tooltip
- **Default:** `1` (`TOOLTIPMODE_SENDVERSION`, as Source-X)

### TOOLTIPCACHE

How long computed tooltip data is reused before recomputing (seconds).

- **Type:** integer (seconds)
- **Default:** `30`

### TELEPORTEFFECTNPC / TELEPORTEFFECTPLAYERS / TELEPORTEFFECTSTAFF

Visual effect id shown on teleport, by who teleported (`0` = no effect). The teleporter's own effect is shown. A script that sets its own effect on the teleport overrides these (`CCharSpell.cpp:178`).

- **Type:** integer (effect graphic id; `0x` prefix or leading zero = hex)
- **Default:** NPC `0x376A`, player `0x3728` (vanish), staff `0x3709` (flamestrike), per Source-X `CServerConfig.cpp:88`

### TELEPORTSOUNDNPC / TELEPORTSOUNDPLAYERS / TELEPORTSOUNDSTAFF

Teleport sound (`0` = no sound).

- **Type:** integer (sound id; `0x` prefix or leading zero = hex)
- **Default:** NPC `0x01FE`, player `0x01FE`, staff `0x01F3`, per Source-X `CServerConfig.cpp:89`

### GENERICSOUNDS

Play generic action sounds (hit, miss, parry ...).

- **Type:** boolean (0/1)
- **Default:** `1`

### DECAYTIMER

Default decay time of items on the ground (minutes).

- **Type:** integer (minutes, at least 1)
- **Default:** `30`
- **Note:** Source-X also reads this value in minutes.

## MOVEMENT

### WALKBUFFER

Capacity of the movement token bucket (the allowed burst of steps). A guard against speed hacks: every accepted step spends one token and an empty bucket refills at `WALKREGEN`. Very low values (e.g. `2`) choke a running player: steps are rejected as `walk_buffer` and the client snaps back ("stutter/rubber-band"). This is the token-bucket movement gate.

- **Type:** integer (tokens, at least 1; below 5 = startup warning)
- **Default:** `75`
- **Note:** In Source-X this key is a time in tenths of a second; SphereNet reads it as a token count.

### WALKREGEN

Movement tokens regained PER SECOND (a RATE, not a period). A running player needs ~5 steps/s, so it must be at least that (`25` leaves ample room). Low values like `1` lock movement to one step per second.

- **Type:** integer (tokens per second; below 5 = startup warning)
- **Default:** `25`

### WALKDELAYFOOT / WALKDELAYMOUNT / RUNDELAYFOOT / RUNDELAYMOUNT

Shortest time (ms) between walking/running steps on foot/mounted (the movement rate limit). Must match the client's own step pace; the next step is accepted (value - 30 ms) later.

- **Type:** integer (ms; `WALKDELAYFOOT` 0 or less = startup warning)
- **Default:** `400` / `200` / `200` / `100` (the client's speeds)

### MOVEMENTCREDITENABLED

Credit-based movement system instead of the token bucket (opt-in). While on, `MOVEMENTCREDIT*` replace `WALKBUFFER`/`WALKREGEN` in the movement gate.

- **Type:** boolean (0/1)
- **Default:** `0`

### MOVEMENTCREDITBASEMS

Base cost of one step in the credit system (ms).

- **Type:** integer (ms; below 50 = startup warning)
- **Default:** `200`
- **Note:** Only used while `MOVEMENTCREDITENABLED=1`.

### MOVEMENTCREDITMAXMS

Most time the credit bucket can save up (ms).

- **Type:** integer (ms)
- **Default:** `1400`
- **Note:** Only used while `MOVEMENTCREDITENABLED=1`.

### MOVEMENTQUEUECAPACITY

Length of the queue holding steps that arrive early.

- **Type:** integer (above 50 may hide speed hacks, startup warning)
- **Default:** `10`

### SPEEDHACKDETECTIONENABLED

Speed-hack detection from the movement history.

- **Type:** boolean (0/1)
- **Default:** `0`

### SPEEDHACKRATETHRESHOLD

How many times the expected step rate counts as a violation.

- **Type:** decimal number with a dot (`1.5`)
- **Default:** `1.5`
- **Note:** Only used while `SPEEDHACKDETECTIONENABLED=1`.

### SPEEDHACKBURSTWINDOW

Consecutive steps looked at in the rate calculation.

- **Type:** integer
- **Default:** `3`
- **Note:** Only used while `SPEEDHACKDETECTIONENABLED=1`.

### SPEEDHACKHISTORYSIZE

Length of the stored movement history (steps).

- **Type:** integer
- **Default:** `20`

### SPEEDHACKCOOLDOWNMS

Time after a violation before another one is reported (ms).

- **Type:** integer (ms)
- **Default:** `60000`
- **Note:** Only used while `SPEEDHACKDETECTIONENABLED=1`.

### MOUNTHEIGHT

A mounted (or hovering gargoyle) character counts 4 units taller and cannot step where the ceiling is within that height. GM and AllMove are exempt.

- **Type:** boolean (0/1)
- **Default:** `0`

## LIGHT, WEATHER & SEASONS

### NOWEATHER

Is there no weather (rain, snow) at all? The flag stops new weather being ROLLED and TOLD to the client; weather a script set on a region is kept and readable (Source-X `CSector.cpp:861`, `CClientMsg.cpp:526`).

- **Type:** boolean (0/1)
- **Default:** `1`. Bare Source-X has NO weather; a shard that wants it writes `0` (this pack does).

### LIGHTDAY

Daytime light level (`0` = brightest, `30` = darkest). Used by the world light calculation.

- **Type:** integer (0-30)
- **Default:** `0`

### LIGHTNIGHT

Night-time light level (`0` = brightest, `30` = darkest). Used by the world light calculation.

- **Type:** integer (0-30)
- **Default:** `25`

### DUNGEONLIGHT

Light level in dungeon regions.

- **Type:** integer (0-30)
- **Default:** `27`

### ALLOWLIGHTOVERRIDE

A sector `LIGHT` pinned by staff or a script overrides the day/night calculation. `0` = sectors always follow the clock.

- **Type:** boolean (0/1)
- **Default:** `1`

### SEASONMODE

Season flow mode.

- **Type:** string or integer
  - `Auto` / `0` = seasons advance automatically as the interval passes
  - `Manual` / `1` = the season stays fixed; scripts/admins change it
- **Default:** `Auto`

### SEASONDEFAULT

Season the server starts in. Login, resync and tick start from this season state.

- **Type:** integer (0-4)
  - `0` = Spring
  - `1` = Summer
  - `2` = Fall
  - `3` = Winter
  - `4` = Desolation
- **Default:** `0`

### SEASONCHANGEINTERVALMINUTES

Season change interval in Auto mode (minutes).

- **Type:** integer (`0` = auto cycle off, `1`+ = minutes)
- **Default:** `30`
- **Note:** Ignored in Manual mode.

## STATE RECORDING & MACROS

### STATERECORDINGENABLED

Character state recording system (SQLite).

- **Type:** boolean (0/1)
  - `0` = off: no SQLite DB is created, zero performance cost
  - `1` = on: position/equipment snapshots of characters are recorded; admins can watch them with `.SREC` and shareable recordings can be made
- **Default:** `0` (off)

### STATERECORDPLAYERSONLY

Record player characters only (skip NPCs).

- **Type:** boolean (0/1)
  - `0` = every character is recorded (costly with many NPCs!)
  - `1` = player characters only (recommended)
- **Default:** `1`
- **Note:** With 100K+ NPCs, `0` causes a serious CPU load.

### STATERECORDMOVESCANMS

Movement scan interval (ms). Lower = finer movement record, more CPU/disk.

- **Type:** integer (ms, at least 500)
- **Default:** `2000`

### STATERECORDSNAPSHOTMS

Full state snapshot interval (equipment, HP, position) (ms).

- **Type:** integer (ms, at least 5000)
- **Default:** `15000`

### MACROENABLED

Player macro record/playback system. Every action runs through the normal game engine: skill checks, timers, weight limits, success/failure all follow the standard rules.

- **Type:** boolean (0/1)
  - `0` = off: the `.MACRO` command does not work
  - `1` = on: players can record and repeat their actions (mining, crafting ...)
- **Default:** `1`

### MACROMAXSTEPS

Most steps one macro can record.

- **Type:** integer (clamped to 5-200)
- **Default:** `50`

### MACROMAXLOOPMINUTES

Longest run time of a looping macro (minutes).

- **Type:** integer (clamped to 1-1440)
- **Default:** `120` (= 2 hours)

## ITEM DURABILITY

### ITEMDURABILITYENABLED

Weapons and armour take damage in combat. Applied after the attack is resolved, to the weapon and a random armour piece.

- **Type:** boolean (0/1)
- **Default:** `1`

### ITEMDURABILITYLOSSCHANCE

Chance of taking damage on each hit (%).

- **Type:** integer (0-100)
- **Default:** `25`

### ITEMDURABILITYLOSSMIN / ITEMDURABILITYLOSSMAX

Range of the durability lost.

- **Type:** integer (MIN 0 or more; MAX at least MIN)
- **Default:** `1` / `1`

### ITEMBREAKONZEROHITS

`0` = the item does not break, it just stays at 0; `1` = it breaks and is deleted.

- **Type:** boolean (0/1)
- **Default:** `1`

### ITEMDEFAULTHITS

Default durability for weapons/armour without a `HITS` value.

- **Type:** integer (at least 1)
- **Default:** `50`
- **Note:** The value is read and assigned to the combat engine's default-hits setting, but nothing reads it, so it currently has no effect.

## DATABASE (MySQL / SQLite / PostgreSQL)

Connections used by scripts through the `DB.*` object. There are two ways:

1. Classic single connection, inside `[SPHERE]` (Source-X compatible). When `MYSQL` is non-zero and `MYSQLHOST` is set, a connection named `default` is created and connects at startup.
2. Named connections, OUTSIDE the `[SPHERE]` block, in their own sections (see below).

### MYSQL

Enable the classic MySQL connection. `SERV.MYSQL` returns whether it is connected.

- **Type:** integer (non-zero = on)
- **Default:** `0`

### MYSQLHOST / MYSQLUSER / MYSQLPASSWORD / MYSQLDATABASE

Server, user, password and database name of the classic connection.

- **Type:** string
- **Default:** `""` (the port is always 3306; for another port use way 2)
- **Note:** Only used while `MYSQL` is non-zero.

### MYSQLCHARSET

Session character set of the classic connection, sent as `SET NAMES` after connecting (same as `CHARSET` in way 2 below).

- **Type:** string
- **Default:** `""` (utf8mb4)

### [MYSQL <name>] / [SQLITE <name>] / [PGSQL <name>]

Named connections, OUTSIDE the `[SPHERE]` block, in their own sections: `[MYSQL <name>]`, `[SQLITE <name>]` or `[PGSQL <name>]`. A section named `default` overrides the connection of way 1. Sections must come BEFORE `[EOF]`. To use the example in the ini, remove the leading `// ` and move it above `[EOF]`.

Keys (all optional):

| Key | Meaning | Default |
|---|---|---|
| `PROVIDER` | ADO.NET provider | `MySqlConnector` (`[SQLITE]` sections: `Microsoft.Data.Sqlite`, `[PGSQL]`: `Npgsql`) |
| `HOST` | Server address (not used by SQLite) | `localhost` |
| `PORT` | Port (not used by SQLite) | `3306`, `[PGSQL]`: `5432` |
| `USER` | User | `""` |
| `PASSWORD` | Password (SQLite: file password) | `""` |
| `DATABASE` | Database name; for SQLite the file path | `""` |
| `KEEPALIVE` | Keep the connection alive (60 s keepalive) | `0` |
| `CONNECTTIMEOUT` | Connect timeout (seconds) | `30` (PostgreSQL: at most 1024) |
| `READTIMEOUT` | Command timeout (seconds) | `30` |
| `WRITETIMEOUT` | Write timeout (seconds). Read, but has no effect: not added to the connection string. | `30` |
| `USETHREAD` | Run queries on a dedicated thread | `0` |
| `AUTOCONNECT` | Connect at startup | `0` |
| `CHARSET` | MySQL session charset, sent as `SET NAMES` on every connect (see below) | `""` (utf8mb4) |

`CHARSET`: tables written by classic Sphere or a PHP site through latin1 sessions hold UTF-8 bytes in latin1 columns and read back as "ÅŸ" instead of "&#351;" (s-cedilla, U+015F): set `latin1`. Ignored for PostgreSQL (the server converts from the column's encoding).

- **Note:** PostgreSQL: string literals must use single quotes (`'text'`); `"text"` is a column name there. `DB.ESCAPEDATA` doubles `'` for a `[PGSQL]` connection.
- **Note:** SQLite: `DB.ESCAPEDATA` doubles both `'` and `"` so a value is safe in either quote style; the quote mark the literal does not use is stored doubled.
- **Example:**

```ini
[MYSQL default]
PROVIDER=MySqlConnector
HOST=localhost
PORT=3306
USER=sphere
PASSWORD=
DATABASE=sphere
KEEPALIVE=0
CONNECTTIMEOUT=30
READTIMEOUT=30
WRITETIMEOUT=30
USETHREAD=0
AUTOCONNECT=1
CHARSET=

[SQLITE local]
DATABASE=save/scriptdb.sqlite
AUTOCONNECT=1

[PGSQL stats]
HOST=localhost
PORT=5432
USER=sphere
PASSWORD=
DATABASE=sphere
AUTOCONNECT=1
```
