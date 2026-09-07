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


## Codex 多 Agent 委派规则

### 启用条件

- 本区块采用失败关闭策略。每个任务只在首次准备委派前检查一次当前会话的子 Agent 能力。
- 只有当前会话明确支持 `gpt-5.6-luna`，并且该模型明确支持 `reasoning_effort: max` 时，才启用本区块的全部规则。
- 若模型不存在、能力无法确认、`max` 不受支持，或首次创建子 Agent 返回模型不可用，则当前任务完全停用本区块；主 Agent 按本区块之外的原有项目规则直接工作。
- 停用后不得替换为其他模型；不得修改模型、提供方、认证或模型目录配置，也不得反复探测或重试。

### 总体职责

- 本区块只定义项目级委派行为，不修改或覆盖当前会话使用的主 Agent 模型、全局模型提供方、认证信息与模型目录。
- 简单对话、需求澄清、任务拆分、结果审核、整合与最终回复由主 Agent 直接处理。
- 涉及仓库探索、文件修改、命令执行、构建、测试或修复的项目工作，主 Agent 默认委派给子 Agent；仅当子 Agent 工具不可用或委派会阻塞任务时，主 Agent 才可直接处理并说明原因。
- 创建任何子 Agent 时，必须显式指定 `model: gpt-5.6-luna` 与 `reasoning_effort: max`。
- 同时运行的子 Agent 最多三个。子 Agent 不得继续创建下级 Agent，也不得直接协调其他子 Agent。

### 探索任务

- 只读搜索与分析项目结构、代码位置、调用关系和影响范围，不修改任何文件。
- 优先使用 `rg` / `rg --files` 和直接文件读取；不得执行会改变仓库、工程、编辑器或外部系统状态的命令。
- 返回关键结论、证据位置、相关文件与行号、未确认事项和建议的下一步，不把推测表述为事实。

### 执行任务

- 只处理主 Agent 明确分配的文件、模块和验收目标，使用最小且局部的改动完成任务。
- 修改前先读取目标文件并确认现有实现；保留用户及其他 Agent 的无关改动，不扩大范围，不顺手重构。
- 除非用户明确要求，不得切换分支、提交、合并、变基、推送或修改仓库外文件。
- 完成后执行与风险相匹配的验证，并如实说明通过、失败、未执行项及剩余风险。

### 并行与审核

- 仅在任务可以拆成互不依赖的文件或模块范围时并行；同一文件、共享接口或紧密耦合逻辑必须串行处理。
- 主 Agent 为每个子 Agent 指定独立范围、禁止触碰范围、集成约定和验收标准，并负责解决冲突与最终整合。
- 主 Agent 必须审阅子 Agent 报告和实际 diff，必要时执行统一验证，不得未经核对直接接受结果。
- 子任务失败或结果不完整时，最多追加两次聚焦修复；仍未解决则停止扩展修改并向用户说明阻塞。

### 委派模板

主 Agent 委派时使用以下结构，并按任务删减无关项：

```text
任务：<具体目标>

上下文：
- <相关背景、入口文件和已知约束>

范围：
- 允许：<可读取或修改的文件/模块>
- 禁止：<不得触碰的文件/模块>

约束：
- 保持改动最小且局部。
- 遵守 AGENTS.md、1.md 与相关项目 Skill。
- 不覆盖无关改动，不执行未经授权的 Git 操作。

验收标准：
- <可观察、可验证的完成条件>

返回中文结构化报告：
- 摘要
- 改动文件
- 执行命令
- 验证结果
- 阻塞项
- 剩余风险
```

<!-- END CODEX_MULTI_AGENT_RULES -->
