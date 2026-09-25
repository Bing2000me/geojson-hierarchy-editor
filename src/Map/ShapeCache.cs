using GeoJsonEditor.Geo;
using GeoJsonEditor.Model;

using NetTopologySuite.Algorithm.Construct;
using NetTopologySuite.Geometries;

namespace GeoJsonEditor.Map;

/// <summary>
/// 节点几何投影到世界坐标后的缓存，几何或坐标系变化时重建。
/// 同时保存分级简化用的顶点显著度；各级的简化结果、分块包围盒和标注位置在第一次用到时才算。
/// 构建可以在后台线程进行（见 <see cref="ShapeCache.BuildMany"/>），交给界面之后只在 UI 线程上访问。
/// </summary>
public sealed class ProjectedShape
{
    private static readonly GeometryFactory LocalFactory = new();

    public int Version;
    public CoordSystem DataCrs;
    public CoordSystem DisplayCrs;
    public NodeKind Kind;

    /// <summary>面：所有环（外环和内环，偶奇填充）；线：每条线。世界坐标 x0,y0,x1,y1…</summary>
    public double[][] Paths = [];

    /// <summary>与 <see cref="Paths"/> 一一对应的原始数据坐标，用于吸附时拿到精确的顶点。</summary>
    public Coordinate[][] DataPaths = [];

    /// <summary>面：每个面（外环 + 内环）占 <see cref="Paths"/> 里的几条路径。</summary>
    public int[] RingCounts = [];

    /// <summary>点要素的世界坐标。</summary>
    public double[] Points = [];

    public double MinX, MinY, MaxX, MaxY;

    public int VertexCount;

    private float[]?[] _significance = [];
    private double[]?[]?[]? _levels;
    private double[][]? _chunks;
    private bool _labelReady;
    private double _labelX, _labelY, _labelRadius;
    private double _labelAngle, _labelElongation = 1;
    private double _area = -1;

    public bool Intersects(double minX, double minY, double maxX, double maxY)
        => MaxX >= minX && MinX <= maxX && MaxY >= minY && MinY <= maxY;

    public double Extent => Math.Max(MaxX - MinX, MaxY - MinY);

    /// <summary>面积（世界坐标单位的平方）：各个面的外环减内环；线和点为 0。</summary>
    public double Area
    {
        get
        {
            if (_area >= 0) return _area;
            double a = 0;
            if (Kind == NodeKind.Polygon)
            {
                for (int p = 0, k = 0; p < RingCounts.Length; k += RingCounts[p], p++)
                {
                    a += Math.Abs(SignedArea(Paths[k]));
                    for (int h = 1; h < RingCounts[p]; h++) a -= Math.Abs(SignedArea(Paths[k + h]));
                }
            }
            return _area = Math.Max(a, 0);
        }
    }

    /// <summary>
    /// 最大一块的主轴方向（弧度，0 为水平，向下为正）和长短轴之比。区域名称据此横排、竖排或沿斜向排开。
    /// 与 <see cref="Label"/> 一起计算。
    /// </summary>
    public (double Angle, double Elongation) LabelAxis
    {
        get
        {
            _ = Label;
            return (_labelAngle, _labelElongation);
        }
    }

    /// <summary>
    /// 某一级简化后的路径，与 <see cref="Paths"/> 一一对应；面积小于容差的环为 null。
    /// 顶点少的路径直接用原数组。
    /// </summary>
    public double[]?[] PathsAt(int level)
    {
        level = Math.Clamp(level, 0, Lod.MaxLevel);
        _levels ??= new double[]?[]?[Lod.MaxLevel + 1];
        var cached = _levels[level];
        if (cached != null) return cached;

        bool closed = Kind == NodeKind.Polygon;
        double tol = Lod.Tolerance(level);
        var result = new double[]?[Paths.Length];
        for (int i = 0; i < Paths.Length; i++)
        {
            var sig = i < _significance.Length ? _significance[i] : null;
            result[i] = sig == null ? Paths[i] : Lod.Filter(Paths[i], sig, tol, closed);
        }
        _levels[level] = result;
        return result;
    }

    public double[]?[] PathsForZoom(double zoom) => PathsAt(Lod.LevelForZoom(zoom));

    /// <summary>每条路径的分块包围盒（见 <see cref="PathChunks"/>）。</summary>
    public double[][] Chunks
    {
        get
        {
            if (_chunks != null) return _chunks;
            var chunks = new double[Paths.Length][];
            for (int i = 0; i < Paths.Length; i++) chunks[i] = PathChunks.Build(Paths[i]);
            return _chunks = chunks;
        }
    }

    /// <summary>标注位置与半径（世界坐标）：面取最大一块的最大内切圆，线取最长一段的中点，点取第一个点。</summary>
    public (double X, double Y, double Radius) Label
    {
        get
        {
            if (!_labelReady)
            {
                _labelReady = true;
                if (Kind == NodeKind.Polygon) ComputePolygonLabel();
            }
            return (_labelX, _labelY, _labelRadius);
        }
    }

    /// <summary>标注半径的上限（包围盒短边的一半），不用算内切圆就能先排除太小的面。</summary>
    public double LabelRadiusBound => Math.Min(MaxX - MinX, MaxY - MinY) / 2;

    public bool HasLabel => _labelReady;

    internal void SetLabel(double x, double y, double radius)
    {
        _labelX = x;
        _labelY = y;
        _labelRadius = radius;
        _labelReady = true;
    }

    internal void ComputeSignificance()
    {
        bool closed = Kind == NodeKind.Polygon;
        _significance = new float[]?[Paths.Length];
        for (int i = 0; i < Paths.Length; i++)
        {
            if (Paths[i].Length / 2 > Lod.MinVertices) _significance[i] = Lod.Significance(Paths[i], closed);
        }
    }

    /// <summary>
    /// 在显示约 400 DIP 大小的那一级简化结果上求最大内切圆：顶点少、算得快，位置误差远小于一个字。
    /// 坐标先平移缩放到 [0,1]，避免世界坐标太小带来的数值问题。
    /// </summary>
    private void ComputePolygonLabel()
    {
        _labelX = (MinX + MaxX) / 2;
        _labelY = (MinY + MaxY) / 2;
        _labelRadius = 0;
        double extent = Extent;
        if (!(extent > 0) || RingCounts.Length == 0) return;

        int level = Math.Clamp((int)Math.Floor(Math.Log2(400 / (MapViewport.TileSize * extent))), 0, Lod.MaxLevel);
        var paths = PathsAt(level);
        int best = -1, bestStart = 0;
        double bestArea = 0;
        for (int p = 0, k = 0; p < RingCounts.Length; k += RingCounts[p], p++)
        {
            var shell = paths[k] ?? Paths[k];
            double a = Math.Abs(SignedArea(shell));
            if (a > bestArea)
            {
                bestArea = a;
                best = p;
                bestStart = k;
            }
        }
        if (best < 0) return;
        ComputeAxis(paths[bestStart] ?? Paths[bestStart]);

        try
        {
            var shellRing = ToLocalRing(paths[bestStart] ?? Paths[bestStart], extent);
            if (shellRing == null) return;
            var holes = new List<LinearRing>();
            for (int h = 1; h < RingCounts[best]; h++)
            {
                var hole = paths[bestStart + h];
                if (hole != null && ToLocalRing(hole, extent) is { } hr) holes.Add(hr);
            }
            var poly = LocalFactory.CreatePolygon(shellRing, holes.ToArray());
            try
            {
                double tol = Math.Max(Math.Sqrt(Math.Abs(poly.Area)) / 60, 1e-6);
                var mic = new MaximumInscribedCircle(poly, tol);
                var c = mic.GetCenter();
                var r = mic.GetRadiusPoint();
                _labelX = MinX + c.X * extent;
                _labelY = MinY + c.Y * extent;
                _labelRadius = Math.Sqrt((c.X - r.X) * (c.X - r.X) + (c.Y - r.Y) * (c.Y - r.Y)) * extent;
            }
            catch (Exception)
            {
                var ip = poly.InteriorPoint;
                _labelX = MinX + ip.X * extent;
                _labelY = MinY + ip.Y * extent;
                _labelRadius = LabelRadiusBound / 2;
            }
        }
        catch (Exception)
        {
            // 退化的环：用包围盒中心
        }
    }

    /// <summary>用环的二阶面积矩求主轴方向和长短轴之比（坐标先减去包围盒中心，避免数值问题）。</summary>
    private void ComputeAxis(double[] xy)
    {
        int n = xy.Length / 2;
        if (n < 4) return;
        double ox = (MinX + MaxX) / 2, oy = (MinY + MaxY) / 2;
        double a = 0, cx = 0, cy = 0, sxx = 0, syy = 0, sxy = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double x0 = xy[j * 2] - ox, y0 = xy[j * 2 + 1] - oy, x1 = xy[i * 2] - ox, y1 = xy[i * 2 + 1] - oy;
            double cross = x0 * y1 - x1 * y0;
            a += cross;
            cx += (x0 + x1) * cross;
            cy += (y0 + y1) * cross;
            sxx += (x0 * x0 + x0 * x1 + x1 * x1) * cross;
            syy += (y0 * y0 + y0 * y1 + y1 * y1) * cross;
            sxy += (x0 * y1 + 2 * x0 * y0 + 2 * x1 * y1 + x1 * y0) * cross;
        }
        a /= 2;
        if (Math.Abs(a) < 1e-30) return;
        cx /= 6 * a;
        cy /= 6 * a;
        // 相对形心的协方差（除以面积）
        double ixx = sxx / 12 / a - cx * cx;
        double iyy = syy / 12 / a - cy * cy;
        double ixy = sxy / 24 / a - cx * cy;
        double tr = ixx + iyy, det = ixx * iyy - ixy * ixy;
        double disc = Math.Sqrt(Math.Max(tr * tr / 4 - det, 0));
        double l1 = tr / 2 + disc, l2 = Math.Max(tr / 2 - disc, 1e-30);
        _labelAngle = 0.5 * Math.Atan2(2 * ixy, ixx - iyy);
        _labelElongation = Math.Sqrt(Math.Max(l1, 0) / l2);
    }

    private LinearRing? ToLocalRing(double[] xy, double extent)
    {
        int n = xy.Length / 2;
        if (n < 4) return null;
        var coords = new Coordinate[n];
        for (int i = 0; i < n; i++) coords[i] = new Coordinate((xy[i * 2] - MinX) / extent, (xy[i * 2 + 1] - MinY) / extent);
        if (!coords[0].Equals2D(coords[^1])) coords[^1] = coords[0].Copy();
        return LocalFactory.CreateLinearRing(coords);
    }

    public static double SignedArea(double[] xy)
    {
        int n = xy.Length / 2;
        double sum = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            sum += (xy[j * 2] - xy[i * 2]) * (xy[j * 2 + 1] + xy[i * 2 + 1]);
        }
        return sum / 2;
    }
}

public sealed class ShapeCache
{
    private readonly Dictionary<GeoNode, ProjectedShape> _shapes = new(ReferenceEqualityComparer.Instance);

    public int Count => _shapes.Count;

    public ProjectedShape? Get(GeoNode node, MapViewport vp)
    {
        var g = node.Geometry;
        if (g == null || g.IsEmpty) return null;
        if (_shapes.TryGetValue(node, out var s) && IsCurrent(s, node, vp.DataCrs, vp.DisplayCrs)) return s;
        s = Build(g, node.GeometryVersion, vp.DataCrs, vp.DisplayCrs);
        _shapes[node] = s;
        return s;
    }

    private static bool IsCurrent(ProjectedShape s, GeoNode node, CoordSystem data, CoordSystem display)
        => s.Version == node.GeometryVersion && s.DataCrs == data && s.DisplayCrs == display;

    /// <summary>并行构建一批节点的投影（可在后台线程调用，不访问缓存本身）。</summary>
    public static List<(GeoNode Node, ProjectedShape Shape)> BuildMany(IEnumerable<GeoNode> nodes, CoordSystem data, CoordSystem display)
    {
        var list = nodes.Where(n => n.Geometry is { IsEmpty: false }).ToList();
        var result = new (GeoNode, ProjectedShape)[list.Count];
        Parallel.For(0, list.Count, i =>
        {
            var n = list[i];
            result[i] = (n, Build(n.Geometry!, n.GeometryVersion, data, display));
        });
        return result.ToList();
    }

    /// <summary>接收后台构建好的投影（UI 线程）。版本已经过时的丢弃。</summary>
    public void Adopt(IEnumerable<(GeoNode Node, ProjectedShape Shape)> items)
    {
        foreach (var (node, shape) in items)
        {
            if (shape.Version == node.GeometryVersion) _shapes[node] = shape;
        }
    }

    /// <summary>把一批节点里过时或缺少的投影一次性并行重建（例如切换坐标系之后）。</summary>
    public void WarmUp(IEnumerable<GeoNode> nodes, MapViewport vp)
    {
        var stale = new List<GeoNode>();
        foreach (var n in nodes)
        {
            if (n.Geometry is not { IsEmpty: false }) continue;
            if (!_shapes.TryGetValue(n, out var s) || !IsCurrent(s, n, vp.DataCrs, vp.DisplayCrs)) stale.Add(n);
        }
        if (stale.Count < 8)
        {
            foreach (var n in stale) Get(n, vp);
            return;
        }
        Adopt(BuildMany(stale, vp.DataCrs, vp.DisplayCrs));
    }

    /// <summary>去掉已不在文档中的节点。</summary>
    public void Prune(GeoDocument doc)
    {
        if (_shapes.Count <= doc.Count + 64) return;
        foreach (var n in _shapes.Keys.ToList())
        {
            if (!ReferenceEquals(doc.Find(n.Id), n)) _shapes.Remove(n);
        }
    }

    public void Clear() => _shapes.Clear();

    public static (double X, double Y) DataToWorld(double lon, double lat, CoordSystem data, CoordSystem display)
    {
        if (data != display) (lon, lat) = ChinaOffset.Convert(lon, lat, data, display);
        return WebMercator.Forward(lon, lat);
    }

    /// <summary>投影一个几何（节点的几何，或层级检查、切割预览里的临时几何）。</summary>
    public static ProjectedShape Build(Geometry g, int version, CoordSystem data, CoordSystem display)
    {
        var s = new ProjectedShape
        {
            Version = version,
            DataCrs = data,
            DisplayCrs = display,
            Kind = GeoNode.KindOf(g),
            MinX = double.MaxValue,
            MinY = double.MaxValue,
            MaxX = double.MinValue,
            MaxY = double.MinValue,
        };

        var paths = new List<double[]>();
        var dataPaths = new List<Coordinate[]>();

        void AddPath(Coordinate[] coords)
        {
            var arr = new double[coords.Length * 2];
            double minX = s.MinX, minY = s.MinY, maxX = s.MaxX, maxY = s.MaxY;
            for (int i = 0; i < coords.Length; i++)
            {
                var (x, y) = DataToWorld(coords[i].X, coords[i].Y, data, display);
                arr[i * 2] = x;
                arr[i * 2 + 1] = y;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
            s.MinX = minX;
            s.MinY = minY;
            s.MaxX = maxX;
            s.MaxY = maxY;
            paths.Add(arr);
            dataPaths.Add(coords);
            s.VertexCount += coords.Length;
        }

        switch (s.Kind)
        {
            case NodeKind.Polygon:
            {
                var counts = new List<int>();
                foreach (var p in Geometries.Polygons(g))
                {
                    AddPath(p.Shell.Coordinates);
                    foreach (var h in p.Holes) AddPath(h.Coordinates);
                    counts.Add(1 + p.NumInteriorRings);
                }
                s.RingCounts = counts.ToArray();
                break;
            }
            case NodeKind.Line:
                foreach (var l in Geometries.Lines(g)) AddPath(l.Coordinates);
                SetLineLabel(s, paths);
                break;
            case NodeKind.Point:
            {
                var pts = Geometries.Points(g).ToList();
                s.Points = new double[pts.Count * 2];
                for (int i = 0; i < pts.Count; i++)
                {
                    var (x, y) = DataToWorld(pts[i].X, pts[i].Y, data, display);
                    s.Points[i * 2] = x;
                    s.Points[i * 2 + 1] = y;
                    s.MinX = Math.Min(s.MinX, x);
                    s.MinY = Math.Min(s.MinY, y);
                    s.MaxX = Math.Max(s.MaxX, x);
                    s.MaxY = Math.Max(s.MaxY, y);
                }
                s.VertexCount = pts.Count;
                if (pts.Count > 0) s.SetLabel(s.Points[0], s.Points[1], 0);
                break;
            }
        }

        s.Paths = paths.ToArray();
        s.DataPaths = dataPaths.ToArray();
        s.ComputeSignificance();
        return s;
    }

    /// <summary>线的标注放在最长一段按长度一半的位置。</summary>
    private static void SetLineLabel(ProjectedShape s, List<double[]> paths)
    {
        double[]? longest = null;
        double longestLen = -1;
        foreach (var p in paths)
        {
            double len = 0;
            for (int i = 2; i < p.Length; i += 2) len += Math.Sqrt((p[i] - p[i - 2]) * (p[i] - p[i - 2]) + (p[i + 1] - p[i - 1]) * (p[i + 1] - p[i - 1]));
            if (len > longestLen)
            {
                longestLen = len;
                longest = p;
            }
        }
        if (longest == null || longest.Length < 2) return;
        double half = longestLen / 2, acc = 0;
        for (int i = 2; i < longest.Length; i += 2)
        {
            double dx = longest[i] - longest[i - 2], dy = longest[i + 1] - longest[i - 1];
            double seg = Math.Sqrt(dx * dx + dy * dy);
            if (acc + seg >= half && seg > 0)
            {
                double t = (half - acc) / seg;
                s.SetLabel(longest[i - 2] + dx * t, longest[i - 1] + dy * t, 0);
                return;
            }
            acc += seg;
        }
        s.SetLabel(longest[0], longest[1], 0);
    }
}

/// <summary>世界坐标下的简单几何判断。</summary>
public static class WorldMath
{
    /// <summary>偶奇规则判断点是否在一组环内（环可以为 null，表示在这一级被简化掉了）。</summary>
    public static bool PointInRings(double[]?[] rings, double x, double y)
    {
        bool inside = false;
        foreach (var r in rings)
        {
            if (r == null) continue;
            int n = r.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = r[i * 2 + 1], yj = r[j * 2 + 1];
                if ((yi > y) == (yj > y)) continue;
                double xi = r[i * 2], xj = r[j * 2];
                if (x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>点到线段的距离平方，并给出最近点的参数 t。</summary>
    public static double SegmentDistanceSq(double px, double py, double ax, double ay, double bx, double by, out double t)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double cx = ax + t * dx - px, cy = ay + t * dy - py;
        return cx * cx + cy * cy;
    }

    /// <summary>点到一组路径的最近距离平方（路径可以为 null）。</summary>
    public static double PathsDistanceSq(double[]?[] paths, double x, double y)
    {
        double best = double.MaxValue;
        foreach (var p in paths)
        {
            if (p == null) continue;
            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                double d = SegmentDistanceSq(x, y, p[i], p[i + 1], p[i + 2], p[i + 3], out _);
                if (d < best) best = d;
            }
        }
        return best;
    }
}
