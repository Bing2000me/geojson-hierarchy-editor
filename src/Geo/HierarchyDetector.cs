using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using GeoJsonEditor.Model;

using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace GeoJsonEditor.Geo;

/// <summary>一条识别出的上下级关系的可信程度。</summary>
public enum LinkStatus
{
    /// <summary>证据充分：属性指向明确且空间上吻合，或者几乎完全落在唯一的上级范围内。</summary>
    Confirmed,
    /// <summary>有候选但不确定：只有部分落在上级内、跨了几个区域、同名的上级不止一个等。</summary>
    Pending,
    /// <summary>证据互相矛盾：属性指定的上级和空间位置不一致，或者会形成循环。</summary>
    Conflict,
    /// <summary>没有找到上级，放在最上层。</summary>
    None,
}

/// <param name="Share">要素落在这个候选里的比例（0 到 1）；没有空间信息时为 -1。</param>
/// <param name="Basis">依据，例如“属性 parent_id”“空间”。</param>
public sealed record LinkCandidate(GeoNode Parent, double Share, string Basis);

/// <summary>一个要素的识别结果。<see cref="Chosen"/> 初始为建议的上级，用户在预览里可以改。</summary>
public sealed class LinkProposal(GeoNode node)
{
    public GeoNode Node { get; } = node;

    public GeoNode? Suggested { get; internal set; }

    public GeoNode? Chosen { get; set; }

    public LinkStatus Status { get; internal set; } = LinkStatus.None;

    public string Reason { get; internal set; } = "";

    public List<LinkCandidate> Candidates { get; } = new();

    /// <summary>属性指向的上级不在数据里：引用字段、引用值、从配套字段取到的名称。</summary>
    internal MissingRef? Missing { get; set; }

    /// <summary>祖父一级的引用（grandparent_id 这类字段），给新建的上级找上级用。</summary>
    internal List<MissingRef>? GrandRefs { get; set; }

    internal void Set(GeoNode? parent, LinkStatus status, string reason)
    {
        Suggested = Chosen = parent;
        Status = status;
        Reason = reason;
    }
}

/// <summary>属性里引用的、但数据里没有的上级。</summary>
internal sealed record MissingRef(string Key, string Value, string? Name);

/// <param name="Attributes">按属性字段识别（parent_id 这类字段、行政区划代码的前缀）。</param>
/// <param name="Spatial">按空间包含关系识别。</param>
/// <param name="KeepExisting">保留要素已有的上级（文件里的 parentId，或文档里现有的层级）。</param>
/// <param name="CreateMissing">属性指向的上级不在数据里时新建分组（按 parent_name 这类配套字段命名），范围由下级自动拼成。</param>
/// <param name="GroupTop">按政权、国家这类字段给最上层的区域再建一级分组。</param>
public sealed record DetectOptions(bool Attributes = true, bool Spatial = true, bool KeepExisting = true, bool CreateMissing = true, bool GroupTop = true);

/// <summary>识别结果：每个要素一条建议，以及用到的规则说明。</summary>
public sealed class HierarchyDetection
{
    internal HierarchyDetection(List<LinkProposal> proposals, List<string> rules, IReadOnlyList<GeoNode> pool, List<GeoNode>? created = null)
    {
        Proposals = proposals;
        Rules = rules;
        _pool = pool;
        CreatedGroups = created ?? new List<GeoNode>();
        _created = new HashSet<GeoNode>(CreatedGroups, ReferenceEqualityComparer.Instance);
        _bySubject = new Dictionary<GeoNode, LinkProposal>(proposals.Count, ReferenceEqualityComparer.Instance);
        foreach (var p in proposals) _bySubject[p.Node] = p;
    }

    private readonly IReadOnlyList<GeoNode> _pool;
    private readonly Dictionary<GeoNode, LinkProposal> _bySubject;
    private readonly HashSet<GeoNode> _created;

    /// <summary>
    /// 识别时新建的上级分组（属性里引用了、数据里没有的上级，以及按政权字段建的最上级），没有几何，
    /// 地图上由下级自动拼出范围。它们的 Parent 是识别出的上级（另一个新建分组、已有的要素或 null）。
    /// </summary>
    public IReadOnlyList<GeoNode> CreatedGroups { get; }

    public bool IsCreated(GeoNode node) => _created.Contains(node);

    /// <summary>按当前选择实际用到的新建分组（有下级选了它，或者是这样的分组的上级），上级在前。</summary>
    public List<GeoNode> UsedGroups()
    {
        var used = new HashSet<GeoNode>(ReferenceEqualityComparer.Instance);
        foreach (var p in Proposals)
        {
            for (var g = p.Chosen; g != null && _created.Contains(g) && used.Add(g); g = g.Parent)
            {
            }
        }
        var result = CreatedGroups.Where(used.Contains).ToList();
        result.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
        return result;

        int Depth(GeoNode g)
        {
            int d = 0;
            for (var x = g.Parent; x != null && _created.Contains(x); x = x.Parent) d++;
            return d;
        }
    }

    public IReadOnlyList<LinkProposal> Proposals { get; }

    /// <summary>用到的识别规则，例如“属性 parent_id → feature_id”。</summary>
    public IReadOnlyList<string> Rules { get; }

    public int Count(LinkStatus status) => Proposals.Count(p => p.Status == status);

    /// <summary>已确认的上下级关系数（状态为已确认且有上级）。</summary>
    public int ConfirmedLinks => Proposals.Count(p => p.Status == LinkStatus.Confirmed && p.Chosen != null);

    /// <summary>某个要素按当前选择的上级（没参与识别的要素保持原来的上级）。</summary>
    public GeoNode? ParentOf(GeoNode node) => _bySubject.TryGetValue(node, out var p) ? p.Chosen : node.Parent;

    /// <summary>按当前选择得到的上级表（只含参与识别的要素）。</summary>
    public Dictionary<GeoNode, GeoNode?> ParentMap()
    {
        var map = new Dictionary<GeoNode, GeoNode?>(Proposals.Count, ReferenceEqualityComparer.Instance);
        foreach (var p in Proposals) map[p.Node] = p.Chosen;
        return map;
    }

    /// <summary>
    /// 把还没进文档的一组要素（先序）按当前选择重新组织成树，返回这批要素里的根。同一上级下保持原来的先后。
    /// 上级是这批要素以外的（导入时挂到文档里已有的要素下）也算根，它的 Parent 指向那个要素，由调用方加进文档。
    /// </summary>
    public List<GeoNode> Rebuild(IReadOnlyList<GeoNode> nodes)
    {
        var set = new HashSet<GeoNode>(nodes, ReferenceEqualityComparer.Instance);
        var groups = UsedGroups();
        foreach (var g in groups) set.Add(g);
        var parents = nodes.Select(ParentOf).ToList();
        foreach (var n in nodes) n.ChildList.Clear();
        foreach (var g in groups) g.ChildList.Clear();
        var roots = new List<GeoNode>();

        // 新建的分组放在它第一个下级出现的位置
        var placed = new HashSet<GeoNode>(ReferenceEqualityComparer.Instance);
        void Place(GeoNode g)
        {
            if (!placed.Add(g)) return;
            var gp = g.Parent;
            if (gp != null && _created.Contains(gp)) Place(gp);
            if (gp != null && set.Contains(gp)) gp.ChildList.Add(g);
            else roots.Add(g);
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var parent = parents[i];
            if (parent != null && _created.Contains(parent)) Place(parent);
            n.Parent = parent;
            if (parent != null && set.Contains(parent)) parent.ChildList.Add(n);
            else roots.Add(n);
        }
        return roots;
    }

    /// <summary>
    /// 检查用户的选择会不会形成循环（A 的上级是 B、B 的上级又是 A），会的话取消其中一条，返回被取消的要素。
    /// </summary>
    public List<GeoNode> BreakCycles()
    {
        var broken = new List<GeoNode>();
        foreach (var p in Proposals)
        {
            if (p.Chosen == null) continue;
            int guard = _pool.Count + 2;
            for (var a = p.Chosen; a != null && guard-- > 0; a = ParentOf(a))
            {
                if (!ReferenceEquals(a, p.Node)) continue;
                p.Chosen = null;
                broken.Add(p.Node);
                break;
            }
        }
        return broken;
    }

    /// <summary>
    /// 按当前选择统计各层级的要素数（点标记单独统计）。层级的名称取这一层里最常见的级别文字，
    /// 例如“省”“路”，显示为“省级”“路级”；没有级别文字时为“第 1 级”。
    /// </summary>
    public List<(string Name, int Count)> LevelSummary(out int points)
    {
        points = 0;
        var byDepth = new SortedDictionary<int, List<(GeoNode Node, string? Level)>>();
        bool anyLevel = false;
        foreach (var node in Proposals.Select(p => p.Node).Concat(UsedGroups()))
        {
            if (node.Kind == NodeKind.Point)
            {
                points++;
                continue;
            }
            int depth = 0, guard = _pool.Count + CreatedGroups.Count + 2;
            for (var a = ParentOf(node); a != null && guard-- > 0; a = ParentOf(a)) depth++;
            if (!byDepth.TryGetValue(depth, out var list)) byDepth[depth] = list = new List<(GeoNode, string?)>();
            var level = HierarchyDetector.LevelName(node);
            anyLevel |= level != null;
            list.Add((node, level));
        }

        // 数据里有级别文字时按级别命名，没有级别文字的要素（例如混在一起的水系）单独算“其他”；
        // 完全没有级别文字时按深度叫“第 N 级”
        var result = new List<(string, int)>();
        int others = 0;
        foreach (var (depth, nodes) in byDepth)
        {
            if (!anyLevel)
            {
                result.Add(($"第 {depth + 1} 级", nodes.Count));
                continue;
            }
            var named = nodes.Where(x => x.Level != null).ToList();
            others += nodes.Count - named.Count;
            if (named.Count == 0) continue;
            var name = named.GroupBy(x => x.Level!).OrderByDescending(g => g.Count()).First().Key;
            bool plain = name is "政权" or "国家" or "朝代" || name.Length > 2 || name.EndsWith('级');
            result.Add((plain ? name : name + "级", named.Count));
        }
        if (others > 0) result.Add(("其他", others));
        return result;
    }
}

/// <summary>
/// 自动识别上下级关系。依据两类证据：
/// <list type="number">
/// <item>属性字段：自动找出“指向另一个要素”的字段（名称里带 parent、pid、上级等，值能在其他要素的 id、编码或名称字段里找到），
/// 以及行政区划代码的前缀（330106 → 330100 → 330000）。</item>
/// <item>空间包含：在要素内部均匀取点，看有多大比例落在另一个面里；取包含它的最小的面作为上级（县 → 州 → 路）。
/// 能认出级别时，面的上级必须比它高一级以上。</item>
/// </list>
/// 两类证据一致时为“已确认”，只有部分落在范围内、跨了几个区域或同名候选不止一个时为“待确认”，
/// 属性和空间位置矛盾、或会形成循环时为“存在冲突”。只读取节点，不修改任何东西，可以在后台线程运行。
/// </summary>
public static class HierarchyDetector
{
    private const double ConfirmShare = 0.9;
    private const double ContainShare = 0.5;
    private const double CandidateShare = 0.2;

    public static HierarchyDetection Detect(IReadOnlyList<GeoNode> subjects, IReadOnlyList<GeoNode> pool, DetectOptions options, CancellationToken token = default)
    {
        var rules = new List<string>();
        var inPool = new HashSet<GeoNode>(pool, ReferenceEqualityComparer.Instance);
        var order = new Dictionary<GeoNode, int>(pool.Count, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < pool.Count; i++) order[pool[i]] = i;

        var attributes = options.Attributes ? AttributeLinks.Discover(subjects, pool, inPool, rules) : null;
        var spatial = options.Spatial ? new SpatialIndex(pool, order) : null;
        if (spatial != null) rules.Add("空间包含关系");

        var proposals = subjects.Select(n => new LinkProposal(n)).ToList();
        var parallel = new ParallelOptions { CancellationToken = token };
        Parallel.For(0, proposals.Count, parallel, i =>
        {
            Decide(proposals[i], options, inPool, attributes, spatial);
        });
        token.ThrowIfCancellationRequested();

        var created = new List<GeoNode>();
        if (attributes != null && options.CreateMissing) CreateMissingParents(proposals, attributes, created, rules);
        if (options.GroupTop) GroupTopLevel(proposals, created, rules);

        var detection = new HierarchyDetection(proposals, rules, pool, created);
        foreach (var node in detection.BreakCycles())
        {
            var p = proposals.First(x => ReferenceEquals(x.Node, node));
            p.Suggested = null;
            p.Status = LinkStatus.Conflict;
            p.Reason += "；这样会和其他关系形成循环，已取消";
        }
        return detection;
    }

    private static void Decide(LinkProposal p, DetectOptions options, HashSet<GeoNode> inPool, AttributeLinks? attributes, SpatialIndex? spatial)
    {
        var node = p.Node;

        // 文件里本来就有的上级（parentId、DataV 的 parent.adcode）或文档里现有的层级
        if (options.KeepExisting && node.Parent != null && inPool.Contains(node.Parent))
        {
            p.Set(node.Parent, LinkStatus.Confirmed, "保留已有的上下级关系");
            return;
        }

        var attr = attributes?.Resolve(node, options.CreateMissing) ?? default;
        if (attr.Missing != null)
        {
            p.Missing = attr.Missing;
            p.GrandRefs = attributes!.GrandRefs(node);
        }
        var geo = spatial != null && node.Geometry is { IsEmpty: false } ? spatial.Analyze(node) : null;

        if (attr.Parent is { } a)
        {
            double share = geo?.ShareIn(a) ?? -1;
            p.Candidates.Add(new LinkCandidate(a, share, "属性"));
            if (share < 0 || share >= ContainShare)
            {
                p.Set(a, LinkStatus.Confirmed, share >= 0 ? $"{attr.Rule}，空间上也在其范围内" : attr.Rule);
                return;
            }
            // 属性指定的上级在空间上对不上：看它落在哪个与属性上级同一层级的面里
            if (geo!.Peer(a) is { } b)
            {
                var other = b.Entry.Node;
                p.Candidates.Add(new LinkCandidate(other, b.Share, "空间"));
                if (other.DisplayName == a.DisplayName)
                {
                    // 同名的两个要素（例如同一个州分成了几块）：多半只是数据拆分，列为待确认
                    p.Set(a, LinkStatus.Pending, $"{attr.Rule}（{a.Id}），但它位于另一个同名的「{other.DisplayName}」（{other.Id}）内");
                }
                else
                {
                    p.Set(a, LinkStatus.Conflict, $"{attr.Rule}，但它有{Percent(b.Share)}位于「{other.DisplayName}」内");
                }
                return;
            }
            p.Set(a, LinkStatus.Pending, share > 0 ? $"{attr.Rule}，但只有{Percent(share)}在它的范围内" : $"{attr.Rule}，但位置不在它的范围内");
            return;
        }

        if (attr.Ambiguous is { Count: > 1 } many)
        {
            // 同名的上级不止一个：用空间位置挑
            var ranked = many.Select(n => (Node: n, Share: geo?.ShareIn(n) ?? -1)).OrderByDescending(x => x.Share).ToList();
            foreach (var (n, share) in ranked) p.Candidates.Add(new LinkCandidate(n, share, "属性"));
            if (ranked[0].Share >= ContainShare && (ranked.Count < 2 || ranked[1].Share < CandidateShare))
            {
                p.Set(ranked[0].Node, LinkStatus.Confirmed, $"{attr.Rule}（同名{many.Count}个，按位置确定）");
            }
            else
            {
                p.Set(ranked[0].Node, LinkStatus.Pending, $"{attr.Rule}，有{many.Count}个同名要素，无法确定是哪一个");
            }
            return;
        }

        if (attr.ExplicitlyNone)
        {
            p.Set(null, LinkStatus.None, attr.Rule);
            return;
        }

        if (geo?.Best is { } best)
        {
            p.Candidates.Add(new LinkCandidate(best.Entry.Node, best.Share, "空间"));
            var rivals = geo.Rivals(best);
            foreach (var r in rivals) p.Candidates.Add(new LinkCandidate(r.Entry.Node, r.Share, "空间"));
            string prefix = attr.Unresolved != null ? attr.Unresolved + "；" : "";
            if (best.SameShape)
            {
                p.Set(best.Entry.Node, LinkStatus.Pending, $"{prefix}形状与「{best.Entry.Node.DisplayName}」几乎相同，无法判断谁是上级");
            }
            else if (best.Share >= ConfirmShare && rivals.Count == 0)
            {
                p.Set(best.Entry.Node, LinkStatus.Confirmed, $"{prefix}{Percent(best.Share)}位于「{best.Entry.Node.DisplayName}」内");
            }
            else
            {
                string detail = rivals.Count == 0
                    ? $"只有{Percent(best.Share)}位于「{best.Entry.Node.DisplayName}」内"
                    : $"{Percent(best.Share)}位于「{best.Entry.Node.DisplayName}」内，另有" + string.Join("、", rivals.Select(r => $"{Percent(r.Share)}在「{r.Entry.Node.DisplayName}」"));
                p.Set(best.Entry.Node, LinkStatus.Pending, prefix + detail);
            }
            return;
        }

        p.Set(null, LinkStatus.None, attr.Unresolved ?? "没有找到包含它的区域");
    }

    private static string Percent(double share) => $"{Math.Round(share * 100):0}%";

    // ───────────────────────── 级别文字 ─────────────────────────

    private static readonly string[] LevelKeys = ["admin_type", "level", "级别", "行政级别", "等级", "类型", "type"];

    /// <summary>要素的级别文字：级别字段，没有时看 admin_type 等属性；太长的不算。</summary>
    public static string? LevelName(GeoNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Level)) return node.Level.Trim();
        foreach (var key in LevelKeys)
        {
            if (LevelTiers.TextOf(node.Extra, key) is { Length: > 0 and <= 6 } text) return text.Trim();
        }
        return null;
    }

    // ───────────────────────── 属性 ─────────────────────────

    /// <summary>按属性找到的上级。<see cref="Missing"/>：直接上级的引用字段有值，但数据里没有这个要素。</summary>
    private readonly record struct AttributeResult(GeoNode? Parent, List<GeoNode>? Ambiguous, bool ExplicitlyNone, string Rule, string? Unresolved, MissingRef? Missing = null);

    /// <summary>自动发现的“引用字段 → 被引用字段”规则和查找表。</summary>
    private sealed class AttributeLinks
    {
        private readonly List<(string Ref, string Target, Dictionary<string, List<GeoNode>> Lookup)> _pairs = new();
        private readonly List<string> _dangling = new();
        private readonly List<string> _grand = new();
        private readonly Dictionary<string, Dictionary<string, List<GeoNode>>> _lookups = new(StringComparer.Ordinal);
        private IReadOnlyList<GeoNode> _pool = [];
        private List<string> _idKeys = new();
        private List<string> _nameKeys = new();
        private string? _codeKey;
        private Dictionary<string, List<GeoNode>>? _codes;

        /// <summary>名称暗示“上级”的字段。</summary>
        private static bool IsParentKey(string key)
        {
            var k = key.ToLowerInvariant();
            return k.Contains("parent") || k is "pid" or "p_id" or "pcode" or "p_code" or "pid_code"
                   || k.Contains("father") || k.Contains("upper") || k.Contains("superior") || k.Contains("belong")
                   || k.Contains("上级") || k.Contains("父") || k.Contains("所属") || k.Contains("隶属");
        }

        private static bool IsIdKey(string key)
        {
            var k = key.ToLowerInvariant();
            return k.Contains("id") || k.Contains("code") || k.Contains("编码") || k.Contains("代码") || k.Contains("编号") || k is "gb" or "fid";
        }

        public static bool IsNameKey(string key)
        {
            var k = key.ToLowerInvariant();
            return k.Contains("name") || k.Contains("名") || k == "title";
        }

        /// <summary>“祖父”一类的字段排在后面，只在没有直接上级字段时用。</summary>
        private static int KeyPriority(string key)
        {
            var k = key.ToLowerInvariant();
            if (k.Contains("grand") || k.Contains("root") || k.Contains("祖")) return 2;
            return IsNameKey(key) ? 1 : 0;
        }

        public static string? Value(GeoNode node, string key)
        {
            switch (key)
            {
                case "@id":
                    return node.Id;
                case "@name":
                    return string.IsNullOrWhiteSpace(node.Name) ? null : node.Name.Trim();
            }
            if (!node.Extra.TryGetValue(key, out var v) || v is not JsonValue value) return null;
            string? text = value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number => value.ToJsonString(),
                _ => null,
            };
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static bool Has(GeoNode node, string key) => key.StartsWith('@') || node.Extra.ContainsKey(key);

        private Dictionary<string, List<GeoNode>> Lookup(string key)
        {
            if (_lookups.TryGetValue(key, out var d)) return d;
            d = new Dictionary<string, List<GeoNode>>(StringComparer.Ordinal);
            foreach (var n in _pool)
            {
                if (Value(n, key) is not { } v) continue;
                if (!d.TryGetValue(v, out var list)) d[v] = list = new List<GeoNode>(1);
                list.Add(n);
            }
            return _lookups[key] = d;
        }

        public static AttributeLinks Discover(IReadOnlyList<GeoNode> subjects, IReadOnlyList<GeoNode> pool, HashSet<GeoNode> inPool, List<string> rules)
        {
            var links = new AttributeLinks { _pool = pool };

            // 字段名（抽样统计，只看字符串和数字）
            var keys = new Dictionary<string, int>(StringComparer.Ordinal);
            int step = Math.Max(1, pool.Count / 5000);
            for (int i = 0; i < pool.Count; i += step)
            {
                foreach (var kv in pool[i].Extra)
                {
                    if (kv.Value is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
                    {
                        keys[kv.Key] = keys.GetValueOrDefault(kv.Key) + 1;
                    }
                }
            }
            var refKeys = keys.Keys.Where(IsParentKey).OrderBy(KeyPriority).ThenBy(k => k, StringComparer.Ordinal).ToList();
            var targets = new List<string> { "@id", "@name" };
            targets.AddRange(keys.Keys.Where(k => !IsParentKey(k) && (IsIdKey(k) || IsNameKey(k))));
            links._idKeys = ["@id", .. keys.Keys.Where(k => !IsParentKey(k) && IsIdKey(k) && !IsNameKey(k))];
            links._nameKeys = ["@name", .. keys.Keys.Where(k => !IsParentKey(k) && IsNameKey(k))];

            // 每个引用字段找命中最多的被引用字段
            int sampleStep = Math.Max(1, subjects.Count / 4000);
            int sampled = (subjects.Count + sampleStep - 1) / sampleStep;
            foreach (var r in refKeys)
            {
                int nonEmpty = 0;
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                var hits = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < subjects.Count; i += sampleStep)
                {
                    var s = subjects[i];
                    if (Value(s, r) is not { } v) continue;
                    nonEmpty++;
                    distinct.Add(v);
                    foreach (var t in targets)
                    {
                        if (t == r) continue;
                        if (links.Lookup(t).TryGetValue(v, out var found) && found.Any(x => !ReferenceEquals(x, s)))
                        {
                            hits[t] = hits.GetValueOrDefault(t) + 1;
                        }
                    }
                }
                if (nonEmpty == 0) continue;
                if (KeyPriority(r) == 2) links._grand.Add(r);

                bool paired = false;
                if (hits.Count > 0)
                {
                    // 命中数相同时 id、编码类字段优先于名称
                    var best = hits.OrderByDescending(h => h.Value).ThenBy(h => IsNameKey(h.Key) || h.Key == "@name" ? 1 : 0).First();
                    if (best.Value >= Math.Max(1, nonEmpty * 0.3))
                    {
                        links._pairs.Add((r, best.Key, links.Lookup(best.Key)));
                        paired = true;
                        string target = best.Key switch
                        {
                            "@id" => "要素 id",
                            "@name" => "名称",
                            _ => best.Key,
                        };
                        rules.Add($"属性 {r} → {target}");
                    }
                }
                // 数据里完全找不到、但很多要素共用同一个值的引用字段：上级不在数据里（例如只有县的文件带着州的编号和名称）
                if (!paired && KeyPriority(r) < 2 && nonEmpty >= sampled * 0.5 && distinct.Count <= nonEmpty * 0.8)
                {
                    links._dangling.Add(r);
                }
            }

            // 行政区划代码：6 位数字按前缀找上级
            foreach (var key in new[] { "@id", "adcode", "ADCODE", "code", "Code", "区划代码", "行政区划代码", "gb", "GB" })
            {
                if (key != "@id" && !keys.ContainsKey(key)) continue;
                int total = 0, sixDigits = 0;
                for (int i = 0; i < pool.Count; i += step)
                {
                    if (Value(pool[i], key) is not { } v) continue;
                    total++;
                    if (v.Length == 6 && v.All(char.IsAsciiDigit)) sixDigits++;
                }
                if (total < 2 || sixDigits < total * 0.8) continue;
                links._codeKey = key;
                links._codes = links.Lookup(key);
                rules.Add("行政区划代码的前缀");
                break;
            }
            return links;
        }

        /// <param name="createMissing">直接上级的引用找不到时记下来（之后新建分组），不再退而用祖父字段。</param>
        public AttributeResult Resolve(GeoNode node, bool createMissing)
        {
            string? unresolved = null;
            bool explicitNone = false;
            string? explicitRule = null;
            MissingRef? missing = null;
            foreach (var (r, t, lookup) in _pairs)
            {
                if (!Has(node, r)) continue;
                // 直接上级找不到、要新建时，不用祖父字段顶替
                if (missing != null && KeyPriority(r) == 2) break;
                var v = Value(node, r);
                if (v == null)
                {
                    // 有这个字段但值为空：数据明确说它没有上级（只看优先级最高的字段）
                    if (explicitRule == null)
                    {
                        explicitNone = true;
                        explicitRule = $"属性{r}为空";
                    }
                    continue;
                }
                explicitNone = false;
                if (lookup.TryGetValue(v, out var found))
                {
                    var list = found.Where(x => !ReferenceEquals(x, node)).ToList();
                    if (list.Count == 1) return new AttributeResult(list[0], null, false, $"属性{r}指向「{list[0].DisplayName}」", null);
                    if (list.Count > 1) return new AttributeResult(null, list, false, $"属性{r}为「{v}」", null);
                }
                unresolved ??= $"属性{r}指向的「{v}」不在数据里";
                if (createMissing && missing == null && KeyPriority(r) < 2) missing = new MissingRef(r, v, NameFor(node, r, v));
            }
            if (createMissing && missing == null)
            {
                foreach (var r in _dangling)
                {
                    if (Value(node, r) is not { } v) continue;
                    missing = new MissingRef(r, v, NameFor(node, r, v));
                    unresolved ??= $"属性{r}指向的「{v}」不在数据里";
                    explicitNone = false;
                    break;
                }
            }

            if (missing == null && _codes != null && _codeKey != null && Value(node, _codeKey) is { Length: 6 } code && code.All(char.IsAsciiDigit))
            {
                foreach (var parentCode in CodeParents(code))
                {
                    if (_codes.TryGetValue(parentCode, out var found) && found.FirstOrDefault(x => !ReferenceEquals(x, node)) is { } parent)
                    {
                        return new AttributeResult(parent, null, false, $"区划代码{code}属于「{parent.DisplayName}」（{parentCode}）", null);
                    }
                }
            }
            return new AttributeResult(null, null, explicitNone && unresolved == null, explicitRule ?? "", unresolved, missing);
        }

        /// <summary>祖父一级的引用（id 类字段在前），给新建的上级找上级。</summary>
        public List<MissingRef>? GrandRefs(GeoNode node)
        {
            List<MissingRef>? list = null;
            foreach (var r in _grand)
            {
                if (Value(node, r) is not { } v) continue;
                (list ??= new List<MissingRef>()).Add(new MissingRef(r, v, NameFor(node, r, v)));
            }
            return list;
        }

        /// <summary>
        /// 缺少的上级叫什么：引用字段本身是名称字段时就是它的值，否则找配套的名称字段（parent_id → parent_name）。
        /// </summary>
        private static string? NameFor(GeoNode node, string key, string value)
        {
            if (IsNameKey(key)) return value;
            foreach (var candidate in CompanionNameKeys(key))
            {
                if (Value(node, candidate) is { } name) return name;
            }
            return null;
        }

        private static IEnumerable<string> CompanionNameKeys(string key)
        {
            (string From, string To)[] swaps =
            [
                ("_id", "_name"), ("Id", "Name"), ("ID", "NAME"), ("_code", "_name"), ("Code", "Name"), ("CODE", "NAME"),
                ("_adcode", "_name"), ("编码", "名称"), ("代码", "名称"), ("编号", "名称"), ("id", "name"),
            ];
            foreach (var (from, to) in swaps)
            {
                if (key.EndsWith(from, StringComparison.Ordinal)) yield return key[..^from.Length] + to;
            }
            yield return key + "_name";
            yield return key + "Name";
        }

        /// <summary>在数据里按 id、编码或名称找一个要素（找不到或不唯一时返回 null）。</summary>
        public GeoNode? FindByValue(string value, string refKey)
        {
            var keys = IsNameKey(refKey) ? _nameKeys.Concat(_idKeys) : _idKeys.Concat(_nameKeys);
            foreach (var k in keys)
            {
                if (Lookup(k).TryGetValue(value, out var found) && found.Count == 1) return found[0];
            }
            return null;
        }

        /// <summary>6 位行政区划代码的上级代码，由近到远：330106 → 330100 → 330000。</summary>
        private static IEnumerable<string> CodeParents(string code)
        {
            if (code[4..] != "00") yield return code[..4] + "00";
            if (code[2..4] != "00") yield return code[..2] + "0000";
        }
    }

    // ───────────────────────── 新建缺少的上级 ─────────────────────────

    /// <summary>名称的最后一个字是这些之一时作为新建分组的级别文字（“广南西路” → 路）。</summary>
    private const string LevelSuffixes = "路道府州军监县省市区盟旗郡国";

    private static string LevelFromName(string name)
    {
        name = name.Trim();
        return name.Length >= 2 && LevelSuffixes.Contains(name[^1]) ? name[^1].ToString() : "";
    }

    /// <summary>
    /// 属性里引用了、但数据里没有的上级：按引用值新建分组（名称取 parent_name 这类配套字段），引用它的要素挂到分组下。
    /// 新分组自己的上级先看要素的祖父字段（grandparent_id 等，找得到就挂到已有的要素下，找不到也新建），
    /// 没有祖父字段时看下级在空间上大多落在哪个区域里。
    /// </summary>
    private static void CreateMissingParents(List<LinkProposal> proposals, AttributeLinks links, List<GeoNode> created, List<string> rules)
    {
        var byValue = new Dictionary<string, GeoNode>(StringComparer.Ordinal);
        var members = new Dictionary<GeoNode, List<(LinkProposal P, GeoNode? Previous)>>(ReferenceEqualityComparer.Instance);
        int grandCounter = 0;

        GeoNode GetOrCreate(MissingRef m, string scope)
        {
            string key = scope + "\u0001" + m.Value;
            if (byValue.TryGetValue(key, out var g)) return g;
            string name = m.Name ?? m.Value;
            string id = AttributeLinks.IsNameKey(m.Key) ? $"grp_{scope}_{++grandCounter}" : m.Value;
            g = new GeoNode(id) { Name = name, Level = LevelFromName(name) };
            byValue[key] = g;
            created.Add(g);
            members[g] = new List<(LinkProposal, GeoNode?)>();
            return g;
        }

        foreach (var p in proposals)
        {
            if (p.Missing is not { } m) continue;
            var previous = p.Suggested;
            var group = GetOrCreate(m, "p");
            members[group].Add((p, previous));
            p.Candidates.Insert(0, new LinkCandidate(group, -1, "属性"));
            string name = m.Name ?? m.Value;
            p.Set(group, LinkStatus.Confirmed, $"属性{m.Key}指向的「{name}」不在数据里，已新建这个上级（范围由下级拼成）");
        }
        if (created.Count == 0) return;
        rules.Add("新建数据里缺少的上级");

        // 新分组的上级
        foreach (var group in created.ToList())
        {
            var list = members[group];
            var grand = list
                .Select(x => x.P.GrandRefs is { Count: > 0 } refs ? refs[0] : null)
                .OfType<MissingRef>()
                .GroupBy(r => (r.Key, r.Value))
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (grand != null && grand.Count() * 2 >= list.Count)
            {
                var reference = grand.First();
                group.Parent = links.FindByValue(reference.Value, reference.Key) ?? GetOrCreate(reference, "g");
                continue;
            }
            var spatial = list
                .Select(x => x.Previous)
                .OfType<GeoNode>()
                .GroupBy(x => x)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (spatial != null && spatial.Count() * 2 >= list.Count) group.Parent = spatial.Key;
        }

        // 新建的上级不能挂到自己的下级下面（数据自相矛盾时）
        var chosen = new Dictionary<GeoNode, GeoNode?>(ReferenceEqualityComparer.Instance);
        foreach (var p in proposals) chosen[p.Node] = p.Chosen;
        foreach (var group in created)
        {
            int guard = proposals.Count + created.Count + 2;
            for (var a = group.Parent; a != null && guard-- > 0; a = chosen.TryGetValue(a, out var c) ? c : a.Parent)
            {
                if (!ReferenceEquals(a, group)) continue;
                group.Parent = null;
                break;
            }
        }
    }

    private static readonly (string Key, string Level)[] RealmKeys =
    [
        ("realm", "政权"), ("polity", "政权"), ("regime", "政权"), ("政权", "政权"), ("所属政权", "政权"),
        ("country", "国家"), ("kingdom", "国家"), ("empire", "国家"), ("国家", "国家"), ("国别", "国家"), ("所属国", "国家"), ("国号", "国家"),
        ("dynasty", "朝代"), ("朝代", "朝代"),
    ];

    /// <summary>
    /// 按政权、国家这类字段给最上层再建一级分组（例如 24 路都属于“大宋帝国”）：
    /// 最上层的要素（和新建的分组，取其下级最常见的值）里，带这个字段的有六成以上有值时才建。
    /// </summary>
    private static void GroupTopLevel(List<LinkProposal> proposals, List<GeoNode> created, List<string> rules)
    {
        // 按当前建议的上级建出下级表
        var chosen = new Dictionary<GeoNode, GeoNode?>(ReferenceEqualityComparer.Instance);
        foreach (var p in proposals) chosen[p.Node] = p.Chosen;
        foreach (var g in created) chosen[g] = g.Parent;
        var children = new Dictionary<GeoNode, List<GeoNode>>(ReferenceEqualityComparer.Instance);
        foreach (var (n, parent) in chosen)
        {
            if (parent == null) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<GeoNode>();
            list.Add(n);
        }
        var tops = chosen.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList();
        if (tops.Count == 0) return;

        foreach (var (key, level) in RealmKeys)
        {
            // 每个最上层要素的值：自身的值，没有时取下级里最常见的值
            var values = new List<(GeoNode Node, string Value)>();
            int present = 0;
            foreach (var top in tops)
            {
                var (has, value) = ValueOf(top, key, children);
                if (!has) continue;
                present++;
                if (value != null) values.Add((top, value));
            }
            if (values.Count == 0 || values.Count < present * 0.6) continue;

            var groups = new Dictionary<string, GeoNode>(StringComparer.Ordinal);
            var byNode = new Dictionary<GeoNode, LinkProposal>(ReferenceEqualityComparer.Instance);
            foreach (var p in proposals) byNode[p.Node] = p;
            foreach (var (node, value) in values)
            {
                if (!groups.TryGetValue(value, out var group))
                {
                    group = new GeoNode($"realm_{groups.Count + 1}") { Name = value, Level = level };
                    groups[value] = group;
                    created.Add(group);
                }
                if (byNode.TryGetValue(node, out var p))
                {
                    p.Candidates.Insert(0, new LinkCandidate(group, -1, "属性"));
                    p.Set(group, LinkStatus.Confirmed, $"属性{key}为「{value}」，归入新建的「{value}」");
                }
                else
                {
                    node.Parent = group;
                }
            }
            rules.Add($"按属性 {key} 新建最上级");
            return;
        }

        static (bool Has, string? Value) ValueOf(GeoNode node, string key, Dictionary<GeoNode, List<GeoNode>> children)
        {
            // 合并过的数据里各要素字段相同、值为 null 的（例如水系也有 realm 字段但没有值）算没有这个字段
            if (LevelTiers.TextOf(node.Extra, key) is { } own && own.Trim().Length > 0) return (true, own.Trim());
            if (!children.TryGetValue(node, out var kids)) return (false, null);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            bool has = false;
            var stack = new Stack<GeoNode>(kids);
            int guard = 0;
            while (stack.Count > 0 && guard++ < 100_000)
            {
                var n = stack.Pop();
                if (LevelTiers.TextOf(n.Extra, key) is { } v && v.Trim().Length > 0)
                {
                    has = true;
                    counts[v.Trim()] = counts.GetValueOrDefault(v.Trim()) + 1;
                    continue;
                }
                if (children.TryGetValue(n, out var more))
                {
                    foreach (var m in more) stack.Push(m);
                }
            }
            return (has, counts.Count == 0 ? null : counts.OrderByDescending(kv => kv.Value).First().Key);
        }
    }

    // ───────────────────────── 空间 ─────────────────────────

    /// <summary>候选上级面。定位器第一次用到时才建，建好后可以多线程查询。</summary>
    private sealed class PolygonEntry(GeoNode node, Geometry geometry, int order)
    {
        public GeoNode Node { get; } = node;
        public Geometry Geometry { get; } = geometry;
        public double Area { get; } = geometry.Area;
        public int Order { get; } = order;
        public int? Tier { get; } = LevelTiers.Of(node);

        private readonly Lazy<IPointOnGeometryLocator> _locator = new(() =>
        {
            var locator = new IndexedPointInAreaLocator(geometry);
            locator.Locate(geometry.EnvelopeInternal.Centre); // 先建好索引，之后只读
            return locator;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly Lazy<Coordinate?> _inner = new(() =>
        {
            try
            {
                return geometry.InteriorPoint?.Coordinate;
            }
            catch (Exception)
            {
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        public IPointOnGeometryLocator Locator => _locator.Value;

        public Coordinate? Inner => _inner.Value;

        public bool Covers(Coordinate c) => Locator.Locate(c) != Location.Exterior;
    }

    private sealed class SpatialIndex
    {
        private readonly STRtree<PolygonEntry> _tree = new();
        private readonly Dictionary<GeoNode, PolygonEntry> _byNode = new(ReferenceEqualityComparer.Instance);

        public SpatialIndex(IReadOnlyList<GeoNode> pool, Dictionary<GeoNode, int> order)
        {
            foreach (var n in pool)
            {
                if (n.Kind != NodeKind.Polygon || n.Geometry is not { IsEmpty: false } g || !(g.Area > 0)) continue;
                var e = new PolygonEntry(n, g, order[n]);
                _byNode[n] = e;
                _tree.Insert(g.EnvelopeInternal, e);
            }
            _tree.Build();
        }

        public PolygonEntry? EntryOf(GeoNode node) => _byNode.GetValueOrDefault(node);

        public Placement Analyze(GeoNode node)
        {
            var g = node.Geometry!;
            var self = EntryOf(node);
            var samples = Samples(node, g, self);
            var result = new Placement(this, samples);
            if (samples.Length == 0) return result;

            double area = node.Kind == NodeKind.Polygon ? g.Area : 0;
            int? tier = node.Kind == NodeKind.Polygon ? LevelTiers.Of(node) : null;
            var found = new List<Hit>();
            foreach (var e in _tree.Query(g.EnvelopeInternal))
            {
                if (ReferenceEquals(e.Node, node)) continue;
                bool sameShape = false;
                if (area > 0)
                {
                    // 上级不会比下级小；面积几乎一样时（只有一个下级的上级）按文件里的先后，前面的算上级
                    if (e.Area < area * 0.98) continue;
                    if (e.Area < area * 1.02)
                    {
                        if (self != null && e.Order > self.Order) continue;
                        sameShape = true;
                    }
                }
                double share = result.ShareIn(e);
                if (share < CandidateShare) continue;
                // 能认出级别时，面的上级必须比它高
                if (tier is int t && e.Tier is int et && et >= t) continue;
                found.Add(new Hit(e, share, sameShape));
            }
            result.Hits = found;
            result.Best = found.Where(h => h.Share >= ContainShare).OrderBy(h => h.Entry.Area).FirstOrDefault();
            if (result.Best is { SameShape: true } same && same.Share < 0.95) result.Best = same with { SameShape = false };
            return result;
        }

        /// <summary>
        /// 取样点：面在内部均匀取约 40 个点（大块多、小块少，按面积分配），线取沿线的顶点，点取自身。
        /// </summary>
        private static Coordinate[] Samples(GeoNode node, Geometry g, PolygonEntry? self)
        {
            switch (node.Kind)
            {
                case NodeKind.Point:
                    return g.Coordinates.Take(32).ToArray();
                case NodeKind.Line:
                {
                    var coords = g.Coordinates;
                    if (coords.Length <= 32) return coords;
                    var list = new Coordinate[32];
                    for (int i = 0; i < 32; i++) list[i] = coords[(int)((long)i * (coords.Length - 1) / 31)];
                    return list;
                }
                case NodeKind.Polygon:
                {
                    var env = g.EnvelopeInternal;
                    IPointOnGeometryLocator locator = self?.Locator ?? new IndexedPointInAreaLocator(g);
                    double fill = env.Area > 0 ? g.Area / env.Area : 1;
                    int k = Math.Clamp((int)Math.Ceiling(Math.Sqrt(40 / Math.Max(fill, 0.02))), 4, 48);
                    var list = new List<Coordinate>(64);
                    for (int i = 0; i < k; i++)
                    {
                        for (int j = 0; j < k; j++)
                        {
                            // 格子中心略微错开，避免正好落在横平竖直的边界上
                            var c = new Coordinate(env.MinX + (i + 0.5 + 0.013 * j) / k * env.Width, env.MinY + (j + 0.5 + 0.017 * i) / k * env.Height);
                            if (locator.Locate(c) == Location.Interior) list.Add(c);
                        }
                    }
                    if (list.Count < 4)
                    {
                        try
                        {
                            if (g.InteriorPoint?.Coordinate is { } ip) list.Add(ip);
                        }
                        catch (Exception)
                        {
                        }
                    }
                    return list.ToArray();
                }
                default:
                    return [];
            }
        }
    }

    private sealed record Hit(PolygonEntry Entry, double Share, bool SameShape);

    /// <summary>一个要素相对于各候选面的位置关系。</summary>
    private sealed class Placement(SpatialIndex index, Coordinate[] samples)
    {
        private readonly ConcurrentDictionary<PolygonEntry, double> _shares = new();

        public List<Hit> Hits { get; set; } = new();

        /// <summary>包含它（一半以上落在里面）的最小的面。</summary>
        public Hit? Best { get; set; }

        public double ShareIn(PolygonEntry e) => _shares.GetOrAdd(e, entry =>
        {
            if (samples.Length == 0) return 0;
            int inside = 0;
            foreach (var c in samples)
            {
                if (entry.Covers(c)) inside++;
            }
            return (double)inside / samples.Length;
        });

        /// <summary>落在某个要素里的比例；它不是面时为 -1（没法按空间判断）。</summary>
        public double ShareIn(GeoNode node) => index.EntryOf(node) is { } e ? ShareIn(e) : -1;

        /// <summary>
        /// 包含这个要素、并且与 <paramref name="expected"/>（属性指定的上级）同一层级的另一个面：
        /// 级别相同的优先，其次面积最接近的。没有时返回 null。
        /// </summary>
        public Hit? Peer(GeoNode expected)
        {
            if (index.EntryOf(expected) is not { } e) return null;
            Hit? best = null;
            foreach (var h in Hits)
            {
                if (h.Share < ContainShare || ReferenceEquals(h.Entry, e)) continue;
                // 把属性上级也包在里面的更高一级区域不算反证
                if (h.Entry.Area > e.Area && e.Inner is { } c && h.Entry.Covers(c)) continue;
                // 同一层级：能认出级别时级别相同，认不出时面积相差不超过 5 倍
                bool peer = h.Entry.Tier is int ht && e.Tier is int et ? ht == et : Math.Abs(Math.Log(h.Entry.Area / e.Area)) < Math.Log(5);
                if (!peer) continue;
                if (best == null || h.Share > best.Share || h.Share == best.Share && Math.Abs(Math.Log(h.Entry.Area / e.Area)) < Math.Abs(Math.Log(best.Entry.Area / e.Area)))
                {
                    best = h;
                }
            }
            return best;
        }

        /// <summary>与最佳候选同一层级、分走了一部分的其他面（不含把最佳候选也包在里面的更高一级）。</summary>
        public List<Hit> Rivals(Hit best)
        {
            var list = new List<Hit>();
            foreach (var h in Hits)
            {
                if (ReferenceEquals(h.Entry, best.Entry) || h.Share < 0.25 && !h.SameShape) continue;
                if (h.Entry.Area > best.Entry.Area && best.Entry.Inner is { } c && h.Entry.Covers(c)) continue;
                list.Add(h);
            }
            list.Sort((x, y) => y.Share.CompareTo(x.Share));
            if (list.Count > 3) list.RemoveRange(3, list.Count - 3);
            return list;
        }
    }
}
