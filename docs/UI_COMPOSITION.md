# UI 组件组合

当前 UI 使用程序化 WinForms。AI 修改源码时，从 `native/UI/MainForm.UiComposition.cs` 的组合列表入手；列表使用从上到下的视觉顺序，不需要自行推算 WinForms 的反向 Dock 顺序。

## 组合接口

`UiComposition` 只接收 WinForms 控件、可选高度及填充控件，不依赖项目、Stage、MainForm 或业务事件。

```csharp
var shape = new ShapeSettingsPanel();
var image = new ImageInspectorPanel();
var page = new ThemedScrollPanel();

shape.SetState(new(ShapeKind.Star, 5));
image.SetState(new("image-id", "sprite.png" + Environment.NewLine + "1920 × 1080"));

UiComposition.MountVertical(page.Content,
[
    new(shape, shape.PreferredHeight),
    new(image, image.PreferredHeight)
]);
page.PerformLayout();

shape.SettingsChanged += (_, e) => ApplyShapeSettings(e.State);
image.SettingsRequested += (_, e) => OpenImageSettings(e.AssetId);

// 重新排列既有组件：保留实例、状态和事件。
UiComposition.OrderVertical(page.Content, [image, shape]);
page.PerformLayout();

// 普通 Panel 也可作为宿主，最后一个参数占满剩余空间。
UiComposition.MountVertical(parent, [new(shape, shape.PreferredHeight)], fill: details);
parent.PerformLayout();
```

示例中的命令由宿主实现。控件类型在同一个程序集内可直接使用，不需要另建框架或容器。

- `MountVertical` 增加或移动指定组件，保留未列出的兄弟控件，不负责删除或释放控件。
- `OrderVertical` 只排列当前宿主内的组件，跳过暂时属于其他宿主的组件。
- 隐藏的组件不占 Dock 空间；显示、启用状态由宿主控制。
- 组件的 TabIndex 随视觉顺序更新，键盘导航顺序与排列保持一致；组件内部仍管理自身焦点。
- 高度省略时保留控件现有高度或 AutoSize 行为，不强制所有面板采用统一尺寸。
- 重复组件、已释放控件、负高度或父子循环在修改布局前被拒绝。
- 接口暂停布局并以 `ResumeLayout(false)` 结束，允许外层批量组合；外层结束后调用 `PerformLayout` 或 `ThemedScrollPanel.ResumeContentLayout(true)`。
- 一个 WinForms 控件只能属于一个父容器。需要同时显示两份时分别创建控件，通过宿主同步状态。
- 所有创建、布局、状态设置与事件处理在 UI 线程进行；父容器负责释放子控件。组件移动时，`ThemedScrollPanel` 会解除旧容器的滚轮订阅，并接入新容器。

## 组件边界

| 组件 | 状态输入 | 用户意图 / 输出 | 依赖方式 |
| --- | --- | --- | --- |
| `ShapeSettingsPanel` | `SetState(ShapeSettingsState)` | `SettingsChanged`，含形状和顶点数量 | 无 DrawSettings 实例订阅或修改 |
| `ImageInspectorPanel` | `SetState(ImageInspectorState?)` | `SettingsRequested`，含资产 ID | 宿主查询资产、格式化摘要和打开编辑命令 |
| `SelectionSummaryPanel` | 四个文本属性 | 只读展示 | 宿主计算选择、图层和统计信息 |
| `LayerBlendModePanel` | `SetLayer` / `ClearLayer` | `BlendModeChanged` | 宿主同步当前图层并执行混合模式编辑，控件负责标题、图层名称与选择框的展示 |
| `TextSettingsPanel` | `SetSettings` | `SettingsChanged` | 既有独立状态接口 |
| `BrushTipPanel` / `MixingBrushSettingsPanel` | 各自的 `Set*` | 各自的设置事件 | 既有独立状态接口 |
| `SpatialMaterialPanel` / `SceneLightingPanel` | `SetMaterials` / `SetLights` | 类型化编辑、选择事件及交互开始/完成/取消 | 宿主保留撤销与模型更新规则 |
| `SceneEditorPanel` / `PlaybackSettingsPanel` | 既有绑定 / 属性接口 | 各自的场景命令 / 播放设置事件 | 同一检查器中的平级组件，可分别重排 |

宿主分工：`MainForm.UiComposition.cs` 负责位置与排列，`MainForm.WorkbenchCommands.cs` 连接事件，检查器和领域 partial 负责计算状态与执行原有编辑命令。叶组件不查找 MainForm、不引用兄弟组件，也不通过父容器查询业务状态。

## 检查器模块标题与分隔线

检查器顶部的“图层”标题由 `native/UI/LayerBlendModePanel.cs` 管理。`MainForm.UiComposition.cs` 负责挂载与排列面板，标题通过 `InspectorModuleStyle.CreateTitle` 复用字体、颜色和对齐规则，内部定位由叶组件维护；标题样式不改变图层模型或混合模式编辑流程。

- 标题使用 `Theme.UiFont(8.5f)` 的常规字重，靠左上对齐。面板左侧内边距为 4、顶部内边距为 0，标题不额外添加内边距；这些数值沿用 WinForms 的缩放机制。
- 标题颜色从当前 `Theme.Panel` 与 `Theme.Text` 按 50% 混合，并通过 `Theme.ReadableUiColor` 保持可读性，与时间轴左侧图标采用一致的柔和配色。绘制时重新获取主题颜色，高对比度模式使用 `SystemColors.ControlText`。
- 当前图层名称在标题旁左对齐，使用 9pt 常规字重和 `Theme.Text`，标题列按本地化文字自适应宽度，后留 10 的间距，再显示 8 的圆形图层色标与名称；长名称省略，悬停和可访问描述提供完整名称。色标颜色由宿主在 `SetLayer` 中传入，绘图上下文使用 `GetLayerColor`，场景上下文使用 `SceneLayerDefinition.ColorArgb`，与时间轴一致；清空状态移除色标和提示。混合模式继续独占下一行，与标题之间保留较宽松间距，底部留白为 16。调整标题时，应保留 `SetLayer` / `ClearLayer` 的状态刷新保护和 `BlendModeChanged` 的事件边界。

“着色”模块由 `MaterialEditorPanel` 管理，其标题也使用 `InspectorModuleStyle.CreateTitle`，与“图层”保持相同的 8.5 常规字号、柔和主题色和左上对齐。标题左侧内边距为 4，顶部和底部各保留 10 的留白，填色／描边行高为 50；字段通过行边距补偿保留原有水平位置。

模块边界使用 `InspectorModuleStyle.DrawBottomDivider` / `DrawDivider`：`LayerBlendModePanel.OnPaint` 绘制底部边界，组合根连接检查器宿主绘制事件，补齐外侧内容留白，使分隔线两端贴齐检查器边缘。模块线与着色内部分组线共用 `DividerColor`，即 `Theme.Panel` 与 `Theme.Border` 按 55% 混合；模块线宽为内部分组线的 1.5 倍（96 DPI 下分别为 1.5 与 1），随 DPI 缩放，高对比度模式使用系统文字色。边界绘制不增加焦点、命中区域或业务绑定。后续模块可复用这个绘制入口；组合根负责模块顺序，叶组件负责自身标题及边界。

通宽模块线用于识别可独立组合的面板边界，着色内部随内容缩进的较细分组线继续表示模块内分组，两者保持不同的视觉层级。本次建立展示约定，不引入运行时拖拽或模块配置协议。

## 场景与播放检查器

`SceneEditorPanel` 和 `PlaybackSettingsPanel` 沿用与着色模块一致的 `InspectorModuleStyle.CreateTitle`：小字号、柔和主题色、左上对齐。两个面板采用左 4、右 8、上 10、下 16 的内边距，字段额外缩进 4，参数标签列为 96，编辑器占满剩余宽度。镜头投影下拉框随参数列伸展，不使用默认控件宽度。

场景模块首选高度为 340，基础信息行高为 40，列表操作标题行高为 36；场景与实例列表保留上下间距，关闭整数项高度约束以填满可用区域。播放模块首选高度为 204，四个参数行高为 36，以 TableLayoutPanel 替代绝对坐标，统一标签与编辑器对齐。循环开关只显示开关本体，说明由左侧字段标签提供。

布局由两个叶组件各自维护，组合根读取首选高度。已有场景选择、实例双击、添加命令、投影切换、播放参数事件、帧范围约束和 `_updating` 刷新保护保持原有边界。

## 镜头与导演检查器

`ShotDirectorPanel` 的相机列表标题与所选相机属性标题复用 `InspectorModuleStyle.CreateTitle`，采用 8.5pt 常规字重、柔和主题色和左上对齐。标题左侧缩进为 4，属性字段左侧为 8，标签宽 90、标签到编辑器间距 6，编辑器右边距为 8，与场景／播放模块的参数列关系保持一致。

单参数控件高度为 28、行步长为 36，位置／旋转轴组保留独立轴标签及 10 的组间距；窄布局中相机列表与属性区之间留 16。顶层相机标题不添加顶部内边距，与图层模块标题对齐；标题、工具栏高度分别为 24 与 40，尺寸沿用 `ScaleMetric` 适配 DPI。宽布局仍并排展示列表与属性，窄布局仍上下堆叠，属性区滚动和共享曲线编辑器保持原有归属。

顶部相机／帧数／时长摘要与属性区帧／关键帧／补间统计不常驻显示，分别通过相机模块标题和当前相机标题的悬停提示提供。未选中时不显示操作指导行，属性布局不为隐藏提示预留高度；提示资源随面板释放。

这些展示调整不改变相机激活、增删重排、定位、投影与构图参数、连续编辑事件或撤销边界。

## 检查器工作区切换动画

`MainForm.ShowWorkspace` 在基础绘制、场景与动画、镜头与导演之间切换时，使用 `InspectorTransitionOverlay` 为右侧检查器提供 180ms 的过渡。真实控件沿原有流程绑定、排列并恢复布局，覆盖层只保存前后快照，以缓出曲线淡出旧内容、淡入新内容；新内容从下方 6 个逻辑像素移到原位。画布与时间轴不参与该动画，过渡期间覆盖层拦截检查器鼠标输入，不改变真实控件状态或事件绑定。

首次展示和同工作区刷新不播放动画。连续切换从当前过渡画面重新开始，不排队；宿主大小或可见性变化、系统禁用动画或高对比度、快照捕获失败时取消过渡并直接展示真实目标控件。快照超过 800 万像素时跳过捕获。覆盖层由检查器宿主拥有，释放时解除订阅并销毁计时器与位图；新类型注册到 UI 热重载模块。自绘控件快照的视觉完整性仍需实际界面验收。

## 工具栏图标开关

时间轴的自动关键帧、运动轨、洋葱皮与画布右上角 `DrawSnappingStrip` 的六个吸附开关，共用 `SvgToggleButton` 的 `IconOnly = true`、`ShowCheckedBackground = true` 绘制路径。选中时图标下方显示圆角胶囊底色，颜色按宿主背景和主题文字色混合；未选中时使用宿主背景，悬停和按下沿用控件的渐变反馈。该路径不使用另一种按钮样式的底部强调条。

圆角、底色、主题对比度与焦点反馈由 `SvgToggleButton` 管理，宿主仅设置尺寸和图标大小：吸附栏保留 28 × 28 的按钮、20 的图标，时间轴沿用现有尺寸。高对比度模式使用系统选中色和矩形底色。吸附栏保持 `DrawSettings` 的状态读回、`_updating` 防反馈、点击/空格切换、说明提示和窄窗口布局；统一选中外观不改变吸附行为。

工作区按钮等分段选择按钮通过 Theme.StyleSegmentedButton 统一底色：未选中时将面板色向主题文字色混合 6.5%，悬停 10%，按下 4.5%，让深浅主题都保留轻微但清晰的按钮轮廓。选中状态继续使用主题强调底色，尺寸与布局保持稳定。

## 弹出菜单视觉约定

主菜单、二级菜单和上下文菜单复用 `AnimatedContextMenuStrip`。菜单保留 30 的操作行高、7 的外侧留白、随个性化设置切换的直角／4 逻辑像素微圆角外框与悬停底色，圆角随 DPI 缩放；窗口区域与圆角外框一致。悬停背景复用 `Theme.MenuSelectionSurface`（面板底色与强调色按 28% 混合）；正文与快捷键分别以 `Theme.Text` 和 `Theme.Muted` 为首选色，通过 `ReadableText` 保持可读对比度，外框使用 `Theme.ControlOutline`，子菜单箭头通过 `ReadableUiColor` 提高辨识度。菜单每次展开刷新主题和窗口区域，避免角样式切换后残留旧轮廓；分隔线采用淡化的主题边框色。

所有命令子菜单在 `StyleItem` 中统一为 `AnimatedContextMenuStrip`，迁移原菜单项实例以保留事件、Tag 和勾选状态；动态新增的嵌套项使用同一入口。宽度依据正文、快捷键、勾选／图标与箭头共同测量，逻辑宽度范围为 224–480；尺寸与留白随 DPI 缩放。二级菜单直接展开，不使用一级菜单的开场位移。调色浮层等嵌入编辑器保持其专用布局。

渲染器在菜单项局部坐标中绘制高亮与分隔线。高对比度模式恢复矩形区域和系统文字／选中色，系统关闭动画时跳过开启动画。原有点击、子菜单、勾选、禁用、快捷键和键盘导航继续由 ToolStrip 管理。

下拉选择框通过 `Theme.StyleComboBox` 和 `DrawComboBoxItem` 统一选项行：30 逻辑像素行高、跟随直角／微圆角方案的内缩选中底色、12 的文字留白，与命令菜单使用一致的主题配色。列表最多显示八行，展开时根据当前项目数更新高度，更多选项使用原生滚动和键盘选择。关闭状态高度仍由 `ComboBoxSurfaceAdapter` 独立维护；文字和箭头按 ClientSize 居中，不能用包含非客户区的外部 Height 计算；原生下拉窗口外框仍由系统管理。

时间轴工具栏与页签栏下沿的横向分隔线也使用 Theme.ControlOutline，厚度按 1.5 逻辑像素随 DPI 缩放并向上取整（100% 缩放为 2 像素），在内容绘制结束后向栏内填充实心横条，防止抗锯齿衰减和背景覆盖，避免页签栏通过对比度增强产生亮色长线。

## 控件描边

`Theme.ControlOutline` 统一交互控件外框与模块分隔线的颜色，普通主题按面板色与边框色混合 55%，高对比度使用系统窗口文字色。图标按钮、普通按钮、开关按钮、工作区色块、颜色目标按钮、数值输入、下拉选择框和命令菜单外框均使用该颜色，悬停、选中和焦点不再把外框提升成亮白或强调色。选中底色、图标和独立的键盘焦点提示继续保留；直角／微圆角仍由个性化设置控制。
滑动条 `ModernSlider` 使用 CSS 风格的三层表面：轨道使用 `PanelStrong` 与 `Border` 的低对比混合色，进度使用 `AccentSurface` 与 `Accent` 的主题色混合，滑块使用 `PanelStrong`／`PanelHover` 的紧凑圆形表面，外框和刻度使用 `Theme.ControlOutline`。悬停只提升进度和滑块表面，拖动保留按下动画；禁用状态降低填充和刻度对比度。暗黑模式不再把整条轨道提升为白色，高对比度仍使用系统颜色。

状态统计统一放在底栏：渲染 FPS、UPS／动画 FPS、绘制数、原子数、缩放及当前帧只显示一份。上方参数行保留工具操作，移除统计文字。辅助状态使用 Theme.SecondaryText 的柔和灰色，工作区颜色说明、网格说明和热重载正常状态降为同级，失败和恢复仍保留错误／警告色。

文本框和列表框通过 Theme.NativeFrames 的 NativeFrameAdapter 绘制非客户区边框，使用 ControlOutline，保留原生输入、选中、滚动和键盘行为。文本框左右内边距为 7 逻辑像素，随 DPI 调整；不套额外容器，不改变绑定或布局。文本输入框采用统一紧凑高度，NativeFrameAdapter 独占非客户区计算和边框绘制，不在原生计算后叠加边距；根据当前字体高度将原生文字和光标居中，外框复用 Theme.PaintUiSurface，跟随直角／微圆角偏好；列表保持矩形结构。和谐色选择器限制为 156 逻辑像素，窄面板时收缩到可用宽度；所在行根据选择框实际高度加上下留白更新，响应 SizeChanged 与 DPI，避免固定行高裁切底边。

颜色输入使用独立组件 `ModernTextInput`：外层 `UserControl` 通过 `Theme.PaintUiSurface` 绘制主题表面和单层描边，内层无边框原生 `TextBox` 负责文本、选区、光标、粘贴和输入法。编辑器按实际字体高度垂直居中，横向留白为 8 逻辑像素。组件通过 PreferredFieldHeight 返回字体与 DPI 对应的安全高度；HEX 宿主行按该高度和上下留白分配空间。外框向内留一个设备像素，避免直角描边在非整数 DPI 时被底边裁切。公开 Text、MaxLength、CharacterCasing、ReadOnly、SelectAll，并转发键盘事件，保留原有解析、提交和取消处理；错误文字色由宿主控制。焦点与悬停改变表面色，不改变尺寸。外框随直角／微圆角偏好，主题颜色在绘制时读取；禁止将内层编辑器接入 NativeFrameAdapter。着色面板、专业颜色对话框与工作区颜色浮层的 HEX 字段统一采用该组件，新类型登记到 Shell 热重载模块。普通列表／文本框继续使用各自 Theme 入口。
## 扩展和验证

新增组件优先提供无参数构造、显式状态输入、类型化事件和尺寸属性。状态刷新应使用 `_updating` 防止触发编辑，连续手势沿用开始/完成/取消协议。复用 Theme、UiLocalization 和既有基础控件。

新类型同步登记 `native/App/HotReloadModules.cs` 的所属模块。当前 UI 树的结构变更仍使用现有进程重启与会话恢复策略。

组合回归位于 `Benchmark.AppIntegration.cs`，由 `RunUiRefreshStabilityRegression` 接入现有 `Render` 套件，覆盖视觉顺序、Fill、隐藏组件、重复挂载、跨容器与动态子组件滚轮解绑、独立状态/事件以及主题/语言截图。场景与播放的平级关系由现有工作区回归检查。使用仓库验证脚本的 `-Suite Build,Render -Plan` 先核对计划，再串行执行实际套件。

当前解耦主要覆盖检查器组合及以上组件。Timeline、Vault、部分 DrawSettings 控件仍使用既有模型绑定；它们可以通过组合接口放置，但更改其业务行为仍需要遵循原有宿主命令、撤销和数据契约。源码组合能力不包含运行时布局编辑器或 JSON 布局协议。
