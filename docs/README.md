# SphereNet Documentation

This folder holds the technical and operational documentation for SphereNet — a .NET 10 Ultima Online private server emulator with Source-X script compatibility.

For the project overview, features, and quick start, see the top-level **[README.md](../README.md)** (🇹🇷 **[README-TR.md](../README-TR.md)**).

---

## Index

### Reference
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — How the engine is put together: the tick pipeline, sectors, persistence, scripting, and networking.
- **[STAFF_COMMANDS.md](STAFF_COMMANDS.md)** — Every in-game staff/GM command and server-console command, by privilege level.
- **[TRIGGERS.md](TRIGGERS.md)** — Every script trigger that fires, with the `<src>`/`<argo>`/`<argn>`/`<args>` values available inside it.
- **[PROTOCOL_MATRIX.md](PROTOCOL_MATRIX.md)** — Incoming client opcodes routed through `PacketManager` (kept in sync with `PacketManagerTests`).
- **[PACKET_FLOW_GUIDE.md](PACKET_FLOW_GUIDE.md)** — How packets move from network parsing to `GameClient` and its handler classes, plus behavior-level packet sequences for Source-X developers (TR + EN).
- **[DEVELOPER_MAP_TR.md](DEVELOPER_MAP_TR.md)** — Geliştirici haritası: Source-X → SphereNet eşleşmeleri, paket yolculuğu, kod haritası, özellik ekleme rehberi.

### Parity & open work
- **[PARITY.md](PARITY.md)** — The single Source-X / Sphere 56x parity document: domain summary, itemised behaviour-surface detail (SERV verbs, triggers, persistence, object verbs), and the deferred tail / sound-visual gaps at the end.
- **[STUB_INVENTORY_TR.md](STUB_INVENTORY_TR.md)** — Kodda kalan stub, no-op ve bilinçli ertelenmiş uyumluluk açıkları.
- **[INCELEME_DOGRULAMA_PLANI_TR.md](INCELEME_DOGRULAMA_PLANI_TR.md)** — Doğrulanmış inceleme bulgularının tek yaşayan iş takipçisi (açık A/B/C/D/E maddeleri burada).

### Operations
- **[DEPLOY.md](DEPLOY.md)** — Files, host modes, security model (operator surfaces + checklist), and validation needed to run a shard.
- **[RUNBOOK.md](RUNBOOK.md)** — First-response actions for a live shard (tick lag, save failures, health checks).
- **[PERFORMANCE.md](PERFORMANCE.md)** — Telemetry signals and a repeatable benchmark recipe.

### Changelog
- **[CHANGELOG-EN.txt](../CHANGELOG-EN.txt)** / **[CHANGELOG-TR.txt](../CHANGELOG-TR.txt)** — Per-language changelog.
- **[CHANGELOG_OLD.txt](../CHANGELOG_OLD.txt)** — Bilingual history archive (entries before 2026-05-29).

> Housekeeping (2026-07-18): docs/ trimmed from 24 to 13 files. Merged:
> `PARITY_MATRIX` + the open tails of `PARITY_BACKLOG_SUB90` and
> `SOUND_VISUAL_MOVEMENT_PARITY_TR` → `PARITY.md`; `TRANSITION_GUIDE_TR` →
> `DEVELOPER_MAP_TR.md`. Deleted (closed reports / stale / absorbed — full text
> in git history): `PARITY_ROADMAP_TR`, `PACKET_GUIDE_SIMPLE_TR`,
> `PROJE_GENEL_INCELEME_PLANI_TR`, `HOUSE_SHIP_DEED_SISTEM_INCELEMESI_TR`,
> `PERFORMANS_LOG_INCELEMESI_TR`, `PERF_KONUSMA_NPC_VIEW_PLANI_TR`,
> `GAMECLIENT_DECOMPOSITION_TR`, `PARITY_BACKLOG_SUB90`,
> `SOUND_VISUAL_MOVEMENT_PARITY_TR`.

---

## What SphereNet is, in one minute

SphereNet keeps the classic Sphere/Source-X content model — `.scp` scripts, defnames, triggers, the `.` staff commands — and rebuilds the server engine on modern .NET:

- **Scripting** runs Sphere-style `.scp` content: an expression engine, flow control, object queries, and the trigger system documented in [TRIGGERS.md](TRIGGERS.md).
- **The world** is partitioned into sectors; only sectors near online players tick, and changes are tracked per-field so clients receive deltas, not full resends.
- **The tick loop** is split into parallel and serial phases that scale across cores, with automatic single-threaded fallback.
- **Persistence** offers four save formats (text/binary, optionally GZip) with parallel sharding, plus multi-database MySQL support.
- **Operations** are first-class: a SignalR web panel, a Telnet console, IP/account management, bot-driven stress tests, and a record/replay engine.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the details behind each of these.
