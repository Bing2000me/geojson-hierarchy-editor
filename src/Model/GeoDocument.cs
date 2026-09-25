using System.Text.Json.Nodes;

using NetTopologySuite.Geometries;

namespace GeoJsonEditor.Model;

[Flags]
public enum ChangeKind
{
    None = 0,
    /// <summary>节点增删、移动、重排。</summary>
    Structure = 1,
    /// <summary>几何形状变化。</summary>
    Geometry = 2,
    /// <summary>名称、级别、备注、颜色、图标等属性。</summary>
    Properties = 4,
    /// <summary>显示 / 隐藏。</summary>
    Visibility = 8,
    /// <summary>整个文档被替换（打开、新建、撤销、重做）。</summary>
    Reset = 16,
    All = Structure | Geometry | Properties | Visibility | Reset,
}

public readonly record struct DocumentChange(ChangeKind Kind, object? Origin);

/// <summary>
/// 层级文档：节点树、按 id 的索引、选择集，以及快照式撤销 / 重做。
/// 所有修改都通过 <see cref="Edit"/> 包起来，这样撤销、脏标记和界面刷新只有一条路径。
/// </summary>
public sealed class GeoDocument
{
    private const int MaxUndo = 200;

    /// <summary>撤销历史里节点状态的总数上限：文档很大时自动减少可撤销的步数，控制内存。</summary>
    private const int MaxUndoNodeStates = 4_000_000;

    private readonly List<GeoNode> _roots = new();
    private readonly Dictionary<string, GeoNode> _byId = new(StringComparer.Ordinal);
    private readonly List<GeoNode> _selection = new();
    private readonly List<UndoEntry> _undo = new();
    private readonly List<UndoEntry> _redo = new();
    private long _serial;
    private long _savedState;
    private int _editDepth;
    private ChangeKind _pendingKind;
    private object? _pendingOrigin;
    private int _idCounter;

    public IReadOnlyList<GeoNode> Roots => _roots;

    /// <summary>
    /// 文档实例的标识，每次打开或新建都会换一个。复制到剪贴板时带上它，
    /// 粘贴时据此判断是不是在同一个文档里复制粘贴。
    /// </summary>
    public string InstanceId { get; private set; } = Guid.NewGuid().ToString("N");

    public string? FilePath { get; set; }

    /// <summary>
    /// 保存时是否把层级写进要素属性（id、parentId）。新建的文档和本来就带 parentId 的文件为 true；
    /// 打开的是没有层级字段的文件时为 false：识别出的上下级关系只保存在程序里，保存时保持原有字段，
    /// 用户选择“写入层级信息”或“导出并保留层级信息”时才写入。
    /// </summary>
    public bool WritesHierarchy { get; set; } = true;

    /// <summary>用户已经确认过“只保存原有字段”（同一个文档不再询问）。</summary>
    public bool PlainSaveConfirmed { get; set; }

    public string DisplayName => FilePath is null ? "未命名" : Path.GetFileNameWithoutExtension(FilePath);

    public bool IsDirty => CurrentState != _savedState;

    private long CurrentState => _undo.Count == 0 ? 0 : _undo[^1].Serial;

    public event Action<DocumentChange>? Changed;
    public event Action? SelectionChanged;
    public event Action? HistoryChanged;

    // ───────────────────────── 查询 ─────────────────────────

    public int Count => _byId.Count;

    public GeoNode? Find(string? id) => id != null && _byId.TryGetValue(id, out var n) ? n : null;

    /// <summary>全部节点，先序（上级在前）。</summary>
    public IEnumerable<GeoNode> AllNodes()
    {
        foreach (var r in _roots)
        {
            foreach (var n in r.SelfAndDescendants()) yield return n;
        }
    }

    public IReadOnlyList<GeoNode> SiblingsOf(GeoNode node) => node.Parent?.ChildList ?? _roots;

    public int IndexOf(GeoNode node) => IndexOfIn(SiblingsOf(node), node);

    private static int IndexOfIn(IReadOnlyList<GeoNode> list, GeoNode node)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], node)) return i;
        }
        return -1;
    }

    public string NewId(string prefix = "n")
    {
        string id;
        do
        {
            id = prefix + (++_idCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        while (_byId.ContainsKey(id));
        return id;
    }

    /// <summary>从一组节点中去掉“上级也在集合里”的节点。</summary>
    public static List<GeoNode> TopMost(IEnumerable<GeoNode> nodes)
    {
        var set = new HashSet<GeoNode>(nodes, ReferenceEqualityComparer.Instance);
        var result = new List<GeoNode>();
        foreach (var n in set)
        {
            bool covered = false;
            for (var p = n.Parent; p != null; p = p.Parent)
            {
                if (set.Contains(p)) { covered = true; break; }
            }
            if (!covered) result.Add(n);
        }
        return result;
    }

    /// <summary>最近公共上级；没有时返回 null（即根）。</summary>
    public static GeoNode? CommonAncestor(IReadOnlyList<GeoNode> nodes)
    {
        if (nodes.Count == 0) return null;
        var chain = new List<GeoNode?>();
        for (var p = nodes[0].Parent; p != null; p = p.Parent) chain.Add(p);
        chain.Add(null);
        foreach (var candidate in chain)
        {
            bool all = true;
            for (int i = 1; i < nodes.Count && all; i++)
            {
                all = candidate == null || candidate.IsAncestorOf(nodes[i]);
            }
            if (all) return candidate;
        }
        return null;
    }

    // ───────────────────────── 选择集 ─────────────────────────

    public IReadOnlyList<GeoNode> Selection => _selection;

    /// <summary>最后选中的节点。</summary>
    public GeoNode? Primary => _selection.Count > 0 ? _selection[^1] : null;

    // 选择集很大时（例如全选几千个同级）按引用查找的集合，选择集一变就作废
    private HashSet<GeoNode>? _selectionSet;

    public bool IsSelected(GeoNode node)
    {
        if (_selection.Count <= 8) return _selection.Contains(node);
        _selectionSet ??= new HashSet<GeoNode>(_selection, ReferenceEqualityComparer.Instance);
        return _selectionSet.Contains(node);
    }

    public void Select(GeoNode? node)
    {
        if (node == null)
        {
            ClearSelection();
            return;
        }
        if (_selection.Count == 1 && ReferenceEquals(_selection[0], node)) return;
        _selectionSet = null;
        _selection.Clear();
        _selection.Add(node);
        SelectionChanged?.Invoke();
    }

    public void SetSelection(IEnumerable<GeoNode> nodes)
    {
        var list = nodes.Distinct().ToList();
        if (list.SequenceEqual(_selection)) return;
        _selectionSet = null;
        _selection.Clear();
        _selection.AddRange(list);
        SelectionChanged?.Invoke();
    }

    public void ToggleSelected(GeoNode node)
    {
        _selectionSet = null;
        if (!_selection.Remove(node)) _selection.Add(node);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selectionSet = null;
        _selection.Clear();
        SelectionChanged?.Invoke();
    }

    // ───────────────────────── 结构操作（在 Edit 内调用） ─────────────────────────

    public void Add(GeoNode node, GeoNode? parent, int index = -1)
    {
        RequireEdit();
        if (_byId.ContainsKey(node.Id)) node.Id = NewId();
        var list = parent?.ChildList ?? _roots;
        if (index < 0 || index > list.Count) index = list.Count;
        list.Insert(index, node);
        node.Parent = parent;
        IndexSubtree(node);
        Touch(ChangeKind.Structure);
    }

    /// <summary>移除节点及其全部下级。</summary>
    public void Remove(GeoNode node)
    {
        RequireEdit();
        var list = node.Parent?.ChildList ?? _roots;
        list.Remove(node);
        foreach (var n in node.SelfAndDescendants())
        {
            _byId.Remove(n.Id);
            _selectionSet = null;
            _selection.Remove(n);
        }
        node.Parent = null;
        Touch(ChangeKind.Structure);
    }

    /// <summary>只移除节点本身，它的下级上移一级、占据它原来的位置。</summary>
    public void RemoveKeepChildren(GeoNode node)
    {
        RequireEdit();
        var parent = node.Parent;
        var list = parent?.ChildList ?? _roots;
        int index = list.IndexOf(node);
        list.RemoveAt(index);
        foreach (var c in node.ChildList)
        {
            c.Parent = parent;
            list.Insert(index++, c);
        }
        node.ChildList.Clear();
        _byId.Remove(node.Id);
        _selectionSet = null;
        _selection.Remove(node);
        node.Parent = null;
        Touch(ChangeKind.Structure);
    }

    public bool CanMove(GeoNode node, GeoNode? newParent)
        => newParent == null || (!ReferenceEquals(newParent, node) && !node.IsAncestorOf(newParent));

    public void Move(GeoNode node, GeoNode? newParent, int index = -1)
    {
        RequireEdit();
        if (!CanMove(node, newParent)) return;
        var oldList = node.Parent?.ChildList ?? _roots;
        var newList = newParent?.ChildList ?? _roots;
        int oldIndex = oldList.IndexOf(node);
        oldList.RemoveAt(oldIndex);
        if (ReferenceEquals(oldList, newList) && index > oldIndex) index--;
        if (index < 0 || index > newList.Count) index = newList.Count;
        newList.Insert(index, node);
        node.Parent = newParent;
        Touch(ChangeKind.Structure);
    }

    /// <summary>
    /// 按给定的上级重新组织整棵树（自动识别层级后应用）。没有列出的节点保持原来的上级；
    /// 同一上级下的顺序保持原来的先后。调用方保证不会形成循环。
    /// </summary>
    public void Restructure(IReadOnlyDictionary<GeoNode, GeoNode?> parents)
    {
        RequireEdit();
        var order = AllNodes().ToList();
        var newParent = new Dictionary<GeoNode, GeoNode?>(order.Count, ReferenceEqualityComparer.Instance);
        foreach (var n in order)
        {
            var p = parents.TryGetValue(n, out var chosen) ? chosen : n.Parent;
            if (p != null && !ReferenceEquals(Find(p.Id), p)) p = null;
            newParent[n] = p;
        }
        _roots.Clear();
        foreach (var n in order) n.ChildList.Clear();
        foreach (var n in order)
        {
            var p = newParent[n];
            n.Parent = p;
            (p?.ChildList ?? _roots).Add(n);
        }
        Touch(ChangeKind.Structure);
    }

    public void SetGeometry(GeoNode node, Geometry? geometry)
    {
        RequireEdit();
        node.Geometry = geometry;
        Touch(ChangeKind.Geometry);
    }

    /// <summary>在 Edit 内修改节点属性后调用，用于汇总变化类型。</summary>
    public void Touch(ChangeKind kind) => _pendingKind |= kind;

    private void IndexSubtree(GeoNode node)
    {
        foreach (var n in node.SelfAndDescendants())
        {
            if (_byId.TryGetValue(n.Id, out var existing) && !ReferenceEquals(existing, n))
            {
                n.Id = NewId();
            }
            _byId[n.Id] = n;
            foreach (var c in n.ChildList) c.Parent = n;
        }
    }

    private void RequireEdit()
    {
        if (_editDepth == 0)
        {
            throw new InvalidOperationException("文档修改必须放在 GeoDocument.Edit 里执行。");
        }
    }

    // ───────────────────────── 事务与撤销 ─────────────────────────

    /// <summary>
    /// 执行一次可撤销的修改。<paramref name="coalesceKey"/> 与上一次相同时合并成一步撤销
    /// （例如连续输入名称）。<paramref name="origin"/> 会随变化事件传出，发起方可据此忽略自己引起的刷新。
    /// </summary>
    public void Edit(string label, Action action, string? coalesceKey = null, object? origin = null)
    {
        if (_editDepth > 0)
        {
            action();
            return;
        }

        bool coalesce = coalesceKey != null && _redo.Count == 0 && _undo.Count > 0 && _undo[^1].CoalesceKey == coalesceKey;
        var before = coalesce ? null : Capture();

        _editDepth++;
        _pendingKind = ChangeKind.None;
        _pendingOrigin = origin;
        try
        {
            action();
        }
        finally
        {
            _editDepth--;
        }

        if (_pendingKind == ChangeKind.None) return;

        if (coalesce)
        {
            _undo[^1] = _undo[^1] with { Serial = ++_serial };
        }
        else
        {
            _undo.Add(new UndoEntry(before!, label, coalesceKey, ++_serial));
            TrimUndo();
        }
        _redo.Clear();

        var kind = _pendingKind;
        PruneSelection();
        Changed?.Invoke(new DocumentChange(kind, _pendingOrigin));
        HistoryChanged?.Invoke();
    }

    /// <summary>结束连续编辑的合并（例如输入框失去焦点）。</summary>
    public void BreakCoalescing()
    {
        if (_undo.Count > 0 && _undo[^1].CoalesceKey != null)
        {
            _undo[^1] = _undo[^1] with { CoalesceKey = null };
        }
    }

    private void TrimUndo()
    {
        int limit = Math.Clamp(MaxUndoNodeStates / Math.Max(1, _byId.Count), 20, MaxUndo);
        if (_undo.Count > limit) _undo.RemoveRange(0, _undo.Count - limit);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        var current = Capture();
        _redo.Add(entry with { State = current });
        Restore(entry.State);
        Changed?.Invoke(new DocumentChange(ChangeKind.Reset, null));
        SelectionChanged?.Invoke();
        HistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        var current = Capture();
        _undo.Add(entry with { State = current });
        Restore(entry.State);
        Changed?.Invoke(new DocumentChange(ChangeKind.Reset, null));
        SelectionChanged?.Invoke();
        HistoryChanged?.Invoke();
    }

    public void MarkSaved()
    {
        _savedState = CurrentState;
        HistoryChanged?.Invoke();
    }

    /// <summary>用一棵新树替换整个文档，清空撤销历史。</summary>
    public void Load(IEnumerable<GeoNode> roots, string? filePath, bool writesHierarchy = true)
    {
        WritesHierarchy = writesHierarchy;
        PlainSaveConfirmed = false;
        _roots.Clear();
        _byId.Clear();
        _selectionSet = null;
        _selection.Clear();
        _undo.Clear();
        _redo.Clear();
        _savedState = 0;
        _idCounter = 0;
        InstanceId = Guid.NewGuid().ToString("N");
        foreach (var r in roots)
        {
            r.Parent = null;
            _roots.Add(r);
            IndexSubtree(r);
        }
        FilePath = filePath;
        Changed?.Invoke(new DocumentChange(ChangeKind.Reset, null));
        SelectionChanged?.Invoke();
        HistoryChanged?.Invoke();
    }

    private void PruneSelection()
    {
        int before = _selection.Count;
        _selectionSet = null;
        _selection.RemoveAll(n => !_byId.TryGetValue(n.Id, out var live) || !ReferenceEquals(live, n));
        if (_selection.Count != before) SelectionChanged?.Invoke();
    }

    // ───────────────────────── 快照 ─────────────────────────

    private sealed record NodeState(
        string Id,
        string? ParentId,
        string Name,
        string Level,
        string Note,
        string? Color,
        MarkerIcon Icon,
        bool Visible,
        Geometry? Geometry,
        IReadOnlyDictionary<string, JsonNode?> Extra,
        SourceInfo? Source);

    private sealed record Snapshot(List<NodeState> Nodes, List<string> SelectedIds);

    private sealed record UndoEntry(Snapshot State, string Label, string? CoalesceKey, long Serial);

    private Snapshot Capture()
    {
        var nodes = new List<NodeState>(_byId.Count);
        foreach (var n in AllNodes())
        {
            // 没有变化的节点复用上一次快照里的状态对象，每一步撤销只多占一个引用
            string? parentId = n.Parent?.Id;
            if (n.UndoState is NodeState s
                && s.Id == n.Id && s.ParentId == parentId && s.Name == n.Name && s.Level == n.Level && s.Note == n.Note
                && s.Color == n.Color && s.Icon == n.Icon && s.Visible == n.Visible
                && ReferenceEquals(s.Geometry, n.Geometry) && ReferenceEquals(s.Extra, n.Extra) && ReferenceEquals(s.Source, n.Source))
            {
                nodes.Add(s);
                continue;
            }
            s = new NodeState(n.Id, parentId, n.Name, n.Level, n.Note, n.Color, n.Icon, n.Visible, n.Geometry, n.Extra, n.Source);
            n.UndoState = s;
            nodes.Add(s);
        }
        return new Snapshot(nodes, _selection.Select(x => x.Id).ToList());
    }

    /// <summary>就地恢复：同 id 的节点对象保持不变，界面里对节点的引用不会失效。</summary>
    private void Restore(Snapshot snapshot)
    {
        var old = new Dictionary<string, GeoNode>(_byId, StringComparer.Ordinal);
        _roots.Clear();
        _byId.Clear();
        foreach (var n in old.Values) n.ChildList.Clear();

        foreach (var s in snapshot.Nodes)
        {
            if (!old.TryGetValue(s.Id, out var node)) node = new GeoNode(s.Id);
            node.Name = s.Name;
            node.Level = s.Level;
            node.Note = s.Note;
            node.Color = s.Color;
            node.Icon = s.Icon;
            node.Visible = s.Visible;
            node.Geometry = s.Geometry;
            node.Extra = s.Extra;
            node.Source = s.Source;
            node.UndoState = s;
            var parent = s.ParentId != null && _byId.TryGetValue(s.ParentId, out var p) ? p : null;
            node.Parent = parent;
            (parent?.ChildList ?? _roots).Add(node);
            _byId[node.Id] = node;
        }

        _selectionSet = null;
        _selection.Clear();
        foreach (var id in snapshot.SelectedIds)
        {
            if (_byId.TryGetValue(id, out var n)) _selection.Add(n);
        }
    }
}
