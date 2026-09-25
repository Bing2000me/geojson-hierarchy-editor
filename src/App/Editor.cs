using System.Text.Json.Nodes;

using Aprillz.MewUI;

using GeoJsonEditor.Geo;
using GeoJsonEditor.IO;
using GeoJsonEditor.Map;
using GeoJsonEditor.Model;

using NetTopologySuite.Geometries;

namespace GeoJsonEditor.App;

public enum EditTool
{
    Select,
    DrawPoint,
    DrawLine,
    DrawPolygon,
    Cut,
}

/// <summary>点标记的显示方式。</summary>
public enum PointDisplay
{
    /// <summary>逐级显示：点多时每一片只显示最重要的点，放大后逐级显示更多（常见地图软件显示地名的方式）。</summary>
    Declutter,
    /// <summary>聚合计数：相邻的点合成一个带数量的圆，放大逐级展开。</summary>
    Cluster,
    /// <summary>全部显示。</summary>
    All,
}

public enum CutMode
{
    /// <summary>切开的块替换原区域，原来的下级按位置分到各块。</summary>
    Replace,
    /// <summary>原区域保留，切出的块作为它的下级（已有下级时切它的下级）。</summary>
    Subdivide,
}

/// <summary>
/// 编辑器状态与全部编辑操作。界面（菜单、工具栏、画布、面板）只调这里的方法，
/// 所以同一个操作无论从哪里触发，行为都一致。
/// </summary>
public sealed class Editor
{
    public GeoDocument Doc { get; } = new();

    public ObservableValue<EditTool> Tool { get; } = new(EditTool.Select);
    public ObservableValue<TileSource> BaseMap { get; } = new(TileSource.Amap);
    public ObservableValue<CoordSystem> DataCrs { get; } = new(CoordSystem.Wgs84);
    public ObservableValue<double> BaseMapFade { get; } = new(0.35);
    public ObservableValue<bool> BaseMapGray { get; } = new(false);
    public ObservableValue<bool> ShowLabels { get; } = new(true);

    /// <summary>点标记的显示方式（逐级显示 / 聚合计数 / 全部显示）。</summary>
    public ObservableValue<PointDisplay> PointDisplay { get; } = new(App.PointDisplay.Declutter);

    /// <summary>
    /// 按层级缩放显示：缩小时下级合并成上级整块显示，放大后逐级展开（参照 P 社游戏的地图）。
    /// 关闭时所有层级同时显示。
    /// </summary>
    public ObservableValue<bool> RegionLod { get; } = new(true);

    /// <summary>层级展开的早晚（0 到 1）：越大越早展开下级，同一缩放下显示的细节越多。</summary>
    public ObservableValue<double> LodDetail { get; } = new(0.5);
    public ObservableValue<bool> Snapping { get; } = new(true);
    public ObservableValue<bool> LinkedEditing { get; } = new(true);
    public ObservableValue<bool> ClipToParent { get; } = new(true);
    public ObservableValue<bool> AvoidSiblings { get; } = new(true);
    public ObservableValue<CutMode> CutMode { get; } = new(App.CutMode.Replace);

    /// <summary>绘制工具新建的要素挂到哪个节点下（null 表示根级）。选择绘制工具时按当前选择确定。</summary>
    public ObservableValue<GeoNode?> DrawTarget { get; } = new(null);

    /// <summary>
    /// 正在编辑顶点的面或线（null 表示没有）。选中要素时只高亮，双击、回车或“编辑顶点”按钮才进入顶点编辑，
    /// 选择变了、换了工具或要素被删除、隐藏时自动退出。
    /// </summary>
    public ObservableValue<GeoNode?> VertexEditTarget { get; } = new(null);

    /// <summary>提示消息（界面用 toast 显示）。</summary>
    public event Action<string>? Notify;

    /// <summary>请求地图定位到某些节点。</summary>
    public event Action<IReadOnlyList<GeoNode>>? ZoomToRequested;

    public Editor()
    {
        Tool.Changed += OnToolChanged;
        Doc.SelectionChanged += ClearHighlights;
        Doc.SelectionChanged += CheckVertexEdit;
        Doc.Changed += _ =>
        {
            ClearHighlights();
            CheckVertexEdit();
        };
    }

    // ───────────────────────── 顶点编辑 ─────────────────────────

    /// <summary>能不能编辑这个要素的顶点：文档里可见的面或线。</summary>
    public bool CanEditVertices(GeoNode? node)
        => node is { Kind: NodeKind.Polygon or NodeKind.Line } && ReferenceEquals(Doc.Find(node.Id), node) && node.IsEffectivelyVisible;

    /// <summary>当前选择能不能进入顶点编辑（单选一个面或线）。</summary>
    public GeoNode? VertexEditCandidate => Doc.Selection.Count == 1 && CanEditVertices(Doc.Selection[0]) ? Doc.Selection[0] : null;

    public void BeginVertexEdit(GeoNode? node = null)
    {
        node ??= VertexEditCandidate;
        if (!CanEditVertices(node)) return;
        if (Tool.Value != EditTool.Select) Tool.Value = EditTool.Select;
        Doc.Select(node);
        VertexEditTarget.Value = node;
    }

    public void EndVertexEdit()
    {
        if (VertexEditTarget.Value != null) VertexEditTarget.Value = null;
    }

    private void CheckVertexEdit()
    {
        var target = VertexEditTarget.Value;
        if (target == null) return;
        if (Tool.Value != EditTool.Select || !CanEditVertices(target) || Doc.Selection.Count != 1 || !ReferenceEquals(Doc.Selection[0], target))
        {
            VertexEditTarget.Value = null;
        }
    }

    private void ClearHighlights()
    {
        if (Highlights.Value.Count > 0) Highlights.Value = Array.Empty<Geometry>();
    }

    private void Say(string message) => Notify?.Invoke(message);

    public void RequestZoomTo(IReadOnlyList<GeoNode> nodes)
    {
        if (nodes.Count > 0) ZoomToRequested?.Invoke(nodes);
    }

    // ───────────────────────── 工具 ─────────────────────────

    private void OnToolChanged()
    {
        CheckVertexEdit();
        if (Tool.Value is EditTool.DrawPoint or EditTool.DrawLine or EditTool.DrawPolygon)
        {
            DrawTarget.Value = SuggestDrawTarget(Doc.Primary);
        }
    }

    /// <summary>选中面或分组时新要素成为它的下级；选中点或线时与它同级。</summary>
    public static GeoNode? SuggestDrawTarget(GeoNode? selected) => selected switch
    {
        null => null,
        { Kind: NodeKind.Group or NodeKind.Polygon } => selected,
        _ => selected.Parent,
    };

    public static string ChildLevel(string? parentLevel) => parentLevel switch
    {
        "国家" => "省",
        "省" => "市",
        "市" => "区县",
        "区县" => "乡镇",
        "乡镇" => "村",
        _ => "",
    };

    private string DefaultName(NodeKind kind)
    {
        string prefix = kind switch
        {
            NodeKind.Point => "标记",
            NodeKind.Line => "线",
            NodeKind.Polygon => "区域",
            _ => "分组",
        };
        int n = Doc.AllNodes().Count(x => x.Kind == kind) + 1;
        return $"{prefix} {n}";
    }

    // ───────────────────────── 文件 ─────────────────────────

    public void NewDocument()
    {
        Doc.Load(Array.Empty<GeoNode>(), null, writesHierarchy: true);
    }

    public ReadResult Open(string path)
    {
        var result = GeoJsonIO.ReadFile(path);
        Load(result, path);
        return result;
    }

    /// <summary>
    /// 用已经读好的内容替换文档（大文件在后台线程读取，回到 UI 线程后调用）。
    /// <paramref name="roots"/> 为按识别结果整理过的树，为 null 时用读入的原样。
    /// 文件本来没有层级字段时，保存默认保持原有字段（见 <see cref="GeoDocument.WritesHierarchy"/>）。
    /// </summary>
    public void Load(ReadResult result, string? path, IReadOnlyList<GeoNode>? roots = null)
        => Doc.Load(roots ?? result.Roots, path, result.HasHierarchyFields);

    /// <summary>把文件导入到指定节点下（null 为根级）。</summary>
    public ReadResult Import(string path, GeoNode? parent)
    {
        var result = GeoJsonIO.ReadFile(path);
        AddImported(result.Roots, parent);
        return result;
    }

    /// <summary>
    /// 把已经读好的要素加到文档里，作为一步撤销。自动识别过层级时，根的 Parent 可能指向文档里已有的要素，
    /// 就挂到那里；其余的挂到 <paramref name="target"/> 下（null 为根级）。
    /// </summary>
    public void AddImported(IReadOnlyList<GeoNode> roots, GeoNode? target)
    {
        if (target != null && Doc.Find(target.Id) != target) target = null;
        Doc.Edit("导入", () =>
        {
            foreach (var r in roots)
            {
                var parent = r.Parent != null && ReferenceEquals(Doc.Find(r.Parent.Id), r.Parent) ? r.Parent : target;
                r.Parent = null;
                Doc.Add(r, parent);
            }
        });
        Doc.SetSelection(roots);
    }

    /// <summary>按识别结果重新组织当前文档的层级（一步撤销）。识别时新建的上级分组一起加进文档。</summary>
    public void ApplyHierarchy(Geo.HierarchyDetection detection)
    {
        var map = detection.ParentMap();
        var created = detection.UsedGroups();
        int changed = map.Count(kv => !ReferenceEquals(kv.Key.Parent, kv.Value));
        if (changed == 0 && created.Count == 0)
        {
            Say("层级没有变化。");
            return;
        }
        // 新建的分组先放在根级，再由 Restructure 按识别结果挂到各自的上级下
        var groupParents = new Dictionary<GeoNode, GeoNode?>(ReferenceEqualityComparer.Instance);
        foreach (var g in created) groupParents[g] = g.Parent;
        Doc.Edit("识别层级结构", () =>
        {
            foreach (var g in created)
            {
                g.Parent = null;
                g.ChildList.Clear();
                Doc.Add(g, null);
            }
            foreach (var (g, p) in groupParents) map[g] = p;
            Doc.Restructure(map);
        });
        Say(created.Count > 0
            ? $"已调整 {changed:N0} 个要素的上级，新建了 {created.Count:N0} 个上级分组（范围由下级自动拼成），可以撤销。"
            : $"已调整 {changed:N0} 个要素的上级，可以撤销。");
    }

    /// <param name="hierarchy">是否写入层级字段；为 null 时按文档的设置（见 <see cref="GeoDocument.WritesHierarchy"/>）。</param>
    public void Save(string path, bool? hierarchy = null)
    {
        GeoJsonIO.WriteFile(path, Doc.Roots, hierarchy ?? Doc.WritesHierarchy);
        Doc.FilePath = path;
        Doc.MarkSaved();
    }

    /// <summary>导出整个文档并保留层级信息（每个要素写入 id、parentId），不改变文档的保存位置。</summary>
    public void ExportWithHierarchy(string path)
    {
        GeoJsonIO.WriteFile(path, Doc.Roots, hierarchy: true);
    }

    public void ExportSelection(string path)
    {
        GeoJsonIO.WriteFile(path, SelectedRoots());
    }

    // ───────────────────────── 新建要素 ─────────────────────────

    /// <summary>绘制完成后调用。几何为数据坐标系下的经纬度。</summary>
    public GeoNode? AddDrawn(Geometry geometry)
    {
        var parent = DrawTarget.Value;
        if (parent != null && Doc.Find(parent.Id) != parent) parent = null;
        var kind = GeoNode.KindOf(geometry);

        if (kind == NodeKind.Polygon && (ClipToParent.Value || AvoidSiblings.Value))
        {
            var siblings = (parent?.Children ?? Doc.Roots)
                .Where(s => s.Kind == NodeKind.Polygon && s.Visible)
                .Select(s => s.Geometry);
            var fitted = GeometryOps.FitPolygon(geometry, parent?.Geometry, siblings, ClipToParent.Value, AvoidSiblings.Value);
            if (fitted == null)
            {
                Say("画出的面落在上级范围之外，或已被同级区域完全覆盖，没有生成新区域。");
                return null;
            }
            geometry = fitted;
        }

        var node = new GeoNode(Doc.NewId())
        {
            Name = DefaultName(kind),
            Level = kind == NodeKind.Polygon ? ChildLevel(parent?.Level) : "",
            Geometry = geometry,
        };
        Doc.Edit(kind switch
        {
            NodeKind.Point => "添加点标记",
            NodeKind.Line => "绘制线",
            _ => "绘制面",
        }, () => Doc.Add(node, parent));
        Doc.Select(node);
        return node;
    }

    public GeoNode NewGroup(GeoNode? parent)
    {
        var node = new GeoNode(Doc.NewId("g")) { Name = DefaultName(NodeKind.Group), Level = ChildLevel(parent?.Level) };
        Doc.Edit("新建分组", () => Doc.Add(node, parent));
        Doc.Select(node);
        return node;
    }

    /// <summary>
    /// 编组：为所选要素新建一个共同的上级分组（放在它们的最近公共上级下、第一个所选要素的位置）。
    /// 分组没有自身的边界，地图上由下级自动拼出范围，缩小时整块显示。没有选择时新建一个空分组。
    /// </summary>
    public GeoNode GroupSelection()
    {
        var roots = SelectedRoots();
        if (roots.Count == 0) return NewGroup(SuggestDrawTarget(Doc.Primary));
        var parent = GeoDocument.CommonAncestor(roots);
        // 公共上级是所选要素之一的上级链上的节点；插在第一个所选要素（或它在公共上级下的祖先）的位置
        GeoNode anchor = roots[0];
        while (anchor.Parent != parent && anchor.Parent != null) anchor = anchor.Parent;
        int index = Doc.IndexOf(anchor);
        var levels = roots.Select(n => LevelTiers.Of(n)).ToList();
        string level = "";
        if (levels.All(t => t != null) && levels.Distinct().Count() == 1)
        {
            level = levels[0] switch
            {
                1 => "国家",
                2 => "省",
                3 => "市",
                4 => "区县",
                _ => "",
            };
        }
        var group = new GeoNode(Doc.NewId("g")) { Name = DefaultName(NodeKind.Group), Level = level };
        Doc.Edit("编组", () =>
        {
            Doc.Add(group, parent, index);
            foreach (var n in roots) Doc.Move(n, group);
        });
        Doc.Select(group);
        Say($"已把 {roots.Count} 个要素编为「{group.DisplayName}」，它的范围由下级自动拼成。可以在右侧改名。");
        return group;
    }

    // ───────────────────────── 剪贴板 ─────────────────────────

    /// <summary>最上层的所选节点（上级已选中的不重复算），按文档中的先后顺序。</summary>
    public List<GeoNode> SelectedRoots()
    {
        var top = GeoDocument.TopMost(Doc.Selection);
        if (top.Count <= 1) return top;
        var order = new Dictionary<GeoNode, int>(ReferenceEqualityComparer.Instance);
        int i = 0;
        foreach (var n in Doc.AllNodes()) order[n] = i++;
        return top.OrderBy(n => order.GetValueOrDefault(n)).ToList();
    }

    /// <summary>把所选要素（连同全部下级）序列化成剪贴板文本；没有选择时返回 null。</summary>
    public string? CopySelection()
    {
        var roots = SelectedRoots();
        return roots.Count == 0 ? null : GeoClipboard.Serialize(roots, DataCrs.Value, Doc.InstanceId);
    }

    /// <summary>
    /// 粘贴到哪里：在同一个文档里复制后直接粘贴（没有选择，或者选中的就是被复制的要素），
    /// 放在原要素的后面、与它同级；否则按当前选择，与绘制新要素的规则相同（选中面或分组时成为它的下级）。
    /// </summary>
    public (GeoNode? Parent, int Index) PasteTarget(ClipboardContent content)
    {
        if (content.DocumentId == Doc.InstanceId && content.SourceIds.Count > 0)
        {
            var sel = Doc.Selection;
            bool selectionIsSource = sel.Count == 0 || sel.All(s => content.SourceIds.Contains(s.Id));
            var anchor = content.SourceIds.Select(Doc.Find).LastOrDefault(n => n != null);
            if (selectionIsSource && anchor != null) return (anchor.Parent, Doc.IndexOf(anchor) + 1);
        }
        return (SuggestDrawTarget(Doc.Primary), -1);
    }

    /// <summary>
    /// 粘贴剪贴板内容，作为一步撤销。来源坐标系与本文档不同时先纠偏；本文档还是空的就直接改用来源的坐标系。
    /// 返回粘贴进来的最上层节点。
    /// </summary>
    public List<GeoNode> Paste(ClipboardContent content)
    {
        var roots = content.Roots;
        if (roots.Count == 0) return roots;
        var (parent, index) = PasteTarget(content);
        if (parent != null && Doc.Find(parent.Id) != parent) parent = null;

        string note = "";
        if (content.Crs is { } crs && crs != DataCrs.Value)
        {
            string from = crs == CoordSystem.Gcj02 ? "GCJ-02" : "WGS-84";
            string to = DataCrs.Value == CoordSystem.Gcj02 ? "GCJ-02" : "WGS-84";
            if (Doc.Count == 0)
            {
                DataCrs.Value = crs;
                note = $"，数据坐标系已改为 {from}";
            }
            else
            {
                GeoClipboard.ConvertCrs(roots, crs, DataCrs.Value);
                note = $"，坐标已从 {from} 转换为 {to}";
            }
        }

        if (content.Format is ClipboardFormat.Wkt or ClipboardFormat.Coordinates)
        {
            foreach (var n in roots)
            {
                if (string.IsNullOrWhiteSpace(n.Name)) n.Name = DefaultName(n.Kind);
                if (n.Kind == NodeKind.Polygon) n.Level = ChildLevel(parent?.Level);
            }
        }

        Doc.Edit("粘贴", () =>
        {
            int i = index;
            foreach (var r in roots)
            {
                Doc.Add(r, parent, i);
                if (i >= 0) i++;
            }
        });
        Doc.SetSelection(roots);

        int total = roots.Sum(r => r.SelfAndDescendants().Count());
        string what = content.Format switch
        {
            ClipboardFormat.Wkt => "WKT 几何",
            ClipboardFormat.Coordinates => "坐标",
            _ => "要素",
        };
        Say($"已粘贴 {total} 个{what}到「{parent?.DisplayName ?? "根级"}」{note}。");
        return roots;
    }

    /// <summary>剪切：复制后删除（一步撤销）。</summary>
    public string? CutSelection()
    {
        var text = CopySelection();
        if (text != null) DeleteSelection(label: "剪切");
        return text;
    }

    /// <summary>在原要素后面创建副本（连同全部下级），副本名称加“副本”。</summary>
    public List<GeoNode> Duplicate()
    {
        var roots = SelectedRoots();
        var copies = new List<GeoNode>();
        if (roots.Count == 0) return copies;
        Doc.Edit("创建副本", () =>
        {
            foreach (var n in roots)
            {
                var copy = CloneTree(n);
                copy.Name = string.IsNullOrWhiteSpace(n.Name) ? "副本" : n.Name + " 副本";
                Doc.Add(copy, n.Parent, Doc.IndexOf(n) + 1);
                copies.Add(copy);
            }
        });
        Doc.SetSelection(copies);
        Say(copies.Count == 1 ? $"已创建「{copies[0].DisplayName}」。" : $"已创建 {copies.Count} 个副本。");
        return copies;
    }

    /// <summary>复制节点及其全部下级（几何和属性字典按不可变值共享，id 在加入文档时自动避开重复）。</summary>
    private static GeoNode CloneTree(GeoNode n)
    {
        var c = new GeoNode(n.Id)
        {
            Name = n.Name,
            Level = n.Level,
            Note = n.Note,
            Color = n.Color,
            Icon = n.Icon,
            Visible = n.Visible,
            Extra = n.Extra,
            Source = n.Source,
            Geometry = n.Geometry,
        };
        foreach (var child in n.Children)
        {
            var cc = CloneTree(child);
            cc.Parent = c;
            c.ChildList.Add(cc);
        }
        return c;
    }

    /// <summary>全选同级：选中与当前主选同一上级的全部要素；没有选择时选中全部根级要素。</summary>
    public void SelectSiblings()
    {
        var primary = Doc.Primary;
        Doc.SetSelection(primary == null ? Doc.Roots : Doc.SiblingsOf(primary));
    }

    // ───────────────────────── 合并 ─────────────────────────

    public bool CanMerge()
    {
        var sel = GeoDocument.TopMost(Doc.Selection);
        if (sel.Count < 2) return false;
        return sel.All(n => n.Kind == NodeKind.Polygon) || sel.All(n => n.Kind == NodeKind.Line);
    }

    public void Merge()
    {
        // 保持用户选择的先后顺序：第一个选中的节点保留名称和 id。
        var top = GeoDocument.TopMost(Doc.Selection);
        var sel = Doc.Selection.Where(n => top.Contains(n)).ToList();
        if (sel.Count < 2)
        {
            Say("至少选择两个面（或两条线）才能合并。按住 Shift 或 ⌘/Ctrl 单击可以多选。");
            return;
        }
        var kind = sel[0].Kind;
        if (kind is not (NodeKind.Polygon or NodeKind.Line) || sel.Any(n => n.Kind != kind))
        {
            Say("只能合并同一类要素：全部是面，或全部是线。");
            return;
        }

        Geometry? merged;
        try
        {
            merged = kind == NodeKind.Polygon
                ? GeometryOps.UnionPolygons(sel.Select(n => n.Geometry))
                : GeometryOps.MergeLines(sel.Select(n => n.Geometry));
        }
        catch (Exception ex)
        {
            Say("合并失败：" + ex.Message);
            return;
        }
        if (merged == null)
        {
            Say("合并结果为空。");
            return;
        }

        var keep = sel[0];
        var lca = GeoDocument.CommonAncestor(sel);
        bool movedUp = !ReferenceEquals(keep.Parent, lca);

        Doc.Edit("合并", () =>
        {
            Doc.SetGeometry(keep, merged);
            if (movedUp) Doc.Move(keep, lca, lca == null ? -1 : lca.Children.Count);
            foreach (var other in sel.Skip(1))
            {
                foreach (var child in other.Children.ToList()) Doc.Move(child, keep);
                Doc.Remove(other);
            }
        });
        Doc.Select(keep);

        int parts = merged.NumGeometries;
        string msg = $"已合并 {sel.Count} 个要素为「{keep.DisplayName}」";
        if (kind == NodeKind.Polygon && parts > 1) msg += $"，结果包含 {parts} 个不相连的部分";
        if (movedUp) msg += $"。所选要素不属于同一上级，合并结果放在「{lca?.DisplayName ?? "根级"}」下";
        Say(msg + "。");
    }

    // ───────────────────────── 切割 ─────────────────────────

    /// <summary>
    /// 切割的候选对象，在 UI 线程上从文档里取出（几何按不可变值共享），之后的精确判断可以放到后台线程。
    /// <see cref="Deepest"/> 为 true 时只保留最底层：候选的上级链用来判断“有没有下级也被切到”。
    /// </summary>
    public sealed record CutPlan(IReadOnlyList<CutCandidate> Candidates, GeoNode? SubdivideParent, bool Deepest);

    public sealed record CutCandidate(GeoNode Node, Geometry Geometry, NodeKind Kind, GeoNode[] Ancestors);

    /// <summary>
    /// 确定切割对象的第一步（UI 线程）。选中了要素时只切选中的（划分下级模式下，已有下级的切它的下级）；
    /// 什么都没选时，候选是包围盒与切割线相交的全部可见面和线，之后只保留最底层的。
    /// <paramref name="scope"/> 是地图上当前显示着的那一级（按层级缩放显示时）：只在其中找候选，
    /// 缩小时切的是合并显示的上级（它的下级跟着切开），而不是看不见的最底层。
    /// </summary>
    public CutPlan PlanCut(Envelope cutterEnvelope, IReadOnlySet<GeoNode>? scope = null)
    {
        static CutCandidate Candidate(GeoNode n) => new(n, n.Geometry!, n.Kind, n.Ancestors().ToArray());

        var selected = GeoDocument.TopMost(Doc.Selection)
            .Where(n => n.Kind is NodeKind.Polygon or NodeKind.Line)
            .ToList();

        if (CutMode.Value == App.CutMode.Subdivide && selected.Count == 1 && selected[0].Kind == NodeKind.Polygon)
        {
            var target = selected[0];
            var polyChildren = target.Children.Where(c => c.Kind == NodeKind.Polygon && c.Visible).ToList();
            return polyChildren.Count == 0
                ? new CutPlan([Candidate(target)], target, false)
                : new CutPlan(polyChildren.Select(Candidate).ToList(), null, false);
        }

        if (selected.Count > 0) return new CutPlan(selected.Select(Candidate).ToList(), null, false);

        var candidates = Doc.AllNodes()
            .Where(n => n.Kind is NodeKind.Polygon or NodeKind.Line && (scope == null || scope.Contains(n))
                        && n.Geometry!.EnvelopeInternal.Intersects(cutterEnvelope) && n.IsEffectivelyVisible)
            .Select(Candidate)
            .ToList();
        return new CutPlan(candidates, null, true);
    }

    /// <summary>确定切割对象的第二步：精确判断与切割线相交（不访问文档，可在后台线程调用）。</summary>
    public static List<GeoNode> ResolveCutTargets(CutPlan plan, LineString cutter)
    {
        var hits = plan.Candidates.Where(c => c.Geometry.Intersects(cutter)).ToList();
        if (!plan.Deepest) return hits.Select(c => c.Node).ToList();

        // 只保留最底层：被切到的面如果是另一个被切到的面的上级，就不切它
        var covered = new HashSet<GeoNode>(ReferenceEqualityComparer.Instance);
        foreach (var h in hits)
        {
            if (h.Kind != NodeKind.Polygon) continue;
            foreach (var a in h.Ancestors) covered.Add(a);
        }
        return hits.Where(c => c.Kind == NodeKind.Line || !covered.Contains(c.Node)).Select(c => c.Node).ToList();
    }

    /// <summary>切割预览：每个目标会被切成的块（不修改文档，可在后台线程调用）。</summary>
    public static List<Geometry> PreviewCut(CutPlan plan, LineString cutter)
    {
        var geometries = new Dictionary<GeoNode, CutCandidate>(ReferenceEqualityComparer.Instance);
        foreach (var c in plan.Candidates) geometries[c.Node] = c;
        var result = new List<Geometry>();
        foreach (var target in ResolveCutTargets(plan, cutter))
        {
            var c = geometries[target];
            try
            {
                var pieces = c.Kind == NodeKind.Polygon
                    ? GeometryOps.SplitPolygonal(c.Geometry, cutter)
                    : GeometryOps.SplitLineal(c.Geometry, cutter);
                if (pieces.Count > 1) result.AddRange(pieces);
            }
            catch
            {
            }
        }
        return result;
    }

    private List<GeoNode> CutTargets(LineString cutter, IReadOnlySet<GeoNode>? scope, out GeoNode? subdivideParent)
    {
        var plan = PlanCut(cutter.EnvelopeInternal, scope);
        subdivideParent = plan.SubdivideParent;
        return ResolveCutTargets(plan, cutter);
    }

    /// <param name="scope">没有选择时在哪些要素里找切割对象（地图上当前显示着的那一级）；为 null 时是全部可见要素。</param>
    public void Cut(LineString cutter, IReadOnlySet<GeoNode>? scope = null)
    {
        var targets = CutTargets(cutter, scope, out var subdivideParent);
        if (targets.Count == 0)
        {
            Say("切割线没有穿过可以切割的面或线。");
            return;
        }

        var created = new List<GeoNode>();
        int splitCount = 0;
        try
        {
            Doc.Edit("切割", () =>
            {
                if (subdivideParent != null)
                {
                    splitCount += SubdivideInto(subdivideParent, cutter, created) ? 1 : 0;
                }
                else
                {
                    foreach (var t in targets)
                    {
                        if (Doc.Find(t.Id) != t) continue;
                        if (SplitReplace(t, cutter, created)) splitCount++;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Say("切割失败：" + ex.Message);
            return;
        }

        if (splitCount == 0)
        {
            Say("切割线需要完整穿过要素（两端都在要素外面）才能切开。");
            return;
        }

        var select = subdivideParent != null ? subdivideParent.Children.ToList() : targets.Concat(created).Where(n => Doc.Find(n.Id) == n).ToList();
        Doc.SetSelection(select);
        Say(subdivideParent != null
            ? $"已把「{subdivideParent.DisplayName}」划分为 {subdivideParent.Children.Count(c => c.Kind == NodeKind.Polygon)} 个下级区域。"
            : $"已切开 {splitCount} 个要素，新增 {created.Count} 块。");
    }

    /// <summary>
    /// 把节点切开并替换：最大的一块保留原节点（id、名称、属性不变），其余块作为新节点插在它后面。
    /// 下级中与切割线相交的先递归切开，然后所有下级按位置分到各块，父子关系在几何上保持一致。
    /// </summary>
    private bool SplitReplace(GeoNode node, LineString cutter, List<GeoNode> created)
    {
        var pieces = node.Kind == NodeKind.Polygon
            ? GeometryOps.SplitPolygonal(node.Geometry!, cutter)
            : GeometryOps.SplitLineal(node.Geometry!, cutter);
        if (pieces.Count <= 1) return false;

        // 先切下级
        foreach (var child in node.Children.ToList())
        {
            if (child.Kind is NodeKind.Polygon && child.Geometry!.Intersects(cutter))
            {
                SplitReplace(child, cutter, created);
            }
        }

        var parent = node.Parent;
        int index = Doc.IndexOf(node);
        var pieceNodes = new List<GeoNode> { node };
        Doc.SetGeometry(node, pieces[0]);
        for (int i = 1; i < pieces.Count; i++)
        {
            var piece = new GeoNode(Doc.NewId())
            {
                Name = $"{node.Name}-{i + 1}",
                Level = node.Level,
                Note = node.Note,
                Icon = node.Icon,
                Visible = node.Visible,
                Geometry = pieces[i],
            };
            Doc.Add(piece, parent, index + i);
            pieceNodes.Add(piece);
            created.Add(piece);
        }

        if (node.Kind == NodeKind.Polygon)
        {
            foreach (var child in node.Children.ToList())
            {
                var rep = RepresentativeGeometry(child);
                if (rep == null) continue;
                int best = GeometryOps.BestPiece(rep, pieces);
                if (best != 0) Doc.Move(child, pieceNodes[best]);
            }
        }
        return true;
    }

    /// <summary>划分下级：原区域不变，切出的块作为它的下级；原有的点、线下级按位置归入各块。</summary>
    private bool SubdivideInto(GeoNode node, LineString cutter, List<GeoNode> created)
    {
        var pieces = GeometryOps.SplitPolygonal(node.Geometry!, cutter);
        if (pieces.Count <= 1) return false;
        var existing = node.Children.ToList();
        var level = ChildLevel(node.Level);
        var pieceNodes = new List<GeoNode>();
        for (int i = 0; i < pieces.Count; i++)
        {
            var piece = new GeoNode(Doc.NewId())
            {
                Name = $"{node.Name}-{i + 1}",
                Level = level,
                Geometry = pieces[i],
            };
            Doc.Add(piece, node);
            pieceNodes.Add(piece);
            created.Add(piece);
        }
        foreach (var child in existing)
        {
            var rep = RepresentativeGeometry(child);
            if (rep == null) continue;
            Doc.Move(child, pieceNodes[GeometryOps.BestPiece(rep, pieces)]);
        }
        return true;
    }

    private static Geometry? RepresentativeGeometry(GeoNode node)
    {
        if (node.Geometry != null) return node.Geometry;
        foreach (var d in node.Descendants())
        {
            if (d.Geometry != null) return d.Geometry;
        }
        return null;
    }

    // ───────────────────────── 层级几何 ─────────────────────────

    public bool CanRebuildFromChildren(GeoNode? node)
        => node != null && node.Kind is NodeKind.Group or NodeKind.Polygon && CollectChildPolygons(node).Any();

    private static IEnumerable<Geometry> CollectChildPolygons(GeoNode node)
    {
        foreach (var c in node.Children)
        {
            if (c.Kind == NodeKind.Polygon)
            {
                yield return c.Geometry!;
            }
            else if (c.Kind == NodeKind.Group)
            {
                foreach (var g in CollectChildPolygons(c)) yield return g;
            }
        }
    }

    /// <summary>用全部下级面的并集重新生成节点的范围。</summary>
    public void RebuildFromChildren()
    {
        var targets = Doc.Selection.Where(CanRebuildFromChildren).ToList();
        if (targets.Count == 0)
        {
            Say("所选节点没有下级面，无法生成边界。");
            return;
        }
        // 由深到浅处理，这样上级用到的是已经更新过的下级
        targets = targets.OrderByDescending(n => n.Depth).ToList();
        try
        {
            Doc.Edit("由下级生成边界", () =>
            {
                foreach (var t in targets)
                {
                    var union = GeometryOps.UnionPolygons(CollectChildPolygons(t));
                    if (union != null) Doc.SetGeometry(t, union);
                }
            });
            Say(targets.Count == 1 ? $"已用下级区域重新生成「{targets[0].DisplayName}」的边界。" : $"已重新生成 {targets.Count} 个节点的边界。");
        }
        catch (Exception ex)
        {
            Say("生成边界失败：" + ex.Message);
        }
    }

    public bool CanClipToParent(GeoNode? node)
        => node is { Parent.Kind: NodeKind.Polygon } && node.Kind is NodeKind.Polygon or NodeKind.Line;

    /// <summary>把所选要素裁剪到上级范围以内。</summary>
    public void ClipSelectionToParent()
    {
        var targets = Doc.Selection.Where(CanClipToParent).ToList();
        if (targets.Count == 0)
        {
            Say("所选要素没有面状上级，无法裁剪。");
            return;
        }
        int changed = 0, removed = 0;
        try
        {
            Doc.Edit("裁剪到上级", () =>
            {
                foreach (var t in targets)
                {
                    var clipped = GeometryOps.Intersection(t.Geometry!, t.Parent!.Geometry!);
                    clipped = t.Kind == NodeKind.Polygon ? Geometries.PolygonalPart(clipped) : Geometries.ToLineal(Geometries.Lines(clipped));
                    if (clipped == null)
                    {
                        removed++;
                        continue;
                    }
                    if (!clipped.EqualsTopologically(t.Geometry!))
                    {
                        Doc.SetGeometry(t, clipped);
                        changed++;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Say("裁剪失败：" + ex.Message);
            return;
        }
        Say(changed == 0 && removed == 0
            ? "所选要素已经完全在上级范围内。"
            : $"已裁剪 {changed} 个要素。" + (removed > 0 ? $"{removed} 个要素完全在上级之外，未做修改。" : ""));
    }

    // ───────────────────────── 删除与层级调整 ─────────────────────────

    public void DeleteSelection(bool keepChildren = false, string label = "删除")
    {
        var targets = GeoDocument.TopMost(Doc.Selection);
        if (targets.Count == 0) return;
        Doc.Edit(label, () =>
        {
            foreach (var t in targets)
            {
                if (keepChildren) Doc.RemoveKeepChildren(t);
                else Doc.Remove(t);
            }
        });
    }

    public void SetVisible(IEnumerable<GeoNode> nodes, bool visible)
    {
        var list = nodes.ToList();
        if (list.Count == 0) return;
        Doc.Edit(visible ? "显示" : "隐藏", () =>
        {
            foreach (var n in list) n.Visible = visible;
            Doc.Touch(ChangeKind.Visibility);
        });
    }

    public void ToggleVisible(GeoNode node) => SetVisible(new[] { node }, !node.Visible);

    public void MoveUp(GeoNode node)
    {
        int i = Doc.IndexOf(node);
        if (i <= 0) return;
        Doc.Edit("上移", () => Doc.Move(node, node.Parent, i - 1));
    }

    public void MoveDown(GeoNode node)
    {
        int i = Doc.IndexOf(node);
        if (i < 0 || i >= Doc.SiblingsOf(node).Count - 1) return;
        Doc.Edit("下移", () => Doc.Move(node, node.Parent, i + 2));
    }

    /// <summary>降一级：成为前一个同级节点的下级。</summary>
    public void Indent(GeoNode node)
    {
        int i = Doc.IndexOf(node);
        if (i <= 0) return;
        var newParent = Doc.SiblingsOf(node)[i - 1];
        Doc.Edit("降一级", () => Doc.Move(node, newParent));
    }

    /// <summary>升一级：移到上级的后面，与上级同级。</summary>
    public void Outdent(GeoNode node)
    {
        var parent = node.Parent;
        if (parent == null) return;
        int pi = Doc.IndexOf(parent);
        Doc.Edit("升一级", () => Doc.Move(node, parent.Parent, pi + 1));
    }

    public void MoveTo(IReadOnlyList<GeoNode> nodes, GeoNode? newParent)
    {
        var list = GeoDocument.TopMost(nodes).Where(n => Doc.CanMove(n, newParent)).ToList();
        if (list.Count == 0) return;
        Doc.Edit("移动到", () =>
        {
            foreach (var n in list) Doc.Move(n, newParent);
        });
    }

    // ───────────────────────── 属性 ─────────────────────────

    public void Rename(GeoNode node, string name, object? origin = null)
    {
        if (node.Name == name) return;
        Doc.Edit("重命名", () =>
        {
            node.Name = name;
            Doc.Touch(ChangeKind.Properties);
        }, coalesceKey: "name:" + node.Id, origin: origin);
    }

    public void SetLevel(GeoNode node, string level, object? origin = null)
    {
        if (node.Level == level) return;
        Doc.Edit("修改级别", () =>
        {
            node.Level = level;
            Doc.Touch(ChangeKind.Properties);
        }, coalesceKey: "level:" + node.Id, origin: origin);
    }

    public void SetNote(GeoNode node, string note, object? origin = null)
    {
        if (node.Note == note) return;
        Doc.Edit("修改备注", () =>
        {
            node.Note = note;
            Doc.Touch(ChangeKind.Properties);
        }, coalesceKey: "note:" + node.Id, origin: origin);
    }

    /// <summary>设置颜色。<paramref name="coalesce"/> 为 true 时（例如拖动取色盘）连续修改合并成一步撤销。</summary>
    public void SetColor(IEnumerable<GeoNode> nodes, string? color, bool coalesce = false)
    {
        var list = nodes.ToList();
        if (list.Count == 0 || list.All(n => n.Color == color)) return;
        Doc.Edit("修改颜色", () =>
        {
            foreach (var n in list) n.Color = color;
            Doc.Touch(ChangeKind.Properties);
        }, coalesceKey: coalesce ? "color:" + string.Join(",", list.Select(n => n.Id)) : null);
    }

    public void SetIcon(IEnumerable<GeoNode> nodes, MarkerIcon icon)
    {
        var list = nodes.Where(n => n.Kind == NodeKind.Point).ToList();
        if (list.Count == 0) return;
        Doc.Edit("修改图标", () =>
        {
            foreach (var n in list) n.Icon = icon;
            Doc.Touch(ChangeKind.Properties);
        });
    }

    /// <summary>修改或新增一个其他属性。连续输入同一属性合并成一步撤销。</summary>
    public void SetExtra(GeoNode node, string key, JsonNode? value, object? origin = null)
    {
        key = key.Trim();
        if (key.Length == 0) return;
        if (node.Extra.TryGetValue(key, out var old) && JsonNode.DeepEquals(old, value)) return;
        Doc.Edit("修改属性", () =>
        {
            var copy = new Dictionary<string, JsonNode?>(node.Extra, StringComparer.Ordinal) { [key] = value };
            node.Extra = copy;
            Doc.Touch(ChangeKind.Properties);
        }, coalesceKey: $"extra:{node.Id}:{key}", origin: origin);
    }

    public void RemoveExtra(GeoNode node, string key)
    {
        if (!node.Extra.ContainsKey(key)) return;
        Doc.Edit("删除属性", () =>
        {
            var copy = new Dictionary<string, JsonNode?>(node.Extra, StringComparer.Ordinal);
            copy.Remove(key);
            node.Extra = copy;
            Doc.Touch(ChangeKind.Properties);
        });
    }

    /// <summary>把输入框里的文字转成合适的 JSON 值：能解析成数字或布尔时保持原来的类型。</summary>
    public static JsonNode? ParseValue(string text, JsonNode? previous)
    {
        text = text.Trim();
        bool wasNumber = previous is JsonValue pv && pv.GetValueKind() == System.Text.Json.JsonValueKind.Number;
        bool wasBool = previous is JsonValue bv && bv.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
        if ((wasNumber || previous == null) && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && text.Length > 0 && !(text.Length > 1 && text[0] == '0' && text[1] != '.'))
        {
            return d % 1 == 0 && Math.Abs(d) < 9e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);
        }
        if (wasBool && bool.TryParse(text, out var b)) return JsonValue.Create(b);
        if (text == "null") return null;
        return JsonValue.Create(text);
    }

    // ───────────────────────── 层级检查 ─────────────────────────

    /// <summary>地图上临时高亮的问题区域（层级检查结果）。</summary>
    public ObservableValue<IReadOnlyList<Geometry>> Highlights { get; } = new(Array.Empty<Geometry>());

    public sealed record HierarchyIssue(string Kind, string Description, Geometry Area, GeoNode A, GeoNode? B);

    /// <summary>
    /// 检查一个节点和它的下级面在几何上是否一致：下级之间是否重叠、下级是否超出本区域、
    /// 本区域是否有没被下级覆盖的空隙。面积小于本区域的十亿分之一的视为浮点误差。
    /// </summary>
    public List<HierarchyIssue> CheckHierarchy(GeoNode node)
    {
        var issues = new List<HierarchyIssue>();
        var kids = node.Children.Where(c => c.Kind == NodeKind.Polygon).ToList();
        if (kids.Count == 0) return issues;
        var geoms = kids.Select(k => GeometryOps.Clean(k.Geometry!)).ToList();
        double scale = node.Kind == NodeKind.Polygon ? node.Geometry!.Area : geoms.Sum(g => g.Area);
        double eps = Math.Max(scale * 1e-9, 1e-14);

        for (int i = 0; i < kids.Count; i++)
        {
            for (int j = i + 1; j < kids.Count; j++)
            {
                if (!geoms[i].EnvelopeInternal.Intersects(geoms[j].EnvelopeInternal)) continue;
                var inter = Geometries.PolygonalPart(geoms[i].Intersection(geoms[j]));
                if (inter != null && inter.Area > eps)
                {
                    issues.Add(new HierarchyIssue("重叠", $"「{kids[i].DisplayName}」与「{kids[j].DisplayName}」重叠 {GeoMeasure.FormatArea(GeoMeasure.Area(inter))}", inter, kids[i], kids[j]));
                }
            }
        }

        if (node.Kind == NodeKind.Polygon)
        {
            var parent = GeometryOps.Clean(node.Geometry!);
            for (int i = 0; i < kids.Count; i++)
            {
                var outside = Geometries.PolygonalPart(geoms[i].Difference(parent));
                if (outside != null && outside.Area > eps)
                {
                    issues.Add(new HierarchyIssue("越界", $"「{kids[i].DisplayName}」超出本区域 {GeoMeasure.FormatArea(GeoMeasure.Area(outside))}", outside, kids[i], null));
                }
            }
            var union = GeometryOps.UnionPolygons(geoms);
            var gap = union == null ? parent : Geometries.PolygonalPart(parent.Difference(union));
            if (gap != null && gap.Area > eps)
            {
                issues.Add(new HierarchyIssue("空隙", $"有 {GeoMeasure.FormatArea(GeoMeasure.Area(gap))} 没有被任何下级覆盖", gap, node, null));
            }
        }
        return issues;
    }

    /// <summary>把节点的全部下级面、线裁剪到它的范围以内。</summary>
    public void ClipChildrenTo(GeoNode node)
    {
        if (node.Kind != NodeKind.Polygon) return;
        var kids = node.Children.Where(c => c.Kind is NodeKind.Polygon or NodeKind.Line).ToList();
        int changed = 0;
        Doc.Edit("裁剪下级", () =>
        {
            foreach (var k in kids)
            {
                var clipped = GeometryOps.Intersection(k.Geometry!, node.Geometry!);
                clipped = k.Kind == NodeKind.Polygon ? Geometries.PolygonalPart(clipped) : Geometries.ToLineal(Geometries.Lines(clipped));
                if (clipped == null || clipped.EqualsTopologically(k.Geometry!)) continue;
                Doc.SetGeometry(k, clipped);
                changed++;
            }
        });
        Say(changed == 0 ? "下级都已经在本区域以内。" : $"已把 {changed} 个下级裁剪到「{node.DisplayName}」以内。");
    }

    // ───────────────────────── 简化边界 ─────────────────────────

    /// <summary>要简化的面和线：所选要素及其全部下级，或整个文档。</summary>
    public List<(GeoNode Node, Geometry Geometry)> SimplifyCandidates(bool selectionOnly)
    {
        var nodes = selectionOnly ? SelectedRoots().SelectMany(r => r.SelfAndDescendants()) : Doc.AllNodes();
        return nodes
            .Where(n => n.Kind is NodeKind.Polygon or NodeKind.Line)
            .Select(n => (n, n.Geometry!))
            .ToList();
    }

    /// <summary>应用简化结果（一步撤销）。几何在计算期间没有变过的才替换。</summary>
    public void ApplySimplify(SimplifyResult result, double toleranceMeters)
    {
        var changes = result.Changes.Where(c => Doc.Find(c.Node.Id) == c.Node).ToList();
        if (changes.Count == 0) return;
        string tol = toleranceMeters >= 1000 ? $"{toleranceMeters / 1000:0.#} km" : $"{toleranceMeters:0} m";
        ReplaceGeometries($"简化边界（{tol}）", changes);
        double saved = result.VerticesBefore == 0 ? 0 : 1 - (double)result.VerticesAfter / result.VerticesBefore;
        Say($"已简化 {changes.Count:N0} 个要素，顶点从 {result.VerticesBefore:N0} 个减到 {result.VerticesAfter:N0} 个（减少 {saved * 100:0}%）。");
    }

    /// <summary>编辑顶点或拖动点标记时调用：一次拖动只记一步撤销。</summary>
    public void ReplaceGeometries(string label, IReadOnlyList<(GeoNode Node, Geometry Geometry)> changes)
    {
        if (changes.Count == 0) return;
        Doc.Edit(label, () =>
        {
            foreach (var (node, g) in changes) Doc.SetGeometry(node, g);
        });
    }
}
