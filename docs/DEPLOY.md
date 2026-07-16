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

## Security

SphereNet has several operator surfaces. Treat every one of them as an admin
control plane, not as a public gameplay endpoint.

### Operator surfaces

- **UO client port** — public gameplay traffic. Account creation is controlled
  by `AccApp` in `sphere.ini`.
- **Telnet admin** — binds to loopback and starts only when `AdminPassword` is
  non-empty. Empty passwords fail closed.
- **Web status** — loopback-only status JSON intended for local tooling. Keep it
  behind a trusted host boundary.
- **Web panel** — bearer-token admin API served by `SphereNet.Host`. Use a
  reverse proxy with TLS if it is exposed beyond localhost.
- **IPC named pipe** — local-trust channel between host and managed server. Any
  local process with pipe access is trusted. The managed host uses a random pipe
  name per run, but this is not a remote-auth boundary.
- **Headless stdin** — direct process console commands. Only run the server
  under an account trusted operators can access.

### Checklist

- Set `AccApp=0` unless open account creation is intentional.
- Set a non-empty `AdminPassword` before enabling telnet or panel access.
- Keep `DefaultCommandLevel=0` for public shards — auto-created accounts must not
  receive elevated command access.
- Keep `Md5Passwords=0` for new deployments. MD5 is accepted only for legacy
  compatibility and should be migrated away from.
- Keep telnet, web status, panel, and IPC bound to localhost unless protected by
  a trusted reverse proxy; use TLS at the proxy when exposing the panel.
- Treat named pipe IPC and headless stdin as local-admin surfaces; do not expose
  them or raw panel HTTP to untrusted users.
- Rotate `AdminPassword` / the panel password after setup and after operator
  changes.
- Watch startup validation warnings — unsafe public-shard defaults are reported
  by `SphereConfig.Validate()` before the shard opens.

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

## Updating

Two independent update paths ship with SphereNet. They solve different problems;
pick one per box.

### Panel update (binary, no toolchain)

The **Updates** page in the panel downloads a prebuilt package and applies it in
place. The box needs network access only — no git, .NET SDK, or Node. This only
works under `SphereNet.Host`: applying an update replaces `SphereNet.Host.exe`,
and only the Host can exit to release its own file lock.

`.github/workflows/release.yml` builds a `win-x64` package on every `main` commit
and refreshes the assets of a rolling `nightly` prerelease. The panel reads fixed
asset URLs (`releases/download/<tag>/...`) — anonymous, unauthenticated, and not
API-rate-limited. Actions artifacts are deliberately *not* used: they require an
`actions:read` token even on a public repo and expire after 90 days.

```ini
[SPHERE]
APPUPDATEREPO=spherenetserver/sphereNet   ; empty = feature off, page hidden
APPUPDATECHANNEL=nightly                  ; release tag holding the assets
APPUPDATERUNTIME=win-x64                  ; selects spherenet-<rid>.zip
APPUPDATECHECKMINUTES=15                  ; 0 = no background check
APPUPDATETOKEN=                           ; only needed if the repo goes private
```

The background check only lights a badge — **applying is always an explicit
click**. Applying saves the world (and aborts if the save is rejected), verifies
the package SHA256 against the published checksum, then hands the file swap to a
short-lived external process that waits for the Host to exit, backs up, swaps,
and relaunches. On failure it restores the backup and still relaunches. The
package contains no `config/`, `save/`, or `scripts/`, so shard data is never
touched. Details land in `logs/update.log`; the previous build stays in
`.update/backup/`.

A build without a `version.json` next to the exe reports as a dev build and
refuses to apply, so a locally compiled shard is never silently overwritten
(`build.ps1` strips `version.json` from local Release output).

### Source update (`update.cmd`)

`update.cmd` / `update.ps1` run `git pull` + `build.ps1` and copy the result into
the deploy folder. This needs git, the .NET SDK, and Node on the box, and the
**server must be stopped first**. Point `APPUPDATEREPODIR` at the git clone when
the deploy folder holds only binaries.

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
