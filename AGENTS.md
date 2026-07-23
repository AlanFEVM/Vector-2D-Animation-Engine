# Vector 2D Animation Engine Agent Guide

## Bootstrap

- Start with `git status --short --branch` and record pre-existing changes. Treat unknown changes as user or other-Agent work; never revert, overwrite, stage, or attribute them to the current task.
- Route implementation through the matching project skill: `$vector2d-drawing-geometry`, `$vector2d-project-model`, `$vector2d-stage-workflow`, or `$vector2d-winforms-ui`.
- Use `$vector2d-coordinate-agents` when the user requests multiple Agents or when a change has at least two independent workstreams. Keep a small or tightly coupled change with one Agent.
- Use `$vector2d-validate-change` after changes under `native/` or `launcher/`, and before declaring a product change complete.

## Coordination Contract

The root Agent is the coordinator and integration owner. All Agents share this worktree, so coordination is based on explicit write ownership rather than branches or patch transfer.

1. Build a dependency graph before delegating. Run only work packages that are independent and ready.
2. Give each sub-Agent a bounded packet containing the goal, owned paths or symbols, read-only dependencies, prerequisites, required invariants, deliverable, and suggested validation.
3. Assign exactly one writer to each file at a time. An Agent may read outside its owned paths but must not edit there.
4. Reserve shared hotspots for one integration owner: `native/App/Benchmark.cs`, `native/UI/MainForm.cs`, `README.md`, `docs/USER_GUIDE.md`, project files, and release artifacts. If a domain Agent owns one of these, no other Agent may edit it concurrently.
5. Prefer parallel read-only reconnaissance, independent implementation in disjoint files, and independent review. Serialize dependent model/UI work, edits to the same file, final documentation, builds, benchmarks, visual capture, publishing, and root EXE replacement.
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

- Use `scripts\publish-split-package.ps1` for normal releases. Its default mode must produce only the replaceable `.V2DEngine` patch ZIP and checksum; do not rebuild or upload `.Runtime` or the root launcher.
- Use `scripts\publish-split-package.ps1 -FullPackage` only when the target framework, private runtime, root launcher, or package layout changes. Full mode also produces the matching patch artifact.
- Treat the complete `.V2DEngine` directory as the patch boundary. Never publish a merge-style subset of individual application files.

## Completion

- Re-read the aggregate diff for ownership leaks, incomplete cross-domain updates, and unrelated files.
- Confirm every work package is `done`, deliberately cancelled, or reported as unresolved; do not silently drop blocked work.
- Update `docs/USER_GUIDE.md` in the same change for user-facing features or interaction changes.
- Report exact validation commands and outcomes, plus any baseline uncertainty from the pre-existing worktree.
