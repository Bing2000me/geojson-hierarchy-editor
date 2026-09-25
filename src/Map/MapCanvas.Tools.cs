using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using GeoJsonEditor.App;
using GeoJsonEditor.Geo;
using GeoJsonEditor.Model;

using NetTopologySuite.Geometries;

using SkiaSharp;

using Point = Aprillz.MewUI.Point;

namespace GeoJsonEditor.Map;

public sealed partial class MapCanvas
{
    private enum DragMode
    {
        None,
        /// <summary>按下后还没移动：抬起时算单击，移动超过阈值变成平移。</summary>
        Pending,
        Pan,
        Vertex,
        MovePoint,
    }

    private enum SnapKind
    {
        Vertex,
        Edge,
        First,
    }

    private readonly record struct SnapResult(Coordinate Data, double X, double Y, SnapKind Kind);

    private readonly record struct HandleRef(int Path, int Index, bool IsMid);

    /// <summary>视野内顶点超过这个数时不显示顶点手柄（放大后再编辑）。</summary>
    private const int MaxHandles = 2500;

    private DragMode _drag;
    private Point _downPos;
    private Point _lastPos;
    private Point _mouse;
    private bool _mouseInside;
    private bool _suppressNextUp;
    private bool _spaceDown;
    private GeoNode? _hover;
    private HandleRef? _vertexHover;
    private SnapResult? _snap;

    // 绘制 / 切割中的点（数据坐标）
    private readonly List<Coordinate> _drawPts = new();

    // 切割预览在后台计算：同一时间只算一次，算的过程中鼠标又动了就只记下最新的切割线
    private Coordinate[]? _cutPreviewPending;
    private bool _cutPreviewRunning;
    private int _cutPreviewSerial;

    // 顶点拖动：每个受影响的要素记下原始几何、拖动基准几何（插入中点后）和被拖动顶点的位置
    private sealed record DragTarget(
        GeoNode Node,
        NetTopologySuite.Geometries.Geometry Original,
        NetTopologySuite.Geometries.Geometry Base,
        List<(int Path, int Index)> Positions);

    private GeoNode? _editNode;
    private Coordinate? _dragFrom;
    private readonly List<DragTarget> _dragTargets = new();
    private int _movePointIndex;

    // 视野内可编辑的顶点，按视图状态缓存（绘制手柄和鼠标命中共用）
    private (ProjectedShape? Shape, double Zoom, double Cx, double Cy, double W, double H) _handleKey;
    private List<(int Path, int Index)>? _handleVertices;
    private string _handleHint = "";

    /// <summary>要素在屏幕上小于这个尺寸时不显示顶点手柄：手柄会盖住整个要素，单击就没法穿透去选上一级。</summary>
    private const double MinHandleShapePixels = 48;

    /// <summary>相邻顶点在屏幕上的平均间距小于这个值时不显示手柄（挤成一条粗线，既看不清也点不准）。</summary>
    private const double MinHandleSpacing = 4;

    private const double SnapPixels = 10;

    // ───────────────────────── 状态 ─────────────────────────

    public bool IsDrawing => _drawPts.Count > 0;

    /// <summary>正在编辑顶点的面或线（双击、回车或“编辑顶点”按钮进入）。只是选中时不显示顶点手柄。</summary>
    private GeoNode? EditableNode
    {
        get
        {
            if (_editor.Tool.Value != EditTool.Select) return null;
            var n = _editor.VertexEditTarget.Value;
            return n != null && _editor.CanEditVertices(n) ? n : null;
        }
    }

    // 双击的第一下单击可能已经把选择轮换到了上一级，双击时要知道单击之前选的是什么
    private List<GeoNode> _selectionBeforeClick = new();

    public void CancelInteraction()
    {
        if (_drag is DragMode.Vertex or DragMode.MovePoint) RestoreDragOriginals();
        _drag = DragMode.None;
        _drawPts.Clear();
        ClearCutPreview();
        _snap = null;
        _editNode = null;
        ReleaseCapture();
        UpdateHint();
        InvalidateVisual();
    }

    private void UpdateCursor()
    {
        Cursor = _drag == DragMode.Pan || _spaceDown
            ? CursorType.SizeAll
            : _editor.Tool.Value == EditTool.Select ? CursorType.Arrow : CursorType.Cross;
    }

    public void UpdateHint()
    {
        string hint = _editor.Tool.Value switch
        {
            EditTool.DrawPoint => "单击地图放置点标记。按 Esc 回到选择工具。",
            EditTool.DrawLine => _drawPts.Count == 0
                ? "单击添加第一个顶点。拖动可平移地图，靠近已有要素时会自动吸附。"
                : "继续单击添加顶点；双击或按回车完成，退格撤回上一点，Esc 取消。",
            EditTool.DrawPolygon => _drawPts.Count == 0
                ? "单击添加第一个顶点。画到上级边界外也没关系，完成后会自动裁剪。"
                : "继续单击添加顶点；单击起点、双击或按回车闭合，退格撤回上一点，Esc 取消。",
            EditTool.Cut => _drawPts.Count == 0
                ? "画一条完整穿过要素的切割线：从要素外面开始单击。"
                : "继续单击；切割线穿出要素后双击或按回车执行切割，Esc 取消。",
            _ => EditableNode != null
                ? "编辑顶点：拖动白色方块移动顶点，拖动边中间的小圆点插入顶点，右键或双击顶点删除。按回车或 Esc 完成。"
                : _editor.VertexEditCandidate != null
                    ? "双击要素或按回车编辑顶点；再次单击同一位置选中上一级；Shift 或 ⌘/Ctrl 单击多选。"
                    : "单击选择要素，再次单击同一位置选中上一级；Shift 或 ⌘/Ctrl 单击多选；拖动空白处平移，滚轮缩放。",
        };
        HintChanged?.Invoke(hint);
    }

    private void Capture()
    {
        if (FindVisualRoot() is Window w) w.CaptureMouse(this);
    }

    private void ReleaseCapture()
    {
        if (FindVisualRoot() is Window w && IsMouseCaptured) w.ReleaseMouseCapture();
    }

    // ───────────────────────── 鼠标 ─────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        SyncSize();
        var p = e.GetPosition(this);
        _mouse = p;
        _downPos = p;
        _lastPos = p;
        e.Handled = true;

        if (e.Button == MouseButton.Middle || (e.Button == MouseButton.Left && _spaceDown))
        {
            _drag = DragMode.Pan;
            Capture();
            UpdateCursor();
            return;
        }

        if (e.Button == MouseButton.Right)
        {
            OnRightClick(p);
            return;
        }

        if (e.Button != MouseButton.Left) return;

        if (e.ClickCount >= 2)
        {
            _suppressNextUp = true;
            return;
        }

        if (_editor.Tool.Value == EditTool.Select)
        {
            var node = EditableNode;
            var handle = node != null ? HitHandle(node, p) : null;
            if (node != null && handle != null)
            {
                if (e.AltKey && !handle.Value.IsMid)
                {
                    DeleteVertex(node, handle.Value);
                    return;
                }
                BeginVertexDrag(node, handle.Value);
                Capture();
                return;
            }

            if (Doc.Selection.Count == 1 && Doc.Selection[0] is { Kind: NodeKind.Point } pt && pt.IsEffectivelyVisible && HitPointIndex(pt, p) is int idx)
            {
                BeginMovePoint(pt, idx);
                Capture();
                return;
            }
        }

        _drag = DragMode.Pending;
        Capture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SyncSize();
        var p = e.GetPosition(this);
        _mouse = p;
        _mouseInside = true;
        PointerMoved?.Invoke(_vp.ScreenToData(p.X, p.Y));

        if (_drag == DragMode.Pending && (Math.Abs(p.X - _downPos.X) > 4 || Math.Abs(p.Y - _downPos.Y) > 4))
        {
            _drag = DragMode.Pan;
            UpdateCursor();
        }

        switch (_drag)
        {
            case DragMode.Pan:
                _vp.Pan(p.X - _lastPos.X, p.Y - _lastPos.Y);
                _lastPos = p;
                AfterViewChange();
                return;
            case DragMode.Vertex:
                DragVertexTo(p);
                _lastPos = p;
                return;
            case DragMode.MovePoint:
                DragPointTo(p);
                _lastPos = p;
                return;
            case DragMode.Pending:
                return;
        }

        UpdateHover(p);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var p = e.GetPosition(this);
        e.Handled = true;
        ReleaseCapture();

        if (_suppressNextUp)
        {
            _suppressNextUp = false;
            _drag = DragMode.None;
            return;
        }

        var mode = _drag;
        _drag = DragMode.None;
        UpdateCursor();

        switch (mode)
        {
            case DragMode.Pending when e.Button == MouseButton.Left:
                OnClick(p, e.Modifiers);
                break;
            case DragMode.Vertex:
                CommitVertexDrag("编辑顶点");
                break;
            case DragMode.MovePoint:
                CommitVertexDrag("移动点标记");
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButton.Left) return;
        e.Handled = true;
        var p = e.GetPosition(this);
        switch (_editor.Tool.Value)
        {
            case EditTool.DrawLine or EditTool.DrawPolygon or EditTool.Cut:
                FinishDrawing();
                break;
            case EditTool.Select:
                if (_lastClickWasCluster)
                {
                    // 第一下单击已经放大展开了簇，第二下不再处理
                    _lastClickWasCluster = false;
                    break;
                }
                if (EditableNode is { } node)
                {
                    // 编辑顶点中：双击顶点删除，双击别处不做什么（单击已经处理了退出）
                    if (HitHandle(node, p) is { IsMid: false } h) DeleteVertex(node, h);
                    break;
                }
                if (DoubleClickTarget(p) is { } target)
                {
                    _editor.BeginVertexEdit(target);
                }
                else
                {
                    _vp.ZoomAround(p.X, p.Y, Math.Round(_vp.Zoom) + 1);
                    AfterViewChange();
                }
                break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        SyncSize();
        var p = e.GetPosition(this);
        double delta = e.Delta.Y;
        if (Math.Abs(delta) < 1e-6) return;
        _vp.ZoomAround(p.X, p.Y, _vp.Zoom + Math.Clamp(delta, -3, 3) * 0.5);
        _zoomGestureTick = Environment.TickCount64;
        e.Handled = true;
        AfterViewChange();
        if (_drawPts.Count > 0) UpdateRubber(p);
    }

    protected override void OnMouseLeave()
    {
        base.OnMouseLeave();
        _mouseInside = false;
        PointerMoved?.Invoke(null);
        if (_hover != null || _vertexHover != null || _snap != null || _clusterHover != null)
        {
            _hover = null;
            _vertexHover = null;
            _snap = null;
            UpdateClusterHover(null);
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        switch (e.Key)
        {
            case Key.Space:
                if (!_spaceDown)
                {
                    _spaceDown = true;
                    UpdateCursor();
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (IsDrawing)
                {
                    FinishDrawing();
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (_drag is DragMode.Vertex or DragMode.MovePoint || IsDrawing)
                {
                    CancelInteraction();
                    e.Handled = true;
                }
                break;
            case Key.Backspace or Key.Delete:
                if (IsDrawing)
                {
                    _drawPts.RemoveAt(_drawPts.Count - 1);
                    ClearCutPreview();
                    UpdateRubber(_mouse);
                    UpdateHint();
                    InvalidateVisual();
                    e.Handled = true;
                }
                else if (EditableNode is { } node && _vertexHover is { IsMid: false } h)
                {
                    DeleteVertex(node, h);
                    e.Handled = true;
                }
                break;
            case Key.Left when e.Modifiers == ModifierKeys.None:
                _vp.Pan(120, 0);
                AfterViewChange();
                e.Handled = true;
                break;
            case Key.Right when e.Modifiers == ModifierKeys.None:
                _vp.Pan(-120, 0);
                AfterViewChange();
                e.Handled = true;
                break;
            case Key.Up when e.Modifiers == ModifierKeys.None:
                _vp.Pan(0, 120);
                AfterViewChange();
                e.Handled = true;
                break;
            case Key.Down when e.Modifiers == ModifierKeys.None:
                _vp.Pan(0, -120);
                AfterViewChange();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            UpdateCursor();
            e.Handled = true;
        }
    }

    // ───────────────────────── 单击 ─────────────────────────

    private void OnClick(Point p, ModifierKeys modifiers)
    {
        switch (_editor.Tool.Value)
        {
            case EditTool.Select:
                ClickSelect(p, modifiers);
                break;
            case EditTool.DrawPoint:
            {
                var c = SnapOrRaw(p);
                _editor.AddDrawn(Geometries.Factory.CreatePoint(c));
                break;
            }
            case EditTool.DrawLine or EditTool.DrawPolygon or EditTool.Cut:
            {
                var snap = FindSnap(p, includeFirst: _editor.Tool.Value == EditTool.DrawPolygon);
                if (snap is { Kind: SnapKind.First })
                {
                    FinishDrawing();
                    return;
                }
                var c = snap?.Data ?? _vp.ScreenToData(p.X, p.Y);
                if (_drawPts.Count > 0 && _drawPts[^1].Equals2D(c)) return;
                _drawPts.Add(c);
                UpdateRubber(p);
                UpdateHint();
                InvalidateVisual();
                break;
            }
        }
    }

    private void ClickSelect(Point p, ModifierKeys modifiers)
    {
        bool additive = (modifiers & (ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Meta)) != 0;
        _lastClickWasCluster = false;
        _selectionBeforeClick = Doc.Selection.ToList();

        // 单击簇：放大展开；Shift 或 ⌘/Ctrl 单击把簇里的点加入或移出选择
        if (HitCluster(p) is { } cluster)
        {
            _lastClickWasCluster = true;
            var members = ClusterMembers(cluster);
            if (!additive)
            {
                ExpandCluster(cluster);
            }
            else if (members.All(Doc.IsSelected))
            {
                Doc.SetSelection(Doc.Selection.Where(n => !members.Contains(n)).ToList());
            }
            else
            {
                Doc.SetSelection(Doc.Selection.Concat(members).ToList());
            }
            return;
        }

        var hits = HitTestAll(p.X, p.Y);

        // 编辑顶点中：单击正在编辑的要素内部不改变选择（不会逐级选到上级），点到别处才退出
        if (EditableNode is { } editing && !additive && hits.Contains(editing) && (ReferenceEquals(hits[0], editing) || hits[0].Kind == NodeKind.Polygon))
        {
            return;
        }

        if (hits.Count == 0)
        {
            if (!additive) Doc.ClearSelection();
            return;
        }

        if (additive)
        {
            Doc.ToggleSelected(hits[0]);
            return;
        }

        // 已选中的要素在命中列表里：选它的下一项（通常是上一级区域），实现逐级向上选择
        if (Doc.Selection.Count == 1)
        {
            int i = hits.IndexOf(Doc.Selection[0]);
            if (i >= 0)
            {
                Doc.Select(hits[(i + 1) % hits.Count]);
                return;
            }
        }
        Doc.Select(hits[0]);
    }

    /// <summary>
    /// 双击要编辑顶点的要素：双击的第一下单击可能已经把选择轮换到了上一级，
    /// 所以先看单击之前选中的要素是否就在鼠标下，其次才是当前选中的。
    /// </summary>
    private GeoNode? DoubleClickTarget(Point p)
    {
        var hits = HitTestAll(p.X, p.Y);
        if (hits.Count == 0) return null;
        if (_selectionBeforeClick is [var before] && hits.Contains(before) && _editor.CanEditVertices(before)) return before;
        if (Doc.Selection is [var current] && hits.Contains(current) && _editor.CanEditVertices(current)) return current;
        return null;
    }

    private void OnRightClick(Point p)
    {
        switch (_editor.Tool.Value)
        {
            case EditTool.DrawLine or EditTool.DrawPolygon or EditTool.Cut when IsDrawing:
                FinishDrawing();
                return;
            case EditTool.Select:
                if (EditableNode is { } node && HitHandle(node, p) is { IsMid: false } h)
                {
                    DeleteVertex(node, h);
                    return;
                }
                // 右键簇：选中簇里的全部点，再弹出菜单（可以一起删除、复制、改颜色）
                if (HitCluster(p) is { } cluster)
                {
                    Doc.SetSelection(ClusterMembers(cluster));
                    ContextRequested?.Invoke(p);
                    return;
                }
                // 右键先选中鼠标下的要素，再弹出上下文菜单（由外层处理）
                var hits = HitTestAll(p.X, p.Y);
                if (hits.Count > 0 && !Doc.IsSelected(hits[0])) Doc.Select(hits[0]);
                ContextRequested?.Invoke(p);
                return;
        }
    }

    /// <summary>在选择工具下右键点击地图时触发，参数为画布内坐标。</summary>
    public event Action<Point>? ContextRequested;

    /// <summary>画布内某一点对应的数据坐标（右键菜单“复制坐标”用）。</summary>
    public Coordinate DataAt(Point p) => _vp.ScreenToData(p.X, p.Y);

    private void UpdateHover(Point p)
    {
        bool changed = false;
        if (_editor.Tool.Value == EditTool.Select)
        {
            var node = EditableNode;
            var handle = node != null ? HitHandle(node, p) : null;
            if (!Nullable.Equals(handle, _vertexHover))
            {
                _vertexHover = handle;
                changed = true;
            }
            var cluster = handle == null ? HitCluster(p) : null;
            changed |= UpdateClusterHover(cluster);
            var hover = handle != null || cluster != null ? null : HitTestAll(p.X, p.Y).FirstOrDefault();
            if (!ReferenceEquals(hover, _hover))
            {
                _hover = hover;
                changed = true;
            }
            Cursor = handle != null || cluster != null ? CursorType.Hand : CursorType.Arrow;
        }
        else
        {
            if (_hover != null)
            {
                _hover = null;
                changed = true;
            }
            changed |= UpdateRubber(p);
        }
        if (changed) InvalidateVisual();
    }

    /// <summary>绘制中更新橡皮筋终点和吸附提示；切割时顺便请求新的预览。</summary>
    private bool UpdateRubber(Point p)
    {
        var snap = _editor.Tool.Value == EditTool.Select ? null : FindSnap(p, includeFirst: _editor.Tool.Value == EditTool.DrawPolygon);
        _snap = snap;
        if (_editor.Tool.Value == EditTool.Cut && _drawPts.Count >= 1)
        {
            RequestCutPreview(_drawPts.Append(snap?.Data ?? _vp.ScreenToData(p.X, p.Y)).ToArray());
        }
        return true;
    }

    private void ClearCutPreview()
    {
        _cutPreviewSerial++;
        _cutPreviewPending = null;
        _cutPreviewShapes = null;
    }

    /// <summary>
    /// 在后台线程计算切割预览。候选对象在 UI 线程上取出（几何不可变，可以跨线程共享），
    /// 精确求交、切分和投影都在后台做，大区域切割时画线不会卡。
    /// </summary>
    private async void RequestCutPreview(Coordinate[] pts)
    {
        _cutPreviewPending = pts;
        if (_cutPreviewRunning) return;
        _cutPreviewRunning = true;
        try
        {
            while (_cutPreviewPending is { } next)
            {
                _cutPreviewPending = null;
                int serial = _cutPreviewSerial;
                if (next.Length < 2) continue;
                var cutter = Geometries.Factory.CreateLineString(next);
                var plan = _editor.PlanCut(cutter.EnvelopeInternal);
                var (data, display) = (_vp.DataCrs, _vp.DisplayCrs);
                var shapes = await Task.Run(() =>
                {
                    try
                    {
                        return Editor.PreviewCut(plan, cutter).Select(g => ShapeCache.Build(g, 0, data, display)).ToList();
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                });
                if (serial == _cutPreviewSerial && _editor.Tool.Value == EditTool.Cut && _drawPts.Count > 0)
                {
                    _cutPreviewShapes = shapes;
                    InvalidateVisual();
                }
            }
        }
        finally
        {
            _cutPreviewRunning = false;
        }
    }

    private Coordinate SnapOrRaw(Point p) => FindSnap(p, false)?.Data ?? _vp.ScreenToData(p.X, p.Y);

    /// <summary>完成当前的线、面绘制或执行切割（工具条上的“完成”按钮）。</summary>
    public void CompleteDrawing()
    {
        if (IsDrawing) FinishDrawing();
        Focus();
    }

    private void FinishDrawing()
    {
        var tool = _editor.Tool.Value;
        var pts = _drawPts.ToList();
        _drawPts.Clear();
        ClearCutPreview();
        _snap = null;
        var f = Geometries.Factory;
        try
        {
            switch (tool)
            {
                case EditTool.DrawLine when pts.Count >= 2:
                    _editor.AddDrawn(f.CreateLineString(pts.ToArray()));
                    break;
                case EditTool.DrawPolygon when pts.Count >= 3:
                {
                    pts.Add(pts[0].Copy());
                    NetTopologySuite.Geometries.Geometry poly = f.CreatePolygon(pts.ToArray());
                    if (!poly.IsValid) poly = GeometryOps.Clean(poly);
                    var polygonal = Geometries.PolygonalPart(poly);
                    if (polygonal != null) _editor.AddDrawn(polygonal);
                    break;
                }
                case EditTool.Cut when pts.Count >= 2:
                    _editor.Cut(f.CreateLineString(pts.ToArray()));
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        UpdateHint();
        InvalidateVisual();
    }

    // ───────────────────────── 吸附 ─────────────────────────

    private SnapResult? FindSnap(Point p, bool includeFirst)
    {
        if (includeFirst && _drawPts.Count >= 3)
        {
            var (fx, fy) = _vp.DataToScreen(_drawPts[0].X, _drawPts[0].Y);
            if (Math.Abs(fx - p.X) <= SnapPixels && Math.Abs(fy - p.Y) <= SnapPixels)
            {
                return new SnapResult(_drawPts[0], fx, fy, SnapKind.First);
            }
        }
        if (!_editor.Snapping.Value) return null;

        var (wx, wy) = _vp.ScreenToWorld(p.X, p.Y);
        double s = _vp.WorldSize;
        double tol = SnapPixels / s;
        double bestV = tol * tol, bestE = (tol * 0.8) * (tol * 0.8);
        SnapResult? vertex = null, edge = null;
        double qMinX = wx - tol, qMinY = wy - tol, qMaxX = wx + tol, qMaxY = wy + tol;

        void Scan(double[] path, Coordinate[] data, int start, int end)
        {
            for (int i = start; i <= end; i++)
            {
                if (_dragFrom != null && data[i].Equals2D(_dragFrom)) continue;
                double dx = path[i * 2] - wx, dy = path[i * 2 + 1] - wy;
                double d = dx * dx + dy * dy;
                if (d < bestV)
                {
                    bestV = d;
                    vertex = new SnapResult(data[i], 0, 0, SnapKind.Vertex);
                }
                if (vertex == null && i + 1 < data.Length)
                {
                    if (_dragFrom != null && data[i + 1].Equals2D(_dragFrom)) continue;
                    double de = WorldMath.SegmentDistanceSq(wx, wy, path[i * 2], path[i * 2 + 1], path[i * 2 + 2], path[i * 2 + 3], out double t);
                    if (de < bestE)
                    {
                        bestE = de;
                        var a = data[i];
                        var b = data[i + 1];
                        edge = new SnapResult(new Coordinate(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t), 0, 0, SnapKind.Edge);
                    }
                }
            }
        }

        foreach (var (n, shape) in Candidates())
        {
            if (_drag == DragMode.Vertex && ReferenceEquals(n, _editNode)) continue;
            if (!shape.Intersects(qMinX, qMinY, qMaxX, qMaxY)) continue;

            if (shape.Kind == NodeKind.Point)
            {
                var pts = Geometries.Points(n.Geometry).ToList();
                for (int i = 0; i < pts.Count && i * 2 + 1 < shape.Points.Length; i++)
                {
                    double dx = shape.Points[i * 2] - wx, dy = shape.Points[i * 2 + 1] - wy;
                    double d = dx * dx + dy * dy;
                    if (d < bestV)
                    {
                        bestV = d;
                        vertex = new SnapResult(pts[i].Coordinate, 0, 0, SnapKind.Vertex);
                    }
                }
                continue;
            }

            // 顶点多的路径只检查鼠标附近的分块
            var chunks = shape.VertexCount > PathChunks.Size * 4 ? shape.Chunks : null;
            for (int k = 0; k < shape.Paths.Length; k++)
            {
                var path = shape.Paths[k];
                var data = shape.DataPaths[k];
                if (chunks == null)
                {
                    Scan(path, data, 0, data.Length - 1);
                    continue;
                }
                var boxes = chunks[k];
                for (int c = 0; c < boxes.Length / 4; c++)
                {
                    if (!PathChunks.Intersects(boxes, c, qMinX, qMinY, qMaxX, qMaxY)) continue;
                    var (start, end) = PathChunks.Range(c, data.Length);
                    Scan(path, data, start, end);
                }
            }
        }

        var result = vertex ?? edge;
        if (result is { } r)
        {
            var (sx, sy) = _vp.DataToScreen(r.Data.X, r.Data.Y);
            return r with { X = sx, Y = sy };
        }
        return null;
    }

    // ───────────────────────── 顶点编辑 ─────────────────────────

    /// <summary>
    /// 视野内可编辑的顶点（环的最后一个点与起点重合，不单独算）。超过 <see cref="MaxHandles"/> 时返回 null，
    /// 这时不显示手柄，放大后再编辑。用分块包围盒跳过视野外的部分，几十万顶点的边界也能编辑局部。
    /// </summary>
    private List<(int Path, int Index)>? VisibleHandles(ProjectedShape shape)
    {
        var key = (shape, _vp.Zoom, _vp.CenterX, _vp.CenterY, _vp.Width, _vp.Height);
        if (_handleKey == key) return _handleVertices;
        _handleKey = key;
        _handleVertices = null;

        double s = _vp.WorldSize;
        if (shape.Extent * s < MinHandleShapePixels)
        {
            _handleHint = "要素在屏幕上太小，放大后显示顶点手柄";
            return null;
        }

        var (minX, minY, maxX, maxY) = _vp.VisibleWorld();
        double pad = 12 / _vp.WorldSize;
        minX -= pad;
        minY -= pad;
        maxX += pad;
        maxY += pad;

        bool closed = shape.Kind == NodeKind.Polygon;
        var list = new List<(int, int)>();
        var chunks = shape.VertexCount > PathChunks.Size * 4 ? shape.Chunks : null;
        int total = 0;
        for (int k = 0; k < shape.Paths.Length; k++)
        {
            var path = shape.Paths[k];
            int n = path.Length / 2;
            int count = closed ? n - 1 : n;
            int from = 0;
            int chunkCount = chunks == null ? 1 : chunks[k].Length / 4;
            for (int c = 0; c < chunkCount; c++)
            {
                int start = 0, end = count - 1;
                if (chunks != null)
                {
                    if (!PathChunks.Intersects(chunks[k], c, minX, minY, maxX, maxY)) continue;
                    (start, end) = PathChunks.Range(c, n);
                    end = Math.Min(end, count - 1);
                }
                for (int i = Math.Max(start, from); i <= end; i++)
                {
                    double x = path[i * 2], y = path[i * 2 + 1];
                    if (x < minX || x > maxX || y < minY || y > maxY) continue;
                    list.Add((k, i));
                    if (++total > MaxHandles)
                    {
                        _handleHint = $"视野内有 {CountVisibleVertices(shape, minX, minY, maxX, maxY):N0} 个顶点，放大后显示顶点手柄";
                        return null;
                    }
                }
                from = end + 1;
            }
        }

        // 顶点在屏幕上挤在一起时也不显示
        double length = 0;
        int segments = 0;
        for (int j = 1; j < list.Count; j++)
        {
            var (k0, i0) = list[j - 1];
            var (k1, i1) = list[j];
            if (k0 != k1 || i1 != i0 + 1) continue;
            var path = shape.Paths[k0];
            double dx = (path[i1 * 2] - path[i0 * 2]) * s, dy = (path[i1 * 2 + 1] - path[i0 * 2 + 1]) * s;
            length += Math.Sqrt(dx * dx + dy * dy);
            segments++;
        }
        if (segments >= 12 && length / segments < MinHandleSpacing)
        {
            _handleHint = "顶点太密，放大后显示顶点手柄";
            return null;
        }

        _handleVertices = list;
        _handleHint = "";
        return list;
    }

    private static int CountVisibleVertices(ProjectedShape shape, double minX, double minY, double maxX, double maxY)
    {
        int total = 0;
        var chunks = shape.Chunks;
        for (int k = 0; k < shape.Paths.Length; k++)
        {
            var path = shape.Paths[k];
            int n = path.Length / 2;
            for (int c = 0; c < chunks[k].Length / 4; c++)
            {
                if (!PathChunks.Intersects(chunks[k], c, minX, minY, maxX, maxY)) continue;
                var (start, end) = PathChunks.Range(c, n);
                if (end < n - 1) end--; // 块与块共用端点，不重复计数
                for (int i = start; i <= end; i++)
                {
                    double x = path[i * 2], y = path[i * 2 + 1];
                    if (x >= minX && x <= maxX && y >= minY && y <= maxY) total++;
                }
            }
        }
        return total;
    }

    private HandleRef? HitHandle(GeoNode node, Point p)
    {
        var shape = _shapes.Get(node, _vp);
        if (shape == null) return null;
        var handles = VisibleHandles(shape);
        if (handles == null) return null;

        HandleRef? best = null;
        double bestD = 7.5 * 7.5;
        foreach (var (k, i) in handles)
        {
            var path = shape.Paths[k];
            var (x, y) = _vp.WorldToScreen(path[i * 2], path[i * 2 + 1]);
            double d = (x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y);
            if (d < bestD)
            {
                bestD = d;
                best = new HandleRef(k, i, false);
            }
        }
        if (best != null) return best;

        bestD = 6 * 6;
        foreach (var (k, i) in handles)
        {
            var path = shape.Paths[k];
            if (i * 2 + 3 >= path.Length) continue;
            var (x0, y0) = _vp.WorldToScreen(path[i * 2], path[i * 2 + 1]);
            var (x1, y1) = _vp.WorldToScreen(path[i * 2 + 2], path[i * 2 + 3]);
            if ((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) < 24 * 24) continue;
            double mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
            double d = (mx - p.X) * (mx - p.X) + (my - p.Y) * (my - p.Y);
            if (d < bestD)
            {
                bestD = d;
                best = new HandleRef(k, i, true);
            }
        }
        return best;
    }

    private int? HitPointIndex(GeoNode node, Point p)
    {
        var shape = _shapes.Get(node, _vp);
        if (shape == null) return null;
        for (int i = 0; i < shape.Points.Length; i += 2)
        {
            var (x, y) = _vp.WorldToScreen(shape.Points[i], shape.Points[i + 1]);
            if (MarkerHitRect(node.Icon, x, y).Contains((float)p.X, (float)p.Y)) return i / 2;
        }
        return null;
    }

    private void BeginVertexDrag(GeoNode node, HandleRef handle)
    {
        var paths = VertexEditing.ExtractPaths(node.Geometry!);
        _editNode = node;
        _dragTargets.Clear();

        Coordinate from;
        var neighbours = new List<(GeoNode Node, NetTopologySuite.Geometries.Geometry Original, NetTopologySuite.Geometries.Geometry Base)>();
        var original = node.Geometry!;
        NetTopologySuite.Geometries.Geometry baseGeom;
        if (handle.IsMid)
        {
            var a = paths[handle.Path][handle.Index];
            var b = paths[handle.Path][handle.Index + 1];
            from = new Coordinate((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            baseGeom = VertexEditing.InsertVertex(original, handle.Path, handle.Index, from);
            if (_editor.LinkedEditing.Value) InsertIntoNeighbours(node, a, b, from, neighbours);
        }
        else
        {
            from = paths[handle.Path][handle.Index];
            baseGeom = original;
            if (_editor.LinkedEditing.Value) CollectNeighbours(node, from, neighbours);
        }

        AddDragTarget(node, original, baseGeom, from);
        foreach (var (n, o, b) in neighbours) AddDragTarget(n, o, b, from);

        _dragFrom = from.Copy();
        _drag = DragMode.Vertex;
        InvalidateVisual();
    }

    private void AddDragTarget(GeoNode node, NetTopologySuite.Geometries.Geometry original, NetTopologySuite.Geometries.Geometry baseGeom, Coordinate from)
    {
        var positions = new List<(int, int)>();
        var paths = VertexEditing.ExtractPaths(baseGeom);
        for (int k = 0; k < paths.Length; k++)
        {
            for (int i = 0; i < paths[k].Length; i++)
            {
                if (paths[k][i].Equals2D(from)) positions.Add((k, i));
            }
        }
        if (positions.Count == 0) return;
        _dragTargets.Add(new DragTarget(node, original, baseGeom, positions));
        node.Geometry = baseGeom;
    }

    /// <summary>可能与某个坐标共用顶点的其他要素：先用包围盒排除，再逐个顶点比较。</summary>
    private IEnumerable<GeoNode> LinkCandidates(GeoNode except, Coordinate c)
    {
        foreach (var n in Doc.AllNodes())
        {
            if (ReferenceEquals(n, except) || n.Geometry == null || n.Kind is NodeKind.Group or NodeKind.Point) continue;
            if (!n.Geometry.EnvelopeInternal.Covers(c)) continue;
            yield return n;
        }
    }

    /// <summary>找出与拖动顶点重合的其他要素（例如相邻区域的公共边界、上级的外边界）。</summary>
    private void CollectNeighbours(GeoNode node, Coordinate c, List<(GeoNode, NetTopologySuite.Geometries.Geometry, NetTopologySuite.Geometries.Geometry)> result)
    {
        foreach (var n in LinkCandidates(node, c))
        {
            if (VertexEditing.HasCoordinate(n.Geometry!, c)) result.Add((n, n.Geometry!, n.Geometry!));
        }
    }

    /// <summary>在边中点插入顶点时，共用这条边的相邻要素也插入同一个顶点，保持边界重合。</summary>
    private void InsertIntoNeighbours(GeoNode node, Coordinate a, Coordinate b, Coordinate mid, List<(GeoNode, NetTopologySuite.Geometries.Geometry, NetTopologySuite.Geometries.Geometry)> result)
    {
        foreach (var n in LinkCandidates(node, a))
        {
            if (!n.Geometry!.EnvelopeInternal.Covers(b)) continue;
            var paths = VertexEditing.ExtractPaths(n.Geometry);
            for (int k = 0; k < paths.Length; k++)
            {
                var ring = paths[k];
                int hit = -1;
                for (int i = 0; i + 1 < ring.Length; i++)
                {
                    if ((ring[i].Equals2D(a) && ring[i + 1].Equals2D(b)) || (ring[i].Equals2D(b) && ring[i + 1].Equals2D(a)))
                    {
                        hit = i;
                        break;
                    }
                }
                if (hit >= 0)
                {
                    result.Add((n, n.Geometry, VertexEditing.InsertVertex(n.Geometry, k, hit, mid)));
                    break;
                }
            }
        }
    }

    private void DragVertexTo(Point p)
    {
        var snap = FindSnap(p, false);
        _snap = snap;
        var to = snap?.Data ?? _vp.ScreenToData(p.X, p.Y);
        foreach (var t in _dragTargets)
        {
            var paths = VertexEditing.ExtractPaths(t.Base).Select(r => (Coordinate[])r.Clone()).ToArray();
            foreach (var (k, i) in t.Positions) paths[k][i] = to.Copy();
            t.Node.Geometry = VertexEditing.Rebuild(t.Base, paths);
        }
        InvalidateVisual();
    }

    private void BeginMovePoint(GeoNode node, int index)
    {
        _editNode = node;
        _movePointIndex = index;
        _dragTargets.Clear();
        _dragTargets.Add(new DragTarget(node, node.Geometry!, node.Geometry!, new List<(int, int)>()));
        _dragFrom = null;
        _drag = DragMode.MovePoint;
    }

    private void DragPointTo(Point p)
    {
        if (_editNode == null || _dragTargets.Count == 0) return;
        var to = _vp.ScreenToData(p.X, p.Y);
        _editNode.Geometry = VertexEditing.MovePoint(_dragTargets[0].Base, _movePointIndex, to);
        InvalidateVisual();
    }

    private void CommitVertexDrag(string label)
    {
        var changes = new List<(GeoNode, NetTopologySuite.Geometries.Geometry)>();
        foreach (var t in _dragTargets)
        {
            var final = t.Node.Geometry;
            t.Node.Geometry = t.Original;
            if (final != null && !ReferenceEquals(final, t.Original)) changes.Add((t.Node, final));
        }
        _dragTargets.Clear();
        _editNode = null;
        _dragFrom = null;
        _snap = null;
        if (changes.Count > 0)
        {
            _editor.ReplaceGeometries(changes.Count > 1 ? $"{label}（联动 {changes.Count - 1} 个相邻要素）" : label, changes);
        }
        InvalidateVisual();
    }

    private void RestoreDragOriginals()
    {
        foreach (var t in _dragTargets) t.Node.Geometry = t.Original;
        _dragTargets.Clear();
        _editNode = null;
        _dragFrom = null;
    }

    private void DeleteVertex(GeoNode node, HandleRef h)
    {
        var paths = VertexEditing.ExtractPaths(node.Geometry!);
        var c = paths[h.Path][h.Index];
        var g = VertexEditing.DeleteVertex(node.Geometry!, h.Path, h.Index);
        if (g == null)
        {
            HintChanged?.Invoke(node.Kind == NodeKind.Polygon ? "面至少需要 3 个顶点。" : "线至少需要 2 个顶点。");
            return;
        }
        var changes = new List<(GeoNode, NetTopologySuite.Geometries.Geometry)> { (node, g) };
        if (_editor.LinkedEditing.Value)
        {
            // 相邻要素里同一个顶点也一并删除（删不掉的保持原样）
            foreach (var n in LinkCandidates(node, c))
            {
                var np = VertexEditing.ExtractPaths(n.Geometry!);
                for (int k = 0; k < np.Length; k++)
                {
                    int idx = Array.FindIndex(np[k], x => x.Equals2D(c));
                    if (idx < 0) continue;
                    var ng = VertexEditing.DeleteVertex(n.Geometry!, k, idx);
                    if (ng != null) changes.Add((n, ng));
                    break;
                }
            }
        }
        _vertexHover = null;
        _editor.ReplaceGeometries("删除顶点", changes);
    }

    // ───────────────────────── 覆盖层绘制 ─────────────────────────

    private void DrawVertexHandles(SKCanvas canvas)
    {
        var node = EditableNode;
        if (node == null) return;
        var shape = _shapes.Get(node, _vp);
        if (shape == null) return;

        var handles = VisibleHandles(shape);
        if (handles == null) return;

        // 边中点
        if (_drag == DragMode.None)
        {
            foreach (var (k, i) in handles)
            {
                var path = shape.Paths[k];
                if (i * 2 + 3 >= path.Length) continue;
                var (x0, y0) = _vp.WorldToScreen(path[i * 2], path[i * 2 + 1]);
                var (x1, y1) = _vp.WorldToScreen(path[i * 2 + 2], path[i * 2 + 3]);
                if ((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) < 24 * 24) continue;
                float mx = (float)((x0 + x1) / 2), my = (float)((y0 + y1) / 2);
                if (mx < -10 || my < -10 || mx > _vp.Width + 10 || my > _vp.Height + 10) continue;
                bool hot = _vertexHover is { IsMid: true } h && h.Path == k && h.Index == i;
                _fill.Color = hot ? MapStyle.Accent : new SKColor(255, 255, 255, 200);
                canvas.DrawCircle(mx, my, hot ? 5 : 3.6f, _fill);
                _stroke.Color = MapStyle.Accent.WithAlpha(hot ? (byte)255 : (byte)170);
                _stroke.StrokeWidth = 1.3f;
                canvas.DrawCircle(mx, my, hot ? 5 : 3.6f, _stroke);
            }
        }

        // 顶点
        foreach (var (k, i) in handles)
        {
            var path = shape.Paths[k];
            var (x, y) = _vp.WorldToScreen(path[i * 2], path[i * 2 + 1]);
            bool hot = _vertexHover is { IsMid: false } h && h.Path == k && h.Index == i;
            float r = hot ? 5.5f : 4f;
            var rect = new SKRect((float)x - r, (float)y - r, (float)x + r, (float)y + r);
            _fill.Color = hot ? MapStyle.Accent : SKColors.White;
            canvas.DrawRoundRect(rect, 1.5f, 1.5f, _fill);
            _stroke.Color = MapStyle.Accent;
            _stroke.StrokeWidth = 1.6f;
            canvas.DrawRoundRect(rect, 1.5f, 1.5f, _stroke);
        }
    }

    /// <summary>编辑顶点时顶点太多、没显示手柄的提示，画在最上层（点标记和标注之上），位于工具条下方。</summary>
    private void DrawVertexHint(SKCanvas canvas)
    {
        var node = EditableNode;
        if (node == null || _drag != DragMode.None || _shapes.Get(node, _vp) is not { } shape) return;
        if (VisibleHandles(shape) == null && _handleHint.Length > 0) DrawBadge(canvas, _handleHint, (float)(_vp.Width / 2), 68);
    }

    /// <summary>地图上方居中的小提示条。</summary>
    private void DrawBadge(SKCanvas canvas, string text, float centerX, float top)
    {
        var font = Font(12, false);
        float w = font.MeasureText(text);
        var rect = new SKRect(centerX - w / 2 - 12, top, centerX + w / 2 + 12, top + 26);
        _fill.Color = new SKColor(0x11, 0x18, 0x27, 0xD8);
        canvas.DrawRoundRect(rect, 13, 13, _fill);
        _text.Color = SKColors.White;
        canvas.DrawText(text, centerX, rect.Bottom - 8.5f, SKTextAlign.Center, font, _text);
    }

    private void DrawToolOverlay(SKCanvas canvas)
    {
        var tool = _editor.Tool.Value;
        int level = Level;

        // 层级检查发现的问题区域：红色斜线填充
        var highlights = _editor.Highlights.Value;
        if (!ReferenceEquals(highlights, _highlightSource))
        {
            _highlightSource = highlights;
            _highlightShapes = highlights.Select(g => ShapeCache.Build(g, 0, _vp.DataCrs, _vp.DisplayCrs)).ToList();
        }
        if (_highlightShapes.Count > 0)
        {
            using var hatch = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(6, 6),
                [MapStyle.Danger.WithAlpha(150), MapStyle.Danger.WithAlpha(150), MapStyle.Danger.WithAlpha(30), MapStyle.Danger.WithAlpha(30)],
                [0f, 0.5f, 0.5f, 1f], SKShaderTileMode.Repeat);
            foreach (var shape in _highlightShapes)
            {
                _path.Reset();
                _path.FillType = SKPathFillType.EvenOdd;
                AppendPaths(_path, shape.PathsAt(level), shape.Kind == NodeKind.Polygon);
                _fill.Shader = hatch;
                canvas.DrawPath(_path, _fill);
                _fill.Shader = null;
                _stroke.Color = MapStyle.Danger;
                _stroke.StrokeWidth = 2;
                canvas.DrawPath(_path, _stroke);
            }
        }

        // 切割预览：用不同颜色显示将切出的各块
        if (tool == EditTool.Cut && _cutPreviewShapes is { Count: > 0 })
        {
            int i = 0;
            foreach (var piece in _cutPreviewShapes)
            {
                var color = MapStyle.Parse(MapStyle.Palette[(i++ * 5 + 3) % MapStyle.Palette.Length]);
                bool polygon = piece.Kind == NodeKind.Polygon;
                _path.Reset();
                _path.FillType = SKPathFillType.EvenOdd;
                AppendPaths(_path, piece.PathsAt(level), polygon);
                if (polygon)
                {
                    _fill.Color = color.WithAlpha(90);
                    canvas.DrawPath(_path, _fill);
                    _stroke.Color = SKColors.White;
                    _stroke.StrokeWidth = 2;
                    canvas.DrawPath(_path, _stroke);
                }
                else
                {
                    _stroke.Color = color;
                    _stroke.StrokeWidth = 4;
                    canvas.DrawPath(_path, _stroke);
                }
            }
        }

        if (_drawPts.Count > 0 && tool is EditTool.DrawLine or EditTool.DrawPolygon or EditTool.Cut)
        {
            var color = tool == EditTool.Cut ? MapStyle.Danger : MapStyle.Accent;
            var screen = _drawPts.Select(c => _vp.DataToScreen(c.X, c.Y)).Select(t => new SKPoint((float)t.X, (float)t.Y)).ToList();
            SKPoint? end = null;
            if (_mouseInside && _drag == DragMode.None)
            {
                end = _snap is { } sn ? new SKPoint((float)sn.X, (float)sn.Y) : new SKPoint((float)_mouse.X, (float)_mouse.Y);
            }

            if (tool == EditTool.DrawPolygon && screen.Count >= 2)
            {
                _path.Reset();
                _path.FillType = SKPathFillType.Winding;
                _path.MoveTo(screen[0]);
                foreach (var pt in screen.Skip(1)) _path.LineTo(pt);
                if (end != null) _path.LineTo(end.Value);
                _path.Close();
                _fill.Color = color.WithAlpha(40);
                canvas.DrawPath(_path, _fill);
            }

            _path.Reset();
            _path.MoveTo(screen[0]);
            foreach (var pt in screen.Skip(1)) _path.LineTo(pt);
            _stroke.Color = SKColors.White.WithAlpha(220);
            _stroke.StrokeWidth = 5;
            canvas.DrawPath(_path, _stroke);
            _stroke.Color = color;
            _stroke.StrokeWidth = 2.4f;
            canvas.DrawPath(_path, _stroke);

            if (end != null)
            {
                using var dash = SKPathEffect.CreateDash([7f, 5f], 0);
                _stroke.PathEffect = dash;
                _stroke.StrokeWidth = 2;
                canvas.DrawLine(screen[^1], end.Value, _stroke);
                if (tool == EditTool.DrawPolygon && screen.Count >= 2) canvas.DrawLine(end.Value, screen[0], _stroke);
                _stroke.PathEffect = null;
            }

            for (int i = 0; i < screen.Count; i++)
            {
                bool first = i == 0 && tool == EditTool.DrawPolygon && screen.Count >= 3;
                float r = first ? 6 : 4.2f;
                _fill.Color = first ? color : SKColors.White;
                canvas.DrawCircle(screen[i], r, _fill);
                _stroke.Color = color;
                _stroke.StrokeWidth = 1.8f;
                canvas.DrawCircle(screen[i], r, _stroke);
            }

            if (end != null) DrawMeasureBadge(canvas, end.Value);
        }

        // 吸附提示
        if (_snap is { } s && _mouseInside && (tool != EditTool.Select || _drag == DragMode.Vertex))
        {
            var c = s.Kind == SnapKind.Edge ? new SKColor(0xF5, 0x9E, 0x0B) : new SKColor(0xD9, 0x46, 0xEF);
            _stroke.Color = SKColors.White;
            _stroke.StrokeWidth = 4;
            canvas.DrawCircle((float)s.X, (float)s.Y, 7, _stroke);
            _stroke.Color = c;
            _stroke.StrokeWidth = 2.2f;
            canvas.DrawCircle((float)s.X, (float)s.Y, 7, _stroke);
        }
    }

    /// <summary>绘制时在光标旁显示长度或面积。</summary>
    private void DrawMeasureBadge(SKCanvas canvas, SKPoint end)
    {
        var tool = _editor.Tool.Value;
        var pts = _drawPts.ToList();
        pts.Add(_snap?.Data ?? _vp.ScreenToData(_mouse.X, _mouse.Y));
        string text;
        if (tool == EditTool.DrawPolygon && pts.Count >= 3)
        {
            var ring = pts.Append(pts[0]).ToArray();
            try
            {
                var poly = Geometries.Factory.CreatePolygon(ring);
                text = "面积 " + GeoMeasure.FormatArea(GeoMeasure.Area(poly));
            }
            catch
            {
                text = "";
            }
        }
        else
        {
            text = "长度 " + GeoMeasure.FormatLength(GeoMeasure.PathLength(pts));
        }
        if (string.IsNullOrEmpty(text)) return;

        var font = Font(11.5f, false);
        float w = font.MeasureText(text);
        var rect = new SKRect(end.X + 14, end.Y + 12, end.X + 14 + w + 14, end.Y + 12 + 22);
        _fill.Color = new SKColor(0x11, 0x18, 0x27, 0xE0);
        canvas.DrawRoundRect(rect, 6, 6, _fill);
        _text.Color = SKColors.White;
        canvas.DrawText(text, rect.Left + 7, rect.Bottom - 7, SKTextAlign.Left, font, _text);
    }
}
