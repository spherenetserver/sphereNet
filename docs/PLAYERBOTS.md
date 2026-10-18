# Player bots

Server-driven player characters. Turned on with `PlayerBots=1` in `sphere.ini`; off by
default. Code: `src/SphereNet.Game/PlayerBots/`, wiring: `src/SphereNet.Server/Program.PlayerBots.cs`.

## What a bot is

A bot is an ordinary **player character** (`ISPLAYER`) with no network connection. The
server plays it through its own `GameClient` whose `NetState` has no socket, so
everything it would be sent is dropped. Attacking, casting, using a bandage or a potion,
opening a door and speaking all go through the same `GameClient` handlers a connected
player's packets reach, which means the rules a player meets (swing timing, notoriety
and crime, fizzles and reagents, bandage delay, `@Speech`, NPCs hearing speech, guards)
apply to bots unchanged.

| | A bot | A connected player |
|---|---|---|
| `IsOnline` (ticks, keeps its sectors awake, seen by witnesses and guards) | yes | yes |
| `IsPlayerBot` | yes | no |
| `HasClient` | no | yes |
| In `_clients` (connections, view deltas built for it) | no | yes |
| In `_clientsByCharUid` (the world finds "this character's client") | yes | yes |

Client-only loops and counts use `HasClient`: `FORCLIENTS`, `SECTOR.ALLCLIENTS`,
`SECTOR.CLIENTS`, the "N other players" line at login, the all-clients console loop.
Move notifications skip bots (they keep no view of the world).

## Persistence

A bot is saved like any player character. Its marker and the state needed to resume
are tags; the save format is unchanged:

| Tag | Meaning |
|---|---|
| `PLAYERBOT` | marks the character as a bot |
| `PLAYERBOT.HOME` | home town (most trips stay there) |
| `PLAYERBOT.ARCH` | the CHARDEF it was created from |
| `PLAYERBOT.GOAL` | the destination it is walking to, if any |
| `PLAYERBOT.CORPSE` | the corpse it is going back to, while it has one |

After the world loads, `PlayerBotEngine.AdoptSaved` brings every `PLAYERBOT` character
back online where it stood, and a bot that had a goal sets off for it again. The
population is then filled up to `PlayerBotCount`, one new bot at a time. Lowering the
count logs the surplus out; they stay in the save as logged-out players. With
`PlayerBots=0` the saved bots are simply logged-out characters.

## Data (`PlayerBotDataDir`, default `playerbots/`)

| Path | Contents |
|---|---|
| `nav/waypoints.json` | road network: named nodes (classic map 0 tiles) and their links |
| `nav/destinations.json` | banks, shops, inns, healers, moongates, dungeon entrances and rooms; each with its nearest node and the spots a visitor stops on |
| `chat/<language>/<category>.txt` | lines a bot says, one per line, `#` comments, `{token}` placeholders |
| `archetypes.txt` | archetype CHARDEF defnames with weights |
| `scripts/*.scp` | the archetype CHARDEFs; loaded with the script pack when `PlayerBots=1` |

On load every node's Z is re-seated on the standable surface the local map data has
at that point (a node recorded at a placeholder height lands on the floor) and a node
with no surface is dropped. A destination point or stopping spot with no surface on its
own tile is seated on the nearest standable tile within two (the source data and this
map can disagree by a tile at a wall line), so no bot is sent to stand inside a wall. Links are made two-way. The graph's connected pieces are
labelled: islands and most dungeon levels are pieces of their own.

Archetypes are pack CHARDEFs. `ON=@Create` dresses and skills the character exactly as
for an NPC (keys after an `ITEM=` line go to that item, so `ADDCIRCLE` fills a spellbook);
the engine then makes it a player character.

A chat category missing in `PlayerBotLanguage` falls back to `en`. A line whose tokens
cannot be filled is skipped.

## Behaviour

Each bot has a mode, acted on when its next action time comes:

- **Linger** at a destination: a few words now and then, a step or two about. Banks
  hold a bot longest.
- **Travel**: a route over the road graph (A* on nodes) from the nearest node to the
  destination's, then node to node and onto a stopping spot. Destinations are chosen on
  the piece of road the bot stands on; most stay in the home town. A fighter sometimes
  heads for a dungeon entrance; underground it goes room to room for a while, then to
  the way out. A bot away from home, with no road back, takes a moongate.
- **Fight**: anything attacking the bot is fought; a fighter outside a guarded region
  also picks fights with monsters up to its nerve (a per-bot multiple of its own hit
  points). Melee closes in, archers and casters keep their distance, casters cast.
  Below a per-bot hit-point threshold it heals (Greater Heal, bandage, heal potion) or
  runs.
- **Death**: half a minute to a minute as a ghost walking about the body in war mode - a
  manifested ghost, which the living can see - then the bot is carried off the way a
  stuck bot is and raised there: a murderer at the anchor of the `PlayerBotRedRespawn`
  region (Delucia by default), anyone else at the nearest town healer. A corpse within
  `PlayerBotReclaimRange` tiles (40) is walked back to: the bot opens it (only an opened
  container can be reached into) and takes its things off one at a time through its client -
  picked up, worn on the layer they came off (`EQUIPLAYER`) or dropped into the pack - as T2A
  had no self-loot; it gives up four minutes after reaching the body. A farther corpse has
  what is still on it handed back by the server (same layer-or-pack rule) and is removed.
  Until then the corpse is an ordinary corpse: other players can loot it, and what they take
  is not handed back.
- **Party**: a player's party invitation is accepted after a moment, through the same
  accept sub-command a client sends. In a party led by a player the bot keeps within three
  tiles of the leader, joins the fight the leader picks, and leaves the party when the
  leader has been out of reach for two minutes.

Being talked to: a player saying a bot's name gets an answer from that bot, a greeting
may get one from someone nearby (`respond_name`, `respond_question`, `respond_greet`).
Replies come after a short typing delay. Only real players are answered.

### Walking

A step goes straight at the target, or 45 degrees either side. Against an obstacle the
bot keeps it on one hand and walks its edge (Bug2) until it is nearer the target than
where it met it and the way ahead is open; a long way round with no gain switches hands.
A shut, unlocked door in the way is opened with a double click. A tile search
(`Pathfinder`) is used only to close in during a fight, within a few tiles, because a
search costs a walk check per neighbour of every node it opens.

Shops and banks are entered and left by their door: a route starts at the road node the
bot's stopping spot is linked to (`Arrivals[].Waypoints` in the data) and ends at the
node linked to the spot it is heading for, not at whichever node is nearest through a
wall. When the first leg makes no ground for 20 seconds the bot walks to the nearest
unlocked door within 8 tiles and opens it, then tries the next-nearest start nodes. A
leg that makes no ground for a minute lifts the bot onto the leg's target (a rescue); a
second rescue on the way out of or into the same stopping spot marks it as unreachable
(`badspots` in the telemetry), so a shop that is locked in this world costs two rescues,
not one per visit. Learned spots are kept in `playerbots_badspots.txt` in the world save
directory (one `destination|spot` per line) and read back at start; deleting the file
lets bots try every spot again.

A bot carried far without walking (a gate, a teleporter, a script) plans again from
where it landed. A tile whose step carried it off is remembered as a portal for three
minutes and stepped round, unless the trip is to that very portal (a dungeon entrance,
a moongate, stairs), so a route that merely passes over a teleporter does not bounce the
bot back and forth.

## Telemetry

Every 30 seconds the server log carries one line:

```
[playerbots] online=1000 tick avg=7682us max=26916us deferred=15584 steps=... searches=0 (failed 0, slowest 0us) doors=... fights=60 deaths=2 reclaims=0 handbacks=1 rescues=95 badspots=12 slowest act ...us (Travel->Travel) route ...us step ...us spawn ...us moves 127434 avg 62us max ...us at ...
```

`tick` is the whole bot pass per server tick. Bot decisions share a budget of 12 ms
per tick; `deferred` counts the decisions that waited for the next tick (100 ms
later) because it was spent, and the next tick resumes where the last one stopped.
`moves` and their `avg` are the walk-path steps the bots took (one in sixteen is
timed), `rescues` the legs that had to be lifted, `slowest act` the single most
expensive decision of the window and `spawn` the costliest new bot (its CHARDEF
`@Create`). The first fifteen rescues of a window are logged with position, target
and destination, which is where to look when a spot keeps catching bots.

## Scale

A bot is a player character to the world, so a thousand of them wake as much of the
world as a thousand players spread over it would. They read no packets: the
broadcast and view-refresh loops skip them (no tooltip, status or view work is built
for a bot), and only a real client gets a view delta. Measured on a 16-core machine
with the live save (26 000 NPCs, 86 000 items), the default script pack and 1000 bots
walking and fighting: the bot pass averages 4-9 ms a tick, the slowest single server
ticks take 50-65 ms (mostly NPC decisions), the main loop now and then runs two ticks
back to back to catch up (up to about 230 ms), and the process holds about 0.9 GB.
At that density about one bot in a hundred needs a rescue a minute, mostly bots
blocking each other in doorways.
