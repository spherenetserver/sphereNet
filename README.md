# SphereNet

> A modern, high-performance **Ultima Online private server emulator** written in **.NET 10 (LTS)**, designed for script compatibility with [Source-X](https://github.com/Sphereserver/Source-X) while going far beyond it in performance, persistence, and operability.

🇹🇷 **Türkçe okumak için → [README-TR.md](README-TR.md)**
📚 **Documentation → [docs/](docs/README.md)** (architecture, [staff commands](docs/STAFF_COMMANDS.md), [triggers](docs/TRIGGERS.md), deploy, runbook)
📜 **Changelog → [CHANGELOG-EN.txt](CHANGELOG-EN.txt) · [CHANGELOG-TR.txt](CHANGELOG-TR.txt)**

---

## What is SphereNet?

SphereNet is a clean-room reimplementation of a classic Sphere-style UO server core on top of modern .NET. It keeps what server admins already know — the `.scp` scripting language, the trigger model, the data formats — and rebuilds the engine underneath for multicore hardware, large worlds, and live operations.

If you already have Sphere/Source-X scripts and save data, SphereNet aims to run them with minimal changes, then give you the headroom to grow: more players, bigger maps, faster saves, and a live web dashboard to watch it all.

### Design goals

- **Script compatibility first** — the `.scp` parser, expression engine, triggers, and object model target Source-X behavior so existing content runs.
- **Scale on modern hardware** — a parallel tick pipeline, sector sleeping, and field-level change tracking turn idle CPU into headroom.
- **Operable in production** — multiple save formats, multi-database support, a SignalR web panel, a Telnet console, and a SQLite record/replay engine for investigations.
- **Cross-platform** — runs headless on Windows, Linux, and macOS.
- **Testable** — a growing automated test suite guards parser, combat, persistence, and protocol behavior.

---

## Feature overview

| Area | Highlights |
|---|---|
| **Scripting** | Source-X `.scp` parser, full expression engine (math, string, object queries), `FOR`/`WHILE`/`DORAND`/`DOSWITCH` flow, brace-ranges `{n m}`, `@`/`MAX`/`MIN`/`QVAL` |
| **Triggers** | `@Login`, `@Death`, `@Hit`, `@GetHit`, `@Click`, `@DClick`, `@Step`, `@Equip`, `@ReceiveItem`, `@Criminal`, `@SeeCrime`, `@SpellInterrupt`, `@Hunger`, and many more |
| **Combat** | Era-selectable hit/damage formulas (0/1/2), elemental damage & resist, weapon/shield parry, swing timers, reactive armor, poison |
| **Magic** | 60+ spells, fizzle/interrupt, reagent & mana costs, fields, summons, travel (Recall/Gate/Mark), buff/debuff expiry |
| **Skills** | 30+ skill handlers with gain curves, crafting (recipes, exceptional quality, material hue), gathering (mining/fishing/lumberjacking), taming & pet loyalty |
| **NPC AI** | Monster / Pet / Healer / Guard / Vendor / Animal brains, A\* pathfinding, home-leash, aggro management |
| **World** | Housing (client-rendered multis, sign gump, decay, co-owner/friend, lockdown/secure), ships (speech commands, smooth movement, planks/hold, tillerman dry-dock), parties, guilds (war/alliance), weather & day/night, plant growth, regions |
| **Justice** | Criminal flag, murder count, karma/fame, timed jail, notoriety |
| **Networking** | Full UO login flow, T2A → TOL expansion packets, Blowfish/Twofish/Huffman encryption |
| **Persistence** | 4 save formats, shard-based parallel save, MySQL multi-database |
| **Operations** | SignalR web dashboard, Telnet admin console, bot stress testing, SQLite record/replay |

---

## Beyond Source-X

These are capabilities SphereNet adds on top of the classic engine.

### 1. Multiple save formats with runtime switching

Source-X only saves plain-text `.scp`. SphereNet supports four formats and can migrate between them live.

| Format | Extension | Relative size | Notes |
|---|---|---|---|
| `Text` | `.scp` | 100% | Source-X compatible, human-readable |
| `TextGz` | `.scp.gz` | ~15% | Same text, GZip-wrapped |
| `Binary` | `.sbin` | ~50% | Tag-stream binary |
| `BinaryGz` | `.sbin.gz` | ~8–10% | Smallest and fastest |

**Sharding:** `SAVESHARDS=0` writes a single file, `1` enables size-based rolling, and `2–16` splits saves into parallel hash shards (`UID % N`) for concurrent I/O. The runtime command `.SAVEFORMAT BinaryGz 4` switches format and shard count in one step, performing a one-shot migration.

### 2. Multi-database support

Source-X supports a single MySQL connection. SphereNet connects to several databases at once — each with its own host, threading mode, and timeouts.

```ini
[MYSQL default]
Host=localhost
User=root
Password=secret
Database=sphere
AutoConnect=1

[MYSQL logging]
Host=10.0.0.2
User=logger
Password=logpass
Database=logs
UseThread=1
```

Scripts switch the active connection with `db.select <name>`:

```
db.select logging
db.execute "INSERT INTO logs (msg) VALUES ('event')"
db.select default
db.query "SELECT * FROM users WHERE id=1"
```

### 3. Multicore tick pipeline

Source-X runs single-threaded. SphereNet splits each tick into four phases, runs the parallelizable ones across cores, and **auto-falls-back to single-threaded** on any error.

| Phase | Type | Work |
|---|---|---|
| Snapshot | Parallel | Sector tick, NPC snapshot |
| Build | Parallel | NPC decision computation (read-only) |
| Apply | Serial | Decisions applied in UID order (deterministic) |
| Flush | Serial | Decay, light, telnet, web |

**Region cache:** `FindRegion` is called thousands of times per tick (guard zones, PvP, music, weather). Source-X scans the full region list every call (O(n)); SphereNet uses an 8×8-tile grid `ConcurrentDictionary` cache that avoids rescans and auto-invalidates when regions change.

### 4. Sector sleeping

Source-X iterates every sector each tick. SphereNet only ticks sectors that contain online players (a 5×5 sector window around each player), so NPCs and items in empty regions cost zero CPU. When 300 players cluster in one city, the rest of the map sleeps.

**Timer integrity:** all timers (item decay, spawn intervals, `TIMER` triggers) use absolute timestamps (`Environment.TickCount64`), not tick counters. Sleeping sectors still receive a lightweight maintenance pass every 3 minutes that processes item timers only — so spawners keep producing, expired items are removed, and `TIMER` triggers fire on schedule, with no timer drift or loss.

### 5. Delta view (field-level change tracking)

Source-X resends visible objects in full each tick. SphereNet tracks per-field changes via `DirtyFlag` bitmasks (Position, Body, Hue, Stats, Equip, …) and sends only what changed. View computation runs in the parallel phase; packet I/O runs in the serial phase, keeping it safe for multicore ticks.

### 6. Memory-mapped maps

Source-X loads map files entirely into RAM. SphereNet uses `MemoryMappedFile`, letting the OS manage page residency and saving roughly 200 MB.

### 7. NPC timer wheel

Instead of scanning every NPC every tick, SphereNet uses a 256-slot hashed timer wheel; NPCs are bucketed by their `nextActionTime` for O(1) scheduling.

### 8. Web panel (SignalR live dashboard)

A real-time admin dashboard built on ASP.NET Core + SignalR: live log streaming, CPU/RAM/thread metrics, player list, and server-control commands — all from the browser. Includes token-based auth and response compression. Runs via the `SphereNet.Host` launcher or standalone.

### 9. Bot stress-test system

A built-in bot framework simulates real client connections over TCP. Bots follow the full UO login flow (login server → relay → game server) and perform in-game actions — walking, combat, looting, and skill use — so you can load-test before real players arrive.

```
.bot spawn 100          # spawn 100 bots
.bot spawn britain 50   # spawn 50 bots in Britain
.bot status             # show status
.bot stop               # stop all bots
```

### 10. State recording & replay

Source-X has no way to replay past events. SphereNet includes a SQLite-backed record/replay engine that captures character movement and state snapshots at intervals, enabling retrospective playback for GM investigations, cheat detection, and debugging.

---

## Performance

All figures below were measured on **2026-07-20 against the current build** — the full feature set, not an early prototype — using the in-tree harness: `STRESS` generates the population, `BOT` spawns real TCP clients that complete the entire UO login flow and play. The world runs the full production script pack (32,842 itemdefs, 821 chardefs, all spawns/regions) and real MUL maps.

**Test environment (deliberately modest):** a 5-vCPU virtual machine (AMD Ryzen 9 9950X host), 12 GB RAM, Windows Server 2019, .NET 10 Release, adaptive (DATAS) GC. Bots run **in-process**, so their client-side CPU is charged against the server — every number below is pessimistic. The tick interval is **100 ms (10 ticks/s, matching Source-X `MSECS_PER_TICK`)**; *budget* is the share of that 100 ms an average tick consumes. Each sample is a steady-state 30-second window (300 ticks) after spawn/login settled.

### Measured scenarios

| Scenario | Avg | p50 | p95 | p99 | Budget | RSS | Verdict |
|---|---|---|---|---|---|---|---|
| 30,000 NPCs, no players online | 0.1 ms | 0.1 ms | <2 ms | <5 ms | <1 % | ~440 MB | ✅ free (sector sleeping) |
| 30,000 NPCs + 300 players walking every town | ~8 ms | ~6 ms | ~13 ms | ~29 ms | 8 % | ~580 MB | ✅ comfortable |
| 2,000 hostile monsters + 300 players fighting | ~3.5 ms | ~2.4 ms | ~7 ms | ~16 ms | 4 % | ~470 MB | ✅ comfortable |
| 1,000 live clients roaming | ~2.5 ms | ~1.9 ms | ~5 ms | ~14 ms | 3 % | ~580 MB | ✅ comfortable |
| 1,000 clients + 2,000 hostiles engaging | ~15 ms | ~10 ms | ~30 ms | ~39 ms | 15 % | ~690 MB | ✅ headroom left |
| 100,000 items + 50,000 NPCs + 300 players — everything piled into ACTIVE sectors | ~31 ms | ~24 ms | ~58 ms | ~90 ms | 31 % | ~700 MB | ⚠ heavy but stable |

Reading the table:

- **Sector sleeping works as designed.** 30,000 NPCs with nobody online cost 0.1 ms/tick — a large idle world is effectively free, and only the sectors players actually visit are ever paid for.
- **The dominant cost is simultaneously-active AI**, not the client count: 1,000 roaming clients cost ~2.5 ms, while adding 2,000 aggroed monsters multiplies that by six. Population that players cannot see costs nothing.
- **The last row is a deliberate worst case** — the stress generator drops all 100k items and 50k NPCs into the same town sectors the players occupy, so nothing can sleep. Even then the loop holds its 10 Hz tick with ~3× headroom on average.
- **GC never became the story**: blocking Gen2 collections stayed at 0–1 per 30 s window in every scenario (pause time 1–5 %), thanks to pooled A* scratch, allocation-free walkability and `ArrayPool`-rented packet buffers.

### Logins, boot, save

| Operation | Result |
|---|---|
| 300 client logins (full UO login flow) | 7.4 s, 0 failures |
| 1,000 client logins | 18.1 s, 0 failures, all stayed connected |
| Cold boot to accepting connections (defs + empty world) | < 2 s (definitions load in ~0.2 s) |
| World save, 102,400 items + 50,440 chars (BinaryGz, 3 shards, synchronous) | **1.08 s** — measured while 300 clients and 50k NPCs kept playing |
| World save, ~32,000 objects | 0.75 s |

The save snapshot capture is parallelized across cores; with `SAVEBACKGROUND=1` the shard writing moves to a low-priority background thread and the main loop only pays for the capture.

### Dense single-screen crowd — the broadcast wall

A crowd packed into **one screen** all broadcasting is the worst case: speech fans out to every bot in view, so outgoing packets scale ~N². From a dedicated broadcast-flood run — 1,000 bots clustered around one town centre, all speaking:

| Metric | Per-recipient build (old) | Shared broadcast (current) |
|---|---|---|
| Send-queue overflows | ~919 | **0** |
| Fleet under the flood | 1,000 → ~31 | **all 1,000 held at full tick rate** |
| Avg tick (settled) | collapse | **~3 ms** |
| Blocking Gen2 | present | **~0** |
| Crash / wire corruption | none | none |

Originally this saturated the per-client send queues — a broadcast packet was rebuilt and re-Huffman-compressed once per recipient, identical bytes recomputed N times — and the server shed load via overflow disconnects (1,000 → ~31). Broadcasting one **shared** packet (build + compress once, reuse across all recipients) removed that bottleneck: the same scenario now logs **zero** send-queue overflows and holds all 1,000 broadcasting clients at the full tick rate. Movement never hits this wall the same way: a dense crowd can't keep moving (mobiles block each other, so steps are rejected and the move-broadcast storm self-throttles).

As a graceful-degradation layer, **interest management** sheds low-priority cosmetic broadcasts (overhead speech, sound) to any connection that is backing up — either its packet queue or its unsent-byte backlog passes a soft cap — while state-bearing packets (movement, status, combat) are never dropped. It is inert in normal play (a 300-player combat run sheds nothing) and activates only under a genuine per-connection backlog.

**Non-blocking sends** keep a slow client from ever stalling the server. The game stream uses a per-connection persistent send buffer drained with non-blocking sends — when the OS send buffer is full the bytes wait for the next flush instead of blocking the flush thread. So a slow or distant client costs only its own buffer, never a shared server thread.

Together these mean the worst case no longer collapses: with chatter shed off backed-up connections, the 1,000-bot one-screen flood **holds all 1,000 clients at the full tick rate (~3 ms avg)** with zero overflow and zero forced disconnects — the server feeds each client only what it can drain (movement and state), throttling the cosmetic flood per-connection instead of cutting clients off.

### Allocation & GC

Blocking Gen2 collections are the main source of tick-time jitter, so the hot
paths avoid per-tick allocation. The A* pathfinder pools its scratch
collections per worker thread (`[ThreadStatic]`, cleared per call) instead of
allocating a PriorityQueue, HashSet and two Dictionaries on every `FindPath`,
and outgoing packet buffers are rented from `ArrayPool<byte>` and returned once
the socket send has consumed them. A `[gc_stats]` log line (allocation rate,
Gen0/1/2 deltas, GC pause %, heap) is emitted next to `[tick_stats]` every 30 s
so allocation regressions are visible in any run.

---

## Quick start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Ultima Online client data files (MUL/UOP)

### Build & run

```bash
git clone https://github.com/Yunusolcay/sphereNet.git
cd sphereNet
dotnet build
```

1. Edit `config/sphere.ini`.
2. Point `MULFILES` at your UO client data files.
3. Put your scripts under `scripts/`.

```bash
dotnet run --project src/SphereNet.Server   # headless console
dotnet run --project src/SphereNet.Host     # web panel + server manager
```

### Default ports

| Port | Purpose |
|---|---|
| 2593 | UO client |
| 2594 | Telnet admin console |
| 2595 | HTTP status endpoint |
| 2596 | Web panel (Host mode) |

---

## Configuration

Core settings live in `config/sphere.ini`. A few of the most relevant keys:

| Key | Purpose |
|---|---|
| `MULFILES` | Path to UO client data (maps, statics, tiledata) |
| `SCPFILES` | Script directory root |
| `SAVEFORMAT` | `Text` / `TextGz` / `Binary` / `BinaryGz` |
| `SAVESHARDS` | `0` single file, `1` rolling, `2–16` parallel hash shards |
| `[MYSQL <name>]` | Named database connections (multi-DB) |

The Telnet console (port 2594) and the web panel both expose runtime administration; many `sphere.ini` values can also be changed live with dot-commands.

---

## Administration

SphereNet provides GM/admin commands through speech, the Telnet console, and the web panel. A small sampling:

```
.SAVE                       # save the world now
.SAVEFORMAT BinaryGz 4      # switch save format + shard count (live migration)
.bot spawn 100              # spawn stress-test bots
.JAIL <serial> <minutes>    # jail a player for a duration (auto-release)
.GO <x>,<y>,<z>             # teleport
.ADD <id|defname>           # create an item/NPC
```

Timed jail sentences auto-release on schedule and survive reboots (stored as wall-clock release times).

---

## Scripting

SphereNet runs Sphere-style `.scp` content. The engine supports:

- **Expressions:** integer & float math, bitwise ops, the `@` power operator, `MAX`/`MIN`, `ABS`, `SQRT`, trig, `RAND`, brace-ranges `{n m}` and weighted `{a w b w}` selection.
- **Strings:** `STRSUB`, `STRLEN`, `STRMATCH`, `STRREGEX`, `STRARG`, `STREAT`, and more.
- **Object queries:** `ISNEARTYPE`, `FINDID`, `FINDLAYER`, `DISTANCE`, `TOPOBJ`, container/char iteration (`FORCHARS`, `FORITEMS`, `FORCONT`, …).
- **Flow control:** `IF`/`ELIF`/`ELSE`, `WHILE`, `FOR` (multiple argument forms), `DORAND`/`DOSWITCH`, `BEGIN`/`END`.
- **Object model:** read/write properties and run verbs on characters and items (`STR`, `HITS`, `TAG.*`, `MORE1`, `CONT`, `DUPE`, `ATTACK`, `CURE`, …).

Definitions (`CHARDEF`, `ITEMDEF`, `[SPELL ...]`, regions, spawns, templates) are loaded from your script directory and resolved by defname.

---

## Project structure

```
src/
├── SphereNet.Core/          # Core types, enums, configuration
├── SphereNet.Network/       # UO protocol, TCP, encryption (Blowfish/Twofish/Huffman)
├── SphereNet.Scripting/     # Script parser, expression engine, execution
├── SphereNet.Game/          # Game logic (AI, Combat, Magic, Skills, Death, World, ...)
├── SphereNet.MapData/       # MUL/UOP map & tiledata readers
├── SphereNet.Persistence/   # Save/load, importers
├── SphereNet.Panel/         # SignalR web panel (ASP.NET Core)
├── SphereNet.Host/          # Launcher / server manager
├── SphereNet.Server/        # Headless server entry point
└── SphereNet.Tests/         # Automated test suite
```

---

## Testing

```bash
dotnet test
```

The suite (~1,900 tests) covers the expression/script engine, combat formulas, persistence/save formats, packet/era compatibility, movement, housing/ships, targeting, and runtime safety — and is kept green on every commit.

---

## Roadmap & ideas

Areas under active consideration (contributions welcome):

- Broader spell-school support (Chivalry / Bushido / Ninjitsu / Spellweaving / Mysticism)
- Richer NPC AI (pack tactics, fleeing, formation movement)
- Expanded web panel (live map view, object inspector, script console)
- Additional persistence backends and incremental saves

---

## Contributing

Issues and pull requests are welcome. Please:

- Keep changes focused and covered by tests where practical.
- Match the existing code style and engine-level naming (avoid third-party product names in identifiers).
- Run `dotnet build` and `dotnet test` before submitting.

---

## Acknowledgments

- [SphereServer Source-X](https://github.com/Sphereserver/Source-X) — the scripting/behavior reference this project targets for compatibility
- [ServUO](https://github.com/ServUO/ServUO) — reference for several UO mechanics
- [Ultima Online](https://uo.com/) — Origin Systems / Electronic Arts

---

## License

Open source — see [LICENSE](LICENSE).
