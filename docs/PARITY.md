# SphereNet Parity Matrix

This document tracks Source-X/Sphere compatibility work that is easy to lose in
large backlog notes. It supersedes stale status lines in `tools/plan.txt`.

## Completed Or Covered

- Save/load: `EQUIP[n]`, nested containers, and bank box roundtrips have
  regression tests.
- Account creation: `AccApp` drives `AutoCreateAccounts`; production can disable
  automatic account creation.
- Admin telnet: starts only with a non-empty `AdminPassword`.
- CI: GitHub Actions restores, builds, and runs the main regression suite.
- Client/script triggers: mount/dismount, secure trade lifecycle, vendor
  `@Buy`/`@Sell`, context menu, AOS tooltips, target flows, rename/profile/dye,
  single-click `@AfterClick`, corpse carving, and User* packet hooks.
- Network safety: crypto primitive tests and no-crypt login/game-login harness
  exist; outbound Huffman compress on game connections. Client→server packets
  remain plaintext after decrypt (standard UO protocol).
- Combat core: shared `CombatHelper` gates (safe region, archery range,
  shield+bow, movement delay), player `@Attack`/`@HitTry`/`@HitCheck`, reveal
  on attack, NPC miss swing feedback, and per-observer notoriety on NPC swings.
- Trade safety: disconnect abort returns items; complete validates carry weight.
- NPC AI script hooks: `@NPCLookAtChar`, `@NPCActFight`, `@NPCActWander`,
  `@NPCActFollow`, `@NPCActCast`, `@NPCLookAtItem` wired from `NpcAI`.
- Runtime state: STATLOCK, cast/skill pending, rune MoreP use native fields.
- Modern opcodes: `0xF0` movement handler; `0xF6` smooth boat broadcast on move.
- Viewport: `0xBF sub 0x0005` / `0x001C` update `Character.SetScreenSize`.
- Housing limits: `MaxHousesPlayer` and `MaxHousesAccount` enforced in
  `HousingEngine.PlaceHouse`.
- Script globals: generic `f_onitem_*` dispatch mirrors `f_onchar_*`.

## Partial

- Housing: account/player limits are enforced; more script-facing `HOUSE.n`
  behaviors still need focused coverage.
- Vendor/trade: core packet paths and triggers exist; gold stack edge cases
  need tests.
- `0xBF` extended commands: several subcommands are handled in
  `GameClient.WorldFeatures`, while `PacketManager.RegisterExtended` is not the
  primary dispatch path yet.
- Program bootstrap: `TickYieldStrategy` was extracted, but `Program.cs` remains
  the main wiring monolith.
- Combat: `COMBATFLAGS` and era values load from ini and feed `CombatEngine`,
  but full Source-X swing-state machine (`SWING_READY/SWINGING`) and arrow
  projectile effects are still open.

## Open Script Parity

- Expression gaps: true floating-point expression behavior.
- Duplicate spell definition model: runtime uses `SphereNet.Game.Magic.SpellDef`;
  `SphereNet.Scripting.Definitions.SpellDef` should be removed or reduced to a
  loader-side mapper.

## Open Gameplay Parity

- Item-use stubs for tinker/poison target flows.
- Active skill STROKE animation loop.

## Open Network And Client Parity

- Selected `0xDF` buff interactions and remaining opcode/client matrix work.
- Loopback login integration test beyond current no-crypt unit slices.

## Open Ops And Panel Work

- `docs/DEPLOY.md` with host/headless/panel deployment steps.
- Panel script write/hot-reload and player actions such as kick/goto/whisper.
- Panel token storage and TLS/reverse proxy guidance.
- IPC and web status threat-model decisions beyond localhost trust.
