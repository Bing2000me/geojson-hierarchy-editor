using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using GeoJsonEditor.Geo;
using GeoJsonEditor.Model;

using NetTopologySuite.Geometries;

using SkiaSharp;

namespace GeoJsonEditor.Map;

/// <summary>
/// 按层级缩放显示里的一个区域：自身是面、或者下级里有面的可见节点。
/// 没有自身边界的区域（分组）用下级拼出的边界（见 <see cref="DerivedShapes"/>）。
/// </summary>
public sealed class Region
{
    internal Region(GeoNode node, Region? parent)
    {
        Node = node;
        Parent = parent;
        Depth = parent == null ? 0 : parent.Depth + 1;
    }

    public GeoNode Node { get; }

    /// <summary>最近的区域上级（中间隔着的点、线、没有面的分组不算）。</summary>
    public Region? Parent { get; }

    /// <summary>在区域树里的深度：最上层的区域为 0。</summary>
    public int Depth { get; }

    /// <summary>下级区域，按文档顺序。</summary>
    public List<Region> Children { get; } = new();

    /// <summary>挂在这个区域下的线（最近的区域上级是这里），区域展开后才显示。</summary>
    public List<GeoNode> Lines { get; } = new();

    /// <summary>挂在这个区域下的点数。</summary>
    public int PointCount { get; internal set; }

    /// <summary>自身是面，用自己的边界。</summary>
    public bool HasOwnShape { get; internal set; }

    /// <summary>由下级拼出的边界（没有自身边界时；后台算好之前为 null）。</summary>
    public ProjectedShape? Derived { get; internal set; }

    /// <summary>自动边界还在计算。</summary>
    public bool DerivedPending { get; internal set; }

    /// <summary>自身和全部下级的面的范围（世界坐标）。</summary>
    public double MinX, MinY, MaxX, MaxY;

    /// <summary>面积（世界坐标单位的平方）。</summary>
    public double Area { get; internal set; }

    /// <summary>
    /// 展开尺度（世界坐标单位）：下级区域的典型边长；没有下级区域、只有点和线时取自身边长的一部分。
    /// 它在屏幕上达到展开阈值时区域展开，显示下级。没有可展开的内容时为 0。
    /// </summary>
    public double DetailScale { get; internal set; }

    /// <summary>内容签名：自身几何、下级区域的签名都算在里面。自动边界按它缓存。</summary>
    public long Signature { get; internal set; }

    public SKColor Color { get; internal set; }

    public bool Intersects(double minX, double minY, double maxX, double maxY)
        => MaxX >= minX && MinX <= maxX && MaxY >= minY && MinY <= maxY;

    public IEnumerable<Region> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
        {
            foreach (var d in c.SelfAndDescendants()) yield return d;
        }
    }

    public override string ToString() => Node.DisplayName;
}

/// <summary>
/// 按层级缩放显示用的区域树，做法参照 P 社游戏的地图：几何只在下级存一份，上级的范围由下级拼成；
/// 缩小时整块显示上级（只有上级的边界和名称），放大到下级在屏幕上足够大时逐级展开。
/// 文档每改动一次重建一次（UI 线程，线性时间），自动边界按内容签名缓存，没变的不重算。
/// </summary>
public sealed class RegionIndex
{
    /// <summary>没有下级区域、只有点和线的区域：自身边长是展开阈值的这么多倍时展开（显示其中的线和完整的点标记）。</summary>
    public const double LeafExpandRatio = 5.5;

    public static readonly RegionIndex Empty = new();

    private readonly Dictionary<GeoNode, Region> _byNode = new(ReferenceEqualityComparer.Instance);

    /// <summary>最上层的区域。</summary>
    public List<Region> Roots { get; } = new();

    /// <summary>不属于任何区域的线（总是显示）。</summary>
    public List<GeoNode> RootLines { get; } = new();

    /// <summary>需要计算自动边界的区域，由深到浅（下级在前）。</summary>
    public List<Region> Pending { get; } = new();

    public int Count => _byNode.Count;

    public Region? Find(GeoNode node) => _byNode.GetValueOrDefault(node);

    /// <summary>节点所在的区域：自身是区域就是自己，否则是最近的区域上级；都没有时为 null。</summary>
    public Region? RegionOf(GeoNode node)
    {
        for (GeoNode? n = node; n != null; n = n.Parent)
        {
            if (_byNode.TryGetValue(n, out var r)) return r;
        }
        return null;
    }

    public IEnumerable<Region> All()
    {
        foreach (var r in Roots)
        {
            foreach (var d in r.SelfAndDescendants()) yield return d;
        }
    }

    /// <param name="colorOf">节点的显示颜色（与图层面板一致）。</param>
    public static RegionIndex Build(GeoDocument doc, ShapeCache shapes, MapViewport vp, DerivedShapes derived, Func<GeoNode, SKColor> colorOf)
    {
        var index = new RegionIndex();

        // 第一遍：哪些可见节点的子树里有可见的面
        var areal = new HashSet<GeoNode>(ReferenceEqualityComparer.Instance);
        bool Mark(GeoNode n)
        {
            if (!n.Visible) return false;
            bool any = n.Kind == NodeKind.Polygon && n.Geometry is { IsEmpty: false };
            foreach (var c in n.Children)
            {
                if (Mark(c)) any = true;
            }
            if (any) areal.Add(n);
            return any;
        }
        foreach (var r in doc.Roots) Mark(r);

        // 第二遍：建区域树，线和点挂到最近的区域上
        void Visit(GeoNode n, Region? parent)
        {
            if (!n.Visible) return;
            var here = parent;
            if (areal.Contains(n))
            {
                here = new Region(n, parent);
                (parent?.Children ?? index.Roots).Add(here);
                index._byNode[n] = here;
                here.HasOwnShape = n.Kind == NodeKind.Polygon && shapes.Get(n, vp) != null;
            }
            switch (n.Kind)
            {
                case NodeKind.Line:
                    if (parent != null) parent.Lines.Add(n);
                    else index.RootLines.Add(n);
                    break;
                case NodeKind.Point when parent != null:
                    parent.PointCount++;
                    break;
            }
            foreach (var c in n.Children) Visit(c, here);
        }
        foreach (var r in doc.Roots) Visit(r, null);

        // 第三遍（后序）：范围、面积、签名、自动边界、展开尺度
        void Finish(Region r)
        {
            foreach (var c in r.Children) Finish(c);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            ulong sig = Mix(0x9E3779B97F4A7C15UL, (ulong)RuntimeHelpers.GetHashCode(r.Node));
            double area = 0;
            if (r.HasOwnShape && shapes.Get(r.Node, vp) is { } own)
            {
                (minX, minY, maxX, maxY) = (own.MinX, own.MinY, own.MaxX, own.MaxY);
                area = own.Area;
                sig = Mix(sig, (ulong)r.Node.GeometryVersion);
            }
            double childArea = 0;
            foreach (var c in r.Children)
            {
                minX = Math.Min(minX, c.MinX);
                minY = Math.Min(minY, c.MinY);
                maxX = Math.Max(maxX, c.MaxX);
                maxY = Math.Max(maxY, c.MaxY);
                childArea += c.Area;
                sig = Mix(sig, (ulong)c.Signature);
            }
            (r.MinX, r.MinY, r.MaxX, r.MaxY) = (minX, minY, maxX, maxY);
            r.Signature = (long)sig;
            if (!r.HasOwnShape)
            {
                r.Derived = derived.Get(r.Node, r.Signature, vp);
                if (r.Derived == null && r.Children.Count > 0 && !derived.HasFailed(r.Node, r.Signature))
                {
                    r.DerivedPending = true;
                    index.Pending.Add(r);
                }
                area = r.Derived?.Area ?? childArea;
            }
            r.Area = area;

            if (r.Children.Count > 0)
            {
                double mean = childArea / r.Children.Count;
                if (!(mean > 0)) mean = (maxX - minX) * (maxY - minY) / r.Children.Count;
                r.DetailScale = Math.Sqrt(Math.Max(mean, 0));
            }
            else if (r.Lines.Count > 0 || r.PointCount > 0)
            {
                r.DetailScale = Math.Sqrt(area) / LeafExpandRatio;
            }
        }
        foreach (var r in index.Roots) Finish(r);

        // 第四遍：颜色（要先有上级的颜色）
        foreach (var r in index.All()) r.Color = colorOf(r.Node);

        derived.Prune(index._byNode.Keys);
        return index;
    }

    private static ulong Mix(ulong h, ulong v)
    {
        unchecked
        {
            h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
            h *= 0xBF58476D1CE4E5B9UL;
            return h ^ (h >> 31);
        }
    }
}

/// <summary>
/// 自动边界（由下级拼出的上级范围）的缓存和后台计算。按区域的内容签名缓存：下级没变就一直沿用，
/// 坐标系变了只重新投影。计算由深到浅进行（上级要用到下级刚拼好的边界），同一层里并行。
/// </summary>
public sealed class DerivedShapes
{
    private sealed class Entry(long signature, Geometry geometry, ProjectedShape shape)
    {
        public long Signature { get; } = signature;
        public Geometry Geometry { get; } = geometry;
        public ProjectedShape Shape { get; set; } = shape;
    }

    private readonly Dictionary<GeoNode, Entry> _entries = new(ReferenceEqualityComparer.Instance);

    // 算过但拼不出结果的（下级都是退化的面）：同样的内容不再反复计算
    private readonly Dictionary<GeoNode, long> _failed = new(ReferenceEqualityComparer.Instance);

    public int Count => _entries.Count;

    /// <summary>这个内容已经算过、拼不出边界。</summary>
    public bool HasFailed(GeoNode node, long signature) => _failed.TryGetValue(node, out var s) && s == signature;

    /// <summary>已经算好的投影；签名不符（下级变了）时返回 null。坐标系变了时在这里重新投影。</summary>
    public ProjectedShape? Get(GeoNode node, long signature, MapViewport vp)
    {
        if (!_entries.TryGetValue(node, out var e) || e.Signature != signature) return null;
        if (e.Shape.DataCrs != vp.DataCrs || e.Shape.DisplayCrs != vp.DisplayCrs)
        {
            e.Shape = ShapeCache.Build(e.Geometry, 0, vp.DataCrs, vp.DisplayCrs);
        }
        return e.Shape;
    }

    /// <summary>已经算好的自动边界（数据坐标）；签名不符时返回 null。</summary>
    public Geometry? GeometryOf(GeoNode node, long signature)
        => _entries.TryGetValue(node, out var e) && e.Signature == signature ? e.Geometry : null;

    /// <summary>去掉已经不在区域树里的缓存（被删除、隐藏或者有了自身边界的节点）。</summary>
    public void Prune(IEnumerable<GeoNode> alive)
    {
        if (_entries.Count + _failed.Count == 0) return;
        var keep = alive as ICollection<GeoNode> ?? alive.ToList();
        if (_entries.Count + _failed.Count <= keep.Count / 2 + 16) return;
        var set = new HashSet<GeoNode>(keep, ReferenceEqualityComparer.Instance);
        foreach (var n in _entries.Keys.ToList())
        {
            if (!set.Contains(n)) _entries.Remove(n);
        }
        foreach (var n in _failed.Keys.ToList())
        {
            if (!set.Contains(n)) _failed.Remove(n);
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _failed.Clear();
    }

    /// <summary>
    /// 一个待计算的区域。输入是各个下级的几何：自身的几何、已经算好的自动边界，
    /// 或者同一批里另一个区域（<see cref="Input.Pending"/>）的结果。
    /// </summary>
    public sealed record Job(GeoNode Node, long Signature, int Depth, IReadOnlyList<Input> Inputs);

    public readonly record struct Input(Geometry? Geometry, GeoNode? Pending, long PendingSignature);

    public readonly record struct Result(GeoNode Node, long Signature, Geometry Geometry, ProjectedShape Shape);

    /// <summary>在 UI 线程上为待算的区域取出输入（几何按不可变值共享，可以交给后台）。</summary>
    public List<Job> MakeJobs(IReadOnlyList<Region> pending)
    {
        var jobs = new List<Job>(pending.Count);
        var inBatch = new HashSet<GeoNode>(pending.Select(r => r.Node), ReferenceEqualityComparer.Instance);
        foreach (var r in pending)
        {
            var inputs = new List<Input>(r.Children.Count);
            foreach (var c in r.Children)
            {
                if (c.HasOwnShape)
                {
                    inputs.Add(new Input(c.Node.Geometry, null, 0));
                }
                else if (GeometryOf(c.Node, c.Signature) is { } g)
                {
                    inputs.Add(new Input(g, null, 0));
                }
                else if (inBatch.Contains(c.Node))
                {
                    inputs.Add(new Input(null, c.Node, c.Signature));
                }
            }
            jobs.Add(new Job(r.Node, r.Signature, r.Depth, inputs));
        }
        return jobs;
    }

    /// <summary>计算一批自动边界（后台线程，不访问缓存和文档）。</summary>
    public static List<Result> Compute(IReadOnlyList<Job> jobs, CoordSystem data, CoordSystem display, CancellationToken token = default)
    {
        var done = new ConcurrentDictionary<(GeoNode, long), Geometry>();
        var results = new ConcurrentBag<Result>();
        foreach (var level in jobs.GroupBy(j => j.Depth).OrderByDescending(g => g.Key))
        {
            token.ThrowIfCancellationRequested();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Parallel.ForEach(level, new ParallelOptions { CancellationToken = token }, job =>
            {
                var parts = new List<Geometry>(job.Inputs.Count);
                foreach (var input in job.Inputs)
                {
                    if (input.Geometry != null) parts.Add(input.Geometry);
                    else if (input.Pending != null && done.TryGetValue((input.Pending, input.PendingSignature), out var g)) parts.Add(g);
                }
                Geometry? merged;
                try
                {
                    merged = GeometryOps.Dissolve(parts);
                }
                catch (Exception)
                {
                    merged = Geometries.ToPolygonal(parts.SelectMany(Geometries.Polygons));
                }
                if (merged == null || merged.IsEmpty) return;
                done[(job.Node, job.Signature)] = merged;
                var shape = ShapeCache.Build(merged, 0, data, display);
                _ = shape.Area;
                results.Add(new Result(job.Node, job.Signature, merged, shape));
            });
            if (Environment.GetEnvironmentVariable("GEOJSON_EDITOR_TRACE") == "1") Console.WriteLine($"  自动边界 深度 {level.Key}：{level.Count()} 个，{watch.ElapsedMilliseconds} ms");
        }
        return results.ToList();
    }

    /// <summary>接收后台算好的结果（UI 线程）。这一批里没有结果的记为拼不出，同样的内容不再重算。</summary>
    public void Adopt(IReadOnlyList<Job> jobs, IEnumerable<Result> results)
    {
        var done = new HashSet<GeoNode>(ReferenceEqualityComparer.Instance);
        foreach (var r in results)
        {
            _entries[r.Node] = new Entry(r.Signature, r.Geometry, r.Shape);
            _failed.Remove(r.Node);
            done.Add(r.Node);
        }
        foreach (var j in jobs)
        {
            if (!done.Contains(j.Node)) _failed[j.Node] = j.Signature;
        }
    }
}
