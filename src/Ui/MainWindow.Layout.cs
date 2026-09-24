using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using GeoJsonEditor.App;
using GeoJsonEditor.Geo;
using GeoJsonEditor.Map;
using GeoJsonEditor.Model;

namespace GeoJsonEditor.Ui;

public sealed partial class MainWindow
{
    private readonly List<(EditTool Tool, ClickToggle Button)> _toolButtons = new();
    private readonly ObservableValue<string> _targetName = new("根级");
    private Border _toolOptionsHost = null!;
    private Menu _fileMenu = null!;
    private Menu _recentMenu = null!;
    private Menu _targetMenu = null!;
    private ContextMenu _mapMenu = null!;
    private Point _contextPoint;

    // 命令：菜单、右键菜单、快捷键共用
    private readonly Command _cmdNew = new("file.new", "新建");
    private readonly Command _cmdNewWindow = new("file.newWindow", "新建窗口");
    private readonly Command _cmdOpen = new("file.open", "打开…");
    private readonly Command _cmdOpenInNewWindow = new("file.openInNewWindow", "在新窗口中打开…");
    private readonly Command _cmdOpenRecent = new("file.recent", "最近打开");
    private readonly Command _cmdImport = new("file.import", "导入到所选节点下…");
    private readonly Command _cmdSave = new("file.save", "保存");
    private readonly Command _cmdSaveAs = new("file.saveAs", "另存为…");
    private readonly Command _cmdExport = new("file.export", "导出所选（含下级）…");
    private readonly Command _cmdUndo = new("edit.undo", "撤销");
    private readonly Command _cmdRedo = new("edit.redo", "重做");
    private readonly Command _cmdNewGroup = new("edit.newGroup", "新建分组");
    private readonly Command _cmdSearch = new("view.search", "搜索图层");
    private readonly Command _cmdMerge = new("edit.merge", "合并所选");
    private readonly Command _cmdRebuild = new("edit.rebuild", "由下级生成边界");
    private readonly Command _cmdClip = new("edit.clip", "裁剪到上级范围");
    private readonly Command _cmdZoomSel = new("view.zoomSelection", "定位到所选");
    private readonly Command _cmdToggleVis = new("edit.toggleVisible", "显示 / 隐藏");
    private readonly Command _cmdDelete = new("edit.delete", "删除");
    private readonly Command _cmdDeleteKeep = new("edit.deleteKeepChildren", "删除（保留下级）");
    private readonly Command _cmdRename = new("edit.rename", "重命名");
    private readonly Command _cmdSetTarget = new("draw.target", "添加到");
    private readonly Command _cmdDuplicate = new("edit.duplicate", "创建副本");
    private readonly Command _cmdCopyCoordinate = new("edit.copyCoordinate", "复制此处坐标");
    private readonly Command _cmdSimplify = new("edit.simplify", "简化边界…");

    // ───────────────────────── 整体布局 ─────────────────────────

    private FrameworkElement BuildLayout()
    {
        var center = new SplitPanel()
            .FirstLength(GridLength.Star)
            .SecondLength(new GridLength(316))
            .MinFirst(360)
            .MinSecond(260)
            .MaxSecond(520)
            .SplitterThickness(5)
            .First(BuildMapHost())
            .Second(WithLeftBorder(_inspector.View));

        var body = new SplitPanel()
            .FirstLength(new GridLength(284))
            .MinFirst(200)
            .MaxFirst(520)
            .SplitterThickness(5)
            .First(WithRightBorder(_layers.View))
            .Second(center);

        var root = new Border()
            .WithTheme((t, b) => b.Background(UiColors.Chrome(t)))
            .Child(new DockPanel().Children(
                BuildTopBar().DockTop(),
                BuildStatusBar().DockBottom(),
                body));
        SyncToolButtons();
        return root;
    }

    private static Border WithRightBorder(UIElement child)
        => new Border().BorderThickness(new Thickness(0, 0, 1, 0)).WithTheme((t, b) => b.BorderBrush(UiColors.Divider(t))).Child(child);

    private static Border WithLeftBorder(UIElement child)
        => new Border().BorderThickness(new Thickness(1, 0, 0, 0)).WithTheme((t, b) => b.BorderBrush(UiColors.Divider(t))).Child(child);

    private static FrameworkElement VDivider()
        => new Border().Width(1).Height(22).Margin(6, 0).CenterVertical().WithTheme((t, b) => b.Background(UiColors.Divider(t)));

    private static Button IconButton(string icon, string tip, Action click, double size = 18)
        => new Button()
            .StyleName(AppStyles.IconButton)
            .Padding(6)
            .Content(new IconView(icon, size))
            .ToolTip(tip)
            .OnClick(click);

    // ───────────────────────── 顶部工具栏 ─────────────────────────

    private FrameworkElement BuildTopBar()
    {
        var logo = new Border()
            .Size(30, 30)
            .CornerRadius(8)
            .CenterVertical()
            .WithTheme((t, b) => b.Background(t.Palette.Accent))
            .Child(new IconView(Icons.Layers, 18) { Tint = Color.White, StrokeWidth = 2 }.Center());

        var title = new StackPanel().Vertical().Spacing(1).CenterVertical().MinWidth(150).Children(
            new TextBlock().BindText(_docTitle).FontSize(13.5).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(230),
            new TextBlock().BindText(_docSubtitle).FontSize(11).WithTheme((t, tb) => tb.Foreground = UiColors.Faint(t)));

        _recentMenu = new Menu();
        _fileMenu = new Menu()
            .Item(_cmdNew)
            .Item(_cmdNewWindow)
            .Separator()
            .Item(_cmdOpen)
            .Item(_cmdOpenInNewWindow)
            .SubMenu("最近打开", _recentMenu)
            .Separator()
            .Item(_cmdSave)
            .Item(_cmdSaveAs)
            .Separator()
            .Item(_cmdImport)
            .Item(_cmdExport);

        var editMenu = new Menu()
            .Item(_cmdUndo)
            .Item(_cmdRedo)
            .Separator()
            .Item("剪切", StandardCommands.Cut)
            .Item("复制", StandardCommands.Copy)
            .Item("粘贴", StandardCommands.Paste)
            .Item(_cmdDuplicate)
            .Separator()
            .Item("全选同级", StandardCommands.SelectAll)
            .Item(_cmdNewGroup)
            .Separator()
            .Item(_cmdMerge)
            .Item(_cmdSimplify)
            .Item(_cmdRebuild)
            .Item(_cmdClip)
            .Separator()
            .Item(_cmdDelete);

        var fileButton = new DropDownButton()
            .StyleName(AppStyles.DropDown)
            .Content(new StackPanel().Horizontal().Spacing(6).Children(
                new IconView(Icons.FolderOpen, 16).CenterVertical(),
                new TextBlock().Text("文件").CenterVertical()))
            .DropDownMenu(_fileMenu)
            .OnDropDownOpening(RebuildRecentMenu)
            .CenterVertical();

        var editButton = new DropDownButton()
            .StyleName(AppStyles.DropDown)
            .Content(new StackPanel().Horizontal().Spacing(6).Children(
                new IconView(Icons.Edit, 16).CenterVertical(),
                new TextBlock().Text("编辑").CenterVertical()))
            .DropDownMenu(editMenu)
            .OnDropDownOpening(RequerySuggested)
            .CenterVertical();

        var undo = IconButton(Icons.Undo, "撤销", () => _editor.Doc.Undo()).BindIsEnabled(_canUndo);
        undo.ToolTip(new TextBlock().BindText(_undoTip));
        var redo = IconButton(Icons.Redo, "重做", () => _editor.Doc.Redo()).BindIsEnabled(_canRedo);
        redo.ToolTip(new TextBlock().BindText(_redoTip));

        var left = new StackPanel().Horizontal().Spacing(4).CenterVertical().Left().Children(
            logo,
            title.Margin(6, 0, 10, 0),
            fileButton,
            editButton,
            IconButton(Icons.Save, "保存（⌘/Ctrl+S）", () => SaveDocument(saveAs: false)),
            VDivider(),
            undo,
            redo);

        var tools = new Border()
            .CornerRadius(10)
            .Padding(3)
            .CenterVertical()
            .WithTheme((t, b) =>
            {
                b.Background(t.IsDark ? Color.FromRgb(0x14, 0x16, 0x1A) : Color.FromRgb(0xEC, 0xEE, 0xF2));
            })
            .Child(new StackPanel().Horizontal().Spacing(2).Children(
                ToolButton(EditTool.Select, Icons.Select, "选择", "V", "选择要素、编辑顶点、平移地图"),
                ToolButton(EditTool.DrawPoint, Icons.Point, "点", "P", "放置点标记"),
                ToolButton(EditTool.DrawLine, Icons.Line, "线", "L", "绘制线"),
                ToolButton(EditTool.DrawPolygon, Icons.Polygon, "面", "A", "绘制面（区域）"),
                ToolButton(EditTool.Cut, Icons.Cut, "切割", "X", "画一条线把区域切开")));

        var merge = new Button()
            .StyleName(AppStyles.Ghost)
            .Content(new StackPanel().Horizontal().Spacing(6).Children(
                new IconView(Icons.Merge, 16).CenterVertical(),
                new TextBlock().Text("合并").CenterVertical()))
            .ToolTip("合并所选的面或线（M）")
            .BindIsEnabled(_canMerge)
            .OnClick(() => _editor.Merge());

        var centerGroup = new StackPanel().Horizontal().Spacing(8).CenterVertical().CenterHorizontal().Children(tools, merge);

        var baseMapButton = new Button()
            .StyleName(AppStyles.Ghost)
            .Content(new StackPanel().Horizontal().Spacing(6).Children(
                new IconView(Icons.Map, 16).CenterVertical(),
                new TextBlock().BindText(_baseMapName).CenterVertical(),
                new IconView(Icons.ChevronDown, 14).CenterVertical()))
            .ToolTip("切换底图、调整底图显示");
        baseMapButton.Click += () => ShowBaseMapPopup(baseMapButton);

        var labels = new ToggleButton()
            .StyleName(AppStyles.Tool)
            .Padding(7)
            .Content(new IconView(Icons.Label, 18))
            .ToolTip("显示 / 隐藏地图标注")
            .BindIsChecked(_editor.ShowLabels);

        var themeIcon = new IconView(Icons.Moon, 18);
        var themeButton = new Button().StyleName(AppStyles.IconButton).Padding(6).Content(themeIcon).ToolTip("切换深色 / 浅色界面");
        themeButton.Click += ToggleTheme;
        ThemeChanged += (_, t) => themeIcon.Data = t.IsDark ? Icons.Sun : Icons.Moon;
        Loaded += () => themeIcon.Data = Theme.IsDark ? Icons.Sun : Icons.Moon;

        var right = new StackPanel().Horizontal().Spacing(4).CenterVertical().Right().Children(
            baseMapButton,
            labels,
            themeButton,
            IconButton(Icons.Keyboard, "快捷键与操作说明", ShowShortcuts));

        var grid = new Grid().Columns("*,Auto,*").Margin(12, 0).Children(
            left.Column(0),
            centerGroup.Column(1),
            right.Column(2));

        return new Border()
            .Height(54)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .WithTheme((t, b) =>
            {
                b.Background(UiColors.Surface(t));
                b.BorderBrush(UiColors.Divider(t));
            })
            .Child(grid);
    }

    private ClickToggle ToolButton(EditTool tool, string icon, string label, string key, string description)
    {
        var button = new ClickToggle(AppStyles.Tool, new StackPanel().Horizontal().Spacing(6).Children(
                new IconView(icon, 17).CenterVertical(),
                new TextBlock().Text(label).CenterVertical()), stayChecked: true);
        button.ToolTip($"{description}（{key}）");
        button.Clicked += () =>
        {
            _editor.Tool.Value = tool;
            SyncToolButtons();
        };
        _toolButtons.Add((tool, button));
        return button;
    }

    private void SyncToolButtons()
    {
        foreach (var (tool, button) in _toolButtons) button.IsChecked = _editor.Tool.Value == tool;
    }

    private void RebuildRecentMenu()
    {
        _recentMenu.Items.Clear();
        var files = _settings.RecentFiles.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            _recentMenu.Item("（没有最近打开的文件）", isEnabled: false);
            return;
        }
        foreach (var f in files) _recentMenu.Item(Path.GetFileName(f), _cmdOpenRecent, f);
    }

    private void ToggleTheme()
    {
        var next = Theme.IsDark ? ThemeVariant.Light : ThemeVariant.Dark;
        Application.Current.SetThemeMode(next);
        _settings.Theme = next;
    }

    // ───────────────────────── 状态栏 ─────────────────────────

    private FrameworkElement BuildStatusBar()
    {
        static TextBlock Info(ObservableValue<string> source) => new TextBlock()
            .BindText(source)
            .FontSize(11.5)
            .CenterVertical()
            .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t));

        var crs = new TextBlock().FontSize(11.5).CenterVertical().WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t));
        void UpdateCrs()
        {
            var data = _editor.DataCrs.Value == CoordSystem.Gcj02 ? "GCJ-02" : "WGS-84";
            var base_ = _editor.BaseMap.Value;
            crs.Text = base_.IsBlank || base_.Crs == _editor.DataCrs.Value
                ? $"数据 {data}"
                : $"数据 {data} → 底图 {(base_.Crs == CoordSystem.Gcj02 ? "GCJ-02" : "WGS-84")}（已纠偏）";
        }
        _editor.DataCrs.Changed += UpdateCrs;
        _editor.BaseMap.Changed += UpdateCrs;
        UpdateCrs();

        var pointer = Info(_pointer).MinWidth(200);

        var right = new StackPanel().Horizontal().Spacing(18).DockRight().Children(
            Info(_selectionText),
            pointer,
            Info(_zoomText),
            crs);

        var hint = new StackPanel().Horizontal().Spacing(6).Children(
            new IconView(Icons.Info, 13).CenterVertical().WithTheme((t, i) => i.Tint = UiColors.Faint(t)),
            Info(_hint).TextTrimming(TextTrimming.CharacterEllipsis));

        return new Border()
            .Height(28)
            .BorderThickness(new Thickness(0, 1, 0, 0))
            .WithTheme((t, b) =>
            {
                b.Background(UiColors.Surface(t));
                b.BorderBrush(UiColors.Divider(t));
            })
            .Child(new DockPanel().Margin(12, 0).Children(right, hint));
    }

    // ───────────────────────── 地图区域 ─────────────────────────

    private FrameworkElement BuildMapHost()
    {
        _toolOptionsHost = new Border().Top().CenterHorizontal().Margin(0, 14, 0, 0);
        _editor.Tool.Changed += () =>
        {
            SyncToolButtons();
            RebuildToolOptions();
        };
        _editor.DrawTarget.Changed += UpdateTargetName;
        _editor.Doc.SelectionChanged += () =>
        {
            if (_editor.Tool.Value == EditTool.Select) RebuildToolOptions();
        };
        SyncToolButtons();
        RebuildToolOptions();

        var zoom = FloatingCard(new StackPanel().Vertical().Spacing(2).Children(
            IconButton(Icons.Plus, "放大", () => _map.ZoomBy(1)),
            IconButton(Icons.Minus, "缩小", () => _map.ZoomBy(-1)),
            new Border().Height(1).Margin(4, 2).WithTheme((t, b) => b.Background(UiColors.Divider(t))),
            IconButton(Icons.Fit, "显示全部数据（0 或 Shift+F）", () => _map.ZoomToAll()),
            IconButton(Icons.Target, "定位到所选（F）", ZoomToSelection)))
            .Padding(3)
            .Right()
            .Bottom()
            .Margin(0, 0, 14, 44);

        var empty = BuildEmptyState();
        empty.BindIsVisible(_isEmpty);

        return new Grid().Children(_map, _toolOptionsHost, zoom, empty);
    }

    private static Border FloatingCard(UIElement child)
        => new Border()
            .CornerRadius(10)
            .BorderThickness(1)
            .WithTheme((t, b) =>
            {
                b.Background(UiColors.Surface(t).WithAlpha(245));
                b.BorderBrush(UiColors.Divider(t));
            })
            .Child(child);

    private FrameworkElement BuildEmptyState()
    {
        var open = new Button()
            .StyleName(AppStyles.Primary)
            .Content(new StackPanel().Horizontal().Spacing(7).Children(
                new IconView(Icons.FolderOpen, 16).CenterVertical(),
                new TextBlock().Text("打开 GeoJSON").CenterVertical()))
            .OnClick(OpenDocument);
        var draw = new Button()
            .StyleName(AppStyles.Ghost)
            .Content(new StackPanel().Horizontal().Spacing(7).Children(
                new IconView(Icons.Polygon, 16).CenterVertical(),
                new TextBlock().Text("开始绘制").CenterVertical()))
            .OnClick(() => SetTool(EditTool.DrawPolygon));

        return FloatingCard(new StackPanel().Vertical().Spacing(10).Margin(28, 24).Children(
                new IconView(Icons.Layers, 36).CenterHorizontal().WithTheme((t, i) => i.Tint = t.Palette.Accent),
                new TextBlock().Text("开始一个层级地图").FontSize(16).SemiBold().CenterHorizontal(),
                new TextBlock()
                    .Text("把 .geojson 文件拖进窗口，或打开一个文件。\n也可以直接画一个面作为最上级区域，再用切割划分下级；\n或者从另一个窗口复制要素，按 ⌘/Ctrl+V 粘贴进来。")
                    .TextAlignment(TextAlignment.Center)
                    .TextWrapping(TextWrapping.Wrap)
                    .MaxWidth(320)
                    .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)),
                new StackPanel().Horizontal().Spacing(8).CenterHorizontal().Margin(0, 6, 0, 0).Children(open, draw)))
            .Center();
    }

    private void UpdateTargetName()
    {
        var t = _editor.DrawTarget.Value;
        _targetName.Value = t == null ? "根级" : t.DisplayName;
    }

    private void RebuildToolOptions()
    {
        var tool = _editor.Tool.Value;
        if (tool == EditTool.Select)
        {
            var sel = _editor.Doc.Selection;
            if (sel.Count == 1 && sel[0].Kind is NodeKind.Polygon or NodeKind.Line && sel[0].IsEffectivelyVisible)
            {
                ShowToolOptions(new StackPanel().Horizontal().Spacing(10).Children(
                    new StackPanel().Horizontal().Spacing(7).CenterVertical().Children(
                        new IconView(Icons.Select, 16).CenterVertical().WithTheme((t, i) => i.Tint = t.Palette.Accent),
                        new TextBlock().Text("编辑顶点").SemiBold().CenterVertical()),
                    VDivider(),
                    new CheckBox().Content("吸附").BindIsChecked(_editor.Snapping).CenterVertical().ToolTip("拖动顶点时对齐到附近的顶点和边"),
                    new CheckBox().Content("联动相邻边界").BindIsChecked(_editor.LinkedEditing).CenterVertical()
                        .ToolTip("移动公共边界上的顶点时，相邻区域和上级区域的同一个顶点一起移动，边界保持重合"),
                    VDivider(),
                    new TextBlock().Text("拖动方块移动 · 拖动边中点插入 · 右键删除").FontSize(12).CenterVertical()
                        .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t))));
            }
            else
            {
                _toolOptionsHost.IsVisible = false;
                _toolOptionsHost.Child = null;
            }
            return;
        }
        UpdateTargetName();

        var (icon, title) = tool switch
        {
            EditTool.DrawPoint => (Icons.Point, "放置点标记"),
            EditTool.DrawLine => (Icons.Line, "绘制线"),
            EditTool.DrawPolygon => (Icons.Polygon, "绘制面"),
            _ => (Icons.Cut, "切割"),
        };

        var row = new StackPanel().Horizontal().Spacing(10);
        row.Add(new StackPanel().Horizontal().Spacing(7).CenterVertical().Children(
            new IconView(icon, 17).CenterVertical().WithTheme((t, i) => i.Tint = t.Palette.Accent),
            new TextBlock().Text(title).SemiBold().CenterVertical()));
        row.Add(VDivider());

        if (tool is EditTool.DrawPoint or EditTool.DrawLine or EditTool.DrawPolygon)
        {
            _targetMenu = new Menu();
            var target = new DropDownButton()
                .StyleName(AppStyles.DropDown)
                .Content(new StackPanel().Horizontal().Spacing(6).Children(
                    new IconView(Icons.Hierarchy, 14).CenterVertical(),
                    new TextBlock().BindText(_targetName).MaxWidth(160).TextTrimming(TextTrimming.CharacterEllipsis).CenterVertical()))
                .DropDownMenu(_targetMenu)
                .OnDropDownOpening(RebuildTargetMenu)
                .ToolTip("新画的要素作为哪个节点的下级")
                .CenterVertical();
            row.Add(new TextBlock().Text("添加到").CenterVertical().WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)));
            row.Add(target);

            if (tool == EditTool.DrawPolygon)
            {
                row.Add(VDivider());
                row.Add(new CheckBox().Content("裁剪到上级").BindIsChecked(_editor.ClipToParent).CenterVertical()
                    .ToolTip("超出上级区域的部分自动去掉"));
                row.Add(new CheckBox().Content("避让同级").BindIsChecked(_editor.AvoidSiblings).CenterVertical()
                    .ToolTip("与已有同级区域重叠的部分自动去掉，相邻区域正好共用边界"));
            }
        }
        else
        {
            var mode = new ObservableValue<int>(_editor.CutMode.Value == CutMode.Replace ? 0 : 1);
            mode.Changed += () => _editor.CutMode.Value = mode.Value == 0 ? CutMode.Replace : CutMode.Subdivide;
            row.Add(new SegmentedControl()
                .Items("替换原区域", "划分为下级")
                .BindSelectedIndex(mode)
                .CenterVertical()
                .ToolTip("替换：切出的块取代原区域；划分为下级：原区域保留，切出的块成为它的下级"));
        }

        row.Add(VDivider());
        row.Add(new CheckBox().Content("吸附").BindIsChecked(_editor.Snapping).CenterVertical().ToolTip("靠近已有顶点和边时自动对齐"));

        row.Add(VDivider());
        if (tool != EditTool.DrawPoint)
        {
            row.Add(new Button()
                .StyleName(AppStyles.Primary)
                .Padding(10, 4)
                .Content(tool == EditTool.Cut ? "执行切割" : "完成")
                .ToolTip("也可以双击或按回车")
                .OnClick(() => _map.CompleteDrawing())
                .CenterVertical());
        }
        row.Add(new Button()
            .StyleName(AppStyles.Ghost)
            .Padding(10, 4)
            .Content("退出")
            .ToolTip("回到选择工具（Esc）")
            .OnClick(() => SetTool(EditTool.Select))
            .CenterVertical());

        ShowToolOptions(row);
    }

    private void ShowToolOptions(UIElement content)
    {
        _toolOptionsHost.Child = new ShadowDecorator()
            .BlurRadius(18)
            .OffsetY(4)
            .CornerRadius(12)
            .ShadowColor(Color.FromArgb(46, 15, 23, 42))
            .Child(new Border()
                .CornerRadius(12)
                .Padding(12, 7)
                .BorderThickness(1)
                .WithTheme((t, b) =>
                {
                    b.Background(UiColors.Surface(t));
                    b.BorderBrush(UiColors.Divider(t));
                })
                .Child(content));
        _toolOptionsHost.IsVisible = true;
    }

    private void RebuildTargetMenu()
    {
        _targetMenu.Items.Clear();
        var current = _editor.DrawTarget.Value;
        _targetMenu.Item((current == null ? "✓ " : "    ") + "根级（最上级）", _cmdSetTarget, "");

        var chain = new List<GeoNode>();
        if (current != null)
        {
            chain.AddRange(current.Ancestors().Reverse());
            chain.Add(current);
        }
        foreach (var n in chain)
        {
            string indent = new string(' ', n.Depth * 3);
            _targetMenu.Item((ReferenceEquals(n, current) ? "✓ " : "    ") + indent + n.DisplayName + (string.IsNullOrEmpty(n.Level) ? "" : $"（{n.Level}）"), _cmdSetTarget, n.Id);
        }

        var options = (current?.Children ?? _editor.Doc.Roots)
            .Where(c => c.Kind is NodeKind.Group or NodeKind.Polygon)
            .Take(30)
            .ToList();
        if (options.Count > 0)
        {
            _targetMenu.Separator();
            foreach (var n in options)
            {
                string indent = new string(' ', n.Depth * 3);
                _targetMenu.Item("    " + indent + n.DisplayName + (string.IsNullOrEmpty(n.Level) ? "" : $"（{n.Level}）"), _cmdSetTarget, n.Id);
            }
        }
    }

    // ───────────────────────── 底图面板 ─────────────────────────

    private void ShowBaseMapPopup(Button anchor)
    {
        var popup = new Popup { StaysOpen = false };
        var list = new StackPanel().Vertical().Spacing(2);

        void RebuildList()
        {
            list.Clear();
            foreach (var source in TileSource.All)
            {
                var s = source;
                bool selected = ReferenceEquals(_editor.BaseMap.Value, s);
                var dot = new Border()
                    .Size(16, 16)
                    .CornerRadius(8)
                    .BorderThickness(selected ? 5 : 1.5)
                    .CenterVertical()
                    .WithTheme((t, b) => b.BorderBrush(selected ? t.Palette.Accent : UiColors.Faint(t)));
                var item = new Button()
                    .StyleName(AppStyles.IconButton)
                    .Padding(10, 7)
                    .StretchHorizontal()
                    .Content(new DockPanel().Spacing(12).StretchHorizontal().Children(
                        dot.DockLeft(),
                        new StackPanel().Vertical().Spacing(1).Children(
                            new TextBlock().Text(s.Name).FontSize(13).SemiBold().WithTheme((t, tb) => tb.Foreground = t.Palette.WindowText),
                            new TextBlock().Text(s.Description).FontSize(11.5).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)))));
                item.Click += () =>
                {
                    _editor.BaseMap.Value = s;
                    RebuildList();
                };
                list.Add(item);
            }
        }
        RebuildList();

        var fade = new Slider().Minimum(0).Maximum(0.8).BindValue(_editor.BaseMapFade).Width(150).CenterVertical();
        var gray = new ToggleSwitch().BindIsChecked(_editor.BaseMapGray).CenterVertical();

        var content = new StackPanel().Vertical().Spacing(10).Children(
            new TextBlock().Text("底图").FontSize(12).SemiBold().Margin(10, 2, 0, 0).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)),
            list,
            new Border().Height(1).Margin(4, 2).WithTheme((t, b) => b.Background(UiColors.Divider(t))),
            new DockPanel().Margin(10, 0).Children(fade.DockRight(), new TextBlock().Text("底图淡化").CenterVertical()),
            new DockPanel().Margin(10, 0, 10, 4).Children(gray.DockRight(), new TextBlock().Text("灰度显示").CenterVertical()),
            new TextBlock()
                .Text("高德底图使用 GCJ-02 坐标，其余为 WGS-84。数据坐标系在右侧概况里设置，两者不同时自动纠偏显示。")
                .FontSize(11)
                .Margin(10, 0, 10, 6)
                .TextWrapping(TextWrapping.Wrap)
                .WithTheme((t, tb) => tb.Foreground = UiColors.Faint(t)));

        popup.Content = new ShadowDecorator()
            .BlurRadius(20)
            .OffsetY(6)
            .CornerRadius(12)
            .ShadowColor(Color.FromArgb(56, 15, 23, 42))
            .Child(new Border()
                .Width(320)
                .Padding(6, 10)
                .CornerRadius(12)
                .BorderThickness(1)
                .WithTheme((t, b) =>
                {
                    b.Background(UiColors.Surface(t));
                    b.BorderBrush(UiColors.Divider(t));
                })
                .Child(content));
        popup.ShowAt(anchor, anchor.Bounds, PopupAnchorSide.Below);
    }

    // ───────────────────────── 右键菜单 ─────────────────────────

    private ContextMenu BuildRowMenu()
        => new ContextMenu()
            .Item(_cmdZoomSel)
            .Item(_cmdRename)
            .Separator()
            .Item("剪切", StandardCommands.Cut)
            .Item("复制", StandardCommands.Copy)
            .Item("粘贴", StandardCommands.Paste)
            .Item(_cmdDuplicate)
            .Separator()
            .Item(_cmdNewGroup)
            .Item(_cmdRebuild)
            .Item(_cmdClip)
            .Item(_cmdMerge)
            .Item(_cmdSimplify)
            .Separator()
            .Item(_cmdToggleVis)
            .Item(_cmdExport)
            .Separator()
            .Item(_cmdDelete)
            .Item(_cmdDeleteKeep);

    private void ShowMapContextMenu(Point p)
    {
        _contextPoint = p;
        _mapMenu ??= new ContextMenu()
            .Item("剪切", StandardCommands.Cut)
            .Item("复制", StandardCommands.Copy)
            .Item("粘贴", StandardCommands.Paste)
            .Item(_cmdDuplicate)
            .Separator()
            .Item(_cmdMerge)
            .Item(_cmdSimplify)
            .Item(_cmdRebuild)
            .Item(_cmdClip)
            .Separator()
            .Item(_cmdZoomSel)
            .Item(_cmdToggleVis)
            .Item(_cmdRename)
            .Item(_cmdCopyCoordinate)
            .Separator()
            .Item(_cmdDelete);
        _mapMenu.Show(_map, new Point(_map.Bounds.X + p.X, _map.Bounds.Y + p.Y));
    }

    // ───────────────────────── 命令注册 ─────────────────────────

    private void RegisterCommands()
    {
        var primary = ModifierKeys.Primary;
        bool HasSel() => _editor.Doc.Selection.Count > 0;

        Commands.Register(_cmdNew, NewDocument);
        Commands.Register(_cmdNewWindow, () => OpenNewWindow());
        Commands.Register(_cmdOpen, OpenDocument);
        Commands.Register(_cmdOpenInNewWindow, OpenInNewWindow);
        Commands.Register<string>(_cmdOpenRecent, async path =>
        {
            if (!_busy && await ConfirmDiscardAsync()) await OpenPathAsync(path);
        });
        Commands.Register(_cmdImport, ImportDocument);
        Commands.Register(_cmdSave, () => SaveDocument(saveAs: false));
        Commands.Register(_cmdSaveAs, () => SaveDocument(saveAs: true));
        Commands.Register(_cmdExport, ExportSelection, HasSel);
        Commands.Register(_cmdUndo, () => _editor.Doc.Undo(), () => _editor.Doc.CanUndo);
        Commands.Register(_cmdRedo, () => _editor.Doc.Redo(), () => _editor.Doc.CanRedo);
        Commands.Register(_cmdNewGroup, () => _editor.NewGroup(Editor.SuggestDrawTarget(_editor.Doc.Primary)));
        Commands.Register(_cmdSearch, () => _layers.FocusSearch());
        Commands.Register(_cmdMerge, () => _editor.Merge(), () => _editor.CanMerge());
        Commands.Register(_cmdRebuild, () => _editor.RebuildFromChildren(), () => _editor.Doc.Selection.Any(_editor.CanRebuildFromChildren));
        Commands.Register(_cmdClip, () => _editor.ClipSelectionToParent(), () => _editor.Doc.Selection.Any(_editor.CanClipToParent));
        Commands.Register(_cmdZoomSel, ZoomToSelection, HasSel);
        Commands.Register(_cmdToggleVis, ToggleSelectionVisibility, HasSel);
        Commands.Register(_cmdDelete, () => _editor.DeleteSelection(), HasSel);
        Commands.Register(_cmdDeleteKeep, () => _editor.DeleteSelection(keepChildren: true), () => _editor.Doc.Selection.Any(n => n.Children.Count > 0));
        Commands.Register(_cmdRename, () => _inspector.FocusName(), () => _editor.Doc.Selection.Count == 1);
        Commands.Register<string>(_cmdSetTarget, id =>
        {
            _editor.DrawTarget.Value = string.IsNullOrEmpty(id) ? null : _editor.Doc.Find(id);
        });

        // 剪贴板走 MewUI 的标准命令：输入框有焦点时由输入框自己处理（复制文字），
        // 焦点在地图、图层树或按钮上时由这里处理（复制要素）。快捷键用的是应用级的 ⌘/Ctrl+C、X、V、A。
        Commands.Register(StandardCommands.Copy, CopySelection, HasSel);
        Commands.Register(StandardCommands.Cut, CutSelection, HasSel);
        Commands.Register(StandardCommands.Paste, PasteFromClipboard, () => !_busy);
        Commands.Register(StandardCommands.SelectAll, () => _editor.SelectSiblings(), () => _editor.Doc.Count > 0);
        Commands.Register(_cmdDuplicate, () => _editor.Duplicate(), HasSel);
        Commands.Register(_cmdCopyCoordinate, () => CopyCoordinate(_map.DataAt(_contextPoint)));
        Commands.Register(_cmdSimplify, ShowSimplifyDialog, () => _editor.Doc.Count > 0);

        InputMap.Map(_cmdNew, new KeyGesture(Key.N, primary));
        InputMap.Map(_cmdNewWindow, new KeyGesture(Key.N, primary | ModifierKeys.Shift));
        InputMap.Map(_cmdDuplicate, new KeyGesture(Key.D, primary));
        InputMap.Map(_cmdOpen, new KeyGesture(Key.O, primary));
        InputMap.Map(_cmdSave, new KeyGesture(Key.S, primary));
        InputMap.Map(_cmdSaveAs, new KeyGesture(Key.S, primary | ModifierKeys.Shift));
        InputMap.Map(_cmdImport, new KeyGesture(Key.I, primary));
        InputMap.Map(_cmdExport, new KeyGesture(Key.E, primary));
        InputMap.Map(_cmdUndo, new KeyGesture(Key.Z, primary));
        InputMap.Map(_cmdRedo, new KeyGesture(Key.Z, primary | ModifierKeys.Shift), new KeyGesture(Key.Y, primary));
        InputMap.Map(_cmdNewGroup, new KeyGesture(Key.G, primary));
        InputMap.Map(_cmdSearch, new KeyGesture(Key.F, primary));
    }

    private async void ShowSimplifyDialog()
    {
        if (_busy) return;
        await SimplifyDialog.ShowAsync(this, _editor);
    }

    // ───────────────────────── 快捷键说明 ─────────────────────────

    private async void ShowShortcuts()
    {
        (string Group, (string Keys, string Text)[] Items)[] groups =
        [
            ("工具", [
                ("V", "选择：单击选中，再次单击同一处选上一级；拖动空白处平移"),
                ("P", "放置点标记"),
                ("L", "绘制线"),
                ("A", "绘制面"),
                ("X", "切割：画线穿过区域，双击或回车执行"),
                ("Esc", "取消当前绘制 / 回到选择工具 / 清除选择"),
            ]),
            ("绘制中", [
                ("单击", "添加顶点（靠近已有顶点或边时自动吸附）"),
                ("双击 / 回车 / 右键", "完成"),
                ("退格", "撤回上一个顶点"),
                ("空格 + 拖动", "平移地图"),
            ]),
            ("编辑", [
                ("Shift/⌘/Ctrl 单击", "多选"),
                ("M", "合并所选的面或线"),
                ("拖动白色方块", "移动顶点（相邻区域的公共边界会联动）"),
                ("拖动边中点", "插入顶点"),
                ("右键 / 双击顶点", "删除顶点"),
                ("Delete", "删除所选"),
                ("H", "显示 / 隐藏所选"),
                ("F2", "重命名"),
                ("⌘/Ctrl+Z，⌘/Ctrl+Shift+Z", "撤销，重做"),
            ]),
            ("剪贴板", [
                ("⌘/Ctrl+C", "复制所选要素（含下级），可粘贴到另一个窗口、另一个程序实例或其他 GIS 软件"),
                ("⌘/Ctrl+X", "剪切"),
                ("⌘/Ctrl+V", "粘贴：选中面或分组时粘贴为它的下级；也能粘贴 GeoJSON、WKT 或“经度, 纬度”文本"),
                ("⌘/Ctrl+D", "在原位置创建副本"),
                ("⌘/Ctrl+A", "全选同级"),
                ("右键 → 复制此处坐标", "复制鼠标位置的经纬度"),
            ]),
            ("视图与文件", [
                ("F", "定位到所选"),
                ("0 或 Shift+F", "显示全部数据"),
                ("滚轮", "缩放"),
                ("⌘/Ctrl+F", "搜索图层"),
                ("⌘/Ctrl+O，⌘/Ctrl+S", "打开，保存"),
                ("⌘/Ctrl+Shift+N", "新建窗口（同时编辑几张地图）"),
                ("⌘/Ctrl+I，⌘/Ctrl+E", "导入到所选节点，导出所选"),
                ("⌘/Ctrl+G", "新建分组"),
            ]),
        ];

        var stack = new StackPanel().Vertical().Spacing(16);
        foreach (var (group, items) in groups)
        {
            var grid = new Grid().Columns("190,*");
            grid.Rows(string.Join(",", Enumerable.Repeat("Auto", items.Length)));
            for (int i = 0; i < items.Length; i++)
            {
                grid.Add(new Border()
                    .Padding(7, 2)
                    .Margin(0, 3, 12, 3)
                    .CornerRadius(5)
                    .Left()
                    .WithTheme((t, b) => b.Background(UiColors.Hover(t)))
                    .Child(new TextBlock().Text(items[i].Keys).FontSize(12))
                    .Row(i).Column(0));
                grid.Add(new TextBlock().Text(items[i].Text).FontSize(12.5).CenterVertical().TextWrapping(TextWrapping.Wrap).Row(i).Column(1));
            }
            stack.Add(new StackPanel().Vertical().Spacing(6).Children(
                new TextBlock().Text(group).FontSize(12).SemiBold().WithTheme((t, tb) => tb.Foreground = t.Palette.Accent),
                grid));
        }

        var dialog = new Window
        {
            Title = "快捷键与操作说明",
            StartupLocation = WindowStartupLocation.CenterOwner,
            WindowSize = WindowSize.Resizable(620, 640),
            Padding = new Thickness(0),
        };
        dialog.StyleSheet = AppStyles.Create();
        dialog.Content = new ScrollViewer().NoHorizontalScroll().Content(stack.Margin(24, 20));
        await dialog.ShowDialogAsync(this);
    }
}
