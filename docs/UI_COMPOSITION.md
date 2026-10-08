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
| `TextSettingsPanel` | `SetSettings` | `SettingsChanged` | 既有独立状态接口 |
| `BrushTipPanel` / `MixingBrushSettingsPanel` | 各自的 `Set*` | 各自的设置事件 | 既有独立状态接口 |
| `SpatialMaterialPanel` / `SceneLightingPanel` | `SetMaterials` / `SetLights` | 类型化编辑、选择事件及交互开始/完成/取消 | 宿主保留撤销与模型更新规则 |
| `SceneEditorPanel` / `PlaybackSettingsPanel` | 既有绑定 / 属性接口 | 各自的场景命令 / 播放设置事件 | 同一检查器中的平级组件，可分别重排 |

宿主分工：`MainForm.UiComposition.cs` 负责位置与排列，`MainForm.WorkbenchCommands.cs` 连接事件，检查器和领域 partial 负责计算状态与执行原有编辑命令。叶组件不查找 MainForm、不引用兄弟组件，也不通过父容器查询业务状态。

## 扩展和验证

新增组件优先提供无参数构造、显式状态输入、类型化事件和尺寸属性。状态刷新应使用 `_updating` 防止触发编辑，连续手势沿用开始/完成/取消协议。复用 Theme、UiLocalization 和既有基础控件。

新类型同步登记 `native/App/HotReloadModules.cs` 的所属模块。当前 UI 树的结构变更仍使用现有进程重启与会话恢复策略。

组合回归位于 `Benchmark.AppIntegration.cs`，由 `RunUiRefreshStabilityRegression` 接入现有 `Render` 套件，覆盖视觉顺序、Fill、隐藏组件、重复挂载、跨容器与动态子组件滚轮解绑、独立状态/事件以及主题/语言截图。场景与播放的平级关系由现有工作区回归检查。使用仓库验证脚本的 `-Suite Build,Render -Plan` 先核对计划，再串行执行实际套件。

当前解耦主要覆盖检查器组合及以上组件。Timeline、Vault、部分 DrawSettings 控件仍使用既有模型绑定；它们可以通过组合接口放置，但更改其业务行为仍需要遵循原有宿主命令、撤销和数据契约。源码组合能力不包含运行时布局编辑器或 JSON 布局协议。
