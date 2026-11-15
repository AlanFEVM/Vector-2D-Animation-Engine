# 面板体验问题修复台账 · 2026-11-10

本台账对应此前报告的 76 个编号，逐项记录已完成的代码修改。用户随后明确要求“回归验证”，本轮已实际执行：**Timeline 通过，Render 因大批量选中导航的性能门槛失败，尚不能认定全部验收通过。** 表格状态记录实现情况，具体覆盖与限制见末尾。本轮保留任务开始前的工作树改动，未暂存、提交或发布。

验证时应优先重现原操作：窄栏、长名称、多选混合值、滚动中悬停输入框、取消、失败重试和切换上下文。仅看控件出现或编译通过，不足以证明体验问题已解决。第 68、69 项已补实际排序和多色标编辑，并需验证模型、保存与 CPU/GPU 渲染；原先仅说明“固定顺序”“双色”的中间方案不计为修复结果。

| 编号 | 原体验问题 | 实现行为／验收关注点 | 主要源码位置 | 状态 |
| --- | --- | --- | --- | --- |
| 1 | 滚动面板时误改数值 | 数值框默认将滚轮转给所在 `ThemedScrollPanel`；键盘、步进、拖动保留 | `native/UI/ModernNumericUpDown.cs`：`MouseWheelChangesValue`、`TryAdjustFromMouseWheel` | 已实现，见验证记录 |
| 2 | 非法数字静默恢复，无法知道哪里错了 | 非法输入保留并显示可理解的错误提示 | `native/UI/ModernNumericUpDown.cs`：`CommitEditorText` | 已实现，见验证记录 |
| 3 | 越界数字悄悄截成边界值 | 提示允许范围，拒绝提交越界草稿 | `native/UI/ModernNumericUpDown.cs`：`CommitEditorText` | 已实现，见验证记录 |
| 4 | 数值编辑按 Esc 不能可靠取消 | 恢复进入编辑前的数字与混合值状态，清除输入错误 | `native/UI/ModernNumericUpDown.cs`：`HandleEditorKeyDown` | 已实现，见验证记录 |
| 5 | Tab 焦点进入屏幕外控件 | 焦点控件进入时沿父级滚动列定位可见位置 | `native/UI/ThemedScrollPanel.cs`：焦点可见性处理 | 已实现，见验证记录 |
| 6 | 小幅滚轮被吞、系统零行设置无效 | 累计小数滚动余量；尊重零行及整页滚动设置 | `native/UI/ThemedScrollPanel.cs`：`ScrollWheel` | 已实现，见验证记录 |
| 7 | 颜色内容可滚却没有滚动条 | 颜色页显示可见滚动条，提示还有下方内容 | `native/UI/MainForm.InspectorRail.cs`：颜色页滚动容器 | 已实现，见验证记录 |
| 8 | 重复点当前对象页就回到顶部 | 同页重复导航保留滚动位置 | `native/UI/MainForm.InspectorRail.cs`：分类激活 | 已实现，见验证记录 |
| 9 | 未实现的 Physics 入口像可用功能 | 移除未完成的物理导航入口，快捷键不跳到空页 | `native/UI/MainForm.InspectorRail.cs` | 已实现，见验证记录 |
| 10 | 相机重置悄悄修改镜头等无关参数 | 标为“重置相机变换”，仅重置位置和旋转，并说明保留项 | `native/UI/ShotDirectorPanel.cs`：相机重置操作 | 已实现，见验证记录 |
| 11 | 相机与其他数值框单位不清 | 共享数值控件显式显示相应单位 | `native/UI/ShotDirectorPanel.cs`、`CameraParallaxPanel.cs`、`ModernNumericUpDown.cs` | 已实现，见验证记录 |
| 12 | 相机统计只藏在标题悬停中 | 将统计显示在可见底部区域，同时保留完整悬停说明 | `native/UI/ShotDirectorPanel.cs`：统计文本与布局 | 已实现，见验证记录 |
| 13 | “保持大小”容易误解成全程锁定 | 提示明确保护深度调整的参考构图，推拉缩放仍可生效 | `native/UI/CameraParallaxPanel.cs`：保持大小说明 | 已实现，见验证记录 |
| 14 | 重叠图层深度线难以选中 | 重复点击循环命中项；左右键选择图层，并提供说明 | `native/UI/CameraDepthDiagram.cs`：命中循环与键盘选择 | 已实现，见验证记录 |
| 15 | 图层深度看起来是当前相机私有值 | 明示同场景全部相机共用图层深度 | `native/UI/CameraParallaxPanel.cs` | 已实现，见验证记录 |
| 16 | 深度受近裁剪限制却没有解释 | 常驻安全间距说明；触限时显示限制值及原因，固定提示区域避免跳动 | `native/UI/CameraParallaxPanel.cs`：深度限制反馈 | 已实现，见验证记录 |
| 17 | 相机／图层长名称只剩省略号 | 可悬停查看完整名称，保留紧凑行宽 | `native/UI/ShotDirectorPanel.cs`、`CameraParallaxPanel.cs`、`CameraDepthDiagram.cs` | 已实现，见验证记录 |
| 18 | 分类切换同步截两张整面板图，可能阻塞 | 改用轻量顶部提示条，避免两次 `DrawToBitmap`；实机确认切换延迟 | `native/UI/InspectorTransitionOverlay.cs`、`MainForm.InspectorAndWorkspace.cs` | 已实现，见验证记录 |
| 19 | 设置 Cancel 后主题仍被保存 | 主题及圆角为草稿预览；取消恢复已保存外观 | `native/UI/MainForm.cs`：`ShowSettings`；`MainForm.PanelSettings.cs` | 已实现，见验证记录 |
| 20 | 子面板 OK 后还要父窗口 Save，容易漏存 | 子面板改用 Apply，即时保存本类设置；明确父 Cancel 不撤销已应用内容 | `native/UI/SettingsDialog.cs`、`OperationPreferencesDialog.cs`、`ShortcutProfilesDialog.cs`、`SelectionPreferencesDialog.cs`、`MainForm.PanelSettings.cs` | 已实现，见验证记录 |
| 21 | 设置保存失败仍关闭，草稿丢失 | 关闭前调用保存回调；失败返回 false，错误提示后保留设置窗口 | `native/UI/SettingsDialog.cs`、`MainForm.PanelSettings.cs`：`CommitPanelSettings` | 已实现，见验证记录 |
| 22 | 主题滑条每次变化同步写盘 | 60ms 合并外观预览；只有保存动作持久化设置 | `native/UI/MainForm.cs`：`ShowSettings`、`ApplyThemePreview` | 已实现，见验证记录 |
| 23 | 对话框不能放大，小屏又超出工作区 | 现代无边框窗口可拖动边缘缩放，首次显示限制在当前屏幕工作区 | `native/UI/ModernDialogForm.cs`：`WndProc`、`OnShown` | 已实现，见验证记录 |
| 24 | 长错误内容被裁掉 | 消息正文使用可滚动多行只读文本框 | `native/UI/ModernDialogForm.cs`：`ModernMessageDialog` | 已实现，见验证记录 |
| 25 | 错误详情不能选中或复制 | 正文可选中，支持复制详情、Ctrl+A/C；复制不关闭窗口 | `native/UI/ModernDialogForm.cs`：`ModernMessageDialog` | 已实现，见验证记录 |
| 26 | 快捷键命令只能逐类翻找 | 添加按命令名／按键搜索；列表 Enter 编辑，长行可查看完整内容 | `native/UI/ShortcutProfileEditorPanel.cs` | 已实现，见验证记录 |
| 27 | 内置快捷键方案只读却没有下一步指引 | 常驻提示通过 New 创建可编辑副本 | `native/UI/ShortcutProfileEditorPanel.cs`：方案反馈 | 已实现，见验证记录 |
| 28 | 输入非法键后可能 Apply 上一次合法键 | 非法／保留键清空待应用 Gesture，并禁用 Apply | `native/UI/ShortcutProfileEditorPanel.cs`：`ShortcutCaptureDialog` | 已实现，见验证记录 |
| 29 | 列表滚动条点击轨道无反应，拖动丢捕获残留 | 点击轨道按页移动，丢失捕获结束拖动状态 | `native/UI/ShortcutProfileEditorPanel.cs`：`ThemedTableList` | 已实现，见验证记录 |
| 30 | MCP 状态把已生效服务与草稿连接混为一谈 | 标明“已生效的服务状态”，草稿改动另示待 Save；提供复制配置 | `native/UI/CodexIntegrationPanel.cs`：`RefreshFields` | 已实现，见验证记录 |
| 31 | 图片高级设置展开后滚回顶部 | 展开前保留滚动位置，布局后恢复 | `native/UI/ImageImportSettingsDialog.cs`：`SetAdvancedExpanded` | 已实现，见验证记录 |
| 32 | 裁切数值一改就解除固定比例 | 宽高按选定比例联动，位置修改保持裁切尺寸 | `native/UI/ImageCropDialog.cs`：`UpdateFromFields` | 已实现，见验证记录 |
| 33 | 裁切数值每改一次就重新 FitView | 数值变更保留当前缩放和平移，更新裁切框 | `native/UI/ImageCropDialog.cs`：`SetSelection` | 已实现，见验证记录 |
| 34 | 裁切数字不显示像素单位 | 裁切输入显式显示 px | `native/UI/ImageCropDialog.cs`：精确输入配置 | 已实现，见验证记录 |
| 35 | 画布设置数值框与面板输入不一致 | 尺寸、缩放、偏移及透明度统一共享数值控件，保留原精度、范围和单位 | `native/UI/SceneCanvasDialog.cs`：`NumberInput` | 已实现，见验证记录 |
| 36 | 选择背景大图同步解码卡窗口 | 后台加载，加载时禁用无效应用；取消或换图使旧结果失效并释放资源 | `native/UI/SceneCanvasDialog.cs`：`LoadSelectedImage`、`Dispose` | 已实现，见验证记录 |
| 37 | 场景列表不能重命名／删除 | 场景菜单和 F2/Delete 入口；删除确认范围，保留至少一个场景，支持撤销 | `native/UI/SceneEditorPanel.cs`、`MainForm.ScenePanelCommands.cs` | 已实现，见验证记录 |
| 38 | 实例列表单击与舞台选择不同步 | 单击选择放置实例，同步舞台与检查器 | `native/UI/SceneEditorPanel.cs`、`MainForm.ScenePanelCommands.cs`：`SelectInstanceFromScenePanel` | 已实现，见验证记录 |
| 39 | 双击实例误以为只编辑这一个放置 | 明示双击编辑共享源元件，单击选择放置实例 | `native/UI/SceneEditorPanel.cs`：实例操作说明 | 已实现，见验证记录 |
| 40 | “添加实例”不知道会引用哪个源 | 显示当前源元件；无源时提示先选素材 | `native/UI/SceneEditorPanel.cs`：`SetInstanceSource`；`MainForm.ScenePanelCommands.cs` | 已实现，见验证记录 |
| 41 | 新实例都堆在原点，又未自动选中 | 按当前视口中心添加，重叠时偏移，新增后选中 | `native/UI/MainForm.ScenePanelCommands.cs`：`AddSceneInstanceFromPanel` | 已实现，见验证记录 |
| 42 | 添加实例不能撤销 | 添加前保存实例和时间轴快照，接入已有撤销流程 | `native/UI/MainForm.ScenePanelCommands.cs`：`AddSceneInstanceFromPanel` | 已实现，见验证记录 |
| 43 | 切换场景清掉已有撤销历史 | 切换保存场景历史与帧位置；撤销按全局操作先后自动进入对应场景，重做保持全局队列，历史仍受数量／内存预算限制 | `native/UI/MainForm.ScenePanelCommands.cs`：`SaveScenePanelHistory`、`ActivateLatestScenePanelHistory`；`MainForm.DocumentRedo.cs` | 已实现，见验证记录 |
| 44 | 场景／实例短列表形成嵌套滚动陷阱 | 列表随行数展开，由外层面板统一滚动 | `native/UI/SceneEditorPanel.cs`：`InlineListBox`、布局 | 已实现，见验证记录 |
| 45 | 素材目录不能删除或删除范围不明 | 确认后只移除目录，其素材和子目录提升到父目录，不删除素材，支持一次撤销 | `native/UI/LibraryVaultPanel.cs`、`MainForm.VaultPanelCommands.cs`、`native/Engine/VectorProject.AssetFolders.cs` | 已实现，见验证记录 |
| 46 | 图片素材缺少重命名入口 | 图片、SVG 素材重命名事件接入稳定资源 ID 操作 | `native/UI/LibraryVaultPanel.cs`、`MainForm.VaultPanelCommands.cs` | 已实现，见验证记录 |
| 47 | 图片／SVG 与元件不能同样使用目录和标签 | 托管素材增加目录及标签元数据，参与搜索筛选、移动、持久化 | `native/UI/LibraryVaultPanel.Categories.cs`、`LibraryVaultPanel.Tags.cs`；`native/Engine/VectorProject.LibraryMetadata.cs`、`native/App/ProjectVaultStore.cs` | 已实现，见验证记录 |
| 48 | 右键已选素材会丢多选 | 右键已选行保留集合；右键其他行才调整目标 | `native/UI/LibraryVaultPanel.cs`：树选择处理 | 已实现，见验证记录 |
| 49 | 多选后菜单仍只处理一项 | 批量复制／删除／移动／标签形成一次撤销；导出按整批类型启用，选一次目录、不覆盖文件并汇总失败 | `native/UI/LibraryVaultPanel.cs`、`MainForm.VaultPanelCommands.cs`、`MainForm.PanelHistory.cs` | 已实现，见验证记录 |
| 50 | 素材列表 F2/Delete 没有直达操作 | 树列表聚焦时 F2 重命名、Delete 删除当前选择 | `native/UI/LibraryVaultPanel.cs`：`ProcessCmdKey` | 已实现，见验证记录 |
| 51 | 刷新总 EnsureVisible 导致浏览位置跳动 | 普通刷新恢复原顶部行，明确定位请求才滚到目标 | `native/UI/LibraryVaultPanel.cs`：`RefreshProjectObjects` | 已实现，见验证记录 |
| 52 | 素材刷新丢掉多选 | 通过稳定 ID 恢复多选集合、焦点行与展开目录 | `native/UI/LibraryVaultPanel.cs`：刷新与 `NodeIdentity` | 已实现，见验证记录 |
| 53 | 层级树达到数量上限后无入口查看剩余项 | 添加加载更多与搜索；程序定位超页对象时扩页；普通模式不为全量对象分配列表，无场景时不显示假对象 | `native/UI/HierarchyPanel.cs`、`MainForm.WorkbenchCommands.cs` | 已实现，见验证记录 |
| 54 | 文档“对象／帧”统计范围不清，嵌套内容误报空 | 标明当前编辑对象／帧，空态说明嵌套元件仍可能有内容 | `native/UI/DocumentInfoPanel.cs` | 已实现，见验证记录 |
| 55 | 颜色对话框 Esc 仍提交预览颜色 | 取消关闭恢复交互快照；仅确认完成调色事务，核对各调用方 | `native/UI/ProfessionalColorPickerDialog.cs`、`MainForm.ObjectAppearance.cs`、`SymbolFiltersPanel.Selector.cs`、`SceneLightingPanel.cs` | 已实现，见验证记录 |
| 56 | 多选描边宽度／透明度显示主对象值像共同值 | 分别判断宽度与透明度差异，显示 Mixed，编辑时才统一 | `native/UI/ObjectAppearancePanel.cs`、`MainForm.ObjectAppearance.cs` | 已实现，见验证记录 |
| 57 | 几何数值拖动缺少实时预览／撤销边界 | 连续参数预览，完成合并一次撤销，取消恢复快照 | `native/UI/ObjectGeometryPanel.cs`、`MainForm.InspectorGestures.cs`、`MainForm.ObjectTransforms.cs` | 已实现，见验证记录 |
| 58 | 几何标签和框内重复显示 px | 标签简化，单位由数值框统一显示 | `native/UI/ObjectGeometryPanel.cs` | 已实现，见验证记录 |
| 59 | 恢复原始大小连旋转／倾斜也清零 | 仅恢复 XYZ 缩放为 100%，保留位置、旋转、倾斜 | `native/UI/MainForm.DrawingTools.cs`：`RestoreDrawingObjectOriginalSize` | 已实现，见验证记录 |
| 60 | 源锚点看起来是当前实例局部属性 | 标为源元件锚点 X/Y，说明共享修改及全部放置补偿 | `native/UI/DrawingObjectInstancePanel.cs` | 已实现，见验证记录 |
| 61 | 连续锚点编辑撤销不能按一次手势恢复 | 锚点交互捕获完整相关状态，完成一次记录，取消恢复 | `native/UI/MainForm.InspectorGestures.cs`、`MainForm.DrawingTools.cs` | 已实现，见验证记录 |
| 62 | 多选实例播放参数差异未提示 | FPS、播放方式及保持帧独立展示混合状态 | `native/UI/DrawingObjectInstancePanel.cs`、`MainForm.InspectorAndWorkspace.cs` | 已实现，见验证记录 |
| 63 | 只改 FPS 却覆盖其他播放属性 | 播放变更携带字段标记，仅应用实际编辑字段 | `native/UI/DrawingObjectInstancePanel.cs`、`MainForm.DrawingTools.cs` | 已实现，见验证记录 |
| 64 | Hold Frame 从 0 编号与时间轴不一致 | 显示与输入使用 1 起始帧号，写模型时转换 | `native/UI/DrawingObjectInstancePanel.cs` | 已实现，见验证记录 |
| 65 | 元件透明度数字精度不足 | 百分比输入保留两位小数及细步长，显示混合值 | `native/UI/DrawingObjectInstancePanel.cs`：透明度输入 | 已实现，见验证记录 |
| 66 | 滤镜窗口套原生边框，与全局弹窗不同 | 使用现代对话框基类，保留移动缩放及原有编辑行为 | `native/UI/DrawingObjectInstancePanel.cs`：`FilterWindow` | 已实现，见验证记录 |
| 67 | 滤镜缺少启用效果概览 | 展示当前效果、颜色变换及导入效果概览；与选择状态同步 | `native/UI/SymbolFiltersPanel.cs`：`RefreshSelector` | 已实现，见验证记录 |
| 68 | 效果顺序不可见且无法调整 | 六种内置效果可上移／下移，关闭仍保留位置，多选按各自顺序调整；附加模糊跟随 Blur 槽，Flash 链按原序后置，核对保存与一次撤销 | `native/UI/SymbolFiltersPanel.OrderAndGradient.cs`、`native/Engine/SymbolFilters.cs`、`native/Rendering/SymbolFilterRasterizer.cs`、`SymbolFilterGpuRasterizer.cs` | 已实现，见验证记录 |
| 69 | 渐变滤镜只支持双色，无多色标编辑 | 渐变斜角／发光支持 2–16 色标，位置／颜色／透明度编辑，中间增删，端点固定，核对存储与 CPU/GPU 一致 | `native/UI/SymbolFiltersPanel.OrderAndGradient.cs`、`native/Engine/SymbolFilters.cs`、`native/Rendering/SymbolFilterRasterizer.cs`、`SymbolFilterGpuRasterizer.cs` | 已实现，见验证记录 |
| 70 | 滤镜参数挤在不同高度与边距里 | 复用标准字段、单位、分隔与行间距；窄栏完整可滚动 | `native/UI/SymbolFiltersPanel.Selector.cs`、`SymbolFiltersPanel.cs` | 已实现，见验证记录 |
| 71 | 自定义材质参数后预设回到空白 | 明确显示 Custom；多选差异显示 Mixed，匹配时显示对应预设 | `native/UI/SpatialMaterialPanel.cs` | 已实现，见验证记录 |
| 72 | 光学／混色参数只能凭滑条猜值 | 材质与混色关键参数提供可精确输入数字，并同步滑条 | `native/UI/SpatialMaterialPanel.cs`、`MixingBrushSettingsPanel.cs` | 已实现，见验证记录 |
| 73 | 灯光类型看似下拉可改，实际不生效 | 只读显示类型，并指引通过 Add light 创建其他类型 | `native/UI/SceneLightingPanel.cs`：`BuildTypeRow` | 已实现，见验证记录 |
| 74 | 灯光 XYZ 挤在一行，轴与值难辨 | XYZ 分行标注，共享数值字段与单位 | `native/UI/SceneLightingPanel.cs`：`BuildAxisRow` | 已实现，见验证记录 |
| 75 | 环境光等显示大量不相关参数 | 按灯光类型显示位置／方向／范围／区域／阴影行，高度随可见内容更新 | `native/UI/SceneLightingPanel.cs`：参数可用性及可见行 | 已实现，见验证记录 |
| 76 | 笔尖硬度灰显却不知道原因 | 悬停说明硬度仅用于柔边圆笔尖，自定义／传统笔尖保留自身边缘 | `native/UI/BrushTipPanel.cs` | 已实现，见验证记录 |

补充的前序小修：相机微动区移除“恢复自然手持”按钮；图层深度列表并入面板统一滚动；分栏中允许多个独立折叠区。它们不是上述 76 个编号的额外验收数量。

已执行的验证（2026-11-10）：

- `pwsh -NoProfile -ExecutionPolicy Bypass -File .agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1 -Suite Build`：最终构建通过，0 错误、29 条警告。较早的集成构建发现的缺失命名空间、不可访问查询及测试夹具初始化错误均已修正；没有把首次失败当成通过。
- `git diff --check`：通过。新增源码、未跟踪 partial 与台账另行检查；素材参考 ZIP 和解压目录未改动。
- `pwsh -NoProfile -STA -ExecutionPolicy Bypass -File artifacts/ui-verification/camera-scroll-review.ps1`：360px/272px 相机微动与深度区截图已检查。滚轮落在深度输入框：`DepthBefore=0`、`DepthAfter=0`、外层滚动位置变为 `84`、`WheelHandled=true`、内部列表为普通 `Panel`。
- `pwsh -NoProfile -STA -ExecutionPolicy Bypass -File artifacts/ui-verification/panel-final-visual.ps1`：已捕获并检查中文 360px/272px 的几何、混合实例、混色、材质、环境光、场景及滤镜面板，以及设置窗口。实例混合数值与混合模式明确显示“混合”；环境光隐藏不适用参数；数值框与滑条保留各自用途。截图是隔离绑定控件的视觉验收，不等同于完整工作台操作回归。
- `pwsh -NoProfile -ExecutionPolicy Bypass -File .agents/skills/vector2d-coordinate-agents/scripts/test-agent-workflow.ps1`：失败，原因是工作树既有 15 个 `Benchmark.Flash*.cs` 未登记在 benchmark map（本次新用例 `Benchmark.PanelUsability.cs` 已登记）。该静态指南检查没有执行产品回归；未顺手改动这些无关登记。
- `pwsh -NoProfile -ExecutionPolicy Bypass -Command "& '.agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1' -Suite Timeline,Render -Plan"`：计划解析通过，只检查计划，没有构建或执行用例。

本轮实际回归结果：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 最终构建 | 通过，0 错误、29 条警告，退出码 0 | `artifacts/validation/panel-build-final.log` |
| Timeline 最终复核 | 通过，退出码 0，188.159 秒 | `artifacts/validation/panel-timeline-final.log` |
| Render 最新完整尝试 | 失败，退出码 1，167.847 秒；在选中对象缩放性能检查处中止 | `artifacts/validation/panel-render-26.log` |
| 滤镜 GPU/CPU 对照 | 较早运行通过 279 个案例，最大通道差 1，适配器 RTX 5060 Ti，CPU 回退通过 | `artifacts/validation/panel-render-10.log`；最终 Render 在到达该矩阵之前中止 |
| 参考视图缓存 | 状态变更与恢复检查通过，稳定复用分配 0 字节 | 最新 Render 日志中的 `scene_reference_render_plan_cache_allocated_bytes=0` |
| 真实窗口像素与预渲染目标 | 灯光手柄、透视材质、遮挡、图层组、SVG、预渲染与缩放目标检查通过 | 最新 Render 日志；最终运行没有将这些检查标为无桌面跳过 |

实际命令（先执行对应 `-Plan`，串行运行）：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -Command "& '.agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1' -Suite Timeline,Render -ContinueOnFailure"
pwsh -NoProfile -ExecutionPolicy Bypass -Command "& '.agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1' -Suite Render"
pwsh -NoProfile -ExecutionPolicy Bypass -Command "& '.agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1' -Suite Timeline -NoBuild"
pwsh -NoProfile -ExecutionPolicy Bypass -Command "& '.agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1' -Suite Build"
```

最后一条在同一份已成功构建、未更改产品代码的 Release 程序上执行。其后仅移除了验证文件中已不用的 `Bounds` 本地函数，并另做最终构建。没有修改 60 FPS、像素差或内存分配门槛；没有执行发布、launcher 生成或提交。

回归中补修的实际问题：

1. 取消图层／图层组调色时，精确恢复原值；未设置自定义颜色的图层组仍保持继承状态，不产生额外撤销。图层组管理窗口的颜色子对话框仅确认后更新草稿。涉及 `MainForm.TimelineAndHistory.cs`、`TimelineStrip.TabGroups.cs`、`TimelineTabGroupOrderDialog.cs`。
2. 精确裁剪的位置输入不再被旧裁剪宽度挡住；贴近边缘时缩小裁剪范围，并保持比例和当前图片缩放。增加自由裁剪、锁定比例及边缘输入验证。涉及 `ImageCropDialog.cs`、`Benchmark.ImageAssets.cs`。
3. 图层混合模式、轮廓、颜色、名称和锁定状态的模型接口补齐呈现状态版本，参考视图可及时更新并恢复匹配缓存；缓存键改用值类型，稳定复用分配为 0 字节。涉及 `VectorScene.LayersAndTimeline.cs`、`StageControl.Reference3D.cs`。

同步修正的验证夹具与误判：

- 按当前行为检查无多余保存成功弹窗、真实空层级、展开后的微动区域、数值连续预览与单次撤销、明确的“混合”选项，以及清单中的真实 SVG 路径。
- 工程快照比较保留完整时间轴、几何、遮罩、资源和实例状态，仅排除恢复时按时间轴重算的旧 `LayerStart`／`LayerEnd` 派生缓存。
- 灯光夹具显式启用实验灯光；需要独立 GPU 的窗口检查采用现有 GPU 用例相同的局部启用／`finally` 恢复边界。Render 入口默认禁用独立 GPU，不能据此前提下的黑色客户区判断内容丢失。窗口捕获增加黑色客户区识别和屏幕捕获回退，失败图仍保留在验证产物中。
- 面光源局部范围改为检查真实轮廓差集，避免把圆形样本的外接矩形误判为覆盖整个接收面；仍要求存在未受照区域。
- 当前 `DrawScene` 使用完整原始几何，缩放夹具改为检查所有对象都被绘制、选区缓存复用、像素一致性；没有要求启用已不走的马赛克绘制路径。性能门槛保留。耗时较长的滤镜硬件矩阵放在功能检查之后，调用覆盖仍保留。

尚未通过的性能验收：

夹具为 373 个 16 顶点对象、全部选中、1920×1080、RTX 5060 Ti；平移／缩放均记录 120 个采样帧，缩放前 12 帧预热。最新运行：

| 操作 | 平均帧耗时 | P95 | 最大值 | 现有门槛结果 |
| --- | --- | --- | --- | --- |
| 未选中平移 | 8.529 ms | 10.739 ms | 22.067 ms | 对照数据 |
| 全选平移 | 15.541 ms | 18.992 ms | 26.365 ms | `dense_selection_pan_budget_met=false` |
| 全选缩放 | 14.933 ms | 20.885 ms | 37.982 ms | `dense_selection_zoom_budget_met=false`，原有断言抛出 |

缩放过程中几何与命令重建均为 0；记录到绘制命令平均 7.131 ms、提交平均 8.436 ms。此前同夹具也出现平均 11.698 ms、P95 13.695 ms 的通过数据，以及四组缩放像素误差 0。说明存在运行波动，不能挑选通过的一轮认定“卡顿已消除”。执行期间另观察到桌面合成及内存压缩活动，未关闭用户进程；这些事实限制性能归因，不能据此豁免失败。

最新 Render 在 `RunDenseSelectionPanRenderRegressionCore.MeasureZoom` 中止，其后的滚轮新断言、其余舞台交互、画笔及最终滤镜矩阵未在最后一次运行中执行。较早部分运行的通过结果不等于最终套件通过。一次形状浮窗可见性检查失败后，未修改该处源码的复跑越过该项，此不稳定现象也尚未证明消除。

其他验收边界：真实高 DPI、多显示器边缘缩放、偏好设置保存失败注入及大型工程长时间手工操作尚未全面检查；工程／包保存失败恢复已有 Timeline 用例通过。没有把 76 行实现记录等同于 76 项完整人工验收，也没有进行干净基线与当前工作树的性能 A/B 对比。

补充收尾：设置 H/S/B 输入统一行高并显示单位；混色与光学材质以精确值为准，滑条仅显示近似位置；跨场景重做始终保留全局队列，历史预算驱逐会同时裁剪可能覆盖较新内容的旧项目级快照。
