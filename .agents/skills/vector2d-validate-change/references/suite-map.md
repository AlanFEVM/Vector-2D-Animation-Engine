# Validation Suite Map

## Minimum Matrix

| Changed area | Required suite |
| --- | --- |
| Any `native/` C# | `Build` |
| Timeline, keyframes, cel ownership, snapshots, project instances, composition semantics | `Timeline` |
| Pressure sampling, pressure smoothing, `BrushShape`, pressure brush commit | `Pressure` |
| Freehand, topology, hit testing, fill/line merging, marquee materialization, render order | `Freehand` |
| Stress generation, spatial index, parallel batching, composition batching/performance | `Stress` |
| `StageControl`, Direct2D, LOD, underlay, renderer caches, hot-reload routing | `Render` |
| `launcher/` only | `Launcher` |
| User-visible panel/control layout | `Build` plus UI screenshot/manual check |
| User-visible Stage visual or interaction | relevant functional suite plus real-HWND/manual check |

The script automatically performs one native Release build before benchmark suites unless `-NoBuild` is set.

## Native Entry Points

| Script suite | Native argument | Main coverage |
| --- | --- | --- |
| `Timeline` | `--bench-timeline` | fixed-step scheduling, exposures, frame commands, snapshots, track sync, cel ownership, project model, instances, composition |
| `Pressure` | `--bench-pressure` | pressure brush geometry and continuity |
| `Freehand` | `--bench-freehand` | freehand sampling/commit, render order, topology/materialization/merging |
| `Stress` | `--bench` | deterministic stress build, spatial index, render collection, parallel order, composition performance |
| `Render` | `--bench-render` | shared geometry/brush checks, hot-reload routing, Direct2D underlay, LOD and cache behavior |

Run suites sequentially. `Stress` and `Render` measurements are not clean while the GUI, launcher, debugger, or other benchmarks are consuming CPU/GPU.

## Failure Interpretation

1. Record the command, exit code, and first `InvalidOperationException` or build error.
2. Locate the failing regression name in `native/App/Benchmark.cs`.
3. Check whether the regression is shared by more than one CLI mode.
4. If the worktree was already dirty, reproduce against the pre-change state when feasible; otherwise clearly mark the baseline uncertainty.
5. Never waive a failure only because it occurred outside the nominal feature area.

With `-EnforcePerformanceBudget`, the script treats any output line ending in `_budget_met=false` as failure even when the process exits zero.
