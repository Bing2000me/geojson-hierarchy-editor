namespace GeoJsonEditor.Geo;

/// <summary>Web 墨卡托：经纬度与 [0,1] 世界坐标互转。</summary>
public static class WebMercator
{
    public const double MaxLatitude = 85.05112878;

    public static (double X, double Y) Forward(double lon, double lat)
    {
        lat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        double x = (lon + 180.0) / 360.0;
        double s = Math.Sin(lat * Math.PI / 180.0);
        double y = 0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI);
        return (x, y);
    }

    public static (double Lon, double Lat) Inverse(double x, double y)
    {
        double lon = x * 360.0 - 180.0;
        double n = Math.PI - 2.0 * Math.PI * y;
        double lat = 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
        return (lon, lat);
    }

    /// <summary>某纬度处每个世界单位对应的地面米数（世界宽度 = 1）。</summary>
    public static double MetersPerWorldUnit(double lat) => 40075016.686 * Math.Cos(lat * Math.PI / 180.0);
}

public enum CoordSystem
{
    Wgs84,
    Gcj02,
}

/// <summary>WGS-84 与 GCJ-02（国测局坐标）互转。境外坐标不做偏移。</summary>
public static class ChinaOffset
{
    private const double A = 6378245.0;
    private const double Ee = 0.00669342162296594323;

    public static bool OutOfChina(double lon, double lat)
        => lon < 72.004 || lon > 137.8347 || lat < 0.8293 || lat > 55.8271;

    public static (double Lon, double Lat) Wgs84ToGcj02(double lon, double lat)
    {
        if (OutOfChina(lon, lat)) return (lon, lat);
        var (dLon, dLat) = Delta(lon, lat);
        return (lon + dLon, lat + dLat);
    }

    /// <summary>迭代求逆，精度约 1e-7 度。</summary>
    public static (double Lon, double Lat) Gcj02ToWgs84(double lon, double lat)
    {
        if (OutOfChina(lon, lat)) return (lon, lat);
        double wLon = lon, wLat = lat;
        for (int i = 0; i < 8; i++)
        {
            var (gLon, gLat) = Wgs84ToGcj02(wLon, wLat);
            double eLon = gLon - lon, eLat = gLat - lat;
            wLon -= eLon;
            wLat -= eLat;
            if (Math.Abs(eLon) < 1e-9 && Math.Abs(eLat) < 1e-9) break;
        }
        return (wLon, wLat);
    }

    public static (double Lon, double Lat) Convert(double lon, double lat, CoordSystem from, CoordSystem to)
    {
        if (from == to) return (lon, lat);
        return from == CoordSystem.Wgs84 ? Wgs84ToGcj02(lon, lat) : Gcj02ToWgs84(lon, lat);
    }

    private static (double DLon, double DLat) Delta(double lon, double lat)
    {
        double x = lon - 105.0, y = lat - 35.0;
        double dLat = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
        dLat += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        dLat += (20.0 * Math.Sin(y * Math.PI) + 40.0 * Math.Sin(y / 3.0 * Math.PI)) * 2.0 / 3.0;
        dLat += (160.0 * Math.Sin(y / 12.0 * Math.PI) + 320 * Math.Sin(y * Math.PI / 30.0)) * 2.0 / 3.0;

        double dLon = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
        dLon += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        dLon += (20.0 * Math.Sin(x * Math.PI) + 40.0 * Math.Sin(x / 3.0 * Math.PI)) * 2.0 / 3.0;
        dLon += (150.0 * Math.Sin(x / 12.0 * Math.PI) + 300.0 * Math.Sin(x / 30.0 * Math.PI)) * 2.0 / 3.0;

        double radLat = lat / 180.0 * Math.PI;
        double magic = Math.Sin(radLat);
        magic = 1 - Ee * magic * magic;
        double sqrtMagic = Math.Sqrt(magic);
        dLat = dLat * 180.0 / (A * (1 - Ee) / (magic * sqrtMagic) * Math.PI);
        dLon = dLon * 180.0 / (A / sqrtMagic * Math.Cos(radLat) * Math.PI);
        return (dLon, dLat);
    }
}
