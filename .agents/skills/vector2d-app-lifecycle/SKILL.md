---
name: vector2d-app-lifecycle
description: Implement and diagnose Vector 2D Animation Engine application lifecycle behavior involving native startup, the development launcher, dotnet watch, module hot reload, editor restart state handoff, single-instance coordination, launcher shutdown signals, settings initialization, startup windows, crash logging, and process cleanup.
---

# Vector2D App Lifecycle

Treat launcher and native process behavior as one stateful protocol with bounded recovery and observable diagnostics.

## Route The Task

- Read [module-map.md](references/module-map.md) first to identify the launcher, native host, restart, hot-reload, or diagnostics owner.
- Read [lifecycle-contract.md](references/lifecycle-contract.md) for process, token, timeout, state handoff, logging, or fallback changes.
- Use `$vector2d-assets-persistence` for durable project Save/Open; editor restart state is temporary lifecycle state.
- Use `$vector2d-release-workflow` for the formal distribution bootstrap or release packaging.

## Workflow

1. Identify the process boundary, startup mode, arguments, mutex/event/token names, timeouts, and ownership of cleanup.
2. Validate every cross-process token and sidecar before consuming it.
3. Keep restart handoff two-phase: prepare state, start replacement, confirm readiness, then remove pending state.
4. Route hot reload by updated root types; request process restart for unsupported structural changes.
5. Preserve project, workspace, frame, selection, camera, and relevant UI state only when the receiving version validates it.
6. Add lifecycle regressions to `Benchmark.AppIntegration.cs` or the existing project-composition restart section.
7. Run `$vector2d-validate-change` with `Build,Launcher`; add `Render` for hot-reload rendering routes.
8. Update README/user troubleshooting when startup commands or log locations change.

## Invariants

- The repository-root EXE is the development launcher only.
- A second native instance must not replace or corrupt the first instance's state.
- Reject stale, malformed, oversized, or reparse-point restart sidecars.
- Hot reload callbacks marshal to the UI thread and preserve a usable app when a scoped refresh fails.
- Log failures before process exit and bound launcher restart loops.
