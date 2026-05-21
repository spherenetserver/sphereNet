# Performance Benchmark Notes

SphereNet already emits enough runtime telemetry to build repeatable benchmarks.
Do not optimize `npc_apply`, view updates, or packet flush paths without capturing
before/after numbers.

## Existing Signals

- `[tick_stats]`: periodic average/max tick, entity counts, and bot packet rates.
- `[slow_tick]`: phase breakdown when a tick exceeds the slow threshold. Useful
  fields include NPC apply, view build, output flush, and world maintenance.
- `TickSleepMode`: controls main-loop yield strategy.

## Manual Benchmark Recipe

1. Start a staging shard with representative scripts and map data.
2. Set `AccApp=0` and use test accounts or bot-only load.
3. Spawn a fixed bot count and duration, for example 50 bots for 60 seconds.
4. Capture server logs from startup until bots disconnect.
5. Record:
   - average tick from `[tick_stats]`
   - max tick from `[tick_stats]`
   - count and worst phase from `[slow_tick]`
   - packet send/receive rate
6. Repeat the same run after code changes.

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
