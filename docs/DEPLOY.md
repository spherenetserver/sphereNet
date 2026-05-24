# Deployment Guide

This guide covers the minimal files and security decisions needed to run a
SphereNet shard outside a development workspace.

## Required Inputs

- `sphere.ini`: server, account, save, admin, and feature configuration.
- `scripts/`: shard scripts and definitions.
- UO client data: MUL/UOP files referenced by `MulFiles`.
- `save/` or configured `WorldSave`: persistent world snapshots.

Keep `save/`, account files, and admin passwords out of source control.

## Host Modes

- `SphereNet.Server`: headless server process. It owns gameplay, networking,
  telnet, web status, and stdin commands.
- `SphereNet.Host`: panel plus managed server child process. Use this when the
  web panel should manage start, stop, logs, and setup.

The web panel is not started by the headless server alone.

## Security Checklist

- Set `AccApp=0` unless open account creation is intentional.
- Set a non-empty `AdminPassword` before enabling telnet or panel access.
- Keep telnet, web status, panel, and IPC bound to localhost unless protected by
  a trusted reverse proxy.
- Use TLS at the reverse proxy when exposing the panel beyond localhost.
- Treat named pipe IPC and headless stdin as local-admin surfaces.
- Rotate `AdminPassword` after setup and after operator changes.

## Suggested Layout

```text
shard/
  sphere.ini
  scripts/
  mul/
  save/
  logs/
```

Point `ScpFiles`, `MulFiles`, `WorldSave`, and `AcctFiles` at these directories.

## Basic Validation

Before accepting real players:

- Run `dotnet test sphereNet.sln`.
- Start with `AccApp=0` and verify known accounts can log in.
- Confirm telnet refuses to start when `AdminPassword` is empty.
- Confirm save/load roundtrip on a staging world.
- Watch startup logs for missing scripts, map data, and unknown packet warnings.
- Check `http://localhost:<status-port>/health` returns `{"status":"ok"}`.
- Check `http://localhost:<status-port>/status` includes `runtime.tick` data
  such as `p95Ms`, `p99Ms`, `multicoreEnabled`, and phase timings.

## Windows Service / Scheduled Host

For a first production shard on Windows, run `SphereNet.Server.exe` from a
dedicated service account with read/write access only to the shard directory.
Use NSSM, Windows Service Wrapper, Task Scheduler, or your preferred supervisor
to restart on failure. Redirect stdout/stderr to `logs/` and keep the current
working directory at the shard root so relative `sphere.ini` paths resolve.

## Linux systemd Sketch

```ini
[Unit]
Description=SphereNet shard
After=network.target

[Service]
WorkingDirectory=/opt/spherenet/shard
ExecStart=/usr/bin/dotnet /opt/spherenet/SphereNet.Server.dll
Restart=on-failure
RestartSec=10
User=spherenet
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
```

Keep saves and account files on persistent storage and back them up outside the
process. `BackupLevels` protects recent file generations, not disk loss.

## Reverse Proxy Notes

If the panel is exposed remotely, terminate TLS in a reverse proxy and forward
only to the localhost panel port. Do not expose raw panel HTTP, web status, or
telnet directly to the internet.
