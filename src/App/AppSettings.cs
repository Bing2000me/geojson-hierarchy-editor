using System.Text.Json;
using System.Text.Json.Nodes;

using Aprillz.MewUI;

namespace GeoJsonEditor;

/// <summary>保存在本机的界面偏好：主题、底图、最近打开的文件。</summary>
public sealed class AppSettings
{
    public ThemeVariant Theme { get; set; } = ThemeVariant.System;
    public string BaseMap { get; set; } = "amap";
    public double BaseMapFade { get; set; } = 0.35;
    public bool BaseMapGray { get; set; }
    public string DataCrs { get; set; } = "wgs84";
    public bool ShowLabels { get; set; } = true;

    /// <summary>点标记的显示方式（见 <see cref="App.PointDisplay"/>）。</summary>
    public App.PointDisplay PointDisplay { get; set; } = App.PointDisplay.Declutter;

    /// <summary>按层级缩放显示，以及层级展开的早晚（0 到 1）。</summary>
    public bool RegionLod { get; set; } = true;
    public double LodDetail { get; set; } = 0.5;

    /// <summary>启动时自动检查 GitHub 上的新版本（每天最多一次）。</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }

    /// <summary>用户选择“跳过此版本”的版本号，自动检查时不再提示它。</summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>打开没有层级字段的文件时，询问是否自动识别上下级关系。</summary>
    public bool AskHierarchyOnOpen { get; set; } = true;

    /// <summary>识别层级时是否用属性字段、空间包含关系（记住上次的选择）。</summary>
    public bool DetectByAttributes { get; set; } = true;
    public bool DetectBySpace { get; set; } = true;

    /// <summary>识别层级时新建数据里缺少的上级、按政权字段建最上一级（记住上次的选择）。</summary>
    public bool DetectCreateMissing { get; set; } = true;
    public bool DetectGroupTop { get; set; } = true;

    public List<string> RecentFiles { get; set; } = new();

    private static string FilePath
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "GeoJsonEditor", "settings.json");
        }
    }

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            var o = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            if (o == null) return s;
            if (Enum.TryParse<ThemeVariant>((string?)o["theme"], out var theme)) s.Theme = theme;
            s.BaseMap = (string?)o["baseMap"] ?? s.BaseMap;
            s.BaseMapFade = (double?)o["baseMapFade"] ?? s.BaseMapFade;
            s.BaseMapGray = (bool?)o["baseMapGray"] ?? false;
            s.DataCrs = (string?)o["dataCrs"] ?? s.DataCrs;
            s.ShowLabels = (bool?)o["showLabels"] ?? true;
            if (Enum.TryParse<App.PointDisplay>((string?)o["pointDisplay"], out var pd)) s.PointDisplay = pd;
            else if ((bool?)o["clusterPoints"] == false) s.PointDisplay = App.PointDisplay.All;
            s.RegionLod = (bool?)o["regionLod"] ?? true;
            s.LodDetail = Math.Clamp((double?)o["lodDetail"] ?? 0.5, 0, 1);
            s.AutoCheckUpdates = (bool?)o["autoCheckUpdates"] ?? true;
            if (DateTime.TryParse((string?)o["lastUpdateCheck"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var last)) s.LastUpdateCheck = last;
            s.SkippedVersion = (string?)o["skippedVersion"] ?? "";
            s.AskHierarchyOnOpen = (bool?)o["askHierarchyOnOpen"] ?? true;
            s.DetectByAttributes = (bool?)o["detectByAttributes"] ?? true;
            s.DetectBySpace = (bool?)o["detectBySpace"] ?? true;
            s.DetectCreateMissing = (bool?)o["detectCreateMissing"] ?? true;
            s.DetectGroupTop = (bool?)o["detectGroupTop"] ?? true;
            if (o["recentFiles"] is JsonArray arr)
            {
                s.RecentFiles = arr.Select(x => (string?)x).OfType<string>().Where(File.Exists).Take(8).ToList();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
        }
        return s;
    }

    public void Save()
    {
        try
        {
            var o = new JsonObject
            {
                ["theme"] = Theme.ToString(),
                ["baseMap"] = BaseMap,
                ["baseMapFade"] = BaseMapFade,
                ["baseMapGray"] = BaseMapGray,
                ["dataCrs"] = DataCrs,
                ["showLabels"] = ShowLabels,
                ["pointDisplay"] = PointDisplay.ToString(),
                ["regionLod"] = RegionLod,
                ["lodDetail"] = LodDetail,
                ["autoCheckUpdates"] = AutoCheckUpdates,
                ["lastUpdateCheck"] = LastUpdateCheck.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["skippedVersion"] = SkippedVersion,
                ["askHierarchyOnOpen"] = AskHierarchyOnOpen,
                ["detectByAttributes"] = DetectByAttributes,
                ["detectBySpace"] = DetectBySpace,
                ["detectCreateMissing"] = DetectCreateMissing,
                ["detectGroupTop"] = DetectGroupTop,
                ["recentFiles"] = new JsonArray(RecentFiles.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 8) RecentFiles.RemoveRange(8, RecentFiles.Count - 8);
    }
}
