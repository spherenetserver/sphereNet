# SphereNet Source-X / Sphere 56x Parity Matrix

This matrix tracks compatibility work by the same categories used for project
scoring. Every non-covered item names either a regression test to add/extend or
an explicit deferred reason so the backlog does not turn into folklore.

## Scripting Parity

| Area | Status | Test / Guardrail | Notes |
|---|---|---|---|
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
| Active skill delay/stroke | Partial | `SkillDelayTests`, add interrupt cases | `@SkillStroke` loop exists; add damage/movement/cancel ordering tests. |
| Item-use target flows | Covered | `ItemUseParityTests` | Weapon DClick -> poisoning, repair/tinkering and trap DClick -> remove-trap paths are covered. |
| Vendor/trade safety | Partial | `TradeSafetyTests`, `HousingEconomyTests` | Disconnect/weight rollback, nested gold stacks and sell validation covered; buy/sell packet roundtrip still open. |
| Housing economy | Partial | `HousingEconomyTests` | Placement/account limits, access-list script surface and decay covered; richer house gump/transfer packet roundtrips remain open. |

## Network / Crypto / Client Parity

| Area | Status | Test / Guardrail | Notes |
|---|---|---|---|
| Sphere 56x default client profile | Covered | `PacketEraCompatibilityTests` | Unknown clients default to old packet formats via `ClientEra=Sphere56x`. |
| Modern feature gates | Covered | `PacketEraCompatibilityTests`, AOS tooltip tests | Modern `0xDF` buff and AOS tooltip behavior require client/version support. |
| Crypto primitives | Covered | `EncryptionTests` | Login, Blowfish, Twofish, no-crypt login/game-login, Huffman roundtrips. |
| Relay/game crypto matrix | Partial | Extend `EncryptionTests` | Add explicit `ENC_BFISH`, `ENC_TFISH`, `ENC_BTFISH` relay vectors. |
| `0xBF` extended command routing | Partial | `PacketManagerTests`, add dispatch integration | `GameClient.HandleExtendedCommand` is primary; `RegisterExtended` remains a registry utility. |
| Loopback login integration | Open (deferred: needs socket harness) | Add `ClientLoginIntegrationTests` | Cover login -> relay auth -> game login -> char select -> enter world. |
| Packet opcode matrix | Open (deferred: needs fixture corpus) | Add `PacketEraCompatibilityTests` cases | Classify required Sphere 56x opcodes, optional modern opcodes, ignored hooks. |
