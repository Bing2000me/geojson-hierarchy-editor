using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;

namespace GeoJsonEditor.Geo;

/// <summary>自动边界的计算统计（测试程序用）。</summary>
public static class DissolveStats
{
    public static int Coverage;
    public static int Fallback;
}

/// <summary>合并、切割、裁剪等几何运算。输入输出都是经纬度几何，不修改输入对象。</summary>
public static class GeometryOps
{
    public static Geometry Clean(Geometry g)
    {
        if (g.IsValid) return g;
        try
        {
            return GeometryFixer.Fix(g);
        }
        catch
        {
            return g.Buffer(0);
        }
    }

    /// <summary>面的并集，结果只保留面状部分。</summary>
    public static Geometry? UnionPolygons(IEnumerable<Geometry?> geometries)
    {
        var polys = geometries
            .SelectMany(Geometries.Polygons)
            .Select(p => Clean(p))
            .ToList();
        if (polys.Count == 0) return null;
        if (polys.Count == 1) return Geometries.PolygonalPart(polys[0]);
        var union = UnaryUnionOp.Union(polys);
        return Geometries.PolygonalPart(union);
    }

    /// <summary>
    /// 由下级拼出上级的范围（显示用的自动边界，与 P 社游戏里由省份拼出国家的做法相同）。
    /// 下级正好拼成覆盖面（公共边界的顶点一致）时用覆盖面合并，比一般的并集快得多；
    /// 否则退回稳健的并集。结果去掉下级之间的细缝留下的狭长空洞（宽度不到范围的万分之一），
    /// 真正的飞地空洞保留。并集失败时返回不合并的多面，至少范围是完整的。
    /// </summary>
    public static Geometry? Dissolve(IReadOnlyList<Geometry> parts)
    {
        var polys = parts.SelectMany(Geometries.Polygons).Where(p => !p.IsEmpty && p.Area > 0).ToList();
        if (polys.Count == 0) return null;
        if (polys.Count == 1) return polys[0];
        var f = Geometries.Factory;
        double sum = polys.Sum(p => p.Area);
        Geometry? result = null;
        try
        {
            var coverage = NetTopologySuite.Operation.OverlayNG.CoverageUnion.Union(f.BuildGeometry(polys));
            // 不是合格的覆盖面（有重叠）时结果面积对不上或者无效
            // 下级之间有细缝（公共边界两侧的顶点不一致）时，结果里会有环自身相接形成的小圈和来回的尖刺，
            // 在 CleanRings 里直接剪掉，比检查有效性再修复（或者重算一般并集）快一个数量级
            if (coverage != null && Math.Abs(coverage.Area - sum) <= sum * 1e-7) result = CleanRings(coverage, sum);
        }
        catch (Exception)
        {
        }
        if (result != null) Interlocked.Increment(ref DissolveStats.Coverage);
        else Interlocked.Increment(ref DissolveStats.Fallback);
        if (result == null)
        {
            try
            {
                result = NetTopologySuite.Operation.OverlayNG.OverlayNGRobust.Union(f.BuildGeometry(polys.Select(p => Clean(p)).ToArray()));
            }
            catch (Exception)
            {
                return Geometries.ToPolygonal(polys);
            }
        }
        return RemoveSlivers(Geometries.PolygonalPart(result));
    }

    /// <summary>
    /// 剪掉环上自身相接形成的小圈（面积不到总面积的十万分之一，例如细缝留下的倒转小空洞）和来回的尖刺，
    /// 再去掉因此退化的环和面积极小的碎块。线性时间。
    /// </summary>
    private static Geometry? CleanRings(Geometry g, double total)
    {
        double minLoop = total * 1e-5, minPart = total * 1e-9;
        var f = Geometries.Factory;
        var result = new List<Polygon>();
        foreach (var p in Geometries.Polygons(g))
        {
            var shell = CleanRing(p.Shell.Coordinates, minLoop);
            if (shell == null) continue;
            var holes = new List<LinearRing>();
            foreach (var h in p.Holes)
            {
                var hole = CleanRing(h.Coordinates, minLoop);
                if (hole != null) holes.Add(f.CreateLinearRing(hole));
            }
            var poly = f.CreatePolygon(f.CreateLinearRing(shell), holes.ToArray());
            if (Math.Abs(poly.Area) >= minPart) result.Add(poly);
        }
        return Geometries.ToPolygonal(result);
    }

    private static Coordinate[]? CleanRing(Coordinate[] ring, double minLoop)
    {
        int n = ring.Length - 1;
        if (n < 3) return null;
        var output = new List<Coordinate>(n + 1);
        var seen = new Dictionary<(double, double), int>(n);
        for (int i = 0; i < n; i++)
        {
            var c = ring[i];
            if (output.Count > 0 && output[^1].Equals2D(c)) continue;
            if (seen.TryGetValue((c.X, c.Y), out int j) && j < output.Count && output[j].Equals2D(c))
            {
                // output[j..] 回到了 c：一个小圈（或尖刺），面积小就剪掉
                double area = 0;
                for (int k = j, m = output.Count; k < m; k++)
                {
                    var a = output[k];
                    var b = k + 1 < m ? output[k + 1] : c;
                    area += a.X * b.Y - b.X * a.Y;
                }
                if (Math.Abs(area) / 2 < minLoop)
                {
                    for (int k = j + 1; k < output.Count; k++) seen.Remove((output[k].X, output[k].Y));
                    output.RemoveRange(j + 1, output.Count - j - 1);
                    continue;
                }
            }
            seen[(c.X, c.Y)] = output.Count;
            output.Add(c);
        }
        // 首尾之间也可能是尖刺
        while (output.Count >= 3 && output[^2].Equals2D(output[0])) output.RemoveAt(output.Count - 1);
        if (output.Count < 3) return null;
        output.Add(output[0].Copy());
        return output.ToArray();
    }

    /// <summary>
    /// 去掉下级之间的缝隙留下的空洞：平均宽度（2 × 面积 / 周长）不到范围尺度的千分之三（例如县界之间空出来的河道），
    /// 或者面积不到总面积的十万分之一。湖泊、飞地这样成片的空洞保留。
    /// </summary>
    private static Geometry? RemoveSlivers(Geometry? g)
    {
        if (g == null) return null;
        var polys = Geometries.Polygons(g).ToList();
        double total = polys.Sum(p => p.Area);
        if (!(total > 0)) return g;
        double minWidth = Math.Sqrt(total) * 3e-3, minArea = total * 1e-5;
        bool changed = false;
        var result = new List<Polygon>(polys.Count);
        foreach (var p in polys)
        {
            if (p.NumInteriorRings == 0)
            {
                result.Add(p);
                continue;
            }
            var keep = new List<LinearRing>();
            foreach (var hole in p.Holes)
            {
                double area = Math.Abs(NetTopologySuite.Algorithm.Area.OfRing(hole.CoordinateSequence));
                double width = 2 * area / Math.Max(hole.Length, 1e-300);
                if (area >= minArea && width >= minWidth) keep.Add(hole);
            }
            if (keep.Count == p.NumInteriorRings)
            {
                result.Add(p);
                continue;
            }
            changed = true;
            result.Add(Geometries.Factory.CreatePolygon(p.Shell, keep.ToArray()));
        }
        return changed ? Geometries.ToPolygonal(result) : g;
    }

    /// <summary>把相接的线首尾相连。</summary>
    public static Geometry? MergeLines(IEnumerable<Geometry?> geometries)
    {
        var merger = new LineMerger();
        foreach (var l in geometries.SelectMany(Geometries.Lines)) merger.Add(l);
        var merged = merger.GetMergedLineStrings().OfType<LineString>().ToList();
        return Geometries.ToLineal(merged);
    }

    public static Geometry? Intersection(Geometry a, Geometry b)
    {
        var r = Clean(a).Intersection(Clean(b));
        return r.IsEmpty ? null : r;
    }

    public static Geometry? Difference(Geometry a, Geometry b)
    {
        var r = Clean(a).Difference(Clean(b));
        return r.IsEmpty ? null : r;
    }

    /// <summary>
    /// 新画的面放进上级之前的处理：可选地裁剪到上级范围，并扣除同级已有的面，
    /// 这样相邻区域正好共用边界，不重叠也不留缝。
    /// </summary>
    public static Geometry? FitPolygon(Geometry drawn, Geometry? parent, IEnumerable<Geometry?> siblings, bool clipToParent, bool avoidSiblings)
    {
        Geometry? g = Clean(drawn);
        if (clipToParent && parent != null && Geometries.Polygons(parent).Any())
        {
            g = Intersection(g, parent);
            if (g == null) return null;
        }
        if (avoidSiblings)
        {
            var others = UnionPolygons(siblings);
            if (others != null)
            {
                g = Difference(g, others);
                if (g == null) return null;
            }
        }
        return Geometries.PolygonalPart(g);
    }

    // ───────────────────────── 切割 ─────────────────────────

    /// <summary>
    /// 用切割线把面分成若干块。每个面分量单独处理：边界与切割线求并完成打断，
    /// 再多边形化，保留落在原面内部的块。没有被切到的分量（例如海岛）并入离它最近的块。
    /// 切割线没有完整穿过时返回只有原几何的列表。
    /// </summary>
    public static List<Geometry> SplitPolygonal(Geometry target, LineString cutter)
    {
        var pieces = new List<Polygon>();
        var untouched = new List<Polygon>();

        foreach (var part in Geometries.Polygons(Clean(target)))
        {
            if (!part.Intersects(cutter))
            {
                untouched.Add(part);
                continue;
            }

            var split = SplitPolygon(part, cutter);
            if (split.Count <= 1)
            {
                untouched.Add(part);
            }
            else
            {
                pieces.AddRange(split);
            }
        }

        if (pieces.Count == 0) return new List<Geometry> { target };

        var groups = pieces.Select(p => new List<Polygon> { p }).ToList();
        foreach (var island in untouched)
        {
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < pieces.Count; i++)
            {
                double d = pieces[i].Distance(island);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            groups[best].Add(island);
        }

        return groups
            .Select(g => Geometries.ToPolygonal(g)!)
            .OrderByDescending(g => g.Area)
            .ToList();
    }

    private static List<Polygon> SplitPolygon(Polygon polygon, LineString cutter)
    {
        var noded = polygon.Boundary.Union(cutter);
        var polygonizer = new Polygonizer();
        polygonizer.Add(noded);
        var result = new List<Polygon>();
        foreach (var g in polygonizer.GetPolygons())
        {
            if (g is not Polygon piece || piece.IsEmpty || piece.Area <= 0) continue;
            var inside = piece.InteriorPoint;
            if (polygon.Contains(inside)) result.Add(piece);
        }
        return result;
    }

    /// <summary>在切割线与线要素的每个交点处把线打断。</summary>
    public static List<Geometry> SplitLineal(Geometry target, LineString cutter)
    {
        var pieces = new List<LineString>();
        bool any = false;
        foreach (var line in Geometries.Lines(target))
        {
            var split = SplitLine(line, cutter);
            if (split.Count > 1) any = true;
            pieces.AddRange(split);
        }
        if (!any) return new List<Geometry> { target };
        return pieces.Cast<Geometry>().ToList();
    }

    public static List<LineString> SplitLine(LineString line, LineString cutter)
    {
        var f = Geometries.Factory;
        var coords = line.Coordinates;
        var cut = cutter.Coordinates;
        var result = new List<LineString>();
        var current = new List<Coordinate> { coords[0].Copy() };
        const double eps = 1e-12;

        for (int i = 0; i < coords.Length - 1; i++)
        {
            var a = coords[i];
            var b = coords[i + 1];
            var hits = new List<(double T, Coordinate P)>();
            for (int j = 0; j < cut.Length - 1; j++)
            {
                if (SegmentIntersection(a, b, cut[j], cut[j + 1], out double t, out var p) && t > eps && t <= 1 - eps)
                {
                    hits.Add((t, p));
                }
            }
            hits.Sort((x, y) => x.T.CompareTo(y.T));
            foreach (var (_, p) in hits)
            {
                if (current[^1].Equals2D(p)) continue;
                current.Add(p);
                if (current.Count >= 2) result.Add(f.CreateLineString(current.ToArray()));
                current = new List<Coordinate> { p.Copy() };
            }
            if (!current[^1].Equals2D(b)) current.Add(b.Copy());
        }
        if (current.Count >= 2) result.Add(f.CreateLineString(current.ToArray()));
        if (result.Count == 0) result.Add(line);
        return result;
    }

    private static bool SegmentIntersection(Coordinate a, Coordinate b, Coordinate c, Coordinate d, out double t, out Coordinate p)
    {
        double rX = b.X - a.X, rY = b.Y - a.Y;
        double sX = d.X - c.X, sY = d.Y - c.Y;
        double denom = rX * sY - rY * sX;
        t = 0;
        p = a;
        if (Math.Abs(denom) < 1e-18) return false;
        double qpX = c.X - a.X, qpY = c.Y - a.Y;
        t = (qpX * sY - qpY * sX) / denom;
        double u = (qpX * rY - qpY * rX) / denom;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        p = new Coordinate(a.X + t * rX, a.Y + t * rY);
        return true;
    }

    /// <summary>把任意几何的代表点（面取内部点，线取中间顶点）用于判断它落在哪一块里。</summary>
    public static Point RepresentativePoint(Geometry g) => g.InteriorPoint;

    /// <summary>在若干块中找出最应该容纳 <paramref name="g"/> 的那一块：包含代表点优先，其次按重叠面积或距离。</summary>
    public static int BestPiece(Geometry g, IReadOnlyList<Geometry> pieces)
    {
        if (pieces.Count == 1) return 0;
        var rep = RepresentativePoint(g);
        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Covers(rep)) return i;
        }
        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < pieces.Count; i++)
        {
            double d = pieces[i].Distance(rep);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }
}
