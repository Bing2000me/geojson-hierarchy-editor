using GeoJsonEditor.Geo;

using NetTopologySuite.Geometries;

namespace GeoJsonEditor.Map;

/// <summary>
/// 视口：中心点（Web 墨卡托世界坐标，0..1）、连续缩放级别和屏幕尺寸（DIP）。
/// 数据坐标（数据坐标系下的经纬度）经过坐标系纠偏再投影到世界坐标。
/// </summary>
public sealed class MapViewport
{
    public const double TileSize = 256;
    public const double MinZoom = 1;
    public const double MaxZoom = 21;

    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    public double Zoom { get; set; } = 3;
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 600;

    /// <summary>数据所用坐标系。</summary>
    public CoordSystem DataCrs { get; set; } = CoordSystem.Wgs84;

    /// <summary>底图所用坐标系。</summary>
    public CoordSystem DisplayCrs { get; set; } = CoordSystem.Wgs84;

    public bool NeedsOffset => DataCrs != DisplayCrs;

    public double WorldSize => TileSize * Math.Pow(2, Zoom);

    // ─── 数据经纬度 ↔ 世界坐标 ───

    public (double X, double Y) DataToWorld(double lon, double lat)
    {
        if (NeedsOffset) (lon, lat) = ChinaOffset.Convert(lon, lat, DataCrs, DisplayCrs);
        return WebMercator.Forward(lon, lat);
    }

    public (double Lon, double Lat) WorldToData(double x, double y)
    {
        var (lon, lat) = WebMercator.Inverse(x, y);
        if (NeedsOffset) (lon, lat) = ChinaOffset.Convert(lon, lat, DisplayCrs, DataCrs);
        return (lon, lat);
    }

    // ─── 世界坐标 ↔ 屏幕坐标 ───

    public (double X, double Y) WorldToScreen(double wx, double wy)
    {
        double s = WorldSize;
        return ((wx - CenterX) * s + Width / 2, (wy - CenterY) * s + Height / 2);
    }

    public (double X, double Y) ScreenToWorld(double sx, double sy)
    {
        double s = WorldSize;
        return ((sx - Width / 2) / s + CenterX, (sy - Height / 2) / s + CenterY);
    }

    public (double X, double Y) DataToScreen(double lon, double lat)
    {
        var (wx, wy) = DataToWorld(lon, lat);
        return WorldToScreen(wx, wy);
    }

    public Coordinate ScreenToData(double sx, double sy)
    {
        var (wx, wy) = ScreenToWorld(sx, sy);
        var (lon, lat) = WorldToData(wx, wy);
        return new Coordinate(lon, lat);
    }

    /// <summary>当前视野的世界坐标范围。</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) VisibleWorld()
    {
        var (x0, y0) = ScreenToWorld(0, 0);
        var (x1, y1) = ScreenToWorld(Width, Height);
        return (x0, y0, x1, y1);
    }

    public void Pan(double dxScreen, double dyScreen)
    {
        double s = WorldSize;
        CenterX -= dxScreen / s;
        CenterY = Math.Clamp(CenterY - dyScreen / s, 0, 1);
    }

    /// <summary>以屏幕上的某一点为锚点缩放，锚点下的地物保持不动。</summary>
    public void ZoomAround(double sx, double sy, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var (wx, wy) = ScreenToWorld(sx, sy);
        Zoom = newZoom;
        double s = WorldSize;
        CenterX = wx - (sx - Width / 2) / s;
        CenterY = Math.Clamp(wy - (sy - Height / 2) / s, 0, 1);
    }

    /// <summary>缩放到能完整显示世界坐标范围，四周留出 <paramref name="padding"/>（DIP）。</summary>
    public void FitWorld(double minX, double minY, double maxX, double maxY, double padding = 48, double maxZoom = 17)
    {
        double w = Math.Max(maxX - minX, 1e-12);
        double h = Math.Max(maxY - minY, 1e-12);
        double availW = Math.Max(Width - padding * 2, 64);
        double availH = Math.Max(Height - padding * 2, 64);
        double scale = Math.Min(availW / w, availH / h);
        Zoom = Math.Clamp(Math.Log2(scale / TileSize), MinZoom, maxZoom);
        CenterX = (minX + maxX) / 2;
        CenterY = (minY + maxY) / 2;
    }

    /// <summary>当前缩放级别下地图中心处 1 DIP 对应的地面米数。</summary>
    public double MetersPerPixel()
    {
        var (_, lat) = WebMercator.Inverse(CenterX, CenterY);
        return WebMercator.MetersPerWorldUnit(lat) / WorldSize;
    }
}
