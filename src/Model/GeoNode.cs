using System.Text.Json.Nodes;

using NetTopologySuite.Geometries;

namespace GeoJsonEditor.Model;

/// <summary>节点类别由几何类型决定：没有几何的是分组。</summary>
public enum NodeKind
{
    Group,
    Polygon,
    Line,
    Point,
}

/// <summary>点标记的图标形状。</summary>
public enum MarkerIcon
{
    Pin,
    Circle,
    Star,
    Square,
    Triangle,
    Flag,
}

/// <summary>
/// 层级树中的一个节点，对应 GeoJSON 里的一个 Feature。
/// 几何对象按不可变值使用：任何编辑都替换成新的 <see cref="Geometry"/> 实例，
/// 这样撤销快照可以直接共享引用。
/// </summary>
public sealed class GeoNode
{
    private Geometry? _geometry;

    public GeoNode(string id)
    {
        Id = id;
    }

    public string Id { get; internal set; }

    public GeoNode? Parent { get; internal set; }

    /// <summary>下级节点。只能通过 <see cref="GeoDocument"/> 的结构操作修改。</summary>
    public IReadOnlyList<GeoNode> Children => ChildList;

    internal List<GeoNode> ChildList { get; } = new();

    public string Name { get; set; } = "";

    /// <summary>级别文字，例如 省 / 市 / 区县。可为空。</summary>
    public string Level { get; set; } = "";

    public string Note { get; set; } = "";

    /// <summary>显式颜色（#RRGGBB）。为 null 时按调色板自动取色。</summary>
    public string? Color { get; set; }

    public MarkerIcon Icon { get; set; } = MarkerIcon.Pin;

    public bool Visible { get; set; } = true;

    /// <summary>读入时的其他属性，原样保留并写回。整体替换，不原地修改。</summary>
    public IReadOnlyDictionary<string, JsonNode?> Extra { get; set; } = EmptyExtra;

    public static readonly IReadOnlyDictionary<string, JsonNode?> EmptyExtra = new Dictionary<string, JsonNode?>();

    public Geometry? Geometry
    {
        get => _geometry;
        set
        {
            if (ReferenceEquals(_geometry, value)) return;
            _geometry = value;
            GeometryVersion++;
        }
    }

    /// <summary>几何每替换一次加一，渲染缓存据此失效。</summary>
    public int GeometryVersion { get; private set; }

    /// <summary>上一次撤销快照里这个节点的状态。没有变化时下一次快照直接复用，不再新建。</summary>
    internal object? UndoState { get; set; }

    public NodeKind Kind => KindOf(_geometry);

    public static NodeKind KindOf(Geometry? g) => g switch
    {
        null => NodeKind.Group,
        Polygon or MultiPolygon => NodeKind.Polygon,
        LineString or MultiLineString => NodeKind.Line,
        Point or MultiPoint => NodeKind.Point,
        GeometryCollection gc => gc.NumGeometries > 0 ? KindOf(gc.GetGeometryN(0)) : NodeKind.Group,
        _ => NodeKind.Group,
    };

    public int Depth
    {
        get
        {
            int d = 0;
            for (var p = Parent; p != null; p = p.Parent) d++;
            return d;
        }
    }

    /// <summary>自身和所有上级都可见时才算可见。</summary>
    public bool IsEffectivelyVisible
    {
        get
        {
            for (var n = this; n != null; n = n.Parent)
            {
                if (!n.Visible) return false;
            }
            return true;
        }
    }

    public IEnumerable<GeoNode> Ancestors()
    {
        for (var p = Parent; p != null; p = p.Parent) yield return p;
    }

    public bool IsAncestorOf(GeoNode other)
    {
        for (var p = other.Parent; p != null; p = p.Parent)
        {
            if (ReferenceEquals(p, this)) return true;
        }
        return false;
    }

    /// <summary>自身加全部下级，先序遍历。</summary>
    public IEnumerable<GeoNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
        {
            foreach (var d in c.SelfAndDescendants()) yield return d;
        }
    }

    public IEnumerable<GeoNode> Descendants()
    {
        foreach (var c in Children)
        {
            foreach (var d in c.SelfAndDescendants()) yield return d;
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "（未命名）" : Name;

    public override string ToString() => DisplayName;
}
