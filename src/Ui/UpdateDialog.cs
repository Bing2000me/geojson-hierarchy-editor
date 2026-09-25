using System.Text;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using GeoJsonEditor.App;

namespace GeoJsonEditor.Ui;

/// <summary>
/// “检查更新”对话框：显示 GitHub 上的最新版本和更新内容，下载安装包（带进度、可取消），
/// 准备好后可以立即重启完成更新，或者等退出程序时再安装。
/// </summary>
public static class UpdateDialog
{
    /// <param name="info">已经检查到的新版本；为 null 时打开后先去 GitHub 检查。</param>
    /// <param name="prepareRestart">重启前处理所有窗口里未保存的修改并关闭窗口；用户取消时返回 false。</param>
    public static async Task ShowAsync(Window owner, AppSettings settings, UpdateInfo? info, Func<Task<bool>> prepareRestart)
    {
        var title = new TextBlock().FontSize(16).SemiBold();
        var subtitle = new TextBlock().FontSize(12.5).TextWrapping(TextWrapping.Wrap).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t));
        var icon = new IconView(Icons.Update, 30).CenterVertical().WithTheme((t, i) => i.Tint = t.Palette.Accent);
        var ring = new ProgressRing().Width(22).Height(22).CenterVertical();

        var notesText = new TextBlock().FontSize(12.5).TextWrapping(TextWrapping.Wrap);
        var notes = new Border()
            .Padding(14, 10)
            .CornerRadius(8)
            .WithTheme((t, b) => b.Background(UiColors.Card(t)))
            .Child(new ScrollViewer().NoHorizontalScroll().MaxHeight(260).Content(notesText));

        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0 };
        var progressText = new TextBlock().FontSize(12).WithTheme((t, tb) => tb.Foreground = UiColors.Subtle(t));
        var progressPanel = new StackPanel().Vertical().Spacing(6).Children(progress, progressText);

        var autoCheck = new ObservableValue<bool>(settings.AutoCheckUpdates);
        autoCheck.Changed += () =>
        {
            settings.AutoCheckUpdates = autoCheck.Value;
            settings.Save();
        };
        var autoBox = new CheckBox().Content("启动时自动检查更新").BindIsChecked(autoCheck).CenterVertical();

        var buttons = new StackPanel().Horizontal().Spacing(8).Right();
        var footer = new DockPanel().Children(autoBox.DockLeft(), buttons);

        var dialog = new Window
        {
            Title = "检查更新",
            StartupLocation = WindowStartupLocation.CenterOwner,
            WindowSize = WindowSize.FitContentHeight(560, 680),
            Padding = new Thickness(0),
        };
        dialog.StyleSheet = AppStyles.Create();

        CancellationTokenSource? download = null;
        dialog.Closed += () => download?.Cancel();

        Button Make(string text, Action click, string style = AppStyles.Ghost)
            => new Button().StyleName(style).Content(text).MinWidth(style == AppStyles.Primary ? 108 : 76).OnClick(click);

        void SetButtons(params Button[] list)
        {
            buttons.Clear();
            foreach (var b in list) buttons.Add(b);
        }

        void Busy(bool on)
        {
            ring.IsActive = on;
            ring.IsVisible = on;
            icon.IsVisible = !on;
        }

        void ShowChecking()
        {
            Busy(true);
            title.Text = "正在检查更新…";
            subtitle.Text = $"当前版本 {UpdateService.CurrentVersion}。正在从 GitHub 获取最新的发布信息。";
            notes.IsVisible = false;
            progressPanel.IsVisible = false;
            SetButtons(Make("关闭", dialog.Close));
        }

        void ShowError(string message)
        {
            Busy(false);
            icon.Data = Icons.Info;
            title.Text = "检查更新失败";
            subtitle.Text = message;
            notes.IsVisible = false;
            progressPanel.IsVisible = false;
            SetButtons(Make("打开发布页", () => UpdateService.OpenInBrowser(UpdateService.ReleasesPage)), Make("关闭", dialog.Close));
        }

        void ShowResult(UpdateInfo latest)
        {
            Busy(false);
            progressPanel.IsVisible = false;
            string when = latest.Published is { } p ? $"，发布于 {p.ToLocalTime():yyyy-MM-dd}" : "";
            notesText.Text = FormatNotes(latest.Notes);
            notes.IsVisible = notesText.Text.Length > 0;

            if (!latest.IsNewer)
            {
                icon.Data = Icons.Check;
                title.Text = "已是最新版本";
                subtitle.Text = $"当前版本 {UpdateService.CurrentVersion}，与 GitHub 上最新的 {latest.Version}{when}一致。";
                SetButtons(Make("关闭", dialog.Close, AppStyles.Primary));
                return;
            }

            icon.Data = Icons.Update;
            title.Text = $"发现新版本 {latest.Version}";
            var install = UpdateService.FindInstall(out string reason);
            string size = latest.Asset is { Size: > 0 } a ? $" · 安装包 {a.Size / 1024.0 / 1024.0:0.0} MB" : "";
            subtitle.Text = $"当前版本 {UpdateService.CurrentVersion}{when}{size}。";

            var skip = Make("跳过此版本", () =>
            {
                settings.SkippedVersion = latest.Tag;
                settings.Save();
                UpdateService.SetAvailable(null);
                dialog.Close();
            });
            var page = Make("打开发布页", () => UpdateService.OpenInBrowser(latest.PageUrl));
            var later = Make("稍后", dialog.Close);
            if (latest.Asset == null)
            {
                subtitle.Text += "发布页上没有当前系统的安装包，请到发布页查看。";
                SetButtons(skip, page, later);
                return;
            }
            if (install == null)
            {
                subtitle.Text += reason + "可以到发布页手动下载。";
                SetButtons(skip, page, later);
                return;
            }
            SetButtons(skip, page, later, Make("下载并安装", () => StartDownload(latest, install), AppStyles.Primary));
        }

        async void StartDownload(UpdateInfo latest, InstallTarget install)
        {
            var asset = latest.Asset!;
            download = new CancellationTokenSource();
            var token = download.Token;
            progress.Value = 0;
            progressText.Text = "正在连接…";
            progressPanel.IsVisible = true;
            SetButtons(Make("取消", () => download.Cancel()));
            UpdateService.SetDownloadFraction(0);
            UpdateService.SetStatus(UpdateStatus.Downloading);
            var reporter = new Progress<(long Done, long Total)>(x =>
            {
                double total = Math.Max(1, x.Total);
                progress.Value = Math.Clamp(x.Done / total, 0, 1);
                progressText.Text = $"已下载 {x.Done / 1024.0 / 1024.0:0.0} / {total / 1024.0 / 1024.0:0.0} MB";
                UpdateService.SetDownloadFraction(x.Done / total);
            });
            try
            {
                var zip = await UpdateService.DownloadAsync(asset, reporter, token);
                progressText.Text = "正在解压…";
                var staged = await Task.Run(() => UpdateService.Extract(zip, latest.Version), token);
                UpdateService.SetPending(new PendingUpdate(staged, install, latest.Version));
                UpdateService.SetStatus(UpdateStatus.Ready);
                ShowReady(latest);
            }
            catch (OperationCanceledException)
            {
                UpdateService.SetStatus(UpdateStatus.Available);
                progressPanel.IsVisible = false;
                ShowResult(latest);
            }
            catch (UpdateException e)
            {
                UpdateService.SetStatus(UpdateStatus.Available);
                progressPanel.IsVisible = false;
                ShowResult(latest);
                subtitle.Text = e.Message;
            }
            catch (Exception e)
            {
                UpdateService.SetStatus(UpdateStatus.Available);
                progressPanel.IsVisible = false;
                ShowResult(latest);
                subtitle.Text = "下载或解压失败：" + e.Message;
            }
        }

        void ShowReady(UpdateInfo latest)
        {
            Busy(false);
            icon.Data = Icons.Check;
            progressPanel.IsVisible = false;
            title.Text = $"新版本 {latest.Version} 已准备好";
            subtitle.Text = "重启程序即可完成更新。选“退出时安装”的话，下次退出程序时自动替换，下次打开就是新版本。";
            SetButtons(
                Make("退出时安装", () =>
                {
                    UpdateService.RestartAfterApply = false;
                    dialog.Close();
                    owner.ShowToast($"退出程序时会自动安装新版本 {latest.Version}。");
                }),
                Make("立即重启", async () =>
                {
                    UpdateService.RestartAfterApply = true;
                    dialog.Close();
                    if (!await prepareRestart())
                    {
                        UpdateService.RestartAfterApply = false;
                        owner.ShowToast($"已取消重启。退出程序时会自动安装新版本 {latest.Version}。");
                    }
                }, AppStyles.Primary));
        }

        dialog.Content = new StackPanel().Vertical().Spacing(16).Margin(24, 20).Children(
            new DockPanel().Spacing(14).Children(
                new Grid().Width(32).DockLeft().Children(icon, ring),
                new StackPanel().Vertical().Spacing(4).Children(title, subtitle)),
            notes,
            progressPanel,
            footer);

        if (UpdateService.Pending is { } pending && info != null && pending.Version == info.Version)
        {
            ShowReady(info);
        }
        else if (info != null)
        {
            ShowResult(info);
        }
        else
        {
            ShowChecking();
            _ = CheckNow();
        }

        async Task CheckNow()
        {
            try
            {
                var latest = await UpdateService.CheckWithStatusAsync();
                if (!latest.IsNewer)
                {
                    settings.LastUpdateCheck = DateTime.UtcNow;
                    settings.Save();
                }
                ShowResult(latest);
            }
            catch (UpdateException e)
            {
                ShowError(e.Message);
            }
            catch (Exception e)
            {
                ShowError("检查更新时出错：" + e.Message);
            }
        }

        await dialog.ShowDialogAsync(owner);
    }

    /// <summary>发布说明是 Markdown，这里转成适合直接显示的纯文本。</summary>
    public static string FormatNotes(string markdown)
    {
        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) line = trimmed.TrimStart('#').Trim();
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal)) line = "• " + trimmed[2..];
            line = line.Replace("**", "").Replace("`", "");
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 启动时的自动检查：没有新版本时一天最多查一次；查到新版本（且没被跳过）后每次启动都查，直到装上或跳过。
    /// </summary>
    public static async Task<UpdateInfo?> AutoCheckAsync(AppSettings settings)
    {
        if (!settings.AutoCheckUpdates || (DateTime.UtcNow - settings.LastUpdateCheck).TotalHours < 20) return null;
        try
        {
            var latest = await UpdateService.CheckWithStatusAsync(quiet: true);
            if (!latest.IsNewer || string.Equals(latest.Tag, settings.SkippedVersion, StringComparison.OrdinalIgnoreCase))
            {
                settings.LastUpdateCheck = DateTime.UtcNow;
                settings.Save();
                // 跳过的版本不提示（按钮上也不显示“新版本”）
                if (latest.IsNewer) UpdateService.SetAvailable(null);
                return null;
            }
            return latest;
        }
        catch (Exception)
        {
            // 自动检查失败不打扰用户
            return null;
        }
    }
}
