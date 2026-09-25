using GeoJsonEditor.Model;

using SkiaSharp;

namespace GeoJsonEditor.Map;

/// <summary>
/// 点聚合索引，做法参照 Mapbox 的 supercluster：从最细的缩放级别往上逐级做贪心聚合，每一级的簇都由下一级的簇合并而来，
/// 所以放大时一个簇总是拆成几个更小的簇，缩小时再合回去，不会出现跳来跳去的情况。
/// <para>
/// 与 supercluster 取重心不同，这里先把点按重要度排序（层级、人口，见 <see cref="LevelTiers.PointRank"/>），
/// 按顺序做种子去吸收半径内的点，簇就放在种子（其中最重要的那个点）的真实位置上。
/// 缩小时都城、州府这样的点留在原处代表周围一片，放大后周围的点逐级展开。
/// </para>
/// 索引只依赖点的位置、排序值和颜色，构建可以放在后台线程；交给界面后只读。
/// </summary>
public sealed class PointClusterIndex
{
    /// <summary>聚合到这一级为止；再放大（缩放级别四舍五入后大于它）时每个点单独显示。</summary>
    public const int MaxLevel = 18;

    /// <summary>聚合半径（DIP）：两点在屏幕上的距离小于它就合并。</summary>
    public const double RadiusDip = 44;

    /// <summary>簇的颜色构成最多记几种颜色，其余归入“其他”。</summary>
    private const int MaxColors = 5;

    /// <param name="Tier">层级（见 <see cref="LevelTiers"/>），认不出时为 -1，决定标注字号。</param>
    public readonly record struct Leaf(GeoNode Node, int PointIndex, double X, double Y, float Rank, int Tier, SKColor Color);

    /// <summary>某一级上的一个簇。<see cref="Rep"/> 是代表点（最重要的成员）在 <see cref="Leaves"/> 里的下标，也是簇的位置。</summary>
    public struct Cluster
    {
        public double X, Y;
        public int Rep;
        public int Count;

        /// <summary>上一级（更粗一级）里包含它的簇的下标。</summary>
        public int Parent;

        /// <summary>颜色构成（按数量从多到少），单点簇为 null。</summary>
        public (SKColor Color, int Count)[]? Mix;
    }

    private readonly Cluster[][] _levels;

    private PointClusterIndex(Leaf[] leaves, Cluster[][] levels)
    {
        Leaves = leaves;
        _levels = levels;
    }

    /// <summary>全部点，按重要度排好序（下标越小越重要）。</summary>
    public Leaf[] Leaves { get; }

    public int Count => Leaves.Length;

    /// <summary>缩放级别对应的聚合级别；返回值大于 <see cref="MaxLevel"/> 时不聚合。</summary>
    public static int LevelForZoom(double zoom) => Math.Max(0, (int)Math.Floor(zoom + 0.5));

    /// <summary>某一级的全部簇。<paramref name="level"/> 为 MaxLevel + 1 时每个点自成一簇。</summary>
    public Cluster[] At(int level) => _levels[Math.Clamp(level, 0, MaxLevel + 1)];

    /// <summary>
    /// 构建索引。<paramref name="leaves"/> 不必排序；按排序值、再按原来的先后排序后使用。
    /// </summary>
    public static PointClusterIndex Build(IReadOnlyList<Leaf> leaves, CancellationToken token = default)
    {
        var sorted = leaves.Select((l, i) => (Leaf: l, Order: i))
            .OrderBy(x => x.Leaf.Rank)
            .ThenBy(x => x.Order)
            .Select(x => x.Leaf)
            .ToArray();
        int n = sorted.Length;
        var levels = new Cluster[MaxLevel + 2][];
        var bottom = new Cluster[n];
        for (int i = 0; i < n; i++)
        {
            bottom[i] = new Cluster { X = sorted[i].X, Y = sorted[i].Y, Rep = i, Count = 1, Parent = -1 };
        }
        levels[MaxLevel + 1] = bottom;

        var prev = bottom;
        for (int z = MaxLevel; z >= 0; z--)
        {
            token.ThrowIfCancellationRequested();
            prev = levels[z] = ClusterLevel(prev, sorted, RadiusDip / (MapViewport.TileSize * Math.Pow(2, z)));
        }
        return new PointClusterIndex(sorted, levels);
    }

    /// <summary>
    /// 把下一级的簇按代表点的重要度依次做种子，吸收半径内还没归属的簇。
    /// 下一级的数组本身就按代表点的下标（重要度）排好了序，所以直接顺序处理。
    /// </summary>
    private static Cluster[] ClusterLevel(Cluster[] input, Leaf[] leaves, double radius)
    {
        int n = input.Length;
        var result = new List<Cluster>(n);
        if (n == 0) return [];

        // 网格：格子边长等于半径，查询只看周围 3×3 个格子。用数组串起每个格子里的簇，避免大量小列表。
        double inv = 1 / radius;
        var heads = new Dictionary<long, int>(n);
        var next = new int[n];
        for (int i = n - 1; i >= 0; i--)
        {
            long key = CellKey((long)Math.Floor(input[i].X * inv), (long)Math.Floor(input[i].Y * inv));
            next[i] = heads.TryGetValue(key, out int head) ? head : -1;
            heads[key] = i;
        }

        var taken = new bool[n];
        double r2 = radius * radius;
        for (int i = 0; i < n; i++)
        {
            if (taken[i]) continue;
            taken[i] = true;
            ref var seed = ref input[i];
            int index = result.Count;
            seed.Parent = index;
            var cluster = new Cluster { X = seed.X, Y = seed.Y, Rep = seed.Rep, Count = seed.Count, Parent = -1 };
            List<(SKColor, int)>? mix = null;

            long cx = (long)Math.Floor(seed.X * inv), cy = (long)Math.Floor(seed.Y * inv);
            for (long gx = cx - 1; gx <= cx + 1; gx++)
            {
                for (long gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!heads.TryGetValue(CellKey(gx, gy), out int j)) continue;
                    for (; j >= 0; j = next[j])
                    {
                        if (taken[j]) continue;
                        double dx = input[j].X - seed.X, dy = input[j].Y - seed.Y;
                        if (dx * dx + dy * dy > r2) continue;
                        taken[j] = true;
                        input[j].Parent = index;
                        if (mix == null)
                        {
                            mix = new List<(SKColor, int)>(MaxColors + 1);
                            AddMix(mix, seed, leaves);
                        }
                        AddMix(mix, input[j], leaves);
                        cluster.Count += input[j].Count;
                    }
                }
            }
            cluster.Mix = mix != null ? FinishMix(mix) : seed.Mix;
            result.Add(cluster);
        }
        return result.ToArray();
    }

    private static long CellKey(long x, long y) => (x << 32) ^ (y & 0xFFFFFFFFL);

    private static void AddMix(List<(SKColor Color, int Count)> mix, in Cluster c, Leaf[] leaves)
    {
        if (c.Mix == null)
        {
            Add(mix, leaves[c.Rep].Color, c.Count);
            return;
        }
        foreach (var (color, count) in c.Mix) Add(mix, color, count);

        static void Add(List<(SKColor Color, int Count)> mix, SKColor color, int count)
        {
            for (int k = 0; k < mix.Count; k++)
            {
                if (mix[k].Color == color)
                {
                    mix[k] = (color, mix[k].Count + count);
                    return;
                }
            }
            mix.Add((color, count));
        }
    }

    /// <summary>按数量排序，只留最多的几种颜色，其余合成一种中性灰。</summary>
    private static (SKColor Color, int Count)[] FinishMix(List<(SKColor Color, int Count)> mix)
    {
        mix.Sort((a, b) => b.Count.CompareTo(a.Count));
        if (mix.Count <= MaxColors) return mix.ToArray();
        int rest = 0;
        for (int k = MaxColors - 1; k < mix.Count; k++) rest += mix[k].Count;
        var result = new (SKColor, int)[MaxColors];
        for (int k = 0; k < MaxColors - 1; k++) result[k] = mix[k];
        result[MaxColors - 1] = (OtherColor, rest);
        return result;
    }

    /// <summary>颜色构成里“其他”一类用的颜色。</summary>
    public static readonly SKColor OtherColor = new(0x94, 0xA3, 0xB8);

    /// <summary>某个簇的全部成员（叶子下标，按重要度排序）。</summary>
    public List<int> Members(int level, int cluster)
    {
        var result = new List<int>();
        if (level > MaxLevel)
        {
            result.Add(cluster);
            return result;
        }
        var bottom = _levels[MaxLevel + 1];
        for (int i = 0; i < bottom.Length; i++)
        {
            int c = bottom[i].Parent;
            for (int z = MaxLevel; z > level && c >= 0; z--) c = _levels[z][c].Parent;
            if (c == cluster) result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// 放大到哪一级这个簇才会拆开（supercluster 的 getClusterExpansionZoom）。
    /// 一直到最细一级都拆不开（成员位置几乎重合）时返回 MaxLevel + 1。
    /// </summary>
    public int ExpansionLevel(int level, int cluster)
    {
        int current = cluster;
        for (int z = level; z <= MaxLevel; z++)
        {
            var finer = _levels[z + 1];
            int children = 0, only = -1;
            for (int i = 0; i < finer.Length; i++)
            {
                if (finer[i].Parent != current) continue;
                children++;
                only = i;
                if (children > 1) return z + 1;
            }
            if (children == 0) break;
            current = only;
        }
        return MaxLevel + 1;
    }
}
