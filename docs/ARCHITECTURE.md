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

The map is partitioned into sectors. **Sectors within a 5×5 window around an online
player tick**, and a sector keeps ticking for **SECTORSLEEP** after the last player
leaves it (`config/sphere.ini`, 1 minute as shipped; Source-X
`CSector::_CanSleep`/`g_Cfg._iSectorSleepDelay` defaults to 10). NPCs and items
elsewhere cost zero CPU until a player approaches.

The grace is stamped on the sector a player is **in**, not on the whole window that
sweeps over it, so a traveller leaves a trail one sector wide.

`SECTORSLEEP=0` does **not** mean what it means upstream. Upstream ticks every sector
and lets `_CanSleep` decide, so a delay of zero really does keep the whole map awake.
Here the tick set is built from the 5×5 window and the predicate is only ever asked
about sectors that were awake last tick, so zero means: **a sector that has woken never
sleeps again, and a sector no player has been near still never ticks**. The awake set
therefore only grows — measured at 32 → 137 sectors as one player crossed eight areas
(`SectorWakeTransitionTests`) — and keeps growing for the life of the process. Use a
small non-zero value (the shipped `1`) rather than `0` for "barely sleeps".

**Measured** — 500 online players, 50,000 NPCs, 300,000 ground items on a
6144×4096 map (96×64 = 6144 sectors), 355 MB of managed heap (~1 KB per object),
world tick only, against the 100 ms server tick:

| Shape | Awake sectors | tick p50 | tick p95 |
|---|---|---|---|
| Players in ~10 towns, no trail | 365 | 1.5 ms | 2.1 ms |
| Same, with a 1-minute trail (`SECTORSLEEP=1`, as shipped) | 485 | 1.5 ms | 2.3 ms |
| Same, with a 10-minute trail (`SECTORSLEEP=10`, upstream default) | 817 | 1.9 ms | 2.4 ms |
| 500 players scattered evenly across the whole map | 5726 | 11.9 ms | 19.6 ms |

The sleep grace is cheap, and awake **area** stopped being the cost it was. Those
same four rows measured 3.2/4.7, 3.5/5.1, 5.5/7.9 and 66.8/171.3 ms while an awake
sector still called `OnTick` on every item it held; item deadlines moved to a due
queue and the sector stopped polling, which is what took the scattered case from
171% of the tick budget to 20%. What remains in an awake sector is character work.

The first tick after a load is the exception: a save arrives with every deadline it
was stored with, most of them already past, so the queue drains that backlog at up
to 2000 timers a tick until it is caught up. Reproduce all of it with
`SectorSleepLoadProbe` (`SPHERENET_LOADPROBE=1`).

Because gameplay can pause for empty regions but timers cannot, every deadline is
an **absolute timestamp** (`Environment.TickCount64`), never a tick counter. A
deadline therefore never drifts, and nothing overdue is skipped: an item ten
minutes past its deadline runs on the first tick after its sector wakes.

Running the callback **at** the deadline is a separate promise, and it is not the
same for every kind of timer. What a sleeping sector costs is delay, and this is
how much:

| Timer | With nobody nearby |
|---|---|
| Anything in an active sector (5×5 sectors around a player) | next tick |
| `TIMERF` on any object | next tick, wherever it is |
| `TIMER` on any item — worn, contained, or lying in a sleeping sector | next tick, wherever it is |
| Spawn interval | next tick when it comes due; a spawner at its cap is restarted by the death of one of its creatures, not by being polled |
| Ground-item decay, corpses included, anywhere | next tick: armed deadlines sit in a due-ordered queue, drained **256** per tick, with an audit every **60 s** that re-queues anything armed the queue does not hold |
| Everything a character does — AI, regen, poison — in a sleeping sector | not at all until a player comes within two sectors |
| A sector flagged `SECF_NoSleep` | like an active sector |
| A pet or a summon, wherever it is | next tick: a creature with a master stays in the AI wheel whatever its sector does, which is what makes a summon's expiry and a pet's loyalty and food exact. Releasing it in a sleeping sector stops all three |

**Transitions**, measured in `SectorWakeTransitionTests`: a teleport wakes the
destination on the next tick (the window is rebuilt from positions, so no sector needs
to be walked through); the sector left behind keeps ticking through its grace, while a
sector nobody has been near does not; pacing a sector boundary causes no sleep/wake
churn; a map change wakes the same coordinates on the new map and leaves the old map's
sector to its grace; a client that is lingering after a link loss still holds its sector
awake. Waking a sector of **240** creatures spreads them over about three ticks, worst
tick **100** — the spread is the creature's uid modulo 800 ms, so creatures with
consecutive uids (a sector filled by one spawner, or by a world load) are one
millisecond apart and occupy as many 100 ms ticks as they are hundreds. The per-tick AI
budget of 500 is the backstop.

What sleeps is **character work** — AI, regen, poison. Item deadlines are held in
world-level due queues (one for `TIMER`, one for decay), drained every tick, so an
item is reached when its deadline arrives and not before: sector sleep stopped
being part of the answer for them. That also leaves `CAN=O_NOSLEEP` with nothing to
buy for a timer; it is honest about that rather than appearing to do something.

The queues are fed by the single write door each deadline goes through, and each
has an auditor that re-queues and **reports** anything armed it does not hold — a
deadline that never reached a queue would otherwise be a timer that never fires.

The delays above are pinned to the engine's own constants by
`SleepingSectorTimerContractTests`.

**Deadlines are written through one door.** `Timeout` changes only through
`ObjBase.SetTimeout` (which is what registers an armed timer with the world-level
pump for items no sector list covers), and `DecayTime` only through `Item`'s
`SetDecayTime` / `SetDecayAt` / `ClearDecay`. Upstream runs every timer from a
single time-sorted list (`CWorldTicker`), where a deadline has to be registered when
it changes; SphereNet still reaches deadlines by sweeping, but a field that a dozen
call sites assign directly could never make that move safely — the one assignment
that forgot to register would produce a timer that never fires, with nothing to say
so. `DeadlineWriteGateGuardrailTests` holds the invariant.

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

**The contract**, measured against a plain list of deadlines over fixed-seed
schedule/remove/advance sequences (`TimerWheelModelTests`):

- A slot is 100 ms and there are 256 of them, so the wheel turns every 25.6 s. A
  deadline is parked in the slot that covers it, **rounded up**: it is delivered at
  that slot's start and never a millisecond earlier. Asking for +101 ms gets +200 ms;
  asking for exactly one revolution (+25,600 ms) is exact. Rounding may only ever
  delay — nothing fires early.
- `Schedule` with a deadline that has already passed does **not** fire immediately: it
  is clamped to the next slot.
- `Schedule` for a uid the wheel already holds is refused, not replaced. Cancel with
  `Remove` first if the deadline is to change.
- `Remove` retires the schedule but leaves its slot entry to be discarded when that
  slot is next walked, so **`Count` is live schedules and not retained memory**:
  50 NPCs rescheduled 20 times report `Count` 50 while 1,000 entries are still parked.
  One full revolution reclaims all of them.
- A uid that comes back as a different creature does not inherit the old schedule; the
  generation stamped on each entry retires it.
- A forward clock jump is walked slot by slot, so it costs the elapsed time divided by
  100 ms: one hour on an empty wheel measured at **1.5 ms** (36,000 steps), and
  everything that came due inside the jump fires in that one advance.
- One advance returns at most 500,000 due entries; the rest are picked up by the next
  one.

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

## Map data: what is mapped and what is held

MUL and UOP map files are memory-mapped, so the OS pages in the regions being read and
pages out the rest. A UOP container is extracted to a temp file first and that file is
opened `DeleteOnClose`, so it goes when the last handle closes even if nothing disposes
the reader. A constructor that fails part way through gives back what it has taken: the
extraction is removed, and the statics index mapping is released when the data file
beside it cannot be opened — on Windows a mapped file cannot be deleted or replaced, so
a leak there means an operator cannot swap a bad map file out without restarting.

In front of the statics mapping sits a per-block cache with **no eviction**, and that is
a deliberate choice rather than an oversight. It is bounded by the map: one entry per
8×8 block, so the worst case is every block a player has ever walked past.
**Measured** (`StaticCacheGrowthTests`) at 124 bytes per block with four statics each,
which extrapolates to **~46 MB** for a full 6144×4096 map (393,216 blocks) — after
walking all of it. A second pass over the same ground allocates nothing. An eviction
policy would cost locking on a read path that the parallel NPC prestage reaches, to
reclaim tens of megabytes that only a complete tour of the map can accumulate, so the
number is documented instead. Note that an empty block costs an entry too: the bound is
blocks, not statics.

The README's "~200 MB saved" is the saving against loading the MUL files into RAM
outright; it is not a cap on what the map subsystem holds.

## Performance-sensitive hot paths

When changing these, capture before/after telemetry (see [PERFORMANCE](PERFORMANCE.md));
do not restructure data-locality without a baseline:

- Tick & sector work — `GameWorld.OnTick/OnTickParallel`, `Sector.OnTick`,
  `GetObjectsInRange`, `Program.Tick.cs` build/apply/flush.
- NPC decision build/apply (the dominant `npc_apply` phase under load).
- View delta build/apply and packet flush.
- Region lookup cache and the sleeping-sector maintenance scan.
- Statics block reads (`StaticReader.ReadBlock`), reached by the parallel NPC
  prestage — see the cache note above before putting a lock on it.
