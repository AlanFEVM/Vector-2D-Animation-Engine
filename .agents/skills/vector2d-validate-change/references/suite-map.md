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
| `launcher/`, `distribution-launcher/`, runtime bootstrap, single-EXE packaging | `Launcher` |
| User-visible panel/control layout | `Build` plus UI screenshot/manual check |
| User-visible Stage visual or interaction | relevant functional suite plus real-HWND/manual check |

The script automatically performs one native Release build before benchmark suites unless `-NoBuild` is set.

## Multi-Agent Coordination

Assign one validation owner for a shared worktree. Each implementation Agent reports its suggested suites to that owner and may use `invoke-validation.ps1 -Plan` to inspect the resolved `Suites`, `Builds`, and `Benchmarks`; it must not start a competing build or benchmark. The owner combines the suggestions and executes the resulting suites serially.

Non-plan invocations are guarded by a repository-specific Windows cross-process mutex. Lock contention fails immediately by default; `-LockTimeoutSeconds` allows a bounded wait when the validation owner deliberately queues work. `-Suite All` resolves to native `Build`, `Launcher`, and every benchmark suite.

## Native Entry Points

| Script suite | Native argument | Main coverage |
| --- | --- | --- |
| `Timeline` | `--bench-timeline` | fixed-step scheduling, exposures, frame commands, snapshots, track sync, cel ownership, project model, instances, composition |
| `Pressure` | `--bench-pressure` | pressure brush geometry and continuity |
| `Freehand` | `--bench-freehand` | freehand sampling/commit, render order, topology/materialization/merging |
| `Stress` | `--bench` | deterministic stress build, spatial index, render collection, parallel order, composition performance |
| `Render` | `--bench-render` | shared geometry/brush checks, hot-reload routing, Direct2D underlay, LOD and cache behavior |

`Launcher` builds the source hot-reload launcher, publishes a disposable single-EXE release, enforces the 5 MiB/one-file contract, and runs the bootstrap's embedded-payload, metadata, URL, hash-shape, and archive-path safety self-tests.

Run suites sequentially. `Stress` and `Render` measurements are not clean while the GUI, launcher, debugger, or other benchmarks are consuming CPU/GPU.

## Failure Interpretation

1. Record the command, exit code, and first `InvalidOperationException` or build error.
2. Locate the failing regression name in `native/App/Benchmark.cs`.
3. Check whether the regression is shared by more than one CLI mode.
4. If the worktree was already dirty, reproduce against the pre-change state when feasible; otherwise clearly mark the baseline uncertainty.
5. Never waive a failure only because it occurred outside the nominal feature area.

With `-EnforcePerformanceBudget`, the script treats any output line ending in `_budget_met=false` as failure even when the process exits zero.
