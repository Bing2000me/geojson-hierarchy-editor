using System.Globalization;

using GeoJsonEditor.Model;

using SkiaSharp;

namespace GeoJsonEditor.Map;

/// <summary>地图配色、调色板与字体。</summary>
public static class MapStyle
{
    /// <summary>自动配色与颜色面板共用的调色板。</summary>
    public static readonly string[] Palette =
    [
        "#3B82F6", "#F59E0B", "#10B981", "#EF4444", "#8B5CF6", "#06B6D4",
        "#EC4899", "#84CC16", "#F97316", "#6366F1", "#14B8A6", "#A855F7",
    ];

    public static readonly SKColor Accent = new(0x25, 0x63, 0xEB);
    public static readonly SKColor AccentSoft = new(0x25, 0x63, 0xEB, 0x33);
    public static readonly SKColor Danger = new(0xDC, 0x26, 0x26);
    public static readonly SKColor LabelInk = new(0x1F, 0x29, 0x37);
    public static readonly SKColor LabelHalo = new(0xFF, 0xFF, 0xFF, 0xE6);

    public static SKColor Parse(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        return new SKColor(0x3B, 0x82, 0xF6);
    }

    /// <summary>节点的实际颜色：有显式颜色用显式颜色，否则按在同级中的位置和层级深度取调色板。</summary>
    public static SKColor ColorOf(GeoNode node)
    {
        if (node.Color != null) return Parse(node.Color);
        return Parse(AutoColorHex(node));
    }

    public static string AutoColorHex(GeoNode node)
    {
        var siblings = node.Parent?.Children;
        int index = 0;
        if (siblings != null)
        {
            int k = 0;
            foreach (var s in siblings)
            {
                if (ReferenceEquals(s, node)) { index = k; break; }
                if (s.Kind == node.Kind) k++;
            }
        }
        else
        {
            index = RootIndex(node);
        }
        return Palette[PaletteIndex(index, node.Depth)];
    }

    /// <summary>自动配色在调色板里的位置：同级同类中的序号，再按层级深度错开。</summary>
    public static int PaletteIndex(int siblingIndex, int depth) => (siblingIndex + depth * 5) % Palette.Length;

    /// <summary>根级节点没有“同级序号”的稳定含义，用 id 的哈希取色。</summary>
    public static int RootIndex(GeoNode node) => Math.Abs(StableHash(node.Id) % Palette.Length);

    /// <summary>
    /// 一次算出某个上级（null 为根级）全部下级的自动配色，写入 <paramref name="cache"/>。
    /// 地图每帧都要取色，逐个节点去数同级序号在下级很多时是平方复杂度。
    /// </summary>
    public static void FillAutoColors(GeoNode? parent, IReadOnlyList<GeoNode> siblings, Dictionary<GeoNode, SKColor> cache)
    {
        if (siblings.Count == 0) return;
        int depth = parent == null ? 0 : parent.Depth + 1;
        Span<int> counters = stackalloc int[4];
        foreach (var s in siblings)
        {
            int index = parent == null ? RootIndex(s) : counters[(int)s.Kind]++;
            cache[s] = Parse(Palette[PaletteIndex(index, depth)]);
        }
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    public static SKColor Darken(SKColor c, float amount)
    {
        c.ToHsl(out float h, out float s, out float l);
        return SKColor.FromHsl(h, s, Math.Max(0, l * (1 - amount))).WithAlpha(c.Alpha);
    }

    public static SKColor Lighten(SKColor c, float amount)
    {
        c.ToHsl(out float h, out float s, out float l);
        return SKColor.FromHsl(h, s, Math.Min(100, l + (100 - l) * amount)).WithAlpha(c.Alpha);
    }

    // ───────────────────────── 字体 ─────────────────────────

    private static SKTypeface? _regular;
    private static SKTypeface? _bold;

    /// <summary>能显示中文的系统字体（macOS 上是苹方，Windows 上是微软雅黑）。</summary>
    public static SKTypeface Regular => _regular ??= FindCjk(SKFontStyle.Normal);

    public static SKTypeface Bold => _bold ??= FindCjk(SKFontStyle.Bold);

    private static SKTypeface FindCjk(SKFontStyle style)
    {
        string[] preferred = OperatingSystem.IsWindows()
            ? ["Microsoft YaHei UI", "Microsoft YaHei", "SimHei"]
            : OperatingSystem.IsMacOS()
                ? ["PingFang SC", "Hiragino Sans GB", "Heiti SC"]
                : ["Noto Sans CJK SC", "Source Han Sans SC", "WenQuanYi Micro Hei"];
        var fm = SKFontManager.Default;
        foreach (var family in preferred)
        {
            var tf = fm.MatchFamily(family, style);
            if (tf != null && tf.ContainsGlyph('中')) return tf;
        }
        return fm.MatchCharacter(null, style, null, '中') ?? SKTypeface.Default;
    }
}
