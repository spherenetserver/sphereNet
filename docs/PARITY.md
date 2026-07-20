# SphereNet Source-X / Sphere 56x Parity Matrix

Single parity document: **Part 1** is the domain-level summary matrix, **Part 2**
is the itemised behaviour-surface detail (formerly `PARITY_MATRIX.md`, merged
2026-07-18). Every non-covered item names either a regression test to add/extend
or an explicit deferred reason so the backlog does not turn into folklore.
The deferred tail lives in the "Open threads" section at the end; open
verification/action items live in `INCELEME_DOGRULAMA_PLANI_TR.md`.

Current verification snapshot (2026-07-18): `dotnet test .\src\SphereNet.Tests\SphereNet.Tests.csproj --nologo` passes 1867 tests (3 skipped). The current overall Source-X parity estimate is about 8.8/10; the remaining 9.x work is concentrated in ship/statics packet fidelity, native statics output, client/admin verb long tail, and a few infrastructure-gated triggers.

Important terminology note: `SERV.*`, arbitrary verb fallback, and trigger return/argument propagation are not missing as core systems. They are implemented for the common runtime paths. Remaining gaps are mostly the long-tail Source-X server/admin verbs and a few exact context/VarObj edge cases.

---

# Part 1 — Summary matrix

## Scripting Parity

| Area | Status | Test / Guardrail | Notes |
|---|---|---|---|
| External script pack bridge matrix | Covered | `ExternalScriptPackSmokeTests`, `ScriptPackCompatibilitySummary` | Category scoring now tracks load, runtime triggers, DB/LDB, `SERV`, dialogs, vendor/craft, world/map, multi/housing, packet and staff/worldgen gaps. |
| External P0 bridges | Covered | `GameSystemTests`, `ExternalScriptPackSmokeTests` | `SERV.AREA`, `SERV.GMPAGE`, `SERV.LIST`, `SERV.DEFLIST`, scalar `SERV.*`, `SERV.WRITEFILE`, `SERV.LOG`, `SERV.ALLCLIENTS`, `SERV.NEWITEM` and safe `LDB.CONNECT` path handling are compatibility-safe. |
| `TIMERF` callbacks | Covered | `GameSystemTests` | `TIMERF delay,function args` queues delayed callbacks on chars/items and runs due entries from world tick, with deleted-object no-op behavior. |
| `SENDPACKET` compatibility | Covered | `GameSystemTests` | Safe byte/word/dword raw packet parser sends to owner client and rejects invalid/out-of-range payloads without crashing. |
| External world sections | Covered | `DefinitionAndSpellRegressionTests` | `[STARTS]`, `[STARTSGOLD]`, `[MOONGATES]`, numeric `[MULTIDEF]` and retained MULTIDEF keys are loaded for diagnostics/runtime lookup. |
| Worldgen safety profile | Covered | `ExternalScriptPackSmokeTests` | `functions/worldgen/**` is excluded from core profile and loaded only through the explicit audit profile. |
| Save/load script-facing fields | Covered | `SaveFormatTests` | `EQUIP[n]`, nested containers, bank box roundtrips. |
| Trigger dispatch basics | Covered | `GameSystemTests`, script fixtures | Client, NPC, combat, trade, vendor, context menu and `f_onitem_*` hooks. |
| Expression float pipeline | Covered | `ExpressionRegressionTests`, `ScriptObjectParityTests` | `FEVAL`, `FLOATVAL`, `FHVAL`, `FVAL`, leading-zero hex and local `FLOAT.*` are covered. |
| String helper functions | Covered | `ExpressionRegressionTests` | `STRREPLACE`, `STRJOIN`, safe regex, `ISOBSCENE=0` compatibility fallback. |
| `LOCAL.*`, `DLOCAL.*`, `REFn.*` | Covered | `ScriptObjectParityTests` | Scope locals, decimal-forced locals, ref property reads and ref command bridge are covered. |
| Source-X verb inventory guardrail | Covered | `SourceXVerbInventoryGuardrailTests` | Pins upstream `CObjBase`/`CChar`/`CItem`/`CClient` function tables and `CServer::sm_szVerbKeys` so new Source-X surface or backlog drift becomes visible in CI. |
| `SERV.*`, `UID.*` common runtime paths | Covered | `ScriptObjectParityTests`, `GameSystemTests` | Server property reads, direct UID reads/dispatch, `SERV.ALLCLIENTS`, `SERV.WRITEFILE`, `SERV.LOG`, `SERV.GMPAGE`, `SERV.NEWITEM`, `SERV.NEWDUPE`, `SERV.SEASON`, `SAVE`/`RESYNC`/`SHUTDOWN` bridge and account/char/item/map/skill lookups are covered. |
| Arbitrary verb / function fallback | Covered | `GameSystemTests`, `ScriptObjectParityTests` | Unknown script commands fall through to named `[FUNCTION]` calls; the same pattern exists for `SRC.` commands after property/verb/script-command dispatch. |
| Trigger return/argument propagation | Covered | `TriggerCoverageGuardrailTests`, `GameSystemTests`, focused parity tests | `RETURN`, `ARGS`, `ARGV`, `ARGN1/2/3`, `ARGO`, `ACT`, shared `LOCAL.*`, and ARGN copy-back through chained trigger blocks are covered. |
| Source-X server/admin verb long tail | Partial | `SourceXVerbInventoryGuardrailTests`, `ParityMatrixServTests`, `ParityWaveI1Tests` | `VARLIST`, `PRINTLISTS`, `CLEARLISTS`, `EXPORT`, `IMPORT`, `RESTORE`, `SECURE`, `SHRINKMEM`, `INFORMATION`, `LOAD`, `GARBAGE`, indexed `SERV.ACCOUNT.n`, `CHATFLAGS`, `GENERICSOUNDS`, and `HEARALL` are now accounted for. Remaining long tail: native `SAVESTATICS` map output and console/security-sensitive admin operations. |
| `DEFMSG` runtime override | Partial | `ParityWaveI1Tests`, `ServerMessagesTests` | `DEFMSG.*` reads and `DEFMSG name=value` runtime override now flow through `ServerMessages`. Persistent save/load of runtime changes is still open. |
| `HOUSE.n` script API | Covered | `HousingEconomyTests` | Owned-house indexing, owner/co-owner/friend/ban lists, lockdown/secure predicates and decay stages are covered. |
| Duplicate spell definition model | Covered | `DefinitionAndSpellRegressionTests` | Runtime uses `SphereNet.Game.Magic.SpellDef`; obsolete scripting duplicate was removed. |

## Gameplay Parity

| Area | Status | Test / Guardrail | Notes |
|---|---|---|---|
| Combat gates and triggers | Covered | `CombatHelperTests`, `CombatEngineTests`, `GameSystemTests` | Safe region, range, shield/bow, movement delay, `@Hit*`, notoriety feedback. |
| Swing-state machine | Covered | `CombatSwingParityTests` | `Ready`, `Swinging`, `Equipping` and `EquippingNoWait` now drive equip waits and swing recoil. |
| Archery projectile feedback | Covered | `CombatHelperTests`, `CombatSwingParityTests` | Ammo/range/movement checks and Sphere 56x-style arrow projectile packet assertions are covered. |
| Active skill delay/stroke | Covered | `SkillDelayTests`, `ActiveSkillStrokeMatrixTests` | Single model: stateless `ActiveSkillEngine` (one call → success/fail) wrapped by the client per-tick `@SkillStroke` loop (`TickPendingSkill`). Locked: PreStart/Start/Stroke/Success ordering, multi-stroke loop order, target-cancel, and movement/damage interrupt both at start and mid-loop (fires `@SkillAbort`, no success/fail). |
| Item-use target flows | Covered | `ItemUseParityTests` | Weapon DClick -> poisoning, repair/tinkering and trap DClick -> remove-trap paths are covered. |
| Vendor/trade safety | Covered | `TradeSafetyTests`, `HousingEconomyTests`, `VendorTradeTests`, `VendorPacketRoundtripTests` | Disconnect/weight rollback, nested gold stacks and sell validation covered; raw `0x3B`/`0x9F` bytes now roundtrip through the real parser and `GameClient.HandleVendorBuy/Sell` into `VendorEngine` (stock decrement, gold settlement, crafted-serial rejection). |
| Housing economy | Partial | `HousingEconomyTests` | Placement/account limits, access-list script surface and decay covered; richer house gump/transfer packet roundtrips remain open. |

## Network / Crypto / Client Parity

| Area | Status | Test / Guardrail | Notes |
|---|---|---|---|
| Sphere 56x default client profile | Covered | `PacketEraCompatibilityTests` | Unknown clients default to old packet formats via `ClientEra=Sphere56x`. |
| Modern feature gates | Covered | `PacketEraCompatibilityTests`, AOS tooltip tests | Modern `0xDF` buff and AOS tooltip behavior require client/version support. |
| Crypto primitives | Covered | `EncryptionTests` | Login, Blowfish, Twofish, no-crypt login/game-login, Huffman roundtrips. |
| Relay/game crypto matrix | Partial | Extend `EncryptionTests` | Add explicit `ENC_BFISH`, `ENC_TFISH`, `ENC_BTFISH` relay vectors. |
| `0xBF` extended command routing | Covered | `PacketManagerTests`, `ExtendedCommandDispatchTests` | Parse/route covered by `PacketManagerTests`; dispatch integration drives the registered `0xBF` router into `GameClient.HandleExtendedCommand` (screen size `0x05`, language `0x0B`, stat lock `0x1A` effects) and asserts the registry gate drops unknown sub-commands. |
| Loopback login integration | Covered | `ClientLoginIntegrationTests` | Raw seed + `0x80`/`0xA0`/`0x91`/`0x5D` bytes drive the production `NetworkManager` receive pipeline (no-crypt detect, framing, dispatch) across login and game connections: login -> `0xA8` -> relay `0x8C`+authId -> game login -> `0xB9`/`0xA9` -> char select -> enter world `0x1B`. In-process via `InjectReceived` + `ProcessInput` for determinism (no socket). |
| Packet opcode matrix | Open (deferred: needs fixture corpus) | Add `PacketEraCompatibilityTests` cases | Classify required Sphere 56x opcodes, optional modern opcodes, ignored hooks. |

---

# Part 2 — Itemised behaviour-surface detail

Measurement backbone: the largest score gains come from locking the Source-X
**behaviour surface** — `SERV.*` verbs, object verbs/properties, and triggers —
rather than adding isolated features. This part turns the otherwise-subjective
category scores into an itemised status list with the code location for each
entry, so later phases target concrete gaps instead of guesses.

## Status legend

| Status | Meaning |
|---|---|
| **Implemented** | Reachable from scripts with Source-X-equivalent behaviour. |
| **Partial** | Works on one surface (e.g. admin console) but not all (e.g. not from `SERV.*`), or behaviour is narrowed. |
| **Stub** | Recognised but returns a placeholder / no-op. |
| **Missing** | Not handled anywhere. |
| **NotApplicable** | Source-X feature with no meaning in SphereNet's design. |

Each wave should keep this table in sync and ideally back it with a guardrail test
(see `ParityMatrixServTests` for the SERV list/var primitives,
`SourceXVerbInventoryGuardrailTests` for the Source-X function/verb inventory,
and `TriggerCoverageGuardrailTests` for the trigger surface).

## SERV.* world-ops verbs

Dispatch: script reads route through `Program.ResolveServerProperty`
(`src/SphereNet.Server/Program.Scripting.cs`); the admin console routes through
`AdminCommandProcessor` (`src/SphereNet.Server/Admin/AdminCommandProcessor.cs`).

| Verb | Status | Where | Notes |
|---|---|---|---|
| `RESPAWN` | Implemented | `HandleServRespawn` → `RespawnAllSpawners` | Wave 197 — was admin-console only. |
| `RESTOCK` | Implemented | `HandleServRestock` | Wave 197 — was admin-console only. |
| `CLEARLISTS` | Implemented | `HandleServClearLists` → `GameWorld.ClearGlobalLists` | Wave 197 — new primitive; mirrors `CLEARVARS`. Optional `[prefix]`. |
| `VARLIST` | Implemented | `HandleServVarList` / `HandleServVarListToCaller` | Optional `[prefix]`. As a command, dumps `VAR.*` to the invoking client's console (log fallback); as a `<...>` read, returns the count. Wave 207 added caller-console routing. |
| `PRINTLISTS` | Implemented | `HandleServPrintLists` / `HandleServPrintListsToCaller` | As a command, dumps list names + sizes to the caller's console (Wave 207); read form returns the count. |
| `CLEARVARS` | Implemented | `_CLEARVARS=` → `GameWorld.ClearGlobalVars` | Pre-existing. |
| `ACCOUNT.<name>[.prop]` | Implemented | `ResolveServAccount` | Name lookup + sub-property read. |
| `ACCOUNT.<n>[.prop]` | Implemented | `ResolveServAccount` → `AccountManager.GetByIndex` | Wave 200 — indexed access (stable name order); was stubbed to "0". |
| `INFORMATION` | Implemented | `HandleServInformationToCaller` (`_INFORMATION=` protocol) | W-E — status lines to the invoking client's console (log fallback). |
| `GARBAGE` | Implemented | `HandleServGarbage` (`_GARBAGE=`) | W-E — GC pass from scripts; the FixWeirdness world-integrity sweep is still open (hedef 12.4). |
| `B` / `BROADCAST` | Implemented | `HandleServBroadcast` (`_BROADCAST=`) | W-E — was console-only; `serv.b` from scripts was a silent no-op. |
| `SHRINKMEM` | Implemented | `HandleServShrinkMem` (`_SHRINKMEM=`) | W-H3 — dedicated Source-X bridge mapped to compacting managed GC. |
| `HEARALL` | Implemented | `HandleServHearAll` (`_HEARALL=`) | W-I1 — exposes/toggles the `LOGM_PLAYER_SPEAK` bit (`0x002000`); `<SERV.HEARALL>` reads current state. |
| `CHATFLAGS` | Implemented | `SphereConfig.ChatFlags` / `ResolveServerProperty` | W-I1 — loaded from `sphere.ini` and exposed through `SERV.CHATFLAGS`. |
| `GENERICSOUNDS` | Implemented | `SphereConfig.GenericSounds` / `ResolveServerProperty` | W-I1 — loaded from `sphere.ini` and exposed through `SERV.GENERICSOUNDS`; deeper generic sound table parity remains a sound-system task. |
| `BLOCKIP` / `UNBLOCKIP` | Implemented | `HandleServBlockIp` (`_BLOCKIP=`/`_UNBLOCKIP=`) + `AdminCommandProcessor` | Verb long-tail wave — script surface gated at PLEVEL_Admin via the srcUid protocol (Source-X SV_BLOCKIP); decay seconds accepted but logged (no timed-block model). |
| `CALCCRYPT` | Implemented | `CalcCryptLine` / `HandleServCalcCryptToCaller` | Verb long-tail wave — CCryptoKeyCalc::CalculateLoginKeys bit-mix ported; `<SERV.CALCCRYPT ver>` returns the SphereCrypt.ini-style line, the command form prints to the caller. |
| `EXPORT` / `IMPORT` / `RESTORE` | Partial | `HandleServExport` / `HandleServImport` / `HandleServLoad` / `HandleServRestore` | Wave 213-217 — text `.scp` object/world export and non-destructive runtime import are wired under `WorldSaveDir`; `RESTORE` pre-replaces colliding world serials with rollback snapshot hardening; `EXPORT`/`IMPORT file, flags, distance` support Source-X-style object-centered scope filtering. |
| `SAVESTATICS` | Partial | `HandleServSaveStatics` → `WorldSaver.ExportStatics` | Wave 213/215 — exports `ATTR_STATIC` world items to text `.scp`; no-arg calls write `spherestatics.scp`, and saver-level scoped statics are available. Native static-map file generation remains a later slice. |
| `LOAD` | Implemented | `HandleServLoad` → `WorldLoader.LoadFile` | Wave 213 — loads one `.scp`/save-format file at runtime with two-pass item/char parsing and container/equipment relinking. |
| `SECURE` | Implemented | `HandleServSecure` (`_SECUREMODE=`) | W-H3 — toggles secure mode and blocks script/console `SHUTDOWN` while enabled. |

### Already-implemented SERV.* surface

Confirmed live in `ResolveServerProperty`: read-only stats (`CLIENTS`, `ACCOUNTS`,
`CHARS`, `ITEMS`, `VERSION`, `SERVNAME`), time (`TIME`, `TIMEUP`, `RTIME`, `RTICKS`,
`TICKPERIOD`), `SAVECOUNT`, `MEM`, `REGEN0-3`, `SEASON`/`SEASONMODE`, feature flags;
lookups `MAP*`, `SKILL.n`, `CHARDEF.`, `ITEMDEF.`, `AREA.`, `MULTIDEF.`, `LIST.`,
`DEFLIST.`, `GMPAGE.`, `ISEVENT.`, `ACCOUNT.n`, `LOOKUPSKILL`; var/obj access
`VAR.`/`VAR0.`, `OBJ`, `NEW`, `UID.`, `DEFMSG.`; write verbs `_SET_*`, `NEWDUPE`,
`ALLCLIENTS`, `WRITEFILE`, `LOG`, `RESYNC`, `SAVE`, `SHUTDOWN`.

## Triggers

The trigger surface is measured and regression-guarded by
`src/SphereNet.Tests/TriggerCoverageGuardrailTests.cs`, which recomputes the
"defined but not fired" set from source on every run. The ITEM-trigger backlog is
now EMPTY (the champion port wired Level/Complete and the four candle triggers);
outstanding char entry: `UserVirtue` (virtue-gump select; `NPCSeeWantItem` is now fired).

Per-trigger **arg contract** (`SRC`, `ARGO`, `ACT`, `ARGN1/2/3`, `ARGS`, `LOCAL`,
`RETURN 0/1`, arg mutation):

| Aspect | Status | Notes |
|---|---|---|
| `ARGN1`/`ARGN2` seed + read | Implemented | via `WrapArgs`. |
| `ARGN3` seed + read | Implemented | Wave 202 — `WrapArgs` never seeded `Number3`, so `<ARGN3>` read 0 (e.g. `@DropOn_*` drop-Z). |
| `ARGN1/2/3` mutation (`ARGN3=x`) | Implemented | Wave 202 — the interpreter had no ARGN assignment path, so scripts could not modify trigger numbers; `RunWrapped` copies the mutation back. |
| ARGN write-back from every trigger source | Implemented | Wave 203 — item `ITEMDEF`/`TEVENTS`/`TYPEDEF`, region/room and global events bypassed `RunWrapped`, so their ARGN mutations were dropped; all firing paths now go through it. |
| `ARGS` seed + read | Implemented | via `WrapArgs` (constructor). |
| `ARGS` mutation (`ARGS=text`) | Implemented | Wave 204 — the string arg was read-only (no assignment path, no copy-back); now writable and copied back. |
| `@Speech` text rewrite (end-to-end) | Implemented | Wave 206 — the ARGS-rewrite mechanism is now wired through: a script's `ARGS=` in `@Speech` reaches the NPC-hear routing and the spatial broadcast, so the rewritten words are what others hear. |
| `ARGO`, `SRC`, `LOCAL`, `REFn` | Implemented | pre-existing; `LINK` decoupled from `ACT` in Wave 199. |
| `RETURN 1` short-circuit / order | Implemented | firing order EVENTS → TEVENTS → base def → global → `f_onchar_*`, any `RETURN 1` blocks. |
| `RETURN <value>` string vs number | Implemented | Wave 205 — RETURN evaluated its arg as a long and stored the number, so a [FUNCTION] returning a name/defname/message collapsed to a digit; `ExpressionParser.TryEvaluate` now reports whether the arg was genuinely numeric, and RETURN keeps a string value. |
| Trigger names `@Jailed` / `@Ship_Move,_Stop,_Turn` / `@itemSPELL` | Implemented | W-A — SphereNet emitted `@Jail`/`@ShipMove`/`@itemSpellEffect`, so Source-X-named blocks never matched. |
| `@Hit` family SRC/ARGO contract | Implemented | W-B — SRC = the victim, ARGO = the weapon on `@Hit/@HitTry/@HitCheck/@HitMiss` (was attacker/target); `@Hit` seeds ARGN2 = damage type, `@HitCheck` ARGN1 = swing state + ARGN2, `@GetHit` ARGN2. |
| `@GetHit` armor-damage LOCAL contract | Implemented | C1 — `LOCAL.ItemDamageLayer` (random Source-X armor layer, writable), `LOCAL.ItemDamageChance`, elemental `LOCAL.DamagePercent*`; the item `@GetHit` fires on the script-final layer piece (was shield-only) and the durability wear follows the same roll in BOTH armor modes. Elemental split: full resists may zero a hit; unset physical is the remainder. |
| `@HitCheck/@HitMiss/@Hit` LOCAL contract | Implemented | C2 — `LOCAL.Recoil_NoRange` (per-swing SWING_NORANGE override, fired before range validation), `LOCAL.Arrow` = live ammo stack UID + `ArrowHandled`/RETURN 1 ammo takeover, `LOCAL.ItemDamageChance` (weapon wear) + `ItemPoisonReductionChance/Amount` (poison charge spend). Open: `@Hit` LOCAL.Arrow (hit-path ammo takeover). |
| CombatFlags full behavior wiring | Implemented | C3 — DCLICKSELF_UNMOUNTS, ALLOWHITFROMSHIP, NOPETDESERT, ATTACK_NOAGGREIVED wired; PREHIT\|SWING_NORANGE normalized at config load. Every enum member now has a behavior site, locked by `CombatFlagGuardrailTests`. |
| Slayer system (COMBAT_SLAYER) | Implemented | C4 — `SlayerFaction` is a verbatim CFactionDef port; char `FACTION_GROUP/SPECIES`, item `SLAYER_GROUP/SPECIES` (tag/def-tag backed); weapon-first, talisman-fallback damage scaling. Open: the magic-damage/spellbook path. |
| AOS on-hit properties | Implemented | C5 — leeches/mana drain (Source-X formulas), `HITAREA*` splashes, `HITDISPEL/FIREBALL/HARM/LIGHTNING/MAGICARROW` procs; 14-name tag-backed surface. Deferred (unimplemented in Source-X too): HitLowerAtk/Def, HitCurse, HitFatigue. |
| NPC_AI_* flag behaviors | Implemented | N1 — FOOD feeding pass, COMBAT ally-heal gate, EXTRA (NPC_ExtraAI: @NPCAction, war equip, night light), MOVEOBSTACLES; wand ATTR_MAGIC gate; `@NPCActCast` ARGN2=wand-use; `@NPCLookAtItem` full contract (dist/want/RETURN 0). VEND_TIME deferred (define-only upstream). Bonus: chardef CAN leading-zero-hex parse fix. |
| Housing/ship/movement/script-pack audit | Implemented | M1/H1/H1b/S1/P3 — negative-coordinate sector/pathfinder crash guards, ship redeed cargo crate + placement overlap + boundary guard + REDEED dry-dock, house placement footprint loop (NoBuild margin, ship overlap, I_Block items, fixed no-op char check), lockdown footprint gate, unknown-def-key diagnostics, VISIBLE/SFX verbs. Deferred with reasons in wiki/housing-movement-scriptpack.txt. |
| Death flow (CChar::Death) | Implemented | D1-D3 — player exp/fame/deaths penalties (DEATH_NOFAMECHANGE), Kill() state cleanup, carve crime + birth-gates, victim memory clear, kill record + party echo, corpse hair/beard copies, antimagic rez gate, summon vanish burst, trade cancel at death, `HitpointPercentOnRez` + `PacketDeathAnimation` configs. Deferred with reasons in wiki/death-corpse.txt (corpse max-weight, manifest model, client niceties). |
| `@Death` SRC / `@Kill` ARGN1 / item `@Step` ARGN1 | Implemented | W-A — SRC = the dying char (killer on ARGO), ARGN1 = victim's attacker count, ARGN1 = fStanding. |
| `@SpellEffect` on the affected char + LOCAL contract | Implemented | W-C — fires on the target with SRC = caster, ARGN2 = skill level, LOCAL.Effect/Resist/Duration seeded & read back; per-spell `[SPELL] @EFFECT` runs per-target. Was caster-only with N1 alone. |
| `@SpellCast` ARGN2/ARGN3 | Implemented | W-C — difficulty + writable cast wait (tenths). Open: WOP locals. |
| `[SKILL n]` section stages (`Skill_OnTrigger`) | Implemented | W-E — `FireSkillTrigger` runs the resource-section stage alongside every char `@Skill*` trigger; those ON= blocks never executed before. |
| `[SPELL]` section stages beyond Effect/Success/Fail | Implemented | W-H1/W-H3 covered `@Start`, `@Select`, `@TargetCancel`, `@EffectAdd`, `@EffectRemove`, and `@EffectTick`. |
| Firing tests: `TriggerArgParityTests`, `ParityWaveA-ETests` | — | ARGN seed + mutation round-trip; per-wave contract fixtures. |

## Persistence

Save-load round-trip of trigger-relevant runtime state. Guarded by `SaveFormatTests`.

| State | Status | Notes |
|---|---|---|
| Global `VAR.*` / `LIST` | Implemented | Saved (`[GLOBALS]` / `[LIST name]`) and loaded. |
| Item decay / `TIMERMS` | Implemented | Saved as remaining time. |
| Object `TIMERF` / `TIMERFMS` timers | Implemented | Wave 208 — pending delayed function/verb timers were never saved (lost on restart); now persisted per object as remaining time and re-scheduled on load. |
| Active poison (level, remaining ticks, poisoner) | Implemented | Wave 209 — was lost on restart; now saved as remaining time + tick count and restored exactly. |
| Active spell effects (buffs/debuffs) | Implemented | Wave 211 — temporary spell effects are saved as remaining-time `SPELLEFFECT` records, restored into `SpellEngine` on load, re-applied to live stats/flags/visuals, and expire normally instead of becoming permanent after restart. |
| Spawn timers / vendor restock time | Implemented | Wave 212 — spawn component schedules now preserve loaded `TIMERMS` remaining time across component init, item spawners mirror their next tick to object timeout, and vendor `RESTOCK_TIME` uses restart-safe Unix milliseconds. |
| Tags, memories, equipment | Implemented | Per-object save/load. |
| Combat memories + attacker log | Implemented | W-D — ALL memory types now persist with remaining timeout + creation stamp (was only IPet/Guard/Guild/Town/Friend); the attacker log (damage totals, ignore flags) is saved as `ATTACKER=` records. |
| Sector environment (light/season/weather/rain/cold) | Implemented | W-D — `[SECTORS] ENV=` records in spheredata for non-default sectors (Source-X `CSector::r_Write`); previously reset every restart. |

## Object verbs / properties

Char/item verb + property parity is exercised across `ScriptObjectParityTests`,
`GameSystemTests`, and the per-area parity suites. The pinned Source-X verb
inventory (`SourceXVerbInventoryGuardrailTests`) now has an **empty deferred
backlog**: the verb long-tail wave implemented the remaining CObjBase entries
(BASEPROPLIST/BASETAGLIST/CLILOCLIST/DIALOGCLOSE/EDIT/EFFECTLOCATION/GOAWAKE/
GOSLEEP/PROPLIST/REMOVECLILOC/REPLACECLILOC/SAYUA), the CChar entries
(AFK/GOCHARID/GOCLI/GOSOCK/GOTYPE/HEAR/NEWBIESKILL/TARGETCLOSE/UNDERWEAR) and
the CClient entries (BADSPAWN/CHARLIST/CLOSEPROFILE/CLOSESTATUS/CODEXOFWISDOM/
DYE/EVERBTARG/EXTRACT/GOTARG/LAST/LINK/MAPWAYPOINT/NUDGE/NUKE/NUKECHAR/REPAIR/
SCROLL/SHOWSKILLS/SKILLUPDATE/SUMMON/TILE/UNEXTRACT). New packets: 0xBF sub
0x16 close-UI, 0xBF sub 0x17 Codex of Wisdom, 0xA6 scroll, 0x95 dye window,
0x3A single-skill. Known simplifications: NUKE/NUKECHAR/NUDGE keep SphereNet's
single-pick + range area model (Source-X two-corner) while TILE/EXTRACT use a
real two-corner flow; EXTRACT covers dynamic items (statics export is a
MapData feature); SKILLUPDATE reports cap 1000.

| Verb | Status | Where | Notes |
|---|---|---|---|
| `TIMERF` | Implemented | `ObjBase.ScheduleTimerF` (delay in seconds) | Runs a delayed function; falls back to running the payload as a verb (Wave 198). |
| `TIMERFMS` | Implemented | `ObjBase.ScheduleTimerF` (delay in ms) | Wave 198 — was missing; millisecond counterpart of `TIMERF`. |
| item `CONSUME` / `BOUNCE` / `DECAY` | Implemented | `Item.TryExecuteCommand` | W-E — stack consume, bounce-to-owner-pack (unequips first), decay-timer arm. |
| `DISTANCE.<uid \| x,y>` (property) | Implemented | `ObjBase.TryGetProperty` + `GetTopLevelPosition` | W-E — OC_DISTANCE; contained items measure from the wearer/container. Bare `<DISTANCE>` (to SRC) still unresolved. |
| `HITPOINTS` / `USESCUR` / `USESMAX` aliases | Implemented | char + item get/set | W-E — Source-X exposes them as first-class keys; `<src.hitpoints>` read empty before. |
| Client-scoped verbs via `UID.<player>.DIALOG` etc. | Implemented | `HandleRefExec` fallback (`Program.Scripting.cs`) | Pre-existing — routes DIALOG/SDIALOG/MENU/INPDLG to the target player's client; the audit's "missing object-verb DIALOG" was largely covered by this path. |
| `Use_Trap` state machine | Implemented | `Item.UseTrap` / `SetTrapState` | W-A — dclick/step springs the trap (MORE2 damage, MORE1 graphic swap, MOREX/MOREY/MOREZ timing); dclick previously opened RemoveTrap instead. |
| Equipped-cursed move gate | Implemented | `ItemMoveRules.CanMove` | W-A — ATTR_CURSED/CURSED2 while equipped refuses to move (`cantmove_cursed`). |

### TRY-family control verbs (`ScriptInterpreter`)

| Verb | Status | Notes |
|---|---|---|
| `TRY` | Implemented | Run a verb / property-set on the target without halting on failure. |
| `TRYP <plevel>` | Implemented | Gate the verb on the source's PrivLevel; aborts with a message below it. Tested (Wave 201). |
| `TRYSRC <src>` | Implemented | Run the verb with a different source object (via `_REF_EXEC`). |
| `TRYSRV` | Implemented | Run at SERVER privilege (Owner / PLEVEL 7), bypassing the source's PLEVEL. Wave 201 — previously passed the original (low-plevel) source, so privileged verbs failed. |

### VarObjs reference chain (`ScriptInterpreter.ResolveVarForTarget`)

| Ref | Resolves to | Status | Notes |
|---|---|---|---|
| `SRC` | trigger source | Implemented | via source console / `IScriptObj`. |
| `ARGO` / `ARGO.x` | `Object1` | Implemented | the trigger argument object. |
| `ACT` / `ACT.x` | `Object2` | Implemented | the acted-on object. |
| `LINK` / `LINK.x` | target's own `LINK` property | Implemented | Wave 199 — was aliased to `Object2` (collided with `ACT`); now reads the object's `m_uidLink`. |
| `REFn` / `REFn.x` | scope-local ref slots | Implemented | local, per-scope. |
| `OBJ`, `NEW` | global object / last-created | Implemented | via server resolver. |

## Champion spawns (Source-X CCChampion / CCChampionDef)

`[CHAMPION x]` sections load as retained `ResType.Champion` defs (NAME,
LEVELMAX, SPAWNSMAX, CHAMPIONID, NPCGROUP[n]). An item of `t_spawn_champion`
(ItemType.SpawnChampion = 300, matching IT_SPAWN_CHAMPION) attaches
`ChampionComponent` alongside its SpawnComponent; MORE1/MORE1_DEFNAME links
the def. The full state machine is ported: START/STOP/INIT/ADDSPAWN/
DELRED-WHITECANDLE/ADDOBJ/DELOBJ verbs, the ICHMPL_* read/write keys, kill →
white candle (quota = red/5) → 4 whites = 1 red (whites consumed) → reds per
level from the 16-candle list → boss (CHAMPIONID) at LEVELMAX → @Complete.
The 10-minute decay tick removes a red candle and refunds kill progress;
no reds left stops the spawn. Triggers fired: @Start (post-arm, aborts the
initial burst), @Stop, @Level (ARGN1..3), @Complete, @AddRed/WhiteCandle
(ARGO = candle, RETURN 1 deletes) and @DelRed/WhiteCandle (ARGN1 = reason,
RETURN 1 keeps). Kill credit rides the SPAWNITEM back-link in
DeathEngine.ProcessDeath (Source-X credits at object destroy). State persists
through CHAMPION_* item tags (level/counters/candle uid lists) — no new save
records. Verbatim quirks kept: the Start burst loop races the quota
(ceil(quota/2) initial spawns). Guarded by `ChampionSystemTests`.

## FILE object (Source-X CSFileObj on g_Serv._hFile)

Backing store: `ScriptFileHandle` — ONE shared server-global slot (clients and
the server resolver use the same instance, mirroring `g_Serv._hFile`), gated by
`OF_FileCommands` and sandboxed under `<scpdir>/files` (a deliberate hardening
over Source-X's unrestricted paths). Full surface: OPEN/CLOSE/FLUSH/DELETEFILE,
WRITE/WRITELINE/WRITECHR, MODE.APPEND/CREATE/READFLAG/WRITEFLAG/SETDEFAULT,
INUSE/ISEOF/FILEPATH/POSITION/LENGTH(-1 closed)/READCHAR(numeric)/READBYTE n/
READLINE n (position-restoring)/SEEK/FILEEXIST/FILELINES. Reachable from client
consoles AND from no-console script contexts through `ResolveServerFileObject`
(the interpreter routes `FILE.*` verbs there as a fallback). Source-X defaults
ported: append+read+write mode, OPEN refused while open, MODE changes refused
while open. `CSFileObjContainer` (multi-file pool) is intentionally absent —
it is not wired to the `FILE.` ref in Source-X either. Guarded by
`ScriptFileObjectParityTests`.

## Open threads

- Native static-map (`SAVESTATICS`) file generation.
- Persistent save/load of `DEFMSG` runtime overrides.
- Relay/game crypto matrix vectors (`ENC_BFISH`/`ENC_TFISH`/`ENC_BTFISH`).
- Packet opcode matrix fixture corpus (era classification).
- Open verification/action items live in `INCELEME_DOGRULAMA_PLANI_TR.md`
  (the single work tracker).

### Deferred tail (absorbed from the closed PARITY_BACKLOG_SUB90, 2026-07-18)

The below-90 backlog closed through Wave 270 (70/76 items); the 6 surviving
items were all explicitly deferred with reasons and are parked here:

- **Spell schools** — 2026-07-20 update: **Chivalry is native** (Cleanse by
  Fire, Close Wounds, Dispel Evil, Divine Fury, Holy Light, Noble Sacrifice,
  Remove Curse, Sacred Journey; Consecrate Weapon / Enemy of One stay script
  territory — typed-damage coupling) and **Spellweaving Gift of Renewal** is a
  native HoT. Remaining Bushido/Ninjitsu/Mysticism/Spellweaving uniques are
  combat/stealth/form-coupled and stay deferred — the Source-X reference
  implements none of them natively either (its native surface is buff-icon
  layer bookkeeping; verified 2026-07); pack script defs run through the
  generic flag engine. The `[SPELL n] ON=@Select` stage now fires from
  CastStart (2026-07-20), so @Select-only pack forms (Reaper/Stone Form)
  execute their script bodies — and their `FINDID.<RUNE_ITEM>.REMOVE`
  toggle works end-to-end: FINDID searches the hidden memory layer and
  deleting an IT_SPELL memory reverts its effect
  (SpellEngine.RemoveEffectByMemory, the Source-X Spell_Effect_Remove
  contract). Zero-duration forms hold with no expiry.
- **Custom Sphere spells 1000+** — 2026-07-20 update: native behaviors for
  Light, Hallucination (with periodic trip sounds), Stone, Particle Form,
  Shrink (kills conjured, figurines the rest), Refresh, Restore, Mana,
  Sustenance, Gender Swap, Trance, Shield, Steelskin, Stoneskin, Regenerate,
  Ale/Wine/Liquor (per-tick stam/mana drain), Bone Armor (top-level skeleton
  corpse only, pieces get ITEMDEF @Create metadata), and the poly family
  Chameleon / Beast Form / Monster Form riding the shared polymorph route
  (plus the earlier Summon Undead / Animate Dead / Fire Bolt). Reaper Form /
  Stone Form apply their form bodies on first cast (Reaper 0xE6 by
  maintainer decision — the reference assigns CREID_STONE_FORM to both, an
  apparent copy-paste slip). Still deferred — no case in the reference:
  **Enchant, Forget**; the inert gate refuses them unless the pack scripts
  them.
- **SERV no-op tail** — `STAT` / `TIMERF` section form / `SERVERS`: acceptable
  no-ops for a single-server shard. LOW.
- **Pet economy sub-commands** — pet-sells-loot buy/sell/sample are
  message-only. LOW.
- **`IsCorpseSleeping`** — SphereNet has no sleeping mechanic; Source-X callers
  are Forensics/steal, out of scope.
- **Niche display packets** — `PacketBondedStatus` (0xBF.0x19),
  `PacketStatueAnimation`, `PacketToggleHotbar`, `PacketSignGump`,
  `PacketGameTime`, `PacketGlobalChat` (out), `PacketQueryClient`,
  `PacketDisplayPopup`, `PacketCharacterListUpdate` (0x86): add only when a
  real consumer appears — an unwired stub adds no value.
- **`CCryptoKeyCalc` auto-detect** — client-version key-table auto-detection
  (keys currently supplied externally). Related: bcrypt accounts need an
  external dependency and Source-X core itself is plain+MD5 only — already
  equivalent-or-better.

### Sound/visual gaps (absorbed from the closed SOUND_VISUAL_MOVEMENT_PARITY_TR, 2026-07-18)

- `GenericSounds` — config key + `SERV.GENERICSOUNDS` exist (W-I1) but the
  addSound emission gate is not wired; sounds still play when disabled.
- Drop sound is still the flat `0x0042` (gold amount differentiates); Source-X
  `GetDropSound(pObjOn)` is item/tiledata-based.
- Sector ambient sounds (wind etc., Source-X `CSector` tick) are absent.
- `0x23` drag animation: pickup/ground-drop covered; the container-drop path is
  not modeled to full Source-X depth.
- `PacketBoatSmoothMove` does not yet carry the full Source-X `PacketMoveShip`
  component list.
