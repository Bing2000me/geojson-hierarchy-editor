using System.Diagnostics;

using GeoJsonEditor.Geo;
using GeoJsonEditor.Model;

using SkiaSharp;

namespace GeoJsonEditor.Map;

public sealed partial class MapCanvas
{
    /// <summary>视野内的点标记超过这个数时不画阴影。</summary>
    private const int MarkerShadowLimit = 400;

    /// <summary>视野内的点标记超过这个数时改画小圆点（选中和悬停的仍画完整图标）。</summary>
    private const int MarkerDotLimit = 3000;

    /// <summary>每帧计算新标注位置的时间上限，超出的留到下一帧。</summary>
    private const double LabelBudgetMs = 18;

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _tilePaint = new() { IsAntialias = false };
    private readonly SKPaint _text = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _halo = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _shadow = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = new SKColor(0, 0, 0, 70),
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.6f),
    };
    private readonly SKPath _path = new();
    private readonly List<SKPath> _pathPool = new();
    private int _pathPoolUsed;
    private SKPoint[] _points = new SKPoint[1024];
    private readonly SKSamplingOptions _sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private SKColorFilter? _grayFilter;
    private readonly Dictionary<(float Size, bool Bold), SKFont> _fonts = new();
    private readonly Dictionary<GeoNode, SKColor> _colors = new(ReferenceEqualityComparer.Instance);
    private readonly LabelGrid _labelGrid = new();

    // 区域名称占的位置（小圆点和它们重叠时不画）；放区域名称期间为 true
    private readonly LabelGrid _regionLabelGrid = new();
    private bool _placingRegionLabels;

    // 上一帧视野内的要素，命中测试和吸附只在这里面找（文档改动后到下一次绘制之前不用它）
    private List<VisibleItem> _lastVisible = new();
    private long _lastVisibleStamp = -1;
    private long _docStamp;

    // 层级检查结果和切割预览的投影
    private IReadOnlyList<NetTopologySuite.Geometries.Geometry>? _highlightSource;
    private List<ProjectedShape> _highlightShapes = new();
    private List<ProjectedShape>? _cutPreviewShapes;

    private SKColor MapBackground => _isDarkTheme ? new SKColor(0x1B, 0x1F, 0x27) : new SKColor(0xEE, 0xF1, 0xF5);

    private bool DarkBase => _editor.BaseMap.Value.IsBlank ? _isDarkTheme : _editor.BaseMap.Value.IsDark;

    private int Level => Lod.LevelForZoom(_vp.Zoom);

    private void OnPaint(SKCanvas canvas, SKImageInfo info)
    {
        SyncSize();
        if (!_hasView && _vp.Width > 50 && _vp.Height > 50)
        {
            _hasView = true;
            ZoomToAll();
        }

        float scale = (float)(info.Width / Math.Max(1, _vp.Width));
        canvas.Clear(MapBackground);
        canvas.Save();
        canvas.Scale(scale);

        _tiles.BeginFrame();
        DrawTiles(canvas, scale);
        DrawFade(canvas);

        _pathPoolUsed = 0;
        var frame = BuildFrame();
        _frame = frame;
        var visible = CollectVisible(frame);
        _lastVisible = visible;
        _lastVisibleStamp = _docStamp;

        if (!DrawFeatureLayerCached(canvas, scale, frame)) DrawRegionLayer(canvas, frame);
        DrawSelection(canvas);
        DrawVertexHandles(canvas);
        // 先画图钉和簇，再放区域名称（避开图钉和簇），然后画小圆点（和区域名称重叠的让位），最后放地名
        bool labels = _editor.ShowLabels.Value;
        DrawPoints(canvas, visible);
        if (labels) DrawRegionLabels(canvas, frame);
        DrawPendingDots(canvas, labels);
        if (labels) DrawPointLabels(canvas, visible);
        DrawClusterHoverCard(canvas);
        DrawToolOverlay(canvas);
        DrawVertexHint(canvas);
        DrawScaleAndAttribution(canvas);

        canvas.Restore();
        TrimPathPool();
    }

    // ───────────────────────── 底图 ─────────────────────────

    private void DrawTiles(SKCanvas canvas, float scale)
    {
        var source = _editor.BaseMap.Value;
        if (source.IsBlank) return;

        if (_editor.BaseMapGray.Value)
        {
            _grayFilter ??= SKColorFilter.CreateColorMatrix(
            [
                0.30f, 0.59f, 0.11f, 0, 0,
                0.30f, 0.59f, 0.11f, 0, 0,
                0.30f, 0.59f, 0.11f, 0, 0,
                0, 0, 0, 1, 0,
            ]);
            _tilePaint.ColorFilter = _grayFilter;
        }
        else
        {
            _tilePaint.ColorFilter = null;
        }

        int tz = (int)Math.Clamp(Math.Round(_vp.Zoom), source.MinZoom, source.MaxZoom);
        int n = 1 << tz;
        var (minX, minY, maxX, maxY) = _vp.VisibleWorld();
        int x0 = (int)Math.Floor(minX * n), x1 = (int)Math.Floor(maxX * n);
        int y0 = Math.Max(0, (int)Math.Floor(minY * n)), y1 = Math.Min(n - 1, (int)Math.Floor(maxY * n));
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 600) return;

        // 先请求视野中心附近的瓦片（后请求的先下载，所以按距离从远到近请求）
        double cxTile = _vp.CenterX * n, cyTile = _vp.CenterY * n;
        var order = new List<(int X, int Y, double D)>();
        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                double d = (tx + 0.5 - cxTile) * (tx + 0.5 - cxTile) + (ty + 0.5 - cyTile) * (ty + 0.5 - cyTile);
                order.Add((tx, ty, d));
            }
        }
        order.Sort((a, b) => b.D.CompareTo(a.D));

        for (int layer = 0; layer < source.Layers.Length; layer++)
        {
            var tileLayer = source.Layers[layer];
            foreach (var (tx, ty, _) in order)
            {
                int wx = ((tx % n) + n) % n;
                var dest = TileRect(tx, ty, n, scale);
                var key = new TileKey(source.Id, layer, tz, wx, ty);
                var img = _tiles.Get(key, tileLayer.BuildUrl(wx, ty, tz));
                if (img != null)
                {
                    canvas.DrawImage(img, new SKRect(0, 0, img.Width, img.Height), dest, _sampling, _tilePaint);
                    continue;
                }

                // 还没下载到：用已缓存的上级瓦片放大顶替，缩放时画面不会闪白
                for (int dz = 1; dz <= 6 && tz - dz >= 0; dz++)
                {
                    int pz = tz - dz;
                    var parent = _tiles.Peek(new TileKey(source.Id, layer, pz, wx >> dz, ty >> dz));
                    if (parent == null) continue;
                    int sub = 1 << dz;
                    float sw = parent.Width / (float)sub, sh = parent.Height / (float)sub;
                    float sx = (wx % sub) * sw, sy = (ty % sub) * sh;
                    canvas.DrawImage(parent, new SKRect(sx, sy, sx + sw, sy + sh), dest, _sampling, _tilePaint);
                    break;
                }
            }
        }
    }

    private SKRect TileRect(int tx, int ty, int n, float scale)
    {
        var (sx0, sy0) = _vp.WorldToScreen((double)tx / n, (double)ty / n);
        var (sx1, sy1) = _vp.WorldToScreen((double)(tx + 1) / n, (double)(ty + 1) / n);
        // 对齐到物理像素，避免相邻瓦片之间出现细缝
        static float Snap(double v, float s) => (float)(Math.Round(v * s) / s);
        return new SKRect(Snap(sx0, scale), Snap(sy0, scale), Snap(sx1, scale), Snap(sy1, scale));
    }

    private void DrawFade(SKCanvas canvas)
    {
        double fade = _editor.BaseMapFade.Value;
        if (fade <= 0.001 || _editor.BaseMap.Value.IsBlank) return;
        var baseColor = DarkBase ? new SKColor(0x10, 0x14, 0x1C) : new SKColor(0xFF, 0xFF, 0xFF);
        _fill.Color = baseColor.WithAlpha((byte)(Math.Clamp(fade, 0, 0.9) * 255));
        _fill.Shader = null;
        canvas.DrawRect(0, 0, (float)_vp.Width, (float)_vp.Height, _fill);
    }

    // ───────────────────────── 要素 ─────────────────────────

    private readonly record struct VisibleItem(GeoNode Node, ProjectedShape Shape, int Depth);

    /// <summary>
    /// 这一帧可以点中、吸附的要素：显示着的区域（有自身几何的）、显示着的线，以及视野内的全部点
    /// （点的聚合在绘制时再做）。
    /// </summary>
    private List<VisibleItem> CollectVisible(DisplayFrame frame)
    {
        var list = new List<VisibleItem>(frame.Units.Count + frame.Lines.Count + 256);
        foreach (var u in frame.Units)
        {
            if (u.Region.HasOwnShape && u.Shape != null) list.Add(new VisibleItem(u.Region.Node, u.Shape, u.Region.Depth));
        }
        foreach (var l in frame.Lines)
        {
            if (l.Alpha >= 0.15f) list.Add(new VisibleItem(l.Node, l.Shape, l.Region?.Depth + 1 ?? 0));
        }
        var (minX, minY, maxX, maxY) = _vp.VisibleWorld();
        double pad = 40 / _vp.WorldSize;
        foreach (var root in Doc.Roots) CollectPoints(root, 0, list, minX - pad, minY - pad, maxX + pad, maxY + pad);
        return list;
    }

    private void CollectPoints(GeoNode node, int depth, List<VisibleItem> list, double minX, double minY, double maxX, double maxY)
    {
        if (!node.Visible) return;
        if (node.Kind == NodeKind.Point && _shapes.Get(node, _vp) is { } shape && shape.Intersects(minX, minY, maxX, maxY))
        {
            list.Add(new VisibleItem(node, shape, depth));
        }
        foreach (var c in node.Children) CollectPoints(c, depth + 1, list, minX, minY, maxX, maxY);
    }

    /// <summary>命中测试和吸附的候选：上一帧视野内的要素；文档刚改过、还没重绘时退回遍历全部。</summary>
    private IEnumerable<(GeoNode Node, ProjectedShape Shape)> Candidates()
    {
        if (_lastVisibleStamp == _docStamp)
        {
            foreach (var v in _lastVisible) yield return (v.Node, v.Shape);
            yield break;
        }
        foreach (var n in Doc.AllNodes())
        {
            if (n.Geometry == null || !n.IsEffectivelyVisible) continue;
            var s = _shapes.Get(n, _vp);
            if (s != null) yield return (n, s);
        }
    }

    private SKColor ColorOf(GeoNode node)
    {
        if (node.Color != null) return MapStyle.Parse(node.Color);
        if (_colors.TryGetValue(node, out var c)) return c;
        MapStyle.FillAutoColors(node.Parent, node.Parent?.Children ?? Doc.Roots, _colors);
        return _colors.TryGetValue(node, out c) ? c : MapStyle.ColorOf(node);
    }

    private SKPath RentPath()
    {
        SKPath path;
        if (_pathPoolUsed < _pathPool.Count)
        {
            path = _pathPool[_pathPoolUsed];
            path.Reset();
        }
        else
        {
            path = new SKPath();
            _pathPool.Add(path);
        }
        _pathPoolUsed++;
        return path;
    }

    /// <summary>路径池只保留最近一帧用得上的数量，多出的释放掉。</summary>
    private void TrimPathPool()
    {
        int keep = Math.Max(_pathPoolUsed, 64);
        if (_pathPool.Count <= keep * 2) return;
        for (int i = keep; i < _pathPool.Count; i++) _pathPool[i].Dispose();
        _pathPool.RemoveRange(keep, _pathPool.Count - keep);
    }

    /// <summary>
    /// 把世界坐标路径转成屏幕坐标加进 <paramref name="path"/>。输入已经按缩放级别简化过，
    /// 这里再去掉屏幕上挨得太近的点，然后整段一次交给 Skia。
    /// </summary>
    private void AppendPaths(SKPath path, double[]?[] paths, bool close)
    {
        double s = _vp.WorldSize, cx = _vp.CenterX, cy = _vp.CenterY, hw = _vp.Width / 2, hh = _vp.Height / 2;
        foreach (var p in paths)
        {
            if (p == null) continue;
            int n = p.Length / 2;
            if (n < 2) continue;
            if (_points.Length < n) _points = new SKPoint[Math.Max(n, _points.Length * 2)];
            int m = 0;
            float lastX = 0, lastY = 0;
            for (int i = 0; i < n; i++)
            {
                float x = (float)((p[i * 2] - cx) * s + hw);
                float y = (float)((p[i * 2 + 1] - cy) * s + hh);
                if (i > 0 && i < n - 1 && MathF.Abs(x - lastX) + MathF.Abs(y - lastY) < 0.4f) continue;
                _points[m++] = new SKPoint(x, y);
                lastX = x;
                lastY = y;
            }
            if (m >= 2) path.AddPoly(new ReadOnlySpan<SKPoint>(_points, 0, m), close);
        }
    }

    private void DrawSelection(SKCanvas canvas)
    {
        int level = Level;

        // 悬停（缩小时是合并后的上级区域）
        if (_hover != null && !Doc.IsSelected(_hover) && _hover.IsEffectivelyVisible && _hover.Kind != NodeKind.Point && _drag == DragMode.None)
        {
            var s = ShapeOf(_hover);
            if (s != null)
            {
                _path.Reset();
                _path.FillType = SKPathFillType.EvenOdd;
                AppendPaths(_path, s.PathsAt(level), s.Kind == NodeKind.Polygon);
                if (s.Kind == NodeKind.Polygon)
                {
                    _fill.Color = MapStyle.Accent.WithAlpha(22);
                    canvas.DrawPath(_path, _fill);
                }
                _stroke.Color = MapStyle.Accent.WithAlpha(170);
                _stroke.StrokeWidth = s.Kind == NodeKind.Line ? 4 : 2;
                canvas.DrawPath(_path, _stroke);
            }
        }

        var (minX, minY, maxX, maxY) = _vp.VisibleWorld();
        foreach (var node in Doc.Selection)
        {
            if (!node.IsEffectivelyVisible) continue;
            // 分组：画由下级拼出的边界；还没算好时高亮它的全部下级
            IEnumerable<GeoNode> targets = node.Geometry != null || ShapeOf(node) != null
                ? [node]
                : node.Descendants().Where(d => d.Geometry != null && d.IsEffectivelyVisible);
            foreach (var t in targets)
            {
                var s = ShapeOf(t);
                if (s == null || s.Kind == NodeKind.Point || !s.Intersects(minX, minY, maxX, maxY)) continue;
                _path.Reset();
                _path.FillType = SKPathFillType.EvenOdd;
                AppendPaths(_path, s.PathsAt(level), s.Kind == NodeKind.Polygon);
                if (_path.IsEmpty) continue;
                if (s.Kind == NodeKind.Polygon)
                {
                    _fill.Color = MapStyle.Accent.WithAlpha(40);
                    canvas.DrawPath(_path, _fill);
                }
                _stroke.Color = new SKColor(255, 255, 255, 230);
                _stroke.StrokeWidth = s.Kind == NodeKind.Line ? 8 : 5.5f;
                canvas.DrawPath(_path, _stroke);
                _stroke.Color = MapStyle.Accent;
                _stroke.StrokeWidth = s.Kind == NodeKind.Line ? 4 : 2.6f;
                canvas.DrawPath(_path, _stroke);
            }
        }
    }

    // ───────────────────────── 点标记 ─────────────────────────

    /// <summary>画一个点标记。(x, y) 是锚点：图钉和旗帜在底端，其余在中心。</summary>
    private void DrawMarker(SKCanvas canvas, MarkerIcon icon, SKColor color, float x, float y, bool selected, bool hover, bool shadow)
    {
        var (path, center) = MarkerShape(icon);
        float cx = x + center.X, cy = y + center.Y;
        if (selected || hover)
        {
            _fill.Color = MapStyle.Accent.WithAlpha(selected ? (byte)60 : (byte)35);
            canvas.DrawCircle(cx, cy, 17, _fill);
            if (selected)
            {
                _stroke.Color = MapStyle.Accent;
                _stroke.StrokeWidth = 2;
                canvas.DrawCircle(cx, cy, 17, _stroke);
            }
        }

        canvas.Save();
        canvas.Translate(x, y);
        if (shadow)
        {
            canvas.Translate(0, 1.5f);
            canvas.DrawPath(path, _shadow);
            canvas.Translate(0, -1.5f);
        }
        _fill.Color = color;
        canvas.DrawPath(path, _fill);
        _stroke.Color = SKColors.White;
        _stroke.StrokeWidth = 1.8f;
        canvas.DrawPath(path, _stroke);
        canvas.Restore();

        if (icon == MarkerIcon.Pin)
        {
            _fill.Color = SKColors.White;
            canvas.DrawCircle(cx, cy, 3.4f, _fill);
        }
    }

    private static readonly Dictionary<MarkerIcon, (SKPath Path, SKPoint Center)> MarkerShapes = new();

    /// <summary>以锚点为原点的标记形状，按图标缓存。</summary>
    private static (SKPath Path, SKPoint Center) MarkerShape(MarkerIcon icon)
    {
        if (MarkerShapes.TryGetValue(icon, out var cached)) return cached;
        var p = new SKPath();
        SKPoint center;
        switch (icon)
        {
            case MarkerIcon.Pin:
            {
                float r = 9.5f;
                float cy = -20;
                center = new SKPoint(0, cy);
                // 圆头 + 尖脚的水滴形
                double a = Math.Asin(r / 20.0 * 0.9);
                float tx = (float)(r * Math.Cos(a));
                float ty = (float)(r * Math.Sin(a));
                p.MoveTo(0, 0);
                p.LineTo(-tx, cy + ty);
                p.ArcTo(new SKRect(-r, cy - r, r, cy + r), 180 - (float)(a * 180 / Math.PI), 180 + 2 * (float)(a * 180 / Math.PI), false);
                p.LineTo(0, 0);
                p.Close();
                break;
            }
            case MarkerIcon.Circle:
                center = new SKPoint(0, 0);
                p.AddCircle(0, 0, 8);
                break;
            case MarkerIcon.Square:
                center = new SKPoint(0, 0);
                p.AddRoundRect(new SKRect(-7.5f, -7.5f, 7.5f, 7.5f), 3, 3);
                break;
            case MarkerIcon.Triangle:
                center = new SKPoint(0, 0);
                p.MoveTo(0, -10);
                p.LineTo(9.5f, 7);
                p.LineTo(-9.5f, 7);
                p.Close();
                break;
            case MarkerIcon.Star:
            {
                center = new SKPoint(0, 0);
                for (int i = 0; i < 10; i++)
                {
                    double ang = -Math.PI / 2 + i * Math.PI / 5;
                    float rr = i % 2 == 0 ? 11f : 4.8f;
                    float px = (float)(rr * Math.Cos(ang));
                    float py = (float)(rr * Math.Sin(ang)) + 1;
                    if (i == 0) p.MoveTo(px, py);
                    else p.LineTo(px, py);
                }
                p.Close();
                break;
            }
            default: // Flag：旗杆 + 飘带形旗面，锚点在旗杆底端
                center = new SKPoint(8, -21);
                p.MoveTo(-1.6f, 1);
                p.LineTo(-1.6f, -29);
                p.LineTo(1.6f, -29);
                p.CubicTo(6, -31.5f, 11, -26.5f, 18, -28.5f);
                p.LineTo(18, -15.5f);
                p.CubicTo(11, -13.5f, 6, -18.5f, 1.6f, -16);
                p.LineTo(1.6f, 1);
                p.Close();
                break;
        }
        MarkerShapes[icon] = (p, center);
        return (p, center);
    }

    // ───────────────────────── 标注 ─────────────────────────

    /// <summary>
    /// 标注，按重要度依次占位，放不下的不画（与常见地图软件的做法一致）：
    /// 选中要素 → 上两级区域 → 点和簇（按层级、人口排序，每个试右、左、上、下四个位置）→ 更深层级的区域 → 线。
    /// 点标记和簇本身也占位，后放的标注不会压住它们。
    /// </summary>
    /// <summary>区域名称：选中点的名称先占位，图钉和簇也先占位；然后浅的层级先放，同一层里大的先放。</summary>
    private void DrawRegionLabels(SKCanvas canvas, DisplayFrame frame)
    {
        _labelGrid.Clear();
        _regionLabelGrid.Clear();
        bool dark = DarkBase;
        var ink = dark ? new SKColor(0xF8, 0xFA, 0xFC) : MapStyle.LabelInk;
        var halo = dark ? new SKColor(0x0B, 0x10, 0x18, 0xD0) : MapStyle.LabelHalo;
        long start = Stopwatch.GetTimestamp();
        bool pending = false;

        foreach (var label in _pointLabels)
        {
            if (label.Force) PlacePointLabel(canvas, label, ink, halo);
        }
        foreach (var m in _shownMarkers)
        {
            var (x, y) = _vp.WorldToScreen(m.X, m.Y);
            _labelGrid.Add(m.Compact ? DotHitRect(x, y, m.Tier) : MarkerHitRect(m.Node.Icon, x, y));
        }
        foreach (var c in _shownClusters)
        {
            var (x, y) = _vp.WorldToScreen(c.X, c.Y);
            float r = c.Radius + 2;
            _labelGrid.Add(new SKRect((float)x - r, (float)y - r, (float)x + r, (float)y + r));
        }

        var regions = new List<UnitItem>();
        foreach (var u in frame.Units)
        {
            if (u.Shape != null && u.LabelAlpha >= 0.05f && !string.IsNullOrWhiteSpace(u.Region.Node.Name)) regions.Add(u);
        }
        regions.Sort((a, b) => a.Region.Depth != b.Region.Depth
            ? a.Region.Depth.CompareTo(b.Region.Depth)
            : b.Shape!.LabelRadiusBound.CompareTo(a.Shape!.LabelRadiusBound));
        _placingRegionLabels = true;
        try
        {
            foreach (var u in regions) pending |= !TryRegionLabel(canvas, u, ink, halo, start);
        }
        finally
        {
            _placingRegionLabels = false;
        }

        // 还有标注位置没算完：下一帧继续
        if (pending) QueueRedraw();
    }

    /// <summary>地名（点和簇的名称，按层级、人口排序）和线的名称，放不下的不画。</summary>
    private void DrawPointLabels(SKCanvas canvas, List<VisibleItem> visible)
    {
        bool dark = DarkBase;
        var ink = dark ? new SKColor(0xF8, 0xFA, 0xFC) : MapStyle.LabelInk;
        var halo = dark ? new SKColor(0x0B, 0x10, 0x18, 0xD0) : MapStyle.LabelHalo;
        if (_pointLabels.Count > 0)
        {
            var order = new List<PointLabel>(_pointLabels.Count);
            foreach (var label in _pointLabels)
            {
                if (!label.Force) order.Add(label);
            }
            order.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : b.Count.CompareTo(a.Count));
            foreach (var label in order) PlacePointLabel(canvas, label, ink, halo);
        }

        double s = _vp.WorldSize;
        foreach (var (node, shape, _) in visible)
        {
            if (shape.Kind != NodeKind.Line || string.IsNullOrWhiteSpace(node.Name)) continue;
            if (shape.Extent * s < 40 && !Doc.IsSelected(node)) continue;
            var (lx, ly, _) = shape.Label;
            var (x, y) = _vp.WorldToScreen(lx, ly);
            TryDrawLabel(canvas, node.Name, (float)x, (float)y - 8, 12, false, SKTextAlign.Center, MapStyle.Darken(ColorOf(node), 0.35f), halo, Doc.IsSelected(node));
        }
    }

    /// <summary>
    /// 点或簇的名称：依次试右、左、上、下四个位置，第一个不与已有标注和标记重叠的位置胜出；都不行就不画
    /// （选中的点除外，放在右边）。字号按层级：都城、省级最大，乡镇、村最小。
    /// </summary>
    private void PlacePointLabel(SKCanvas canvas, in PointLabel label, SKColor ink, SKColor halo)
    {
        var (size, bold) = label.Tier switch
        {
            0 => (13.5f, true),
            1 => (13f, true),
            2 => (12.5f, true),
            3 => (12f, false),
            4 or 5 => (11.5f, false),
            _ => (12.5f, false),
        };
        var font = Font(size, bold);
        float w = font.MeasureText(label.Text);
        float x = label.X, y = label.Y;

        // 标记占的范围（与 MarkerHitRect 一致，相对锚点）和可见中心：图钉、旗帜的锚点在底端
        float left, right, top, bottom, cx = x, cy = y;
        if (label.Radius > 0)
        {
            left = top = -label.Radius - 2;
            right = bottom = label.Radius + 2;
        }
        else
        {
            var r = MarkerHitRect(label.Icon, 0, 0);
            (left, right, top, bottom) = (r.Left, r.Right, r.Top, r.Bottom);
            if (label.Icon == MarkerIcon.Pin) cy = y - 20;
            else if (label.Icon == MarkerIcon.Flag) (cx, cy) = (x + 8, y - 21);
        }
        float mid = cy + size * 0.35f;

        Span<(float X, float Baseline, SKTextAlign Align)> candidates =
        [
            (x + right + 4, mid, SKTextAlign.Left),
            (x + left - 4, mid, SKTextAlign.Right),
            (cx, y + top - 2 - size * 0.3f, SKTextAlign.Center),
            (cx, y + bottom + 2 + size, SKTextAlign.Center),
        ];
        foreach (var (lx, baseline, align) in candidates)
        {
            if (TryDrawLabel(canvas, label.Text, lx, baseline, size, bold, align, ink, halo, false, w)) return;
        }
        if (label.Force)
        {
            var (fx, fb, fa) = candidates[0];
            TryDrawLabel(canvas, label.Text, fx, fb, size, bold, fa, ink, halo, true, w);
        }
    }

    private SKFont Font(float size, bool bold)
    {
        if (_fonts.TryGetValue((size, bold), out var font)) return font;
        font = new SKFont(bold ? MapStyle.Bold : MapStyle.Regular, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        _fonts[(size, bold)] = font;
        return font;
    }

    private bool TryDrawLabel(SKCanvas canvas, string text, float x, float baselineY, float size, bool bold, SKTextAlign align, SKColor ink, SKColor halo, bool force, float width = -1)
    {
        var font = Font(size, bold);
        float w = width >= 0 ? width : font.MeasureText(text);
        float left = align switch
        {
            SKTextAlign.Center => x - w / 2,
            SKTextAlign.Right => x - w,
            _ => x,
        };
        var rect = new SKRect(left - 3, baselineY - size - 1, left + w + 3, baselineY + size * 0.3f + 1);
        if (rect.Right < 0 || rect.Left > _vp.Width || rect.Bottom < 0 || rect.Top > _vp.Height) return false;
        if (!force && _labelGrid.Collides(rect)) return false;
        _labelGrid.Add(rect);
        if (_placingRegionLabels) _regionLabelGrid.Add(rect);
        _halo.Color = halo;
        _halo.StrokeWidth = 3.2f;
        canvas.DrawText(text, x, baselineY, align, font, _halo);
        _text.Color = ink;
        canvas.DrawText(text, x, baselineY, align, font, _text);
        return true;
    }

    /// <summary>标注占位的网格索引：碰撞检测只看附近格子里的标注，标注再多也是线性开销。</summary>
    private sealed class LabelGrid
    {
        private const float Cell = 96;
        private readonly Dictionary<long, List<SKRect>> _cells = new();

        public void Clear()
        {
            foreach (var list in _cells.Values) list.Clear();
        }

        private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        public bool Collides(SKRect r)
        {
            int x0 = (int)MathF.Floor(r.Left / Cell), x1 = (int)MathF.Floor(r.Right / Cell);
            int y0 = (int)MathF.Floor(r.Top / Cell), y1 = (int)MathF.Floor(r.Bottom / Cell);
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cy = y0; cy <= y1; cy++)
                {
                    if (!_cells.TryGetValue(Key(cx, cy), out var list)) continue;
                    foreach (var other in list)
                    {
                        if (other.IntersectsWith(r)) return true;
                    }
                }
            }
            return false;
        }

        public void Add(SKRect r)
        {
            int x0 = (int)MathF.Floor(r.Left / Cell), x1 = (int)MathF.Floor(r.Right / Cell);
            int y0 = (int)MathF.Floor(r.Top / Cell), y1 = (int)MathF.Floor(r.Bottom / Cell);
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cy = y0; cy <= y1; cy++)
                {
                    long key = Key(cx, cy);
                    if (!_cells.TryGetValue(key, out var list))
                    {
                        list = new List<SKRect>();
                        _cells[key] = list;
                    }
                    list.Add(r);
                }
            }
        }
    }

    // ───────────────────────── 比例尺与版权 ─────────────────────────

    private void DrawScaleAndAttribution(SKCanvas canvas)
    {
        float h = (float)_vp.Height, w = (float)_vp.Width;
        var (text, px) = ScaleBar();
        var font = Font(11, false);
        float tw = font.MeasureText(text);
        float left = 12, bottom = h - 12;
        var bg = new SKRect(left, bottom - 22, left + (float)px + tw + 22, bottom);
        _fill.Color = _isDarkTheme ? new SKColor(0x1F, 0x24, 0x2E, 0xD8) : new SKColor(255, 255, 255, 0xD8);
        canvas.DrawRoundRect(bg, 6, 6, _fill);
        _stroke.Color = _isDarkTheme ? new SKColor(0xE5, 0xE7, 0xEB) : new SKColor(0x37, 0x41, 0x51);
        _stroke.StrokeWidth = 1.5f;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        float bx = left + 8, by = bottom - 8;
        canvas.DrawLine(bx, by, bx + (float)px, by, _stroke);
        canvas.DrawLine(bx, by - 5, bx, by, _stroke);
        canvas.DrawLine(bx + (float)px, by - 5, bx + (float)px, by, _stroke);
        _stroke.StrokeCap = SKStrokeCap.Round;
        _text.Color = _stroke.Color;
        canvas.DrawText(text, bx + (float)px + 6, by, SKTextAlign.Left, font, _text);

        var attribution = _editor.BaseMap.Value.Attribution;
        if (!string.IsNullOrEmpty(attribution))
        {
            var small = Font(10.5f, false);
            float aw = small.MeasureText(attribution);
            var abg = new SKRect(w - aw - 16, h - 20, w - 4, h - 4);
            _fill.Color = _isDarkTheme ? new SKColor(0x1F, 0x24, 0x2E, 0xB0) : new SKColor(255, 255, 255, 0xB8);
            canvas.DrawRoundRect(abg, 4, 4, _fill);
            _text.Color = _isDarkTheme ? new SKColor(0xCB, 0xD5, 0xE1) : new SKColor(0x47, 0x55, 0x69);
            canvas.DrawText(attribution, w - 10, h - 8.5f, SKTextAlign.Right, small, _text);
        }
    }

    // ───────────────────────── 要素图层缓存 ─────────────────────────
    //
    // 数据量大时，面和线先画到一张比视野大一圈的离屏图像上（与画布同一个 GPU 上下文）。
    // 平移时只要视野还在图像范围内就直接贴图；滚轮缩放过程中先把图像按比例缩放顶替，
    // 停下一小会儿再重画清晰的版本。选中、悬停、顶点手柄、点标记和标注仍然每帧实时绘制。

    private const double LayerMargin = 0.2;
    private const int ZoomSettleMs = 140;

    private SKSurface? _layerSurface;
    private SKImage? _layerImage;
    private object? _layerContext;
    private long _layerDocStamp = -1;
    private long _layerStyleStamp = -1;
    private long _layerRegionVersion = -1;
    private float _layerScale;
    private double _layerZoom;
    private double _layerMinX, _layerMinY, _layerMaxX, _layerMaxY;
    private long _styleStamp;
    private long _zoomGestureTick;
    private bool _layerRefreshPending;

    /// <summary>视野内的面和线多到值得缓存时才用（小文档每帧直接画更简单也更清晰）。</summary>
    private static bool IsHeavy(DisplayFrame frame)
        => frame.Units.Count + frame.Lines.Count >= 300 || frame.VertexCount >= 150_000;

    private bool DrawFeatureLayerCached(SKCanvas canvas, float scale, DisplayFrame frame)
    {
        if (_drag is DragMode.Vertex or DragMode.MovePoint || !IsHeavy(frame))
        {
            DropLayerCache();
            return false;
        }

        object? context = canvas.Context;
        bool valid = _layerImage != null
                     && ReferenceEquals(_layerContext, context)
                     && _layerDocStamp == _docStamp
                     && _layerRegionVersion == _regionVersion
                     && _layerStyleStamp == _styleStamp
                     && _layerScale == scale;
        var (vx0, vy0, vx1, vy1) = _vp.VisibleWorld();
        bool sameZoom = Math.Abs(_layerZoom - _vp.Zoom) < 1e-9;
        bool covers = vx0 >= _layerMinX && vy0 >= _layerMinY && vx1 <= _layerMaxX && vy1 <= _layerMaxY;

        if (valid && sameZoom && covers)
        {
            BlitLayer(canvas, scale);
            return true;
        }
        if (valid && Environment.TickCount64 - _zoomGestureTick < ZoomSettleMs)
        {
            // 滚轮缩放进行中：先用缩放后的旧图顶替，停下后再重画
            BlitLayer(canvas, scale);
            ScheduleLayerRefresh();
            return true;
        }
        if (!RenderLayer(canvas, scale, context)) return false;
        BlitLayer(canvas, scale);
        return true;
    }

    private bool RenderLayer(SKCanvas canvas, float scale, object? context)
    {
        double w = _vp.Width * (1 + 2 * LayerMargin), h = _vp.Height * (1 + 2 * LayerMargin);
        int pw = (int)Math.Ceiling(w * scale), ph = (int)Math.Ceiling(h * scale);
        if ((long)pw * ph > 48_000_000) return false;

        if (_layerSurface == null || _layerSurface.Canvas.DeviceClipBounds.Width != pw || _layerSurface.Canvas.DeviceClipBounds.Height != ph || !ReferenceEquals(_layerContext, context))
        {
            DropLayerCache();
            var info = new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul);
            _layerSurface = canvas.Context is GRRecordingContext gpu ? SKSurface.Create(gpu, true, info) : SKSurface.Create(info);
            if (_layerSurface == null) return false;
            _layerContext = context;
        }

        // 先释放旧快照，重画时 Skia 就不必为了保留它而复制整张图
        _layerImage?.Dispose();
        _layerImage = null;
        var oc = _layerSurface.Canvas;
        oc.Clear(SKColors.Transparent);
        oc.Save();
        oc.Scale(scale);
        double ow = _vp.Width, oh = _vp.Height;
        _vp.Width = w;
        _vp.Height = h;
        try
        {
            DrawRegionLayer(oc, BuildFrame());
            (_layerMinX, _layerMinY, _layerMaxX, _layerMaxY) = _vp.VisibleWorld();
        }
        finally
        {
            _vp.Width = ow;
            _vp.Height = oh;
        }
        oc.Restore();

        _layerImage = _layerSurface.Snapshot();
        _layerZoom = _vp.Zoom;
        _layerScale = scale;
        _layerDocStamp = _docStamp;
        _layerRegionVersion = _regionVersion;
        _layerStyleStamp = _styleStamp;
        return true;
    }

    private void BlitLayer(SKCanvas canvas, float scale)
    {
        if (_layerImage == null) return;
        var (x0, y0) = _vp.WorldToScreen(_layerMinX, _layerMinY);
        var (x1, y1) = _vp.WorldToScreen(_layerMaxX, _layerMaxY);
        // 同一缩放级别时对齐到物理像素，贴图不会发虚
        static float Snap(double v, float s) => (float)(Math.Round(v * s) / s);
        var dest = Math.Abs(_layerZoom - _vp.Zoom) < 1e-9
            ? new SKRect(Snap(x0, scale), Snap(y0, scale), Snap(x0, scale) + _layerImage.Width / scale, Snap(y0, scale) + _layerImage.Height / scale)
            : new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
        canvas.DrawImage(_layerImage, dest, _sampling);
    }

    private async void ScheduleLayerRefresh()
    {
        if (_layerRefreshPending) return;
        _layerRefreshPending = true;
        try
        {
            await Task.Delay(ZoomSettleMs + 30);
        }
        finally
        {
            _layerRefreshPending = false;
        }
        InvalidateVisual();
    }

    private void DropLayerCache()
    {
        _layerImage?.Dispose();
        _layerImage = null;
        _layerSurface?.Dispose();
        _layerSurface = null;
        _layerContext = null;
    }

    private void DisposeRenderResources()
    {
        DropLayerCache();
        foreach (var p in _pathPool) p.Dispose();
        _pathPool.Clear();
        foreach (var f in _fonts.Values) f.Dispose();
        _fonts.Clear();
    }
}
