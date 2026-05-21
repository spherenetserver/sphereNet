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
  exist; inbound Huffman behavior is intentionally unchanged until broader
  packet fixtures are available.

## Partial

- Housing: `HOUSES` and owned-house lookup are present, but `MAXHOUSES*` limits
  and more script-facing `HOUSE.n` behaviors still need focused coverage.
- Vendor/trade: core packet paths and triggers exist, but disconnect, cancel,
  invalid quantity, and stack/gold edge cases need tests.
- `0xBF` extended commands: several subcommands are handled in
  `GameClient.WorldFeatures`, while `PacketManager.RegisterExtended` is not the
  primary dispatch path yet.
- Program bootstrap: `TickYieldStrategy` was extracted, but `Program.cs` remains
  the main wiring monolith.

## Open Script Parity

- `NpcAI` does not yet fire `@NPCAct*` or `@NPCLook*` triggers from AI decisions.
- `[TYPEDEF]` resources are parsed but not registered as executable script
  trigger handlers.
- Generic `f_onchar_*` dispatch is missing; only selected hooks such as speech
  and region enter/leave are wired.
- Expression gaps: `STRREPLACE`, `STRJOIN`, and true floating-point expression
  behavior.
- Duplicate spell definition model: runtime uses `SphereNet.Game.Magic.SpellDef`;
  `SphereNet.Scripting.Definitions.SpellDef` should be removed or reduced to a
  loader-side mapper.

## Open Gameplay Parity

- Item-use stubs for tinker/poison target flows.
- Active skill STROKE animation loop.
- Polymorph body restore on resurrect.
- Housing limits and script token parity.

## Open Network And Client Parity

- Inbound Huffman receive path after packet/login fixture coverage is in place.
- Modern client opcodes: `0xF0` movement, `0xF6` boat, and selected `0xDF` buff
  interactions.
- Opcode/client matrix for ClassicUO and the target official client era.
- Loopback login integration test beyond current no-crypt unit slices.

## Open Ops And Panel Work

- `docs/DEPLOY.md` with host/headless/panel deployment steps.
- Panel script write/hot-reload and player actions such as kick/goto/whisper.
- Panel token storage and TLS/reverse proxy guidance.
- IPC and web status threat-model decisions beyond localhost trust.
