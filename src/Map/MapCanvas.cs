using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Skia.Controls;

using GeoJsonEditor.App;
using GeoJsonEditor.Geo;
using GeoJsonEditor.Model;

using NetTopologySuite.Geometries;

using SkiaSharp;

using Point = Aprillz.MewUI.Point;

namespace GeoJsonEditor.Map;

/// <summary>
/// 基于 Skia 的地图画布：绘制底图瓦片与要素，处理平移缩放、选择、绘制、切割和顶点编辑。
/// 这里只做交互和绘制，所有对文档的修改都交给 <see cref="Editor"/>。
/// </summary>
public sealed partial class MapCanvas : SkiaCanvasView
{
    private readonly Editor _editor;
    private readonly MapViewport _vp = new();
    private readonly TileCache _tiles;
    private readonly ShapeCache _shapes = new();
    private int _redrawQueued;
    private bool _hasView;
    private bool _isDarkTheme;

    /// <summary>鼠标在地图上的经纬度（数据坐标系），离开地图时为 null。</summary>
    public event Action<Coordinate?>? PointerMoved;

    /// <summary>视口改变（缩放级别、比例尺）。</summary>
    public event Action? ViewChanged;

    /// <summary>画布状态提示（例如“单击添加顶点…”）。</summary>
    public event Action<string>? HintChanged;

    public MapCanvas(Editor editor, TileCache tiles)
    {
        _editor = editor;
        _tiles = tiles;
        ContinuousAnimation = false;
        Focusable = true;
        PaintSurface += OnPaint;

        _tiles.TileArrived += QueueRedraw;
        _editor.Doc.Changed += OnDocumentChanged;
        _editor.Doc.SelectionChanged += OnSelectionChanged;
        _editor.Tool.Changed += OnToolChanged;
        _editor.BaseMap.Changed += OnBaseMapChanged;
        _editor.DataCrs.Changed += OnBaseMapChanged;
        _editor.BaseMapFade.Changed += InvalidateVisual;
        _editor.BaseMapGray.Changed += InvalidateVisual;
        _editor.ShowLabels.Changed += InvalidateVisual;
        _editor.PointDisplay.Changed += InvalidateVisual;
        _editor.RegionLod.Changed += OnDisplayOptionChanged;
        _editor.LodDetail.Changed += OnDisplayOptionChanged;
        _editor.VertexEditTarget.Changed += OnVertexEditChanged;
        _editor.Highlights.Changed += InvalidateVisual;
        _editor.ZoomToRequested += nodes => ZoomTo(nodes);
        OnBaseMapChanged();
        UpdateCursor();
    }

    public MapViewport Viewport => _vp;

    public double Zoom => _vp.Zoom;

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (_isDarkTheme == value) return;
            _isDarkTheme = value;
            _styleStamp++;
            InvalidateVisual();
        }
    }

    private GeoDocument Doc => _editor.Doc;

    private void QueueRedraw()
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 1) return;
        var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
        if (dispatcher == null)
        {
            _redrawQueued = 0;
            return;
        }
        dispatcher.BeginInvoke(() =>
        {
            _redrawQueued = 0;
            InvalidateVisual();
        });
    }

    private void OnDocumentChanged(DocumentChange change)
    {
        _docStamp++;
        _colors.Clear();
        if ((change.Kind & (ChangeKind.Reset | ChangeKind.Structure)) != 0) _shapes.Prune(Doc);
        if ((change.Kind & ChangeKind.Reset) != 0) CancelInteraction();
        if ((change.Kind & (ChangeKind.Reset | ChangeKind.Structure | ChangeKind.Geometry)) != 0)
        {
            // 新增或改动的几何一次性并行投影，避免下一帧逐个构建造成卡顿
            _shapes.WarmUp(Doc.AllNodes(), _vp);
        }
        InvalidateVisual();
    }

    /// <summary>当前投影用的坐标系（后台准备投影时用）。</summary>
    public (CoordSystem Data, CoordSystem Display) ShapeCrs => (_vp.DataCrs, _vp.DisplayCrs);

    /// <summary>接收在后台线程准备好的投影（打开大文件时与读取一起完成）。</summary>
    public void AdoptShapes(IEnumerable<(GeoNode Node, ProjectedShape Shape)> shapes)
    {
        _shapes.Adopt(shapes);
    }

    private void OnDisplayOptionChanged()
    {
        _styleStamp++;
        InvalidateVisual();
    }

    private void OnSelectionChanged()
    {
        _vertexHover = null;
        InvalidateVisual();
    }

    private void OnVertexEditChanged()
    {
        _vertexHover = null;
        _handleKey = default;
        UpdateHint();
        UpdateCursor();
        InvalidateVisual();
    }

    private void OnToolChanged()
    {
        CancelInteraction();
        UpdateCursor();
        UpdateHint();
        InvalidateVisual();
    }

    private void OnBaseMapChanged()
    {
        // 底图明暗决定要素的配色，缓存的要素图层要重画
        _styleStamp++;
        var data = _editor.DataCrs.Value;
        var display = _editor.BaseMap.Value.IsBlank ? data : _editor.BaseMap.Value.Crs;
        if (data != _vp.DataCrs || display != _vp.DisplayCrs)
        {
            _vp.DataCrs = data;
            _vp.DisplayCrs = display;
            _docStamp++;
            _highlightSource = null;
            _cutPreviewShapes = null;
            _shapes.WarmUp(Doc.AllNodes(), _vp);
        }
        InvalidateVisual();
    }

    private void SyncSize()
    {
        var b = Bounds;
        if (b.Width > 0 && b.Height > 0)
        {
            _vp.Width = b.Width;
            _vp.Height = b.Height;
        }
    }

    // ───────────────────────── 视图 ─────────────────────────

    public void ZoomBy(double delta)
    {
        SyncSize();
        _vp.ZoomAround(_vp.Width / 2, _vp.Height / 2, _vp.Zoom + delta);
        AfterViewChange();
    }

    /// <summary>缩放到能看到全部数据。</summary>
    public void ZoomToAll()
    {
        // 只看根级：ZoomTo 会把每个节点的下级都算进去
        var nodes = Doc.Roots.Where(n => n.Visible && n.SelfAndDescendants().Any(d => d.Geometry != null && d.IsEffectivelyVisible)).ToList();
        if (nodes.Count == 0) nodes = Doc.Roots.ToList();
        if (!nodes.Any(n => n.SelfAndDescendants().Any(d => d.Geometry != null))) nodes.Clear();
        if (nodes.Count == 0)
        {
            // 空文档：显示中国全境
            SyncSize();
            var (x0, y0) = WebMercator.Forward(73, 53.5);
            var (x1, y1) = WebMercator.Forward(135, 18);
            _vp.FitWorld(x0, y0, x1, y1);
            _hasView = true;
            AfterViewChange();
            return;
        }
        ZoomTo(nodes);
    }

    public void ZoomTo(IReadOnlyList<GeoNode> nodes)
    {
        SyncSize();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var root in nodes)
        {
            foreach (var n in root.SelfAndDescendants())
            {
                var s = _shapes.Get(n, _vp);
                if (s == null) continue;
                any = true;
                minX = Math.Min(minX, s.MinX);
                minY = Math.Min(minY, s.MinY);
                maxX = Math.Max(maxX, s.MaxX);
                maxY = Math.Max(maxY, s.MaxY);
            }
        }
        if (!any) return;
        bool single = maxX - minX < 1e-9 && maxY - minY < 1e-9;
        if (single)
        {
            _vp.CenterX = minX;
            _vp.CenterY = minY;
            _vp.Zoom = Math.Max(_vp.Zoom, 15);
        }
        else
        {
            _vp.FitWorld(minX, minY, maxX, maxY, padding: 56);
        }
        _hasView = true;
        AfterViewChange();
    }

    /// <summary>这些节点（含下级）是否有一部分在当前视野内。</summary>
    public bool IsInView(IReadOnlyList<GeoNode> nodes)
    {
        SyncSize();
        var (minX, minY, maxX, maxY) = _vp.VisibleWorld();
        foreach (var root in nodes)
        {
            foreach (var n in root.SelfAndDescendants())
            {
                var s = _shapes.Get(n, _vp);
                if (s != null && s.Intersects(minX, minY, maxX, maxY)) return true;
            }
        }
        return false;
    }

    private void AfterViewChange()
    {
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>比例尺文字，例如“1 km”，以及它在屏幕上的长度（DIP）。</summary>
    public (string Text, double Pixels) ScaleBar(double maxPixels = 110)
    {
        double mpp = _vp.MetersPerPixel();
        double maxMeters = mpp * maxPixels;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(maxMeters)));
        double nice = pow;
        foreach (var m in new[] { 5.0, 2.0, 1.0 })
        {
            if (pow * m <= maxMeters)
            {
                nice = pow * m;
                break;
            }
        }
        string text = nice >= 1000 ? $"{nice / 1000:0.##} km" : $"{nice:0} m";
        return (text, nice / mpp);
    }

    // ───────────────────────── 命中测试 ─────────────────────────

    /// <summary>
    /// 鼠标下的全部可见要素，按优先级排列：点标记 → 线 → 区域。区域先是当前显示的那一级
    /// （缩小时是合并后的上级），然后逐级向上，再从最底层回到显示级的下一级（见 <see cref="RegionChainAt"/>）。
    /// 连续单击同一位置时按这个顺序轮换选择。
    /// </summary>
    private List<GeoNode> HitTestAll(double sx, double sy)
    {
        var (wx, wy) = _vp.ScreenToWorld(sx, sy);
        double s = _vp.WorldSize;
        int level = Lod.LevelForZoom(_vp.Zoom);
        var points = new List<(GeoNode Node, double D)>();
        var lines = new List<(GeoNode Node, double D)>();
        double tolLine = 6 / s;

        // 点只认上一帧画出来的标记：聚合时收进簇里的点看不见，也不该被点中
        foreach (var (n, d) in HitShownMarkers(sx, sy))
        {
            if (!IsLive(n)) continue;
            int k = points.FindIndex(x => ReferenceEquals(x.Node, n));
            if (k < 0) points.Add((n, d));
            else if (d < points[k].D) points[k] = (n, d);
        }

        // 线只认这一帧画出来的（所在区域折叠时不显示，也点不中），用当前缩放级别的简化路径判断
        foreach (var l in _frame.Lines)
        {
            if (l.Alpha < 0.15f || !IsLive(l.Node)) continue;
            var shape = _shapes.Get(l.Node, _vp);
            if (shape == null || !shape.Intersects(wx - tolLine, wy - tolLine, wx + tolLine, wy + tolLine)) continue;
            double best = WorldMath.PathsDistanceSq(shape.PathsAt(level), wx, wy);
            if (best <= tolLine * tolLine) lines.Add((l.Node, best));
        }

        var result = new List<GeoNode>();
        result.AddRange(points.OrderBy(p => p.D).Select(p => p.Node));
        result.AddRange(lines.OrderBy(p => p.D).Select(p => p.Node));
        result.AddRange(RegionChainAt(wx, wy));
        return result;
    }

    private static SKRect MarkerHitRect(MarkerIcon icon, double x, double y)
    {
        float fx = (float)x, fy = (float)y;
        return icon switch
        {
            MarkerIcon.Pin => new SKRect(fx - 12, fy - 31, fx + 12, fy + 2),
            MarkerIcon.Flag => new SKRect(fx - 5, fy - 32, fx + 20, fy + 2),
            _ => new SKRect(fx - 12, fy - 12, fx + 12, fy + 12),
        };
    }

    protected override void OnDispose()
    {
        _tiles.TileArrived -= QueueRedraw;
        _cutPreviewSerial++;
        DisposeRenderResources();
        base.OnDispose();
    }
}
