using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using GeoJsonEditor.App;
using GeoJsonEditor.Geo;

namespace GeoJsonEditor.Ui;

/// <summary>
/// “简化边界”对话框：选范围和允许偏差，后台计算出简化后的顶点数作为预览，确认后作为一步撤销应用。
/// </summary>
public static class SimplifyDialog
{
    private static readonly (double Meters, string Text)[] Tolerances =
    [
        (1, "1 m"), (5, "5 m"), (10, "10 m"), (20, "20 m"), (50, "50 m"),
        (100, "100 m"), (200, "200 m"), (500, "500 m"), (1000, "1 km"),
    ];

    private static double _lastTolerance = 10;

    public static async Task ShowAsync(Window owner, Editor editor)
    {
        bool hasSelection = editor.Doc.Selection.Count > 0;
        bool useSelection = hasSelection;
        double tolerance = _lastTolerance;

        CancellationTokenSource? cts = null;
        Task<SimplifyResult?>? pending = null;
        (bool Selection, double Tolerance)? pendingKey = null;

        var summary = new TextBlock().FontSize(12.5).TextWrapping(TextWrapping.Wrap);
        var detail = new TextBlock().FontSize(12).TextWrapping(TextWrapping.Wrap).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t));
        var ring = new ProgressRing().Width(16).Height(16).CenterVertical();
        var apply = new Button().StyleName(AppStyles.Primary).Content("应用简化").MinWidth(96);
        var cancel = new Button().StyleName(AppStyles.Ghost).Content("取消").MinWidth(72);

        async void Recompute()
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            var items = editor.SimplifyCandidates(useSelection);
            var key = (useSelection, tolerance);
            pendingKey = key;
            ring.IsActive = true;
            ring.IsVisible = true;
            apply.IsEnabled = false;
            summary.Text = items.Count == 0 ? "范围内没有面或线。" : $"正在计算 {items.Count:N0} 个面和线…";
            detail.Text = "";
            if (items.Count == 0)
            {
                ring.IsActive = false;
                ring.IsVisible = false;
                pending = Task.FromResult<SimplifyResult?>(null);
                return;
            }
            var task = Task.Run(() =>
            {
                try
                {
                    return (SimplifyResult?)TopoSimplifier.Simplify(items, tolerance, token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            });
            pending = task;
            var result = await task;
            if (token.IsCancellationRequested || pendingKey != key) return;
            ring.IsActive = false;
            ring.IsVisible = false;
            if (result == null) return;
            double saved = result.VerticesBefore == 0 ? 0 : 1 - (double)result.VerticesAfter / result.VerticesBefore;
            summary.Text = $"顶点 {result.VerticesBefore:N0} → {result.VerticesAfter:N0}（减少 {(saved * 100).ToString("0.#", CultureInfo.InvariantCulture)}%）";
            var notes = new List<string> { $"{result.Features:N0} 个面和线，其中 {result.Changes.Count:N0} 个会改变" };
            if (result.DroppedParts > 0) notes.Add($"{result.DroppedParts:N0} 个小于偏差的小块或空洞会被去掉");
            if (result.Repaired > 0) notes.Add($"{result.Repaired:N0} 个要素简化后自相交，已自动修复");
            detail.Text = string.Join("；", notes) + "。";
            apply.IsEnabled = result.Changes.Count > 0;
        }

        // 范围
        var scopeSel = new ClickToggle(AppStyles.Chip, new TextBlock().Text("所选要素（含下级）"), useSelection, stayChecked: true) { IsEnabled = hasSelection };
        var scopeAll = new ClickToggle(AppStyles.Chip, new TextBlock().Text("整个文档"), !useSelection, stayChecked: true);
        scopeSel.Clicked += () =>
        {
            useSelection = true;
            scopeAll.IsChecked = false;
            Recompute();
        };
        scopeAll.Clicked += () =>
        {
            useSelection = false;
            scopeSel.IsChecked = false;
            Recompute();
        };

        // 允许偏差
        var chips = new WrapPanel().Spacing(6);
        var chipList = new List<(ClickToggle Chip, double Meters)>();
        foreach (var (meters, text) in Tolerances)
        {
            var chip = new ClickToggle(AppStyles.Chip, new TextBlock().Text(text), Math.Abs(meters - tolerance) < 1e-9, stayChecked: true);
            chip.Margin = new Thickness(0, 0, 0, 6);
            chip.Clicked += () =>
            {
                tolerance = meters;
                foreach (var (c, m) in chipList) c.IsChecked = Math.Abs(m - meters) < 1e-9;
                Recompute();
            };
            chipList.Add((chip, meters));
            chips.Add(chip);
        }

        static TextBlock Caption(string text) => new TextBlock().Text(text).FontSize(12).SemiBold().WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t));

        var preview = new Border()
            .Padding(14, 10)
            .CornerRadius(8)
            .WithTheme((t, b) => b.Background(UiColors.Card(t)))
            .Child(new DockPanel().Spacing(10).Children(
                ring.DockLeft(),
                new StackPanel().Vertical().Spacing(4).Children(summary, detail)));

        var dialog = new Window
        {
            Title = "简化边界",
            StartupLocation = WindowStartupLocation.CenterOwner,
            WindowSize = WindowSize.FitContentHeight(600, 640),
            Padding = new Thickness(0),
        };
        dialog.StyleSheet = AppStyles.Create();

        cancel.Click += () => dialog.Close();
        apply.Click += async () =>
        {
            if (pending == null) return;
            apply.IsEnabled = false;
            var result = await pending;
            if (result == null || result.Changes.Count == 0) return;
            _lastTolerance = tolerance;
            editor.ApplySimplify(result, tolerance);
            dialog.Close();
        };

        dialog.Content = new StackPanel().Vertical().Spacing(16).Margin(24, 20).Children(
            new StackPanel().Vertical().Spacing(6).Children(
                new TextBlock().Text("简化边界").FontSize(16).SemiBold(),
                new TextBlock()
                    .Text("去掉对形状影响很小的顶点，文件更小、显示和编辑更快。相邻区域的公共边界、上下级重合的边界会一起简化，简化后依然严丝合缝。")
                    .FontSize(12.5)
                    .TextWrapping(TextWrapping.Wrap)
                    .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t))),
            new StackPanel().Vertical().Spacing(8).Children(
                Caption("范围"),
                new WrapPanel().Spacing(6).Children(scopeSel, scopeAll)),
            new StackPanel().Vertical().Spacing(8).Children(
                Caption("允许偏差（简化后的边界离原来的边界最远多少）"),
                chips),
            preview,
            new StackPanel().Horizontal().Spacing(8).Right().Children(cancel, apply));

        dialog.Closed += () => cts?.Cancel();
        Recompute();
        await dialog.ShowDialogAsync(owner);
    }
}
