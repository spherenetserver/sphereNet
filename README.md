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
| **Persistence** | 4 save formats (`Text`→`BinaryGz`, ~8–10% size), parallel sharded saves, background save mode, MySQL/PostgreSQL multi-DB |
| **Multicore engine** | Parallel tick pipeline with serial apply, sector sleeping (idle world ≈ free), field-level delta views, memory-mapped maps |
| **Operations** | SignalR web dashboard, Telnet admin console, in-process bot stress tests (`STRESS`/`BOT`), SQLite record/replay |

## Beyond Source-X

Capabilities the classic engine does not have:

| | |
|---|---|
| **4 save formats + live switching** | `Text` (100%) / `TextGz` (~15%) / `Binary` (~50%) / `BinaryGz` (~8–10%); `.SAVEFORMAT BinaryGz 4` migrates format + shard count at runtime; `SAVESHARDS=2–16` writes parallel hash shards; `SAVEBACKGROUND=1` moves the write off the main loop |
| **Multi-database** | Several named `[MYSQL <name>]` / `[PGSQL <name>]` / `[SQLITE <name>]` connections at once; scripts switch with `db.select <name>` |
| **Multicore tick pipeline** | NPC decision prestage and client view-delta build run on worker threads; sector ticks, script callbacks and Apply stay serial & deterministic; auto-fallback to single-thread on error or (cooperative) phase timeout |
| **Sector sleeping** | Only sectors near online players tick — a 30k-NPC idle world costs 0.1 ms. What sleeps is character work (AI, regen, poison); item deadlines are exact wherever the item lies, from world-level due queues ([contract](docs/ARCHITECTURE.md#sectors-and-sector-sleeping)) |
| **Delta views** | Field-level change tracking (`DirtyFlag`) sends only what changed, not full object resends |
| **Memory-mapped maps** | The OS pages MUL files on demand (~200 MB saved vs full RAM load) |
| **NPC timer wheel** | 256-slot wheel schedules NPC actions O(1) instead of scanning every NPC per tick |
| **Web panel (SignalR)** | Live log stream, CPU/RAM metrics, player list and server commands in the browser (port 9999) |
| **Bot stress testing** | In-process TCP bots complete the real login flow and play: `.bot spawn 100` / `.botmenu`, telnet `BOT`/`STRESS` |
| **Record & replay** | SQLite-backed movement/state recording for GM investigations and debugging |

## Performance

### Current measurement (2026-10-12)

Measured with `SphereNet.LoadTest` (in-tree, reproducible): real TCP bot clients in a
separate process log in through the normal flow, walk (a step every ~450 ms), talk and
double-click in a synthetic town of **2,000 wandering NPCs and 4,000 ground items**, all
bots **clustered** so every one of them sees the others; one world save runs in the middle
of each 45 s window. Machine: AMD Ryzen 7 5700X3D (8C/16T), Windows 11, an ordinary busy
desktop; 4 multicore workers. *Step answer* is the time from a bot's walk request to the
server's answer, measured on the bot.

| Bots | Tick | Tick p50 / p95 | Step answer p50 / p95 | Alloc |
|---|---|---|---|---|
| 50 | 100 ms (default) | 6.1 / 12.1 ms | 1.4 / 5.0 ms | 6.8 MB/s |
| 200 | 100 ms (default) | 10.7 / 29.5 ms | 3.8 / 15.2 ms | 21 MB/s |
| 50 | 50 ms | 2.8 / 10.0 ms | 1.5 / 5.5 ms | 6.8 MB/s |
| 200 | 50 ms | 4.8 / 17.3 ms | 3.7 / 14.1 ms | 21 MB/s |

- **Receive model** (`AsyncReceive`): completion-driven receive (default) against the polled
  path (`AsyncReceive=0`, one `Socket.Select` per pass) on the same scenario — step answer
  p50 **1.4 vs 4.3 ms** at 50 bots and **3.8 vs 6.8 ms** at 200 bots; tick time is the same.
- **Tick length**: `ServerTickMs=50` fits easily (200 bots: p95 17 ms of a 50 ms budget) and
  costs about the same CPU per second, since each tick does half the work. It tightens
  timers checked on the tick (swings, casts) from 100 to 50 ms granularity; NPC actions are
  scheduled on a 100 ms timer wheel and player steps are handled every loop iteration, so
  neither changes. The default stays 100 ms (Source-X `MSECS_PER_TICK`).
- **Limit**: 500 bots on **one screen** overload this machine in every configuration (tick
  p95 ~90 ms, step answer p95 ~0.6 s): every step goes to 499 viewers. Spread over a map
  the same count is far cheaper — what costs is how many players see each other.
- **Real world**: a copy of a live shard (86,055 items, 26,767 characters, 10,048 spawners,
  Scripts-X pack) with 200 bots spread over the cities ran with no slow tick and no stall
  after the login burst; the script interpreter was under 0.1 % of server time in a 90 s
  profile. 200 simultaneous logins caused one ~1.3 s pause.
- **Soak** (2026-10-14): the same live-shard copy with 100 in-process bots of the `Soak`
  scenario (walking, fighting, casting, looting, vendor buying, trading) for 3 hours: tick
  p50 0.23-0.35 ms, p99 2.2-4.1 ms, worst tick 40 ms; 12 world saves (0.45-0.72 s pause
  each, synchronous), 13 world audits with no anomaly; working set flat at ~1 GB after a
  40-minute warm-up (-7.7 MB/h over the run); 0.13 cores on average.

The dominant cost is simultaneously-active AI, not population or client count. A sleeping
sector costs nothing to tick, and an awake one costs what is *due* rather than what it
holds: item deadlines live in due-ordered queues instead of being found by walking every
item every tick. On a separate rig, 500 players with 50,000 NPCs and 300,000 ground items
measure 1.5 ms p50 clustered in towns and 11.9 ms p50 scattered across the whole map —
conditions, method and the timer contract are in
[ARCHITECTURE](docs/ARCHITECTURE.md#sectors-and-sector-sleeping).

## Quick start

```bash
git clone https://github.com/Yunusolcay/sphereNet.git
cd sphereNet
dotnet build
dotnet run --project src/SphereNet.Server   # headless (or SphereNet.Host for the web panel)
```

Edit `config/sphere.ini`: point `MULFILES` at your UO client data, put scripts under `SCPFILES`. Key settings: `SAVEFORMAT` (`Text`…`BinaryGz`), `SAVESHARDS` (0–16), `SAVEBACKGROUND`, `[MYSQL <name>]` / `[PGSQL <name>]` blocks. Every key is described in [docs/CONFIG-EN.md](docs/CONFIG-EN.md) ([Turkish](docs/CONFIG-TR.md)).

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
└── SphereNet.Tests/         # automated test suite (kept green)
```

## Roadmap

Bushido/Ninjitsu/Mysticism unique mechanics · richer NPC AI (pack tactics) · expanded web panel (live map, inspector) · incremental saves.

## Contributing

Issues and PRs welcome — keep changes focused, covered by tests, and run `dotnet build && dotnet test` before submitting.

**Credits:** [Source-X](https://github.com/Sphereserver/Source-X) (behavior reference) · [ServUO](https://github.com/ServUO/ServUO) · [Ultima Online](https://uo.com/) — Origin/EA.
**License:** open source — see [LICENSE](LICENSE).
