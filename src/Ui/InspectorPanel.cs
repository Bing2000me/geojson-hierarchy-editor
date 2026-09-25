using System.Globalization;
using System.Text.Json.Nodes;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using GeoJsonEditor.App;
using GeoJsonEditor.Geo;
using GeoJsonEditor.Map;
using GeoJsonEditor.Model;

namespace GeoJsonEditor.Ui;

/// <summary>右侧属性面板：未选择时显示文档概况，单选时编辑属性和查看统计，多选时提供批量操作。</summary>
public sealed class InspectorPanel
{
    private static readonly string[] CommonLevels = ["省", "市", "区县", "乡镇", "村"];

    private readonly Editor _editor;
    private readonly Border _host;

    /// <summary>打开“简化边界”对话框（由主窗口提供）。</summary>
    public Action? RequestSimplify { get; set; }
    private readonly ScrollViewer _scroll;
    private TextBox? _nameBox;

    public InspectorPanel(Editor editor)
    {
        _editor = editor;
        _scroll = new ScrollViewer().NoHorizontalScroll();
        _host = new Border()
            .WithTheme((t, b) => b.Background(UiColors.Surface(t)))
            .Child(_scroll);

        _editor.Doc.SelectionChanged += Rebuild;
        _editor.Doc.Changed += OnDocumentChanged;
        _editor.VertexEditTarget.Changed += SyncVertexButton;
        Rebuild();
    }

    public FrameworkElement View => _host;

    /// <summary>把焦点放到名称输入框并全选（F2 / 右键“重命名”）。</summary>
    public void FocusName()
    {
        if (_nameBox == null) return;
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    private void OnDocumentChanged(DocumentChange change)
    {
        // 自己发起的属性修改不重建，否则正在输入的文本框会失去焦点
        if (ReferenceEquals(change.Origin, this) && (change.Kind & ~ChangeKind.Properties) == 0) return;
        Rebuild();
    }

    // “编辑顶点 / 完成编辑顶点”按钮：进出顶点编辑时只换这个按钮，不重建整个面板（滚动位置不变）
    private Border? _vertexButtonHost;
    private GeoNode? _vertexNode;

    private void SyncVertexButton()
    {
        if (_vertexButtonHost == null || _vertexNode == null) return;
        var node = _vertexNode;
        bool editing = ReferenceEquals(_editor.VertexEditTarget.Value, node);
        _vertexButtonHost.Child = (editing
                ? ActionButton(Icons.Check, "完成编辑顶点（回车）", () => _editor.EndVertexEdit(), style: AppStyles.Primary)
                : ActionButton(Icons.EditVertices, "编辑顶点（双击 / 回车）", () => _editor.BeginVertexEdit(node)))
            .ToolTip("拖动顶点修改形状、在边中点插入顶点、右键删除顶点")
            .StretchHorizontal();
    }

    private void Rebuild()
    {
        var sel = _editor.Doc.Selection;
        _nameBox = null;
        _vertexButtonHost = null;
        _vertexNode = null;
        FrameworkElement content = sel.Count switch
        {
            0 => BuildOverview(),
            1 => BuildSingle(sel[0]),
            _ => BuildMulti(sel),
        };
        _scroll.Content = new StackPanel().Vertical().Margin(16, 14, 16, 20).Spacing(18).Children(content);
    }

    // ───────────────────────── 通用小部件 ─────────────────────────

    private static TextBlock Caption(string text)
        => new TextBlock().Text(text).FontSize(11.5).SemiBold().WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t));

    private static FrameworkElement Section(string title, params UIElement[] children)
        => new StackPanel().Vertical().Spacing(8).Children(
            new UIElement[] { Caption(title) }.Concat(children).ToArray());

    private static FrameworkElement Field(string label, UIElement editor)
        => new StackPanel().Vertical().Spacing(5).Children(
            new TextBlock().Text(label).FontSize(12).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)),
            editor);

    private static FrameworkElement StatGrid(IEnumerable<(string Label, string Value)> rows)
    {
        var list = rows.ToList();
        var grid = new Grid().Columns("Auto,*").Spacing(0);
        grid.Rows(string.Join(",", Enumerable.Repeat("Auto", Math.Max(1, list.Count))));
        for (int i = 0; i < list.Count; i++)
        {
            grid.Add(new TextBlock().Text(list[i].Label).FontSize(12).Margin(0, 3, 16, 3)
                .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)).Row(i).Column(0));
            grid.Add(new TextBlock().Text(list[i].Value).FontSize(12).Margin(0, 3, 0, 3)
                .TextWrapping(TextWrapping.Wrap).Row(i).Column(1));
        }
        return new Border()
            .Padding(12, 8)
            .CornerRadius(8)
            .WithTheme((t, b) => b.Background(UiColors.Card(t)))
            .Child(grid);
    }

    private static Button ActionButton(string icon, string text, Action click, string style = AppStyles.Ghost, bool enabled = true)
        => new Button()
            .StyleName(style)
            .IsEnabled(enabled)
            .Content(new StackPanel().Horizontal().Spacing(7).Children(
                new IconView(icon, 15).CenterVertical(),
                new TextBlock().Text(text).CenterVertical()))
            .OnClick(click);

    private static IconView KindIcon(GeoNode node, double size)
    {
        var c = MapStyle.ColorOf(node);
        var tint = Color.FromArgb(255, c.Red, c.Green, c.Blue);
        return node.Kind switch
        {
            NodeKind.Polygon => new IconView(Icons.Polygon, size) { Tint = tint, StrokeWidth = 2 },
            NodeKind.Line => new IconView(Icons.Line, size) { Tint = tint, StrokeWidth = 2 },
            NodeKind.Point => new IconView(Icons.Point, size) { Tint = tint, StrokeWidth = 2 },
            _ => new IconView(Icons.Folder, size),
        };
    }

    private static string KindText(NodeKind kind) => kind switch
    {
        NodeKind.Polygon => "面",
        NodeKind.Line => "线",
        NodeKind.Point => "点标记",
        _ => "分组",
    };

    // ───────────────────────── 未选择 ─────────────────────────

    private FrameworkElement BuildOverview()
    {
        var doc = _editor.Doc;
        var all = doc.AllNodes().ToList();
        if (all.Count == 0)
        {
            return new StackPanel().Vertical().Spacing(10).Margin(0, 24, 0, 0).Children(
                new IconView(Icons.Layers, 40).CenterHorizontal().WithTheme((t, i) => i.Tint = UiColors.Faint(t)),
                new TextBlock().Text("文档是空的").FontSize(14).SemiBold().CenterHorizontal(),
                new TextBlock()
                    .Text("打开或拖入一个 GeoJSON 文件，或者用上方的工具直接在地图上画点、线、面。")
                    .TextWrapping(TextWrapping.Wrap)
                    .TextAlignment(TextAlignment.Center)
                    .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)));
        }

        int polys = all.Count(n => n.Kind == NodeKind.Polygon);
        int lines = all.Count(n => n.Kind == NodeKind.Line);
        int points = all.Count(n => n.Kind == NodeKind.Point);
        int groups = all.Count(n => n.Kind == NodeKind.Group);
        int depth = all.Max(n => n.Depth) + 1;
        long vertices = all.Sum(n => (long)(n.Geometry?.NumPoints ?? 0));
        var levels = all.Where(n => !string.IsNullOrEmpty(n.Level))
            .GroupBy(n => n.Level)
            .Select(g => $"{g.Key} {g.Count()}")
            .ToList();

        var rows = new List<(string, string)>
        {
            ("要素总数", all.Count.ToString(CultureInfo.InvariantCulture)),
            ("面 / 线 / 点", $"{polys} / {lines} / {points}"),
            ("分组", groups.ToString(CultureInfo.InvariantCulture)),
            ("顶点", vertices.ToString("N0", CultureInfo.InvariantCulture)),
            ("层级深度", $"{depth} 级"),
        };
        if (levels.Count > 0) rows.Add(("级别分布", string.Join("  ·  ", levels)));

        var parts = new List<UIElement>
        {
            new StackPanel().Vertical().Spacing(3).Children(
                new TextBlock().Text(doc.DisplayName).FontSize(16).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis),
                new TextBlock().Text(doc.FilePath ?? "尚未保存").FontSize(11.5).TextTrimming(TextTrimming.CharacterEllipsis)
                    .WithTheme((t, tb) => tb.Foreground = UiColors.Faint(t))),
            Section("概况", StatGrid(rows)),
        };
        if (polys + lines > 0)
        {
            parts.Add(Section("数据量",
                new TextBlock()
                    .Text(vertices >= 200_000
                        ? "数据量较大。地图已按缩放级别自动简化显示；想让文件更小、编辑更快，可以简化边界，相邻区域和上下级的边界会一起简化，不留缝隙。"
                        : "地图按缩放级别自动简化显示。需要减少顶点时可以简化边界，相邻区域和上下级的边界会一起简化，不留缝隙。")
                    .FontSize(12)
                    .TextWrapping(TextWrapping.Wrap)
                    .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)),
                ActionButton(Icons.Simplify, "简化边界…", () => RequestSimplify?.Invoke()).StretchHorizontal()));
        }
        parts.Add(Section("数据坐标系", BuildCrsSelector()));
        parts.Add(Section("提示", Tips()));
        return new StackPanel().Vertical().Spacing(18).Children(parts.ToArray());
    }

    private FrameworkElement BuildCrsSelector()
    {
        var wgs = new ClickToggle(AppStyles.Chip, new TextBlock().Text("WGS-84（GPS / 国际标准）"), stayChecked: true);
        var gcj = new ClickToggle(AppStyles.Chip, new TextBlock().Text("GCJ-02（高德 / DataV）"), stayChecked: true);
        void Sync()
        {
            wgs.IsChecked = _editor.DataCrs.Value == CoordSystem.Wgs84;
            gcj.IsChecked = _editor.DataCrs.Value == CoordSystem.Gcj02;
        }
        Sync();
        wgs.Clicked += () => { _editor.DataCrs.Value = CoordSystem.Wgs84; Sync(); };
        gcj.Clicked += () => { _editor.DataCrs.Value = CoordSystem.Gcj02; Sync(); };
        return new StackPanel().Vertical().Spacing(6).Children(
            new WrapPanel().Spacing(6).Children(wgs, gcj),
            new TextBlock()
                .Text("数据与底图坐标系不同时，显示时自动纠偏，文件里的坐标不会被改动。")
                .FontSize(11.5)
                .TextWrapping(TextWrapping.Wrap)
                .WithTheme((t, tb) => tb.Foreground = UiColors.Faint(t)));
    }

    private static FrameworkElement Tips()
    {
        (string Key, string Text)[] tips =
        [
            ("缩放", "缩小时下级合并成上级，放大逐级展开"),
            ("单击", "选中当前显示的那一级；再次单击同一处选上一级"),
            ("双击 / 回车", "编辑所选面或线的顶点；双击分组放大到它"),
            ("⌘/Ctrl+G", "编组：为所选要素新建共同上级"),
            ("Shift/⌘ 单击", "多选，之后按 M 合并"),
            ("X", "切割：画线穿过要素后双击"),
            ("P / L / A", "画点 / 线 / 面"),
            ("⌘/Ctrl+C、V", "复制、粘贴要素，可以跨窗口"),
            ("F", "定位到所选要素"),
        ];
        var grid = new Grid().Columns("Auto,*");
        grid.Rows(string.Join(",", Enumerable.Repeat("Auto", tips.Length)));
        for (int i = 0; i < tips.Length; i++)
        {
            grid.Add(new Border()
                .Padding(6, 1)
                .Margin(0, 3, 10, 3)
                .CornerRadius(4)
                .Left()
                .WithTheme((t, b) => b.Background(UiColors.Hover(t)))
                .Child(new TextBlock().Text(tips[i].Key).FontSize(11))
                .Row(i).Column(0));
            grid.Add(new TextBlock().Text(tips[i].Text).FontSize(12).CenterVertical().TextWrapping(TextWrapping.Wrap)
                .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)).Row(i).Column(1));
        }
        return grid;
    }

    // ───────────────────────── 单选 ─────────────────────────

    private FrameworkElement BuildSingle(GeoNode node)
    {
        var parts = new List<UIElement>
        {
            Header(node),
            BuildBasics(node),
            BuildStyle(node),
        };

        var stats = BuildStats(node);
        if (stats != null) parts.Add(stats);
        if (node.Children.Any(c => c.Kind == NodeKind.Polygon)) parts.Add(BuildHierarchyCheck(node));
        parts.Add(BuildExtra(node));
        parts.Add(BuildActions(node));
        return new StackPanel().Vertical().Spacing(20).Children(parts.ToArray());
    }

    private FrameworkElement Header(GeoNode node)
    {
        var path = node.Ancestors().Reverse().ToList();
        var crumbs = new WrapPanel().Spacing(2);
        foreach (var a in path)
        {
            var target = a;
            crumbs.Add(new Button()
                .StyleName(AppStyles.IconButton)
                .Padding(4, 1)
                .Content(new TextBlock().Text(a.DisplayName).FontSize(11.5))
                .ToolTip("选择上级「" + a.DisplayName + "」")
                .OnClick(() => _editor.Doc.Select(target)));
            crumbs.Add(new IconView(Icons.ChevronRight, 12).CenterVertical().WithTheme((t, i) => i.Tint = UiColors.Faint(t)));
        }
        if (path.Count == 0)
        {
            crumbs.Add(new TextBlock().Text("根级").FontSize(11.5).Margin(4, 1).WithTheme((t, tb) => tb.Foreground = UiColors.Faint(t)));
        }

        string subtitle = KindText(node.Kind) + (string.IsNullOrEmpty(node.Level) ? "" : " · " + node.Level)
                          + (node.Children.Count > 0 ? $" · {node.Children.Count} 个下级" : "")
                          + (node.Visible ? "" : " · 已隐藏");

        return new StackPanel().Vertical().Spacing(8).Children(
            crumbs,
            new DockPanel().Spacing(10).Children(
                new Border()
                    .Size(38, 38)
                    .CornerRadius(10)
                    .DockLeft()
                    .WithTheme((t, b) => b.Background(UiColors.Card(t)))
                    .Child(KindIcon(node, 20).Center()),
                new StackPanel().Vertical().Spacing(2).CenterVertical().Children(
                    new TextBlock().Text(node.DisplayName).FontSize(16).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis),
                    new TextBlock().Text(subtitle).FontSize(12).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)))));
    }

    private FrameworkElement BuildBasics(GeoNode node)
    {
        var name = new TextBox().Text(node.Name).Placeholder("名称");
        name.TextChanged += text => _editor.Rename(node, text, origin: this);
        name.LostFocus += () => _editor.Doc.BreakCoalescing();
        _nameBox = name;

        var level = new TextBox().Text(node.Level).Placeholder("例如 省、市、区县");
        var chips = new WrapPanel().Spacing(6);
        var chipButtons = new List<ClickToggle>();
        foreach (var l in CommonLevels)
        {
            var value = l;
            var chip = new ClickToggle(AppStyles.Chip, new TextBlock().Text(l), node.Level == l);
            chip.Clicked += () =>
            {
                string next = node.Level == value ? "" : value;
                level.Text = next;
                _editor.SetLevel(node, next, origin: this);
                _editor.Doc.BreakCoalescing();
                foreach (var b in chipButtons) b.IsChecked = (string?)b.Tag == node.Level;
            };
            chip.Tag = value;
            chipButtons.Add(chip);
            chips.Add(chip);
        }
        level.TextChanged += text =>
        {
            _editor.SetLevel(node, text.Trim(), origin: this);
            foreach (var b in chipButtons) b.IsChecked = (string?)b.Tag == node.Level;
        };
        level.LostFocus += () => _editor.Doc.BreakCoalescing();

        var note = new MultiLineTextBox().Text(node.Note).Placeholder("备注，可多行").Height(72).Wrap(true);
        note.TextChanged += text => _editor.SetNote(node, text, origin: this);
        note.LostFocus += () => _editor.Doc.BreakCoalescing();

        return Section("基本信息",
            Field("名称", name),
            Field("级别", new StackPanel().Vertical().Spacing(7).Children(level, chips)),
            Field("备注", note));
    }

    private FrameworkElement BuildStyle(GeoNode node)
    {
        var children = new List<UIElement> { ColorRow(new[] { node }) };
        if (node.Kind == NodeKind.Point)
        {
            children.Add(Field("图标", IconRow(node)));
        }
        return Section(node.Kind == NodeKind.Group ? "颜色（用于下级自动配色之外的显式颜色）" : "样式", children.ToArray());
    }

    private FrameworkElement ColorRow(IReadOnlyList<GeoNode> nodes)
    {
        var current = nodes.Select(n => n.Color).Distinct().Count() == 1 ? nodes[0].Color : "?";
        var wrap = new WrapPanel().Spacing(6);

        var auto = new ClickToggle(AppStyles.Chip, new TextBlock().Text("自动"), current == null, stayChecked: true);
        auto.ToolTip("按层级和位置自动配色");
        auto.Clicked += () => _editor.SetColor(nodes, null);
        wrap.Add(auto);

        foreach (var hex in MapStyle.Palette)
        {
            var value = hex;
            var c = MapStyle.Parse(hex);
            var color = Color.FromArgb(255, c.Red, c.Green, c.Blue);
            bool selected = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Border()
                .Size(20, 20)
                .CornerRadius(10)
                .Background(color)
                .BorderThickness(selected ? 2.5 : 0)
                .WithTheme((t, b) => b.BorderBrush(UiColors.Surface(t)));
            var ring = new Border()
                .Padding(2)
                .CornerRadius(12)
                .BorderThickness(selected ? 2 : 0)
                .BorderBrush(color)
                .Child(dot);
            wrap.Add(new Button()
                .StyleName(AppStyles.IconButton)
                .Padding(1)
                .Content(ring)
                .ToolTip(hex)
                .OnClick(() => _editor.SetColor(nodes, value)));
        }

        var picker = new ColorPicker().Kind(ColorPickerKind.Wheel).Width(56).ToolTip("自定义颜色");
        if (current is { Length: 7 } && current != "?")
        {
            var c = MapStyle.Parse(current);
            picker.SelectedColor = Color.FromArgb(255, c.Red, c.Green, c.Blue);
        }
        picker.SelectedColorChanged += c =>
        {
            var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _editor.SetColor(nodes, hex, coalesce: true);
        };
        wrap.Add(picker);
        return wrap;
    }

    private FrameworkElement IconRow(GeoNode node)
    {
        var row = new WrapPanel().Spacing(6);
        (MarkerIcon Icon, string Shape, string Name)[] items =
        [
            (MarkerIcon.Pin, Icons.ShapePin, "图钉"),
            (MarkerIcon.Circle, Icons.ShapeCircle, "圆点"),
            (MarkerIcon.Star, Icons.ShapeStar, "星形"),
            (MarkerIcon.Square, Icons.ShapeSquare, "方块"),
            (MarkerIcon.Triangle, Icons.ShapeTriangle, "三角"),
            (MarkerIcon.Flag, Icons.ShapeFlag, "旗帜"),
        ];
        var c = MapStyle.ColorOf(node);
        var tint = Color.FromArgb(255, c.Red, c.Green, c.Blue);
        foreach (var (icon, shape, name) in items)
        {
            var value = icon;
            var b = new ClickToggle(AppStyles.Chip, new IconView(shape, 18) { Filled = icon != MarkerIcon.Flag, Tint = tint, StrokeWidth = 2.2 }, node.Icon == icon, stayChecked: true);
            b.Padding = new Thickness(8, 5);
            b.ToolTip(name);
            b.Clicked += () => _editor.SetIcon(new[] { node }, value);
            row.Add(b);
        }
        return row;
    }

    private FrameworkElement? BuildStats(GeoNode node)
    {
        var g = node.Geometry;
        var rows = new List<(string, string)>();
        switch (node.Kind)
        {
            case NodeKind.Polygon:
                rows.Add(("面积", GeoMeasure.FormatArea(GeoMeasure.Area(g))));
                rows.Add(("周长", GeoMeasure.FormatLength(GeoMeasure.Length(g))));
                rows.Add(("顶点", Geometries.VertexCount(g).ToString("N0", CultureInfo.InvariantCulture)));
                if (g!.NumGeometries > 1) rows.Add(("组成部分", $"{g.NumGeometries} 块"));
                int holes = Geometries.Polygons(g).Sum(p => p.NumInteriorRings);
                if (holes > 0) rows.Add(("内部空洞", $"{holes} 个"));
                break;
            case NodeKind.Line:
                rows.Add(("长度", GeoMeasure.FormatLength(GeoMeasure.Length(g))));
                rows.Add(("顶点", Geometries.VertexCount(g).ToString("N0", CultureInfo.InvariantCulture)));
                if (g!.NumGeometries > 1) rows.Add(("组成部分", $"{g.NumGeometries} 段"));
                break;
            case NodeKind.Point:
            {
                var p = Geometries.Points(g).First();
                rows.Add(("坐标", GeoMeasure.FormatLonLat(p.X, p.Y)));
                break;
            }
            default:
            {
                var descendants = node.Descendants().ToList();
                rows.Add(("下级要素", $"{descendants.Count} 个"));
                double area = descendants.Where(d => d.Kind == NodeKind.Polygon && d.Children.All(c => c.Kind != NodeKind.Polygon)).Sum(d => GeoMeasure.Area(d.Geometry));
                if (area > 0)
                {
                    rows.Add(("下级面积合计", GeoMeasure.FormatArea(area)));
                    rows.Add(("范围", "由下级自动拼成"));
                }
                break;
            }
        }

        if (node.Kind is NodeKind.Polygon or NodeKind.Line && g != null)
        {
            var c = g.Centroid;
            rows.Add(("中心", GeoMeasure.FormatLonLat(c.X, c.Y)));
        }

        var polyChildren = node.Children.Where(c => c.Kind == NodeKind.Polygon).ToList();
        if (node.Kind == NodeKind.Polygon && polyChildren.Count > 0)
        {
            double own = GeoMeasure.Area(g);
            double sum = polyChildren.Sum(c => GeoMeasure.Area(c.Geometry));
            double ratio = own > 0 ? sum / own : 0;
            rows.Add(("下级覆盖", $"{polyChildren.Count} 个区域，合计 {ratio:P1}"));
        }

        return rows.Count == 0 ? null : Section("统计", StatGrid(rows));
    }

    /// <summary>其他属性：可以改值、删除、新增。值会尽量保持原来的类型（数字、布尔）。</summary>
    private FrameworkElement BuildExtra(GeoNode node)
    {
        var rows = new StackPanel().Vertical().Spacing(6);
        foreach (var (key, value) in node.Extra)
        {
            var k = key;
            var box = new TextBox().Text(FormatJson(value));
            box.TextChanged += text => _editor.SetExtra(node, k, Editor.ParseValue(text, node.Extra.GetValueOrDefault(k)), origin: this);
            box.LostFocus += () => _editor.Doc.BreakCoalescing();
            var remove = new Button()
                .StyleName(AppStyles.IconButton)
                .Padding(4)
                .Content(new IconView(Icons.Close, 13))
                .ToolTip("删除属性「" + k + "」")
                .OnClick(() => _editor.RemoveExtra(node, k));
            rows.Add(new Grid().Columns("96,*,Auto").Children(
                new TextBlock().Text(k).FontSize(12).CenterVertical().TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(k)
                    .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)).Column(0),
                box.Column(1),
                remove.Margin(4, 0, 0, 0).CenterVertical().Column(2)));
        }

        var newKey = new TextBox().Placeholder("属性名");
        var newValue = new TextBox().Placeholder("值");
        void Add()
        {
            var key = newKey.Text.Trim();
            if (key.Length == 0 || key is "id" or "parentId" or "name" or "level" or "note" or "color" or "icon" or "hidden") return;
            _editor.SetExtra(node, key, Editor.ParseValue(newValue.Text, null));
            _editor.Doc.BreakCoalescing();
        }
        newValue.KeyDown += e =>
        {
            if (e.Key == Key.Enter)
            {
                Add();
                e.Handled = true;
            }
        };
        var add = new Button()
            .StyleName(AppStyles.IconButton)
            .Padding(4)
            .Content(new IconView(Icons.Plus, 14))
            .ToolTip("添加属性（回车）")
            .OnClick(Add);
        rows.Add(new Grid().Columns("96,*,Auto").Children(
            newKey.Margin(0, 0, 6, 0).Column(0),
            newValue.Column(1),
            add.Margin(4, 0, 0, 0).CenterVertical().Column(2)));

        return Section(node.Extra.Count > 0 ? $"其他属性（{node.Extra.Count}）" : "其他属性", rows);
    }

    /// <summary>层级检查：下级之间的重叠、超出本区域的部分、没有被下级覆盖的空隙。</summary>
    private FrameworkElement BuildHierarchyCheck(GeoNode node)
    {
        var results = new StackPanel().Vertical().Spacing(6);
        var run = ActionButton(Icons.Hierarchy, "检查下级：重叠 / 越界 / 空隙", () =>
        {
            results.Clear();
            List<Editor.HierarchyIssue> issues;
            try
            {
                issues = _editor.CheckHierarchy(node);
            }
            catch (Exception ex)
            {
                results.Add(new TextBlock().Text("检查失败：" + ex.Message).TextWrapping(TextWrapping.Wrap));
                return;
            }
            _editor.Highlights.Value = issues.Select(i => i.Area).ToList();
            if (issues.Count == 0)
            {
                results.Add(new StackPanel().Horizontal().Spacing(6).Children(
                    new IconView(Icons.Check, 15).CenterVertical().WithTheme((t, i) => i.Tint = Color.FromRgb(0x16, 0xA3, 0x4A)),
                    new TextBlock().Text("没有发现问题：下级互不重叠、都在本区域内，且正好铺满。").FontSize(12).TextWrapping(TextWrapping.Wrap).CenterVertical()));
                return;
            }
            results.Add(new TextBlock().Text($"发现 {issues.Count} 处问题，已在地图上用红色斜线标出：").FontSize(12).TextWrapping(TextWrapping.Wrap));
            foreach (var issue in issues.Take(12))
            {
                var tag = new Border()
                    .Padding(6, 1)
                    .CornerRadius(4)
                    .CenterVertical()
                    .WithTheme((t, b) => b.Background(UiColors.Danger(t).WithAlpha(36)))
                    .Child(new TextBlock().Text(issue.Kind).FontSize(11).WithTheme((t, tb) => tb.Foreground = UiColors.Danger(t)));
                results.Add(new DockPanel().Spacing(8).Children(
                    tag.DockLeft(),
                    new TextBlock().Text(issue.Description).FontSize(12).TextWrapping(TextWrapping.Wrap)));
            }
            if (issues.Count > 12) results.Add(new TextBlock().Text($"……另有 {issues.Count - 12} 处").FontSize(12));
            var fixes = new WrapPanel().Spacing(6);
            if (issues.Any(i => i.Kind == "越界"))
            {
                fixes.Add(ActionButton(Icons.Crop, "裁剪下级到本区域", () => _editor.ClipChildrenTo(node)));
            }
            if (issues.Any(i => i.Kind is "空隙" or "越界") && node.Kind == NodeKind.Polygon)
            {
                fixes.Add(ActionButton(Icons.Rebuild, "用下级重建本区域边界", () => _editor.RebuildFromChildren()));
            }
            if (fixes.Children.Count > 0) results.Add(fixes);
        }).StretchHorizontal();

        return Section("层级检查", run, results);
    }

    private static string FormatJson(JsonNode? value)
    {
        if (value is null) return "null";
        if (value is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        var text = value.ToJsonString();
        return text.Length > 120 ? text[..117] + "…" : text;
    }

    private FrameworkElement BuildActions(GeoNode node)
    {
        var list = new StackPanel().Vertical().Spacing(6);
        if (_editor.CanEditVertices(node))
        {
            _vertexNode = node;
            _vertexButtonHost = new Border();
            SyncVertexButton();
            list.Add(_vertexButtonHost);
        }
        list.Add(ActionButton(Icons.Target, "定位到此要素（F）", () => _editor.RequestZoomTo(new[] { node })).StretchHorizontal());
        if (node.Kind is NodeKind.Polygon or NodeKind.Line || node.Descendants().Any(d => d.Kind is NodeKind.Polygon or NodeKind.Line))
        {
            list.Add(ActionButton(Icons.Simplify, "简化边界…", () => RequestSimplify?.Invoke())
                .ToolTip("减少顶点，相邻区域和上下级的边界一起简化")
                .StretchHorizontal());
        }
        if (node.Kind is NodeKind.Group or NodeKind.Polygon)
        {
            bool group = node.Kind == NodeKind.Group;
            list.Add(ActionButton(Icons.Rebuild, group ? "把自动范围写入为边界" : "由下级生成边界", () => _editor.RebuildFromChildren(), enabled: _editor.CanRebuildFromChildren(node))
                .ToolTip(group
                    ? "分组的范围在地图上由下级自动拼成，只用于显示。写入后成为它自己的几何，保存和导出时一起写出"
                    : "用全部下级面的并集重新生成它的范围")
                .StretchHorizontal());
        }
        if (_editor.CanClipToParent(node))
        {
            list.Add(ActionButton(Icons.Crop, "裁剪到上级范围", () => _editor.ClipSelectionToParent())
                .ToolTip("去掉超出上级区域的部分")
                .StretchHorizontal());
        }
        list.Add(ActionButton(node.Visible ? Icons.EyeOff : Icons.Eye, node.Visible ? "隐藏（H）" : "显示（H）", () => _editor.ToggleVisible(node)).StretchHorizontal());
        list.Add(ActionButton(Icons.Trash, node.Children.Count > 0 ? "删除（连同全部下级）" : "删除", () => _editor.DeleteSelection(), style: AppStyles.Danger).StretchHorizontal());
        return Section("操作", list);
    }

    // ───────────────────────── 多选 ─────────────────────────

    private FrameworkElement BuildMulti(IReadOnlyList<GeoNode> sel)
    {
        int polys = sel.Count(n => n.Kind == NodeKind.Polygon);
        int lines = sel.Count(n => n.Kind == NodeKind.Line);
        int points = sel.Count(n => n.Kind == NodeKind.Point);
        int groups = sel.Count(n => n.Kind == NodeKind.Group);
        var parts = new List<string>();
        if (polys > 0) parts.Add($"{polys} 个面");
        if (lines > 0) parts.Add($"{lines} 条线");
        if (points > 0) parts.Add($"{points} 个点");
        if (groups > 0) parts.Add($"{groups} 个分组");

        var rows = new List<(string, string)> { ("组成", string.Join("、", parts)) };
        if (polys > 0) rows.Add(("面积合计", GeoMeasure.FormatArea(sel.Where(n => n.Kind == NodeKind.Polygon).Sum(n => GeoMeasure.Area(n.Geometry)))));
        var parents = sel.Select(n => n.Parent).Distinct().ToList();
        rows.Add(("上级", parents.Count == 1 ? parents[0]?.DisplayName ?? "根级" : $"分属 {parents.Count} 个上级"));

        var actions = new StackPanel().Vertical().Spacing(6).Children(
            ActionButton(Icons.Merge, "合并为一个要素（M）", () => _editor.Merge(), style: AppStyles.Primary, enabled: _editor.CanMerge()).StretchHorizontal(),
            ActionButton(Icons.Target, "定位到所选（F）", () => _editor.RequestZoomTo(sel.ToList())).StretchHorizontal(),
            ActionButton(Icons.Simplify, "简化边界…", () => RequestSimplify?.Invoke()).StretchHorizontal(),
            ActionButton(Icons.Eye, "显示 / 隐藏（H）", () => _editor.SetVisible(sel.ToList(), !sel.All(n => n.Visible))).StretchHorizontal(),
            ActionButton(Icons.Trash, "删除所选", () => _editor.DeleteSelection(), style: AppStyles.Danger).StretchHorizontal());

        return new StackPanel().Vertical().Spacing(20).Children(
            new StackPanel().Vertical().Spacing(3).Children(
                new TextBlock().Text($"已选择 {sel.Count} 个要素").FontSize(16).SemiBold(),
                new TextBlock().Text(_editor.CanMerge() ? "可以合并：结果保留第一个选中要素的名称和属性。" : "按住 Shift 或 ⌘/Ctrl 继续单击可增减选择。")
                    .FontSize(12).TextWrapping(TextWrapping.Wrap).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t))),
            Section("概况", StatGrid(rows)),
            Section("统一颜色", ColorRow(sel)),
            Section("操作", actions));
    }
}
