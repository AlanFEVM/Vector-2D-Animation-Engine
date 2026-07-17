using System.Runtime.CompilerServices;

namespace VectorAnimationEngine;

internal enum UiLanguage
{
    English,
    SimplifiedChinese
}

internal static class UiLocalization
{
    private sealed class TextState(string sourceText, string sourceAccessibleName, string sourceAccessibleDescription)
    {
        public string SourceText { get; set; } = sourceText;
        public string SourceAccessibleName { get; set; } = sourceAccessibleName;
        public string SourceAccessibleDescription { get; set; } = sourceAccessibleDescription;
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
            ["Accessibility"] = "辅助功能",
            ["Action"] = "操作",
            ["1000 layers, 100000 objects and 100000000 virtual atoms."] = "1000 个图层、100000 个对象和 100000000 个虚拟原子。",
            ["24 FPS looping timeline range for hand-drawn animation."] = "用于手绘动画的 24 FPS 循环时间轴范围。",
            ["2D scene with depth"] = "带纵深的 2D 场景",
            ["3D scene"] = "3D 场景",
            ["+ Instance"] = "+ 实例",
            ["+ Object"] = "+ 对象",
            ["+ Scene"] = "+ 场景",
            ["A drawing object cannot contain itself or create a recursive containment cycle."] = "绘制对象不能包含自身，也不能形成递归包含关系。",
            ["A project must retain at least one drawing object."] = "项目必须至少保留一个绘制对象。",
            ["A gradient preset needs at least two color stops."] = "渐变预设至少需要两个色标。",
            ["Add a color stop after the selected stop"] = "在所选色标后添加色标",
            ["Add current color to custom palette"] = "将当前颜色添加到自定义调色板",
            ["Add file reference to Vault"] = "向素材库添加文件引用",
            ["Add to Vault"] = "添加到素材库",
            ["Adjust alpha"] = "调整透明度",
            ["Align"] = "对齐",
            ["All"] = "全部",
            ["All files"] = "所有文件",
            ["All alpha"] = "整体透明度",
            ["Alpha"] = "透明度",
            ["Analogous"] = "邻近色",
            ["Angle"] = "角度",
            ["Angle snap"] = "角度吸附",
            ["Angle snap degrees"] = "角度吸附度数",
            ["Animation"] = "动画",
            ["Animation frame rate"] = "动画帧率",
            ["Animation frame rate in frames per second"] = "动画每秒帧数",
            ["Animation Timing"] = "动画时序",
            ["Appearance"] = "外观",
            ["Application settings"] = "应用设置",
            ["Aspect"] = "比例",
            ["Aurora"] = "极光",
            ["Basic Drawing"] = "基础绘制",
            ["Basic Shapes"] = "基础形状",
            ["Behavior"] = "行为",
            ["Benchmark Preset"] = "基准预设",
            ["Bloom"] = "绽放",
            ["Blue"] = "蓝色",
            ["Brush style"] = "笔刷样式",
            ["Brush tip images"] = "笔尖图像",
            ["Brush tips must be exactly 128 x 128 pixels."] = "笔尖图像必须正好为 128 x 128 像素。",
            ["Brush Tip"] = "笔尖",
            ["Brush"] = "笔刷",
            ["Brush Tool"] = "笔刷工具",
            ["Bring Forward"] = "上移一层",
            ["Camera"] = "镜头",
            ["Cancel"] = "取消",
            ["Capture"] = "捕获",
            ["Choose a color harmony rule"] = "选择色彩和谐规则",
            ["Clear Keyframes"] = "清除关键帧",
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
            ["Custom"] = "自定义",
            ["Decrease value"] = "减小数值",
            ["Delete"] = "删除",
            ["Delete Drawing Object"] = "删除绘制对象",
            ["Delete Frames"] = "删除帧",
            ["Drag the ring to adjust the primary hue"] = "拖动色环调整主色相",
            ["Draw Settings"] = "绘制设置",
            ["Drawing Object"] = "绘制对象",
            ["Drawing object actions"] = "绘制对象操作",
            ["Drawing object hold frame"] = "绘制对象停留帧",
            ["Drawing object name"] = "绘制对象名称",
            ["Drawing object playback FPS"] = "绘制对象播放帧率",
            ["Drawing object playback mode"] = "绘制对象播放模式",
            ["Drawing object unavailable"] = "绘制对象不可用",
            ["Drawing Objects"] = "绘制对象",
            ["Drawing Timeline"] = "绘制时间轴",
            ["Duplicate"] = "复制副本",
            ["Edit fill color"] = "编辑填充颜色",
            ["Edit object"] = "编辑对象",
            ["Edit stroke color"] = "编辑描边颜色",
            ["Edit the selected gradient stop color"] = "编辑所选渐变色标颜色",
            ["Ellipse Tool"] = "椭圆工具",
            ["Ellipse"] = "椭圆",
            ["Empty drawing object"] = "空绘制对象",
            ["Empty project"] = "空项目",
            ["Enable onion skin for the active layer"] = "为活动图层启用洋葱皮",
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
            ["Fill"] = "填充",
            ["Fill Tool"] = "填充工具",
            ["Flip Horizontal"] = "水平翻转",
            ["Flip Vertical"] = "垂直翻转",
            ["Folder"] = "文件夹",
            ["Frame"] = "帧",
            ["Free Transform Tool"] = "任意变形工具",
            ["Free Transform"] = "任意变形",
            ["Freq"] = "频率",
            ["Gradient"] = "渐变",
            ["Gradient color stops"] = "渐变色标",
            ["Gradient presets"] = "渐变预设",
            ["Gradient Tool"] = "渐变工具",
            ["Gradients"] = "渐变",
            ["Green"] = "绿色",
            ["Grid"] = "网格",
            ["Grid snap"] = "网格吸附",
            ["Grid vu"] = "网格 vu",
            ["Hard"] = "硬度",
            ["Harmony"] = "和谐色",
            ["Hex"] = "HEX",
            ["Hex color (#RRGGBB or #RRGGBBAA)"] = "十六进制颜色（#RRGGBB 或 #RRGGBBAA）",
            ["Hidden"] = "隐藏",
            ["Hold Frame"] = "停留帧",
            ["Hide Selected Layers"] = "隐藏所选图层",
            ["Hierarchy"] = "层级",
            ["HSV color field"] = "HSV 颜色区域",
            ["Hue"] = "色相",
            ["Hand"] = "手形",
            ["Import"] = "导入",
            ["Import 128x128 Brush Tip"] = "导入 128x128 笔尖",
            ["Import brush tip"] = "导入笔尖",
            ["Increase value"] = "增大数值",
            ["Ink"] = "墨水",
            ["Ink Bottle"] = "墨水瓶",
            ["Ink Bottle Tool"] = "墨水瓶工具",
            ["Insert Blank Keyframes"] = "插入空白关键帧",
            ["Insert Frames"] = "插入帧",
            ["Insert Keyframes"] = "插入关键帧",
            ["Inspector"] = "检查器",
            ["Item"] = "项目",
            ["Kind"] = "类型",
            ["Layer"] = "图层",
            ["Language"] = "语言",
            ["Layer Color for Selected..."] = "所选图层颜色...",
            ["Layer Color..."] = "图层颜色...",
            ["Layer name"] = "图层名称",
            ["Layers"] = "图层",
            ["Library"] = "库",
            ["Library Presets"] = "库预设",
            ["Lightness"] = "明度",
            ["Lin"] = "线性",
            ["Line Tool"] = "直线工具",
            ["Line"] = "直线",
            ["Linear"] = "线性",
            ["Linear gradient fill"] = "线性渐变填充",
            ["Live drawing objects from the current project."] = "当前项目中的实时绘制对象。",
            ["Lock Layer"] = "锁定图层",
            ["Lock Selected Layers"] = "锁定所选图层",
            ["Loop"] = "循环",
            ["Main menu"] = "主菜单",
            ["Main Camera"] = "主镜头",
            ["Mask"] = "遮罩",
            ["Material Set"] = "材质集",
            ["Material Swatches"] = "材质色板",
            ["Materials"] = "材质",
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
            ["New Drawing Layer"] = "新建绘制图层",
            ["New Folder Layer"] = "新建文件夹图层",
            ["New Mask Layer"] = "新建遮罩图层",
            ["Next"] = "后",
            ["Next onion skin frames"] = "后续洋葱皮帧数",
            ["No content at this frame"] = "当前帧无内容",
            ["No drawing objects"] = "没有绘制对象",
            ["No preview"] = "无预览",
            ["No recent colors"] = "没有最近使用的颜色",
            ["No saved colors"] = "没有保存的颜色",
            ["No stored items"] = "没有已存项目",
            ["No timeline tracks"] = "没有时间轴轨道",
            ["No visual preview"] = "无可视预览",
            ["Note"] = "备注",
            ["Note text"] = "备注内容",
            ["Number Keys"] = "数字键",
            ["Object placeholder"] = "对象占位符",
            ["Object snap"] = "对象吸附",
            ["Object Snapshot"] = "对象快照",
            ["Objects"] = "对象",
            ["Original Size"] = "原始大小",
            ["Ocean"] = "海洋",
            ["OK"] = "确定",
            ["Onion"] = "洋葱皮",
            ["Open"] = "打开",
            ["Open selected drawing object"] = "打开选中的绘制对象",
            ["Open color and gradient palettes"] = "打开颜色与渐变调色板",
            ["Orthographic"] = "正交",
            ["Paint"] = "绘制",
            ["Paste Frames"] = "粘贴帧",
            ["Pen Tool"] = "钢笔工具",
            ["Pen"] = "钢笔",
            ["Pencil Tool"] = "铅笔工具",
            ["Pencil"] = "铅笔",
            ["Persistent notes, file references, snapshots, and object references."] = "持久保存的备注、文件引用、快照和对象引用。",
            ["Perspective"] = "透视",
            ["Playback"] = "播放",
            ["Playback FPS"] = "播放帧率",
            ["Play Once"] = "仅播放一次",
            ["Points"] = "尖角数",
            ["Polygon sides or star points"] = "多边形边数或星形尖角数",
            ["Polygon Tool"] = "多边形工具",
            ["Polygon"] = "多边形",
            ["Position"] = "位置",
            ["Preset"] = "预设",
            ["Pressure Brush Tool"] = "压感笔刷工具",
            ["Prev"] = "前",
            ["Previous onion skin frames"] = "前序洋葱皮帧数",
            ["Primitive Set"] = "基础图元集",
            ["Project"] = "项目",
            ["Project Objects"] = "项目对象",
            ["Project Assets"] = "项目素材",
            ["Rad"] = "径向",
            ["Radial"] = "径向",
            ["Radial gradient fill"] = "径向渐变填充",
            ["Ratio"] = "比例",
            ["Recent"] = "最近使用",
            ["Recent gradients"] = "最近渐变",
            ["Rectangle Tool"] = "矩形工具",
            ["Rectangle"] = "矩形",
            ["Rectangle, ellipse, triangle, polygon, star and line presets."] = "矩形、椭圆、三角形、多边形、星形和直线预设。",
            ["Red"] = "红色",
            ["Remove"] = "移除",
            ["Remove selected custom color"] = "移除所选自定义颜色",
            ["Remove selected saved gradient"] = "移除所选已保存渐变",
            ["Remove the selected color stop"] = "移除所选色标",
            ["Rename"] = "重命名",
            ["Rename Drawing Object"] = "重命名绘制对象",
            ["Rename Layer"] = "重命名图层",
            ["Rename Layer..."] = "重命名图层...",
            ["Restart editor"] = "重启编辑器",
            ["Restart editor and preserve the current project"] = "重启编辑器并保留当前项目",
            ["Restore"] = "还原",
            ["Restore original size"] = "恢复原始大小",
            ["Round"] = "圆形",
            ["Rounded end connection"] = "圆角末端连接",
            ["Rounded start connection"] = "圆角起始连接",
            ["Saturation"] = "饱和度",
            ["Save"] = "保存",
            ["Save current gradient"] = "保存当前渐变",
            ["Scene"] = "场景",
            ["Scene assembly, hierarchy and library workflow"] = "场景组装、层级与素材库工作流",
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
            ["Shp"] = "形状",
            ["Shape radial gradient fill"] = "形状径向渐变填充",
            ["Shape radial"] = "形状径向",
            ["Shape %"] = "形宽 %",
            ["Shape drawing and direct object editing"] = "形状绘制与对象直接编辑",
            ["Sharp"] = "尖角",
            ["Shortcut"] = "快捷键",
            ["Shortcut map"] = "快捷键映射",
            ["Show or hide color editor"] = "显示或隐藏颜色编辑器",
            ["Show Selected Layers"] = "显示所选图层",
            ["Sides"] = "边数",
            ["Size"] = "大小",
            ["Smooth"] = "平滑",
            ["Smoothing"] = "平滑度",
            ["Snap"] = "吸附",
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
            ["Stress Scene Setup"] = "压力场景设置",
            ["Stress generation is disabled for non-drawable scene compositions."] = "不可绘制的场景合成中已禁用压力场景生成。",
            ["Sunset"] = "日落",
            ["System"] = "系统",
            ["Symbol"] = "元件",
            ["Teal, amber, coral, violet and white starter swatches."] = "青绿、琥珀、珊瑚、紫罗兰和白色入门色板。",
            ["The vault file could not be parsed."] = "无法解析素材库文件。",
            ["Tetradic"] = "四色组",
            ["The settings could not be saved. See the latest log file for details."] = "无法保存设置。详细信息请查看最新日志文件。",
            ["This drawing object is not available in the current project."] = "当前项目中没有此绘制对象。",
            ["Tight fit"] = "紧贴",
            ["Timeline"] = "时间轴",
            ["Timeline, playback and keyframe workflow"] = "时间轴、播放与关键帧工作流",
            ["Timing Preset"] = "时序预设",
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
            ["Use a linear fill gradient"] = "使用线性填充渐变",
            ["Use a radial fill gradient"] = "使用径向填充渐变",
            ["Use a shape radial fill gradient"] = "使用形状径向填充渐变",
            ["Use a solid fill color"] = "使用纯色填充",
            ["Use number-key tool shortcuts"] = "使用数字键工具快捷键",
            ["Use traditional Flash tool shortcuts"] = "使用传统 Flash 工具快捷键",
            ["Reusable drawing object"] = "可复用绘制对象",
            ["The brush tip image could not be loaded."] = "无法加载笔尖图像。",
            ["The imported brush tip does not contain a visible RGB or alpha mask."] = "导入的笔尖不包含可见的 RGB 或 Alpha 遮罩。",
            ["Value"] = "数值",
            ["Vault"] = "素材库",
            ["Vault load failed"] = "素材库加载失败",
            ["Vault is empty"] = "素材库为空",
            ["Vault Note"] = "素材库备注",
            ["Visible"] = "可见",
            ["Width percentage relative to the brush size"] = "相对于笔刷大小的宽度百分比",
            ["Width pt"] = "宽度 pt",
            ["World grid opacity"] = "世界网格透明度",
            ["Module Reloading"] = "模块重载中",
            ["Module Reload Applied"] = "模块重载完成",
            ["Module Reload Recovering"] = "模块重载恢复中",
            ["Module Reload Failed"] = "模块重载失败"
        };

    public static UiLanguage CurrentLanguage { get; private set; } = UiLanguage.English;

    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || CurrentLanguage == UiLanguage.English) return text ?? string.Empty;
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
                if (!control.IsDisposed) ApplyControlTree(control);
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
            var state = new TextState(control.Text, control.AccessibleName ?? string.Empty, control.AccessibleDescription ?? string.Empty);
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
            .Replace(" Drawing Object Instances", " 个绘制对象实例", StringComparison.Ordinal)
            .Replace(" scene instances", " 个场景实例", StringComparison.Ordinal)
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
            ("Drawing Object | ", "绘制对象 | "),
            ("Drawing Object: ", "绘制对象："),
            ("Drawing Object ", "绘制对象 "),
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
