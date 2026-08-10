---
name: vector2d-coordinate-agents
description: Coordinate multiple Agents in the shared Vector 2D Animation Engine worktree through dependency ordering, domain-skill routing, exclusive file ownership, handoffs, integration, and serialized validation. Use only when the user requests multiple Agents/parallel work or at least two independent ready workstreams can proceed without overlapping writes. Do not use for a small single-file or tightly coupled change.
---

# Vector2D Agent Coordination

Act as the coordinator. All Agents share the current worktree; do not design the workflow around branches, cherry-picks, or isolated copies.

## Decide Whether To Split

Use multiple Agents only when at least two ready work packages can make progress without writing the same file or waiting on each other's unresolved contract. Good candidates are parallel reconnaissance, disjoint domain implementation, independent review, and test selection.

Keep one Agent when the change is local, the next step depends on the preceding result, or all useful work converges on `MainForm.cs`, `Benchmark.cs`, `VectorScene.cs`, or the same documentation.

## Workflow

1. Capture `git status --short --branch` and separate user-owned baseline changes from task changes.
2. Read [work-packages.md](references/work-packages.md), select the smallest owning skills, and identify shared hotspots.
3. Define a dependency graph. Mark a package `ready` only when its inputs and behavioral contract are stable.
4. Assign one writer per file. Give every Agent a packet with owned paths or symbols, read-only dependencies, invariants, exclusions, deliverable, and suggested validation.
5. Delegate independent packets and retain a useful integration task locally. Do not create more Agents than there is independent work.
6. Track `pending`, `ready`, `in_progress`, `review`, `done`, `blocked`, and `cancelled` explicitly. Reassign work only after the prior writer has stopped and handed off.
7. On every handoff, inspect the shared worktree and the relevant diff. Resolve cross-domain contracts centrally; never ask one Agent to undo another Agent's unrelated changes.
8. Let one integration owner edit shared regression dispatchers, coordinator files, documentation, project files, release sources, and publishing scripts after domain behavior stabilizes.
9. For Agent configuration changes, run `scripts/test-agent-workflow.ps1` from this skill directory.
10. Ask `$vector2d-validate-change` for the minimum suite set. Use its `-Plan` mode during planning, then let exactly one validation owner run builds and suites after integration.
11. Review the aggregate diff and report completed, cancelled, and unresolved packages with exact validation results.

## Ownership Rules

- Read access may overlap; write ownership may not.
- Core files in `Benchmark*`, `MainForm*`, `VectorScene*`, `StageControl*`, and `Direct2DStageRenderer*`; `README.md`; `docs/USER_GUIDE.md`; project files; release sources; publishing scripts; and generated artifacts require an explicit single owner.
- Sub-Agents must not stage, commit, reset, cherry-pick, create worktrees, or replace root/runtime binaries unless their packet explicitly assigns that action.
- Builds, benchmarks, visual capture, and publishing are exclusive resources. Implementation Agents normally recommend suites; the validation owner executes them serially.
- A coordinator must verify files and tests directly. A handoff summary is evidence, not automatic acceptance.

## Commands

```powershell
git status --short --branch
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Build,Timeline -Plan
.agents\skills\vector2d-coordinate-agents\scripts\test-agent-workflow.ps1
git diff --check
```
