using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using GeoJsonEditor.Geo;
using GeoJsonEditor.Model;

using NetTopologySuite.Geometries;

namespace GeoJsonEditor.IO;

/// <param name="Meta">FeatureCollection 上的 geojsonEditor 字段（本程序复制到剪贴板时写入），没有时为 null。</param>
/// <param name="HasHierarchyFields">是否有要素带 parentId（本程序保存的层级格式）。</param>
public sealed record ReadResult(List<GeoNode> Roots, int FeatureCount, int LinkedCount, List<string> Warnings, JsonObject? Meta = null, bool HasHierarchyFields = false);

/// <summary>
/// 带层级的 GeoJSON 读写。层级写在 properties 里：id / parentId。
/// 读取时兼容阿里云 DataV 行政区数据（adcode + parent.adcode）和 simplestyle 的颜色字段。
/// 读取用 <see cref="JsonDocument"/> 直接取坐标，不为每个数字建对象；写出是流式的，大文件也不需要拼接整段字符串。
/// </summary>
public static class GeoJsonIO
{
    /// <summary>本程序在 FeatureCollection 上写的扩展字段名（剪贴板里带上来源、坐标系等信息）。</summary>
    public const string MetaKey = "geojsonEditor";

    private static readonly HashSet<string> ConsumedKeys = new(StringComparer.Ordinal)
    {
        "id", "parentId", "name", "level", "note", "color", "icon", "hidden", "kind",
    };

    // 没有 name、level、id 时依次尝试的字段（这些字段不从属性里拿走，原样保留）
    private static readonly string[] NameKeys = ["title", "NAME", "Name", "name_zh", "name_cn", "NAME_CHN", "NAME_ZH", "名称", "地名", "name_en"];
    private static readonly string[] LevelKeys = ["admin_type", "级别", "行政级别", "level_name"];
    private static readonly string[] IdKeys = ["adcode", "feature_id", "featureId"];

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    // ───────────────────────── 读取 ─────────────────────────

    public static ReadResult ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var doc = JsonDocument.Parse(stream, DocOptions);
        return Read(doc.RootElement);
    }

    public static ReadResult Read(string json)
    {
        using var doc = JsonDocument.Parse(json.AsMemory().TrimStart('﻿'), DocOptions);
        return Read(doc.RootElement);
    }

    public static ReadResult Read(JsonElement root)
    {
        var warnings = new List<string>();
        var features = new List<JsonElement>();
        JsonObject? meta = null;

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                // 要素或几何的数组（有些工具复制出来的是这种形式）
                foreach (var e in root.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object) features.Add(e);
                }
                break;
            case JsonValueKind.Object:
                switch (TypeOf(root))
                {
                    case "FeatureCollection":
                        if (root.TryGetProperty("features", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in arr.EnumerateArray())
                            {
                                if (f.ValueKind == JsonValueKind.Object) features.Add(f);
                            }
                        }
                        if (root.TryGetProperty(MetaKey, out var m) && m.ValueKind == JsonValueKind.Object)
                        {
                            meta = ToNode(m) as JsonObject;
                        }
                        break;
                    case null:
                        throw new FormatException("不是 GeoJSON：缺少 type 字段。");
                    default:
                        features.Add(root);
                        break;
                }
                break;
            default:
                throw new FormatException("不是 GeoJSON。");
        }

        var items = new List<(GeoNode Node, string? ParentKey)>(features.Count);
        var byId = new Dictionary<string, GeoNode>(StringComparer.Ordinal);
        var sources = new Dictionary<SourceInfo, SourceInfo>();
        int autoId = 0;
        int badGeometry = 0;
        bool hasHierarchy = false;

        foreach (var f in features)
        {
            // 要素，或者裸几何对象
            bool isFeature = TypeOf(f) == "Feature";
            JsonElement? props = isFeature && f.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : null;
            JsonElement? geometry = isFeature ? (f.TryGetProperty("geometry", out var g) ? g : null) : f;

            var fields = SourceFields.None;
            JsonNode? featureId = null;
            string? featureIdText = null;
            if (isFeature && f.TryGetProperty("id", out var fid) && fid.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                featureId = ToNode(fid);
                featureIdText = ScalarText(fid);
            }
            string? id = Prop(props, "id");
            if (id != null) fields |= SourceFields.Id;
            id ??= featureIdText;
            foreach (var key in IdKeys)
            {
                id ??= Prop(props, key);
            }
            if (string.IsNullOrEmpty(id) || byId.ContainsKey(id))
            {
                do { id = "f" + (++autoId).ToString(CultureInfo.InvariantCulture); } while (byId.ContainsKey(id));
            }

            var node = new GeoNode(id);
            try
            {
                node.Geometry = geometry is { } ge ? ReadGeometry(ge) : null;
            }
            catch (Exception)
            {
                badGeometry++;
                node.Geometry = null;
            }

            string? parentKey = Prop(props, "parentId");
            if (parentKey != null)
            {
                fields |= SourceFields.ParentId;
                hasHierarchy = true;
            }
            if (parentKey == null && props is { } pp && pp.TryGetProperty("parent", out var parent))
            {
                parentKey = parent.ValueKind == JsonValueKind.Object
                    ? (parent.TryGetProperty("adcode", out var pa) ? ScalarText(pa) : null) ?? (parent.TryGetProperty("id", out var pi) ? ScalarText(pi) : null)
                    : ScalarText(parent);
            }

            if (props is { } pr)
            {
                string? nameKey = null, levelKey = null, noteKey = null, colorKey = null, levelRaw = null;

                if (Prop(pr, "name") is { } name)
                {
                    node.Name = name;
                    fields |= SourceFields.Name;
                }
                else
                {
                    foreach (var key in NameKeys)
                    {
                        if (Prop(pr, key) is not { Length: > 0 } alt) continue;
                        node.Name = alt;
                        nameKey = key;
                        break;
                    }
                }

                if (Prop(pr, "level") is { } level)
                {
                    node.Level = MapLevel(level);
                    fields |= SourceFields.Level;
                    if (node.Level != level) levelRaw = level;
                }
                else
                {
                    foreach (var key in LevelKeys)
                    {
                        if (Prop(pr, key) is not { Length: > 0 and <= 24 } alt) continue;
                        node.Level = alt;
                        levelKey = key;
                        break;
                    }
                }

                if (Prop(pr, "note") is { } note)
                {
                    node.Note = note;
                    fields |= SourceFields.Note;
                }
                else if (Prop(pr, "description") is { } description)
                {
                    node.Note = description;
                    noteKey = "description";
                }

                if (pr.TryGetProperty("color", out _))
                {
                    node.Color = NormalizeColor(Prop(pr, "color"));
                    fields |= SourceFields.Color;
                }
                else
                {
                    string styleKey = node.Kind switch
                    {
                        NodeKind.Point => "marker-color",
                        NodeKind.Line => "stroke",
                        _ => "fill",
                    };
                    if (NormalizeColor(Prop(pr, styleKey)) is { } styled)
                    {
                        node.Color = styled;
                        colorKey = styleKey;
                    }
                }

                if (pr.TryGetProperty("icon", out _)) fields |= SourceFields.Icon;
                if (Prop(pr, "icon") is string icon && Enum.TryParse<MarkerIcon>(icon, true, out var mi))
                {
                    node.Icon = mi;
                }
                if (pr.TryGetProperty("hidden", out var hv))
                {
                    fields |= SourceFields.Hidden;
                    node.Visible = hv.ValueKind != JsonValueKind.True;
                }

                var info = new SourceInfo(fields, nameKey, levelKey, noteKey, colorKey, levelRaw, featureId);
                if (featureId == null)
                {
                    // 同一份文件里大多数要素的字段来源相同，共用一个对象
                    if (sources.TryGetValue(info, out var shared)) info = shared;
                    else sources[info] = info;
                }
                node.Source = info;

                Dictionary<string, JsonNode?>? extra = null;
                foreach (var kv in pr.EnumerateObject())
                {
                    if (ConsumedKeys.Contains(kv.Name)) continue;
                    extra ??= new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                    extra[kv.Name] = ToNode(kv.Value);
                }
                if (extra != null) node.Extra = extra;
            }

            else
            {
                node.Source = new SourceInfo(fields, FeatureId: featureId);
            }

            byId[id] = node;
            items.Add((node, parentKey));
        }

        var roots = new List<GeoNode>();
        int linked = 0;
        foreach (var (node, parentKey) in items)
        {
            if (parentKey != null && byId.TryGetValue(parentKey, out var parent) && !ReferenceEquals(parent, node) && !WouldCycle(node, parent))
            {
                node.Parent = parent;
                parent.ChildList.Add(node);
                linked++;
            }
            else
            {
                roots.Add(node);
            }
        }

        if (badGeometry > 0) warnings.Add($"{badGeometry} 个要素的几何无法解析，已作为分组读入。");
        return new ReadResult(roots, features.Count, linked, warnings, meta, hasHierarchy);
    }

    private static string? TypeOf(JsonElement o)
        => o.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static bool WouldCycle(GeoNode node, GeoNode parent)
    {
        for (var p = parent; p != null; p = p.Parent)
        {
            if (ReferenceEquals(p, node)) return true;
        }
        return false;
    }

    private static string MapLevel(string? level) => level switch
    {
        null => "",
        "country" => "国家",
        "province" => "省",
        "city" => "市",
        "district" => "区县",
        "street" => "乡镇",
        _ => level,
    };

    private static string? Prop(JsonElement? obj, string name)
        => obj is { } o && o.TryGetProperty(name, out var v) ? ScalarText(v) : null;

    private static string? ScalarText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>把属性值转成独立的 JsonNode（不依赖已释放的 JsonDocument）。数字保留原文，写回时不变形。</summary>
    private static JsonNode? ToNode(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return JsonValue.Create(e.GetString());
            case JsonValueKind.Number:
                return JsonValue.Create(e.Clone());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Object:
            {
                var o = new JsonObject();
                foreach (var kv in e.EnumerateObject()) o[kv.Name] = ToNode(kv.Value);
                return o;
            }
            case JsonValueKind.Array:
            {
                var a = new JsonArray();
                foreach (var item in e.EnumerateArray()) a.Add(ToNode(item));
                return a;
            }
            default:
                return null;
        }
    }

    public static string? NormalizeColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.StartsWith('#')) return null;
        var hex = text[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return null;
        return "#" + hex.ToUpperInvariant();
    }

    private static Geometry? ReadGeometry(JsonElement g)
    {
        if (g.ValueKind != JsonValueKind.Object) return null;
        var f = Geometries.Factory;
        g.TryGetProperty("coordinates", out var c);
        switch (TypeOf(g))
        {
            case "Point":
                return f.CreatePoint(ReadCoordinate(c));
            case "MultiPoint":
            {
                var pts = ReadCoordinates(c);
                return pts.Length == 0 ? null : f.CreateMultiPointFromCoords(pts);
            }
            case "LineString":
            {
                var coords = ReadCoordinates(c);
                return coords.Length >= 2 ? f.CreateLineString(coords) : null;
            }
            case "MultiLineString":
            {
                var lines = new List<LineString>();
                if (c.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in c.EnumerateArray())
                    {
                        var coords = ReadCoordinates(part);
                        if (coords.Length >= 2) lines.Add(f.CreateLineString(coords));
                    }
                }
                return lines.Count == 0 ? null : f.CreateMultiLineString(lines.ToArray());
            }
            case "Polygon":
                return ReadPolygon(c);
            case "MultiPolygon":
            {
                var polys = new List<Polygon>();
                if (c.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in c.EnumerateArray())
                    {
                        if (ReadPolygon(part) is { } poly) polys.Add(poly);
                    }
                }
                return polys.Count == 0 ? null : f.CreateMultiPolygon(polys.ToArray());
            }
            case "GeometryCollection":
            {
                var parts = new List<Geometry>();
                if (g.TryGetProperty("geometries", out var geoms) && geoms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in geoms.EnumerateArray())
                    {
                        if (ReadGeometry(part) is { } pg) parts.Add(pg);
                    }
                }
                return parts.Count == 0 ? null : f.CreateGeometryCollection(parts.ToArray());
            }
            default:
                return null;
        }
    }

    private static Polygon? ReadPolygon(JsonElement rings)
    {
        if (rings.ValueKind != JsonValueKind.Array || rings.GetArrayLength() == 0) return null;
        LinearRing? shell = null;
        var holes = new List<LinearRing>();
        foreach (var r in rings.EnumerateArray())
        {
            var ring = ReadRing(r);
            if (shell == null)
            {
                if (ring == null) return null;
                shell = ring;
            }
            else if (ring != null)
            {
                holes.Add(ring);
            }
        }
        return shell == null ? null : Geometries.Factory.CreatePolygon(shell, holes.ToArray());
    }

    private static LinearRing? ReadRing(JsonElement ring)
    {
        var coords = ReadCoordinates(ring);
        if (coords.Length < 3) return null;
        if (!coords[0].Equals2D(coords[^1]))
        {
            Array.Resize(ref coords, coords.Length + 1);
            coords[^1] = coords[0].Copy();
        }
        if (coords.Length < 4) return null;
        return Geometries.Factory.CreateLinearRing(coords);
    }

    private static Coordinate[] ReadCoordinates(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return [];
        var result = new Coordinate[arr.GetArrayLength()];
        int i = 0;
        foreach (var p in arr.EnumerateArray()) result[i++] = ReadCoordinate(p);
        return result;
    }

    private static Coordinate ReadCoordinate(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) throw new FormatException("坐标格式不正确。");
        var e = p.EnumerateArray();
        e.MoveNext();
        double x = e.Current.GetDouble();
        e.MoveNext();
        double y = e.Current.GetDouble();
        return new Coordinate(x, y);
    }

    // ───────────────────────── 写出 ─────────────────────────

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>写成 FeatureCollection 字符串，每个要素一行，上级在前。</summary>
    public static string Write(IEnumerable<GeoNode> roots, JsonObject? meta = null, bool hierarchy = true)
    {
        using var ms = new MemoryStream();
        WriteTo(ms, roots, meta, hierarchy);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <param name="hierarchy">
    /// true：写成本程序的层级格式（每个要素带 id、parentId、name 等）；
    /// false：保持原有字段，只写回源文件里本来就有的字段（和程序里新填的名称、颜色等），不加层级字段。
    /// </param>
    public static void WriteFile(string path, IEnumerable<GeoNode> roots, bool hierarchy = true)
    {
        var temp = path + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            WriteTo(fs, roots, null, hierarchy);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// 流式写出：每个要素用同一个缓冲区单独序列化后写进流，要素之间换行，便于阅读和版本比较。
    /// 只有上级也在输出范围内时才写 parentId。
    /// </summary>
    public static void WriteTo(Stream stream, IEnumerable<GeoNode> roots, JsonObject? meta = null, bool hierarchy = true)
    {
        var rootList = roots.ToList();
        var included = new HashSet<GeoNode>(rootList.SelectMany(r => r.SelfAndDescendants()), ReferenceEqualityComparer.Instance);

        stream.Write("{\"type\":\"FeatureCollection\","u8);
        if (meta != null)
        {
            stream.Write("\""u8);
            stream.Write(Encoding.UTF8.GetBytes(MetaKey));
            stream.Write("\":"u8);
            using (var mw = new Utf8JsonWriter(stream, WriterOptions)) meta.WriteTo(mw);
            stream.Write(","u8);
        }
        stream.Write("\"features\":[\n"u8);

        var buffer = new ArrayBufferWriter<byte>(1 << 16);
        using var w = new Utf8JsonWriter(buffer, WriterOptions);
        bool first = true;
        foreach (var root in rootList)
        {
            foreach (var n in root.SelfAndDescendants())
            {
                if (!first) stream.Write(",\n"u8);
                first = false;
                buffer.ResetWrittenCount();
                w.Reset(buffer);
                var parentId = n.Parent != null && included.Contains(n.Parent) ? n.Parent.Id : null;
                if (hierarchy) WriteFeature(w, n, parentId);
                else WriteOriginalFeature(w, n, parentId);
                w.Flush();
                stream.Write(buffer.WrittenSpan);
            }
        }
        stream.Write("\n]}\n"u8);
    }

    private static void WriteFeature(Utf8JsonWriter w, GeoNode n, string? parentId)
    {
        w.WriteStartObject();
        w.WriteString("type", "Feature");
        w.WriteString("id", n.Id);
        w.WritePropertyName("properties");
        w.WriteStartObject();
        w.WriteString("id", n.Id);
        if (parentId != null) w.WriteString("parentId", parentId);
        w.WriteString("name", n.Name);
        if (!string.IsNullOrEmpty(n.Level)) w.WriteString("level", n.Level);
        if (!string.IsNullOrEmpty(n.Note)) w.WriteString("note", n.Note);
        if (n.Color != null) w.WriteString("color", n.Color);
        if (n.Kind == NodeKind.Point) w.WriteString("icon", n.Icon.ToString().ToLowerInvariant());
        if (!n.Visible) w.WriteBoolean("hidden", true);
        foreach (var kv in n.Extra)
        {
            w.WritePropertyName(kv.Key);
            if (kv.Value is null) w.WriteNullValue();
            else kv.Value.WriteTo(w);
        }
        w.WriteEndObject();
        w.WritePropertyName("geometry");
        WriteGeometry(w, n.Geometry);
        w.WriteEndObject();
    }

    /// <summary>
    /// 保持原有字段写出：只写源文件里本来就有的字段，名称、级别等改过的写回它们原来所在的字段，
    /// 不添加 id、parentId（源文件本来就有的除外）。程序里新建的要素写名称、级别等非空的字段。
    /// </summary>
    private static void WriteOriginalFeature(Utf8JsonWriter w, GeoNode n, string? parentId)
    {
        var src = n.Source;
        var fields = src?.Fields ?? SourceFields.None;
        w.WriteStartObject();
        w.WriteString("type", "Feature");
        if (src?.FeatureId is { } fid)
        {
            w.WritePropertyName("id");
            fid.WriteTo(w);
        }
        w.WritePropertyName("properties");
        w.WriteStartObject();
        if ((fields & SourceFields.Id) != 0) w.WriteString("id", n.Id);
        if ((fields & SourceFields.ParentId) != 0 && parentId != null) w.WriteString("parentId", parentId);

        // 改过的值写回原来所在的字段（在下面写 Extra 时替换）
        string? nameOverride = null, levelOverride = null, noteOverride = null;
        if ((fields & SourceFields.Name) != 0) w.WriteString("name", n.Name);
        else if (src?.NameKey is { } nk) nameOverride = ExtraText(n, nk) == n.Name ? null : n.Name;
        else if (n.Name.Length > 0) w.WriteString("name", n.Name);

        if ((fields & SourceFields.Level) != 0)
        {
            w.WriteString("level", src!.LevelRaw != null && MapLevel(src.LevelRaw) == n.Level ? src.LevelRaw : n.Level);
        }
        else if (src?.LevelKey is { } lk) levelOverride = ExtraText(n, lk) == n.Level ? null : n.Level;
        else if (n.Level.Length > 0) w.WriteString("level", n.Level);

        if ((fields & SourceFields.Note) != 0) w.WriteString("note", n.Note);
        else if (src?.NoteKey is { } ok) noteOverride = ExtraText(n, ok) == n.Note ? null : n.Note;
        else if (n.Note.Length > 0) w.WriteString("note", n.Note);

        if ((fields & SourceFields.Color) != 0 || (src?.ColorKey == null && n.Color != null))
        {
            if (n.Color != null) w.WriteString("color", n.Color);
        }
        if ((fields & SourceFields.Icon) != 0 || (n.Kind == NodeKind.Point && n.Icon != MarkerIcon.Pin))
        {
            w.WriteString("icon", n.Icon.ToString().ToLowerInvariant());
        }
        if ((fields & SourceFields.Hidden) != 0 || !n.Visible) w.WriteBoolean("hidden", !n.Visible);

        foreach (var kv in n.Extra)
        {
            string? replace = null;
            if (kv.Key == src?.NameKey) replace = nameOverride;
            else if (kv.Key == src?.LevelKey) replace = levelOverride;
            else if (kv.Key == src?.NoteKey) replace = noteOverride;
            else if (kv.Key == src?.ColorKey)
            {
                // 颜色改成自动时去掉原来的颜色字段；改过颜色时写回原字段
                if (n.Color == null) continue;
                if (NormalizeColor(ExtraText(n, kv.Key)) != n.Color) replace = n.Color;
            }
            w.WritePropertyName(kv.Key);
            if (replace != null) w.WriteStringValue(replace);
            else if (kv.Value is null) w.WriteNullValue();
            else kv.Value.WriteTo(w);
        }
        w.WriteEndObject();
        w.WritePropertyName("geometry");
        WriteGeometry(w, n.Geometry);
        w.WriteEndObject();
    }

    private static string? ExtraText(GeoNode n, string key)
        => n.Extra.TryGetValue(key, out var v) && v is JsonValue value && value.TryGetValue<string>(out var text) ? text : v?.ToJsonString();

    private static void WriteGeometry(Utf8JsonWriter w, Geometry? g)
    {
        if (g == null || g.IsEmpty)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        switch (g)
        {
            case Point p:
                w.WriteString("type", "Point");
                w.WritePropertyName("coordinates");
                WriteCoordinate(w, p.Coordinate);
                break;
            case MultiPoint mp:
                w.WriteString("type", "MultiPoint");
                w.WritePropertyName("coordinates");
                w.WriteStartArray();
                for (int i = 0; i < mp.NumGeometries; i++) WriteCoordinate(w, mp.GetGeometryN(i).Coordinate);
                w.WriteEndArray();
                break;
            case LineString ls:
                w.WriteString("type", "LineString");
                w.WritePropertyName("coordinates");
                WriteCoordinates(w, ls.Coordinates);
                break;
            case MultiLineString mls:
                w.WriteString("type", "MultiLineString");
                w.WritePropertyName("coordinates");
                w.WriteStartArray();
                for (int i = 0; i < mls.NumGeometries; i++) WriteCoordinates(w, ((LineString)mls.GetGeometryN(i)).Coordinates);
                w.WriteEndArray();
                break;
            case Polygon poly:
                w.WriteString("type", "Polygon");
                w.WritePropertyName("coordinates");
                WritePolygon(w, poly);
                break;
            case MultiPolygon mpoly:
                w.WriteString("type", "MultiPolygon");
                w.WritePropertyName("coordinates");
                w.WriteStartArray();
                for (int i = 0; i < mpoly.NumGeometries; i++) WritePolygon(w, (Polygon)mpoly.GetGeometryN(i));
                w.WriteEndArray();
                break;
            case GeometryCollection gc:
                w.WriteString("type", "GeometryCollection");
                w.WritePropertyName("geometries");
                w.WriteStartArray();
                for (int i = 0; i < gc.NumGeometries; i++) WriteGeometry(w, gc.GetGeometryN(i));
                w.WriteEndArray();
                break;
        }
        w.WriteEndObject();
    }

    private static void WritePolygon(Utf8JsonWriter w, Polygon p)
    {
        // GeoJSON（RFC 7946）要求外环逆时针、内环顺时针。
        w.WriteStartArray();
        WriteRing(w, p.Shell, counterClockwise: true);
        foreach (var hole in p.Holes) WriteRing(w, hole, counterClockwise: false);
        w.WriteEndArray();
    }

    private static void WriteRing(Utf8JsonWriter w, LinearRing ring, bool counterClockwise)
    {
        var coords = ring.Coordinates;
        bool isCcw = NetTopologySuite.Algorithm.Orientation.IsCCW(coords);
        w.WriteStartArray();
        if (isCcw == counterClockwise)
        {
            foreach (var c in coords) WriteCoordinate(w, c);
        }
        else
        {
            for (int i = coords.Length - 1; i >= 0; i--) WriteCoordinate(w, coords[i]);
        }
        w.WriteEndArray();
    }

    private static void WriteCoordinates(Utf8JsonWriter w, Coordinate[] coords)
    {
        w.WriteStartArray();
        foreach (var c in coords) WriteCoordinate(w, c);
        w.WriteEndArray();
    }

    private static void WriteCoordinate(Utf8JsonWriter w, Coordinate c)
    {
        w.WriteStartArray();
        w.WriteNumberValue(Math.Round(c.X, 7));
        w.WriteNumberValue(Math.Round(c.Y, 7));
        w.WriteEndArray();
    }
}
