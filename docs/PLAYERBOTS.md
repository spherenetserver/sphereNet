# Player bots

Server-driven player characters. Turned on with `PlayerBots=1` in `sphere.ini`; off by
default. Code: `src/SphereNet.Game/PlayerBots/`, wiring: `src/SphereNet.Server/Program.PlayerBots.cs`.

## What a bot is

A bot is an ordinary **player character** (`ISPLAYER`) with no network connection. The
server plays it through its own `GameClient` whose `NetState` has no socket, so
everything it would be sent is dropped. Attacking, casting, using a bandage or a potion,
opening a door, gathering, crafting, buying, selling and speaking all go through the same
`GameClient` handlers a connected player's packets reach, which means the rules a player
meets (swing timing, notoriety and crime, fizzles and reagents, bandage delay, skill
strokes and gains, vendor purses, `@Speech`, NPCs hearing speech, guards) apply to bots
unchanged.

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
| `PLAYERBOT.DEAD` | a death in progress: `corpse\|unix second\|ghost ms\|x,y,map` (the corpse, when it died, what was left of its time as a ghost, the last open ground it stood on); removed when it is raised |
| `PLAYERBOT.NAME` | its own name, without `PlayerBotNamePrefix` |
| `PLAYERBOT.RIDER` | 1 when it rides, 0 when it walks (decided once) |
| `PLAYERBOT.MOUNT` | the creature it owns and rides |
| `PLAYERBOT.KNOWN` | players it has talked with (up to 24 serials), greeted by name |
| `PLAYERBOT.ROLE` | `pk`, `thief` or `-` (decided once) |
| `PLAYERBOT.HAUNT` | a player killer's haunt (a destination name) |
| `PLAYERBOT.TRAIT` | its nature: `kind`, `shy`, `chatty` or `stingy` (decided once) |
| `PLAYERBOT.NERVE` | its nerve and the hit-point percentage it runs at: `nerve\|fleeAt` (`1.35\|28`), rolled once; a bot from an older save without it rolls once and writes it |
| `PLAYERBOT.BONDS` | how fond it is of players: `uid:score:minute` each (the unix minute last met), up to 24 |
| `PLAYERBOT.MEMORY` | things shared with players: `uid\|kind\|place\|minute`, `;`-joined, the last 12 |
| `PLAYERBOT.COMPANION` | the player it goes about with |
| `PLAYERBOT.HELD` | things it holds for players: `item:player` pairs |
| `PLAYERBOT.PLAN` | its day plan: `minute\|step\|why[:need]\|act[:place];act...\|death minute` (`29000000\|1\|Supplies:reagent\|Shop:Britain Mage;Hunt:Destard L1 Entrance;Sell\|0`): the unix minute it was made, the step it is on, why, the steps, the minute of its last death |
| `PLAYERBOT.DREAD` | hunting grounds it died at: `name:minute`, `;`-joined, the last 4 |
| `PLAYERBOT.WORK` | the job in hand: `job\|skill\|place\|sale type` (`Gather\|Mining\|MiningSpot 418\|Normal`); removed when the job ends |
| `PLAYERBOT.KILLERS` | the kinds of creature that killed it: `defname:minute:strength` each (the CHARDEF defname, the unix minute, how strong the bot was then), `;`-joined, the last 6 |
| `PLAYERBOT.BEATEN` | the kinds it has killed and how often: `defname:count`, `;`-joined, up to 8 (the least beaten makes room) |

A bot from an older build may carry `PLAYERBOT.RUNE` (the one rune it was given); it is
no longer read: its runes are whatever its pack holds.

After the world loads, `PlayerBotEngine.AdoptSaved` brings every `PLAYERBOT` character
back online where it stood, and a bot that had a goal sets off for it again. A bot that
was on its way to a step of its plan goes on with it (one on its way to a shop shops when
it gets there, one on its way to a hunt hunts) and its next stay takes the plan's next
step; the planner's clocks (company, time in one town) start over. A trip to work sets
no goal: the bot stands where it was, its first stay ends at once and the job is taken up
from `PLAYERBOT.WORK` (where it stands, when it is at the place, else it walks there); the
tile it worked and the recipe it was making are not saved. A ghost goes on from where its
death had got to (`PLAYERBOT.DEAD`, below). The population is then filled up to
`PlayerBotCount`, one new bot at a time. Lowering the count logs the surplus out; they
stay in the save as logged-out players. With `PlayerBots=0` the saved bots are simply
logged-out characters.

## Data (`PlayerBotDataDir`, default `playerbots/`)

| Path | Contents |
|---|---|
| `nav/waypoints.json` | road network: named nodes (classic map 0 tiles) and their links |
| `nav/destinations.json` | banks, shops, inns, healers, moongates, dungeon entrances and rooms, mines, groves, docks, forges; each with its nearest node and the spots a visitor stops on |
| `chat/<language>/<category>.txt` | lines a bot says, one per line, `#` comments, `{token}` placeholders; each category is named below where it is said. A line written as a gesture (`*yawns*`) is said as an emote |
| `chat/<language>/token_*.txt` | word lists tokens are filled from: `token_item`, `token_mat`, `token_pet`, `token_when`, `token_when_now`, `token_when_earlier`, `token_dir` (compass points, north first, clockwise); `Key=word` lists for `token_activity` (`Hunt=hunting`), `token_need` (`Load=my pack is full`, `Supplies.reagent=out of reagents`) and `token_work` (`Mining=mining`, `Smelt=...`, `Sell=...`) |
| `intents.txt` | keywords that tell what a player's line asks for, the places a "where" can name, and the words that tell a language |
| `scenes.txt` | the exchanges bots act out near players |
| `towns.txt` | home town weights |
| `ai.txt` | the brain: the model file, CPU threads, reply length, how long a bot waits for a reply, queue, history, temperature |
| `places.txt` | destination type weights and room (how many bots a place of the type holds) |
| `plans.txt` | the planner: drive weights by class, nerve, nature and role (`weight`), what each activity does for each drive (`does`), base worth (`base`), gold a class keeps (`keep`), rough prices of needs (`price`), how much each need matters, alone and when the bot has another answer for it (`need`), activities a role never takes up (`forbid`), and the choice's knobs (`set travel risk fit temperature top margin stick urgent minsteps maxsteps stale social boredom homesick wear pursefull failrest`). Every value is also built in; a line replaces it |
| `work.txt` | the trades: `gather <skill> <tool item type> <place type>...`, `smelt <place type>...`, `craft <skill> <tool item type> hand\|pack <material type,...> <place type>...`, `sell <item type \| prefix* \| *> <place type>...` |
| `mounts.txt` | share of riders and the creatures they ride |
| `shopping.txt` | what a bot buys when it runs short (item type, too-few count, how many) and the place types that sell it: supplies (bandages, reagents, arrows, bolts, runes), potions by the spell in the bottle (`Potion:GreaterHeal`, `Potion:Cure`, `Potion:Refresh`), `food`, and the tools and materials of the trades (`pickaxe WeaponMacePick 1 1 VendorWeaponer ...`) |
| `bonds.txt` | how a bot grows fond of a player: the levels, the points for talk, favours, hunts, trades, presents, being raised, helped (`helped`, `helped_gap`), attacked, snooped on or robbed (`snooped`, `stolen`), how a bond fades, and the weights of the bots' natures |
| `archetypes.txt` | archetype CHARDEF defnames with weights |
| `scripts/*.scp` | the archetype CHARDEFs; loaded with the script pack when `PlayerBots=1` |

In `work.txt` skills are SkillType names, items ItemType names and places
`destinations.json` types. `hand`: the craft's tool must be held (taken up with a double
click). The materials are what the craft's recipes may be made of. `sell Weapon*` is
every item type whose name starts so; `*` is everything else.

On load every node's Z is re-seated on the standable surface the local map data has
at that point (a node recorded at a placeholder height lands on the floor) and a node
with no surface is dropped. A destination point or stopping spot with no surface on its
own tile is seated on the nearest standable tile within two (the source data and this
map can disagree by a tile at a wall line), so no bot is sent to stand inside a wall. Links are made two-way. The graph's connected pieces are
labelled: islands and most dungeon levels are pieces of their own.

Archetypes are pack CHARDEFs. `ON=@Create` dresses and skills the character exactly as
for an NPC (keys after an `ITEM=` line go to that item, so `ADDCIRCLE` fills a spellbook);
the engine then makes it a player character. The townsfolk archetypes
(`scripts/playerbots.scp`) come with one whole trade each - miner and smith (pickaxe,
smith's hammer), lumberjack and carpenter (hatchet, saw), fisher and cook (fishing pole,
frying pan, dagger), tailor (sewing kit, scissors, cloth); a warrior carries a weapon for
each of its two weapon skills and refresh and strength potions beside heal and cure; an
archer a dagger (with a little Fencing), heal, refresh and agility potions; every kit a
little food (bread, cheese, a muffin or a chicken leg) and a glass of water or milk.

Every language directory under `chat/` is loaded. `PlayerBotLanguage` is the language
used when no listener has shown theirs; a category missing in a language falls back to
`en`. A line whose tokens cannot be filled is skipped.

## Behaviour

Each bot has a mode, acted on when its next action time comes:

- **Linger** at a destination. Where a player watches, it does what people do at such a
  place (see Presence and manners); unseen, a step about now and then. Banks hold a bot
  longest. A caster low on mana meditates at a stop, never halfway along a road. When
  the stay ends, what it does next is the planner's choice (see A day's plan).
- **Travel**: a route over the road graph (A* on nodes) from the nearest node to the
  destination's, then node to node and onto a stopping spot. Destinations are chosen on
  the piece of road the bot stands on; most stay in the home town. A hunt at a dungeon
  goes on underground room to room for ten to twenty-five minutes while the bot is above
  half its hit points, then to the way out; a full pack or supplies run out end it early.
  A bot away from home, with no road back, takes a moongate that carries a
  walker; where the moongates only send people by a menu, it is taken home (recalling on
  its rune when it has one, else lifted out of sight).
- **Fight**: anything attacking the bot is fought, a player in war mode against it
  included (striking back at an aggressor is no crime); set on in a guarded region by a
  criminal or a murderer, the bot also calls the guards. A fighter with nothing in hand
  (lingering or on the road: not over a body, at a counter, at work or on the way back to
  its corpse) and out of a guarded region also picks fights with monsters within eight
  tiles that it sizes itself up against and can see: a monster behind a fence or a wall is
  no prey, nor, for a bot with a blade, one more than 16 high above or below it. A monster
  a player or another bot is already fighting is theirs, though a bold bot may join in.
  Where it hunts is the planner's choice; how it fights, when it breaks off and how it
  gets away is in Fighting below. A foe the bot never stands in reach of for 20 seconds -
  one in a pen, across water, up on a ledge - is given up and left alone for five minutes;
  if it is the one attacking, the bot runs from it instead. In reach is where a blow
  lands: within the weapon's range, in sight and no more than 16 high apart, so a foe on a
  ledge right above the bot is out of reach and is not taken up again while it stands
  there. A bot idling at a stop that is struck acts on it at once rather than at its next
  idle act.
- **Supplies**: a won fight ends at the body: gold, reagents, bandages, potions,
  arrows and bolts are taken off a monster's corpse through the client. A bot short of
  what it fights or works with (bandages for a healer, reagents for a caster, arrows or
  bolts for the bow it carries, heal, cure and refresh potions for a fighter that does not
  cast, the tools and materials of its trade, food; `shopping.txt`) has a need the planner
  weighs; when it shops, it walks to the nearest place on its road that sells the thing,
  opens the vendor's buy list and buys what its gold allows. A tool counts in the hands as
  well as the pack, and only one its trade can be plied with (see Working life: a shovel is
  no pickaxe). No gold is made out of nothing. Of the things it is short of, the one that
  matters most to it is its need (`need` in `plans.txt`: a heal potion weighs little to a
  bot with bandages and Healing, a refresh potion little to anyone, reagents everything to
  a mage). A shop where nobody turns out to sell the thing, or whose vendor cannot be
  walked up to or has none left, is not walked to for it again for half an hour (by any
  bot); a need nobody on the bot's road sells within 1200 tiles (`NeedShopRange`; a blank
  rune within 300) is no need to the planner: it neither stands before the next one, nor
  holds a hunt back, nor sets a plan aside.
- **Work** and **Sell**: a tradesman gathering, smelting or crafting, and any bot taking
  goods to a vendor (see Working life).
- **Rogues**: `PlayerBotPkPercent` of the fighters are player killers. Each is given a
  haunt (`PLAYERBOT.HAUNT`): a dungeon mouth, moongate, shrine, landmark, adventure area
  or graveyard outside every guarded region. It takes no trips and makes no plans: it
  roams the road nodes
  within 40 tiles of the haunt, stands a while, and looks about every second or two. It
  never steps into a guarded region (the step is refused, `guard_zone`), gives up a
  victim that reaches one, and one found in one or more than 120 tiles off is set down
  at its haunt. It attacks the nearest blue player or bot (innocent to it, NOTO_GOOD) it
  can see within 14 tiles that stands outside the guards' reach (never staff, another
  player killer or a party member) and goes through the body (weapons too). It runs no
  bank, rune, recall, shopping, work or party errands. Haunts are shared out: a new player
  killer takes the haunt fewest others hold. It leaves alone anyone whose skill total
  is under `PlayerBotPkSpareSkill` points (new characters) and, for
  `PlayerBotPkSpareMinutes`, anyone a player killer has just killed; after a kill it
  moves on to another roam point. Killed, it is a ghost for five to twelve seconds, then
  turns out of war mode (the living no longer see it), is moved to an unseen spot of its
  haunt and waits there `PlayerBotPkRespawnSec` (one to two times it); it is raised at
  another spot of the haunt (a roam point and two to eight tiles aside, out of the
  guards' reach, off any teleporter, unseen if it can be) with its archetype's kit (as a
  bare bot gears up), so player killers killed together come back apart. What it died
  with stays on the corpse for its killer. `PlayerBotThiefPercent` of the rest are thieves, given Stealing,
  Snooping and Hiding: in a guarded town every few minutes one steps up to someone,
  opens their pack, steals gold or something light with the skill's target cursor, hides
  and walks off. A thief plans its day like anyone else but never hunts, works or sells.
  Murder counts, the criminal flag, the guards and the murderer's
  resurrection town all follow the normal rules.
- **Death**: half a minute to a minute as a ghost walking about the body in war mode - a
  manifested ghost, which the living can see. Then a ghost with a healer within 150 tiles
  (`GhostWalkRange`) and a road to it no longer than twice that walks there - manifested,
  at a run, as any trip - and is raised by what the world offers a ghost: an ankh within
  fourteen tiles of where it arrives is used with a double click; else it stands within
  two tiles of the healer, pleads once (`ghost_plea`) and waits for the healer's own
  resurrection (a healer raises a ghost within three tiles). A criminal ghost that is no
  murderer does not go among the guards while the flag lasts: it stays about its body,
  unseen and out of war mode, until the criminal timer has run out, then goes on as any
  ghost (raised in a guarded town while still criminal, it was struck down again by the
  guards at once); one whose flag outlasts ten minutes is raised in the murderers' town. A
  murderer (a healer turns them away), a ghost with no such road, one whose walk takes too long (two
  minutes plus 1.2 s a tile) and one nobody has raised after 25 seconds at the healer's
  (`GhostWaitAtHealerMs`) is carried off the way a stuck bot is, out of sight (see Out of
  sight), and raised there: a player killer at its haunt (above), any other murderer (and
  a criminal still flagged) at the anchor of the `PlayerBotRedRespawn` region (Delucia by
  default), anyone else at the
  healer with the shortest road from the open ground it belongs to - where it stands, or,
  for a death underground, the last open ground it stood on (the mouth it went in by; for
  a ghost from an older save, the mouth whose far side lies nearest). A dungeon's own map
  coordinates decide nothing. A death is noted on the character (`PLAYERBOT.DEAD`); a
  ghost taken back from the save goes on from where its death had got to: the same
  corpse, what is left of its hovering, no second death in the log or the count.
  A corpse within
  `PlayerBotReclaimRange` tiles (40) is walked back to: the bot opens it (only an opened
  container can be reached into) and takes its things off one at a time through its client -
  picked up, worn on the layer they came off (`EQUIPLAYER`) or dropped into the pack - as T2A
  had no self-loot; it gives up four minutes after reaching the body. A farther corpse is
  walked back to as well - at a run - when it lies on the same piece of road within
  `CorpseRunRange` (600) tiles, or behind a chain of at most four portals (a dungeon mouth,
  its stairs) whose first lies within that reach; each portal crossed, the next is taken,
  then the body. Seven minutes without getting there, six portals, or no such way: what is
  still on the body is handed back by the server (same layer-or-pack rule) and the corpse
  is removed. Until then the corpse is an ordinary corpse: other players can loot it, and
  what they take is not handed back.
- **Party**: a player's party invitation is accepted after a moment, through the same
  accept sub-command a client sends. In a party led by a player the bot keeps within three
  tiles of the leader, joins the fight the leader picks (a player only when that is no
  crime: one not blue to the bot), turns on whatever attacks the leader, and leaves the
  party when the leader has been out of reach for two minutes. Every second and a half
  it looks over the members within 10 tiles and in sight: a ghost is raised (a caster
  with Resurrection), a poisoned member cured, the worst wounded under 70% healed -
  Greater Heal or Heal from a caster, else a bandage from a bot with Healing 50 standing
  within two tiles - all through its client. Orders said aloud or in party chat by a
  party member (`stay`, `follow`, `leaveparty`, `status`, `heal` in `intents.txt`) go to
  the bot named in the line, else to every bot of the party: wait here (ten minutes, still
  standing up for the leader), follow again, leave the party, tell hit points and mana
  (`party_stay`, `party_follow`, `party_leave`, `party_status`). Party chat reaches the
  bots through `PartyManager.OnPartyMessage`; anything else said there is answered in
  party chat (0xBF 0x06 sub-command 4, as a client sends it) by the bot named, the one
  told privately, or one of them.
- **Trade**: a bot trades through the secure trade window, its side moved by its
  client (pickup and drop) and agreed with the window's accept. It values goods by their
  make value (the definition's VALUE at the item's quality) times the amount; it buys at
  60% of that and sells at 130%, and spends at most half the gold in its pack. An item
  dropped on it opens a trade: it puts in what the item is worth to it and agrees after
  a moment; it refuses (`trade_worthless`) something worth nothing and says when it
  cannot afford it (`trade_too_dear`). Selling, it says its price (`trade_price`), agrees
  once the gold in the other window (and any virtual gold offered) covers it, and puts in
  change for gold over the price. Every change to either window is looked at again, and
  the window clears both check marks on any change, an item taken out included. "wtb x"
  to a bot carrying such a thing gets its price (`trade_wtb_have`); it walks over and
  opens the trade with the piece in it. "What are you selling" (`selling`) gets three
  pieces with prices (`trade_stock`). Its stock is what its pack holds that is worth
  something, apart from gold, supplies, spellbooks, bags, runes, keys and blessed things;
  from a monster's body it also takes anything worth 20 or more. A trade nobody changes
  for 90 seconds, or a fight, closes the window. A "wts" from a player is answered with
  "show me in a trade" (`trade_show`) by a bot with 100 gold to spend.

### A day's plan

Code: `PlayerBotEngine.Planner.cs` (drives, options, the choice, the plan, explaining
it), `BotPlan.cs` (the plan, its tag, `plans.txt`). When a stay ends a bot does what a
person would do next, for a reason it can say. The Work, Sell and Supplies parts, the
bank errand and the trip carry out what it chooses.

#### Drives

It reads what it wants from its real state - its **drives**, each an urgency from 0 to 1:

| Drive | From |
|---|---|
| `money` | gold carried and banked against what its class likes to have (`keep`), plus what the thing it is short of will cost (`price`) |
| `supplies` | the thing it is short of that matters most to it (`shopping.txt`; `need` in `plans.txt`): reagents to a caster, arrows to an archer, bandages to a healer with no other way to mend weigh most; a potion the bot has another answer for (bandages and Healing for a heal, Healing and Anatomy 60 for a cure, a spell), a refresh potion, a blank rune least; tools 0.8, materials 0.6, food 0.4; the fewer it has, the more; half as much when it has no gold for it at all. A need no shop on its road sells counts nothing |
| `safety` | hit points under 90%, a caster's mana under 40%, a death in the last half hour, a fight in the last minute and a half |
| `load` | goods worth a trip to a vendor (see Working life), their worth; a pack over half its weight limit |
| `purse` | gold carried over `BankDepositAbove` (600): 0.3 there, full at `pursefull` (1500) |
| `growth` | its main skill (its best fighting skill, or its best trade) short of 100.0 |
| `social` | time since a player stood near it or it stood among a crowd (`social` seconds to full) |
| `adventure` | time in one town (`boredom` seconds to full), and the same activity taken up again |

Who it is weighs them (`weight` lines): a mage cares more for its supplies, a warrior for
adventure, a crafter less; a bold bot (nerve 1.45 or more) less for safety and more for
adventure, a timid one (under 1.05) the other way; a chatty bot wants company far more, a
shy one far less; a stingy one minds money and a full purse; a thief wants crowds.

#### Activities

| Activity | When it is an option | Done by |
|---|---|---|
| `hunt` | a fighter: the hunting ground (dungeon mouth, adventure area) on its road that fits it best | the trip; a dungeon run below |
| `work` | a tradesman with work it can do now | the Work part |
| `sell` | goods worth a trip | the Sell part |
| `shop` | short of something, with the gold a vendor takes, a shop for it on its road | the shopping trip |
| `bank` | gold to put in, or the gold for a purchase in the bank but not to hand (when vendors take only the pack) | the trip; the bank errand runs on arrival |
| `rest` | hurt, low on mana or lately dead: the inn, tavern or healer's of its town (here, when it stands at one) | a stay of two to four minutes (it heals and meditates as at any stop) |
| `social` | always: a place near a player who is not already crowded (when `PlayerBotGravity` is above 0), else a bank, inn, tavern or healer's of its town by weight and room; where it stands only when a player is there or three bots are | the trip, or a longer stay where it is |
| `wander` | always: a place picked by weight and room (`places.txt`, gravity, now and then a moongate; see Where bots spend their time) | the trip |
| `home` | in another town than its home | the trip home, or a recall on its rune when no road leads there |

Each is scored: `base` + the sum over the drives of (urgency x weight x what the activity
does for it, `does`) - the road to it (`travel` per 100 tiles; to a hunting ground by the
road graph, in town by the straight line). A `does` can be negative: a hunt wears on
safety and fills the pack, and wants supplies met first, so a mage with no reagents does
not go hunting and a bot with a pack full of loot sells first. Only what it fights with
(bandages, reagents, arrows, bolts, potions) holds a hunt back: a tool, a material, food or
a blank rune does not. With a pack full of goods the bank counts half its purse: the
takings go in with the rest, one trip. A hunting ground also
counts its fit and its risk: what bots have met there (a running mean of the threat of
the creatures fought, as the bots size creatures up; see Fighting) against the bot's own
strength - a fair fight for its nerve fits best, a walkover teaches little, a stronger
foe is a risk - less its deaths there (`risk`, by nerve), less 0.5 for a ground the bot
itself died at in the last three days (`PLAYERBOT.DREAD`). A ground nobody knows yet
counts as a middling fit. What bots have met at each ground is kept in memory only.

The bot picks among the best three (`top`) within `margin` (0.2) of the best by a softmax
(`temperature` 0.08): the best most often, a close second now and then, so bots with the
same needs do not all walk the same way.

#### The plan

The pick is the first step of a **day plan** of two to four steps (`minsteps`,
`maxsteps`): what the step is expected to do to the drives is played forward (the drives
it serves are met, the ones it wears on grow by `wear`), and the best activity for what
is left comes next, no activity twice. A step can be one only an earlier step makes
possible: the gold out of the bank buys, a hunt or a shift brings goods to sell, a sale
gold to bank. So a poor mage plans `shop > hunt > sell`, one whose gold is in the bank
`bank > shop > hunt`, a fighter back with a full pack `sell > bank > hunt`, a wounded one
`rest > ...`.

When the next stay ends the plan's next step is taken when it is still about as good as
anything (`stick` 0.15 is added to it: a bot does not change its mind over a hair), unless

- a need, its wounds, a full pack or a heavy purse is pressing (urgency x weight at
  `urgent`, 0.75), the step does nothing for it, and another option that does is worth
  clearly more than the step (`stick` above it): badly hurt, a rest; out of arrows, a shop.
  Company, something new, growth and money never set a plan aside: they are what the next
  plan is made of; or
- something took it away from the plan: a death, a favour, a companion, a player it
  leads, being carried off by staff or a script, its plans reset after faults, or
- the plan is older than `stale` (90) minutes or used up.

Then it makes a new one. A step that will not start (no road, nobody buys, no place for
its work within reach) is dropped and the next best tried, twice; failing that, a place is
picked as for `wander`. An activity that would not start is not offered again for
`failrest` (600) seconds, so it does not come out on top at every stay. Underground
nothing is planned: the dungeon run goes room to room, and a full pack or supplies run
out end the run early. A player killer keeps its haunt; a party member, a companion and
a bot doing a favour or leading a player are not planned for.

Nothing of this runs per tick. A choice is made where a stay ends (one to five minutes
per bot; a few seconds after a job ends) and on a console or chat question: the pack is
read two or three times, the destination list scanned a few times, the bot's strength
kept ten seconds. Road distances to hunting grounds come from fields of distances to a
node, one Dijkstra over the road graph each, kept per node (at most 96, dropped when a
road link is learned bad), at most two new ones per choice; past that the straight
distance stands in.

#### Asking it

"What is your plan" (`plan` in `intents.txt`) gets `respond_plan` with `{now}` (the step
it is on, from `token_activity`), `{next}` (the step after it), `{goal}` (the place, when
the step has one) and `{why}` (the drive that made the plan, from `token_need`:
`Supplies.reagent=...` for the thing it is short of, else `Supplies=...`, `Money=...`); a
bot with no plan, or going about with a player or a party, says `respond_plan_none`.
"What are you doing" on the way to a shop, the bank, a rest, company or home is answered
with the reason too (`respond_doing_plan`). The brain is told the plan and its reason
with the rest of what the bot is doing.

### Working life

Code: `PlayerBotEngine.Work.cs` (trades, gathering, smelting, crafting),
`PlayerBotEngine.Sell.cs` (selling to vendors), `BotWork.cs` (the job in hand). A bot with
a trade earns its keep the way a player does. Nothing is written into its pack or its
skills: ore, logs, fish, ingots and goods come out of the engine's own gathering,
smelting and crafting, gains included, and the gold out of a vendor's purse.

A bot has a trade when it has the skill at `WorkSkillMin` (30.0); the tool it lacks is a
need like any other and is bought. The tool is one the trade can be plied with, as the
pack's skill script asks: where a gathering skill's script reads the tool from the hand
(`SRC.WEAPON`, as the Scripts-X Mining and Lumberjacking `@PreStart` do with
`<SRC.WEAPON.USESCUR>`), only a tool that goes into a hand (its ITEMDEF's or tiledata's
layer) and has charges left counts, is kept and is bought - the pack's `i_shovel` is a
`t_weapon_mace_pick` that no hand takes, and digging with it swings the dagger in hand
("The tool is out of charges"). Such a tool is taken into the hand with a double click
before the first swing (as the client's own double click equips it); a smith's hammer the
same way. The script is read once per skill. A trade whose job would not start twice in a
row (the tool not taken up, the swing or the craft never starting, "no tool") is left
alone for half an hour: the planner offers no work for it meanwhile. When the planner picks `work`, the bot carries ore to
a forge first, else takes up a craft it has the tool and materials for, else a gathering
shift with the trade it is best at (the next one when that has no place within reach).
When the planner picks `sell`, it takes its goods to a vendor. A job that ends leaves the
bot standing three to nine seconds, then the planner chooses again - which is what takes
ore on to the forge, ingots on to the anvil and goods on to a vendor. Player killers,
thieves, bots in a party or going about with a player, bots doing a favour and bots
underground neither work nor sell; a favour asked, a party joined or a player to trade
with ends the job in hand.

- **Gathering.** The bot walks to a mine, a grove or a dock (within `WorkTripRange`, 400
  tiles; now and then the second nearest, so a town's miners do not all stand in one pit),
  finds a tile of the skill's resource within seven tiles of itself (rock, a tree, water -
  read as the gathering engine reads it: an item lying there, a static of the map, the
  land's own TYPEDEF; looked for at most every three seconds) and works it: a double click
  on the tool, the tool's target cursor answered on the tile, the skill's own strokes
  waited out. On foot (a rider dismounts and mounts again when the shift is over). A tile
  that says "there is nothing here" or "you cannot reach that" is left for another; a tile
  swung at ten times with nothing to show too. Between two swings it takes a breath
  (0.7-2.2 s), now and then a longer look about and a word when someone is by
  (`gather_talk`, `fishing`). The shift ends when the pack is at 75% of the weight limit
  (`WorkLoadPercent`), after a rolled 5-12 minutes, or when the tool is gone. A place where
  fourteen tiles in a row gave nothing, or nothing to work is found after five walks
  further in, is given up and left alone by every bot for half an hour. A place the data
  gives no stopping spot (a grove or a mountain side drawn as an outline only) is
  travelled to as far as its road goes; from there the bot walks on by what it sees,
  toward the inside of the outline.
- **Smelting.** A bot that mines for a living carries its ore to a forge and double
  clicks each pile beside it. A pile its Mining is too low for is not carried there (it is
  sold instead); nor is a pile a forge took nothing of, for two hours; and all its ore is
  goods when no forge is within reach.
- **Crafting.** A bot with a craft, its tool and materials for something works where the
  craft is plied (within `WorkErrandRange`, 300 tiles: a smith and a cook at a forge, a
  tailor at the tailor's, a carpenter at the carpenter's). It takes a smith's hammer into
  its hand with a double click, stands within reach of the forge or fire the engine asks
  for, and makes what picking a piece in the craft menu starts (the menu's MAKEITEM:
  `BeginPendingCraft`, with the pack's @SkillStart / @SkillStroke / @SkillMakeItem and the
  skill's own DELAY). What it makes is a piece the pack's own SKILLMENU of that craft
  offers (every MAKEITEM under `sm_blacksmith`, `sm_carpentry`, `sm_tailor_cloth` /
  `sm_tailor_leather`, `sm_cooking`), made of its trade's materials alone, within its
  skill, and of those the hardest it has the skills, tools and materials for - so it
  trains. Plain work on a pile (boards of logs, steaks in the pan: difficulty 0, a result
  that stacks) is done four to ten at a time, as MAKEITEM's amount does it; a piece worth
  a quality roll one by one. A cook cuts its catch first: the blade double clicked, its
  cursor on the fish. A crafter keeps the materials of its craft and - when it has the
  craft's tool and no gathering trade it can ply from where it is - buys them. A recipe
  is chosen from a list built once per craft, measured against the pack's materials, with
  the engine asked about at most twelve; the choice is kept until the materials run out.
- **Selling.** Goods are worth a trip at 100 gold of make value (`SaleWorthMin`), five
  kinds (`SaleCountMin`), or 60% of the weight limit. They are taken to the nearest shop
  (within 300 tiles) of the kind that buys their most valuable kind (`work.txt`). There
  the bot asks every vendor within twelve tiles what it offers (the vendor's own sell
  list), walks up to the best, opens the list and sells up to twelve lines at a time, as
  a hand fills the list in. What a shop would not buy is learned piece by piece (by its
  definition): no bot carries that piece to that shop again for half an hour, and a piece
  of a bot's own make that found no buyer is, for two hours, made only when nothing else
  can be - so craftsmen settle on what the town's vendors take. A shop whose vendor could
  not pay, or cannot be walked up to, is left alone for that kind of goods for half an
  hour. The gold lands in the pack and goes the way all its gold goes: to the bank when
  there is much of it.

What a bot sells: a fighter, its spoils (anything worth something that is not its
supplies, a scroll, gear that came off it, or its only weapon); anyone else, only what it
made itself and the raw stuff it gathered. Never the tools of its trades, the materials
of a craft it can ply, or anything it holds for a player. A piece nobody in reach buys
stays in the pack: nothing throws goods away or puts them in the bank. Cooked food and
whole fish sell only where a vendor's buy list has them.

What a bot learns at work is kept in memory only (a restart forgets it, and every place
is tried again): places useless for a job, for every bot (nothing to work, no forge, a
vendor that cannot pay or be reached) or for one bot (no road, a job that would not start
for it three times; a trade that would not start twice in a row); pieces a shop did not
buy; piles a forge would not melt. A bot that
lost everything and was given a new kit gets the tools of whatever trade the kit rolls,
not of the trade its skills are; it buys its own tools again.

**Selling talk names real goods.** `{item}` is a piece of the bot's goods (of its stock,
when it has nothing it would take to a vendor), `{mat}` a material it carries with the
amount in `{count}`; a scene's goods are its first speaker's. A bot with nothing to sell
keeps the words of the token lists, and so does any line about wanting a thing
(categories `wtb*`, `buy_*`, `*_buy`, `*_need`). Work talk: `gather_talk`, `gather_haul`,
`gather_deliver`, `fishing`, `smith_talk`, `tailor_talk`, `carpenter_talk`, `craft_talk`.

Nothing of this runs per tick: a working bot acts when its next action is due, like any
other. One look for a tile is a single bounded pass (the items within seven tiles, then
up to 224 tiles' statics and land type, stopping at the nearest hits); the forge look is
one pass over 17x17 tiles. Walks use the ordinary step and its queued tile search.

### Fighting

Code: `PlayerBotEngine.Fight.cs` (the act of a fight), `.Tactics.cs` (reading the fight,
breaking off, getting away, an archer's footwork), `.Spellcraft.cs` (a mage's
repertoire), `.Arms.cs` (weapon, shield, potions, bandages, ammunition), `.Prey.cs`
(sizing a creature up, memory, other people's fights); the state of one bot's fight is
`BotTactics` (`BotMind.Tac`). Every blow, spell, potion, bandage and change of weapon is
the bot's own client's: the attack request, the cast and its target cursor, the double
click, the equip and unequip macros, the skill button. Nothing writes hit points, mana,
skills or items. Player killers and thieves fight by the same rules; a player killer's
victim gets the same repertoire a monster does.

#### Sizing a creature up, and remembering

A bot weighs a creature as a player reads it (from the live character): the damage of
its natural attack or of the weapon it holds, how often it swings and how well it hits
(its weapon skill), whether it casts (its spells, its Magery and mana) or poisons, its hit
points and armour. Against that it sets what it brings (worked out every ten seconds):
the damage a second of the weapon in its hand at its skill - or of the best spell its
book and magery allow, with the damage its present mana pays for -, its hit points and
armour, and what it carries to heal with. It takes a fight when the time it needs to
bring the creature down is no more than the time the creature needs for it, times 0.6 of
its nerve (0.8-2.0, per bot, rolled once and kept in `PLAYERBOT.NERVE` with the hit-point
mark it runs at): a careful bot wants twice the time, a bold one takes an even
fight. A caster whose mana pays for less than 70% of the creature leaves it alone unless
it can also brawl. Of the monsters about, the four nearest are weighed and line of sight
is tested last; a bow with nothing to shoot hunts nothing.

- **What killed it** is remembered (`PLAYERBOT.KILLERS`) and that kind is left alone for
  a week, unless the bot has become a quarter stronger than it was then. Set on by such
  a kind all the same, it defends itself but breaks off sooner (its hit-point mark is 15
  higher).
- **What it has beaten** (`PLAYERBOT.BEATEN`) is what it goes for first: such a kind
  counts as four tiles nearer and may be a quarter tougher.
- **Somebody else's fight.** A monster that is fighting a player or a bot is theirs. A
  bold bot (nerve 1.45 or more) joins: another bot's fight always, a player's when the
  player is under 70% or did not pick the fight - never for a criminal or a murderer,
  never a kind it dreads, and only a fight two can win (`combat_assist` line). A timid
  bot (nerve under 1.05) with such a fight within six tiles that it would not take on
  itself steps away from it for a few seconds.

#### Reading the fight

Every second and a half a fighting bot looks about it (ten tiles; one sight test, on the
one candidate it would turn to):

- **Who is on it.** Everything that targets it (a player only in war mode) is counted.
  From the attacker log it keeps what each has done to it lately (halved at every look,
  the new damage added).
- **A better foe.** It turns to another attacker when that one is nearly down (a quarter
  of its hit points) and within reach - finished first; when it hurts clearly more than
  the present foe has lately; or when the present foe is not attacking it at all (prey,
  somebody else's monster) or has been out of reach for six seconds. A foe that is itself
  nearly down and in reach is not left for one that merely hurts more. A new foe is kept
  at least three seconds.
- **The trend.** Its own and its foe's hit points are compared with the last look: a
  running mean of hit points lost a second on each side (healing counts against the
  loss), and from it who goes down first at the present rate.

It breaks off when

- its hit points fall under its own mark (20-39% per bot), raised by 8 for every
  attacker beyond the first and by 10 when it has nothing left to heal with (no castable
  heal, no bandage with the skill for one, no heal potion), and scaled by its nerve (the
  most timid bot's mark is a quarter higher, the boldest's a quarter lower) - except that
  a bold bot with one foe on it that falls within four seconds stays for the last blow;
- three are on it (up to five for the boldest) and it is under 70%;
- the trend says it goes down first: under 80%, less than fifteen seconds left at the
  present rate, and its foe lasting longer than it by more than its nerve accepts (a
  timid bot leaves unless it is clearly winning, a bold one stays unless it is clearly
  losing). A bot still above 55% with something to mend with stays.

Any fighter standing its ground turns to face its foe, as a player does (with
`COMBAT_FACECOMBAT` no blow starts otherwise).

#### A mage

A spell is cast only when the bot can really cast it: the spell in its book, the magery
for its circle (10.0 for the third, 25.0, 40.0, 55.0, 70.0, 85.0 for the eighth) and the
spell engine's own cast test (`CANCAST`: skill requirement, reagents, tithing, the
region's magic rules, mana for the real cost). A cast the engine refuses at the start is
not tried again for six seconds. A new cast is never started while one is under way;
between two casts the bot hesitates 150-650 ms. In order:

1. **Cure** when poisoned.
2. **A heal** under 65% - not while the foe is under 15% and the bot above 30%: Greater
   Heal under 40%; between 40 and 50% Greater Heal only when a spell to fight with is
   still paid for after it; else Heal. Whichever of the two it can cast when it can cast
   only one.
3. **Out from under a blade**: a melee foe on it at arm's length and the bot under 75%:
   Teleport to a standable spot six to nine tiles away that it can see, else Paralyze.
   A melee foe coming (three to five tiles) with the bot under 60%: a Wall of Stone
   across its way. Once in eight seconds.
4. **Wards**, while the foe is four tiles or more away and for the first twelve seconds,
   two at most: Magic Reflection against something that casts, Reactive Armor against
   something that strikes, then Protection, then Bless - whichever it does not already
   wear.
5. **Something summoned**, out of a guarded region, once per foe, with three tiles of
   room (or the foe held): when the foe takes more bringing down than its mana pays for,
   or is deadly. An Energy Vortex, else Blade Spirits, else an elemental, cast beside
   the foe; what obeys is then told "all kill" and the foe.
6. **An opener** on a melee foe that would kill it within twenty seconds, once per foe:
   Paralyze while it is still three tiles off, else Curse, Weaken or Clumsy.
7. **Poison** on a foe not poisoned and above 35% (two tries per foe, eight seconds apart).
8. **Damage**: the cheapest spell that finishes a foe nearly down; else Flamestrike with
   magery 90.0 and half its mana left; Explosion when Energy Bolt can follow it (the
   Energy Bolt is the next cast); Energy Bolt; Lightning; Fireball; Harm at two tiles;
   Magic Arrow; Mind Blast.

With nothing it can cast: out of mana with the foe held, busy with somebody else or
seven tiles off, a bot with Meditation 30.0 keeps eight tiles away and meditates;
otherwise - and when it is reagents it lacks - it fights on hand to hand with Wrestling
or a hand-weapon skill at 50.0 (the staff from its pack back in hand, or fists when it
wrestles better), as long as it is above half its hit points and the foe is hurt or
harmless; otherwise it leaves the fight. It never stands idle. A mage carrying a mana
potion drinks it under 20% mana. Between casts a caster keeps five tiles from its foe and
steps back from a melee foe that closes; out of a fight, low on mana, it meditates (the
Meditation skill through its client).

#### A warrior

- **Weapon.** A fighter that does not cast wields the weapon it does the most damage a
  second with, of those it carries: the engine's damage for the weapon and its swing
  delay, times the chance to hit its skill for that weapon gives. It changes when another
  is 12% better. Looked at whenever it checks its gear and as soon as it has gone
  through a body.
- **Shield.** With Parrying 30.0, a one-handed weapon in hand and the other hand free, a
  shield from its pack goes on.
- **Bandages.** Only with Healing 30.0: under 65% in a fight, under 80% out of one (a
  caster with mana to spare heals itself by spell instead). One at a time, nine seconds
  apart.
- **Potions**, behind the engine's potion delay: cure when poisoned (a caster cures by
  spell while it can), heal under 45%, refresh under a quarter of its stamina, and once
  per fight against a foe as tough as itself strength (agility for an archer).

#### An archer

It stands off at its bow's range less two (three to seven tiles). Against a melee foe
that comes for it, it steps back when the foe is within three tiles and the bow is not
ready, stands and looses when it is, and does not move with a shot on its way. Out of
arrows (or bolts) it takes the best hand weapon in its pack and closes in; with none it
leaves the fight - running, if the foe is after it. Arrows and bolts are bought for the
bow a bot carries, also while it holds a blade for want of them, so its next plan takes
it to where they are sold.

#### Getting away

A bot that breaks off drops out of war mode and, act by act:

- cures and heals as it can (a potion at once, a spell or a bandage with the foe four
  tiles behind);
- **Invisibility**, once, when it can cast it and at most two are on it;
- **Recall**, once, with five tiles of room (or unseen, or the foe held): on its home
  town's rune, else on any rune of its own that leads at least 64 tiles away - by the
  spell, else by a Recall scroll. Not a player killer;
- **hiding**, with Hiding 30.0, on foot, four tiles from a pursuer that has no line of
  sight to it; hidden, it stays put;
- **toward the guards**: the nearest road node in a guarded region within 40 tiles that
  does not lie past the threat; once inside with a monster, a criminal or a murderer
  still within twelve tiles it calls them (`guards_call`). Not a player killer or a
  criminal;
- else for the nearest road node that takes it away from the threat, not straight at
  whatever wall is behind it.

When the flight is over (ten to twenty seconds, or sixteen tiles clear) it goes back at
the foe only if it is mended (55%, and 25 points above its mark), at most one is on it,
it has something to heal with or the foe is under 30%, and it has something to fight
with; otherwise it gets on with what it was doing. Out of a fight and under 80% it sees
to its wounds before it moves on.

### Friends and favours

A player can ask a bot for help in so many words and follow it through in conversation;
nothing here has a menu. A favour takes as many lines as it needs: the bot asks back
what it does not know ("where to? i have britain, trinsic"), the player's next line is
read as the answer ("trinsic"), "never mind" (`cancel`) or a plain no drops it, and the
bot says why when it cannot. A player has one favour in hand at a time, a bot works on
one at a time; asking another bot drops the first. While it waits for an answer, a rune
or money the bot stays by the player; it gives up after a minute and a half (three
minutes once under way). Every spell is the bot's own cast through its client - mana,
reagents, skill, fizzles, the regions' anti-magic - and the bot checks what it can
first: the spell in its book, the magery, the region's gate-in and gate-out rules, the
reagents (the spell engine's own cast test). Short of mana it says so once and
meditates. A failed cast is tried twice more.

| Asked | What happens |
|---|---|
| `gate` ("britain'e gate açar mısın", "open a gate to trinsic") | a caster with Gate Travel in its book and Magery 60 opens one on its rune of that place. No place named: it lists its runes and asks. No rune for it: it asks for the player's - a marked rune put in a trade window is taken, the gate opened on it, and the rune traded back. The gate opens on the bot's tile; it steps off it, keeps off it, and stays until the player has gone through (a companion goes through after them; "you too" (`follow`) sends any bot through). |
| `mark` ("rün işaretler misin") | the player's blank rune, put in a trade, is marked right by the player and traded back. |
| `buff` ("bless ver", "night sight pls", a spell named on a short line) | Bless, Protection, Magic Reflect, Night Sight, Strength, Agility or Cunning on the player (`buff.<Spell>` words in `intents.txt`); the first of them in its book when none is named. Once in ten seconds. |
| `guide` ("beni bankaya götür") | the bot leads the player to the nearest place of that kind to them (a town's bank for a town, the place for a place named) on its road: it walks in front, waits while they lag more than five tiles behind, gives up when they have been more than 30 tiles off for 45 seconds. Over 400 tiles away, a caster with a rune there offers a gate instead (a yes opens it). |
| `appraise` ("bu ne eder") | things put in a trade window are priced at their make value, never bought; the window is closed a few seconds later. |
| `carry` ("bunu taşır mısın") | a companion only: what the player puts in a trade is held for them (`PLAYERBOT.HELD`), never sold, used or counted as stock, as long as the bot can carry the weight. Its death leaves it on its corpse, like anything else it carries. |
| `giveback` ("eşyalarımı ver") | the bot walks over, opens a trade with what it holds for the player and agrees; a refused trade is offered again a little later. |
| `gift` ("hediye") | the next trade is a present: taken for nothing, remembered, worth a bond point per `gift_per` of its value. |
| `can` ("ne yapabilirsin") | what this bot can do now, from its skills, book and runes (`token_ability` words), with the places of its runes. |
| `protect` ("beni koru") | a companion strikes first at a monster coming at the player. |

**Money.** A gate or a mark costs the reagents' worth (each reagent's make value times
the amount the spell takes) - asked of a stranger, and of an acquaintance unless the bot
is kind; a stingy bot asks double. It is paid in a trade window; a friend or a companion
pays nothing. Heals, cures, buffs, walks and prices are free.

**Who it will not help.** A player killer helps nobody. A bot will not cast on a
criminal or a murderer (the spell would make it one) and does nothing for a player it
holds a grudge against (a negative bond). A bot going about with one player does no
walking favours for another.

**Bonds.** A bot grows fond of a player (`bonds.txt`): a line said to it, a favour done,
a fight won beside the player, a fair trade, a present, being raised from the dead by
them, being healed, cured or blessed by them; striking it costs much more, and so does
being caught snooping in its pack or robbing it. A bond fades a point a day after three
days unseen.
At `acquaintance` it greets the player by name, at `friend` it says so once, does
favours for free, offers help unasked and goes about with the player when asked. Each
bot has a nature (`PLAYERBOT.TRAIT`, picked once): a kind one warms fast and does an
acquaintance's favours free, a shy one warms slowly and offers less, a chatty one warms
a little faster, a stingy one asks double. A line may be tagged for a nature (`[kind]`),
and for how well the bot knows the one it speaks to (`[friend]`, `[acquaintance]`,
`[stranger]`). "Let's be friends" (`friend`) is answered by how far along they are; "do
you remember me" (`remember`) by something they shared (`memory_<kind>`: `met`, `hunt`,
`raised`, `gate`, `gift`, `guide`, `helped`, with `{place}` and `{when}` from
`token_ago_*`), which a friend also brings up now and then when it greets them.

A bot that is raised where it fell (not carried to a healer) thanks whoever raised it
(`thanks_raised`) and walks back to its body.

**Companions.** A friend asked to come along (`companion`, or `follow` from a friend
outside a party) becomes the player's companion (`PLAYERBOT.COMPANION`; one per player).
It leaves its own errands and plans and keeps within three tiles of the player; fights
what the player fights (a player only when that is no crime) and what sets on them; heals,
cures and raises them as a party's bots do; waits when told (`stay`, ten minutes) and
follows again on `follow`; tells its hit points and mana (`status`); and a line the
player says to nobody in particular - a question or an ask - is its to answer. When the
player logs out
it waits four minutes where they parted, then goes about its own life; when they are
back, or have gone somewhere it was not, it comes to them: through a gate standing near
it that leads near them, on a rune of its own near them (Recall), or by road to the place
nearest them, and says it is back (`companion_back`) once it is beside them. With the
player dead it raises them when it can (resting for the mana), stays by the ghost and
strikes at a criminal at the body (a looter of an innocent's corpse is one). "You can go"
(`dismiss`) ends it; a companion who attacks the bot, or robs it, is one no more.

**Help unasked.** Once a second the bots near each player look out for them; a friend
(or a kind acquaintance) may offer a heal to one under half their hit points or a cure to
one poisoned (`offer_heal`, `offer_cure`: a question; a yes casts it), point out a
murderer about (`warn_red`, with `{dir}`), or ask one who has stood about in a town for
two minutes without a word whether they need anything (`offer_help`; a yes is answered
with what it can do). One offer per player in 45 seconds, one per bot a minute. A yes or
a no goes to the bot that asked.

### Presence and manners

Code: `PlayerBotEngine.Manners.cs`, `.Idle.cs`, `.Reactions.cs`, `.GhostWalk.cs`,
`.Death.cs`, state in `BotManners.cs` (`BotMind.Manners`). Almost all of it runs only for
bots a connected player can see (18 tiles); the once-a-second audience pass looks after
the bots within 12 tiles of each player. The odds are properties of `PlayerBotEngine`
(`GesturePercent` 45, `GlancePercent` 35, `JunctionPausePercent` 18, `IdleStandWeight` 12,
`TheftNoticePercent` 35, `BraveNerve` 1.3), not ini keys.

**Facing.** A bot turns to whoever speaks to it the moment the line is heard (before the
reply is typed) and keeps facing them for eight seconds; it faces whoever it says a line
to (answers, scenes, greetings, favours, thanks), its trade partner, the banker, vendor,
forge, board or dummy it is busy with, and now and then looks up at a player passing
within four tiles (`GlancePercent`). A bot on the road stops one and a half to three
seconds for a player who speaks to it (once in twenty seconds; not while it follows or
leads somebody). Turns go through the engine's facing update (`Character.FaceToward`):
the direction changes and the viewers are told through `Character.OnFacingChanged`, as
for any character a script turns.

**Body language.** A greeting (`respond_greet`, `friend_greet`, `bond_friend`) or a thanks
(`thanks_*`) comes with a bow, or a salute from a warrior, 45 times in a hundred
(`GesturePercent`): the client's own animate request (0x12 type 0xC7, `bow` / `salute`).
A line written as a gesture (`*yawns*`) goes out as an emote (talk mode 2, emote hue).
`emotes` holds such gestures only; a place has its own (`emote_bank`, `emote_sit`,
`emote_shop`, `emote_smith`, `emote_dock`, `emote_board`, `emote_train`), and so have a
bite and a drink (`emote_eat`, `emote_drink`). Every five to twelve minutes of standing
about, a bot eats or drinks something out of its pack with a double click (food, fruit, a
drink; never liquor, never a thing it holds for a player). A bot that went to eat and
found its pack bare has a `food` need (bought at an inn or a tavern).

**Standing about with a purpose.** Where a player sees it, a bot at a place does not
shuffle. What the place offers is looked up once - the items, the map's own furniture and
the townsfolk within ten tiles of its middle - and kept two minutes for every bot there.
The bot then picks one pastime for 45-110 seconds:

| Pastime | Where | What it does |
|---|---|---|
| banker | a bank with a banker | walks within four tiles of the banker, faces it and opens its bank box the way players do: it says `bank` (`bank_open`); then it stays there |
| seat | a free chair or bench (item or part of the map; mostly inns and taverns; not in the saddle) | walks onto the chair's tile - standing there is what shows the sitting pose in the client - and stays seated for the rest of the stay, with a drink |
| keeper | a vendor, healer or stable master | walks within four tiles, faces them (looks over the wares) |
| work | a forge or an anvil | walks within two tiles and watches |
| practice | a training dummy (blade, mace, fist) or an archery butte (bow), for a fighter whose weapon skill is 30.0 or less | stands by the dummy, or three to eight tiles from the butte with the way clear, and double-clicks it every three to four and a half seconds: the engine's own practice (swing, skill gain) |
| water | a dock with water within ten tiles | faces the water |
| board | a bulletin board | walks within two tiles, opens it with a double click, reads a while |
| stand | anywhere (`IdleStandWeight`) | stands still facing the place; one step in a while |

**Reactions.** The world tells the engine through three static hooks the host wires
(`SpellEngine.OnHelpfulEffect`, `ActiveSkillEngine.OnBandageHealed`,
`ActiveSkillEngine.OnTheftAttempt`); they only queue what the bot says or does next.

- *Helped.* A heal, a cure or a blessing from a player (a spell or a bandage) is thanked
  for (`thanks_heal`, `thanks_cure`, `thanks_buff`, else `thanks_help`), at most once in
  twenty seconds per helper; the bot faces them, grows fonder (`helped` in `bonds.txt`,
  once per `helped_gap` seconds) and remembers it (`memory_helped`). Another bot's help
  gets a word thirty times in a hundred. An NPC healer's spell is its trade.
- *Robbed.* A fumbled snoop or theft is always felt by the bot it was tried on, a clean
  one 35 times in a hundred (`TheftNoticePercent`; the mark is never one of the witnesses
  the crime code asks). It turns on the thief with a word (`theft_snoop`, `theft_steal`),
  thinks less of a player (`snooped`, `stolen` in `bonds.txt`), calls the guards in a
  guarded region (`theft_guards`: the word the guards hear; they come for one the
  witnesses made a criminal), and out of town a brave fighter (nerve 1.3 or more,
  `BraveNerve`) attacks a thief who is no longer innocent to it - never one still blue.
- *A fight near by.* Somebody within twelve tiles of a player struck in the last six
  seconds: each bot standing about within eight tiles of the victim reacts once in twenty
  to forty seconds - townsfolk back away to seven tiles and look back (`fight_scared`),
  fighters turn to watch (`fight_watch`).
- *Spoken to in a fight.* See Talking.
- *Crowded, followed, left.* A player standing on the bot's tile for six seconds is told
  (`crowded`) and the bot steps aside; one who has kept within three tiles of a walking
  bot for twelve seconds and twelve tiles is asked (`followed`) - the bot stops and turns;
  a player who walks more than eight tiles off in the middle of a talk gets a goodbye
  (`bye_leaving`).

**Pace.** Where a player sees it, a trip is walked inside a guarded region, on its last
leg and when out of breath (under a third of its stamina; running again from half), and
run on the open road, on a trip made in a hurry and on a leg that has taken far longer
than its length asks; looked at again every two seconds. Unseen, a trip keeps its own
pace. A walker a player sees takes a breath (0.6-1.8 s) at a junction - a new leg of the
road - 18 times in a hundred (`JunctionPausePercent`), facing the way it takes next, and,
walking, now and then stops one to three seconds to look about (two steps in a hundred).
Bots whose stays at one place end together set off 0.6-1.5 seconds apart.

**Nothing out of nothing.** A mount is made for a rider only where no player sees the
spot; seen, the rider goes on foot and is asked again later. A creature its rider gives up
(too far, out of reach, will not be ridden) is removed at once where nobody sees it, else
left standing and removed once nobody does. A bare bot gets its kit where no player sees
it; seen, it does what a player with nothing does: at a bank it says `bank` to the banker
and dresses out of its box two seconds later, anywhere else it sets off for the nearest
bank on its road first.

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
for a group, a trade shout at the start of the line, a question, "where are we", a call
for a heal. Questions about the bot ("where are you going") are tried before a "where";
"not" (`not:` words) turns a party, follow or heal ask into a no. A bot that is fighting
or running answers the player who means it (names it, or whose companion, party member
or talk partner it is) in a word: `fight_busy`, or `fight_help` under half its hit
points; once in eight seconds. What a line asks for:

| Intent | Answer |
|---|---|
| greet | `respond_greet`, or `friend_greet` by name to a player it knows |
| where + a place, or a place named alone ("bank?", "what about the inn") | directions from where the player stands to the nearest place of that type, the town or the dungeon named: `respond_where` (`{target}` the word the player used, `{dir}` from `token_dir`, `{dist}` steps), `respond_where_far` past 400 tiles, `respond_where_here` within 4, `respond_where_unknown` |
| how far / which way | the place last talked of, again |
| what are you doing / where are you going | from what the bot does: `respond_doing_travel` / `_hunt` (a dungeon) / `_company` with `{goal}` (a town's place as the town and its `token_placetype` word, a dungeon by name; that place becomes the topic, so "where is that?" is answered), `_plan` on the way to a planned shop, bank, rest, company or home (see A day's plan), `_work` at work or selling (`{work}` from `token_work`), `_linger` with `{at}`, `_shop`, `_bank`, `_reclaim`, `_party` (it follows this player), `_idle` |
| what is your plan | `respond_plan` / `respond_plan_none` (see A day's plan) |
| where are we | `respond_here` with `{here}` (the region, else the nearest town), `respond_here_unknown` |
| what are you | `respond_whoami`: `{skill1}` `{val1}` `{skill2}`, its two best skills |
| any news | a story from the news, told as a scene tells it; `respond_news_none` |
| heal me | a caster with the spell and 11 mana: `respond_heal_yes` and a Greater Heal (Heal, or Cure for poison) through its client, once in 20 s; `respond_heal_fine` to a player not hurt; else `respond_heal_no` |
| follow me | a fighter not in a party: `respond_follow_yes` (asks for an invitation, which it accepts); `respond_follow_already` in the player's party; else `respond_follow_no` |
| are you a bot | `respond_bot` |
| yes / no (a short line) | after a bot's own question (a line it said that ends in `?`, within a minute): `<category>_yes` / `<category>_no` of the line asked, else `ack_yes` / `ack_no`; anything else said to it then gets `<category>_reply` when there is one. Otherwise it is small talk |
| party / lfg | a fighter not in a party: `respond_party_yes` (it accepts the invitation that follows); else `respond_party_no` |
| res | `respond_res_no` from the living; a ghost asking near a caster is raised |
| wts + goods | `buy_notice` or `trade_not_buying`; a price the player names next gets one lower offer (`haggle_offer`), the next one `haggle_walkaway`. A bot carries no gold for a deal, so it never says yes |
| wtb | `respond_wtb_none` |
| thanks / bye / how are you | `respond_thanks` / `respond_bye` / `respond_howru` |
| a question | `respond_question` |
| gate, mark, buff, guide, appraise, carry, giveback, gift, can, cancel, protect, friend, companion, dismiss, remember | see Friends and favours |
| anything else | `respond_name` when named, `respond_chat` to the player it is talking with |

A hello on the same line as a question is answered too, in front of the answer ("hey,
head east, 20 steps"). The same question again within 30 s gets `respond_repeat` in front
("like i said, ..."). A place asked about stays the conversation's topic for two minutes.

A line in any category may open with tags, `[mage] ...`, `[!mage] ...`, `[new vet]`: it
is said only by a bot that has all the tags and none of the `!` ones. The tags are `mage`,
`archer`, `warrior`, `fighter`, `crafter`, `new` (no skill at 50), `vet` (a skill at 90),
`hurt` (under 60% hit points), `town` / `wild` (a guarded region or not), `mounted`,
`travel` / `linger`. A bot does not say one of its last twenty lines again while another
line of the category can be said, nor a line a player in earshot heard from any bot
within the last three minutes.

Replies come after a short typing delay. Only real players are answered, so two bots
never talk themselves into a loop.

### The brain (optional)

With a model named in `ai.txt`, small talk and questions no fact answers are answered
by a small language model the server runs itself: llama.cpp on the CPU, in process
(`SphereNet.BotBrain`, LLamaSharp), with no network and nothing paid per word. Without
the file or its `model` line, or when the model does not load, bots talk as above.

- **What it answers.** A player's line the intents read as a question or "how are you",
  or one they do not read at all, said to a bot (by name, to the bot the player is
  talking with, to their companion). The way, the news, prices, favours, parties, the
  plan and everything else the bot knows a fact for stay with the intents and the chat
  lines.
- **What it is told.** First the same rules for every bot (a player character in
  Ultima Online chatting in game; the player's language; one short casual sentence;
  nothing outside the game; no claim to be human), then the bot: its name, kind
  (mage, archer, warrior, crafter), home town, two best skills, nature, what it is doing
  and where, its plan and why, the region, how well it knows the player and whether it
  is their companion, and its last three exchanges with that player (a conversation is
  forgotten after three minutes without talk).
- **Never waiting on it.** One worker thread writes up to `parallel` replies at once,
  each in its own sequence of one shared context (every step reads the weights once
  for all of them), on `threads` CPU threads; `queue` more wait and the rest are
  refused. The reply is written
  while the bot types. When it is not there `wait` milliseconds after the player's line,
  or the brain was busy, or the reply is unfit (empty, or stepping out of the game: "AI",
  "assistant", links), the bot says its own line, as it would have. A reply is cut to
  its first line and 140 characters, without quotes, a name prefix or emoji.
- **Fast.** The worker keeps one context and what it last read: every prompt starts with
  the same rules, so only the part about the bot and the line is read anew.

Measured on an 8-core desktop CPU with 4 threads: Gemma 3 1B (Q4_K_M, 0.8 GB) answers in
0.5-2.5 s with natural Turkish and English; Gemma 3 4B in 2-5 s with the best replies;
Qwen2.5 1.5B and 3B write broken Turkish. A model is the shard's to download (see
`ai.txt`) and is never committed.

How much it keeps up with (Gemma 3 1B, lines arriving steadily, a reply counted only
when it comes within the 4 s wait):

| Lines a second | threads / parallel | Answered in time | Line to reply |
|---|---|---|---|
| 1 | 4 / 1 | 48 of 60 | 1.9 s |
| 1 | 4 / 4 | 27 of 60 | 4.5 s |
| 2 | 4 / 1 | 57 of 120 | 1.9 s |
| 2 | 4 / 2 | 60 of 120 | 3.0 s |
| 2 | 6 / 2 | 82 of 120 | 2.4 s |
| 2 | 8 / 1 | 73 of 120 | 1.4 s |
| 3 | 4 / 4 | 1 of 181 | 7.0 s |

Reading each new prompt is what costs, and it takes CPU, not memory bandwidth: writing
several together raises the replies a second but makes each slower, and past two, on
four threads, slower than the wait. Thirty players each talking to bots every fifteen
seconds is two lines a second: about half to two thirds get the brain's reply, the
rest the bot's own line. Players do not all talk at once in practice; at one line a
second most replies are the brain's.

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
- A place to look about at (`wander`) or to be among people (`social`) is picked by the
  `places.txt` weight of its type: a bank holds a crowd, a tailor's shop sees one
  customer. The second number on a line is the type's room: the
  bots at a place and on their way to it count against it (places of one type within ten
  tiles of each other, such as a bank listed twice, share one count), the fuller a place
  the less it draws, and a full one draws nobody. When every place on offer is full (a
  small town with more bots than places) the one least over its room is taken. A bot that
  comes to a full place while its town has room elsewhere leaves after 15-45 seconds.
  Most wander trips (85 in a hundred) stay in the home town; four in a hundred go to a
  moongate that carries a walker.
- A bot that comes to a place does not stay on the stopping spot every visitor stops on
  (a doorway, a graveyard's gate): it walks on to a tile of its own, inside the place's
  outline when the data draws one (`Polygon` in `destinations.json`: a graveyard's yard,
  a bank's hall, a dock) and within twelve tiles, else within five tiles of the spot, and
  idles about that tile.
  The tile is on the bot's own level, is not a portal, is reached by walking straight at
  it (no wall, fence or tomb between, so no search is needed) and, of a few tried, has
  the fewest others about it. A walk that has not got there in 30 seconds ends where it is.
- A bot stays two to five minutes at a bank, one to four at other places, two to four
  when it rests.
- `PlayerBotGravity` percent of wander trips go to a place within 30 tiles of a connected
  player, unless `PlayerBotCrowd` bots already stand around that player; a `social` trip
  goes to such a place (within 220 tiles of the bot) whenever there is one.
- A bot at a bank, healer, inn, tavern or moongate stays once more (one to two and a half
  minutes) while a player can see it.

### Riding and gear

`mounts.txt` sets the share of riders (`riders 45`) and the creatures they ride. A rider
on foot in a guarded region gets one, as from the stables, where no player sees the
spot: the creature is made from its CHARDEF, owned by the bot and mounted by a double
click through the bot's client. Knocked off by death, the bot walks back to its own
creature when it is within 20 tiles; otherwise it leaves the creature (removed once
nobody sees it) and gets another in town.

A bot that has its own things back after death takes off the resurrection robe. The robe
comes off as soon as a corpse run starts, so the things go back on where they came off.
A bot left with nothing at all gets a new kit in a guarded town, where no player sees it
or out of its bank box (see Presence and manners): its archetype's @Create is run on a
stand-in, and what that wears, plus the supplies in its pack but not the gold, go to the
bot.

### Walking

A step goes straight at the target, or 45 degrees either side. When that is refused the
bot looks at why (the walk path's refusal reason):

- **Somebody on the tile** (`character_on_tile`: a player or bot it cannot push past
  without full stamina, a guard in a gateway): it waits a moment, three times, the way a
  player waits for someone to pass (now and then saying a `blocked` line), then steps to
  the side; a crowd that stays put for three seconds gets a tile search routed round
  the people. It does not start walking walls because of a person.
- **A wall, a fence, water** (`terrain`, `house`, ...): a shut, unlocked door ahead is
  opened (a door item with a double click, a door that is part of the map with the
  open-door macro, facing it, as the client's auto-open does); a way another bot already found along the same road link
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
people, anybody standing on a tile blocks it. The search is weighted (the way still to
go counts twice): it finds a way at most twice the shortest, in practice within a few
tiles of it, in fewer nodes, so fewer searches run out of time. A search that ran out of
time is asked again a second later (twice; the third counts as failed). A failed search
waits 3, 10, then 20 seconds before the same bot may ask again. A path is followed tile by tile: somebody on
it is waited for, a shut door on it opened, a diagonal round a corner that the step
refuses is taken as two straight steps, and anything else drops the path and asks for a
new one.

**Corridors.** A path found along a road link (node to node) is kept, starting at the
tile the search started from. The next bot stopped on that link joins it where it stands
and walks it with no search of its own. A kept path whose step is refused for good is
forgotten. Kept paths are written to `playerbots_corridors.txt` beside the world save
every five minutes when they changed, by node name, and read back at start.

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
link and the link is left out of every route (`link|from|to` in the bad-spot file). Before
either is written down a tile search (the bot as walker, 4000 nodes, 20 ms) looks for a
way from the spot's road node to the spot, or along the link: when it finds one, or runs
out of time, nothing is learned and the count starts again (a crowd in a gateway or a
search out of time gets a bot lifted too, and a link learned for that would be lost to
every bot for good). A
place with no usable stopping spot left is not chosen at all. A bot rescued within three
tiles of its target teaches something only when a wall stopped it: one held up by people
(a doorway full of others) teaches nothing. A bot lingering right by a door steps
away from it, so it does not stand in the doorway others need.

**The way into a stopping spot.** A rescue on the last leg, from more than three tiles off
(or a wall), first asks whether anybody knows the way in: when no way is kept for that
spot, a search of the rescue's own (the bot as walker, from the leg's road node, 4000 nodes,
20 ms) looks for it. A way found is kept for the last leg into that spot (`spot|node|place|
spot|path` in the corridor file), and every bot on that leg joins it from the road node
instead of searching - a bot's own search has 5 ms, and in a town of doors, counters and
upper floors it gives out before it finds a way that is there, which is why such a spot
was never learned bad and stalled every visitor. A bot's own search that does find the way
in is kept the same way while none is. A spot no search reaches is learned bad as above.

Shops and banks are entered and left by their door: a route starts at the road node the
bot's stopping spot is linked to (`Arrivals[].Waypoints` in the data) and ends at the
node linked to the spot it is heading for, not at whichever node is nearest through a
wall. When the first leg makes no ground for 12 seconds the bot walks to the nearest
unlocked door within 8 tiles and opens it, then tries the next-nearest start nodes.
Learned spots and links are kept in `playerbots_badspots.txt` in the world save directory
and read back at start; deleting the file lets bots try every spot again. A place the file
would leave with no stopping spot at all gets its last learned spot back at start (to be
learned again if it really is shut). A bot with no road home is lifted to a stopping spot
of a usable place of its home town, its bank first; a place known by its middle only, or
with every spot found unreachable, gives its road node instead of its middle (the middle
of a bank can be behind its counter, with no road out).

### Errands

- **Bank.** A bot at a bank opens its box from the banker's context menu (Open Bankbox)
  and moves gold by hand: carrying more than 600 (`BankDepositAbove`) it puts the surplus
  in (keeping 300-600),
  carrying less than 200 it takes up to 500 out. Once every ten minutes at most, unless
  the planner sent it there for that (`bank`).
- **Runes.** A caster with Magery 50 or more whose book holds Mark and Recall keeps a
  library of up to 8 runes in its pack. At a town's bank it has no rune for (and, for one
  who hunts, at a dungeon's mouth) it marks a blank or worn-out rune with the Mark spell
  through its client (mana, reagents and skill as for anyone); room is kept for its home
  town's. Blank runes are bought like anything else: out of blank ones and with room for
  more, it has a `rune` need (`shopping.txt`), for a mage shop within 300 tiles. In the
  Scripts-X pack only the mage shopkeepers (`VENDOR_S_MAGE_SHOP`: `c_mageshop`) sell blank
  runes (`i_rune_marker`), a few at a time; a mage place whose vendors have none (an
  arcane guildmaster's, or a shop sold out) is not walked to for it again for half an
  hour, and while no shop in reach has one the need is dropped.
  A rune marked off every map (the pack's blank rune is made with `MOREP=-1,-1`) is blank. What a rune leads
  to is read from its mark: the town of the nearest place within 40 tiles, else the
  dungeon, else the region. It recalls home on its home town's rune from where no road
  leads home, when it is stranded off the roads, when it is on a map with no roads, or
  when its plan takes it home with no road there, before any lift; the other runes are
  where it can open a gate to for a player (above).
- **Company.** A bot setting off on an ordinary trip asks an idle bot of the same home
  standing within 6 tiles along, 15 times in a hundred (`travel_together` line). The
  companion goes to the same place, waits when it gets ahead, runs when it falls more
  than four tiles behind, and goes its own way once the leader arrives.

Bots cast only spells their spellbook holds. A saved bot whose book is short of what its
archetype's kit gives (the kit's `ADDCIRCLE` lines once reached nothing) gets the kit's
spells when it is adopted, read from a stand-in made by the same `@Create`.

### Other walks

Every walk that is not a trip gives up when it makes no ground, rather than holding the
bot for ever: its own horse (15 s, then the horse is left and another got in a minute;
a horse that will not be mounted, three tries; a bot on a trip does not turn back for its
horse), a thief's mark (10 s), the body of a kill (15 s), a vendor behind a counter (20 s),
its own corpse (60 s, then what is left on it is handed back, as for a corpse with no way
to it), a party leader it never gets within reach of (30 s, then it goes its own way
for 30 s). Each walk keeps its own clock, so two walks taking turns do not reset each
other.

### Portals

A dungeon's way in, the stairs between its levels and a moongate all carry a walker on
the moment it steps onto the tile, and only then. At the first tick the engine looks up
each portal-type destination's tiles in the running world: map teleporters
(`[TELEPORTERS]`) within three tiles of its stopping spots, and gates and telepads that
lead somewhere (a MOREP on the map: a gate made with `MOREP=-1,-1`, such as the pack's
menu moongates, sends players by its menu and does not carry a bot that steps on it, so
it is no portal); when the data names where the portal goes (`TargetX/Y`), only tiles that
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
portalless and never chosen, unless its far side is on the same piece of road: then it is
a ramp or passage that is walked through, and an ordinary place; a dungeon mouth or a moongate with no portal is visited as
an ordinary place (walked into, or waited about: a world without gate items still has its
moongate squares). The server log lists them at start. A teleporter, gate or telepad that leads to a map the road network does not cover
is never stepped on, and a bot found on such a map is taken home.

### Out of sight

A lift - a stuck bot set down on its leg's target, a stranded one on a road node or at
home, a bot on a map with no roads, a player killer outside its haunt, a ghost carried
off to be raised - waits while a player within 18 tiles can see where the bot stands or
where it would appear, for up to `PlayerBotUnseenLiftSec` (90 s), then goes ahead anyway
(`liftwaits` in the telemetry). A new bot logs in at a spot no player sees when one of
eight picks finds one, and a bot over `PlayerBotCount` logs out only where nobody sees
it. A mount or a kit is never made where a player sees it (see Presence and manners).

### Off the roads

A trip needs a road node within 64 tiles on the bot's floor. A bot further out (on a
roof, a bridge, somewhere a gate put it) first walks back to the nearest node within 128
tiles on any nearby floor. A third trip that finds no road, or five minutes of them, lifts
it onto that node; a fifth, onto its home town's bank. A bot whose piece of road leads to
no town place at all (a dungeon mouth out in the wild) goes home - a recall on its rune,
else a lift - the second time a stay ends with nowhere to go, rather than wait there for
good. A bot staying where it was set down wanders a step or two like any other.

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
`tends` (party members healed, cured or raised), `orders` (party orders taken), `trades`
(trades completed), `liftwaits` (lifts held while a player looked),
`badlinks` and `searchtimeouts` (searches given up at their deadline since start),
`retried` (searches out of time asked again), `arrivalways` (ways into a stopping spot
found at a rescue and kept), `unlearned` (spots and links two rescues
pointed at that a search then walked), `settles` (bots walking on into a place)
and `crowdleaves` (bots leaving a full place early).

The end of the line is about getting stuck:

| Field | Meaning |
|---|---|
| `searches`, `failed`, `slowest` | searches run, found nothing, the slowest one |
| `searchq`, `denied`, `dropped`, `notrun`, `backlog` | searches asked for; asks refused by a bot's backoff; results that came too late to use; searches the batch had no time left for; most requests waiting at once |
| `corridors`, `corridoruses`, `cornersplits` | kept road-link paths; times a bot joined one; diagonal path steps taken as two straight ones |
| `waits`, `sidesteps`, `doorway` | waits for somebody in the way; steps aside round them; steps out of a doorway |
| `portals`, `portalmisses` | portal crossings made; portal places written off after their tiles did not carry the bot |
| `noroute`, `stranded`, `maprescues` | trips that found no road; bots lifted back onto the roads; bots taken home from a map with no roads |
| `unreachable`, `giveups`, `faults` | foes given up as out of reach; other walks given up; acts that threw |
| `staticdoors` | map (static) doors opened |
| `deposits`, `withdrawals`, `marks (taken, failed)`, `recalls`, `companions`, `pauses` | bank moves; runes marked, and of those the marks that held or did not; recalls home; bots taken along; stops on the road (a breath at a junction, a look about) |
| `favors`, `favorfails`, `gates`, `gifts`, `offers` | favours done for players and dropped; gates opened; presents taken; help offered unasked |
| `newfriends`, `bondpoints`, `companionsnow` | bonds that became friendships; bond points given (net); bots going about with a player now |
| `stalled [...]` | the most bots stalled at once (no ground for 10 s), by mode, e.g. `Travel:3 Fight:1` |
| `refusals [...]` | why steps were refused, most first, e.g. `terrain:812 character_on_tile:95` |

More lines follow in the same 30 seconds when there is something to count:

```
[playerbots] combat fights=60 switches=7 retreats=9 (trend 3) guardruns=2 escaperecalls=1 vanishes=1 hides=0 assists=4 preyrefused=310 casts=212 wards=18 summons=3 combos=11 manarests=5 potions=14 bandages=22 weaponswaps=6 shields=2
[playerbots] work: jobs=.. gathers=.. smelts=.. crafts=.. sales=.. (gold ..) givenup=..
[playerbots] plans made=41 kept=63 urgent=4 interrupted=9 failed=2 mix hunt=22 work=31 sell=12 shop=9 bank=7 rest=5 social=10 wander=8 home=0
[playerbots] manners turns=.. gestures=.. emotes=.. meals=.. seats=.. practice=.. poiscans=.. thanks=.. theftreacts=.. fightreacts=.. ghostwalks=.. ghostraises=.. corpseruns=.. deathsresumed=.. conjurewaits=.. straymounts=..
```

- **combat**, with fights in the window: `switches` changes of foe during a fight,
  `retreats` fights broken off (`trend`: on the trend alone, above the hit-point mark),
  `guardruns`, `escaperecalls`, `vanishes` (Invisibility) and `hides` the ways away,
  `assists` other people's fights joined, `preyrefused` monsters looked at and not taken
  on, `casts`, `wards`, `summons`, `combos` (Explosion then Energy Bolt), `manarests`
  meditations in the middle of a fight, `potions`, `bandages`, `weaponswaps`, `shields`.
- **work**, while any bot works: `jobs` jobs started, `gathers` swings that put something
  in a pack, `smelts` piles smelted, `crafts` pieces made, `sales` sell lists sold and the
  gold they brought, `givenup` places written off for a job.
- **plans**, while plans are made: `made` new plans, `kept` steps taken as planned,
  `urgent` plans set aside for a pressing need, `interrupted` plans dropped for a death, a
  favour, a companion or a carry, `failed` steps that would not start, `mix` the
  activities taken up.
- **manners**, while a player is on (or a ghost walked or a body was run for): `turns`
  facing turns, `gestures` bows and salutes, `emotes`, `meals`, `seats` bots sat down,
  `practice` blows at a dummy or butte, `poiscans` places looked over, `thanks`,
  `theftreacts`, `fightreacts`, `ghostwalks` and `ghostraises` (ghosts that walked to a
  healer, and were raised there), `corpseruns` (bodies run back to from far off),
  `deathsresumed` (deaths taken back from the save), `conjurewaits` (mounts and kits held
  back while a player looked), `straymounts` (creatures left standing until nobody looks).

With a brain, a `[botbrain]` line every 30 seconds counts its work: `asked` (lines handed
to it), `said` (replies said), `late`, `busy` (refused), `unfit`, `thought` (replies
written) and their `avg` and `max` time.

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
| `PLAYERBOTS TRACE <uid\|name>` | one bot's last 32 decisions with their age (trip chosen, new plan and step kept, plan interrupted, search asked and found, path dropped and why, portal crossed, rescue, walk given up, refused step kinds, and the system messages its client was told, such as "There is nothing here to mine for.") |
| `PLAYERBOTS PLAN <uid\|name>` | the bot's plan (the step it is on starred), who it is, its gold, load and need, each drive (urgency x weight), every option with its utility, place and note (a hunting ground's danger against its strength, and its road), and the drives and top options of its last choice. Nothing is changed (only the drive clocks are brought up to date, as any look at the drives does) |
| `PLAYERBOTS THINK <uid\|name> <line>` | the line put to that bot's brain as a stranger's; the reply (or why there is none) goes to the server log |
| `PLAYERBOTS NEAR x y [range [map]]` | the bots within `range` (12) of a point: how many do what, then one line each with its activity and last decision |
| `PLAYERBOTS PLACES [n]` | the `n` (20) places with the most bots at them or on their way, against their room |
| `PLAYERBOTS NAVCHECK` | every portal-type place and whether the world has a portal there |
| `PLAYERBOTS NAVCHECK LINKS [LEARN]` | also a tile search along every road link and from every stopping spot to its node, with an online bot as the walker; `LEARN` leaves failures out of routes and writes them to the bad-spot file. Runs on the main loop: it takes a while on a full road network |

A trace line for a plan reads `new plan: Shop at Britain Mage (u 1.19, for Supplies)
[*Shop > Hunt > Sell]`. NAVCHECK writes its full report to `playerbots_navcheck.txt`
beside the bad-spot file. The web panel's Players page shows, for a bot, what it is doing
in the version column (`Travel -> Britain Bank`, `Gather Mining at MiningSpot 418`, with
`(stalled 14s)` when it has made no ground for 10 s).

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
