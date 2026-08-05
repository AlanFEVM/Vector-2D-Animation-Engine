# Multi-Agent Work Packages

## Domain Routing

| Area | Required skill | Typical ownership | Suggested validation |
| --- | --- | --- | --- |
| Packed drawing objects, topology, hit testing, fill/line operations | `$vector2d-drawing-geometry` | `native/Engine/VectorScene.cs` and closely related geometry types | `Build`, `Freehand`; add `Render` for visible output |
| Project, timeline, snapshots, instances, composition | `$vector2d-project-model` | project-model types under `native/Engine/` | `Build`, `Timeline`; add `Stress` for batching/performance |
| Canvas tools, pointer state, overlays, cameras, Direct2D/GDI | `$vector2d-stage-workflow` | `native/Rendering/` and assigned Stage coordination symbols | `Build`, `Freehand` and/or `Render` |
| Panels, inspectors, dialogs, controls, theme, accessibility | `$vector2d-winforms-ui` | disjoint controls under `native/UI/` | `Build` plus visual/manual verification |
| Launcher, validation, packaging, release | `$vector2d-validate-change` | `launcher/` or assigned validation/release files | `Launcher` or selected suites |
| Cross-domain planning and integration | `$vector2d-coordinate-agents` | root workflow, shared hotspots, final diff | Aggregate minimum suite set |

The `MainForm` partial family crosses Stage and WinForms boundaries. Route selection movement to `MainForm.SelectionDrag.cs`; keep general tool, UI, and workspace orchestration in `MainForm.cs`. Assign exactly one writer per partial file, and use a single integration owner when a change must touch both.

## Task Packet

Send this contract before an Agent starts implementation:

```text
Task: <stable id and short name>
Goal: <observable result>
Owns: <exact paths and, for shared files, symbols>
Read-only: <dependencies the Agent may inspect>
Depends on: <task ids or established contracts>
Must preserve: <domain invariants and user changes>
Deliverable: <code, findings, review, or test recommendation>
Suggested validation: <suite or focused check; coordinator runs exclusive suites>
Do not: <explicit exclusions, especially shared hotspots>
```

Use path ownership as the enforceable boundary. Symbol ownership is acceptable only when the coordinator has deliberately assigned one Agent as the sole writer for the containing file.

## State And Dependencies

Use `pending -> ready -> in_progress -> review -> done` for normal work. Use `blocked` only with a concrete missing input and `cancelled` only when the coordinator deliberately removes the package.

- A package is `ready` only after all dependencies are `done` or its input contract is frozen.
- At most one Agent owns an `in_progress` package.
- Rework moves `review` back to `in_progress` with the same owner unless ownership is explicitly transferred.
- The coordinator does not finish while a required package is `pending`, `ready`, `in_progress`, `review`, or silently blocked.

## Shared Hotspots

- `native/App/Benchmark.cs`: assign one regression owner. Focused `Benchmark.*.cs` partials may have separate owners only when their paths and callers are disjoint.
- `native/UI/MainForm.cs`: assign one integration owner because UI, Stage, undo, refresh, and shortcut behavior converge here. A focused `MainForm.*.cs` partial may be independently owned when the task does not also edit the core file.
- `native/Engine/VectorScene.cs`: keep one geometry writer when multiple operations touch packed arrays or index remapping.
- `README.md` and `docs/USER_GUIDE.md`: update once behavior and terminology are stable.
- `native/*.csproj`, `launcher/*.csproj`, `Directory.Build.props`, root/runtime binaries, and release artifacts: serialize and require explicit ownership.
- `native/bin`, `native/obj`, benchmark DLLs, GPU/GUI resources, and screenshot destinations: treat as exclusive validation resources, not implementation work packages.

## Handoff

Require a compact, evidence-based handoff:

```text
Status: done | blocked | cancelled
Changed: <paths, or none for read-only work>
Result: <behavior implemented or findings>
Validation: <commands and outcomes, or recommended suites not run>
Unresolved: <risks, assumptions, missing inputs>
Integration: <callers, contracts, docs, tests, or shared files still needed>
Workspace: <unexpected overlapping changes observed>
```

The coordinator then reviews the actual diff, checks call sites and ownership boundaries, integrates shared files, and runs final validation through one validation owner.
