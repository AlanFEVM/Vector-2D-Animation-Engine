# FLA 导入对齐检查（进行中）

素材根目录：`D:\Animations\madness combat resources\madness combat resources\namek_flix sprite sheet wow`。

2026-10-30 只读清点：454 份 FLA，318 份 ZIP/XFL 容器，136 份 OLE 二进制容器。格式识别不代表导入通过；直接导入、Adobe 转换桥、保存重开、编辑和视觉一致性须分别验收。

最近完成的454份全量复测位于 `%TEMP%\v2d-fla-audit\nested-svg-current-full`：131份导入/保存重开/结构比较通过，323份在导入阶段失败，没有保存、重开或比较阶段失败。固定程序集SHA256为`3D6FB08B8A045C34E5AD75AE5B1AA015F7C88AFCCC96858E071E15EF89ABBAA6`，脚本确认`UnchangedBuild=true`；454个唯一路径，无遗漏。相对上一轮`indexed-library-current-full`（SHA256=`553951C15C4096C0EBFDE78AAF4DB3FA6DA3B71A992C261FB6226DD92B9212FA`）通过状态无变化：新增0、退化0，5份失败文件越过旧阻断后停在下一项不支持内容。该轮包含重复Include、缓动及嵌套SVG修复，**不是Adobe全画面一致性验收，也不包含扫描之后新增的遮罩计算器与合成裁剪接入（普通二维绘制入口已接入，参考投影等路径尚待完成）**。此前128份/326份属于更旧构建记录。当前仍有大量功能未对齐，目标未完成。

## 基线证据

### 最近全量扫描的首个阻断分布

以下按 `nested-svg-current-full/results.csv` 的错误去除元件/图层位置后归类，每份失败文件只计当前遇到的第一个阻断，并非文件内全部不支持项的数量：

| 首个阻断 | 文件数 |
| --- | ---: |
| 旧 OLE 二进制 FLA，需先转换为 XFL | 136 |
| 遮罩层 | 34 |
| 未闭合填色边界 | 20 |
| 交互按钮元件 | 18 |
| 非实线矢量描边 | 12 |
| 无样式 Edge | 10 |
| 屏幕空间 hairline 描边 | 10 |
| object/shape tween 几何 | 9 |
| AdjustColorFilter | 7 |
| 静态文本 autoKern=false | 7 |
| MorphShape | 6 |
| 多样式静态文本 | 5 |
| pixel-hinted 描边 | 5 |
| 其他首个阻断 | 44 |

合计323份。修复一项后可能显露下一项阻断，不能将该项文件数直接当作预期新增成功数。分类证据保存在同一临时目录的 `failure-categories.csv`。

执行 `.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Timeline -Plan` 后执行同命令去掉 `-Plan`。修改前 Release 构建和 Timeline 回归退出码均为 0，包含 `flash_import_regression=ok`。工作树有大量既有修改，该结果仅对应当时工作树，不等于本次新增修改已验证。

逐文件直接调用 `FlashDocumentImporter.Load(path, editableGeometry: true)` 的修改前基线扫描已完成：454 份唯一文件，101 份成功、353 份失败；其中 136 份失败为旧 OLE 二进制格式，其余 217 份为 ZIP 内容或实现阻断。结果保存在本机 `%TEMP%\v2d-fla-audit\result-*.json`；`baseline-results.csv` 为阶段汇总。该扫描尚不覆盖所有文件的保存重开或 Adobe 画面对比。

## 素材内容范围

最新构建包含静态文本外观、零行距和 ZIP 大小写修复后，再次执行验证脚本 `-Suite Timeline`，退出码 0；`flash_static_text_filters_save_reopen=ok`、`flash_zip_case_distinct_library=ok` 与 `timeline_regression=ok` 均通过。此前 Motion Track 双击失败本次未复现，不将此前失败抹去。

仓库检查脚本已用真实 `backgrounds/1_bg.fla` 实运行，导入、保存重开及 ID/帧数/时间轴/实例比较通过（退出码 0）。较早构建的全目录检查输出到 `%TEMP%\v2d-fla-audit\current-build-full`。454 份唯一文件中，117 份完成导入、保存重开及结构比较，337 份在 import 阶段失败；没有 save/reopen/compare 阶段失败。相对基线新增通过 16 份，基线通过的 101 份无退化。程序集 SHA-256 在扫描前后均为 `C79BBA2203560237DC132E1BA6420620EA6C0496C788FC10D9F3B0231E8DB1FA`。运行退出码 1 符合“任一文件失败即失败”的脚本规则，不代表全部导入可用。

修复后 Release 构建通过（0 错误、30 警告）。执行 Timeline 套件时，`RunMotionTrackRapidFrameEditingRegression` 报告 `Rapid anchor fixture did not arm frame 0 before the double-click.`，调用顺序在 Flash 回归之前，因此该次 Timeline 不通过。随后通过本机 `%TEMP%\v2d-fla-audit\focused.ps1` 反射调用真实 `Benchmark.RunFlashImportRegression`，退出码 0，包含 `flash_empty_library_timeline_save_reopen=ok`、`flash_zip_logical_paths_disk_suffix_rejection=ok`、`flash_tiny_fill_vector_retained=ok` 和 `flash_import_regression=ok`。此独立结果不替代未通过的完整 Timeline。

首轮 `verified-*.json` 因 PowerShell 反射字符串参数包装错误而停止，不能作为产品失败计数。修正后已先用 `1_bg.fla` 验证导入和保存重开，再启动 `roundtrip-v2-*.json` 全量复测，随后为更新构建而明确停止于第 168 份，保留阶段结果，不计作完成全量复测。该轮使用包含空元件、文本变换中心、逻辑路径后缀和极小填色修复的构建，尚不包含其后新增的库文件大小写匹配、根文档大小写兼容和显式零行距修改。当前该轮保存重开检查仅比较场景和元件数量，尚不能证明每个像素及全部编辑语义一致。

以下数字是包含对应 XML 特征的 ZIP 文件数，允许重叠，并非成功导入数。扫描通过标准 ZIP 读取；具有非标准目录或解析错误的文件仍须结合逐文件结果检查。

| 特征 | 文件数 |
| --- | ---: |
| 静态文本 | 186 |
| 动态文本 / 输入文本 / TLF 文本 | 8 / 2 / 3 |
| 遮罩层 | 73 |
| 引导层 | 64 |
| 文件夹层 | 52 |
| 相机层 | 3 |
| 传统 motion / motion object / shape 补间 | 47 / 14 / 33 |
| 按钮元件 / IK 容器 / IK pose | 23 / 1 / 2 |
| Blur / Glow / DropShadow | 92 / 82 / 10 |
| AdjustColor / Bevel / GradientGlow | 8 / 4 / 2 |
| 放置的视频实例 | 1 |

## 待完成验收

修改前 217 份 ZIP 导入失败的首个阻断中，量化透明位图无原始 PNG 占 29 份，遮罩 18 份，按钮 17 份，开放填色边界 15 份，空时间轴和文本变换中心各 10 份，object/shape 补间 8 份，文本布局和非隔离实例滤镜各 7 份，本地精度塌缩 6 份，AdjustColorFilter 5 份。同一文件可能在修复首个阻断后暴露后续问题，不能把这些计数相加当成全部功能使用量。

高级滤镜模型检查：`SymbolFlashColorTransform` 当前只有逐通道乘数/偏移，不能直接表达 hue/saturation 所需的跨通道颜色矩阵；导入滤镜有序链 `SymbolFlashFilterKind` 仅有 Blur、Glow、Shadow。AdjustColorFilter 完整支持需要扩展有序链、持久化、CPU/GPU 执行及补间，而非仅把亮度映射到实例 tint。现有原生 Bevel/GradientGlow 设置也不能直接证明其算法与 Flash 一致。

可复用检查入口：先通过验证脚本构建，再运行 `pwsh -NoProfile -File scripts/test-fla-import.ps1 -Source <FLA或目录> -OutputDirectory <新的结果目录>`。每个文件在独立 STA PowerShell 进程中调用实际导入器，成功时保存重开并核对场景/元件 ID、帧数、时间轴和实例数据；记录程序集哈希及逐文件阶段。该工具不自动构建，不改写源 FLA，任一失败返回非零。已通过 PowerShell 语法检查、单文件实运行及完整 454 文件检查。结构比较不包含像素一致性或完整几何数据逐值比较。

源码检查发现遮罩不能只解除导入拒绝：`VectorScene.SetLayerMask` 会清除同一遮罩的其他内容层链接，目前是一对一绑定；XFL 可将多个内容层挂在同一遮罩下。此外 `RestoreNativeScene` 的分组复制尚未复制 `LayerMaskIds`。实现需同时处理多内容层、场景投影、保存与渲染，不能直接把 XFL mask 当作普通文件夹。

`backgrounds/BACKGROUND (1).fla` 的 `__Camera__` 是 `layerType="camera"` 内的专用实例，不是普通库条目。其相机层需按舞台取景和时间轴转换，不能靠删除缺失引用修复。`backgrounds/idk.fla` 的 Include 与 ZIP 条目还存在额外实体编码差异，需要确认解码规则及名称碰撞策略。

- 已完成全部 454 份的基线及当前修复复测；后续功能修改仍需定向验证并更新全量结果。
- 验证空库元件、静态文本变换中心、ZIP 逻辑路径后缀与极小填色 SVG 保留的新增回归。
- 对齐遮罩、形状补间、按钮、相机、高级滤镜、文本和媒体的实际导入语义；不能把拒绝、忽略、视觉烘焙或导入成功混称为原生可编辑支持。
- 对成功导入结果验证保存重开、嵌套引用、时间轴和可见内容，并记录 Adobe 对比是否实际执行。
- 用户指南参数须与最终源码保持一致；当前尚未完成上述对齐，不应据此声明全面兼容 Adobe Animate。

## 本轮全量结果与后续优先级

执行命令：`pwsh -NoProfile -File scripts/test-fla-import.ps1 -Source <上述素材根目录> -OutputDirectory "$env:TEMP\v2d-fla-audit\current-build-full"`，退出码 1。逐文件记录为 `case-*/result.json`；汇总为 `results.csv`、`summary.json`、`failure-categories.csv`，新增通过列表为 `newly-passing.csv`。输出及临时工程没有加入仓库。

| 范围 | 文件数 | 完成导入与结构比较 |
| --- | ---: | ---: |
| FlashPortable 附带组件与模板（extras/apps） | 158 | 35 |
| 其余素材 | 296 | 82 |
| 合计 | 454 | 117 |

剩余 337 份的首个错误按关键字归类如下，仅用于分诊，不代表同一文件只使用一种未支持功能：旧二进制 136；量化透明位图 43；遮罩/骨骼层 21；开放填色轮廓 19；按钮元件 18；描边样式 18；文本布局 16；高级滤镜 12；高级补间 12；位图编码/原图 12；库路径/容器 7；填色边数据 6；缺失库引用 4；其他 13。不能将这些归类当成 Adobe 兼容性结论。

遮罩进一步检查确认，`SceneCompositionBuilder.CollectDrawingObjectLayers` 会将宿主层内的嵌套实例展开到子层，而遮罩链接目前仅由源宿主层映射。仅把多个内容层放进组元件再解除导入限制，不能保证展开后的内容实际受裁剪。该试验实现已撤回，未作为支持项交付。完整修复需要同时覆盖嵌套实例、遮罩几何、文件夹、渲染回退和保存重开。

已发现本机 Adobe Animate 2024 (`24.0.0.305`)。使用现有 `FlashLegacyConversionBridge.Prepare` 为 `backgrounds/Backgrounds + Vehicles.fla` 生成隔离副本及脚本（`%TEMP%\v2d-fla-audit\adobe-legacy-001`），记录原文件 SHA-256。Animate 的“打开 JSFL 文件”对话框需要明确选择“作为命令运行”；本次 Adobe 转换最终报告 `SUCCEEDED`，生成现代 ZIP FLA，原始源文件哈希保持不变。对转换产物运行同一导入检查脚本后仍在 import 阶段失败：`XFL [cs_italy]: Interactive button symbols require conversion through Adobe Animate.`。转换格式成功不等于原生导入成功，不能视为 136 份旧格式已支持。

实际 Adobe 转换还出现了以下提示，均只对隔离副本继续处理：旧 ActionScript 2.0 不再受 Animate 2024 支持，另存时会丢失；缺失字体包括 Acquaintance、Arial TUR、Bubble 1、Chiller、Colonna MT，采用本机默认映射。因此本次转换产物不具备完整脚本和字体外观保真证据。没有执行原工程与 V2D 的像素对比。

批量检查脚本新增自动 `results.csv` / `summary.json`，记录阶段计数并检查扫描结束时程序集哈希是否变化；变化即报错，不将混合构建扫描视为一次有效结果。新增汇总逻辑用真实 `backgrounds/1_bg.fla` 再次检查，退出码 0，`UnchangedBuild=true`，结果位于 `%TEMP%\v2d-fla-audit\script-summary-smoke`。本轮全量扫描使用相同核心检查逻辑，汇总在完成后单独生成，扫描前后哈希已单独核对一致。

## 预乘 Alpha 位图解码（后续修复）

通过 Adobe Animate 2024 的 `bitmapItem.exportToFile()` 对 `Characters/Weapons and melee/Pre_animated_casings.fla` 的 `Bitmap 2` 导出 PNG，原始 FLA 和工作副本 SHA-256 一致。库位图尺寸为 598×572，内嵌 03/05 编码的 Alpha 标志为 1，实际像素仅有 Alpha 0 或 255。将内嵌解压数据按 ARGB → BGRA 转换，与 Adobe 导出 PNG 的全部 342,056 个像素逐通道比较，差异数为 0（342,053 个不透明像素、3 个透明像素）。证据在 `%TEMP%\v2d-fla-audit\adobe-bitmap-oracle-001`，包含隔离副本、Adobe 导出 PNG、解压前数据及 `oracle-evidence.json`。API 依据：[Adobe bitmapItem.exportToFile](https://github.com/AdobeDocs/developers-animatesdk-docs/blob/master/BitmapItem_object/bitmapIte2.md)。

第一阶段修复不再仅因 Alpha 标志为 1 而拒绝全透明/全不透明位图。该阶段 Release 构建通过（0 错误、30 个既有警告），Flash 定向回归包含 `flash_import_regression=ok`。完整 Timeline 随后在 `RunSceneShotTimelineUndoRegression` 的 WinForms 消息循环持续运行约 15 分钟，抓取线程栈后明确终止，进程退出码 1；因此本阶段完整 Timeline **未完成**，不能报告通过，也尚未证明卡住由哪项修改引起。证据 `%TEMP%\v2d-fla-audit\timeline-35888-stack.txt`。

进一步用 Animate 2024 导出 `Characters/Salterino_Sprites.fla` 的全部 25 个库位图，合计 317,565 像素（`adobe-bitmap-oracle-002`）。实际半透明数据的 Alpha 与 Adobe PNG 完全一致，所有颜色通道均不超过 Alpha。用规范整数舍入反预乘后，Alpha 差异为 0；直通 RGB 共 55 个通道与 Adobe 导出相差 1，最大差异 1；双方按 `(channel * alpha + 127) / 255` 重新预乘后的颜色差异为 0。没有声称恢复作者量化前的原始直通 RGB。

第二阶段实现已支持半透明预乘 ARGB，保留存储 Alpha，输出 PNG，并报告 `bitmap-premultiplied-decoded`；非法的颜色通道大于 Alpha 仍拒绝。实际调用产品 `WriteLosslessBitmap` 为上述 25 个位图生成 PNG（测试 XML 中移除原始 PNG 引用，源 FLA 不变），逐像素结果与上述统计一致，记录于 `native-oracle-comparison.json`。该阶段 Flash 定向回归退出码 0，包含半透明位图和保存重开检查。

同一真实文件的 `Bitmap 20` 原始 PNG 引用触发了旧匹配逻辑拒绝。原逻辑假设 Alpha +1 和 RGB /256，与本次 Adobe 导出证据不符；新逻辑只在原始 PNG 的 Alpha 和重新预乘颜色匹配时保留它，否则使用内嵌数据并报告 `bitmap-original-mismatch`。新增匹配 PNG 保留、过期 PNG 回退及保存重开检查。执行 `invoke-validation.ps1 -Suite Build -Plan` 后执行 `-Suite Build`，退出码 0（0 错误、30 警告）；`pwsh -NoProfile -STA -File "$env:TEMP/v2d-fla-audit/focused.ps1"` 退出码 0，`flash_import_regression=ok`。使用未经改动的真实 DOMBitmapItem（包括原始 PNG 引用）调用新解码器，25 张 PNG 共 317,565 像素与 Adobe 导出 **所有通道完全一致**；证据 `native-original-oracle-comparison.json`。此前旧匹配报错不能直接证明该真实 PNG 已过期，确认为旧匹配公式错误；过期 PNG 情形另由合成回归覆盖。

上述 117/454 为前一固定构建的全量结果，不能直接套用于位图修复后的构建。端点阶段针对旧 43 个位图首阻断文件的复测为 2 份完整通过、41 份 import 失败（`alpha-endpoints-retest`），其中多份已前进到遮罩、BitmapFill、描边或播放帧等后续阻断。半透明阶段定向 43 份复测完成（`alpha-premultiplied-retest`）：3 份完成导入与保存重开结构比较、40 份 import 失败，程序集哈希 `0C917C9A17EE0E78EEDDF92611D487102513A6EAAD5CB05AACA9765620882EE8` 前后一致。原始 PNG 匹配规则修改后另启 `alpha-original-matching-retest`，不混用构建或将定向计数直接合并为全量通过率。

原图匹配修复后的 43 份定向复测完成：5 份导入/保存重开/结构比较通过，38 份仍在 import 阶段失败，无保存重开或结构比较失败。哈希 `FBE3530DD675AC8BB5E60280BB328AC814711D00833923541058C627EF27BB83` 前后一致。新增通过为 `Salterino_Sprites.fla` 和 `恢复_Salterino_Sprites.fla`；其余通过为 `ClassicGunsPack-V4_1.fla`、`E45_WEAPONS_PACK_1.0.fla`、`Fender-Mustang-Bye45.fla`。这仍不是更新后的全目录统计。

## 完全透明的矢量形状

`redeemer_2_effects_extractd_1.fla` 与 `redeemer_2_effects_extractd.fla` 的首阻断位于 `ehh/Layer 2/frame 1`。读取实际 XML 和生成 SVG 确认形状包含完整矩形边界，填色 `#E9FFFF`、Alpha 为 0。SVG 原生节点提取会因无可见几何抛出 `The SVG contains no editable vector geometry.`；导入器此前把它当成整个 FLA 的失败。现与已有精度限制相同，保留完整 SVG 并报告 `shape-vector-retained`，没有删除路径或改变 Alpha。新增完全透明形状导入、保存重开与场景组合回归。`invoke-validation.ps1 -Suite Build`（先执行 `-Plan`）退出码 0，0 错误、30 警告；Flash 定向回归退出码 0，包含 `flash_transparent_shape_vector_retained=ok` 和 `flash_import_regression=ok`。两份真实文件各用 `scripts/test-fla-import.ps1` 独立重测，退出码均为 1：原透明形状阻断解除，下一首阻断均为 `Heavy Muzzle Effect 1/Camada_1/frame 1` 的 `BitmapFill`。结果位于 `%TEMP%\v2d-fla-audit\transparent-shape-retest`，没有把“通过原阻断”报告为整个文件导入成功。

## 播放起始帧的进一步证据

对隔离的 `Pre_animated_casings.fla` 用 Animate JSFL 实际读取时间轴：`Duplicate Items Folder/Pistol shell 2 spin back` 为 15 帧，其中每个关键帧引用同一个仅有 1 帧的 `Shell casing pistol - 2`，`firstFrame` 依次为 0–14、`loop=loop`；该一帧元件再引用一个 36 帧元件。Animate 可打开并保留这些属性，因此导入器的时长越界拒绝会阻断真实可打开的文件。证据 `adobe-bitmap-oracle-001/firstframe-adobe.tsv`、`firstframe-source.xml`。该阶段尚未确定越界时的嵌套采样语义；后续 Adobe 导出证据和针对一帧循环的修复见下文。


## 一帧循环元件的起始帧修复

进一步用 Animate `document.exportSVG(..., true, false)` 导出上述 `Pistol shell 2 spin back`。实际生成 140 份 SVG（全局导出范围），其中 1–15 有内容，其余为空；不能把导出文件总数当作此元件时长。逐一核对前 15 份 SVG 的路径属性哈希及嵌套变换：内部路径哈希全部为 `114e9867af7540ef486b5a078ee83c958c6c588145d33925e75bc25d3f90fdaf`，内层变换仅有 1 组不同值；外层旋转/平移随关键帧变化。证据 `adobe-bitmap-oracle-001/firstframe-visual-analysis.json`、`parent-sequence1.svg` 至 `parent-sequence15.svg`。因此对于 `graphic`、`loop` 且时长 1 的元件，起始帧超出时长时采样第 0 帧可以保留该实际输出；新增 `single-frame-loop-normalized` 诊断。其他时长或播放模式尚未据此放开。合成回归检查四帧的实际像素、保存重开与未核对的非循环越界继续拒绝。

## BitmapFill 的 Adobe 对照与渲染依赖

真实 `cassiansprites2022.fla` 的 `-666` 元件中 `BitmapFill` 引用 `Bitmap 4`，XML 矩阵为 `(-1.77816772460938, -0.169082641601562, -0.15625, 1.59426879882812, 65.65, -34.6)`。在隔离副本中用 Animate 导出此元件，SVG 图案的对应矩阵为 `(-0.08890838623046875, -0.008453369140625, -0.0078125, 0.07971267700195313, 65.65, -34.6)`：四个线性分量除以 20，平移不除以 20。图案宽高为原位图的 1000×1080，使用 `patternUnits=userSpaceOnUse`。证据 `%TEMP%\v2d-fla-audit\adobe-bitmapfill-oracle-001`，含原文件哈希、隔离副本、Adobe SVG 与导出图片，源文件未写入。

当前依赖 Svg 3.4.7 对应提交 `c53af12d973bffef486c3d6a32c941a0d15cd987`（本机 NuGet nuspec 核对）。其 `SvgPatternServer.GetBrush` 会创建中间瓦片并返回 TextureBrush，未实现 `image-rendering`；`SvgRenderer` 默认插值也不按 Flash 的 `allowSmoothing` 区分。直接把 BitmapFill 写成 pattern 并解除拒绝，不能证明逐点/双线性采样和裁切模式一致。下一实现需要同时验证采样、边界和平铺，尚未将 BitmapFill 作为已支持功能。


本轮验证：先执行 `invoke-validation.ps1 -Suite Build -Plan`，再执行 `-Suite Build`，退出码 0（0 错误、30 警告）。`pwsh -NoProfile -STA -File "$env:TEMP/v2d-fla-audit/focused.ps1"` 退出码 0，包含 `flash_single_frame_loop_offset_save_composition=ok`、`flash_transparent_shape_vector_retained=ok` 和 `flash_import_regression=ok`。真实 `Pre_animated_casings.fla` 用 `scripts/test-fla-import.ps1` 重测，退出码 1：旧 firstFrame 阻断解除，下一个阻断为 `Scene 1/cool text/frame 1` 的未映射静态文本对齐。结果位于 `%TEMP%\v2d-fla-audit\single-frame-loop-real-retest`。未将该文件计为完整通过；没有重复运行此前卡住的完整 Timeline，也未报告完整 Timeline 通过。


## tiled BitmapFill 实现与验证（进行中）

新增 `ImportedSvgRasterizer.BitmapFill.cs`：导入器生成自包含 PNG pattern 并带 `data-v2d-bitmap-sampling`，解析时换为显式的设备像素采样 paint server。GDI TextureBrush 的独立实测表明，即使 Graphics 为 NearestNeighbor，放大纹理仍混合相邻像素；因此不能依赖该选项冒充逐点采样。新路径先按逆矩阵在设备像素中心采样，逐点使用 floor，双线性按半像素中心、预乘颜色插值，再由原 SVG 路径处理边界/裁切。PNG 先校验尺寸和解码预算；采样输出遵循既有 8192 边长、16M 像素预算，不扩预算。GDI HighQuality 合成会把 Alpha128 降至127，实测后仅对该 paint 的路径临时使用 AssumeLinear 并恢复图形状态，以保留存储透明度。

新增 `Benchmark.FlashBitmapFill.cs`，接入原有 Flash 回归入口，覆盖逐点/双线性、平铺、镜像、矢量裁切后采样原点、全局填充不透明度、半透明像素预乘舍入、实际 XFL 导入和删除临时源图片后的保存重开/组合/采样。缺失引用、未核对的 clipped 模式继续拒绝。构建先执行 `invoke-validation.ps1 -Suite Build -Plan`，最终 `-Suite Build` 退出码0（0错误、30警告）；期间修复了本次新增的可访问性、局部变量重名编译错误和 SVG 集合末尾插入异常。最终 `focused.ps1` 退出码0，包含 `flash_bitmap_point_bilinear_tile_reflection_opacity=ok`、`flash_bitmap_fill_import_save_reopen_sampling=ok`、`flash_import_regression=ok`。

执行 `invoke-validation.ps1 -Suite Render -NoBuild -Plan` 后执行 `-Suite Render -NoBuild`，退出码1，首失败 `RunSceneToolPaletteControlsRegression`：`Returning from 3D did not restore the 2D Transform tool, view pad, and hidden-tool state.`，位置 `Benchmark.StageInteraction.cs:539`，在后续渲染用例之前。当前脏工作树未隔离该失败根因，不能声称 Render 整体通过或认定与本次完全无关。

真实文件定向复测运行在 `%TEMP%\v2d-fla-audit\bitmap-fill-tiled-retest`，从前次43个受位图阻断文件筛出12个 BitmapFill 首阻断及2个透明形状后继 BitmapFill 文件，共14份；此范围不是整个目录。先前成功解除原始 PNG 匹配问题的文件没有重复计入。当前 `cassiansprites2022.fla` 与 `raffsprites2021.fla` 已完成导入/保存重开/结构比较；全量定向结果待结束后追加。

对 clipped 模式只在 Adobe 隔离副本里设置 `bitmapIsClipped=true` 并另存 `clipped-copy.fla`、导出 `clipped-symbol.svg`，不写原始素材。Adobe 的 setCustomFill 同时改写各形状的映射矩阵，因此该产物不能单独证明保持原矩阵时的裁切边界语义，暂未把 clipped 模式等同平铺放开。


大型枪械素材 `GunSheet_BoohzuhV9.fla` 的定向检查一度处于保存重开阶段，随后已完成导入、保存重开和结构比较。对当前子进程 PID3164 用本机 dotnet-stack 抓取栈，调用位于 `ProjectVaultStore.LoadValidatedProject` / `ResolveExpectedRelativePath`，并非仍卡在 BitmapFill 解码；证据 `bitmap-fill-3164-stack.txt`。临时工程 `.Vault` 已有3282个文件、46,276,995字节。观察到进程工作集约8.7GB，属于需要进一步量化的资源问题，不能由当前两个成功文件推断大型素材性能已达标。PID3164 已正常结束并返回 `ok/compare`；该次资源观察不能当作完整性能基准。


本轮14份定向复测最终完成，`retest-bitmap-fill.ps1` 退出码0（此临时脚本的退出码仅代表扫描完成，不代表所有文件通过）：4份 `ok/compare`、10份 `failed/import`，没有 save/reopen/compare 失败。通过者为 `cassiansprites2022.fla`、`raffsprites2021.fla`、`GunSheet_BoohzuhV9.fla`、`MuzzleSmokeTryNo2.fla`；失败者已记录各自下一个阻断。全部逐文件结果及固定程序集哈希见 `bitmap-fill-tiled-retest/summary.json` 和 `case-*/result.json`。当前结果仅证明该14份的直接导入和结构比较状态，不是更新后的454份全量统计或完整视觉验收。静态文本 justify、clipped BitmapFill、遮罩/补间等仍是未完成项。


## BitmapFill clamp 与复制状态

在 Adobe Animate 中创建独立32×24文档，导入2×2红/蓝/绿/黄 PNG，填充矩形，设置 `bitmapIsClipped=true`，并通过 contour.fill.matrix 指定矩阵后另存 FLA、导出 PNG/SVG。证据 `%TEMP%\v2d-fla-audit\adobe-clipped-synthetic-001`，未修改素材。`clipped-large.fla` 的映射线性分量80对应导出SVG中的4倍缩放。Adobe PNG中 y=8 时 x=8、12、13为红，x=19、20、24、31为蓝；y=16时对应为绿/黄。远离纹理实际范围仍保持边缘颜色，证明 clipped 使用clamp而非repeat或透明；黑色边界来自矩形默认描边，不作为填充采样证据。双线性过渡例如x=15为红159/蓝95、x=16为红95/蓝159，记录保留在实际PNG中。

实现新增 point-clamp / bilinear-clamp 采样模式，导入 bitmapIsClipped 对应clamp；测试覆盖远边界不混入对边颜色。修复SVG.NET DeepCopy会把自定义pattern/path退回基础类型的问题，自定义类型覆盖DeepCopy并保留不可变像素/采样状态；新增use引用重绘检查。先执行Build -Plan，再执行Build，最终退出码0，0错误30警告；完整Flash定向回归结果随后追加。本轮未重新声称Render或Timeline整体通过。
`focused.ps1` 最终退出码0，包含两项BitmapFill回归及 `flash_import_regression=ok`。Adobe实际生成的 `clipped-large.fla` 用 `scripts/test-fla-import.ps1` 完成导入/保存重开/结构比较，退出码0，证据 `clamp-adobe-import-retest`；这不等于整个素材目录重新通过或整幅画面逐像素一致。

## 静态文本 bitmapSize 与当前全量复测

继续使用 assets-persistence / validate-change 流程，在隔离副本中通过 Animate JSFL 读取原始文本，12像素字号对应bitmapSize=240；把该文本字号设为36并另存text-size36.fla后，DOMTextAttrs为size=36、bitmapSize=720。证据位于 `%TEMP%\v2d-fla-audit\adobe-bitmap-oracle-001`。只改临时副本，未写源素材。导入器此前允许bitmapSize却不读取，现在size存在时优先使用size，否则bitmapSize除20，两者缺省回退12像素，再乘0.75转pt；沿用4–512pt边界。justify仍明确拒绝，未用left冒充。

`invoke-validation.ps1 -Suite Build -Plan` 退出码0，随后 `-Suite Build` 退出码0（0错误、30警告）。`pwsh -NoProfile -STA -File "$env:TEMP/v2d-fla-audit/focused.ps1"` 退出码0，新增 `flash_static_text_bitmap_size_save_reopen=ok` 验证仅有bitmapSize时字号保存重开/组合保留，非法NaN拒绝；原有Flash回归全部通过。当前未重复声明完整Timeline/Render通过。

使用 `pwsh -NoProfile -STA -File scripts/test-fla-import.ps1 -Source 'D:\Animations\madness combat resources\madness combat resources\namek_flix sprite sheet wow' -OutputDirectory "$env:TEMP/v2d-fla-audit/bitmap-text-current-full"` 启动454份全量复测。运行期间不再构建或变更二进制，最终结果待扫描完成后记录；不能将未完成扫描作为通过统计。

进一步只读定位idk.fla库路径：Include解析后的href为`&#038#038&#038#035032jhblikjnjn'.xml`，ZIP实际键为`LIBRARY/&#038&#035032jhblikjnjn'.xml`。存在额外Adobe名称转义层差异，尚未实现回退；需要核对Adobe行为及冲突/路径安全后处理，不做泛化重复HTML解码。检查脚本为`%TEMP%\v2d-fla-audit/inspect-idk-paths.ps1`。

## Adobe 库名额外转义与缺失引用区分（实现待构建验证）

按 assets-persistence 流程只读核对7份此前报Missing XFL entry的容器。`idk.fla` 的 Include 路径移除一层字面 `&#038` 转义后存在对应ZIP条目，Include与该DOMSymbolItem的itemID均为`65595c6f-00002fa6`。Adobe Animate可打开原样隔离副本、列出库并另存，状态SUCCEEDED；原素材和原样副本事后SHA256均为`18AABDE753610BF66177BAFEC7D6BBE339AA123DE716B989CB805D60023189DD`。证据 `adobe-path-oracle-001`，包括JSFL、items.tsv、resaved.fla和hashes-after.json。Adobe另存的容器还保留旧条目并新增转义条目，不能把重复另存的所有条目混为同一别名。

其余6份共56个缺失Include，在实际LIBRARY XML根节点建立的itemID索引中没有唯一匹配：NEXUS_CREW_UPDATES_V.0.1（11）、Random_Ocs_Providence_makesPub（5）、remade_old_sprites（3）、thugs（2）、Classic_Madness_Weapons（3）、weapons_from_whitehank_pack（32）。后续重读明细更正：其中18个完全无匹配，38个有多个同ID候选；先前汇总错误地把“没有唯一匹配”写成“找不到匹配项”，不能据此认定数据全部缺失。尚未删除引用或补造空元件。证据all-missing-includes.json、missing-include-id-index.json。

源码新增精确路径优先、仅一层ampersand转义的受限回退，必须非空itemID一致；新增回归覆盖保存重开、精确路径优先、不同ID、缺ID、禁止递归解码。修改为 `FlashDocumentImporter.cs`、`Benchmark.FlashEmptyTimeline.cs`，用户指南同步中英说明。**本项尚未构建/运行回归，也尚未声称idk整文件通过**：当前454份扫描仍占用验证资源及固定程序集，结束后才执行构建及定向回归。

当前全量扫描固定程序集SHA256为`50EA8693643B5E6EAB5BB17B96079EB30164B20914B22E8F6F9E9CB574370DC1`，包含此前bitmapSize修复，不包含本节尚未构建的库名修复。扫描session34600仍live，已输出96/454；没有重启、替换程序集或把中间结果当最终统计。

另外只读定位3个Mixed XML namespaces首阻断：AAHW KRinkels、scrapeface-and-mc6-hank和CS6 Drag and Drop Sample的外来命名空间均来自Adobe TLF TextFlow（http://ns.adobe.com/textLayout/2008），而非任意XML命名空间损坏。TLF目前仍需完整文本布局支持，未通过去除namespace或丢弃TextFlow来放行。脚本inspect-foreign-namespaces.ps1。

## 失效 Include 的 Adobe 实际行为与引用完整性（实现待验证）

已用Adobe批量打开上述6份原样隔离副本、导出库名列表并另存，每份status均SUCCEEDED；原始文件和原样副本事后哈希均与开始记录一致。证据`%TEMP%\v2d-fla-audit\adobe-missing-library-oracle-001`，包含inventory.json、hashes-after.json、items-*.tsv、resaved-*.fla、JSFL、adobe-include-differences.json。source-2和source-6遇到缺失字体，接受系统默认字体仅用于隔离副本；source-5遇到AS2不再支持和保存会丢失AS2的警告，仅对临时另存副本继续。这不是脚本、字体或画面无损验收。

`remade_old_sprites.fla` 原Include583条、Adobe另存580条，明确移除三条不存在的清单记录。其他5份清单数量保持原值，但包括转义路径重写、重复itemID重新分配；ID的集合差异不等于实体数量差异，不能据此认定只删除了失效条目。源容器所有实际DOMSymbolInstance名称对照全部现存XML定义，6份均无悬空名称；该只读图包含容器全部定义，不等于当前导入器已经通过。

更正前轮汇总：6份共56个缺失Include中，18个无同ID定义、38个存在多个同ID候选，没有唯一匹配。具体为NEXUS：5无/6多，Random_Ocs：5无，remade：3无，thugs：2多，Classic：3无，whitehank：2无/30多。前轮“找不到对应ID”的说法不准确，现已修正文档并保留更正说明。原始missing-include-id-index.json一直保留实际候选列表，修正汇总另存missing-include-id-summary-corrected.json。

源码在单层转义解析之外，新增不可用Include记录收集，仅在ValidateReferences验证所有场景、全部库定义（包括未放置元件）的完整引用图后报告`library-stale-include`。不按重复ID猜测文件，不创造空元件。缺失主场景引用或未放置库内嵌套引用仍使导入失败。Include总数也在解析前检查16384上限，避免跳过不可用条目后绕开库清单预算。新增用例保留可见红色形状并验证保存重开/组合数量，以及两个缺失引用反例。**本项及上一轮转义解析尚未构建验证**，等待session34600的454份固定二进制扫描结束再串行构建、运行定向回归与真实7份复测。当前扫描已输出218/454，仍live；没有声称新源码已被这轮全量扫描覆盖。

## 454份全量复测完成与库路径修改验证

session34600正常结束，退出码1，表示454份中仍有失败。`bitmap-text-current-full/summary.json`显示128份ok/compare、326份failed/import，UnchangedBuild=true，程序集SHA256为`50EA8693643B5E6EAB5BB17B96079EB30164B20914B22E8F6F9E9CB574370DC1`。相对current-build-full的117份通过，新增11份、退化0份。新增通过名单见newly-passing.csv：cassiansprites2022、hands、Kanp_private_pack_sprites、raffsprites2021、Salterino_Sprites、ClassicGunsPack-V4_1、E45_WEAPONS_PACK_1.0、Fender-Mustang-Bye45、GunSheet_BoohzuhV9、恢复_Salterino_Sprites、MuzzleSmokeTryNo2。首次失败消息聚合见failure-messages.csv；136份仍为旧二进制格式，其余190份为ZIP内容/实现阻断。没有把本轮之后的源码修改算入此固定构建统计。

扫描结束后，按此前Build -Plan执行Build，退出码0，0错误30警告。`pwsh -NoProfile -STA -File "$env:TEMP/v2d-fla-audit/focused.ps1"`退出码0，新增两项`flash_adobe_library_escape_identity_exact_precedence_save_reopen=ok`及`flash_stale_include_complete_reference_validation_save_reopen=ok`通过，包含原Flash回归全部输出。后者同时验证不可用Include的路径校验、清单数量预算及未放置库中的缺失引用拒绝。当前库路径源码已不再处于“未构建验证”状态；此前章节的待验证描述为当时状态。

真实7份路径阻断复测`retest-library-paths.ps1`已完成，包装脚本退出码0仅代表检查完成，结果1通过、6失败，固定程序集SHA256为`4F8B1062E0EE77D229047D877D315C91D0D99D74E8237FE4220336258C475187`、UnchangedBuild=true。`thugs.fla`导入/保存重开/结构比较通过（5帧），报告两条不可用Include，不创建替代元件；未完成与Adobe的逐像素/编辑交互验收。其余文件原Missing XFL entry阻断解除，但仍未整文件通过：idk、Random_Ocs_Providence_makesPub、Classic_Madness_Weapons进入非SolidStroke阻断；NEXUS_CREW_UPDATES_V.0.1进入重复库元件名阻断；remade_old_sprites和weapons_from_whitehank_pack进入尚未支持的无损位图编码阻断。逐项证据在library-path-real-retest/results.csv及source-*/case-0001/result.json。

完整Timeline/Render此前的不通过或未结束状态未被本轮定向结果覆盖；新库路径构建尚未再跑454份全量扫描。总体导入和Adobe功能对齐仍未完成。

## 03/03 索引位图支持与 Adobe 像素验收

当前8份被无损编码阻断的真实文件（包括库路径修复后新暴露的2份）全部包含03/03索引位图。只读提取结果bitmap-encodings.json，涉及16张库图记录（部分文件重复使用相同图像）。选4份原样隔离副本、10张不同库图建立`adobe-indexed-bitmap-oracle-001`：gabriel barch sprites的Tweeker、remade_old_sprites的NewgroundsTank.gif、weapons_from_whitehank_pack的6张图、eastlys-muzzle-flash-new的2张图。Adobe JSFL逐项导出PNG，4份状态均SUCCEEDED；source-1遇到AS2不再支持提示、source-3遇到字体替代提示，只对临时副本继续并不保存，原件与原样副本事后哈希均未变化。

格式证据：03/03的公共宽高/舞台边界与03/05相同；row stride为按4对齐的每行索引字节数；offset25的LE ushort是1–256的调色板项数，offset27起为原始ARGB调色板，其后为压缩标志1、LE ushort长度分块zlib及0长度终止。样本覆盖128×128/28色、154×154/16色且stride156、670×464/69色且stride672、1366×764/256色且stride1368、512×278/173色；后两图调色板有部分透明色。独立读取并与Adobe导出对照的10图共2,688,940个可见像素，Alpha差异0、重新预乘颜色通道差异0；其中两张半透明图的规范化直通RGB有3095个通道差异、最大1，其余8图直通RGB也完全一致。证据adobe-pixel-comparison.json、encoded-*.bin、bitmap-*.png/BGRA、item-*.xml及inventory.json。

产品`WriteLosslessBitmap`支持该已核对格式，逐行读索引并展开为现有ARGB处理，不额外分配整幅索引缓冲，也不扩大现有图片尺寸/64M像素预算。行尾填充不参与可见索引校验；可见索引须小于paletteCount。调色板项数、row stride、边界、压缩模式、分块终止、解压长度及实际预乘颜色继续校验；截断解码数据明确为带位置的InvalidDataException。保留原PNG匹配与预乘颜色舍入规则。

新增`Benchmark.FlashIndexedBitmap.cs`并接入原Flash回归：覆盖透明/半透明/不透明、256项和index255、无alpha时忽略调色板alpha、跨行/padding、删除临时图片后保存重开及held exposure组合；反例包括非法可见索引、截断/超量解压数据、非法预乘调色板颜色、错stride、0/257项、截断调色板和未知压缩方式。用户指南中英参数与边界同步更新。

验证按Build -Plan后Build，退出码0（0错误30警告）；Timeline -NoBuild -Plan后运行focused.ps1，退出码0，新增`flash_indexed_bitmap_palette_padding_alpha_256_save_reopen_rejection=ok`及原Flash回归全部通过。再运行native-indexed-oracle.ps1，使用实际产品私有解码器对10图各比较“允许源PNG匹配”与“删除href强制内嵌解码”两条路径，共20次/5,377,880个像素比较，AlphaMismatch=0、PremultipliedMismatch=0、最大直通RGB差异1，退出码0。记录native-oracle-comparison.json；这是位图像素验收，不是整份FLA的排版、滤镜或交互视觉验收。

`retest-indexed-bitmaps.ps1`完成8份定向复测，包装脚本退出码0只代表扫描完成，2份ok/compare、6份failed/import，程序集SHA256=`553951C15C4096C0EBFDE78AAF4DB3FA6DA3B71A992C261FB6226DD92B9212FA`，UnchangedBuild=true。通过hudson-h9-pistol和eastlys-muzzle-flash-new；其余后续阻断为：gabriel barch sprites的经典补间端点/旋转/缓动，两个eastlys reused的MorphShape，MADNESS STREAK 6的mask，remade_old_sprites的hairline，whitehank的pixel-hinted stroke。结果在indexed-bitmap-real-retest/results.csv和source-*/case-0001/result.json。未把2份定向通过加到旧构建128份并冒充最新454份全量统计；完整Timeline/Render仍沿用此前未通过/未完成状态。整体功能对齐仍在进行。

## 花式描边的 Adobe 对照与视觉桥容器阻断（继续检查中）

本轮使用 `survey-stroke-styles.ps1` 检查当前首阻断为非实线的去重素材及全部库 XML，发现28条DashedStroke、2条DottedStroke和134条RaggedStroke记录；这些是该候选集的XML记录数，包含同一素材内重复样式，不是全素材文件数。没有把花式描边替换成实线后宣称支持。

Adobe对照证据在 `%TEMP%\v2d-fla-audit\adobe-stroke-oracle-001`。合成文档使用6条200像素水平线，分别测试Dashed的weight=1/4/0.25、dash1/dash2=4/2或1/1、端点none/round，以及Dotted的weight=1/4、dotSpace=3；包含原生FLA、DOMDocument.xml、SVG、PNG和放大白底预览。实际Duel_Background副本中的缺省DashedStroke，Adobe返回dash1=6/dash2=6；DottedStroke返回dotSpace=3、thickness=1.1。合成样本的粗虚线内部圆端会覆盖间隙，Dotted的点中心间距也随线宽变化，不能仅按同名SVG参数猜测。曲线弧长、拐角、端点和Ragged几何仍未实现/验收。Eastlys混合选择返回style=solid，不能据此认定其Ragged样式已经转成实线。两个原素材与隔离副本的事后SHA256均未变；Eastlys打开时只在临时副本接受缺失LivingHell字体的系统替代，该图不能用于字体视觉一致性结论。

另外定位视觉桥 `FlashVisualConversionBridge.Prepare` 仍直接使用标准ZipArchive，未复用直接导入器已有的容器校验，并只接受精确根目录DOMDocument.xml。进一步对454份文件做只读ZIP元数据/精确根路径检查：318份ZIP均未出现此层阻断；当前.NET也接受合成的已知目录长度变体，Duel_Background的旧版Prepare实际成功。因此不能把“使用标准ZipArchive”本身说成已复现的真实素材失败；此项主要统一入口的根定位及安全校验。源码已改为通过XflSource的有界根文档读取，共用容器、条目、根定位和路径校验；桥自身仍保留32 MiB根XML预算、Adobe根namespace、DTD禁止和深度限制，不调用原生功能解析，因此不误拒绝交给Adobe处理的TLF外来namespace。新增回归覆盖已知mimetype目录差异/损坏反例、根大小写/目录、TLF、重复根/条目、DTD及原始字节保留。此项**尚未构建/运行**，当前固定程序集全量扫描结束后串行验证，不能把源码修改算入当前扫描。

当前新一轮454份全量扫描使用此前已验证的索引位图/库路径程序集，SHA256为`553951C15C4096C0EBFDE78AAF4DB3FA6DA3B71A992C261FB6226DD92B9212FA`，输出 `indexed-library-current-full`。扫描尚在运行；最终计数以summary.json为准，不把中间进度或旧128份计数作为本轮结果。

## 重复 Include 与真正同名冲突的区分（后续验证见末节）

只读遍历四份当前出现Duplicate library symbol name的素材，证据 `duplicate-library-payloads.json` 与脚本 `survey-duplicate-payloads.ps1`。EXTREMEGOREPACK和XPack各1组重复名，均是清单两次引用完全相同的LIBRARY路径，读取的完整XML相同；EXTREME的两条Include itemID分别为619ccec2-00000dbf和5f36ad2a-0000151f，实际根itemID为后者。XPack重复Include的itemID也一致。Hank_Kills_Sheet有49组、NEXUS有2组重复名，完整XML及timeline均不一致，不能通过任取第一项合并。

源码新增按已解析精确路径记录已读定义；仅同一路径且完整XML DeepEquals时保留一个定义，并报告`library-repeated-include`及当前Include/root itemID。不用重复或陈旧的清单ID推测替代路径；不同路径的同名定义仍拒绝，即便内容相同也不合并。原16384条Include上限在解析前检查，不因去重而放宽。新增Benchmark.FlashDuplicateIncludes.cs接入实际Flash回归，覆盖冗余ID、可编辑形状/曝光、保存重开稳定ID、原始字节不变，以及不同路径同名的相同/不同内容两个拒绝反例。当前仍等待固定程序集454份扫描结束后构建验证，不声称两份实际素材已经整文件通过。

Adobe进一步打开EXTREMEGOREPACK与XPack的原样隔离副本并导出完整库列表，两份均SUCCEEDED；按Ordinal精确比较，各自目标名称在Adobe库列表中均只出现1项。证据adobe-repeated-include-oracle-001的inventory.json、items-*.tsv、library-observations.json和hashes-after.json。原始/副本SHA256均未变；EXTREME临时打开时接受缺失Helvetica字体替代，不用于字体或画面无损结论。名称精确计数使用StringComparison.Ordinal，避免PowerShell文化比较将带控制字符的不同名称视为相等。4份去重调查也改用Ordinal字典复核，1/49/2/1组结果不变。

## 索引位图/库路径构建的454份全量复测完成

`pwsh -NoProfile -STA -File scripts/test-fla-import.ps1 -Source <素材根目录> -OutputDirectory "$env:TEMP/v2d-fla-audit/indexed-library-current-full"` 正常结束，退出码1（存在失败）。454份中131份ok/compare、323份failed/import，未出现save/reopen/compare阶段失败。固定程序集553951C15C4096C0EBFDE78AAF4DB3FA6DA3B71A992C261FB6226DD92B9212FA在扫描前后相同。比较脚本compare-indexed-library-full.ps1确认：相对上一轮128份，新增thugs、hudson-h9-pistol和eastlys-muzzle-flash-new，退化0、缺少路径0。证据summary.json、comparison.json、newly-passing.csv、regressions.csv和failure-messages.csv。完整结构比较不是完整几何逐值、编辑或Adobe画面一致性验收。新增重复Include及视觉桥源码修改不在本轮程序集内；全量扫描结束后才启动其构建。

## 重复 Include 与视觉桥修改的构建、回归及真实复测

454份扫描结束后，按Build -Plan执行Build，0错误/30条既有警告、退出码0；`pwsh -NoProfile -STA -File "$env:TEMP/v2d-fla-audit/focused.ps1"`运行完整实际Flash回归，退出码0，包括`flash_repeated_include_identity_save_reopen_distinct_path_rejection=ok`与`flash_visual_archive_roots_foreign_namespaces_ambiguity_dtd=ok`。其中重复Include的首次/重复/实际元件itemID均保留在诊断中；补充首次ID后再次Build -Plan/Build和同一Flash专项均通过。最终程序集SHA256=`D362013AB90039437C33E2888AF7164B6CC3C18D6F36EECADA65A031689F45AD`，日志`focused-repeated-include-visual-final.log`。上文“尚未构建”的段落为当时历史状态，此处覆盖当前验收状态。

`retest-repeated-includes.ps1`对4份真实素材复测完成，结果0份通过/4份failed/import；包装脚本退出0表示扫描完成，不是产品全部通过。固定程序集C56D93ED6F14F7105AB7AFE4B8604007D9369A3C7D8E6013B9C1EE438E0BC6A8（最终首次ID诊断补充前的构建），UnchangedBuild=true。EXTREMEGOREPACK已越过重复Include，后续阻断为blood9/Layer_1/frame 1的Unstyled XFL edge；XPack已越过重复Include，后续阻断为&#032blood splurt 3/Layer 1/frame 1的同类边线。Hank_Kills_Sheet及NEXUS仍为不同路径同名定义冲突，没有任取定义放行。证据repeated-include-real-retest/results.csv与source-*/case-0001/result.json。最终诊断补充只改变首次ID的报告，未把旧构建实际4份结果标成最终程序集复测。

同名不同内容另做只读分类：仅规范化无描边DOMShape的Edge元素顺序后，NEXUS的2组中1组完全一致，另一组仍不同；Hank的49组仍均不同。证据duplicate-library-fill-permutation.json，不据此改动选择规则，也未声明已解除这些冲突。所有名称字典与精确计数已用Ordinal复核。

`git diff --check`退出0，仅提示已有Directory.Build.props的LF/CRLF转换警告。未暂存、提交、改版本或生成launcher；原有大量脏工作树保留。尚未完成当前构建的完整Timeline/Render验收、全部不支持功能和Adobe整文件画面/编辑语义对齐；专项Flash通过不能替代这些未完成验收。下一步继续核对真实素材无样式边线的Adobe显示语义，随后处理剩余库名冲突及花式描边等阻断。

## 无样式 XFL Edge 的 Adobe 对照

`EXTREMEGOREPACK` 与 `XPack` 的后续首阻断是没有 `fillStyle0`、`fillStyle1` 或 `strokeStyle` 的 Edge。Adobe Animate 隔离副本导出 `blood9` 与 `&#032blood splurt 3` 的 PNG/SVG：这些 construction edge 不产生可见像素；同一元件中带 `fillStyle1` 的边仍正常可见。产品原先遇到任意无样式 Edge 就拒绝，和 Adobe 结果不一致。已改为忽略无填充/无描边的 Edge；带样式但缺少实际 edge stream 仍拒绝，避免凭空生成线条。证据 `adobe-unstyled-oracle-001`，两份原素材打开/隔离副本哈希未改写。该修改尚待构建及真实复测。

复核更正：上述PNG/SVG只证明所选元件的导出结果，尚未逐边对应；XPack还存在多个转义不同但根名称相近的库条目，当前JSFL名称可能选择了不同定义。因此“这些Edge不产生像素”的结论证据不足，已撤回本轮忽略Edge的代码修改，保留明确拒绝，等待精确条目与修改前后像素差分后再实现。不得将本节早先推断当作已确认Adobe规则。

## 精确条目隔离对照准备与当前构建核对

本轮从EXTREME的LIBRARY/blood9.xml及XPack的LIBRARY/&#038&#035032blood splurt 3.xml提取实际带无样式Edge的DOMShape，生成保留/删除该Edge两组隔离夹具。首次简化ZIP FLA和简化未压缩XFL均被Adobe拒绝打开，尚无可比较PNG，不能据此判定Edge显示规则。脚本prepare-unstyled-isolated.ps1、inventory.json及Adobe状态位于adobe-unstyled-isolated-001；下一步应以Adobe生成的有效文档为模板替换形状，而不是继续将简化容器当有效Adobe夹具。

撤回忽略Edge修改后重新执行Build -Plan/Build，退出0、0错误30警告；当前源码继续明确拒绝无样式Edge。此前43713构建可能在撤回前读取源文件，因此不作为当前二进制证据；重新构建41834才对应撤回后的状态。git diff --check退出0，只有既有Directory.Build.props行尾提示。本轮没有新的产品兼容性通过结论，目标仍未完成。

## 二次缓动映射与真实素材复测

新增quadIn、quadOut、quadInOut映射，分别使用t²、t(2−t)及分段2t²/1−2(1−t)²。依据本机Adobe Animate 2024随附fl/motion/easing/Quadratic.as和Publish/Canvas/libs/1.0.0/createjs.min.js的二次缓动定义；网络文档工具返回服务错误，没有将其当成已获取证据。沿用单条Ease、target=all和既有端点/变换限制。新增回归检查三种缓动的1/3与2/3时刻、保存重开位置及末帧组合边界。

Build -Plan后Build退出0（0错误30警告），focused.ps1实际Flash回归退出0，包含flash_quadratic_ease_baked_positions_save_reopen_endpoint=ok及flash_import_regression=ok。当前程序集SHA256为52B44E87AB9276B2B2AA9A624FD1B837F8FAFEB99975E90E097D170508549E9F。三份实际quadInOut首阻断素材定向复测均越过缓动拒绝，随后scene14、scene15、scene17分别在llllllllll、BILL BARSCH HEAD 4animated和Symbol 13的mask层停止。结果0通过/3导入失败，执行退出1；证据quadratic-real-retest/source-*/case-0001/result.json，不能将其算成整文件通过。完整Timeline/Render和Adobe整文件视觉仍未通过验收。

无样式Edge隔离对照改用Adobe生成strokes.fla模板后四份成功导出。256×220像素下，case0保留180可见像素，删除Edge前后差异0；case1两者均透明、差异0。记录adobe-unstyled-isolated-001/pixel-comparison.json；阳性对照未验证，因此仍保留无样式Edge拒绝。补加填色并启动Adobe的调用被自动审批以blocked by policy拒绝，未重复该操作。未据此作兼容性结论。

## 遮罩绑定调查与实现前置契约

只读调查当前全量结果中首阻断为mask的素材，遍历其中实际可读取DOMTimeline；脚本survey-mask-bindings.ps1，原始记录mask-bindings-survey.json。共256条mask记录：0内容层1条、1内容层213条、2层26条、3层4条、4层1条、5层1条、6层3条、8层4条、10/11/13层各1条。这是候选文件内所有可读取时间轴的记录数，不是全素材功能覆盖或成功数；解析失败条目跳过，统计范围有限。

源码确认多内容层是实际需求：VectorScene.SetLayerMask会清除同遮罩其它链接；NormalizeLayerMaskIds的claimedMaskIds在重开时只保留首条；GetMaskContentLayerIndex及图层显示排序只主动取一个内容层。RestoreNativeScene分组复制漏LayerMaskIds，且组合需要保留父实例遮罩对嵌套叶节点的约束。不能通过只允许mask层类型来声明支持。实施需同步模型绑定/归一化、顺序与UI调用、场景分组、嵌套组合、保存重开及GDI/Direct2D渲染验证。当前没有对此批既有核心脏文件做局部放行，遮罩仍明确拒绝；下一步沿这些契约实现完整链路。

## 遮罩渲染与导出语义的进一步检查

当前GDI StageControl.GdiSceneRendering.DrawLayerObjects和Direct2DStageRenderer.DrawLayerObjects均以内容层TryGetMaskLayerIndex查找遮罩并几何裁剪；二者不依赖GetMaskContentLayerIndex，因此多个内容层可分别复用同一遮罩几何。GetMaskContentLayerIndex的单值假设主要还出现在显示顺序、UI操作和回归夹具，不能直接删除而不更新调用方。

另发现必须核对的锁定语义差异：VectorScene.ShouldRenderLayerContent对Mask返回!IsLayerEffectivelyLocked，锁定遮罩自身不显示；舞台内容层裁剪只检查遮罩可见性，不检查锁定。DrawingObjectSvgExport.TryGetUsableMask却在mask LayerLocked为true时返回false，导出行为可能和舞台不同。须以Adobe锁定/解锁样本核对并同步导出路径，不能用Stage局部通过证明SVG/保存/嵌套组合一致。当前尚未更改这些规则，未声称已支持FLA遮罩。

核心文件有既有暂存修改：VectorScene.LayersAndTimeline.cs（2增1删）和SceneCompositionBuilder.cs（2增2删），已核对所有权并保留。后续实现不能覆盖这些基线。此轮为只读调用链核对与证据补充，未运行产品回归。

## 共享遮罩模型基础实现与专项验证

VectorScene新增内部SetSharedLayerMask，共用SetLayerMaskCore；原SetLayerMask仍为独占绑定以保留现有UI行为。共享入口不清除同mask其它内容层。NormalizeLayerMaskIds保留多个有效内容层的相同mask引用，仍校验内容/遮罩类型和ID；GetLayerDisplayOrder在mask后依源层顺序追加全部关联内容层。尚未接入FLA解析，也没有改变导入mask拒绝，待补嵌套组合和渲染/导出契约。

新增Benchmark.FlashSharedMasks.cs并接入实际Flash回归，覆盖两个内容层共享、快照恢复、ProjectVaultStore保存重开、显示顺序以及旧独占入口清除其它绑定。第一次夹具误用AddMaskLayer插入前索引导致失败，修正为其自动绑定的层1及新增层2。Build -Plan后Build退出0（0错误30警告），修正夹具后重建退出0；focused.ps1最终退出0，含flash_shared_mask_model_snapshot_save_reopen_display_order=ok及flash_import_regression=ok。未执行本轮完整Timeline/Render，不能由此认定FLA遮罩支持完成。既有暂存核心修改保留。

## 嵌套遮罩组合的具体丢失位置

本轮定向读取SceneCompositionBuilder.CollectDrawingObjectLayers：每个sourceLayer构造localLayer后递归收集activeChildren；只有blend/parent/opacity条件会建立SyntheticGroup，mask关系不触发宿主组。递归调用传递ParentGroupId但没有宿主mask约束。输出阶段destinationLayerBySource以(Source, Owner.InstanceId)分组，仅按每个叶层自身Source.LayerMaskIds恢复，因此宿主内容层的mask不会传到来自其它Source的子叶层。宿主mask层若包含子实例，其子叶层也不进入该Mask源层的几何集合。FLA导入将视觉叶节点做成引用元件，故这两侧均是必须修复的实际路径，不能靠空实例或本地直接几何夹具通过声称完成。

接下来的回归必须包含：遮罩本身由嵌套实例构成、两个内容层各有嵌套实例、外层变换、保持曝光、嵌套自身另有遮罩（应为裁剪交集），保存重开后用GDI/Direct2D及SVG比较。直接给子叶层覆盖一个LayerMaskIds会丢失内部遮罩，不采用该实现。当前未修改组合路径，FLA mask拒绝仍保留；已有共享模型回归只证明绑定/快照/保存/显示顺序，不覆盖此处。核心既有diff保持原样。

## 完整Timeline对共享遮罩契约的验证

执行Timeline -NoBuild -Plan后运行同套件，session99425退出1。Flash全部回归与共享模型用例通过，随后RunTimelineLayerWorkflowRegression在Benchmark.TimelineSuite.cs:4302失败：旧用例要求快照恢复删除第二个有效mask绑定。此断言与新共享遮罩契约冲突，现更新为保留2条有效引用并维持原显示顺序，后续独占绑定/移动检查未删除。Build -Plan/Build退出0（0错误30警告）；完整Timeline重跑session83000最终退出0，含timeline_regression=ok。该通过对应场景映射及SVG导出后续修改之前的程序集。产品仍未开放FLA mask导入。

## 场景遮罩分组映射及SVG导出修复

RestoreNativeScene原先按无Folder父层枚举根，遮罩内容层也被当成独立根；ResolveLayerRemovalIndices又会把mask与全部关联内容放入同组，造成重复场景放置。现排除已绑定遮罩的内容层根，Mask本身始终保留为可编辑组，并补复制LayerMaskIds。Benchmark.FlashSharedMasks新增反射调用真实映射入口的用例：遮罩和两个内容层各放一个不同引用元件，另有一个普通层；映射及ProjectVaultStore保存重开后应只有两个场景层/两个根实例，三个关联引用均只在遮罩组内，两个内容层保留相同mask引用。Build -Plan/Build退出0（0错误30警告）；focused.ps1退出0，含flash_shared_mask_scene_mapping_unique_placements_save_reopen=ok及flash_import_regression=ok。此测试验证分组及引用，不证明嵌套几何已正确裁剪。

DrawingObjectSvgExport修复共享mask重复生成相同SVG ID，以及锁定mask错误关闭裁剪。隐藏、空或空白曝光mask保留绑定；没有当前可用遮罩对象时省略受遮罩内容组，避免渲染库忽略空mask后露出内容。首次完整Timeline重跑session83967退出1，新像素断言实际发现“A hidden mask leaked visible pixels”；仅添加空mask定义不足以修复当前SVG库，随后补充省略内容组。Benchmark.SymbolSvgExport新增唯一mask定义/两个引用、锁定后viewport与非零像素、隐藏和空白曝光透明像素检查。最终Build -Plan/Build退出0（0错误30警告），程序集SHA256=9CC1E7313525EE7EDEE8139C9C470D923A3F156632900F96DF7B657763B37D57；pwsh -NoProfile -STA -File "$env:TEMP/v2d-fla-audit/focused-svg.ps1"退出0，含symbol_svg_shared_locked_hidden_blank_mask_raster=ok。随后按Timeline -NoBuild -Plan运行完整Timeline session22451，最终退出0，含新增场景映射及SVG像素标记、flash_import_regression=ok和timeline_regression=ok。没有据此声明Render或Adobe整文件视觉验收通过。

用户指南同步说明锁定/空/隐藏/共享mask的SVG行为、共享引用保存、UI独占绑定以及FLA mask仍未支持。此轮未改SceneCompositionBuilder和GDI/Direct2D渲染；嵌套遮罩几何汇集及内外遮罩交集仍是实际阻断，没有启用mask解析，也没有重新计算454份成功总数。此前131通过/323失败为旧固定构建全量结果，不代表当前程序集全量验收。既有脏工作树保留，无暂存、提交或发布。

## 重复嵌套放置的遮罩串绑与组合裁剪信息

复核发现SceneCompositionBuilder以(Source, Owner.InstanceId)为键恢复图层引用，但库内子实例ID会随父元件重复放置而复用；即使两个场景根不同，或同一根经不同嵌套分支进入同一库实例，原键也会相同。新增Benchmark.FlashNestedMasks.cs：两个嵌套分支×两个场景根，最终四次出现各有两个内容层共享一个本地mask。修复前专项实际退出1，报“A repeated symbol was clipped by another branch's transformed mask”。最初PowerShell临时探针还因参数PSObject未转string而失败，修正探针后才取得此产品失败证据。修复后改按每次CollectDrawingObjectLayers遍历的OccurrenceId恢复引用，保留原Owner.InstanceId/RootInstanceId等可编辑来源及持久化ID。合成组和Break Apart的中间表示也保留OccurrenceId；已有暂存EditorHidden检查保持不变。

重复放置回归检查12个对象、8个内容层、4个不同遮罩组，逐一比较内容/遮罩位置与场景根来源；ProjectVaultStore保存重开后再次组合检查，并用实际SVG栅格核对四个位置均有可见像素。临时探针使用固定保存目录第二次运行时被ProjectVaultStore正确拒绝覆盖其它工程，未改存储检查；后续统一使用Flash专项自动生成的新临时目录。此回归证明原生直接几何遮罩在重复嵌套出现时的引用隔离，不等于已支持遮罩本身由嵌套元件构成。

SceneCompositionBuilder新增partial Masks.cs和SceneCompositionMaskPlan，按出现位置生成ClipsByLayer（各项求交）、ContentsByMaskLayer（各子层依自身裁剪后求并）、ContainersByLayer（遮罩子树归属）。递归传递宿主mask约束和mask子树身份，内层mask不被外层引用覆盖；合成组保留同样约束，供后续在滤镜/隔离组边界正确裁剪。结果只含派生层索引，未改变工程文件格式或源稳定ID；保存重开后从源图重新构建。无mask时返回共享空计划，构造成本计入FinalizeMilliseconds。已有排除锁定层的选择/洋葱皮路径保留原排除行为。

新增嵌套计划回归：外层mask由引用元件提供几何，两内容层各含有自身mask的子元件；检查两层均继承同一外mask并保留各自内mask、遮罩子层归属、根位移(100,50)叠加遮罩实例位移(7,0)、保持曝光、空白mask曝光及保存重开。内外遮罩几何设置为部分重叠，以便后续渲染验收区分交集与覆盖。当前断言仅检查组合计划和几何来源，尚未检查这个交集的实际渲染像素。

Build -Plan后Build均退出0，0错误/30条既有警告。focused.ps1完整Flash专项退出0，含flash_repeated_masked_occurrence_branch_root_save_reopen=ok、flash_nested_mask_plan_shared_inner_outer_geometry_held_blank_save_reopen=ok及flash_import_regression=ok。最终补齐部分重叠夹具、派生信息复制及计时后再次Build -Plan/Build退出0，最终程序集SHA256=D3CD0B239BC3E1F4FE1684139AF8A6182290F8DF5094C3E587A0985E67C9A681。按Timeline,Render -NoBuild -Plan串行运行完整验证session78010：Timeline完成且timeline_regression=ok，包含两项新增Flash回归；Render在Benchmark.StageInteraction.cs:539的Returning from 3D did not restore the 2D Transform tool, view pad, and hidden-tool state断言失败，整次脚本退出1。该失败与此前记录位置和消息相同，但未用隔离基线证明因果，不能宣称Render通过或所有问题均来自既有改动。git diff --check退出0，仅有既有及相关文件的行尾转换提示。

尚未把新计划接入GDI/Direct2D、参考3D、SVG导出及选择裁剪；尤其需要在合成组滤镜边界应用宿主mask，汇集mask子实例的真实几何，保留曲线/孔洞及各自空间投影，再验证缓存和空遮罩。现有SceneCompositionMaskClip可对对象叠加裁剪，但目前来自UI的场景mask，且二维路径也经过轮廓多边形；不能直接接上并声称保留了原生贝塞尔精度。FLA mask解析仍明确拒绝，完整454份未重跑，整文件Adobe对照未通过，目标保持未完成。

## Adobe官方遮罩规则的重新核对

2026-10-30读取[Adobe官方Using mask layers](https://helpx.adobe.com/animate/desktop/animation/using-mask-layers.html)当前正文（页面标注2026-06-09更新）：支持多个内容层共用mask，mask项目可以是填色形状、文字或元件实例；创建步骤明确忽略mask中的位图、渐变、透明度、颜色和线型，并要求锁定mask及内容层以在编辑器显示效果。该说明用于补全验收约束，尚未将原生编辑器锁定规则直接改成Adobe规则。文档还限制直接mask另一mask及mask上的3D工具；这不能替代嵌套元件内外mask的实际Adobe对照。滤镜组边界、描边不参与mask及位图忽略仍须在FLA映射/渲染接入时验证，不能用现有通用原生遮罩规则代替。

## 2026-10-30：嵌套元件 SVG 导出与真实几何遮罩像素

本轮将 SceneCompositionMaskPlan 接入符号 SVG 导出，并将 UI 文件导出改为传入完整元件库及 FPS。组合根使用恒等变换，保留作者坐标与指定根帧；引用实例按既有曝光/播放模型求值。非活动元件的首帧选择现在同时检查本地图形与可见引用实例曝光，引用专用元件不再固定导出空白第 0 帧。空间姿态、启用滤镜的组合及隔离文件夹/Invert 继续明确拒绝；位图占位输出仍是限制，不宣称图片 SVG 已保真。

新增椭圆像素断言使先前仅矩形通过的用例实际失败：角点 Alpha=255，说明 viewport 收紧掩盖了未裁剪的问题。核对 [SVG.NET v3.4.7 Render/SetClip 源码](https://raw.githubusercontent.com/svg-net/SVG/v3.4.7/Source/Basic%20Shapes/SvgVisualElement.Drawing.cs) 与 [SvgMask 定义](https://raw.githubusercontent.com/svg-net/SVG/v3.4.7/Source/Clipping%20and%20Masking/SvgMask.cs)，依赖只解析 mask，未执行其渲染。不能把先前矩形通过解释为任意遮罩像素正确。

新增 ImportedSvgRasterizer.GeometryMasks.cs，仅对导出器显式标记 data-v2d-geometric-mask=1 的不透明几何遮罩安装 SvgGroup 裁剪。递归对遮罩子项先应用自身 mask 交集，再求并；保留 GraphicsPath 曲线、复合路径填充规则和局部变换，克隆源路径避免修改 SVG.NET 缓存。原始 SVG 仍使用标准 mask 图，标记仅供应用内弥补依赖限制；没有把一般导入 SVG 的亮度/Alpha 遮罩重新解释成几何遮罩。该路径同时覆盖旧本地遮罩导出和新组合遮罩导出，但不是 FLA 的填色专用遮罩语义实现。

导出对象与图层曾重复应用 LayerOpacity（50% 变 25%），现 export 元素不再重复写图层透明度，工程存储 preview 的默认行为保持。组合导出保留普通文件夹层级。新增专项检查实际返回栅格尺寸对应的孔洞位置，而非假定请求尺寸等于返回尺寸。

在程序集 SHA256=F333F99BBD0B2101771EA5C1A41C68999E54FA5E82D35C92439ED9353E60CD26 上，Build -Plan/Build 退出0，0错误/30既有警告；TEMP focused-nested-svg.ps1 与 focused.ps1 均退出0。已输出 flash_nested_mask_svg_intersection_curve_hole_hidden_artwork_blank_save_reopen=ok、symbol_svg_referenced_only_first_content_frame_file=ok、symbol_svg_nested_mask_child_union_curve_cache_layer_folder_opacity=ok、flash_import_regression=ok。覆盖曲线角点、孔洞、内外部分交集、遮罩自身子项裁剪后与兄弟项求并、100/200/100尺寸重用、50%图层及50%父文件夹组合、空白曝光、保存重开与真实文件Write。上述 hash 之后又补充了无引用元件的完整组合导出及对应透明度断言，后续最终验证结果另记。

仍需完成 Stage 的 GDI/Direct2D/参考3D/选择对组合遮罩计划的消费、滤镜边界裁剪和 Adobe 实际文件对照，再开放 FLA mask 解析。原454素材全量统计未更新；最新完整扫描仍为旧版本131成功/323失败，不将专项夹具通过数累加到真实素材成功数。UI导出对话框未手工点击验收。

最终补充验证：无引用元件也走完整组合导出（避免漏掉父文件夹透明度），对应新像素断言通过。最终 Build -Plan/Build退出0，程序集SHA256=3D6FB08B8A045C34E5AD75AE5B1AA015F7C88AFCCC96858E071E15EF89ABBAA6；focused.ps1完整Flash专项退出0，包含新增无引用元件断言（同symbol_svg_nested_mask_child_union_curve_cache_layer_folder_opacity标记）。此前F333F99B构建的Timeline,Render -NoBuild -Plan/运行session94157中Timeline完整通过；Render仍在Benchmark.StageInteraction.cs:539报Returning from 3D did not restore the 2D Transform tool, view pad, and hidden-tool state，脚本退出1，后续Render用例未执行。未用隔离基线证明该失败因果。最终构建未重复完整Timeline/Render，仅重跑覆盖最后入口修改的完整Flash专项。git diff --check退出0（行尾转换提示不作为失败）。

最终构建的454份全量扫描已启动，输出目录为 %TEMP%\v2d-fla-audit\nested-svg-current-full，命令为 pwsh -NoProfile -File scripts/test-fla-import.ps1 -Source <上述素材根目录> -OutputDirectory <上述新目录>。当前仍在运行（统一命令会话85750）；第61份时20份完成比较、41份在import阶段失败，无save/reopen/compare失败。此为阶段进度，不能替代最终summary.json或改写首页全量统计。扫描期间不得重建或替换程序集；结束后须检查UnchangedBuild与逐路径退化比较。

Stage后续接入定位：MainForm.cs:3352及SetSceneCompositionResult提供场景组合结果；DrawingObjectChildren下层组合绑定处尚未传递结果。StageControl.GdiSceneRendering.DrawLayerObjects旧本地mask为空时直接return，会阻止引用子树绘制；不能只追加逐对象clip。LayerBlendCompositor的滤镜/隔离组结果也需在正确的组边界接受宿主clip。现有Direct2DStageRenderer.Reference3D.DrawWithSceneCompositionMaskClips的二维分支仍经WorldContours多边形，不能直接宣称精确曲线支持。当前本轮只修复SVG消费，没有改这些Stage路径。

继续推进（扫描会话85750仍运行）：第122份时49份compare成功/73份import失败，逐路径对旧全量通过状态无变化；DLL hash仍为3D6FB08B8A045C34E5AD75AE5B1AA015F7C88AFCCC96858E071E15EF89ABBAA6。随后输出已到第124份，不能把该进度当作完成。

扫描期间新增待验证源码：SceneCompositionMaskRegions.cs为单渲染周期覆盖求值器，使用调用方提供的目标坐标GraphicsPath，单对象保留孔洞、不同对象求并、子层自身clips求交，再对mask子层求并。活动对象按层一次分桶，覆盖缓存随求值器Dispose释放，不引入跨帧缓存。Benchmark.FlashNestedMasks.cs接入同一组合夹具，检查中心/孔洞/空白/外层交集；当前尚未构建执行，也未接入Stage产品绘制。旧Stage本地遮罩把多个对象拼入同一Alternate路径可能造成重叠XOR，须通过独立像素用例修复，不能把新求值器源码存在视为Stage对齐完成。Benchmark.StageInteraction.cs仅增强既有失败断言消息，输出各状态以定位Render阻断；未放宽断言，尚未构建执行。扫描结束前不替换DLL。

扫描到第168份时66份compare成功/102份import失败，第169份GunSheet_BoohzuhV9.fla仍由独立pwsh探测进程33852处理（已检查实际进程存在），不能因观察等待未输出而重启。扫描期间新增待构建的StageControl.CompositionMasks.cs：在目标二维屏幕坐标生成单对象遮罩路径，原生椭圆使用曲线AddEllipse并叠加对象旋转、实际PlanarCanvasRotation与缩放，其他对象沿现有路径/畸变入口。新增Bezier路径类型断言及独立图元重叠求并/缓存返回副本所有权断言；尚未执行，不声称Stage已消费。文档参数核对补齐当前源码的XML 128 MiB单文件/384 MiB累计/深度128/读取节点4,000,000和视觉桥根文档32 MiB限制；未调整产品限额。

第169份GunSheet_BoohzuhV9.fla已输出ok，扫描推进到第190份：70份compare通过/120份import失败；逐路径对旧全量结果无状态变化。新增待构建的LayerBlendCompositor接口允许CompositeTo/CompositeRangeOnto接受逐层Region工厂，在每层/文件夹ApplyFilters之后裁剪，递归子层保留同一工厂，返回Region由合成器释放。没有改变默认调用的行为。Benchmark.FlashNestedMasks增加文件夹阴影边界测试：无clip时(44,24)应有阴影，clip到x<30时该点透明，而(14,24)源内容保留。该接口尚无Stage产品调用且测试未执行，不能声称已修复舞台滤镜遮罩。git diff --check退出0，仅行尾提示。

## 固定构建454份全量复测完成

会话85750已终止，脚本退出1（任意文件失败即返回1）：454份中131份ok/compare、323份failed/import，0份save/reopen/compare失败；summary.json确认UnchangedBuild=true。results.csv与上轮逐路径核对：454个唯一路径、遗漏0、状态变化0。5份错误位置变化为：EXTREMEGOREPACK.fla与XPack.fla由重复库名转为无样式Edge；scene14.fla、scene15.fla、scene17.fla由quadInOut未映射转为mask层未支持。不能把阻断后移当作整文件通过。

扫描期间准备的CreateLayerBoundaryClip会从子层裁剪中延后祖先组负责的相同遮罩，保留自身内遮罩；完整CreateLayerClip继续供遮罩覆盖求值使用。新增夹具检查完整交集、子层内遮罩与父边界各自覆盖，防止在父滤镜前错误截断输入。该接口尚未进入产品Stage调用，随后Build计划已检查并开始构建（会话6848）；验证结果另记。用于Render工具状态定位的临时脚本为%TEMP%\v2d-fla-audit\focused-render-tools.ps1。

## 新遮罩基础专项与Render阻断定位

新增遮罩基础构建首次在夹具StageControl无参构造失败（CS7036）；修正为StageControl(scene)后Build退出0（30既有警告/0错误）。6A8276E003E229BD1582259A45383B483018EEB4B1EA18447FF0852C1E5C3D1A构建的focused-nested-svg.ps1退出0，包含flash_mask_region_overlap_union_owned_cache=ok、flash_mask_group_clip_after_shadow_filter=ok、flash_mask_parent_filter_boundary_deferred_inner_retained=ok及全部既有嵌套SVG断言。这仅证明覆盖计算器、Stage对象路径及合成器回调夹具正确；产品Stage尚未接入这些基础。

增强原Render失败消息后，focused-render-tools.ps1退出1：维度2D、工具Transform、面板/视图垫/工具列表均正确，但spatialFront=False、referenceProjection=False、effectiveProjection=Perspective。定位为场景绘图快捷键进入绘图源场景后，维度切换未重新绑定组合上下文。MainForm.ToggleSceneDimension与CodexSpatialSetSceneDimension现同步调用RebuildSceneComposition(refreshScenePanels:false)，沿已有上下文选择规则重绑；用户指南新增中英文说明。原维度断言随后通过，专项前进到Front方向工具列表断言，诊断为direction=Front/spatialFront=True但flyout.Visible=False/tools=[]：离屏夹具的真实鼠标不能悬停，100ms隐藏计时器会在Application.DoEvents时关闭浮层。夹具ShowPaletteFlyout在显式打开后暂停该组隐藏计时器；不改变产品隐藏逻辑，保留原列表断言。

最新Build退出0，程序集SHA256=792AFE881517F446D6974745063D310189B858ABE0D23B3AED647ED9136947F2；focused.ps1完整Flash专项退出0，包含所有新遮罩基础断言及flash_import_regression=ok。focused-render-tools.ps1通过原维度/Front列表断言、scene_3d_selection_auto_transform、scene_3d_transform_panel_motion及scene_3d_transform_gizmo_motion后，在Benchmark.StageInteraction.cs:2814的A focused editor did not receive its key after canceling the active spatial transform失败。尚未确定该新暴露失败的因果，不声称Render通过。Timeline,Render -NoBuild -Plan已检查，串行完整验证会话61624仍运行，结果另记。git diff --check退出0，仅行尾提示。本轮没有重新扫描这个最新构建；454份全量结论仍严格属于3D6FB08B构建。

## 继续检查：交互与遮罩命中阻断

此前会话61624在续接时已无法读取，不能据此判断完整Timeline/Render结果。本轮重新保留日志验证：9B978B30BE8570DC8AAF6A635EC0D52BD124A5501D3169F61D39B254DB59B081构建的Timeline,Render -NoBuild计划已检查；Timeline完整通过，包含flash_import_regression=ok及新遮罩基础断言。Render越过旧维度切换、编辑焦点、投影Distort阻断后，停在Scene Mask点选回归；该次命令退出1，日志为%TEMP%\v2d-fla-audit\timeline-render-focus-distort-fixed.log。

本轮定位并修复：

- MainForm.WorkbenchCommands.cs的文本按键提前返回分支绕过空间键盘变换取消。现先取消活动变换再交还文本输入；诊断确认失败前后TextBox均Focused，旧状态却仍modal/editSession=true。focused-render-tools.ps1修复后退出0。
- MainForm.SelectionAndTopology.cs的Distort包络条件只接受直接绘图对象，错误隐藏已选场景实例的包络。现保留实例编辑入口，沿原实例warp与撤销流程；上述专项覆盖投影变换、畸变和撤销，退出0。
- VectorScene.ElementEditing.cs/SelectionVisibility.cs的带过滤命中，候选过滤和后续遮挡计算不一致：已被遮罩排除的上层对象仍遮挡下层。现把同一过滤条件传入该次遮挡判定，默认无过滤调用保持原有规则。RunSceneMaskSelectionRegression新增跨层描边/填色对照，验证可见上层填色继续遮挡下层描边、上层被过滤后下层才可命中。最初新增对照把两者放在同层，但产品同层先填后描，描边本就可见；已修正夹具为两个层，未修改产品的同层绘制规则。

最终Build退出0（30警告/0错误），程序集SHA256=8EDA2659092ED85DA6C0D1D95A3021085CCE6066726FC060ECC724C134DBE89B。focused-mask-selection.ps1退出0，输出scene_mask_selection_filtered_occlusion=ok；git diff --check退出0。Freehand,Render -NoBuild -Plan已检查，完整串行验证正在运行，日志为%TEMP%\v2d-fla-audit\freehand-render-mask-selection-fixed.log，最终结果另记。用户指南已同步上述行为的中英说明。

本轮未重新执行454份扫描，未完成Adobe整文件视觉比较，未开放FLA mask解析。Stage新遮罩计划的全部后端消费仍待完成，不能把本轮交互修复或计算器专项通过表述为FLA遮罩已经支持。

Freehand,Render串行验证退出1：Freehand在RunBezierOperationPerformanceRegression的Dense Fill drag preview预算处失败，p95Ms=1.587，当前源码门槛为1 ms；Render尚未启动。此前已打印drawing_topology_regressions=38、random_fracture_geometry=ok、smart_snap_pointer_regression=ok、snap_point_tool_regression=ok及三项Bezier preview检查通过。没有放宽门槛，也未通过隔离基线确定该性能失败的因果。随后Render -NoBuild -Plan单独检查并启动，日志为%TEMP%\v2d-fla-audit\render-mask-selection-fixed.log，结果另记。

独立Render最终退出1：已通过scene_mask_selection_filtered_occlusion=ok及此前空间键盘/投影变换/工具面板、SVG GDI/Direct2D绘制与选择断言，随后在RunDenseSelectionPanRenderRegressionCore的密集已选对象缩放60 FPS预算处失败。设备NVIDIA GeForce GTX 1660 Ti with Max-Q Design，1920×1080；120帧已选平移avg=17.765 ms/P95=22.257 ms/max=26.742 ms；缩放avg=13.168 ms/P95=17.243 ms/max=21.027 ms，dense_selection_pan_budget_met=false、dense_selection_zoom_budget_met=false。命令未启用-EnforcePerformanceBudget，但该缩放用例自身硬断言仍失败，后续用例未执行。日志为%TEMP%\v2d-fla-audit\render-mask-selection-fixed.log。本轮没有隔离未修改基线，也未排除其他进程负载，不能将此性能失败归因于本轮命中修复或宣称满足性能目标。最终构建Hash保持8EDA2659092ED85DA6C0D1D95A3021085CCE6066726FC060ECC724C134DBE89B，未再次构建或重扫454份。总体仍为进行中；未完成全部FLA功能对齐。

## 普通二维舞台消费组合遮罩计划（2026-10-31，继续进行）

StageControl.CompositionMasksFor仅向与当前SceneCompositionResultScene相同的场景提供计划，避免临时切换Underlay/OnionSkin/DragPreview场景时套用错误索引。StageControl.GdiSceneRendering的普通对象绘制与隔离组绘制现在实际消费SceneCompositionMaskRegions：逐层交集、遮罩独立对象并集、复合路径孔洞、锁定遮罩子树隐藏；隔离组经LayerBlendCompositor.CreateLayerBoundaryClip回调在滤镜完成后裁剪，子层延后父层负责的相同遮罩，保留自身内遮罩。

新增Direct2DStageRenderer.CompositionMasks.cs，在每次普通二维对象绘制中创建本次覆盖缓存，沿同一计划做Direct2D原生Union/Intersect；把Stage的GraphicsPath直接转换为线段和三次Bezier几何，不经过Region扫描多边形化。缓存和返回路径由本次绘制释放，不跨帧/设备保留；未增加全局软件回退策略。Direct2D现有隔离滤镜组仍沿既有GDI合成回退，不将其声称为新增GPU滤镜组支持。

Benchmark.FlashStageMasks.cs接入RunFlashNestedMaskPlanRegression（经RunFlashImportRegression到Timeline）：GDI与Direct2D DC实际舞台DrawObjects入口的像素断言覆盖嵌套交集、曲线角落、孔洞、空白曝光、隐藏遮罩图形、保存重开及重叠图元并集。父滤镜夹具把源图形放在遮罩外，向遮罩内投射阴影，验证父滤镜完整输入保留，最后遮罩裁剪掉源图形。标记为flash_stage_nested_mask_gdi_pixels、flash_stage_nested_mask_direct2d_dc_pixels、flash_stage_mask_overlap_union_gdi_direct2d和flash_stage_mask_parent_filter_input_and_final_boundary。

验证中真实HWND显示LastFrameUsedDirect2D=true，但PrintWindow(Stage)仅捕获背景；直接屏幕读取得到其它窗口画面，增加Fuchsia哨兵边框和TopMost后哨兵校验仍失败。因此这些捕获不构成GPU画面通过证据。最终夹具使用独立Direct2D DC RenderTarget绑定内存位图，调用相同DrawObjects和遮罩几何入口，保留独立GDI对照；DC像素通过不等同硬件窗口画面验收。DC初次透明Alpha比较受GDI DC输出Alpha影响，已改成不透明Fuchsia背景并检查RGB；未改变产品的透明合成规则。首次DC夹具构建因Vortice.Color4/Clear的Color参数不匹配失败，改为Color后构建通过。

该次Build退出0（30警告/0错误），程序集SHA256=6B6BF5A1A09B50F767E335928EBBD318DCE0CC7FB233C19194FBA49E24E4525A；focused.ps1完整Flash专项退出0，日志%TEMP%\v2d-fla-audit\stage-mask-final-flash.log包含全部新像素标记及flash_import_regression=ok。Timeline,Render -NoBuild -Plan已检查，完整串行验证退出1：Timeline完整通过，Render在RunSpatialTransformKeyboardRegression的S Z正厚度断言处停止，未执行后续渲染验收。日志%TEMP%\v2d-fla-audit\stage-mask-timeline-render.log及对应.exit.txt保存结果。用户指南已同步中英文行为和限制。

当前消费范围仅普通二维对象绘制及其GDI隔离合成入口。Scene Building的2D正面默认也会走参考投影，因此不能把上述像素夹具通过当成所有2D场景舞台已完成。参考投影（含GDI/Direct2D与批次滤镜组）、选择命中对新计划的消费、子层Underlay绑定、OnionSkin/DragPreview/播放预载与缓存仍需完成。ImportedSvg/Text/Bitmap等遮罩轮廓及Adobe填色/忽略bitmap等语义仍须实际对照；FLA mask解析保持拒绝。未重新扫描454份素材，未修改原始FLA，未完成全功能对齐。

再次核对[Adobe官方遮罩文档](https://helpx.adobe.com/animate/desktop/animation/using-mask-layers.html)（页面标记2026-06-09更新）：填色形状、文字及元件可作遮罩，可链接多个内容层；遮罩忽略bitmap、渐变、透明度、颜色和线型，锁定遮罩与内容层后显示效果；不支持把mask直接施加给另一mask，遮罩层不使用3D工具。该文档不等于已确认所有嵌套symbol/filter语义，也不能把V2D原生通用遮罩路径对位图矩形等的现有行为当作Adobe语义。FLA开放前仍需相应合成夹具与真实文件画面对照。

## S Z 回归夹具的短轴方向检查（2026-10-31）

Render的S Z失败尚未稳定复现：仅增加失败诊断的构建（SHA256=7155D147FBC25DD02276B45141B4CEE7856AAC3F0713E64E6DCCB1B11F54D3FB）连续两次focused-render-tools.ps1均退出0，日志scale-z-diagnostic.log和scale-z-diagnostic-second.log保存在同一TEMP审计目录。读取TryGetSpatialGizmoScreenGeometry确认其返回标准几何，不能把失败归因于gizmo显示动画。

源码检查发现夹具AxisDragTarget与产品TryBeginSpatialTransformPointer存在方向差异：投影轴长度处于(0.001,4)像素时，产品保留归一化投影方向并使用回退长度，夹具原先统一向上拖动，可能产生负厚度并按产品规则钳制到零。夹具现保留短轴方向、延长到72像素；仅近零轴向上回退。原有正厚度、提交与一次撤销断言保留，并增加失败时的起点、端点、实际指针、会话轴和长度诊断。不宣称这一差异已证明是此前失败的唯一原因。

修正后Build退出0（30警告/0错误），SHA256=58C979F5E12FCA5884B8109E0820B857C657E1C2EE6D81EA33E450C33A2B9CA0；focused-render-tools.ps1退出0，日志scale-z-axis-build.log与scale-z-axis-focused.log。该次仅修改回归夹具及审计文档，未修改产品厚度语义，未重扫454份FLA。

完整命令`.agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1 -Suite Render -NoBuild`先检查Plan后串行执行，退出1，日志scale-z-axis-render.log。脚本抛出异常，后续写exit.txt的命令未执行，不能将不存在的侧文件当作证据。本次通过S Z、遮罩选择及后续SVG检查，停在Dense selected-object zoom exceeded the 60 FPS frame-time budget。设备NVIDIA GeForce GTX 1660 Ti with Max-Q Design、1920×1080、120帧；已选平移avg=16.184/P95=19.715/max=22.215 ms，缩放avg=11.908/P95=17.184/max=22.599 ms；两项budget_met=false。未放宽预算，未隔离未修改基线或排除其它进程负载；不能将失败归因于FLA修改，Render及其后续用例仍未全部通过。git diff --check退出0。

## 编辑元件时的嵌套实例底图绑定（2026-10-31）

发现MainForm.RebuildDrawingObjectUnderlay取得BuildDrawingObjectChildren的完整结果后，仅把HostLayerRanges传给Stage，导致嵌套子元件的LayerMasks在底图绘制时丢失。现通过BindUnderlayComposition把结果与底图场景一起绑定，CompositionMasksFor只在场景身份匹配时使用它；普通BindUnderlayScene重绑会清除组合结果，清除底图与BindScene也清理绑定。绑定变化通过RequestStageFrame使基础画面缓存失效。GDI/Direct2D的DrawScene与对象绘制预算选择同时识别该计划，沿已有逐对象遮罩路径绘制，不交给尚未消费计划的tile路径。

Benchmark.FlashStageMasks新增从真实BuildDrawingObjectChildren结果出发的夹具：宿主实例引用带锁定椭圆遮罩的子元件，分别测试独立DrawScene和宿主图层内底图插入的GDI/Direct2D DC像素，检查中心、遮罩外部和曲线角落；同时检查编辑场景恢复、普通底图重绑、清除底图与切换编辑场景后的计划清理。标记flash_stage_nested_underlay_mask_gdi_direct2d_binding_cleanup=ok，已接入完整Flash专项及Timeline。DC像素仍不代表真实硬件窗口画面验收。

最终Build退出0（30警告/0错误），SHA256=F7464768F9C52827B806B6C4CD91807B869F3495AD853DA1FEEBC4B4EB853359，日志underlay-mask-layered-build.log；focused.ps1完整Flash专项退出0，日志underlay-mask-layered-flash.log包含上述标记及flash_import_regression=ok。Timeline,Render -NoBuild已检查Plan；完整Timeline通过（timeline_regression=ok），Render退出1。本次越过密集缩放检查和多项后续交互检查，停在RunInteractivePreviewSchedulingRegression的填充系统光标颜色/Alpha断言（The fill system cursor lost its colored bucket or alpha-blended interior，Benchmark.StageInteraction.cs:8597），日志underlay-mask-timeline-render.log。未隔离基线证明因果，不能归因于底图修改或声称Render全部通过。用户指南已同步中英文底图行为。未修改FLA解析器、未重扫454份、未修改原始素材。参考投影、洋葱皮/播放组合绑定及其它不支持特性仍未完成，不能由本项推断FLA遮罩导入已可用。

## 无隔离组的参考投影遮罩（2026-10-31）

StageControl.GdiReference3D的非隔离组分支现在用SceneCompositionMaskRegions计算组合层的完整交集与并集，并在逐绘制项边界同时应用主对象、关联次对象的裁剪。锁定遮罩的容器图形不显示；有组合计划时跳过只识别单个本地mask层的旧裁剪入口，空根遮罩的覆盖可来自嵌套图形。隔离滤镜/混合组分支尚未接入，不能推断其滤镜输入与最终边界已正确。

Direct2D参考投影沿相同LayerMasks计划，复用每绘制周期的CompositionMaskGeometries与原生几何Union/Intersect。每层裁剪由当次绘制持有并释放；不跨设备/帧缓存。此入口有计划时不使用尚未消费计划的CPU栅格化与跨项描边批次，仍使用Direct2D矢量绘制，并未新增整帧GDI回退。上游播放预载/缓存入口的判断与绑定尚需接入，不能宣称播放链路完整。

CreateProjectedCompositionMaskObjectPath在正交投影下把普通二维填色路径仿射映射到对象的实际参考投影平面，保留三次Bezier与复合孔洞，不使用Region扫描轮廓。路径控制点全部处于近裁剪面前才使用该映射；穿越近裁剪面或透视投影沿现有近裁剪轮廓表示，未声称这条已有透视采样路径达到精确曲线对齐。ImportedSvg/Text/Bitmap覆盖轮廓及真实Adobe画面对照仍待完成。

新增flash_stage_projected_nested_mask_curve_hole_gdi_direct2d_dc=ok夹具，实际调用GDI与Direct2D的DrawReference3DCurrentScene，覆盖Scene Building普通二维正面路径的空根遮罩、嵌套覆盖、双遮罩交集、孔洞、曲线角落与隐藏遮罩图形；另检查正交曲线保留Bezier，以及倾斜正交平面穿越近裁剪面时不能套用未裁剪仿射曲线。DC像素仍不等同真实GPU窗口验收，未据此声称所有3D/透视画面已对齐。

该次构建SHA256=E8D6DE7A167E9F85234226F1C719B505CC1A963975728DF1D37FECD9C41CE973，Build退出0（30警告/0错误），日志projected-mask-near-build.log；`pwsh -NoProfile -File "$env:TEMP/v2d-fla-audit/focused.ps1"`退出0，日志projected-mask-near-flash.log包含新标记与flash_import_regression=ok。完整`.agents/skills/vector2d-validate-change/scripts/invoke-validation.ps1 -Suite Timeline,Render -NoBuild`已先检查Plan后串行运行：完整Timeline通过；Render退出1，在RunSpatialFrontProjectionRegression的Direct2D光照gizmo强度手柄像素断言处停止（Benchmark.StageRenderSuite.cs:6836，The Direct2D reference-3D overlay did not paint the light-Gizmo intensity handle），日志projected-mask-timeline-render.log。未隔离基线证明因果，未声称Render全部通过。用户指南已同步中英文阶段性行为与限制。未重扫454份FLA，未开放mask解析，未改变原始素材，整体目标仍未完成。

## 投影隔离滤镜组的最终遮罩边界（2026-10-31）

LayerBlendCompositor.CompositeBatchesTo新增可选图层边界Region回调。每层/文件夹先完成内容和滤镜，再裁剪；没有自身边界裁剪的正常层仍沿旧批次路径，存在边界裁剪的文件夹即使自身没有滤镜也保留组边界。回调Region在当层释放，旧调用方不提供回调时维持原路径。

StageControl.GdiReference3D的隔离组分支现在传递SceneCompositionMaskRegions.CreateLayerBoundaryClip，并把逐项的同一组合遮罩延后到图层/组边界，避免提前删除父滤镜输入。子层自身的内遮罩仍保留；关联次对象的独立遮罩通过CreateRelatedLayerClip应用，与主对象共享的遮罩延后到主对象边界。两个投影渲染器也检查次对象是否来自隐藏的遮罩容器，避免相关派生项显示遮罩图形。此关联区域规则已加入区域回归；尚无完整复杂3D相交滤镜的视觉对照，不能宣称所有3D场景已对齐。

新增真实DrawReference3DCurrentScene像素夹具flash_stage_projected_mask_parent_filter_inner_clip_leaf_filter_boundary=ok：父投影阴影保留父遮罩外的源输入、子层内遮罩拒绝源输入时不产生阴影、无滤镜父文件夹裁剪子层滤镜输出，以及视口外源图形的阴影进入视口后仍被最终边界裁剪。夹具同时核对Direct2D隔离滤镜仍走既有RequiresSoftwareLayerCompositing路径；没有新增GPU滤镜组支持或新的整帧回退策略。

最终Build退出0（30警告/0错误），SHA256=9778740ABFDD204FCBF2ED40E498A9FB70568E2FDD2901EB6EF77276CC47302F，日志projected-filter-mask-final-build.log；focused.ps1完整Flash专项退出0，日志projected-filter-mask-final-flash.log包含上述标记、关联区域断言及flash_import_regression=ok。Timeline,Render -NoBuild已先检查Plan；该次Timeline持续卡在镜头预览UI派发，采集两次托管堆栈并核对进程身份后人工结束，退出1，Render未执行。日志projected-filter-mask-timeline-render.log及projected-filter-timeline-stacks.txt、projected-filter-timeline-stacks-second.txt。此结果不表示Timeline通过或自然失败；后续修复及重新验证见下节。git diff --check退出0。用户指南已更新中英文投影组边界行为；未重扫454份、未开放FLA mask解析。播放预载/缓存、选择命中、ImportedSvg/Text/Bitmap遮罩覆盖与Adobe实际画面对照仍未完成。

## 播放遮罩计划与缓存身份（2026-10-31，专项已通过）

只读核对发现CPU/GDI播放栅格入口只识别SceneCompositionMaskClips或单个本地mask关系，没有识别LayerMasks；准备结果与播放位图的ViewKey也未包含遮罩计划身份。同一场景、帧号和几何不变而替换计划时，旧未遮罩数据可能仍满足匹配条件。

源码现为RejectReference3DPlaybackRasterPreparation、CanUseReference3DCpuRaster和TryRenderReference3DPlaybackGdi增加组合计划判断，让尚未实现该计划的栅格路径交还已有矢量绘制；Reference3DPlaybackViewKey与Reference3DWorkspaceFrameCacheKey增加计划身份，避免计划替换沿用旧预载/画面。该项是正确性保护，不是CPU栅格遮罩支持，也不代表遮罩播放性能达标；仍需补齐与验证CPU栅格预载本身。

新增Benchmark.FlashPlaybackMasks.cs，经RunFlashNestedMaskPlanRegression接入Flash/Timeline。夹具先用无计划的同一场景证明栅格控制可准备，再在场景、帧号、几何版本和工作区缓存版本不变时交替绑定filled/blank/filled计划，检查旧准备结果失配、栅格入口识别计划、两类缓存键变化，以及GDI/Direct2D投影像素随绑定变化。构建SHA256=299F7DEB89613D18BF5212290C46957D2EF35B056C9E54A56EA3E119D5F95743，Build退出0（30警告/0错误），日志playback-mask-clock-build.log；focused.ps1完整Flash专项退出0，日志playback-mask-clock-flash.log包含flash_stage_playback_mask_binding_raster_guard_cache_keys_filled_blank_gdi_direct2d=ok及flash_import_regression=ok。


## 镜头预览时钟派发与完整验证（2026-10-31）

旧构建9778740的Timeline进程39252持续消耗CPU但未返回。两次dotnet-stack report均显示主线程在RunSceneShotTimelineUndoRegression的Application.DoEvents中处理ShotPreviewDisplayClock.Tick，预览工作线程继续派发。诊断工具dotnet-stack 8.0.547301仅安装在TEMP诊断目录，日志diagnostic-tool-install.log。核对命令行为--bench-timeline后人工停止该进程；没有改动原始FLA。

MainForm.ShotPreview.cs现对DXGI垂直同步立即返回的情况也应用显示频率截止时间，并在UI刷新完成后保留至少1ms的派发间隔，防止消息泵持续被刷新回调占用。取消等待仍生效。299F7DEB构建的focused-shot-undo.ps1退出0，约7秒，日志playback-mask-clock-shot-undo.log包含shot_director_keyboard_undo=ok。完整Timeline,Render -NoBuild先检查Plan后串行运行；Timeline已打印timeline_regression=ok，Render退出1，停在Benchmark.StageRenderSuite.cs:6836的Direct2D光照gizmo强度手柄像素断言，日志playback-mask-clock-timeline-render.log。未隔离基线确定因果，不能宣称Render通过。只读采集进度堆栈时Timeline进程恰已退出，对该进程的dotnet-stack报Process not running，不影响已有通过日志；Render进度堆栈playback-clock-render-progress-stack.txt显示当时在滤镜GPU检查。

## 投影点选消费组合遮罩（2026-10-31，专项通过）

StageControl.Reference3D.cs的IsProjectedPointVisibleThroughMasks先消费当前场景绑定的LayerMasks计划：锁定遮罩容器不可选，使用与投影绘制相同的对象路径计算逐层交集和独立图元并集。存在完整计划时不重复套用本地mask根轮廓，避免空根加嵌套图形的有效遮罩被判空。没有计划时保留旧判断。

Benchmark.FlashStageMasks.cs在现有投影像素夹具中加入真实TryHitTestProjectedObjectFiltered断言：交集内可选、孔洞及曲线外不可选、隐藏嵌套遮罩图形不可选。另外在播放绑定夹具中检查filled/blank/filled计划切换后的点选结果。Build先检查Plan后运行，退出0（30警告/0错误），SHA256=D66D34CDFD30679F001C75DCDD44F9E9385A1F45527306D14ED55235E24E13EE，日志projected-mask-picking-build.log。focused.ps1完整Flash专项退出0，日志projected-mask-picking-flash.log包含flash_stage_projected_nested_mask_point_selection_curve_hole_hidden_artwork=ok及flash_import_regression=ok。git diff --check退出0。该构建未重复完整Timeline/Render；上个构建完整Timeline通过、Render光照手柄断言失败的结果不能移植为此构建全套通过。尚未接入普通二维、框选及全部预览路径；未开放FLA mask解析，也未重扫454份素材。


## 洋葱皮组合遮罩（2026-10-31，继续进行）

SceneCompositionBuilder.BuildSceneOnionSkin现返回各相邻曝光的合并遮罩计划；CombineOnionSkinMaskPlans按CombineOnionSkinPreviews相同的非空源图层偏移重映射Clips、Contents与Containers，避免前后帧遮罩互相裁剪。VectorScene.CombineOnionSkinPreviews复制LayerLocked；Stage.BindOnionSkinComposition将计划绑定到对应预览场景，普通重绑、清除和BindScene清理计划，绑定变化使基础画面缓存失效。MainForm的Scene Building入口消费返回计划。没有改变持久化格式。

夹具初次构建因调用不存在的WorldToScreen(PointF)重载失败，改为(X,Y)后构建通过。夹具设置阶段随后把SetOnionSkinRange的“值未变化”误判为失败，已改为检查实际曝光结果。进一步发现原BuildSceneOnionSkin重复加入同一图层保持曝光：frame2/frame3同属key2，却生成两份预览。产品现以图层ID和SourceKeyframeFrame去重，保留前后不同关键帧。未放宽原有像素断言。

构建SHA256=344537BD07686BF16A730C085F5FFF39E4ABF60599767A2128938EDA4BD1DCD8，Build退出0（30警告/0错误），日志scene-onion-mask-held-build.log；focused-scene-onion.ps1退出0，日志scene-onion-mask-held-focused.log；focused.ps1完整Flash退出0，日志scene-onion-mask-held-flash.log包含flash_stage_scene_onion_nested_mask_frame_offsets_curve_gdi_direct2d_binding_cleanup=ok及flash_import_regression=ok。夹具覆盖前后曝光、空mask根的嵌套椭圆覆盖、两个帧计划隔离、锁定状态、GDI/Direct2D DC像素、普通重绑/关闭/切场景清理；DC像素不等同真实GPU HWND验收。Timeline,Render -NoBuild先检查Plan后串行运行，Timeline完整通过，Render退出1，仍停在Benchmark.StageRenderSuite.cs:6836的Direct2D光照gizmo强度手柄像素断言，日志scene-onion-mask-timeline-render.log；未隔离基线确定因果。

继续准备Basic Drawing嵌套洋葱皮：BuildDrawingObjectOnionSkin返回计划，组合本地预览时按图层偏移合并绑定；递归遍历保留锁定mask根及其嵌套覆盖依赖，普通锁定内容仍不加入洋葱皮。Benchmark.FlashOnionMasks新增nested-only与mixed-local像素夹具。该后续源码尚未构建验证，不能计为通过。本地原始绘图图层的原生遮罩依赖仍未补齐，不能把嵌套路径修改称为全部Basic Drawing洋葱皮已对齐。未开放FLA mask解析，未重扫454份或修改原始FLA。


## Basic Drawing完整曝光洋葱皮（2026-10-31，专项通过）

继续核对发现原本地BuildOnionSkinPreview只复制内容Cel，不能携带该Cel依赖的mask。编辑器现使用BuildDrawingObjectOnionSkin(includeLocalArtwork:true)，按候选来源Cel在相邻帧组合本地图形、嵌套实例、mask根及祖先依赖，之后统一着色与按曝光合并。新SceneCompositionBuilder.OnionSkin.cs使用编辑元件坐标的单位变换，不以新实例的注册点偏移本地图形；通过HoldFrame绑定相邻帧，避免根实例播放速率二次换算。root层过滤仅限制当前元件，不传入递归子元件。旧纯嵌套默认入口仍保留给现有调用方。本地与嵌套不再在UI各自生成后丢失原遮罩关系；移除两份已无调用的预览场景字段。

锁定mask根和它的嵌套图形作为覆盖依赖保留，普通锁定图层仍排除。新增夹具分别验证纯嵌套、手工合并偏移、本地与嵌套的实际完整曝光组合、本地无实例的原生椭圆mask，以及mask在相邻帧变空白后的裁剪。首次本地像素断言失败的诊断为只有Rectangle且无Ellipse：夹具内容轨延长至3帧，mask轨仍仅1帧，相邻第1帧已经无mask曝光。显式将夹具mask轨延长至3帧后验证有内容及空白帧；未改变产品短轨或空白语义。

构建SHA256=421139BD00F513839BC1E3815B9469764F27900D06A489C4CA0304EDC6E720F7，Build先检查Plan后运行，退出0（30警告/0错误），日志drawing-onion-mask-duration-build.log。focused-local-onion.ps1退出0，日志drawing-onion-local-duration.log；纯嵌套/混合定向夹具在前一构建退出0，日志drawing-onion-nested-focused.log。最新focused.ps1完整Flash退出0，日志drawing-onion-mask-duration-flash.log同时包含flash_stage_drawing_onion_nested_locked_mask_mixed_local_gdi_direct2d=ok、flash_stage_local_onion_mask_dependency_curve_blank_gdi_direct2d=ok、Scene Building洋葱皮标记及flash_import_regression=ok。GDI/Direct2D DC结果不代表真实GPU窗口完整视觉验收。

最新Timeline,Render -NoBuild先检查Plan后串行执行，Timeline已打印timeline_regression=ok，Render退出1，仍停在Benchmark.StageRenderSuite.cs:6836的Direct2D光照gizmo强度手柄像素断言，日志drawing-onion-mask-timeline-render.log。未隔离基线确定因果。用户指南更新中英文行为；git diff --check退出0。未重新扫描454份，未开放FLA mask解析，未修改原始素材；CPU遮罩栅格预载、框选、拖动预览绑定及ImportedSvg/Text/Bitmap覆盖等仍待补齐与Adobe实际对照，不能宣称全部FLA已兼容。


## 元件拖入预览遮罩绑定（2026-10-31，专项通过）

MainForm.UpdateDrawingObjectDragPreview原先丢弃BuildDrawingObjectPreview返回的组合结果。现把结果传给Stage.BindDragPreviewScene的可选composition参数，CompositionMasksFor按拖入场景身份提供计划；其它普通预览调用不提供结果时清除旧绑定。BindScene和清除预览也移除结果。连续拖动仍沿原TranslateAllObjectsForPreview更新几何，不重复组合元件；每次绘制新建覆盖计算器，不沿用旧坐标遮罩缓存。源对象隐藏规则保持原调用约定。

新增Benchmark.FlashDragMasks.cs，经FlashNestedMasks接入完整Flash/Timeline：反射调用实际MainForm.UpdateDrawingObjectDragPreview入口，检查三次位置变化后同一场景及计划仍绑定，空mask根的嵌套椭圆裁剪中心/外部/曲线角落；普通二维DrawScene及参考投影实际绘制入口分别验证GDI和Direct2D DC像素，并检查编辑场景隔离、普通预览重绑、源对象隐藏/清理、再次拖入及BindScene清理。DC像素不等同真实硬件窗口验收。

构建SHA256=2FEFF659CEC9D82D552EC0CAEAD6BB3A792AAE9FDC6531B7F726140DDD1BD406，Build先检查Plan后执行，退出0（30警告/0错误），日志drag-mask-projected-build.log；focused-drag-mask.ps1退出0，日志drag-mask-projected-focused.log包含flash_stage_drag_preview_nested_mask_translation_curve_projected_gdi_direct2d_binding_cleanup=ok；focused.ps1完整Flash退出0，日志drag-mask-projected-flash.log包含该标记及flash_import_regression=ok。Timeline,Render -NoBuild先检查Plan后串行运行，Timeline完整通过；Render退出1，停在Benchmark.StageInteraction.cs:1139的Returning to Basic Drawing did not restore all shape-flyout tools断言，未到此前光照gizmo检查。日志drag-mask-timeline-render.log；未隔离基线确定因果。git diff --check退出0，用户指南同步中英说明。未重扫454份，未开放FLA mask解析，未修改原始素材。CPU遮罩预载、完整选择链路和ImportedSvg/Text/Bitmap覆盖及Adobe画面对照仍未完成。


## 普通二维文字遮罩轮廓（2026-10-31，专项通过）

CreateCompositionMaskObjectPath原先对Text沿普通对象边界查询，可能以字框作为覆盖。现对未畸变文字沿TryGetTextWorldContours和AppendPolygonContours生成与文字绘制一致的轮廓，保留复合路径内孔；无字形时返回空路径，不退回字框。未把此改动推广为畸变文字或精确字体曲线的新实现。

新增Benchmark.FlashTextMasks.cs，经FlashNestedMasks接入Flash/Timeline。使用O字形检查实际字形与字框空白、内孔以及更新为空白文字后的空覆盖，并比较普通二维GDI/Direct2D DC像素。初次像素断言在轮廓边缘失败：expected/path=false、GDI透明，Direct2D为A255/R239/G8/B247，属于边缘抗锯齿混色。夹具现排除周围3像素范围内穿越轮廓的样点，仍要求实际采样到字形内部与空白覆盖；未改变产品裁剪或放宽内部像素断言。

构建SHA256=F2ECB0CDEF8B95B7BEE845A7702FA07C16372D71A5A022AC6DEA93DF8F5E0D94，Build先检查Plan后执行，退出0（30警告/0错误），日志text-mask-interior-build.log；focused-text-mask.ps1退出0，日志text-mask-interior-focused.log；focused.ps1完整Flash退出0，日志text-mask-interior-flash.log包含flash_stage_text_mask_glyph_counter_gdi_direct2d=ok及flash_import_regression=ok。Timeline,Render -NoBuild先检查Plan后串行运行，Timeline退出1，在空白文字夹具的Direct2D转换处抛出System.ArgumentException: Parameter is not valid（GraphicsPath.PathPoints），Render未执行，日志text-mask-timeline-render.log。反射专项在PowerShell运行时通过不能代替应用自身运行时验证；后续修复见下一节。用户指南更新中英说明，git diff --check退出0。DC像素不代表真实GPU窗口视觉通过；畸变文字、ImportedSvg/Bitmap覆盖、CPU预载和Adobe实际对照仍未完成。未开放FLA mask解析、未重扫454份、未修改原始素材。


## 应用运行时的空遮罩路径转换（2026-10-31，构建通过，CLI复测中）

完整Timeline在CheckFlashCompositionTextMaskCoverage的空白文字部分失败。调用栈为Direct2DStageRenderer.CreateCompositionMaskGeometry读取空GraphicsPath.PathPoints；同一夹具在PowerShell反射宿主通过，说明该宿主不能代替产品运行时验收。源码现先检查PointCount，空路径使用空点/类型数组，仍正常关闭原生D2D geometry sink，保留空覆盖而不抛异常。没有用矩形或全覆盖替代空mask。

Build先检查Plan后运行，退出0（30警告/0错误），SHA256=414A126EB5BB1F62205F52293108EED4058887DAA4D73CD2FD01CD76A9650457，日志empty-mask-runtime-build.log。Timeline,Render -NoBuild已先检查Plan后串行启动，日志empty-mask-runtime-timeline-render.log；该次Timeline退出1，在RunMotionTrackSceneInteractionRegression:163的Rapid anchor clicks entered symbol editing or cancelled the drag in ThreeD/Orthographic断言处提前停止，尚未执行Flash入口；Render未启动。不能计为通过。git diff --check退出0。SVG实际轮廓核对暂后移，未新增SVG遮罩支持，未开放FLA mask解析，未重扫454份素材。整体目标仍未完成。


空路径修复补充验证：为排除PowerShell宿主运行时差异，在TEMP/v2d-fla-audit/net8-flash-host创建仅反射调用Benchmark入口的net8.0-windows临时宿主，不新增产品CLI或修改项目文件。临时宿主Build退出0，日志net8-flash-host-build.log。直接执行宿主首次因System.Text.Json 9依赖解析失败，日志empty-mask-net8-flash.log，该次未执行回归。随后使用产品runtimeconfig与deps启动：

`dotnet exec --runtimeconfig native/bin/Release/net8.0-windows/VectorAnimationEngine.runtimeconfig.json --depsfile native/bin/Release/net8.0-windows/VectorAnimationEngine.deps.json "$env:TEMP/v2d-fla-audit/net8-flash-host/bin/Release/net8.0-windows/FlashHost.dll"`

该命令退出0，日志empty-mask-net8-product-deps-flash.log包含flash_stage_text_mask_glyph_counter_gdi_direct2d=ok及flash_import_regression=ok，确认产品.NET 8运行时和依赖下完整Flash入口通过。此证据不等于完整Timeline/Render通过；前置运动轨迹断言仍需诊断。所有临时宿主文件与输出均位于TEMP，不纳入工作树。git diff --check退出0。SVG实际覆盖工作尚未修改。


## 前置运动轨迹失败的产品运行时定向核对（2026-10-31）

对414A126E构建，以相同产品runtimeconfig/deps和TEMP net8宿主调用RunMotionTrackSceneInteractionRegression，退出0，日志motion-track-native-focused.log输出motion_track_scene_interaction=ok。完整套件中的ThreeD/Orthographic快速锚点点击失败尚未稳定复现，未据此改变交互规则或删除原断言。沿StageMouseDoubleClick及MotionTrackPointerDown/BeginMotionTrackDragCore核对，双击已有运动轨迹会话会提前返回；尚无证据证明是FLA修改引起。为检查前序状态相关性，先检查Plan后再次串行运行Timeline,Render -NoBuild，日志empty-mask-runtime-second-timeline-render.log，Timeline已完整通过，Render退出1，在Benchmark.SymbolFilterPanel.cs:439的Two continuous filter edits failed to modify the held source Cel with exactly one undo entry断言处停止，未到此前光照检查；未隔离基线确定因果。

SVG遮罩只读核对：ImportedSvgBreakApart.Extract(requireExactGeometry:true)可返回多个独立ImportedSvgBreakFill，每项保留自身填充规则及Bezier；无法精确表示的内容会拒绝。现有Stage单GraphicsPath接口不能直接拼接这些填色而仍保证独立图元并集（Alternate会令重叠抵消，Winding又可能改变内孔规则）。因此尚未将转换器机械接入或把Region扫描多边形作为精确曲线实现。下一步需让遮罩覆盖入口支持每对象的独立子路径并集及各自填充规则，再覆盖变换/clip/use等实际语义；本轮未新增SVG遮罩支持，FLA mask解析仍拒绝。


## 单对象独立填色路径接口（2026-10-31，构建及Flash专项通过）

SceneCompositionMaskRegions新增Func<int,IReadOnlyList<GraphicsPath>>入口，保留单路径重载。对每对象每条路径分别Region.Union，保留路径自身Alternate/Winding填充规则，finally释放工厂返回的全部路径。Stage增加多路径工厂，现仍将既有单路径封装为一项；普通二维GDI及Direct2D已消费多路径接口。Direct2D逐路径建立原生geometry再Union，不把不同填充规则强行合成一个GraphicsPath。参考投影目前仍是单路径，不声称已支持SVG多路径。

FlashNestedMasks新增flash_mask_single_object_independent_paths_union_fill_rules_curve_hole夹具：一个对象提供带孔椭圆与Winding矩形，检查重叠并集、保留孔洞、曲线外部及覆盖缓存。旧构建Render结束后串行Build退出0（30警告/0错误），日志multipath-mask-build.log，SHA256=7672E520C7E228BD33E60FC01B163D675E08D87F3F2B572EC1BB01BFBF36B1EB。使用TEMP net8宿主及产品runtimeconfig/deps执行完整RunFlashImportRegression退出0，日志multipath-mask-net8-flash.log包含flash_mask_single_object_independent_paths_union_fill_rules_curve_hole=ok及flash_import_regression=ok。git diff --check退出0。新构建未重复完整Timeline/Render，旧构建Timeline通过和Render滤镜撤销失败不能移植为新构建全套通过。此项为SVG覆盖的前置接口，未接入SVG转换器，不表示SVG遮罩已经支持；未开放FLA mask解析，未重扫454份。


## 普通二维精确纯填色SVG遮罩（2026-10-31，专项通过）

Stage.CreateCompositionMaskObjectPaths对未畸变ImportedSvg尝试ImportedSvgBreakApart.Extract(requireExactGeometry:true)，只接受全部为ImportedSvgBreakFill的结果。每填色单独建立GraphicsPath，沿BezierContours保留控制点及自身填充规则，再映射文档坐标到对象尺寸、位置、旋转及Stage二维视图。GDI与Direct2D沿已有多路径Union入口消费，避免把独立填色拼接为Alternate后重叠抵消。转换结果以源字符串弱键缓存，不持有屏幕路径或设备资源；每次绘制建立并释放屏幕路径，位置/尺寸/相机变化不复用旧覆盖。失败或非纯填色仍沿原有对象边界路径，此既有近似尚未对齐Adobe；不是新增加的精确支持。参考投影、畸变、描边、滤镜/clip等不在本项已验证范围。

Benchmark.FlashSvgMasks新增独立环形填色与穿孔矩形重叠的实际GDI/Direct2D DC像素检查，验证内孔、独立并集、外部透明及旋转；经FlashNestedMasks接入完整Flash/Timeline。Build先检查Plan后退出0（30警告/0错误），日志exact-svg-mask-build.log，SHA256=207B1D16116C4C1F4FFEDD4ECB9CEB83BCC1CFD0E738C6E768DFFF16F5FBBFA2。产品runtimeconfig/deps的TEMP net8宿主调用CheckFlashExactSvgMaskCoverage退出0，日志exact-svg-mask-focused.log，标记flash_stage_exact_svg_mask_independent_fill_union_hole_rotation_gdi_direct2d=ok；完整RunFlashImportRegression退出0，日志exact-svg-mask-flash.log包含flash_import_regression=ok。DC像素不代表真实GPU HWND视觉验收。

Timeline,Render -NoBuild已先检查Plan后串行启动，日志exact-svg-mask-timeline-render.log。Timeline在RunMotionParentPartPointerRegression（Benchmark.TimelineParenting.cs:66）的“Dragging parent 001's fill did not move child 002.”断言提前失败，尚未到Flash入口；Render未启动。未隔离基线确定因果。用户指南同步中英文支持边界，git diff --check退出0。未重扫454份，未修改原始素材，未开放FLA mask解析；整体功能对齐仍未完成。

## 正交参考视图的纯填色SVG遮罩（2026-10-31，专项通过）

正交参考绘制与投影点选现逐条投影独立填色路径；GDI、Direct2D和点选共同消费多路径Union计算器，保留各填色的fill-rule、孔洞与曲线。仅在整个控制点包络位于近裁剪面前时使用仿射映射；透视及穿越近裁剪面的SVG仍沿既有投影轮廓路径，尚未对齐。未改变原始素材或开放FLA mask解析。

Build先检查Plan后构建成功，日志projected-svg-mask-build.log记录30警告/0错误。原运行session在续接时已不可查询，因此退出码未保留，不将日志推断为已收取的退出码。程序集SHA256=4B89A82A01A1E6D42D236877CC8E79F22A9D4835999B685F8DF8B48E893D07A2。产品runtimeconfig/deps的TEMP net8宿主调用CheckFlashExactSvgMaskCoverage退出0，日志projected-svg-mask-focused.log；完整RunFlashImportRegression退出0，日志projected-svg-mask-flash.log，包含flash_stage_exact_svg_mask_orthographic_gdi_direct2d_picking=ok与flash_import_regression=ok。夹具比较GDI/Direct2D DC像素及投影点选，覆盖两个角度、内孔、独立填色并集与外部透明；不代表真实GPU HWND验收。未重扫454份；普通二维选择链路正在继续核对。

## 普通二维元件点选接入组合遮罩（2026-10-31）

TryResolveSceneInstanceAt原先只向TryResolveCompositionInstance提供旧SceneCompositionMaskClips，漏掉新的LayerMasks。现传入Stage坐标和精确对象路径提供者，在本次点选内创建SceneCompositionMaskRegions，过滤锁定mask图形及覆盖外对象，再沿原命中栈顺序解析根实例；无组合计划的旧入口保留。Basic Drawing嵌套实例的underlay与Scene Building普通二维入口均沿同一调用。没有将此结果推广为框选已完成：FindCompositionInstancesInsideBounds仍需接入新计划。

扩展CheckFlashExactSvgMaskCoverage，在两个旋转角度下实际调用MainForm.TryResolveCompositionInstance，核对孔洞、外部透明、独立填色并集以及返回根实例身份。首次构建因WorldToScreen调用缺少y参数失败，改用world.X/world.Y后Build退出0（30警告/0错误），日志flat-mask-picking-build.log。程序集SHA256=7CDDBC76C145F30E90468296783D8B7C0A4EF4E0A96248796BEC73137707B1B5。产品runtimeconfig/deps的TEMP net8宿主执行完整RunFlashImportRegression退出0，日志flat-mask-picking-flash.log，包含flash_stage_exact_svg_mask_flat_instance_picking_owner=ok及flash_import_regression=ok。Timeline,Render -NoBuild先检查Plan后串行启动，日志flat-mask-picking-timeline-render.log，结果待补录。git diff --check退出0，用户指南同步中英文边界；未重扫454份、未开放FLA mask解析、未修改原始素材。全量导入和Adobe视觉对齐目标仍未完成。
