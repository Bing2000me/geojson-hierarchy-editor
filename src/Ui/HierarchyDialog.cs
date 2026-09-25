using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using GeoJsonEditor.Geo;
using GeoJsonEditor.Model;

namespace GeoJsonEditor.Ui;

/// <summary>
/// “自动识别上下级关系”对话框：先选依据（属性字段、空间包含关系），在后台识别，
/// 然后预览结果（各层级的数量、已确认 / 待确认 / 冲突的关系数），逐条修改待确认和有冲突的要素，最后应用。
/// 识别结果只进入程序的内部数据（层级树），不改动原始 GeoJSON；保存时是否写入层级字段由用户决定。
/// </summary>
public static class HierarchyDialog
{
    public enum Purpose
    {
        /// <summary>打开文件前询问。</summary>
        Open,
        /// <summary>导入到当前文档前询问。</summary>
        Import,
        /// <summary>对当前文档重新识别（编辑菜单）。</summary>
        Document,
    }

    /// <param name="Cancelled">用户取消了打开 / 导入。</param>
    /// <param name="Detection">要应用的识别结果；为 null 表示不识别，按文件原样打开。</param>
    public sealed record Outcome(bool Cancelled, HierarchyDetection? Detection);

    /// <summary>预览里最多逐条列出多少个要素（其余按建议应用）。</summary>
    private const int MaxRows = 300;

    /// <param name="subjects">要确定上级的要素（先序）。</param>
    /// <param name="pool">可以作为上级的全部要素（包含 <paramref name="subjects"/>）。</param>
    public static async Task<Outcome> ShowAsync(Window owner, AppSettings settings, Purpose purpose, string subjectName,
        IReadOnlyList<GeoNode> subjects, IReadOnlyList<GeoNode> pool, int existingLinks)
    {
        var result = new Outcome(true, null);

        var dialog = new Window
        {
            Title = purpose == Purpose.Document ? "识别层级结构" : "识别上下级关系",
            StartupLocation = WindowStartupLocation.CenterOwner,
            WindowSize = WindowSize.FitContentHeight(660, 760),
            Padding = new Thickness(0),
        };
        dialog.StyleSheet = AppStyles.Create();

        var body = new StackPanel().Vertical().Spacing(16);
        var buttons = new StackPanel().Horizontal().Spacing(8).Right();
        var footerLeft = new StackPanel().Horizontal().Spacing(8).CenterVertical();
        dialog.Content = new DockPanel().Margin(24, 20).Children(
            new DockPanel().Margin(0, 16, 0, 0).DockBottom().Children(footerLeft.DockLeft(), buttons),
            body);

        CancellationTokenSource? running = null;
        dialog.Closed += () => running?.Cancel();

        Button Make(string text, Action click, string style = AppStyles.Ghost)
            => new Button().StyleName(style).Content(text).MinWidth(style == AppStyles.Primary ? 108 : 76).OnClick(click);

        void SetButtons(params Button[] list)
        {
            buttons.Clear();
            foreach (var b in list) buttons.Add(b);
        }

        FrameworkElement Header(string icon, string title, string subtitle) => new DockPanel().Spacing(14).Children(
            new IconView(icon, 28).DockLeft().Top().WithTheme((t, i) => i.Tint = t.Palette.Accent),
            new StackPanel().Vertical().Spacing(4).Children(
                new TextBlock().Text(title).FontSize(16).SemiBold(),
                new TextBlock().Text(subtitle).FontSize(12.5).TextWrapping(TextWrapping.Wrap).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t))));

        // ───────── 第一步：选依据 ─────────

        var detect = new ObservableValue<bool>(purpose == Purpose.Document || settings.DetectByAttributes || settings.DetectBySpace);
        var byAttributes = new ObservableValue<bool>(settings.DetectByAttributes);
        var bySpace = new ObservableValue<bool>(settings.DetectBySpace);
        var keepExisting = new ObservableValue<bool>(true);
        var askAgain = new ObservableValue<bool>(settings.AskHierarchyOnOpen);

        void ShowOptions()
        {
            footerLeft.Clear();
            string title = purpose switch
            {
                Purpose.Open => $"打开「{subjectName}」",
                Purpose.Import => $"导入「{subjectName}」",
                _ => "识别层级结构",
            };
            string summary = Describe(subjects) + (purpose == Purpose.Document
                ? "。按属性字段和空间位置重新整理上下级关系，结果可以预览、修改，作为一步撤销应用。"
                : existingLinks > 0
                    ? $"。文件里已有 {existingLinks:N0} 个上下级关系。"
                    : "。文件里没有层级字段，所有要素都在同一级。");

            var sub = new StackPanel().Vertical().Spacing(8).Margin(26, 0, 0, 0).Children(
                new TextBlock()
                    .Text("根据属性字段和空间包含关系尝试建立层级结构。原始 GeoJSON 不会被修改。")
                    .FontSize(12)
                    .TextWrapping(TextWrapping.Wrap)
                    .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)),
                new WrapPanel().Spacing(16).Children(
                    new CheckBox().Content("属性字段（上级编号、区划代码等）").BindIsChecked(byAttributes),
                    new CheckBox().Content("空间包含关系").BindIsChecked(bySpace)));
            if (purpose == Purpose.Document)
            {
                sub.Add(new CheckBox().Content("保留现有的上下级关系，只为最上层的要素找上级").BindIsChecked(keepExisting));
            }
            sub.BindIsVisible(detect);

            var card = new Border()
                .Padding(14, 12)
                .CornerRadius(8)
                .WithTheme((t, b) => b.Background(UiColors.Card(t)))
                .Child(new StackPanel().Vertical().Spacing(8).Children(
                    purpose == Purpose.Document
                        ? new TextBlock().Text("识别依据").SemiBold()
                        : new CheckBox().Content("自动识别上下级关系").BindIsChecked(detect),
                    sub));

            body.Clear();
            body.Add(Header(Icons.Hierarchy, title, summary));
            body.Add(card);

            if (purpose != Purpose.Document)
            {
                footerLeft.Add(new CheckBox().Content("打开没有层级的文件时询问").BindIsChecked(askAgain)
                    .ToolTip("取消后打开文件时不再询问，需要时可以在“编辑 → 识别层级结构”里再识别"));
            }

            var primary = Make("", Start, AppStyles.Primary);
            void SyncPrimary()
            {
                primary.Content(!detect.Value ? (purpose == Purpose.Import ? "导入" : "打开") : "识别并预览");
                primary.IsEnabled = !detect.Value || byAttributes.Value || bySpace.Value;
            }
            detect.Changed += SyncPrimary;
            byAttributes.Changed += SyncPrimary;
            bySpace.Changed += SyncPrimary;
            SyncPrimary();
            SetButtons(Make("取消", dialog.Close), primary);
        }

        void SaveChoices()
        {
            settings.AskHierarchyOnOpen = askAgain.Value;
            if (detect.Value)
            {
                settings.DetectByAttributes = byAttributes.Value;
                settings.DetectBySpace = bySpace.Value;
            }
            settings.Save();
        }

        async void Start()
        {
            SaveChoices();
            if (!detect.Value)
            {
                result = new Outcome(false, null);
                dialog.Close();
                return;
            }

            // ───────── 第二步：在后台识别 ─────────
            footerLeft.Clear();
            var ring = new ProgressRing { IsActive = true }.Width(22).Height(22).CenterVertical();
            body.Clear();
            body.Add(new DockPanel().Spacing(14).Children(
                ring.DockLeft(),
                new StackPanel().Vertical().Spacing(4).Children(
                    new TextBlock().Text("正在识别上下级关系…").FontSize(16).SemiBold(),
                    new TextBlock().Text($"共 {subjects.Count:N0} 个要素，大数据可能需要几秒钟。").FontSize(12.5)
                        .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)))));
            running = new CancellationTokenSource();
            var token = running.Token;
            SetButtons(Make("取消", () => running.Cancel()));
            var options = new DetectOptions(byAttributes.Value, bySpace.Value, purpose != Purpose.Document || keepExisting.Value);
            try
            {
                var detection = await Task.Run(() => HierarchyDetector.Detect(subjects, pool, options, token), token);
                ShowResults(detection);
            }
            catch (OperationCanceledException)
            {
                if (dialog.IsVisible) ShowOptions();
            }
            catch (Exception e)
            {
                ShowOptions();
                body.Add(new TextBlock().Text("识别时出错：" + e.Message).TextWrapping(TextWrapping.Wrap).WithTheme((t, tb) => tb.Foreground = UiColors.Danger(t)));
            }
        }

        // ───────── 第三步：预览和修改 ─────────

        void ShowResults(HierarchyDetection detection)
        {
            footerLeft.Clear();
            body.Clear();

            int confirmed = detection.ConfirmedLinks;
            int pending = detection.Count(LinkStatus.Pending);
            int conflicts = detection.Count(LinkStatus.Conflict);
            string rules = detection.Rules.Count > 0 ? "依据：" + string.Join("、", detection.Rules) + "。" : "";
            body.Add(Header(Icons.Check, "自动识别完成", rules + "结果先保存在程序里，不会改动原始文件。"));

            var levels = new StackPanel().Vertical().Spacing(2);
            var edited = new TextBlock().FontSize(12).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t));
            void RefreshLevels()
            {
                levels.Clear();
                var rows = detection.LevelSummary(out int points);
                foreach (var (name, count) in rows) levels.Add(StatRow(name, count));
                if (points > 0) levels.Add(StatRow("点标记", points));
                int changed = detection.Proposals.Count(p => !ReferenceEquals(p.Chosen, p.Suggested));
                edited.Text = changed > 0 ? $"已手动修改 {changed:N0} 条" : "";
            }
            RefreshLevels();

            var relations = new StackPanel().Vertical().Spacing(2).Children(
                StatRow("已确认关系", confirmed),
                StatRow("待确认", pending, pending > 0 ? UiColors.Warning : null),
                StatRow("存在冲突", conflicts, conflicts > 0 ? UiColors.Danger : null),
                StatRow("没有上级", detection.Proposals.Count(p => p.Suggested == null)));

            body.Add(new Grid().Columns("*,*").Spacing(12).Children(
                StatCard("层级", levels).Column(0),
                StatCard("关系", relations).Column(1)));

            var review = detection.Proposals
                .Where(p => p.Status is LinkStatus.Pending or LinkStatus.Conflict)
                .OrderBy(p => p.Status == LinkStatus.Conflict ? 0 : 1)
                .ToList();
            if (review.Count > 0)
            {
                var list = new StackPanel().Vertical().Spacing(2);
                foreach (var p in review.Take(MaxRows)) list.Add(ReviewRow(p, RefreshLevels));
                if (review.Count > MaxRows)
                {
                    list.Add(new TextBlock().Text($"……另有 {review.Count - MaxRows:N0} 条没有列出，应用时采用建议的上级。").FontSize(12).Margin(8, 6)
                        .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)));
                }
                body.Add(new StackPanel().Vertical().Spacing(8).Children(
                    new DockPanel().Children(
                        edited.DockRight().CenterVertical(),
                        new TextBlock().Text($"需要确认的 {review.Count:N0} 个要素").SemiBold().CenterVertical()),
                    new TextBlock()
                        .Text("右边默认是建议的上级（有属性字段时以属性为准），可以逐个改成其他候选，或者不设上级。")
                        .FontSize(12)
                        .TextWrapping(TextWrapping.Wrap)
                        .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)),
                    new Border()
                        .CornerRadius(8)
                        .BorderThickness(1)
                        .WithTheme((t, b) => b.BorderBrush(UiColors.Divider(t)))
                        .Child(new ScrollViewer().NoHorizontalScroll().MaxHeight(300).Content(list.Margin(4)))));
            }

            string skip = purpose switch
            {
                Purpose.Open => "不识别，按原样打开",
                Purpose.Import => "不识别，按原样导入",
                _ => "取消",
            };
            SetButtons(
                Make(skip, () =>
                {
                    result = purpose == Purpose.Document ? new Outcome(true, null) : new Outcome(false, null);
                    dialog.Close();
                }),
                Make(purpose == Purpose.Document ? "应用" : "应用层级", () =>
                {
                    detection.BreakCycles();
                    result = new Outcome(false, detection);
                    dialog.Close();
                }, AppStyles.Primary));
        }

        ShowOptions();
        await dialog.ShowDialogAsync(owner);
        return result;
    }

    /// <summary>“1,700 个要素：413 个面、1,287 个点”。</summary>
    private static string Describe(IReadOnlyList<GeoNode> nodes)
    {
        int polygons = 0, lines = 0, points = 0, groups = 0;
        foreach (var n in nodes)
        {
            switch (n.Kind)
            {
                case NodeKind.Polygon: polygons++; break;
                case NodeKind.Line: lines++; break;
                case NodeKind.Point: points++; break;
                default: groups++; break;
            }
        }
        var parts = new List<string>();
        if (polygons > 0) parts.Add($"{polygons:N0} 个面");
        if (lines > 0) parts.Add($"{lines:N0} 条线");
        if (points > 0) parts.Add($"{points:N0} 个点");
        if (groups > 0) parts.Add($"{groups:N0} 个分组");
        return $"{nodes.Count:N0} 个要素：" + string.Join("、", parts);
    }

    private static FrameworkElement StatCard(string title, UIElement content) => new Border()
        .Padding(14, 10)
        .CornerRadius(8)
        .WithTheme((t, b) => b.Background(UiColors.Card(t)))
        .Child(new StackPanel().Vertical().Spacing(6).Children(
            new TextBlock().Text(title).FontSize(11.5).SemiBold().WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)),
            content));

    private static FrameworkElement StatRow(string label, int value, Func<Theme, Color>? tint = null)
    {
        var number = new TextBlock().Text(value.ToString("N0", CultureInfo.InvariantCulture)).FontSize(13).SemiBold().Right();
        if (tint != null) number.WithTheme((t, tb) => tb.Foreground = tint(t));
        return new DockPanel().Children(number.DockRight(), new TextBlock().Text(label).FontSize(13));
    }

    /// <summary>待确认 / 冲突的一行：名称、原因，右边选上级。</summary>
    private static FrameworkElement ReviewRow(LinkProposal p, Action changed)
    {
        // 候选：建议的上级、其他候选，最后是“不设上级”
        var options = new List<GeoNode?>();
        void AddOption(GeoNode? n)
        {
            if (!options.Any(o => ReferenceEquals(o, n))) options.Add(n);
        }
        AddOption(p.Suggested);
        foreach (var c in p.Candidates) AddOption(c.Parent);
        options.Remove(null);
        options.Add(null);

        var names = options.Select(n => n?.DisplayName).ToList();
        var labels = options.Select(n =>
        {
            if (n == null) return "不设上级（放在最上层）";
            var candidate = p.Candidates.FirstOrDefault(c => ReferenceEquals(c.Parent, n));
            string text = n.DisplayName;
            // 同名的候选带上 id 区分（太长的只留末尾）
            if (names.Count(x => x == n.DisplayName) > 1) text += $"（{(n.Id.Length > 10 ? "…" + n.Id[^9..] : n.Id)}）";
            if (candidate != null)
            {
                text += candidate.Basis == "空间" && candidate.Share >= 0 ? $" · 空间 {Math.Round(candidate.Share * 100):0}%" : $" · {candidate.Basis}";
            }
            return text;
        }).ToArray();

        var combo = new ComboBox().Items(labels).Width(270).CenterVertical();
        combo.SelectedIndex = Math.Max(0, options.FindIndex(o => ReferenceEquals(o, p.Chosen)));
        combo.OnSelectionChanged(_ =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < options.Count)
            {
                p.Chosen = options[combo.SelectedIndex];
                changed();
            }
        });

        bool conflict = p.Status == LinkStatus.Conflict;
        var tag = new Border()
            .Padding(6, 1)
            .CornerRadius(4)
            .CenterVertical()
            .WithTheme((t, b) => b.Background((conflict ? UiColors.Danger(t) : UiColors.Warning(t)).WithAlpha(36)))
            .Child(new TextBlock().Text(conflict ? "冲突" : "待确认").FontSize(11)
                .WithTheme((t, tb) => tb.Foreground = conflict ? UiColors.Danger(t) : UiColors.Warning(t)));

        string kind = p.Node.Kind switch
        {
            NodeKind.Polygon => "面",
            NodeKind.Line => "线",
            NodeKind.Point => "点",
            _ => "分组",
        };
        string level = string.IsNullOrWhiteSpace(p.Node.Level) ? kind : $"{p.Node.Level} · {kind}";
        return new Border()
            .Padding(10, 7)
            .CornerRadius(6)
            .WithTheme((t, b) => b.Background(UiColors.Surface(t)))
            .Child(new DockPanel().Spacing(12).Children(
                combo.DockRight(),
                new StackPanel().Vertical().Spacing(3).Children(
                    new StackPanel().Horizontal().Spacing(8).Children(
                        tag,
                        new TextBlock().Text(p.Node.DisplayName).SemiBold().CenterVertical().MaxWidth(220).TextTrimming(TextTrimming.CharacterEllipsis),
                        new TextBlock().Text(level).FontSize(11.5).CenterVertical().WithTheme((t, tb) => tb.Foreground = UiColors.Faint(t))),
                    new TextBlock().Text(p.Reason).FontSize(11.5).TextWrapping(TextWrapping.Wrap)
                        .WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t)))));
    }
}
