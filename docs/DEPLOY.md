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

Three update paths ship with SphereNet. The first two apply the same prebuilt
package; the third builds from source. Pick one per box.

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
package contains no `save/` or `scripts/`, and the swap never writes into
`config/`, `save/`, `scripts/`, `logs/` or `accounts/`. The config templates
travel as `defaults/config/` and are copied into `config/` **only when the file is
missing** (checked in both `config/` and the install root, the two places the
Host looks), so an existing `sphere.ini` is never replaced. Details land in
`logs/update.log`; the previous build stays in `.update/backup/`.

A build without a `version.json` next to the exe reports as a dev build and
refuses to apply, so a locally compiled shard is never silently overwritten
(`build.ps1` strips `version.json` from local Release output).

### Standalone updater (`SphereNet.Updater.exe`)

The same package, without the panel: `SphereNet.Updater.exe` ships in the install
folder and as its own asset on the `nightly` release. Run it in the install
folder to update, or drop it into an empty folder to install from scratch — it
downloads the package, verifies the SHA256, lays it over the folder with the
same rules as the panel (user data untouched, missing config from
`defaults/config/`, backup in `.update/backup/`, rollback on failure) and starts
`SphereNet.Host`. It reads the same `APPUPDATE*` keys; command-line flags override
them.

A running server is never killed silently: the updater waits until the
operator closes it (saving the world), unless `--kill` is given.

```text
SphereNet.Updater.exe              update (or install) this folder, then start the Host
SphereNet.Updater.exe --check      only compare versions
SphereNet.Updater.exe --dir D:\shard --no-start --no-pause
SphereNet.Updater.exe --force      reinstall even when current / over a source build
```

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

- The panel only answers the Host names `localhost`, `127.0.0.1` and `::1`. If
  the proxy passes the public name on as `Host`, add it to
  `ADMINPANELALLOWEDHOSTS=panel.example.org` (comma-separated), otherwise every
  request gets 400. The check blocks DNS rebinding against the local panel.
- The proxy should append the client to `X-Forwarded-For`; the login limiter
  keys on that address when the request comes from loopback.
- `/api/auth/local-hint` never answers a proxied request, but keep
  `ADMINPANELAUTOFILL=0` on any machine reachable from outside.
- Public paperdolls (`PUBLICPAPERDOLL=1`) are served by the same panel port at
  `/public/paperdoll/<serial>.png` and `/public/paperdoll/<serial>.json`, without
  a login. To show them on a website, route only the `/public/` prefix to the
  panel (keep `/api/` and the panel UI internal if you can), add the public name
  to `ADMINPANELALLOWEDHOSTS`, and list the site in
  `PUBLICPAPERDOLLORIGINS` if its scripts `fetch` the JSON (a plain `<img>` needs
  no CORS). Write host names there (`www.example.org`, `example.org:8443`), not
  `https://...`: sphere.ini treats `//` as a comment. Only player characters
  below Counselor are shown; everything else is a 404. Responses carry
  `Cache-Control: max-age=60`, and each client address is limited to 60
  requests a minute (keyed on `X-Forwarded-For` from the proxy).

The Host starts the game server itself (`HOSTAUTOSTART=1`) and restarts it
after a crash (`HOSTRESTARTONCRASH=1`, backing off, and giving up after five
crashes in ten minutes).
