using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeoJsonEditor.Model;

/// <summary>
/// 从级别文字和常见属性推断要素的行政层级，数值越小层级越高：
/// 0 国家 / 国都，1 省 / 路 / 道，2 市 / 州 / 府，3 县 / 区，4 乡镇，5 村。认不出时返回 null。
/// 点聚合用它挑选代表点、决定标注字号；自动识别层级用它排除不合理的上下级（下级的层级不能比上级高）。
/// </summary>
public static class LevelTiers
{
    // 按顺序匹配，先匹配到的为准：长词、有歧义的词放前面（“市辖区”先于“市”，“街道”先于“道”）
    private static readonly (string Word, int Tier)[] Keywords =
    [
        ("首都", 0), ("国都", 0), ("都城", 0), ("京师", 0), ("行在", 0), ("capital", 0), ("country", 0), ("国家", 0), ("政权", 0), ("朝代", 0),
        ("特别行政区", 1), ("自治区", 1), ("直辖市", 1), ("province", 1), ("circuit", 1), ("state", 1), ("省", 1),
        ("地级", 2), ("地区", 2), ("自治州", 2), ("prefecture", 2), ("盟", 2),
        ("市辖区", 3), ("自治县", 3), ("county", 3), ("district", 3), ("县", 3), ("旗", 3),
        ("街道", 4), ("乡", 4), ("镇", 4), ("town", 4), ("suburb", 4),
        ("村", 5), ("社区", 5), ("village", 5), ("hamlet", 5), ("locality", 5),
        ("city", 2), ("路", 1), ("道", 1), ("府", 2), ("州", 2), ("军", 2), ("监", 2), ("厅", 2), ("市", 2), ("区", 3),
    ];

    /// <summary>点要素的类别字段（治所类型、OSM 的 place 等），比级别字段更能说明点的重要程度。</summary>
    private static readonly string[] PointTypeKeys = ["settlement_type", "place", "fclass", "featurecla", "marker-symbol"];

    /// <summary>数值型的行政级别字段（越小越高），比级别文字更可靠。</summary>
    private static readonly string[] LevelNumberKeys = ["admin_level", "adm_level", "admin_lvl"];

    /// <summary>数值型的重要度字段（越小越重要），只在没有别的级别信息时用。</summary>
    private static readonly string[] RankKeys = ["scalerank", "labelrank", "rank"];

    /// <summary>文字型的级别字段。</summary>
    private static readonly string[] TextKeys = ["admin_type", "level", "type", "class", "category", "级别", "行政级别", "等级", "类型", "类别"];

    private static readonly string[] PopulationKeys = ["population", "pop", "pop_max", "POP", "POP_MAX", "total_population", "人口"];

    public static int? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 24) return null;
        foreach (var (word, tier) in Keywords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return tier;
        }
        return null;
    }

    /// <summary>要素的层级：点先看类别字段，然后是级别文字、数值级别字段、其他文字字段。</summary>
    public static int? Of(GeoNode node)
    {
        var extra = node.Extra;
        if (extra.Count > 0 && extra.TryGetValue("capital", out var cap) && IsTruthy(cap)) return 0;
        if (node.Kind == NodeKind.Point)
        {
            foreach (var key in PointTypeKeys)
            {
                if (FromText(TextOf(extra, key)) is int t) return t;
            }
        }
        foreach (var key in LevelNumberKeys)
        {
            if (NumberOf(extra, key) is double v && v >= 0 && v < 20) return (int)v;
        }
        if (FromText(node.Level) is int lt) return lt;
        foreach (var key in TextKeys)
        {
            if (FromText(TextOf(extra, key)) is int t) return t;
        }
        foreach (var key in RankKeys)
        {
            if (NumberOf(extra, key) is double v && v >= 0 && v < 20) return (int)v;
        }
        return null;
    }

    /// <summary>
    /// 点的排序值（越小越重要）：层级为主，人口多的靠前，没有名称的排到最后。认不出层级的点排在县和乡镇之间。
    /// </summary>
    public static float PointRank(GeoNode node) => PointRankAndTier(node).Rank;

    public static (float Rank, int? Tier) PointRankAndTier(GeoNode node)
    {
        int? tier = Of(node);
        double rank = tier ?? 3.5;
        double pop = 0;
        foreach (var key in PopulationKeys)
        {
            if (NumberOf(node.Extra, key) is double v && v > pop) pop = v;
        }
        if (pop > 0) rank -= Math.Min(0.45, Math.Log10(pop + 1) / 20);
        if (string.IsNullOrWhiteSpace(node.Name)) rank += 10;
        return ((float)rank, tier);
    }

    public static string? TextOf(IReadOnlyDictionary<string, JsonNode?> extra, string key)
    {
        if (extra.Count == 0 || !extra.TryGetValue(key, out var v) || v is not JsonValue value) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        };
    }

    public static double? NumberOf(IReadOnlyDictionary<string, JsonNode?> extra, string key)
    {
        if (extra.Count == 0 || !extra.TryGetValue(key, out var v) || v is not JsonValue value) return null;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                return double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            case JsonValueKind.String:
                return double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
            default:
                return null;
        }
    }

    private static bool IsTruthy(JsonNode? v) => v is JsonValue value && value.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.ToJsonString() != "0",
        JsonValueKind.String => value.GetValue<string>() is "1" or "true" or "yes" or "Y" or "是",
        _ => false,
    };
}
