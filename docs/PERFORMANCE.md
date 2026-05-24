# Performance Benchmark Notes

SphereNet already emits enough runtime telemetry to build repeatable benchmarks.
Do not optimize `npc_apply`, view updates, or packet flush paths without capturing
before/after numbers.

## Existing Signals

- `[tick_stats]`: periodic average/max/p50/p95/p99 tick, entity counts, and bot packet rates.
- `[slow_tick]`: phase breakdown when a tick exceeds the slow threshold. Useful
  fields include NPC apply, view build, output flush, and world maintenance.
- `/status`: exposes the same rolling tick percentiles and last phase telemetry
  under the `runtime` object for automation and panels.
- `runtime.maps`: per-map read-only counts for chars, items, sectors, active
  sectors, and online players. This is the evidence source for any future
  map-based worker partitioning decision.
- `runtime.slowTickCount` and `runtime.lastSlowTickDominantPhase`: quick signal
  for whether recent lag is mostly `npc_apply`, `view_build`, `flush`, or
  another phase.
- `TickSleepMode`: controls main-loop yield strategy.

## Manual Benchmark Recipe

1. Start a staging shard with representative scripts and map data.
2. Set `AccApp=0` and use test accounts or bot-only load.
3. Spawn a fixed bot count and duration, for example 50 bots for 60 seconds.
4. Capture server logs from startup until bots disconnect.
5. Record:
   - average, max, p50, p95, p99 tick from `[tick_stats]`
   - count and worst phase from `[slow_tick]`
   - packet send/receive rate
6. Repeat the same run after code changes.

## Baseline Artifact

Store benchmark results as JSON so nightly jobs and reviewers compare the same
fields:

```json
{
  "commit": "git-sha",
  "scenario": "idle",
  "durationSeconds": 180,
  "hardware": "cpu/ram/os note",
  "tick": {
    "avgMs": 0.0,
    "maxMs": 0.0,
    "p50Ms": 0.0,
    "p95Ms": 0.0,
    "p99Ms": 0.0
  },
  "runtime": {
    "multicoreEnabled": true,
    "slowTickCount": 0,
    "worstPhase": "view_build",
    "maps": [
      { "mapId": 0, "chars": 0, "items": 0, "sectors": 0, "activeSectors": 0, "onlinePlayers": 0 }
    ]
  },
  "gc": {
    "gen0": 0,
    "gen1": 0,
    "gen2": 0,
    "managedHeapMB": 0
  }
}
```

## Regression Gates

Use these as starting thresholds for CI or nightly jobs:

- No test/build failures.
- Average tick does not regress by more than 15 percent.
- Max tick does not regress by more than 25 percent.
- No new recurring `[slow_tick]` phase dominates the run.

Adjust thresholds only after collecting baseline data on the target hardware.

## Optimization Order

1. Measure `npc_apply` and AI decision/apply cost.
2. Check repeated world scans and region/resource lookups.
3. Check view delta build and packet flush overlap.
4. Extract small helpers only when the benchmark stays green.
5. Keep behavior-changing AI/network optimizations behind focused tests.
