using GeoJsonEditor.Map;
using GeoJsonEditor.Model;

using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace GeoJsonEditor.Geo;

public sealed record SimplifyResult(
    List<(GeoNode Node, Geometry Geometry)> Changes,
    int VerticesBefore,
    int VerticesAfter,
    int Features,
    int Repaired,
    int DroppedParts);

/// <summary>
/// 保持拓扑的边界简化（与 mapshaper / TopoJSON 的做法相同）：
/// 先把所有环和线在“交汇点”处切成弧段，完全相同的弧段（相邻区域的公共边界、上级与下级重合的外边界）只保留一份，
/// 每条弧段只简化一次（Douglas–Peucker，端点固定），再把简化后的弧段拼回各个要素。
/// 这样相邻区域之间、上下级之间简化后仍然严丝合缝，不会出现缝隙或重叠。
/// 交汇点：某个顶点在不同位置出现时前后相邻的顶点不一致（三块区域交界处、公共边界的起止点），以及线的端点。
/// </summary>
public static class TopoSimplifier
{
    private readonly record struct PathRef(int Item, int Part, int Ring, bool Closed);

    private sealed class ArcKey(int[] ids) : IEquatable<ArcKey>
    {
        public int[] Ids { get; } = ids;

        private readonly int _hash = ComputeHash(ids);

        private static int ComputeHash(int[] ids)
        {
            var h = new HashCode();
            h.Add(ids.Length);
            foreach (var i in ids) h.Add(i);
            return h.ToHashCode();
        }

        public bool Equals(ArcKey? other) => other != null && other._hash == _hash && other.Ids.AsSpan().SequenceEqual(Ids);

        public override bool Equals(object? obj) => Equals(obj as ArcKey);

        public override int GetHashCode() => _hash;
    }

    /// <param name="items">要简化的要素（面和线），几何在 UI 线程上取出后传进来，这里不访问文档。</param>
    /// <param name="toleranceMeters">允许的最大偏差（米）。</param>
    public static SimplifyResult Simplify(IReadOnlyList<(GeoNode Node, Geometry Geometry)> items, double toleranceMeters, CancellationToken ct = default)
    {
        // ── 1. 收集路径，给每个不同的坐标编号 ──
        var paths = new List<PathRef>();
        var pathIds = new List<int[]>();
        var ids = new Dictionary<(double X, double Y), int>();
        var coords = new List<Coordinate>();
        int before = 0;

        int Id(Coordinate c)
        {
            if (!ids.TryGetValue((c.X, c.Y), out int id))
            {
                id = coords.Count;
                ids[(c.X, c.Y)] = id;
                coords.Add(c);
            }
            return id;
        }

        void AddPath(int item, int part, int ring, Coordinate[] seq, bool closed)
        {
            var list = new List<int>(seq.Length);
            int count = closed ? seq.Length - 1 : seq.Length;
            for (int i = 0; i < count; i++)
            {
                int id = Id(seq[i]);
                if (list.Count > 0 && list[^1] == id) continue;
                list.Add(id);
            }
            if (closed && list.Count > 1 && list[^1] == list[0]) list.RemoveAt(list.Count - 1);
            paths.Add(new PathRef(item, part, ring, closed));
            pathIds.Add(list.ToArray());
        }

        for (int k = 0; k < items.Count; k++)
        {
            var g = items[k].Geometry;
            before += g.NumPoints;
            if (GeoNode.KindOf(g) == NodeKind.Polygon)
            {
                int part = 0;
                foreach (var p in Geometries.Polygons(g))
                {
                    AddPath(k, part, 0, p.Shell.Coordinates, true);
                    for (int h = 0; h < p.NumInteriorRings; h++) AddPath(k, part, h + 1, p.GetInteriorRingN(h).Coordinates, true);
                    part++;
                }
            }
            else
            {
                int part = 0;
                foreach (var l in Geometries.Lines(g)) AddPath(k, part++, 0, l.Coordinates, false);
            }
        }
        ct.ThrowIfCancellationRequested();

        // ── 2. 找交汇点 ──
        int nv = coords.Count;
        var nbLo = new int[nv];
        var nbHi = new int[nv];
        Array.Fill(nbLo, -2);
        var junction = new bool[nv];
        for (int p = 0; p < paths.Count; p++)
        {
            var seq = pathIds[p];
            bool closed = paths[p].Closed;
            int n = seq.Length;
            for (int i = 0; i < n; i++)
            {
                int v = seq[i];
                int prev = i > 0 ? seq[i - 1] : closed ? seq[n - 1] : -1;
                int next = i < n - 1 ? seq[i + 1] : closed ? seq[0] : -1;
                if (!closed && (i == 0 || i == n - 1)) junction[v] = true;
                int lo = Math.Min(prev, next), hi = Math.Max(prev, next);
                if (nbLo[v] == -2)
                {
                    nbLo[v] = lo;
                    nbHi[v] = hi;
                }
                else if (nbLo[v] != lo || nbHi[v] != hi)
                {
                    junction[v] = true;
                }
            }
        }
        ct.ThrowIfCancellationRequested();

        // ── 3. 在交汇点处切成弧段，相同的弧段（含反向）只保留一份 ──
        var arcIndex = new Dictionary<ArcKey, int>();
        var arcs = new List<int[]>();
        var pathArcs = new List<(int Arc, bool Reversed)>[paths.Count];

        void AddArc(List<(int, bool)> refs, int[] seq)
        {
            // 规范方向：正向与反向中字典序较小的那个
            var rev = seq.Reverse().ToArray();
            bool useRev = false;
            for (int i = 0; i < seq.Length; i++)
            {
                if (seq[i] == rev[i]) continue;
                useRev = rev[i] < seq[i];
                break;
            }
            var key = new ArcKey(useRev ? rev : seq);
            if (!arcIndex.TryGetValue(key, out int a))
            {
                a = arcs.Count;
                arcs.Add(key.Ids);
                arcIndex[key] = a;
            }
            refs.Add((a, useRev));
        }

        for (int p = 0; p < paths.Count; p++)
        {
            var seq = pathIds[p];
            var refs = new List<(int, bool)>();
            pathArcs[p] = refs;
            int n = seq.Length;
            if (n < 2) continue;

            if (!paths[p].Closed)
            {
                int start = 0;
                for (int i = 1; i < n; i++)
                {
                    if (!junction[seq[i]] && i < n - 1) continue;
                    AddArc(refs, seq[start..(i + 1)]);
                    start = i;
                }
                continue;
            }

            int first = Array.FindIndex(seq, v => junction[v]);
            if (first < 0)
            {
                // 没有交汇点的独立环：从坐标最小的顶点起算，完全相同的两个环（例如只有一个下级时上下级边界相同）得到同一条弧段
                int min = 0;
                for (int i = 1; i < n; i++)
                {
                    var a = coords[seq[i]];
                    var b = coords[seq[min]];
                    if (a.X < b.X || (a.X == b.X && a.Y < b.Y)) min = i;
                }
                var cycle = new int[n + 1];
                for (int i = 0; i <= n; i++) cycle[i] = seq[(min + i) % n];
                AddArc(refs, cycle);
                continue;
            }

            var rotated = new int[n + 1];
            for (int i = 0; i <= n; i++) rotated[i] = seq[(first + i) % n];
            int s0 = 0;
            for (int i = 1; i <= n; i++)
            {
                if (i < n && !junction[rotated[i]]) continue;
                AddArc(refs, rotated[s0..(i + 1)]);
                s0 = i;
            }
        }
        ct.ThrowIfCancellationRequested();

        // ── 4. 每条弧段简化一次 ──
        // 在“经度 × cos(平均纬度), 纬度”的平面上按度数计算，容差由米换算成度
        double meanLat = nv == 0 ? 0 : coords.Average(c => c.Y);
        double kx = Math.Cos(meanLat * Math.PI / 180);
        double tol = toleranceMeters / 111320.0;
        var simplified = new int[arcs.Count][];
        Parallel.For(0, arcs.Count, new ParallelOptions { CancellationToken = ct }, a =>
        {
            var arc = arcs[a];
            if (arc.Length <= 2)
            {
                simplified[a] = arc;
                return;
            }
            bool cyclic = arc[0] == arc[^1];
            var xy = new double[arc.Length * 2];
            for (int i = 0; i < arc.Length; i++)
            {
                var c = coords[arc[i]];
                xy[i * 2] = c.X * kx;
                xy[i * 2 + 1] = c.Y;
            }
            var sig = Lod.Significance(xy, cyclic);
            var keep = new List<int>(arc.Length);
            for (int i = 0; i < arc.Length; i++)
            {
                if (sig[i] >= tol) keep.Add(arc[i]);
            }
            simplified[a] = keep.ToArray();
        });

        // ── 5. 拼回各条路径 ──
        var rebuilt = new Coordinate[paths.Count][];
        for (int p = 0; p < paths.Count; p++)
        {
            var list = new List<int>();
            foreach (var (a, reversed) in pathArcs[p])
            {
                var arc = simplified[a];
                IEnumerable<int> seq = reversed ? arc.Reverse() : arc;
                foreach (var v in seq)
                {
                    if (list.Count > 0 && list[^1] == v) continue;
                    list.Add(v);
                }
            }
            if (paths[p].Closed)
            {
                if (list.Count > 1 && list[^1] == list[0]) list.RemoveAt(list.Count - 1);
                if (list.Count < 3)
                {
                    rebuilt[p] = [];
                    continue;
                }
                list.Add(list[0]);
            }
            rebuilt[p] = list.Select(v => coords[v].Copy()).ToArray();
        }

        // ── 6. 组装几何 ──
        var f = Geometries.Factory;
        var changes = new List<(GeoNode, Geometry)>();
        int after = 0, repaired = 0, dropped = 0;
        int cursor = 0;
        for (int k = 0; k < items.Count; k++)
        {
            var (node, original) = items[k];
            int start = cursor;
            while (cursor < paths.Count && paths[cursor].Item == k) cursor++;
            Geometry? result;
            if (GeoNode.KindOf(original) == NodeKind.Polygon)
            {
                var polys = new List<Polygon>();
                for (int p = start; p < cursor;)
                {
                    int part = paths[p].Part;
                    var shell = rebuilt[p];
                    var holes = new List<LinearRing>();
                    int q = p + 1;
                    while (q < cursor && paths[q].Part == part)
                    {
                        if (rebuilt[q].Length >= 4) holes.Add(f.CreateLinearRing(rebuilt[q]));
                        else dropped++;
                        q++;
                    }
                    if (shell.Length >= 4) polys.Add(f.CreatePolygon(f.CreateLinearRing(shell), holes.ToArray()));
                    else dropped++;
                    p = q;
                }
                result = Geometries.ToPolygonal(polys);
                if (result != null && !result.IsValid)
                {
                    result = Geometries.PolygonalPart(GeometryFixer.Fix(result));
                    repaired++;
                }
            }
            else
            {
                var lines = new List<LineString>();
                for (int p = start; p < cursor; p++)
                {
                    if (rebuilt[p].Length >= 2) lines.Add(f.CreateLineString(rebuilt[p]));
                }
                result = Geometries.ToLineal(lines);
            }

            // 整个要素都被简化没了（比容差还小）：保留原样
            if (result == null || result.IsEmpty)
            {
                after += original.NumPoints;
                continue;
            }
            after += result.NumPoints;
            if (result.NumPoints != original.NumPoints || !result.EqualsExact(original)) changes.Add((node, result));
        }

        return new SimplifyResult(changes, before, after, items.Count, repaired, dropped);
    }
}
