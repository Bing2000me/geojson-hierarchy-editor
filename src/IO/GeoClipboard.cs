using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using GeoJsonEditor.Geo;
using GeoJsonEditor.Model;

using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace GeoJsonEditor.IO;

public enum ClipboardFormat
{
    /// <summary>本程序复制的要素（带层级、坐标系和来源信息）。</summary>
    Editor,
    /// <summary>其他程序复制的 GeoJSON。</summary>
    GeoJson,
    /// <summary>WKT 文本，每行一个几何。</summary>
    Wkt,
    /// <summary>经纬度文本：一对是点，多对（每行一对）是线，首尾相同是面。</summary>
    Coordinates,
}

/// <param name="Crs">来源坐标系；只有本程序复制的内容才知道，其他来源为 null（按目标文档的坐标系理解）。</param>
/// <param name="DocumentId">来源文档的实例 id，用来判断是不是在同一个文档里复制粘贴。</param>
/// <param name="SourceIds">复制时最上层要素的 id。</param>
public sealed record ClipboardContent(
    List<GeoNode> Roots,
    ClipboardFormat Format,
    CoordSystem? Crs,
    string? DocumentId,
    IReadOnlyList<string> SourceIds,
    int FeatureCount);

/// <summary>
/// 剪贴板里的要素用标准 GeoJSON 文本交换：两个窗口、两个进程之间，或者与 geojson.io、QGIS、文本编辑器之间都能互相粘贴。
/// 本程序复制的内容在 FeatureCollection 上多一个 geojsonEditor 字段，记录坐标系和来源，粘贴时据此纠偏和决定位置。
/// </summary>
public static class GeoClipboard
{
    public static string Serialize(IReadOnlyList<GeoNode> roots, CoordSystem crs, string documentId)
    {
        var meta = new JsonObject
        {
            ["version"] = 1,
            ["crs"] = CrsName(crs),
            ["document"] = documentId,
            ["roots"] = new JsonArray(roots.Select(r => (JsonNode?)JsonValue.Create(r.Id)).ToArray()),
        };
        return GeoJsonIO.Write(roots, meta);
    }

    public static string CrsName(CoordSystem crs) => crs == CoordSystem.Gcj02 ? "gcj02" : "wgs84";

    public static CoordSystem? ParseCrs(string? name) => name switch
    {
        "gcj02" => CoordSystem.Gcj02,
        "wgs84" => CoordSystem.Wgs84,
        _ => null,
    };

    /// <summary>识别剪贴板文本；认不出来时返回 null。</summary>
    public static ClipboardContent? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('﻿');

        if (text[0] is '{' or '[')
        {
            ReadResult r;
            try
            {
                r = GeoJsonIO.Read(text);
            }
            catch (Exception)
            {
                return null;
            }
            if (r.Roots.Count == 0) return null;
            if (r.Meta is { } meta)
            {
                var ids = meta["roots"] is JsonArray arr
                    ? arr.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
                    : new List<string>();
                return new ClipboardContent(r.Roots, ClipboardFormat.Editor, ParseCrs(Str(meta["crs"])), Str(meta["document"]), ids, r.FeatureCount);
            }
            return new ClipboardContent(r.Roots, ClipboardFormat.GeoJson, null, null, [], r.FeatureCount);
        }

        if (TryParseWkt(text) is { Count: > 0 } wkt)
        {
            var nodes = wkt.Select(g => new GeoNode("n1") { Geometry = g }).ToList();
            return new ClipboardContent(nodes, ClipboardFormat.Wkt, null, null, [], nodes.Count);
        }

        if (TryParseCoordinates(text) is { } coords)
        {
            return new ClipboardContent([new GeoNode("n1") { Geometry = coords }], ClipboardFormat.Coordinates, null, null, [], 1);
        }
        return null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static readonly Regex WktStart = new(@"^\s*(SRID=\d+;\s*)?(POINT|LINESTRING|POLYGON|MULTIPOINT|MULTILINESTRING|MULTIPOLYGON|GEOMETRYCOLLECTION)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SridPrefix = new(@"^\s*SRID=\d+;\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>WKT / EWKT，一行一个几何（也接受整段只有一个几何、中间有换行的情况）。</summary>
    private static List<Geometry>? TryParseWkt(string text)
    {
        if (!WktStart.IsMatch(text)) return null;
        var reader = new WKTReader(NtsGeometryServices.Instance);
        var f = Geometries.Factory;

        Geometry? ReadOne(string s)
        {
            try
            {
                var g = reader.Read(SridPrefix.Replace(s, ""));
                if (g == null || g.IsEmpty) return null;
                var copy = f.CreateGeometry(g);
                return GeoNode.KindOf(copy) == NodeKind.Group ? null : copy;
            }
            catch (Exception)
            {
                return null;
            }
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1 && lines.All(l => WktStart.IsMatch(l)))
        {
            var list = lines.Select(ReadOne).OfType<Geometry>().ToList();
            if (list.Count == lines.Length) return list;
        }
        return ReadOne(text) is { } single ? [single] : null;
    }

    private static readonly Regex NumberPattern = new(@"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant);

    /// <summary>
    /// 经纬度文本：每行一对数字（逗号、空格、制表符分隔都行）。看起来是“纬度, 经度”顺序时自动交换
    /// （第一个数在 ±90 以内而第二个超出，例如从地图网站复制的“39.9, 116.4”）。
    /// </summary>
    private static Geometry? TryParseCoordinates(string text)
    {
        var lines = text.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Length > 100000) return null;
        var pairs = new List<(double A, double B)>();
        foreach (var line in lines)
        {
            // 行里除了数字只能有分隔符和括号
            var rest = NumberPattern.Replace(line, "");
            if (rest.Any(ch => !(ch is ',' or ' ' or '\t' or '(' or ')' or '[' or ']' or '，' or '\r'))) return null;
            var nums = NumberPattern.Matches(line).Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
            if (nums.Count != 2) return null;
            pairs.Add((nums[0], nums[1]));
        }

        bool latLon = pairs.All(p => Math.Abs(p.A) <= 90 && Math.Abs(p.B) <= 180) && pairs.Any(p => Math.Abs(p.B) > 90);
        var coords = pairs.Select(p => latLon ? new Coordinate(p.B, p.A) : new Coordinate(p.A, p.B)).ToArray();
        if (coords.Any(c => Math.Abs(c.X) > 180 || Math.Abs(c.Y) > 90)) return null;

        var f = Geometries.Factory;
        if (coords.Length == 1) return f.CreatePoint(coords[0]);
        if (coords.Length >= 4 && coords[0].Equals2D(coords[^1]))
        {
            var poly = f.CreatePolygon(coords);
            return poly.IsValid ? poly : Geometries.PolygonalPart(GeometryOps.Clean(poly));
        }
        return f.CreateLineString(coords);
    }

    /// <summary>把一批节点的几何从一个坐标系换到另一个（WGS-84 ↔ GCJ-02），替换成新的几何对象。</summary>
    public static void ConvertCrs(IEnumerable<GeoNode> roots, CoordSystem from, CoordSystem to)
    {
        if (from == to) return;
        var filter = new OffsetFilter(from, to);
        foreach (var n in roots.SelectMany(r => r.SelfAndDescendants()))
        {
            if (n.Geometry == null) continue;
            var g = n.Geometry.Copy();
            g.Apply(filter);
            g.GeometryChanged();
            n.Geometry = g;
        }
    }

    private sealed class OffsetFilter(CoordSystem from, CoordSystem to) : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            var (x, y) = ChinaOffset.Convert(seq.GetX(i), seq.GetY(i), from, to);
            seq.SetX(i, x);
            seq.SetY(i, y);
        }
    }
}
