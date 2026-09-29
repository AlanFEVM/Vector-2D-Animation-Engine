# Vector 2D Animation Engine Agent Guide

## 工作方式（Astra / 当前会话）

- 主 Agent 负责理解目标、选择实现、完成修改与验证，并对最终结果负责。小改动和紧密耦合任务直接闭环；只有存在可独立推进的工作包时才委派，避免把每次搜索、编辑或命令执行拆成子任务。
- 使用当前会话提供的模型和工具能力。子 Agent 默认继承主 Agent，不固定模型名称或推理档位，不探测或修改提供方、认证、模型目录与全局配置。用户明确指定模型时，以实际可用能力为准；不可用则说明并继续可独立完成的工作。
- 实现请求推进到可审阅的修改和适度验证；分析、诊断或评审请求先给出证据，不擅自扩展成产品修改。任务范围内可逆的常规步骤直接执行，只有缺失决定性信息、无法避开他人修改或需要新增授权时才询问。
- 默认用中文沟通，保留代码标识符与命令原文。进度说明聚焦发现、决定和待验证点；最终说明改了什么、验证结果及实际限制。

## 启动与证据

1. 首先运行 `git status --short --branch`，记录已修改和未跟踪文件。未知改动视为用户或其他 Agent 所有，不回退、覆盖、暂存或归功于本任务。存在基线改动不代表必须停止；在不冲突的范围继续。
2. 按下表选择最小 owning skill，完整读取其 `SKILL.md`，再按行为读取所需引用。仅当跨越行为契约时组合技能，不因相邻目录而扩大加载范围。
3. 用 `rg` / `rg --files` 和定向读取定位入口、调用方与回归覆盖。独立只读检查可批量并行；有依赖的修改顺序执行。知识图谱仅作导航，结论以当前源码为准；不要为普通定位重建图谱或改写其缓存。
4. 编辑前读取目标文件及相关差异。已有改动若把代码提取到未跟踪 partial，必须同时核对新文件，不能仅凭 `git diff --stat` 判断功能被删除。
5. 多步任务维护简短计划和验收条件；局部修正无需任务包仪式。只读取实际存在且适用的说明文件，不依赖未提供的 `1.md` 等历史约定。

## 当前产品与开发重点

以下是源码导航和应保护的契约，不是发布验收声明。相关任务开始时核对当前文件；未提交实现、文档描述、测试通过和正式发布是不同状态。

- Windows 原生桌面：`native/UI/` 为 WinForms 工作台，`native/Engine/` 为几何、项目、时间轴与存储，`native/Rendering/` 为 Stage 和渲染，`native/App/` 为生命周期、集成与回归入口。SDK 以 `global.json` 为准，版本以 `Directory.Build.props` 为准，避免在此复制易过期版本号。
- 已有 Basic Drawing 与 Scene Building 工作区，包含 Symbol/嵌套实例、Cel/曝光/补间、Vault/SVG 和项目目录存储。沿现有命令、撤销与组合流程扩展，保护稳定 ID、引用来源、图层顺序、Auto Key/held exposure 和旧文档兼容性。
- 当前源码包含 Reference 3D 光学、GPU 光学/透视投影、后台准备与工作区预渲染。入口包括 `Direct2DStageRenderer.Reference3D*`、`Direct2DStageRenderer.Gpu*`、`StageControl.Reference3D*` 和 `*WorkspacePreRender*`。优化须保留近裁剪、真实材质边界、遮罩、深度、线性颜色与预乘 Alpha，同时核对 GPU、Direct2D 与 GDI 回退路径。
- 缓存与并行修改须核对工程/帧/相机/尺寸/灯光/内容变化的失效、设备重建与释放、后台和前台共享 SVG 资源的生命周期。不要以跳过内容、改变最终几何、静默降质或放宽预算冒充性能修复；已有预览策略按各域契约处理。
- 当前源码包含本地 Codex / MCP 编辑器接口与动画包导入。入口为 `native/App/CodexBridge*`、`AnimationBundleImporter.cs`、`native/UI/MainForm.Codex*` 和 `CodexIntegrationPanel.cs`。区分服务启用与编辑写权限，保护 UI 线程、忙碌状态、参数校验、撤销与原有文档操作边界；具体工具按当前协议发现，勿在此复制工具清单。
- 产品行为与限制见 [用户指南](docs/USER_GUIDE.md)，架构入口见 [README](README.md)，正式包历史见 [文档索引](docs/README.md)。文档与代码不一致时核对实现和测试，明确差异，不把规模目标或基准夹具当成已达成的性能。

## Skill Routing

| 请求行为 | Owning skill |
| --- | --- |
| Packed geometry、拓扑、命中/空间查询、布尔、变换/畸变 | `$vector2d-drawing-geometry` |
| Brush、Pressure、Mixing Brush、painted regions、笔刷擦除 | `$vector2d-brush-paint` |
| 项目图、Symbol/实例、图层/遮罩、组合与 provenance | `$vector2d-project-model` |
| 帧、Cel、曝光、关键帧、Auto Key、补间、洋葱皮、播放语义 | `$vector2d-timeline-animation` |
| Save/Open、Vault、标签/目录、SVG 导入导出/Break Apart、持久化 | `$vector2d-assets-persistence` |
| Stage 工具、输入/选择/拖动、覆盖层、2D 渲染与缓存 | `$vector2d-stage-workflow` |
| Reference 3D、空间变换、相机、场景遮罩、光学与透视投影 | `$vector2d-scene-spatial` |
| 非 Stage 的 WinForms 面板、控件、主题、可访问性与绑定 | `$vector2d-winforms-ui` |
| 启动、开发 launcher、hot reload、重启、单实例、诊断 | `$vector2d-app-lifecycle` |
| 版本、双语发布说明、Release Manager、正式单 EXE 打包 | `$vector2d-release-workflow` |
| 多 Agent 分工、所有权、交接和集成 | `$vector2d-coordinate-agents` |
| 构建、回归、性能、视觉/手工、启动和发布验收 | `$vector2d-validate-change` |

MCP 不是独立业务模型：按工具实际修改的项目、时间轴、空间或存储行为选技能；仅涉及面板时用 UI 技能，涉及服务随应用启停时用生命周期技能。动画包导入按存储及实际跨越的项目/时间轴契约处理。

## 多 Agent 协作

采用 `$vector2d-coordinate-agents` 的 [work-packages.md](.agents/skills/vector2d-coordinate-agents/references/work-packages.md)。根 Agent 是协调者和最终集成负责人；默认共享当前工作树，不主动创建分支/worktree 或转移补丁。

- 用户要求多 Agent 或有至少两个独立就绪工作流时评估拆分。适合并行的是只读探索、独立评审与不重叠实现；依赖未确定接口、同文件或同一串行步骤的工作保持单 Agent。根 Agent 保留可并行推进的实际工作，不为单纯等待而委派。
- 子 Agent 默认继承当前模型；并发不超过会话上限，通常最多三个子 Agent，且不超过独立任务数。子 Agent 不再创建或协调下级 Agent。
- 委派必须给出目标、独占写路径、只读依赖、前置条件、必须保留的行为、禁止范围、交付物和建议验证。只读任务不得改文件或生成缓存。
- 每个文件同一时间只有一个写入者。`Benchmark*`、`MainForm*`、`VectorScene*`、`StageControl*`、`Direct2DStageRenderer*` 的核心文件，项目文件、公共文档、发布源/脚本/产物由集成负责人独占；仅在调用契约稳定时分配互不重叠的 partial 文件。
- 子 Agent 编辑前和交接前刷新 `git status --short`。发现范围重叠先报告并停止该文件写入；不得撤销他人工作。未经明确授权不执行暂存、提交、分支切换、合并、变基或推送。
- 构建、回归、基准、视觉捕获、launcher 生成和发布只有一位验证负责人执行，其他 Agent 仅建议套件。UI/GPU 和同一运行实例也属于独占资源。
- 交接须包含完成状态、文件、行为或证据位置、已执行命令及结果、未验证项和集成需求。根 Agent 审阅实际 diff 与调用方后验收，不能只接受摘要。
- 子任务不完整时进行聚焦修正；重复失败应重新检查根因、拆分或接管，不机械重试。结束前每个必要工作包须完成、明确取消或说明具体阻塞。

## 验证门槛

- 产品、launcher、Release Manager 或发布修改使用 `$vector2d-validate-change` 选择最小充分套件。现有回归位于 `native/App/Benchmark*.cs`；不要假设存在独立测试工程或 CI。
- 先用 `.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite <suite> -Plan` 检查计划，再由单一验证负责人运行。`-Plan` 不构建、不获取验证锁，也不是测试通过。
- 根据 [benchmark-map.md](.agents/skills/vector2d-validate-change/references/benchmark-map.md) 和当前 `Program.cs` 核对真实入口与调用覆盖。新增回归接入已有 suite；新增 CLI 模式同步检查验证脚本、suite map 与 workflow 检查。不要仅凭 `Benchmark.*.cs` 文件存在声称用例已运行。
- 性能与渲染验收串行执行，记录夹具、实际可见内容、视图/分辨率、渲染设备/回退、预热和缓存条件。按现有用例检查平均值/P95/最大值等指标；机器有竞争负载时明确性能结论的限制。`Stress` 与 `Render` 不并发。
- 修复优先验证可观察失败行为及受影响契约。相关检查通过后，不无理由重复全套验证；纯说明文档变更无需产品构建。视觉与交互未实际检查时明确标注，不以编译成功替代。
- 修改 `AGENTS.md` 或 `.agents/` 后运行 `.agents\skills\vector2d-coordinate-agents\scripts\test-agent-workflow.ps1` 和 `git diff --check`。失败时区分本次引入与原有工作树问题，不顺手修复无关基线。
- `bin/`、`obj/`、运行时、验证输出、截图、发布产物以及任务无关的 graphify 缓存不得混入提交，除非用户明确要求。

## 发布边界

- 根目录 `VectorAnimationEngine.exe` 专用于本地开发，由 `scripts\publish-development-launcher.ps1` 生成，默认启动带模块热重载的源码 `dotnet watch`。绝不以正式发布 EXE 覆盖、复制或同步到此处。
- 每次正式发布使用 `scripts\publish-single-exe.ps1`，输出仅放在 `artifacts\release` 或用户明确指定的空发布目录。产物必须是一个小于 5 MiB 的可替换 EXE，不附带 ZIP、校验侧文件或展开的 `.V2DEngine` / `.Runtime`。
- EXE 内嵌完整压缩 `.V2DEngine`；替换 EXE 是更新边界，下次启动由 bootstrap 原子刷新外部 `.V2DEngine`。
- `.Runtime` 保持外部持久化。缺失或无效时由 bootstrap 获取兼容的 Microsoft .NET 8 Core 与 Windows Desktop x64 runtime；不把开发 SDK 或本机运行时误当成发布依赖已满足。
- 普通功能修复不自动生成 launcher、修改版本或发布；按用户请求和 release skill 完成相应流程。

## 完成交付

- 重读本任务 aggregate diff，连同相关未跟踪文件核对所有权、调用链、跨域更新与无关改动。未授权不暂存或提交。
- 用户可见功能或交互变化同步更新 `docs/USER_GUIDE.md`；仅 Agent 指南调整无需更改产品说明。新增行为遵循现有中英文本地化约定。
- 报告修改文件、实际行为、准确验证命令与通过/失败/未执行结果。说明脏工作树的基线不确定性及未完成验收，不将他人修改、源码存在或计划输出表述为本任务已验证成果。
