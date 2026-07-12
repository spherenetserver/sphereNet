# CLAUDE.md

Guidance for AI coding agents (Claude Code and similar) working in this repository.
For the product overview, feature list, and deployment docs, read [README.md](README.md)
and [docs/](docs/README.md) — this file is about *how to work in the codebase*.

## What this is

SphereNet is a clean-room **Ultima Online server emulator** on **.NET 10**, built for
runtime and script compatibility with [Source-X](https://github.com/Sphereserver/Source-X).
It runs existing Sphere/Source-X `.scp` scripts and legacy save data, with a rebuilt
multicore engine underneath.

**Two hard requirements shape almost every change:**
1. **Source-X compatibility** — behavior should match the Source-X reference. A local
   copy of the reference lives under `oldSphere/Source-X-full/src` (C++); consult it
   before implementing or changing gameplay behavior.
2. **Legacy data compatibility** — existing mortechUO/Sphere save and script files must
   keep loading. Never break the save/load format or the `.scp` parser.

## Build, run, test

Primary shell is **PowerShell** on Windows (the repo also builds on Linux/macOS). A Bash
tool is available for POSIX scripts.

```powershell
# Full build (Debug: fast, split DLLs / Release: single-file publish)
./build.ps1 -Configuration Debug -SkipPanel

# Build one project directly
dotnet build src/SphereNet.Server/SphereNet.Server.csproj -c Debug

# Run the whole test suite (do this before declaring a change done)
dotnet test src/SphereNet.Tests/SphereNet.Tests.csproj

# Run a subset
dotnet test src/SphereNet.Tests/SphereNet.Tests.csproj --filter "FullyQualifiedName~CombatEngine"
```

- Solution files: `SphereNet.slnx` (modern) / `sphereNet.sln`. SDK pinned in `global.json`.
- The server reads its config from `config/sphere.ini`. `SCPFILES=` points at the script
  pack, `WorldSave=` at the save directory (defaults to `save/`).

## Project layout (`src/`)

| Project | Responsibility |
|---|---|
| `SphereNet.Core` | Enums, config (`SphereConfig`), primitive types (`Serial`, `Point3D`) |
| `SphereNet.Network` | Packet framing, `NetState`, encryption (Blowfish/Twofish), Huffman |
| `SphereNet.Scripting` | `.scp` parsing, expression engine, `ResourceHolder`, definitions |
| `SphereNet.Game` | Gameplay: `Character`, `Item`, combat, magic, skills, NPC AI, clients |
| `SphereNet.Persistence` | `WorldSaver` / `WorldLoader`, save formats, legacy import |
| `SphereNet.MapData` | `.mul` map/statics/multi readers |
| `SphereNet.Server` | `Program.*` startup, main loop, admin console, engine wiring |
| `SphereNet.Panel` / `SphereNet.Host` | SignalR web dashboard / host process |
| `SphereNet.Tests` | xUnit suite (build + run before every commit) |

## Architecture conventions

- **Definitions load before the world.** `DefinitionLoader.LoadAll()` builds the
  char/item/spell tables; it MUST run before the world save is loaded. Legacy saves
  store an NPC's body only in the `[WORLDCHAR <defname>]` section header, resolved
  through `DefinitionLoader.GetCharDef` — if defs aren't loaded yet, bodies resolve to
  0 and NPCs are invisible. See `Program.cs` startup order.
- **Defnames are data, not constants.** Body/graphic IDs, item types, and NPC stats come
  from the loaded `.scp` scripts (`[CHARDEF <id>] DEFNAME=<name>`), never hardcoded. A
  save only stores the defname; the running script pack must define it.
- **Persistence safety is paramount.** When a value is derived (e.g. an effective stat =
  base + equipped item bonus), persist the **base**, never the derived total — otherwise
  the bonus compounds across save cycles. Prefer **live-scan-on-read** aggregation over
  equip-time mutation (SphereNet has no equip/unequip recompute hook).
- **Source-X trigger model.** `EVENTS`/`TEVENTS` chains, `@Trigger` blocks, `ARGN`/`ARGS`
  read-back, `RETURN 1` to cancel. `TriggerDispatcher` gates hot-path triggers via
  `IsTrigUsed`.
- **Legacy save compatibility** lives in `WorldLoader`, which accepts both native
  SphereNet headers (`[WORLDITEM]`) and classic Sphere/Source-X headers with a defname
  suffix (`[WORLDITEM i_backpack]` / `[WORLDCHAR c_man]`) in the same load path.

## Testing conventions

- Tests that touch shared engine statics use the `[Collection("DefinitionLoaderSerial")]`
  serialization and `ResetEngineStatics` (a `BeforeAfterTest` hook). When you add a new
  static engine hook, add it to `ResetEngineStatics.Reset()`.
- Real-data compatibility tests (e.g. `Sphere56TSaveCompatTests`) skip cleanly when the
  referenced save/script directory is absent, so they're CI-safe.
- Deterministic combat: set `CombatEngine.WeaponDefLookup`, elevate `PrivLevel` for
  guaranteed hits, and zero out parry as needed — see existing combat tests.
- Keep the full suite green. State test results honestly, including failures.

## Working style for agents

- **Verify against Source-X** (`oldSphere/Source-X-full/src`) before changing gameplay;
  match its behavior rather than inventing new semantics.
- **Don't run `git commit`/`push` unless explicitly asked** — the maintainer manages git.
- **Commit messages: English only, technical/engine-level terms**, no marketing and no
  script/dialog names as motivation. Do not add AI co-author attribution lines.
- **Changelogs are split**: `CHANGELOG-EN.txt` (English) and `CHANGELOG-TR.txt` (Turkish,
  ASCII-only). Add both when you land a user-facing change.
- Prefer the smallest change that fixes the root cause; reproduce/diagnose before editing.

## Reference material (in-repo)

- `oldSphere/Source-X-full/` — Source-X C++ engine (behavior reference)
- `oldSphere/scripts/`, `oldSphere/Scripts-X-main/` — script packs
- `oldSphere/ClassicUO-main/` — client source (packet format / client behavior)
- `docs/` — architecture, staff commands, triggers, deploy, runbook
- `wiki/` — parity notes and roadmaps
