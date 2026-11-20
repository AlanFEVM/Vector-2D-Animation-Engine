# Wiki 配图导出

在仓库根目录使用 PowerShell 7（.NET 8 或更高版本）运行：

```powershell
pwsh -NoProfile -STA -File scripts/export-wiki-images.ps1
```

脚本先构建当前引擎到 `artifacts/wiki-export-engine`，再加载该程序集，在独立进程中创建离屏控件和示例工程。无需启动正式编辑器，也不使用屏幕复制、鼠标点击或截图坐标。设置文件保存在本次输出目录，不读取或覆盖用户设置。工具栏和菜单按控件实际尺寸出图；舞台按引擎现有二维 GDI 路径绘制，不代表 GPU 截图或三维渲染验收。

默认使用中文、碳灰主题、微圆角、简洁选中高亮、`#585454` 工作区背景和 100% 网格不透明度。可以单独选择图片或更换外观：

```powershell
pwsh -NoProfile -STA -File scripts/export-wiki-images.ps1 -Theme White -WorkspaceColor '#E6EFF4' -Image scene-canvas-dialog
pwsh -NoProfile -STA -File scripts/export-wiki-images.ps1 -Theme Charcoal -WorkspaceColor '#585454' -CornerStyle SubtleRounded -GridOpacity 100 -UpdateWiki
```

`-Theme` 支持 `Dark`、`White`、`Charcoal`。`-WorkspaceColor` 使用六位十六进制颜色。`-Image` 支持 `All`、`tool-palette`、`stage-area`、`stage-context-menu`、`stage-context-menu-selection`、`scene-canvas-start-detail`、`scene-canvas-dialog`、`stage-view-controls`、`grid-cartesian`、`grid-polar`、`grid-golden-spiral`。

右键菜单分别导出无选区和选中示例图形两种状态，通过实际菜单打开流程更新命令。画布引导图直接导出真实引导控件；顶部控制区按控件实时边界裁切，将网格、吸附、视图按钮三排紧凑排列，不使用固定屏幕坐标。

`-CornerStyle` 支持 `Square`、`SubtleRounded`；`-SimpleSelectionHighlight` 默认启用，可传入 `$false` 关闭。画布弹窗导出复用实际内容控件并使用独立离屏宿主，不触发弹窗的后台图片加载，当前示例固定使用纯色背景。

每次输出到 `artifacts/wiki-export/<本次编号>/`，附带 `manifest.json`，记录引擎哈希、主题、背景色、实际 DPI、图片尺寸、绘制方式和 PNG 哈希。图片通过尺寸及非空像素检查后，`-UpdateWiki` 才按固定文件名逐张替换 `docs/encyclopedia/public/images/`。默认只生成预览，不替换 Wiki。像素抽样只能发现明显空图，不能代替人工检查文字、裁剪和控件状态。

替换配图后执行 `npm.cmd --prefix docs/encyclopedia run build`。脚本失败时本轮不会开始替换；替换过程中若遇到文件系统错误，可能已有部分图片更新，可从本次完整输出重新执行。

`-SkipBuild` 仅用于确认源代码与已构建程序集一致时；`-AssemblyPath` 可指定其它兼容构建。内部控件或绘制接口更名时脚本会明确失败，需要同步维护对应名称。
