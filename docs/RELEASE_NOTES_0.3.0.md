# 0.3.0 更新公告 / Release Notes

2026-10-30 · 相对 0.2.7 / Since 0.2.7

## 版本 0.3.0

升级场景绘制、局部拓扑与智能布局，新增标尺辅助线并补齐舞台快捷键。

### 场景绘制与检查器

- 场景与动画的二维前视支持直接绘制，绘制内容与实例沿用场景图层、时间轴和保存流程。
- 重新整理右侧检查器分类与工具上下文，完善对象几何、位置尺寸、多选摘要、文本、图片和笔刷参数的展示与编辑。
- 改进跨图层选择、图层内容高亮和对象变换，让当前选择与可编辑参数更清晰。

### 线段、填色与局部拓扑

- 调整线段时优先命中端点；拖动端点按住 C 可吸附连接其他端点。
- 改进填色边界随端点和曲线编辑的联动，并在操作完成后整理受影响区域的拓扑；自交封闭区域保留填色，在交点处分割描边。
- 完善局部填色／描边的拆离、实体化和对象索引映射，减少编辑后选择错位与轮廓刷新异常。

### 标尺、辅助线与智能对齐

- 新增舞台底部与右侧标尺，支持像素、毫米、厘米和英寸，并提供显示开关。
- 从标尺拖出辅助线；Ctrl+拖动重新定位，拖回标尺删除。辅助线使用工程高亮色，并随场景、元件及开放画布保存。
- 升级紧贴与对齐：支持可见边缘、中心、画布、辅助线、等距与连续间距提示，提供吸附目标和容差设置。
- 新增选择范围、主选对象和画布基准的对齐与等间距分布；绘制线段与拖动端点时偏好 15°、30°、45°、60°、90° 等常用方向。

### 舞台快捷键与编辑效率

- 新增 Alt 拖拽复制、方向键微移、Shift 轴锁定、取消全选、反选、穿透选择及框选减选／交集。
- 补齐就地复制与重复位移、原位粘贴、关联复制、复制／粘贴外观、相连拓扑选择和选择扩张／收缩。
- 新增二维适应窗口、实际大小、键盘缩放、缩放到选中、画布旋转、面板组显隐及二维数值移动／旋转／缩放。
- 完善隐藏、孤立、对象锁定、层级进入退出、关键帧跳转、Backspace 删除、拆散和堆叠命令，并补充可配置快捷键目录。
- 扩展文档编辑重做，支持 Ctrl+Y 与 Ctrl+Shift+Z，完善编辑操作的撤销／重做衔接。

### 体验与使用说明

- 移除拖动镜头及取景框时的浮动操作提示，保留悬停说明。
- 新增可离线阅读的中文 HTML 图文手册与内嵌截图的单文件版本，整理绘制、动画、场景、素材、保存和快捷键说明。

## Version 0.3.0

Expanded scene drawing, local topology and smart layout, with ruler guides and a broader stage shortcut workflow.

### Scene drawing and inspectors

- Added direct drawing in the 2D front view of Scene & Animation, integrated with scene layers, timelines and project saving.
- Reorganized inspector categories and tool context, with improved geometry, transform, multi-selection, text, image and brush controls.
- Improved cross-layer selection, layer-content highlighting and object transforms.

### Lines, fills and local topology

- Endpoint hits take priority during line editing; hold C while dragging an endpoint to connect it to another endpoint.
- Improved fill-boundary updates during endpoint and curve edits, with topology cleanup limited to affected regions. Closed self-intersection regions retain fills and strokes split at crossings.
- Improved local fill/stroke detachment, materialization and object-index remapping to reduce stale selections and outlines.

### Rulers, guides and smart layout

- Added bottom and right stage rulers with pixel, millimetre, centimetre and inch units and a visibility switch.
- Drag guides from rulers; Ctrl-drag to reposition and drop back on a ruler to delete. Guides use the project accent colour and persist with scenes, symbols and open canvases.
- Upgraded Tight Fit and Align with visible edges, centres, canvas and guide targets, equal-gap feedback, and configurable targets and tolerance.
- Added alignment and equal-gap distribution relative to selection, key object or canvas. Line drawing and endpoint editing favour common 15°, 30°, 45°, 60° and 90° directions.

### Stage shortcuts and editing

- Added Alt-drag duplication, arrow-key nudging, Shift axis locking, deselection, inverse selection, click-through selection and subtract/intersect marquees.
- Added in-place duplication and repeated displacement, paste in place, linked duplication, appearance copy/paste, connected-topology selection and selection growth/shrink.
- Added 2D fit, actual size, keyboard zoom, zoom to selection, canvas rotation, panel-group visibility and numeric move/rotate/scale.
- Expanded hide, isolate, object locking, hierarchy navigation, keyframe navigation, Backspace deletion, Break Apart and stacking commands, with configurable shortcut entries.
- Extended document-edit redo with Ctrl+Y and Ctrl+Shift+Z and improved undo/redo integration.

### Usability and documentation

- Removed floating operation tips while dragging cameras and framing boxes; hover help remains available.
- Added an offline Chinese illustrated HTML manual and a portable edition with embedded screenshots, covering drawing, animation, scenes, assets, saving and shortcuts.

