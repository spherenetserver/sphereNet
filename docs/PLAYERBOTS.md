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
`SERV.ALLCLIENTS`, `SECTOR.CLIENTS`, the "N other players" line at login, the
all-clients console loop. Move notifications skip bots as recipients (they keep no view
of the world).

### Seen by staff

- `SERV.ALLBOTS <function|verb>` runs a line once per online bot, as `SERV.ALLCLIENTS`
  does per client (same dispatch, SRC is the caller). `ISPLAYERBOT` reads 1 on a bot.
  `SERV.ALLCLIENTS` itself still leaves bots out, so a broadcast never reaches one.
- The Scripts-X `.admin` dialog (`core/dialogs/admin/d_admin_main.scp` in the script
  pack) has a Players / Bots / All switch on its bottom bar, Players by default. Bot
  rows read `[bot]` in the account and name columns; the row opens the same player page
  (go, summon, tweak) as for a player. The pack lives outside this repository, so the
  switch is a change to the pack's own file.
- The web panel's Players page has the same Players / Bots / All switch (remembered in
  the browser) and counts players and bots separately. `PlayerInfo.IsBot` marks a bot
  row (no IP, client version `bot`), also across the host IPC bridge. A bot row has no
  Disconnect (there is no connection to close; the endpoint answers "not online" for a
  bot); Message (delivered to the bot's socketless client, so nobody reads it) and the
  detail / actions page work as for a player.

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
| `PLAYERBOT.NAME` | its own name, without `PlayerBotNamePrefix` |
| `PLAYERBOT.RIDER` | 1 when it rides, 0 when it walks (decided once) |
| `PLAYERBOT.MOUNT` | the creature it owns and rides |
| `PLAYERBOT.KNOWN` | players it has talked with (up to 24 serials), greeted by name |
| `PLAYERBOT.ROLE` | `pk`, `thief` or `-` (decided once) |

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
| `chat/<language>/token_*.txt` | word lists tokens are filled from: `token_item`, `token_mat`, `token_pet`, `token_when`, `token_when_now`, `token_when_earlier`, `token_dir` (compass points, north first, clockwise) |
| `intents.txt` | keywords that tell what a player's line asks for, the places a "where" can name, and the words that tell a language |
| `scenes.txt` | the exchanges bots act out near players |
| `towns.txt` | home town weights |
| `places.txt` | destination type weights |
| `mounts.txt` | share of riders and the creatures they ride |
| `shopping.txt` | what a bot buys when it runs short (item type, too-few count, how many) and the place types that sell it |
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

Every language directory under `chat/` is loaded. `PlayerBotLanguage` is the language
used when no listener has shown theirs; a category missing in a language falls back to
`en`. A line whose tokens cannot be filled is skipped.

## Behaviour

Each bot has a mode, acted on when its next action time comes:

- **Linger** at a destination: a few words now and then, a step or two about. Banks
  hold a bot longest.
- **Travel**: a route over the road graph (A* on nodes) from the nearest node to the
  destination's, then node to node and onto a stopping spot. Destinations are chosen on
  the piece of road the bot stands on; most stay in the home town. A fighter sometimes
  heads for a dungeon entrance; underground it goes room to room for a while, then to
  the way out. A bot away from home, with no road back, takes a moongate.
- **Fight**: anything attacking the bot is fought, a player in war mode against it
  included (striking back at an aggressor is no crime); set on in a guarded region by a
  criminal or a murderer, the bot also calls the guards. A monster a player is already
  fighting is left to that player; a fighter outside a guarded region
  also picks fights with monsters up to its nerve (a per-bot multiple of its own hit
  points). Melee closes in, archers and casters keep their distance, casters cast.
  Below a per-bot hit-point threshold it heals (Greater Heal, bandage, heal potion) or
  runs - for the nearest road node that takes it away from the threat, not straight at
  whatever wall is behind it. Poisoned, it cures (Cure, else a cure potion); a caster
  low on mana meditates between fights (the Meditation skill through its client).
- **Supplies**: a won fight ends at the body: gold, reagents, bandages, potions,
  arrows and bolts are taken off a monster's corpse through the client. A bot short of
  what it fights with (bandages for a healer, reagents for a caster, arrows or bolts for
  the bow it holds; `shopping.txt`) walks to the nearest place that sells it, opens the
  vendor's buy list and buys what its gold allows. No gold is made out of nothing.
- **Rogues**: `PlayerBotPkPercent` of the fighters are player killers. They keep out of
  guarded towns, wait about dungeon mouths, moongates, shrines and landmarks, and set on
  a lone player or bot that is not much tougher than they are (never staff, another
  rogue or a party member), with a minute or more between attempts; a kill is looted
  (weapons too). `PlayerBotThiefPercent` of the rest are thieves, given Stealing,
  Snooping and Hiding: in a guarded town every few minutes one steps up to someone,
  opens their pack, steals gold or something light with the skill's target cursor, hides
  and walks off. Murder counts, the criminal flag, the guards and the murderer's
  resurrection town all follow the normal rules.
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

### Names

Players see every bot's name with `PlayerBotNamePrefix` in front (`[Bot] ` by default;
empty for none). The bot's own name is kept in `PLAYERBOT.NAME`, so a changed prefix
replaces the old one the next time the bot comes online. Speech uses the name without
the prefix: a player saying "jarek" is talking to `[Bot] Jarek`.

### Talking

Bots talk only where a connected player can hear them (18 tiles); a bot nobody hears
says nothing. Once a second the engine looks at every connected player:

- **Scenes.** Every `PlayerBotChatPace` seconds on average (half to one and a half of
  it) a scene from `scenes.txt` is played by one or two lingering bots within 14 tiles of
  the player: a line about the place, a trade shout and the haggle that follows, a call
  for a group and its answer, real news and the reaction to it. One scene at a time near
  a player, one more for every eight bots around. `{name}` in a scene line is the other
  bot of the scene; the goods, the price and the news are kept for the whole scene.
- **Greeting on sight.** A bot a player walks up to (5 tiles) may say hello: by name
  (`friend_greet`) when it knows the player (`PLAYERBOT.KNOWN`), now and then a plain
  hello to a stranger. Each bot decides once per meeting.
- **A ghost.** A caster with the mana near a dead player says a `res_offer` line and
  casts Resurrection through its client.
- **A murderer.** A murderer among the bots is news (`red`); a bot nearby shouts
  `guards_call` in a guarded region, `red_alert` elsewhere.

Spoken to, a bot answers in the language the player wrote in (Turkish letters, else the
`lang.*` words of `intents.txt`). A player who names a bot (its first name) keeps that bot
for a minute: what they say next is answered by it without naming it again. A line that
names nobody may still be taken up by someone near: a greeting, "where is ...", a call
for a group, a trade shout at the start of the line, a question. What a line asks for:

| Intent | Answer |
|---|---|
| greet | `respond_greet`, or `friend_greet` by name to a player it knows |
| where + a place | directions from where the bot stands to the nearest place of that type, the town or the dungeon named: `respond_where` (`{target}` the word the player used, `{dir}` from `token_dir`, `{dist}` steps), `respond_where_far` past 400 tiles, `respond_where_here` within 4, `respond_where_unknown` |
| party / lfg | a fighter not in a party: `respond_party_yes` (it accepts the invitation that follows); else `respond_party_no` |
| res | `respond_res_no` from the living; a ghost asking near a caster is raised |
| wts + goods | `buy_notice` or `trade_not_buying`; a price the player names next gets one lower offer (`haggle_offer`), the next one `haggle_walkaway`. A bot carries no gold for a deal, so it never says yes |
| wtb | `respond_wtb_none` |
| thanks / bye / how are you | `respond_thanks` / `respond_bye` / `respond_howru` |
| a question | `respond_question` |
| anything else | `respond_name` when named, `respond_chat` to the player it is talking with |

Replies come after a short typing delay. Only real players are answered, so two bots
never talk themselves into a loop.

### News

Deaths are news (`DeathEngine.OnDeath`): a player killed by a monster (`death`), a
player killed by a player (`pk`), a monster of 200 hit points or more killed by a player
(`kill`), and a murderer seen among the bots (`red`). The last 48 stories of the last 45
minutes are kept in memory, not saved. A scene with `event=` tells one: its actor, other
and place fill the tokens, and `{when}` comes from `token_when_now` (under ten minutes) or
`token_when_earlier`. A bot the story is about tells the `_self` version.

### Where bots spend their time

- A new bot's home town is the one furthest below its share of the population by
  `towns.txt`. A bot of a town over its share, out of every player's sight, now and then
  moves house to a town under its share; its next trips take it there.
- Destinations are picked by the `places.txt` weight of their type: a bank holds a crowd,
  a tailor's shop sees one customer.
- `PlayerBotGravity` percent of town trips go to a place within 30 tiles of a connected
  player, unless `PlayerBotCrowd` bots already stand around that player.
- A bot at a bank, healer, inn or moongate stays up to three times longer while a
  player can see it, and a fighter in sight of a player goes hunting less often.

### Riding and gear

`mounts.txt` sets the share of riders (`riders 45`) and the creatures they ride. A rider
on foot in a guarded region gets one, as from the stables: the creature is made from its
CHARDEF, owned by the bot and mounted by a double click through the bot's client. Knocked
off by death, the bot walks back to its own creature when it is within 20 tiles; otherwise
it leaves the creature and gets another in town.

A bot that has its own things back after death takes off the resurrection robe. The robe
comes off as soon as a corpse run starts, so the things go back on where they came off.
A bot left with nothing at all gets a new kit in a guarded town: its archetype's @Create
is run on a stand-in, and what that wears, plus the supplies in its pack but not the gold,
go to the bot.

### Walking

A step goes straight at the target, or 45 degrees either side. Against an obstacle the
bot keeps it on one hand and walks its edge (Bug2) until it is nearer the target than
where it met it and the way ahead is open; a long way round with no gain switches hands.
A shut, unlocked door in the way is opened with a double click. A tile search
(`Pathfinder`) is used only to close in during a fight, within a few tiles, because a
search costs a walk check per neighbour of every node it opens.

The stopping spot of a trip is stepped on; one tile short does when someone else stands
on it, and three short once the last steps have stalled for four seconds. A leg that makes
no ground for five seconds within 40 tiles of its end gets a tile search (`Pathfinder`,
at most 800 nodes and 4 ms, sharing the per-tick search budget), and a second one at 25
seconds; at eight seconds the bot also goes to open the nearest unlocked shut door
within 8 tiles (once per leg), and a last leg still stalled after twenty seconds tries
another stopping spot of the same place.

A rescue more than three tiles from the leg's target teaches the way: twice on the same
stopping spot and the spot is not used again; twice on the same road link and the link is
left out of every route (`link|from|to` in the bad-spot file). A place with no usable
stopping spot left is not chosen at all. A bot rescued right by its target (a doorway
full of others) teaches nothing.

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

Every accepted step is broadcast to the players who can see the bot as a 0x77 move
(direction, `| 0x80` when running, hue, mobile flags, notoriety), exactly what the walk
handler sends after a player's step. The step goes through `MovementEngine` directly,
whose move hook only tells viewers about enter and leave range; without the 0x77 a
watching player saw the bot stand still and then jump when they moved themselves. A
step that lands anywhere but the next tile (a teleporter) sends no 0x77, as for a player.

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

The end of the line counts the talk and the rest: `watchers` (connected players),
`seen avg/max` (bots within sight of a player, sampled each second), `lines`, `replies`
(answers to players), `scenes`, `moved` (bots that moved house), `mounts` (creatures
given), `regears`, `robes` (resurrection robes taken off), `stalldoors` (stalled legs sent
to a door), `spotswitches` (last legs moved to another stopping spot), `stallsearches`,
`purchases`, `meditations`, `pkattacks`, `thefts`, `badlinks` and `searchtimeouts`
(searches given up at their deadline since start).

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
