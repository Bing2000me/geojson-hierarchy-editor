using GeoJsonEditor.App;
using GeoJsonEditor.Model;

using SkiaSharp;

using Point = Aprillz.MewUI.Point;

namespace GeoJsonEditor.Map;

/// <summary>
/// 点标记的绘制，三种方式（见 <see cref="PointDisplay"/>）：
/// 逐级显示（默认）——点多时按缩放级别取舍，每一片只显示最重要的点（都城、州府优先），放大后逐级显示更多，
/// 与常见地图软件显示地名的方式相同；聚合计数——相邻的点合成带数量的圆；全部显示。
/// 所属区域还折叠着（缩小时）的点画成小圆点（重要的点大一些），区域展开后画完整图标。
/// 选中的点总是单独画在最上层。画出来的标记和簇记下来，供命中测试和标注避让使用。
/// </summary>
public sealed partial class MapCanvas
{
    /// <summary>点数超过这个值时在后台构建聚合索引（构建期间先用旧索引）。</summary>
    private const int ClusterAsyncThreshold = 20_000;

    private PointClusterIndex? _clusterIndex;
    private long _clusterStamp = -1;
    private long _clusterBuildingStamp = -1;
    private int _clusterSerial;

    // 上一帧画出来的点标记和簇（世界坐标），命中测试只认这些
    private readonly List<ShownMarker> _shownMarkers = new();
    private readonly List<ShownCluster> _shownClusters = new();
    private readonly List<PointLabel> _pointLabels = new();

    /// <summary>
    /// 等区域名称放好之后再画的小圆点：和区域名称重叠的不画（像地图软件那样，次要的点给名称让位），
    /// 画出来的才能点中、才有名称。
    /// </summary>
    private readonly List<PendingDot> _pendingDots = new();

    private readonly record struct PendingDot(GeoNode Node, int PointIndex, double WX, double WY, float X, float Y, SKColor Color, int Tier, bool Hover, float Rank, int Count);

    // 鼠标下的簇和它的成员名称（悬停卡片）
    private (int Level, int Index)? _clusterHover;
    private string _clusterHoverNames = "";
    private bool _lastClickWasCluster;

    /// <summary>逐级显示时这一屏的点画成小圆点（藏起来的点多，说明缩得很小）。</summary>
    private bool _compactPoints;

    /// <param name="Compact">画成了小圆点（所属区域折叠着）。</param>
    private readonly record struct ShownMarker(GeoNode Node, int PointIndex, double X, double Y, bool Compact = false, int Tier = -1);

    private readonly record struct ShownCluster(int Level, int Index, double X, double Y, float Radius, int Count, int Rep);

    /// <summary>
    /// 一个点或簇的标注候选。(X, Y) 是锚点的屏幕坐标，<see cref="Radius"/> 为簇的半径（点为 0），
    /// <see cref="Tier"/> 为层级（-1 表示认不出），决定字号。
    /// </summary>
    private readonly record struct PointLabel(string Text, float X, float Y, MarkerIcon Icon, float Radius, float Rank, int Tier, int Count, bool Force);

    private static readonly SKColor ClusterInkLight = new(0x1F, 0x29, 0x37);

    // ───────────────────────── 聚合索引 ─────────────────────────

    /// <summary>当前文档的聚合索引；文档改动后重建（点很多时在后台建，建好之前沿用旧的）。</summary>
    private PointClusterIndex? EnsureClusterIndex()
    {
        long stamp = _docStamp;
        if (_clusterIndex != null && _clusterStamp == stamp) return _clusterIndex;
        if (_clusterBuildingStamp == stamp) return _clusterIndex;

        var leaves = CollectLeaves();
        if (leaves.Count < ClusterAsyncThreshold)
        {
            _clusterIndex = PointClusterIndex.Build(leaves);
            _clusterStamp = stamp;
            return _clusterIndex;
        }
        BuildClustersInBackground(leaves, stamp);
        return _clusterIndex;
    }

    private async void BuildClustersInBackground(List<PointClusterIndex.Leaf> leaves, long stamp)
    {
        int serial = ++_clusterSerial;
        _clusterBuildingStamp = stamp;
        PointClusterIndex? index;
        try
        {
            index = await Task.Run(() => PointClusterIndex.Build(leaves));
        }
        catch (Exception)
        {
            index = null;
        }
        if (serial != _clusterSerial) return;
        _clusterBuildingStamp = -1;
        if (index == null) return;
        _clusterIndex = index;
        _clusterStamp = stamp;
        InvalidateVisual();
    }

    /// <summary>全部可见的点（多点要素的每个点单独算），带排序值和颜色。</summary>
    private List<PointClusterIndex.Leaf> CollectLeaves()
    {
        var leaves = new List<PointClusterIndex.Leaf>();
        void Walk(GeoNode node)
        {
            if (!node.Visible) return;
            if (node.Kind == NodeKind.Point && _shapes.Get(node, _vp) is { } shape)
            {
                var (rank, tier) = LevelTiers.PointRankAndTier(node);
                var color = ColorOf(node);
                for (int i = 0; i + 1 < shape.Points.Length; i += 2)
                {
                    leaves.Add(new PointClusterIndex.Leaf(node, i / 2, shape.Points[i], shape.Points[i + 1], rank, tier ?? -1, color));
                }
            }
            foreach (var c in node.Children) Walk(c);
        }
        foreach (var r in Doc.Roots) Walk(r);
        return leaves;
    }

    /// <summary>索引建好之后节点可能已被删除或隐藏（后台构建期间），这样的点不画。</summary>
    private bool IsLive(GeoNode node) => ReferenceEquals(Doc.Find(node.Id), node) && node.IsEffectivelyVisible;

    // ───────────────────────── 绘制 ─────────────────────────

    private void DrawPoints(SKCanvas canvas, List<VisibleItem> visible)
    {
        _shownMarkers.Clear();
        _shownClusters.Clear();
        _pointLabels.Clear();
        _pendingDots.Clear();

        var mode = _editor.PointDisplay.Value;
        int level = PointClusterIndex.LevelForZoom(_vp.Zoom);
        var index = mode != PointDisplay.All && level <= PointClusterIndex.MaxLevel ? EnsureClusterIndex() : null;
        if (index == null || index.Count < 2)
        {
            DrawMarkersPlain(canvas, visible);
            return;
        }

        float w = (float)_vp.Width, h = (float)_vp.Height;
        var clusters = index.At(level);
        var coarser = level > 0 ? index.At(level - 1) : null;
        bool dark = DarkBase;

        // 先数一下画多少个，决定要不要阴影；逐级显示时再看藏起来的点多不多，多的话这一屏都画成小圆点
        int onScreen = 0, hidden = 0;
        foreach (ref readonly var c in clusters.AsSpan())
        {
            var (x, y) = _vp.WorldToScreen(c.X, c.Y);
            if (x < -40 || y < -40 || x > w + 40 || y > h + 40) continue;
            onScreen++;
            hidden += c.Count - 1;
        }
        bool shadow = onScreen <= MarkerShadowLimit;
        if (mode == PointDisplay.Declutter)
        {
            // 带一点滞后，缩放到临界处时不会来回切换
            double hiddenShare = onScreen == 0 ? 0 : (double)hidden / (hidden + onScreen);
            _compactPoints = _compactPoints ? hiddenShare > 0.25 : hiddenShare > 0.4;
        }

        for (int k = 0; k < clusters.Length; k++)
        {
            ref readonly var c = ref clusters[k];
            var (x, y) = _vp.WorldToScreen(c.X, c.Y);
            if (x < -40 || y < -40 || x > w + 40 || y > h + 40) continue;
            var leaf = index.Leaves[c.Rep];
            var node = leaf.Node;
            if (!IsLive(node)) continue;

            if (c.Count == 1 || mode == PointDisplay.Declutter)
            {
                // 逐级显示：一片点里只画最重要的那个（簇的代表点）；所属区域还合并着（缩小时）的点取舍得更粗一级。
                // 选中的点最后单独画在最上层
                if (Doc.IsSelected(node)) continue;
                if (mode == PointDisplay.Declutter && coarser != null && c.Parent >= 0 && coarser[c.Parent].Rep != c.Rep && !IsPointRegionOpen(node)) continue;
                bool hover = ReferenceEquals(node, _hover);
                bool compact = mode == PointDisplay.Declutter ? _compactPoints : !IsPointRegionOpen(node);
                if (compact)
                {
                    _pendingDots.Add(new PendingDot(node, leaf.PointIndex, c.X, c.Y, (float)x, (float)y, leaf.Color, leaf.Tier, hover, leaf.Rank, c.Count));
                    continue;
                }
                DrawMarker(canvas, node.Icon, leaf.Color, (float)x, (float)y, false, hover, shadow || hover);
                _shownMarkers.Add(new ShownMarker(node, leaf.PointIndex, c.X, c.Y));
                if (!string.IsNullOrWhiteSpace(node.Name))
                {
                    _pointLabels.Add(new PointLabel(node.Name, (float)x, (float)y, node.Icon, 0, leaf.Rank, leaf.Tier, c.Count, false));
                }
                continue;
            }

            float radius = ClusterRadius(c.Count);
            bool hot = _clusterHover is { } ch && ch.Level == level && ch.Index == k;
            DrawCluster(canvas, (float)x, (float)y, radius, c, leaf.Color, dark, hot, shadow);
            _shownClusters.Add(new ShownCluster(level, k, c.X, c.Y, radius, c.Count, c.Rep));
            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                _pointLabels.Add(new PointLabel(node.Name, (float)x, (float)y, MarkerIcon.Circle, radius, leaf.Rank, leaf.Tier, c.Count, false));
            }
        }

        DrawSelectedMarkers(canvas);
    }

    /// <summary>不聚合时（关闭了点聚合、放得很大、点很少）逐个画点标记；点极多时改画小圆点。</summary>
    private void DrawMarkersPlain(SKCanvas canvas, List<VisibleItem> visible)
    {
        int total = 0;
        foreach (var v in visible)
        {
            if (v.Shape.Kind == NodeKind.Point) total += v.Shape.Points.Length / 2;
        }
        if (total == 0) return;

        bool dots = total > MarkerDotLimit;
        bool shadow = total <= MarkerShadowLimit;
        float w = (float)_vp.Width, h = (float)_vp.Height;
        foreach (var (node, shape, _) in visible)
        {
            if (shape.Kind != NodeKind.Point || Doc.IsSelected(node)) continue;
            bool hover = ReferenceEquals(node, _hover);
            var color = ColorOf(node);
            var (rank, tier) = dots ? (0f, null) : LevelTiers.PointRankAndTier(node);
            for (int i = 0; i + 1 < shape.Points.Length; i += 2)
            {
                var (x, y) = _vp.WorldToScreen(shape.Points[i], shape.Points[i + 1]);
                if (x < -40 || y < -40 || x > w + 40 || y > h + 40) continue;
                bool compact = !dots && total > 250;
                if (compact)
                {
                    _pendingDots.Add(new PendingDot(node, i / 2, shape.Points[i], shape.Points[i + 1], (float)x, (float)y, color, tier ?? -1, hover, rank, 1));
                    continue;
                }
                _shownMarkers.Add(new ShownMarker(node, i / 2, shape.Points[i], shape.Points[i + 1]));
                if (dots && !hover)
                {
                    _fill.Color = color;
                    canvas.DrawCircle((float)x, (float)y, 3.6f, _fill);
                    _stroke.Color = SKColors.White.WithAlpha(220);
                    _stroke.StrokeWidth = 1.2f;
                    canvas.DrawCircle((float)x, (float)y, 3.6f, _stroke);
                    continue;
                }
                DrawMarker(canvas, node.Icon, color, (float)x, (float)y, false, hover, shadow || hover);
                // 点极多时只给选中的写名字（选中的在 DrawSelectedMarkers 里处理）
                if (!dots && !string.IsNullOrWhiteSpace(node.Name))
                {
                    _pointLabels.Add(new PointLabel(node.Name, (float)x, (float)y, node.Icon, 0, rank, tier ?? -1, 1, false));
                }
            }
        }
        DrawSelectedMarkers(canvas);
    }

    /// <summary>画推迟的小圆点：显示标注时，和区域名称重叠的不画。</summary>
    private void DrawPendingDots(SKCanvas canvas, bool labels)
    {
        foreach (var d in _pendingDots)
        {
            var rect = DotHitRect(d.X, d.Y, d.Tier);
            if (labels && !d.Hover && _regionLabelGrid.Collides(rect)) continue;
            float r = DrawDot(canvas, d.Color, d.X, d.Y, d.Tier, d.Hover);
            _shownMarkers.Add(new ShownMarker(d.Node, d.PointIndex, d.WX, d.WY, true, d.Tier));
            if (labels) _labelGrid.Add(rect);
            if (!string.IsNullOrWhiteSpace(d.Node.Name))
            {
                _pointLabels.Add(new PointLabel(d.Node.Name, d.X, d.Y, MarkerIcon.Circle, r, d.Rank, d.Tier, d.Count, false));
            }
        }
        _pendingDots.Clear();
    }

    /// <summary>选中的点：不参与聚合，按实时位置（拖动中也是）画在最上层，名称必定显示。</summary>
    private void DrawSelectedMarkers(SKCanvas canvas)
    {
        var sel = Doc.Selection;
        if (sel.Count == 0) return;
        float w = (float)_vp.Width, h = (float)_vp.Height;
        int drawn = 0;
        foreach (var node in sel)
        {
            if (node.Kind != NodeKind.Point || !node.IsEffectivelyVisible) continue;
            var shape = _shapes.Get(node, _vp);
            if (shape == null) continue;
            var color = ColorOf(node);
            int tier = LevelTiers.Of(node) ?? -1;
            for (int i = 0; i + 1 < shape.Points.Length; i += 2)
            {
                var (x, y) = _vp.WorldToScreen(shape.Points[i], shape.Points[i + 1]);
                if (x < -40 || y < -40 || x > w + 40 || y > h + 40) continue;
                DrawMarker(canvas, node.Icon, color, (float)x, (float)y, true, false, true);
                _shownMarkers.Add(new ShownMarker(node, i / 2, shape.Points[i], shape.Points[i + 1]));
                if (!string.IsNullOrWhiteSpace(node.Name))
                {
                    _pointLabels.Add(new PointLabel(node.Name, (float)x, (float)y, node.Icon, 0, -1, tier, 1, true));
                }
                // 几千个选中的点时不再逐个画（全选同级之后），聚合结果照常显示
                if (++drawn > 2000) return;
            }
        }
    }

    private static float ClusterRadius(int count) => 12.5f + 4f * MathF.Log10(count);

    /// <summary>小圆点的半径：都城、省级治所大一些。</summary>
    private static float DotRadius(int tier) => tier switch
    {
        0 => 5.4f,
        1 => 4.8f,
        2 => 4.2f,
        _ => 3.4f,
    };

    private static SKRect DotHitRect(double x, double y, int tier)
    {
        float r = DotRadius(tier) + 4;
        return new SKRect((float)x - r, (float)y - r, (float)x + r, (float)y + r);
    }

    /// <summary>
    /// 城市符号式的小圆点：白边彩心，都城和省级治所中间再加一个白点（◉），返回占用的半径。
    /// 所属区域还折叠着（缩小时）的点用它，图钉只在放大后出现。
    /// </summary>
    private float DrawDot(SKCanvas canvas, SKColor color, float x, float y, int tier, bool hover)
    {
        float r = DotRadius(tier);
        if (hover)
        {
            _fill.Color = MapStyle.Accent.WithAlpha(60);
            canvas.DrawCircle(x, y, r + 6, _fill);
        }
        _fill.Color = SKColors.White.WithAlpha(DarkBase ? (byte)215 : (byte)245);
        canvas.DrawCircle(x, y, r + 1.4f, _fill);
        _fill.Color = color.WithAlpha(255);
        canvas.DrawCircle(x, y, r, _fill);
        if (tier is 0 or 1)
        {
            _fill.Color = SKColors.White;
            canvas.DrawCircle(x, y, r * 0.4f, _fill);
        }
        return r + 1.4f;
    }

    /// <summary>
    /// 簇：外圈是按成员颜色比例分段的环（颜色构成一目了然），里面是数量。
    /// 浅色底图上是白底深字，深色底图上是深底白字。
    /// </summary>
    private void DrawCluster(SKCanvas canvas, float x, float y, float radius, in PointClusterIndex.Cluster c, SKColor repColor, bool dark, bool hot, bool shadow)
    {
        const float ring = 4.2f;
        if (shadow)
        {
            canvas.DrawCircle(x, y + 1.5f, radius + 0.5f, _shadow);
        }
        if (hot)
        {
            _fill.Color = MapStyle.Accent.WithAlpha(60);
            canvas.DrawCircle(x, y, radius + 6, _fill);
        }

        // 白色描边把簇和底图、其他簇分开
        _fill.Color = SKColors.White.WithAlpha(dark ? (byte)200 : (byte)255);
        canvas.DrawCircle(x, y, radius + 1.5f, _fill);

        // 颜色构成环
        float mid = radius - ring / 2;
        var rect = new SKRect(x - mid, y - mid, x + mid, y + mid);
        _stroke.StrokeWidth = ring;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        var mix = c.Mix;
        if (mix == null || mix.Length <= 1)
        {
            _stroke.Color = mix is { Length: 1 } ? mix[0].Color : repColor;
            canvas.DrawCircle(x, y, mid, _stroke);
        }
        else
        {
            float start = -90;
            int total = 0;
            foreach (var (_, n) in mix) total += n;
            foreach (var (color, n) in mix)
            {
                float sweep = 360f * n / total;
                _stroke.Color = color;
                canvas.DrawArc(rect, start, sweep, false, _stroke);
                start += sweep;
            }
        }
        _stroke.StrokeCap = SKStrokeCap.Round;

        // 内圈和数字
        _fill.Color = dark ? new SKColor(0x1B, 0x20, 0x29, 0xF2) : SKColors.White;
        canvas.DrawCircle(x, y, radius - ring, _fill);
        if (hot)
        {
            _stroke.Color = MapStyle.Accent;
            _stroke.StrokeWidth = 2;
            canvas.DrawCircle(x, y, radius + 3.5f, _stroke);
        }

        string text = FormatCount(c.Count);
        var font = Font(text.Length >= 4 ? 10.5f : 11.5f, true);
        _text.Color = dark ? SKColors.White : ClusterInkLight;
        canvas.DrawText(text, x, y + font.Size * 0.36f, SKTextAlign.Center, font, _text);
    }

    private static string FormatCount(int n) => n < 10_000
        ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : (n / 10_000.0).ToString(n < 100_000 ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture) + "万";

    // ───────────────────────── 命中测试与交互 ─────────────────────────

    /// <summary>鼠标下的簇（上一帧画出来的）。</summary>
    private ShownCluster? HitCluster(Point p)
    {
        ShownCluster? best = null;
        double bestD = double.MaxValue;
        foreach (var c in _shownClusters)
        {
            var (x, y) = _vp.WorldToScreen(c.X, c.Y);
            double d = (x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y);
            double r = c.Radius + 3;
            if (d <= r * r && d < bestD)
            {
                bestD = d;
                best = c;
            }
        }
        return best;
    }

    /// <summary>鼠标下画出来的点标记（聚合时被收进簇里的点不算）。</summary>
    private IEnumerable<(GeoNode Node, double D)> HitShownMarkers(double sx, double sy)
    {
        foreach (var m in _shownMarkers)
        {
            var (x, y) = _vp.WorldToScreen(m.X, m.Y);
            var rect = m.Compact ? DotHitRect(x, y, m.Tier) : MarkerHitRect(m.Node.Icon, x, y);
            if (rect.Contains((float)sx, (float)sy))
            {
                yield return (m.Node, (x - sx) * (x - sx) + (y - sy) * (y - sy));
            }
        }
    }

    /// <summary>簇的成员节点（去重，按重要度排序）。</summary>
    private List<GeoNode> ClusterMembers(ShownCluster c)
    {
        var result = new List<GeoNode>();
        if (_clusterIndex == null) return result;
        var seen = new HashSet<GeoNode>(ReferenceEqualityComparer.Instance);
        foreach (int i in _clusterIndex.Members(c.Level, c.Index))
        {
            var node = _clusterIndex.Leaves[i].Node;
            if (IsLive(node) && seen.Add(node)) result.Add(node);
        }
        return result;
    }

    /// <summary>单击簇：放大到它拆开的级别，并把成员移到视野中间；成员位置重合拆不开时改为全部选中。</summary>
    private void ExpandCluster(ShownCluster c)
    {
        if (_clusterIndex == null) return;
        var members = _clusterIndex.Members(c.Level, c.Index);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (int i in members)
        {
            var l = _clusterIndex.Leaves[i];
            minX = Math.Min(minX, l.X);
            minY = Math.Min(minY, l.Y);
            maxX = Math.Max(maxX, l.X);
            maxY = Math.Max(maxY, l.Y);
        }
        int expand = _clusterIndex.ExpansionLevel(c.Level, c.Index);
        double spanPx = Math.Max(maxX - minX, maxY - minY) * _vp.WorldSize;
        if (expand > PointClusterIndex.MaxLevel && spanPx < 1)
        {
            var nodes = ClusterMembers(c);
            Doc.SetSelection(nodes);
            HintChanged?.Invoke($"这 {nodes.Count:N0} 个点的位置几乎重合，已全部选中。");
            return;
        }

        // 放到拆开的那一级（区间中间），成员范围放不下时再缩一点
        double zoom = Math.Max(_vp.Zoom + 1, expand);
        double fit = Math.Log2(Math.Min(
            Math.Max(_vp.Width - 160, 64) / Math.Max(maxX - minX, 1e-12),
            Math.Max(_vp.Height - 160, 64) / Math.Max(maxY - minY, 1e-12)) / MapViewport.TileSize);
        zoom = Math.Min(zoom, Math.Max(fit, _vp.Zoom + 0.5));
        _vp.CenterX = (minX + maxX) / 2;
        _vp.CenterY = (minY + maxY) / 2;
        _vp.Zoom = Math.Clamp(zoom, MapViewport.MinZoom, MapViewport.MaxZoom);
        _clusterHover = null;
        AfterViewChange();
    }

    /// <summary>更新悬停的簇；返回是否有变化。</summary>
    private bool UpdateClusterHover(ShownCluster? hit)
    {
        (int, int)? key = hit is { } h ? (h.Level, h.Index) : null;
        if (Nullable.Equals(key, _clusterHover)) return false;
        _clusterHover = key;
        _clusterHoverNames = "";
        if (hit is { } c)
        {
            var names = ClusterMembers(c)
                .Select(n => n.Name.Trim())
                .Where(n => n.Length > 0)
                .Distinct()
                .Take(5)
                .ToList();
            _clusterHoverNames = names.Count == 0 ? "" : string.Join("、", names) + (c.Count > names.Count ? " 等" : "");
            HintChanged?.Invoke($"这里聚合了 {c.Count:N0} 个点。单击放大展开；Shift 或 ⌘/Ctrl 单击选中它们。");
        }
        else
        {
            UpdateHint();
        }
        return true;
    }

    /// <summary>悬停在簇上时，光标旁的小卡片：数量和前几个成员的名称。</summary>
    private void DrawClusterHoverCard(SKCanvas canvas)
    {
        if (_clusterHover is not { } key || !_mouseInside || _drag != DragMode.None) return;
        ShownCluster? found = null;
        foreach (var c in _shownClusters)
        {
            if (c.Level == key.Level && c.Index == key.Index) found = c;
        }
        if (found is not { } cluster) return;

        var title = $"{cluster.Count:N0} 个点";
        var bold = Font(12.5f, true);
        var regular = Font(12f, false);
        string names = _clusterHoverNames;
        const float maxWidth = 280;
        if (names.Length > 0 && regular.MeasureText(names) > maxWidth)
        {
            // 太长时截断，末尾加省略号
            while (names.Length > 2 && regular.MeasureText(names + "…") > maxWidth) names = names[..^1];
            names += "…";
        }
        float width = Math.Max(bold.MeasureText(title), names.Length > 0 ? regular.MeasureText(names) : 0) + 24;
        float height = names.Length > 0 ? 50 : 30;
        var (cx, cy) = _vp.WorldToScreen(cluster.X, cluster.Y);
        float left = (float)cx + cluster.Radius + 10, top = (float)cy - height / 2;
        if (left + width > _vp.Width - 8) left = (float)cx - cluster.Radius - 10 - width;
        top = Math.Clamp(top, 8, (float)_vp.Height - height - 8);

        var rect = new SKRect(left, top, left + width, top + height);
        canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top + 2, rect.Right, rect.Bottom + 2), 9, 9, _shadow);
        _fill.Color = new SKColor(0x11, 0x18, 0x27, 0xEE);
        canvas.DrawRoundRect(rect, 9, 9, _fill);
        _text.Color = SKColors.White;
        canvas.DrawText(title, left + 12, top + 20, SKTextAlign.Left, bold, _text);
        if (names.Length > 0)
        {
            _text.Color = new SKColor(0xCB, 0xD5, 0xE1);
            canvas.DrawText(names, left + 12, top + 39, SKTextAlign.Left, regular, _text);
        }
    }
}
