# Vector 2D Animation Engine 用户指南

本文档描述当前代码对应的操作行为，重点说明工作流、交互规则和排查方法。具体版本的新增、修复和兼容性差异请查看[文档索引](README.md)中的 Release History。

界面刷新会复用当前元件标签和添加按钮；灯光参数变化采用列表增量更新，保持当前选择与滚动位置。主动选择其他灯光时，列表仍会定位到所选项。此优化不改变元件切换、拖放或灯光编辑命令。

初次使用时，可悬停顶部工作区标签查看用途与快捷键：先在“基础绘图”绘制元件（Ctrl+1），再到“场景与动画”装配并制作关键帧动画（Ctrl+2），需要相机构图时使用“镜头与导演”（Ctrl+3）。吸附栏的网格、对象、对齐和角度按钮提供用途与开关状态提示；总吸附关闭时会说明前置条件，数值框会解释单位和步长。提示随中英文语言与当前设置更新，不改变已选的吸附选项。

## 使用本指南

时间轴分组栏右侧集中放置新建分组加号与分组设置按钮，与上方新建图层加号分开。分组设置提供当前分组的颜色、恢复默认颜色、重命名、删除和排序；选择“全部”时可按分组进入这些操作。内置分组不能重命名或删除。

The timeline group bar keeps the create-group plus beside group settings on the right, separate from the add-layer plus above. Settings exposes group color, color reset, rename, delete and ordering; in All, choose a group submenu. Built-in groups cannot be renamed or deleted.

### 时间轴父子关系（二维编辑跟随）

关系栏使用简短的“父子关系 / Parent links”表头，完整说明可通过悬停查看。父子关系图标位于时间轴顶部工具栏的“＋”与“独显”之间，点击切换关系栏显示。节点与图层行对齐，折线箭头指向父层；当前行及其祖先链突出显示，便于区分同一父层下的不同子层。

The gutter is expanded by default and uses the compact “Parent links” header with a full hover description. Nodes align with layer rows, elbow arrows point to parent layers, and the active row and its ancestor chain are highlighted without highlighting sibling links.

时间轴提供默认展开的独立父子关系栏，使用两层相连的图标切换显示。从子图层节点拖到父图层节点建立关系；右键节点可解除关系或定位父层。关系线与文件夹缩进分开，不改变图层绘制顺序、文件夹归属、曝光或补间。普通内容层可以建立多级关系；新增或更换父级只允许同一时间轴分组（TabGroup）内操作，在“全部”视图中也不能跨组绑定；不允许自绑定、循环或跨时间轴引用。已有关系不会因本限制自动删除，仍可解除。折叠或筛选隐藏了父层时，关系仍保留并显示提示。

Adding or changing a parent is allowed only between layers in the same timeline tab group, including in All view. Existing links are preserved and can still be detached.

关系编辑支持撤销和工程保存重开，删除父层会清理关联关系。基础绘图中，使用选择工具整体拖动父层对象，或用自由变换移动、旋转、缩放，会对后代图层当前曝光内容应用同一次变换；子层仍可独立编辑。隐藏的后代内容也参与跟随，父子同时选中不会重复变换。Auto Key 开启时，为受影响的后代内容层同时准备当前帧关键帧；关闭时编辑各层当前持有的 Cel。场景二维视图的选择拖动与自由变换会联动后代层的实例。绑定和解除关系本身不改变位置。

在子图层节点上右键选择“解除父子关系”，只解除该层与其父层的连接，保留该层自己的子级连接和内容位置；可通过撤销恢复。

Right-click a child layer node and choose “Remove layer parent” to detach only that layer from its parent. Its own child links and content positions are preserved; Undo restores the removed link.

基础绘图中的元件实例也支持实例之间的父子编辑跟随：拖动父层实例会移动后代层实例，移动子层不会反向移动祖先层。

Nested instances in Basic Drawing also support linked instance edits: dragging a parent-layer instance moves descendant-layer instances without moving ancestor layers when a child is edited.

**当前实现是编辑联动，不是独立图层变换轨道**：变换结果写入后代的几何或实例状态，保存、命中和撤销使用实际变换后的内容。播放时不会仅根据父层已有动画动态生成子层运动；形状/节点编辑、属性数值编辑、3D 操作尚不属于此联动入口。

The timeline parenting gutter stores parent links independently of folders and paint order. Whole-object drag and Free Transform in Basic Drawing propagate the edit to descendant layers; scene 2D pointer transforms include descendant instances. These are persisted edit-time transforms, not runtime inheritance of existing parent animation. Binding and detaching preserve positions. Node edits, numeric property edits and 3D operations are outside these linked edit paths.

在镜头属性的“宽高比”中，可选择 **16:9、9:16、4:3、3:2、1:1、4:5、21:9**，或使用默认的“跟随场景”。每个镜头独立保存比例，切换比例不会修改场景尺寸；比例在目标关键帧处切换，位置和镜头参数继续按原有补间规则变化。“重置相机”同时恢复为跟随场景。

相机取景框支持通过缩放、焦距或正交范围调整大小，保持所选宽高比，不再限制为最小 1920 × 1080 vu。这里的 vu 是场景单位，不是导出分辨率。视差深度拖动中仅同步参数和预览，松开后完成面板刷新；一次拖动保留一次撤销。

Camera framing can be resized below 1920 x 1080 scene units while preserving its aspect ratio. Parallax depth drags update values and previews without rebuilding the camera list on each move; releasing completes the panel refresh and retains one undo entry.

| 目标 | 建议章节 |
| --- | --- |
| 首次启动并完成一段动画 | [启动与更新](#1-启动与更新)、[五分钟上手](#2-五分钟上手) |
| 绘制、填色和调整基础着色 | [绘制工具](#5-绘制工具)、[Fill、Stroke 与基础着色](#7-fillstroke-与基础着色) |
| 选择、节点编辑和任意变形 | [选择与变形](#6-选择与变形) |
| 管理图层、Cel、关键帧和补间 | [图层](#8-图层)、[时间轴与关键帧](#9-时间轴与关键帧) |
| 将封闭形状破碎并生成碎片动画 | [时间轴与关键帧](#9-时间轴与关键帧)中的[随机破碎形状修改器](#随机破碎形状修改器) |
| 装配实例和管理素材 | [Scene、实例与 Vault](#10-scene实例与-vault) |
| 导出 SVG 或收发可再导入的元件文件 | [SVG、图片与文本](#11-svg图片与文本)中的[导出单个元件为 SVG](#导出单个元件为-svg)、[导出与导入元件文件（.V2DSymbol）](#导出与导入元件文件v2dsymbol) |
| 导入图片并调整滤镜、PPI 等导入设置 | [SVG、图片与文本](#11-svg图片与文本)中的[导入图片](#导入图片) |
| 保存工程或排查问题 | [保存、打开与工程结构](#12-保存打开与工程结构)、[常见问题](#14-常见问题) |
| 用 Codex / MCP 自动操作编辑器 | [设置与快捷键](#13-设置与快捷键)中的 [Codex / MCP 接口](#codex-mcp-接口) |
| 查询快捷键 | [固定快捷键与焦点](#固定快捷键与焦点)、[工具快捷键方案](#工具快捷键方案) |
| 查看当前不支持的能力 | [当前限制](#15-当前限制) |
| 从源码建立版本并发布 | [源码发布管理器](#17-源码发布管理器) |

文档职责：本指南记录当前稳定行为；版本化 Release Notes 只记录对应发布包的变化；仓库根目录 [README](../README.md) 面向源码开发、验证和打包。

章节速览：

| | | |
| --- | --- | --- |
| [1. 启动与更新](#1-启动与更新) | [2. 五分钟上手](#2-五分钟上手) | [3. 界面与工作区](#3-界面与工作区) |
| [4. 视图操作](#4-视图操作) | [5. 绘制工具](#5-绘制工具) | [6. 选择与变形](#6-选择与变形) |
| [7. Fill、Stroke 与基础着色](#7-fillstroke-与基础着色) | [8. 图层](#8-图层) | [9. 时间轴与关键帧](#9-时间轴与关键帧) |
| [10. Scene、实例与 Vault](#10-scene实例与-vault) | [11. SVG、图片与文本](#11-svg图片与文本) | [12. 保存、打开与工程结构](#12-保存打开与工程结构) |
| [13. 设置与快捷键](#13-设置与快捷键) | [14. 常见问题](#14-常见问题) | [15. 当前限制](#15-当前限制) |
| [16. 操作原则速查](#16-操作原则速查) | [17. 源码发布管理器](#17-源码发布管理器) | |

阅读约定：`代码样式` 表示界面上的英文控件名或按键；「机制」说明底层规则，「操作提示」给出容易踩坑的实操建议。界面可在 `Settings...` 中切换 English 或简体中文，下文保留英文控件名以便对照。

## 1. 启动与更新

正式版本只有一个文件：

```text
VectorAnimationEngine-<version>-win-x64.exe
```

把 EXE 放在可写目录中再启动。首次运行会自动下载并安装所需的 .NET 8 Core 与 Windows Desktop x64 运行环境，需要网络连接；以后会复用同目录的 `.Runtime`。

正式 EXE、首次运行准备窗口和编辑器任务栏使用统一的软件图标：深色圆角底、青色矢量 V 与橙色播放箭头。图标内嵌在程序中，无需随包附带图标文件。

编辑器启动横幅会显示当前软件版本，并在每次启动时随机展示一条从本指南提炼的操作技巧。提示随应用语言显示为中文或英文，沿用当前主题；同一次启动期间提示保持不变，工作区准备好后横幅自动关闭。

更新时先关闭软件，再用新版 EXE 启动即可。新版会从自身重新部署 `.V2DEngine`，但保留 `.Runtime`、工程、日志和用户数据。不要把正式 EXE 改名为源码仓库根目录的 `VectorAnimationEngine.exe`，该文件只用于源码开发。

添加元件（包括开放画布生成与 `F8` 转换）会先弹出名称输入框，默认使用三位编号 `001`、`002`……，可修改后确认；取消或输入空白名称不会创建元件。初始空元件尚未绘制、重命名或被引用时，不占用新增编号，因此从初始开放画布第一次生成元件默认是 `001`；已使用的同名编号会自动跳过。

操作提示：

- 软件默认打开一个空工程、一个元件和一个图层，不会自动生成压测内容。
- 标题中的 `*` 表示当前工程有未保存修改。
- 保存失败时仍保留原工程的保存目标和未保存状态；从 `.v2d` 另存为文件夹工程失败后，可继续用 `Ctrl+S` 保存回原 `.v2d`。图片或 SVG 工程在打包失败后也可解除文件占用或修复磁盘问题再重试，不必重新导入资源。
- A failed save preserves the original save target and unsaved state. If folder Save As fails for a `.v2d` project, `Ctrl+S` still saves to the original package; image and SVG assets remain available for retry after packaging fails.
- 启动或运行环境安装失败时，先查看程序目录下的 `logs/bootstrap.log`；应用运行异常查看 `logs/native-*.log`。
- 从源码使用仓库根目录开发启动器时，进程级热更新会先保存当前工程，再显示启动状态窗，直到新编辑器窗口完成恢复。若源码暂时无法编译，状态窗会继续保留；修正编译错误后 `dotnet watch` 会继续启动，不需要重复打开启动器。
- 源码开发启动器使用仓库内 `artifacts/development-temp` 保存开发临时文件、NuGet 临时锁和编辑器重启暂存，不依赖系统临时目录的权限，也不修改系统环境变量。开发进程退出后才能清理该目录；未保存工程与重启恢复所需资源也可能位于其中。
- 源码开发版将语言等用户设置保存在 `artifacts/development-user-data/<Windows SID>/settings.json`，按 Windows 用户隔离。首次读取沿用原有 AppData 设置，保存时写入工作区；请保留该目录以保留偏好。正式发行版继续使用 AppData。
- The source development launcher keeps preferences in `artifacts/development-user-data/<Windows SID>/settings.json`, importing existing AppData preferences on first use. Keep this directory to preserve preferences. Distribution builds continue to use AppData.
- 开发启动进程连续失败时最多自动重试三次，随后退出并提示查看 `logs/launcher.log`，避免长时间反复显示“正在启动”。源码编译错误导致的 watch 等待仍可在修正代码后继续。
- The source development launcher uses `artifacts/development-temp` for temporary files, NuGet scratch locks and editor restart handoff without changing system environment settings. Keep this directory while the editor is running or unsaved/restart data is needed. Failed development processes retry at most three times before reporting `logs/launcher.log`; watch can still resume after compilation errors are corrected.
- 热更新恢复会把最小化窗口恢复为普通窗口，并将已离开当前显示器工作区的坐标移回可见区域。

## 2. 五分钟上手

1. 保持在 `Basic Drawing` 工作区。
2. 在左侧工具栏选择矩形、线条、铅笔或笔刷，在中央舞台拖动绘制。
3. 在右侧着色面板切换 `Fill` 或 `Stroke`，调整颜色、透明度和线宽。颜色页顶部为静态图标标题，色域与 Hex 常驻；颜色通道及和谐色按需独立展开。填色／描边目标只显示色块与名称，纯色编码通过悬停提示查看；渐变目标保留类型和色标数量摘要。绘制模式不再单独显示“绘制”标签；线宽单位仅显示在数值框内。渐变的增删按钮位于色标条旁，选中色标的颜色与位置合并在同一行。自研数值输入框使用连续统一外框，上下箭头位于外框内部且无独立盒状边框，悬停和按下箭头时轻量高亮，聚焦或调值时强调外框；保留键盘输入、滚轮和拖动调值。着色面板通过简洁分割线与分组留白区分材质设置、颜色选择和通道参数，参数行保留清晰间距；使用紧凑的填色／描边目标、等宽色彩模型标签和对齐的通道／Hex 参数列；和谐色按标题、规则和色块纵向排列。不可编辑下拉框采用低对比连续外框与实心小箭头，展开时箭头朝上，聚焦或展开时强调边框，保留原有选项和键盘操作；关闭状态高度与列表选项行高分别设置，配色规则行按实际控件高度布局，避免系统缩放后裁掉底边。文字按主次分级：模块标题为 8.5pt 常规字重和柔和主题色，颜色分组为 9pt 加粗，字段与选项为 8.5pt，渐变摘要为 8pt 辅助文字。填色／描边文字按实际字体行高排列；绘制模式按按钮实际宽度和当前语言选用完整或短标签，折叠后再展开时保持当前缩放比例的行高。绘制模式标签栏使用较小的统一字体与紧凑间距，保留清晰的选中高亮和原有点击高度。颜色顶部为静态图标标题，不接收折叠操作；颜色通道和和谐色保留各自的折叠入口。当前图层名称靠近“图层”标题并左对齐，名称前的小色标与时间轴图层颜色一致；长名称显示省略号，悬停显示完整名称。混合模式独占下方输入行。
4. 使用 `Select` 选择对象并拖动；需要缩放、旋转或倾斜时切换 `Free Transform`。
5. 在底部时间轴点击 `+` 新建图层，使用 `F6` 创建关键帧，拖动红色播放头切换帧。
6. 按 `Enter` 播放或暂停。
7. 使用 `Ctrl+S` 保存工程；首次保存默认生成单个 `.v2d` 工程文件，选择保存位置即可。

机制：绘制内容总是写入“当前元件 > 当前活动图层 > 当前 Cel”。如果操作没有出现在预期位置，优先检查当前工作区、活动图层、播放头和图层锁定状态。

## 3. 界面与工作区

检查器中的场景设置、播放设置和属性面板按独立组件排列。源码定制布局时可以分别移动或组合这些面板；形状参数与图片导入信息仍通过各自控件编辑。具体组合接口和组件边界见 [UI 组件组合说明](UI_COMPOSITION.md)。

主界面分为五个区域：

- 顶部栏：主菜单、工程名称、FPS 和窗口控制。属性、时间轴与工作区颜色入口位于画布标题区的中间区域；切换工作区时保留相同高度位置；颜色入口显示色样、名称和 Hex，空间较窄时收紧为色样。
- 左侧悬浮工具栏：绘制、选择、变形、填充、擦除及 Vault。
- 中央舞台：绘制、选择、预览和场景装配。
- 右侧检查器：基础着色、对象、图层混合、场景和动画参数。在场景与动画的“镜头”分组或“全部”中选中相机时，显示相机管理面板和取景预览；选回普通图层恢复原检查器。
- 底部时间轴：图层、帧、关键帧、洋葱皮和播放头。

左侧工具栏的吸管、文本、填充、橡皮擦和 Vault 分别使用滴管、衬线 T、倾斜颜料桶与液滴、斜切橡皮和素材盒图标，采用与矩形等形状工具一致的半透明均匀填色和描边，随当前主题和按钮状态调整；工具操作与快捷键不变。

工作区标签和元件标签会随 Windows 显示缩放比例同步调整按钮尺寸；顶部工程标题按实际字体高度居中。在高 DPI 或大屏幕缩放环境中，图标放大时文字仍会保留足够显示空间，过长名称则使用省略号。

属性、时间轴和工作区颜色入口在工作区标签所在的第一行内垂直居中，元件标签与吸附栏使用第二行。元件实例检查器的标签、输入框和面板高度随 DPI 同步调整；窄窗口中的透明度滑条为百分比数值保留空间。

Properties, Timeline and workspace color controls are centered in the first workspace header row; symbol tabs and snapping occupy the second row. Symbol Instance labels, fields and panel height scale together with DPI, and the opacity slider reserves room for its percentage in narrow inspectors.

底部状态栏中的“渲染 FPS”统计实际完成呈现的画面次数；即使时间轴帧号未变化，重复呈现也会计入。它与项目设置的“动画 FPS”是两个独立指标。

两个工作区的职责不同；镜头已集成到场景与动画：

| 工作区 | 用途 | 是否直接绘制 |
| --- | --- | --- |
| `Basic Drawing` | 编辑可复用的元件、图层和 Cel | 是 |
| `Scene & Animation` | 装配场景、制作关键帧动画，并通过时间轴的“镜头”分组管理相机 | 仅编辑 Scene Mask 时可以 |

相机不再使用单独的顶部工作区。进入 `Scene & Animation` 后，时间轴默认显示“全部”；场景时间轴初始包含“图层”“地形”“镜头”分组。“镜头”只显示相机轨道；进入时优先选中之前的相机，否则选中第一个可用相机，右侧显示相机面板。没有相机时也可从此面板新建。在“全部”中，相机轨道固定排在普通图层上方，不受图层分组排序、移动或文件夹／遮罩关系影响；相机不能移入普通分组，普通图层也不能移入“镜头”。选中普通图层后恢复场景检查器。相机仍观察完整场景，不拥有场景图层。

机制：元件是可复用的绘制资产；Scene 保存元件实例，不复制其源几何。修改源元件后，引用它的场景和嵌套实例会使用新内容。

基础绘制顶部的“当前 / 开放”（`Current scope` / `Open scope`）切换实际编辑范围。“当前”沿用原有逻辑，整个画布为当前元件服务；“开放”进入独立的项目画布，不改变当前元件的几何。开放画布随工程保存、打开，不在 Vault 的可复用元件列表中显示，也不能拖入场景。切换画布会结束当前绘制操作并清空该编辑上下文的撤销记录。

在开放画布中选中图案后，点击“＋元件”（`Symbol`）或按 `F8`，仅将当前帧选中的图案复制成新元件；未选内容不会加入，开放画布原内容保持不变，并继续留在开放画布。生成操作可以在该上下文中用 `Ctrl+Z` 撤销；切回“当前”会编辑最近生成的元件。当前支持同一个未锁定绘制图层中的图案（含选中 Fill/Stroke 部分），不支持跨图层或嵌套实例选区；空选区不创建元件并显示提示。要编辑其它元件，从 Vault 打开即可返回“当前”。

操作提示：

- `F1` 显示或隐藏右侧检查器，`F2` 显示或隐藏时间轴。
- `Ctrl+1` / `Ctrl+2` 切换到基础绘制（`Basic Drawing`）／场景与动画（`Scene & Animation`）；`Ctrl+3` 直接打开场景与动画时间轴里的“镜头”分组。使用键盘上方数字键；`Ctrl+4`–`Ctrl+9` 暂为预留。
- 工作区标签获得焦点后，可使用方向键切换相邻工作区，`Home` / `End` 跳到首个或末个工作区。
- 主窗口按当前显示器的可用工作区设置初始尺寸；最小尺寸采用随 DPI 缩放的 760 × 520 逻辑像素，并限制在可用工作区内，便于分屏。移动到不同分辨率或 DPI 缩放比例的显示器后会重新约束窗口，并按调整后的实际大小布局；重启恢复时保留符合下限的分屏尺寸。
- 窗口变窄或变矮时，Vault、右侧检查器和底部时间轴会按可用空间收紧；画布上方指标会按空间隐藏，属性、时间轴和颜色入口随工作区标签行的可用空间收紧，缩放入口保持在右侧；吸附栏收紧为六个开关，网格间距和角度步长输入框在恢复宽窗口后重新显示。空间不足时顶部 FPS 输入会暂时隐藏。工作区、网格与工具栏也会自动收紧或换列，核心窗口命令始终保留。
- 检查器内容较长时可滚轮滚动；刷新后会尽量保留当前位置。
- 随工具或选择出现的笔尖、混色、形状、文本、元件实例、滤镜、补间曲线、图片信息及 3D 材质／灯光面板沿用轻量模块标题；线条起始／末端连接选项使用紧凑按钮，形状参数行不会随面板空白拉高。
- 模块标题在窄栏中以省略号显示，禁用时保持清晰的单色文字，不叠加系统浮雕阴影。形状边数／顶点数采用 32 逻辑像素高的标准输入框。图片信息逐行显示，长路径省略但不会挤走后续参数；悬停可查看完整信息，底部设置按钮采用主题图标与标准高度。

Inspector module titles ellipsize in narrow panels and retain clean, single-color disabled text. Polygon sides and star points use standard 32-logical-pixel fields. Image metadata stays on separate rows; long paths ellipsize without displacing later settings, and hovering reveals the full summary. The image settings action uses a themed icon and standard button height.

- 文本、绘制工具、笔尖、元件实例、滤镜、空间材质与灯光等条件面板统一使用主题字段标签、分区标题和输入框间距；完整属性输入框采用 32 逻辑像素高度，普通数值使用 11pt 字体，滑条和 XYZ 联合输入保持紧凑。空间材质的覆盖开关、预设和阴影开关分行显示，避免窄栏互相挤压。镜头、视差、文档和播放面板沿用同一文字层级；灯光名称及 Codex / MCP 单行文本输入使用主题边框与垂直居中，密码令牌继续遮蔽。

Text, drawing tools, brush tips, symbol instances, filters, spatial materials and lighting share themed field labels, section headings and spacing. Full property fields are 32 logical pixels high, ordinary numeric values use 11pt text, and sliders and grouped XYZ editors remain compact. Material override, preset and shadow switches use separate rows in narrow panels. Camera, parallax, document and playback panels share the same text hierarchy. Light names and single-line Codex / MCP fields use themed borders and vertically centered text; bearer tokens remain masked.

浅色主题的下拉框文字不再出现黑色重影；展开和谐色时完整显示规则及色块，滤镜底部颜色按钮也保留完整高度。

Light-theme selectors no longer produce black text ghosting. Expanding Harmony reveals the complete rule and swatches, and filter color actions retain their full height.
- 着色面板的描边“宽度”行采用左侧标签、右侧约半栏宽数值框的属性布局，随面板宽度等比例伸缩，切换填色／描边或调整侧栏宽度时重新按当前容器计算边界，避免沿用窄栏尺寸；在渐变模式按钮与下方颜色分组之间保留上下留白，数值框采用 32 个逻辑像素的标准高度与 11pt 常规数值／单位文字，不使用偏扁的紧凑高度。
- “颜色”“高级设置”等折叠标题悬停、按下或展开时仅强调文字与箭头颜色，不显示整行背景高亮；键盘焦点提示保留。
- 场景与动画的播放参数（FPS、开始、结束）与描边“宽度”使用相同的半栏输入宽度、32 个逻辑像素高度与 11pt 数值字体，循环开关保持原有样式。
- 图层混合模式收起时采用约半栏宽、32 个逻辑像素高，文字字号与左侧“混合模式”标签一致，不套用数值输入框的 11pt 字号；面板随控件实际高度和 DPI 保留完整下沿及底部留白。展开菜单保持原有字号、行高、选项及选择行为。
- 混合模式、描边宽度、和谐色与 HEX 完整输入框统一左右边界和 32 个逻辑像素高度；文字与数值字号分别保留，颜色通道滑条旁的小数值框仍采用紧凑布局。
- 展开的颜色编辑区在模式按钮上下、选色区顶部与底部分隔线前保留额外留白；和谐色小色块与上方下拉框保留间隔，通道及 HEX 参数区与上方分隔线留出更大距离，不改变输入框尺寸和列对齐。
- 在基础绘制、场景与动画、镜头与导演之间切换时，右侧检查器使用约 180ms 的淡入淡出和轻微上移过渡；连续切换以最后一次选择为准。窗口尺寸变化或系统关闭动画时直接显示目标面板。
- 镜头与导演顶部相机标题与图层标题采用相同的顶部留白；相机数量、时长及帧／关键帧统计收纳到标题悬停提示中，不占用常驻参数区。
- 相机单行属性采用半栏宽、32 逻辑像素高的统一输入框；文字保持常规字号，数值使用 11pt。位置与旋转各使用一个半栏宽的暗色向量输入组，XYZ 纵向排列；左侧采用主题强调色底的轴标徽章和轴向细色条，行间使用轻分隔线，点击轴标可聚焦对应数值。保留数值编辑、步进和连续编辑行为；下拉菜单及相机编辑行为保持不变。
- 镜头与导演连续参数框采用与播放面板一致的 8 逻辑像素净间距，向量组之间及其相邻参数也沿用该间距，不额外拉大空行。
- 场景与动画、播放及镜头与导演面板沿用着色模块的轻量标题和留白；参数标签／输入列保持一致的对齐关系，场景和相机列表与属性区保留分组间距，循环字段只显示一次文字标签。
- 检查器的“图层”和“着色”模块标题统一采用较小的常规字号和随背景变化的柔和颜色，靠近左侧边缘。图层与下方检查器之间使用两端贴齐面板边缘的模块分隔线，颜色与着色内部分组线一致、粗细为其 1.5 倍。标题、输入行及模块之间保留较宽松的间距。
- 画布右上角吸附开关选中后显示与时间轴开关一致的圆角胶囊底色；未选中时跟随工具栏背景。
- 舞台右键菜单按任务分组：`Delete`（删除嵌套实例）与 `Break Apart` 为一级命令；`Line / 线条` 收纳 `Convert Line(s) to Fill` 与 `Merge and Simplify Lines`；`Transform / 变换` 收纳水平／垂直翻转和 `Bring Forward`／`Send Backward`；`Symbol / 元件` 收纳 SVG 导出、`.V2DSymbol` 导出与导入；`Fracture / 破碎` 收纳 `Add Collision Terrain` 与 `Random Fracture`。分组在没有可用命令时不显示；3D 场景视图的 `Add light` 子菜单与绘制模式下的右键菜单共用同一套分组。
- 画布标题区中间的工作区颜色入口仅色样方框可点击，名称与 Hex 在宽布局中直接显示、窄布局中通过悬停提示显示；入口没有外层边框和阴影。它只改变编辑背景，不写入工程内容，也不改变对象材质。点击色样后展开专业调色浮层，可使用 HSV 色域、Hue、RGB、Hex 和预设色；调整立即生效，点击外部或按 `Esc` 关闭时保留当前颜色并保存为应用设置。

镜头与导演工作区中，舞台、相机面板按钮或时间轴获得焦点时，Ctrl+Z 撤销最近的场景／镜头编辑；文字和数值编辑框获得焦点时优先保留控件自身的编辑按键。

### 场景画布与背景 / Scene Canvas And Background

在主菜单 `Project Files → Project settings… / 项目文件 → 项目设置…`，或未选择对象时的文档信息面板点击 **Canvas & background… / 画布与背景…**，设置当前活动场景的画布。每个场景独立保存；基础绘制中的元件仍保持透明，画布设置不改变元件内容或编辑器外围的工作区底色。

空场景尚未设置画布时，场景与动画工作区显示“尚未添加画布与背景”的轻量引导。可以打开设置，也可以点击“直接开始”；放入元件、创建镜头或图层、绘制实际内容或建立有效关键帧后，引导自动消失，删除或撤销内容不会反复弹回。平移、缩放和切换工具不会自动创建画布或背景。提示消失后，文档信息仍准确显示“未添加”，设置入口始终可用。

- **画布**：提供标准 `640 × 480`、高清 `1280 × 720`、全高清 `1920 × 1080`、4K `3840 × 2160`、竖屏 `1080 × 1920` 和方形 `1080 × 1080` 预设，也可输入 1–32768 像素的宽高，锁定比例或交换横竖。改变尺寸以场景原点为中心改变边界，不缩放、移动或删除已有对象，不改动关键帧或镜头位置；镜头“跟随场景”使用所设画布比例，独立画幅预设不变。
- **背景**：可选无背景、纯色或图片。无背景在编辑器中以棋盘格表示透明；纯色支持自选颜色和不透明度。背景位于内容下方，不参与普通选择、擦除、图层遮罩或场景灯光。需要透视、受光或动画的背景图，应作为普通图片对象放入场景。
- **图片**：可以选择项目内图片或外部文件。外部文件仅在点击“应用”后导入素材库，随工程保存，不依赖原电脑上的图片路径。完整显示保留整张图片，铺满画布等比裁切超出部分，原始大小按源像素放置。缩放、显示宽高始终等比；偏移使用画布像素，可居中、对齐或让画布匹配图片尺寸。图片背景的留白/透明区域还可设置底色。原图尺寸只读。
- **编辑与预览**：舞台可以压暗画布外区域，但仍允许在画布外编辑；背景不被误选。2D 前视画布随视图平移缩放，Reference 3D 和非前视参考视图以画面底图显示，不是三维平面。应用后可 `Ctrl+Z` 撤销画布设置，`Ctrl+Y` 或 `Ctrl+Shift+Z` 重做；文字/数值编辑框仍保留自身编辑按键。取消弹窗不修改工程；旧工程不会自动增加白底或画布。

预设卡片显示名称、尺寸和画幅示意；颜色按钮显示色块，透明色使用棋盘格。直角和微圆角界面均保留这些提示。弹窗默认展示画布尺寸、背景类型及对应的常用选项；「高级设置」默认折叠，展开后可调整不透明度、图片等比缩放、显示宽高、偏移、对齐，以及画布外压暗，并查看图片来源和原始尺寸。折叠只隐藏控件，不重置已有设置；左侧预览和底部应用按钮保持独立可见。

参数行按标签 48% / 输入 52% 分配宽度。背景类型、项目图片和图片适配的下拉框沿用标准字段高度与标签字号，收起状态与相邻输入框对齐；展开菜单保留原有字号、行高及键盘选择行为。

Parameter rows use 48% label / 52% input columns. Background type, project image and image-fit selectors use the standard closed-field height and label typography, aligned with adjacent inputs. Popup typography, row heights and keyboard selection remain unchanged.

相机观察窗包含场景背景，透明棋盘格、画布外压暗和边框属于编辑辅助，不烘焙进相机预览。当前元件 SVG 导出仍只导出元件本身，不附带场景画布或背景；本功能不增加新的视频、动画或场景图片导出格式。

Use **Project Files → Project settings…**, or **Canvas & background…** in document information, to configure the active scene. Canvas sizes and transparent/solid/image backgrounds are scene-owned and travel with saved projects. Presets, locked dimensions, proportional image sizing, fit modes, alignment, opacity and canvas-pixel offsets do not rescale artwork or rewrite animation. Applying commits once; Cancel leaves the project unchanged. The empty-scene guide dismisses on actual work or **Start creating**, while settings remain available. Editor checkerboards, outside dimming and borders stay out of camera previews; symbol SVG exports remain transparent and do not acquire scene backgrounds. Canvas settings support undo and redo without taking shortcuts away from text editors.

Preset cards show their names, dimensions and aspect-ratio illustrations. Color buttons include swatches, with a checkerboard for transparency, in both square and subtly rounded interface styles. Common canvas and background choices appear first; Advanced settings starts collapsed and contains opacity, proportional image sizing, offsets, alignment, source details and outside dimming. Collapsing never resets values, and the preview and action footer stay separate from the scrolling settings.

弹窗恢复各自的原有尺寸（逻辑像素）：设置 820×700、操作偏好 620×380、快捷键方案 860×680、画布与背景 900×680、图层组管理 1000×640，不再统一放大。常规设置内容超出可见区域时可滚动；画布与背景保留高级设置折叠和预览布局。

Dialogs retain their original sizes in logical pixels: Settings 820×700, operation preferences 620×380, shortcut profiles 860×680, canvas backgrounds 900×680 and layer-group management 1000×640. General settings scroll when content exceeds the visible area; canvas backgrounds retain collapsible advanced settings and the preview layout.

## 4. 视图操作

2D 舞台：

- 鼠标滚轮：缩放。
- 鼠标中键拖动：平移。
- `Ctrl+鼠标中键` 上下拖动：连续缩放。
- 按住 `Space` 再左键拖动：临时手形平移，松开后恢复原工具。
- `Fit Stage`：显示完整舞台。

中键平移、滚轮或按钮连续缩放和 `Space` 临时平移期间，舞台优先响应视图导航，暂时清除工具悬停提示、填充区域预览及笔刷光标，停止相关命中和吸附查询；拖动结束或连续缩放停止约 250 ms 后恢复当前工具反馈。填充、吸附点、钢笔、选择等工具均使用同一导航规则，大量元素时不会因滚动伴随的鼠标移动重新计算填充区域。点击舞台会立即结束暂停并按当前工具处理。滚轮按实际滚动量连续缩放，未触及倍率上下限时，等量反向滚动恢复原倍率。

在 `Scene & Animation` 的 2D 视图中，画布右下角提供上、下、左、右和 Front 参考视图。方向按钮只切换观察方向，不修改实例位置；所有方向均可使用鼠标中键拖动平移。Front 返回正交正视图，并按实例真实的 XYZ 位置、三维旋转和相机深度显示及命中内容。处于侧视或顶/底视图时，同样使用正交投影；在 2D 视图放置的实例默认位于 `Z = 0` 基准平面。

元素较多时，滚轮或 `Ctrl+鼠标中键` 连续缩放会暂时使用缓存的图层摘要保持交互流畅；停止输入后约 250 ms 自动恢复完整矢量绘制，最终画面不会降低清晰度。

3D Scene & Animation：

- 新场景首次进入 3D 时默认使用透视投影；所有 2D 参考视图仍按正交方式显示。
- 鼠标中键：旋转视图。
- 按住鼠标右键拖动：以当前预览摄像机位置为固定点环视，鼠标向右看右、向上看上；松开右键停止并保留当前朝向。与中键绕观察目标旋转不同，右键环视不会移动摄像机位置，也不改变倍率或投影模式。
- 右键单击后松开：打开原有场景菜单；按住但未拖动时，松开也会打开菜单。超过拖动阈值后才进入环视，环视结束时不弹出菜单。`Alt+右键` 仍用于平移，变换操作中的右键仍优先取消变换。
- `Shift+鼠标中键`：在镜头目标平面上跟随鼠标平移；正交视图不受观察距离影响，透视视图随当前深度和倍率调整移动量。
- 切换到 3D 或点击舞台后，按 `←` / `→` 沿镜头横向移动，按 `↑` / `↓` 沿当前镜头中心视线前进 / 后退；旋转或俯仰镜头后，移动方向会随镜头朝向同步改变。按住键位会连续平滑移动，按住 `Shift` 以 4 倍速度移动。
- `Ctrl+鼠标中键` 或滚轮：透视模式推近/拉远，正交模式缩放视图。滚轮按实际滚动量连续响应；未触及倍率或距离上下限时，等量反向滚动恢复原倍率。
- `Numpad 1/3/7`：前/右/顶视图；配合 `Ctrl` 查看反方向。
- 舞台顶部右侧的 `正交` / `透视` 按钮：显示当前投影并一键切换；`Numpad 5` 提供相同操作，`Home` 重置视图。

镜头过渡：切换 `正交` / `透视`、`2D` / `3D`、2D 方向参考视图、`Numpad 1/3/7` 固定视图、使用 `Home` 重置，或在 3D 视图按 `F` 聚焦所选元件时，画面会平滑过渡到目标镜头。快速重复切换会从当前画面继续，不会先跳回上一目标；过渡期间使用滚轮或拖动旋转、平移、推近/拉远时，手动操作会立即接管镜头。系统关闭界面动画或启用高对比模式时，这些切换会即时完成。

播放时切换 `2D` / `3D` 会先停止播放，再立即完成旧镜头过渡并应用新维度，避免两套投影状态同时参与绘制。3D 播放会复用光学 surface，并跳过不会影响当前画面的工作区缓存和 CPU 光栅预加载；2D 播放使用后台光栅帧快速呈现，不会进入 3D GPU 光学通道；硬件光学通道不可用时仍自动回退到原有 Direct2D/GDI 路径。

播放自然结束或手动停止后，时间轴拖动会立即恢复当前帧预览；此时可以继续播放、拖动 3D 镜头，或切换 `2D` / `3D`。若后台预加载仍在收尾，界面不会等待其完成。

CPU 光学回退的后台播放准备沿用前台播放的光学预览分辨率，避免预加载帧与正在显示的预览不一致；停止播放后恢复编辑态完整分辨率。

Background playback preparation on the CPU optical fallback uses the same optical preview resolution as foreground playback. Stopping playback restores full editing resolution.

反复播放并切换 `2D` / `3D` 后，可以继续调整窗口或面板大小、返回 `Drawing` 编辑；画布会持续更新并显示当前帧。

3D 播放对均匀环境光和没有局部光照层的对象使用等价的矢量 Direct2D 光照，避免空的逐像素 GPU dispatch；GPU 光学 surface 会按播放计划复用，编辑态仍保留完整光学 surface 契约。投影 SVG 的纹理按内容和尺寸复用缓存，播放期间不会降低纹理分辨率。

右键环视只在 3D 场景视图中启用，不修改场景实例，也不产生撤销记录。按 `Escape`、失去焦点或切换帧、工具、工作区时会结束环视，不补弹右键菜单；`F`、`Home` 和小键盘视图命令会先结束环视，再切换镜头。环视期间滚轮不改变摄像机距离。

机制：视图缩放和平移只改变观察方式，不修改对象坐标。工程内部使用矢量单位，`1 px = 25 vu`；线宽以 pt 输入，`1 pt = 0.5 px = 12.5 vu`。

顶部状态栏和底部状态栏的 `Zoom` 百分比始终显示**当前工作区**的缩放，与画面一致。三个工作区的缩放相互独立：Basic Drawing 与 2D Front 场景视图使用绘制相机倍率，3D 场景视图、侧向/顶底参考视图和镜头工作区使用参考相机倍率。因此在不同工作区之间切换时，百分比会切换为各自保存的值，而不会带着上一个工作区的倍率。顶部工具条的放大/缩小按钮与滚轮都会作用于当前工作区的相机。绘制相机的倍率范围约为 2%–6400%，参考相机的倍率范围为 25%–800%；3D 视图的滚轮推拉改变摄像机距离，属于观察距离而非倍率，不计入 `Zoom` 百分比。

大量选中对象时，Basic Drawing 的 GPU 绘制路径会复用完整的矢量选区绘制指令；平移只改变这些指令的屏幕偏移，不隐藏选框或将场景截图拉伸。滚轮和拖动缩放复用世界坐标矢量选区几何，选框线宽和控制点尺寸保持屏幕像素大小。对象、选区、帧或图层变化后重新生成，设备重建时释放并重建缓存；非 GPU 路径保留原有绘制。鼠标拖动和连续滚轮缩放期间固定选区高亮相位，交互结束后恢复动画。全部对象已选中时直接绘制原始矢量场景，避免缩放预览先画 LOD、随后重复画全部选中对象；纯画布导航不执行对象命中所需的延迟覆盖层准备。

选中的对象数量很大时（数万对象），选区高亮仍保持交互流畅：轮廓按高亮颜色合并为极少数矢量几何，一帧只需个位数绘制调用；整对象拖动直接平移已录制的选区指令，不再逐帧重建；每个选中对象被上方不透明填充遮挡的细化会按预算裁剪，裁剪预算用尽后仍绘制完整轮廓，不会跳过任何选中对象。为避免选中量本身拖慢画面，选中超过 128 个对象期间，相机仍在移动的帧用 LOD 马赛克表示选中对象，相机停下的第一帧恢复清晰绘制；选中超过 512 个对象时，上述遮挡细化不再参与，轮廓照常绘制。合并的选区轮廓会按当前缩放做亚像素抽稀（容差约 1/3 屏幕像素），顶点减少但轮廓外观不变。

对象数量不多但曲线很密的场景（例如导入 SVG 后拆散得到的上千个矢量对象）在相机移动的帧同样使用舞台马赛克 LOD：当场景对象数不少于 700 且顶点量较大时，平移与缩放过程中显示按真实场景降采样得到的马赛克，相机停止后的下一帧恢复逐对象的精确绘制。该策略只在图层结构为纯分组（文件夹/元件层、Normal 或 Isolate 混合）时启用；含图层遮罩或真实混合模式的场景始终保持精确绘制。它只影响交互期的显示分辨率，不改变对象几何、颜色合成结果与导出内容。

## 5. 绘制工具

浅色（White）主题下，未选中的工具栏图标使用柔和的灰色轮廓，减少近黑描边的突兀感；工具栏背景、边框、深色主题和选中状态保持原有样式。高对比度模式沿用系统颜色。

常用工具：

- `Shape`：Rectangle、Ellipse、Triangle、Polygon、Star。拖动时按 `Shift` 保持等比，按 `Ctrl` 从中心绘制。
- `Line`：绘制按二次贝塞尔编辑的独立线段；拖动预览直接使用当前 `Stroke` 颜色和线宽，`Shift` 按当前角度步长约束方向。切换到 `Select` 后，直接拖动端点手柄或未选中线段的中段可拉出平滑弧线，保持单控制点的二次编辑；`Ctrl+拖动` 中段会从命中处拆成两条独立线段并拖动它们共享的角点，`Alt+拖动` 端点会延伸出相连的分支线段。
- `Pen`：连续建立直线或三次贝塞尔路径；拖动新锚点建立方向手柄，单击首锚点闭合。
- `Simple Pen`：逐段绘制二次贝塞尔曲线，使用一个控制点调整曲率。
- `Pencil`：生成开放描边，使用 `Stroke` 材质；激活后可在右侧 `Pencil` 面板把 `Smoothing` 调整为 `0–100%`，数值越高，拖动预览和最终路径越平滑，并会在误差允许范围内使用更少的贝塞尔曲线段。绘制后切换到 `Select`，将指针移到目标曲线段即可显示该段的贝塞尔锚点与控制柄。
- `Brush`：生成闭合填色轮廓，使用 `Fill` 材质。
- `Pressure Brush`：使用数位笔压力；鼠标输入时根据速度与停留时间模拟压力。
- `Mixing Brush`：以当前 `Fill` 颜色作为载色，沿笔迹拾取活动图层中的已有填色；可选择光学混色或颜料混色。
- `Text`：单击舞台创建文本，双击已有文本再次编辑；`Ctrl+Enter` 提交，`Esc` 取消。
- `Fill`：修改对象填色；点击活动图层当前 Cel 中由描边和已有填色边缘共同围成的空白区域可创建填色，其他图层的边缘不会参与边界计算。按住 `Alt` 时临时切换为吸管，可吸取对象的渐变色（含各色标位置）与 alpha 通道，松开 `Alt` 立即回到填色；吸取后仍处于填充工具，点按即可用吸取的材质继续填色。
- `Ink Bottle`：给填色边界应用当前 `Stroke` 颜色与线宽。
- `Eyedropper`：吸取填色、描边或渐变；点在放置的图片上时吸取该处像素颜色（含透明通道，图片裁剪掉的区域与全透明像素不取样）。
- `Gradient`：给填色或线条应用并调整渐变。
- `Snap Point Tool / 吸附顶点工具`：为当前元件配置可复用的吸附点。左键点击添加，右键点击已有点删除；创建后的点可再次左键拖动。指针靠近可见线段端点时优先使用端点，靠近填色轮廓时使用轮廓上的最近点，否则使用点击位置；悬浮点、活动点和自动检测候选会以固定屏幕尺寸高亮。
- `Eraser`：可分别启用擦除 `Stroke`、`Fill` 或同时擦除；启用 `Fill` 时也会擦除 Mixing Brush 区域，中段擦除会把剩余的顶点色网格拆成可独立选择的连通区域。

操作提示：

- 工具按钮右下角有分组标记；悬停分组按钮后可在右侧弹出区选择子工具。
- `Tab` / `Shift+Tab` 在当前工具组中前后切换。
- `[` / `]` 调整铅笔、笔刷或橡皮擦宽度。
- 笔刷激活时按住 `Shift` 可在指针周围选择最近使用的颜色。
- 填充工具激活时按住 `Alt` 可临时使用吸管，松开即回到填充；按住期间不会取消已在进行的拖动或平移手势。吸管同样可吸取放置图片的像素颜色。
- 拖动期间显示半透明预览，通常在松开鼠标时才正式写入工程；按 `Esc` 可取消尚未提交的操作。
- 调整混色笔刷参数后可直接在舞台落笔；开始笔触时舞台会重新接管键盘焦点，松手提交后可立即按 `Ctrl+Z` 撤销该笔区域。

### Mixing Brush 参数与行为

- `Mode`：`Optical` 在线性光 RGB 空间混合，并以可累积覆盖率沉积，同色反复涂抹会逐步增加色彩密度；`Pigment` 持续保存 Kubelka-Munk 颜料吸收状态，纯色和同色混合保持当前 `Fill` 色值，跨越不同底色时逐点计算连续过渡色，互补色混合会更暗。
- `Strength`：控制从已有填色中拾取颜色的力度。
- `Viscosity`：控制颜色进入笔刷的速度；数值越高，颜色沿笔迹变化越缓慢。
- `Paint Load`：控制当前 `Fill` 颜色的保持量；数值越高，笔刷载色越不容易被底色替换。
- `Influence`：控制笔刷离开采样颜料后，所携带颜色恢复到当前 `Fill` 颜色的速度；数值越高，恢复越快。

行为规则：

- 新 Mixing Brush 笔触保存为区域顶点和三角形索引。顶点包含坐标与实时计算后的 ARGB 色值，最小顶点间距为 `25 vu`；底色、混色历史和预览缓存不会写入工程。
- Optical 使用线性光覆盖计算，同色反复涂抹会稳定增加密度；Pigment 使用颜料吸收状态计算过渡色。两种模式都通过顶点 alpha 和颜色插值形成柔滑边缘。
- 折返、自交或再次涂抹已有区域时，会结合当前区域与活动图层底色重新计算覆盖结果。长笔触只降低预览重建频率，不改变最终顶点色。
- 同一帧、同一图层顶部的连续混色结果会合并为区域对象。重叠位置只保留最后一次计算结果，普通对象会阻断跨对象合并。
- Select 或 Free Transform 只选择实际命中的连通岛；`Shift` 可追加。移动、变形、删除或局部框选时会拆出所选岛，未选岛保持不变，操作仍可一次撤销。
- Eraser 在启用 `Fill` 擦除时直接裁切区域网格，并把不再连接的剩余部分拆成独立区域。旧工程中的轨迹式 Mixing Brush 数据仍可读取和渲染。

Pressure Brush 的连续圆形笔尖预览使用完整采样的压力与平滑结果，并按最终笔划的闭合轮廓显示；长距离不停笔绘制时不再反复从整条笔迹抽取固定数量的点。Direct2D 与 GDI 使用相同的预览轮廓，保留笔尖软边与透明度。

Continuous round Pressure Brush previews retain the full pressure and smoothing samples and display the same closed outlines used for the committed stroke. Long uninterrupted strokes no longer repeatedly resample the whole trajectory to a fixed point count. Direct2D and GDI share the preview outlines, including brush softness and opacity.

普通 Brush 的长笔划预览同样保留完整轨迹，不随笔划增长重新抽点。连续圆形笔尖使用与提交相同的等宽闭合轮廓生成方式，Direct2D 与 GDI 共用软边分层和透明度。

Long Brush previews also retain the full trajectory without repeatedly selecting new samples as the stroke grows. Continuous round tips use the same constant-width outline builder as committed strokes, with shared softness layers and opacity in Direct2D and GDI.

Brush 和 Pressure Brush 仍保存为闭合 Path 填色。普通同色笔刷填色相交或边界距离小于 `10 vu` 时可能自动合并；未接触的岛屿之间不会自动补成填色。

### 吸附与绘制设置

`Basic Drawing` 的元件标签行右侧提供 Snap、Grid、Objects、Tight fit（紧贴）、Align、Angle 六个开关；紧贴位于 Objects 与 Align 之间，与旧版本位置一致。窄窗口仍保留这些开关，恢复宽窗口后数值输入框重新显示。总 `Snap` 关闭时其他吸附不生效。

- `Grid`：吸附到当前缩放级别的可见网格。
- `Objects`：吸附到附近对象或线端点。
- `Align`：使用已有锚点的 X/Y 对齐位置。
- `Angle`：按设定角度步长约束线条。

吸附顶点属于当前元件共享信息，不属于任何绘制图层、Cel 或帧，因此切换图层和播放头不会改变点的位置或数量。复制元件时会复制点的位置并生成新的稳定 ID；工程保存、打开和编辑器重启会保留这些点。添加、删除或一次连续拖动各对应一条撤销记录。

操作提示：吸附结果不符合预期时，先临时关闭总 `Snap`，再逐项启用需要的规则。

## 6. 选择与变形

`Select` 用于选择和移动；同一选择工具组中的 `Polygon Lasso` 与 `Freehand Lasso` 分别用于逐点绘制直线边界和拖动绘制自由边界；`Free Transform` 用于缩放、旋转和倾斜；`Distort` 用于可编辑三次贝塞尔包络扭曲。

- 单击对象：选择单个填色、线条、文本或整体 SVG。
- `Polygon Lasso`：单击逐点添加直线顶点，每次松开左键后保留已添加的顶点；双击、按 `Enter` 或在至少三个顶点时点击首点附近提交。移动时会预览当前边；按 `Escape` 或右键取消。
- `Freehand Lasso`：按住左键拖动绘制自定义边界，松开提交；短促单击或退化路径不会改变选择。两种套索都支持按住 `Shift` 追加选择。
- 打开元件后，在 Basic Drawing 内可沿实际套索边界选出局部填色、线段和描边；完整圈住的填色也可与线段同时选择。切换到 `Select` 后可移动所选内容；取消未编辑的选区会恢复临时切分，圈选和移动均可撤销。元件实例、导入的 SVG、文字及带扭曲包络的对象仍按整体圈选；混合画笔按连通色块选择。Scene & Animation 的 2D 前视图支持套索，侧视与顶视保留投影选择工具。
- 在舞台选择绘制元素或整体对象时，时间轴会自动激活其实际所在图层；目标位于折叠文件夹或当前滚动范围外时，会自动展开并滚动到该图层。
- 点选和框选不限于活动图层。锁定图层不会抢占点选命中；下层内容仍可在未被不透明填色遮挡的位置选中，包括透过锁定的半透明上层。锁定的不透明填色仍保留遮挡，不会穿透选择完全隐藏的内容。
- 遮挡填色在透明与不透明之间切换后，可见选框会随之更新，不复用旧的遮挡结果。Selection outlines update when covering paint switches between transparent and opaque.
- 画布尺寸不是选择边界：平移或缩小视图后，画布外可见的图片和绘制对象仍可点选、框选；无需先移动回画布内。

  Click and marquee selection are not restricted to the active layer. Locked layers do not claim click hits, so exposed lower-layer content remains selectable through translucent locked layers. Opaque locked fills still occlude hidden content.

  Canvas dimensions do not restrict selection: visible images and drawing objects outside the canvas remain selectable by click and marquee after panning or zooming out.
- 空白处拖框：普通对象需完整落入框内；选择框穿过 Line 或 Pencil 时，会沿框边把描边拆成框内与框外的开放曲线，并只选中框内部分。Mixing Brush 只选择与框选范围实际着色相交的连通岛，外接矩形中的透明区域不会误选；按 `Shift` 追加选择。
- 拖动已选对象：移动。指针悬停在已选直线、描边、填色或整体对象的主体上，以及整体移动期间，显示四向交叉箭头；移动已选边界描边或运动轨锚点时同样显示该光标。端点和曲率编辑使用十字光标，缩放手柄使用对应方向的双向箭头。
- 舞台获得焦点时按 `Ctrl+A`：仅全选当前帧实际曝光、可见且未锁定的内容；不会跨帧选择。Basic Drawing 优先选择本地对象，该帧没有本地对象时选择实际可见的嵌套实例；Scene & Animation 选择当前帧实际可见的可编辑场景实例。
- `Delete`：删除全部所选内容；Basic Drawing 可在一次操作中同时删除本地图形和嵌套元件实例，Scene & Animation 可删除一个或多个场景元件实例。
- `Ctrl+C` / `Ctrl+V`：复制并带小偏移粘贴全部所选内容，支持多选以及同时选择本地图形和嵌套元件实例；Scene & Animation 中可复制粘贴多个场景元件实例。场景实例和嵌套元件实例均粘贴到粘贴时当前选中的图层；一次粘贴多个元件时，全部使用该目标图层。
- `F8`：把同一绘制图层中的所选对象转换为新元件，并在当前元件、当前图层和原位置留下该元件的嵌套实例。新元件以选区中心作为注册点；跨图层选区不会被压平转换。为保持遮挡关系，图层中仍有未选对象时，选区需是当前 Cel 的顶部连续对象组；新实例保留在已有嵌套实例后方。Convert to Symbol preserves stacking by accepting the topmost contiguous local objects and placing the new instance behind existing nested instances.
- `Ctrl+Z`：撤销。
- 框选或套索产生的临时填色、线段切分会随首次实际编辑一起撤销，一次撤销即可恢复选择前的完整形状、材质和描边，不需要再次移动来触发合并。连续多次编辑仍逐步撤销，已经实际移开的部分不会提前合并。

Free Transform 控制框：

- 方形手柄：缩放。
- 拖动缩放手柄时，`Shift` 会临时切换等比／自由缩放，方向由 `Settings → General → Operation preferences...` 中的「使用 Shift 启用等比缩放」决定。该偏好默认**开启**：不按 `Shift` 时自由缩放，按住 `Shift` 时等比缩放。关闭后反过来：不按 `Shift` 时等比缩放，按住 `Shift` 时自由缩放。所有对象（矢量形状、元件、图片、文本与导入 SVG）的角手柄和边手柄都遵循同一规则；拖动中按下或松开 `Shift` 会切换当前缩放模式。
- 角外圆形手柄：旋转。
- 旋转拖动期间，控制框与图形一起刚性转动，不再按旋转后的包围盒逐次重算方框尺寸，因此抓住的手柄始终停在光标下方，框体不会在拖动中来回跳动。按下的瞬间会记录当前框体、旋转中心和各边长度；一次拖动结束后控制框才回到紧贴图形的新包围盒。
- 边外菱形手柄：倾斜。
- 元件倾斜拖动时直接预览剪切后的轮廓，首次拖动与松手后的结果保持一致；源元件几何不受预览修改。
- 位图、导入 SVG 与文本属于整体对象，没有可编辑顶点几何：自由变换只提供缩放、旋转和中心手柄，不提供倾斜手柄。
- 中心十字：旋转中心，可拖到任意位置。

选中元件实例后，属性面板中的 `Restore original size / 恢复原始大小` 将所选实例的 X/Y/Z 缩放恢复为 100%，X/Y/Z 旋转和 X/Y 倾斜归零，保留位置、滤镜和其他属性。仅旋转或倾斜过的实例也可使用此按钮。操作支持多选，一次撤销即可恢复；关闭 Auto Key 时修改当前曝光的源关键帧，开启时在当前帧创建或编辑关键帧。

Distort 控制框：

- 方形锚点：拖动角点或边界锚点修改包络；拖动框内可整体移动包络。
- 圆形方向柄：用三次贝塞尔控制边界曲率；拖动方向柄时按住 `Shift` 可保持锚点两侧切线连续。
- 按住 `Ctrl` 单击包络边界：在命中位置添加扭曲锚点。插点使用精确曲线分割，添加瞬间不会改变当前扭曲形态。整个包络全局只允许存在一个插入锚点：四个角点属于固定结构锚点，不占用该名额；已有一个插入锚点时再次单击边界不会添加第二个锚点。旧工程中已存在多个插入锚点的包络会在下次写入时自动折叠为单个锚点，折叠会保留边界曲率而不是把曲线拉直。
- 未预先选择对象时，第一次点击对象会先建立扭曲选择框；随后即可直接拖动锚点、方向柄或包络内部。
- 扭曲支持当前帧中的基础图形、文本、普通对象、导入 SVG、混色区域和嵌套实例，也可在 `Scene & Animation` 中用于场景实例。
- 位图支持包络扭曲：`Distort` 为图片放置对象显示扭曲包络，像素随包络变形，透明区域保留。扭曲参数可保存和撤销，不改变 Vault 中共享的原始图片素材。切回 `Select` 后可重新选中并整体移动，扭曲包络随图片一起平移；隐藏的原始角点不会拦截图片内部的点击。当前包络扭曲沿用软件栅格渲染路径；拖动扭曲柄时保留轻量网格预览，松手后生成完整结果并缓存。
- 位图按原素材的像素精度生成一张透明的扭曲贴图，同时缓存选择轮廓和场景裁剪边界。缩放、平移、窗口尺寸、帧号、背景、舞台遮罩或对象透明度变化时，直接映射并绘制已有贴图，不重新计算图片扭曲；放大后的细节仍受原素材像素精度限制，`Point` 采样保持最近邻。图片内容、擦除范围、采样设置、对象位置/尺寸/旋转或包络变化时重新生成。结果缓存最多保留 32 项、合计 64 MiB；超出尺寸、内存或网格精度预算时使用原绘制路径，这些图片缩放时仍可能重算。首次生成大图的完整结果仍可能耗时。
- 扭曲只保存源坐标框架、边界锚点和控制柄参数；不会拆散、隐藏或矢量化打包对象、文本和导入 SVG。复制、撤销、工程保存、素材 SVG 保存及实例关键帧都会保留这些参数。
- Rectangle、Ellipse、Triangle、Polygon、Star、Path 和 Line 在扭曲后会按包络的实际映射结果重新拟合三次贝塞尔。填色边缘、描边、图层遮罩、选择轮廓和框选判断共用这份派生几何；Line 保持开放路径及原有端点样式，复合 Path 保持轮廓与孔洞。源对象和扭曲参数不会被改写。
- Select 下悬浮 Line、选择填色周围的局部描边或编辑填色边界时，局部高亮、锚点、方向柄、曲线命中与最终扭曲轮廓使用同一份派生贝塞尔；拖动这些展示句柄时会先把指针逆映射回源对象坐标，因此不会把屏幕中的扭曲坐标错误写回原始路径。
- 对象存在多次扭曲时按创建顺序叠加；重新选择单个对象后继续编辑最后一层扭曲。
- 拖动锚点和方向柄时，基础矢量使用缓存的派生贝塞尔预览；渐变内部颜色场以及文本、未拆散 SVG、混色区域等复杂内容继续使用缓存源图和轻量网格，并由实际贝塞尔边界裁切。交互期间不会重建场景索引或嵌套元件组合；松开鼠标后才写入参数并完成一次最终重建。持久扭曲完成后会缓存稳定的舞台基础画面，控制框和工具游标只刷新覆盖层。
- 包络锚点和方向柄可以越过相邻边或对边，可形成凹陷、自交、局部折叠、整体压平和反向翻转；这些形态会按偶奇规则显示重叠区域。折叠位置存在多个可能源点时，编辑器会稳定选择重映射误差最小的源点继续操作。只有坐标成为非有限值、锚点数据损坏或所有锚点完全缩成同一点时才会拒绝本次拖动。

机制：选择可能落在完整对象，也可能落在填色、描边或由交点分割出的局部。移动局部时，软件会把虚拟拓扑部分实体化为可编辑对象；一次拖动仍只生成一个撤销步骤。

图形调整提交时，按既有 Adobe Animate 风格的合并绘制规则重新计算局部拓扑：自交填色拆成有效轮廓，同色填色合并、异色填色覆盖，独立描边在新交点处分割。端点、曲率、移动、变换及边界拉弧复用同一套规则。计算以被修改对象及其关联填色为起点，通过空间查询寻找同图层、同 Cel 的相关邻居；不对远处无关图形执行全场景布尔运算。拖动预览结束后提交，拓扑变化包含在原操作的一步撤销中。
On commit, shape edits recompute local topology using the existing Adobe Animate-style merge drawing rules: resolve self-crossing fills into valid contours, merge matching fills, overwrite different fills, and split independent strokes at new intersections. Endpoint, curvature, move, transform, and boundary-arc edits share these rules. Edited objects and linked fills seed spatial queries for relevant neighbours in the same layer and Cel; distant objects do not undergo a scene-wide Boolean pass. Topology commits after the drag preview and remains part of the same undo step.

单条封闭边界自交形成两个封闭填色区域时，两块填色均保留，边界曲线在交点处切分并建立端点；不会通过删除回环来消除自交。
When a closed boundary crosses itself into two filled faces, both faces remain, with boundary curves split into endpoints at the intersection. The loop is not discarded.

移动填色时，当前被拖动的填色会临时显示在所属图层的最上方，便于在纯色、线性渐变、径向渐变和形状渐变中持续确认位置；松开或取消后恢复原有显示顺序，不会改写对象的永久层级。多个填色一起移动时仍保留它们原来的相对顺序。

### 锚点与曲线

- 基础绘制中的移动、端点/方向柄、弧度与折角拖动以松开左键的位置作为最终位置，即使最后一次鼠标移动消息尚未送达；已经开始的移动或变形拖回按下位置时会还原到起始状态。`Ctrl` 框选不会被当前选中对象的普通句柄抢占，可编辑曲线原有的 `Ctrl` 插点/折角手势仍优先。
  Basic Drawing commits move, handle, arc and corner drags at the left-button release position. Returning an active move or transform to its press position restores its starting state. Ctrl-marquee bypasses ordinary selected-object handles while existing Ctrl curve-edit gestures retain priority.

- 选择独立 Line、Pencil 描边、Path 描边或可编辑填色边界后，会显示锚点和贝塞尔控制柄；Pencil 只显示指针所在曲线段的四个手柄，避免长笔迹同时显示过多锚点。
- `Simple Pen` 与 `Line` 新建的线段按二次贝塞尔编辑：两个端点的蓝色辅助线汇聚到同一个蓝色控制方格，选中轮廓使用浅金黄色与较深的金色光晕。`Pen`、Pencil、形状边界及其他生成来源使用三次贝塞尔与默认的两个独立方向柄，其高亮不变。复制、拆分、撤销和保存重开保留编辑类型；未记录来源的旧工程线段默认按三次编辑，已有曲线几何不变。
- 位图、导入 SVG 与文本不显示锚点和方向柄；它们按整体对象选择，只显示整体包围框。
- 直线绘制、延伸分支、拉弧和折点拖动会立即处理首个移动位置，后续高频输入按显示帧合并为最新位置，减少重复吸附和几何计算。松手仍以实际松手位置提交；取消操作或丢失鼠标捕获时清空排队预览。
  Line drawing, branch extension, arc reshaping and corner drags respond to the first move immediately, then coalesce rapid input to the latest position for each preview frame, reducing repeated snapping and geometry work. Release commits the actual release position; cancellation or capture loss clears queued previews.
- 拖动共享端点时，相连线条会保持连接；拖动过程中实际描边与黄色编辑线、端点手柄同步刷新，直线折角也保持重合。端点及折点编辑的对象吸附半径为屏幕上的 8 像素，画线时仍为 32 像素；关闭吸附可自由调整。拖动新拆出的折点时排除正在编辑的两段，按住 Ctrl 创建折点不会强制开启对象吸附；抓取偏移在整个拖动中保持，回到按下位置恢复原锚点。
  Connected endpoints stay joined during dragging; the actual stroke, yellow editing outline and endpoint handles update together, including straight corners. Endpoint and corner editing snaps within 8 screen pixels, while drawing retains its 32-pixel radius. Disabling snapping allows free adjustment. A new corner excludes both edited segments from snap targets; holding Ctrl to create it does not force object snapping on. The initial grab offset is preserved, and returning to the press position restores the original anchor.
- 线段（`Line`、Pencil 描边、未填充的 Path 描边）作为独立对象编辑：**调整曲率**时可让重合的填色边界一起变化——拖动曲率方向柄（三次线段的两条方向柄、二次线段的单个控制方格）、在未选中线段上拖动中段拉弧、`Ctrl+拖动` 拉折角、拖动 Pencil 锚点，以及与边界重合的端点共享关系，都会同步该填色的对应边。**只移动线段本身**时不带动任何填色——选中整条线段拖动、拖动端点、使用变换框缩放/旋转/倾斜，填色边界保持在原处（三次贝塞尔线段同样如此）。填色也可以随时通过自己的边界控制点单独编辑。
  Strokes (`Line`, Pencil freehand, unfilled Path strokes) are independent objects: dragging a **curvature** handle (either direction handle of a cubic stroke, the single control of a quadratic one), pulling the arc of an unselected stroke, `Ctrl`+dragging a corner, dragging a Pencil anchor, or moving an endpoint shared with the fill boundary keeps a coincident fill edge in sync. **Moving the stroke itself** — dragging a selected stroke, dragging an endpoint, or scaling/rotating/skewing it with the transform box — never moves a fill edge (cubic-Bezier strokes included). Fills stay editable through their own boundary controls.
- 独立线段（包括封闭填色外的线段）的端点命中优先于曲率控制柄和中段拉弧；端点与曲率调整区域重叠时，拖动会调整端点，未选中线段也遵循此规则。
  Independent Line endpoints, including lines outside closed fills, take priority over curvature handles and arc dragging. When their hit areas overlap, dragging edits the endpoint, even when the line is unselected.
- 封闭填色的边界描边同样优先命中黄色端点，避免曲率手柄或未选中边界的拉弧手势抢占端点拖动。拖动这种边界端点会同时调整填色轮廓与相连边界，不会把描边分离成独立线段。
  Closed-fill boundary strokes also prioritize yellow endpoints over curvature handles and unselected-edge arc dragging. Dragging a boundary endpoint reshapes the fill and connected boundary edges together, without detaching the stroke into an independent line.
- 拖动曲线锚点会同时平移该锚点附着的控制柄，保留原有曲率；直接拖动控制柄只调整对应切线。
- 直接用左键拖动 Line 的独立端点会重新弯曲该段：被拖端跟随指针，另一端固定，位移沿整段逐渐减弱，带动较大范围的曲线一起移动，避免控制点钉在原处造成局部过度折弯。二次控制点跟随一半位移；三次方向柄从靠近被拖端到远端分别跟随三分之二、三分之一位移，不锁定原切线。二次段保留一个控制点，三次段保留两个方向柄，编辑类型不变；原本笔直的段仍保持新的直线弦。共享折点使用独立的角点拖动：所有相连端点一起移动，外侧端点及按下时的控制点保持固定，允许尖角左右摆动、切线转向和反向回弯；不会把独立端点的整段跟随规则套到折点上。单独拖动外侧端点只重塑对应线段。弯曲段可以拉平后继续反向回弯，拖回按下位置可恢复原形，一次拖动作为一步撤销。
  Dragging an unshared Line endpoint reshapes its curve: the dragged endpoint follows the pointer, the opposite endpoint stays fixed, and displacement decreases linearly along the segment so the curve body follows instead of folding around stationary controls. The quadratic control follows half the displacement; cubic handles follow two thirds near the dragged end and one third near the fixed end, allowing the tangent to change. Quadratic segments retain one control and cubic segments retain both handles without changing degree; originally straight segments remain straight. A shared corner moves all connected endpoints together while keeping the outer endpoints and gesture-start controls fixed, allowing the tip to swing left and right and its tangents to reverse. The distributed motion used for free endpoints does not apply to shared corners. Dragging an outer endpoint reshapes only its own segment. Curves can pass through a flat position and reverse their bend. Returning to the press position restores the original shape, and each drag uses one undo step.
- 拖动未选中线段的中段会拉出平滑弧线：被拖到的曲线上那一点按鼠标位移移动，控制点由原曲线求解，不会在第一帧跳到光标位置；控制方格从偏离中心的位置抓取时也保留初始偏移。已选中线段的本体拖动则移动整段。`Ctrl+拖动` 中段会改用角点手势：原线段在命中处拆成两条独立 Line，两条线共享命中点的端点，拖动时该共享端点跟随指针、两条线的另一端保持不动。原本笔直的线段仍得到由两条直线段组成的折角；原本弯曲的线段按 de Casteljau 精确分割，拆分瞬间不改变形态，拖动时两半各自保留拆分得到的曲率——二次段保留自己的单控制点，三次段保留两个方向柄——只移动角点而不会把弧拉直，与 Animate 的角点行为一致。拖动过程中即使某一半暂时拉平，继续拖动仍沿用切分时的控制点，拖回起点可恢复切分时的弧形。拆分只在指针移动超过拖动阈值后提交，未拖动就松手会还原为原来的一条线段，一次 `Ctrl+Z` 可撤销整个手势。
- `Ctrl+左键` 点击可编辑曲线可插入锚点；`Ctrl+右键` 点击真实锚点可删除。
- `Alt+单击` 曲率控制柄可把该段恢复为直线。
- 调整矩形等填色形状的一条边后，可直接拖动同一形状的另一条未选边继续调整弧度；快速连续操作不会切换为整形移动或连通描边选择。已选边的主体拖动仍保留原有移动/分离行为。
  填色边界弯曲沿用按下时的两个控制点与抓取位置，根据指针位移调整弧度，不再把两个控制点直接吸到鼠标处；第一帧立即响应，拖回按下位置恢复原边界。拖动填色边界上的锚点（含 `Ctrl` 插入的折点）时，填色区域同样随指针实时更新，取消拖动会恢复原边界。分离已选边时也保留从按下开始的完整位移。
  Fill-boundary bending preserves the original controls and grab offset, responds on the first drag sample and restores the original edge when dragged back. Dragging a fill-boundary anchor — including the corner Ctrl inserts — reshapes the filled region live as the pointer moves, and cancelling restores the original boundary. Detaching a selected edge also preserves the full displacement from the press position.
  连续弯曲后分离并移动边线时，快速双击不会改选连通描边；松手后线段停止跟随鼠标。分离线被交点物化或再次拆分后仍与原填色独立。弯曲期间失去鼠标捕获会取消该次弯曲并清理操作状态。
  Rapid clicks during edge detachment do not select connected strokes. Detached strokes stop moving on release and remain independent after intersection materialization or splitting. Losing mouse capture cancels an active boundary bend and clears its pointer state.

机制：Pencil 保存为开放的三次贝塞尔节点，并同步维护采样折线供渲染、命中、框选、擦除和填色边界计算使用；编辑、框选切分或交点切分都不会把笔迹转换为闭合 Path，也不会生成末点到首点的额外线段。Pencil 的预览和最终提交使用同一套连续平滑与拟合误差，平滑值越高，允许合并的轨迹跨度越长，但首尾点和误差边界保持稳定。旧工程中只有采样折线的 Pencil 会在首次实际拖动控制柄时生成精确节点，单纯悬停不会修改工程；一次拖动只产生一个撤销步骤。Pencil、独立 Line、填色边界和形状描边使用同一套交点关系；新笔画经过交点时会物化为可独立选择的开放曲线段。由平滑 Pencil 描边围成的新 Fill 会在误差范围内拟合为少量贝塞尔段，不会把每个采样点显示成锚点；自相交 Fill 的交点也会作为共享锚点同步移动。

已选填色或线段在检测到首个位移时，会先平移已经缓存的选择几何并立即提交一个 GPU 覆盖层帧；CPU 随后准备轻量撤销状态、局部拓扑实体化和边界关系，完成后由真实模型帧接替预览。首次可见反馈以 `30 ms` 为预算，不再等待复杂拆分事务结束。后续指针输入按显示帧合并，并在松手前应用最后位置；层级、检查器和复杂覆盖层刷新会继续延迟到指针交互结束。密集场景处于低缩放级别时，舞台会保留 LOD 背景，并单独实时绘制当前编辑对象。

复杂场景中的选择轮廓保持静态高亮；较小场景保留脉冲动画，避免大型画布为了装饰动画持续整帧重绘。

### 控制框类型

选中对象后显示哪一种框，由对象类型决定：

| 控制框 | 适用对象 | 可做的操作 |
| --- | --- | --- |
| 橙色路径轮廓（锚点 + 方向柄） | Line、Pencil 描边、Path 描边 | 编辑曲线锚点与曲率 |
| 绿色填色边界（锚点 + 方向柄） | 可编辑填色 | 逐顶点变形 |
| 薄荷矩形包围框（四角缩放手柄） | 矩形、椭圆、多边形、路径、位图、导入 SVG、文本等填充 / 整体对象 | 整体移动、四角缩放 |
| 蓝色虚线框（方柄缩放、圆柄旋转、菱形倾斜、中心十字） | `Free Transform` | 缩放、旋转、倾斜 |
| 绿色虚线网格包络（方锚点 + 圆方向柄） | `Distort` | 包络扭曲 |
| 薄荷矩形框（中心十字锚点，与主舞台填充对象同款） | 元件 / 组合实例 | 整体变换 |

整体对象（位图、导入 SVG、文本、未拆散的混色笔画）没有可编辑顶点几何，选择工具不显示填色边界的锚点或方向柄。它们支持整体移动、缩放和旋转，也可使用 `Distort` 编辑扭曲包络；扭曲后的选择轮廓按实际边界显示，缩放和旋转使用 `Free Transform`。

## 7. Fill、Stroke 与基础着色

软件中的颜色调整统一为随调随生效：填色、描边、渐变、工作区背景、图层/时间轴分组标识色、元件色调、灯光以及滤镜颜色均不需要调色确认按钮。独立颜色窗口关闭或按 `Esc` 保留当前值；合法 RGB Hex 输入立即生效，不完整或非法输入保留最后有效颜色。颜色窗口的 `Reset` 是一次立即恢复原色的编辑操作。对象、灯光、图层和分组颜色沿用各自撤销机制，一次颜色窗口编辑合并为一条记录。

右侧基础着色面板顶部用于切换编辑目标：

- `Fill`：填色、文本和笔刷轮廓的内部材质。
- `Stroke`：Line、Pencil 和形状边界的颜色与线宽。

面板使用传统的二维色域方块和竖向主通道条，并支持 RGB、HSV、HSL、CIELAB（D65）、Hex、透明度、纯色和渐变。模式切换会保留当前颜色与 Alpha：RGB 使用 `R | G/B`，HSV 使用 `H | S/V`，HSL 使用 `H | S/L`，Lab 使用 `L* | a*/b*`；竖条控制分隔线左侧的主通道，方块控制右侧两个通道。拖动标记时颜色会实时预览，一次连续拖动只生成一个撤销步骤，按 `Esc` 可取消本次拖动。

Lab 模式提供可按 0.1 步进编辑的 `L*`（0 到 100）、`a*` 和 `b*`（-128 到 127）通道；超出 sRGB 可显示色域的 Lab 值在转换时按 RGB 通道裁剪，Alpha 保持不变。先选择目标，再修改颜色；修改单个已选对象时会更新选择，未选择对象时会作为下一次绘制的默认材质。

在舞台选择填色时，检查器会优先切换到 `Fill` 颜色；选择独立描边或填色边界描边时会优先切换到 `Stroke` 颜色。选择整体对象时继续按对象类型使用默认材质目标。

操作提示：

- 找不到颜色编辑器时，确认右侧检查器已展开，并点击 `Fill` 或 `Stroke` 目标。
- 文本只使用 `Fill` 和透明度，不使用 Stroke 或渐变。
- 直接修改渐变对象的纯色会把该材质切回纯色。
- 线宽使用 pt；舞台缩放不会改变其工程尺寸。

机制：同一图层内先绘制所有 Fill，再绘制 Line、Pencil 和边界 Stroke，因此描边通常显示在填色上方。图层 0 位于最上方。

## 8. 图层

底部时间轴左侧管理图层：

时间轴顶部的 TabGroup 标签采用与着色面板一致的分段切换样式：标签按文字宽度紧凑排列，图层、地形及自定义时间轴组标签使用较小字号与较矮的标签高度，全部标签保持原有字号与尺寸，文字居中，选中项使用主题实色底并贴齐下沿、底部保持平直，未选中项融入面板背景，悬停时高亮；高对比度模式保留系统描边与颜色。标签按组筛选图层列表：`Layers` 收纳普通图层，`Terrain` 收纳碰撞地形，`All` 显示全部图层。切换标签只改变时间轴中的显示，不改变舞台渲染、图层可见性、遮罩关系或动画播放。可以创建自定义组、重命名组，并通过图层右键菜单将所选图层分配到组；删除自定义组会把其中的图层移回 `Layers`，保留全部内容。分组编辑支持撤销，分组名称、归属和当前标签随工程保存。

新增分组的 `+` 按钮跟随在最后一个可见分组标签后，设置齿轮固定在标签栏最右侧。点击设置按钮，或在标签右键/溢出菜单选择“图层分组排序”，可打开排序设置窗口。窗口只保留一条排序标签带：越左位于底层，越右位于上层。标签只显示名称，悬浮提示显示序号和组内图层；左右拖拽或使用 `Ctrl+左/右方向键` 调整层级。点击“确定”后同步修改“全部”列表及舞台前后遮挡顺序，组内保留原图层上下顺序；“取消”或 `Esc` 放弃窗口内的调整。内置普通图层组和地形组也可排序，`All` 仅为视图入口，不参与排序。每个时间轴最多创建 32 个自定义分组（不含两个内置组），达到上限后禁用新增；旧工程已有分组保留，不裁掉内容。

直接按住时间轴顶部的分组标签左右拖拽，也可调整分组排序。标签只显示名称，悬浮提示显示序号。拖拽标签跟随鼠标，相邻标签让位，高亮竖线标出落点，松开后同步修改“全部”列表和舞台层级；左侧分组更低，右侧分组更高。拖到标签栏边缘可翻动被折叠的标签，按 `Esc`、失去鼠标捕获或在标签栏外松开则取消，普通点击仍切换筛选。`All` 固定且不可拖动。

分组排序支持一次撤销并随工程保存。旧工程在首次确认排序前保持原来的叠放顺序；启用排序后，调整归属和新增图层会保持分组层级，在“全部”视图中跨组拖动图层或图层组，或通过右键“移动到分组”改变归属时，会先弹出二次确认，无需先启用分组排序；点击“确定”移入目标组，取消则保留原位置，同组内排序无需确认。文件夹及其子图层、遮罩及其关联内容作为整体归属同一个分组，调整其中任一成员归属会移动整个关联块，保留文件夹与遮罩关系。切换分组标签仍只改变列表筛选，不改变舞台内容。

Drag group tabs directly on the timeline’s top tab bar to reorder them. Tabs show names only, with ordinals in hover tips. The dragged tab follows the pointer and neighbours make room; the insertion line shows the destination; releasing updates the All list and stage stacking in one undoable command. Left is lower and right is higher. Hold at the bar edges to reveal hidden tabs. Esc, losing capture, or releasing outside the bar cancels. Clicking still switches filters, and All stays fixed.

The add-group `+` follows the last visible group tab, with the settings gear fixed at the far right of the tab bar. The group order dialog is available from the settings gear or the tab context/overflow menu. The compact dialog has one horizontal tag strip, ordered from left (bottom) to right (top), with ordinals and member layers in hover tips. Confirming updates both the All list and stage stacking while preserving the layer order within each group. `Ctrl+Left/Right` provides keyboard reordering. Up to 32 custom groups can be created per timeline, in addition to Layers and Terrain. All is a view, not a sortable group. Sorting is saved and undoable; Cancel leaves the project unchanged. In All view, dragging a layer or layer group into another group or using Move to tab group asks for confirmation before changing the document, including projects that have not enabled group sorting; Cancel keeps the original placement. Reordering within the same group does not prompt. Folder children and linked mask/content layers move together. Legacy projects keep their original stack until sorting is confirmed.

右键 TabGroup 标签可设置自定义颜色或恢复默认颜色；普通图层组、地形组和自定义组均支持，`All` 保持主题默认外观。颜色显示在标签及分组菜单的色样中。“全部”视图内同组图层使用一致的行底色与色块，优先采用分组自定义颜色；未设置时使用稳定的分组识别色，选中状态仍有高亮。切换到“图层”、地形或自定义分组视图时保留各图层原有颜色显示。分组识别色仅用于时间轴，不改变原始图层颜色、舞台材质或渲染。溢出菜单中也可调整分组颜色。颜色选择器中调整立即生效，关闭或按 `Esc` 保留当前颜色；颜色随工程保存，一次调色以及恢复默认均可撤销。

In All view, layer rows in the same group share a background tint and swatch, using the custom group colour or a stable fallback. Selection remains highlighted. Individual group views retain each layer's original colour display. This affects timeline presentation only.

- `+`：新增普通图层并切换到新层，清除此前的帧选区，仅突出新层当前帧；撤销新增会恢复原图层和帧选区。新建 Folder、Mask 或地形层同样清除旧帧选区。
- 文件夹加号图标：新建 Folder，并把当前绘制层收纳到该 Folder 中；可在文件夹内继续新建图层，或把其他图层拖入其中。
- 眼睛：显示或隐藏。
- 锁：禁止选择和编辑。
- 彩色方块：显示图层颜色和 Outline 状态。
- 拖动名称：调整顺序或放入文件夹。
- 右键名称：重命名、新建 Folder/Mask、设置颜色和其他图层命令。
- `Move Layer Up` / `Move Layer Down`：向列表上方 / 下方移动一层；列表越靠上，画面层级越靠前。

时间轴图层列表上方的眼睛、锁和方块列头是图层总控：单击眼睛可显示或隐藏全部图层，单击锁可锁定或解锁全部绘制图层，单击方块可在正常显示与全部 Outline 之间切换。图标会反映当前是否全部开启；存在混合状态时，下一次点击会统一开启。每次总控操作只产生一条撤销记录，元件时间轴中的嵌套实例也会跟随可视总控同步显示或隐藏。

拖动排序时，被拖动的图层或文件夹组会跟随指针，被跨越的图层会平滑让出目标位置；高亮插入线表示松手后的最终顺序。拖到 Folder 中部或兼容的 Mask/内容层中部时，整行目标高亮表示放入文件夹或建立遮罩关系。拖动 Mask 行会搬动整个遮罩组；拖动已被遮罩的内容层到普通位置或 Folder 会自动将它移出原遮罩层。动画只用于预览，松手后才提交一次可撤销的结构修改；关闭系统动画后会立即显示目标位置。

多选图层：

- `Ctrl+单击`：追加或取消。
- `Shift+单击`：连续选择。
- 时间轴获得焦点时按 `Ctrl+A`：选择当前可见图层。

眼睛、锁定、Outline、颜色和混合模式可以批量应用，并合并为一次撤销。重命名、新建和移动仍只针对当前目标。

### Folder 与 Mask

- Folder 用于收纳图层。隐藏、锁定或 Outline 文件夹会影响其子层；折叠只改变时间轴显示。
- Mask 位于内容层上方，使用当前 Cel 中的封闭填色裁切内容。锁定 Mask 后隐藏遮罩图形本身，只保留裁切结果。
- 右键已被遮罩的内容层并选择 `Move Out of Mask Layer`，可解除遮罩关系并把该层保留在原 Mask 后方；该操作适用于绘制时间轴和 Scene Mask。
- 空、隐藏或当前帧没有内容的 Mask 不会显示被遮罩层。
- Mask 图层可以和普通绘制图层一样，在两个有内容的关键帧之间创建 `Shape Tween`；中间帧使用现有形状补间规则生成遮罩轮廓。

机制：遮罩先裁切，再执行图层混合和透明度计算。

### 图层颜色与 Outline

图层颜色是编辑标识色，不是对象材质。双击彩色方块或使用 `Layer Color...` 打开颜色编辑器；调整立即生效，关闭或按 `Esc` 保留当前颜色，一次调色可用 `Ctrl+Z` 撤销。颜色会以低对比底色覆盖时间轴左侧的完整图层项，并同步显示在 Hierarchy 的完整图层行；帧格区域保持独立底色，便于辨认曝光和关键帧。图层色样本始终保留真实颜色，图层名称、层级引导、图标和焦点框会根据合成后的背景自动选择可读颜色。

单击彩色方块可切换 Outline：填色和原描边暂时隐藏，内容以约 1 px 的图层色轮廓显示。该状态只影响编辑器舞台，不改变材质或工程几何。

在时间轴图层列表中选中图层后，舞台会自动用该图层颜色为它当前帧的可见内容描边高亮：普通绘制图层包含其 Folder 子层内容，`Scene & Animation` 工作区高亮所选场景图层中的实例内容，编辑 Scene Mask 时高亮遮罩内容。播放、镜头/导演工作区和 3D 参考视图下不显示；图层没有可见内容时也不显示；对象数量很多时最多高亮 512 个。高亮用于初次选中图层时定位内容；使用选择或变换工具点击舞台空白可取消，活动图层仍保持选中。取消后，切帧、面板刷新或绘制不会重新开启同一图层的提示，切换选中图层后再次显示。高亮只是编辑提示，不会改变舞台选区、活动图层、材质或输出。

Selecting a layer in the timeline layer list automatically outlines that layer's visible content on the Stage in the layer color. Folder descendants are included; in Scene & Animation the selected scene layer's instance content is outlined, and Scene Mask editing outlines the mask content. The outline is hidden during playback, in Shots & Directing, and in 3D reference views; it is also hidden when the layer has no visible content, and at most 512 objects are outlined. The highlight initially helps locate the selected layer: clicking blank Stage space with a selection or transform tool dismisses it while keeping the layer active. Frame changes, panel refreshes and drawing do not restore a dismissed hint for the same layer; selecting a different layer shows its hint again. This is an editor hint only and does not change the stage selection, active layer, materials, or output.

### 图层混合模式

选择绘制图层或场景图层后，在右侧检查器顶部使用 `Layer > Blend Mode`。下拉菜单包含 29 种模式，包括 Multiply、Screen、Overlay、Difference、Hue、Color、Subtract、Divide、Dissolve、Isolated Group 和 Invert。

机制：

- 当前层与其下方可见内容进行混合，下层不会被移除。
- Folder 和场景图层作为隔离组整体混合。
- Mask 先裁切组内容，再参与混合。
- 非 `Normal` 帧使用精确软件合成；全部恢复为 `Normal` 后使用 Direct2D 硬件路径。
- 混合模式会随保存、复制、Vault、嵌套组合和重启保留。

## 9. 时间轴与关键帧

时间轴面板左上角使用左侧双圆环与分段竖线、右侧两组长短圆角横线组成的时间轴图标标识，悬停显示 `Timeline / 时间轴` 提示；图标颜色随界面主题调整，浅色主题使用柔和灰色，高对比度模式使用系统颜色。图标与添加图层、独显、全部按钮紧邻排列并垂直居中，按钮仅在悬停时显示高亮底色；拖动图层列宽不会移动这组控件。相机轨道模式继续显示 `Cameras / 相机` 标题。

时间轴顶部与图层栏分界处的拖动手柄采用短三点提示，替代长线和密集刻度；原有高度与列宽拖动区域保留。固定表头的“图层”文字采用浅灰辅助色，随主题变化。图层区采用中性背景和内缩的选中底色，当前行显示短图层色标；隐藏或锁定图层的名称弱化，悬停显示轻量底色。时间轴左侧图层区默认宽度为 360 像素，拖动图层区右边的分隔线可继续调整列宽；顶部工具栏位置不随列宽变化，窄窗口会限制图层区宽度，为帧格保留空间。

图层行使用柔和的中性底色，仅保留少量图层颜色作为底色提示；活动图层通过左侧强调色标记和轻微加亮区分，图层色块继续显示实际图层颜色。创建图层的短暂反馈沿用界面强调色，不再闪现大面积高饱和绿色；图层颜色不会改变作品材质。

时间轴顶部的洋葱皮与运动轨范围输入保留统一宽度；切换相对帧数与绝对帧范围时，不会推动旁边的开关或输入框。

时间轴的核心概念：

- 播放头：竖线表示当前帧，颜色跟随个性化中的强调色，顶部手柄与旁边的小标签使用同一强调色，标签文字自动选择可读的深色或浅色；高对比度模式下改用系统高亮色。没有帧选区时，当前编辑图层的帧格加亮显示。点击图层名称会清除原有帧选区，高亮编辑格立即跟随选中的图层；在帧网格中使用 `Ctrl`、`Shift` 仍可追加或扩展跨层帧选区。
- 实心关键帧：该 Cel 有内容。
- 空心关键帧：该 Cel 为空。
- 曝光：从一个关键帧保持到下一个关键帧之前的区间。
- Cel：某个绘制图层在某个关键帧拥有的独立内容。

图层名称可上下拖动以调整叠放顺序，上方图层覆盖下方图层。编辑元件时，本地绘制和嵌套元件共同遵循所属图层的顺序；同一图层中元件显示在本地绘制之上，点击及双击也优先命中可见的上层元件。该规则在元件编辑和场景组合中保持一致。

Drag layer names vertically to change stacking order; upper layers cover lower layers. Local drawing and nested symbols follow their host layers in both symbol editing and scene composition. Within one layer, symbols appear above local drawing, and clicks or double-clicks target the visible symbol first.

场景与动画以场景图层中是否存在元件实例来判定关键帧内容。空场景图层始终显示空心关键帧，包括旧工程中残留了有内容曝光标记的情况；实例仅被隐藏时仍属于图层内容。

机制：复制关键帧后，新 Cel 与旧 Cel 相互独立。修改后面的 Cel 不会反向修改前面的关键帧。

在帧网格中选中一个或多个帧格后，舞台会自动全选这些帧格实际曝光的可编辑内容。多选时会合并各 Cel 的对象或元件实例，同一保持曝光只计入一次；空白曝光、隐藏或锁定的绘图内容以及不可见实例不会进入选区。所选帧格没有可选内容时，舞台选区会清空。跨帧内容会保留在同一选择集合中，舞台只在当前播放头帧显示其中当前活动内容的选中轮廓。自动检查器在选择场景实例或补间时保留对应的实例参数／补间曲线面板，不会误切换为无选择的文档信息页。

包含大量图层的工程在播放时，时间轴只重绘顶部播放时间、相关标尺列、播放头和当前可见帧格，帧摘要在底部状态栏更新。播放头到达可见范围边缘后，视窗会按区段向前跟随，而不会在之后的每一帧滚动并重绘整个时间轴面板。播放调度同时只保留一个等待或执行中的界面更新；繁忙帧不会继续向消息队列堆叠刷新请求，绘制和输入仍可获得处理机会。

拖动时间轴顶部的高度调整条或改变窗口尺寸时，时间轴文字与帧格绘制遵守局部刷新边界，避免旧文字残影和越界重绘引起的闪烁；共享的列表、树和下拉框文字绘制也保留刷新裁剪。时间轴尺寸改变时不复制旧窗口像素，拖动的每次尺寸更新后立即重绘，避免旧底部滚动条短暂出现在新位置。拖动高度或按 `Esc` 恢复高度时，底边保持固定，顶部位置与高度一次更新，避免先缩放再重新停靠时短暂露出大块工作台背景。时间轴与状态栏之间只保留状态栏的分隔线，底部滚动条按整像素绘制，避免重复底边和半像素边缘。此修正不改变帧选区、曝光或播放行为。

选中的帧格使用暖色半透明高亮，配合持续缓慢的呼吸效果和流动虚线边框；存在帧选区时优先突出选区，不再单独高亮其他图层的当前帧。播放头的竖线与光晕也随呼吸同步呈现柔和亮度变化，关键帧标记、曝光条和补间类型仍可辨认。动画只重绘可见帧区，隐藏或禁用时间轴后停止；系统关闭界面动画或启用高对比时保留静态高亮。播放头帧号标签与拖动标签遵循局部重绘裁剪；跳帧时同时刷新旧、新帧号标签的完整范围，避免多位数帧号留下残影。

常用操作：

| 操作 | 结果 |
| --- | --- |
| `Enter` | 播放/暂停 |
| `,` / `.` | 上一帧/下一帧 |
| `Shift+,` / `Shift+.` | 播放范围首帧/末帧 |
| `F5` | 延长当前曝光，后续帧右移（播放头保持原帧不动） |
| `Shift+F5` | 删除当前帧，后续帧左移 |
| `F6` | 插入关键帧并复制当前保持内容（按住时合并为一条撤销记录） |
| `Shift+F6` | 清除关键帧，重新继承前一曝光 |
| `F7` | 插入空白关键帧（按住时合并为一条撤销记录） |
| `Ctrl+C` / `Ctrl+V` | 时间轴聚焦时复制/粘贴 Cel；新复制的系统剪贴板图片粘贴到当前绘图层的视图中心 |
| `Reverse Frames` | 将选中时间范围内的帧按时间倒序排列 |

在时间轴末帧执行 `F6` 时，如果末帧仍是保持曝光，会直接在末帧创建关键帧而不延长时间轴；如果末帧本身已经是关键帧，则先延长一帧，再在下一帧创建关键帧。

按住 `F5`、`Shift+F5`、`F6` 或 `F7` 连续插帧/删帧/插关键帧时，键盘重复只推进帧模型：每次重复仍会插入或删除一帧、或插入一个关键帧，但整段按住过程合并为一条撤销记录，时间轴、镜头检查器和属性面板的结构刷新在松开按键后统一执行一次，避免长按时的面板卡顿。单次点按的行为不变，仍是立即刷新并产生一条撤销记录。

`F5` 插入帧时播放头保持不动，与 Adobe Animate 的插入帧行为一致：命令只改变播放头所在位置的曝光并右移后续帧，不会把播放头带到新的曝光末尾。插入点之前的帧没有变化，因此播放头下的内容也不变；在播放头位置或其后插入时，播放头仍停在原来的帧号。需要移动到别处时请继续使用 `,` / `.` 或直接点击帧格。

在帧网格拖动可框选多个图层和帧；`Shift` 扩展矩形选择，`Ctrl` 追加或取消。右键帧网格可执行复制、粘贴、插入/删除帧和关键帧命令。

框选拖动期间，选框和时间轴游标实时更新；舞台内容与检查器在松开鼠标后统一同步到最终选区和帧，避免逐格重建复杂场景造成卡顿。同一帧格内移动不会重复计算选区；鼠标移出控件后松开仍会采用边界内的最终位置。
选中至少两个时间帧后右键选择 `Reverse Frames`，会按每条轨道的选中范围翻转帧顺序，并同步处理空白帧、绘制 Cel、场景实例状态、灯光和遮罩内容；一次 `Ctrl+Z` 可撤销整个操作。
框选后，选区四周会显示变换框：在框内拖动可整体平移选中的帧；拖动左右边可进行水平拉伸，拖动四角可同时拉伸水平和垂直方向，上下边不提供单独的操作手柄。拉伸会按目标网格重采样曝光内容，放大时填充目标范围，缩小时合并碰撞位置；超出时间轴或图层边界会自动夹取。移动和缩放始终覆盖目标格，不推动后续帧。变换会同步处理绘制 Cel、实例状态、灯光和遮罩内容；跨图层时实例状态按实例 ID、元件 ID 和图层内顺序映射，目标帧中未匹配的旧状态会被覆盖清除。不兼容的轨道类型不会执行；一次拖拽只产生一条撤销记录，按 `Escape` 可取消预览。

### 镜头与导演

相机已并入“场景与动画”：进入后默认显示时间轴“全部”，点击“镜头”可集中编辑相机；选中相机显示右侧面板，选回普通图层恢复原检查器。镜头轨道在“全部”中始终置顶，且不能与普通图层互换分组。旧工程打开时补齐镜头分组，原有相机和关键帧保留。

Cameras are integrated into Scene & Animation. The timeline opens on All; choose Shots for camera-only rows. Selecting a camera opens its inspector; selecting a regular layer restores the scene inspector. Camera rows stay above regular layers in All, independently of tab-group ordering. Camera and regular-layer groups cannot exchange tracks. Existing projects retain cameras and keys and gain the built-in Shots group. Ctrl+3 opens this group.

在“镜头”或“全部”中，右键相机轨道名称，选择“删除图层”即可删除相机及其动画；支持多选删除，一次 `Ctrl+Z` 可撤销。最后一台相机也可以删除，不会删除场景物体；固定“镜头”分组仍然保留。普通场景图层仍须至少保留一层。

In Shots or All, right-click a camera row name and choose Delete Layer to remove the camera and its animation. Multi-selection deletion is supported and one Ctrl+Z restores the operation. The last camera can be removed without deleting scene objects; the built-in Shots group remains. At least one regular scene layer must still remain.

#### 默认 2D 与实验性 3D

2D 相机的旋转输入不显示 Z 轴标记，仅保留画面旋转角度；开启实验性 3D 后恢复轴标记。

The 2D camera rotation input shows only the picture rotation angle, without a Z-axis badge. Experimental 3D restores the axis badge.

默认 2D 相机面板隐藏描述、相机类型、投影模式、焦距、正交尺寸、位置 Z 和旋转 X/Y。基础参数包括名称、画面比例、二维位置、画面旋转和“缩放”；相机列表不再显示透视／正交术语。打开“高级相机参数”可调整投影模式、位置 Z，以及当前投影对应的焦距或正交尺寸，无需启用实验性 3D；旋转 X/Y 仍不可编辑。缩放百分比越大，取景越近，并与镜头参数同步。新建、定位、排序、删除、重置及简化相机样式入口保留。仅打开高级参数不会改变工程；“缩放”按原投影调整焦距或正交尺寸，不转换旧工程。启用实验性 3D 后恢复完整参数。

The 2D camera inspector keeps basic framing controls visible. Enable **Advanced camera settings** to edit projection, position Z and the applicable focal length or orthographic size without enabling experimental 3D. X/Y rotation stays locked; zoom and lens values stay synchronized. Expanding the settings alone does not change project data.

在舞台、时间轴或相机列表中按 `Enter` 播放／停止时间轴，相机的位置、旋转、缩放及已建立的补间随播放头正常求值，观察窗同步更新。播放遵循现有帧率、播放范围和循环开关；关闭循环时播放到结束帧后停止，已在结束帧时再次播放会从起始帧重新开始。名称、数值和下拉框编辑期间，回车仍用于控件自身操作，不抢占输入。

Press `Enter` from the Stage, timeline or camera list to play/stop the timeline. Camera position, rotation, zoom and existing tweens are evaluated at the playhead, and the monitor follows playback. Playback respects the existing FPS, range and loop setting; with looping off it stops at the end, and starting again from the end restarts at the range start. Editors retain their own Enter behavior.

### 2D 相机视差 / 2D camera parallax

相机检查器在窄栏中将新建与定位操作显示为图标，参数列及向量组共用同一对齐线；切换基础／高级或 2D／3D 时按实际轴组高度重新排布，滚动区宽度变化后同步更新布局。相机列表为空时显示空态而非空列表底板。

In narrow Camera inspectors, creation and locating use icon buttons. Scalar fields and vector groups share the same column alignment. Basic/advanced and 2D/3D transitions use the actual axis-group height, and scrolling-content width changes refresh the layout. An empty camera list shows its empty state instead of a blank list surface.

2D 视差按 DPI 排布，不再叠加字体自动缩放；展开或调整宽度后重绘控件，避免复选框文字与旧数值残影重叠。

2D parallax uses DPI-based layout without additional font autoscaling. Expanding or resizing the section repaints its controls, keeping checkbox labels clear of stale numeric text.

右侧文档、颜色、相机及场景页的顶部标题统一为常规字重、次级文字色与细分隔线；颜色页使用静态图标标题，编辑区常驻；颜色通道与和谐色仍可独立折叠。标题保持现有深灰面板风格，不使用大号粗体或强调色底板。

Document, Color, Camera and Scene inspector page headers share regular-weight typography, secondary text and a thin divider. The Color page uses a static icon title and a permanently visible editor; Color channels and Harmony still collapse independently. Headers retain the existing dark-panel styling without oversized bold type or accent-filled banners.

检查器折叠标题使用常规字重与次级文字色，悬停或交互时再强调文字和箭头；图层深度分区标题同样降低对比度，让参数值保持视觉重点。

Inspector disclosure headers use regular weight and secondary text color, emphasizing their text and chevrons on hover or interaction. The layer-depth heading also uses secondary text, keeping parameter values visually prominent.

颜色页重新挂载后立即按当前宿主宽度布局，纯色与渐变切换不应保留旧的窄栏宽度。顶部图标、标题与分隔线按标题容器的实际缩放比例定位，避免分隔线落到容器外被裁掉。

The Color page lays out against its current host width immediately after mounting, so switching between solid and gradient modes does not retain a stale narrow width. Its icon, title and divider use the header container's actual layout scale to keep the divider inside its bounds.

相机检查器的 **2D 视差** 默认展开，效果默认关闭。标题右侧独立启用；折叠不关闭效果，关闭也不丢失参数。可直接调整视差强度（0–200%）、相机 Z、参考平面 Z 和「保持大小」，并编辑图层深度列表与深度图；默认折叠的高级响应提供水平／垂直响应（0–200%）及「推拉产生深度缩放」。只对 2D 相机生效，不需要开启实验性 3D。

图层列表逐层控制是否参与及深度 Z，负向为近、正向为远。深度图显示相机、视锥和带图层颜色的深度线：上下拖动线调整图层前后，拖动相机点调整推进／拉远；选中后也可用前后按钮或上下方向键调整。`Shift` 精调，`Esc`、失去鼠标捕获或中断手势取消；一次拖动只生成一次撤销。相机与图层间保留近面安全距离，播放时也限制投影比例，防止穿越后反转。深度改变不重排图层，不改写实例缩放；遮罩与其内容保持共同响应，重叠遮罩组采用首个内容对象所在图层的响应。

2D 视差以每台相机时间轴首个有效帧为构图基准。开启「保持大小」时，调整图层深度不改变该帧的位置和大小；关闭后恢复近大远小的深度缩放，围绕起始相机位置缩放。后续相机相对移动产生近快远慢的视差。Z 推拉围绕起始相机位置计算额外缩放，关闭推拉深度缩放不影响普通变焦。切换预览、播放头或重开工程不重置基准，不写回对象坐标或改变关键帧。图层深度或参与状态改变后，观察窗同步刷新；关闭视差或取消图层参与时，不因近面约束改变未参与图层的深度。

观察相机模式保持编辑场景不变，左下观察窗显示视差；锁定相机和相机取景同时显示相同的视差结果。回车仍按原时间轴播放，效果随当前帧相机移动求值。当前接入已有相机预览渲染链路，不新增视频或最终渲染导出功能。

**2D parallax** starts expanded with the effect disabled. Its independent header checkbox preserves values when disabled; collapsing never disables rendering. Strength, camera Z, reference-plane Z, Keep size, layer depths and the depth diagram are directly available. The initially collapsed Advanced response adds horizontal/vertical gains and dolly depth scaling. Each scene layer has shared static participation/depth, while each camera owns its effect settings. Camera scalar parameters follow Classic tweens; switches change at the destination keyframe.

Drag colored depth lines or the camera point, or use the closer/farther buttons and arrow keys. Shift is precise, Escape/capture loss cancels, and a gesture creates one undo entry. Depth never reorders layers or rewrites authored instance scale. Mask/content groups share one response; overlapping mask groups use their first content object's layer. Near-distance and projection bounds prevent inversion. The first populated frame of each camera anchors composition. Keep size preserves its positions and sizes as depth changes; disabling it restores near-large/far-small scaling around the starting camera position. Subsequent camera displacement drives parallax, and Z scaling pivots around that same anchor. Playback, preview changes and reopening never recapture that baseline; disabling dolly scaling leaves ordinary zoom unchanged. Depth and participation changes refresh the monitor immediately. Inactive effects and nonparticipating layers do not clamp authored layer depth to the camera's near plane. Save/open and undo preserve settings; legacy projects remain disabled. Observe mode leaves the editing scene untouched while the monitor shows parallax; Locked and Camera modes show the same projected scene during normal Enter playback. This extends camera preview rendering, not final video/export support.

相机属性顶部的 **预览方式** 提供三种 2D 舞台预览，回车始终只控制播放／停止：

1. **观察相机**：保持编辑视角与场景坐标，相机框按时间轴移动、旋转与变焦；播放期间仍绘制相机框。
2. **锁定相机**：将切换时的相机框位置、比例与朝向固定在屏幕上，场景按当前相机的逆变换呈现；保留镜头外的内容，方便比较构图。重新切换相机时重新建立锁定基准。
3. **相机取景**：将相机画幅等比适配到舞台，相机框居中固定，框内场景随运镜变化，框外留黑边。窗口尺寸变化时重新适配，不拉伸画面。

“场景不动”仅表示不受相机运动带动，场景对象自身的动画在三种模式中仍正常播放。锁定相机与相机取景是只读预览：舞台不响应拖动编辑、双击编辑或滚轮导航，仍可通过时间轴拖动播放头、通过相机面板调整参数。切换预览不会修改相机关键帧、对象坐标或原编辑视角；退出相机工作区或选择不适用的相机／3D 参考视图时恢复观察相机。本次模式选择仅保留在当前会话，不写入工程。两种模式统一使用相机变换后的完整软件帧呈现，不混用原编辑视角的快速播放帧；沿用既有渲染预览质量策略，不属于最终渲染导出。

The camera inspector's **Preview mode** selector offers **1 · Observe**, **2 · Locked**, and **3 · Camera**. Observe keeps the editor view while the animated camera frame moves. Locked keeps the camera frame at its switch-time screen position, scale and orientation and presents the scene relative to the animated camera, including content outside the frame. Camera view fits the frame to the Stage without stretching and clips the scene to its picture area. Switching cameras resets the lock anchor. Object animation continues in every mode; Enter always plays/stops the same timeline.

Locked and Camera view are read-only 2D previews: Stage dragging, double-click editing and wheel navigation are disabled, while timeline scrubbing and camera inspector editing remain available. Preview switching does not change object transforms, camera keys or the original editor view. Leaving the camera workspace or selecting an unsupported camera/reference view restores Observe. The choice is session-only. These two modes use existing GDI scene rendering and full-size software-frame presentation, retaining the renderer's existing preview-quality policies, not a screenshot of the current Stage. They do not mix camera-relative presentation with fast playback frames prepared for the original editor view. The planar camera monitor also renders the camera's world-space coverage, so moving the camera outside the editor viewport does not crop away its content. This is preview infrastructure, not a final export/render command.

放大观察窗仅通过相机面板／观察窗标题的预览按钮或双击观察窗图像／标题切换，`Esc` 可还原；放大状态下的 `Enter` 仍用于播放／停止。放大不改变舞台导航、相机参数或关键帧。观察窗按现有刷新节奏更新，并非独立的全帧率输出窗口。

Use the inspector/monitor preview buttons or double-click the monitor image/title to enlarge or restore it; `Esc` also restores it. `Enter` remains play/stop even while enlarged. Enlarging changes no Stage navigation, camera parameters or keys. The monitor uses its existing refresh cadence rather than acting as a separate full-frame-rate output window.

左下观察窗自动避让左侧工具栏（包括换列后的宽度）及展开的 Vault 资源面板，并限制在舞台与时间轴之间的可用区域。调整窗口大小、工具栏位置、资源面板或时间轴显隐后会重新布局；空间不足时暂时隐藏，恢复空间后重新出现并保留首选尺寸。放大预览退出后还原原观察窗尺寸；右下角缩放与失去鼠标捕获后的结束处理均受舞台边界约束。

The lower-left monitor avoids the left tool palette, including wrapped columns, and the open Vault drawer, and stays inside the visible Stage above the timeline. Window, toolbar, drawer and timeline layout changes reposition it; insufficient space temporarily hides it without discarding its preferred size. Leaving enlarged preview restores the monitor size. Grip resizing and capture-loss completion respect Stage bounds.

二维“旋转”使用与“缩放”一致的半栏宽、32 逻辑像素高的单值输入框，不显示 Z 轴徽章，参数之间保留 8 逻辑像素间距。镜头属性区不显示滚动条，保留滚轮与键盘滚动；侧栏高度不足时，可在属性区空白或标签处滚轮浏览下方参数。

二维相机样式隐藏 Z 深度移动手柄及其连线，隐藏区域不响应手柄命中；自由变换仍可调整画面旋转。已有镜头深度数据不变，实验性 3D 中的三维相机保留深度手柄。

The default 2D camera inspector hides description, camera type, projection, focal length, orthographic size, position Z and rotation X/Y. It retains name, aspect ratio, XY position, picture rotation and a single Zoom percentage (larger means closer). Creation, locate, ordering, deletion, reset and simple camera style remain available. Existing projection and hidden values are preserved; Zoom adjusts the existing projection's lens or orthographic size without converting the project. Enabling experimental 3D restores the full inspector.

默认仅开放 2D 工作流。在 **设置 → 常规 → 实验功能 → 启用实验性 3D 功能** 中可以手动打开 3D，保存后立即生效，并在下次启动时保留。新设置及未记录此选项的旧设置均默认关闭。

关闭时隐藏／禁用 2D/3D 切换、空间参考方向、3D Transform、灯光与三维材质面板、灯光轨道和 Vault 的 3D 元件分类。场景按正面二维视图显示，不启用三维光学。镜头面板隐藏相机类型选择、位置 Z 与旋转 X/Y，新建及重置相机均使用 2D；仍保留平移、Z 轴画面旋转、画幅及焦距／正交尺寸等二维取景操作。

已有 3D 相机在列表中标记为“已禁用”，不能编辑、重置、删除或预览，其时间轴和舞台辅助图形也不会开放。关闭开关不删除或转换工程内的场景维度、相机关键帧、灯光、材质及三维元件；重新开启后可继续使用。MCP 的 3D 写入同样受此开关约束，读取仍可用。旧工程的二维显示不是原有 3D 效果的等效预览。

Only 2D workflows are enabled by default. Use **Settings → General → Experimental features → Enable experimental 3D features** to opt in; saving applies the setting immediately and persists it across restarts. Older settings without this option also default to off. With the option off, 3D view switching, reference directions, spatial transforms, lights, optical materials, light tracks and the Vault 3D category are unavailable. New/reset cameras use 2D, with position Z and rotation X/Y hidden. Existing 3D cameras are disabled and read-only, without camera-track editing or previews. Project data is not deleted or converted; enabling the option restores access. MCP 3D writes are also gated, while reads remain available. The 2D view of an older project is not an equivalent preview of its original 3D appearance.

镜头与导演面板采用统一主题输入框和紧凑操作栏；相机列表以柔和底色标记选中项，次级信息显示投影类型。名称、描述字段与其他检查器保持相同的边框和圆角风格。

在 `Scene & Animation` 的时间轴中点击“镜头”，管理同一场景中的独立相机。默认仅启用 2D；实验性 3D 开关保持原有行为。相机面板替换右侧场景检查器，舞台继续显示完整场景；选择相机时只操作相机框和手柄，不直接修改场景物体。切到“全部”可同时查看相机和场景动画，相机始终置顶。点击时间轴添加按钮或面板“新建相机”均可在镜头分组中新建相机。

当前相机的取景预览显示为舞台左下角的浮动监视器，位于时间轴面板正上方。它是真正的相机视图：按相机的实际 3D 位置、三轴旋转、投影模式和焦距/正交尺寸渲染，Z 轴推拉会改变取景覆盖范围。可以拖动监视器右下角的斜纹手柄调整大小，松开后按新尺寸重新取景；尺寸会一直保持，除非舞台空间不足。预览随相机选择、播放头和参数变化刷新，不占据右侧检查器空间。预览在播放和拖动取景手柄期间持续更新，以监视器所在屏幕的刷新信号调度；移到另一屏幕时自动跟随。静止画面复用缓存，隐藏预览或最小化窗口后停止调度。实际帧率受场景渲染耗时限制，动画内容仍按工程设定的动画帧率变化。右侧检查器只保留相机列表和相机参数。

The floating shot monitor continues updating during playback and camera drags, paced by the refresh signal of its current display. Moving the window between displays changes the pacing source. Unchanged images reuse cached content; hiding the monitor or minimizing the window stops scheduling. Achievable preview FPS depends on scene render cost; animation still advances at the project's animation FPS.

#### 相机类型

`2D Camera` 的舞台采用简易取景框：只显示当前相机的边框、四角手柄、顶部旋转手柄和名称，隐藏相机机身、视锥、三轴箭头、旋转环、焦距滑杆及其他相机线框。拖边框移动取景，拖四角等比调整范围，拖顶部手柄旋转；框内部和隐藏控件不接收相机拖动。左下角监视器仍显示实际取景。此调整不删除相机参数或已有动画；`3D Camera` 保留原有样式。

The `2D Camera` Stage overlay shows only the active frame, four corner handles, top rotation handle and name. Camera bodies, frustums, axes, rotation rings, lens sliders and other camera outlines are hidden. Drag the border to move, corners to resize with the aspect ratio preserved, or the top handle to rotate. The interior and hidden controls do not accept camera drags. The lower-left monitor still shows the actual view. Parameters and animation are preserved; `3D Camera` keeps its original style.

每组相机有 `Type` 参数，可选择 `2D Camera` 或 `3D Camera`。两种类型都保留真实场景与透视纵深，区别在于可编辑的通道：

| 类型 | 可编辑参数 | 手柄 |
| --- | --- | --- |
| `3D Camera` | 完整 XYZ 位置与三轴旋转、焦距/正交尺寸 | 位置三轴箭头、三轴旋转环、焦距滑杆 |
| `2D Camera` | X/Y 平移、**Z 推拉**、**仅 Z 轴旋转**、焦距/正交尺寸 | 简易取景框：四角等比缩放、顶部旋转、边框移动；其他参数在检查器调整 |

`2D Camera` 是平面摄像台：保留 Z 轴的纵深与透视缩放效果，但 X/Y 旋转固定为 `0`，因此取景平面始终与舞台平行。切换为 `2D Camera` 时，原有的 X/Y 旋转会被归零并作为一次可撤销的提交；`Rotation X` / `Rotation Y` 输入框在 2D 类型下禁用，3D 相机手柄上也不再显示 X/Y 旋转环。Z 位置、Z 旋转和镜头参数在两种类型下都可用，经典补间会保持 Z 纵深与 Z 旋转连续。

| 操作 | 结果 |
| --- | --- |
| `New camera` | 在当前场景中添加一组独立相机，并创建对应的相机轨道 |
| 相机列表 | 选择、定位、上移、下移或删除相机；可同时保留多组相机进行不同捕捉 |
| `Name` / `Description` | 编辑当前相机的名称和描述 |
| `Type` | 在 `2D Camera` 与 `3D Camera` 之间切换；2D 保留 Z 纵深并只允许 Z 轴旋转 |
| `Mode` | 在 `Perspective` 与 `Orthographic` 之间选择投影模式 |
| `Camera position X/Y/Z` | 编辑相机在场景世界坐标中的位置 |
| `Camera rotation X/Y/Z` | 编辑相机的三轴旋转角度 |
| `Focal length` | 编辑透视相机焦距；可在时间轴上对焦距做关键帧和补间，实现推拉变焦 |
| `Ortho size` | 编辑正交相机的可视尺寸；可在时间轴上做关键帧和补间 |
| `Reset camera` | 将当前相机恢复为默认 3D 取景 |

场景仍只有一个连续时间轴。相机不会拥有场景内容图层，旧工程中的镜头图层归属只作为兼容数据读取后清空；“镜头”只显示相机轨道，“全部”同时显示相机和场景图层（实验性 3D 开启时也显示灯光轨道），当前场景帧范围仍由同一条时间轴决定。

#### 相机关键帧与补间

每组相机在时间轴中拥有独立轨道，显示方式采用 AE 时间轴的连续相机色补间条和菱形关键帧标记；补间有效时只显示起止端点，内部为可编辑状态而物化的帧不会重复绘制菱形。轨道关键帧保存该帧的完整相机状态：投影模式、位置 XYZ、旋转 XYZ、焦距和正交尺寸。选择补间色带后，右侧镜头检查器会显示动画曲线编辑器，可以拖动曲线锚点调整 easing。相机轨道只支持 `Classic Tween`：

- 开启 `Auto Key` 时，编辑检查器参数会在播放头创建相机关键帧；关闭时，编辑当前保持曝光的来源关键帧；
- 选择相机轨道上的两个有内容关键帧后创建 `Classic Tween`，位置、旋转、焦距和正交尺寸会按补间曲线逐帧插值；
- 透视与正交是相机模式，不会把场景拆成两份；补间在数值属性上连续，模式在目标关键帧生效；
- 空白相机关键帧表示该帧没有相机取景；端点、曲线或帧位置变化后，中间取景会重新求值；
- 相机轨道的关键帧、补间和设置随场景时间轴保存，并经过独立的帧序、引用和轨道校验。

相机预览会随相机选择、播放头和参数变化刷新。3D 相机的舞台辅助图形保留相机机身、取景平面和视锥线；透视相机使用会聚视锥，正交相机使用平行引导线。2D 相机只显示上述简易取景框。

镜头与导演工作区始终显示当前场景的所有相机框，包括未选中相机、空白曝光和播放期间。选中的镜头框以橙色加粗高亮显示，未选中的框以普通蓝色细线显示；取消选择不会隐藏相机框。仅选中的镜头显示并可拖动缩放点、旋转环、轴杆和镜头滑杆；未选中的框不显示操作手柄。拖动选中镜头时其他框保持显示，播放期间手柄仅显示、不接受拖动。通过右侧相机列表或时间轴选择镜头，使用相机检查器调整位置、旋转、焦距和正交尺寸；框线随当前帧求值更新。离开导演工作区后隐藏这些辅助框。 同一视图下，绘制和命中查询复用相机框与手柄的投影结果；旋转视图、缩放、窗口尺寸、相机参数或舞台尺寸变化后重新计算，保留全部框线、操作手柄及近裁剪。

### 传统补间与形状补间

在一个绘制图层中选择起止关键帧之间的帧范围，然后右键选择 `Create Classic Tween` 或 `Create Shape Tween`。可以在两个关键帧之间的任意一帧右键，命令会自动使用两侧相邻的有内容关键帧；只选择起始关键帧时会使用右侧下一个有内容的关键帧，右键最后一个端点关键帧时也可以解析其左侧区间。蓝色色带表示传统补间，绿色色带表示形状补间；端点仍显示关键帧标记。

`Scene & Animation` 的普通内容层也可用同一右键菜单创建 `Create Classic Tween`。该层必须恰好包含一个场景实例，且起止帧都是有内容的关键帧；补间会逐帧插值实例的 XYZ 位置、最短方向 XYZ 旋转、XY 倾斜、XYZ 缩放、旋转锚点、缩放锚点、透明度和 RGB 色调。2D 或 3D Transform 修改补间端点后，中间帧会自动按当前端点重算。场景层不支持 `Create Shape Tween`，包含多个实例时请先把实例分到独立图层。

- 传统补间：图层必须只包含一个本地矢量对象，或只包含一个嵌套元件实例。矢量对象要求起止类型及可编辑几何拓扑兼容，位置、尺寸、最短方向旋转、线宽、填色、描边、渐变色标和渐变端点会逐帧插值。嵌套实例要求前后为同一实例，其位置、XYZ 缩放、最短方向 XYZ 旋转、XY 倾斜、透明度和 RGB 色调会逐帧插值；可见性与播放模式在中间帧保持起点设置。
- 形状补间：起止关键帧都必须包含原始矢量内容，但对象数量可以不同。封闭填色和开放描边分别按照相对位置、面积/长度和周长自动配对；圆形或其他形状在编辑后拆分成一个填色与多段边界描边时仍可创建补间。单轮廓填色中，能够在端点确认与填色精确共边的独立线会复用该填色的成对轮廓，新增或消失时沿边界淡入或淡出，不会从线段中心展开并漂离填色；明确脱离填色的线仍按独立对象运动。其他多出对象会从退化几何展开或收缩，同时淡入或淡出。几何未变化的开放描边会优先保持原位，开放描边也会自动校正首尾方向。不同顶点数以及 Rectangle、Ellipse、Polygon、Path 等封闭形状会编译为可编辑 Path 的成对边计划；Line 到 Line 保持原生三次曲线，Pencil、开放笔刷及不同开放描边类型之间的过渡会编译为可编辑 Freeform 描边。复合 Path 支持孔洞与孤岛的增加、消失、移动和合并；轮廓综合嵌套层级、相对位置、面积与周长自动匹配，未配对轮廓会从稳定锚点展开或收缩。填色与描边颜色同时参与补间；补间创建后修改任一端点的填色、描边或渐变会立即重算中间帧。纯色可以过渡到渐变，渐变也可以过渡到纯色，不同数量的渐变色标会按色标位置采样后插值。渐变类型不同时，空间类型在补间中点切换，颜色保持连续变化。
- 起止帧必须是有内容的关键帧，补间范围内不能预先存在其他关键帧。同一图层混合本地矢量与嵌套实例、包含多个嵌套实例，或对嵌套实例创建形状补间都会被拒绝。文本、未拆散 SVG、封闭形状与开放描边之间的直接转换以及 Mixing Brush 区域不参与形状补间。
- 创建补间是一次可撤销操作；插入/删除帧会同步移动补间区间并重新计算实例补间状态，之后在区间内部新建或修改关键帧会取消原补间关系。
- 补间创建后，在任一端点关键帧移动、缩放、旋转、倾斜、修改实例外观或编辑对象控制点，完成交互时会按当前补间曲线重新计算受影响图层的全部中间帧；连续拖动期间不会逐事件重复计算。
- 点击补间色带中的任意帧会选中整个补间区间，并在右侧检查器顶部显示 `Tween Curve`；该面板同时适用于 `Basic Drawing` 的传统/形状补间，以及 `Scene & Animation` 普通内容层的传统补间。曲线横轴为时间、纵轴为补间进度；端点固定为 `0%` 和 `100%`，内部锚点始终保持时间递增、进度不倒退。
- 双击曲线可在指针位置新增锚点，也可使用面板下方的 `+` 按钮在最大空档中新增锚点。拖动锚点时曲线和数值会即时预览，但不会反复写入模型或重新计算中间帧；松开鼠标后才会一次性提交曲线并重新计算受影响的补间。方向键按 `1%` 微调，`Shift+方向键` 按 `5%` 微调；连续按住方向键时同样在松开后只提交一次。
- 选中内部锚点后按 `Delete` / `Backspace` 或使用 `-` 按钮删除。拖动、新增和删除带有弹性反馈；动画只影响界面显示，实际保存的曲线值始终稳定且可重复。
- 在已选补间上右键选择 `Remove Tween` 会删除补间关系及其自动生成的中间关键帧/Cel，并恢复为创建补间前只有原始端点的状态；`Ctrl+Z` 可恢复补间及其曲线。

### Auto Key

时间轴上的带 A 关键帧图标用于切换自动关键帧，开启时显示高亮背景。它与运动轨、洋葱皮一样采用独立图标开关，可同时开启；悬停可查看功能提示。

开启 `Auto Key` 后，在普通保持帧修改画布或调整元件实例的位置、旋转、缩放、倾斜、外观与播放设置，会先为实际受影响的图层创建当前帧 Cel，再提交修改。撤销时，自动关键帧和编辑会一起撤销。

关闭 `Auto Key` 时，在保持帧直接绘制或调整元件实例会修改该曝光来源 Cel，不会在播放头插入关键帧；当前帧本身已有关键帧时则修改该关键帧。如果内容必须从当前播放头才开始，请先按 `F6` 或 `F7`。

`Scene & Animation` 工作区共用同一开关：开启后调整场景实例的位置、旋转、缩放、倾斜、外观与播放设置，以及修改灯光参数，都会先为实际受影响的场景轨道插入当前帧关键帧，再提交修改；关闭时这些编辑作用于当前保持曝光的来源关键帧。

### Onion Skin

时间轴左侧标题图标使用随工具栏背景变化的柔和线条颜色，并保留可辨识的对比度。
左侧标题图标、图层操作（文件夹／新增／父子关系）、独显与全部之间使用上下留白的短竖分割线，组间保留间距。元件／场景名称及当前帧／末帧移至底部状态栏，位于缩放之后、模块重载提示之前，随当前时间轴上下文和帧号更新。

时间轴的独显按钮使用居中的单图层，右下角带眼睛图标，全部按钮使用叠层右下角带眼睛图标，悬停显示原有操作提示。工具栏文件夹、新增、父子关系和自动关键帧采用线性图标，按钮命中区域保持原有大小。界面自绘控件统一移除虚线焦点框，键盘焦点改为短实线提示；时间轴图标保持线性轮廓，不添加内部半透明填色，按钮仅在悬停与选中时显示胶囊底色，键盘操作保持可用。时间轴操作图标的线条粗度和选中圆角底色统一对齐洋葱皮；独显／全部高亮互斥，单图层时按最近一次独显／全部操作区分；其他情况反映实际可见状态，父子关系高亮反映开关状态，新增／文件夹仅在交互时显示底色。左侧操作图标缩为统一紧凑尺寸，独显／全部按钮宽度和组间距同步收紧，保留悬停提示。顶部动画帧率标签完整显示 FPS，数值显示一位小数，采用辅助文字颜色；时间轴新增、独显及帧格控件的中文说明补齐。时间轴工具栏采用 48 逻辑像素高度，图标、输入框和文字垂直居中。中间集中显示自动关键帧、运动轨、洋葱皮与前后范围输入，整组随窗口宽度居中；图层操作保留在左侧，播放时间保留在右侧可用空间。动画开关与范围输入之间、整组两侧使用较短、柔和的细竖分割线，线与控件之间保留留白；图标之间和前后范围之间保持适度间距。

洋葱皮与运动轨使用加粗的统一矢量线稿，图标保持居中；洋葱皮双层方框逆时针旋转 45° 呈菱形，避免与复制图标混淆。运动轨不再经过位图缩放与横向压缩。两个图标紧凑排列，开启时图标背后显示中性灰色圆角方块底色，图标保持清晰的前景色；关闭时底色消失，不显示下划线或额外边框。保留原有点击区域和图标位置，顶部高度调整条不穿过图标区域。

两个图标不是互斥选项，可以同时开启并分别关闭。工具栏按 Auto Key、运动轨、洋葱皮和范围输入紧邻排列，图标不再随右侧帧宽／帧高控件的显隐左右移动；切换运动轨的范围模式、选中状态或实例可用状态不会改变图标位置。前／后模式使用较窄的数值框和标签间距，开始／结束模式保留可完整显示六位帧号的宽度。窄窗口优先保留两个开关及范围输入，再显示右侧帧尺寸控件；空间仍不足时隐藏范围输入。极窄窗口若连图标也放不下，则隐藏越界图标，不压缩点击区域或改变其他控件位置。帧摘要显示在底部状态栏，不占用顶部图标或输入框空间。

洋葱皮使用可选中的双层菱形图标按钮，不再显示滑块或 `Onion` 文字：左侧前框完整清晰，右侧后框淡化并只露出未被遮挡的轮廓，重叠处没有穿透线。点击切换，选中时显示圆角方块底色；悬停显示洋葱皮提示，聚焦后可用空格切换。`Prev` / `Next` 设置前后范围，数值输入框采用紧凑高度并在工具栏中垂直居中。

- 所有可见且未锁定的绘制图层参与预览。
- 锁定图层和锁定文件夹的子层被排除。
- 前帧为暖色，后帧为冷色；Outline 图层保持轮廓显示。
- 播放时洋葱皮暂时隐藏，暂停后恢复。

机制：洋葱皮只用于参考，不可选择，不会写入当前 Cel。

在 `Scene & Animation` 工作区，洋葱皮按当前场景独立保存：`Onion` / `Prev` / `Next` 控制场景自己的开关与范围，预览内容为相邻帧的场景合成结果。相邻帧与当前帧解析到相同曝光时不会重复显示；范围上限与其他时间轴一致，设置随工程保存、编辑器重启快照和 Vault 场景数据保留。

### 运动轨（Motion）

开启运动轨后，共用范围输入显示为「开始 / 结束」，标签使用主题强调色，数值表示绝对帧号。选中锚点使用单层强调色外圈；悬停帧号保留原有深色提示框的尺寸、圆角和动画，仅将内容改为与时间轴一致的小方点与数字。

运动轨使用带两端圆点的折返路径图标按钮，不再显示滑块或 `Motion` 文字。点击切换，选中时显示圆角方块底色；悬停显示运动轨提示，聚焦后可用空格切换。它在**当前工作区存在元件实例**时可开启（`Basic Drawing` 与 `Scene & Animation` 均是），跟随的实例优先取舞台上的选择；没有选择时自动采用当前工作区的第一个实例，并在开启时把它同步为舞台选择。开启后舞台在正常洋葱皮之上叠加该元件在所选范围内的运动轨，范围与洋葱皮的指针范围（`Prev` / `Next`）一致。

> 前提：工作区里必须已经有一个元件实例（例如从 `Vault` 拖入一个元件，或在当前符号内嵌套放置另一个符号）。若当前符号只有直接绘制的内容、没有任何实例，则没有可跟随的放置状态，开关保持灰显；禁用提示说明需先放入元件实例。

- 每一帧的残影上显示该帧的素材锚点，锚点之间以虚线连接成轨迹。
- 锚点默认取元件在该帧实际曝光内容的中心，再应用实例变换；元件的循环、保持帧及播放 FPS 与舞台一致，不混入其他关键帧的几何。
- 若某帧锚点相对原始锚点被调整过，则该锚点异色显示。
- 空白关键帧与保持帧的锚点比正常帧更浅；补间内部帧之间的轨迹线段以异色虚线显示。
- 鼠标悬停在任意锚点上会显示该帧帧号；锚点按帧序连接，空白帧不可选中。
- 时间轴当前帧对应的运动轨锚点以薄荷绿填充和紧凑光晕标识，轨迹虚线不发光；移动时间轴指针不改变舞台对象或运动轨锚点的实际选择。悬停提示在 Direct2D 和 GDI 中均沿用紧凑深色提示框，内容显示小方点与帧号（如「▪ 1」），数字字体与时间轴一致，靠近画布边缘时自动避让。
- `Scene & Animation` 中运动轨按当前二维或三维相机投影显示，支持正交与透视视图；拖动沿经过锚点的视平面移动实例，取消与撤销会恢复实例及其时间轴关键帧。Direct2D 与 GDI 回退均显示运动轨。

选中与编辑：

- 单击锚点选中该帧，时间轴指针跳转到该帧，洋葱皮渲染中心随之移动，便于在两侧渐隐的残影间操作。
- `Ctrl` 单击可多选或取消选中单帧，`Shift` 单击为追加选择。
- 按住已选中的锚点拖动可整体移动所选各帧的位置，用于快速调整各帧位移；按 `Esc` 或右键取消本次拖动。
- `Ctrl+A` 在运动轨开启时选中范围内全部可选锚点；`Ctrl+Shift` 拖动可框选锚点。
- 选中不超过 3 帧后，用 Free Transform 工具可对所选帧做缩放与旋转；超过 3 帧时只显示选择范围而不显示变换框。

机制：运动轨与洋葱皮一样只用于参考和定位，不会新建绘制几何；拖动锚点与 Free Transform 写入的是所选帧的实例状态，与在舞台上移动实例走同一条编辑与撤销路径。关闭开关后轨道立即从舞台消失。

开启后运动轨绑定到当时的实例，选中其他实例、取消选择、切换工作区、进入其他绘图对象、编辑遮罩或播放均不会重新绑定或重置范围与锚点选择。目标不在当前编辑上下文或已被删除时，轨道仅暂停显示；返回原上下文或撤销删除后自动恢复。只有主动关闭运动轨开关才清空该轨道的编辑状态，且不会删除实例已记录的位置关键帧；若要跟随其他实例，请先关闭开关，选择目标后再开启。轨道暂停显示时开关仍可关闭，延迟到达的范围事件不会覆盖保留的范围。拖动中关闭开关会先取消本次未提交拖动。

选择与自由变形工具下，快速连续点击运动轨锚点只操作轨迹，不会触发下方元件的双击进入编辑；锚点以外的正常双击导航不受影响。拖动未松手时快捷切帧会取消本次未提交拖动，保留此前已记录的轨迹与范围。

With Select or Free Transform, rapid clicks on motion-track anchors stay in the track interaction instead of opening the underlying symbol. Ordinary double-click navigation away from anchors remains available. A frame shortcut during an unfinished drag cancels that drag while retaining previously committed motion and the track range.

开发热重启与编辑器重启会保留运动轨开关、绑定实例、绝对帧范围和锚点选择，不恢复未完成的拖动会话；旧版重启数据缺少该状态时按关闭处理。该状态属于临时编辑会话，不改变工程文件格式。

Development hot restarts and editor restarts retain the motion-track toggle, target instance, absolute frame range and anchor selection, but never resume an unfinished drag. Older restart data without this state defaults to off; the project file format is unchanged.

Motion stays bound to the instance chosen when enabled. Selection changes, workspace navigation and playback do not reset its range or anchor selection. A missing or out-of-context target suspends the overlay; returning or undoing deletion restores it. Only switching Motion off clears its editing state, never the instance's recorded keys. Switch off and on to follow another instance; switching off during a drag cancels that uncommitted gesture first.

### 播放设置

`Scene & Animation` 工作区可设置 FPS、循环、起始帧和结束帧。FPS 支持三位小数，范围 `1.000-120.000`。这些设置随工程保存。

### 随机破碎形状修改器

在 `Basic Drawing` 中选中当前帧的一个封闭填色形状或 Path，右键舞台并在 `Fracture / 破碎` 组中选择 `Random Fracture...`。该命令只处理未锁定绘制图层中的整体对象；场景实例、开放描边、未选中整个填色对象以及场景合成不可使用。

对话框会先生成一个舞台 overlay：源对象暂时隐藏，碎片在 overlay 中实时刷新。参数变化会短暂防抖并在后台生成预览，旧请求会自动取消，生成期间对话框仍可操作；点击 `Cancel` 或按 `Esc` 不会改变工程。预览通过后点击 `Apply` 才提交；一次提交可用 `Ctrl+Z` 撤销。点击预览播放按钮可以逐帧查看演算结果，暂停、重置和修改参数都不会移动正式时间轴。碎片输出为独立的 Path 舞台对象，不会创建或显示在 Vault/素材库中，并会保留原对象的填色、渐变和可选描边。

渐变材质会完整继承到每个碎片：任意数量色标、色标位置、颜色和 Alpha 都会保留，`Linear`、`Radial`、`ShapeRadial` 以及线性轨迹渐变均支持。渐变坐标会跟随碎片的平移和旋转；`ShapeRadial` 默认继续使用源对象的整体轮廓映射，因此碎片之间不会各自从中心重新开始着色。

可调参数：

- `Fragment count limit` 限制碎片数量；实际数量会因有效几何和最小面积而小于等于该值。
- `Fragment edge count` 控制切割边界的细分程度；Path 的 Bezier 源轮廓会按精确节点采样，不会退回包围矩形；`Fragment randomness` 与 `Random seed` 控制扰动强度和可重复结果。
- `Mode` 提供 `Linear`（共享扰动切割边）和 `Pointillize`（种子点 Voronoi 点化）两种破碎方式。
- `Preserve stroke` 为每个碎片保留源描边；关闭后碎片只保留填色。`Fracture strength` 控制爆裂速度，`Rotation strength` 控制碎片旋转速度。
- `Force algorithm` 提供 `Radial`、`Directional` 和 `Random`。`Directional` 另可设置 `Direction angle`；力度按碎片相对源中心的方向、指定方向或稳定随机方向计算，并使用随机种子生成速度变化。

勾选 `Generate animation` 可按 `Animation frames` 生成破碎演算关键帧。应用后会创建一个破碎专用 Folder，并为每个碎片创建一个独立 Drawing layer；该碎片的所有关键帧和曝光都只写入自己的图层，所有碎片图层都位于同一个 Folder 下。`Ground position` 以源对象顶部加源高度百分比指定隐式地面位置，`Gravity` 设置重力，`Air resistance` 设置空气阻力，`Bounce` 设置接触面的反弹比例。默认启用碎片碰撞，碎片会按真实填充轮廓相互分离并交换法向速度；`Allow fragment overlap` 仅在需要穿透效果时开启。模拟使用自适应子步和连续扫掠检测，薄地形和高速碎片不会只因跨过一个演算帧而穿透；接触复杂斜面、凹形边界或孔洞边界后，会按实际接触边调整碎片姿态并停止继续旋转。连续接触会保持法线和切线方向，低速切向运动会由静摩擦吸收并进入休眠，减少复杂边界上的往返抖动。存在自定义地形时只使用自定义地形，不再提前使用隐式地面。动画会为当前碎片写入独立关键帧，目标关键帧按覆盖规则替换，目标 Cel 中原有的无关状态会被清除，但其他同层对象和跨层对象会保留。

在舞台右键菜单的 `Fracture / 破碎` 组中选择 `Add Collision Terrain`，每次都会在当前绘制对象中新增一个未链接的半透明地形 Mask，并放入 `Terrain` 分组；该分组下的图层新增按钮也可继续添加地形。多个地形层可以分别命名、设置关键帧和补间，改名不会丢失地形身份。激活 `Polygon` 工具后直接在地形 Mask 上绘制低多边形封闭填充区域；可在同一地形层绘制多个区域，凹形地形、孔洞和相互重叠的区域都会按真实填充轮廓参与碰撞。该区域作为随机破碎的碰撞地形，不会改变普通内容 Mask 的裁剪关系。地形 Mask 的关键帧、形状补间、透明度、轮廓和图层关系随工程保存。生成破碎动画时，会从破碎起始帧逐帧读取所有可见地形层并合并碰撞区域；各层独立移动、变形或在空白关键帧消失都会影响后续碎片运动，隐藏的地形层不参与碰撞，失去地形支撑的静止碎片会恢复下落。已设置地形层的空白帧不会临时补出隐式地面。预览播放会同步显示对应帧的各层地形，正式时间轴播放头保持不动。地形层不参加普通舞台内容渲染，也不会进入普通填色归并；编辑地形或随机破碎预览时只通过半透明 Overlay 显示。破碎轨迹在应用时写入关键帧，之后修改地形需要重新生成破碎动画。

破碎应用在当前 Cel 上，源对象被碎片替换；动画终点会继续写入后续关键帧，不会加入素材库或改变源元件的 Vault 资产。

## 10. Scene、实例与 Vault

场景中的光源全部删除、禁用或强度为零时，2D 与 3D 视图均保留对象原色，不会仅因切换维度而变黑；已初始化光学材质的透射透明度仍按原有规则生效。

`Scene & Animation` 用于装配和动画制作。可先在 `Basic Drawing` 选择源元件，再点击 `+ Instance`；也可从 Vault 直接拖入场景。舞台顶部右侧的 `2D` / `3D` 按钮切换同一场景的观察视图，不会切换场景内容或创建另一份场景；其左侧始终显示当前 `正交` / `透视` 镜头模式的快捷切换按钮。新场景把透视保存为首选 3D 投影，所有 2D 参考视图仍实际使用正交投影；手动选择正交后，该选择会随工程保存并在下次打开时保留。这两组按钮都使用平滑镜头过渡，2D 下设置的投影模式会在切换到 3D 后直接生效。因此在 2D 视图放置的实例切到 3D 后仍然可见。新场景实例位于 `Z = 0` 的基准 2D 平面上，初始 `Scale Z = 0`，因此默认没有厚度。

场景第一次切换到 3D 时会建立一盏白色 `直线光` 和一盏强度为 `50%` 的白色 `环境光`；默认直线光会投射阴影。右侧 `光照` 面板可以新增直线光、点光源、区域光源或环境光，并调整名称、启用状态、颜色、强度、位置、方向、有效范围和区域尺寸。也可在 3D 舞台空白处右键，从 `添加光源` 子菜单直接选择四种类型；除环境光外，新光源会放在右键位置对应的场景基准平面上。直线光、点光源与区域光源支持阴影开关、阴影强度和柔和度；环境光不投射阴影。光源可复制、重置或删除，显式删除全部光源后场景会保持无光状态，不会在重新打开工程时自动补回。

3D 舞台始终以不同的矢量类型图标显示全部灯光：直线光、点光源和区域光源按世界位置投影，环境光图标排列在视口左上角。点击图标或在 `光照` 面板点击光源会选择该光源并取消元件选择；随后选择元件也会取消光源选择。选中光源后会显示对应的彩色操作手柄：直线光、点光源和区域光源可拖动中心自由修改位置，也可使用红色 X、绿色 Y、蓝色 Z 轴手柄沿单一世界轴移动；方向箭头用于直线光和区域光源，圆形范围环用于点光源和区域光源，区域光源另有宽度与高度轴。紧凑滑轨可调整强度，以及已启用阴影时的阴影强度和柔和度。灯光手柄的命中优先于元件 Transform gizmo，检查器数值会在拖动中同步；一次拖动只生成一条撤销记录，按 `Escape`、切换帧或切换场景会取消本次拖动并恢复原值。

每盏灯光在 Scene 时间轴中拥有一个独立的灯光轨道，轨道使用对应的类型图标和灯光颜色。选中灯光会同步选中其轨道；可使用 `F5` 插入帧、`F6` 插入并复制当前灯光状态的关键帧、`F7` 插入空白关键帧、`Shift+F6` 清除关键帧，也可复制和粘贴同类型灯光的 Cel。两个有内容的灯光关键帧之间可以创建 `Create Classic Tween`，位置、方向、颜色、强度、范围、区域尺寸和阴影参数会随自定义 easing 曲线插值；拖动灯光手柄或连续调整补间端点参数时，中间帧保持实时预览，但只在抬手或完成输入后统一重算一次，曲线也只在用户松开锚点后重新计算中间帧。开启 `Auto Key` 时修改当前帧会物化灯光关键帧，关闭时则修改当前保持曝光的来源；空白曝光不会被直接修改。复制光源会同时复制整条关键帧、补间和 easing 动画，删除光源会删除对应轨道。

选择一个或多个 3D 场景实例后，右侧 `3D 表面` 面板可以启用独立材质覆盖，并调整透光率、反射率、金属性、粗糙度、折射率（IOR），以及是否投射或接收阴影。`默认`、`玻璃`、`金属` 和 `哑光` 预设可作为起点；未启用覆盖的实例继承上层实例材质，最终回退到默认表面材质。光源、材质覆盖和阴影选项会随工程保存、编辑器重启快照和 Vault 场景数据一同保留，连续拖动参数只产生一条撤销记录。

Scene & Animation 光学在 2D 与 3D 视图中使用同一套与场景组合缓存绑定的世界空间平面光照方案，并在当前时间轴帧统一求值灯光补间、实例材质和阴影。理解它只需把握四组规则：光源几何、逐像素着色、硬件路径与缓存、相交面与裁切。

**光源几何与分布**

- 环境光作为完整接收面的基础照明；直线光、点光源和区域光源共享相同的线性颜色、强度、元件自有色、漫反射、高光和表面参数响应。
- 三者只保留光源几何差异：直线光使用无限远平行方向且不按距离衰减，点光源使用单个世界位置，区域光使用发射面上 3×3 均匀分层中点的确定性分布，各样本权重之和为 1；极小区域会退化为单一点光源。
- 区域尺寸决定实际发射面分布。调整阴影柔和度不会改变无遮挡时的光源位置、区域采样、直接光照或距离衰减，只会让被遮挡区域保留更多该盏灯的直接光能量；阴影强度与遮挡材质的透光率继续共同决定遮挡深度。
- 点光源与区域光源只影响有效范围与接收平面的实际交区，其能量按每个光源样本到接收点的世界距离使用边缘一阶连续的平滑衰减曲线计算；渐变停止点连续插值，不再绘制可见的矢量分层。
- 推近、拉远或平移摄像机只会重新投影并近裁剪光照覆盖区，不会移动光源、重置有效范围或改变固定世界距离下的漫反射强度；高光仍会按实际观察方向产生合理变化。
- 零厚度 2D 元件可以从平面任一侧观看并接收当前可见面一侧的直接光，反侧光不会穿过表面；有厚度元件继续按正面、背面与侧面的实际朝向分别接收光照。

**逐像素着色**

- 每个渐变像素和纯色像素都会先转换到线性空间，再与环境光和直接光逐像素混合；原渐变的色标外观保持不变，不同色相不会被归并成统一灯色。
- 材质 Alpha 在光照前后保持不变，完全透明的色标不会被高光重新填色。
- 介质高光的基础反射由折射率与反射率共同确定，金属高光按当前像素的元件自有色着色；两者在掠射角下都通过 Fresnel 项逐渐趋向光源颜色。
- 三类直接光都只扣除各自被遮挡的贡献，直线光阴影不会再额外压暗环境光或其他点光源、区域光源；同一挤出元件的不同表面也可互相遮挡，关闭投影的材质不参与遮挡，透光材质按透光率保留对应能量。

**硬件路径与缓存**

- 硬件支持时，舞台会优先创建 Direct3D11 与 Direct2D 共用的显式高性能显卡设备，并将主画布直接提交到交换链；GPU 光学通道不可用时会自动回退到经过验证的 Direct2D 光学路径，GDI 回退保留相同的线性光照公式与预乘透明度语义。
- 稳定画面会复用同一渲染计划和有界光学表面缓存，并设有内存预算。
- 超大或高倍率表面会按整帧统一级别同比降低光学栅格分辨率，继续保持渐变、本色与 Alpha 的逐像素光照；最低分辨率仍无法建立时显示包含原透明度的未受光矢量材质，不再退回会污染色相的彩色叠层。

**相交面与裁切**

- 面相交后的每个碎片只栅格化真实材质边界与自身片段边界的交区，逐像素光照使用固定大小分带缓冲，避免大视口产生与表面积成正比的临时光照数组。
- 相交碎片也会作为独立遮挡域参与直线光、点光源和区域光源的阴影投影，避免直接光穿过交叉表面。
- 直接光与阴影先裁到元件真实表面，再以接收面和现有 Scene Mask/片段边界做最终裁切。
- 元件的实际填色、描边以及元件表面相交后自动生成的连接边都会参与材质响应并接收光照；选择高亮和 gizmo 只是编辑叠层，不参与光照。
- 当前元件表面仍属于 2.5D 平面或挤出表面，因此透光率和折射率用于透明度、漫反射与高光响应，不执行光线追踪折射或焦散。

直接光高光采用 GGX 微表面分布、Smith 遮蔽项与 Schlick Fresnel。降低粗糙度会收紧高光，提高粗糙度会扩大高光尾部；金属度和透光率会减少漫反射。现有光强继续使用编辑器的相对单位，点光源和区域光源保留有效范围内的平滑衰减，旧工程无需迁移。局部光照仍使用径向分布近似，阴影柔和度保留原有语义；当前实现不包含 Blender Cycles 的路径追踪、间接光、多次反射、真实折射或焦散，也不提供与 Blender 相同参数下的像素级一致性。

Scene & Animation 的 2D Front 始终使用正交空间投影，并按实例在当前帧求值后的 Z 深度从远到近绘制和命中。Z 轴经典补间经过 `Z = 0` 时不会切回普通二维画布或丢失深度排序；同一位置的共面元件才回退到图层和对象顺序。

编辑普通场景内容时，左侧工具栏只显示当前视图可用的工具。2D Front 视图提供 `Select`、`Free Transform`、`Distort` 和 Vault；2D 侧向或顶/底参考视图只保留 `Select` 和 Vault；3D 视图提供 `Select`、`3D Transform` 和 Vault。形状、线条、笔刷、文字、填充、取色、渐变与擦除等绘制工具会隐藏，相关工具快捷键也不会激活；平移仍可使用鼠标中键或临时 `Space` 手形操作。

2D 视图中的 `Select` 可以直接拖动场景实例。Front 视图移动 X/Y 并保持 Z，Left/Right 视图移动 Y/Z 并保持 X，Top/Bottom 视图移动 X/Z 并保持 Y；一次拖动只产生一条撤销记录。在 Front 视图使用 `Free Transform` 或 `Distort` 时，控制框、锚点、方向柄、命中和预览都投影到当前镜头画面，提交后写回所选场景实例；右键或 `Escape` 会恢复操作前状态。侧向和顶/底参考视图不显示这两种变形工具，避免把屏幕包围框误写到不同空间平面。

在 2D 视图移动元件时按住 `Shift`，或在 3D Transform 中移动元件时按住 `Ctrl`，所选实例的吸附点会与其他当前可见实例的吸附点相互吸附。判定使用约 `10 px` 的屏幕距离，因此缩放和透视镜头下仍保持稳定精度；多选时会检查所有选中实例，并排除自身和同组选中对象。2D Front 移动只修正 X/Y，侧向和顶/底参考视图只修正当前观察平面；3D gizmo 的轴移动和面移动仍严格保留当前轴或平面约束。命中时舞台显示源点到目标点的指示器，松开、取消或离开移动会清除。包含大量吸附点或曲线对象时，候选点会在当前拖动会话内建立近邻索引并复用；平移、缩放或切换镜头后只更新屏幕投影索引，不再为每次指针移动执行全部点对匹配。

3D 视图中滚轮向上放大、向下缩小；`Ctrl+滚轮` 调整参考视图倍率。透视模式在推近或拉远期间保持固定焦距：摄像机推近时对象变大，拉远时对象变小，同一镜头内靠近摄像机的对象大于远处对象；正交模式直接改变显示倍率，不修改观察距离。滚轮和连续缩放保持方向一致，小幅滚动与多格滚动按实际输入量响应。滚轮缩放只改变投影倍率，不会平移视图目标：向上滚再向下滚相同格数会精确回到原画面。因此镜头焦距或正交尺寸的大小不会影响滚轮缩放的手感，缩放也不会因为当前镜头不同而漂移。

在 3D 视图使用 `Select` 点击场景实例后，会自动切换到 `3D Transform`，实例边缘使用橙色实线高亮。使用 `3D Transform` 可以调整三维位置、旋转和缩放：舞台 gizmo 的 X/Y/Z 轴可直接拖动；移动模式中的 XY、XZ、YZ 方形面手柄可在对应平面内同时调整两个坐标；舞台右上角的 Transform 浮动面板可输入精确数值，并可为移动、旋转选择 `Global`（全局）或 `Local`（局部）坐标系。浮动面板固定在舞台顶部工具条下方，窗口缩放时保持右侧间距，不再占用右侧层级检查器的高度；选中一个或多个实例时会弹性滑入，取消全部选择后弹性滑出，切换到 2D、Mask 或其他工作区时立即隐藏。舞台上的 Transform gizmo 会同步弹性展开和收起；收起开始后立即停止命中，拖动手柄时则稳定为最终尺寸，避免改变操作灵敏度。多选时 gizmo 原点位于所有选中实例的平均位置，局部轴方向取主选实例；缩放固定使用局部轴，以保持当前工程中 Scale X/Y/Z 数据的可表示性。鼠标悬浮到轴、旋转环、平面或缩放标记时，对应手柄会高亮；拖动期间高亮锁定到活动手柄，重叠旋转环按屏幕最近距离命中。

Transform 面板中的 `R Pivot` 和 `S Pivot` 分别编辑旋转锚点与缩放锚点。该功能只在单选一个实例时启用：点击目标按钮后，舞台自动切换为局部坐标的移动 gizmo，可拖动轴或平面手柄；锚点移动期间元件的当前外观和空间位置保持不变。也可在对应 XYZ 数值行输入精确坐标，或点击行尾的重置图标恢复为零；再次点击当前锚点按钮，或选择 Move / Rotate / Scale，可退出锚点编辑。右键、`Escape`、切换帧或切换场景会取消当前拖动，一次完成的拖动或重置只产生一条撤销记录。`Scale Z = 0` 时缩放空间在局部 Z 方向已压平，缩放锚点的 Z 手柄不会产生位移；可使用数值输入，或先增加厚度后再拖动。

3D Transform 拖动期间按住 `Shift` 可把移动、旋转、缩放和厚度调整速度降到正常的 10%；中途按下或松开不会重算此前的位移。移动时 `Ctrl` 启用吸附点对齐，可与精细调整配合使用。左键释放提交，右键或 `Escape` 取消并恢复拖动前状态；其他鼠标按键不会提交当前手柄拖动。

旋转操作以每个实例的 `R Pivot` 为中心，缩放操作以 `S Pivot` 为中心；多选时群组 gizmo 位于所有对应锚点的平均位置，并保持各实例锚点相对群组中心的运动关系。两个锚点会随复制、撤销、关键帧、经典补间、编辑器重启和工程保存一同保留；旧工程未包含锚点数据时默认使用零锚点，显示结果与原行为一致。

轴或面拖动从按下时的固定原点、固定坐标基和固定约束面持续求解，不会把正在移动的 gizmo 位置反馈为下一次计算基准；普通拖动回到按下点会精确恢复原状态且不产生空撤销记录。中途切换精细调整后，按各阶段的调整速度累计位移，右键或 `Escape` 可完整恢复起始状态。旋转使用轴角矩阵连续合成，并在写回现有 X/Y/Z 旋转字段时选择最接近起始姿态的等价分支，因此旋转后的局部轴保持正交，接近 X 轴 ±90° 或连续跨过 ±180° 时也不会发生操作轴锁死或跳向。也可先按 `G`、`R` 或 `S` 进入移动、旋转或缩放，再按 `X`、`Y` 或 `Z` 约束当前坐标系的轴并移动鼠标。左键或 `Enter` 确认，右键或 `Escape` 取消。一次确认、一次 gizmo 拖动或一次连续数值编辑只产生一条撤销记录。

在 3D 视图按 `F` 会聚焦全部选中实例，并保持当前镜头朝向和正交/透视模式。元件能以 100% 倍率完整显示时保持 100%；元件过大时才自动降低倍率以完整装入画面。聚焦只改变镜头，不修改实例坐标，也不产生撤销记录。

在 3D 视图中选择同一普通场景图层、当前帧有内容且层级连续的两个或更多实例后按 `F8`，可把它们组合为可复用的 3D 元件。新元件以所选实例当前帧位置包围盒的中心为枢轴，保留成员稳定 ID、XYZ 位置、旋转、缩放、外观和全部状态关键帧，并在原层级位置留下一个缩放为 100% 的根实例。一次 `Ctrl+Z` 会恢复原实例并移除刚创建的 3D 元件；跨图层、层级不连续或空白曝光上的选择不会被组合。

`Hierarchy`（层级）默认隐藏，不再占用右侧检查器。点击左上角主菜单，选择 `Window → Hierarchy`（窗口 → 层级），可打开或隐藏独立浮动窗口；拖动标题栏移动窗口，拖动边框调整大小。关闭按钮或 `Esc` 只隐藏窗口，同一会话中再次打开会保留窗口位置、大小与树节点状态。切换工作区或工程时，层级内容继续同步当前编辑上下文。窗口跟随主工作台，不单独显示任务栏按钮。

在浮动 `Hierarchy` 中双击对象会聚焦该对象；双击普通图层会聚焦该层当前帧的可见对象，双击文件夹图层会连同可见后代图层一起聚焦。2D Front 会居中并在 2% 到 100% 的倍率范围内按边界适配，超大目标使用 2% 的最远视图；2D 参考方向和 3D 视图使用当前参考镜头平滑过渡。空图层、隐藏内容、根节点和占位项不会移动镜头。该操作保留当前投影与观察方向，只改变视图，不修改工程内容或创建撤销记录。

元件源几何仍是二维平面。`Scale X` 和 `Scale Y` 改变元件尺寸和线条中心线范围，但不会放大或缩小可见 Stroke 的粗细；渐变 Line 的透视网格、Freeform 挤出轮廓、背面、侧面和选择边也使用同一份补偿线宽。`Scale Z` 在场景实例上表示沿元件局部 Z 轴挤出的厚度，单位为 `vu`，可以从 `0` 开始增加。非零厚度的背面使用元件填色，侧壁优先使用可见描边颜色；没有可见填色或描边时，分别回退到可用的轮廓或填色。旋转实例后可以从侧面看到该厚度。

`Line` 的起点和终点样式在 2D 与 3D 视图中保持一致：`Round` 使用圆形端点，`Sharp` 使用平端；相连且全部为 `Sharp` 的线段会在共享端点形成尖角连接，并把共同挤出体视为同一个物理壳体，前描边不会与自己的侧面或背面互相切分而出现穿透。线段被 3D 镜头近裁剪面截断时，临时断口始终保持平端，不会生成额外的圆形凸起。

3D 视图中，普通混合图层的实例会跨图层按实际摄像机空间深度从远到近绘制，图层位置不会覆盖物体真实的前后关系。两个非共面二维元件相互穿过时，系统沿真实三维交线拆分可见区域，分别判断交线两侧的前后关系，并使用元件描边或轮廓颜色自动绘制连接边；连接边同时受两个元件的场景遮罩约束。只有描边的 `Line`、`Pencil` 和闭合形状的边界 Stroke 都会按投影后的实际笔画面积参与相交面分片与局部排序，因此位于遮挡面前后的线段或外边界会分别显示或隐藏。三个或更多有限平面发生交叉或分离重叠时，系统还会按其他平面的实际投影轮廓继续拆分局部片段，并用当前相机射线比较每个重叠区域的真实深度；即使出现 A 遮住 B、B 遮住 C、C 又遮住 A 的循环关系，也不会退回整块物体的平均深度排序。密集交叉场景的分片预算只限制局部深度切分复杂度，不会删除已经确认的平面对交线；漫游镜头时，同一平面对和未发生真实遮挡拓扑变化的局部分片保持稳定身份，第三个面产生的内部切点也不会叠加圆形端帽而形成跳动亮点。元件内部的渐变和未拆散 SVG 图案使用与外轮廓相同的透视投影，靠近镜头的图案间距与细节会同步放大，远端会同步缩小，不会只改变外框；元件穿过镜头近裁剪面时只裁掉镜头后的部分，剩余材质继续按对应源坐标显示。旋转镜头、切换正交/透视或在 2D/3D 间切换后会按当前投影重新计算，不会沿用旧视角的遮挡结果。只有两个表面处于同一空间平面、法向朝向相同并发生重叠时，才使用图层顺序作为遮挡关系的兜底；同一图层内继续使用对象顺序。非 `Normal` 混合模式或非 100% 组透明度会形成软件合成边界，以保留对背景的混合语义；边界内部及边界前后的连续 Normal 图层仍分别执行空间切分。空混合层、隐藏内容或与交叉面无关的其他混合组不会全局关闭连接边生成。

无描边渐变填色在透视视图中使用固定复杂度的材质域和精确屏幕轮廓共同渲染；曲线路径、孔洞和分离区域仍由原矢量边界裁切，透视细分精度不会为了性能而降低。最终外轮廓只执行一次抗锯齿覆盖，不会把轨迹渐变的末端颜色补画成整圈深色边线；Direct2D 不可用时的 GDI 回退保持相同边界行为。

未拆散 SVG 在 3D 透视视图中使用随视口大小调整的纹理分辨率，并只计算屏幕内的像素，避免镜头靠近大幅背景或穿过元件平面时生成超大纹理。GPU 支持时可使用单次透视纹理绘制，减少逐三角形提交的开销；近裁剪、透明度、光照和遮罩继续生效。高倍放大的纹理细节会受素材栅格采样分辨率限制。多个动画元件可以在前台显示与后台预生成中共用 SVG 素材；缓存更新不会中断正在进行的绘制。

SVG 实例的平移、旋转以及可保持矩形两轴正交的缩放会复用源素材，避免动画每帧重新生成 SVG 文本；剪切、镜像和退化变换继续使用完整变换路径。严格共面且没有启用场景光学的 3D 内容会跳过无需参与深度判断的笔画遮挡轮廓构建；相交表面、挤出和光学计算仍保留所需轮廓。

SVG 位图会复用同一内容、宽高比相容且分辨率足够的已有缓存。动画的小尺寸素材变体最多保留 1024 项，CPU 位图和 Direct2D 纹理各自仍受 512 MiB 上限约束，以减少反复播放时的重新栅格化；这不代表所有场景都已达到 60 FPS。

3D 动画会优先使用高性能硬件显卡；光学结果始终保留原有线性颜色、局部灯光、阴影覆盖与材质 Alpha。播放期间 GPU 光学层、边界和渐变停止点缓冲会按内容复用，只在光照几何改变时更新，减少逐帧驱动分配。硬件光学通道不可用时自动回退到原有 Direct2D/GDI 路径，软件光照回退在播放与镜头交互时使用预览分辨率，暂停后恢复完整分辨率。预渲染缓存淘汰当前帧时会使显示缓存失效，下一次重绘会重新生成该帧。

选择场景中的普通内容层，并在时间轴右键菜单中使用 `New Mask Layer`，即可为该内容层建立 2D Scene Mask。选择 Mask 图层后，舞台切换到独立的二维遮罩编辑面，维度按钮固定显示为禁用的 `2D`，完整场景作为不可编辑参考；此时启用基础形状、线条、路径、笔刷、文字、填充和擦除工具，并禁用 3D Transform。右侧检查器随当前工具提供材质、形状、铅笔、笔刷、混合笔和文字参数，Pressure Brush 继续读取数位笔压力。Mask 同时裁切该内容层中的 2D 与 3D 实例；空白关键帧、隐藏 Mask 或当前帧没有有效遮罩几何时，被遮罩内容不显示。完成后选择普通内容层即可返回场景装配。

实例可以独立设置：

- 二维位置、旋转、缩放和倾斜，以及三维位置、旋转和缩放。
- 可见性、Alpha 和色调。
- 独立 FPS、循环、仅播放一次或停留帧。

在 `Scene & Animation` 的二维正面视图中双击实例，会留在当前工作区就地编辑其源元件。左侧绘画工具和快捷键重新启用，时间轴切到元件自身的图层与帧；编辑直接更新共享源元件，因此返回后所有引用它的实例都会更新。场景其余内容以进入时的画面加半透明白色遮罩作参考，不可选中或绘改，也不会保存为实际蒙版图层。参考图随编辑画布平移和缩放；实例的旋转、缩放或倾斜会换算到元件局部坐标，元件按自身坐标编辑。参考范围为进入时的可见画面。

就地编辑时，双击元件中的嵌套实例可以继续逐层进入编辑。左上角显示完整路径，例如 `场景 → 角色 → 手臂 → 手掌`；长路径可横向滚动查看，不会省略中间层级。点击路径中的场景或任一祖先元件可直接返回该层；在没有未完成绘画操作时按 `Esc` 返回上一层。返回会恢复该层进入前的帧、视图和实例选择，每一层的周围内容均保留为不可编辑的淡化参考。切换到基础绘制会退出全部就地编辑层级。空间旋转或非线性变形实例沿用独立元件编辑入口，三维和非正面参考视图不提供该就地绘画入口。普通场景模式仍只允许实例操作。右侧场景和实例列表采用增量刷新，切换工作区或帧时不会重复清空列表。

Double-click a placement in the 2D Front view of Scene & Animation to edit its shared source symbol in place. Drawing tools and shortcuts are enabled, and the timeline shows the symbol's own layers and frames. Double-click nested placements to edit successive symbol levels. The top-left breadcrumb shows the complete scene-to-symbol path, with horizontal scrolling for long paths. Click the scene or any ancestor symbol to return directly to that level; Esc after finishing a drawing gesture returns one level. Each return restores that parent's frame, view and placement selection. Surrounding content at each level is a frozen viewport reference faded with a translucent white veil; it is neither selectable nor saved as a mask layer, and follows canvas pan and zoom in symbol-local coordinates. Switching to Basic Drawing exits all levels. All placements of the shared symbol reflect source edits. Spatially rotated or warped placements retain the independent symbol editor; 3D and other reference directions do not offer in-place drawing.

机制：实例只保存“引用 + 变换 + 播放/外观状态”。多个实例可以引用同一个源对象；源对象也可嵌套其他对象，但软件会拒绝自身引用和循环引用。

### 元件混合模式 / Symbol Blend Mode

在 Basic Drawing 中选择嵌套元件，或在 Scene & Animation 中选择元件实例，在右侧元件属性的 **Blend Mode / 混合模式** 下拉框选择模式。先合成元件内部的内容和滤镜，再以元件整体与同一父组中位于其下方的内容混合；内部重叠图形不会分别对外层背景重复应用实例的混合模式或透明度。Isolated Group 创建透明隔离组，Invert 对覆盖区域的背景颜色反相。普通父元件包含非 Normal 子元件时会自动保留隔离边界。

多选值不同时下拉框显示空白，选择模式统一修改选中实例，并合并为一次撤销。编辑沿用 Auto Key 与保持曝光规则；传统补间在中间帧保持起点模式，终点使用终点模式。设置随工程保存/打开、复制、重启及 `.V2DSymbol` 文件保留；旧工程默认 Normal。带整体混合效果的元件不能通过 Break Apart 丢弃合成边界；普通 SVG 导出遇到无法保留的隔离混合组或 Invert 会报错，应保存 V2D 工程。混合组沿既有软件合成路径显示，可能增加复杂场景的渲染开销。

### 元件滤镜 / Symbol Filters

在 Basic Drawing 中选择嵌套元件，或在 Scene & Animation 中选择元件实例后，点击右侧元件属性下方的 **元件滤镜 / Symbol Filters** 按钮可打开独立浮动窗口。窗口可移动、缩放；关闭后可再次打开。通过下拉框选择效果，按 **+** 添加、按 **−** 移除所选效果；移除保留参数，再次添加可恢复。每种效果最多一项，可叠加不同效果。普通矢量图形不显示这组属性；需先将图形组合为元件。

- **模糊 / Blur**：分别调整水平、垂直模糊范围，任一方向可设为 0。
- **发光 / Glow**：调整水平/垂直范围、强度、不透明度和发光颜色。
- **投影 / Drop Shadow**：调整水平/垂直范围、强度、不透明度、颜色、角度和距离；0° 向右，90° 向下。
- **质量 / Quality**：模糊、发光、投影、斜角、渐变斜角和渐变发光均可独立设置 1–15 次模糊处理。常用 1（低）、2（中）、3（高），默认 3；更高次数增加平滑和扩散，也增加计算量。旧工程没有此字段时继续使用原有三次处理，不改变打开后的画面。Quality 随保存、重启、复制、元件包和关键帧保留，支持多选、撤销和 Auto Key。CPU/GPU 使用相同次数，滤镜边界与缓存也计入 Quality。原生效果使用 V2D 现有模糊核；导入的 Flash 效果链使用独立兼容路径。两者均不代表已通过 Adobe 逐像素画面对比，直接导入仍检查具体滤镜和属性。
- 三个开关相互独立，可以同时启用。关闭效果会保留参数，再次打开继续使用；合成顺序固定为模糊 → 发光 → 投影。每种效果作用于元件整体，嵌套元件先计算自身效果，再进入外层元件的效果组。
- 尺寸以 100% 画布缩放下的舞台像素计，参数范围为模糊 0–128 px、投影距离 0–256 px、强度 0–10、不透明度 0–100%。多选时显示主选实例的参数及混合值提示，调整一项只修改所选实例的这一项。
- 数值连续调整、一次调色分别合并为一次撤销；取消数值操作恢复原值，关闭颜色选择器或按 `Esc` 保留当前颜色，可用 `Ctrl+Z` 撤销。编辑沿用 Auto Key 与保持曝光规则，参数随工程保存/打开、重启恢复、元件复制和 .V2DSymbol 文件保留。当前传统补间在中间帧保持起点滤镜设置，抵达终点时使用终点设置。
- 新增 Bevel / 斜角、Gradient Bevel / 渐变斜角、Gradient Glow / 渐变发光：支持模糊半径、强度、不透明度和双色端点；斜角还支持光照角度和距离。斜角在元件轮廓内着色，渐变发光位于轮廓后方。执行顺序为模糊、发光、投影、斜角、渐变斜角、渐变发光。新增效果与原有滤镜一起使用 GPU 加速；设备不可用时使用完整精度 CPU 合成。
- 2D 和参考投影视图均显示滤镜。支持 Direct3D 11 的显卡会自动加速模糊、发光、投影、斜角、渐变斜角和渐变发光，优先使用高性能显卡；中间结果保留浮点透明度，再回到现有全分辨率图层合成，保留透明边缘和遮罩。很小或超出显存工作区上限的滤镜区域，以及设备不可用/计算失败时自动使用 CPU，不降低效果质量。较大模糊范围、嵌套效果和大量滤镜元件仍会增加渲染开销。选择/命中仍使用原元件几何，不把光晕或影子变成可编辑图形。
  GPU acceleration applies to blur, glow and shadow on Direct3D 11 hardware, with automatic CPU fallback. Layer/mask composition and final readback remain part of the frame cost. For diagnostics, `VECTOR_DISABLE_GPU_FILTERS=1` disables filter acceleration only.
- 普通、不透明度为 100% 的图层使用原生预乘透明度快速合成；带滤镜时只合成包含完整光晕和投影的实际受影响区域。滤镜输入像素、区域尺寸、缩放和参数完全相同时复用最近使用的多组结果，减少多个元件交替重绘时的重复计算；内容或边缘采样发生变化会重新计算。每个合成器的 GPU 结果缓存最多 8 组、输入与输出合计不超过 32 MiB，并随合成器释放。滤镜像素和 CPU 工作缓冲区复用数组池，减少大数组反复分配引起的停顿。
  Normal layers at full opacity use native premultiplied composition. The last filter result is reused only when input pixels, dimensions, scale and every filter parameter match exactly.

元件入口的开放／当前滑块仅按开放／当前文字和图标确定紧凑宽度，较长元件名称以省略号显示，滑块显示软件图标，目前仅提供 UI 状态切换；切到当前约两秒后显示当前元件名称，不改变编辑范围或添加元件逻辑。

### Vault

左侧工具栏的 Vault 打开项目素材列表，抽屉与上方面板交接的顶边采用柔和分界线颜色。面板使用小号弱化标题、统一分类按钮和紧凑的主题搜索／标签筛选框；右侧标签图标用于管理选中素材的标签。导入、打开、文件夹、标签及素材列表图标采用统一的圆润线性风格，右上角操作图标保持一致的轮廓尺寸与圆角，新建文件夹使用与时间轴相同的文件夹图标。输入框、下拉框和按钮随主题及直角／微圆角偏好变化，搜索与列表之间保留清晰间距。
The Vault panel uses a muted heading, consistent category buttons and compact themed search/tag filters. The tag icon manages tags for the selected asset; controls follow the theme and corner preference.

工具栏位于左侧时，会随素材库抽屉的开合移到可见边缘右侧；刷新工具状态或调整窗口大小后也保持可见，关闭素材库后回到左边缘。顶部工具栏不随抽屉横向移动。

The left toolbar follows the Vault drawer's visible edge while it opens or closes, stays accessible after tool refreshes and window resizing, and returns to the left edge when the drawer closes. The top toolbar keeps its horizontal position.

素材库重新打开时复用未变化的素材列表，仍检查项目变化与当前列表中的文件失效状态。开合动画按实际经过时间推进，重复位置不触发裁剪更新；动画中再次点击可从当前位置反向开合。

Reopening Vault reuses unchanged asset rows while checking project changes and file availability in the current list. The drawer animation follows elapsed time, skips unchanged clipping positions, and reverses from its current position when toggled again.

项目素材列表的操作：

- 单击选择当前行；`Ctrl+单击` 追加或取消单行，`Alt+单击` 仅取消该行；`Shift+单击` 选择可见行范围，`Ctrl+Shift` 追加范围，`Alt+Shift` 取消范围。列表聚焦时 `Ctrl+A` 全选当前可见行（不包含折叠文件夹中的隐藏行），`Shift+方向键` 扩展范围，`Enter` 打开当前素材或展开／折叠文件夹。打开、拖入和右键操作仍作用于当前行，不批量执行；列表聚焦时 `Delete` 不会删除舞台对象。
  Click selects one row; Ctrl-click toggles it, Alt-click removes it, and Shift-click selects a visible range. Ctrl+Shift adds a range and Alt+Shift removes it. With the list focused, Ctrl+A selects visible rows, Shift+arrow extends the range, and Enter opens the current asset or toggles its folder. Open, drag and context actions target the current row; Delete does not delete stage objects while a list has focus.

- 分类栏包含 `基础元件`、`3D 元件`、`SVG 素材` 和 `图片`。基础元件显示普通可复用绘制元件；3D 元件显示在 3D 场景中通过 `F8` 组合得到的空间元件；两类元件均可打开或拖入场景复用。
- `SVG 素材` 保存工程托管副本，双击、点击放置按钮或拖到舞台会创建共享素材引用。通过 `Import SVG...` 或拖文件直接导入舞台时，也会登记共享素材；同一来源再次放置会使用已有工程内容，不会自动从外部文件更新。原文件移动或删除不影响已导入的工程。
  SVG assets are stored in the project. Library placements share the asset; direct SVG import also registers a shared resource. Reimporting the same source uses the stored project content until an explicit update.
- `图片` 保存位图素材：导入时图片字节会被复制进工程的 `.Vault/Images` 托管目录，源文件随后移动或删除都不影响工程（见 [11. SVG、图片与文本](#11-svg图片与文本)）。点击图片上传图标（导入图片）选择并验证图片，双击、点击使用按钮或拖到舞台会把它作为**一个整体对象**放置，可整体选择、移动、缩放和旋转，但不能 `Break Apart`。也可以从资源管理器把图片文件直接拖入素材库的资产列表区域导入素材。
- 图片和 SVG 的右键菜单提供 `从源文件更新`、`替换源文件...` 和 `创建独立副本`。更新会传播到所有引用位置，并保留各放置对象的位置、尺寸、旋转、畸变和动画；验证或读取失败时保留上一次有效内容。独立副本拥有新素材 ID，已有放置不会自动改用副本。搜索可匹配名称和路径；标签与素材文件夹仍只适用于元件。
  Images and SVG assets support Update from Source, Replace Source, and Create Independent Copy. Updates preserve placement and animation; failed updates retain the last valid content. An independent copy receives a new asset ID without rebinding existing placements.
- 删除未使用的素材会直接移除库项目；正在使用的素材会列出直接使用位置、受影响的嵌套元件和场景。可选择同类素材替换引用后删除，或删除素材及全部使用位置，默认取消；更新、独立副本和删除均可一次撤销。引用数量按存储的放置对象及各关键帧统计，不等于某一帧可见的实例数量。
  Deleting an asset in use shows its usage locations and affected symbols/scenes. Replace references with an asset of the same type, or delete all uses; Cancel is the default. Updates, independent copies and deletions support a single undo.
- 顶部搜索框采用现代紧凑外观，带左侧搜索图标、舒展的文字留白及随主题变化的圆角／直角边缘；按素材名称、类型、说明和已分配的标签名称搜索。
- 标签下拉框：独立按某个标签筛选；可与文字搜索叠加，进一步缩小结果。
- 右键素材时会先收起悬停预览；展开 `Set Tags` 后可直接从已有项目标签中勾选或取消勾选，每次选择后子菜单会保持打开，便于连续设置多个标签。
- 选中素材后点击标签管理图标，或右键选择 `Manage Tags...`：新增、重命名、改色或删除项目标签，并为当前素材分配或取消分配标签。删除项目标签会同时从所有素材移除该标签。
- 标签管理器中的 `Add` 会立即创建并分配一个随机颜色的 `New tag`；重名时自动追加序号。选中标签后可直接使用管理器内的 R、G、B 滑杆或数值框调整颜色。
- 标签可使用自定义颜色。已分配标签会以彩色小圆点显示在素材名称右侧；悬停可查看该素材的全部标签。
- 素材行和标签管理列表在悬浮、按下与选中时使用短过渡，并只重绘发生变化的行；关闭 Windows 界面动画后会立即切换状态。
- 双击对象：进入对应元件。
- 拖到 Scene & Animation：创建场景实例。
- 拖到 Basic Drawing：创建嵌套实例。
- 悬停：稳定预览实际几何和嵌套内容；元件存在多帧时按当前工程 FPS 循环播放，单帧元件保持静态。

标签定义、颜色和每个元件的分配关系会随工程保存与打开，并在编辑器重启恢复时保留。

图片或 SVG 转换为元件时保留内部素材引用；复制元件也继续共享内部素材。拆散元件只解除元件容器，内部图片/SVG 仍引用库素材；SVG 继续 `Break Apart` 成原生路径后才脱离该素材。`.V2DSymbol` 版本 2 携带共享 SVG 依赖，导入其他工程时为这些依赖生成新 ID，旧版元件包仍可读取。
Converting content to a symbol preserves asset references, as does duplicating a symbol. Breaking apart the symbol retains its internal image/SVG references; converting the SVG itself into editable paths detaches those paths. Version 2 symbol packages include shared SVG dependencies with fresh IDs on import.

右键 `Break Apart / 拆散` 可把选中的实例冻结为当前帧普通几何。拆散不会删除源元件；带非 `Normal` 图层混合或启用滤镜的实例不能无损转换为普通矢量时会被拒绝，需先关闭效果再拆散。

## 11. SVG、图片与文本

在主菜单选择 `Import SVG...`，或把单个 `.svg` 文件拖到 Basic Drawing 舞台。导入结果先作为一个整体对象，可选择、移动、变形、复制和保存。导入对象会保留不含扩展名的 SVG 文件名；如果当前元件完全没有绘制内容或嵌套实例，它也会自动改用该文件名。

需要编辑内部内容时，右键选择 `Break Apart`：

- 基础路径、形状和文字优先转换为普通矢量。
- 拆散后的 SVG 矢量与同类型原生矢量使用相同的选择 UI：整对象 Path 使用原生选择框，填色区域使用原生填色高亮，线条和边界段使用原生描边高亮、端点与曲线控制柄。框选多个对象沿用原生多选规则，SVG 的线宽、端帽和转角不会改变选择 UI；高亮只用于编辑提示，不修改实际描边宽度。
- Adobe Animate 等软件使用 `<defs>` 与 `<use>` 引用的基础图形也可拆散为可编辑路径与纯色填充，保留重复放置、嵌套引用、位置变换和绘制顺序；未引用的定义不会生成对象。基础曲线保留 Bézier 控制点，纯色填色与描边分别转换；渐变与复杂效果继续采用外观轮廓化回退，不恢复原始渐变参数。
- 普通实色 SVG 描边保留中心路径、曲线控制点、原始宽度、`butt/round/square` 端帽、`miter/round/bevel` 转角和 miter limit；小于 `1` 的细描边不会被放大。这些属性随保存、撤销、复制和场景组合保留。为保持逐元素的 `fill -> stroke` 顺序，必要时拆分绘制层；非等比缩放或特殊描边仍转换为等价填色轮廓。SVG 本身写成填色的黑色轮廓仍属于填色，不能从文件中恢复不存在的中心线参数。
- 工程保存重开保留 SVG 描边样式、开放 Bézier 路径、隐藏边界段和边界脱离状态；导出的 SVG 画布计入方形端帽与尖角。GDI 软件渲染和扭曲后的矢量描边也保留这些端帽与转角属性，细线按实际宽度交给渲染器栅格化。
- 普通命中和悬停轮廓按遮挡处理，完全被衣服覆盖的下层素体不会参与选择。明确选中仍有可见区域的填色后，边界编辑显示该填色的完整闭合轮廓，覆盖在边缘的描边不会截断控制点。填色工具优先命中可见填色区域；与填色边界重合的 Bézier 描边仍可双向同步控制点——拖动描边的曲率方向柄会带动填色对应边，移动整条描边则不会移动填色。不能确认重合关系的导入轮廓保持独立，不按近似端点强行联动。原始几何保留，移开遮挡后可再次编辑。填色孔洞和半透明覆盖下的路径仍可使用。遮罩、滤镜、非 Normal 混合等复杂合成暂不作为完全遮挡依据。
- `nonzero` 填充和 `<text><tspan>` 字形会转换为等价轮廓，复合孔洞与嵌套文字不会因引擎的偶奇填充规则或空父文本节点而丢失。
- 图片、滤镜、复杂裁切或混合效果会按最终外观有限精度轮廓化。
- 每个 SVG 的拆分图层会放入一个独立 Folder，文件夹使用导入时保存的 SVG 文件名；旧工程中没有文件名元数据的 SVG 使用 `SVG`。
- 拆分器会按实际填色区域和描边宽度判断源层是否重叠。互不重叠的显式图层、普通分组或连续绘制区段会合并到同一个 Drawing Layer；存在重叠时只保留维持原绘制前后关系所需的最少层数。
- 新图层统一继承源图层的图层颜色和显示属性；对象原有填色、描边和透明度不会被图层颜色重写。栅格轮廓化回退只创建一个图层。
- 单个零面积或量化后退化的 SVG 部件会被忽略，不会阻止其余有效内容完成拆散。
- 拆散作为一次撤销事务。

机制：未拆散 SVG 保留整体外观，但不开放内部材质和节点。复杂效果轮廓化后在高倍缩放下可能出现轻微像素边缘或颜色分级。

文本可保持可编辑状态，也可通过 `Break Apart` 转换为轮廓 Path。转换后不再保留文字内容和字体编辑能力。

### 导入图片

剪贴板图片按像素内容去重（含尺寸、颜色与透明度）：相同图片再次粘贴只新增放置对象，复用 Vault 中已有素材及其导入设置，不重复导入或创建缓存源文件。保存重开后也会检查现有素材；内容发生变化的图片会作为新素材导入。

从截图工具或其他软件复制图片后，在 Basic Drawing（或场景遮罩的绘图层）按 Ctrl+V，可直接粘贴到当前选中的可编辑绘图层。图片中心对齐当前舞台视图的正中心，平移或缩放视图后仍按当前视角定位；时间轴聚焦时也支持。粘贴无需导入设置确认，保留 PNG 剪贴板内容的透明度，图片自动选中，并加入 Vault 的图片分类。Ctrl+Z 撤销此次放置，素材仍可在 Vault 中复用。保存后图片进入工程托管目录；保存前由本地图片缓存保留，支持编辑器重启。文本输入框继续使用文本粘贴；在软件内复制对象或 Cel 后，粘贴优先使用刚复制的内容，直到系统剪贴板被更新。

在主菜单选择 `Import Image...`，或在 Vault 的 `图片` 分类中点击添加按钮，可一次多选图片导入素材库；也可把单张图片直接拖到 Basic Drawing 舞台的一个位置放下，或把一批图片拖入素材库的资产列表区域。支持 `PNG`、`JPEG`、`BMP`、`GIF`、`TIFF` 和 `WebP`。

批量导入会先读取有效文件，再统一确认设置，最后写入素材库。独立进度窗口分别显示读取／写入阶段的已处理数量、总数与百分比；当前行显示文件名，悬停可查看完整路径。点击 `Cancel / 取消`、按 `Esc` 或关闭进度窗口会停止后续导入，已成功导入的素材保留；取消设置对话框也会停止整批导入。重复路径及在预览中移除的图片计为跳过，单个文件失败不会阻止后续文件，结束后统一显示成功、跳过、失败与未处理数量及最多 5 条错误详情。工程图片素材上限仍为 4096 张，导入 3000 张前需确认剩余额度足够；批量导入只添加素材，不自动把全部图片放到舞台。

拖放约定：

- 拖入素材库只导入素材，不放置到舞台；拖入舞台会先导入素材（首次需要确认导入设置），再在落点放置一个位图对象。
- 一次拖入多个文件时，素材库会共用第一个文件确认的导入设置，不再为每个文件重复弹出对话框。
- 同一路径的图片（或同一路径的 SVG 链接）不会重复添加；已经导入过的文件直接复用现有素材。
- 同一次拖入必须是同一种素材：图片、`.svg` 或 `.V2DSymbol` 各自一批。混入其它类型时整批拒绝，避免只导入选中内容的一部分。
- 拖入舞台时先显示落点预览，预览使用与实际放置相同的解码尺寸和宽高比；预览来自临时副本，悬停期间不会修改工程，松开鼠标才写入。

导入窗口默认显示图片预览、文件名和像素尺寸，透明区域使用棋盘格。普通导入可直接点击 `Import / 导入`，之后在舞台上移动、缩放；编辑已有素材时按钮为 `Apply / 应用`。预览在后台加载，切换图片时显示加载提示；无法读取的预览显示明确提示，不会一直停留在加载状态。批量导入可用左右箭头切换大图，点击 `All images / 全部图片` 展开缩略图网格；首次打开时保留当前大图并显示加载数量，首屏缩略图就绪后再切换，不先展示空黑格。准备期间再次点击可取消切换。网格支持滚轮滚动，删除图片后仍可继续滚动。Ctrl 点击切换多选、Shift 点击范围选择；移除按钮仅在缩略图悬浮或获得键盘焦点时显示，使用细线叉号与主题一致的小按钮底板；点击可移除所选图片（至少保留一张），只移出此次导入，不删除源文件。删除后保留当前图片（若仍在批次中）及滚动位置，剩余缩略图补齐空位。

`Advanced settings / 高级设置` 默认折叠，支持鼠标及键盘展开。展开后使用紧凑两列：首行是显示方式和最大分辨率，第二行是放置比例和保留透明背景；字段标题在输入框上方，说明在悬停标题时显示。底部保留解码尺寸与基础放置尺寸。文件信息、尺寸和说明使用主题辅助文字色，主要操作保持清晰强调。

新导入默认使用 `Automatic / 自动识别`：首先检查原始解码尺寸，宽或高任一边超过 256 像素，一律选择平滑；恰好 256 像素及以内再依据有限采样中的调色板复用、透明度与连续渐变特征选择平滑或像素风。该尺寸上限对自动模式下的每张图片统一生效，包括大尺寸像素作品；可手动选择像素风覆盖整批，不受此上限限制。相邻颜色接近不直接视为平滑过渡，低对比的灰色／同色系像素贴图仍可识别为像素风。连续渐变需要相邻三点同向、近似等步长地变色；交替抖色不算连续渐变。混合批次分别识别，不把第一张的结果强加给其它图片。这是启发式识别，不保证识别所有像素作品，也不单凭尺寸小就判定为像素风。编辑已有素材时保留原显示设置，不自动覆盖。像素风在大图、缩略图和库中弹出预览窗均使用最近邻采样。

预览右下角的编辑图标没有按钮底色，悬停显示 `Edit image / 编辑图片` 提示，点击后直接在原导入窗口内切换到图片编辑页，不再叠加新窗口；窗口位置和尺寸保持不变。编辑页沿用当前显示方式：像素风（含自动识别为像素风）使用最近邻采样，放大、缩小和移动构图时保持像素边缘；平滑模式继续使用平滑插值。拖动四边或四角调整裁剪框，在框内拖动图片调整构图，滚轮或放大／缩小按钮改变图片缩放；图片始终覆盖裁剪框，不会露出空白。拖过对边时停在最小范围，不翻转选区。底部可选择自由、原始比例、1:1、4:3、3:4、16:9 或 9:16；比例锁定后拖边框继续保持比例，最终像素取整可能有 1 像素误差。

编辑工具栏提供 `Flip horizontally / 水平翻转` 和 `Flip vertically / 垂直翻转`，使用镜像图标并高亮当前状态；再次点击恢复，两个方向可以组合。翻转不改变选中的源像素范围与裁剪尺寸，按先裁剪、后镜像的顺序应用于预览和最终导入；原文件不改写。编辑页点击 `Back / 返回` 可回到进入前的导入设置和滚动位置，未确认的编辑不会提交。裁剪和翻转仅作用于当前图片，切换图片后各自保留已确认的编辑，未编辑的图片保持原样；大图、网格缩略图和最终导入均使用各自的裁剪与翻转。显示方式、最大分辨率、透明背景和放置比例仍为整批共用。各图片的编辑设置随项目保存，旧项目缺少翻转字段时默认不翻转。

左右按钮或缩略图切换当前图片时，预览立即请求加载，不等待设置调整的防抖计时；已有缓存直接复用，过期请求取消且不能覆盖当前图片。首次打开仍需要解码时间，设置连续调整仍保留防抖。批量预览到最后一张后，再点击右箭头显示“已经是最后一张”，不会循环跳回第一张。编辑页的“返回”只回到原来的图片预览与设置页，不执行导入；只有在预览设置页主动点击“导入／应用”才提交。

`Precise crop / 精确裁剪` 默认收起，展开可输入当前显示方向下的左侧、顶部、宽度和高度；直接输入数值会切换为自由裁剪。选区聚焦时方向键移动 1 像素，Shift+方向键移动 10 像素；Esc、失去鼠标捕获或窗口失去焦点会取消正在进行的拖动并恢复原选区。`Restore full image / 恢复整图` 清除裁剪与比例锁定，保留翻转方向。点击 `Confirm edits / 确认编辑` 返回导入预览，最后点击导入或应用才提交；编辑页取消仅丢弃本次裁剪和翻转并返回原预览，关闭或取消整个导入窗口不提交编辑。

拖动裁剪框时，图片与裁剪辅助线一起刷新，图片位置与缩放保持稳定；精确数值与选区尺寸仍实时更新。

裁剪保存在素材设置中，原文件与工程托管副本保持完整，可重新编辑并恢复整图。解码分辨率和放置宽高按选区计算；编辑已有素材会更新所有引用，按裁剪宽高变化比例调整对象尺寸，保留中心位置。批量导入将同一相对区域应用于所有图片，裁剪窗口会提示。现有对象的擦除范围继续相对于裁剪后的图片，裁剪不是逐对象的擦除操作。

| 设置 | 说明 |
| --- | --- |
| `Image display / 显示方式` | 自动识别逐张建议显示方式；可手动覆盖为平滑（照片和插画）或像素风（最近邻采样，保留硬像素边缘）。 |
| `Maximum resolution / 最大分辨率` | 解码最长边上限，默认 `8192`；只缩小、不放大，保留源文件，不改变已有对象大小。 |
| `Keep transparent background / 保留透明背景` | 默认保留透明通道；取消后丢弃透明度，不等于铺白底。 |
| `Placement scale / 放置比例` | `100%` 保持当前导入比例；修改后按比例调整所有使用此素材的对象，保留各自的自定义大小。比例映射到旧整数参数，以换算后的宽高为准。 |

底部显示解码尺寸与基础放置宽高。折叠不会修改参数。未显示的旧二次幂、三线性、压缩、读写、Mipmaps、精确锚点数据继续保留；旧二次幂设置会提示。源图仍受每边 `8192` 像素等读取预算限制。

导入后的行为：

- 图片字节会被复制进工程的 `.Vault/Images` 托管目录，源文件之后移动或删除都不影响工程渲染与打开；`SourcePath` 只作为诊断信息和 `重新定位...` 的目标保留。相同内容的图片共用同一份解码结果。
- 放置结果是**一个整体位图对象**：可整体选择、移动、缩放、旋转，可复制粘贴并随工程保存；不支持 `Break Apart`，编辑器会明确拒绝而不是静默丢弃。
- 点击图片后，右侧监视器显示该图片的来源、像素尺寸与导入设置，并可打开导入设置对话框；不同图片素材分别保存设置，同一素材的多个放置对象共用其导入设置。
- 图片在 `Free Transform` 中的任意变形缩放遵循操作偏好「使用 Shift 启用等比缩放」：默认开启，此时默认自由缩放、按住 `Shift` 等比缩放；关闭后默认等比、按住 `Shift` 自由缩放，与矢量对象、文本和导入 SVG 一致。复制粘贴保留当前尺寸和擦除结果，排列命令与图层顺序同样作用于图片。
- 选择工具直接拖动未扭曲图片的四角手柄时始终保持原始宽高比，并固定相对角位置；这条规则独立于自由变换工具的 `Shift` 操作偏好。扭曲后的图片继续使用实际边界和 `Free Transform` 流程。
- 橡皮擦启用擦除填色时，可擦除图片的局部区域。擦除位置变透明，保留区域维持原图颜色；每个放置对象独立保存擦除范围，不修改原始图片或同一素材的其它放置对象，支持撤销和工程保存。
- 位图属于**整体对象**，没有可编辑的顶点几何：未扭曲时，选择工具显示整体包围框和四角缩放手柄；扭曲后按实际边界显示选择轮廓，缩放和旋转使用 `Free Transform`。选择工具不显示填色边界的锚点和贝塞尔控制柄。使用 `Distort` 可拖动图片的扭曲包络及贝塞尔控制柄，预览实时显示，松手提交；支持撤销和工程保存。扭曲只影响该放置对象，保留图片透明度，不修改共享素材或其它放置对象。自由变换仍提供整体移动、缩放和旋转，不提供倾斜手柄。导入的 SVG、文本与未拆散的混色笔画同样按整体对象处理。
- 位图以小于源分辨率绘制时（默认 `100` PPI 导入在 100% 缩放下约为源分辨率的四分之一），会改用按高质量滤波预先生成的缩小副本采样，而不是直接用全分辨率源做双线性缩小，从而消除旋转后的边缘锯齿。这实现了导入设置中 `Generate mipmaps` 所承诺的缩小行为；`Point`（点采样）过滤模式保持最近邻，不走这条路径。
- 右键素材可打开 `Import Settings...` 调整显示方式、解码分辨率、透明背景和放置比例；只有比例改变时才更新已有对象尺寸。
- 原始源文件移动或改名不影响工程托管的图片；只有托管副本不可用时素材行才标记缺失。右键选择 `替换源文件...` 可指定新的有效图片，素材 ID、放置变换和动画保持不变。
- 删除图片素材前会显示使用范围，可替换引用或明确删除所有使用位置，作为一次撤销事务。

### 导出单个元件为 SVG

在 Vault 元件上右键选择 `Export SVG...`，或在舞台右键的 `Symbol / 元件` 组中选择 `Export Symbol as SVG...`，可把当前元件导出为独立的 `.svg` 文件。舞台右键导出的是当前元件。

- 导出范围只包含当前帧实际可见的内容：隐藏图层、父级文件夹被隐藏的图层、文件夹图层和遮罩图层本身都不会写入；被遮罩的绘制图层会生成对应的 SVG `mask` 后裁切输出。
- SVG 画布按实际导出的图元范围收紧，计入描边、旋转和遮罩覆盖范围；显式像素尺寸按 `25 vu = 1 px` 换算。重新导入时不会再将整个工作区的透明空白算进对象边界。旧版已导出的 SVG 文件需从原元件重新导出；重启软件不会改写旧文件。
- 图层顺序按舞台绘制顺序写入：更高索引的图层在下方，因此先出现在文档中，索引 `0` 的图层最后绘制并位于最上方；同一图层内按对象叠放顺序排列。
- 图层混合模式会映射为等效的 CSS `mix-blend-mode`；没有标准等价模式的混合模式退回普通绘制。
- 导出的是普通 SVG，不包含 `.Vault` 内部的可逆场景元数据，因此不能替代工程存储，也不能再次导入还原为可编辑元件。需要可再次导入的元件文件请使用下一节的 `.V2DSymbol`。

### 导出与导入元件文件（.V2DSymbol）

在 Vault 元件上右键选择 `Export Symbol File...`，或在舞台右键的 `Symbol / 元件` 组中选择 `Export Symbol File...`，可把元件导出为单个 `.V2DSymbol` 文件；舞台右键导出的是当前元件。通过同一组的 `Import Symbol File...` 可把该文件导入当前工程。

也可以从资源管理器将一个或多个 `.V2DSymbol` 文件拖入素材库的资产列表区域，直接导入当前工程；扩展名不区分大小写，请勿混入其它类型的文件或文件夹。多文件按顺序独立导入，失败文件会显示提示，不影响其它文件继续导入；最后成功导入的根元件会被选中。导入仍按包内记录恢复目录和标签，不会根据鼠标落点改变元件目录。图片与 `.svg` 文件也可用同样的方式拖入，会分别按图片素材和共享 SVG 素材导入；但同一次拖入不能混合不同种类。

`.V2DSymbol` 是自包含的可逆元件包：文件使用 Brotli 无损压缩 UTF-8 JSON，几何、图层、Cel 曝光、关键帧和实例状态均无损保存，导入后可继续作为可编辑元件使用。

- 导出会写入完整的依赖闭包：除所选元件外，它嵌套引用的所有元件及其时间轴都会一并打包，因此导入方无需预先拥有这些元件。导出时仍显示实例但引用的元件已不存在时，该悬空引用会被跳过。
- 元件的标签会随文件保存：标签名称与颜色都会写入；导入时按名称合并，已存在的同名标签保留工程中的原 ID 与颜色，只有新的标签才使用文件中的颜色，不会覆盖工程已有标签。
- 元件在库中的路径会随文件保存：导入时按路径重建目录链，复用到已存在同路径的目录，因此重复导入不会不断产生同名目录。
- 导入的元件一律分配新的 ID，不与现有元件或实例发生绑定；同一文件重复导入会得到相互独立的副本。重名元件会自动追加序号区分。
- 导入前会完整校验并预先解析包内容：不支持的版本、重复或悬空的元件引用、递归嵌套、非法目录路径、以及越界的图层/时间轴元数据都会被拒绝，此时工程不会被修改。
- 若在写入元件过程中失败，本次导入已创建的元件会被移除，不会留下部分导入的元件；目录与标签为新增型元数据，可能保留但不会错误关联到其它元件。

## 12. 保存、打开与工程结构

主菜单和右键菜单采用统一的圆角外框、柔和悬停底色和淡色快捷键提示。参数下拉列表采用相同配色的圆角选中底色，长列表最多展开八行，其余选项可滚动选择。主菜单采用两级分组：`Project Files / 工程文件` 包含新建、打开、保存、另存为与重命名；`Import / 导入` 包含 Flash 工程、视觉转换桥、SVG 与图片入口。设置、窗口与更新公告保留在一级菜单。子菜单继续显示当前方案的快捷键。菜单文字和快捷键按实际行高垂直居中，与悬停底色及箭头保持同一中心线。

参数下拉列表使用居中的内缩选中底色；焦点提示遵循系统键盘提示状态，使用短下划线，不在鼠标悬浮的选项外叠加矩形边框。

Parameter dropdowns use centered inset selection fills. Focus cues follow the system keyboard-cue state and use a short underline instead of an extra rectangular outline around hovered options.

| 菜单项 | 快捷键 / 说明 |
| --- | --- |
| `New Project` | `Ctrl+N` |
| `Open Project...` | `Ctrl+O` |
| `Import Flash Project...` | 单向导入 Flash / Animate `.fla`、未压缩 `.xfl` 或视觉桥的 `manifest.json`，生成新的 V2D 工程 |
| `Create Flash Visual Bridge...` | 生成 Adobe Animate 逐帧 SVG 转换脚本，用于直接导入不能表示的画面内容 |
| `Import SVG...` | 仅在可绘制上下文（Basic Drawing，或 Scene Mask 编辑面）中可用 |
| `Import Image...` | 同上；可多选导入图片素材库，通过 Vault 使用或拖到舞台后作为整体位图对象放置 |
| `Save Project` | `Ctrl+S` |
| `Save Project As...` | `Ctrl+Shift+S` |
| `Settings...` | 语言、主题、操作偏好、快捷键与 Codex / MCP 接口，见[设置与快捷键](#13-设置与快捷键) |
| `Release Notes...` | 查看当前版本的更新说明 |

### Flash / Animate → V2D 单向转换

选择 `Import Flash Project... / 导入 Flash 工程...`，先选择一个空目录，软件会自动创建并保存普通 `.v2dProject` 工程，保存成功后直接打开转换结果。转换使用既有场景、元件、实例、图层、时间轴和 SVG 对象，不改变 V2D 的工程格式。导入不会改写原始 FLA，也不会自动导出回 FLA；解析或保存失败时不会替换当前工程。


导入或转换桥失败时，错误窗口上方显示可选中的失败原因，下方显示可滚动的诊断报告。可按 Ctrl+A / Ctrl+C 或右键全选、复制，也可点击“复制报告 / Copy report”复制完整内容；复制不会关闭窗口。报告包含失败阶段、源文件、目标目录（已选择时）、程序版本、程序集路径、构建标识、日志路径（可用时）、完整异常链及已捕获的 stack trace。后台解析和保存异常保留原始堆栈；没有捕获到堆栈时明确说明，不生成虚假调用位置。定位问题时可提供完整报告，同时确认报告中的程序路径和构建标识对应正在使用的版本。

部分 Animate FLA 的 ZIP 目录长度包含未列入目录的固定 `mimetype` 条目记录。导入器仅在目录、本地条目和该固定条目结构完全匹配时，通过只读流修正长度字段；不改写源文件，也不尝试通用修复损坏档案。静态延续曝光只保存必要实例状态，减少长时间轴的重复数据。

密集形状按流式边数据解析，完整保留线段与二次曲线。生成内容超过单个 SVG 对象的 8 MiB 预算时，仅在独立填色/描边路径之间分块，沿现有嵌套元件保存并保持覆盖顺序；同一填色的孔洞与闭合轮廓不拆散。单条路径或总形状预算仍超限时停止转换，不简化或跳过几何。

| 来源 | 转换路径与实际边界 |
| --- | --- |
| Flash CS5、CS5.5、CS6、Flash Professional CC、Animate 的 ZIP/XFL 型 `.fla` | 直接读取容器，无需安装 Adobe。也可选择未压缩工程的 `.xfl` 或 `DOMDocument.xml`。支持范围取决于实际内容，而不是文件版本号。 |
| Flash 8、CS3、CS4 的二进制 `.fla` | 自动识别后选择空目录，生成源副本、`convert.jsfl` 与状态文件。在已安装的 Flash CS5+ / Animate 中通过 `Commands > Run Command` 运行脚本，成功后回到 V2D 导入 `converted.fla`。本软件不直接解码旧二进制格式，也不自动启动或安装 Adobe。 |
| 直接映射不能表达的视觉内容 | 选择 `Create Flash Visual Bridge...`，在空目录生成脚本，在 Animate 2020+ 中运行，再通过 `Import Flash Project...` 选择生成的 `manifest.json`。视觉桥按场景顺序将逐帧 SVG 合成一条动画，原始库结构、可编辑补间、声音和脚本不保留。 |

直接导入保留多场景、库元件和嵌套引用、普通图层及文件夹、显示/锁定状态、图层颜色/轮廓显示、填色/实线描边、二维仿射变换、空白帧与曝光时长。每个源库元件对应一个可复用的 V2D 元件，同一元件的多个实例共享定义；库目录映射到 Vault 目录，也保留空目录。目录名遵循现有 V2D 命名规则；首尾空白被规范化时会在报告中说明，源元件引用仍按原始完整路径区分。图形元件的首帧和循环通过实例状态重采样，普通曝光保留原始关键帧与延续帧；支持的经典实例补间会烘焙为逐帧关键帧，并在转换报告中说明。支持作用于全部属性的 CustomEase 分段三次 Bézier 曲线，以及 cubicIn、cubicOut、cubicInOut；实际采样帧存在歧义或超调时仍拒绝。末尾关键帧上的 motion 标记按普通曝光处理；支持范围内的运动引导路径会烘焙为普通逐帧关键帧，不增加 V2D 引导层功能；自定义旋转仍不支持。导入成功后，可精确表示的矢量叶节点自动拆离为 V2D 原生路径、贝塞尔控制点和原生描边。渐变、无法精确拆离的轮廓/描边及超过拆离点预算的形状保留完整 SVG 矢量，并在报告中说明，仍可移动、变换和随工程保存；逐节点编辑需要手动 Break Apart，其转换限制仍适用。导入不会因此静默栅格化、简化或跳过部件。

Flash guide 层的参考图不参与导出，会从 V2D 图层中省略，并报告 guide-layer-omitted。绑定该层的经典实例补间可沿单条开放、无分叉的直线／二次 Bézier 描边路径烘焙，按弧长和已有缓动计算每帧位置，支持反向路径、形状／组的二维变换和元件 transformationPoint；Orient to Path 会生成每帧朝向。结果是可编辑、可保存重开的普通关键帧，报告 guide-motion-baked。路径在整个补间区间必须保持静态，实例绑定点须与路径两端在 0.25 舞台像素内匹配；数值弧长采样容差为 0.01 舞台像素。闭合、分叉、分离的多路径、路径中段绑定、变化的变换中心或与端点旋转不一致的朝向暂不支持，直接导入仍会指出位置并停止；可使用 Adobe 视觉桥获得逐帧导出。尚未完成 Adobe 画面逐帧对比，导入通过不代表视觉完全一致。

主场景的普通、未锁定顶层图层直接映射到 V2D 场景图层，实例保留二维位置、变换、外观、播放状态与原来的元件引用，按源图层和叠放顺序还原画面。V2D 场景图层没有文件夹/锁定字段，因此源顶层文件夹或锁定图层映射为场景中的分组元件：双击进入该元件，可编辑其原始子图层、锁定状态和各自时间轴；场景时间轴显示该组的合成曝光。组的顶层显示开关由场景图层控制；文件夹的轮廓开关留在组内，以保留子图层各自的轮廓颜色，其他顶层图层的轮廓开关由场景控制。导入报告会说明这些映射，不额外保留一份脱离场景的完整时间轴副本。库元件内部仍保留原有的完整图层结构。对支持的内容，画面位置和动画从源舞台坐标还原；这不表示不支持的效果也已还原。

位图实例 `DOMBitmapInstance` 会映射为可复用的图片元件，像素资源保存到工程的 `.Vault/Images`，保留图层、曝光、位置及可表示的缩放/旋转。支持内嵌 JPEG，以及已验证的 `03 05` 分块 zlib 无损位图。不透明无损数据解码为 PNG；带透明度的数据要求工程内有与内嵌数据逐像素匹配的原始 PNG，以避免 Flash 预乘量化造成颜色或 Alpha 歧义。未知编码、调色板变体、缺少原 PNG 的有歧义透明数据会停止转换。成功保存后重载托管资源，后续保存不依赖临时图片。

直接映射元件实例的 normal、layer、multiply、screen、lighten、darken、difference、add、subtract、invert、overlay、hardlight；其中 add 对应 Linear Dodge，layer 对应 Isolated Group。alpha、erase、shader 和未确认的数字模式仍严格拒绝。导入在生成几何和解码位图前预检效果，报错显示元件、图层、帧及具体模式/滤镜名称。支持按源顺序保留最多 32 项 BlurFilter、GlowFilter 和 DropShadowFilter；不同模糊参数不再合并，Quality 各自保留 1–15，保留 Inner、Knockout 和 Hide Object。导入的效果链随保存、重启和复制保留，由 CPU/GPU 执行；现有滤镜面板尚不能逐项编辑导入的附加链。支持滤镜之前的 brightness、tint、RGB multiplier/offset 和 AlphaMultiplier，非零 alphaOffset 仍拒绝。经典补间可逐帧烘焙兼容的模糊宽度和颜色变换；离散 Quality 在区间内保持起点值，到端点使用终点值，转换报告会说明。BevelFilter、GradientGlowFilter、AdjustColorFilter 等尚未映射的 Flash 效果仍停止直接导入。元件内容保持透明，Flash 舞台背景颜色尚未映射为 V2D 原生场景背景；滤镜及补间尚未通过 Adobe 逐像素画面对比，不能从导入成功推断整份 FLA 外观完全一致。混合语义参考 [Adobe BlendMode](https://help.adobe.com/en_US/FlashPlatform/reference/actionscript/3/flash/display/BlendMode.html)，滤镜参数参考 [Adobe BlurFilter](https://help.adobe.com/en_US/FlashPlatform/reference/actionscript/3/flash/filters/BlurFilter.html)。

单样式 DOMStaticText 映射为可编辑 V2D 文本，保留字符、请求的字体、字号、颜色、二维变换和左/中/右对齐。字体缺失时报告 text-font-missing，并使用 V2D 现有替代字体显示；原字体名称仍随工程保存。Flash 字形度量、行距和换行排版未做视觉匹配，报告 text-layout-mapped 会提示核对。多样式文本、动态/输入文本仍不支持。复杂形状补间、尚未支持的运动路径/其他高级缓动、遮罩/骨骼、按钮、视频、未映射的滤镜及三维/相机等内容不能保证直接映射；不支持的内容指明位置并拒绝直接导入。位图的倾斜、镜像或嵌套后产生的非正交变换仍受现有位图组合能力限制。声音、脚本和帧标签不会转移，转换前显示报告；脚本不会执行。独立 Movie Clip 播放仅从放置帧采样，不能复现脚本控制的交互。视觉桥也不能承诺 Adobe SVG 导出未支持的效果或交互无损，请核对输出画面。源文件属于旧版本时，Adobe 自身还可能转换已弃用的功能。

直接导入的纯色透明度分别写入 SVG 填色与描边属性，并在可精确拆离时转换为可编辑的原生填色和描边，避免把透明纯色误当作渐变进行外观轮廓化。斜向方形端帽计入源图边界，防止端帽角部被裁切。不能精确拆离的形状以完整 SVG 矢量叶节点保留，并在报告中标明。

视觉桥要求 Adobe 的 SVG 导出设置使用完整舞台范围、将文本转为轮廓、将位图嵌入；仅接纳有尺寸和解码预算校验的 PNG/JPEG 内嵌图片。外部资源、脚本或不兼容的 SVG 会报错，不能靠拉伸裁剪框掩盖尺寸差异。失败后查看桥目录的状态文件和 Adobe Output 面板；重新生成桥时使用新的空目录。

转换先读取、校验并构建独立项目，再处理当前工程的未保存提示。取消或解析失败不会替换当前工程；解析窗口的取消会在当前解析结束后丢弃结果。源中的危险路径、重复条目、XML DTD、过大内容、悬空引用和递归嵌套会被拒绝。直接导入上限包括 512 MiB 容器及展开内容、单 XML 128 MiB、累计 XML 384 MiB、10,000 帧、16,384 个库元件；高复杂度文件还受节点、图元、SVG 和帧状态预算限制。

这些路径覆盖 Flash 8 到 Animate 的格式代际；它们不等于已经用每一版本的真实 FLA 验证，也不等于所有 Flash 功能都能无损转移。[Adobe 的格式及旧功能转换说明](https://helpx.adobe.com/animate/desktop/multimedia-and-video/opening-cs6-files-cc.html)、[SVG 导出 API](https://github.com/AdobeDocs/developers-animatesdk-docs/blob/master/Document_object/docu6074.md)。

首次保存默认生成单个 `.v2d` 工程文件（类似 FLA 的单文件容器），复制、分享或移动工程时只需要处理这一个文件，素材不会在文件夹里散开。`.v2d` 内部打包了与传统文件夹工程完全相同的结构：

```text
Project Name.v2d          ← 一个文件就是完整工程（ZIP 容器）
  └─ Project.v2dProject
     .Vault/
       Images/
     .TimeLine/
```

需要直接查看或处理内部文件时，可以使用菜单中的“另存为开发模式”，一步保存为传统文件夹工程（`.v2dProject`，与旧版行为一致）；也可以在“工程另存为”的格式下拉中选择传统文件夹工程（`.v2dProject`）：

```text
Project Folder/
  Project Name.v2dProject
  .Vault/
    Images/
  .TimeLine/
```

机制：

- `.v2d` 是单文件容器，打开时自动解包到系统 LocalAppData 下的托管工作区编辑，保存时原子写回容器；旧版文件夹工程仍可正常打开，也可以另存为 `.v2d`，两种格式互相转换不丢失数据。
- `.v2dProject` 保存工程索引、播放设置和校验信息。
- 元件吸附点作为元件级静态数据保存在 `.v2dProject`，不写入图层或时间轴文件。
- `.Vault` 保存可逆 SVG 绘制资产：普通 SVG 预览保持可用，内部可编辑场景数据采用 Brotli 无损压缩，不降低几何精度或画质。
- `.TimeLine` 使用 `.json.br` 文件无损压缩保存图层、关键帧、Cel、场景和实例状态，打开时自动解压。
- 新保存的工程采用格式版本 7，保留共享 SVG 素材引用及图片/SVG 的原始更新来源。版本 1–6 的工程仍可打开；旧版未关联库素材的 SVG 保持原有独立内容，不根据名称或外观猜测关联。保存后的版本 7 工程需要新版编辑器打开。
- SVG 素材字节保存在 `.Vault/ExternalSvg/<素材ID>.svg`，由 SHA-256 校验；放置对象保留稳定素材 ID 及自包含的 SVG 内容。原始路径只用于主动更新，工程打开不依赖该文件。
- 图片素材把导入的字节复制到 `.Vault/Images`，并按内容的 SHA-256 校验托管副本；源文件丢失不会阻止打开工程。
- 保存时先写入临时内容并完整校验，再替换原工程；中断后会通过事务日志恢复。
- 打开工程会先校验路径和存储文件的 SHA-256，再按受限大小解压并校验 ID、引用和时间轴，成功后才替换当前工程；损坏、截断或解压后超出限制的数据会被拒绝。`.v2d` 容器额外拒绝路径穿越、重解析点和任何不在工程结构内的条目。
- 首次启动会在当前用户注册表（HKCU，无需管理员权限）注册 `.v2d` 扩展名、工程图标与双击打开；之后在资源管理器双击 `.v2d` 即可启动软件并打开工程，软件已运行时会在原窗口中打开。

操作提示：

- 不要手工移动、改名或单独替换 `.Vault`、`.TimeLine` 中的文件。
- 不要把两个工程保存到同一个受管理目录；软件会拒绝覆盖不属于当前工程的数据。
- 新建、打开或关闭前出现保存提示时，`Cancel` 会保留当前编辑状态。

## 13. 设置与快捷键

`Settings → General` 中可调整语言、Dark/White/Charcoal 主题、主题色和高亮色，并通过“更多设置”中的 **Operation preferences... / 操作偏好...** 与 **Shortcut profiles... / 快捷键方案...** 按钮打开各自的独立面板。子面板点击 `OK` 后返回设置窗口，点击 `Cancel` 或关闭子面板则放弃该次子面板修改；再次打开会保留已确认的调整。语言、快捷键、操作偏好与 Codex / MCP 配置在设置窗口点击 `Save` 后应用；`Cancel` 放弃这些未保存调整。Dark/White/Charcoal 主题、主题色和高亮色独立实时生效并自动保存，关闭或取消设置窗口也会保留。`Codex / MCP` 仍在设置窗口中切换查看，切换到该页时窗口会展开以容纳连接配置。主题色、亮度和饱和度改变后，文字、边框、禁用态、焦点态以及颜色编辑器中的控件会按实际背景重新校正对比度；HSV/RGB/Lab 色域、渐变和工作区色样本不会被改色逻辑覆盖。

新增的 **Charcoal / 炭灰** 主题采用中性炭灰面板、深灰输入框、低对比边框和青色高亮，接近设计稿配色；原有深色和白色主题保留。可继续调整主题色与高亮色，切换主题不会改动画布背景或作品颜色。

着色面板默认展开，以大色域和常用操作为主：默认 HSV，使用横向色相条；在「颜色通道」中切换 RGB 或 Lab 时保留原有竖向主分量与色域含义。HEX 行整合当前色样、完整编码和复制按钮；点击色样打开已有色板。不透明度独立显示为 0–100%（一位小数），内部仍保留 0–255 Alpha。颜色通道默认折叠，展开后可切换 RGB／HSV／HSL／Lab 并以三列数值精调。最近使用色直接显示在面板中，没有历史时不占位；选取历史颜色保留当前透明度。和谐色仍可独立展开，两个折叠区均保留状态，不重置颜色或渐变色标。HEX 支持 #RRGGBB 与 #RRGGBBAA，完全不透明时简写为 6 位；输入 6 位仍表示不透明。连续调色及撤销边界沿用原实现。

The shading panel opens with a large HSV field and a horizontal hue strip. RGB and Lab retain their original fields and vertical primary-component strip when selected under Color channels. The HEX row combines the current swatch, complete code and Copy; clicking the swatch opens the existing palette. Opacity is displayed as 0–100% with one decimal place while internal Alpha remains 0–255. Expand Color channels for RGB/HSV/HSL/Lab model selection and three-column numeric editing. Recent colors appear inline only when history exists, and selecting one retains the current alpha. Harmony remains independently collapsible, and both disclosure states survive reopening the editor without resetting colors or gradient stops. HEX accepts #RRGGBB and #RRGGBBAA; opaque colors use the shorter display and six-digit input still means opaque. Existing continuous-edit and undo boundaries are retained.

颜色面板使用原生 C# / WinForms 控件：填色显示实色色样，纯色描边显示空心色样；材质类型和颜色模型通过选中底色与下划线区分。横向色相和透明度滑条采用圆形手柄，HEX 标签位于输入框内部，右侧图标可复制当前编码。通道标签位置不变，数值框和和谐色下拉框继续遵循原有尺寸及键盘操作约定。

The color panel uses native C# / WinForms controls: fill uses a solid swatch and solid-color stroke uses an outlined swatch; selected material and color-model buttons use a tinted surface and underline. Horizontal hue and opacity sliders have circular handles. The HEX label sits inside the field, with a copy icon beside it. Channel labels keep their positions, and numeric fields and the harmony selector retain their established sizing and keyboard behavior.

The **Charcoal** theme adds neutral charcoal panels, darker fields, subtle borders and cyan accents. Dark and White remain available. Existing theme and accent adjustments still apply; switching themes does not alter scene backgrounds or artwork colors.

在 `Settings → General` 勾选 **Simple selection highlight / 简洁选中高亮**，再点击 `Save`，可将线条、填色和元件选区切换为静态细轮廓，去掉多层光晕与呼吸动画，同时保留控制点、变换手柄及线条/填色的颜色区分。默认关闭以保留原有样式；取消勾选并保存即可恢复。偏好自动持久化，不改变工程内容。简洁模式的二次贝塞尔线条使用金黄色高亮，三次贝塞尔线条使用橙色高亮，主选与多选均保留区分；旧版光晕样式不变。

### 个性化

滑动条会根据当前主题显示分层轨道、强调色进度和圆形滑块；暗黑模式下保持深色轨道与清晰刻度，悬停、拖动和禁用状态会调整对比度。

绘制数量、原子数量、FPS 和缩放信息统一在底部状态栏显示，重复统计已移除，辅助文字采用柔和灰色。

时间轴工具栏和页签栏下沿使用柔和分隔线；按钮、色块、输入框、场景列表和菜单外框统一使用分隔线风格的柔和描边，文本输入框使用单层柔和边框与居中的文字留白，并跟随直角／微圆角设置；和谐色选择框使用紧凑宽度，容器高度跟随控件，完整显示底边；着色面板、颜色对话框及工作区颜色浮层的 HEX 输入使用独立主题输入组件，按字体和显示缩放预留完整高度，保留编辑、粘贴及提交行为，选中状态通过底色显示。菜单外框和下拉选项的选中底色跟随个性化中的直角／微圆角设置；选中项、文字和边框按当前主题保持清晰对比。工作区页签的未选中底色也与背景轻微区分，便于识别可切换的按钮。

在 `Settings → General → Personalization / 个性化` 中选择 **Square corners / 直角** 或 **Subtle rounded corners / 微圆角**。微圆角用于按钮、按钮式页签、数值输入外框和菜单高亮等独立交互表面，约 4 个逻辑像素并随 DPI 缩放，使用抗锯齿轮廓与连续边框。工作台板块、停靠容器、分隔线、原生文本/下拉输入和舞台内容区保持矩形，避免板块接缝出现缺口；网格、时间轴格子等内容绘制保持原样。主窗口与现代对话框请求 Windows 系统微圆角，实际外轮廓取决于系统支持和窗口状态。选择立即预览，点击 `Save` 保存并在下次启动恢复；`Cancel` 或关闭设置窗口恢复原来的角样式。默认直角；切换不裁剪控件区域，不改变控件尺寸、停靠布局、命中范围、抽屉动画裁剪或工程内容，高对比度模式保留系统可读性。

### 操作偏好

**工具栏位置 / Toolbar position**：在操作偏好中选择 **左侧 / Left**（默认）或 **顶部 / Top**，点击 `OK` 返回设置，再点击 `Save` 即时应用并保存，下次启动仍保留。左侧沿用紧凑纵向工具栏；顶部仅使用元件页签右侧、吸附设置左侧的原有顶部空白，显示适度大小的图标及下方名称，不新增功能区高度、不压缩画布。工具按钮随可用宽度缩排，空间仍不足时可横向滚动；极窄窗口回退到原有视图操作行中的紧凑图标布局。选择、形状、线条、笔刷、填色等折叠工具组向下展开为带图标与名称的网格，点击工具后收起。取消子面板或设置窗口不改变已保存的位置。

顶部工具栏不绘制容器外框，下方名称保持 8pt 字体；图标在原有图标槽内缩小显示，文字位置、按钮点击区域及整体布局保持不变。选中状态及键盘焦点提示保留，左侧工具栏、右侧分类栏和展开网格的图标不受影响。从顶部切回左侧时，工具栏立即显示在画布上方，无需重启。

In **Settings → General → Operation preferences**, choose **Toolbar position → Left** (default) or **Top**, confirm with `OK`, then `Save`. Top uses only the existing header space between the symbol tabs and snapping controls, with smaller icons centered in their existing slots and unchanged 8pt labels underneath, without a container border; selection and keyboard focus cues remain visible. It adds no header height and takes no extra canvas space. Buttons adapt to the available width, with horizontal scrolling when needed and compact icons in the existing view-action row for very narrow windows. Grouped tools open below as a labeled grid and close after selection. Switching from Top back to Left immediately restores the toolbar above the canvas without restarting. The position persists across restarts; canceling either dialog leaves the saved position unchanged.

点击 `Settings → General → Operation preferences... / 操作偏好...` 打开独立面板，调整与具体工具操作相关的开关。点击 `OK` 返回设置，再点击 `Save` 后写入 `settings.json`，重新打开软件仍然生效；取消子面板或设置窗口不会改变已保存值。

- **Use Shift for proportional scaling / 使用 Shift 启用等比缩放**：默认**开启**。此偏好决定 `Shift` 临时切换缩放模式的方向。

| 状态 | 不按 Shift | 按住 Shift |
| --- | --- | --- |
| 开启（默认） | 自由缩放 | 等比缩放 |
| 关闭 | 等比缩放 | 自由缩放 |

等比缩放保留缩放开始时的宽高比例。拖动中按下或松开 `Shift` 可临时切换模式，松开后恢复偏好指定的默认模式；角手柄和边手柄均适用。

该偏好统一作用于场景实例、绘制对象（含图片、文本与导入 SVG）和运动轨多帧缩放三种任意变形路径。它属于普通设置，可通过 MCP 的 `settings_get` / `settings_update` 读写；通过 `settings_update` 修改它不会授予或改变 `Codex / MCP` 页面的连接权限。

### Codex / MCP 接口

在 `Settings → Codex / MCP` 中启用 `Enable MCP integration / 启用 MCP 接口`，点击 `Save` 后启动本地服务。默认关闭接口；`Allow editor changes / 允许修改编辑器` 默认关闭，启用后客户端才可切换工具、修改设置和操作工程。取消设置窗口不会改变连接配置。

服务地址默认为 `http://127.0.0.1:43521/mcp/`。可以调整本地端口，并设置可选的 Bearer 访问令牌。连接只接受本机原生客户端，拒绝带浏览器 Origin 的请求。令牌不通过工具结果、连接配置文本或日志输出。重新打开此页面可查看实际服务状态；若启动失败，页面显示监听错误，详细信息写入软件日志。

保持编辑器运行，在 Codex 中添加 **Streamable HTTP** MCP 服务器，或将下列配置加入 Codex 的 `config.toml`：

```toml
[mcp_servers.vector2d]
url = "http://127.0.0.1:43521/mcp/"
# 在软件中设置令牌时，取消下一行注释，并在 Codex 进程环境中定义该变量：
# bearer_token_env_var = "VECTOR2D_MCP_TOKEN"
```

连接配置遵循 [Codex 官方 MCP 文档](https://developers.openai.com/codex/mcp)。更改端口或令牌后同步更新客户端配置。编辑器关闭时服务停止；编辑器重建或进程重启时连接短暂中断，窗口就绪后重新监听。服务不负责启动编辑器。

接口支持 JSON-RPC 2.0 和 MCP Streamable HTTP 的 JSON 响应模式，提供 `initialize`、`ping`、`tools/list`、`tools/call`、`resources/list`、`resources/read`。通知返回 HTTP 202；不提供 SSE 推送、资源订阅或任意脚本执行。工具参数、类型和范围均可通过 `tools/list` 发现。

| 工具 | 作用 |
| --- | --- |
| `editor_get_state` | 读取工作区、工具、帧、播放、选择、忙碌状态和修改权限；`render` 字段提供最近一帧耗时、Direct2D 命令/Present、缓存维护和 GPU 状态 |
| `project_get_info` | 读取工程 ID、保存路径、未保存标记、场景和元件 |
| `tools_get` | 获取绘图工具名称、当前上下文的可用性以及绘图设置 |
| `tool_set_active` | 用 `tool` 参数切换到可用的绘图工具 |
| `timeline_set_frame` | 用 `frame` 参数定位帧；索引从 0 开始，必须在当前播放范围内 |
| `timeline_set_playback` | 用 `playing` 布尔参数开始或停止播放 |
| `editor_undo` | 使用现有撤销历史撤销当前上下文的最近编辑 |
| `settings_get` | 读取软件设置与可修改字段的 Schema；不返回令牌 |
| `settings_update` | 部分更新并持久化语言、主题、主题/高亮 HSB、时间轴格尺寸、自动关键帧和工作区背景色 |
| `project_open` | 从绝对 `path` 打开工程，支持 `.v2d` 单文件容器与 `.v2dProject` 文件夹清单 |
| `project_save` | 保存到当前路径；首次保存需提供绝对 `path`，扩展名 `.v2d` 保存为单文件容器，`.v2dProject` 保存为文件夹工程 |
| `project_new` | 创建空工程 |
| `animation_import_bundle` | 从绝对 JSON 路径 `path` 导入 SVG 帧、嵌套元件和实例动画，建立新工程 |

图层、元件与场景与动画工具：

| 工具 | 作用 |
| --- | --- |
| `workspace_set_active` | 切换 `BasicDrawing`（基础绘制）或 `SceneEditor`（场景与动画）工作区 |
| `layers_get` | 列出当前编辑上下文的图层及稳定 ID、顺序、显隐和锁定状态 |
| `layer_create` / `layer_update` | 创建普通图层，或更新名称、显隐；绘制图层还支持锁定和不透明度 |
| `layer_set_active` / `layer_reorder` / `layer_remove` | 用 `layerId` 选择、排序或删除图层；排序参数 `index` 从 0 开始 |
| `symbols_get` | 列出元件及稳定 ID |
| `symbol_create` / `symbol_duplicate` | 创建空元件或复制已有元件 |
| `symbol_rename` / `symbol_remove` / `symbol_set_active` | 用 `symbolId` 重命名、删除或选择当前元件 |
| `scene_create` / `scene_rename` / `scene_remove` / `scene_set_active` | 创建、重命名、删除或选择场景；已有场景用 `sceneId` 定位 |
| `scene_instances_get` | 列出当前场景中的元件实例 |
| `scene_instance_create` / `scene_instance_remove` | 将 `symbolId` 对应的元件放入场景图层，或用 `instanceId` 删除实例 |

灯光和 3D 工具：

| 工具 | 作用 |
| --- | --- |
| `scene_set_dimension` | 设置当前场景为 `TwoD` 或 `ThreeD` |
| `scene_lights_get` | 列出当前场景的灯光与参数 |
| `scene_light_create` | 添加 `Directional`、`Point`、`Area` 或 `Ambient` 灯光 |
| `scene_light_update` / `scene_light_remove` | 用 `lightId` 修改或删除灯光 |
| `scene_instance_set_transform` | 用 `instanceId` 更新实例的 XYZ 位置、XYZ 旋转、XY 缩放及 Z 厚度 |
| `scene_camera_get` / `scene_camera_update` | 读取或修改工程中的场景相机 |
| `scene_view_get` / `scene_view_update` | 读取或调整编辑器的参考视角 |

先通过 `project_get_info`、`layers_get`、`symbols_get` 等读取 ID，再调用写入工具。名称用于显示，ID 用于定位；重命名后继续使用同一 ID。图层操作作用于当前工作区的编辑上下文，场景操作应先切换到场景与动画并选择目标场景。具体可选字段、枚举、数值范围与必填项以 `tools/list` 返回的 Schema 为准。

场景相机与编辑器视角是两个独立接口：`scene_camera_update` 修改并保存相机数据，`scene_view_update` 只调整编辑器参考视角，不标记工程为未保存；场景维度通过 `scene_set_dimension` 保存。参考视角的位置与朝向应通过 `scene_view_update` 调整。坐标使用软件的世界单位，灯光和实例的旋转使用角度，参考视角的 `yaw` / `pitch` 使用弧度。实例 `scaleX` / `scaleY` 为缩放比例，`scaleZ` 是挤出厚度（世界单位，可为 0）。参数支持小数；不支持的字段、错误类型、非有限数值和超出范围的参数会返回工具错误。

图层编辑、实例编辑与灯光编辑接入现有撤销流程；实例变换和灯光参数更新遵循当前帧、Auto Key 与曝光规则。元件和场景的创建、重命名、删除，以及相机参数和场景维度修改沿用现有命令，暂不支持撤销，删除前请保存工程。新灯光从第 0 帧开始曝光。自由 `yaw` / `pitch` 要求 3D 视图，不能与 `viewDirection` 在同一次调用中混用。

例如，以下是按顺序发送的 `tools/call` 的 `params`。`symbolId`、`sceneId`、`instanceId` 应替换为查询或创建结果中的实际 ID：

```json
{"name":"workspace_set_active","arguments":{"workspace":"SceneEditor"}}
{"name":"scene_set_active","arguments":{"sceneId":"场景 ID"}}
{"name":"scene_set_dimension","arguments":{"dimension":"ThreeD"}}
{"name":"scene_instance_create","arguments":{"symbolId":"元件 ID","x":120.5,"y":0}}
{"name":"scene_instance_set_transform","arguments":{"instanceId":"实例 ID","z":200,"rotationY":30,"scaleX":1.2}}
{"name":"scene_light_create","arguments":{"kind":"Point","name":"主光"}}
```

还可读取 `vector2d://editor/state` 和 `vector2d://application/settings` 两个 JSON 资源。

`animation_import_bundle` 的版本 1 文件包含 `version`、`name`、`fps`、`frameCount`、`width`、`height`、`symbols` 和 `scene`。每个元件用唯一 `key`、显示 `name`、`frameCount` 定义；可选 `frames` 中每项包含零基 `frame`、相对于 JSON 所在目录的 SVG `path`、`width` 和 `height`。SVG 帧保持到下一个关键帧。元件及场景的 `instances` 通过 `symbol` 引用先前定义的元件，按从底到顶的顺序声明；每项包含 `name`，可设置 `playbackMode: "Loop"`、位置、缩放、旋转、显隐，以及 `keys` 中逐帧的变换。关键帧未指定的字段继承前一状态，车轮或踏蹬等循环元件可以被多个实例共享。SVG 默认以原点居中，尺寸与位置使用相同坐标单位。

导入器先在独立文档中校验并构建完整动画，通过后才替换当前工程；未保存内容默认受到保护，可先 `project_save` 或显式传入 `discardUnsaved`。SVG 必须位于 JSON 目录内，不允许目录逃逸、重解析点、脚本或外部引用。导入完成后仍需调用 `project_save` 保存为原生 `.v2d` 单文件工程或 `.v2dProject` 文件夹工程。

所有编辑器工具都在 UI 线程执行；拖拽、文本编辑或模态窗口打开期间，修改操作返回忙碌错误。工具切换仍受当前工作区、图层锁定等规则约束。打开和新建工程遇到未保存内容时默认拒绝；应先保存，或显式传入 `"discardUnsaved": true`。参数错误和操作失败返回 `isError: true`，不会弹出无人操作的文件对话框。连接开关、令牌和修改权限只通过软件设置窗口调整。

以下 PowerShell 示例读取状态（设置令牌后另加 `Authorization: Bearer …` 请求头）：

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:43521/mcp/' -Method Post `
  -ContentType 'application/json' `
  -Headers @{ Accept = 'application/json, text/event-stream' } `
  -Body '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"editor_get_state","arguments":{}}}'
```

### 固定快捷键与焦点

同一按键会根据当前焦点执行对应上下文的命令。舞台快捷键不会抢占文本框、数值框、下拉框或时间轴输入。

| 焦点 | `Ctrl+A` | `Ctrl+C` / `Ctrl+V` |
| --- | --- | --- |
| 舞台 / 绘图上下文 | 全选当前帧可见且未锁定的内容 | Basic Drawing 中复制 / 粘贴所选本地内容或嵌套实例 |
| 时间轴 | 选择当前可见图层 | 复制 / 粘贴所选 Cel |
| 文本、搜索和数值编辑器 | 由编辑器处理文本选择 | 由编辑器处理文本或数值 |

全局与舞台命令的默认绑定（下列文件、编辑和面板命令可在快捷键方案中调整；3D 交互键及工作区切换键仍按上下文保留）：

| 按键 | 命令 |
| --- | --- |
| `F1` / `F2` | 显示或隐藏检查器 / 时间轴 |
| `Ctrl+1` / `Ctrl+2` / `Ctrl+3` | 切换基础绘制 / 场景与动画 / 场景时间轴的镜头分组（键盘上方数字键） |
| `Ctrl+4`–`Ctrl+9` | 按标签顺序预留给后续工作流模块；空槽不执行操作，`Ctrl+1`–`Ctrl+9` 均不能分配给自定义工具快捷键 |
| `Ctrl+N` / `Ctrl+O` | 新建 / 打开工程 |
| `Ctrl+S` / `Ctrl+Shift+S` | 保存 / 另存工程 |
| `Ctrl+Z` | 撤销当前编辑 |
| `Ctrl+X` / `Ctrl+C` / `Ctrl+V` | 剪切 / 复制 / 粘贴舞台多选；支持本地图形与元件实例的混合选择，以及 Scene & Animation 场景实例 |
| `F8` | Basic Drawing：把同一绘制图层中的舞台选择转换为元件；Scene & Animation 3D：把同一普通场景图层、当前帧有内容且层级连续的多个实例组合为 3D 元件 |
| `Ctrl+Up` / `Ctrl+Down` | Basic Drawing：将所选本地图形或嵌套元件实例在当前图层、当前 Cel 内前移 / 后移一层；Scene & Animation 2D：将所选场景实例在所属场景图层内前移 / 后移一层。3D 模式不执行层级排序 |
| `←` / `→`、`↑` / `↓` | Scene & Animation 3D：切换到 3D 或点击舞台后，左右横移或沿当前镜头中心视线前进 / 后退；方向随镜头旋转和俯仰同步改变，按住可连续平滑移动，配合 `Shift` 以 4 倍速度移动 |
| `G` / `R` / `S`，再按 `X` / `Y` / `Z` | Scene & Animation 3D：沿当前坐标系的指定轴移动 / 旋转 / 缩放所选实例；缩放固定使用局部轴；左键或 `Enter` 确认，右键或 `Escape` 取消 |
| `F` | Scene & Animation 3D：平滑聚焦全部选中实例；能完整显示时保持 100%，过大时自动缩小适配 |
| `Delete` | 删除舞台全部选择，包括 Scene & Animation 场景实例和 Basic Drawing 混合选择 |

### 工具快捷键方案

点击 `Settings → General → Shortcut profiles... / 快捷键方案...` 打开独立面板，可选择方案、创建/重命名/删除自定义方案、分配或清除快捷键。点击 `OK` 返回设置，再点击 `Save` 应用；取消子面板会放弃该次编辑。

传统 Flash 工具键：

| 按键 | 工具 | 按键 | 工具 |
| --- | --- | --- | --- |
| `V` | Select | `Q` | Free Transform |
| `H` | Hand | `R` | Rectangle |
| `O` | Ellipse | `N` | Line |
| `P` | Pen | `Y` | Pencil |
| `T` | Text | `B` | Brush |
| `K` | Fill | `S` | Ink Bottle |
| `I` | Eyedropper | `G` | Gradient |
| `E` | Eraser |  |  |

可切换 `Number Keys` 方案，也可复制内建方案后创建自定义方案。面板采用双栏布局：左侧按全部、工程文件、编辑、绘图工具、工具组、绘制、视图与时间轴分类，右侧按“命令 / 快捷键”显示所选分类。切换分类仅筛选显示，快捷键冲突仍检查整个方案。除工具外还支持自定义以下已有操作：

- **文件**：新建、打开、保存、另存为。
- **编辑**：撤销、全选、剪切、复制、粘贴、删除、转换为元件、前移一层、后移一层。
- **面板**：显示或隐藏检查器、时间轴。
- **时间轴**：播放 / 停止、上一帧 / 下一帧、播放范围首帧 / 末帧、插入帧 / 删除帧、插入关键帧 / 清除关键帧 / 插入空白关键帧。
- **绘制**：减小 / 增大绘制宽度。

内建方案仍保留原默认键；已有自定义方案加载时自动补齐新增命令的默认绑定，已明确清除的绑定不会恢复。改键或清除后，旧按键不再执行该命令；菜单中的快捷键提示随方案更新。同一组合键冲突时可确认替换原绑定，仍受当前工作区、焦点及正在进行的舞台操作限制。文本和数值编辑器保留自己的复制、粘贴、选择与撤销键。

`Ctrl+1`–`Ctrl+9`、`Space`、`Escape`、`Tab` / `Shift+Tab` 和 `Alt+F4` 仍为工作区或交互保留键；`Enter` 仅可绑定播放 / 停止（在输入框或按钮上仍用于输入确认或按钮激活）。3D 相机、轴向变换等上下文交互键未纳入此次自定义；相机导航等已有上下文保留键不能分配给新增的工作台命令，工具键在 3D 中仍按原上下文优先处理。

## 14. 常见问题

### 无法绘制

依次检查：

1. 是否位于 `Basic Drawing`。
2. 当前是否为普通绘制图层，而不是 Folder 或 Mask 的错误目标。
3. 图层是否可见、未锁定。
4. 当前帧是否允许写入；需要新 Cel 时先按 `F6` 或 `F7`。
5. 是否正在播放动画、编辑文本或操作输入框。

### 选择不到对象

- 检查图层和父文件夹是否锁定。
- 检查对象是否位于嵌套实例中；本地几何优先，双击实例可进入源对象。
- 未拆散 SVG 只能作为整体选择。
- Outline 不会禁止选择，但 Mask 和可见性会影响实际显示内容。

### 混合模式看不到预期效果

- 混合需要下方存在可见像素；确认图层顺序和可见性。
- 检查 Folder 或 Scene Layer 是否作为整体隔离混合。
- 检查 Mask 是否先裁掉了内容。
- 临时切回 `Normal` 可区分材质问题与混合问题。

### Fill 或 Stroke 颜色模块不显示

- 用 `F1` 展开检查器。
- 返回 `Basic Drawing`，选择绘制工具或可编辑对象。
- 在基础着色面板顶部点击 `Fill` 或 `Stroke` 目标。
- 滚动检查器，确认图层混合面板下方的基础着色区域已展开。

### 播放时内容被提前修改

这通常是保持曝光机制导致的。关闭 Auto Key 时，在普通保持帧编辑会修改来源 Cel。需要从当前帧开始变化时，先建立关键帧，或开启 Auto Key。

### 找不到导出元件或导入元件的命令

- 导出命令要求存在可导出的当前元件：在 Vault 中右键元件，或在舞台右键菜单的 `Symbol / 元件` 组中使用导出项（作用于当前元件）。舞台右键菜单的 `Symbol / 元件` 组中没有可用导出项，通常说明当前没有活动的可导出元件。
- Vault 的 `SVG 素材` 分类提供放置、从源更新、替换来源、独立副本和删除；素材本身不提供元件导出。
- Vault 中选中 `图片` 分类的素材时，菜单提供 `Place`、`Import Settings...`、从源更新、替换来源、独立副本和删除，不提供元件导出，也不能 `Break Apart`。
- `Import Symbol File...` 位于舞台右键菜单的 `Symbol / 元件` 组，且始终可用；如果看不到，请确认是在舞台画布上右键，而不是在时间轴、Vault 或检查器上右键。
- 误把 `.V2DSymbol` 当成 SVG 打开会失败：它是有独立格式版本的压缩包，请使用导入命令，而非 `Import SVG...`。

### 性能或渲染异常

- 填色桶图标绑定到系统鼠标光标，由 Windows 跟随指针显示，不再作为舞台覆盖层逐帧绘制；仅移动填色光标不会触发舞台重绘。光标保留当前填色颜色与原命中点，按颜色、主题和 DPI 缓存；区域悬停预览和填色动画保持不变。填色光标的流畅度与舞台 `Render FPS` 读数分离。
- The Fill bucket is a Windows cursor rather than a Stage overlay, so moving it alone does not repaint the Stage. It retains the active fill color and original hotspot and is cached by color, theme and DPI. Region hover previews and fill animations are unchanged; cursor smoothness is independent of Stage Render FPS.

- 填色、文本、吸管等工具的悬停反馈也会合并最新指针位置并主动排入 UI 队列，不再仅由 16ms 定时器推进。文本与吸管使用系统光标，其移动频率不等于舞台 `Render FPS`；静态画面不会为提高读数而空转重绘。120 FPS 以上的实际表现仍需在目标场景、输入设备与渲染设备上实测确认。
- Fill, Text and Eyedropper hover feedback also coalesces the latest pointer position into the UI queue instead of relying only on the 16 ms timer. Text and Eyedropper use system cursors, whose motion rate is not Stage Render FPS. Static content is not continuously repainted to inflate the counter; performance above 120 FPS still requires measurement with the target scene, input device and renderer.

- 线条、贝塞尔边界、选择拖动、二维变换和自由绘制预览会合并连续输入，只处理最新待处理位置，并主动排入 UI 队列，不再仅等待 16ms 预览定时器；定时器仍作为兜底。硬件渲染的交互同步呈现门槛为 5.5ms，不降低曲线采样精度、抗锯齿或最终画质。该门槛不是实测耗时或 180 FPS 保证；底部 `Render FPS` 统计实际完成绘制的频率，静止时不会为提高数字而持续空转重绘。
- 在全 `Normal` 混合的 2D 绘制中，舞台会把稳定底图保留在 Direct2D GPU 资源中；笔刷预览、填色/线段首移预览、选区、贝塞尔控制柄和工具光标作为实时覆盖层更新。首移只平移缓存几何并复用底图，避免先重建整幅场景再显示位移。
- Reference 3D 播放会启用有界的工作区帧缓存：已经绘制过的组合帧会保留为当前 Direct2D 目标上的 GPU 位图，后续播放或循环命中时只提交整帧位图，再绘制选区和交互覆盖层。帧缓存不跨窗口、尺寸、相机、灯光或工程编辑复用；这些状态变化会自动清空，缓存达到内存上限时按最早使用顺序淘汰。
- Reference 3D 停止播放且舞台空闲时，会按后续播放顺序逐帧预渲染邻近帧；预渲染帧在切换到对应播放头时直接复用 GPU 位图。相同窗口尺寸下，连续预渲染会复用兼容渲染目标和绘制资源，减少逐帧重建；窗口尺寸改变、渲染设备重置或关闭舞台时会释放这些资源，不改变预渲染分辨率。复杂帧每次只预取一帧，慢帧完成后会短暂让出 UI；正在编辑、镜头过渡、光学交互、遮罩或软件合成状态下会自动暂停预渲染。
- Reference 3D 的密集 Normal 场景（通常超过 100 个绘制项）会在线程池中并行准备渲染计划，并在满足条件时并行栅格化独立瓦片；最终仍由单个 Direct2D 目标按原始深度顺序提交，以保留遮挡、透明度、渐变和光照结果。连续播放时，高密度 CPU 栅格会使用约 0.7 倍预览分辨率并以线性插值放大到舞台，减少每帧像素计算；暂停、停止和工作区预渲染仍使用完整分辨率。含复杂混合、畸变、组合遮罩、拖动预览或上下文图层时自动使用精确路径。连续播放时会跳过不会被下一帧复用的整帧 GPU 拷贝；停止播放后可用工作区预渲染缓存按帧直接回放。
- 精确布尔拓扑、填色边界和最终贝塞尔几何仍由 CPU 在提交阶段计算；GPU 负责缓存、变换、栅格化和交互反馈，因此预览提速不会降低最终矢量精度。
- 非 `Normal` 混合模式使用软件合成，大画布和多层效果可能比全 Normal 慢。
- Direct2D 创建失败时会自动回退到 GDI，功能仍可使用但性能可能下降。
- 查看 `logs/native-*.log` 中的 Direct2D、GDI fallback 或未处理异常信息。

### 崩溃诊断报告

软件遇到未处理错误时会自动在程序目录的 `logs/crashes/` 下写出一份崩溃报告（`crash-<日期>-<时间>-pid<进程号>.txt`）。报告与常规日志分开保存，包含：

- 崩溃来源（UI 线程、后台线程、启动致命错误）、发生时间与是否终止进程。
- 进程与运行环境：进程号、命令行、工作目录、操作系统与架构、.NET 版本、进程运行时长、内存与 GC 堆占用。
- 已加载模块及版本。
- 完整异常链：外层异常、每一层 `InnerException`、聚合异常的各成员以及各自的调用栈（含 `HRESULT`）。
- 最近约 120 条用户操作记录（工具切换、选区、跳帧、工程打开/保存等），用于还原崩溃前正在做什么。
- 崩溃前最后 200 条日志行，并附完整日志文件路径。

报告会覆盖最常见的崩溃通道：UI 线程未处理异常、后台线程未处理异常、启动阶段的致命异常，以及被遗漏的 Task 异常。需要更细的排查时，可设置环境变量 `V2D_LOG_FIRST_CHANCE_EXCEPTIONS=1` 启动，额外记录所有首次 Chance 异常（包括被后续代码捕获的异常），用于定位异常最初抛出的位置；该选项日志量较大，仅在排查时临时开启。

报告以纯文本写出，可直接附在问题反馈中。若报告未生成，`logs/native-*.log` 仍保留崩溃时的 fatal 记录与报告路径。

命令行原生回归（`--bench`、`--bench-*`）出现用例异常时，会将异常写入标准错误和常规日志，并以非零退出码结束，不弹出需要人工关闭的崩溃对话框；自动化执行应同时检查退出码与性能预算结果。

## 15. 当前限制

- 当前复杂形状使用确定性的自动轮廓匹配，尚未提供 Adobe Animate 的 Shape Hints（a-z）和 Angular/Distributive 手动混合模式。自动结果不符合预期时，应像 Animate 官方建议一样增加中间关键形状来约束变化。
- 通用路径节点编辑尚未覆盖所有对象类型；文本和未拆散 SVG 不开放内部节点。
- Scene 时间轴实例支持关键帧状态，也支持在舞台多选后剪切、复制、粘贴和删除；但不直接创建传统补间或形状补间。元件图层中的单个嵌套实例可创建传统补间；形状补间只适用于本地矢量内容。
- 导出能力分为两类：单个元件可导出为独立 SVG 文件（只保留外观），也可导出为可再次导入的 `.V2DSymbol` 元件包（保留完整可逆数据、嵌套关系与库元数据）。当前仍没有整场景或整段动画的作品导出命令。`.Vault` 中的 SVG 是工程内部可逆资产，与上述两种导出用途不同。
- `.V2DSymbol` 以元件为粒度：包内只包含所选元件及其嵌套引用的元件，不会带上引用它们的场景、场景实例或时间轴播放设置。导入是“新增”而非“替换”，无法用它对已有元件做覆盖式更新。
- 复杂 SVG 拆散是有限精度转换，无法保证与原始滤镜或混合效果逐像素一致。
- 非 Normal 混合依赖软件合成，复杂场景的实时性能低于 Direct2D Normal 路径。
- 更复杂的自动对齐规则和持久 GPU 几何缓存仍在持续完善。

## 16. 操作原则速查

- 先确认工作区，再确认活动图层和播放头。
- 先选 `Fill` 或 `Stroke`，再改材质。
- 需要从当前帧开始变化时，先建立 Cel 或开启 Auto Key。
- 图层颜色用于编辑识别，材质颜色才会进入作品。
- Outline 和 Onion Skin 都是编辑辅助，不修改工程几何。
- Scene 实例引用源元件；修改源会影响所有引用。
- Mask 先裁切，Blend Mode 后合成。
- 保存为文件夹工程时保持整个工程文件夹结构完整；保存为 `.v2d` 时只需保管这一个文件，系统托管工作区会自动维护。

## 17. 源码发布管理器

源码维护者可运行 `scripts\run-release-manager.ps1` 打开独立 Release Manager；需要独立应用文件时运行 `scripts\publish-release-manager.ps1`，默认输出 `artifacts\release-manager-exe\ReleaseManager.exe`。它可以创建严格三段式版本号、管理中英文结构化 Release Notes、控制各版本是否在程序内显示、编辑中英文鸣谢、使用正式公告面板预览（含鸣谢），并调用唯一的单 EXE 发布流程。公告编辑、鸣谢编辑和预览的初始语言跟随软件界面语言设置，仍可分别切换 `English / 简体中文`。

点击 `Save Version` 会把所选版本设为当前源码版本，并同步 `Directory.Build.props` 中的四个版本属性；中英文分区的增删移动和条目数量会保持对齐。鸣谢在 `Acknowledgements` 页编辑，同样支持中英文标题、摘要、分组与名单，并可整体关闭以回退到内置默认鸣谢。新建版本的占位说明可以先保存，但替换前不会允许正式发布。工具会在保存和发布前检查源码文件是否被外部修改；发布取消采用协作式提交边界，正式文件替换开始后会完成校验再返回。`Preview` 直接读取内存草稿，编辑后无需先保存即可查看最终效果。正式输出仍只能位于 `artifacts\release` 或明确选择的空目录，仓库根目录开发启动器不会被替换。完整工作流和数据职责见 [Release Manager](RELEASE_MANAGER.md)。

右侧检查器不显示滚动条；内容超出面板时可使用鼠标滚轮浏览下方参数。

右侧检查器按固定职责分为**属性、工具设置、外观、物理、镜头、场景**。属性显示当前对象／元件／图像／文字与普通补间的属性，无选区时显示文档信息；工具设置集中管理笔尖、笔刷大小、压力平滑、混色、铅笔和形状绘制参数，文字排版仍在属性页；外观集中管理填充、描边颜色、线宽和渐变。场景页保留场景、播放和适用的灯光设置，镜头页保留相机列表、构图和镜头补间，物理尚未实现，保持禁用。

右侧分类栏使用导入的矢量图标：列表对应属性、调色板对应颜色、原子对应物理、视频对应镜头、场景图标对应场景。工具设置使用笔刷图标，原颜色入口更名为外观。分类栏采用 38 逻辑像素宽的紧凑纯图标布局，不常驻显示名称，也不随语言加宽；完整名称通过悬停提示及无障碍名称提供。颜色跟随主题、选中及禁用状态，物理入口仍禁用。图标已随应用内置，不需要安装图标字体。

The category rail uses the supplied vector icons in a compact, fixed 38-logical-pixel icon-only strip: list for Properties, palette for Color, atom for Physics, video for Camera, and scene for Scene. Full names remain available through hover tips and accessible names; changing language does not widen the rail. Icons follow theme, selection and disabled colors. They are built into the application and require no font installation.

右侧检查器在常规窗口中比原布局加宽一个分类栏（38 逻辑像素），展开总宽上限为 410 逻辑像素；窄窗口仍自适应收缩，保留原有 272 逻辑像素下限。所有分类页共用该宽度，切换分类不改变舞台宽度；字体、控件高度及图标栏宽度不变。

The right inspector gains one category-rail width (38 logical pixels) in regular windows, with an expanded width cap of 410 logical pixels. Narrow windows still adapt down to the existing 272-logical-pixel minimum. All category pages share the same width, so switching categories does not resize the stage. Fonts, control heights and rail width remain unchanged.

未选中对象时，属性检查器显示“文档与图层”：工程名称与真实保存状态、保存位置、当前场景、当前图层及混合模式、画布与背景。点击工程名称可重命名，`Enter` 提交、`Esc` 取消；混合模式沿用原有图层编辑与撤销流程。“设置画布与背景”打开实际设置弹窗，不提供仅供设计预览的尺寸控件。工程概览可折叠，显示现有统计与素材数量；空工程提示在当前工作内容中有对象后隐藏。内容超出窗口时可滚动，切换到工具设置、外观、镜头或场景分类不会与文档页叠加。

With no object selected, Properties shows **Document & layers**: project name, actual save state and location, current scene and layer, blend mode, and canvas/background status. Click the project name to rename it (`Enter` commits; `Esc` cancels). Blend mode keeps the existing layer editing and undo path. **Set canvas & background** opens the real settings dialog rather than a design-only size selector. The collapsible project overview presents existing statistics and asset counts; the empty-content hint disappears once the current working content contains objects. Overflow scrolls, and Tool Settings, Appearance, Camera and Scene remain mutually exclusive with the document page.

笔刷和形状等绘制工具的参数统一在“工具设置”页；文字排版在“属性”页。切换工具不会自动切换分类，无选区时属性页保留文档信息，文字编辑期间保留排版设置。

文档页用明亮文字显示标题、工程名称和实际数值，次级文字显示字段名、路径、统计标签与操作提示；未保存状态使用警示色。次级色按面板背景保持可读对比度，跟随深浅主题切换；系统高对比度模式使用系统文字色。

The document page uses primary text for headings, the project name and actual values, secondary text for field labels, paths, statistic captions and hints, and a warning color for unsaved state. Secondary text is contrast-checked against the panel surface and follows light/dark theme changes; system high-contrast mode uses system text colors.

“场景与动画”页采用场景卡片、当前场景标识和实例数量；没有实例时显示可直接添加的空态。播放设置以双列展示帧率／循环和开始／结束帧，沿用真实的播放范围。“镜头”页按镜头设置、变换、显示与预览、2D 视差分区；2D 位置 X／Y 与旋转／缩放并排显示，窄栏改为纵向排列，3D 轴及原有相机参数保留。展开“2D 视差”后，直接显示视差强度、相机 Z、参考平面 Z、保持大小、图层深度列表与可拖动的深度图；近／远方向提示与图层色标帮助识别空间关系。仅水平／垂直响应和推拉深度缩放收在“高级响应”中，展开状态在数据刷新时保持。相机新建、定位、排序、删除、重置、预览及连续编辑仍沿用现有命令与撤销流程。小窗口可滚动，不以隐藏参数替代功能。

**Scene & Animation** uses scene cards, the active-scene indicator and instance counts, with a real Add Instance action in the empty state. Playback presents FPS/loop and start/end frames in two columns using the actual playback range. **Camera** is organized into lens settings, transforms, display/preview and 2D parallax. In 2D, X/Y position and rotation/zoom sit side by side and stack in narrow panels; 3D axes and existing camera parameters remain available. Expanding **2D Parallax** directly exposes strength, camera Z, reference-plane Z, Keep Size, layer depths and the draggable depth diagram. Near/far hints and layer color markers clarify the spatial relationship. Only horizontal/vertical response and dolly depth scaling live under **Advanced Response**, whose expansion state survives refreshes. Camera creation, locating, ordering, removal, resetting, preview and continuous edits keep the existing commands and undo path. Smaller windows scroll rather than lose settings.

Tool Settings owns brush-tip, brush-size, pressure smoothing, mixing, pencil and shape-drawing options. Properties owns selected-object attributes, text typography and ordinary tween curves. Appearance owns fill, stroke color, line width and gradients. Camera retains camera tween curves. With no selection, Properties shows document information; active text editing keeps typography available.

选中普通图层或灯光补间时，检查器会切换到“属性”显示曲线；选中相机补间时切换到“相机”。清除补间选择后隐藏对应曲线，不修改曲线数据或增加撤销记录。

Selecting a layer or light tween reveals its curve in Properties; selecting a camera tween reveals Camera. Clearing the tween selection hides the curve without modifying it or adding an undo entry.

分类由用户主动选择。选择／取消选择对象、切换工具、切换帧和普通刷新只更新内容，不自动切换分类，也不重新打开已关闭的检查器。外观页显示当前编辑目标；无选区时说明使用当前绘图外观，选中不支持着色的 SVG／位图时说明原因并禁用编辑器。

Selecting or clearing objects, changing tools or frames, and ordinary refreshes update data without switching the active category or reopening a closed inspector. Appearance identifies its editing target and disables unsupported SVG/bitmap material editing with an explanation.

分类页互斥占用检查器正文。重复点击当前分类保持该页；点击属性或在分类栏中按 `Esc` 返回属性页。悬停不切换；`↑` / `↓` / `Home` / `End` 移动焦点，`Enter` / `Space` 确认并跳过禁用项。`Ctrl+Alt+1..5` 保留属性、外观、物理、镜头、场景的原有顺序，`Ctrl+Alt+6` 打开工具设置。分类切换不自动更换工作区；不适用时显示原因和“进入基础绘制／场景与动画”按钮，点击该按钮才切换工作区。手动切换工作区仍保留已选分类。打开镜头页、添加灯光或调用绘画色板这类明确编辑命令可定位对应分类。检查器宽度小于 324 逻辑像素时，图标栏改为顶部分类下拉框，释放正文宽度；仍可使用分类快捷键。笔尖操作在窄栏自动换行，绘图参数保持可访问。

Inspector tabs have stable ownership: Properties, Tool Settings, Appearance, Physics, Camera and Scene. Physics remains disabled. Clicking the active tab keeps it open; Properties or Escape with rail focus returns to Properties. Arrow/Home/End moves focus and Enter/Space activates. Ctrl+Alt+1..5 retains Properties/Appearance/Physics/Camera/Scene; Ctrl+Alt+6 opens Tool Settings. Category navigation never changes workspaces automatically. An incompatible category explains its required context and offers an explicit Open Basic Drawing / Open Scene & Animation action. Manual workspace changes preserve the chosen category. Explicit editing commands such as opening the shot page, adding a light or invoking the brush color palette may reveal the relevant category. Below 324 logical pixels, a top category selector replaces the icon rail to preserve usable content width; category shortcuts remain available. Brush-tip actions wrap in narrow panels rather than clipping controls.

时间轴图层组栏右上角齿轮直接打开较大的图层组排序弹窗，可拖动组标签调整顺序，确认后应用、取消不应用。重命名、颜色与删除保留在图层组右键菜单中。

图层组管理弹窗包含重命名、删除、排序、颜色和恢复默认颜色。配置在弹窗中暂存，确认后通过既有图层组编辑边界统一应用，取消不应用；内置组禁止重命名和删除。名称输入弹窗复用 ModernTextInput，关闭附加焦点下划线并保留主题边框。

图层组管理窗口顶部显示组标签和管理操作，下方显示所选组中的图层名称，便于调整分组顺序时核对内容。图层组管理窗口采用 1000×640 的宽敞尺寸，组名称输入窗口保持原来的 440×224 紧凑尺寸。

图层组管理采用紧凑组标签区，所选组名称与管理操作共用一行：标题靠左、操作靠右。下方图层列表使用带内边距的 36 逻辑像素行高和柔和行底色，保持 1000×640 窗口尺寸。

图层组标签超出窗口时采用细窄的主题横向滚动条，无系统箭头和白色轨道，支持滑块拖动、滚轮浏览与标签拖动时的边缘滚动。

按钮和菜单命令不再添加表示后续弹窗的固定省略号；仅在文本实际超长时显示截断省略号，加载状态、搜索提示及更多入口保留各自语义。

主菜单的设置与鸣谢入口使用纯文字。鸣谢入口打开主题对话框，按当前界面语言显示鸣谢标题、摘要与分组名单；中英文共用同一份鸣谢名单，Release Manager 中任一语言下的名单修改会同步到另一语言。名单来自 `release/release-notes.json` 中的 `acknowledgements` 内容，由 Release Manager 编辑，清单缺少该内容时回退到内置默认名单（聪明尼）。


颜色与渐变预设弹窗按按钮右侧对齐向左展开，并限制在屏幕工作区内；下方空间不足时向上展开。内容高度覆盖全部固定行与内边距，标题复用模块标题样式并接入本地化，边框使用主题分界线色。

设置窗口采用统一的内边距与分区间距：主题色和高亮色并排调节，语言和个性化选项与左侧名称对齐，简洁选中高亮使用主题开关。颜色数值的单位只显示一次；常规与 Codex / MCP 页面切换保持窗口尺寸稳定。

时间轴顶部的前、后、帧高和帧宽标签使用浅色次级文字，保证深色主题下的可读性。

顶部网格与吸附工具使用统一高度和间距，吸附图标扩大可见绘制尺寸，辅助数值使用次级文字。配色色板弹窗统一分区标题、内边距和色块间距，默认色块与渐变预览使用柔和的主题边框，悬浮和选中仍保留清晰反馈。
