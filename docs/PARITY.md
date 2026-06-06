# SphereNet Source-X / Sphere 56x Parity Matrix

This matrix tracks compatibility work by the same categories used for project
scoring. Every non-covered item names either a regression test to add/extend or
an explicit deferred reason so the backlog does not turn into folklore.

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
| `SERV.*`, `UID.*` | Covered | `ScriptObjectParityTests` | Server property reads, direct UID reads, UID command dispatch and `SERV.ALLCLIENTS` bridge are covered. |
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
