---
name: vector2d-validate-change
description: Select and run Vector 2D Animation Engine build, CLI regression, performance-budget, visual/manual, development-launcher, Release Manager, and formal packaging validation. Use after product, launcher, release-manager, distribution, or publishing changes; before declaring a feature/fix complete; or when diagnosing build, startup, hot reload, Direct2D, performance, or release failures. This skill validates changes and does not own their implementation.
---

# Vector2D Change Validation

Use the smallest suite set that covers every changed contract, then broaden when failures or cross-domain behavior require it.

## Route Validation

- Read [suite-map.md](references/suite-map.md) to map changed behavior to suites.
- Read [benchmark-map.md](references/benchmark-map.md) when adding/moving regressions or interpreting which CLI entry point reaches a partial.
- Read [launcher-release.md](references/launcher-release.md) only for development-launcher, lifecycle, Release Manager, bootstrap, or formal-package acceptance.

## Workflow

1. Inspect `git status --short --branch`, tracked diff, and untracked files. Treat extraction into untracked partials as a move until proven otherwise.
2. Select every suite whose contract changed; use `-Plan` to inspect resolution without locking, restore, build, or execution.
3. In multi-Agent work, assign one validation owner. Other Agents recommend suites or run `-Plan` only.
4. Run non-plan suites serially through `scripts/invoke-validation.ps1`; `Stress`, `Render`, and `Release` require uncontended exclusive resources.
5. Perform the visual/manual checks named by the owning UI/Stage/spatial skill for visible changes.
6. Report the exact command, exit code, first meaningful failure, and whether a dirty baseline creates uncertainty.

## Commands

```powershell
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Build
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Timeline,Render -Plan
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Freehand,Render
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Stress -EnforcePerformanceBudget
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Launcher
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Release
```

## Rules

- The repository has no solution, test project, or CI pipeline; `Benchmark.*.cs` is the native CLI regression harness.
- Use `-Restore` only after dependency/project changes or missing assets, and `-NoBuild` only after an unchanged successful Release build.
- Performance budget booleans require `-EnforcePerformanceBudget` when they are acceptance criteria.
- Shared regressions may fail before the named suite reaches its nominal target; follow the exception and call chain.
- Keep generated launchers, runtimes, packages, screenshots, and validation artifacts out of the final diff.
