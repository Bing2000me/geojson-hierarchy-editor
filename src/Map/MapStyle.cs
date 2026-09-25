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

    /// <summary>
    /// 节点的实际颜色：有显式颜色用显式颜色，否则自动配色。
    /// 自动配色按“同族同色”：最上层按 id 取调色板；下级区域（面、分组）是上级颜色的深浅变化（与 CK3 里附庸领地的配色相同），
    /// 所以缩小时合并成上级、放大展开下级，整体色调不变；区域里的点和线用上级颜色加深。
    /// </summary>
    public static SKColor ColorOf(GeoNode node)
    {
        if (node.Color != null) return Parse(node.Color);
        var parent = node.Parent;
        if (parent == null) return Parse(Palette[RootIndex(node)]);
        int index = 0;
        bool areal = IsArealKind(node.Kind);
        foreach (var s in parent.Children)
        {
            if (ReferenceEquals(s, node)) break;
            if (areal ? IsArealKind(s.Kind) : s.Kind == node.Kind) index++;
        }
        return ChildColor(ColorOf(parent), parent.Kind, node.Kind, index, node.Depth);
    }

    public static string AutoColorHex(GeoNode node)
    {
        var c = ColorOf(node);
        return $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
    }

    private static bool IsArealKind(NodeKind kind) => kind is NodeKind.Polygon or NodeKind.Group;

    /// <summary>下级的自动配色：<paramref name="index"/> 是它在同级同类里的序号。</summary>
    private static SKColor ChildColor(SKColor parent, NodeKind parentKind, NodeKind kind, int index, int depth)
    {
        if (!IsArealKind(parentKind)) return Parse(Palette[PaletteIndex(index, depth)]);
        return IsArealKind(kind) ? Tint(parent, index, depth) : Darken(parent, 0.3f);
    }

    /// <summary>
    /// 同一上级下第 <paramref name="index"/> 个下级区域的颜色：在上级颜色上按 0、+1、−1、+2、−2、+3、−3 的顺序
    /// 改变明度（并略微偏转色相），相邻序号一深一浅，整体仍是上级的色调。
    /// </summary>
    public static SKColor Tint(SKColor parent, int index, int depth)
    {
        int k = index % 7;
        int step = (k + 1) / 2 * (k % 2 == 1 ? 1 : -1);
        if (step == 0) return parent;
        parent.ToHsl(out float h, out float s, out float l);
        // 变浅的幅度大、变深的幅度小（深色半透明地铺在底图上会发灰发脏）；越往下级变化越小
        float dl = depth <= 1 ? 7f : 4.5f;
        float nl = l + (step > 0 ? step * dl : step * dl * 0.7f);
        nl = Math.Clamp(nl, 34, 84);
        float nh = (h + step * 2.5f + 360) % 360;
        return SKColor.FromHsl(nh, s, nl).WithAlpha(parent.Alpha);
    }

    /// <summary>自动配色在调色板里的位置：同级同类中的序号，再按层级深度错开。</summary>
    public static int PaletteIndex(int siblingIndex, int depth) => (siblingIndex + depth * 5) % Palette.Length;

    /// <summary>根级节点没有“同级序号”的稳定含义，用 id 的哈希取色。</summary>
    public static int RootIndex(GeoNode node) => Math.Abs(StableHash(node.Id) % Palette.Length);

    /// <summary>
    /// 一次算出某个上级（null 为根级）全部下级的颜色，写入 <paramref name="cache"/>（上级的颜色要先在缓存里）。
    /// 地图每帧都要取色，逐个节点去数同级序号在下级很多时是平方复杂度。
    /// </summary>
    public static void FillAutoColors(GeoNode? parent, IReadOnlyList<GeoNode> siblings, Dictionary<GeoNode, SKColor> cache)
    {
        if (siblings.Count == 0) return;
        if (parent == null)
        {
            foreach (var s in siblings) cache[s] = s.Color != null ? Parse(s.Color) : Parse(Palette[RootIndex(s)]);
            return;
        }
        if (!cache.TryGetValue(parent, out var parentColor))
        {
            parentColor = ColorOf(parent);
            cache[parent] = parentColor;
        }
        int depth = parent.Depth + 1;
        Span<int> counters = stackalloc int[4];
        foreach (var s in siblings)
        {
            int slot = IsArealKind(s.Kind) ? (int)NodeKind.Group : (int)s.Kind;
            int index = counters[slot]++;
            cache[s] = s.Color != null ? Parse(s.Color) : ChildColor(parentColor, parent.Kind, s.Kind, index, depth);
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
