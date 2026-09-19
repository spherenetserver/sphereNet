# Operator Runbook

This runbook covers first-response actions for a live SphereNet shard.

## Health Checks

- `GET /health`: returns `{"status":"ok"}` when the local status server is alive.
- `GET /status`: returns uptime, counts, memory, connections and `runtime`
  telemetry. Watch `runtime.p95Ms`, `runtime.p99Ms`, `runtime.maxSinceStartMs`
  and phase timings when diagnosing lag.
- Panel `/api/server/status`: mirrors the same server stats for operators.

## Tick Lag

1. Check `[tick_stats]` for p95/p99 and player/item counts.
2. Check `[slow_tick]` phase fields: `npc_apply`, `view_build`, `flush`,
   `compute`, and `snapshot`.
3. If `view_build` dominates, inspect item density and stacked tiles.
4. If `npc_apply` dominates, reduce active NPC stress or capture a profiler run.
5. Avoid changing data-locality structures without a before/after baseline.

## Save Failure

1. Stop new risky admin actions such as mass item moves.
2. Check disk free space and write permissions for `WorldSave`.
3. Remove stale `*.tmp` files only after confirming the server is stopped or no
   save is active.
4. Restore from live file first, then `.bak1`, `.bak2`, etc. if the live file is
   corrupted.
5. After restore, start on a staging copy and run a save/load smoke test.

## Disk Full

1. Stop the shard or block login.
2. Move old logs and external backups away from the save volume.
3. Keep the newest live save and newest `.bakN` files.
4. Restart only after a manual `.save` succeeds.

## Packet Flood

1. Check logs for packet quota, partial timeout, malformed packet and unknown
   opcode warnings.
2. Block the source IP at the firewall or reverse proxy layer.
3. Keep `MaxPacketsPerTick` conservative for public shards.
4. Preserve a short packet/debug log sample for parser regression tests.

## The Server Died

Look in `<LogDir>/server-crash.log` first. Every fault the tick loop could not contain
is appended there with the exception chain and the state around it: uptime, the tick it
was on, how many objects and clients it was holding, the GC heap, and which thread it
happened on. A crash loop leaves a history, so read from the bottom.

What lands there:

- an exception on any thread that is not the main loop (network accept, timers, save
  worker, console reader) — `AppDomain.UnhandledException`
- a fault during boot, before the main loop exists — `ServerMain`
- a task fault nobody awaited — `TaskScheduler.UnobservedTaskException`. Not fatal, but
  a fault nobody observed is often the first sign of the one that is

What does NOT land there, because the runtime does not give managed code the chance:
`StackOverflowException`, some `OutOfMemoryException`, `Environment.FailFast`, and a
crash inside native code. For those the record has to come from the runtime itself, so
start the server with:

```
DOTNET_DbgEnableMiniDump=1
DOTNET_DbgMiniDumpType=2
DOTNET_DbgMiniDumpName=<LogDir>/server-%p.dmp
```

`MiniDumpType=2` is heap-with-stacks, which is what a stack overflow needs; `4` is a full
dump and much larger. Open the file with `dotnet-dump analyze`. Leaving this on costs
nothing until the process actually dies.

A fault the tick loop DID contain does not appear here at all: it is logged as
`Main-loop action failed` and counted, and `loopFaults` in the crash record shows how
many had already happened before the fatal one. A rising count with no crash is still
worth chasing.

## Restore Drill

At least once per release cycle:

1. Copy `save/`, accounts and `sphere.ini` to a clean staging directory.
2. Start the server with gameplay ports firewalled.
3. Verify `/health`, `/status`, account login and one manual `.save`.
4. Record elapsed restore time and any manual fixes required.
