# Multi-Agent Work Packages

## Domain Routing

| Behavior | Owning skill | Typical ownership | Suggested validation |
| --- | --- | --- | --- |
| Packed geometry, topology, Boolean, hit/query, transforms | `$vector2d-drawing-geometry` | focused `VectorScene.*.cs` geometry partials | `Build`, `Freehand`; add `Render` for output |
| Brush, pressure, mixing paint, brush erasing | `$vector2d-brush-paint` | brush/paint Engine modules plus assigned tool/render partials | `Pressure` and/or `Freehand`; add `Render` for raster changes |
| Project graph, instances, layers, masks, composition | `$vector2d-project-model` | definitions and `SceneCompositionBuilder.cs` | `Build`, `Timeline`; add `Stress` for batching |
| Timeline, Cels, keys, tween, playback | `$vector2d-timeline-animation` | timeline/tween Engine and focused UI partials | `Build`, `Timeline`; add `Render` for Stage feedback |
| Save/Open, Vault, tags/folders, SVG | `$vector2d-assets-persistence` | `ProjectVaultStore.cs`, asset/SVG modules and assigned panels | `Build`, `Timeline`; add `Freehand`/`Render` as applicable |
| Stage tools, pointer state, overlays, 2D rendering | `$vector2d-stage-workflow` | focused `MainForm.*`, `StageControl.*`, renderer partials | `Build`, `Freehand` and/or `Render` |
| Scene 3D references, cameras, masks, spatial transforms | `$vector2d-scene-spatial` | spatial model/Stage/UI partials | `Build`, `Timeline`, `Render` |
| Non-Stage panels, controls, theme, accessibility | `$vector2d-winforms-ui` | disjoint controls under `native/UI/` | `Build` plus visual/manual verification |
| Startup, source launcher, hot reload, restart, diagnostics | `$vector2d-app-lifecycle` | `launcher/`, assigned `native/App/` files | `Build`, `Launcher`; add affected suites |
| Versioning, release notes, Release Manager, packaging | `$vector2d-release-workflow` | release sources, manager, scripts, distribution bootstrap | `Release` plus affected product suites |
| Cross-domain planning and integration | `$vector2d-coordinate-agents` | root workflow and shared hotspots | aggregate minimum suite set |
| Validation selection and execution | `$vector2d-validate-change` | validation scripts/artifacts only | selected suites, serialized |

Choose by behavior, not directory alone. A vertical feature may need a primary skill plus one neighboring contract; for example, a spatial mask model/render change uses project model, scene spatial, and final validation, while a spatial-panel layout-only change uses scene spatial plus WinForms UI.

## Task Packet

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

Use path ownership as the enforceable boundary. Symbol ownership is acceptable only when the coordinator deliberately assigns one Agent as the sole writer for the containing file.

## State And Dependencies

Use `pending -> ready -> in_progress -> review -> done`. Use `blocked` only with a concrete missing input and `cancelled` only when the coordinator deliberately removes the package.

- Mark a package `ready` only after dependencies are done or its input contract is frozen.
- Assign at most one Agent to an `in_progress` package.
- Move rework from `review` back to `in_progress` with the same owner unless ownership is explicitly transferred.
- Do not finish while required work remains pending, ready, in progress, in review, or silently blocked.

## Shared Hotspots

- Core `Benchmark.cs`, `MainForm.cs`, `VectorScene.cs`, `StageControl.cs`, and `Direct2DStageRenderer.cs` remain single-owner integration files. Focused partials may have separate writers only when files and callers are disjoint.
- `README.md` and `docs/USER_GUIDE.md` update once behavior and terminology stabilize.
- `Directory.Build.props`, `release/release-notes.json`, `scripts/publish-single-exe.ps1`, project files, and release-manager shared sources are serialized release hotspots.
- Root/runtime binaries, `bin`, `obj`, validation output, screenshots, GPU/GUI resources, publishing, builds, and benchmarks are exclusive resources.
- A dirty diff that extracts code into untracked partials must be reviewed with both tracked and untracked file lists; `git diff --stat` alone can misreport extraction as deletion.

## Handoff

```text
Status: done | blocked | cancelled
Changed: <paths, or none for read-only work>
Result: <behavior implemented or findings>
Validation: <commands and outcomes, or recommended suites not run>
Unresolved: <risks, assumptions, missing inputs>
Integration: <callers, contracts, docs, tests, or shared files still needed>
Workspace: <unexpected overlapping changes observed>
```

The coordinator reviews the actual diff, checks call sites and ownership boundaries, integrates shared files, and assigns one final validation owner.
