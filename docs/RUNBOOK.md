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

## Restore Drill

At least once per release cycle:

1. Copy `save/`, accounts and `sphere.ini` to a clean staging directory.
2. Start the server with gameplay ports firewalled.
3. Verify `/health`, `/status`, account login and one manual `.save`.
4. Record elapsed restore time and any manual fixes required.
