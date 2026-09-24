namespace GeoJsonEditor.Map;

/// <summary>
/// 按缩放级别的分级简化（Douglas–Peucker）。
/// 每条路径预先算出每个顶点的“显著度”：DP 递归中这个点被选为分割点时离弦的距离，并且不超过上一层分割点的显著度。
/// 这样某一级的简化结果就是显著度不小于该级容差的顶点，与直接在该容差下做 DP 的结果相同，只需一次线性筛选。
/// 容差取该级别下的 0.25 DIP，所以显示误差始终小于四分之一像素。
/// </summary>
public static class Lod
{
    public const int MaxLevel = 24;

    /// <summary>顶点数不超过这个值的路径不做简化。</summary>
    public const int MinVertices = 48;

    public const double PixelTolerance = 0.25;

    /// <summary>某一级的简化容差（世界坐标单位）。</summary>
    public static double Tolerance(int level) => PixelTolerance / (MapViewport.TileSize * Math.Pow(2, level));

    /// <summary>当前缩放用哪一级：向上取整，保证屏幕上的误差不超过容差。</summary>
    public static int LevelForZoom(double zoom) => Math.Clamp((int)Math.Ceiling(zoom - 1e-9), 0, MaxLevel);

    /// <summary>
    /// 计算显著度。<paramref name="closed"/> 为环（首尾相同）：起点和离起点最远的点作为两个锚点，两段分别递归。
    /// 锚点的显著度为无穷大。
    /// </summary>
    public static float[] Significance(double[] xy, bool closed)
    {
        int n = xy.Length / 2;
        var sig = new float[n];
        if (n <= 2)
        {
            Array.Fill(sig, float.PositiveInfinity);
            return sig;
        }
        sig[0] = float.PositiveInfinity;
        sig[n - 1] = float.PositiveInfinity;

        var stack = new Stack<(int A, int B, float Cap)>();
        if (closed)
        {
            int far = 0;
            double best = -1;
            double x0 = xy[0], y0 = xy[1];
            for (int i = 1; i < n - 1; i++)
            {
                double dx = xy[i * 2] - x0, dy = xy[i * 2 + 1] - y0;
                double d = dx * dx + dy * dy;
                if (d > best)
                {
                    best = d;
                    far = i;
                }
            }
            if (far > 0)
            {
                sig[far] = float.PositiveInfinity;
                stack.Push((0, far, float.PositiveInfinity));
                stack.Push((far, n - 1, float.PositiveInfinity));
            }
        }
        else
        {
            stack.Push((0, n - 1, float.PositiveInfinity));
        }

        while (stack.Count > 0)
        {
            var (a, b, cap) = stack.Pop();
            if (b - a < 2) continue;
            double ax = xy[a * 2], ay = xy[a * 2 + 1];
            double bx = xy[b * 2], by = xy[b * 2 + 1];
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double maxD = -1;
            int idx = a + 1;
            for (int i = a + 1; i < b; i++)
            {
                double px = xy[i * 2] - ax, py = xy[i * 2 + 1] - ay;
                double d;
                if (len2 <= 0)
                {
                    d = px * px + py * py;
                }
                else
                {
                    double t = (px * dx + py * dy) / len2;
                    if (t < 0) t = 0;
                    else if (t > 1) t = 1;
                    double cx = px - t * dx, cy = py - t * dy;
                    d = cx * cx + cy * cy;
                }
                if (d > maxD)
                {
                    maxD = d;
                    idx = i;
                }
            }
            float s = MathF.Min((float)Math.Sqrt(maxD), cap);
            sig[idx] = s;
            stack.Push((a, idx, s));
            stack.Push((idx, b, s));
        }
        return sig;
    }

    /// <summary>
    /// 取显著度不小于 <paramref name="tolerance"/> 的顶点。环剩不到 4 个点（面积小于容差）时返回 null；
    /// 保留的点接近全部时直接返回原数组，不复制。
    /// </summary>
    public static double[]? Filter(double[] xy, float[] sig, double tolerance, bool closed)
    {
        int n = sig.Length;
        float t = (float)tolerance;
        int kept = 0;
        for (int i = 0; i < n; i++)
        {
            if (sig[i] >= t) kept++;
        }
        if (closed ? kept < 4 : kept < 2) return null;
        if (kept >= n - n / 8) return xy;
        var r = new double[kept * 2];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            if (sig[i] < t) continue;
            r[k++] = xy[i * 2];
            r[k++] = xy[i * 2 + 1];
        }
        return r;
    }
}

/// <summary>
/// 路径的分块包围盒：每 <see cref="Size"/> 个顶点一块（块与块共用端点，所以每条线段都完整落在某一块里）。
/// 用来在很大的环上只检查鼠标附近或视野内的那几块。
/// </summary>
public static class PathChunks
{
    public const int Size = 64;

    /// <summary>返回每块的 minX, minY, maxX, maxY。</summary>
    public static double[] Build(double[] xy)
    {
        int n = xy.Length / 2;
        int chunks = Math.Max(1, (n - 1 + Size - 1) / Size);
        var boxes = new double[chunks * 4];
        for (int c = 0; c < chunks; c++)
        {
            int start = c * Size;
            int end = Math.Min(n - 1, start + Size);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = start; i <= end; i++)
            {
                double x = xy[i * 2], y = xy[i * 2 + 1];
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
            boxes[c * 4] = minX;
            boxes[c * 4 + 1] = minY;
            boxes[c * 4 + 2] = maxX;
            boxes[c * 4 + 3] = maxY;
        }
        return boxes;
    }

    /// <summary>第 <paramref name="chunk"/> 块覆盖的顶点下标范围 [start, end]。</summary>
    public static (int Start, int End) Range(int chunk, int vertexCount)
        => (chunk * Size, Math.Min(vertexCount - 1, chunk * Size + Size));

    public static bool Intersects(double[] boxes, int chunk, double minX, double minY, double maxX, double maxY)
        => boxes[chunk * 4 + 2] >= minX && boxes[chunk * 4] <= maxX && boxes[chunk * 4 + 3] >= minY && boxes[chunk * 4 + 1] <= maxY;
}
