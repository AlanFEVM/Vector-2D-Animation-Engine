# 工作区颜色入口与布局按钮调整

## 交付行为

- 从工作区标题区移除工作区颜色入口，移到“编辑 → 设置 → 常规 → 外观”，显示色样及 Hex，沿用原 HSV、RGB、Hex 和预设调色浮层。
- 调色实时预览。关闭浮层保留设置草稿，设置“保存”成功后持久化；取消或关闭设置恢复已保存颜色。
- 属性、时间轴两个布局按钮移到画布工具栏右侧、缩放按钮左侧。保留显示状态、悬停提示和快捷键，隐藏面板后仍可恢复。
- 工具栏按钮统一尺寸、间距与垂直位置，并随 DPI 缩放。

## 修改文件

- `native/UI/MainForm.cs`：入口移动、按钮布局、颜色预览与取消恢复。
- `native/UI/SettingsDialog.cs`：颜色设置行及浮层资源管理。
- `native/UI/MainForm.PanelSettings.cs`：颜色纳入设置保存流程。
- `native/UI/MainForm.Codex.cs`：移除旧颜色入口同步，保留设置接口应用背景颜色。
- `docs/USER_GUIDE.md`：更新中英文位置及保存、取消说明。

## 验证

以下命令退出码均为 0：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1 -Suite Build -Plan
pwsh -NoProfile -ExecutionPolicy Bypass -File .agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1 -Suite Build
pwsh -NoProfile -STA -ExecutionPolicy Bypass -File artifacts/ui-verification/workspace-controls-check.ps1
git diff --check
```

- Release 增量构建通过，最终输出 0 错误、0 警告。
- STA 窗口检查：中英文 × 1600/1000/760 窗口宽度 × 基础绘制/场景，共 12 组。布局按钮在工具栏内且没有重叠，面板显示切换通过。
- 颜色检查：取消、保存并重新读取、再次修改后取消均通过，预览与 Hex 同步。
- 已查看普通、580px 窄版设置截图及窄版工具栏截图，新增入口无裁切或重叠。截图及检查脚本位于 `artifacts/ui-verification/workspace-controls*`；没有进行真实高 DPI 显示器验收。
- 最终构建日志：`artifacts/validation/workspace-controls-build-2026-11-15.log`。
- 本次为 UI 位置及设置入口调整，没有重跑全量产品回归，也不代表先前渲染性能问题已通过。

共享工作树包含其他未提交改动，期间也出现其他文件的编译变化。本任务未修改这些几何、位图及渲染实现；以上构建结果对应验证时的当前工作树，不是干净基线对照。
