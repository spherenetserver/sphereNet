# Staff / GM Commands

Complete reference of in-game staff commands and server-console commands.

- In-game commands are invoked with the `.` prefix (e.g. `.add`), spoken by a staff character.
- Privilege order: **Player < Counsel < GM < Admin < Owner**. A command requires its listed level or higher.
- Commands that open a **target cursor** are noted; with no argument many inspection/edit commands fall back to a target cursor.
- Generic fallbacks exist: an unregistered verb starting with `x` (e.g. `.xhits 100`, `.xkill`) opens a target cursor (Counsel) and applies the inner verb to the picked object; `verb=value` and `verb value` run as a property/command on the staff character.

> Source of truth: `SpeechEngine.cs` (`CommandHandler.RegisterDefaults`) and `AdminCommandProcessor.ProcessCommand`. Script `[PLEVEL n]` sections can remap command privileges at load time; those are shard-specific and not listed here.

---

## Player

| Command | Arguments | Description |
|---|---|---|
| `.macro` | `<args>` | Player macro record/playback. |
| `.page` | `<message>` | Send a help request to online staff. |
| `.srec` | `[args]` | Open the state-recording browser. |
| `.suicide` | — | Instantly kill yourself. |
| `.unmount` | — | Dismount. |
| `.where` | — | Show current X, Y, Z, map and terrain/effective Z. |

## Counsel

| Command | Arguments | Description |
|---|---|---|
| `.add` | `<id\|defname>` | Target a location; add an item/NPC there. |
| `.allmove` | `[0\|1]` | Toggle "move all items" mode. |
| `.allshow` | `[0\|1\|on\|off]` | Reveal offline/hidden characters (toggle). |
| `.edit` | `[uid] [page]` | Object property editor; no arg opens a target cursor. |
| `.fix` | — | Re-seat the character on its tile and resync visuals. |
| `.gm` | — | Toggle GM (invisible) mode; below GM only reports priv level. |
| `.go` | `<x> <y> [z] [map]` / `<x,y,z,map>` / `<area name>` | Teleport to coordinates or a named AREADEF location. |
| `.gochar` | `<name>` | Teleport next to a character by name. |
| `.gouid` | `<serial>` | Teleport to the object with that UID. |
| `.info` | `[uid]` | Inspect dialog; no arg opens a target cursor. |
| `.invis` | `[0\|1]` | Toggle invisibility. |
| `.nudge` | `[range]` (def 2) | Target a tile; shift nearby objects. |
| `.nuke` | `[range]` (def 4) | Target; delete items in the area. |
| `.nukechar` | `[range]` (def 4) | Target; delete mobiles/NPCs in the area. |
| `.openpaperdoll` | `[uid]` | Open the target's paperdoll (no arg = self). |
| `.record` | — | Open the recording manager dialog. |
| `.sectorlist` | — | Show the active-sector diagnostics dialog. |
| `.show` | `EVENTS [uid]` | Show the target's EVENTS/TEVENTS list (no arg = self). |
| `.showskills` | — | Send the skill list to the target client. |
| `.statics` | — | Dump static tiles and walkability at the current tile. |
| `.stressreport` | — | Dump runtime metrics to the server log. |
| `.tele` | — | Target; teleport to the picked point/object. |
| `.unstick` | — | Teleport to the default Britain bank (stuck recovery). |
| `.update` | — | Trigger a script resync. |
| `.xedit` | `[uid page]` | With args like `.edit`; no arg opens an EVENTS target cursor. |
| `.xshow` | `[text]` (def EVENTS) | Target; show the picked object's EVENTS. |

## GM

| Command | Arguments | Description |
|---|---|---|
| `.addnpc` | `<bodyId(hex)>` | Create a basic NPC with that body and place it at your location. |
| `.addskill` | `<skillId> <value>` | Set a skill value on the character (clamped 0–1200). |
| `.anim` | `<id>` | Play an animation on yourself. |
| `.bank` | `[target]` | No arg = your bank; with target opens another's bank. |
| `.body` | `<body\|chardef>` | Set the character's BODY. |
| `.broadcast` | `<message>` | Broadcast a message to all players. |
| `.cast` | `<spellId\|0xID\|spell name>` | Start casting the given spell. |
| `.chardef` | `<chardef>` | Set the character's CHARDEF. |
| `.control` | — | Target; make the picked NPC player-controlled. |
| `.cure` | `[uid]` | Cure poison (no arg = self). |
| `.dialog` | `<name> [page]` | Open a named script dialog on the client. |
| `.dupe` | — | Target; duplicate the picked item. |
| `.freeze` | `<uid(hex)>` | Freeze the target (StatFlag.Freeze). |
| `.heal` | `[self]` | Fully heal/resurrect self; otherwise opens a target cursor. |
| `.invul` | — | Toggle invulnerability. |
| `.jail` | `<serial(hex)> [minutes]` | Teleport + freeze in jail; timed if minutes given (persists via tag, auto-release). |
| `.kill` | `[uid(hex)]` | Kill the target; no arg = self. |
| `.mount` | — | Target; mount the picked rideable NPC. |
| `.poison` | `[uid] [level]` | Poison the target (level 1–5; no arg = self). |
| `.poly` | `<body[,hue]>` | Polymorph into the given body (and hue). |
| `.remove` | `[uid(hex)]` | Delete an item/character; no arg opens a target cursor (can't self-delete). |
| `.resurrect` | `[uid(hex)]` | Resurrect; no arg = self. |
| `.reveal` | `[uid]` | Clear hidden/invisible flags (no arg = self). |
| `.sdialog` | `<name> [page]` | Alias of `.dialog`. |
| `.set` | `<key> <value>` | Set a property on the character. |
| `.summoncage` | — | Target; summon the picked character to your feet and cage it. |
| `.summonto` | — | Target; summon the picked character to you. |
| `.unfreeze` | `<uid(hex)>` | Unfreeze the target. |
| `.unjail` | `<uid(hex)>` | Release from jail, clear the jail tag, teleport to spawn. |
| `.xresurrect` | — | Target; resurrect the picked character. |

## Admin

| Command | Arguments | Description |
|---|---|---|
| `.account` | — | Account management message (in-game placeholder; use console for full management). |
| `.resync` / `.ry` | — | Trigger a server/script resync. |
| `.save` | — | Trigger a world save. |
| `.setpriv` | `<uid(hex)> <PrivLevel>` | Change the target's privilege level. |
| `.shutdown` | — | Shut the server down. |

## Owner

| Command | Arguments | Description |
|---|---|---|
| `.bot` | `<count> [walk\|combat\|full\|smart] [city]` · `stop\|start\|clean\|status` · `spawn <city>` | Start/stop/clean TCP stress-test bots; set spawn city. |
| `.botmenu` | — | Open the bot manager dialog. |
| `.saveformat` | `<Text\|TextGz\|Binary\|BinaryGz> [shards]` | Switch save format (and shard count) and force a full save in the new format. No arg lists the current format. |
| `.scriptdebug` | `[on\|off\|1\|0]` | Toggle script expression-resolver diagnostics. |
| `.stress` | `[items] [npcs]` | Generate a large test population (default 500,000 items + 400,000 NPCs). |
| `.stressclean` | — | Delete all stress-tagged objects. |

---

## Server console (Telnet / stdin / GUI / IPC `exec`)

These are **not** in-game `.` commands. They are handled by `AdminCommandProcessor.ProcessCommand`, shared by the Telnet console (requires auth), headless stdin, the WinForms console, and the IPC `exec` op.

| Command | Arguments | Description |
|---|---|---|
| `HELP` | — | Show the command list. |
| `STATUS` / `INFO` | — | Character / item / sector / connection / account / tick stats. |
| `INFORMATION` | — | Server version, name, stats, memory usage. |
| `SAVE` | — | Save world + accounts. |
| `SHUTDOWN` | — | Shut the server down. |
| `RESYNC` / `RY` | — | Reload changed script files. |
| `DEBUG` | — | Toggle DebugPackets. |
| `SCRIPTDEBUG` | — | Toggle ScriptDebug. |
| `BROADCAST` | `<message>` | Message all players. |
| `WHO` | — | Online connection count. |
| `LOG` | `<message>` | Write a line to the server log. |
| `RESPAWN` | — | Respawn all NPCs. |
| `RESTOCK` | — | Restock all vendors. |
| `GARBAGE` | — | Force garbage collection. |
| `BLOCKIP` / `UNBLOCKIP` / `LISTBLOCKED` | `<ip>` | Manage blocked IP addresses. |
| `QUIT` / `EXIT` | — | Close the Telnet session. |
| `ACCOUNT` | — | List all accounts. |
| `ACCOUNT ADD` | `<name> <password>` | Create an account. |
| `ACCOUNT <name>` | — | Show account info. |
| `ACCOUNT <name> PASSWORD` | `<new>` | Change password. |
| `ACCOUNT <name> PLEVEL` | `<0-7>` | Set privilege level. |
| `ACCOUNT <name> DELETE` | — | Delete the account. |
| `ACCOUNT <name> BAN` / `BLOCK` / `UNBAN` | — | Ban / unban the account. |

The IPC channel also exposes structured ops (`save`, `resync`, `shutdown`, `gc`, `respawn`, `restock`, `broadcast`, account operations) used by the managed host/panel.
