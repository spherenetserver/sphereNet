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
- **[PACKET_FLOW_GUIDE.md](PACKET_FLOW_GUIDE.md)** — How packets move from network parsing to `GameClient`, plus behavior-level packet sequences for Source-X developers.
- **[PARITY.md](PARITY.md)** — Source-X / Sphere 56x compatibility matrix and the tests guarding each area.
- **[PARITY_ROADMAP_TR.md](PARITY_ROADMAP_TR.md)** — 9.0 / 9.5 parity hedefi için kategori bazlı skor ve uygulama yol haritası.
- **[STUB_INVENTORY_TR.md](STUB_INVENTORY_TR.md)** — Kodda kalan stub, no-op ve bilinçli ertelenmiş uyumluluk açıkları.
- **[SOUND_VISUAL_MOVEMENT_PARITY_TR.md](SOUND_VISUAL_MOVEMENT_PARITY_TR.md)** — Ses, efekt, ışık, hareket ve drag/drop packet parite rehberi.
- **[TRANSITION_GUIDE_TR.md](TRANSITION_GUIDE_TR.md)** — Source-X'ten SphereNet'e geçiş rehberi.
- **[PACKET_GUIDE_SIMPLE_TR.md](PACKET_GUIDE_SIMPLE_TR.md)** — Paket sistemi için basit anlatım rehberi.
- **[DEVELOPER_MAP_TR.md](DEVELOPER_MAP_TR.md)** — Kapsamlı geliştirici haritası ve paket akış rehberi.
- **[GAMECLIENT_DECOMPOSITION_TR.md](GAMECLIENT_DECOMPOSITION_TR.md)** — `GameClient` ayrıştırmasının tamamlanan fazları ve dosya haritası.

### Operations
- **[DEPLOY.md](DEPLOY.md)** — Files, host modes, security model (operator surfaces + checklist), and validation needed to run a shard.
- **[RUNBOOK.md](RUNBOOK.md)** — First-response actions for a live shard (tick lag, save failures, health checks).
- **[PERFORMANCE.md](PERFORMANCE.md)** — Telemetry signals and a repeatable benchmark recipe.

### Changelog
- **[CHANGELOG-EN.txt](../CHANGELOG-EN.txt)** / **[CHANGELOG-TR.txt](../CHANGELOG-TR.txt)** — Per-language changelog.
- **[CHANGELOG_OLD.txt](../CHANGELOG_OLD.txt)** — Bilingual history archive (entries before 2026-05-29).

---

## What SphereNet is, in one minute

SphereNet keeps the classic Sphere/Source-X content model — `.scp` scripts, defnames, triggers, the `.` staff commands — and rebuilds the server engine on modern .NET:

- **Scripting** runs Sphere-style `.scp` content: an expression engine, flow control, object queries, and the trigger system documented in [TRIGGERS.md](TRIGGERS.md).
- **The world** is partitioned into sectors; only sectors near online players tick, and changes are tracked per-field so clients receive deltas, not full resends.
- **The tick loop** is split into parallel and serial phases that scale across cores, with automatic single-threaded fallback.
- **Persistence** offers four save formats (text/binary, optionally GZip) with parallel sharding, plus multi-database MySQL support.
- **Operations** are first-class: a SignalR web panel, a Telnet console, IP/account management, bot-driven stress tests, and a record/replay engine.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the details behind each of these.
