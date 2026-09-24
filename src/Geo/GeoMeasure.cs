using System.Globalization;
using System.Runtime.CompilerServices;

using NetTopologySuite.Geometries;

namespace GeoJsonEditor.Geo;

/// <summary>在球面上计算面积和长度（WGS-84 平均半径），用于属性面板的统计数字。</summary>
public static class GeoMeasure
{
    private const double R = 6371008.8;

    // 几何按不可变值使用，结果可以按对象缓存（几何被回收时缓存项自动消失）
    private static readonly ConditionalWeakTable<Geometry, StrongBox<double>> AreaCache = new();
    private static readonly ConditionalWeakTable<Geometry, StrongBox<double>> LengthCache = new();

    public static double Area(Geometry? g)
    {
        if (g == null) return 0;
        if (AreaCache.TryGetValue(g, out var cached)) return cached.Value;
        double sum = 0;
        foreach (var p in Geometries.Polygons(g))
        {
            sum += Math.Abs(RingArea(p.Shell.Coordinates));
            foreach (var h in p.Holes) sum -= Math.Abs(RingArea(h.Coordinates));
        }
        double area = Math.Max(0, sum);
        AreaCache.AddOrUpdate(g, new StrongBox<double>(area));
        return area;
    }

    /// <summary>面的周长或线的长度（米）。</summary>
    public static double Length(Geometry? g)
    {
        if (g == null) return 0;
        if (LengthCache.TryGetValue(g, out var cached)) return cached.Value;
        double length = ComputeLength(g);
        LengthCache.AddOrUpdate(g, new StrongBox<double>(length));
        return length;
    }

    private static double ComputeLength(Geometry g)
    {
        double sum = 0;
        foreach (var p in Geometries.Polygons(g))
        {
            sum += PathLength(p.Shell.Coordinates);
            foreach (var h in p.Holes) sum += PathLength(h.Coordinates);
        }
        foreach (var l in Geometries.Lines(g)) sum += PathLength(l.Coordinates);
        return sum;
    }

    public static double PathLength(IReadOnlyList<Coordinate> coords)
    {
        double sum = 0;
        for (int i = 1; i < coords.Count; i++) sum += Distance(coords[i - 1], coords[i]);
        return sum;
    }

    public static double Distance(Coordinate a, Coordinate b)
    {
        double lat1 = a.Y * Math.PI / 180, lat2 = b.Y * Math.PI / 180;
        double dLat = lat2 - lat1, dLon = (b.X - a.X) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    // Chamberlain & Duquette 的球面多边形面积公式。
    private static double RingArea(Coordinate[] c)
    {
        int n = c.Length;
        if (n < 4) return 0;
        double total = 0;
        for (int i = 0; i < n - 1; i++)
        {
            var p1 = c[i];
            var p2 = c[i + 1];
            total += (p2.X - p1.X) * Math.PI / 180 * (2 + Math.Sin(p1.Y * Math.PI / 180) + Math.Sin(p2.Y * Math.PI / 180));
        }
        return total * R * R / 2.0;
    }

    public static string FormatArea(double m2)
    {
        if (m2 >= 1e6) return (m2 / 1e6).ToString(m2 >= 1e8 ? "N0" : "N2", CultureInfo.InvariantCulture) + " km²";
        if (m2 >= 1e4) return (m2 / 1e4).ToString("N2", CultureInfo.InvariantCulture) + " 公顷";
        return m2.ToString("N0", CultureInfo.InvariantCulture) + " m²";
    }

    public static string FormatLength(double m)
    {
        if (m >= 1000) return (m / 1000).ToString(m >= 100000 ? "N0" : "N2", CultureInfo.InvariantCulture) + " km";
        return m.ToString("N0", CultureInfo.InvariantCulture) + " m";
    }

    public static string FormatLonLat(double lon, double lat)
    {
        string ew = lon >= 0 ? "E" : "W";
        string ns = lat >= 0 ? "N" : "S";
        return $"{Math.Abs(lon).ToString("F5", CultureInfo.InvariantCulture)}°{ew}  {Math.Abs(lat).ToString("F5", CultureInfo.InvariantCulture)}°{ns}";
    }
}
