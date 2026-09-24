using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;

using GeoJsonEditor.App;
using GeoJsonEditor.Map;
using GeoJsonEditor.Model;

namespace GeoJsonEditor.Ui;

/// <summary>左侧图层面板：层级树、搜索、层级调整。与文档选择集双向同步。</summary>
public sealed class LayerPanel
{
    private const string NodeFormat = "application/x-geojson-editor-nodes";

    private readonly Editor _editor;
    private readonly TreeView _tree;
    private readonly TextBox _search;
    private readonly TextBlock _count;
    private readonly List<GeoNode> _roots = new();
    private readonly HashSet<GeoNode> _matches = new(ReferenceEqualityComparer.Instance);
    private readonly TreeItemsView<GeoNode> _items;
    private bool _syncing;
    private string _filter = "";

    public LayerPanel(Editor editor, ContextMenu rowMenu)
    {
        _editor = editor;
        _items = TreeItemsView.Create<GeoNode>(
            _roots,
            ChildrenOf,
            textSelector: n => n.DisplayName,
            keySelector: n => n.Id);

        _tree = new TreeView()
            .ItemsSource(_items)
            .ItemHeight(30)
            .Indent(16)
            .ExpandTrigger(TreeViewExpandTrigger.ClickChevron);
        _tree.SelectionMode = ItemsSelectionMode.Extended;
        _tree.HorizontalScroll = ScrollMode.Disabled;
        _tree.ItemTemplate<GeoNode>(BuildRow, BindRow);
        _tree.PrepareContainer<GeoNode>((container, _, _, _) => container.ContextMenu = rowMenu);
        _tree.SelectedIndicesChanged += OnTreeSelectionChanged;
        _tree.MouseDoubleClick += OnTreeDoubleClick;
        _tree.WithTheme((t, tree) =>
        {
            tree.Background = UiColors.Surface(t);
            tree.BorderThickness = 0;
        });

        // 拖到树的空白处：移到根级
        _tree.AllowDrop = true;
        _tree.DragOver += e =>
        {
            if (e.Handled) return;
            if (TryGetDragged(e.Data, out var nodes) && nodes.Any(n => n.Parent != null))
            {
                e.Effect = DragDropEffects.Move;
                e.Accepted = true;
            }
        };
        _tree.Drop += e =>
        {
            if (e.Handled || !TryGetDragged(e.Data, out var nodes)) return;
            _editor.MoveTo(nodes, null);
            e.Effect = DragDropEffects.Move;
            e.Accepted = true;
        };

        _search = new TextBox()
            .Placeholder("搜索名称或级别")
            .OnTextChanged(text => ApplyFilter(text));
        _search.Padding = new Thickness(28, 0, 8, 0);

        _count = new TextBlock().FontSize(11.5).CenterVertical();
        _count.WithTheme((t, tb) => tb.Foreground = UiColors.Faint(t));

        _editor.Doc.Changed += OnDocumentChanged;
        _editor.Doc.SelectionChanged += OnDocumentSelectionChanged;

        View = Build();
        Refresh(expandTop: true);
    }

    public FrameworkElement View { get; }

    public void FocusSearch() => _search.Focus();

    private FrameworkElement Build()
    {
        var header = new DockPanel()
            .Height(44)
            .Margin(14, 0, 8, 0)
            .Children(
                new StackPanel().Horizontal().Spacing(2).DockRight().CenterVertical().Children(
                    IconButton(Icons.FolderPlus, "新建分组（⌘/Ctrl+G）", () => _editor.NewGroup(Editor.SuggestDrawTarget(_editor.Doc.Primary))),
                    IconButton(Icons.Expand, "全部展开", ExpandAll),
                    IconButton(Icons.Collapse, "全部折叠", CollapseAll)),
                new StackPanel().Horizontal().Spacing(8).CenterVertical().Children(
                    new TextBlock().Text("图层").FontSize(13).SemiBold().CenterVertical(),
                    _count));

        var searchBox = new Grid().Margin(12, 0, 12, 10).Children(
            _search,
            new IconView(Icons.Search, 14) { Margin = new Thickness(9, 0, 0, 0) }
                .CenterVertical()
                .Left()
                .WithTheme((t, i) => i.Tint = UiColors.Faint(t)));

        var footer = new Border()
            .BorderThickness(new Thickness(0, 1, 0, 0))
            .Padding(8, 6)
            .WithTheme((t, b) => b.BorderBrush(UiColors.Divider(t)))
            .Child(new DockPanel().Children(
                new StackPanel().Horizontal().Spacing(2).DockRight().Children(
                    IconButton(Icons.Eye, "显示 / 隐藏（H）", () => ToggleVisibleSelection()),
                    IconButton(Icons.Trash, "删除（Delete）", () => _editor.DeleteSelection())),
                new StackPanel().Horizontal().Spacing(2).Children(
                    IconButton(Icons.ArrowUp, "上移", () => Each(n => _editor.MoveUp(n))),
                    IconButton(Icons.ArrowDown, "下移", () => Each(n => _editor.MoveDown(n), reverse: true)),
                    IconButton(Icons.Outdent, "升一级：移到上级的后面", () => Each(n => _editor.Outdent(n))),
                    IconButton(Icons.Indent, "降一级：成为上一个同级节点的下级", () => Each(n => _editor.Indent(n))))));

        return new Border()
            .WithTheme((t, b) => b.Background(UiColors.Surface(t)))
            .Child(new DockPanel().Children(
                header.DockTop(),
                searchBox.DockTop(),
                footer.DockBottom(),
                _tree));
    }

    private static Button IconButton(string icon, string tip, Action click)
        => new Button()
            .StyleName(AppStyles.IconButton)
            .Content(new IconView(icon, 16))
            .ToolTip(tip)
            .OnClick(click);

    private void Each(Action<GeoNode> action, bool reverse = false)
    {
        var nodes = GeoDocument.TopMost(_editor.Doc.Selection)
            .OrderBy(n => _editor.Doc.IndexOf(n))
            .ToList();
        if (reverse) nodes.Reverse();
        foreach (var n in nodes) action(n);
    }

    private void ToggleVisibleSelection()
    {
        var sel = _editor.Doc.Selection.ToList();
        if (sel.Count == 0) return;
        _editor.SetVisible(sel, !sel.All(n => n.Visible));
    }

    // ───────────────────────── 行模板 ─────────────────────────

    private FrameworkElement BuildRow(TemplateContext ctx)
    {
        var eye = new Button()
            .StyleName(AppStyles.IconButton)
            .Padding(3)
            .Content(new IconView(Icons.Eye, 14).Register(ctx, "EyeIcon"))
            .Register(ctx, "Eye");
        eye.Click += () =>
        {
            if (eye.Tag is GeoNode n) _editor.ToggleVisible(n);
        };

        var levelBadge = new Border()
            .Padding(5, 0)
            .CornerRadius(4)
            .CenterVertical()
            .Register(ctx, "LevelBadge")
            .WithTheme((t, b) => b.Background(UiColors.Hover(t)))
            .Child(new TextBlock().FontSize(10.5).Register(ctx, "Level").WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)));

        var grid = new Grid()
            .Columns("20,*,Auto,Auto,Auto")
            .Height(30)
            .Children(
                new IconView(Icons.ShapeSquare, 14).Register(ctx, "Kind").CenterVertical().Column(0),
                new TextBlock()
                    .Register(ctx, "Name")
                    .Margin(6, 0, 4, 0)
                    .CenterVertical()
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .Column(1),
                levelBadge.Column(2),
                new TextBlock()
                    .Register(ctx, "Count")
                    .FontSize(11)
                    .Margin(6, 0, 2, 0)
                    .CenterVertical()
                    .WithTheme((t, tb) => tb.Foreground = UiColors.Faint(t))
                    .Column(3),
                eye.Margin(2, 0, 4, 0).CenterVertical().Column(4));

        // 行可以拖动：拖到另一行上成为它的下级
        var row = new Border().CornerRadius(5).Register(ctx, "Row").Child(grid);
        row.CanDrag = true;
        row.AllowDrop = true;
        row.DragStarting += e =>
        {
            if (row.Tag is not GeoNode n)
            {
                e.Cancel = true;
                return;
            }
            var nodes = _editor.Doc.IsSelected(n) ? GeoDocument.TopMost(_editor.Doc.Selection) : new List<GeoNode> { n };
            var data = new DataObject();
            data.SetData(NodeFormat, nodes.Select(x => x.Id).ToArray());
            e.Data = data;
            e.AllowedEffects = DragDropEffects.Move;
        };
        row.DragOver += e =>
        {
            if (row.Tag is GeoNode target && TryGetDragged(e.Data, out var nodes) && CanDropOn(nodes, target))
            {
                e.Effect = DragDropEffects.Move;
                e.Accepted = true;
                e.Handled = true;
                row.WithTheme((t, b) => b.Background(UiColors.AccentSoft(t)));
            }
        };
        row.DragLeave += _ => row.Background = Color.Transparent;
        row.Drop += e =>
        {
            row.Background = Color.Transparent;
            if (row.Tag is not GeoNode target || !TryGetDragged(e.Data, out var nodes) || !CanDropOn(nodes, target)) return;
            _editor.MoveTo(nodes, target);
            e.Effect = DragDropEffects.Move;
            e.Accepted = true;
            e.Handled = true;
        };
        return row;
    }

    private bool TryGetDragged(IDataObject data, out List<GeoNode> nodes)
    {
        nodes = new List<GeoNode>();
        if (!data.TryGetData<string[]>(NodeFormat, out var ids)) return false;
        foreach (var id in ids)
        {
            if (_editor.Doc.Find(id) is { } n) nodes.Add(n);
        }
        return nodes.Count > 0;
    }

    private bool CanDropOn(List<GeoNode> nodes, GeoNode target)
        => nodes.All(n => !ReferenceEquals(n, target) && !ReferenceEquals(n.Parent, target) && _editor.Doc.CanMove(n, target));

    private void BindRow(FrameworkElement view, GeoNode node, int index, TemplateContext ctx)
    {
        var row = ctx.Get<Border>("Row");
        row.Tag = node;
        row.Background = Color.Transparent;
        var kind = ctx.Get<IconView>("Kind");
        var color = MapStyle.ColorOf(node);
        var tint = Color.FromArgb(255, color.Red, color.Green, color.Blue);
        switch (node.Kind)
        {
            case NodeKind.Polygon:
                kind.Data = Icons.ShapeSquare;
                kind.Filled = true;
                kind.SetTint(tint);
                break;
            case NodeKind.Line:
                kind.Data = Icons.Line;
                kind.Filled = false;
                kind.StrokeWidth = 2.4;
                kind.SetTint(tint);
                break;
            case NodeKind.Point:
                kind.Data = node.Icon switch
                {
                    MarkerIcon.Circle => Icons.ShapeCircle,
                    MarkerIcon.Square => Icons.ShapeSquare,
                    MarkerIcon.Triangle => Icons.ShapeTriangle,
                    MarkerIcon.Star => Icons.ShapeStar,
                    MarkerIcon.Flag => Icons.ShapeFlag,
                    _ => Icons.ShapePin,
                };
                kind.Filled = node.Icon != MarkerIcon.Flag;
                kind.StrokeWidth = 2.2;
                kind.SetTint(tint);
                break;
            default:
                kind.Data = Icons.Folder;
                kind.Filled = false;
                kind.StrokeWidth = 1.8;
                kind.SetTint(null);
                break;
        }

        var name = ctx.Get<TextBlock>("Name");
        name.Text = node.DisplayName;
        name.Opacity = node.IsEffectivelyVisible ? 1.0 : 0.45;
        name.FontWeight = node.Children.Count > 0 ? FontWeight.SemiBold : FontWeight.Normal;

        var badge = ctx.Get<Border>("LevelBadge");
        badge.IsVisible = !string.IsNullOrEmpty(node.Level);
        ctx.Get<TextBlock>("Level").Text = node.Level;

        var count = ctx.Get<TextBlock>("Count");
        count.Text = node.Children.Count > 0 ? node.Children.Count.ToString() : "";

        var eye = ctx.Get<Button>("Eye");
        eye.Tag = node;
        eye.ToolTip(node.Visible ? "隐藏" : "显示");
        var eyeIcon = ctx.Get<IconView>("EyeIcon");
        eyeIcon.Data = node.Visible ? Icons.Eye : Icons.EyeOff;
        eye.Opacity = node.Visible ? 0.55 : 1.0;
    }

    // ───────────────────────── 数据 ─────────────────────────

    private IReadOnlyList<GeoNode> ChildrenOf(GeoNode node)
    {
        if (_filter.Length == 0) return node.Children;
        return node.Children.Where(_matches.Contains).ToList();
    }

    private void OnDocumentChanged(DocumentChange change)
    {
        if ((change.Kind & (ChangeKind.Structure | ChangeKind.Reset | ChangeKind.Properties | ChangeKind.Visibility | ChangeKind.Geometry)) == 0) return;
        Refresh(expandTop: (change.Kind & ChangeKind.Reset) != 0 && _editor.Doc.Count > 0 && _roots.Count == 0);
        SyncFromDocument();
    }

    private void Refresh(bool expandTop = false)
    {
        _roots.Clear();
        if (_filter.Length == 0)
        {
            _roots.AddRange(_editor.Doc.Roots);
        }
        else
        {
            RebuildMatches();
            _roots.AddRange(_editor.Doc.Roots.Where(_matches.Contains));
        }
        _items.Invalidate();

        if (expandTop)
        {
            // 默认展开前两级
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items.GetDepth(i) < 2 && _items.GetHasChildren(i) && !_items.GetIsExpanded(i)) _items.SetIsExpanded(i, true);
            }
        }
        else if (_filter.Length > 0)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items.GetHasChildren(i) && !_items.GetIsExpanded(i)) _items.SetIsExpanded(i, true);
            }
        }

        int total = _editor.Doc.Count;
        _count.Text = _filter.Length == 0 ? total.ToString() : $"{_matches.Count(n => Matches(n))} / {total}";
    }

    private void ApplyFilter(string text)
    {
        _filter = text.Trim();
        Refresh();
        SyncFromDocument();
    }

    private bool Matches(GeoNode n)
        => n.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || n.Level.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private void RebuildMatches()
    {
        _matches.Clear();
        foreach (var n in _editor.Doc.AllNodes())
        {
            if (!Matches(n)) continue;
            _matches.Add(n);
            foreach (var a in n.Ancestors()) _matches.Add(a);
        }
    }

    private void ExpandAll()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items.GetHasChildren(i) && !_items.GetIsExpanded(i)) _items.SetIsExpanded(i, true);
        }
    }

    private void CollapseAll()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items.GetIsExpanded(i)) _items.SetIsExpanded(i, false);
        }
        SyncFromDocument();
    }

    // ───────────────────────── 选择同步 ─────────────────────────

    private void OnTreeSelectionChanged()
    {
        if (_syncing) return;
        var selected = new List<GeoNode>();
        foreach (int i in _tree.SelectedIndices)
        {
            if (_items.GetItem(i) is GeoNode n) selected.Add(n);
        }
        // 保持“最后点选的是主选”：把树的主选行放到最后
        if (_items.SelectedItem is GeoNode primary && selected.Remove(primary)) selected.Add(primary);

        _syncing = true;
        try
        {
            _editor.Doc.SetSelection(selected);
            if (_editor.Tool.Value is EditTool.DrawPoint or EditTool.DrawLine or EditTool.DrawPolygon)
            {
                _editor.DrawTarget.Value = Editor.SuggestDrawTarget(_editor.Doc.Primary);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnDocumentSelectionChanged()
    {
        if (_syncing) return;
        SyncFromDocument(reveal: true);
    }

    /// <summary>把文档选择集同步到树上；<paramref name="reveal"/> 时展开上级并滚动到主选节点。</summary>
    private void SyncFromDocument(bool reveal = false)
    {
        _syncing = true;
        try
        {
            var selection = new HashSet<GeoNode>(_editor.Doc.Selection, ReferenceEqualityComparer.Instance);
            if (reveal && selection.Count > 0) Reveal(selection);

            _items.ClearSelection();
            int primaryIndex = -1;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items.GetItem(i) is GeoNode n && selection.Contains(n))
                {
                    _items.SetSelected(i, true);
                    if (ReferenceEquals(n, _editor.Doc.Primary)) primaryIndex = i;
                }
            }
            if (primaryIndex >= 0 && reveal) _tree.ScrollIntoView(primaryIndex);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 展开所选节点的全部上级。只遍历一遍行：展开某行后它的下级紧接着出现在后面，
    /// 同一遍里就会继续检查到，选中几千个节点时也不会变慢。
    /// </summary>
    private void Reveal(HashSet<GeoNode> selection)
    {
        var ancestors = new HashSet<GeoNode>(ReferenceEqualityComparer.Instance);
        foreach (var n in selection)
        {
            foreach (var a in n.Ancestors())
            {
                if (!ancestors.Add(a)) break;
            }
        }
        if (ancestors.Count == 0) return;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items.GetItem(i) is GeoNode n && ancestors.Contains(n) && !_items.GetIsExpanded(i)) _items.SetIsExpanded(i, true);
        }
    }

    private void OnTreeDoubleClick(MouseEventArgs e)
    {
        if (_tree.TryGetItemIndexAt(e, out int index) && _items.GetItem(index) is GeoNode n)
        {
            _editor.RequestZoomTo(new[] { n });
        }
    }
}
