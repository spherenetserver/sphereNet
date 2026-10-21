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

- **Linger** at a destination: a few words now and then, a step or two about (more often,
  every 3-8 s, while a player watches). Banks hold a bot longest. A caster low on mana
  meditates at a stop, never halfway along a road.
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
  points) that it can see: a monster behind a fence or a wall is no prey. Melee closes
  in, archers and casters keep their distance, casters cast. A foe the bot never stands
  in reach of (and in sight of, for a bow or a spell) for 20 seconds - one in a pen,
  across water, up on a ledge - is given up and left alone for five minutes; if it is
  the one attacking, the bot runs from it instead. A bot idling at a stop that is
  struck acts on it at once rather than at its next idle act.
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
- A bot at a bank, healer, inn or moongate stays once more (one to two and a half
  minutes) while a player can see it, and a fighter in sight of a player goes hunting
  less often.

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

A step goes straight at the target, or 45 degrees either side. When that is refused the
bot looks at why (the walk path's refusal reason):

- **Somebody on the tile** (`character_on_tile`: a player or bot it cannot push past
  without full stamina, a guard in a gateway): it waits a moment, three times, the way a
  player waits for someone to pass (now and then saying a `blocked` line), then steps to
  the side; a crowd that stays put for three seconds gets a tile search routed round
  the people. It does not start walking walls because of a person.
- **A wall, a fence, water** (`terrain`, `house`, ...): a shut, unlocked door ahead is
  opened with a double click; a way another bot already found along the same road link
  is followed (below); otherwise a tile search is asked for, and meanwhile the bot keeps
  the obstacle on one hand and walks its edge (Bug2) until it is nearer the target than
  where it met it and the way ahead is open.
- **Out of breath or held fast** (`stamina`, paralysis, a freezing cast): it waits.

**Tile searches** (`Pathfinder`, with the bot itself as the walker, so the search climbs
and drops exactly as its steps will) do not run in the bot's act. Requests are gathered
at the end of a tick; up to `PlayerBotPathSearchesPerTick` (16) of them run together in
the next tick's parallel phase, beside the creature path searches and under the same
read-only rules, and the paths are handed to their bots before they act. A search has at
most 2000 nodes (fewer for a short way) and 5 ms, and the whole batch 6 ms of the phase
(a search that has not started by then waits for its bot to ask again). For a bot a shut, unlocked door is a
way through (it is opened on reaching it) and, when the search was asked for because of
people, anybody standing on a tile blocks it. A failed search waits 3, 10, then 20
seconds before the same bot may ask again. A path is followed tile by tile: somebody on
it is waited for, a shut door on it opened, a diagonal round a corner that the step
refuses is taken as two straight steps, and anything else drops the path and asks for a
new one.

**Corridors.** A path found along a road link (node to node) is kept, starting at the
tile the search started from. The next bot stopped on that link joins it where it stands
and walks it with no search of its own. A kept path whose step is refused for good is
forgotten.

The stopping spot of a trip is stepped on; one tile short does when someone else stands
on it, and three short once the last steps have stalled for four seconds. At eight
seconds without ground the bot goes to open the nearest unlocked shut door within 8
tiles (once per leg), and a last leg still stalled after twenty seconds tries another
stopping spot of the same place.

A leg's progress clock restarts only on a real gain: any gain while walking straight, or
three tiles while walking a wall. A leg that makes none for a minute, or takes far longer
than its length allows (90 seconds plus 0.8 s per tile), lifts the bot onto the leg's
target (a rescue). A rescue more than three tiles from the leg's target teaches the way:
twice on the same stopping spot and the spot is not used again; twice on the same road
link and the link is left out of every route (`link|from|to` in the bad-spot file). A
place with no usable stopping spot left is not chosen at all. A bot rescued within three
tiles of its target teaches something only when a wall stopped it: one held up by people
(a doorway full of others) teaches nothing. A bot lingering right by a door steps
away from it, so it does not stand in the doorway others need.

Shops and banks are entered and left by their door: a route starts at the road node the
bot's stopping spot is linked to (`Arrivals[].Waypoints` in the data) and ends at the
node linked to the spot it is heading for, not at whichever node is nearest through a
wall. When the first leg makes no ground for 12 seconds the bot walks to the nearest
unlocked door within 8 tiles and opens it, then tries the next-nearest start nodes.
Learned spots and links are kept in `playerbots_badspots.txt` in the world save directory
and read back at start; deleting the file lets bots try every spot again.

### Other walks

Every walk that is not a trip gives up when it makes no ground, rather than holding the
bot for ever: its own horse (15 s, then the horse is left and another got in a minute;
a horse that will not be mounted, three tries; a bot on a trip does not turn back for its
horse), a thief's mark (10 s), the body of a kill (15 s), a vendor behind a counter (20 s),
its own corpse (60 s, then what is left on it is handed back, as for a corpse too far to
walk to), a party leader it never gets within reach of (30 s, then it goes its own way
for 30 s). Each walk keeps its own clock, so two walks taking turns do not reset each
other.

### Portals

A dungeon's way in, the stairs between its levels and a moongate all carry a walker on
the moment it steps onto the tile, and only then. At the first tick the engine looks up
each portal-type destination's tiles in the running world: map teleporters
(`[TELEPORTERS]`) within three tiles of its stopping spots, and gates and telepads that
lead somewhere; when the data names where the portal goes (`TargetX/Y`), only tiles that
go there count. A trip to a portal ends on its tile: the last leg is never stopped short
of, a tile somebody stands on is swapped for a free one of the same portal, and a bot
still on this side at the end steps onto the other tiles (or off and back onto the only
one) before the spot is written off. A rescue on a portal's last leg sets the bot down
beside the tile, not on it (a teleporter only answers a step).

The crossing is noticed the moment the step carries the bot: the trip ends there, the
place it landed at becomes where it is, a dungeon run starts once it is inside, and the
portals within two tiles of the landing (the way back is usually one tile away) are
stepped round for three minutes. Any other step that carries the bot off (a teleporter it
was only passing over) ends the trip where it landed; that tile is stepped round for
three minutes unless the trip is to that very portal.

A stair between dungeon levels that the data names but the world does not have is
portalless and never chosen; a dungeon mouth or a moongate with no portal is visited as
an ordinary place (walked into, or waited about: a world without gate items still has its
moongate squares). The server log lists them at start. A teleporter, gate or telepad that leads to a map the road network does not cover
is never stepped on, and a bot found on such a map is taken home.

### Off the roads

A trip needs a road node within 64 tiles on the bot's floor. A bot further out (on a
roof, a bridge, somewhere a gate put it) first walks back to the nearest node within 128
tiles on any nearby floor. A third trip that finds no road, or five minutes of them, lifts
it onto that node; a fifth, onto its home town's bank.

## Telemetry

Every 30 seconds the server log carries one line:

```
[playerbots] online=1000 tick avg=7682us max=26916us deferred=15584 steps=... searches=... (failed ..., slowest ...us) doors=... fights=60 deaths=2 reclaims=0 handbacks=1 rescues=... badspots=12 slowest act ...us (Travel->Travel) route ...us step ...us spawn ...us moves 127434 avg 62us max ...us at ...
```

The middle of the line counts the talk and the rest: `watchers` (connected players),
`seen avg/max` (bots within sight of a player, sampled each second), `lines`, `replies`
(answers to players), `scenes`, `moved` (bots that moved house), `mounts` (creatures
given), `regears`, `robes` (resurrection robes taken off), `stalldoors` (stalled legs sent
to a door), `spotswitches` (last legs moved to another stopping spot), `stallsearches`
(searches asked for on trips), `purchases`, `meditations`, `pkattacks`, `thefts`,
`badlinks` and `searchtimeouts` (searches given up at their deadline since start).

The end of the line is about getting stuck:

| Field | Meaning |
|---|---|
| `searches`, `failed`, `slowest` | searches run, found nothing, the slowest one |
| `searchq`, `denied`, `dropped`, `backlog` | searches asked for; asks refused by a bot's backoff; results that came too late to use; most requests waiting at once |
| `corridors`, `corridoruses`, `cornersplits` | kept road-link paths; times a bot joined one; diagonal path steps taken as two straight ones |
| `waits`, `sidesteps`, `doorway` | waits for somebody in the way; steps aside round them; steps out of a doorway |
| `portals`, `portalmisses` | portal crossings made; portal places written off after their tiles did not carry the bot |
| `noroute`, `stranded`, `maprescues` | trips that found no road; bots lifted back onto the roads; bots taken home from a map with no roads |
| `unreachable`, `giveups`, `faults` | foes given up as out of reach; other walks given up; acts that threw |
| `stalled [...]` | the most bots stalled at once (no ground for 10 s), by mode, e.g. `Travel:3 Fight:1` |
| `refusals [...]` | why steps were refused, most first, e.g. `terrain:812 character_on_tile:95` |

`tick` is the whole bot pass per server tick. Bot decisions share a budget of 12 ms
per tick; `deferred` counts the decisions that waited for the next tick (100 ms
later) because it was spent, and the next tick resumes where the last one stopped.
`moves` and their `avg` are the walk-path steps the bots took (one in sixteen is
timed), `rescues` the legs that had to be lifted, `slowest act` the single most
expensive decision of the window and `spawn` the costliest new bot (its CHARDEF
`@Create`). The first fifteen rescues of a window are logged with position, target,
destination, the last refusal and the bot's last decisions, which is where to look when a
spot keeps catching bots.

### Console

| Command | Shows |
|---|---|
| `PLAYERBOTS STALLS [n]` | the `n` (20) bots stalled longest right now: uid, name, position, mode, seconds, where it is going, the last refusal, whether it walks a wall, its tile path and its search state |
| `PLAYERBOTS TRACE <uid\|name>` | one bot's last 32 decisions with their age (trip chosen, search asked and found, path dropped and why, portal crossed, rescue, walk given up, refused step kinds) |
| `PLAYERBOTS NAVCHECK` | every portal-type place and whether the world has a portal there |
| `PLAYERBOTS NAVCHECK LINKS [LEARN]` | also a tile search along every road link and from every stopping spot to its node, with an online bot as the walker; `LEARN` leaves failures out of routes and writes them to the bad-spot file. Runs on the main loop: it takes a while on a full road network |

NAVCHECK writes its full report to `playerbots_navcheck.txt` beside the bad-spot file.
The web panel's Players page shows, for a bot, what it is doing in the version column
(`Travel -> Britain Bank`, with `(stalled 14s)` when it has made no ground for 10 s).

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
