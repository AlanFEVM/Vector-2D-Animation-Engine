# Validation Suite Map

## Minimum Matrix

| Changed behavior | Required suite |
| --- | --- |
| Any `native/` C# | `Build` |
| Project graph, timeline, Cels, keyframes, tween, snapshots, instances, masks, persistence, composition | `Timeline` |
| Pressure sampling/smoothing or pressure brush commit | `Pressure` |
| Drawing tools, freehand, brush/mixing, topology, hit testing, Boolean/merge/materialize, Bezier geometry | `Freehand` |
| Stress generation, spatial index, deterministic parallel or composition batching/performance | `Stress` |
| Stage interaction, Direct2D/GDI, LOD, underlay, renderer caches, reference 3D/cameras, hot-reload render routing | `Render` |
| Explicit GPU optical shading, pixel comparison, swap-chain resize/presentation | `GpuOptics` plus affected `Render` checks |
| Focused large-selection frame-time and spike budgets | `SelectionPerf` (also reached by `Render`) |
| Realistic-scale (50–1,000 mixed objects with layer effects) pan/zoom/drag/playback frame-time data at 1080p and 4K | `RealisticInteraction` |
| Source launcher, dotnet watch, restart-token or development-launcher behavior | `Launcher` |
| `release-manager/`, release manifest/version sources, distribution bootstrap, or publishing scripts | `Release` |
| Non-Stage panel/control layout | affected build suite plus UI screenshot/manual matrix |
| Visible Stage/spatial behavior | functional suite plus real-HWND/manual check |

`Build` is implied before native benchmark suites unless `-NoBuild` is set. `Release` builds the Release Manager and runs disposable formal-package validation; it does not replace affected native functional suites.

## CLI Entry Points

| Suite | Native argument | Main coverage |
| --- | --- | --- |
| `Timeline` | `--bench-timeline` | project graph, assets/persistence, layers/Cels, frame commands, tween, composition |
| `Pressure` | `--bench-pressure` | pressure brush sampling and continuity |
| `Freehand` | `--bench-freehand` | tools, freehand/brush, Bezier, topology, fill/materialize/merge |
| `Stress` | `--bench` | large deterministic build, spatial queries, order, batching, composition budgets |
| `Render` | `--bench-render` | shared geometry, Stage interaction, hot reload, Direct2D/GDI, cache/LOD, reference views |
| `GpuOptics` | `--bench-gpu-optics` | hardware GPU/CPU optical comparison, alpha, resize/presentation; requires a hardware Direct3D device |
| `SelectionPerf` | `--bench-selection-perf` | focused large-selection frame-time and spike budgets |
| `RealisticInteraction` | `--bench-realistic-interaction` | diagnostic frame-time table for 50/200/500/1,000 objects at 1080p and 4K across pan, zoom, selection drag and playback |

`All` includes `GpuOptics`; unsupported hardware is a failure, not a skipped pass. `SelectionPerf` is available separately but is not repeated by `All`, because `Render` already calls it. `RealisticInteraction` is a measurement entry point and does not fail on an over-budget tier; it prints `budget_met` per action for comparison. `--bench-collision` remains an external-fixture manual probe.

`GpuOptics` rejects `VECTOR_BENCH_SCENE_PERFORMANCE=1`: that variable replaces the optical checks with a separate manual scene benchmark. The GPU colour delta is currently diagnostic; only its existing assertions are enforced. A successful suite does not establish a new colour-error tolerance.

See [benchmark-map.md](benchmark-map.md) for the partial-to-entrypoint map.

## Failure Interpretation

1. Record command, exit code, and first build error/exception.
2. Locate the failing method in the benchmark map and inspect its callers.
3. Check whether another entry point shares the same regression.
4. Compare with the pre-change dirty baseline when feasible; otherwise report uncertainty.
5. Never waive a failure only because it appears outside the nominal feature area.

With `-EnforcePerformanceBudget`, any output line ending in `_budget_met=false` (allowing trailing whitespace) fails validation even if the native process exits zero. Without the switch, these markers produce warnings and `PassedWithWarnings`, not a clean pass. Existing in-process budget assertions remain mandatory in either mode.

Every run emits a final JSON summary, including on failure, with per-step `Passed`, `PassedWithWarnings`, `Failed` or `NotRun`, elapsed time, benchmark exit code and failure/budget details. `-Plan` uses `Planned` and executes nothing. The default remains fail-fast; `-ContinueOnFailure` continues subsequent suites serially but still fails the overall run. Native build failure always stops the run to avoid testing stale binaries. Results are suite-level: a failing assertion can still prevent later cases within that suite from running.
