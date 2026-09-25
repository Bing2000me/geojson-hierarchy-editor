using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace GeoJsonEditor.App;

/// <summary>发布页上的一个安装包。<see cref="Sha256"/> 来自 GitHub 为每个附件算的摘要（旧的发布没有）。</summary>
public sealed record UpdateAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>GitHub 上最新发布的版本。<see cref="Asset"/> 为当前平台的安装包，没有时为 null。</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string PageUrl, DateTimeOffset? Published, UpdateAsset? Asset)
{
    public bool IsNewer => Version > UpdateService.CurrentVersion;
}

/// <summary>可以直接给用户看的更新错误。</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>程序自己所在的位置：macOS 是 .app 包，Windows 是 exe 所在的文件夹。</summary>
public sealed record InstallTarget(string Path, bool IsBundle);

/// <summary>已下载并解压、等待替换的新版本。</summary>
public sealed record PendingUpdate(string StagedPath, InstallTarget Target, Version Version);

/// <summary>
/// 从 GitHub Releases 检查新版本、下载当前平台的安装包、替换程序文件并重启。
/// <list type="bullet">
/// <item>Windows：正在运行的 exe 不能覆盖但可以改名，所以先把旧文件改名为 .old，再把新文件放到原位，下次启动时删掉 .old。</item>
/// <item>macOS：整个 .app 挪走、换上新的（运行中的进程不受影响），再用 open 启动新版本；旧的包下次启动时删掉。</item>
/// </list>
/// 替换在程序退出后进行（<see cref="ApplyPending"/>），这时所有窗口都已关闭、文档都已处理。
/// </summary>
public static class UpdateService
{
    public const string Repository = "Bing2000me/geojson-hierarchy-editor";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    private static string LatestApi => $"https://api.github.com/repos/{Repository}/releases/latest";

    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>安装包文件名里的平台后缀，例如 GeoJsonEditor-1.2.0-win-x64.zip。不支持的平台为空。</summary>
    public static string PlatformSuffix
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture;
            if (OperatingSystem.IsWindows() && arch == Architecture.X64) return "win-x64";
            if (OperatingSystem.IsMacOS() && arch == Architecture.Arm64) return "macos-arm64";
            return "";
        }
    }

    /// <summary>下载、解压和替换用的工作目录。</summary>
    public static string WorkRoot
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = System.IO.Path.GetTempPath();
            return System.IO.Path.Combine(baseDir, "GeoJsonEditor", "update");
        }
    }

    /// <summary>最近一次检查到的新版本（所有窗口共用，用来显示右上角的提示）。</summary>
    public static UpdateInfo? Available { get; private set; }

    public static event Action? AvailableChanged;

    public static PendingUpdate? Pending { get; private set; }

    /// <summary>程序退出后是否启动新版本（“立即重启”）。</summary>
    public static bool RestartAfterApply { get; set; }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GeoJsonEditor/{CurrentVersion} (+https://github.com/{Repository})");
        return client;
    }

    public static void SetAvailable(UpdateInfo? info)
    {
        Available = info is { IsNewer: true } ? info : null;
        AvailableChanged?.Invoke();
    }

    // ───────────────────────── 检查 ─────────────────────────

    public static async Task<UpdateInfo> CheckAsync(CancellationToken token = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new UpdateException("连接 GitHub 超时，请检查网络后再试。");
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException("无法连接 GitHub：" + e.Message, e);
        }
        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    throw new UpdateException("GitHub 上还没有发布过版本。");
                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                    throw new UpdateException("GitHub 暂时限制了访问次数，请过一会儿再试。");
            }
            if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub 返回了错误（{(int)response.StatusCode}）。");
            return Parse(await response.Content.ReadAsStringAsync(cts.Token));
        }
    }

    /// <summary>解析 releases/latest 接口返回的 JSON。</summary>
    public static UpdateInfo Parse(string json, string? platformSuffix = null)
    {
        platformSuffix ??= PlatformSuffix;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = Str(root, "tag_name") ?? "";
        var version = ParseVersion(tag) ?? throw new UpdateException($"无法识别发布的版本号“{tag}”。");
        DateTimeOffset? published = DateTimeOffset.TryParse(Str(root, "published_at"), out var p) ? p : null;

        UpdateAsset? asset = null;
        if (platformSuffix.Length > 0 && root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = Str(a, "name") ?? "";
                if (!name.EndsWith($"-{platformSuffix}.zip", StringComparison.OrdinalIgnoreCase)) continue;
                string? digest = Str(a, "digest");
                string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                asset = new UpdateAsset(name, Str(a, "browser_download_url") ?? "", size, sha);
                break;
            }
        }
        return new UpdateInfo(
            version,
            tag,
            Str(root, "name") is { Length: > 0 } title ? title : tag,
            Str(root, "body") ?? "",
            Str(root, "html_url") ?? ReleasesPage,
            published,
            asset);
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>“v1.2.0”“1.2”“V2.0.1-beta” 这类标签里的版本号。</summary>
    public static Version? ParseVersion(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        int end = 0;
        while (end < t.Length && (char.IsDigit(t[end]) || t[end] == '.')) end++;
        return Version.TryParse(t[..end].TrimEnd('.'), out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    // ───────────────────────── 安装位置 ─────────────────────────

    /// <summary>当前程序能否自动更新；不能时 <paramref name="reason"/> 说明原因（这时只能打开发布页手动下载）。</summary>
    public static InstallTarget? FindInstall(out string reason)
    {
        reason = "";
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || PlatformSuffix.Length == 0)
        {
            reason = "当前系统不支持自动更新。";
            return null;
        }
        var dir = System.IO.Path.GetDirectoryName(exe)!;
        if (OperatingSystem.IsMacOS())
        {
            var contents = System.IO.Path.GetDirectoryName(dir);
            var app = contents == null ? null : System.IO.Path.GetDirectoryName(contents);
            if (app == null || !app.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                reason = "从源码运行的程序不能自动更新。";
                return null;
            }
            if (app.Contains("/AppTranslocation/", StringComparison.Ordinal))
            {
                reason = "程序是直接从下载位置打开的，系统把它放在了只读的隔离位置。先把它拖到“应用程序”文件夹再打开，就能自动更新。";
                return null;
            }
            if (!CanWrite(System.IO.Path.GetDirectoryName(app)!))
            {
                reason = "没有权限写入程序所在的文件夹。";
                return null;
            }
            return new InstallTarget(app, true);
        }
        if (OperatingSystem.IsWindows())
        {
            // 发布包是单文件 exe；旁边有 GeoJsonEditor.dll 说明是开发时的构建
            if (File.Exists(System.IO.Path.Combine(dir, "GeoJsonEditor.dll")))
            {
                reason = "从源码运行的程序不能自动更新。";
                return null;
            }
            if (!CanWrite(dir))
            {
                reason = "没有权限写入程序所在的文件夹（例如放在了 Program Files 里）。";
                return null;
            }
            return new InstallTarget(dir, false);
        }
        reason = "当前系统不支持自动更新。";
        return null;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = System.IO.Path.Combine(dir, $".geojson-editor-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ───────────────────────── 下载与解压 ─────────────────────────

    /// <summary>下载安装包，边下边算 SHA-256；发布页给了摘要时核对，不一致就报错。</summary>
    public static async Task<string> DownloadAsync(UpdateAsset asset, IProgress<(long Done, long Total)>? progress, CancellationToken token)
    {
        var dir = System.IO.Path.Combine(WorkRoot, "download");
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, asset.Name);
        var part = path + ".part";
        try
        {
            using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"下载失败（{(int)response.StatusCode}）。");
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            string hash;
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), token);
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    progress?.Report((done, total));
                }
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            if (asset.Sha256 != null && hash != asset.Sha256)
            {
                throw new UpdateException("下载的文件与发布页记录的校验值不一致，可能没有下载完整，请重试。");
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException("下载中断：" + e.Message, e);
        }
        finally
        {
            if (File.Exists(part)) TryDelete(part);
        }
    }

    /// <summary>
    /// 解压到暂存目录，返回新版本的位置：macOS 为 .app，Windows 为 GeoJsonEditor.exe 所在的文件夹。
    /// macOS 用系统的 ditto 解压，保留可执行权限和签名。
    /// </summary>
    public static string Extract(string zipPath, Version version)
    {
        var dir = System.IO.Path.Combine(WorkRoot, "staged-" + version);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                RunTool("/usr/bin/ditto", "-x", "-k", zipPath, dir);
                var app = Directory.EnumerateDirectories(dir, "*.app", SearchOption.AllDirectories)
                    .FirstOrDefault(d => File.Exists(System.IO.Path.Combine(d, "Contents", "MacOS", "GeoJsonEditor")));
                if (app == null) throw new UpdateException("安装包里没有找到程序，内容可能不完整。");
                RunTool("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app], allowFailure: true);
                return app;
            }
            ZipFile.ExtractToDirectory(zipPath, dir);
            var exe = Directory.EnumerateFiles(dir, "GeoJsonEditor.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null) throw new UpdateException("安装包里没有找到 GeoJsonEditor.exe，内容可能不完整。");
            return System.IO.Path.GetDirectoryName(exe)!;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new UpdateException("解压安装包失败：" + e.Message, e);
        }
    }

    public static void SetPending(PendingUpdate update) => Pending = update;

    // ───────────────────────── 替换 ─────────────────────────

    /// <summary>程序退出时调用：有等待安装的新版本就替换文件，需要时启动新版本。失败时保持旧版本不动。</summary>
    public static void ApplyPending()
    {
        var p = Pending;
        if (p == null) return;
        Pending = null;
        try
        {
            if (p.Target.IsBundle) ApplyBundle(p);
            else ApplyFolder(p, Environment.ProcessPath!);
            if (RestartAfterApply) Launch(p.Target);
        }
        catch (Exception e)
        {
            // 替换失败：旧版本已经恢复，记下原因，下次手动更新
            try
            {
                Directory.CreateDirectory(WorkRoot);
                File.WriteAllText(System.IO.Path.Combine(WorkRoot, "last-error.txt"), $"{DateTime.Now:u}\n{e}");
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>macOS：旧的 .app 挪到工作目录（同一个磁盘上只是改名），新的放到原位。</summary>
    public static void ApplyBundle(PendingUpdate p)
    {
        var app = p.Target.Path;
        Directory.CreateDirectory(WorkRoot);
        var backup = System.IO.Path.Combine(WorkRoot, $"previous-{DateTime.UtcNow.Ticks}.app");
        try
        {
            Directory.Move(app, backup);
        }
        catch (IOException)
        {
            // 程序在别的磁盘上：就地改名
            backup = app + ".old";
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            Directory.Move(app, backup);
        }
        try
        {
            try
            {
                Directory.Move(p.StagedPath, app);
            }
            catch (IOException)
            {
                RunTool("/usr/bin/ditto", p.StagedPath, app);
            }
        }
        catch (Exception)
        {
            if (Directory.Exists(app)) Directory.Delete(app, recursive: true);
            Directory.Move(backup, app);
            throw;
        }
    }

    /// <summary>
    /// Windows：先把新文件都复制成 .new，再把旧文件改名为 .old、.new 改成正式名字。
    /// 中途出错时把改过名的旧文件恢复回来。<paramref name="exePath"/> 是正在运行的 exe（用户可能改过名）。
    /// </summary>
    public static void ApplyFolder(PendingUpdate p, string exePath)
    {
        var dir = p.Target.Path;
        var files = Directory.EnumerateFiles(p.StagedPath, "*", SearchOption.AllDirectories).ToList();
        var plan = new List<(string New, string Dest)>();
        foreach (var src in files)
        {
            var rel = System.IO.Path.GetRelativePath(p.StagedPath, src);
            var dest = rel.Equals("GeoJsonEditor.exe", StringComparison.OrdinalIgnoreCase) ? exePath : System.IO.Path.Combine(dir, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            var staged = dest + ".new";
            File.Copy(src, staged, overwrite: true);
            plan.Add((staged, dest));
        }

        var done = new List<(string Dest, string? Old)>();
        try
        {
            foreach (var (staged, dest) in plan)
            {
                string? old = null;
                if (File.Exists(dest))
                {
                    old = $"{dest}.{DateTime.UtcNow.Ticks}.old";
                    File.Move(dest, old);
                }
                File.Move(staged, dest);
                done.Add((dest, old));
            }
        }
        catch (Exception)
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (dest, old) = done[i];
                TryDelete(dest);
                if (old != null) File.Move(old, dest, overwrite: true);
            }
            foreach (var (staged, _) in plan) TryDelete(staged);
            throw;
        }
    }

    private static void Launch(InstallTarget target)
    {
        if (target.IsBundle)
        {
            Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-n", target.Path }, UseShellExecute = false });
        }
        else
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = target.Path });
        }
    }

    /// <summary>启动时清理上次更新留下的旧文件和临时文件（在后台线程调用）。</summary>
    public static void Cleanup()
    {
        try
        {
            if (Directory.Exists(WorkRoot))
            {
                foreach (var d in Directory.EnumerateDirectories(WorkRoot))
                {
                    var name = System.IO.Path.GetFileName(d);
                    if (name.StartsWith("previous-", StringComparison.Ordinal) || name.StartsWith("staged-", StringComparison.Ordinal))
                    {
                        TryDeleteDirectory(d);
                    }
                }
                var downloads = System.IO.Path.Combine(WorkRoot, "download");
                if (Directory.Exists(downloads)) TryDeleteDirectory(downloads);
            }
            var install = FindInstall(out _);
            if (install == null) return;
            if (install.IsBundle)
            {
                var old = install.Path + ".old";
                if (Directory.Exists(old)) TryDeleteDirectory(old);
            }
            else
            {
                foreach (var f in Directory.EnumerateFiles(install.Path, "*.old", SearchOption.AllDirectories)) TryDelete(f);
                foreach (var f in Directory.EnumerateFiles(install.Path, "*.new", SearchOption.AllDirectories)) TryDelete(f);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响使用，下次再试
        }
    }

    /// <summary>用系统浏览器打开网址。</summary>
    public static void OpenInBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { url }, UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
        }
    }

    private static void RunTool(string file, params string[] args) => RunTool(file, args, allowFailure: false);

    private static void RunTool(string file, string[] args, bool allowFailure)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new IOException($"无法运行 {file}");
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure) throw new IOException($"{System.IO.Path.GetFileName(file)} 失败：{error.Trim()}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
