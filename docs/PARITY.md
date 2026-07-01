# SphereNet Source-X / Sphere 56x Parity Matrix

This matrix tracks compatibility work by the same categories used for project
scoring. Every non-covered item names either a regression test to add/extend or
an explicit deferred reason so the backlog does not turn into folklore.

Current verification snapshot (2026-06-30): `dotnet test .\src\SphereNet.Tests\SphereNet.Tests.csproj --nologo` passes 1011/1011 tests. The current overall Source-X parity estimate is about 7.8/10; the 9.x execution plan is tracked in [PARITY_ROADMAP_TR.md](PARITY_ROADMAP_TR.md).

Important terminology note: `SERV.*`, arbitrary verb fallback, and trigger return/argument propagation are not missing as core systems. They are implemented for the common runtime paths. Remaining gaps are mostly the long-tail Source-X server/admin verbs and a few exact context/VarObj edge cases.

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
| `SERV.*`, `UID.*` common runtime paths | Covered | `ScriptObjectParityTests`, `GameSystemTests` | Server property reads, direct UID reads/dispatch, `SERV.ALLCLIENTS`, `SERV.WRITEFILE`, `SERV.LOG`, `SERV.GMPAGE`, `SERV.NEWITEM`, `SERV.NEWDUPE`, `SERV.SEASON`, `SAVE`/`RESYNC`/`SHUTDOWN` bridge and account/char/item/map/skill lookups are covered. |
| Arbitrary verb / function fallback | Covered | `GameSystemTests`, `ScriptObjectParityTests` | Unknown script commands fall through to named `[FUNCTION]` calls; the same pattern exists for `SRC.` commands after property/verb/script-command dispatch. |
| Trigger return/argument propagation | Covered | `TriggerCoverageGuardrailTests`, `GameSystemTests`, focused parity tests | `RETURN`, `ARGS`, `ARGV`, `ARGN1/2/3`, `ARGO`, `ACT`, shared `LOCAL.*`, and ARGN copy-back through chained trigger blocks are covered. |
| Source-X server/admin verb long tail | Partial | Extend `ScriptObjectParityTests` and admin command integration tests | `CServer::r_Verb` contains additional maintenance verbs such as `VARLIST`, `PRINTLISTS`, `CLEARLISTS`, `EXPORT`, `IMPORT`, `RESTORE`, `SAVESTATICS`, `SECURE`, `SHRINKMEM`, `INFORMATION`, `LOAD`, `GARBAGE`, full `BLOCKIP`/`UNBLOCKIP`, indexed `SERV.ACCOUNT.n`, and richer `RESPAWN`/`RESTOCK` script entry points. |
| `DEFMSG` runtime override | Partial | Add `ScriptObjectParityTests` coverage | `DEFMSG.*` reads exist, but `DEFMSG name=value` still needs persistent runtime override semantics. |
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
