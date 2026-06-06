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

---

## Character triggers

| Trigger | When | this | `<src>` | `<argo>` | `<argn>` / `<argn2>` | `<args>` | RETURN 1 |
|---|---|---|---|---|---|---|---|
| `@Attack` | Player starts an attack | attacker | attacker | target | – | – | cancels the attack |
| `@CombatStart` | Combat begins after an attack passes | attacker | attacker | target | – | – | cancels combat |
| `@HitTry` | Each swing attempt (before the swing) | attacker | attacker | target | N1=swing delay (1/10s); writing N1 changes it | – | cancels the hit |
| `@HitCheck` | After HitTry, while resolving the hit | attacker | attacker | target | – | – | turns it into a miss (fires `@HitMiss`) |
| `@Hit` | Damage > 0 (a landed hit) | attacker | attacker | target | N1=damage | – | ignored |
| `@GetHit` | When a hit is taken | victim | attacker | – | N1=damage | – | ignored |
| `@HitMiss` | Miss (damage=0) or HitCheck block | attacker | attacker | target | – | – | ignored |
| `@HitParry` | Defender blocks with shield/weapon | defender | attacker | attacker | – | – | ignored |
| `@Kill` | A target is killed | killer | killer | victim | – | – | ignored |
| `@Death` | A character dies | the dead | killer (null / attacker on reactive) | – | – | – | ignored |
| `@Resurrect` | At resurrection | the revived | the revived | – | – | – | cancels the resurrect |
| `@UserWarmode` | Client war-mode toggle (before flip) | player | player | – | N1=1(war)/0(peace) | – | cancels the toggle |
| `@SpellCast` | Casting begins | caster | caster | – | N1=spell id | – | cancels the cast |
| `@SpellFail` | Cast fails | caster | caster | – | N1=spell id | – | ignored |
| `@SpellEffect` | Cast completes (effect moment) | caster | caster | – | N1=spell id | – | ignored |
| `@SpellSuccess` | Cast completes successfully | caster | caster | – | N1=spell id | – | ignored |
| `@SpellInterrupt` | Cast interrupted (e.g. by damage) | caster | caster | – | – | – | ignored |
| `@SpellTargetCancel` | Spell target cursor cancelled | player | player | – | N1=spell id | – | ignored |
| `@SkillPreStart` | First stage before a skill use | player | player | – | N1=skill id | – | cancels the skill |
| `@SkillStart` | Skill start | player | player | – | N1=skill id | – | cancels the skill |
| `@SkillStroke` | Skill action moment / each tick | player | player | – | N1=skill id, N2=stroke count | – | ignored |
| `@SkillSuccess` | Skill succeeds | player | player | – | N1=skill id | – | ignored |
| `@SkillFail` | Skill fails | player | player | – | N1=skill id | – | ignored |
| `@SkillGain` | Skill value increases | character | character | – | N1=skill id, N2=new value | – | ignored |
| `@SkillAbort` | Active skill interrupted | character | character | – | N1=skill id | – | ignored |
| `@SkillSelect` | A scripted skill is selected | character | character | – | N1=skill id | – | cancels the skill |
| `@SkillMakeItem` | A recipe is chosen in the craft gump | player | player | – | N1=craft skill id | – | ignored |
| `@SkillTargetCancel` | Skill target cursor cancelled | player | player | – | N1=skill id | – | ignored |
| `@LogIn` | Character enters the world | player | player | – | – | – | ignored |
| `@LogOut` | Connection drops | player | player | – | – | – | ignored |
| `@Mount` | Mounting | rider | rider | mount (Character) | – | – | ignored |
| `@Dismount` | Dismounting | rider | rider | mount (Character) | – | – | ignored |
| `@Click` | Single-click (char target) | clicked char | clicker | – | – | – | cancels the name label |
| `@AfterClick` | After Click (name shown) | clicked char | clicker | – | – | – | ignored |
| `@DClick` | Double-click (char target) | target char | clicker | – | – | – | cancels the default action |
| `@ClientTooltip` | AOS tooltip requested (char) | char | viewer | – | – | – | cancels the default tooltip |
| `@ContextMenuRequest` | Context menu opens (char) | char | opener | – | N1=0 | – | ignored |
| `@ContextMenuSelect` | Context menu selection (char) | char | selector | – | N1=entry tag | – | ignored |
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
| `@StepStealth` | Stepping while hidden | char | char | – | – | – | ignored |
| `@PartyInvite` | On party invite | invitee | inviter | – | – | – | ignored |
| `@PartyLeave` | Leaving a party | leaver | leaver | – | – | – | ignored |
| `@PartyRemove` | Removed from a party | removed | remover | – | – | – | ignored |
| `@TradeCreate` | Secure trade starts | each side | other side | other side | N1=session id | – | ignored |
| `@TradeAccepted` | Both sides accept | each side | other side | other side | N1=session id | – | ignored |
| `@TradeClose` | Trade closes | each side | other side | other side | N1=session id | – | ignored |
| `@NPCRestock` | Vendor restock | vendor NPC | vendor (or buyer on the ItemUse path) | – | – | – | ignored |
| `@NPCAction` | Vendor buy/sell action | vendor NPC | buyer | – | – | S1="BUY"/"SELL" | ignored |
| `@NPCHearGreeting` | NPC hears a greeting | NPC | speaker | – | – | S1=spoken text | treats speech as handled |
| `@NPCHearUnknown` | NPC hears unrecognized speech | NPC | speaker | – | – | S1=text | ignored |
| `@NPCLookAtChar` | AI sees a character | NPC | seen char | – | N1=target uid | – | overrides the AI action |
| `@NPCLookAtItem` | AI sees an item | NPC | NPC | item | N1=item uid | – | overrides the AI action |
| `@NPCActFight` | AI picks a combat action | NPC | target char | – | N1=target uid | – | overrides the action |
| `@NPCActWander` | AI wanders | NPC | NPC | – | – | – | overrides the action |
| `@NPCActFollow` | AI follow action | NPC | followed | followed | – | – | overrides the action |
| `@NPCActCast` | AI decides to cast | NPC | target | target | N1=spell id | – | overrides the action |

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
| `@Pickup_Ground` | Picked up from the ground | item | picker | – | – | – | cancels the pickup |
| `@Pickup_Pack` | Picked up from a container | item | picker | – | – | – | cancels the pickup |
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
| `@Buy` | Bought from a vendor | item | buyer | vendor | N1=amount, N2=price | – | ignored |
| `@Sell` | Sold to a vendor | item | seller | vendor | N1=amount, N2=price | – | ignored |
| `@ClientTooltip` | AOS tooltip (item) | item | viewer | – | – | – | cancels the default tooltip |
| `@ClientTooltipAfterDefault` | After default tooltip lines added | item | viewer | – | – | – | ignored |
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

**Character (26)**

- **P0 (3)** — all remaining are deferred (need infrastructure first): `HitIgnore`, `NPCSeeNewPlayer`, `NPCSeeWantItem`.
  - **Wired** (the rest of the original P0 set): `KarmaChange`, `FameChange`, `MurderMark` (`DeathEngine` + `Character.On*`); `SkillChange`, `StatChange` (`SkillEngine` gain hooks, runtime gain only); `CombatAdd`, `CombatDelete`, `CombatEnd` (`Character` attacker-list hooks); `NPCRefuseItem` (drop-on-NPC accept gate); `NPCSpecialAction` (breath/throw); `MurderDecay` (`Character` notoriety-decay hook); `NotoSend` (`ComputeNotoriety` via `Character.OnNotoSend`, installed only when the **IsTrigUsed gate** — `TriggerDispatcher.IsCharTriggerUsed` / `BuildUsedTriggerCache` — reports `@NotoSend` hooked, so the per-observer hot path stays a null check otherwise).
  - **Deferred — infrastructure first:** `HitIgnore` (no attacker "ignore" flag / NPC ignore-scan); `NPCSeeNewPlayer` (no per-NPC seen-player memory; `@NPCLookAtChar` already fires on every scan); `NPCSeeWantItem` (NPCs only scan corpses; no ground-item "want" logic). Skill/stat **decay** trigger coverage is also a follow-up.
- **P1 (9)** — moderate: PetDesert, PersonalSpace, ExpChange, ExpLevelChange, SkillUseQuick, Jail, CallGuards, EnvironChange, EffectAdd. *(Wired since: `Eat` (food/booze use-item gate), `SkillMenu` (skill selection menu), `SkillWait` (per-tick skill-in-progress, IsTrigUsed-gated), `Follow` (pet "follow me"/"come" command), `PartyDisband` (party drops to 0 members), `SpellSelect` (cast request, before mana/skill checks), `SpellBook` (spellbook open). Deferred: `SkillUseQuick` — `UseQuick` is atomic, pre-roll cancel needs a check/gain split; `Jail` — no central jail method; `CallGuards` — no "guards" keyword / guard-summon system; `PetDesert` — no pet loyalty/happiness meter, decay, or go-wild path.)*
- **P2 (14)** — low: the `User*` modern-client buttons (UserBugReport, UserExWalkLimit, UserGlobalChatButton, UserKRToolbar, UserMailBag, UserQuestArrowClick, UserSpecialMove, UserUltimaStoreButton, UserVirtue), HouseDesignCommit, HouseDesignExit, ToolTip, Targon_Cancel, NPCLostTeleport.

**Item (20)** — all **P2** (no core gameplay gate today): SpellEffect, MemoryEquip, Redeed, RegionEnter, RegionLeave, ShipMove, ShipStop, ShipTurn, Smelt, Start, Stop, Level, Complete, AddRedCandle, AddWhiteCandle, DelRedCandle, DelWhiteCandle, PickupSelf, PickupStack, Tooltip. (`ResourceGather` / `ResourceTest` are fired via the resource path above, not as item triggers, so they are excluded.)
