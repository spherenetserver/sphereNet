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
- Script profiler (`EXPERIMENTAL` bit `0x400`, Source-X `EF_Script_Profiler`): every
  `[FUNCTION]` and `@trigger` is timed. The console's `P` prints, as Source-X does,
  the entries whose average is above the overall average (calls, average, min, max,
  total), then the ten with the most **self** time - their own time without the
  functions and triggers they called - and the ten script files with the most self
  time. `E` clears it, so measure a window: `E`, reproduce the lag, `P`. When the
  slow phase in `[slow_tick]` is a scripted one, this names the script. Off, it costs
  one field read per call; on, two timestamps and a dictionary update.

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

## Soak Run

The nightly benchmark runs for under a minute; what grows slowly - memory, state that
drifts out of step, saves that start failing under load - needs hours. The in-process
bot scenarios run from the server console:

```
SCENARIO                      list the scenarios and the running one
SCENARIO Soak 100 240         100 bots for 240 minutes
SCENARIO STOP                 end early and report
```

`Soak` mixes walkers, melee, mages, vendor buyers, looters, skill users and traders
(bots that hand items to each other through secure trade). While it runs:

- memory is sampled every minute (the managed heap as the last collection left it,
  and the working set);
- the world is saved every 15 minutes, and `WorldInvariantAuditor` checks it after each
  save (container index vs contents, orphaned contained items, item types against their
  definitions, spawners over their cap);
- every sample and audit is appended to `logs/soak-<date>.csv` as it happens, so a run
  that is stopped or crashes still leaves its trend.

At the end the server logs (at Warning, so it reaches the file log) a `[soak] PASSED` or
`[soak] FAILED` line: heap and working-set growth in MB per hour - a least-squares line
through the samples after the warm-up (the first fifth of the run, at least 5 minutes) -
saves and failed saves, audits and anomalies, trade windows opened and closed. The run
fails on heap growth above 64 MB/h, on any anomaly and on any failed save; a run too
short to fit a trend says `NOT MEASURED` rather than passing. The other scenarios
(`WalkTalk`, `VendorCycle`, `CombatSoak`, `MixedLoad`, `LoginStorm`) report through the
same bot gate, without the save/audit/memory checks.

Combine with the script profiler: `E` before the run, `P#` after, and the report names
the scripts that cost the most under that load.

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
