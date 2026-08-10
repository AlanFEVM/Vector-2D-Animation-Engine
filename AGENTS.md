# Vector 2D Animation Engine Agent Guide

## Bootstrap

- Start with `git status --short --branch` and record pre-existing changes. Treat unknown changes as user or other-Agent work; never revert, overwrite, stage, or attribute them to the current task.
- Select the smallest owning skill from the routing table below. Read that `SKILL.md` completely, then load only the references it names for the requested behavior. Combine skills only when the change crosses contracts.
- Use `$vector2d-coordinate-agents` when the user requests multiple Agents or when a change has at least two independent workstreams. Keep a small or tightly coupled change with one Agent.
- Use `$vector2d-validate-change` after product, launcher, release-manager, or publishing changes and before declaring a product change complete. Load it for validation planning/execution, not as the implementation owner.

## Skill Routing

| Request surface | Owning skill |
| --- | --- |
| Packed geometry, topology, hit/query, Boolean operations, transforms, distortions | `$vector2d-drawing-geometry` |
| Brush, pressure, Mixing Brush, painted regions, brush erasing | `$vector2d-brush-paint` |
| Project graph, scenes, symbols, instances, layers, masks, composition/provenance | `$vector2d-project-model` |
| Frames, Cels, exposures, keyframes, Auto Key, tween, onion skin, playback | `$vector2d-timeline-animation` |
| Project Save/Open, Vault, asset tags/folders, SVG import/export/Break Apart | `$vector2d-assets-persistence` |
| Stage tools, pointer/keyboard sessions, selection, overlays, 2D rendering/caches | `$vector2d-stage-workflow` |
| Scene reference 3D, cameras, spatial transforms, scene masks/reference projection | `$vector2d-scene-spatial` |
| Non-Stage WinForms panels, controls, layout, theme, accessibility, binding | `$vector2d-winforms-ui` |
| Native/source-launcher startup, hot reload, restart, single instance, diagnostics | `$vector2d-app-lifecycle` |
| Versioning, bilingual release notes, Release Manager, formal single-EXE publishing | `$vector2d-release-workflow` |
| Multi-Agent decomposition, ownership, handoff, integration | `$vector2d-coordinate-agents` |
| Suite selection, builds, benchmarks, visual/manual and release acceptance | `$vector2d-validate-change` |

Do not load a neighboring skill only because its directory appears in the diff. Load it when the requested behavior crosses that skill's contract. For example, a tween model change uses `$vector2d-timeline-animation`; add `$vector2d-stage-workflow` only if Stage presentation changes, and add `$vector2d-winforms-ui` only if a non-canvas control changes.

## Coordination Contract

The root Agent is the coordinator and integration owner. All Agents share this worktree, so coordination is based on explicit write ownership rather than branches or patch transfer.

1. Build a dependency graph before delegating. Run only work packages that are independent and ready.
2. Give each sub-Agent a bounded packet containing the goal, owned paths or symbols, read-only dependencies, prerequisites, required invariants, deliverable, and suggested validation.
3. Assign exactly one writer to each file at a time. An Agent may read outside its owned paths but must not edit there.
4. Reserve shared hotspots for one integration owner: core files in the `Benchmark*`, `MainForm*`, `VectorScene*`, `StageControl*`, and `Direct2DStageRenderer*` partial families; `README.md`; `docs/USER_GUIDE.md`; project files; release sources; publishing scripts; and release artifacts. Disjoint partial files may have separate writers only when their contracts and callers are stable.
5. Prefer parallel read-only reconnaissance, independent implementation in disjoint files, and independent review. Serialize dependent model/UI work, edits to the same file, final documentation, builds, benchmarks, visual capture, publishing, and release-launcher generation.
6. Sub-Agents must refresh `git status --short` before editing and before handoff. They must preserve changes outside their packet and report unexpected overlap to the coordinator.
7. Sub-Agents do not stage, commit, cherry-pick, reset, or create worktrees unless the coordinator explicitly delegates that operation. The coordinator owns the final diff and any requested commit.
8. A handoff is not complete without changed files, behavior or findings, validation performed, unresolved risks, and integration notes. The coordinator verifies the actual diff instead of relying only on the summary.

Read [work-packages.md](.agents/skills/vector2d-coordinate-agents/references/work-packages.md) for routing, task packet, state, and handoff templates.

## Validation Gate

- Only one validation owner may build or run regression, performance, graphics, launcher, or visual suites at a time. Other Agents report suggested suites without running them.
- Use `.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite <suite> -Plan` to inspect the resolved validation plan without taking the validation lock or building.
- The validation owner runs the selected suites after implementation handoffs are integrated. `Stress` and `Render` remain sequential and require an uncontended machine for performance interpretation.
- For changes under `.agents/`, run `.agents\skills\vector2d-coordinate-agents\scripts\test-agent-workflow.ps1` and `git diff --check`; product builds are not required unless product code also changed.
- Keep generated `bin/`, `obj/`, runtime, validation, screenshot, and release artifacts out of the final diff unless the request explicitly requires them.

## Release Packaging

- The repository-root `VectorAnimationEngine.exe` is exclusively the local development launcher. Generate it with `scripts\publish-development-launcher.ps1`; it must start source `dotnet watch` with module hot reload by default.
- Never copy, rename, or synchronize a formal release EXE onto the repository-root `VectorAnimationEngine.exe`. Formal release output belongs only in `artifacts\release` or an explicitly requested empty release directory.
- Use `scripts\publish-single-exe.ps1` for every release. It must produce exactly one replaceable EXE smaller than 5 MiB; do not publish ZIPs, checksum sidecars, expanded `.V2DEngine`, or `.Runtime`.
- The EXE embeds the complete compressed `.V2DEngine` payload. Replacing the EXE is the update boundary; the bootstrap atomically refreshes the external `.V2DEngine` directory on next launch.
- Keep `.Runtime` external and persistent. The bootstrap must acquire the compatible Microsoft .NET 8 Core and Windows Desktop x64 runtimes when the local runtime is absent or invalid.

## Completion

- Re-read the aggregate diff for ownership leaks, incomplete cross-domain updates, and unrelated files.
- Confirm every work package is `done`, deliberately cancelled, or reported as unresolved; do not silently drop blocked work.
- Update `docs/USER_GUIDE.md` in the same change for user-facing features or interaction changes.
- Report exact validation commands and outcomes, plus any baseline uncertainty from the pre-existing worktree.
