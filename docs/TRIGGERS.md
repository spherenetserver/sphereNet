# Script Trigger Reference

Every trigger SphereNet actually fires, with the script values available inside it.

## Argument model

When a trigger runs, the script body executes **on the object the trigger belongs to** (`this` / the implicit `i` context) and can read these variables:

| Variable | Source field | Meaning |
|---|---|---|
| `<src>` | `CharSrc ?? ItemSrc` | The actor that caused the trigger |
| `<argo>` | `Object1` (`O1`) | Primary related object |
| `<act>` | `Object2` (= `CharSrc ?? ItemSrc`) | Same as `<src>` in every wiring |
| `<argn>` / `<argn1>` | `Number1` (`N1`) | First number |
| `<argn2>` | `Number2` (`N2`) | Second number |
| `<argn3>` | `Number3` (`N3`) | Third number |
| `<args>` | `ArgString` (`S1`) | String argument |

`<act>` always equals `<src>`, so it is omitted from the tables below. "this" is the trigger's owning object. **RETURN 1** describes what cancelling the trigger does (blank = return value is ignored).

> Source of truth: `TriggerDispatcher.cs` (`WrapArgs`, name maps), `TriggerTypes.cs` (enums), and every `FireCharTrigger` / `FireItemTrigger` call site. Only triggers with a real fire site are listed; enum-only triggers are in the last section.

Current guardrail snapshot (2026-07-18): the only character trigger defined but not fired is `UserVirtue` (the virtue-gump select path / Event_VirtueSelect; SphereNet has no virtue gump yet). `NPCSeeWantItem` is now wired and fires from the NPC ground-item scan. The item-trigger backlog is empty. This is locked by `TriggerCoverageGuardrailTests`.

---

## Character triggers

| Trigger | When | this | `<src>` | `<argo>` | `<argn>` / `<argn2>` | `<args>` | RETURN 1 |
|---|---|---|---|---|---|---|---|
| `@Attack` | Player starts an attack | attacker | attacker | target | – | – | cancels the attack |
| `@CombatStart` | Combat begins after an attack passes | attacker | attacker | target | – | – | cancels combat |
| `@HitTry` | Each swing attempt (before the swing) | attacker | victim | weapon | N1=swing delay (1/10s, writable); `LOCAL.Anim`/`LOCAL.AnimDelay` override the swing animation | – | cancels the swing |
| `@HitCheck` | Swing start, BEFORE range/LoS validation | attacker | victim | weapon | N1=war swing state, N2=damage type; `LOCAL.Recoil_NoRange` (seeded from SWING_NORANGE, writable — drives the per-swing range-ignore + windup window) | – | forces a miss (fires `@HitMiss`) |
| `@Hit` | A connecting hit, before HP applies | attacker | victim | weapon | N1=damage (writable), N2=damage type; `LOCAL.ItemDamageChance` (weapon wear %, seed 25), `LOCAL.ItemPoisonReductionChance/Amount` (poison charge spend) — shared with the weapon item `@Hit` | – | cancels the hit (0 damage) |
| `@GetHit` | When a hit is taken, before HP applies | victim | attacker | – | N1=damage (writable), N2=damage type; `LOCAL.ItemDamageLayer` (random armor layer, writable — the item `@GetHit` + durability wear target), `LOCAL.ItemDamageChance` (seed 25), `LOCAL.DamagePercent*` (elemental split, read-only) | – | cancels the hit + skips the armor wear |
| `@HitMiss` | A resolved miss or a `@HitCheck` block | attacker | victim | weapon | `LOCAL.Arrow` = the live pack ammo stack UID (ranged); `LOCAL.ArrowHandled=1` hands the ammo's fate to the script | – | skips the ammo economy (nothing consumed/dropped) |
| `@HitParry` | Defender blocks with shield/weapon | defender | attacker | attacker | N1=damage allowed through (0=full block, writable for a partial block) | – | ignored |
| `@HitIgnore` | An attacker marked with `ATTACKER.n.IGNORE=1` lands a hit | victim | victim | attacker | – | – | clears the ignore flag |
| `@Kill` | A target is killed | killer | killer | victim | – | – | ignored |
| `@Death` | A character dies | the dead | killer (null / attacker on reactive) | – | – | – | ignored |
| `@DeathCorpse` | Corpse exists and loot has transferred | the dead | the dead | corpse | – | – | ignored |
| `@Resurrect` | At resurrection | the revived | the revived | – | – | – | cancels the resurrect |
| `@UserWarmode` | Client war-mode toggle (before flip) | player | player | – | N1=1(war)/0(peace) | – | cancels the toggle |
| `@SpellCast` | Casting begins | caster | caster | – | N1=spell id | – | cancels the cast |
| `@SpellFail` | Cast fails | caster | caster | – | N1=spell id | – | ignored |
| `@SpellEffect` | Cast completes (effect moment) | caster | caster | – | N1=spell id | – | ignored |
| `@SpellEffectAdd` | A timed spell effect/buff is applied | target | caster or target | – | N1=spell id | – | ignored |
| `@SpellEffectRemove` | A timed spell effect/buff expires, refreshes, or is cleaned up | target | target | – | N1=spell id | – | ignored |
| `@SpellEffectTick` | Periodic spell-effect tick (poison bridge) before damage applies | victim | victim | spell memory shim | N1=spell id, N2=strength | LOCAL.EFFECT/DELAY/CHARGES/DAMAGETYPE | cures/destroys the effect |
| `@SpellSuccess` | Cast completes successfully | caster | caster | – | N1=spell id | – | ignored |
| `@SpellInterrupt` | Cast interrupted (e.g. by damage) | caster | caster | – | – | – | ignored |
| `@SpellSelect` | Cast request is selected before normal cast checks | caster | caster | – | N1=spell id | – | cancels selection |
| `@SpellBook` | Spellbook is opened | player | player | spellbook | – | – | keeps it shut |
| `@SpellTargetCancel` | Spell target cursor cancelled | player | player | – | N1=spell id | – | ignored |
| `@SkillPreStart` | First stage before a skill use | player | player | – | N1=skill id | – | cancels the skill |
| `@SkillStart` | Skill start | player | player | – | N1=skill id | – | cancels the skill |
| `@SkillStroke` | Skill action moment / each tick | player | player | – | N1=skill id, N2=stroke count | – | ignored |
| `@SkillSuccess` | Skill succeeds | player | player | – | N1=skill id | – | ignored |
| `@SkillFail` | Skill fails | player | player | – | N1=skill id | – | ignored |
| `@SkillGain` | Skill value increases | character | character | – | N1=skill id, N2=new value | – | ignored |
| `@SkillChange` | Skill value is about to be changed | character | character | – | N1=skill id, N2=old value, N3=new value | – | cancels / rewrites new value |
| `@StatChange` | Stat value changes through gain/decay | character | character | – | N1=stat index, N2=new value | – | ignored |
| `@SkillAbort` | Active skill interrupted | character | character | – | N1=skill id | – | ignored |
| `@SkillSelect` | A scripted skill is selected | character | character | – | N1=skill id | – | cancels the skill |
| `@SkillMenu` | Skill/craft menu selection path | player | player | – | N1=skill/menu id | – | cancels selection |
| `@SkillWait` | Delayed skill is waiting/stroking per tick | player | player | – | N1=skill id | – | cancels waiting skill |
| `@SkillUseQuick` | Instant skill check after the success roll | character | character | – | N1=skill id, N2=difficulty, N3=result | – | cancels or rewrites result |
| `@SkillMakeItem` | A recipe is chosen in the craft gump | player | player | – | N1=craft skill id | – | ignored |
| `@SkillTargetCancel` | Skill target cursor cancelled | player | player | – | N1=skill id | – | ignored |
| `@LogIn` | Character enters the world | player | player | – | – | – | ignored |
| `@LogOut` | Connection drops | player | player | – | – | – | ignored |
| `@Mount` | Mounting | rider | rider | mount (Character) | – | – | ignored |
| `@Dismount` | Dismounting | rider | rider | mount (Character) | – | – | ignored |
| `@Reveal` | Hidden/invisible state is about to drop | character | character | – | – | – | keeps concealment |
| `@Click` | Single-click (char target) | clicked char | clicker | – | – | – | cancels the name label |
| `@AfterClick` | After Click (name shown) | clicked char | clicker | – | – | – | ignored |
| `@DClick` | Double-click (char target) | target char | clicker | – | – | – | cancels the default action |
| `@ClientTooltip` | AOS tooltip requested (char) | char | viewer | – | – | – | cancels the default tooltip |
| `@ContextMenuRequest` | Context menu opens (char) | char | opener | – | N1=0 | – | ignored |
| `@ContextMenuSelect` | Context menu selection (char) | char | selector | – | N1=entry tag | – | ignored |
| `@HouseDesignCommit` | Custom house design commit command | player | player | – | – | – | ignored |
| `@HouseDesignExit` | Custom house design close/exit command | player | player | – | – | – | ignored |
| `@Create` | NPC/character created (def applied) | new NPC | the NPC | – | – | – | ignored |
| `@CreateLoot` | Loot stage during NPC creation | new NPC | the NPC (or creator) | – | – | – | ignored |
| `@Destroy` | Character deleted (`.remove`) | the deleted | the remover | – | – | – | ignored |
| `@ReceiveItem` | An item is given to an NPC | NPC | giver | item | – | – | treats item as fully consumed (NPC won't pack it) |
| `@NPCAcceptItem` | NPC accepts an item into its pack | NPC | giver | item | – | – | ignored |
| `@Profile` | Paperdoll profile read/write | profile owner | requester | – | N1=mode (1=write) | S1=bio (on write) | cancels the default profile op |
| `@UserStats` | Client opens its status window | player | player | – | – | – | ignored |
| `@UserSkills` | Client requests its skill list | player | player | – | – | – | ignored |
| `@UserExtCmd` | Extended cmd / menu reply (raw) | player | player | – | – | S1=command/menu string | ignored |
| `@UserChatButton` | 0xBF 0x000B chat button | player | player | – | N1=0x000B | – | ignored |
| `@UserGuildButton` | 0xBF 0x0028 guild button | player | player | – | N1=0x0028 | – | ignored |
| `@UserQuestButton` | 0xBF 0x0032 quest button | player | player | – | N1=0x0032 | – | ignored |
| `@UserVirtueInvoke` | 0xBF 0x002C virtue invoke | player | player | – | N1=0x002C, N2=virtue id | – | ignored |
| `@Rename` | GM rename request | target char | renamer | – | – | S1=new name | cancels the rename |
| `@RegionEnter` | Entering a new region | the char | (not set) | – | – | S1=region name | ignored |
| `@RegionLeave` | Leaving a region | the char | (not set) | – | – | S1=old region name | ignored |
| `@RegionStep` | Step within the same region | the char | (not set) | – | – | S1=region name | ignored |
| `@RoomEnter` | Entering a new room | the char | (not set) | – | – | S1=room name | ignored |
| `@RoomLeave` | Leaving a room | the char | (not set) | – | – | S1=old room name | ignored |
| `@RoomStep` | Step within the same room | the char | (not set) | – | – | S1=room name | ignored |
| `@Hunger` | Hunger decay tick | char | char | – | – | – | ignored |
| `@Criminal` | Before a crime flag is set | char | char | – | – | – | cancels the criminal flag |
| `@SeeCrime` | A nearby NPC witnesses a crime | witness NPC | the criminal | – | – | – | ignored |
| `@MurderMark` | PvP murder count is about to be recorded | killer | killer | victim | N1=proposed count, N2=criminal flag toggle | – | blocks mark and criminal flag |
| `@MurderDecay` | One murder count decays off | murderer | murderer | – | N1=new kill count, N2=next-decay seconds override | – | ignored |
| `@KarmaChange` | Karma delta is about to apply | character | character | – | N1=delta | – | cancels or rewrites delta |
| `@FameChange` | Fame delta is about to apply | character | character | – | N1=delta | – | cancels or rewrites delta |
| `@NotoSend` | A viewer is about to receive a notoriety byte for a subject | subject | viewer | – | N1=computed notoriety | – | rewrites displayed noto |
| `@SeeSnoop` | A snoop is observed | witness | snooper | – | – | – | ignored |
| `@StepStealth` | Stepping while hidden | char | char | – | – | – | ignored |
| `@PersonalSpace` | Movement shoves past another character's tile | mover | mover | blocker | – | – | ignored |
| `@PetDesert` | Pet loyalty reaches zero and it is about to go wild | pet | pet | owner | – | – | cancels desertion |
| `@Jail` | Character is sent to jail | jailed char | jailed char | – | N1=sentence minutes | – | ignored |
| `@EnvironChange` | Perceived light level changes | character | character | – | N1=new light level | – | ignored |
| `@ExpChange` | Experience delta is about to apply | character | character | – | N1=delta | – | cancels or rewrites delta |
| `@ExpLevelChange` | Experience level changes | character | character | – | N1=new level | – | ignored |
| `@PartyInvite` | On party invite | invitee | inviter | – | – | – | ignored |
| `@PartyDisband` | Party drops to zero / disbands | last member | last member | – | – | – | ignored |
| `@PartyLeave` | Leaving a party | leaver | leaver | – | – | – | ignored |
| `@PartyRemove` | Removed from a party | removed | remover | – | – | – | ignored |
| `@TradeCreate` | Secure trade starts | each side | other side | other side | N1=session id | – | ignored |
| `@TradeAccepted` | Both sides accept | each side | other side | other side | N1=session id | – | ignored |
| `@TradeClose` | Trade closes | each side | other side | other side | N1=session id | – | ignored |
| `@CombatAdd` | A new attacker enters the character's attacker list | character | character | attacker | – | – | ignored |
| `@CombatDelete` | An attacker leaves the character's attacker list | character | character | attacker | – | – | ignored |
| `@CombatEnd` | Attacker list transitions to empty | character | character | – | – | – | ignored |
| `@NPCRestock` | Vendor restock | vendor NPC | vendor (or buyer on the ItemUse path) | – | – | – | ignored |
| `@NPCAction` | Vendor buy/sell action | vendor NPC | buyer | – | – | S1="BUY"/"SELL" | ignored |
| `@NPCHearGreeting` | NPC hears a greeting | NPC | speaker | – | – | S1=spoken text | treats speech as handled |
| `@NPCHearUnknown` | NPC hears unrecognized speech | NPC | speaker | – | – | S1=text | ignored |
| `@NPCSeeNewPlayer` | NPC perceives a player it has not seen recently | NPC | NPC | newly-seen player | – | – | ignored |
| `@NPCLookAtChar` | AI sees a character | NPC | seen char | – | N1=target uid | – | overrides the AI action |
| `@NPCLookAtItem` | AI sees an item | NPC | NPC | item | N1=item uid | – | overrides the AI action |
| `@NPCActFight` | AI picks a combat action | NPC | target char | – | N1=target uid | – | overrides the action |
| `@NPCActWander` | AI wanders | NPC | NPC | – | – | – | overrides the action |
| `@NPCActFollow` | AI follow action | NPC | followed | followed | – | – | overrides the action |
| `@NPCActCast` | AI decides to cast | NPC | target | target | N1=spell id | – | overrides the action |
| `@NPCSpecialAction` | NPC special action such as breath/throw is about to run | NPC | target | target | – | – | cancels special action |
| `@NPCLostTeleport` | Severely lost NPC is about to teleport home | NPC | NPC | – | – | – | cancels teleport |
| `@CallGuards` | Guard keyword reports a hostile/criminal | speaker | speaker | hostile | – | – | cancels reporting that hostile |
| `@UserVirtue` | Virtue button path | player | player | – | N1=subcommand/virtue id | – | ignored |
| `@UserKRToolbar` | KR toolbar extended command | player | player | – | N1=0x24 | – | ignored |
| `@UserQuestArrowClick` | Quest arrow click extended command | player | player | – | N1=0x07 | – | ignored |
| `@UserBugReport` | Crash/bug report packet | player | player | – | N1=0x00F4 | – | ignored |
| `@UserUltimaStoreButton` | Ultima Store button packet | player | player | – | N1=0x00FA | – | ignored |
| `@UserGlobalChatButton` | Chat window open packet | player | player | – | N1=0x00B5 | – | ignored |
| `@UserMailBag` | Legacy mail-drop packet (0xBB) | recipient | sender | – | – | – | cancels recipient notification |
| `@UserExWalkLimit` | Fast-walk token bucket is exhausted | player | player | – | – | – | ignored |
| `@UserSpecialMove` | Encoded combat special move command | player | player | – | N1=ability index | – | ignored |

Notes:
- `@RegionEnter/Leave/Step` and `@Room*` do **not** set `<src>` (only `S1`); the region/room name is in `<args>`. The region's own EVENTS `@Enter/@Exit/@Step` blocks fire separately via `FireRegionEvents` with `<src>` set.
- Region EVENTS also receive two periodic triggers via `FireRegionEvents` (fired from the environment tick, ~6s, only while players are present): `@CliPeriodic` fires once per online player in the region (`<src>` = that player, `<args>` = region name); `@RegPeriodic` fires once per inhabited region per tick (`<src>` = a representative player in that region, `<args>` = region name). Uninhabited regions never tick. Mirrors Source-X `CSector` `iRegionPeriodic`.
- `@Death` on the reactive-armor path (attacker dies) has `<src>` = the target.
- `@NPCRestock` fires from two paths: spawn/finalize (`<src>` = the NPC) and opening a vendor (`<src>` = the player).

---

## Item triggers

| Trigger | When | this (item) | `<src>` | `<argo>` | `<argn>` / `<argn2>` | `<args>` | RETURN 1 |
|---|---|---|---|---|---|---|---|
| `@Click` | Single-click (item) | item | clicker | – | – | – | cancels the name label |
| `@AfterClick` | After Click | item | clicker | – | – | – | ignored |
| `@DClick` | Double-click | item | user | – | – | – | cancels the default use |
| `@Create` | Item created (crafting result) | item | creator | – | – | – | ignored |
| `@Destroy` | Item deleted (eaten / `.remove` / decay / deed) | item | remover | – | – | – | cancels the default delete (food/drink/deed paths) |
| `@Step` | Stepped on (trap/telepad/moongate/switch) | item | the stepper | – | – | – | cancels the native step effect |
| `@Timer` | Item timer expires | item | (not set) | – | – | – | (returns TriggerResult) |
| `@Pickup_Ground` | Picked up loose from the ground | item | picker | – | – | – | cancels the pickup |
| `@Pickup_Pack` | Picked up from inside a container | item | picker | – | – | – | cancels the pickup |
| `@Pickup_Self` | Dragged off the picker's own equipment layers | item | picker | – | – | – | cancels the pickup |
| `@Pickup_Stack` | A partial amount split out of a larger stack | item | picker | – | – | – | cancels the pickup |
| `@Equip` | Equipped | item | wearer | – | – | – | ignored |
| `@EquipTest` | Tested before equipping | item | wearer | – | – | – | refuses the equip |
| `@Unequip` | Unequipped (picking up worn item) | item | remover | – | – | – | cancels the unequip |
| `@DropOn_Item` | Dropped onto a container/item | item | dropper | target container | – | – | cancels the drop |
| `@DropOn_Self` | Dropped onto the player itself | item | dropper | – | – | – | cancels the drop (item returns to pack) |
| `@DropOn_Char` | Dropped onto another character | item | dropper | target char | – | – | cancels the drop |
| `@DropOn_Ground` | Dropped on the ground | item | dropper | – | – | – | cancels the drop |
| `@DropOn_Trade` | Dropped into a trade window | item | dropper | trade partner | N1=session id | – | cancels the drop |
| `@Hit` | Weapon lands a hit | weapon | attacker | target char | N1=damage | – | ignored |
| `@GetHit` | Shield/armor takes a hit | shield | attacker | – | N1=damage | – | ignored |
| `@Damage` | Item loses durability | item | (not set) | – | N1=durability lost | – | cancels the durability loss |
| `@Dye` | Dye applied (dye reply / dye vat) | item | dyer | dye vat (vat path) | N1=hue | – | cancels the dye |
| `@SpellEffect` | Item-targeted spell effect resolves | item | caster | caster | N1=spell id | S1=spell name | cancels native item spell effect |
| `@Smelt` | Ore is targeted for smelting | ore item | smelter | target item | – | S1=smelt message | cancels smelt |
| `@Buy` | Bought from a vendor | item | buyer | vendor | N1=amount, N2=price | – | ignored |
| `@Sell` | Sold to a vendor | item | seller | vendor | N1=amount, N2=price | – | ignored |
| `@MemoryEquip` | Memory item is equipped on a character | memory item | (not set) | – | – | – | ignored |
| `@Redeed` | A house/multi collapses into a deed | deed item | deed item | – | – | – | ignored |
| `@ClientTooltip` | AOS tooltip (item) | item | viewer | – | – | – | cancels the default tooltip |
| `@ClientTooltipAfterDefault` | After default tooltip lines added | item | viewer | – | – | – | ignored |
| `@ToolTip` | Single-click tooltip hook before `@Click` | item | viewer | – | – | – | ignored |
| `@ContextMenuRequest` | Context menu opens (item) | item | opener | – | N1=0 | – | ignored |
| `@ContextMenuSelect` | Context menu selection (item) | item | selector | – | N1=entry tag | – | ignored |
| `@TargOn_Char` | Item-sourced target → char | source item | targeter | target char | N1=x, N2=y, N3=z | S1=graphic | cancels the targeting |
| `@TargOn_Item` | Item-sourced target → item | source item | targeter | target item | N1=x, N2=y, N3=z | S1=graphic | cancels the targeting |
| `@TargOn_Ground` | Item-sourced target → ground | source item | targeter | – | N1=x, N2=y, N3=z | S1=graphic | cancels the targeting |
| `@TargOn_Cancel` | Item target cursor cancelled | source item | canceller | – | N1=x, N2=y, N3=z | S1=graphic | ignored |
| `@CarveCorpse` | A corpse is carved | corpse | carver | – | – | – | cancels carving (no loot) |
| `@PreSpawn` | Spawner before producing | spawner item | spawned char (if any) | spawned char | N1=spawn def index | – | cancels the spawn |
| `@Spawn` | Spawner produces | spawner item | spawned char | spawned char | N1=spawn def index | – | cancels the spawn |
| `@AddObj` | Spawner object added | spawner item | added char | added char | – | – | ignored |
| `@DelObj` | Spawner object removed | spawner item | removed char | removed char | – | – | ignored |
| `@Start` | Spawner START verb toggles spawning on | spawner item | spawner item | – | – | – | ignored |
| `@Stop` | Spawner STOP verb toggles spawning off | spawner item | spawner item | – | – | – | ignored |
| `@ShipMove` | Ship/movable multi moves | ship multi | pilot | – | – | – | ignored |
| `@ShipStop` | Ship/movable multi stops | ship multi | pilot | – | – | – | ignored |
| `@ShipTurn` | Ship/movable multi turns | ship multi | pilot | – | – | – | ignored |
| `@RegionLeave` | Ship/movable multi leaves a region boundary | ship multi | pilot | old region | – | – | blocks the move step |
| `@RegionEnter` | Ship/movable multi enters a region boundary | ship multi | pilot | new region | – | – | blocks the move step |
| `@Hear` | Nearby item hears speech | item | speaker | – | N1=talk mode | S1=spoken text | ignored |

Notes:
- `@TargOn_*`: `<argn1/2/3>` are the target point's x/y/z; `<args>` is the targeted graphic id (as a string).
- `@Damage` and `@Timer` do not set `CharSrc` (only `ItemSrc`), so `<src>`/`<act>` = the item itself.

---

## Cross-fired `item*` character triggers

When an item trigger fires, `TriggerDispatcher` also runs the matching `item<TrigName>` block (e.g. `@itemDClick`, `@itemEquip`, `@itemStep`) on the **`<src>` character** as a cross-target. These have no dedicated fire site — they are a by-product of the item trigger, letting a character script react to its items.

## Resource triggers

`@ResourceGather` / `@ResourceTest` fire on a `REGIONRESOURCE` definition via `FireResourceTrigger` (`GatheringEngine.cs`), not on a normal item. `<src>` = the gatherer; `@ResourceTest` provides N1=SkillMin, N2=SkillMax; `@ResourceGather` provides N1=reap amount.

---

## Defined but not fired

These exist in `TriggerTypes.cs` (and have name mappings) but have **no** literal `FireCharTrigger` / `FireItemTrigger` call site, so scripts hooking them will not run today. This list is locked by `TriggerCoverageGuardrailTests`, which recomputes it from the engine source on every run — wiring a backlog trigger (or adding a new enum value) fails that test until this list is updated.

> The `Char*` and `item*` mirror families (e.g. `CharAttack`, `itemDClick`) are **not** in this list: they are fired indirectly by name through the dispatcher cross-fire path (`"char" + name` / `"item" + name` in `TriggerDispatcher.cs`), so a script hooking them does run.

Each trigger carries a wiring priority by shard impact (P0 highest). The buckets are encoded in `TriggerCoverageGuardrailTests` and partition the backlog exactly.

**Character (1)**

- **P0/P1:** none currently documented as unfired.
- **P2:** `UserVirtue` (the virtue-gump select path, Source-X Event_VirtueSelect / 0xB1 dialog) is not fired — SphereNet has no virtue gump yet. Distinct from `UserVirtueInvoke` (the 0x12/0xF4 hotkey), which IS fired.
  - `NPCSeeWantItem` is now wired and fires from the NPC ground-item scan (previously deferred).

**Item (0):** all item triggers have a real fire site.

Deferred reasons: item leveling is not implemented, and champion altar/candle infrastructure is not implemented. `ResourceGather` / `ResourceTest` are fired via the resource path, not the normal item path, so they are excluded from this backlog.
