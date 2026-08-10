# Vector 2D Animation Engine 0.1.9

[文档索引](README.md) · [当前用户指南](USER_GUIDE.md)

发布日期：2026-07-30

0.1.9 为绘制图层和场景图层加入全部 27 种混合模式，完善图层颜色与 Outline 工作流，并修复混合后下层不可见以及右侧检查器颜色模块被遮挡的问题。

## 重点更新

### 完整图层混合模式

- 选中绘制图层或场景图层后，可在右侧检查器的 `Layer > Blend Mode` 中选择 `Normal`、`Multiply`、`Screen`、`Darken`、`Lighten`、`Dissolve`、`Color Burn`、`Linear Burn`、`Darker Color`、`Color Dodge`、`Linear Dodge`、`Lighter Color`、`Overlay`、`Soft Light`、`Hard Light`、`Vivid Light`、`Linear Light`、`Pin Light`、`Hard Mix`、`Difference`、`Exclusion`、`Hue`、`Saturation`、`Color`、`Luminosity`、`Subtract` 和 `Divide`。
- 使用 `Ctrl` 或 `Shift` 多选图层后，修改会一次应用到全部选中图层，并只生成一条撤销记录。
- 遮罩先裁切内容再参与混合；文件夹与场景图层按隔离组整体合成，模式不会逐个重复应用到组内对象。
- 混合模式会随工程快照、SVG、Vault、图层复制、嵌套组合和应用重启持久保存。旧工程缺少混合数据时按 `Normal` 打开，非法持久值会在载入时拒绝。

### 合成与渲染修复

- 修复所有非 `Normal` 模式下下层图层不可见的问题。下层像素现在会作为背景参与标准预乘 Alpha 混合，不会被上层离屏结果替换。
- 包含非 `Normal` 模式的帧自动使用精确的软件图层合成器；全部恢复为 `Normal` 后继续使用 Direct2D 硬件路径。
- `Dissolve` 对同一图层和像素位置保持确定性。遮罩、不透明度、先填色后描边顺序、洋葱皮、嵌套底图和拖拽预览继续保持正确层级。
- 新增覆盖全部 27 种模式、透明度、下层保留、隔离组、持久化与渲染器切换的回归检查。

### 图层颜色、Outline 与检查器

- 双击时间轴图层色块或使用 `Layer Color...` 可打开主题化颜色面板，使用饱和度/明度平面、色相条、RGB、`#RRGGBB` 和预设色编辑图层标识色。
- 颜色调整会在时间轴与舞台实时预览；`Cancel` 恢复原颜色，`Apply` 只生成一次撤销，多选图层可一起修改。
- 图层色块可切换正常/Outline 显示。Outline 使用图层色显示约 `1 px` 的空心边界；文件夹会传递到子层，场景图层会覆盖其嵌套实例树，但对象材质与导出结果不变。
- 修复图层混合面板占用检查器布局后遮挡材质控件的问题；右侧 `Fill` 与 `Stroke` 颜色目标模块现在会正常显示、展开和切换。
- 洋葱皮改为绘制时间轴全局开关：所有可见且未锁定图层自动参与，锁定层被排除，Outline 图层保留暖色/冷色轮廓预览。

### 编辑稳定性

- 自相交 Fill 边界现在会显示真实交点锚点；拖动其中一条分支时，相交分支会保持同一交点，控制点仍可分别调整。
- 填色边界在可行时会恢复并保留精确三次贝塞尔关联，减少旧工程密集采样线段对后续编辑的影响。
- 图层混合、颜色、Outline 和洋葱皮状态会随快照、图层顺序与组合预览保持稳定。

## 安装与更新

正式发布物只有一个文件：

```text
VectorAnimationEngine-0.1.9-win-x64.exe
```

1. 关闭正在运行的软件。
2. 将 `VectorAnimationEngine-0.1.9-win-x64.exe` 放入现有可写运行目录；可以保留旧版 EXE 用于回测。
3. 保留已有 `.Runtime`、`.V2DEngine`、`logs`、工程和用户数据。
4. 启动 0.1.9 EXE。兼容的 `.Runtime` 会直接复用，`.V2DEngine` 会从当前 EXE 的内嵌负载原子重建。

仓库根目录的 `VectorAnimationEngine.exe` 是源码开发启动器，不属于正式发布包，请勿用正式 EXE 覆盖。

## 兼容性与已知说明

- 支持 Windows x64；首次自动安装 .NET 8 Core 与 Windows Desktop x64 运行环境需要网络连接和可写目录。
- 工程格式保持向后兼容；已有日志、工程、用户数据和有效本地运行环境会继续保留。
- 非 `Normal` 模式使用精确软件合成，复杂大画布上的性能可能低于全部为 `Normal` 时的 Direct2D 路径。
- 复杂 SVG 栅格化拆散仍受有限精度约束，高倍缩放时可能出现轻微边缘或颜色分级差异。
- 当前正式 EXE 尚未进行代码签名。
