using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;

using GeoJsonEditor.App;
using GeoJsonEditor.Geo;
using GeoJsonEditor.IO;
using GeoJsonEditor.Map;
using GeoJsonEditor.Model;

using NetTopologySuite.Geometries;

namespace GeoJsonEditor.Ui;

/// <summary>
/// 主窗口：顶部工具栏、左侧图层树、中间地图、右侧属性面板、底部状态栏。
/// 菜单、工具栏、右键菜单和快捷键都调用同一组命令 / 编辑器方法。
/// 同一进程可以开多个窗口（每个窗口一个文档），瓦片缓存共用，窗口之间通过系统剪贴板复制粘贴要素。
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly IReadOnlyList<FileFilter> GeoJsonFilters =
    [
        new FileFilter("GeoJSON", "*.geojson", "*.json"),
        new FileFilter("所有文件", "*.*"),
    ];

    /// <summary>超过这个大小（字节）的文件在后台读取，读取时显示进度遮罩。</summary>
    private const long BackgroundReadThreshold = 2 * 1024 * 1024;

    private static TileCache? _sharedTiles;

    private static TileCache SharedTiles => _sharedTiles ??= new TileCache(TileCache.DefaultDiskRoot());

    private readonly AppSettings _settings;
    private readonly Editor _editor = new();
    private readonly TileCache _tiles = SharedTiles;
    private readonly MapCanvas _map;
    private readonly LayerPanel _layers;
    private readonly InspectorPanel _inspector;

    private readonly ObservableValue<string> _hint = new("");
    private readonly ObservableValue<string> _pointer = new("");
    private readonly ObservableValue<string> _zoomText = new("");
    private readonly ObservableValue<string> _selectionText = new("");
    private readonly ObservableValue<string> _docTitle = new("");
    private readonly ObservableValue<string> _docSubtitle = new("");
    private readonly ObservableValue<bool> _hasSelection = new(false);
    private readonly ObservableValue<bool> _canMerge = new(false);
    private readonly ObservableValue<bool> _canUndo = new(false);
    private readonly ObservableValue<bool> _canRedo = new(false);
    private readonly ObservableValue<string> _undoTip = new("撤销");
    private readonly ObservableValue<string> _redoTip = new("重做");
    private readonly ObservableValue<string> _baseMapName = new("");
    private readonly ObservableValue<bool> _isEmpty = new(true);

    private bool _closeConfirmed;
    private bool _busy;

    /// <param name="args">命令行参数，第一个存在的文件会被打开。</param>
    /// <param name="openSample">没有要打开的文件时是否打开示例数据（第一个窗口打开，“新建窗口”不打开）。</param>
    public MainWindow(AppSettings settings, string[] args, bool openSample = true)
    {
        _settings = settings;
        StyleSheet = AppStyles.Create();
        Padding = new Thickness(0);
        this.Resizable(1440, 900, minWidth: 1024, minHeight: 640)
            .StartCenterScreen()
            .Title("GeoJSON 层级编辑器");

        try
        {
            Icon = IconSource.FromResource<MainWindow>("GeoJsonEditor.app.ico");
        }
        catch (Exception)
        {
            // 图标只影响标题栏和任务栏外观，加载失败不影响使用
        }

        ApplySettings();

        _map = new MapCanvas(_editor, _tiles);
        _inspector = new InspectorPanel(_editor) { RequestSimplify = () => ShowSimplifyDialog() };
        _layers = new LayerPanel(_editor, BuildRowMenu());

        Content = BuildLayout();
        RegisterCommands();
        WireEvents();

        AllowDrop = true;
        DragOver += HandleDragOver;
        Drop += HandleDrop;
        Closing += OnClosing;
        Closed += SaveSettings;

        LoadInitialDocument(args, openSample);
    }

    // ───────────────────────── 设置 ─────────────────────────

    private void ApplySettings()
    {
        _editor.BaseMap.Value = TileSource.ById(_settings.BaseMap);
        _editor.BaseMapFade.Value = Math.Clamp(_settings.BaseMapFade, 0, 0.85);
        _editor.BaseMapGray.Value = _settings.BaseMapGray;
        _editor.DataCrs.Value = _settings.DataCrs == "gcj02" ? CoordSystem.Gcj02 : CoordSystem.Wgs84;
        _editor.ShowLabels.Value = _settings.ShowLabels;
    }

    private void SaveSettings()
    {
        _settings.BaseMap = _editor.BaseMap.Value.Id;
        _settings.BaseMapFade = _editor.BaseMapFade.Value;
        _settings.BaseMapGray = _editor.BaseMapGray.Value;
        _settings.DataCrs = _editor.DataCrs.Value == CoordSystem.Gcj02 ? "gcj02" : "wgs84";
        _settings.ShowLabels = _editor.ShowLabels.Value;
        _settings.Save();
    }

    private void LoadInitialDocument(string[] args, bool openSample)
    {
        var path = args.FirstOrDefault(File.Exists);
        if (path != null)
        {
            _editor.NewDocument();
            // 窗口先显示出来，大文件在后台读取
            Loaded += () => _ = OpenPathAsync(path);
            return;
        }

        var sample = Path.Combine(AppContext.BaseDirectory, "samples", "示例-行政区层级.geojson");
        if (!openSample || !File.Exists(sample))
        {
            _editor.NewDocument();
            return;
        }
        try
        {
            _editor.Open(sample);
        }
        catch (Exception ex)
        {
            _editor.NewDocument();
            _hint.Value = "无法打开示例数据：" + ex.Message;
        }
    }

    // ───────────────────────── 事件 ─────────────────────────

    private void WireEvents()
    {
        var doc = _editor.Doc;
        doc.Changed += _ => UpdateDocumentState();
        doc.HistoryChanged += UpdateDocumentState;
        doc.SelectionChanged += UpdateSelectionState;
        _editor.Notify += message => this.ShowToast(message);
        _editor.BaseMap.Changed += () => _baseMapName.Value = _editor.BaseMap.Value.Name;
        _baseMapName.Value = _editor.BaseMap.Value.Name;

        _map.HintChanged += text => _hint.Value = text;
        _map.PointerMoved += c => _pointer.Value = c == null ? "" : GeoMeasure.FormatLonLat(c.X, c.Y);
        _map.ViewChanged += () => _zoomText.Value = $"缩放 {_map.Zoom:0.0}  ·  比例尺 {_map.ScaleBar().Text}";
        _map.ContextRequested += ShowMapContextMenu;

        ThemeChanged += (_, t) => _map.IsDarkTheme = t.IsDark;
        Loaded += () =>
        {
            _map.IsDarkTheme = Theme.IsDark;
            _map.UpdateHint();
            _map.Focus();
        };

        UpdateDocumentState();
        UpdateSelectionState();
    }

    private void UpdateDocumentState()
    {
        var doc = _editor.Doc;
        _docTitle.Value = doc.DisplayName;
        _docSubtitle.Value = doc.IsDirty ? "未保存的修改" : doc.FilePath == null ? "新文档" : "已保存";
        Title = (doc.IsDirty ? "● " : "") + doc.DisplayName + " — GeoJSON 层级编辑器";
        _canUndo.Value = doc.CanUndo;
        _canRedo.Value = doc.CanRedo;
        _undoTip.Value = doc.UndoLabel is { } u ? $"撤销「{u}」（⌘/Ctrl+Z）" : "撤销（⌘/Ctrl+Z）";
        _redoTip.Value = doc.RedoLabel is { } r ? $"重做「{r}」（⌘/Ctrl+Shift+Z）" : "重做（⌘/Ctrl+Shift+Z）";
        _isEmpty.Value = doc.Count == 0;
        _canMerge.Value = _editor.CanMerge();
        RequerySuggested();
    }

    private void UpdateSelectionState()
    {
        var sel = _editor.Doc.Selection;
        _hasSelection.Value = sel.Count > 0;
        _canMerge.Value = _editor.CanMerge();
        _selectionText.Value = sel.Count switch
        {
            0 => $"共 {_editor.Doc.Count:N0} 个要素",
            1 => $"已选：{sel[0].DisplayName}",
            _ => $"已选 {sel.Count:N0} 个要素",
        };
        _map.UpdateHint();
        RequerySuggested();
    }

    // ───────────────────────── 快捷键 ─────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (_busy)
        {
            e.Handled = true;
            return;
        }
        if (FocusManager.FocusedElement is TextBase) return;
        if (e.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;

        switch (e.Key)
        {
            case Key.V: SetTool(EditTool.Select); break;
            case Key.P: SetTool(EditTool.DrawPoint); break;
            case Key.L: SetTool(EditTool.DrawLine); break;
            case Key.A: SetTool(EditTool.DrawPolygon); break;
            case Key.X: SetTool(EditTool.Cut); break;
            case Key.M: _editor.Merge(); break;
            case Key.H: ToggleSelectionVisibility(); break;
            case Key.F when e.Modifiers == ModifierKeys.Shift: _map.ZoomToAll(); break;
            case Key.F: ZoomToSelection(); break;
            case Key.D0: _map.ZoomToAll(); break;
            case Key.Add: _map.ZoomBy(1); break;
            case Key.Subtract: _map.ZoomBy(-1); break;
            case Key.F2: _inspector.FocusName(); break;
            case Key.Delete or Key.Backspace:
                if (_editor.Doc.Selection.Count > 0) _editor.DeleteSelection();
                else return;
                break;
            case Key.Escape:
                if (_editor.Tool.Value != EditTool.Select) SetTool(EditTool.Select);
                else _editor.Doc.ClearSelection();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void SetTool(EditTool tool) => _editor.Tool.Value = tool;

    private void ToggleSelectionVisibility()
    {
        var sel = _editor.Doc.Selection.ToList();
        if (sel.Count > 0) _editor.SetVisible(sel, !sel.All(n => n.Visible));
    }

    private void ZoomToSelection()
    {
        if (_editor.Doc.Selection.Count > 0) _map.ZoomTo(_editor.Doc.Selection.ToList());
        else _map.ZoomToAll();
    }

    // ───────────────────────── 剪贴板 ─────────────────────────

    private void CopySelection()
    {
        var count = _editor.SelectedRoots().Sum(r => r.SelfAndDescendants().Count());
        var text = _editor.CopySelection();
        if (text == null)
        {
            this.ShowToast("先选择要复制的要素。");
            return;
        }
        this.ShowToast(AppClipboard.SetText(text)
            ? $"已复制 {count:N0} 个要素（含下级），可以粘贴到其他窗口或其他软件。"
            : "无法写入系统剪贴板，只能在本程序的窗口之间粘贴。");
    }

    private void CutSelection()
    {
        if (_editor.Doc.Selection.Count == 0)
        {
            this.ShowToast("先选择要剪切的要素。");
            return;
        }
        var text = _editor.CopySelection();
        if (text == null) return;
        AppClipboard.SetText(text);
        _editor.DeleteSelection(label: "剪切");
        this.ShowToast("已剪切，粘贴时会连同下级一起还原。");
    }

    private async void PasteFromClipboard()
    {
        if (_busy) return;
        var text = AppClipboard.GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            this.ShowToast("剪贴板是空的。");
            return;
        }

        ClipboardContent? content;
        if (text.Length > 1_000_000)
        {
            // 大段文本在后台解析
            _busy = true;
            var busy = this.CreateBusyIndicator("正在读取剪贴板里的要素…");
            try
            {
                content = await Task.Run(() => GeoClipboard.Parse(text));
            }
            finally
            {
                busy.Dispose();
                _busy = false;
            }
        }
        else
        {
            content = GeoClipboard.Parse(text);
        }

        if (content == null)
        {
            this.ShowToast("剪贴板里没有可以识别的 GeoJSON、WKT 或经纬度坐标。");
            return;
        }
        var pasted = _editor.Paste(content);
        if (pasted.Count > 0 && !_map.IsInView(pasted)) _map.ZoomTo(pasted);
    }

    private void CopyCoordinate(Coordinate c)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{c.X:0.######}, {c.Y:0.######}");
        AppClipboard.SetText(text);
        this.ShowToast($"已复制坐标 {text}（经度, 纬度，{(_editor.DataCrs.Value == CoordSystem.Gcj02 ? "GCJ-02" : "WGS-84")}）");
    }

    // ───────────────────────── 文件 ─────────────────────────

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_editor.Doc.IsDirty) return true;
        // Accept → true（保存），Destructive → null（不保存），Reject → false（取消）
        var answer = await MessageBox.PromptAsync(new MessageBoxOptions
        {
            Owner = this,
            Message = $"是否保存对「{_editor.Doc.DisplayName}」的修改？",
            Detail = "不保存的话，这些修改会丢失。",
            Icon = PromptIconKind.Warning,
            Buttons =
            [
                new MessageButton("保存", MessageButtonRole.Accept),
                new MessageButton("不保存", MessageButtonRole.Destructive),
                new MessageButton("取消", MessageButtonRole.Reject),
            ],
        });
        return answer switch
        {
            true => SaveDocument(saveAs: false),
            null => true,
            false => false,
        };
    }

    private async void NewDocument()
    {
        if (_busy || !await ConfirmDiscardAsync()) return;
        _editor.NewDocument();
        _map.ZoomToAll();
    }

    /// <summary>新建窗口，可以同时开着几张地图互相复制粘贴。</summary>
    private void OpenNewWindow(string? path = null)
    {
        var window = new MainWindow(_settings, path != null ? [path] : [], openSample: false);
        window.Show();
        var p = Position;
        window.MoveTo(p.X + 36, p.Y + 36);
    }

    private string? PickGeoJsonFile(string title)
        => FileDialog.OpenFile(new OpenFileDialogOptions
        {
            Owner = this,
            Title = title,
            Filters = GeoJsonFilters,
            InitialDirectory = InitialDirectory(),
        });

    private async void OpenDocument()
    {
        if (_busy || !await ConfirmDiscardAsync()) return;
        var path = PickGeoJsonFile("打开 GeoJSON");
        if (path != null) await OpenPathAsync(path);
    }

    private void OpenInNewWindow()
    {
        var path = PickGeoJsonFile("在新窗口中打开 GeoJSON");
        if (path != null) OpenNewWindow(path);
    }

    /// <summary>
    /// 打开文件。小文件直接读；大文件在后台线程读取并准备好地图投影（含分级简化），界面不卡，
    /// 读取超过一小会儿时显示进度遮罩。
    /// </summary>
    private async Task OpenPathAsync(string path)
    {
        if (_busy) return;
        try
        {
            var (result, shapes) = await ReadInBackground(path, $"正在打开 {Path.GetFileName(path)}…");
            if (shapes != null) _map.AdoptShapes(shapes);
            _editor.Load(result, path);
            _settings.AddRecent(path);
            _map.ZoomToAll();
            string msg = $"已打开 {Path.GetFileName(path)}：{result.FeatureCount:N0} 个要素";
            if (result.LinkedCount > 0) msg += $"，识别出 {result.LinkedCount:N0} 个上下级关系";
            this.ShowToast(msg + "。" + string.Join("", result.Warnings));
        }
        catch (Exception ex)
        {
            _ = MessageBox.NotifyAsync("无法打开文件", PromptIconKind.Error, ex.Message, this);
        }
    }

    /// <summary>读取文件（并在后台准备投影）。文件小时在 UI 线程上直接读，免得遮罩一闪而过。</summary>
    private async Task<(ReadResult Result, List<(GeoNode, ProjectedShape)>? Shapes)> ReadInBackground(string path, string message)
    {
        long size = new FileInfo(path).Length;
        if (size < BackgroundReadThreshold) return (GeoJsonIO.ReadFile(path), null);

        _busy = true;
        var (data, display) = _map.ShapeCrs;
        var task = Task.Run(() =>
        {
            var r = GeoJsonIO.ReadFile(path);
            var s = ShapeCache.BuildMany(r.Roots.SelectMany(x => x.SelfAndDescendants()), data, display);
            return (r, (List<(GeoNode, ProjectedShape)>?)s);
        });
        IBusyIndicator? busy = null;
        try
        {
            if (await Task.WhenAny(task, Task.Delay(200)) != task)
            {
                busy = this.CreateBusyIndicator(message + $"（{size / 1024.0 / 1024.0:0.#} MB）");
            }
            return await task;
        }
        finally
        {
            busy?.Dispose();
            _busy = false;
        }
    }

    private void ImportDocument()
    {
        if (_busy) return;
        var path = PickGeoJsonFile("导入 GeoJSON 到当前文档");
        if (path != null) _ = ImportPathAsync(path);
    }

    private async Task ImportPathAsync(string path)
    {
        if (_busy) return;
        try
        {
            var parent = Editor.SuggestDrawTarget(_editor.Doc.Primary);
            var (result, shapes) = await ReadInBackground(path, $"正在导入 {Path.GetFileName(path)}…");
            if (shapes != null) _map.AdoptShapes(shapes);
            _editor.AddImported(result.Roots, parent);
            _map.ZoomTo(result.Roots);
            this.ShowToast($"已导入 {result.FeatureCount:N0} 个要素到「{parent?.DisplayName ?? "根级"}」。");
        }
        catch (Exception ex)
        {
            _ = MessageBox.NotifyAsync("无法导入文件", PromptIconKind.Error, ex.Message, this);
        }
    }

    private bool SaveDocument(bool saveAs)
    {
        var path = _editor.Doc.FilePath;
        bool isSample = path != null && path.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal);
        if (saveAs || path == null || isSample)
        {
            path = FileDialog.SaveFile(new SaveFileDialogOptions
            {
                Owner = this,
                Title = "保存 GeoJSON",
                Filters = GeoJsonFilters,
                FileName = _editor.Doc.DisplayName + ".geojson",
                DefaultExtension = "geojson",
                InitialDirectory = InitialDirectory(),
            });
            if (path == null) return false;
        }
        try
        {
            _editor.Save(path);
            _settings.AddRecent(path);
            this.ShowToast("已保存到 " + Path.GetFileName(path));
            return true;
        }
        catch (Exception ex)
        {
            _ = MessageBox.NotifyAsync("保存失败", PromptIconKind.Error, ex.Message, this);
            return false;
        }
    }

    private void ExportSelection()
    {
        if (_editor.Doc.Selection.Count == 0) return;
        var first = _editor.SelectedRoots()[0];
        var path = FileDialog.SaveFile(new SaveFileDialogOptions
        {
            Owner = this,
            Title = "导出所选要素（含全部下级）",
            Filters = GeoJsonFilters,
            FileName = first.DisplayName + ".geojson",
            DefaultExtension = "geojson",
            InitialDirectory = InitialDirectory(),
        });
        if (path == null) return;
        try
        {
            _editor.ExportSelection(path);
            this.ShowToast("已导出到 " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _ = MessageBox.NotifyAsync("导出失败", PromptIconKind.Error, ex.Message, this);
        }
    }

    private string? InitialDirectory()
    {
        var p = _editor.Doc.FilePath ?? _settings.RecentFiles.FirstOrDefault();
        if (p != null && !p.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal)) return Path.GetDirectoryName(p);
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private async void OnClosing(ClosingEventArgs e)
    {
        if (_closeConfirmed || !_editor.Doc.IsDirty) return;
        using var deferral = e.GetDeferral();
        bool ok = await ConfirmDiscardAsync();
        if (!ok) e.Cancel = true;
        else _closeConfirmed = true;
    }

    // ───────────────────────── 拖入文件 ─────────────────────────

    private static string? DroppedGeoJson(IDataObject data)
    {
        if (!data.TryGetData<IReadOnlyList<string>>(StandardDataFormats.StorageItems, out var paths)) return null;
        return paths.FirstOrDefault(p =>
            File.Exists(p) && (p.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
    }

    private void HandleDragOver(DragEventArgs e)
    {
        if (!_busy && DroppedGeoJson(e.Data) != null)
        {
            e.Effect = DragDropEffects.Copy;
            e.Accepted = true;
        }
    }

    private async void HandleDrop(DragEventArgs e)
    {
        var path = DroppedGeoJson(e.Data);
        if (path == null || _busy) return;
        e.Effect = DragDropEffects.Copy;
        e.Accepted = true;
        if (_editor.Doc.Count == 0)
        {
            await OpenPathAsync(path);
            return;
        }
        // Accept → true（导入），Destructive → null（打开），Reject → false（取消）
        var answer = await MessageBox.PromptAsync(new MessageBoxOptions
        {
            Owner = this,
            Message = $"如何使用「{Path.GetFileName(path)}」？",
            Detail = "导入：加到当前文档里所选节点下；打开：替换当前文档。",
            Icon = PromptIconKind.Question,
            Buttons =
            [
                new MessageButton("导入", MessageButtonRole.Accept),
                new MessageButton("打开", MessageButtonRole.Destructive),
                new MessageButton("取消", MessageButtonRole.Reject),
            ],
        });
        if (answer == true) await ImportPathAsync(path);
        else if (answer == null && await ConfirmDiscardAsync()) await OpenPathAsync(path);
    }
}
