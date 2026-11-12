# SphereNet

An **Ultima Online server emulator on .NET 10**, built for compatibility with
[Source-X](https://github.com/Sphereserver/Source-X) scripts and legacy Sphere saves.
Runs on Windows, Linux and macOS.

[Türkçe](README-TR.md) · [Documentation](docs/README.md) · [Changelog](CHANGELOG-EN.txt)

## Features

- **Scripting:** Sphere `.scp` definitions, expressions, triggers and object queries.
- **Gameplay:** combat, magic, skills, crafting, housing, ships, guilds and parties.
- **NPCs and player bots:** creature and pet AI, pathfinding, server-driven player
  characters, and optional local LLM dialogue.
- **Networking:** UO login and game protocol, client-version handling,
  Blowfish/Twofish encryption and Huffman compression.
- **Engine:** parallel NPC preparation and client view building, serial script
  execution and world updates, sector sleeping and incremental client updates.
- **Persistence:** legacy save import, text/binary saves with optional GZip,
  parallel shards, background saving and MySQL/PostgreSQL/SQLite connections.
- **Operations:** web dashboard, admin console, load testing and state recording/replay.

## Quick start

Requires the **.NET 10 SDK**, UO client data and a Sphere/Source-X script pack.

```sh
git clone https://github.com/spherenetserver/sphereNet.git
cd sphereNet
```

Edit `config/sphere.ini` before starting:

| Setting | Directory |
|---|---|
| `MULFILES` | UO client data |
| `SCPFILES` | Script pack |
| `WORLDSAVE` | World saves |

```sh
dotnet build src/SphereNet.Server/SphereNet.Server.csproj -c Debug
dotnet run --project src/SphereNet.Server
```

The game port defaults to **2593**. For the web dashboard and managed server
startup, use **SphereNet.Host**; its panel port defaults to **9999**.
See [deployment](docs/DEPLOY.md) and [configuration](docs/CONFIG-EN.md) for setup.

## Latest recorded performance

**2026-11-11**, measured with `SphereNet.LoadTest`: real TCP clients walking,
talking and using items among **2,000 NPCs and 4,000 ground items**. Each run
includes a world save during a 45-second window. Server and clients share an
AMD Ryzen 7 5700X3D desktop running Windows 11; the server uses 8 workers.

| Clients | Placement | Tick p50 / p95 / p99 | Step response p50 / p95 / p99 | Allocation rate |
|---|---|---|---|---|
| 1,000 | Across town | 5.8 / 9.7 / 17.1 ms | 7.4 / 12.7 / 17.9 ms | 50 MB/s |
| 500 | One screen | 2.9 / 15.2 / 25.0 ms | 5.1 / 12.0 / 42.9 ms | 39 MB/s |
| 1,000 | One screen | 5.5 / 25.2 / 38.4 ms | 13.0 / 31.3 / 57.4 ms | 79 MB/s |

Step response is measured at the client. One-screen runs use
`MaxSizeClientOut=400000`; results depend on visibility, scripts and hardware.
See the [benchmark guide](docs/PERFORMANCE.md) to measure your own shard.

## Development and documentation

Run the automated tests:

```sh
dotnet test src/SphereNet.Tests/SphereNet.Tests.csproj
```

- [Architecture](docs/ARCHITECTURE.md)
- [Staff commands](docs/STAFF_COMMANDS.md) and [script triggers](docs/TRIGGERS.md)
- [Player bots](docs/PLAYERBOTS.md)
- [Operations runbook](docs/RUNBOOK.md)

Issues and focused PRs with tests are welcome. Changes are recorded in the
[changelog](CHANGELOG-EN.txt).

**References:** [Source-X](https://github.com/Sphereserver/Source-X),
[ServUO](https://github.com/ServUO/ServUO), [Ultima Online](https://uo.com/).
