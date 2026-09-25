// 核心逻辑测试（不启动界面）：读写、剪贴板、切割合并、撤销、分级简化、拓扑简化、大数据性能。
//   dotnet run --project tests/LogicTests                                    跑全部检查
//   dotnet run --project tests/LogicTests -- gen big.geojson 8 16 16 150     生成大数据：省数、每省市数、每市区县数、每条边顶点数
//   dotnet run --project tests/LogicTests -- bench big.geojson               离屏绘制一帧的耗时，对比分级简化前后

using System.Diagnostics;
using System.Text;

using GeoJsonEditor.App;
using GeoJsonEditor.Geo;
using GeoJsonEditor.IO;
using GeoJsonEditor.Map;
using GeoJsonEditor.Model;

using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;

if (args.Length >= 2 && args[0] == "gen")
{
    int v = BigData.Generate(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
    Console.WriteLine($"{v:N0} 个顶点，{new FileInfo(args[1]).Length / 1024.0 / 1024.0:0.0} MB");
    return 0;
}

if (args.Length >= 2 && args[0] == "bench")
{
    Bench.Run(args[1]);
    return 0;
}

if (args.Length >= 2 && args[0] == "detect")
{
    // 把几个文件当作一个文档（像依次导入那样）读进来，识别层级并打印结果
    var watch = Stopwatch.StartNew();
    var nodes = new List<GeoNode>();
    foreach (var file in args.Skip(1))
    {
        var rr = GeoJsonIO.ReadFile(file);
        nodes.AddRange(rr.Roots.SelectMany(x => x.SelfAndDescendants()));
        Console.WriteLine($"{Path.GetFileName(file)}：{rr.FeatureCount:N0} 个要素，已有 {rr.LinkedCount:N0} 个上下级");
    }
    Console.WriteLine($"读取 {watch.ElapsedMilliseconds} ms");
    watch.Restart();
    var det = HierarchyDetector.Detect(nodes, nodes, new DetectOptions());
    Console.WriteLine($"识别 {watch.ElapsedMilliseconds} ms；规则：{string.Join("、", det.Rules)}");
    var levelRows = det.LevelSummary(out int pts);
    foreach (var (name, count) in levelRows) Console.WriteLine($"  {name,-10} {count,6:N0}");
    Console.WriteLine($"  点标记     {pts,6:N0}");
    Console.WriteLine($"  已确认关系 {det.ConfirmedLinks,6:N0}  待确认 {det.Count(LinkStatus.Pending):N0}  存在冲突 {det.Count(LinkStatus.Conflict):N0}  无上级 {det.Proposals.Count(p => p.Chosen == null):N0}");
    foreach (var g in det.Proposals.Where(p => p.Status is LinkStatus.Pending or LinkStatus.Conflict).GroupBy(p => p.Status))
    {
        Console.WriteLine($"── {g.Key}（{g.Count()}）");
        foreach (var p in g.Take(12)) Console.WriteLine($"    {p.Node.DisplayName}（{p.Node.Level}）→ {p.Chosen?.DisplayName ?? "无"}：{p.Reason}");
    }
    return 0;
}

int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fails++; }
void Section(string title) => Console.WriteLine("\n── " + title);

// 从程序所在目录往上找仓库根目录（有 GeoJsonEditor.csproj 的那一层）
static string RepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "GeoJsonEditor.csproj"))) return dir.FullName;
    }
    throw new DirectoryNotFoundException("找不到仓库根目录（GeoJsonEditor.csproj）。");
}

var sample = Path.Combine(RepoRoot(), "samples", "示例-行政区层级.geojson");
var f = Geometries.Factory;

// 用程序自己的层级检查：返回全部问题
List<Editor.HierarchyIssue> Issues(Editor ed)
{
    var list = new List<Editor.HierarchyIssue>();
    foreach (var n in ed.Doc.AllNodes().ToList())
    {
        if (n.Children.Any(c => c.Kind == NodeKind.Polygon)) list.AddRange(ed.CheckHierarchy(n));
    }
    return list;
}

// ───────────────────────── 1. 读取 ─────────────────────────
Section("读取示例");
var ed = new Editor();
var doc = ed.Doc;
var r = ed.Open(sample);
Check(doc.Count == 16 && doc.Roots.Count == 1 && r.LinkedCount == 15, $"16 个节点、1 个根、15 个上下级关系（实际 {doc.Count}、{doc.Roots.Count}、{r.LinkedCount}）");
var issues0 = Issues(ed);
Check(issues0.Count == 0, $"示例数据的层级检查没有问题（实际 {issues0.Count} 处）");
foreach (var i in issues0.Take(5)) Console.WriteLine($"      {i.Kind} {i.Description}");

// ───────────────────────── 2. 读写往返 ─────────────────────────
Section("读写");
var text = File.ReadAllText(sample);
var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
var bomPath = Path.Combine(Path.GetTempPath(), "gje-bom.geojson");
File.WriteAllBytes(bomPath, withBom);
Check(GeoJsonIO.ReadFile(bomPath).FeatureCount == 16, "带 BOM 的 UTF-8 文件能读");
var written = GeoJsonIO.Write(doc.Roots);
var again = GeoJsonIO.Read(written);
Check(again.FeatureCount == 16 && again.LinkedCount == 15, "写出再读回，要素和层级不变");
Check(written.Split('\n').Length >= 17, "每个要素一行");
var extra = """{"type":"Feature","properties":{"name":"A","adcode":110000,"center":[116.405285,39.904989],"big":12345678901234567890,"flag":true,"nested":{"x":[1,2]}},"geometry":{"type":"Point","coordinates":[116.4,39.9]}}""";
var re = GeoJsonIO.Read(extra);
var outText = GeoJsonIO.Write(re.Roots);
Check(outText.Contains("\"center\":[116.405285,39.904989]") && outText.Contains("\"big\":12345678901234567890") && outText.Contains("\"nested\":{\"x\":[1,2]}"), "其他属性原样写回（数字不变形）");
Check(GeoJsonIO.Read("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}""").Roots[0].Kind == NodeKind.Polygon, "裸几何对象");
Check(GeoJsonIO.Read("""[{"type":"Feature","properties":{"name":"a"},"geometry":{"type":"Point","coordinates":[1,2]}},{"type":"LineString","coordinates":[[0,0],[1,1]]}]""").Roots.Count == 2, "要素 / 几何数组");
var ringOpen = GeoJsonIO.Read("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1]]]}""");
Check(ringOpen.Roots[0].Geometry is Polygon { NumPoints: 4 }, "没有闭合的环自动闭合");

// ───────────────────────── 3. 原有编辑功能（针对当前示例） ─────────────────────────
Section("编辑");
var c1 = doc.Find("c1")!;
var c2 = doc.Find("c2")!;
double a12 = c1.Geometry!.Area + c2.Geometry!.Area;
doc.SetSelection([c1, c2]);
Check(ed.CanMerge(), "可以合并相邻的两个市");
ed.Merge();
Check(doc.Find("c2") == null && Math.Abs(c1.Geometry!.Area - a12) < 1e-9 && c1.Children.Count == 5, "合并：面积相加，下级都挂到结果下");
doc.Undo();
Check(doc.Count == 16 && doc.Find("c2")!.Children.Count == 2, "撤销合并");

// 竖线切整个省
var env = doc.Find("p1")!.Geometry!.EnvelopeInternal;
double midX = (env.MinX + env.MaxX) / 2;
var cutter = f.CreateLineString([new Coordinate(midX, env.MinY - 0.1), new Coordinate(midX + 0.01, env.MaxY + 0.1)]);
doc.Select(doc.Find("p1"));
ed.CutMode.Value = CutMode.Replace;
ed.Cut(cutter);
Check(doc.Roots.Count(n => n.Kind == NodeKind.Polygon) == 2, "替换式切割：省被切成两块");
Check(Issues(ed).Count == 0, "切割后层级检查仍然没有问题");
doc.Undo();
Check(doc.Count == 16, "撤销切割");

// 预览与执行使用同一套目标
var plan = ed.PlanCut(cutter.EnvelopeInternal);
var preview = Editor.PreviewCut(plan, cutter);
Check(preview.Count >= 2, $"无选择时的切割预览（{preview.Count} 块）");

// 划分下级
var d1 = doc.Find("d1")!;
doc.Select(d1);
ed.CutMode.Value = CutMode.Subdivide;
var de = d1.Geometry!.EnvelopeInternal;
ed.Cut(f.CreateLineString([new Coordinate(de.MinX - 0.05, (de.MinY + de.MaxY) / 2), new Coordinate(de.MaxX + 0.05, (de.MinY + de.MaxY) / 2 + 0.005)]));
Check(d1.Children.Count(c => c.Kind == NodeKind.Polygon) == 2 && doc.Find("m2")!.Parent!.Parent == d1, "划分下级：两块乡镇，点标记归入其中一块");
doc.Undo();
ed.CutMode.Value = CutMode.Replace;

// ───────────────────────── 4. 剪贴板 ─────────────────────────
Section("剪贴板");
doc.Select(doc.Find("c1"));
var clip = ed.CopySelection()!;
var content = GeoClipboard.Parse(clip)!;
Check(content.Format == ClipboardFormat.Editor && content.Crs == CoordSystem.Wgs84 && content.DocumentId == doc.InstanceId, "复制内容带坐标系和来源文档");
Check(content.Roots.Count == 1 && content.Roots[0].SelfAndDescendants().Count() == 6, "复制的市连同 3 个区和 2 个点");
Check(content.SourceIds.SequenceEqual(["c1"]), "记录复制时的最上层 id");

// 同一文档：原样粘贴放在原要素后面
int before = doc.Count;
var pasted = ed.Paste(content);
Check(doc.Count == before + 6 && pasted[0].Parent == doc.Find("p1") && doc.IndexOf(pasted[0]) == doc.IndexOf(doc.Find("c1")!) + 1, "同一文档粘贴：与原要素同级，紧跟其后");
Check(pasted[0].Id != "c1" && pasted[0].Children.All(k => doc.Find(k.Id) == k), "粘贴后 id 不重复");
Check(doc.Selection.SequenceEqual(pasted), "粘贴后选中新要素");
doc.Undo();
Check(doc.Count == before, "撤销粘贴");

// 另一个文档（GCJ-02）：选中的面下面，并且坐标纠偏
var edB = new Editor();
edB.Open(sample);
edB.DataCrs.Value = CoordSystem.Gcj02;
var target = edB.Doc.Find("c3")!;
edB.Doc.Select(target);
var contentB = GeoClipboard.Parse(clip)!;
var src = content.Roots[0].Geometry!.Coordinate;
var pastedB = edB.Paste(contentB);
Check(pastedB[0].Parent == target, "跨文档粘贴：成为所选面的下级");
var dst = pastedB[0].Geometry!.Coordinate;
var (gx, gy) = ChinaOffset.Wgs84ToGcj02(src.X, src.Y);
Check(Math.Abs(dst.X - gx) < 1e-9 && Math.Abs(dst.Y - gy) < 1e-9, "WGS-84 → GCJ-02 自动纠偏");

// 空文档：改用来源坐标系
var edC = new Editor();
edC.NewDocument();
edC.DataCrs.Value = CoordSystem.Gcj02;
var pastedC = edC.Paste(GeoClipboard.Parse(clip)!);
Check(edC.DataCrs.Value == CoordSystem.Wgs84 && pastedC[0].Geometry!.Coordinate.Equals2D(src), "空文档：直接采用来源坐标系，坐标不变");

// 剪切
doc.Select(doc.Find("d3"));
var cutText = ed.CutSelection();
Check(cutText != null && doc.Find("d3") == null && doc.UndoLabel == "剪切", "剪切 = 复制 + 删除（一步撤销）");
doc.Undo();

// 其他来源
var wkt = GeoClipboard.Parse("POLYGON((116 39, 117 39, 117 40, 116 40, 116 39))\nPOINT(116.5 39.5)");
Check(wkt is { Format: ClipboardFormat.Wkt } && wkt.Roots.Count == 2 && wkt.Roots[0].Kind == NodeKind.Polygon, "多行 WKT");
Check(GeoClipboard.Parse("SRID=4326;LINESTRING(116 39, 117 40)")?.Roots[0].Kind == NodeKind.Line, "EWKT");
var ll = GeoClipboard.Parse("39.90923, 116.397428");
Check(ll is { Format: ClipboardFormat.Coordinates } && ll.Roots[0].Geometry is Point p0 && Math.Abs(p0.X - 116.397428) < 1e-9, "“纬度, 经度”自动识别顺序");
var line = GeoClipboard.Parse("116.1 39.1\n116.2 39.2\n116.3 39.1");
Check(line?.Roots[0].Kind == NodeKind.Line, "多行坐标 → 线");
var ring = GeoClipboard.Parse("116.1,39.1\n116.2,39.2\n116.3,39.1\n116.1,39.1");
Check(ring?.Roots[0].Kind == NodeKind.Polygon, "首尾相同 → 面");
Check(GeoClipboard.Parse("你好 世界") == null && GeoClipboard.Parse("{\"a\":1") == null && GeoClipboard.Parse("2024") == null, "认不出的文本返回 null");
var foreign = GeoClipboard.Parse("""{"type":"Feature","properties":{"name":"外部"},"geometry":{"type":"Point","coordinates":[118.8,32.0]}}""");
Check(foreign is { Format: ClipboardFormat.GeoJson, Crs: null }, "其他软件的 GeoJSON");
var edD = new Editor(); edD.NewDocument();
var pw = edD.Paste(wkt!);
Check(pw.All(n => !string.IsNullOrWhiteSpace(n.Name)), "WKT 粘贴后自动命名");

// 创建副本 / 全选同级
doc.Select(doc.Find("c4"));
var copies = ed.Duplicate();
Check(copies.Count == 1 && copies[0].Name == "松原市 副本" && copies[0].Children.Count == 1 && copies[0].Children[0].Id != "m4", "创建副本：带下级，名称加“副本”，id 不重复");
doc.Undo();
doc.Select(doc.Find("d1"));
ed.SelectSiblings();
Check(doc.Selection.Count == 3 && doc.Selection.All(n => n.Parent == doc.Find("c1")), "全选同级");
doc.ClearSelection();
ed.SelectSiblings();
Check(doc.Selection.Count == 1 && doc.Selection[0] == doc.Find("p1"), "没有选择时全选根级");

// ───────────────────────── 5. 撤销快照复用 ─────────────────────────
Section("撤销");
var edU = new Editor(); edU.Open(sample);
var d4 = edU.Doc.Find("d4")!;
edU.Rename(d4, "改名 1");
edU.Doc.BreakCoalescing();
edU.Rename(d4, "改名 2");
edU.Doc.BreakCoalescing();
edU.SetVisible([edU.Doc.Find("c3")!], false);
edU.Doc.Undo();
Check(edU.Doc.Find("c3")!.Visible && d4.Name == "改名 2", "撤销一步");
edU.Doc.Undo();
Check(d4.Name == "改名 1", "再撤销一步");
edU.Doc.Redo();
edU.Doc.Redo();
Check(d4.Name == "改名 2" && !edU.Doc.Find("c3")!.Visible, "重做两步");

// ───────────────────────── 6. 分级简化与 DP 一致 ─────────────────────────
Section("分级简化");
var rnd = new Random(7);
bool lodOk = true;
for (int trial = 0; trial < 40; trial++)
{
    int n = 50 + rnd.Next(2000);
    var xy = new double[n * 2];
    double x = rnd.NextDouble(), y = rnd.NextDouble();
    for (int i = 0; i < n; i++)
    {
        x += (rnd.NextDouble() - 0.5) * 1e-3;
        y += (rnd.NextDouble() - 0.5) * 1e-3;
        xy[i * 2] = x;
        xy[i * 2 + 1] = y;
    }
    var sig = Lod.Significance(xy, false);
    foreach (var tol in new[] { 1e-5, 1e-4, 5e-4 })
    {
        var filtered = Lod.Filter(xy, sig, tol, false)!;
        var coordsIn = Enumerable.Range(0, n).Select(i => new Coordinate(xy[i * 2], xy[i * 2 + 1])).ToArray();
        var dp = DouglasPeuckerLineSimplifier.Simplify(coordsIn, tol);
        // NTS 用点到直线的距离，这里用点到线段的距离，结果可能略有不同：比较保留点数和最大偏差
        var lineIn = f.CreateLineString(coordsIn);
        var outCoords = Enumerable.Range(0, filtered.Length / 2).Select(i => new Coordinate(filtered[i * 2], filtered[i * 2 + 1])).ToArray();
        var lineOut = f.CreateLineString(outCoords);
        double hd = NetTopologySuite.Algorithm.Distance.DiscreteHausdorffDistance.Distance(lineIn, lineOut);
        if (hd > tol * 1.0001) { lodOk = false; Console.WriteLine($"      trial {trial} tol {tol}: 偏差 {hd} > 容差"); }
        if (outCoords.Length > dp.Length * 1.3 + 3) { lodOk = false; Console.WriteLine($"      trial {trial} tol {tol}: {outCoords.Length} 点，NTS {dp.Length} 点"); }
    }
}
Check(lodOk, "按显著度筛选的结果偏差不超过容差，点数与 NTS 的 DP 相当");

// 环：小于容差时消失，大的至少保留三角形
var sq = new double[] { 0, 0, 1e-6, 0, 1e-6, 1e-6, 0, 1e-6, 0, 0 };
var sqSig = Lod.Significance(sq, true);
Check(Lod.Filter(sq, sqSig, 1e-5, true) == null && Lod.Filter(sq, sqSig, 1e-7, true) != null, "小环在粗级别消失，细级别保留");

// 投影缓存：各级结果、标注
var shapeNode = doc.Find("p1")!;
var shape = ShapeCache.Build(shapeNode.Geometry!, 0, CoordSystem.Wgs84, CoordSystem.Wgs84);
var counts = Enumerable.Range(0, 20).Select(l => shape.PathsAt(l).Sum(p => p == null ? 0 : p.Length / 2)).ToList();
Console.WriteLine("      省边界各级顶点数：" + string.Join(" ", counts.Select((c, i) => $"z{i}:{c}")));
Check(counts[4] < counts[12] && counts[18] == shape.VertexCount, "低级别顶点少，高级别等于原始顶点");
var (lx, ly, lr) = shape.Label;
var labelPt = WebMercator.Inverse(lx, ly);
Check(shapeNode.Geometry!.Contains(f.CreatePoint(new Coordinate(labelPt.Lon, labelPt.Lat))) && lr > 0, "标注点在面内");

// ───────────────────────── 7. 拓扑简化 ─────────────────────────
Section("简化边界（保持拓扑）");
var edS = new Editor(); edS.Open(sample);
int provincePoints = edS.Doc.Find("p1")!.Geometry!.NumPoints;
var items = edS.SimplifyCandidates(selectionOnly: false);
var res = TopoSimplifier.Simplify(items, 300);
Console.WriteLine($"      300 m：顶点 {res.VerticesBefore} → {res.VerticesAfter}，改变 {res.Changes.Count} 个，修复 {res.Repaired}，去掉 {res.DroppedParts}");
Check(res.VerticesAfter < res.VerticesBefore * 0.8, "顶点明显减少");
edS.ApplySimplify(res, 300);
var issuesS = Issues(edS);
Check(issuesS.Count == 0, $"简化后层级检查仍然没有问题（实际 {issuesS.Count} 处）");
foreach (var i in issuesS.Take(5)) Console.WriteLine($"      {i.Kind} {i.Description}");
// 相邻区县之间：公共边界仍然完全重合（两块并集的面积 = 面积之和）
var dd1 = edS.Doc.Find("d1")!.Geometry!;
var dd2 = edS.Doc.Find("d2")!.Geometry!;
Check(dd1.Intersection(dd2).Area < 1e-12 && dd1.Intersects(dd2), "相邻区县简化后既不重叠也仍然相接");
edS.Doc.Undo();
Check(edS.Doc.Find("p1")!.Geometry!.NumPoints == provincePoints, "撤销简化");

// ───────────────────────── 8. 大数据 ─────────────────────────
Section("大数据");
var bigPath = Path.Combine(Path.GetTempPath(), "gje-big.geojson");
var sw = Stopwatch.StartNew();
int totalVertices = BigData.Generate(bigPath, provinces: 4, citiesPerProvince: 12, countiesPerCity: 12, vertsPerEdge: 90);
Console.WriteLine($"      生成：{new FileInfo(bigPath).Length / 1024.0 / 1024.0:0.0} MB，约 {totalVertices:N0} 个顶点，{sw.ElapsedMilliseconds} ms");
sw.Restart();
var big = GeoJsonIO.ReadFile(bigPath);
long tRead = sw.ElapsedMilliseconds;
var allNodes = big.Roots.SelectMany(x => x.SelfAndDescendants()).ToList();
sw.Restart();
var shapes = ShapeCache.BuildMany(allNodes, CoordSystem.Wgs84, CoordSystem.Gcj02);
long tBuild = sw.ElapsedMilliseconds;
Console.WriteLine($"      读取 {tRead} ms（{big.FeatureCount:N0} 个要素），并行投影 + 显著度 {tBuild} ms");
sw.Restart();
int sumLow = 0, sumMid = 0;
foreach (var (_, s) in shapes) sumLow += s.PathsAt(5).Sum(p => p == null ? 0 : p.Length / 2);
long tLevel5 = sw.ElapsedMilliseconds;
sw.Restart();
foreach (var (_, s) in shapes) sumMid += s.PathsAt(9).Sum(p => p == null ? 0 : p.Length / 2);
long tLevel9 = sw.ElapsedMilliseconds;
int sumFull = shapes.Sum(x => x.Shape.VertexCount);
Console.WriteLine($"      第 5 级 {sumLow:N0} 个顶点（{tLevel5} ms），第 9 级 {sumMid:N0}（{tLevel9} ms），原始 {sumFull:N0}");
Check(sumLow < sumFull / 20, "全国级别的缩放下绘制的顶点不到原始的 5%");
sw.Restart();
int labels = 0;
foreach (var (_, s) in shapes.Take(300)) { if (s.Label.Radius > 0) labels++; }
Console.WriteLine($"      300 个标注位置 {sw.ElapsedMilliseconds} ms");
var edBig = new Editor();
edBig.Load(big, bigPath);
sw.Restart();
var bigRes = TopoSimplifier.Simplify(edBig.SimplifyCandidates(false), 200);
Console.WriteLine($"      简化边界 200 m：{bigRes.VerticesBefore:N0} → {bigRes.VerticesAfter:N0}，{sw.ElapsedMilliseconds} ms，修复 {bigRes.Repaired}");
edBig.ApplySimplify(bigRes, 200);
var sampleCity = edBig.Doc.AllNodes().First(n => n.Level == "市");
var cityIssues = edBig.CheckHierarchy(sampleCity);
Check(cityIssues.Count == 0, $"大数据简化后抽查一个市：层级检查没有问题（{cityIssues.Count} 处）");
sw.Restart();
var outPath = Path.Combine(Path.GetTempPath(), "gje-big-out.geojson");
edBig.Save(outPath);
Console.WriteLine($"      保存 {sw.ElapsedMilliseconds} ms，{new FileInfo(outPath).Length / 1024.0 / 1024.0:0.0} MB");
sw.Restart();
for (int i = 0; i < 10; i++) edBig.Rename(sampleCity, "改名" + i);
Console.WriteLine($"      10 次编辑（含撤销快照）{sw.ElapsedMilliseconds} ms");
sw.Restart();
edBig.Doc.SetSelection(edBig.Doc.AllNodes().Where(n => n.Parent == sampleCity));
var bigClip = edBig.CopySelection()!;
var parsedBig = GeoClipboard.Parse(bigClip)!;
Console.WriteLine($"      复制 {edBig.Doc.Selection.Count} 个区县并解析 {sw.ElapsedMilliseconds} ms（{bigClip.Length / 1024} KB）");
Check(parsedBig.Roots.Count == edBig.Doc.Selection.Count, "大段剪贴板往返");

// ───────────────────────── 9. 点聚合 ─────────────────────────
Section("点聚合");
{
    var rndC = new Random(11);
    var leafList = new List<PointClusterIndex.Leaf>();
    var dummy = new GeoNode("x");
    for (int i = 0; i < 5000; i++)
    {
        // 几个密集的城市群，另有一成零散分布的点
        double cx = 0.80 + (i % 5) * 0.01, cy = 0.40 + (i % 3) * 0.01;
        double spread = i % 10 == 0 ? 0.05 : 0.002;
        var color = (i % 4) switch { 0 => SkiaSharp.SKColors.Red, 1 => SkiaSharp.SKColors.Blue, 2 => SkiaSharp.SKColors.Green, _ => SkiaSharp.SKColors.Black };
        leafList.Add(new PointClusterIndex.Leaf(dummy, i, cx + (rndC.NextDouble() - 0.5) * spread, cy + (rndC.NextDouble() - 0.5) * spread, rndC.Next(0, 6) + (float)rndC.NextDouble() * 0.1f, -1, color));
    }
    var swC = Stopwatch.StartNew();
    var idx = PointClusterIndex.Build(leafList);
    Console.WriteLine($"      5,000 个点建索引 {swC.ElapsedMilliseconds} ms；第 4 级 {idx.At(4).Length} 簇，第 8 级 {idx.At(8).Length}，第 12 级 {idx.At(12).Length}");
    bool countsOk = true, parentsOk = true, monotone = true, spacingOk = true;
    for (int z = 0; z <= PointClusterIndex.MaxLevel; z++)
    {
        var level = idx.At(z);
        var finer = idx.At(z + 1);
        if (level.Sum(c => c.Count) != leafList.Count) countsOk = false;
        if (level.Length > finer.Length) monotone = false;
        foreach (var c in finer)
        {
            if (c.Parent < 0 || c.Parent >= level.Length) parentsOk = false;
        }
        // 同一级的簇中心之间距离都大于聚合半径
        if (level.Length <= 1500)
        {
            double rad = PointClusterIndex.RadiusDip / (MapViewport.TileSize * Math.Pow(2, z));
            for (int a = 0; a < level.Length && spacingOk; a++)
                for (int b = a + 1; b < level.Length; b++)
                {
                    double dx = level[a].X - level[b].X, dy = level[a].Y - level[b].Y;
                    if (dx * dx + dy * dy <= rad * rad) { spacingOk = false; break; }
                }
        }
    }
    Check(countsOk && parentsOk && monotone, "每一级的簇都由下一级合并而来，数量守恒");
    Check(spacingOk, "同一级的簇之间的距离都大于聚合半径");
    Check(idx.At(PointClusterIndex.MaxLevel + 1).Length == leafList.Count && idx.At(2).Length < 20, "最细一级每点一簇，全国级别只剩少数几个簇");
    var largest = idx.At(6).Select((c, k) => (c, k)).OrderByDescending(x => x.c.Count).First();
    var members = idx.Members(6, largest.k);
    Check(members.Count == largest.c.Count && members.Min() == largest.c.Rep, "簇的代表点是成员里最重要的点，簇的位置就是它的位置");
    int expand = idx.ExpansionLevel(6, largest.k);
    Check(expand > 6 && idx.At(expand).Count(c => members.Contains(c.Rep)) > 1, $"单击放大到第 {expand} 级时这个簇拆开");
    Check(largest.c.Mix != null && largest.c.Mix.Sum(m => m.Count) == largest.c.Count && largest.c.Mix.Length == 4, "簇记录成员的颜色构成");
}
Check(LevelTiers.FromText("省") == 1 && LevelTiers.FromText("市辖区") == 3 && LevelTiers.FromText("prefecture_seat") == 2
      && LevelTiers.FromText("街道") == 4 && LevelTiers.FromText("路") == 1 && LevelTiers.FromText("湖泊") == null, "从级别文字推断层级");

// ───────────────────────── 10. 自动识别层级 ─────────────────────────
Section("自动识别层级");
{
    var truth = new Editor();
    truth.Open(sample);
    var expected = truth.Doc.AllNodes().ToDictionary(n => n.Id, n => n.Parent?.Id);

    // 把 parentId 换成别的字段名：读进来是平的，层级只能靠识别
    var flatText = File.ReadAllText(sample).Replace("\"parentId\"", "\"upper_id\"");
    var flatRead = GeoJsonIO.Read(flatText);
    var flatNodes = flatRead.Roots.SelectMany(x => x.SelfAndDescendants()).ToList();
    Check(flatRead.LinkedCount == 0 && !flatRead.HasHierarchyFields && flatNodes.Count == 16, "去掉 parentId 后读入是平的");

    var byAttr = HierarchyDetector.Detect(flatNodes, flatNodes, new DetectOptions(Attributes: true, Spatial: false));
    Check(byAttr.Rules.Any(r => r.Contains("upper_id")) && byAttr.Proposals.All(p => p.Chosen?.Id == expected[p.Node.Id]),
        $"按属性：自动找到 upper_id → id，全部 16 个要素的上级正确（{string.Join("、", byAttr.Rules)}）");

    var bySpace = HierarchyDetector.Detect(flatNodes, flatNodes, new DetectOptions(Attributes: false, Spatial: true));
    var wrong = bySpace.Proposals.Where(p => p.Node.Kind != NodeKind.Line && p.Chosen?.Id != expected[p.Node.Id]).ToList();
    Check(wrong.Count == 0, $"按空间包含：面和点的上级都与原来一致（不一致 {wrong.Count} 个：{string.Join("、", wrong.Select(p => $"{p.Node.Name}→{p.Chosen?.Name}"))}）");
    var levels = bySpace.LevelSummary(out int pts);
    Console.WriteLine("      " + string.Join("  ", levels.Select(l => $"{l.Name} {l.Count}")) + $"  点标记 {pts}；已确认 {bySpace.ConfirmedLinks}，待确认 {bySpace.Count(LinkStatus.Pending)}，冲突 {bySpace.Count(LinkStatus.Conflict)}");
    Check(levels.Take(3).Select(l => (l.Name, l.Count)).SequenceEqual([("省级", 1), ("市级", 4), ("区县级", 5)]) && pts == 5, "各层级数量：省级 1、市级 4、区县级 5，点标记 5");

    // 应用到还没进文档的要素，再作为一个文档读进来
    var rebuilt = bySpace.Rebuild(flatNodes);
    Check(rebuilt.Count(n => n.Kind == NodeKind.Polygon) == 1 && rebuilt[0].Name == "青禾省" && rebuilt[0].Children.Count(c => c.Kind == NodeKind.Polygon) == 4, "按识别结果组织成树");

    // 在文档上重新识别：一步撤销
    var edH = new Editor();
    edH.Load(GeoJsonIO.Read(flatText), null);
    Check(!edH.Doc.WritesHierarchy, "没有层级字段的文件：保存时默认保持原有字段");
    var all = edH.Doc.AllNodes().ToList();
    var detDoc = HierarchyDetector.Detect(all, all, new DetectOptions());
    edH.ApplyHierarchy(detDoc);
    Check(edH.Doc.Find("d1")!.Parent?.Id == "c1" && edH.Doc.Roots.Count(n => n.Kind == NodeKind.Polygon) == 1 && edH.Doc.UndoLabel == "识别层级结构", "应用到文档（一步撤销）");
    edH.Doc.Undo();
    Check(edH.Doc.Roots.Count == 16, "撤销后恢复为平的");

    // 用户改选后形成循环：取消其中一条
    var cyc = HierarchyDetector.Detect(flatNodes, flatNodes, new DetectOptions(true, false));
    var pp = cyc.Proposals.First(p => p.Node.Id == "p1");
    pp.Chosen = cyc.Proposals.First(p => p.Node.Id == "d1").Node;
    var broken = cyc.BreakCycles();
    Check(broken.Count == 1, "用户的选择形成循环时自动取消");

    var songDir = "/Users/air/Downloads/Geojson/data/processed/1102_song";
    if (Directory.Exists(songDir))
    {
        var swH = Stopwatch.StartNew();
        var songNodes = new[] { "admin/level_1", "admin/level_2", "admin/counties", "settlements/seats" }
            .SelectMany(fn => GeoJsonIO.ReadFile(Path.Combine(songDir, fn + ".geojson")).Roots)
            .ToList();
        long tRead2 = swH.ElapsedMilliseconds;
        swH.Restart();
        var song = HierarchyDetector.Detect(songNodes, songNodes, new DetectOptions());
        var songLevels = song.LevelSummary(out int songPts);
        Console.WriteLine($"      北宋数据 {songNodes.Count:N0} 个要素：读取 {tRead2} ms，识别 {swH.ElapsedMilliseconds} ms；"
            + string.Join("  ", songLevels.Select(l => $"{l.Name} {l.Count:N0}")) + $"  点标记 {songPts:N0}；已确认 {song.ConfirmedLinks:N0}，待确认 {song.Count(LinkStatus.Pending)}，冲突 {song.Count(LinkStatus.Conflict)}");
        Check(songLevels.Take(3).Select(l => (l.Name, l.Count)).SequenceEqual([("路级", 24), ("州级", 330), ("县级", 1287)]), "北宋数据：路 → 州 → 县三级");
        Check(song.ConfirmedLinks > 2800 && song.Count(LinkStatus.Conflict) == 0, "北宋数据：绝大多数关系已确认，没有冲突");
    }
}

// ───────────────────────── 11. 保持原有字段 / 写入层级 ─────────────────────────
Section("保存方式");
{
    var srcJson = """
        {"type":"FeatureCollection","features":[
        {"type":"Feature","id":7,"properties":{"feature_id":"a1","name_zh":"甲","admin_type":"州","fill":"#ff0000","pop":12},"geometry":{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}},
        {"type":"Feature","properties":{"feature_id":"b1","name_zh":"乙","admin_type":"县"},"geometry":{"type":"Point","coordinates":[1,1]}}]}
        """;
    var edP = new Editor();
    edP.Load(GeoJsonIO.Read(srcJson), null);
    var a = edP.Doc.Roots[0];
    var b = edP.Doc.Roots[1];
    Check(a.Id == "7" && a.Name == "甲" && a.Level == "州" && a.Color == "#FF0000" && b.Id == "b1" && b.Name == "乙", "名称、级别、id 从 name_zh、admin_type、feature_id 读取");
    edP.MoveTo([b], a);
    edP.Rename(a, "甲州");
    var plain = GeoJsonIO.Write(edP.Doc.Roots, null, hierarchy: false);
    Check(!plain.Contains("\"parentId\"") && !plain.Contains("\"name\"") && !plain.Contains("\"level\"") && !plain.Contains("\"icon\""),
        "保持原有字段：不加 parentId、name、level 等字段");
    Check(plain.Contains("\"name_zh\":\"甲州\"") && plain.Contains("\"id\":7") && plain.Contains("\"fill\":\"#ff0000\"") && plain.Contains("\"pop\":12"),
        "保持原有字段：改名写回 name_zh，Feature id、颜色、其他属性原样");
    var native = GeoJsonIO.Write(edP.Doc.Roots, null, hierarchy: true);
    var back = GeoJsonIO.Read(native);
    Check(native.Contains("\"parentId\":\"7\"") && back.LinkedCount == 1 && back.HasHierarchyFields, "导出并保留层级信息：写入 id、parentId，读回层级不变");

    edP.Doc.Select(b);
    edP.DeleteSelection();
    edP.Doc.Undo();
    Check(edP.Doc.Find("b1")?.Source?.Fields == SourceFields.None && GeoJsonIO.Write(edP.Doc.Roots, null, false).Contains("\"name_zh\":\"乙\""), "删除再撤销后仍记得原有字段");
}

// ───────────────────────── 12. 自动更新 ─────────────────────────
Section("自动更新");
{
    Check(UpdateService.ParseVersion("v1.2.0") == new Version(1, 2, 0) && UpdateService.ParseVersion("V2.0") == new Version(2, 0, 0)
          && UpdateService.ParseVersion("1.3.1-beta") == new Version(1, 3, 1) && UpdateService.ParseVersion("latest") == null, "解析版本号");
    Check(UpdateService.CurrentVersion >= new Version(1, 2, 0), $"当前版本 {UpdateService.CurrentVersion}");
    var json = """
        {"tag_name":"v9.8.7","name":"GeoJSON 层级编辑器 9.8.7","body":"## 新功能\n- 点聚合\n- **自动更新**","html_url":"https://github.com/x/y/releases/tag/v9.8.7","published_at":"2026-09-26T08:00:00Z",
         "assets":[{"name":"GeoJsonEditor-9.8.7-win-x64.zip","browser_download_url":"https://example.com/win.zip","size":123,"digest":"sha256:ABCDEF"},
                   {"name":"GeoJsonEditor-9.8.7-macos-arm64.zip","browser_download_url":"https://example.com/mac.zip","size":456}]}
        """;
    var win = UpdateService.Parse(json, "win-x64");
    var mac = UpdateService.Parse(json, "macos-arm64");
    Check(win.Version == new Version(9, 8, 7) && win.IsNewer && win.Asset!.Url.EndsWith("win.zip") && win.Asset.Sha256 == "abcdef", "解析发布信息：按平台挑安装包，带 SHA-256 摘要");
    Check(mac.Asset!.Size == 456 && mac.Asset.Sha256 == null && UpdateService.Parse(json, "linux-x64").Asset == null, "没有摘要、没有对应平台安装包的情况");

    // Windows 式替换：旧文件改名为 .old，新文件放到原位，用户自己的文件不动
    var root = Path.Combine(Path.GetTempPath(), "gje-update-test-" + Guid.NewGuid().ToString("N"));
    var app = Path.Combine(root, "app");
    var staged = Path.Combine(root, "staged");
    Directory.CreateDirectory(Path.Combine(app, "samples"));
    Directory.CreateDirectory(Path.Combine(staged, "samples"));
    File.WriteAllText(Path.Combine(app, "GeoJsonEditor.exe"), "old");
    File.WriteAllText(Path.Combine(app, "samples", "a.geojson"), "old");
    File.WriteAllText(Path.Combine(app, "我的数据.geojson"), "mine");
    File.WriteAllText(Path.Combine(staged, "GeoJsonEditor.exe"), "new");
    File.WriteAllText(Path.Combine(staged, "samples", "a.geojson"), "new");
    File.WriteAllText(Path.Combine(staged, "使用说明.txt"), "new");
    UpdateService.ApplyFolder(new PendingUpdate(staged, new InstallTarget(app, false), new Version(9, 9, 9)), Path.Combine(app, "GeoJsonEditor.exe"));
    Check(File.ReadAllText(Path.Combine(app, "GeoJsonEditor.exe")) == "new" && File.ReadAllText(Path.Combine(app, "samples", "a.geojson")) == "new"
          && File.ReadAllText(Path.Combine(app, "我的数据.geojson")) == "mine" && File.Exists(Path.Combine(app, "使用说明.txt"))
          && Directory.GetFiles(app, "GeoJsonEditor.exe.*.old").Length == 1, "替换程序文件夹：新文件到位，旧文件改名备用，用户文件不动");

    // macOS 式替换：整个 .app 换掉
    var bundle = Path.Combine(root, "编辑器.app");
    var stagedBundle = Path.Combine(root, "staged2", "编辑器.app");
    Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
    Directory.CreateDirectory(Path.Combine(stagedBundle, "Contents", "MacOS"));
    File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "GeoJsonEditor"), "old");
    File.WriteAllText(Path.Combine(stagedBundle, "Contents", "MacOS", "GeoJsonEditor"), "new");
    UpdateService.ApplyBundle(new PendingUpdate(stagedBundle, new InstallTarget(bundle, true), new Version(9, 9, 9)));
    Check(File.ReadAllText(Path.Combine(bundle, "Contents", "MacOS", "GeoJsonEditor")) == "new" && !Directory.Exists(stagedBundle), "替换 .app 包");
    Directory.Delete(root, recursive: true);
    UpdateService.Cleanup();
}

Console.WriteLine(fails == 0 ? "\n全部通过" : $"\n{fails} 项失败");
return fails;

/// <summary>
/// 生成层级一致的大数据：省 → 市 → 区县，区县是带噪声边界的网格，相邻区县共用同一条边界折线，
/// 市和省的边界由下级外边界上的同一批顶点组成（与真实的行政区数据一样）。
/// </summary>
static class BigData
{
    public static int Generate(string path, int provinces, int citiesPerProvince, int countiesPerCity, int vertsPerEdge)
    {
        // 区县网格：每个市 cw×ch 个区县，每个省 pw×ph 个市
        int cw = (int)Math.Ceiling(Math.Sqrt(countiesPerCity)), ch = (int)Math.Ceiling((double)countiesPerCity / cw);
        int pw = (int)Math.Ceiling(Math.Sqrt(citiesPerProvince)), phh = (int)Math.Ceiling((double)citiesPerProvince / pw);
        int gx = provinces * pw * cw, gy = phh * ch;
        double x0 = 100, y0 = 25, cell = 0.25;
        var rnd = new Random(42);

        // 网格节点（带抖动）
        var nodes = new Coordinate[gx + 1, gy + 1];
        for (int i = 0; i <= gx; i++)
            for (int j = 0; j <= gy; j++)
            {
                bool border = i == 0 || j == 0 || i == gx || j == gy;
                double jx = border ? 0 : (rnd.NextDouble() - 0.5) * cell * 0.3;
                double jy = border ? 0 : (rnd.NextDouble() - 0.5) * cell * 0.3;
                nodes[i, j] = new Coordinate(Math.Round(x0 + i * cell + jx, 7), Math.Round(y0 + j * cell + jy, 7));
            }

        // 每条网格边一条带噪声的折线（相邻区县共用）
        var hEdges = new Dictionary<(int, int), Coordinate[]>();
        var vEdges = new Dictionary<(int, int), Coordinate[]>();
        Coordinate[] Noisy(Coordinate a, Coordinate b)
        {
            var pts = new Coordinate[vertsPerEdge + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            double nx = -dy / len, ny = dx / len;
            double off = 0;
            for (int k = 0; k <= vertsPerEdge; k++)
            {
                double t = (double)k / vertsPerEdge;
                if (k == 0 || k == vertsPerEdge) off = 0;
                else off += (rnd.NextDouble() - 0.5) * len * 0.02;
                double taper = Math.Sin(Math.PI * t);
                pts[k] = new Coordinate(Math.Round(a.X + dx * t + nx * off * taper, 7), Math.Round(a.Y + dy * t + ny * off * taper, 7));
            }
            return pts;
        }
        for (int i = 0; i < gx; i++) for (int j = 0; j <= gy; j++) hEdges[(i, j)] = Noisy(nodes[i, j], nodes[i + 1, j]);
        for (int i = 0; i <= gx; i++) for (int j = 0; j < gy; j++) vEdges[(i, j)] = Noisy(nodes[i, j], nodes[i, j + 1]);

        // 任意一组格子的外边界：沿格子边走一圈
        Coordinate[] BoundaryOf(HashSet<(int, int)> cells)
        {
            // 收集有向边（逆时针：下边向右、右边向上、上边向左、左边向下），去掉内部边
            var edges = new Dictionary<(int, int, int, int), Coordinate[]>();
            foreach (var (i, j) in cells)
            {
                void Add(int ax, int ay, int bx, int by, Coordinate[] seq)
                {
                    if (edges.Remove((bx, by, ax, ay))) return;
                    edges[(ax, ay, bx, by)] = seq;
                }
                Add(i, j, i + 1, j, hEdges[(i, j)]);
                Add(i + 1, j, i + 1, j + 1, vEdges[(i + 1, j)]);
                Add(i + 1, j + 1, i, j + 1, hEdges[(i, j + 1)].Reverse().ToArray());
                Add(i, j + 1, i, j, vEdges[(i, j)].Reverse().ToArray());
            }
            var byStart = edges.ToDictionary(e => (e.Key.Item1, e.Key.Item2), e => e);
            var first = edges.First();
            var ring = new List<Coordinate>();
            var cur = first;
            do
            {
                ring.AddRange(cur.Value.Take(cur.Value.Length - 1));
                cur = byStart[(cur.Key.Item3, cur.Key.Item4)];
            } while (!cur.Key.Equals(first.Key));
            ring.Add(ring[0]);
            return ring.ToArray();
        }

        var sb = new StringBuilder("{\"type\":\"FeatureCollection\",\"features\":[\n");
        bool firstFeature = true;
        int vertices = 0;
        void Feature(string id, string? parent, string name, string level, Coordinate[] ring)
        {
            if (!firstFeature) sb.Append(",\n");
            firstFeature = false;
            vertices += ring.Length;
            sb.Append("{\"type\":\"Feature\",\"properties\":{\"id\":\"").Append(id).Append('"');
            if (parent != null) sb.Append(",\"parentId\":\"").Append(parent).Append('"');
            sb.Append(",\"name\":\"").Append(name).Append("\",\"level\":\"").Append(level).Append("\"},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[");
            for (int k = 0; k < ring.Length; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append('[').Append(ring[k].X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(ring[k].Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            }
            sb.Append("]]}}");
        }

        for (int p = 0; p < provinces; p++)
        {
            var pcells = new HashSet<(int, int)>();
            var cities = new List<(string Id, HashSet<(int, int)> Cells)>();
            for (int c = 0; c < citiesPerProvince; c++)
            {
                int cx = c % pw, cy = c / pw;
                var ccells = new HashSet<(int, int)>();
                for (int k = 0; k < countiesPerCity; k++)
                {
                    int kx = k % cw, ky = k / cw;
                    ccells.Add((p * pw * cw + cx * cw + kx, cy * ch + ky));
                }
                pcells.UnionWith(ccells);
                cities.Add(($"p{p}c{c}", ccells));
            }
            Feature($"p{p}", null, $"省{p + 1}", "省", BoundaryOf(pcells));
            foreach (var (cid, ccells) in cities)
            {
                Feature(cid, $"p{p}", $"市{cid}", "市", BoundaryOf(ccells));
                int k = 0;
                foreach (var cellKey in ccells)
                {
                    Feature($"{cid}d{k}", cid, $"区县{cid}-{k}", "区县", BoundaryOf([cellKey]));
                    k++;
                }
            }
        }
        sb.Append("\n]}\n");
        File.WriteAllText(path, sb.ToString());
        return vertices;
    }
}


/// <summary>按地图画布的方式往离屏画布上绘制面图层，比较分级简化前后的单帧耗时。</summary>
static class Bench
{
    public static void Run(string path)
    {
        var watch = Stopwatch.StartNew();
        var r = GeoJsonIO.ReadFile(path);
        long tRead = watch.ElapsedMilliseconds;
        var nodes = r.Roots.SelectMany(x => x.SelfAndDescendants()).ToList();
        watch.Restart();
        var shapes = ShapeCache.BuildMany(nodes, CoordSystem.Wgs84, CoordSystem.Gcj02);
        Console.WriteLine($"读取 {tRead} ms，并行投影（含 GCJ-02 纠偏）和显著度 {watch.ElapsedMilliseconds} ms");
        double minX = shapes.Min(s => s.Shape.MinX), maxX = shapes.Max(s => s.Shape.MaxX);
        double minY = shapes.Min(s => s.Shape.MinY), maxY = shapes.Max(s => s.Shape.MaxY);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        int w = 1440, h = 900;
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(w * 2, h * 2));
        var canvas = surface.Canvas;
        using var fill = new SkiaSharp.SKPaint { IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Fill, Color = new SkiaSharp.SKColor(0x3B, 0x82, 0xF6, 74) };
        using var stroke = new SkiaSharp.SKPaint { IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = new SkiaSharp.SKColor(0x1D, 0x4E, 0xD8) };
        var buffer = new SkiaSharp.SKPoint[1 << 16];

        double Frame(double zoom, bool lod)
        {
            double ws = 256 * Math.Pow(2, zoom);
            double vx0 = cx - w / 2.0 / ws, vx1 = cx + w / 2.0 / ws, vy0 = cy - h / 2.0 / ws, vy1 = cy + h / 2.0 / ws;
            int level = Lod.LevelForZoom(zoom);
            var sw = Stopwatch.StartNew();
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.Save();
            canvas.Scale(2);
            int drawn = 0;
            foreach (var (_, s) in shapes)
            {
                if (!s.Intersects(vx0, vy0, vx1, vy1)) continue;
                using var p = new SkiaSharp.SKPath { FillType = SkiaSharp.SKPathFillType.EvenOdd };
                var paths = lod ? s.PathsAt(level) : s.Paths;
                foreach (var ring in paths)
                {
                    if (ring == null) continue;
                    int n = ring.Length / 2;
                    if (buffer.Length < n) buffer = new SkiaSharp.SKPoint[n * 2];
                    for (int i = 0; i < n; i++) buffer[i] = new SkiaSharp.SKPoint((float)((ring[i * 2] - cx) * ws + w / 2.0), (float)((ring[i * 2 + 1] - cy) * ws + h / 2.0));
                    p.AddPoly(new ReadOnlySpan<SkiaSharp.SKPoint>(buffer, 0, n), true);
                    drawn += n;
                }
                canvas.DrawPath(p, fill);
                canvas.DrawPath(p, stroke);
            }
            canvas.Restore();
            surface.Flush();
            return sw.Elapsed.TotalMilliseconds;
        }

        Console.WriteLine($"{shapes.Count:N0} 个面，{shapes.Sum(s => s.Shape.VertexCount):N0} 个顶点；画布 1440×900（2 倍像素）");
        foreach (var zoom in new[] { 5.0, 6.5, 8.0, 10.0 })
        {
            Frame(zoom, true); Frame(zoom, false);
            double a = Enumerable.Range(0, 5).Average(_ => Frame(zoom, true));
            double b = Enumerable.Range(0, 3).Average(_ => Frame(zoom, false));
            Console.WriteLine($"  缩放 {zoom}: 分级简化 {a:0.0} ms / 帧，不简化 {b:0.0} ms / 帧");
        }
    }
}
