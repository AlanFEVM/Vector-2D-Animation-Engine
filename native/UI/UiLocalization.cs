using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

internal enum UiLanguage
{
    English,
    SimplifiedChinese
}

internal static class UiLocalization
{
    private sealed class TextState(
        string sourceText,
        string sourceAccessibleName,
        string sourceAccessibleDescription,
        string sourcePlaceholderText = "")
    {
        public string SourceText { get; set; } = sourceText;
        public string SourceAccessibleName { get; set; } = sourceAccessibleName;
        public string SourceAccessibleDescription { get; set; } = sourceAccessibleDescription;
        public string SourcePlaceholderText { get; set; } = sourcePlaceholderText;
    }

    private static readonly ConditionalWeakTable<Control, TextState> ControlStates = new();
    private static readonly ConditionalWeakTable<ToolStripItem, TextState> ItemStates = new();
    private static readonly ConditionalWeakTable<ToolStrip, object> WatchedToolStrips = new();
    private static readonly List<WeakReference<Control>> ControlRoots = [];
    private static readonly List<WeakReference<ToolStrip>> ToolStripRoots = [];
    private static bool _applying;

    private static readonly IReadOnlyDictionary<string, string> SimplifiedChinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Follow scene"] = "跟随场景",
            ["Camera aspect ratio"] = "镜头宽高比",
            ["Click to rename project"] = "单击重命名工程",
            ["Aspect ratio"] = "宽高比",
            ["General"] = "常规",
            ["Minimum camera frame: 1920 x 1080 vu; aspect ratio is preserved."] = "镜头取景框最小为 1920 × 1080 vu，并保持宽高比。",
            ["Start here: draw and edit a reusable symbol."] = "从这里开始：绘制并编辑可重复使用的元件。",
            ["Arrange symbols in a scene, then animate them with keyframes."] = "把元件放入场景，再用关键帧制作动画。",
            ["Set up cameras and frame your shots on the scene timeline."] = "在场景时间轴上设置相机，为镜头构图。",
            ["Enable or pause snapping. Your individual snap options are kept."] = "开启或暂停吸附；各项吸附设置会保留。",
            ["Place points on the grid. Set the spacing with the vu field."] = "让点对齐网格；使用 vu 数值框设置间距。",
            ["Snap to nearby object points while drawing or moving."] = "绘制或移动时，吸附到附近的对象点。",
            ["Use alignment guides while drawing."] = "绘制时使用对齐辅助线。",
            ["Constrain angles to the step in the degrees field."] = "按角度数值框中设置的步长约束角度。",
            ["Grid spacing in vector units (vu). Smaller values give a finer grid."] = "网格间距，单位为矢量单位（vu）；数值越小，网格越密。",
            ["Angle step in degrees. For example, 15 gives 15, 30, 45 degrees."] = "角度步长，例如设为 15 时按 15、30、45 度等角度吸附。",
            ["Turn on Snap first to use this option."] = "请先开启“吸附”总开关，此选项才会生效。",
            ["Move camera in view plane"] = "在当前视图平面移动相机",
            ["Move camera along X"] = "沿 X 轴移动相机",
            ["Move camera along Y"] = "沿 Y 轴移动相机",
            ["Move camera along Z"] = "沿 Z 轴移动相机",
            ["Rotate camera around X"] = "绕 X 轴俯仰相机",
            ["Rotate camera around Y"] = "绕 Y 轴转动相机",
            ["Roll camera around Z"] = "绕 Z 轴滚转相机",
            ["Adjust focal length"] = "调整焦距",
            ["Adjust orthographic size"] = "调整正交取景尺寸",
            ["Move shot frame"] = "移动取景框",
            ["Rotate shot frame"] = "旋转取景框",
            ["Resize shot frame"] = "缩放取景框",
            ["Shift: fine adjustment · Ctrl: unsnap · Esc: cancel"] = "Shift：精细调整 · Ctrl：取消吸附 · Esc：取消",
            ["Drag to adjust · Esc: cancel"] = "拖动调整 · Esc：取消",
            ["Enable MCP integration"] = "启用 MCP 接口",
            ["Allow editor changes"] = "允许修改编辑器",
            ["Local port"] = "本地端口",
            ["Bearer token (optional)"] = "访问令牌（可选）",
            ["Server address"] = "服务地址",
            ["Codex configuration"] = "Codex 连接配置",
            ["Current server status"] = "当前服务状态",
            ["Stopped"] = "已停止",
            ["Listening"] = "正在监听",
            ["Connect Codex or another MCP client to this running editor."] = "让 Codex 或其他 MCP 客户端连接到正在运行的编辑器。",
            ["Without write access, clients can only read state and settings. Save applies connection changes; Cancel discards them."] = "关闭修改权限时，客户端只能读取状态和设置。保存后应用连接设置，取消则放弃更改。",
            ["Add this to Codex config.toml, or add a Streamable HTTP server using the address above. Keep the editor running. If a token is set, define VECTOR2D_MCP_TOKEN in the Codex process environment."] = "将上方配置添加到 Codex 的 config.toml，或使用服务地址添加 Streamable HTTP 服务器。保持编辑器运行。设置令牌后，需在 Codex 的进程环境中定义 VECTOR2D_MCP_TOKEN。",
            ["A shortcut profile already uses that name."] = "已有快捷键方案使用该名称。",
            ["Add"] = "新增",
            ["All tags"] = "全部标签",
            ["Terrain"] = "地形",
            ["Create timeline tab group"] = "新建时间轴图层组",
            ["More timeline tab groups"] = "更多时间轴图层组",
            ["Timeline tab group actions"] = "时间轴图层组操作",
            ["Timeline tab groups"] = "时间轴图层组",
            ["Move to tab group"] = "移至图层组",
            ["Rename tab group..."] = "重命名图层组...",
            ["Delete tab group"] = "删除图层组",
            ["Rename timeline tab group"] = "重命名时间轴图层组",
            ["Delete timeline tab group"] = "删除时间轴图层组",
            ["New Timeline Tab Group"] = "新建时间轴图层组",
            ["Rename Timeline Tab Group"] = "重命名时间轴图层组",
            ["Delete Timeline Tab Group"] = "删除时间轴图层组",
            ["Group name"] = "组名称",
            ["Timeline tab group name"] = "时间轴图层组名称",
            ["Tab group color..."] = "图层组颜色...",
            ["Reset tab group color"] = "恢复默认组颜色",
            ["Tab Group Color"] = "图层组颜色",
            ["Edit '{0}'"] = "编辑“{0}”",
            ["Rename '{0}'..."] = "重命名“{0}”...",
            ["Delete '{0}'"] = "删除“{0}”",
            ["Delete the timeline tab group '{0}'? Tracks in it will move to Layers."] = "删除时间轴图层组“{0}”？其中的图层将移回普通图层组。",
            ["Assign"] = "分配",
            ["Assigned"] = "已分配",
            ["Asset"] = "素材",
            ["Asset Tags"] = "素材标签",
            ["Assign tags to this asset or manage project tag colors."] = "为此素材分配标签，或管理项目标签颜色。",
            ["Choose color saturation and brightness"] = "选择颜色的饱和度和明度",
            ["CIELAB blue-yellow axis"] = "CIELAB b* 蓝-黄轴",
            ["CIELAB green-red axis"] = "CIELAB a* 绿-红轴",
            ["CIELAB lightness"] = "CIELAB L* 明度",
            ["Color hue in degrees"] = "颜色色相（度）",
            ["Color presets"] = "颜色预设",
            ["Delete Tag"] = "删除标签",
            ["Delete tag '{0}' from every project asset?"] = "确定从全部项目素材中删除标签“{0}”吗？",
            ["Filter project assets by tag"] = "按标签筛选项目素材",
            ["Manage tags for selected asset"] = "管理所选素材的标签",
            ["Manage Tags..."] = "管理标签...",
            ["New Tag"] = "新建标签",
            ["New color"] = "新颜色",
            ["No matching assets"] = "没有匹配的素材",
            ["Not assigned"] = "未分配",
            ["Original color"] = "原始颜色",
            ["Rename Tag"] = "重命名标签",
            ["RGB color"] = "RGB 颜色",
            ["Search assets or tags..."] = "搜索素材或标签...",
            ["Search project assets"] = "搜索项目素材",
            ["Set Tags"] = "设置标签",
            ["Set tags from existing project tags"] = "从已有项目标签中设置标签",
            ["Tag"] = "标签",
            ["Tag Color"] = "标签颜色",
            ["Tag name"] = "标签名称",
            ["Tag names must be unique and contain 1 to 64 characters."] = "标签名称必须唯一，且长度为 1 到 64 个字符。",
            ["Tags..."] = "标签...",
            ["Tags: {0}"] = "标签：{0}",
            ["The asset tag changes could not be applied."] = "无法应用素材标签更改。",
            ["The project tag limit has been reached."] = "已达到项目标签数量上限。",
            ["Unassign"] = "取消分配",
            ["Assign selected shortcut"] = "分配所选快捷键",
            ["Assign Shortcut"] = "分配快捷键",
            ["Apply"] = "应用",
            ["Adjust CIELAB blue-yellow axis"] = "调整 CIELAB b* 蓝-黄轴",
            ["Adjust CIELAB green-red axis"] = "调整 CIELAB a* 绿-红轴",
            ["Adjust CIELAB lightness"] = "调整 CIELAB L* 明度",
            ["Adjust brightness"] = "调整明度",
            ["Adjust hue"] = "调整色相",
            ["Adjust saturation"] = "调整饱和度",
            ["Brightness"] = "明度",
            ["Brush group"] = "笔刷组",
            ["Clear"] = "清除",
            ["Clear selected shortcut"] = "清除所选快捷键",
            ["Color theme"] = "色彩主题",
            ["Create shortcut profile"] = "新建快捷键方案",
            ["Create Shortcut Profile"] = "新建快捷键方案",
            ["Delete shortcut profile"] = "删除快捷键方案",
            ["Delete Shortcut Profile"] = "删除快捷键方案",
            ["Delete shortcut profile '{0}'?"] = "确定删除快捷键方案“{0}”吗？",
            ["Dark"] = "深色",
            ["Enter a unique profile name."] = "请输入唯一的方案名称。",
            ["Eraser"] = "橡皮擦",
            ["Edit color in CIELAB (D65)"] = "使用 CIELAB（D65）编辑颜色",
            ["Fill group"] = "填充组",
            ["Invalid shortcut"] = "无效快捷键",
            ["Highlight color"] = "高亮颜色",
            ["Line group"] = "线条组",
            ["New"] = "新建",
            ["No release notes are enabled for this build."] = "此版本未启用任何更新公告。",
            ["Pressure Brush"] = "压感笔刷",
            ["Profile"] = "方案",
            ["Profile name"] = "方案名称",
            ["Release Notes"] = "更新公告",
            ["Release Notes..."] = "更新公告...",
            ["Release notes for version {0}"] = "版本 {0} 的更新公告",
            ["Released: {0}"] = "发布日期：{0}",
            ["Rename shortcut profile"] = "重命名快捷键方案",
            ["Rename Shortcut Profile"] = "重命名快捷键方案",
            ["Selection group"] = "选择组",
            ["Shape group"] = "形状组",
            ["Shortcut Conflict"] = "快捷键冲突",
            ["Shortcut for {0}"] = "{0}的快捷键",
            ["Shortcut profile"] = "快捷键方案",
            ["Shortcut profile name"] = "快捷键方案名称",
            ["Shortcut profiles"] = "快捷键方案",
            ["The shortcut could not be assigned."] = "无法分配该快捷键。",
            ["The shortcut could not be cleared."] = "无法清除该快捷键。",
            ["Theme color"] = "主题颜色",
            ["This shortcut is overridden in some focused or 3D contexts."] = "在部分控件聚焦或 3D 场景中，此快捷键会被固定操作覆盖。",
            ["This shortcut is reserved by the application."] = "此快捷键已被应用程序保留。",
            ["Unassigned"] = "未分配",
            ["Up to 32 custom shortcut profiles can be created."] = "最多可创建 32 个自定义快捷键方案。",
            ["Use the dark color theme"] = "使用深色色彩主题",
            ["Use the white color theme"] = "使用白色色彩主题",
            ["White"] = "白色",
            ["{0} is already assigned to {1}. Replace it?"] = "{0} 已分配给 {1}。是否替换？",
            ["Accessibility"] = "辅助功能",
            ["Action"] = "操作",
            ["1000 layers, 100000 objects and 100000000 virtual atoms."] = "1000 个图层、100000 个对象和 100000000 个虚拟原子。",
            ["24 FPS looping timeline range for hand-drawn animation."] = "用于手绘动画的 24 FPS 循环时间轴范围。",
            ["2D scene with depth"] = "2D 纵深",
            ["3D scene"] = "3D 场景",
            ["3D Surface"] = "3D 表面",
            ["3D surface material"] = "3D 表面材质",
            ["Add light"] = "添加光源",
            ["Ambient Light"] = "环境光",
            ["Apply an optical material preset"] = "应用光学材质预设",
            ["Area Light"] = "区域光",
            ["Area light height"] = "区域光高度",
            ["Area light width"] = "区域光宽度",
            ["Area width"] = "区域宽度",
            ["Cast shadows"] = "投射阴影",
            ["Default"] = "默认",
            ["Direction"] = "方向",
            ["Directional Light"] = "直线光",
            ["Duplicate light"] = "复制光源",
            ["Edit lights in the active 3D scene"] = "编辑当前 3D 场景中的光源",
            ["Edit optical material overrides for selected 3D instances"] = "编辑所选 3D 实例的光学材质覆盖",
            ["Enabled"] = "启用",
            ["Glass"] = "玻璃",
            ["Height"] = "高度",
            ["Inherited default material"] = "继承默认材质",
            ["Instance material override"] = "实例材质覆盖",
            ["Intensity"] = "强度",
            ["Symbol Filters"] = "元件滤镜",
            ["Bevel"] = "斜角",
            ["Gradient Bevel"] = "渐变斜角",
            ["Gradient Glow"] = "渐变发光",
            ["Effect enabled"] = "效果已启用",
            ["Effect not added"] = "尚未添加此效果",
            ["Filter effect"] = "滤镜效果",
            ["Add filter effect"] = "添加滤镜效果",
            ["Remove filter effect"] = "移除滤镜效果",
            ["Start color"] = "起始颜色",
            ["End color"] = "结束颜色",
            ["Blur"] = "模糊",
            ["Glow"] = "发光",
            ["Drop Shadow"] = "投影",
            ["Blur X"] = "水平模糊",
            ["Blur Y"] = "垂直模糊",
            ["Opacity"] = "不透明度",
            ["Distance"] = "距离",
            ["Glow color"] = "发光颜色",
            ["Shadow color"] = "投影颜色",
            ["Order: Blur → Glow → Drop Shadow"] = "顺序：模糊 → 发光 → 投影",
            ["Combine effects. Sizes are stage pixels."] = "滤镜可叠加，尺寸以舞台像素计。",
            ["Mixed values. Edit one parameter at a time."] = "存在混合值，仅修改当前参数。",
            ["Effects run in order: blur, glow, then drop shadow."] = "滤镜按模糊、发光、投影的顺序执行。",
            ["With multiple symbols selected, only the edited parameter changes."] = "多选元件时，仅改变当前编辑的参数。",
            ["Blur horizontal radius"] = "水平模糊半径",
            ["Blur vertical radius"] = "垂直模糊半径",
            ["Glow horizontal radius"] = "发光水平半径",
            ["Glow vertical radius"] = "发光垂直半径",
            ["Glow intensity"] = "发光强度",
            ["Glow opacity"] = "发光不透明度",
            ["Shadow horizontal radius"] = "投影水平半径",
            ["Shadow vertical radius"] = "投影垂直半径",
            ["Shadow intensity"] = "投影强度",
            ["Shadow opacity"] = "投影不透明度",
            ["Shadow angle"] = "投影角度",
            ["Shadow distance"] = "投影距离",
            ["IOR"] = "折射率",
            ["Index of refraction"] = "折射率",
            ["Light color"] = "光源颜色",
            ["Light direction X"] = "光源方向 X",
            ["Light direction Y"] = "光源方向 Y",
            ["Light direction Z"] = "光源方向 Z",
            ["Light enabled"] = "启用光源",
            ["Light intensity"] = "光源强度",
            ["Light name"] = "光源名称",
            ["Light position X"] = "光源位置 X",
            ["Light position Y"] = "光源位置 Y",
            ["Light position Z"] = "光源位置 Z",
            ["Light range"] = "光源范围",
            ["Light type"] = "光源类型",
            ["Lighting"] = "光照",
            ["Material casts shadows"] = "材质投射阴影",
            ["Material metallic amount"] = "材质金属性",
            ["Material receives shadows"] = "材质接收阴影",
            ["Material reflectivity"] = "材质反射率",
            ["Material roughness"] = "材质粗糙度",
            ["Material transmission"] = "材质透光率",
            ["Matte"] = "哑光",
            ["Metal"] = "金属",
            ["Metallic"] = "金属性",
            ["Metallic value"] = "金属性数值",
            ["Mixed material values"] = "混合材质值",
            ["Name"] = "名称",
            ["No 3D instance selected"] = "未选择 3D 实例",
            ["Off"] = "关",
            ["On"] = "开",
            ["Optical index of refraction from 1 to 4"] = "光学折射率，范围 1 到 4",
            ["Override"] = "覆盖",
            ["Point Light"] = "点光源",
            ["Range"] = "范围",
            ["Receive shadows"] = "接收阴影",
            ["Reflectivity"] = "反射率",
            ["Reflectivity value"] = "反射率数值",
            ["Remove light"] = "删除光源",
            ["Reset light"] = "重置光源",
            ["Roughness"] = "粗糙度",
            ["Roughness value"] = "粗糙度数值",
            ["Scene lighting"] = "场景光照",
            ["Scene lights"] = "场景光源",
            ["Shadow softness"] = "阴影柔和度",
            ["Shadow strength"] = "阴影强度",
            ["Softness"] = "柔和度",
            ["Transmission"] = "透光率",
            ["Transmission value"] = "透光率数值",
            ["Use a material override on the selected instances"] = "为所选实例使用材质覆盖",
            ["Use material override"] = "使用材质覆盖",
            ["3D material preset"] = "3D 材质预设",
            ["3D transform floating panel"] = "3D 变换浮动面板",
            ["+ Instance"] = "+ 实例",
            ["+ Object"] = "+ 对象",
            ["+ Scene"] = "+ 场景",
            ["A symbol cannot contain itself or create a recursive containment cycle."] = "元件不能包含自身，也不能形成递归包含关系。",
            ["A project must retain at least one symbol."] = "项目必须至少保留一个元件。",
            ["A gradient preset needs at least two color stops."] = "渐变预设至少需要两个色标。",
            ["Add a color stop after the selected stop"] = "在所选色标后添加色标",
            ["Add current color to custom palette"] = "将当前颜色添加到自定义调色板",
            ["Add file reference to Vault"] = "向素材库添加文件引用",
            ["Add SVG Link"] = "添加 SVG 链接",
            ["Add SVG Link..."] = "添加 SVG 链接...",
            ["Asset categories"] = "素材分类",
            ["Basic Symbols"] = "基础元件",
            ["3D Symbols"] = "3D 元件",
            ["External SVG"] = "外部 SVG",
            ["Edit the selected instance position, rotation, scale, and pivots"] = "编辑所选实例的位置、旋转、缩放和锚点",
            ["Edit selected instance 3D transform"] = "编辑所选实例的 3D 变换",
            ["Missing link"] = "链接丢失",
            ["No basic symbols"] = "没有基础元件",
            ["No 3D symbols"] = "没有 3D 元件",
            ["No external SVG links"] = "没有外部 SVG 链接",
            ["No editable 3D transform selected"] = "未选择可编辑的 3D 变换",
            ["Relocate..."] = "重新定位...",
            ["Relocate SVG Link..."] = "重新定位 SVG 链接...",
            ["Use"] = "使用",
            ["Use selected SVG link"] = "使用所选 SVG 链接",
            ["Open a Basic Drawing or scene mask drawing layer before using an SVG link."] = "请先打开基础绘制或场景遮罩绘制图层，再使用 SVG 链接。",
            ["Remove SVG link \"{0}\" from the library?"] = "从素材库移除 SVG 链接“{0}”？",
            ["The link metadata is invalid or the library is full."] = "链接信息无效或素材库已满。",
            ["The selected path is invalid."] = "所选路径无效。",
            ["The SVG link could not be added."] = "无法添加 SVG 链接。",
            ["The SVG link could not be relocated."] = "无法重新定位 SVG 链接。",
            ["The SVG link is missing."] = "SVG 链接已丢失。",
            ["Add to Vault"] = "添加到素材库",
            ["Adjust alpha"] = "调整透明度",
            ["Adjust the hue of application surfaces"] = "调整软件界面表面的色调",
            ["Adjust the hue used for active and selected controls"] = "调整活动和选中控件使用的色调",
            ["Align"] = "对齐",
            ["Align Left"] = "左对齐",
            ["Align Center"] = "居中对齐",
            ["Align Right"] = "右对齐",
            ["All"] = "全部",
            ["All files"] = "所有文件",
            ["All alpha"] = "整体透明度",
            ["Alpha"] = "透明度",
            ["Analogous"] = "邻近色",
            ["Angle"] = "角度",
            ["Anchor X"] = "锚点 X",
            ["Anchor Y"] = "锚点 Y",
            ["Angle snap"] = "角度吸附",
            ["Angle snap degrees"] = "角度吸附度数",
            ["Animation"] = "动画",
            ["Animation frame rate"] = "动画帧率",
            ["Animation frame rate in frames per second"] = "动画每秒帧数",
            ["Animation Timing"] = "动画时序",
            ["Auto Key"] = "自动关键帧",
            ["Automatically insert a keyframe before canvas edits"] = "画布编辑前自动插入关键帧",
            ["Appearance"] = "外观",
            ["Application highlight hue in degrees"] = "软件高亮色调（度）",
            ["Application settings"] = "应用设置",
            ["Application theme hue in degrees"] = "软件主题色调（度）",
            ["Aspect"] = "比例",
            ["Aurora"] = "极光",
            ["Basic Drawing"] = "基础绘制",
            ["Basic Shading"] = "基础着色",
            ["Basic Shapes"] = "基础形状",
            ["Behavior"] = "行为",
            ["Benchmark Preset"] = "基准预设",
            ["Bloom"] = "绽放",
            ["Blue"] = "蓝色",
            ["Bold"] = "粗体",
            ["Bold Italic"] = "粗斜体",
            ["Break Apart"] = "拆散",
            ["Random Fracture"] = "随机破碎",
            ["Random Fracture..."] = "随机破碎...",
            ["Random Fracture parameters"] = "随机破碎参数",
            ["Add Collision Terrain"] = "添加碰撞地形",
            ["Collision Terrain"] = "碰撞地形",
            ["Enable fragment collisions"] = "启用碎片碰撞",
            ["Resolve collisions between fragments during simulation"] = "演算过程中处理碎片之间的碰撞",
            ["Play preview"] = "播放预览",
            ["Pause preview"] = "暂停预览",
            ["Reset preview"] = "重置预览",
            ["Preview frame"] = "预览帧",
            ["Preview FPS"] = "预览帧率",
            ["Preview pending"] = "预览准备中",
            ["Preview ready"] = "预览就绪",
            ["Preview unavailable"] = "无法预览",
            ["Geometry"] = "几何",
            ["Fragment count limit"] = "碎片数量限制",
            ["Fragment edge count"] = "碎片边数",
            ["Fragment randomness"] = "碎片随机度",
            ["Random seed"] = "随机种子",
            ["Randomness (%)"] = "随机度（%）",
            ["Pointillize"] = "点化",
            ["Force"] = "力度",
            ["Force algorithm"] = "力度算法",
            ["Choose how fracture force is distributed"] = "选择破碎力度的分布方式",
            ["Directional"] = "方向",
            ["Fracture mode"] = "破碎模式",
            ["Choose the fracture layout mode"] = "选择破碎布局模式",
            ["Fracture strength in pixels per frame"] = "每帧破碎力度（像素）",
            ["Fracture strength (px/frame)"] = "破碎力度（像素/帧）",
            ["Force direction angle in degrees"] = "力度方向角度（度）",
            ["Direction angle (degrees)"] = "方向角度（度）",
            ["Rotation strength in degrees per frame"] = "每帧旋转力度（度）",
            ["Rotation strength (degrees/frame)"] = "旋转力度（度/帧）",
            ["Preserve stroke"] = "保留描边",
            ["Keep the source stroke on every fragment"] = "在每个碎片上保留源描边",
            ["Animation frame count"] = "动画帧数",
            ["Animation frames"] = "动画帧数",
            ["Generate animation"] = "生成动画",
            ["Create keyframes for the fracture simulation"] = "为破碎演算创建关键帧",
            ["Allow fragment overlap"] = "允许碎片重叠",
            ["Allow fragments to overlap during the simulation"] = "允许碎片在演算过程中重叠",
            ["Ground position as a percentage of the source height"] = "地面位置（相对源高度百分比）",
            ["Ground position (%)"] = "地面位置（%）",
            ["Gravity in pixels per frame squared"] = "重力（像素/帧²）",
            ["Gravity (px/frame^2)"] = "重力（像素/帧²）",
            ["Air resistance percentage"] = "空气阻力（%）",
            ["Air resistance (%)"] = "空气阻力（%）",
            ["Bounce percentage"] = "反弹（%）",
            ["Bounce (%)"] = "反弹（%）",
            ["The fracture preview could not be generated."] = "无法生成破碎预览。",
            ["The fracture could not be applied."] = "无法应用破碎。",
            ["Select a drawing object to fracture."] = "请选择要破碎的绘制对象。",
            ["Random Fracture is unavailable in a scene composition."] = "场景合成中无法使用随机破碎。",
            ["Select a drawing object rather than a nested instance."] = "请选择绘制对象，而不是嵌套实例。",
            ["Select the whole filled shape before using Random Fracture."] = "使用随机破碎前请选中整个填充形状。",
            ["Select exactly one active filled shape before using Random Fracture."] = "使用随机破碎前请准确选择一个当前帧可见的填充形状。",
            ["The selected object is no longer available."] = "所选对象已不可用。",
            ["The selected object is not visible at the current frame."] = "所选对象在当前帧不可见。",
            ["The selected object is too small to fracture."] = "所选对象太小，无法破碎。",
            ["The selected fill boundary could not be normalized."] = "无法规范化所选填充边界。",
            ["The selected object has no closed fill boundary."] = "所选对象没有封闭的填充边界。",
            ["The selected object has no usable fill boundary."] = "所选对象没有可用的填充边界。",
            ["The selected object did not produce any valid fragments."] = "所选对象未生成有效碎片。",
            ["The fracture geometry could not be generated"] = "无法生成破碎几何",
            ["The fracture layer has no timeline track."] = "破碎图层没有时间轴轨道。",
            ["The fracture animation keyframe could not be created."] = "无法创建破碎动画关键帧。",
            ["The fracture animation base frame changed unexpectedly."] = "破碎动画基础帧发生了意外变化。",
            ["The selected object could not be materialized at the current frame."] = "无法在当前帧物化所选对象。",
            ["Random Fracture requires an unlocked drawing layer."] = "随机破碎需要未锁定的绘制图层。",
            ["Random Fracture supports closed filled shapes and paths only."] = "随机破碎仅支持封闭填充形状和路径。",
            ["Random"] = "随机",
            ["Brush style"] = "笔刷样式",
            ["Brush tip images"] = "笔尖图像",
            ["Brush tips must be exactly 128 x 128 pixels."] = "笔尖图像必须正好为 128 x 128 像素。",
            ["Brush Tip"] = "笔尖",
            ["Brush"] = "笔刷",
            ["Brush Tool"] = "笔刷工具",
            ["Mixing Brush"] = "混色笔刷",
            ["Mixing Brush Tool"] = "混色笔刷工具",
            ["Mixing"] = "混色",
            ["Optical"] = "光学",
            ["Pigment"] = "颜料",
            ["Strength"] = "力度",
            ["Viscosity"] = "粘性",
            ["Paint Load"] = "载色量",
            ["Influence"] = "影响因子",
            ["Mixing brush settings"] = "混色笔刷设置",
            ["Optical mixing mode"] = "光学混色模式",
            ["Pigment mixing mode"] = "颜料混色模式",
            ["Mixing strength"] = "混色力度",
            ["Mixing viscosity"] = "混色粘性",
            ["Mixing paint load"] = "混色载色量",
            ["Mixing influence"] = "混色影响因子",
            ["Mixing strength value"] = "混色力度值",
            ["Mixing viscosity value"] = "混色粘性值",
            ["Mixing paint load value"] = "混色载色量值",
            ["Mixing influence value"] = "混色影响因子值",
            ["Mixes the sampled and loaded colors as emitted light"] = "以光学方式混合采样颜色与笔刷载色",
            ["Mixes the sampled and loaded colors as physical pigments"] = "以颜料方式混合采样颜色与笔刷载色",
            ["Controls how strongly the brush blends sampled color with loaded paint. Percentage from 0 to 100"] = "控制笔刷混合采样颜色与载色的力度，范围为 0% 到 100%",
            ["Controls how quickly sampled color transfers into the brush. Percentage from 0 to 100"] = "控制采样颜色进入笔刷的速度，范围为 0% 到 100%",
            ["Controls how much loaded paint the brush carries into the stroke. Percentage from 0 to 100"] = "控制笔刷带入笔迹的载色量，范围为 0% 到 100%",
            ["Controls how quickly the brush returns to the selected fill color after leaving sampled paint. Percentage from 0 to 100"] = "控制笔刷离开采样颜料后恢复到当前填色的速度，范围为 0% 到 100%",
            ["Bring Forward"] = "上移一层",
            ["Camera"] = "镜头",
            ["Camera projection"] = "镜头投影",
            ["2D Camera"] = "2D 镜头",
            ["3D Camera"] = "3D 镜头",
            ["Cartesian Grid"] = "直角坐标网格",
            ["Cancel"] = "取消",
            ["Close"] = "关闭",
            ["Capture"] = "捕获",
            ["Choose a color harmony rule"] = "选择色彩和谐规则",
            ["Choose or create a folder for the project asset library."] = "请选择或新建一个用于工程资产库的文件夹。",
            ["Choose a solid workspace color"] = "选择纯色工作区颜色",
            ["Clear Keyframes"] = "清除关键帧",
            ["Classic Tween"] = "传统补间动画",
            ["Classic Tween Curve"] = "传统补间曲线",
            ["Classic tween endpoints must use the same shape type."] = "传统补间动画的起止对象必须使用相同的形状类型。",
            ["Classic tweens require exactly one symbol instance on an instance layer."] = "实例图层必须恰好包含一个元件实例才能创建传统补间动画。",
            ["Classic tweens require one vector object at both endpoints."] = "传统补间动画的起止关键帧都必须恰好包含一个矢量对象。",
            ["Both ends of the span must be populated keyframes."] = "补间范围两端都必须是有内容的关键帧。",
            ["Close"] = "关闭",
            ["Collapse Folder"] = "折叠文件夹",
            ["Color"] = "颜色",
            ["Color -"] = "颜色 -",
            ["Color +"] = "颜色 +",
            ["Color and gradient palette"] = "颜色与渐变调色板",
            ["Color harmony rule"] = "色彩和谐规则",
            ["Color harmony wheel"] = "色彩和谐色轮",
            ["Colors"] = "颜色",
            ["Complementary"] = "互补色",
            ["Content"] = "内容",
            ["Contents"] = "内容",
            ["Continuous"] = "连续",
            ["Controls the number of polygon sides or star points"] = "控制多边形边数或星形尖角数",
            ["Convert Line to Fill"] = "将线条转换为填色",
            ["Convert Lines to Fill"] = "将线条转换为填色",
            ["Copy Frames"] = "复制帧",
            ["Create Classic Tween"] = "创建传统补间动画",
            ["Create Shape Tween"] = "创建形状补间动画",
            ["Delete Anchor"] = "删除锚点",
            ["Custom"] = "自定义",
            ["Decrease value"] = "减小数值",
            ["Delete"] = "删除",
            ["Delete Symbol"] = "删除元件",
            ["Delete Frames"] = "删除帧",
            ["Delete Layer"] = "删除图层",
            ["Delete Layers"] = "删除图层",
            ["Delete Selected Layers"] = "删除所选图层",
            ["Delete {0} layers and all of their contents?"] = "确定删除这 {0} 个图层及其全部内容吗？",
            ["Drag the ring to adjust the primary hue"] = "拖动色环调整主色相",
            ["Draw Settings"] = "绘制设置",
            // Older Vault entries may still carry the former display kind.
            ["Drawing Object"] = "元件",
            ["Add Symbol"] = "添加元件",
            ["Symbol actions"] = "元件操作",
            ["Symbol alpha"] = "元件透明度",
            ["Symbol alpha percentage"] = "元件透明度百分比",
            ["Symbol alpha percentage, mixed values"] = "元件透明度百分比，混合值",
            ["Symbol alpha value"] = "元件透明度值",
            ["Symbol anchor X"] = "元件锚点 X",
            ["Symbol anchor Y"] = "元件锚点 Y",
            ["Symbol hold frame"] = "元件停留帧",
            ["Symbol Instance"] = "元件实例",
            ["Symbol name"] = "元件名称",
            ["Symbol playback FPS"] = "元件播放帧率",
            ["Symbol playback mode"] = "元件播放模式",
            ["Symbol tint"] = "元件色调",
            ["Symbol tint, mixed values"] = "元件色调，混合值",
            ["Symbol unavailable"] = "元件不可用",
            ["Symbols"] = "元件",
            ["Drawing Timeline"] = "绘制时间轴",
            ["Duplicate"] = "复制副本",
            ["Edit fill color"] = "编辑填充颜色",
            ["Edit object"] = "编辑对象",
            ["Edit stroke color"] = "编辑描边颜色",
            ["Edit the selected gradient stop color"] = "编辑所选渐变色标颜色",
            ["Ellipse Tool"] = "椭圆工具",
            ["Ellipse"] = "椭圆",
            ["Empty symbol"] = "空元件",
            ["Empty project"] = "空项目",
            ["Enable onion skin for the drawing timeline"] = "为绘制时间轴启用洋葱皮",
            ["End"] = "结束",
            ["End join"] = "末端连接",
            ["Erase"] = "擦除",
            ["Erase Fills"] = "擦除填色",
            ["Erase Lines"] = "擦除线条",
            ["Eraser Tool"] = "橡皮擦工具",
            ["Eyedropper Tool"] = "吸管工具",
            ["Eyedropper"] = "吸管",
            ["File"] = "文件",
            ["File Reference"] = "文件引用",
            ["Fill"] = "填色",
            ["Fill Tool"] = "填充工具",
            ["Flip Horizontal"] = "水平翻转",
            ["Flip Vertical"] = "垂直翻转",
            ["Font"] = "字体",
            ["Folder"] = "文件夹",
            ["Folder name"] = "文件夹名称",
            ["Frame"] = "帧",
            ["From"] = "起",
            ["Free Transform Tool"] = "任意变形工具",
            ["Distort Tool"] = "扭曲工具",
            ["Freehand Lasso Tool"] = "自由套索工具",
            ["Free Transform"] = "任意变形",
            ["Operation preferences"] = "操作偏好",
            ["More settings"] = "更多设置",
            ["Operation preferences..."] = "操作偏好...",
            ["Shortcut profiles..."] = "快捷键方案...",
            ["Open operation preferences"] = "打开操作偏好",
            ["Open shortcut profiles"] = "打开快捷键方案",
            ["Confirm changes here, then save in Settings."] = "在此确认更改后，请返回设置窗口保存。",
            ["Use Shift for proportional scaling"] = "使用 Shift 启用等比缩放",
            ["On (default): scale freely; hold Shift to keep the aspect ratio. Off: scale proportionally; hold Shift to scale freely."] = "开启（默认）：自由缩放，按住 Shift 等比缩放。关闭：等比缩放，按住 Shift 自由缩放。",
            ["Freq"] = "频率",
            ["Gradient"] = "渐变",
            ["Golden Spiral"] = "黄金比例螺旋线",
            ["Polar Grid"] = "极坐标网格",
            ["Gradient color stops"] = "渐变色标",
            ["Gradient presets"] = "渐变预设",
            ["Gradient Tool"] = "渐变工具",
            ["Gradients"] = "渐变",
            ["Global"] = "全局",
            ["Green"] = "绿色",
            ["Grid"] = "网格",
            ["Grid type"] = "网格类型",
            ["Grid snap"] = "网格吸附",
            ["Grid vu"] = "网格 vu",
            ["Hard"] = "硬度",
            ["Harmony"] = "和谐色",
            ["Hex"] = "HEX",
            ["Hex color (#RRGGBB or #RRGGBBAA)"] = "十六进制颜色（#RRGGBB 或 #RRGGBBAA）",
            ["Hidden"] = "隐藏",
            ["Highlight hue"] = "高亮色调",
            ["Highlight hue preview"] = "高亮色调预览",
            ["Hold Frame"] = "停留帧",
            ["Hide Selected Layers"] = "隐藏所选图层",
            ["Hierarchy"] = "层级",
            ["HSL color field"] = "HSL 颜色区域",
            ["HSV color field"] = "HSV 颜色区域",
            ["Hue"] = "色相",
            ["Hand"] = "手形",
            ["Import"] = "导入",
            ["Import SVG"] = "导入 SVG",
            ["Import SVG..."] = "导入 SVG...",
            ["Import 128x128 Brush Tip"] = "导入 128x128 笔尖",
            ["Import brush tip"] = "导入笔尖",
            ["Import Image"] = "导入图片",
            ["Import Image..."] = "导入图片...",
            ["Clipboard Image"] = "剪贴板图片",
            ["The image could not be pasted."] = "无法粘贴图片。",
            ["Increase value"] = "增大数值",
            ["Ink"] = "墨水",
            ["Ink Bottle"] = "墨水瓶",
            ["Ink Bottle Tool"] = "墨水瓶工具",
            ["Insert Blank Keyframes"] = "插入空白关键帧",
            ["Insert Frames"] = "插入帧",
            ["Insert Keyframes"] = "插入关键帧",
            ["Inspector"] = "检查器",
            ["Italic"] = "斜体",
            ["Properties panel"] = "属性面板",
            ["Properties (F1)"] = "属性 (F1)",
            ["Item"] = "项目",
            ["Kind"] = "类型",
            ["Layer"] = "图层",
            ["Blend Mode"] = "混合模式",
            ["Layer blend mode"] = "图层混合模式",
            ["Controls how the selected layer composites with layers below"] = "控制所选图层与下方图层的合成方式",
            ["Normal"] = "正常",
            ["Multiply"] = "正片叠底",
            ["Screen"] = "滤色",
            ["Darken"] = "变暗",
            ["Lighten"] = "变亮",
            ["Dissolve"] = "溶解",
            ["Color Burn"] = "颜色加深",
            ["Linear Burn"] = "线性加深",
            ["Darker Color"] = "深色",
            ["Color Dodge"] = "颜色减淡",
            ["Linear Dodge"] = "线性减淡",
            ["Lighter Color"] = "浅色",
            ["Overlay"] = "叠加",
            ["Soft Light"] = "柔光",
            ["Hard Light"] = "强光",
            ["Vivid Light"] = "亮光",
            ["Linear Light"] = "线性光",
            ["Pin Light"] = "点光",
            ["Hard Mix"] = "实色混合",
            ["Difference"] = "差值",
            ["Exclusion"] = "排除",
            ["Luminosity"] = "明度",
            ["Subtract"] = "减去",
            ["Divide"] = "划分",
            ["At least one layer must remain."] = "至少需要保留一个图层。",
            ["Language"] = "语言",
            ["Lab color field"] = "Lab 颜色区域",
            ["Layer Color for Selected..."] = "所选图层颜色...",
            ["Layer Color..."] = "图层颜色...",
            ["Layer Color"] = "图层颜色",
            ["Layer color presets"] = "图层颜色预设",
            ["Layer outline hue in degrees"] = "图层轮廓色相（度）",
            ["Layer name"] = "图层名称",
            ["Layers"] = "图层",
            ["Library"] = "库",
            ["Library Presets"] = "库预设",
            ["Lightness"] = "明度",
            ["Lin"] = "线",
            ["Line Tool"] = "直线工具",
            ["Line"] = "直线",
            ["Linear"] = "线性",
            ["Linear gradient fill"] = "线性渐变填充",
            ["Live symbols from the current project."] = "当前项目中的实时元件。",
            ["Local"] = "局部",
            ["Lock Layer"] = "锁定图层",
            ["Lock Selected Layers"] = "锁定所选图层",
            ["Loop"] = "循环",
            ["Main menu"] = "主菜单",
            ["Main Camera"] = "主镜头",
            ["Mask"] = "遮罩",
            ["Material Set"] = "材质集",
            ["Material Swatches"] = "材质色板",
            ["Maximize"] = "最大化",
            ["Merge and Simplify Lines"] = "合并并简化线条",
            ["Minimize"] = "最小化",
            ["Missing object"] = "对象缺失",
            ["Mitered end connection"] = "尖角末端连接",
            ["Mitered start connection"] = "尖角起始连接",
            ["Mixed"] = "混合",
            ["Mode"] = "模式",
            ["Module Reload On"] = "模块重载已开启",
            ["Move Layer Down"] = "下移图层",
            ["Move Layer Up"] = "上移图层",
            ["Move Out of Mask Layer"] = "移出遮罩层",
            ["New Drawing Layer"] = "新建绘制图层",
            ["New Folder Layer"] = "新建文件夹图层",
            ["New Folder"] = "新建文件夹",
            ["New Mask Layer"] = "新建遮罩图层",
            ["Next"] = "后",
            ["Next onion skin frames"] = "后续洋葱皮帧数",
            ["No content at this frame"] = "当前帧无内容",
            ["No symbols"] = "没有元件",
            ["No preview"] = "无预览",
            ["No recent colors"] = "没有最近使用的颜色",
            ["No saved colors"] = "没有保存的颜色",
            ["No stored items"] = "没有已存项目",
            ["No timeline tracks"] = "没有时间轴轨道",
            ["No visual preview"] = "无可视预览",
            ["Note"] = "备注",
            ["Note text"] = "备注内容",
            ["Number Keys"] = "数字键",
            ["Object"] = "对象",
            ["Object placeholder"] = "对象占位符",
            ["Object snap"] = "对象吸附",
            ["Object Snapshot"] = "对象快照",
            ["Objects"] = "对象",
            ["Move"] = "移动",
            ["Original Size"] = "原始大小",
            ["Original"] = "原始",
            ["Original layer color"] = "原始图层颜色",
            ["Ocean"] = "海洋",
            ["OK"] = "确定",
            ["No"] = "否",
            ["New Project"] = "新建工程",
            ["Onion"] = "洋葱皮",
            ["Motion"] = "运动轨",
            ["Motion track first frame"] = "运动轨起始帧",
            ["Motion track last frame"] = "运动轨结束帧",
            ["Show the selected symbol's motion track on the Stage"] = "在舞台显示所选元件的运动轨",
            ["Open"] = "打开",
            ["Open Project"] = "打开工程",
            ["Open Project..."] = "打开工程...",
            ["Open selected symbol"] = "打开选中的元件",
            ["Open color and gradient palettes"] = "打开颜色与渐变调色板",
            ["Orthographic"] = "正交",
            ["Paint"] = "绘制",
            ["Paste Frames"] = "粘贴帧",
            ["Reverse Frames"] = "翻转帧",
            ["Pen Tool"] = "钢笔工具",
            ["Pen"] = "钢笔",
            ["Simple Pen Tool"] = "简易钢笔工具",
            ["Simple Pen"] = "简易钢笔",
            ["Space"] = "坐标系",
            ["Pencil Tool"] = "铅笔工具",
            ["Pencil"] = "铅笔",
            ["Persistent notes, file references, snapshots, and object references."] = "持久保存的备注、文件引用、快照和对象引用。",
            ["Perspective"] = "透视",
            ["Current camera projection is orthographic. Activate to switch to perspective"] = "当前镜头使用正交投影。激活可切换为透视投影",
            ["Current camera projection is perspective. Activate to switch to orthographic"] = "当前镜头使用透视投影。激活可切换为正交投影",
            ["Switch to orthographic projection"] = "切换为正交投影",
            ["Switch to perspective projection"] = "切换为透视投影",
            ["Playback"] = "播放",
            ["Playback FPS"] = "播放帧率",
            ["Play Once"] = "仅播放一次",
            ["Points"] = "尖角数",
            ["Polygon sides or star points"] = "多边形边数或星形尖角数",
            ["Polygon Tool"] = "多边形工具",
            ["Polygon Lasso Tool"] = "多边形套索工具",
            ["Polygon"] = "多边形",
            ["Position"] = "位置",
            ["Preset"] = "预设",
            ["Pressure Brush Tool"] = "压感笔刷工具",
            ["Primary color component"] = "主颜色通道",
            ["Prev"] = "前",
            ["Previous onion skin frames"] = "前序洋葱皮帧数",
            ["Primitive Set"] = "基础图元集",
            ["Rotate"] = "旋转",
            ["Rotation"] = "旋转",
            ["Scale"] = "缩放",
            ["Project"] = "项目",
            ["Project asset count"] = "项目素材数量",
            ["Project Objects"] = "项目对象",
            ["Project Assets"] = "项目素材",
            ["Rad"] = "径",
            ["Radial"] = "径向",
            ["Radial gradient fill"] = "径向渐变填充",
            ["Ratio"] = "比例",
            ["Recent"] = "最近使用",
            ["Recent gradients"] = "最近渐变",
            ["Rectangle Tool"] = "矩形工具",
            ["Rectangle"] = "矩形",
            ["Rectangle, ellipse, triangle, polygon, star and line presets."] = "矩形、椭圆、三角形、多边形、星形和直线预设。",
            ["Red"] = "红色",
            ["RGB color field"] = "RGB 颜色区域",
            ["Red channel"] = "红色通道",
            ["Green channel"] = "绿色通道",
            ["Blue channel"] = "蓝色通道",
            ["New layer color"] = "新图层颜色",
            ["Reset"] = "重置",
            ["Show Layer as Outline"] = "以轮廓显示图层",
            ["Show Layer Normally"] = "正常显示图层",
            ["Show or hide layer"] = "显示或隐藏图层",
            ["Lock or unlock layer"] = "锁定或解锁图层",
            ["Show all layers"] = "显示全部图层",
            ["Hide all layers"] = "隐藏全部图层",
            ["Lock all layers"] = "锁定全部图层",
            ["Unlock all layers"] = "解锁全部图层",
            ["Show all layers normally"] = "正常显示全部图层",
            ["Show all layers as outlines"] = "以轮廓显示全部图层",
            ["Show layer as outline; double-click to change color"] = "以轮廓显示图层；双击可修改颜色",
            ["Show Selected Layers as Outlines"] = "以轮廓显示所选图层",
            ["Show Selected Layers Normally"] = "正常显示所选图层",
            ["Choose layer color saturation and brightness"] = "选择图层颜色的饱和度和明度",
            ["Regular"] = "常规",
            ["Remove"] = "移除",
            ["Remove intermediate keyframes before creating a tween."] = "创建补间动画前请移除范围内的中间关键帧。",
            ["Remove selected custom color"] = "移除所选自定义颜色",
            ["Remove selected saved gradient"] = "移除所选已保存渐变",
            ["Remove the selected color stop"] = "移除所选色标",
            ["Rename"] = "重命名",
            ["Rename Folder"] = "重命名文件夹",
            ["Rename Symbol"] = "重命名元件",
            ["Rename Layer"] = "重命名图层",
            ["Rename Layer..."] = "重命名图层...",
            ["Rename Project..."] = "重命名工程...",
            ["Restart editor"] = "重启编辑器",
            ["Restart editor and preserve the current project"] = "重启编辑器并保留当前项目",
            ["Restore"] = "还原",
            ["Restore original size"] = "恢复原始大小",
            ["Round"] = "圆形",
            ["Rounded end connection"] = "圆角末端连接",
            ["Rounded start connection"] = "圆角起始连接",
            ["Saturation"] = "饱和度",
            ["Save"] = "保存",
            ["Save changes to the current project before continuing?"] = "继续之前是否保存当前工程的更改？",
            ["Save Project"] = "保存工程",
            ["Save Project As..."] = "工程另存为...",
            ["Retry"] = "重试",
            ["Abort"] = "中止",
            ["Ignore"] = "忽略",
            ["Save current gradient"] = "保存当前渐变",
            ["Scene"] = "场景",
            ["Scene assembly, animation and keyframe workflow"] = "场景与动画：场景装配、动画与关键帧工作流",
            ["Scene assembly, hierarchy and library workflow"] = "场景组装、层级与素材库工作流",
            ["Scene & Animation"] = "场景与动画",
            ["Scene Edit"] = "场景编辑",
            ["Scene Instances"] = "场景实例",
            ["Scene type"] = "场景类型",
            ["Scene, layers and objects"] = "场景、图层与对象",
            ["Scene composition context"] = "场景合成上下文",
            ["Send Backward"] = "下移一层",
            ["Scenes"] = "场景",
            ["Select an object on the stage first."] = "请先在舞台上选择一个对象。",
            ["Select Tool"] = "选择工具",
            ["Select"] = "选择",
            ["Selected gradient stop color"] = "所选渐变色标颜色",
            ["Settings"] = "设置",
            ["Settings..."] = "设置...",
            ["Shape"] = "形状",
            ["Shape Tween"] = "形状补间动画",
            ["Shape Tween Curve"] = "形状补间曲线",
            ["Tween Curve"] = "补间曲线",
            ["Tween layers cannot mix vector shapes and symbol instances."] = "同一补间图层不能混合矢量形状与元件实例。",
            ["Tween curve editor"] = "补间曲线编辑器",
            ["Edits the selected tween easing curve"] = "编辑所选补间动画的缓动曲线",
            ["Add Anchor"] = "添加锚点",
            ["Remove Tween"] = "删除补间动画",
            ["Frames {0}-{1} | {2} anchors{3}"] = "帧 {0}-{1} | {2} 个锚点{3}",
            ["Shape tween endpoints require compatible closed contours."] = "形状补间动画的起止对象必须具有兼容的封闭轮廓。",
            ["Shape tweens do not support symbol instances."] = "形状补间动画不支持元件实例，请使用传统补间动画。",
            ["Shape tweens require editable filled vector shapes."] = "形状补间动画仅支持可编辑的封闭矢量填色形状。",
            ["Shape tweens require vector shapes at both endpoints."] = "形状补间动画的起止关键帧都必须包含矢量形状。",
            ["Shape tween endpoints must share a compatible closed shape or open stroke."] = "形状补间动画的起止关键帧必须至少具有一组兼容的封闭形状或开放描边。",
            ["Shape tween endpoints must both be closed shapes or both be open vector strokes."] = "形状补间动画的起止对象必须同为封闭形状或同为开放矢量描边。",
            ["Shape tween endpoints require compatible vector contours."] = "形状补间动画的起止对象必须具有兼容的矢量轮廓。",
            ["Shape tweens require editable vector shapes or strokes."] = "形状补间动画仅支持可编辑的矢量形状或描边。",
            ["Select a span containing a start and end frame."] = "请选择同时包含起始帧和结束帧的范围。",
            ["Shape Settings"] = "形状设置",
            ["Shp"] = "形",
            ["Sol"] = "纯",
            ["Shape radial gradient fill"] = "形状径向渐变填充",
            ["Shape radial"] = "形状径向",
            ["Shape %"] = "形宽 %",
            ["Shape drawing and direct object editing"] = "形状绘制与对象直接编辑",
            ["Sharp"] = "尖角",
            ["Shortcut"] = "快捷键",
            ["Shortcut map"] = "快捷键映射",
            ["Color editor collapsed"] = "颜色编辑器已折叠",
            ["Color editor expanded"] = "颜色编辑器已展开",
            ["Show or hide color editor"] = "显示或隐藏颜色编辑器",
            ["Show Selected Layers"] = "显示所选图层",
            ["Sides"] = "边数",
            ["Size"] = "大小",
            ["Smooth"] = "平滑",
            ["Smoothing"] = "平滑度",
            ["Snap"] = "吸附",
            ["Snap Point Tool"] = "吸附顶点工具",
            ["Shots"] = "镜头",
            ["Shots & Directing"] = "镜头与导演",
            ["Camera list, independent camera tracks and continuous scene timeline"] = "相机列表、独立相机轨道与连续场景时间轴",
            ["Zoom"] = "缩放",
            ["Reset framing"] = "重置镜头",
            ["Shot frame X"] = "镜头 X",
            ["Shot frame Y"] = "镜头 Y",
            ["Shot frame zoom"] = "镜头缩放",
            ["Shot frame rotation in degrees"] = "镜头旋转（度）",
            ["Reset shot framing"] = "重置镜头取景",
            ["Cameras are available in the Scene & Animation workspace"] = "相机功能仅在场景与动画工作区可用",
            ["All shots"] = "全部镜头",
            ["New shot"] = "新建镜头",
            ["New camera"] = "新建相机",
            ["Include selected"] = "包含选中图层",
            ["Locate"] = "定位",
            ["Locate shot"] = "定位到镜头",
            ["Fit timeline"] = "适应时间轴",
            ["Fit timeline to the shot sequence"] = "将时间轴扩展到镜头序列长度",
            ["Delete shot"] = "删除镜头",
            ["Move shot earlier"] = "镜头前移",
            ["Move shot later"] = "镜头后移",
            ["Shot name"] = "镜头名称",
            ["Shot description"] = "镜头描述",
            ["Shot duration in frames"] = "镜头时长（帧）",
            ["No cameras yet. Add one to animate a viewpoint."] = "还没有相机。添加相机后即可制作视角动画。",
            ["Select a camera to edit its 3D parameters"] = "选择相机以编辑其 3D 参数",
            ["{0} cameras · {1} frames · {2:0.00} s"] = "{0} 个相机 · {1} 帧 · {2:0.00} 秒",
            ["Frame {0} / {1} · {2} keys · {3} tweens"] = "帧 {0} / {1} · {2} 个关键帧 · {3} 个补间",
            ["Preview"] = "预览",
            ["Cameras"] = "相机",
            ["Focal length"] = "焦距",
            ["Ortho size"] = "正交尺寸",
            ["Reset camera"] = "重置相机",
            ["{0} shots · {1} frames · {2:0.00} s"] = "{0} 个镜头 · {1} 帧 · {2:0.00} 秒",
            ["{0}f · frames {1} · {2} layers"] = "{0} 帧 · 范围 {1} · {2} 个图层",
            ["No layers"] = "无图层",
            ["Shot preview"] = "镜头预览",
            ["Shot preview of {0} at frame {1}"] = "镜头预览：{0}（第 {1} 帧）",
            ["Soft Round"] = "柔边圆形",
            ["Solid"] = "纯色",
            ["Solid fill"] = "纯色填充",
            ["Solo"] = "独显",
            ["Split complementary"] = "分裂互补色",
            ["Spotlight"] = "聚光",
            ["Square"] = "方形",
            ["Stage"] = "舞台",
            ["Star Tool"] = "星形工具",
            ["Star"] = "星形",
            ["Start"] = "开始",
            ["Stops"] = "色标",
            ["Start join"] = "起始连接",
            ["Starter presets that can be copied into Stored items."] = "可复制到已存项目的入门预设。",
            ["Starting workspace"] = "正在启动工作区",
            ["Stop"] = "色标",
            ["Stop color"] = "色标颜色",
            ["Store"] = "保存",
            ["Stored"] = "已存",
            ["Stored Item"] = "已存项目",
            ["Stored Items"] = "已存项目",
            ["Stroke"] = "描边",
            ["Style"] = "样式",
            ["SVG files"] = "SVG 文件",
            ["SVG Import"] = "SVG 导入",
            ["Export SVG..."] = "导出 SVG...",
            ["Export Symbol as SVG"] = "导出元件为 SVG",
            ["Export Symbol as SVG..."] = "导出元件为 SVG...",
            ["Export Symbol File"] = "导出元件文件",
            ["Export Symbol File..."] = "导出元件文件...",
            ["Import Symbol File"] = "导入元件文件",
            ["Import Symbol File..."] = "导入元件文件...",
            ["Symbol files"] = "元件文件",
            ["The symbol could not be exported."] = "无法导出该元件。",
            ["The symbol could not be imported."] = "无法导入该元件。",
            ["The symbol could not be exported as SVG."] = "无法将元件导出为 SVG。",
            ["Stress Scene Setup"] = "压力场景设置",
            ["Stress generation is disabled for non-drawable scene compositions."] = "不可绘制的场景合成中已禁用压力场景生成。",
            ["Sunset"] = "日落",
            ["System"] = "系统",
            ["Symbol"] = "元件",
            ["Teal, amber, coral, violet and white starter swatches."] = "青绿、琥珀、珊瑚、紫罗兰和白色入门色板。",
            ["Text"] = "文本",
            ["Text Tool"] = "文本工具",
            ["Theme hue"] = "主题色调",
            ["Theme hue preview"] = "主题色调预览",
            ["The vault file could not be parsed."] = "无法解析素材库文件。",
            ["The SVG could not be imported."] = "无法导入 SVG。",
            ["Tetradic"] = "四色组",
            ["The settings could not be saved. See the latest log file for details."] = "无法保存设置。详细信息请查看最新日志文件。",
            ["This symbol is not available in the current project."] = "当前项目中没有此元件。",
            ["Tight fit"] = "紧贴",
            ["Tint"] = "色调",
            ["Timeline"] = "时间轴",
            ["The project could not be opened."] = "无法打开工程。",
            ["The project could not be saved."] = "无法保存工程。",
            ["Timeline panel"] = "时间轴面板",
            ["Timeline (F2)"] = "时间轴 (F2)",
            ["Frame width"] = "帧宽",
            ["Frame height"] = "帧高",
            ["Low"] = "低",
            ["Medium"] = "中",
            ["High"] = "高",
            ["Timeline, playback and keyframe workflow"] = "时间轴、播放与关键帧工作流",
            ["Timing Preset"] = "时序预设",
            ["To"] = "止",
            ["Tool"] = "工具",
            ["Tool shortcut map"] = "工具快捷键映射",
            ["Tool shortcuts"] = "工具快捷键",
            ["Track"] = "轨道",
            ["Traditional Brush"] = "传统笔刷",
            ["Traditional brush direction in degrees"] = "传统笔刷方向（度）",
            ["Traditional brush shape width"] = "传统笔刷形状宽度",
            ["Traditional brush tip shape"] = "传统笔刷笔尖形状",
            ["Traditional Flash"] = "传统 Flash",
            ["Triadic"] = "三角色",
            ["Triangle Tool"] = "三角形工具",
            ["Triangle"] = "三角形",
            ["Type"] = "类型",
            ["Unlock Layer"] = "解锁图层",
            ["Unlock Selected Layers"] = "解锁所选图层",
            ["Untitled"] = "未命名",
            ["Use #RRGGBB or #RRGGBBAA"] = "请输入 #RRGGBB 或 #RRGGBBAA",
            ["Use the English interface"] = "使用英文界面",
            ["Use arrow keys, Page Up, Page Down, Home, or End to read the release notes."] = "可使用方向键、Page Up、Page Down、Home 或 End 阅读更新公告。",
            ["Use a linear fill gradient"] = "使用线性填充渐变",
            ["Use a radial fill gradient"] = "使用径向填充渐变",
            ["Use a shape radial fill gradient"] = "使用形状径向填充渐变",
            ["Use a solid fill color"] = "使用纯色填充",
            ["Use number-key tool shortcuts"] = "使用数字键工具快捷键",
            ["Use traditional Flash tool shortcuts"] = "使用传统 Flash 工具快捷键",
            ["Reusable drawing object"] = "可复用元件",
            ["Reusable symbol"] = "可复用元件",
            ["The brush tip image could not be loaded."] = "无法加载笔尖图像。",
            ["The imported brush tip does not contain a visible RGB or alpha mask."] = "导入的笔尖不包含可见的 RGB 或 Alpha 遮罩。",
            ["Value"] = "数值",
            ["Unsaved Project"] = "未保存的工程",
            ["Untitled Project"] = "未命名工程",
            ["Vector 2D Project"] = "Vector 2D 工程",
            ["Vault"] = "素材库",
            ["Vault load failed"] = "素材库加载失败",
            ["Vault is empty"] = "素材库为空",
            ["Vault Note"] = "素材库备注",
            ["Visible"] = "可见",
            ["Version {0}"] = "版本 {0}",
            ["Width percentage relative to the brush size"] = "相对于笔刷大小的宽度百分比",
            ["Width pt"] = "宽度 pt",
            ["World grid opacity"] = "世界网格透明度",
            ["Workspace color"] = "工作区颜色",
            ["Workspace color picker"] = "工作区调色器",
            ["Presets"] = "预设",
            ["Zoom in"] = "放大",
            ["Zoom out"] = "缩小",
            ["Yes"] = "是",
            ["Module Reloading"] = "模块重载中",
            ["Module Reload Applied"] = "模块重载完成",
            ["Module Reload Recovering"] = "模块重载恢复中",
            ["Module Reload Failed"] = "模块重载失败",
            ["Image Import Settings"] = "图片导入设置",
            ["Images"] = "图片",
            ["Missing image"] = "图片缺失",
            // Image asset context menu (built with target-typed new(), so the dictionary is the
            // only place these are translated).
            ["Place"] = "放置",
            ["Import Settings..."] = "导入设置...",
            ["Relink..."] = "重新链接...",
            ["Open a Basic Drawing or scene mask drawing layer before placing an image."] = "放置图片前，请先打开基础绘制或场景遮罩绘制图层。",
            ["Placed"] = "放置",
            ["Remove image \"{0}\" from the library and delete {1} placed instance(s)?"] = "从素材库移除图片“{0}”并删除 {1} 个已放置实例？",
            ["Remove image \"{0}\" from the library?"] = "从素材库移除图片“{0}”？",
            ["Camera track"] = "镜头轨道",
            ["Camera tracks"] = "镜头轨道",
            ["Adjust symbol alpha"] = "调整元件透明度",
            ["Choose symbol tint"] = "选择元件色调",
            ["Selected: None"] = "已选择：无",
            ["Atoms: -"] = "原子：-",
            ["Image import settings"] = "图片导入设置",
            ["Texture"] = "纹理",
            ["Filter mode"] = "过滤模式",
            ["Pixels per unit"] = "每单位像素",
            ["Max size"] = "最大尺寸",
            ["Max size (pixels)"] = "最大尺寸（像素）",
            ["Non power of two"] = "非二次幂尺寸",
            ["Compression"] = "压缩",
            ["Compression quality"] = "压缩质量",
            ["Format"] = "格式",
            ["Quality"] = "质量",
            ["Alpha source"] = "透明度来源",
            ["Pivot"] = "锚点",
            ["Pivot X (0-1)"] = "锚点 X (0-1)",
            ["Pivot Y (0-1)"] = "锚点 Y (0-1)",
            ["Generate mipmaps"] = "生成多级渐远纹理",
            ["Build reduced-resolution copies for minified drawing"] = "为缩小后的显示生成低分辨率副本",
            ["Read/Write enabled"] = "启用读/写",
            ["Keep a CPU-readable copy of the decoded pixels"] = "保留已解码像素的 CPU 可读副本",
            ["Point (no filter)"] = "点采样（无过滤）",
            ["Bilinear"] = "双线性",
            ["Trilinear"] = "三线性",
            ["None"] = "无",
            ["ToNearest"] = "最邻近缩放",
            ["Lossless (PNG)"] = "无损（PNG）",
            ["Raw"] = "原始数据",
            // Enum member names shown in the Vault hover preview. The dictionary holds the
            // display spellings above; these are the identifiers the enum actually produces.
            ["Point"] = "点采样",
            ["Jpeg"] = "JPEG",
            ["LosslessPng"] = "无损（PNG）",
            ["Image"] = "图片",
            ["Created"] = "创建于",
            ["Kind:"] = "类型：",
            // Fragments of composed strings. A composed string is never a dictionary key, so
            // callers must translate its literal parts before assembling it.
            ["Image unavailable"] = "图片不可用",
            ["px"] = "像素",
            ["PPU"] = "像素/单位",
            ["visible"] = "可见",
            ["layers"] = "个图层",
            ["nested"] = "个嵌套实例",
            ["frame"] = "帧",
            ["Selected:"] = "已选择：",
            ["Layer: Mixed"] = "图层：混合",
            ["Layer: -"] = "图层：-",
            ["Transform:"] = "变换：",
            ["Collapse"] = "折叠",
            ["Expand"] = "展开",
            ["Play"] = "播放",
            ["Pause"] = "暂停",
            ["Recent gradient"] = "最近的渐变",
            ["Edit"] = "编辑",
            ["Edit '{0}'"] = "编辑“{0}”",
            ["English"] = "英语",
            ["Play preview"] = "播放预览",
            ["Pause preview"] = "暂停预览",
            ["Layer:"] = "图层：",
            ["Symbol:"] = "元件：",
            ["Symbol Instances"] = "个元件实例",
            ["Missing layer"] = "缺失图层",
            ["Missing object"] = "缺失对象",
            ["Transform:"] = "变换：",
            ["deg"] = "度",
            ["skew"] = "倾斜",
            ["Atoms:"] = "原子数：",
            ["parts"] = "个部分",
            ["objects"] = "个对象",
            ["Objects:"] = "对象：",
            ["From input"] = "来自源图",
            ["The import settings are out of range."] = "导入设置超出有效范围。",
            ["Finish the current drawing operation before placing an image."] = "放置图片前，请先完成当前的绘制操作。",
            ["The import settings could not be applied."] = "无法应用导入设置。",
            ["The image has no positive pixel size."] = "图片没有有效的像素尺寸。",
            ["Image dimensions must not exceed {0} pixels per side."] = "图片每边尺寸不得超过 {0} 像素。",
            ["Image pixel count must not exceed {0} pixels."] = "图片总像素数不得超过 {0} 像素。"
        };

    public static UiLanguage CurrentLanguage { get; private set; } = UiLanguage.English;

    public static string T(string? text) => T(text, CurrentLanguage);

    public static string T(string? text, UiLanguage language)
    {
        if (string.IsNullOrEmpty(text) || language != UiLanguage.SimplifiedChinese) return text ?? string.Empty;
        if (SimplifiedChinese.TryGetValue(text, out var translated)) return translated;
        return TranslateDynamic(text);
    }

    public static void SetLanguage(UiLanguage language)
    {
        if (!Enum.IsDefined(language)) language = UiLanguage.English;
        if (CurrentLanguage == language) return;
        CurrentLanguage = language;
        RefreshAll();
    }

    public static void Watch(Control root)
    {
        if (!ControlRoots.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, root)))
        {
            ControlRoots.Add(new WeakReference<Control>(root));
        }
        WatchControl(root);
    }

    public static void Watch(ToolStrip strip)
    {
        if (!ToolStripRoots.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, strip)))
        {
            ToolStripRoots.Add(new WeakReference<ToolStrip>(strip));
        }
        WatchToolStrip(strip);
    }

    private static void RefreshAll()
    {
        _applying = true;
        try
        {
            RefreshWeakRoots(ControlRoots, control =>
            {
                if (control.IsDisposed) return;
                ApplyControlTree(control);
                control.PerformLayout();
            });
            RefreshWeakRoots(ToolStripRoots, strip =>
            {
                if (!strip.IsDisposed) ApplyToolStrip(strip);
            });
        }
        finally
        {
            _applying = false;
        }
    }

    private static void WatchControl(Control control)
    {
        if (!ControlStates.TryGetValue(control, out _))
        {
            var state = new TextState(
                control.Text,
                control.AccessibleName ?? string.Empty,
                control.AccessibleDescription ?? string.Empty,
                control is TextBox textBox ? textBox.PlaceholderText : string.Empty);
            ControlStates.Add(control, state);
            control.TextChanged += (_, _) => HandleControlTextChanged(control, state);
            control.ControlAdded += (_, e) =>
            {
                if (e.Control is not null) WatchControl(e.Control);
            };
        }

        if (control is ToolStrip strip) WatchToolStrip(strip);
        if (control.ContextMenuStrip is { } menu) Watch(menu);
        foreach (Control child in control.Controls) WatchControl(child);
        ApplyControl(control);
    }

    private static void HandleControlTextChanged(Control control, TextState state)
    {
        if (_applying) return;
        state.SourceText = control.Text;
        ApplyControl(control);
    }

    private static void ApplyControlTree(Control control)
    {
        ApplyControl(control);
        if (control.ContextMenuStrip is { } menu) ApplyToolStrip(menu);
        foreach (Control child in control.Controls) ApplyControlTree(child);
        control.Invalidate();
    }

    private static void ApplyControl(Control control)
    {
        if (!ControlStates.TryGetValue(control, out var state)) return;
        var skipText = control is TextBoxBase or ComboBox or ListBox or ListView or TreeView or NumericUpDown;
        var previousApplying = _applying;
        _applying = true;
        try
        {
            if (!skipText) control.Text = T(state.SourceText);
            if (control is TextBox textBox && !string.IsNullOrEmpty(state.SourcePlaceholderText))
            {
                textBox.PlaceholderText = T(state.SourcePlaceholderText);
            }
            if (!string.IsNullOrEmpty(state.SourceAccessibleName)) control.AccessibleName = T(state.SourceAccessibleName);
            if (!string.IsNullOrEmpty(state.SourceAccessibleDescription)) control.AccessibleDescription = T(state.SourceAccessibleDescription);
        }
        finally
        {
            _applying = previousApplying;
        }
    }

    private static void WatchToolStrip(ToolStrip strip)
    {
        if (WatchedToolStrips.TryGetValue(strip, out _))
        {
            ApplyToolStrip(strip);
            return;
        }
        WatchedToolStrips.Add(strip, new object());
        strip.ItemAdded += (_, e) =>
        {
            if (e.Item is not null) WatchToolStripItem(e.Item);
        };
        foreach (ToolStripItem item in strip.Items) WatchToolStripItem(item);
        ApplyToolStrip(strip);
    }

    private static void WatchToolStripItem(ToolStripItem item)
    {
        if (!ItemStates.TryGetValue(item, out _))
        {
            var state = new TextState(item.Text ?? string.Empty, item.AccessibleName ?? string.Empty, item.AccessibleDescription ?? string.Empty);
            ItemStates.Add(item, state);
            item.TextChanged += (_, _) => HandleToolStripItemTextChanged(item, state);
        }
        if (item is ToolStripDropDownItem dropDown)
        {
            foreach (ToolStripItem child in dropDown.DropDownItems) WatchToolStripItem(child);
        }
        ApplyToolStripItem(item);
    }

    private static void HandleToolStripItemTextChanged(ToolStripItem item, TextState state)
    {
        if (_applying) return;
        state.SourceText = item.Text ?? string.Empty;
        ApplyToolStripItem(item);
    }

    private static void ApplyToolStrip(ToolStrip strip)
    {
        foreach (ToolStripItem item in strip.Items) ApplyToolStripItem(item);
        strip.Invalidate();
    }

    private static void ApplyToolStripItem(ToolStripItem item)
    {
        if (!ItemStates.TryGetValue(item, out var state)) return;
        var previousApplying = _applying;
        _applying = true;
        try
        {
            item.Text = T(state.SourceText);
            if (!string.IsNullOrEmpty(state.SourceAccessibleName)) item.AccessibleName = T(state.SourceAccessibleName);
            if (!string.IsNullOrEmpty(state.SourceAccessibleDescription)) item.AccessibleDescription = T(state.SourceAccessibleDescription);
            if (item is ToolStripDropDownItem dropDown)
            {
                foreach (ToolStripItem child in dropDown.DropDownItems) ApplyToolStripItem(child);
            }
        }
        finally
        {
            _applying = previousApplying;
        }
    }

    private static void RefreshWeakRoots<T>(List<WeakReference<T>> roots, Action<T> refresh) where T : class
    {
        for (var index = roots.Count - 1; index >= 0; index--)
        {
            if (!roots[index].TryGetTarget(out var target))
            {
                roots.RemoveAt(index);
                continue;
            }
            refresh(target);
        }
    }

    private static string TranslateDynamic(string text)
    {
        var translated = text
            .Replace("    Frame ", "    帧 ", StringComparison.Ordinal)
            .Replace(" Animation FPS ", " 动画 FPS ", StringComparison.Ordinal)
            .Replace(" layers", " 个图层", StringComparison.Ordinal)
            .Replace(" objects", " 个对象", StringComparison.Ordinal)
            .Replace(" nested instances", " 个嵌套实例", StringComparison.Ordinal)
            .Replace(" Symbol Instances", " 个元件实例", StringComparison.Ordinal)
            .Replace(" scene instances", " 个场景实例", StringComparison.Ordinal)
            .Replace(" instances", " 个实例", StringComparison.Ordinal)
            .Replace(" visible", " 可见", StringComparison.Ordinal)
            .Replace(" stored", " 已存", StringComparison.Ordinal)
            .Replace(" project", " 项目", StringComparison.Ordinal)
            .Replace(" - Visible, ", " - 可见，", StringComparison.Ordinal)
            .Replace(" - Hidden, ", " - 隐藏，", StringComparison.Ordinal);

        var prefixTranslations = new (string English, string Chinese)[]
        {
            ("Selected: ", "已选择："),
            ("Layer: ", "图层："),
            ("Objects: ", "对象："),
            ("Atoms: ", "原子："),
            ("Render FPS ", "渲染 FPS "),
            ("Animation FPS ", "动画 FPS "),
            ("Draw ", "绘制 "),
            ("Atoms ", "原子 "),
            ("Zoom ", "缩放 "),
            ("Symbol alpha percentage, ", "元件透明度百分比："),
            ("Symbol tint ", "元件色调 "),
            ("Scene ", "场景 "),
            ("Layer ", "图层 "),
            ("Track ", "轨道 "),
            ("Transform: ", "变换："),
            ("Stored ", "已存 "),
            ("Saved ", "已保存 "),
            ("Recent ", "最近使用的 "),
            ("Custom (", "自定义（"),
            ("Layers (", "图层（"),
            ("Objects (", "对象（"),
            ("Color: ", "颜色："),
            ("Contents: ", "内容："),
            ("Center: ", "中心："),
            ("Size: ", "大小："),
            ("Kind: ", "类型："),
            ("Unit: ", "单位："),
            ("Value must be between ", "数值必须介于 "),
            ("Collapse ", "折叠"),
            ("Expand ", "展开"),
            ("No ", "无")
        };
        foreach (var (english, chinese) in prefixTranslations)
        {
            if (!translated.StartsWith(english, StringComparison.Ordinal)) continue;
            translated = chinese + translated[english.Length..];
            break;
        }

        translated = translated
            .Replace(" parts", " 个部分", StringComparison.Ordinal)
            .Replace(" gradients)", " 个渐变）", StringComparison.Ordinal)
            .Replace(" colors, ", " 种颜色，", StringComparison.Ordinal)
            .Replace(" stops", " 个色标", StringComparison.Ordinal)
            .Replace(" obj / ", " 对象 / ", StringComparison.Ordinal)
            .Replace(" inst", " 实例", StringComparison.Ordinal)
            .Replace(" / frame ", " / 帧 ", StringComparison.Ordinal)
            .Replace(" | frame ", " | 帧 ", StringComparison.Ordinal)
            .Replace(" | ", " | ", StringComparison.Ordinal);
        translated = translated
            .Replace(" Fill ", " 填色 ", StringComparison.Ordinal)
            .Replace(" Stroke ", " 描边 ", StringComparison.Ordinal)
            .Replace(" BoundaryStroke ", " 边界描边 ", StringComparison.Ordinal)
            .Replace(" part ", " 部分 ", StringComparison.Ordinal)
            .Replace("Mixed", "混合", StringComparison.Ordinal)
            .Replace("Mask", "遮罩", StringComparison.Ordinal)
            .Replace("Folder", "文件夹", StringComparison.Ordinal)
            .Replace("Symbol", "元件", StringComparison.Ordinal)
            .Replace("Missing object", "对象缺失", StringComparison.Ordinal)
            .Replace(" deg / skew ", " 度 / 倾斜 ", StringComparison.Ordinal)
            .Replace("Tiles ", "图块 ", StringComparison.Ordinal)
            .Replace("more 个图层", "个更多图层", StringComparison.Ordinal)
            .Replace("more 个对象", "个更多对象", StringComparison.Ordinal)
            .Replace("more layers", "个更多图层", StringComparison.Ordinal)
            .Replace("more objects", "个更多对象", StringComparison.Ordinal);
        return translated;
    }
}
