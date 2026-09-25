using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace GeoJsonEditor.Ui;

/// <summary>线条图标（24×24 网格，圆头描边）。颜色默认继承所在控件的前景色，悬停、选中、禁用时自动跟着变。</summary>
public static class Icons
{
    public const string Select = "M4.04 3.52 10.6 20.3a.5.5 0 0 0 .94-.02l2.3-6.63a1 1 0 0 1 .62-.62l6.63-2.3a.5.5 0 0 0 .02-.94L4.36 3.24a.25.25 0 0 0-.32.28z";
    public const string Point = "M20 10c0 4.99-5.54 10.19-7.4 11.8a1 1 0 0 1-1.2 0C9.54 20.19 4 14.99 4 10a8 8 0 0 1 16 0z M9 10a3 3 0 1 0 6 0a3 3 0 1 0-6 0";
    public const string Line = "M3.5 18.5 9 11l5 4.5 6.5-9 M3.5 18.5m-1.5 0a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0 M20.5 6.5m-1.5 0a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0";
    public const string Polygon = "M12 3.2 20.6 9.4 17.3 19.6H6.7L3.4 9.4z";
    public const string Cut = "M6 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M20 4 8.12 15.88 M14.47 14.48 20 20 M8.12 8.12 12 12";
    public const string Merge = "M8 6l4-4 4 4 M12 2v10.3a4 4 0 0 1-1.17 2.87L4 22 M20 22l-5-5";
    public const string Undo = "M9 14 4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11";
    public const string Redo = "M15 14l5-5-5-5 M20 9H9.5a5.5 5.5 0 0 0 0 11H13";
    public const string Plus = "M5 12h14 M12 5v14";
    public const string Minus = "M5 12h14";
    public const string Fit = "M3 7V5a2 2 0 0 1 2-2h2 M17 3h2a2 2 0 0 1 2 2v2 M21 17v2a2 2 0 0 1-2 2h-2 M7 21H5a2 2 0 0 1-2-2v-2 M8 12h8 M12 8v8";
    public const string Layers = "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12 M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17";
    public const string Eye = "M2.06 12.35a1 1 0 0 1 0-.7 10.75 10.75 0 0 1 19.88 0 1 1 0 0 1 0 .7 10.75 10.75 0 0 1-19.88 0 M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0";
    public const string EyeOff = "M10.73 5.08A10.43 10.43 0 0 1 12 5c4.48 0 8.36 2.87 9.94 6.65a1 1 0 0 1 0 .7 10.8 10.8 0 0 1-1.44 2.49 M14.08 14.16a3 3 0 0 1-4.24-4.24 M17.48 17.5a10.75 10.75 0 0 1-15.42-5.15 1 1 0 0 1 0-.7 10.75 10.75 0 0 1 4.45-5.14 M2 2l20 20";
    public const string Trash = "M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2 M10 11v6 M14 11v6";
    public const string Folder = "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z";
    public const string FolderPlus = "M12 10v6 M9 13h6 M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z";
    public const string FolderOpen = "M6 14l1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2";
    public const string FilePlus = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z M14 2v4a2 2 0 0 0 2 2h4 M9 15h6 M12 18v-6";
    public const string Save = "M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M17 21v-7a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v7 M7 3v4a1 1 0 0 0 1 1h7";
    public const string Import = "M12 3v12 M7 10l5 5 5-5 M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4";
    public const string Export = "M12 15V3 M17 8l-5-5-5 5 M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4";
    public const string Search = "M3 11a8 8 0 1 0 16 0a8 8 0 1 0-16 0 M21 21l-4.3-4.3";
    public const string ChevronDown = "M6 9l6 6 6-6";
    public const string ChevronRight = "M9 18l6-6-6-6";
    public const string ArrowUp = "M12 19V5 M5 12l7-7 7 7";
    public const string ArrowDown = "M12 5v14 M19 12l-7 7-7-7";
    public const string Indent = "M3 8l4 4-4 4 M21 12H11 M21 6H11 M21 18H11";
    public const string Outdent = "M7 8l-4 4 4 4 M21 12H11 M21 6H11 M21 18H11";
    public const string Sun = "M8 12a4 4 0 1 0 8 0a4 4 0 1 0-8 0 M12 2v2 M12 20v2 M4.93 4.93l1.41 1.41 M17.66 17.66l1.41 1.41 M2 12h2 M20 12h2 M6.34 17.66l-1.41 1.41 M19.07 4.93l-1.41 1.41";
    public const string Moon = "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9z";
    public const string Map = "M14.1 5.55a2 2 0 0 0 1.8 0l3.65-1.83A1 1 0 0 1 21 4.62v12.76a1 1 0 0 1-.55.9l-4.56 2.27a2 2 0 0 1-1.78 0l-4.22-2.1a2 2 0 0 0-1.78 0l-3.66 1.83A1 1 0 0 1 3 19.38V6.62a1 1 0 0 1 .55-.9L8.1 3.45a2 2 0 0 1 1.8 0z M15 5.76v15 M9 3.24v15";
    public const string Target = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M22 12h-4 M6 12H2 M12 6V2 M12 22v-4";
    public const string Help = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3 M12 17h.01";
    public const string Magnet = "M6 15l-4-4 6.75-6.77a7.79 7.79 0 0 1 11 11L13 22l-4-4 6.39-6.36a2.14 2.14 0 0 0-3-3L6 15 M5 8l4 4 M12 15l4 4";
    public const string Link = "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71";
    public const string Label = "M4 7V4h16v3 M9 20h6 M12 4v16";
    public const string Crop = "M6 2v14a2 2 0 0 0 2 2h14 M18 22V8a2 2 0 0 0-2-2H2";
    public const string Rebuild = "M5 3a2 2 0 0 0-2 2 M19 3a2 2 0 0 1 2 2 M21 19a2 2 0 0 1-2 2 M5 21a2 2 0 0 1-2-2 M9 3h1 M9 21h1 M14 3h1 M14 21h1 M3 9v1 M21 9v1 M3 14v1 M21 14v1 M8 8h8v8H8z";
    public const string Close = "M18 6 6 18 M6 6l12 12";
    public const string Check = "M20 6 9 17l-5-5";
    public const string Info = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M12 16v-4 M12 8h.01";
    public const string Collapse = "M7 20l5-5 5 5 M7 4l5 5 5-5";
    public const string Expand = "M7 15l5 5 5-5 M7 9l5-5 5 5";
    public const string Hierarchy = "M3 3h6v6H3z M15 15h6v6h-6z M6 9v4a2 2 0 0 0 2 2h7 M15 9h6v6 M9 6h9a3 3 0 0 1 3 3";
    public const string More = "M5 12h.01 M12 12h.01 M19 12h.01";
    public const string Globe = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20 M2 12h20";
    public const string Keyboard = "M2 6a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2z M6 8h.01 M10 8h.01 M14 8h.01 M18 8h.01 M8 12h.01 M12 12h.01 M16 12h.01 M7 16h10";
    public const string Palette = "M12 22a10 10 0 1 1 10-10c0 2.5-2 3.5-3.5 3.5H16a2 2 0 0 0-1.5 3.3c.4.5.5 1 .5 1.4 0 1-.9 1.8-3 1.8z M13.5 6.5h.01 M17.5 10.5h.01 M8.5 7.5h.01 M6.5 12.5h.01";
    public const string Edit = "M12 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7 M18.4 2.6a2.1 2.1 0 0 1 3 3L12 15l-4 1 1-4z";
    public const string Copy = "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2z M4 16a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2";
    public const string Simplify = "M3 16 7.5 7l3.5 6 3-4 3 5 4-7 M3 21h18";
    public const string EditVertices = "M3.5 3.5h4v4h-4z M16.5 3.5h4v4h-4z M16.5 16.5h4v4h-4z M3.5 16.5h4v4h-4z M7.5 5.5h9 M18.5 7.5v9 M7.5 18.5h9 M5.5 7.5v9";
    public const string Cluster = "M8.5 12a3.5 3.5 0 1 0 7 0a3.5 3.5 0 1 0-7 0 M3 5a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M17 5a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M17 19a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M3 19a2 2 0 1 0 4 0a2 2 0 1 0-4 0";
    public const string Update = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M12 7.5v8 M8.5 12.5l3.5 3.5 3.5-3.5";
    public const string Crosshair = "M12 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0-6 0 M12 2v4 M12 18v4 M2 12h4 M18 12h4";

    // ── 实心形状（点标记图标选择器、树节点类别色块） ──
    public const string ShapePin = "M12 22s7-6.1 7-12a7 7 0 1 0-14 0c0 5.9 7 12 7 12z";
    public const string ShapeCircle = "M5 12a7 7 0 1 0 14 0a7 7 0 1 0-14 0";
    public const string ShapeSquare = "M6 5h12a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z";
    public const string ShapeTriangle = "M12 4 21 19H3z";
    public const string ShapeStar = "M12 2.5l2.9 6.2 6.6.7-4.9 4.5 1.4 6.6L12 17.1l-6 3.4 1.4-6.6L2.5 9.4l6.6-.7z";
    public const string ShapeFlag = "M5 21V4 M5 4h13l-3 4.5 3 4.5H5";
}

/// <summary>
/// 画一个 24×24 网格的矢量图标。未指定 <see cref="Tint"/> 时使用继承的前景色，
/// 所以放在按钮里会随按钮的悬停、选中、禁用状态自动变色。
/// </summary>
public sealed class IconView : TextElement
{
    private static readonly Dictionary<string, PathGeometry> Cache = new();
    private PathGeometry? _geometry;
    private string _data = "";

    public IconView()
    {
        IsHitTestVisible = false;
    }

    public IconView(string data, double size = 18) : this()
    {
        Data = data;
        IconSize = size;
    }

    public string Data
    {
        get => _data;
        set
        {
            _data = value ?? "";
            if (!Cache.TryGetValue(_data, out var g))
            {
                g = PathGeometry.Parse(_data);
                Cache[_data] = g;
            }
            _geometry = g;
            InvalidateVisual();
        }
    }

    public double IconSize { get; set; } = 18;

    public double StrokeWidth { get; set; } = 1.8;

    /// <summary>实心绘制（用于形状类图标）。</summary>
    public bool Filled { get; set; }

    /// <summary>固定颜色；为 null 时使用继承的前景色。</summary>
    public Color? Tint { get; set; }

    protected override Size MeasureContent(Size availableSize) => new(IconSize, IconSize);

    protected override void OnRender(IGraphicsContext context)
    {
        base.OnRender(context);
        if (_geometry == null) return;
        var b = Bounds;
        double scale = IconSize / 24.0;
        var color = Tint ?? Foreground;
        context.Save();
        context.Translate(b.X + (b.Width - IconSize) / 2, b.Y + (b.Height - IconSize) / 2);
        context.Scale(scale, scale);
        if (Filled)
        {
            context.FillPath(_geometry, color);
        }
        else
        {
            var pen = new Pen(color, StrokeWidth / scale, new StrokeStyle
            {
                LineCap = StrokeLineCap.Round,
                LineJoin = StrokeLineJoin.Round,
                MiterLimit = 10,
            });
            context.DrawPath(_geometry, pen);
        }
        context.Restore();
    }

    public void SetTint(Color? color)
    {
        Tint = color;
        InvalidateVisual();
    }
}
