# SphereNet Documentation

Technical and operational documentation for SphereNet — a .NET 10 Ultima Online
server emulator with Source-X script compatibility.

For the project overview, features, and quick start, see the top-level
**[README.md](../README.md)** (🇹🇷 **[README-TR.md](../README-TR.md)**).

---

## Index

### Reference
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — How the engine is put together: the tick pipeline, sectors, persistence, scripting, networking.
- **[STAFF_COMMANDS.md](STAFF_COMMANDS.md)** — Every in-game staff/GM command and server-console command, by privilege level.
- **[TRIGGERS.md](TRIGGERS.md)** — Every script trigger that fires, with the `<src>`/`<argo>`/`<argn>`/`<args>` values available inside it.
- **[PROTOCOL_MATRIX.md](PROTOCOL_MATRIX.md)** — Incoming client opcodes routed through `PacketManager` (pinned by `PacketManagerTests`).
- **[PACKET_FLOW_GUIDE.md](PACKET_FLOW_GUIDE.md)** — How a packet travels from network parsing to `GameClient` and its handlers, plus behaviour-level packet sequences (TR + EN).
- **[DEVELOPER_MAP_TR.md](DEVELOPER_MAP_TR.md)** — Gelistirici haritasi: Source-X → SphereNet eslesmeleri, paket yolculugu, kod haritasi, ozellik ekleme rehberi.

### Operations
- **[DEPLOY.md](DEPLOY.md)** — Files, host modes, security model, and the validation needed to run a shard.
- **[RUNBOOK.md](RUNBOOK.md)** — First-response actions for a live shard (tick lag, save failures, health checks).
- **[PERFORMANCE.md](PERFORMANCE.md)** — Telemetry signals and a repeatable benchmark recipe.

### Changelog
- **[CHANGELOG-EN.txt](../CHANGELOG-EN.txt)** / **[CHANGELOG-TR.txt](../CHANGELOG-TR.txt)** — Per-language changelog. **This is the record of what changed and why.**
- **[CHANGELOG_OLD.txt](../CHANGELOG_OLD.txt)** — Bilingual history archive (entries before 2026-05-29).

---

## What belongs here, and what does not

This folder holds documents that describe **how the engine works** — things that
stay true until the code itself changes, and that a reader consults to
understand or operate the server.

It deliberately holds **no** progress trackers, parity scorecards, audit
chapters, coverage matrices or release-acceptance reports. Those were written
against a moving codebase, and every one of them drifted into stating things
that were no longer true — which is worse than having no document at all. They
were removed on 2026-09-16 (full text remains in git history).

Where to look instead:

| Question | Source of truth |
|---|---|
| What changed, and why? | `CHANGELOG-EN.txt` / `CHANGELOG-TR.txt` |
| How does Source-X do this? | `oldSphere/Source-X-full/src` (the C++ reference) |
| What does the client expect? | `oldSphere/ClassicUO-main/src` |
| Is this behaviour actually pinned? | The test suite — `dotnet test src/SphereNet.Tests` |
| Which opcodes are routed? | `PROTOCOL_MATRIX.md` (a test keeps it honest) |

A measurement is worth keeping only when a test fails if it goes stale. Two do,
and they live as data rather than prose: `PROTOCOL_MATRIX.md` and
[`data/sourcex_tables.csv`](data/sourcex_tables.csv).

---

## What SphereNet is, in one minute

SphereNet keeps the classic Sphere/Source-X content model — `.scp` scripts,
defnames, triggers, the `.` staff commands — and rebuilds the server engine on
modern .NET:

- **Scripting** runs Sphere-style `.scp` content: an expression engine, flow control, object queries, and the trigger system documented in [TRIGGERS.md](TRIGGERS.md).
- **The world** is partitioned into sectors; only sectors near online players tick, and changes are tracked per-field so clients receive deltas, not full resends.
- **The tick loop** is split into parallel and serial phases that scale across cores, with automatic single-threaded fallback.
- **Persistence** offers four save formats (text/binary, optionally GZip) with parallel sharding, plus multi-database MySQL support.
- **Operations** are first-class: a SignalR web panel, a Telnet console, IP/account management, bot-driven stress tests, and a record/replay engine.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the details behind each of these.
