---
name: vector2d-validate-change
description: Select and run Vector 2D Animation Engine build, regression benchmark, performance-budget, visual/manual, launcher, hot-reload, diagnostics, and release validation. Use after changes under native or launcher, before declaring a feature or fix complete, when choosing the minimum relevant benchmark suite, or when diagnosing startup, reload, crash, Direct2D, or packaging behavior.
---

# Vector2D Change Validation

Use the smallest suite that covers the changed contract, then broaden when the blast radius crosses model, geometry, UI, or rendering boundaries.

## Workflow

1. Inspect `git status --short` and the relevant diff. Do not attribute pre-existing dirty-worktree failures to the current change without a baseline comparison.
2. Read [suite-map.md](references/suite-map.md) and select all affected suites.
3. In multi-Agent work, nominate one validation owner. Other Agents submit suggested suites or run `-Plan`; only the owner runs builds and benchmarks, serially.
4. The validation owner runs `scripts/invoke-validation.ps1`; benchmarks are sequential because performance and graphics suites interfere with each other.
5. For user-visible UI, also use `$vector2d-winforms-ui` or `$vector2d-stage-workflow` visual verification and manually exercise the changed interaction.
6. Read [launcher-release.md](references/launcher-release.md) for development launch, hot reload, logs, or split-package publishing.
7. Update `docs/USER_GUIDE.md` for user-facing feature or interaction changes. Keep `README.md` commands/features accurate.
8. Report the exact failed suite and first meaningful exception/output; note when a shared prerequisite regression prevented the named suite from reaching its target checks.

## Common Commands

```powershell
# Native Release build only
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Build

# Resolve suites, builds, and benchmarks without locking or executing them
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Freehand,Render -Plan

# Model/timeline change
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Timeline

# Geometry plus renderer change
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Freehand,Render

# Clean performance interpretation; close the GUI/launcher first
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Stress,Render -EnforcePerformanceBudget
```

Use `-Restore` only on a fresh checkout, after package changes, or when `project.assets.json` is missing. Use `-NoBuild` only after an unchanged successful Release build.

Non-plan runs take a repository-specific Windows cross-process mutex. The default `-LockTimeoutSeconds 0` fails immediately when another validation owner is active; set a bounded positive timeout only when waiting is intentional. `-Suite All` includes both native suites and `Launcher`.

## Validation Rules

- The repository has no solution, test project, or CI pipeline. The `native/App/Benchmark*.cs` partial family supplies the CLI regression harness.
- `global.json` pins the stable .NET 8 line. Do not validate with a preview SDK selected outside repository rules.
- Benchmark mode is determined by the first native argument.
- Close running GUI/launcher processes before treating performance numbers as a baseline.
- Performance budget booleans are printed output; the native process may still exit zero. Use `-EnforcePerformanceBudget` when budgets are acceptance criteria.
- A benchmark can execute shared regressions before its named target. Read the exception and call chain rather than assuming `--bench-render` reached Direct2D.
- `-Plan` is read-only: it resolves `Suites`, `Builds`, and `Benchmarks` as JSON without taking the validation mutex, restoring, building, or running a benchmark.
- Keep generated launchers, runtimes, packages, and validation artifacts out of the tracked source tree.
- Do not commit unrelated dirty-worktree files. Stage only files belonging to the completed request.
