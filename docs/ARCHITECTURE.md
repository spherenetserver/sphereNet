# Architecture

How SphereNet is structured and why. This is the engineering companion to the
top-level [README](../README.md); for operations see [DEPLOY](DEPLOY.md) and
[RUNBOOK](RUNBOOK.md).

---

## Project layout

```
src/
├── SphereNet.Core/          Core types, enums, configuration, trigger enums
├── SphereNet.Network/       UO protocol, TCP, encryption (Blowfish/Twofish/Huffman)
├── SphereNet.Scripting/     .scp parser, expression engine, execution, definitions
├── SphereNet.Game/          Game logic: AI, Combat, Magic, Skills, Death, World, Items, ...
├── SphereNet.MapData/       MUL/UOP map & tiledata readers
├── SphereNet.Persistence/   Save/load, importers
├── SphereNet.Panel/         SignalR web panel (ASP.NET Core)
├── SphereNet.Host/          Launcher / managed-server host
├── SphereNet.Server/        Headless server entry point, tick loop, admin/console, IPC
└── SphereNet.Tests/         Automated test suite
```

Dependencies flow downward: `Core` has no game dependencies; `Network`, `Scripting`,
and `MapData` build on `Core`; `Game` ties them together; `Server`/`Host`/`Panel`
host the runtime.

---

## The tick loop

The server advances the world on a fixed interval (default 100 ms, 10 ticks/s).
Each tick is split into phases so the heavy, read-only work can run across cores
while order-sensitive writes stay deterministic.

| Phase | Type | Work |
|---|---|---|
| Snapshot | Parallel | Sector tick, NPC snapshot capture |
| Build | Parallel | NPC decision computation (read-only) |
| Apply | Serial | Decisions applied in UID order (deterministic) |
| Flush | Serial | Decay, light, telnet, web, output |

Key properties:

- **Auto-fallback.** Any error in the parallel path drops the server to a single
  threaded tick for that cycle, so a parallelism bug degrades performance rather
  than corrupting state.
- **Determinism.** All mutations happen in the serial Apply/Flush phases, applied
  in UID order. The parallel phases only read and compute decisions.
- **Telemetry.** `[tick_stats]` (avg/max/p50/p95/p99, entity counts, packet
  rates) and `[slow_tick]` (per-phase breakdown) are emitted continuously and
  exposed under `/status` → `runtime`. See [PERFORMANCE](PERFORMANCE.md).

Entry points: `Program.Tick.cs` (`RunSingleThreadTick`, `RunMulticoreTick`,
`RunPostTickMaintenance`) and `GameWorld.OnTick` / `OnTickParallel`.

---

## Sectors and sector sleeping

The map is partitioned into sectors. **Only sectors within a 5×5 window around an
online player tick.** NPCs and items in empty regions cost zero CPU until a player
approaches.

Because gameplay can pause for empty regions but timers cannot, all timers
(item decay, spawn intervals, `TIMER` triggers) use **absolute timestamps**
(`Environment.TickCount64`), never tick counters. Sleeping sectors still receive a
lightweight maintenance pass every ~3 minutes that processes item timers only
(decay, spawn, `TIMER`) — so spawners keep producing, expired items are removed,
and timers fire on schedule with no drift.

`FindRegion`, called thousands of times per tick (guard zones, PvP, music,
weather), is backed by an 8×8-tile grid `ConcurrentDictionary` cache that avoids
O(n) region scans and auto-invalidates when regions change.

Relevant files: `World/GameWorld.cs`, `World/Sectors/Sector.cs`,
`World/Regions/`, `Server/Program.Tick.cs`.

---

## Change tracking (delta view)

Rather than resending visible objects in full each tick, every object carries a
`DirtyFlag` bitmask (Position, Body, Hue, Stats, Equip, …). Only changed fields
are sent. View computation (`BuildViewDelta`) runs in the parallel phase; the
packet I/O (`ApplyViewDelta`) runs in the serial phase, keeping it multicore-safe.

---

## Scheduling: the NPC timer wheel

Instead of scanning every NPC each tick, NPCs are bucketed into a 256-slot hashed
timer wheel by their `nextActionTime`, giving O(1) scheduling. This keeps the
parallel Build phase proportional to *active* NPCs, not total NPC count.

---

## Scripting engine

SphereNet runs Sphere-style `.scp` content. The pipeline is:

1. **Parsing** (`Scripting/Parsing`): `.scp` files are read into sections and
   `ScriptKey` key/value pairs; definitions (`CHARDEF`, `ITEMDEF`, `[SPELL ...]`,
   regions, spawns, templates) are loaded and indexed by defname.
2. **Expressions** (`Scripting/Expressions/ExpressionParser`): integer & float
   math, bitwise ops, the `@` power operator, `MAX`/`MIN`/`ABS`/`SQRT`/trig/`RAND`,
   string helpers, object queries (`ISNEARTYPE`, `FINDID`, `DISTANCE`, …), and
   Sphere brace-ranges `{n m}` / weighted `{a w b w}`.
3. **Execution** (`Scripting/Execution/ScriptInterpreter`): flow control
   (`IF`/`WHILE`/`FOR`/`DORAND`/`DOSWITCH`/`BEGIN`), property reads/writes, and
   verbs on the object model.
4. **Triggers** (`Game/Scripting/TriggerDispatcher`): engine events fire named
   triggers on characters/items with a defined argument set — fully documented in
   [TRIGGERS.md](TRIGGERS.md).

The object model (`Game/Objects`) exposes properties and verbs to scripts on a
common base (`ObjBase`) with `Character` and `Item` specializations, mirroring the
Source-X `r_WriteVal` / `r_LoadVal` / `r_Verb` model.

---

## Networking and encryption

`SphereNet.Network` implements the UO protocol: the login → relay → game-server
handshake, packet framing, Blowfish/Twofish game encryption, and Huffman
compression on the outbound path. Incoming opcodes are routed through
`PacketManager`; the registered set is tracked in [PROTOCOL_MATRIX.md](PROTOCOL_MATRIX.md)
(a test fails if the registry and the matrix drift).

> **Caution — crypto/compression are high-risk.** Login, relay keys, game
> encryption, framing, and Huffman are tightly coupled; a small change can break
> login before any gameplay code runs. Do not change the inbound Huffman/receive
> path without a login regression or captured-packet replay test, and cover
> `USECRYPT`/`USENOCRYPT` variants plus at least one legacy client in addition to
> ClassicUO.

---

## Persistence

The world saves through `SphereNet.Persistence`:

- **Four formats** — `Text` (`.scp`, Source-X compatible), `TextGz`, `Binary`
  (`.sbin` tag-stream), `BinaryGz`. Switchable at runtime with `.saveformat`,
  which performs a one-shot migration.
- **Sharding** — `SAVESHARDS` of `0` (single file), `1` (size-based rolling), or
  `2–16` (parallel hash shards by `UID % N`) for concurrent I/O.
- **Multi-database** — multiple named MySQL connections, each with its own host,
  threading mode, and timeouts; scripts switch with `db.select <name>`.

Maps are loaded via `MemoryMappedFile`, letting the OS manage page residency
instead of loading every map fully into RAM.

---

## Operations surface

- **Web panel** (`SphereNet.Panel` + `SphereNet.Host`): SignalR dashboard with
  live logs, CPU/RAM/thread metrics, player list, and server controls.
- **Telnet console** and **headless stdin**: both route into the single
  `AdminCommandProcessor`; see [STAFF_COMMANDS.md](STAFF_COMMANDS.md).
- **IPC**: a named-pipe channel between the managed host and the server for
  stats push and structured ops (save/resync/shutdown/…).
- **Bots**: a TCP bot framework for stress testing (`.bot`).
- **Record/replay**: a SQLite-backed engine capturing movement and state
  snapshots for GM investigations and debugging.

Treat every operations surface as an admin control plane — see the security
section of [DEPLOY.md](DEPLOY.md#security).

---

## Performance-sensitive hot paths

When changing these, capture before/after telemetry (see [PERFORMANCE](PERFORMANCE.md));
do not restructure data-locality without a baseline:

- Tick & sector work — `GameWorld.OnTick/OnTickParallel`, `Sector.OnTick`,
  `GetObjectsInRange`, `Program.Tick.cs` build/apply/flush.
- NPC decision build/apply (the dominant `npc_apply` phase under load).
- View delta build/apply and packet flush.
- Region lookup cache and the sleeping-sector maintenance scan.
