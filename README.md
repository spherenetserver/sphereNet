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
| **World** | Housing (multi.mul, decay, co-owner/friend, lockdown/secure), parties, guilds (war/alliance), weather & day/night, regions |
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

The tick loop runs at a **50 ms interval (20 ticks/second)**; the *budget* column below is the share of that 50 ms a tick consumes. All figures are measured with the in-tree harness — `STRESS` generates the population, `BOT` spawns live TCP clients that log in and play — against a real script + MUL install on .NET 10 Release with Server GC. Each sample is a 30-second window (~600 ticks).

### Large idle / roaming world — the common case

**30,000 NPCs + 300 players walking** (live TCP bots roaming every town):

| Sample | Avg | p50 | p95 | p99 | Max | Budget (avg) |
|---|---|---|---|---|---|---|
| Start (gen + 300 logins) | 7.3 ms | 2.2 ms | 41.8 ms | 81 ms | 146 ms | 15 % |
| Steady | ~3.0 ms | 2.2 ms | ~7 ms | ~9 ms | ~42 ms | 6 % |

Steady state holds a full 20 Hz; the only outliers are occasional ~40 ms GC pauses, still inside budget. Sector sleeping means NPCs away from players cost nothing, so a large world is nearly free.

### Active combat — the expensive case

**2,000 hostile monsters + 300 players engaging** (`STRESS 0 2000 mob` + `BOT 300 combat`):

| Sample | Avg | p50 | p95 | p99 | Tick rate |
|---|---|---|---|---|---|
| Early | 22 ms | ~1 ms | ~90 ms | ~160 ms | 20 Hz |
| Later | 40 ms | ~1 ms | ~130 ms | ~200 ms | ~16 Hz |

The median tick stays ~1 ms; cost concentrates in the NPC-AI apply phase whenever many hostiles are simultaneously acquiring targets and retaliating, and it climbs as more monsters aggro and stay active. The dominant cost in a battle is the count of **simultaneously-active combatants**, not the player count.

**Ceiling:** 30,000 *hostile* monsters + 300 players saturates the loop (~600–800 ms ticks, ~1–2 Hz) — yet it degrades gracefully, with no crash and no multicore fallback. Tens of thousands of concurrently-fighting AI exceed a single 50 ms frame; idle populations of that size do not.

**Save:** 102,780 items + 50,363 characters → **0.6 s** (BinaryGz, 3 shards).
**Memory:** ~550–650 MB working set across every scenario above.

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

The suite covers the expression/script engine, combat formulas, persistence/save formats, packet/era compatibility, movement, housing economy, and runtime safety.

---

## Roadmap & ideas

Areas under active consideration (contributions welcome):

- Broader spell-school support (Necromancy / Chivalry / Bushido / Ninjitsu / Mysticism)
- Plant-growth lifecycle (seed → sprout → crop → fruit) with full crop state
- Light-source burnout timers (charges are tracked; auto-extinguish is pending)
- Richer NPC AI (pack tactics, fleeing, formation movement)
- Expanded web panel (live map view, object inspector, script console)
- Additional persistence backends and incremental/async saves

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
