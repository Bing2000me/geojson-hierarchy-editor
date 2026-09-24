using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace GeoJsonEditor.Geo;

public static class Geometries
{
    /// <summary>经纬度（EPSG:4326）几何工厂。</summary>
    public static GeometryFactory Factory { get; } =
        NtsGeometryServices.Instance.CreateGeometryFactory(new PrecisionModel(PrecisionModels.Floating), 4326);

    public static IEnumerable<Polygon> Polygons(Geometry? g)
    {
        switch (g)
        {
            case null:
                yield break;
            case Polygon p:
                yield return p;
                break;
            case GeometryCollection gc:
                for (int i = 0; i < gc.NumGeometries; i++)
                {
                    foreach (var p in Polygons(gc.GetGeometryN(i))) yield return p;
                }
                break;
        }
    }

    public static IEnumerable<LineString> Lines(Geometry? g)
    {
        switch (g)
        {
            case null:
                yield break;
            case LineString l:
                yield return l;
                break;
            case GeometryCollection gc:
                for (int i = 0; i < gc.NumGeometries; i++)
                {
                    foreach (var l in Lines(gc.GetGeometryN(i))) yield return l;
                }
                break;
        }
    }

    public static IEnumerable<Point> Points(Geometry? g)
    {
        switch (g)
        {
            case null:
                yield break;
            case Point p:
                yield return p;
                break;
            case GeometryCollection gc:
                for (int i = 0; i < gc.NumGeometries; i++)
                {
                    foreach (var p in Points(gc.GetGeometryN(i))) yield return p;
                }
                break;
        }
    }

    /// <summary>把若干面组合成 Polygon 或 MultiPolygon；为空时返回 null。</summary>
    public static Geometry? ToPolygonal(IEnumerable<Polygon> polygons)
    {
        var list = polygons.Where(p => !p.IsEmpty).ToArray();
        return list.Length switch
        {
            0 => null,
            1 => list[0],
            _ => Factory.CreateMultiPolygon(list),
        };
    }

    /// <summary>只保留结果里的面状部分（叠加运算可能附带退化的线或点）。</summary>
    public static Geometry? PolygonalPart(Geometry? g) => ToPolygonal(Polygons(g));

    public static Geometry? ToLineal(IEnumerable<LineString> lines)
    {
        var list = lines.Where(l => !l.IsEmpty).ToArray();
        return list.Length switch
        {
            0 => null,
            1 => list[0],
            _ => Factory.CreateMultiLineString(list),
        };
    }

    public static int VertexCount(Geometry? g) => g?.NumPoints ?? 0;
}
