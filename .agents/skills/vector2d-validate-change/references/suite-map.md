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

See [benchmark-map.md](benchmark-map.md) for the partial-to-entrypoint map.

## Failure Interpretation

1. Record command, exit code, and first build error/exception.
2. Locate the failing method in the benchmark map and inspect its callers.
3. Check whether another entry point shares the same regression.
4. Compare with the pre-change dirty baseline when feasible; otherwise report uncertainty.
5. Never waive a failure only because it appears outside the nominal feature area.

With `-EnforcePerformanceBudget`, any output line ending in `_budget_met=false` fails validation even if the native process exits zero.
