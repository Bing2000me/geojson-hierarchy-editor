using System.Diagnostics;

using GeoJsonEditor.Model;

using SkiaSharp;

namespace GeoJsonEditor.Map;

/// <summary>
/// 面和线的绘制：按层级缩放显示（参照 P 社游戏的地图）。
/// <list type="bullet">
/// <item>缩小时下级合并成上级：只画上级的整块填充、边界和名称；放大到下级在屏幕上足够大时展开，逐级显示下级。</item>
/// <item>展开是渐变的：下级的颜色从上级的颜色过渡到自己的颜色，下级的边界和名称逐渐显现，上级的名称逐渐淡出，缩放时没有跳变。</item>
/// <item>填充画在同一个半透明图层里（图层内不透明），上下级重叠、相邻区域的接缝都不会叠出深浅不一。</item>
/// <item>边界按层级分粗细：深的先画，浅的（上级的）压在上面；同一层用同一种颜色，公共边界画两次也看不出来。</item>
/// </list>
/// 没有自身边界的分组用下级拼出的边界（后台计算，见 <see cref="DerivedShapes"/>），算好之前先用下级拼着画。
/// </summary>
public sealed partial class MapCanvas
{
    /// <summary>展开的渐变区间：展开尺度从阈值增长到阈值的 (1 + FadeRatio) 倍时渐变完成。</summary>
    private const double FadeRatio = 0.75;

    /// <summary>各层区域边界的基础粗细（DIP），按区域在区域树里的深度。</summary>
    private static readonly float[] BorderWidths = [1.9f, 1.25f, 0.9f, 0.72f, 0.62f];

    private RegionIndex _regions = RegionIndex.Empty;
    private long _regionStamp = -1;

    /// <summary>区域树每重建一次加一（要素图层缓存据此失效）。</summary>
    private long _regionVersion;
    private readonly DerivedShapes _derived = new();
    private bool _derivedRunning;
    private DisplayFrame _frame = new();

    /// <summary>一帧里显示的一个区域：折叠的（整块显示）或展开的（在下级下面垫底、边界压在下级上面）。</summary>
    private sealed class UnitItem
    {
        public required Region Region;
        public ProjectedShape? Shape;
        public SKColor Fill;
        public SKColor BorderColor;
        public float BorderAlpha;
        public bool Expanded;
        /// <summary>展开的渐变进度（0 到 1），折叠时为 0。</summary>
        public float T;
        public float LabelAlpha;
        public SKPath? Path;
    }

    private readonly record struct LineItem(GeoNode Node, ProjectedShape Shape, float Alpha, Region? Region);

    private sealed class DisplayFrame
    {
        public readonly List<UnitItem> Units = new();
        public readonly List<LineItem> Lines = new();

        /// <summary>这一帧展开了的区域（按节点）和它们的渐变进度。</summary>
        public readonly Dictionary<GeoNode, float> Open = new(ReferenceEqualityComparer.Instance);

        public int VertexCount;
    }

    /// <summary>展开阈值（DIP）：下级的典型边长在屏幕上超过它时展开。细节程度越高越早展开。</summary>
    private double ExpandPx => 60 * Math.Pow(0.4, Math.Clamp(_editor.LodDetail.Value, 0, 1));

    // ───────────────────────── 区域树 ─────────────────────────

    /// <summary>文档改动后重建区域树；有需要拼边界的分组时在后台计算。</summary>
    private void EnsureRegions()
    {
        if (_regionStamp == _docStamp) return;
        _regionStamp = _docStamp;
        _regionVersion++;
        _regions = RegionIndex.Build(Doc, _shapes, _vp, _derived, ColorOf);
        if (_regions.Pending.Count == 0) return;
        if (_syncDerived)
        {
            // 离屏绘制（测试程序）：当场算完，没有界面线程可以回调
            for (int round = 0; round < 16 && _regions.Pending.Count > 0; round++)
            {
                var batch = _derived.MakeJobs(_regions.Pending);
                _derived.Adopt(batch, DerivedShapes.Compute(batch, _vp.DataCrs, _vp.DisplayCrs));
                _regions = RegionIndex.Build(Doc, _shapes, _vp, _derived, ColorOf);
            }
            return;
        }
        if (!_derivedRunning) ComputeDerivedInBackground();
    }

    /// <summary>离屏绘制时同步计算自动边界（测试程序用）。</summary>
    private bool _syncDerived;

    /// <summary>测试程序用：视野中心（数据坐标系的经纬度）、缩放级别和尺寸（DIP）；缩放为 null 时显示全部数据。</summary>
    internal void SetViewForTest(double width, double height, double? zoom = null, double lon = 0, double lat = 0)
    {
        _syncDerived = true;
        _vp.Width = width;
        _vp.Height = height;
        _hasView = true;
        if (zoom is not double z)
        {
            ZoomToAll();
            return;
        }
        var (x, y) = _vp.DataToWorld(lon, lat);
        _vp.CenterX = x;
        _vp.CenterY = y;
        _vp.Zoom = z;
    }

    /// <summary>测试程序用：离屏画一帧（标注位置分几帧算完，所以连画几遍）。</summary>
    internal void RenderForTest(SKCanvas canvas, float scale)
    {
        _syncDerived = true;
        var info = new SKImageInfo((int)Math.Ceiling(_vp.Width * scale), (int)Math.Ceiling(_vp.Height * scale));
        for (int i = 0; i < 4; i++) OnPaint(canvas, info);
    }

    /// <summary>测试程序用：当前视野里显示的区域（名称和是否展开）。</summary>
    internal List<(string Name, int Depth, bool Expanded)> DisplayedForTest()
        => _frame.Units.Select(u => (u.Region.Node.DisplayName, u.Region.Depth, u.Expanded)).ToList();

    internal List<GeoNode> HitTestForTest(double sx, double sy) => HitTestAll(sx, sy);

    internal RegionIndex RegionsForTest
    {
        get
        {
            EnsureRegions();
            return _regions;
        }
    }

    internal (double X, double Y) DataToScreenForTest(double lon, double lat) => _vp.DataToScreen(lon, lat);

    private async void ComputeDerivedInBackground()
    {
        var jobs = _derived.MakeJobs(_regions.Pending);
        if (jobs.Count == 0) return;
        _derivedRunning = true;
        var (data, display) = (_vp.DataCrs, _vp.DisplayCrs);
        List<DerivedShapes.Result>? results = null;
        try
        {
            results = await Task.Run(() => DerivedShapes.Compute(jobs, data, display));
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
        finally
        {
            _derivedRunning = false;
        }
        _derived.Adopt(jobs, results ?? []);
        // 重建区域树：用上新算好的边界，文档在计算期间又改过的话接着算
        _regionStamp = -1;
        InvalidateVisual();
    }

    /// <summary>节点在地图上的形状：自身的几何，或者分组由下级拼出的边界（还没算好时为 null）。</summary>
    private ProjectedShape? ShapeOf(GeoNode node)
    {
        var own = _shapes.Get(node, _vp);
        if (own != null) return own;
        EnsureRegions();
        return _regions.Find(node)?.Derived;
    }

    // ───────────────────────── 一帧的内容 ─────────────────────────

    /// <summary>按当前缩放决定每个区域折叠还是展开，得到这一帧要画的区域和线。</summary>
    private DisplayFrame BuildFrame()
    {
        EnsureRegions();
        var frame = new DisplayFrame();
        var (minX, minY, maxX, maxY) = _vp.VisibleWorld();
        double pad = 40 / _vp.WorldSize;
        (minX, minY, maxX, maxY) = (minX - pad, minY - pad, maxX + pad, maxY + pad);
        bool lod = _editor.RegionLod.Value;
        double ws = _vp.WorldSize, expandPx = ExpandPx;

        void Walk(Region r, UnitItem? parent, float parentT)
        {
            if (!r.Intersects(minX, minY, maxX, maxY)) return;
            var shape = r.HasOwnShape ? _shapes.Get(r.Node, _vp) : r.Derived;
            float t;
            bool expanded;
            if (!lod)
            {
                expanded = r.Children.Count > 0 || r.Lines.Count > 0;
                t = 1;
            }
            else
            {
                double px = r.DetailScale * ws;
                expanded = r.DetailScale > 0 && px > expandPx;
                t = expanded ? (float)Math.Clamp((px - expandPx) / (expandPx * FadeRatio), 0, 1) : 0;
            }

            var unit = new UnitItem
            {
                Region = r,
                Shape = shape,
                Fill = parent == null ? r.Color : Lerp(parent.Fill, r.Color, parentT),
                BorderColor = BorderColorOf(r),
                BorderAlpha = parent == null ? 1 : parentT,
                Expanded = expanded,
                T = expanded ? t : 0,
            };
            // 下级的名称随上级展开逐渐显现；有下级区域的区域展开后自己的名称逐渐淡出（很大的只淡一半，像 P 社地图上的国名）
            float own = 1;
            if (lod && expanded && r.Children.Count > 0)
            {
                bool large = shape != null && shape.LabelRadiusBound * ws >= 150;
                own = large ? 1 - 0.5f * t : 1 - t;
            }
            unit.LabelAlpha = (parent == null || !lod ? 1 : parentT) * own;
            frame.Units.Add(unit);
            if (shape != null) frame.VertexCount += shape.VertexCount;
            if (!expanded) return;

            frame.Open[r.Node] = t;
            foreach (var c in r.Children) Walk(c, unit, t);
            foreach (var line in r.Lines)
            {
                var s = _shapes.Get(line, _vp);
                if (s == null || !s.Intersects(minX, minY, maxX, maxY)) continue;
                frame.Lines.Add(new LineItem(line, s, t, r));
                frame.VertexCount += s.VertexCount;
            }
        }

        foreach (var r in _regions.Roots) Walk(r, null, 1);
        foreach (var line in _regions.RootLines)
        {
            var s = _shapes.Get(line, _vp);
            if (s == null || !s.Intersects(minX, minY, maxX, maxY)) continue;
            frame.Lines.Add(new LineItem(line, s, 1, null));
            frame.VertexCount += s.VertexCount;
        }
        return frame;
    }

    private SKColor BorderColorOf(Region r)
    {
        // 同一上级的下级用同一种边界颜色（上级色调加深），公共边界画两次也不会一边一个颜色
        var basis = r.Parent?.Color ?? r.Color;
        return DarkBase ? MapStyle.Lighten(basis, 0.35f) : MapStyle.Darken(basis, r.Parent == null ? 0.45f : 0.38f);
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t)
    {
        if (t <= 0) return a;
        if (t >= 1) return b;
        return new SKColor(
            (byte)(a.Red + (b.Red - a.Red) * t),
            (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue + (b.Blue - a.Blue) * t),
            (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
    }

    /// <summary>边界粗细随缩放略有变化：缩得很小时细一些，放大后粗一些。</summary>
    private float ZoomWidthFactor => (float)Math.Clamp(0.72 + 0.055 * _vp.Zoom, 0.85, 1.35);

    private static float BorderWidth(int depth) => BorderWidths[Math.Min(depth, BorderWidths.Length - 1)];

    // ───────────────────────── 绘制 ─────────────────────────

    private void DrawRegionLayer(SKCanvas canvas, DisplayFrame frame)
    {
        int level = Level;
        foreach (var u in frame.Units)
        {
            if (u.Shape == null) continue;
            var path = RentPath();
            path.FillType = SKPathFillType.EvenOdd;
            AppendPaths(path, u.Shape.PathsAt(level), true);
            u.Path = path.IsEmpty ? null : path;
        }
        DrawRegionFills(canvas, frame);
        DrawInnerLines(canvas, frame);
        DrawRegionBorders(canvas, frame);
        DrawRootLines(canvas, frame);
    }

    /// <summary>
    /// 填充：整个画在一个半透明图层里，图层内部用不透明的颜色。上级先画、下级盖在上面，
    /// 重叠的地方不会叠深；展开的上级垫在下级下面，下级之间的接缝也不会透出底图。
    /// </summary>
    private void DrawRegionFills(SKCanvas canvas, DisplayFrame frame)
    {
        if (frame.Units.Count == 0) return;
        bool dark = DarkBase;
        using var layer = new SKPaint { Color = SKColors.Black.WithAlpha(dark ? (byte)112 : (byte)84) };
        canvas.SaveLayer(new SKRect(-4, -4, (float)_vp.Width + 4, (float)_vp.Height + 4), layer);
        _fill.Shader = null;
        foreach (var u in frame.Units)
        {
            _fill.Color = u.Fill.WithAlpha(255);
            if (u.Path != null)
            {
                canvas.DrawPath(u.Path, _fill);
            }
            else if (u.Shape == null && !u.Expanded)
            {
                // 自动边界还没算好：先用全部下级拼着填充
                FillDescendants(canvas, u.Region);
            }
        }
        canvas.Restore();
    }

    private void FillDescendants(SKCanvas canvas, Region r)
    {
        int level = Level;
        var (minX, minY, maxX, maxY) = _vp.VisibleWorld();
        foreach (var d in r.SelfAndDescendants())
        {
            if (!d.HasOwnShape || !d.Intersects(minX, minY, maxX, maxY)) continue;
            var s = _shapes.Get(d.Node, _vp);
            if (s == null) continue;
            _path.Reset();
            _path.FillType = SKPathFillType.EvenOdd;
            AppendPaths(_path, s.PathsAt(level), true);
            canvas.DrawPath(_path, _fill);
        }
    }

    /// <summary>
    /// 边界：深的先画，浅的（上级的）压在上面。在屏幕上太小的区域不描边（一团描边只会是个黑点）。
    /// </summary>
    private void DrawRegionBorders(SKCanvas canvas, DisplayFrame frame)
    {
        double ws = _vp.WorldSize;
        float zf = ZoomWidthFactor;
        var order = new List<UnitItem>(frame.Units.Count);
        foreach (var u in frame.Units)
        {
            if (u.Path == null || u.BorderAlpha < 0.02f) continue;
            if (Math.Sqrt(u.Shape!.Area) * ws < 2.5) continue;
            order.Add(u);
        }
        // 稳定排序：深的在前
        order = order.Select((u, i) => (u, i)).OrderByDescending(x => x.u.Region.Depth).ThenBy(x => x.i).Select(x => x.u).ToList();
        foreach (var u in order)
        {
            int depth = u.Region.Depth;
            float width = BorderWidth(depth) * zf;
            float alpha = (depth == 0 ? 0.95f : 0.85f) * u.BorderAlpha;
            _stroke.Color = u.BorderColor.WithAlpha((byte)(alpha * 255));
            _stroke.StrokeWidth = width;
            canvas.DrawPath(u.Path!, _stroke);
        }
    }

    /// <summary>区域里的线（例如画成线的下级边界、河流）：随区域展开逐渐显现，粗细与下一级边界相当。</summary>
    private void DrawInnerLines(SKCanvas canvas, DisplayFrame frame)
    {
        int level = Level;
        float zf = ZoomWidthFactor;
        foreach (var (node, shape, alpha, region) in frame.Lines)
        {
            if (region == null || alpha < 0.02f) continue;
            _path.Reset();
            AppendPaths(_path, shape.PathsAt(level), false);
            if (_path.IsEmpty) continue;
            var color = ColorOf(node);
            _stroke.Color = color.WithAlpha((byte)(Math.Clamp(alpha, 0, 1) * 235));
            _stroke.StrokeWidth = Math.Max(BorderWidth(region.Depth + 1) * zf * 1.25f, 0.9f);
            canvas.DrawPath(_path, _stroke);
        }
    }

    /// <summary>不属于任何区域的线（道路、河流等）：放大后加上浅色衬边，缩小时变细，不会糊成一片。</summary>
    private void DrawRootLines(SKCanvas canvas, DisplayFrame frame)
    {
        int level = Level;
        double zoom = _vp.Zoom;
        float width = (float)Math.Clamp(1.2 + (zoom - 4) * 0.3, 1.4, 3.2);
        float casingAlpha = (float)Math.Clamp((zoom - 6) / 3, 0, 1);
        var casing = DarkBase ? new SKColor(0, 0, 0, 120) : new SKColor(255, 255, 255, 220);
        foreach (var (node, shape, _, region) in frame.Lines)
        {
            if (region != null) continue;
            _path.Reset();
            AppendPaths(_path, shape.PathsAt(level), false);
            if (_path.IsEmpty) continue;
            if (casingAlpha > 0.02f)
            {
                _stroke.Color = casing.WithAlpha((byte)(casing.Alpha * casingAlpha));
                _stroke.StrokeWidth = width + 2.5f;
                canvas.DrawPath(_path, _stroke);
            }
            _stroke.Color = ColorOf(node);
            _stroke.StrokeWidth = width;
            canvas.DrawPath(_path, _stroke);
        }
    }

    // ───────────────────────── 命中测试 ─────────────────────────

    /// <summary>
    /// 鼠标下的区域，按单击轮换的顺序排列：先是当前显示的那一级（缩小时是合并后的上级，放大后是下级），
    /// 然后逐级向上到最上层，最后从最底层回到显示级的下一级。所以缩小时单击选中上级，连续单击可以一直选到最底层。
    /// </summary>
    private List<GeoNode> RegionChainAt(double wx, double wy)
    {
        EnsureRegions();
        int level = Level;
        var path = new List<Region>();
        var others = new List<Region>();
        IReadOnlyList<Region> candidates = _regions.Roots;
        while (true)
        {
            Region? hit = null;
            // 重叠时后画的（在上面的）优先；被压在下面的同级区域排到最后，连续单击也能选到
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                var c = candidates[i];
                if (!c.Intersects(wx, wy, wx, wy) || !RegionContains(c, wx, wy, level)) continue;
                if (hit == null) hit = c;
                else others.Add(c);
            }
            if (hit == null) break;
            path.Add(hit);
            candidates = hit.Children;
        }
        var result = new List<GeoNode>(path.Count + others.Count);
        if (path.Count == 0) return result;

        int display = path.Count - 1;
        for (int i = 0; i < path.Count; i++)
        {
            if (_frame.Open.ContainsKey(path[i].Node)) continue;
            display = i;
            break;
        }
        for (int i = display; i >= 0; i--) result.Add(path[i].Node);
        for (int i = path.Count - 1; i > display; i--) result.Add(path[i].Node);
        foreach (var o in others.OrderByDescending(r => r.Depth)) result.Add(o.Node);
        return result;
    }

    private bool RegionContains(Region r, double wx, double wy, int level)
    {
        var shape = r.HasOwnShape ? _shapes.Get(r.Node, _vp) : r.Derived;
        if (shape != null) return shape.Intersects(wx, wy, wx, wy) && WorldMath.PointInRings(shape.PathsAt(level), wx, wy);
        foreach (var c in r.Children)
        {
            if (c.Intersects(wx, wy, wx, wy) && RegionContains(c, wx, wy, level)) return true;
        }
        return false;
    }

    /// <summary>
    /// 没有选择要素时切割哪些要素：当前显示着的那一级（合并显示的区域整块切开，它的下级跟着切；
    /// 由下级拼成的分组没有自身的几何，切它的下级）和显示着的线。关闭层级缩放显示时为 null（全部可见要素）。
    /// </summary>
    private HashSet<GeoNode>? CutScope()
    {
        if (!_editor.RegionLod.Value) return null;
        var scope = new HashSet<GeoNode>(ReferenceEqualityComparer.Instance);
        foreach (var u in _frame.Units)
        {
            if (u.Expanded && u.Region.Children.Count > 0) continue;
            if (u.Region.HasOwnShape)
            {
                scope.Add(u.Region.Node);
                continue;
            }
            foreach (var d in u.Region.SelfAndDescendants())
            {
                if (d.HasOwnShape) scope.Add(d.Node);
            }
        }
        foreach (var l in _frame.Lines) scope.Add(l.Node);
        return scope;
    }

    /// <summary>点标记所在的区域在这一帧是否展开（没有所属区域的点算展开）。</summary>
    private bool IsPointRegionOpen(GeoNode node)
    {
        if (!_editor.RegionLod.Value || node.Parent == null) return true;
        var r = _regions.RegionOf(node.Parent);
        return r == null || _frame.Open.ContainsKey(r.Node);
    }

    // ───────────────────────── 区域名称 ─────────────────────────

    /// <summary>
    /// 区域名称。大的区域把名字沿区域的主轴方向拉开排（横排、竖排或斜排，字保持正立），
    /// 字号随区域大小变化，像 P 社地图上的国名；小区域照常居中写一行。放不下时不画。
    /// 返回 false 表示超出本帧的计算时间（内切圆还没算），留到下一帧。
    /// </summary>
    private bool TryRegionLabel(SKCanvas canvas, UnitItem unit, SKColor ink, SKColor halo, long start)
    {
        var shape = unit.Shape;
        var node = unit.Region.Node;
        if (shape == null || unit.LabelAlpha < 0.05f) return true;
        double s = _vp.WorldSize;
        if (shape.LabelRadiusBound * s < 12) return true;
        if (!shape.HasLabel && Stopwatch.GetElapsedTime(start).TotalMilliseconds > LabelBudgetMs) return false;
        var (lx, ly, radius) = shape.Label;
        double radiusPx = radius * s;
        if (radiusPx < 12) return true;

        string name = node.Name.Trim();
        int depth = unit.Region.Depth;
        bool selected = Doc.IsSelected(node);
        float alpha = selected ? 1 : unit.LabelAlpha;
        // 区域名称用区域自己色调的深色，和地名（点的名称）区分开
        if (!DarkBase) ink = MapStyle.Darken(unit.Region.Color, 0.62f).WithAlpha(235);
        var inkA = ink.WithAlpha((byte)(ink.Alpha * alpha));
        var haloA = halo.WithAlpha((byte)(halo.Alpha * alpha));
        var (x, y) = _vp.WorldToScreen(lx, ly);

        if (radiusPx >= 30 && name.Length is >= 2 and <= 10 && IsCjkWord(name))
        {
            float size = depth switch
            {
                0 => (float)Math.Clamp(radiusPx * 0.3, 14, 30),
                1 => (float)Math.Clamp(radiusPx * 0.26, 13, 22),
                _ => (float)Math.Clamp(radiusPx * 0.22, 12, 17),
            };
            // 中心被图钉、簇或别的名称占了时，垂直于排字方向挪一挪
            var axis = shape.LabelAxis;
            double nx = -Math.Sin(axis.Elongation >= 1.35 ? axis.Angle : 0), ny = Math.Cos(axis.Elongation >= 1.35 ? axis.Angle : 0);
            foreach (double k in (ReadOnlySpan<double>)[0, 0.35, -0.35])
            {
                float ox = (float)(nx * k * radiusPx), oy = (float)(ny * k * radiusPx);
                if (TrySpreadLabel(canvas, name, (float)x + ox, (float)y + oy, radiusPx, axis, size, depth <= 1, inkA, haloA)) return true;
            }
        }

        float compact = depth switch { 0 => 15f, 1 => 13.5f, _ => 12.5f };
        bool bold = depth <= 1;
        float w = Font(compact, bold).MeasureText(name);
        if (w > radiusPx * 2.6 && !selected) return true;
        float baseline = (float)y + compact * 0.35f;
        // 中心被点标记占了时，在内切圆里上下左右挪一挪
        float dy = (float)Math.Min(radiusPx * 0.55, compact * 1.7), dx = (float)Math.Min(radiusPx * 0.5, w * 0.6);
        Span<(float X, float Y)> offsets = [(0, 0), (0, -dy), (0, dy), (-dx, 0), (dx, 0)];
        foreach (var (ox, oy) in offsets)
        {
            if (TryDrawLabel(canvas, name, (float)x + ox, baseline + oy, compact, bold, SKTextAlign.Center, inkA, haloA, false, w)) return true;
        }
        if (selected) TryDrawLabel(canvas, name, (float)x, baseline, compact, bold, SKTextAlign.Center, inkA, haloA, true, w);
        return true;
    }

    /// <summary>
    /// 沿主轴拉开排列的名称：细长的区域顺着它的走向（接近竖直时竖排），其余横排；字间距按可用长度拉开，
    /// 太挤时缩小字号。每个字单独占位，别的标注可以放进字与字之间的空隙。
    /// </summary>
    private bool TrySpreadLabel(SKCanvas canvas, string name, float cx, float cy, double radiusPx, (double Angle, double Elongation) axis, float size, bool bold, SKColor ink, SKColor halo)
    {
        int n = name.Length;
        double angle = 0;
        double span = radiusPx * 1.5;
        if (axis.Elongation >= 1.35)
        {
            angle = axis.Angle;
            // 统一成从左往右（竖排、陡的斜排从上往下）
            if (angle > Math.PI / 2) angle -= Math.PI;
            if (angle < -Math.PI / 2) angle += Math.PI;
            double deg = Math.Abs(angle) * 180 / Math.PI;
            if (deg > 62) angle = Math.PI / 2;
            else if (deg < 15) angle = 0;
            // 陡的斜排从上往下读（向右上方的翻成从右上往左下排）
            else if (deg > 45 && angle < 0) angle += Math.PI;
            span = radiusPx * 1.7 * Math.Min(axis.Elongation, 2.4) * 0.8;
        }
        float step = size * 1.05f;
        double gap = n > 1 ? (span - n * step) / (n - 1) : 0;
        if (gap < 0)
        {
            size = (float)(span / n / 1.05);
            if (size < 11.5f) return false;
            step = size * 1.05f;
            gap = 0;
        }
        gap = Math.Min(gap, size * 0.75);
        double pitch = step + gap;
        double ux = Math.Cos(angle), uy = Math.Sin(angle);
        var font = Font(MathF.Round(size * 2) / 2, bold);
        size = font.Size;

        Span<SKRect> rects = stackalloc SKRect[n];
        Span<SKPoint> centers = stackalloc SKPoint[n];
        for (int i = 0; i < n; i++)
        {
            double o = (i - (n - 1) / 2.0) * pitch;
            float gx = (float)(cx + ux * o), gy = (float)(cy + uy * o);
            centers[i] = new SKPoint(gx, gy);
            rects[i] = new SKRect(gx - size * 0.55f, gy - size * 0.6f, gx + size * 0.55f, gy + size * 0.6f);
            if (rects[i].Right < 0 || rects[i].Left > _vp.Width || rects[i].Bottom < 0 || rects[i].Top > _vp.Height) return false;
            if (_labelGrid.Collides(rects[i])) return false;
        }
        _halo.Color = halo;
        _halo.StrokeWidth = Math.Max(3.2f, size * 0.16f);
        _text.Color = ink;
        for (int i = 0; i < n; i++)
        {
            _labelGrid.Add(rects[i]);
            if (_placingRegionLabels) _regionLabelGrid.Add(rects[i]);
            string ch = name.Substring(i, 1);
            float baseline = centers[i].Y + size * 0.36f;
            canvas.DrawText(ch, centers[i].X, baseline, SKTextAlign.Center, font, _halo);
            canvas.DrawText(ch, centers[i].X, baseline, SKTextAlign.Center, font, _text);
        }
        return true;
    }

    /// <summary>全是汉字（可以逐字拉开排列）。</summary>
    private static bool IsCjkWord(string text)
    {
        foreach (char c in text)
        {
            if (c < 0x3400 || c > 0x9FFF) return false;
        }
        return true;
    }
}
