using GeoJsonEditor.Geo;

namespace GeoJsonEditor.Map;

/// <summary>一层瓦片（影像底图可能叠一层注记）。</summary>
public sealed record TileLayer(string UrlTemplate, string[] Subdomains, bool FlipY = false)
{
    public string BuildUrl(int x, int y, int z)
    {
        string s = Subdomains.Length == 0 ? "" : Subdomains[Math.Abs(x + y) % Subdomains.Length];
        int yy = FlipY ? (1 << z) - 1 - y : y;
        return UrlTemplate
            .Replace("{s}", s)
            .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{y}", yy.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{z}", z.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>在线底图。<see cref="Crs"/> 表示这套瓦片使用的坐标系，与数据不一致时绘制时自动纠偏。</summary>
public sealed record TileSource(
    string Id,
    string Name,
    string Description,
    CoordSystem Crs,
    string Attribution,
    int MinZoom,
    int MaxZoom,
    TileLayer[] Layers,
    bool IsDark = false)
{
    public bool IsBlank => Layers.Length == 0;

    public static readonly TileSource Amap = new(
        "amap", "高德地图", "街道地图，GCJ-02 坐标",
        CoordSystem.Gcj02, "© 高德地图", 3, 18,
        [new TileLayer("https://webrd0{s}.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=8&x={x}&y={y}&z={z}", ["1", "2", "3", "4"])]);

    public static readonly TileSource AmapSatellite = new(
        "amap-sat", "高德影像", "卫星影像叠加路网注记，GCJ-02 坐标",
        CoordSystem.Gcj02, "© 高德地图", 3, 18,
        [
            new TileLayer("https://webst0{s}.is.autonavi.com/appmaptile?style=6&x={x}&y={y}&z={z}", ["1", "2", "3", "4"]),
            new TileLayer("https://webst0{s}.is.autonavi.com/appmaptile?style=8&x={x}&y={y}&z={z}", ["1", "2", "3", "4"]),
        ],
        IsDark: true);

    public static readonly TileSource EsriLightGray = new(
        "esri-gray", "浅灰画布", "淡雅的灰色底图，突出数据，WGS-84 坐标",
        CoordSystem.Wgs84, "© Esri", 0, 16,
        [new TileLayer("https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Light_Gray_Base/MapServer/tile/{z}/{y}/{x}", [])]);

    public static readonly TileSource EsriDarkGray = new(
        "esri-dark", "深灰画布", "深色底图，适合深色界面，WGS-84 坐标",
        CoordSystem.Wgs84, "© Esri", 0, 16,
        [new TileLayer("https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}", [])],
        IsDark: true);

    public static readonly TileSource EsriImagery = new(
        "esri-sat", "Esri 影像", "全球卫星影像，WGS-84 坐标",
        CoordSystem.Wgs84, "© Esri, Maxar, Earthstar Geographics", 0, 18,
        [new TileLayer("https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}", [])],
        IsDark: true);

    public static readonly TileSource Osm = new(
        "osm", "OpenStreetMap", "开放街道地图，WGS-84 坐标",
        CoordSystem.Wgs84, "© OpenStreetMap 贡献者", 0, 19,
        [new TileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", [])]);

    public static readonly TileSource None = new(
        "none", "无底图", "只显示数据",
        CoordSystem.Wgs84, "", 0, 22, []);

    public static IReadOnlyList<TileSource> All { get; } =
        [Amap, AmapSatellite, EsriLightGray, EsriDarkGray, EsriImagery, Osm, None];

    public static TileSource ById(string? id) => All.FirstOrDefault(s => s.Id == id) ?? Amap;
}
