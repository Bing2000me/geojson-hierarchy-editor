using NetTopologySuite.Geometries;

namespace GeoJsonEditor.Geo;

/// <summary>
/// 顶点级编辑。几何被拆成若干“路径”：面依次是每个面的外环和内环（首尾相同），线是每条线。
/// 顺序与 <c>ShapeCache</c> 的投影缓存一致，所以屏幕上的手柄可以直接对应到路径和下标。
/// </summary>
public static class VertexEditing
{
    public static Coordinate[][] ExtractPaths(Geometry g)
    {
        var list = new List<Coordinate[]>();
        foreach (var p in Geometries.Polygons(g))
        {
            list.Add(p.Shell.Coordinates);
            foreach (var h in p.Holes) list.Add(h.Coordinates);
        }
        if (list.Count == 0)
        {
            foreach (var l in Geometries.Lines(g)) list.Add(l.Coordinates);
        }
        return list.ToArray();
    }

    public static bool IsPolygonal(Geometry g) => Geometries.Polygons(g).Any();

    public static Geometry Rebuild(Geometry original, Coordinate[][] paths)
    {
        var f = Geometries.Factory;
        if (IsPolygonal(original))
        {
            var polys = new List<Polygon>();
            int k = 0;
            foreach (var p in Geometries.Polygons(original))
            {
                var shell = f.CreateLinearRing(paths[k++]);
                var holes = new LinearRing[p.NumInteriorRings];
                for (int i = 0; i < holes.Length; i++) holes[i] = f.CreateLinearRing(paths[k++]);
                polys.Add(f.CreatePolygon(shell, holes));
            }
            return original is Polygon ? polys[0] : f.CreateMultiPolygon(polys.ToArray());
        }

        var lines = paths.Select(c => f.CreateLineString(c)).ToArray();
        return original is LineString ? lines[0] : f.CreateMultiLineString(lines);
    }

    private static Coordinate[][] Clone(Coordinate[][] paths)
        => paths.Select(p => p.Select(c => c.Copy()).ToArray()).ToArray();

    public static Geometry MoveVertex(Geometry g, int path, int index, Coordinate c)
    {
        var paths = Clone(ExtractPaths(g));
        var ring = paths[path];
        bool closed = IsPolygonal(g);
        ring[index] = c.Copy();
        if (closed)
        {
            if (index == 0) ring[^1] = c.Copy();
            else if (index == ring.Length - 1) ring[0] = c.Copy();
        }
        return Rebuild(g, paths);
    }

    public static Geometry InsertVertex(Geometry g, int path, int afterIndex, Coordinate c)
    {
        var paths = Clone(ExtractPaths(g));
        var list = paths[path].ToList();
        list.Insert(afterIndex + 1, c.Copy());
        paths[path] = list.ToArray();
        return Rebuild(g, paths);
    }

    /// <summary>删除顶点；面环至少保留 3 个不同顶点、线至少保留 2 个，不满足时返回 null。</summary>
    public static Geometry? DeleteVertex(Geometry g, int path, int index)
    {
        var paths = Clone(ExtractPaths(g));
        var list = paths[path].ToList();
        bool closed = IsPolygonal(g);
        if (closed)
        {
            if (list.Count <= 4) return null;
            if (index == 0 || index == list.Count - 1)
            {
                list.RemoveAt(list.Count - 1);
                list.RemoveAt(0);
                list.Add(list[0].Copy());
            }
            else
            {
                list.RemoveAt(index);
            }
        }
        else
        {
            if (list.Count <= 2) return null;
            list.RemoveAt(index);
        }
        paths[path] = list.ToArray();
        return Rebuild(g, paths);
    }

    /// <summary>把几何中所有与 <paramref name="from"/> 重合的顶点移到 <paramref name="to"/>；没有重合顶点时返回 null。</summary>
    public static Geometry? ReplaceCoordinate(Geometry g, Coordinate from, Coordinate to)
    {
        if (g is Point p)
        {
            return p.Coordinate.Equals2D(from) ? Geometries.Factory.CreatePoint(to.Copy()) : null;
        }
        var paths = Clone(ExtractPaths(g));
        bool any = false;
        foreach (var ring in paths)
        {
            for (int i = 0; i < ring.Length; i++)
            {
                if (ring[i].Equals2D(from))
                {
                    ring[i] = to.Copy();
                    any = true;
                }
            }
        }
        return any ? Rebuild(g, paths) : null;
    }

    public static bool HasCoordinate(Geometry g, Coordinate c)
    {
        // 逐条路径查找：Geometry.Coordinates 会把整个几何的坐标复制成一个新数组
        foreach (var path in ExtractPaths(g))
        {
            foreach (var v in path)
            {
                if (v.Equals2D(c)) return true;
            }
        }
        return false;
    }

    public static Geometry MovePoint(Geometry g, int index, Coordinate c)
    {
        var f = Geometries.Factory;
        if (g is Point) return f.CreatePoint(c.Copy());
        var pts = Geometries.Points(g).Select(x => x.Coordinate.Copy()).ToArray();
        pts[index] = c.Copy();
        return f.CreateMultiPointFromCoords(pts);
    }
}
