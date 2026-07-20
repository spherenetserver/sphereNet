# SphereNet

> A modern **Ultima Online server emulator** on **.NET 10**, script-compatible with [Source-X](https://github.com/Sphereserver/Source-X) — with a rebuilt multicore engine underneath.

🇹🇷 [README-TR.md](README-TR.md) · 📚 [docs/](docs/README.md) (architecture, [staff commands](docs/STAFF_COMMANDS.md), [triggers](docs/TRIGGERS.md), deploy, runbook) · 📜 [Changelog](CHANGELOG-EN.txt)

SphereNet runs existing Sphere/Source-X `.scp` scripts and legacy save data unchanged, and rebuilds the engine for multicore hardware, large worlds and live operations. Cross-platform (Windows/Linux/macOS), headless.

## Highlights

| Area | What you get |
|---|---|
| **Scripting** | Full Source-X `.scp` parser: expressions, triggers, `FOR`/`WHILE`/`DORAND`/`DOSWITCH`, defname resolution |
| **Combat & Magic** | Era-selectable combat formulas, 60+ Magery/Necromancy spells + native Chivalry, real field spells, fizzle/interrupt, ranged & throwing weapons |
| **Skills & Crafting** | 30+ skills with gain curves, recipes/quality/material hues, mining/fishing/lumberjacking, spinning wheel & loom, taming |
| **NPC AI** | Monster/pet/healer/guard/vendor brains, A\* pathfinding, aggro & home-leash |
| **World** | Housing (client-rendered multis, sign gump, lockdown/secure), ships (speech commands, dry-dock), guilds & towns, parties, weather, plant growth |
| **Networking** | Full UO login flow, T2A→TOL packets, Blowfish/Twofish/Huffman |
| **Persistence** | 4 save formats (`Text`→`BinaryGz`, ~8–10% size), parallel sharded saves, background save mode, MySQL multi-DB |
| **Multicore engine** | Parallel tick pipeline with serial apply, sector sleeping (idle world ≈ free), field-level delta views, memory-mapped maps |
| **Operations** | SignalR web dashboard, Telnet admin console, in-process bot stress tests (`STRESS`/`BOT`), SQLite record/replay |

## Beyond Source-X

Capabilities the classic engine does not have:

| | |
|---|---|
| **4 save formats + live switching** | `Text` (100%) / `TextGz` (~15%) / `Binary` (~50%) / `BinaryGz` (~8–10%); `.SAVEFORMAT BinaryGz 4` migrates format + shard count at runtime; `SAVESHARDS=2–16` writes parallel hash shards; `SAVEBACKGROUND=1` moves the write off the main loop |
| **Multi-database MySQL** | Several named `[MYSQL <name>]` connections at once; scripts switch with `db.select <name>` |
| **Multicore tick pipeline** | Snapshot/Build phases run parallel, Apply stays serial & deterministic; auto-fallback to single-thread on error |
| **Sector sleeping** | Only sectors near online players tick — a 30k-NPC idle world costs 0.1 ms; timers stay wall-clock accurate |
| **Delta views** | Field-level change tracking (`DirtyFlag`) sends only what changed, not full object resends |
| **Memory-mapped maps** | The OS pages MUL files on demand (~200 MB saved vs full RAM load) |
| **NPC timer wheel** | 256-slot wheel schedules NPC actions O(1) instead of scanning every NPC per tick |
| **Web panel (SignalR)** | Live log stream, CPU/RAM metrics, player list and server commands in the browser (port 9999) |
| **Bot stress testing** | In-process TCP bots complete the real login flow and play: `.bot spawn 100` / `.botmenu`, telnet `BOT`/`STRESS` |
| **Record & replay** | SQLite-backed movement/state recording for GM investigations and debugging |

## Performance

Measured **2026-07-20 on the current build** with the in-tree harness (real TCP bot clients, full production script pack) on a modest **5-vCPU VM, 12 GB RAM**. Tick = 100 ms (10/s, Source-X parity); bots run in-process, so numbers are pessimistic.

| Scenario | Avg tick | p95 | Budget |
|---|---|---|---|
| 30,000 NPCs, no players | 0.1 ms | <2 ms | <1 % |
| 30,000 NPCs + 300 players walking | ~8 ms | ~13 ms | 8 % |
| 2,000 hostiles + 300 players fighting | ~3.5 ms | ~7 ms | 4 % |
| 1,000 live clients roaming | ~2.5 ms | ~5 ms | 3 % |
| 1,000 clients + 2,000 hostiles | ~15 ms | ~30 ms | 15 % |
| 100k items + 50k NPCs + 300 players, all active | ~31 ms | ~58 ms | 31 % |

- Logins: 300 clients in 7.4 s, 1,000 in 18.1 s — zero failures. Cold boot < 2 s.
- Save: 102,400 items + 50,440 chars → **1.08 s** while the world kept running (BinaryGz, 3 shards, parallel capture).
- GC: blocking Gen2 ≈ 0–1 per 30 s window in every scenario; RSS ~450–700 MB.

The dominant cost is simultaneously-active AI, not population or client count — sleeping sectors are free.

## Quick start

```bash
git clone https://github.com/Yunusolcay/sphereNet.git
cd sphereNet
dotnet build
dotnet run --project src/SphereNet.Server   # headless (or SphereNet.Host for the web panel)
```

Edit `config/sphere.ini`: point `MULFILES` at your UO client data, put scripts under `SCPFILES`. Key settings: `SAVEFORMAT` (`Text`…`BinaryGz`), `SAVESHARDS` (0–16), `SAVEBACKGROUND`, `[MYSQL <name>]` blocks.

Ports: **2593** UO client · **2594** Telnet admin · **2595** HTTP status · **9999** web panel (`ADMINPANELPORT`).

## Project structure

```
src/
├── SphereNet.Core/          # Types, enums, config
├── SphereNet.Network/       # UO protocol, TCP, encryption
├── SphereNet.Scripting/     # .scp parser, expressions, execution
├── SphereNet.Game/          # Gameplay (AI, combat, magic, skills, world…)
├── SphereNet.MapData/       # MUL/UOP readers
├── SphereNet.Persistence/   # Save/load, legacy import
├── SphereNet.Panel/ + Host/ # Web dashboard / launcher
├── SphereNet.Server/        # Server entry point
└── SphereNet.Tests/         # ~1,900 automated tests (kept green)
```

## Roadmap

Bushido/Ninjitsu/Mysticism unique mechanics · richer NPC AI (pack tactics) · expanded web panel (live map, inspector) · incremental saves.

## Contributing

Issues and PRs welcome — keep changes focused, covered by tests, and run `dotnet build && dotnet test` before submitting.

**Credits:** [Source-X](https://github.com/Sphereserver/Source-X) (behavior reference) · [ServUO](https://github.com/ServUO/ServUO) · [Ultima Online](https://uo.com/) — Origin/EA.
**License:** open source — see [LICENSE](LICENSE).
