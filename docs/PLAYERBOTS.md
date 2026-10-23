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
| `PLAYERBOT.HAUNT` | a player killer's haunt (a destination name) |
| `PLAYERBOT.TRAIT` | its nature: `kind`, `shy`, `chatty` or `stingy` (decided once) |
| `PLAYERBOT.BONDS` | how fond it is of players: `uid:score:minute` each (the unix minute last met), up to 24 |
| `PLAYERBOT.MEMORY` | things shared with players: `uid\|kind\|place\|minute`, `;`-joined, the last 12 |
| `PLAYERBOT.COMPANION` | the player it goes about with |
| `PLAYERBOT.HELD` | things it holds for players: `item:player` pairs |

A bot from an older build may carry `PLAYERBOT.RUNE` (the one rune it was given); it is
no longer read: its runes are whatever its pack holds.

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
| `bonds.txt` | how a bot grows fond of a player: the levels, the points for talk, favours, hunts, trades, presents, being raised or attacked, how a bond fades, and the weights of the bots' natures |
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
  vendor's buy list and buys what its gold allows. No gold is made out of nothing. A shop
  where nobody turns out to sell the thing is not walked to for it again for half an hour
  (by any bot).
- **Rogues**: `PlayerBotPkPercent` of the fighters are player killers. Each is given a
  haunt (`PLAYERBOT.HAUNT`): a dungeon mouth, moongate, shrine, landmark, adventure area
  or graveyard outside every guarded region. It takes no trips: it roams the road nodes
  within 40 tiles of the haunt, stands a while, and looks about every second or two. It
  never steps into a guarded region (the step is refused, `guard_zone`), gives up a
  victim that reaches one, and one found in one or more than 120 tiles off is set down
  at its haunt. It attacks the nearest blue player or bot (innocent to it, NOTO_GOOD) it
  can see within 14 tiles that stands outside the guards' reach (never staff, another
  player killer or a party member) and goes through the body (weapons too). It runs no
  bank, rune, recall, shopping or party errands. Haunts are shared out: a new player
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
  and walks off. Murder counts, the criminal flag, the guards and the murderer's
  resurrection town all follow the normal rules.
- **Death**: half a minute to a minute as a ghost walking about the body in war mode - a
  manifested ghost, which the living can see - then the bot is carried off the way a
  stuck bot is and raised there: a player killer at its haunt (above), any other murderer
  at the anchor of the `PlayerBotRedRespawn` region (Delucia by default), anyone else at
  the nearest town healer. A corpse within
  `PlayerBotReclaimRange` tiles (40) is walked back to: the bot opens it (only an opened
  container can be reached into) and takes its things off one at a time through its client -
  picked up, worn on the layer they came off (`EQUIPLAYER`) or dropped into the pack - as T2A
  had no self-loot; it gives up four minutes after reaching the body. A farther corpse has
  what is still on it handed back by the server (same layer-or-pack rule) and is removed.
  Until then the corpse is an ordinary corpse: other players can loot it, and what they take
  is not handed back.
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
them; striking it costs much more. A bond fades a point a day after three days unseen.
At `acquaintance` it greets the player by name, at `friend` it says so once, does
favours for free, offers help unasked and goes about with the player when asked. Each
bot has a nature (`PLAYERBOT.TRAIT`, picked once): a kind one warms fast and does an
acquaintance's favours free, a shy one warms slowly and offers less, a chatty one warms
a little faster, a stingy one asks double. A line may be tagged for a nature (`[kind]`),
and for how well the bot knows the one it speaks to (`[friend]`, `[acquaintance]`,
`[stranger]`). "Let's be friends" (`friend`) is answered by how far along they are; "do
you remember me" (`remember`) by something they shared (`memory_<kind>`: `met`, `hunt`,
`raised`, `gate`, `gift`, `guide`, with `{place}` and `{when}` from `token_ago_*`), which
a friend also brings up now and then when it greets them.

A bot that is raised where it fell (not carried to a healer) thanks whoever raised it
(`thanks_raised`) and walks back to its body.

**Companions.** A friend asked to come along (`companion`, or `follow` from a friend
outside a party) becomes the player's companion (`PLAYERBOT.COMPANION`; one per player).
It leaves its own errands and keeps within three tiles of the player; fights what the
player fights (a player only when that is no crime) and what sets on them; heals, cures
and raises them as a party's bots do; waits when told (`stay`, ten minutes) and follows
again on `follow`; tells its hit points and mana (`status`); and a line the player says to
nobody in particular - a question or an ask - is its to answer. When the player logs out
it waits four minutes where they parted, then goes about its own life; when they are
back, or have gone somewhere it was not, it comes to them: through a gate standing near
it that leads near them, on a rune of its own near them (Recall), or by road to the place
nearest them, and says it is back (`companion_back`) once it is beside them. With the
player dead it raises them when it can (resting for the mana), stays by the ghost and
strikes at a criminal at the body (a looter of an innocent's corpse is one). "You can go"
(`dismiss`) ends it; a companion who attacks the bot is one no more.

**Help unasked.** Once a second the bots near each player look out for them; a friend
(or a kind acquaintance) may offer a heal to one under half their hit points or a cure to
one poisoned (`offer_heal`, `offer_cure`: a question; a yes casts it), point out a
murderer about (`warn_red`, with `{dir}`), or ask one who has stood about in a town for
two minutes without a word whether they need anything (`offer_help`; a yes is answered
with what it can do). One offer per player in 45 seconds, one per bot a minute. A yes or
a no goes to the bot that asked.

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
"not" (`not:` words) turns a party, follow or heal ask into a no. What a line asks for:

| Intent | Answer |
|---|---|
| greet | `respond_greet`, or `friend_greet` by name to a player it knows |
| where + a place, or a place named alone ("bank?", "what about the inn") | directions from where the player stands to the nearest place of that type, the town or the dungeon named: `respond_where` (`{target}` the word the player used, `{dir}` from `token_dir`, `{dist}` steps), `respond_where_far` past 400 tiles, `respond_where_here` within 4, `respond_where_unknown` |
| how far / which way | the place last talked of, again |
| what are you doing / where are you going | from what the bot does: `respond_doing_travel` / `_hunt` (a dungeon) / `_company` with `{goal}` (a town's place as the town and its `token_placetype` word, a dungeon by name; that place becomes the topic, so "where is that?" is answered), `_linger` with `{at}`, `_shop`, `_bank`, `_reclaim`, `_party` (it follows this player), `_idle` |
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
`travel` / `linger`. A bot does not say one of its last six lines again while another
line of the category can be said.

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
people, anybody standing on a tile blocks it. A failed search waits 3, 10, then 20
seconds before the same bot may ask again. A path is followed tile by tile: somebody on
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

### Errands

- **Bank.** A bot at a bank opens its box from the banker's context menu (Open Bankbox)
  and moves gold by hand: carrying more than 1500 it puts the surplus in (keeping 300-600),
  carrying less than 200 it takes up to 500 out. Once every ten minutes at most.
- **Runes.** A caster with Magery 50 or more whose book holds Mark and Recall keeps a
  library of up to 8 runes in its pack. At a town's bank it has no rune for (and, for one
  who hunts, at a dungeon's mouth) it marks a blank or worn-out rune with the Mark spell
  through its client (mana, reagents and skill as for anyone); room is kept for its home
  town's. Blank runes are bought like anything else: out of blank ones and with room for
  more, it walks to a mage shop within 300 tiles and buys two (`rune` in `shopping.txt`).
  A rune marked off every map (the pack's blank rune is made with `MOREP=-1,-1`) is blank. What a rune leads
  to is read from its mark: the town of the nearest place within 40 tiles, else the
  dungeon, else the region. It recalls home on its home town's rune from where no road
  leads home, when it is stranded off the roads, or when it is on a map with no roads,
  before any lift; the other runes are where it can open a gate to for a player (below).
- **Company.** A bot setting off on an ordinary trip asks an idle bot of the same home
  standing within 6 tiles along, 15 times in a hundred (`travel_together` line). The
  companion goes to the same place, waits when it gets ahead, runs when it falls more
  than four tiles behind, and goes its own way once the leader arrives.
- **A look about.** Walking where a player sees it, a bot stops for one to three
  seconds now and then (two steps in a thousand).

Bots cast only spells their spellbook holds. A saved bot whose book is short of what its
archetype's kit gives (the kit's `ADDCIRCLE` lines once reached nothing) gets the kit's
spells when it is adopted, read from a stand-in made by the same `@Create`.

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
portalless and never chosen, unless its far side is on the same piece of road: then it is
a ramp or passage that is walked through, and an ordinary place; a dungeon mouth or a moongate with no portal is visited as
an ordinary place (walked into, or waited about: a world without gate items still has its
moongate squares). The server log lists them at start. A teleporter, gate or telepad that leads to a map the road network does not cover
is never stepped on, and a bot found on such a map is taken home.

### Out of sight

A lift - a stuck bot set down on its leg's target, a stranded one on a road node or at
home, a bot on a map with no roads, a player killer outside its haunt - waits while a
player within 18 tiles can see where the bot stands or where it would appear, for up to
`PlayerBotUnseenLiftSec` (90 s), then goes ahead anyway (`liftwaits` in the telemetry). A
new bot logs in at a spot no player sees when one of eight picks finds one, and a bot
over `PlayerBotCount` logs out only where nobody sees it.

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
| `staticdoors` | map (static) doors opened |
| `deposits`, `withdrawals`, `marks`, `recalls`, `companions`, `pauses` | bank moves; runes marked; recalls home; bots taken along; stops to look about |
| `favors`, `favorfails`, `gates`, `gifts`, `offers` | favours done for players and dropped; gates opened; presents taken; help offered unasked |
| `newfriends`, `bondpoints`, `companionsnow` | bonds that became friendships; bond points given (net); bots going about with a player now |
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
| `PLAYERBOTS TRACE <uid\|name>` | one bot's last 32 decisions with their age (trip chosen, search asked and found, path dropped and why, portal crossed, rescue, walk given up, refused step kinds, and the system messages its client was told) |
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
