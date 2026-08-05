namespace VectorAnimationEngine;

internal sealed class ReleaseNoteSection
{
    public ReleaseNoteSection(string heading, params string[] items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length == 0 || items.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A release-note section must contain at least one non-empty item.", nameof(items));
        }

        Heading = heading;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public string Heading { get; }
    public IReadOnlyList<string> Items { get; }
}

internal sealed class LocalizedReleaseNote
{
    public LocalizedReleaseNote(string title, string summary, params ReleaseNoteSection[] sections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Length == 0 || sections.Any(section => section is null))
        {
            throw new ArgumentException("Localized release notes must contain at least one section.", nameof(sections));
        }

        Title = title;
        Summary = summary;
        Sections = Array.AsReadOnly(sections.ToArray());
    }

    public string Title { get; }
    public string Summary { get; }
    public IReadOnlyList<ReleaseNoteSection> Sections { get; }
}

internal sealed class ReleaseNoteEntry
{
    public ReleaseNoteEntry(
        Version version,
        DateOnly releaseDate,
        LocalizedReleaseNote english,
        LocalizedReleaseNote simplifiedChinese)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(simplifiedChinese);

        Version = version;
        ReleaseDate = releaseDate;
        English = english;
        SimplifiedChinese = simplifiedChinese;
    }

    public Version Version { get; }
    public DateOnly ReleaseDate { get; }
    public LocalizedReleaseNote English { get; }
    public LocalizedReleaseNote SimplifiedChinese { get; }

    public LocalizedReleaseNote ContentFor(UiLanguage language) =>
        language == UiLanguage.SimplifiedChinese ? SimplifiedChinese : English;
}

internal static class ReleaseNotesCatalog
{
    private static readonly IReadOnlyList<ReleaseNoteEntry> LatestFirstEntries = Array.AsReadOnly(
        new[]
        {
            new ReleaseNoteEntry(
                new Version(0, 1, 12),
                new DateOnly(2026, 8, 4),
                English012(),
                SimplifiedChinese012()),
            new ReleaseNoteEntry(
                new Version(0, 1, 11),
                new DateOnly(2026, 8, 3),
                English011(),
                SimplifiedChinese011()),
            new ReleaseNoteEntry(
                new Version(0, 1, 9),
                new DateOnly(2026, 7, 30),
                English019(),
                SimplifiedChinese019()),
            new ReleaseNoteEntry(
                new Version(0, 1, 7),
                new DateOnly(2026, 7, 29),
                English017(),
                SimplifiedChinese017()),
            new ReleaseNoteEntry(
                new Version(0, 1, 6),
                new DateOnly(2026, 7, 28),
                English016(),
                SimplifiedChinese016()),
            new ReleaseNoteEntry(
                new Version(0, 1, 5),
                new DateOnly(2026, 7, 27),
                English015(),
                SimplifiedChinese015())
        }
        .OrderByDescending(entry => entry.Version)
        .ToArray());

    public static IReadOnlyList<ReleaseNoteEntry> Entries => LatestFirstEntries;
    public static ReleaseNoteEntry Latest => LatestFirstEntries[0];
    public static Version LatestVersion => Latest.Version;

    private static LocalizedReleaseNote English012() => new(
        "Animate-style tweening and editable Pencil curves",
        "Version 0.1.12 adds production-oriented Classic and Shape tween workflows, editable fitted Pencil curves, and focused drawing and Mixing Brush reliability improvements.",
        new ReleaseNoteSection(
            "Classic and Shape tweens",
            "Timeline frame context menus can create Classic or Shape tweens between populated endpoint keys, resolve a surrounding span from an interior frame, and remove a tween without deleting its materialized cels.",
            "Classic tweens interpolate compatible local vector objects or one nested drawing-object instance, including position, scale, shortest-path rotation, skew, Alpha, tint, stroke, fill, and gradient state where applicable.",
            "Shape tweens match closed fills separately from open Line, Pencil, and brush strokes. Cross-primitive edge plans support rectangles, circles, paths, unequal vertex counts, unequal object counts, and compound paths whose holes or islands appear, disappear, move, or merge.",
            "Solid and gradient materials interpolate with geometry, including solid-to-gradient transitions, unequal stop layouts, and deterministic gradient-kind changes. Editing either endpoint or moving its keyframe rematerializes the affected tween."),
        new ReleaseNoteSection(
            "Tween editing and Auto Key",
            "Selecting a tween shows a monotone Tween Curve editor in the right Inspector. Internal anchors can be added, dragged, keyboard-adjusted, or deleted while fixed endpoints preserve the 0 to 100 percent range.",
            "Curve gestures create one undo entry, and spring feedback animates drag, insertion, and deletion without changing the deterministic saved curve values.",
            "Tween metadata, easing anchors, remapped spans, materialized intermediate states, snapshots, project persistence, and Undo remain synchronized after timeline frame edits.",
            "Drawing-object instance transforms and settings now honor Auto Key: enabled edits materialize the playhead key, while disabled edits update the held source key without creating a hidden playhead state."),
        new ReleaseNoteSection(
            "Pencil and drawing workflow",
            "Pencil adds an Animate-style 0 to 100 percent Smoothing control. Higher values fit the pointer trajectory with fewer stable cubic Bezier segments while preserving endpoints and bounded error.",
            "Committed Pencil strokes expose editable cubic anchors and controls, participate in intersections and normal topology splitting, and can be partially selected by a marquee that cuts out the enclosed open-curve section.",
            "The Fill tool now treats same-layer existing fill edges together with strokes as boundaries when creating paint in an enclosed empty region.",
            "Line drag previews use the active Stroke color and actual width, and drawing previews remain available after the recent tool and renderer changes."),
        new ReleaseNoteSection(
            "Performance, release, and compatibility",
            "Long Mixing Brush gestures adapt preview rebuild frequency to the active work-grid size, avoiding the progressive slowdown previously seen during one continuous stroke while leaving final vertex colors unchanged.",
            "The formal package is the single replaceable VectorAnimationEngine-0.1.12-win-x64.exe and remains smaller than 5 MiB.",
            "Replacing the EXE atomically refreshes .V2DEngine while preserving a compatible .Runtime, logs, projects, and user data. Older projects continue to open with compatible defaults.",
            "Windows x64 is supported. First-time runtime provisioning requires network access, and the formal EXE is not code-signed."));

    private static LocalizedReleaseNote SimplifiedChinese012() => new(
        "Animate 风格补间与可编辑铅笔曲线",
        "0.1.12 新增面向实际制作的传统补间、形状补间与补间曲线工作流，完善铅笔贝塞尔编辑，并集中修复绘制和混色可靠性问题。",
        new ReleaseNoteSection(
            "传统补间与形状补间",
            "时间轴帧右键菜单可在有内容的端点关键帧之间创建传统补间或形状补间；在区间内部右键可自动解析相邻端点，删除补间时会保留已经物化的中间 Cel。",
            "传统补间支持兼容的本地矢量对象或单个嵌套绘制对象实例，并按适用类型插值位置、缩放、最短方向旋转、倾斜、Alpha、色调、描边、填色和渐变状态。",
            "形状补间会分别匹配封闭填色与开放的 Line、Pencil、笔刷描边；跨类型边计划支持矩形、圆形、路径、不同顶点数、不同对象数，以及孔洞或岛屿出现、消失、移动和合并的复合 Path。",
            "纯色和渐变会随几何一起插值，包括纯色到渐变、不同色标数量和确定性的渐变类型切换；修改任一端点或移动端点关键帧后会重新物化受影响的补间。"),
        new ReleaseNoteSection(
            "补间曲线与 Auto Key",
            "选中补间后，右侧检查器会显示单调 Tween Curve 编辑器；内部锚点可新增、拖动、键盘微调或删除，固定端点始终保持 0 到 100 百分比范围。",
            "每次曲线手势只生成一条撤销记录；拖动、新增和删除的弹性反馈只影响界面动画，不改变确定性保存的曲线值。",
            "补间元数据、缓动锚点、帧移动后的区间、中间物化状态、快照、工程持久化和撤销会保持同步。",
            "绘制对象实例的变换与设置现在正确遵循 Auto Key：开启时物化播放头关键帧，关闭时修改保持曝光的来源关键帧，不再创建隐藏的播放头状态。"),
        new ReleaseNoteSection(
            "铅笔与绘制工作流",
            "Pencil 新增类似 Animate 的 0 到 100 百分比 Smoothing；数值越高，越会在保留首尾点和误差边界的前提下用更少、稳定的三次贝塞尔段拟合指针轨迹。",
            "绘制后的 Pencil 描边可使用三次贝塞尔锚点和控制柄编辑，正常参与交点及拓扑切分；选框穿过描边时可切出并只选择框内的开放曲线部分。",
            "Fill 工具创建封闭区域填色时，会把同图层已有填色边缘与描边共同作为边界。",
            "Line 拖动预览改为使用当前 Stroke 颜色和实际线宽，并修复近期工具及渲染调整后绘制预览消失的问题。"),
        new ReleaseNoteSection(
            "性能、发布与兼容性",
            "长时间连续使用 Mixing Brush 时，会根据活动工作网格规模自适应降低预览重建频率，避免单笔越画越慢，同时不改变最终顶点色。",
            "正式发布物为唯一一个可替换的 VectorAnimationEngine-0.1.12-win-x64.exe，并继续保持小于 5 MiB。",
            "替换 EXE 后会原子刷新 .V2DEngine，同时保留兼容的 .Runtime、日志、工程和用户数据；旧工程继续使用兼容默认值打开。",
            "支持 Windows x64；首次安装运行环境需要网络连接，当前正式 EXE 尚未进行代码签名。"));

    private static LocalizedReleaseNote English011() => new(
        "Natural Mixing Brush and a more capable asset Vault",
        "Version 0.1.11 introduces real-time optical and pigment paint mixing, connected-region editing, and a refined tag-driven asset workflow.",
        new ReleaseNoteSection(
            "Mixing Brush",
            "The new Mixing Brush samples paint on the active layer and supports linear-light optical mixing or Kubelka-Munk pigment mixing with adjustable strength, viscosity, paint load, influence, size, and edge softness.",
            "Fixed-distance region vertices store only the final real-time ARGB result and brush geometry. Soft alpha edges and premultiplied linear-light interpolation avoid hard bands without storing generated gradients.",
            "Repeated passes recompute overlapped paint, optical deposits accumulate coverage, and pigment deposits preserve continuous transition colors while carried paint gradually returns to the current Fill color.",
            "Incremental rasterization, cached sampling candidates, color conversion tables, and merged region results reduce preview and repaint cost while preserving deterministic output."),
        new ReleaseNoteSection(
            "Mixing-region editing",
            "Each gesture is undoable with Ctrl+Z, and the Eraser can trim or split Mixing Brush paint when Fill erasing is enabled.",
            "Click and marquee selection use actual painted coverage. A connected island inside a merged mixing object can be selected, moved, transformed, deleted, copied, and pasted without selecting unrelated islands.",
            "Editing a selected island materializes only that region, preserves draw order and timeline ownership, and can be reverted to the previous merged result with Undo.",
            "Legacy trajectory-based Mixing Brush data remains readable and renderable."),
        new ReleaseNoteSection(
            "Vault and tags",
            "The Vault now provides a denser professional asset list with stable hover previews, text search, an independent tag filter, and smoother targeted row updates.",
            "Asset right-click menus assign existing project tags without closing the tag submenu after each toggle, and assigned tags appear as compact colored dots beside the asset name.",
            "A new tag starts as New tag with a random color; the manager exposes direct RGB sliders and numeric values for color adjustment.",
            "Tag definitions, colors, assignments, filtering, and search by tag name persist with the project and restart restoration."),
        new ReleaseNoteSection(
            "Release and compatibility",
            "The formal package is the single replaceable VectorAnimationEngine-0.1.11-win-x64.exe and remains smaller than 5 MiB.",
            "Replacing the EXE atomically refreshes .V2DEngine while preserving a compatible .Runtime, logs, projects, and user data.",
            "Older projects remain supported; missing tag and Mixing Brush region data use compatible defaults, and legacy Mixing Brush trajectories continue to render.",
            "Windows x64 is supported. First-time runtime provisioning requires network access, and the formal EXE is not code-signed."));

    private static LocalizedReleaseNote SimplifiedChinese011() => new(
        "更自然的混色笔刷与更完善的素材库",
        "0.1.11 新增实时光学/颜料混色、混色连通区域编辑，并完善基于标签的素材管理流程。",
        new ReleaseNoteSection(
            "混色笔刷",
            "新增 Mixing Brush，可采样活动图层上的已有颜色，并以线性光光学混色或 Kubelka-Munk 颜料混色工作；支持调节混色力度、粘性、载色量、影响因子、笔刷大小和边缘柔度。",
            "笔迹以固定距离区域顶点保存最终实时 ARGB 色值和笔刷几何，不保存生成渐变；透明柔边与预乘线性光插值可避免生硬边缘和色彩层次。",
            "反复涂抹会重新计算重叠区域；光学模式可稳定叠加同色覆盖，颜料模式保留连续过渡色，离开采样颜色后携带色会逐渐恢复到当前 Fill 颜色。",
            "增量栅格化、采样候选缓存、颜色转换表和区域结果合并降低了预览及反复涂抹开销，同时保持确定性输出。"),
        new ReleaseNoteSection(
            "混色区域编辑",
            "每次混色笔迹都可使用 Ctrl+Z 撤销；启用 Fill 擦除时，Eraser 可以裁切混色区域或将中段擦除后的内容拆成有序片段。",
            "点击与框选按真实上色覆盖命中；合并混色对象中的单个连通岛可独立选择、移动、变形、删除、复制和粘贴，不会连带选中无关区域。",
            "首次编辑所选岛时只物化该局部区域，并保留绘制顺序与时间轴归属；Undo 可恢复编辑前的合并结果。",
            "旧工程中按轨迹保存的 Mixing Brush 数据继续兼容读取与渲染。"),
        new ReleaseNoteSection(
            "素材库与标签",
            "Vault 使用更紧凑、专业的素材列表，提供稳定的真实几何悬停预览、文字搜索、独立标签筛选以及更平滑的局部行重绘。",
            "素材右键菜单可从已有项目标签中直接勾选；每次切换后标签子菜单保持打开，已分配标签以紧凑彩色圆点显示在素材名称右侧。",
            "新标签默认命名为 New tag 并使用随机颜色；标签管理器直接提供 RGB 滑杆和数值框调色。",
            "标签定义、颜色、素材分配、标签筛选和按标签名称搜索会随工程及编辑器重启恢复持久保存。"),
        new ReleaseNoteSection(
            "发布与兼容性",
            "正式发布物为唯一一个可替换的 VectorAnimationEngine-0.1.11-win-x64.exe，并继续保持小于 5 MiB。",
            "替换 EXE 后会原子刷新 .V2DEngine，同时保留兼容的 .Runtime、日志、工程和用户数据。",
            "旧工程继续受支持；缺少标签或混色区域数据时会使用兼容默认值，旧版混色笔刷轨迹仍可正常渲染。",
            "支持 Windows x64；首次安装运行环境需要网络连接，当前正式 EXE 尚未进行代码签名。"));

    private static LocalizedReleaseNote English019() => new(
        "Complete layer blending and clearer timeline display controls",
        "Version 0.1.9 adds all 27 layer blend modes for drawing and scene layers, strengthens layer display workflows, and fixes Inspector composition and layout regressions.",
        new ReleaseNoteSection(
            "Layer blend modes",
            "The Inspector now exposes Normal plus all darken, lighten, contrast, difference, component, Subtract, Divide, and deterministic Dissolve modes for the selected drawing or scene layer.",
            "Ctrl/Shift multi-selection applies one blend mode to every selected layer and creates one undo entry.",
            "Masks clip their content before blending, while folders and scene layers composite as isolated groups so the selected mode applies once to the complete group.",
            "Blend modes persist through project snapshots, SVG and Vault data, duplication, nested composition, and application restart; legacy projects default missing values to Normal."),
        new ReleaseNoteSection(
            "Correct compositing and rendering",
            "Non-Normal frames use an exact premultiplied-alpha software compositor, preserving every lower layer as the blend backdrop instead of making lower content disappear.",
            "Normal-only frames continue to use the Direct2D hardware path, and switching modes automatically selects the correct renderer without recording a graphics failure.",
            "Dissolve is deterministic for the same layer and pixel position, while masks, opacity, fill-before-stroke order, onion skin, underlays, and drag previews keep their established ordering."),
        new ReleaseNoteSection(
            "Layer color, Outline, and Inspector fixes",
            "Timeline layer-color swatches now open a themed RGB and Hex editor with live preview, presets, Apply/Cancel behavior, multi-layer editing, and one undo entry.",
            "Drawing, folder, and scene layers can use a non-destructive Outline display in their layer color; folder and scene states propagate through nested content without changing object materials.",
            "The right Inspector reserves space for the layer blend panel, restoring the Fill and Stroke color target controls that were hidden by the panel layout.",
            "Onion skin is now a drawing-timeline-wide switch: visible unlocked layers participate, locked layers are excluded, and outlined layers retain warm/cool outline previews."),
        new ReleaseNoteSection(
            "Editing, compatibility, and release",
            "Self-intersecting Fill boundaries expose real intersection anchors, preserve exact cubic links where possible, and keep connected branches synchronized during editing.",
            "The project format remains backward compatible; unsupported or invalid durable blend values are rejected, while older files without blend data open as Normal.",
            "The formal package is the single replaceable VectorAnimationEngine-0.1.9-win-x64.exe, remains smaller than 5 MiB, and atomically refreshes .V2DEngine while preserving a compatible .Runtime, logs, projects, and user data.",
            "Windows x64 is supported. First-time runtime provisioning requires network access, and the formal EXE is not code-signed."));

    private static LocalizedReleaseNote SimplifiedChinese019() => new(
        "完整图层混合与更清晰的时间轴显示控制",
        "0.1.9 为绘制图层和场景图层加入全部 27 种混合模式，完善图层显示工作流，并修复检查器合成与布局回归。",
        new ReleaseNoteSection(
            "图层混合模式",
            "右侧检查器现在为选中的绘制图层或场景图层提供 Normal，以及全部变暗、变亮、对比、差值、分量、Subtract、Divide 和确定性 Dissolve 模式。",
            "通过 Ctrl/Shift 多选图层后可一次应用同一混合模式，并只生成一条撤销记录。",
            "遮罩会先裁切内容再参与混合；文件夹和场景图层按隔离组整体合成，使所选模式只对完整组应用一次。",
            "混合模式会随工程快照、SVG、Vault、复制、嵌套组合和应用重启持久保存；旧工程缺少该数据时默认使用 Normal。"),
        new ReleaseNoteSection(
            "正确的合成与渲染",
            "包含非 Normal 模式的帧使用精确的预乘 Alpha 软件合成器，下层内容会继续作为混合背景参与计算，不再因混合而消失。",
            "全部为 Normal 的帧继续使用 Direct2D 硬件路径；切换模式时会自动选择正确渲染器，也不会被记录为图形故障。",
            "同一图层与像素位置的 Dissolve 结果保持确定性；遮罩、不透明度、先填色后描边顺序、洋葱皮、底图和拖拽预览继续遵守既有层级。"),
        new ReleaseNoteSection(
            "图层颜色、Outline 与检查器修复",
            "时间轴图层色块现在会打开主题化 RGB 与 Hex 编辑器，支持实时预览、预设色、Apply/Cancel、多图层编辑，并只生成一次撤销。",
            "绘制图层、文件夹和场景图层可以使用非破坏性 Outline 显示；文件夹和场景状态会传递到嵌套内容，但不会修改对象材质。",
            "右侧检查器会为图层混合面板正确保留布局空间，恢复此前被遮挡的 Fill 与 Stroke 颜色目标控件。",
            "洋葱皮改为绘制时间轴全局开关：所有可见且未锁定图层自动参与，锁定层被排除，Outline 图层继续显示暖色/冷色轮廓预览。"),
        new ReleaseNoteSection(
            "编辑、兼容性与发布",
            "自相交 Fill 边界会显示真实交点锚点，在可行时保留精确三次贝塞尔关联，并在编辑时同步连接分支。",
            "工程格式保持向后兼容；无法识别或非法的持久混合值会被拒绝，旧文件没有混合数据时按 Normal 打开。",
            "正式发布物为唯一一个可替换的 VectorAnimationEngine-0.1.9-win-x64.exe，继续小于 5 MiB，并在保留兼容 .Runtime、日志、工程和用户数据的同时原子刷新 .V2DEngine。",
            "支持 Windows x64；首次安装运行环境需要网络连接，当前正式 EXE 尚未进行代码签名。"));

    private static LocalizedReleaseNote English017() => new(
        "Reliable multi-intersection fill anchors and deterministic hot updates",
        "Version 0.1.7 keeps shared fill anchors stable across multiple stroke intersections and makes formal EXE hot updates rebuild the embedded application predictably while reusing a valid runtime.",
        new ReleaseNoteSection(
            "Multi-intersection fill editing",
            "Dragging a durable fill anchor now materializes every visible boundary interval that shares it, so both sides participate in the same edit.",
            "When two fill-boundary intersections lie on one cutting stroke, dragging the anchor between them changes both interior boundary segments without changing the source boundary outside either intersection.",
            "Virtual intersection parameters are refreshed against the current cubic during pointer movement, keeping both intersection anchors attached to the cutting stroke.",
            "Straight cubic subdivision now quantizes the final intersection anchor once and preserves exact linear geometry, preventing tiny residual crossings or inactive-looking anchors."),
        new ReleaseNoteSection(
            "Formal EXE hot updates",
            "The formal package is the single VectorAnimationEngine-0.1.7-win-x64.exe and remains smaller than 5 MiB.",
            "Every formal EXE launch atomically regenerates .V2DEngine from that EXE's embedded payload, so replacing the EXE deterministically deploys the tested application body.",
            "A complete compatible .Runtime is reused without download or replacement; a missing or incomplete runtime still enters the verified Microsoft provisioning flow.",
            "Bootstrap self-tests now verify repeated application deployment, stale-file removal, runtime reuse, embedded metadata, archive safety, and rollback-capable directory replacement."),
        new ReleaseNoteSection(
            "Compatibility and validation",
            "To test the update, keep the 0.1.6 EXE and its external .Runtime, close the application, then launch the 0.1.7 EXE from the same writable directory.",
            "The project file format is unchanged, and existing logs, projects, user data, and a valid local runtime remain external and preserved.",
            "The fill-boundary regression covers repeated movement of a shared anchor across two intersections while checking the protected outer cubic remains byte-for-byte geometrically unchanged.",
            "Windows x64 is supported. First-time runtime provisioning requires network access, and the formal EXE is not code-signed."));

    private static LocalizedReleaseNote SimplifiedChinese017() => new(
        "可靠的多交点填色锚点与确定性热更新",
        "0.1.7 修复共享填色锚点跨多个描边交点时的编辑范围，并让正式 EXE 在复用有效运行环境的同时稳定重建内嵌程序主体。",
        new ReleaseNoteSection(
            "多交点填色编辑",
            "拖动真实填色锚点时会先实体化所有与它相接的可见边界区间，使锚点两侧都参与同一次编辑。",
            "当同一切割描边与填色边界形成两个交点时，拖动两者之间的锚点会同步调整内部两段，不再改变任一交点外侧的原始边界。",
            "指针移动期间会根据当前三次贝塞尔曲线刷新虚拟交点参数，使两个交点锚点始终贴合切割描边。",
            "直线三次贝塞尔拆分改为只对最终交点锚点取整并保持严格直线，避免交点附近产生极短残余交叉或看似失效的锚点。"),
        new ReleaseNoteSection(
            "正式 EXE 热更新",
            "正式发布物为唯一一个 VectorAnimationEngine-0.1.7-win-x64.exe，并继续保持小于 5 MiB。",
            "正式 EXE 每次启动都会从自身内嵌负载原子重建 .V2DEngine，因此替换 EXE 后会确定性部署当前测试版本的软件主体。",
            "完整兼容的 .Runtime 会直接复用，不下载也不替换；缺失或不完整时仍进入经过校验的 Microsoft 运行环境安装流程。",
            "启动器自检现会覆盖重复部署、过期文件清除、运行环境复用、内嵌元数据、压缩路径安全和可回滚目录替换。"),
        new ReleaseNoteSection(
            "兼容性与验证",
            "测试更新时可保留 0.1.6 EXE 及其外部 .Runtime，关闭软件后，在同一个可写目录中启动 0.1.7 EXE。",
            "工程文件格式没有变化；现有日志、工程、用户数据和有效本地运行环境仍位于 EXE 外部并继续保留。",
            "填色边界回归会连续移动跨两个交点的共享锚点，并逐次确认受保护的外侧三次贝塞尔几何完全不变。",
            "支持 Windows x64；首次安装运行环境需要网络连接，当前正式 EXE 尚未进行代码签名。"));

    private static LocalizedReleaseNote English016() => new(
        "Stable shared-junction editing and smoother fill-boundary interaction",
        "Version 0.1.6 focuses on predictable repeated editing where fill boundaries and strokes meet, while reducing redundant geometry and overlay work during pointer drags.",
        new ReleaseNoteSection(
            "Local fill-boundary editing",
            "A selected Fill part stays attached to the intended region through pointer-down materialization, dragging, commit, cancel, undo, and anchor insertion or deletion.",
            "Open portions of a fill boundary expose their complete anchors and cubic controls, while sections fully covered by a same-layer closed stroke remain hidden from fill-edge editing.",
            "Ctrl+left-click inserts a cubic anchor at the clicked curve position and can continue directly into a drag; Ctrl+right-click removes a real anchor while preserving the minimum closed contour.",
            "Dragging an endpoint that belongs to straight adjacent edges keeps those cubic controls collinear, while existing curved edges retain their control geometry."),
        new ReleaseNoteSection(
            "Shared junctions and connected Lines",
            "Fill boundaries, independent Lines, stroked Paths, and closed freeform strokes now use the same shared-junction rules without assigning priority to either side.",
            "Dragging a shared endpoint from either the fill side or the stroke side updates every captured endpoint to the same quantized position.",
            "An independent Line crossing a fill boundary splits into exact cubic subcurves when required; path-backed outlines and closed freeform strokes keep the junction inside their original object.",
            "Ctrl-drag from a Line endpoint creates a connected branch. Ctrl+Alt-drag from the Line interior creates an exact branch anchor, and Shift temporarily enables angle snapping."),
        new ReleaseNoteSection(
            "Topology and repeated-edit stability",
            "A coincident Line receives one synchronization role per drag: full-curve synchronization for a matching boundary, or endpoint-only synchronization for a junction.",
            "Repeated fill-side edits recapture current topology without splitting an existing shared endpoint again, preventing stale control points and unintended duplicate or ghost Lines.",
            "Fill overlap normalization reconstructs matched cubic subcurves and simplifies only unmatched connectors, preserving holes, corners, and real intersections without exposing dense render samples as editable anchors.",
            "Overlapping solid contours are normalized as solid regions at commit instead of becoming unintended even-odd cutouts, and same-color fill merging remains part of the same undo step."),
        new ReleaseNoteSection(
            "Performance and feedback",
            "Pointer events that resolve to the same quantized cubic now skip repeated path mutation, shared-junction updates, overlay reconstruction, and Stage invalidation.",
            "Fill-edge overlay refresh reads lightweight cubic controls directly instead of adaptively sampling every path segment or building a per-frame lookup dictionary.",
            "MainForm transfers its newly built overlay array to the Stage once; identical overlays no longer invalidate Direct2D geometry or restart selection-highlight evaluation.",
            "The offline bilingual Release Notes viewer now includes the 0.1.6 announcement while retaining older entries, and continues to follow the current interface language and color theme."),
        new ReleaseNoteSection(
            "Release and compatibility",
            "The formal package is the single replaceable VectorAnimationEngine-0.1.6-win-x64.exe and remains smaller than 5 MiB.",
            "Close the application and replace the previous formal EXE to update. The embedded application refreshes .V2DEngine atomically while retaining compatible .Runtime, logs, projects, and user data.",
            "Windows x64 is supported. First-time automatic .NET 8 runtime installation requires network access and a writable application directory.",
            "The project file format is unchanged. Complex SVG rasterized break-apart results retain their existing finite-precision limitations, and the formal EXE is not code-signed."));

    private static LocalizedReleaseNote SimplifiedChinese016() => new(
        "稳定的共享交点编辑与更流畅的填色边界操作",
        "0.1.6 重点改善填色边界与描边交汇处的连续编辑稳定性，并减少拖拽期间重复的几何与覆盖层计算。",
        new ReleaseNoteSection(
            "局部填色边界编辑",
            "选中的 Fill part 在按下时拓扑物化、拖动、提交、取消、撤销以及锚点增删过程中会继续关联原有填色区域。",
            "填色边界的开放部分会显示完整锚点和三次贝塞尔控制点；被同层封闭描边完整贴合的部分继续从填色边缘调整中隐藏。",
            "按住 Ctrl 左键点击曲线可在点击位置插入锚点并直接继续拖动；按住 Ctrl 右键可删除真实锚点，同时保证闭合轮廓的最少节点数。",
            "拖动与相邻直边相接的端点时会保持对应贝塞尔控制点共线；原本已经弯曲的边段继续保留控制几何。"),
        new ReleaseNoteSection(
            "共享交点与连接线",
            "填色边界、独立 Line、带描边 Path 和闭合自由线使用同一套共享交点规则，不再区分填色侧或线段侧的优先级。",
            "无论从填色侧还是描边侧拖动共享端点，所有已捕获端点都会同步到同一个量化坐标。",
            "独立 Line 穿过填色边界时会按需要拆分为精确三次贝塞尔子曲线；Path 自带描边和闭合自由线的交点仍保留在原对象内部。",
            "按住 Ctrl 从 Line 端点拖动可创建连接分支；按住 Ctrl+Alt 从 Line 中部拖动可建立精确分支锚点，Shift 可临时启用角度吸附。"),
        new ReleaseNoteSection(
            "拓扑与连续编辑稳定性",
            "同一条贴合 Line 在一次拖拽中只承担一种联动角色：整段匹配时同步完整曲线，仅端点相接时只同步共享端点。",
            "从填色侧连续编辑时会重新捕获当前拓扑，不会再次拆分已经存在的共享端点，从而避免失效控制点、重复线段和幽灵线段。",
            "填色重叠归一化会重建可匹配的三次贝塞尔子曲线，仅简化无法匹配的连接边；孔洞、尖角和真实交点不会变成密集渲染采样锚点。",
            "实心轮廓重叠会在提交时归一化为实心区域，不再意外产生偶奇镂空；同色填色合并仍与本次编辑共用一个撤销步骤。"),
        new ReleaseNoteSection(
            "性能与反馈",
            "连续指针事件落在同一个量化曲线位置时，会跳过重复的路径修改、共享交点更新、覆盖层重建和舞台失效。",
            "填色边缘覆盖层直接读取轻量三次曲线控制点，不再为每个路径段执行自适应采样或每帧建立查询字典。",
            "MainForm 新建的覆盖层数组只向舞台移交一次；相同覆盖层不再使 Direct2D 几何缓存失效，也不会重复计算选择高亮状态。",
            "离线双语“更新公告”查看器现已收录 0.1.6 公告并保留历史条目，同时继续跟随当前界面语言和色彩主题。"),
        new ReleaseNoteSection(
            "发布与兼容性",
            "正式发布物为唯一一个可替换的 VectorAnimationEngine-0.1.6-win-x64.exe，并继续保持小于 5 MiB。",
            "更新时先关闭软件并替换旧版正式 EXE；内嵌程序会原子刷新 .V2DEngine，同时保留兼容的 .Runtime、日志、工程和用户数据。",
            "支持 Windows x64；首次自动安装 .NET 8 运行环境需要网络连接和可写程序目录。",
            "工程文件格式没有变化；复杂 SVG 栅格化拆散仍受原有有限精度约束，当前正式 EXE 尚未进行代码签名。"));

    private static LocalizedReleaseNote English015() => new(
        "Editable content, nested animation, and a streamlined release workflow",
        "Version 0.1.5 expands editable content, nested animation, interface customization, and the formal release workflow while improving text rendering, scene composition, and per-layer instance queries.",
        new ReleaseNoteSection(
            "Text, shapes, and SVG",
            "A new editable text tool supports multiline content, in-stage editing, fonts and sizes, bold and italic styles, alignment, resizable text regions, and automatic wrapping.",
            "Text remains editable through Cels, copy and paste, undo, and project saves, and can be converted to a compound Path with Break Apart when outline editing is needed.",
            "Polygon and Star tools now expose side and point counts, while cubic Bezier boundary controls, anchor editing, and two-way fill-boundary linking have been expanded.",
            "Imported SVG remains a single-object workflow. Break Apart preserves basic vectors and text outlines where possible and converts complex effects to editable, finite-precision Paths."),
        new ReleaseNoteSection(
            "Instances, timeline, and projects",
            "Drawing-object instances now support independent FPS, play-once, loop, hold-frame, Alpha, and tint settings that compose through nested instances.",
            "Position, rotation, scale, skew, visibility, and appearance can be stored per keyframe. Shared-anchor edits compensate existing instances to reduce visual jumps.",
            "Scene layers can contain multiple instances while retaining stable timeline tracks, Cel ownership, and composition provenance.",
            "Project saves continue to use transaction logs, complete read-back verification, and SHA-256 validation so interrupted writes can recover the previous complete project."),
        new ReleaseNoteSection(
            "Workbench and shortcuts",
            "The shortcut-profile editor provides Traditional Flash, Number Keys, and custom profiles with conflict detection and persistent user settings.",
            "Dark and white themes, H/S/B theme and highlight adjustments, live preview, and a Simplified Chinese interface are now available.",
            "Shape, text, instance, color, and timeline panels have been reorganized around compact workflows and hot-reload refresh behavior.",
            "Release Notes is now available from the main menu for offline access to the current update announcement."),
        new ReleaseNoteSection(
            "Performance and stability",
            "Canonical text glyph outlines use a weak-reference cache, avoiding repeated font-path expansion and contour merging during repeated drawing.",
            "Per-layer scene and drawing-object instance queries use lazy indexes, avoiding repeated full scans and array allocations during stable playback and composition rebuilds.",
            "Scene composition, spatial queries, LOD bitmaps, Direct2D caches, pressure-brush previews, and complex marquee interaction continue to be optimized.",
            "GDI fallback remains available after Direct2D failures, with stronger module hot reload, restart restoration, and startup logging."),
        new ReleaseNoteSection(
            "Release and updating",
            "The formal release is a single replaceable VectorAnimationEngine-0.1.5-win-x64.exe smaller than 5 MiB.",
            "The EXE embeds the complete compressed application. First launch, or replacing it with a newer EXE, atomically refreshes the adjacent .V2DEngine directory.",
            "The local .Runtime remains reusable. When compatible .NET 8 Core and Windows Desktop x64 runtimes are missing, the launcher can download, verify, and install them from Microsoft services.",
            "To update, close the application, replace the formal release EXE, and launch it again. Existing projects, logs, and the compatible local runtime are retained."),
        new ReleaseNoteSection(
            "Compatibility notes",
            "Windows x64 is supported. Automatic first-time runtime installation requires network access and a writable application directory.",
            "Breaking apart complex SVG effects produces a finite-precision outline of the rendered result, so extreme zoom may reveal minor edge or color-band differences.",
            "Missing fonts fall back to an available sans-serif font. Install matching fonts when projects move between devices.",
            "The current formal release EXE is not code-signed."));

    private static LocalizedReleaseNote SimplifiedChinese015() => new(
        "可编辑内容、嵌套动画与正式发布流程",
        "0.1.5 重点完善了可编辑内容、嵌套动画、界面定制和正式发布流程，并针对文本绘制、场景组合及按层实例查询进行了性能优化。",
        new ReleaseNoteSection(
            "文本、形状与 SVG",
            "新增可编辑文本工具，支持多行内容、舞台内二次编辑、字体与字号、粗体/斜体、左右及居中对齐、文本区域宽度调整和自动换行。",
            "文本可随 Cel、复制粘贴、撤销和工程文件完整保存；需要轮廓编辑时，可通过“拆散”转换为复合 Path。",
            "Polygon 和 Star 新增边数/尖角数设置；填色边界和线条的三次贝塞尔控制、锚点增删及双向边界联动进一步完善。",
            "SVG 导入继续保持单对象工作流；“拆散”现在可优先保留基础矢量和文字轮廓，并将复杂效果转换为有限精度的可编辑 Path。"),
        new ReleaseNoteSection(
            "实例、时间轴与工程",
            "绘制对象实例支持独立播放 FPS、单次播放、循环、停留帧、Alpha 和色调；嵌套实例会逐层继承播放和外观状态。",
            "实例位置、旋转、缩放、倾斜、可见性和外观可按关键帧保存；共享锚点调整会自动补偿已有实例，减少画面跳动。",
            "场景图层可容纳多个实例，并保持稳定的时间轴轨道、Cel 归属和组合来源信息。",
            "工程保存继续使用事务日志、完整回读与 SHA-256 校验，异常中断后可恢复上一份完整工程。"),
        new ReleaseNoteSection(
            "工作台与快捷键",
            "新增快捷键方案编辑器，提供 Traditional Flash、Number Keys 和自定义方案；自定义绑定会检测冲突并保存到用户设置。",
            "新增深色/白色主题、主题色与高亮色 H/S/B 调整、实时预览和简体中文界面。",
            "形状、文本、实例、颜色和时间轴相关面板经过整理，常用工作流保持紧凑并支持热重载刷新。",
            "主菜单新增“更新公告”，无需联网即可在软件内查看当前版本的重要变化和更新说明。"),
        new ReleaseNoteSection(
            "性能与稳定性",
            "文本规范字形轮廓采用弱引用缓存，重复绘制不再反复执行字体路径展开和轮廓合并。",
            "场景和绘制对象的按层实例查询改为惰性索引，稳定播放和组合重建期间不再为每层重复扫描、分配数组。",
            "场景组合、空间查询、LOD 位图、Direct2D 缓存、压感笔预览和复杂选框交互继续优化。",
            "Direct2D 失败时仍保留 GDI 回退；模块热重载、工程重启恢复和启动日志得到加强。"),
        new ReleaseNoteSection(
            "发布与更新",
            "正式发布物调整为唯一一个小于 5 MiB 的 VectorAnimationEngine-0.1.5-win-x64.exe。",
            "EXE 内嵌完整压缩软件主体；首次启动或替换新版 EXE 后，会原子更新同目录 .V2DEngine。",
            "本地 .Runtime 会持续复用；缺少兼容的 .NET 8 Core/Windows Desktop x64 运行环境时，启动器可从 Microsoft 官方服务下载并校验后安装。",
            "更新时先关闭软件，在正式程序目录中替换 EXE 后重新启动；已有工程、日志和兼容的本地运行环境会继续保留。"),
        new ReleaseNoteSection(
            "兼容性与已知说明",
            "支持 Windows x64；首次自动安装运行环境需要网络连接和可写目录。",
            "复杂 SVG 的拆散结果是对最终渲染效果的有限精度轮廓化，高倍缩放时可能出现轻微边缘或颜色分级差异。",
            "缺失字体会回退到可用的无衬线字体；跨设备协作时建议安装相同字体。",
            "当前正式 EXE 尚未进行代码签名。"));
}
