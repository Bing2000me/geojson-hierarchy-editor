using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;

namespace GeoJsonEditor.Geo;

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
