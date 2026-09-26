# 项目交接说明

## 需求（用户原话 + 后续确认）

> 实现一个支持层级关系的 geojson，需要支持合并切割等功能，需要支持点标记。使用 MewUI 实现，地图渲染画布用 skia。（https://github.com/aprillz/MewUI）

- **层级：** 行政区式树（省 → 市 → 区县），父子关系体现在几何上。
- **合并与切割：** 交互式编辑，画布上选中多个面合并，手画一条线切割，也能手绘新的面、线、点。
- **点标记：** 带名称、备注、图标（颜色可选）。
- **底图：** 在线瓦片底图。
- **UI 质量是重点。** 界面文字用中文。
- 用户在 macOS（Apple Silicon）上开发，**需要 Windows 版本**，所以不用原生 macOS 标题栏之类只在 Mac 上成立的做法，代码保持跨平台。

第二轮（1.1.0）新增的要求：

1. 剪贴板：开两张地图，从一张复制一部分粘贴到另一张。
2. 修复 Windows 上工具栏按钮要点两次才生效的问题（用户定位到 MewUI 关闭 Tooltip 时误调 `ReleaseCapture()`，已确认）。
3. 分级简化，优化大数据下的性能。
4. 其他必要的界面、性能优化和功能改进；打好 Windows 和 macOS 包；发布到用户 GitHub（公开仓库 + Release）。

第三轮（1.2.0）新增的要求：

1. 地名和城市标志：点多时在小比例尺下堆在一起，参考成熟地图软件做逐级展开（做成了点聚合 + 按重要度放置标注）。
2. 顶点编辑改为二级操作：选中只显示轮廓，双击 / 回车 / 按钮才显示顶点手柄。
3. 从 GitHub 获取新版本并自动更新。
4. 导入 GeoJSON 时可以选择自动识别上下级关系（属性字段 + 空间包含），先预览（各级数量、已确认 / 待确认 / 冲突），能逐个改待确认的；识别结果只存在程序内部，用户选“导出并保留层级信息”（或保存时选“写入层级信息”）才写进 properties。

第四轮（1.3.0）新增的要求（用户看了 1111 年地图数据的截图后提出）：

1. 缩小时边界粗细不一很难看。
2. 像 P 社游戏那样按层级显示：缩小时下级自动合并成一个大的上级，放大逐级展开下级；不同层级的显示和选中。
3. 几个区域共同属于一个更上的层级时，要自动生成这个上级。
4. 借鉴成熟经验整体优化，打包并推送 GitHub。检测环节用 Sonnet 子代理做，省 token。
5. （途中追加）更新做成大多数软件那样的“检查更新”按钮，能从 GitHub 查新版本并下载，要看得出它有没有在运行。

## 硬性约束

- UI 框架：MewUI 0.21.1（NuGet `Aprillz.MewUI` + `Aprillz.MewUI.Skia.All`）。
  例外：`Aprillz.MewUI.Platform.Win32` 用 `vendor/packages` 里的本地包 0.21.1.1，是 v0.21.1 源码加上游修复提交 ce00cdc（问题 #253）重新编译的，程序集版本仍是 0.21.1.0，元数据与官方包只差 `HandleDestroy` 这一个方法。生成方法见 `scripts/build-mewui-patch.sh`，本地源由根目录的 `nuget.config` 提供。上游发布包含该修复的版本后，删掉 `vendor/`、`nuget.config` 和 csproj 里那一行单独引用，所有 MewUI 包一起升级。
- 地图画布：Skia（`SkiaCanvasView` 的子类 `Map/MapCanvas`）。
- 几何运算：NetTopologySuite 2.6.0（含 `Coverage` 命名空间）。

## 当前状态（2026-09-25，1.3.0）

| 内容 | 状态 |
|---|---|
| 编译 | 通过，无警告 |
| 核心逻辑 | `dotnet run --project tests/LogicTests`：原有各项，加上“层级缩放显示与自动上级”一节（缺少的上级按 parent_name / grandparent_name 新建、按 realm 建最上级、自动边界面积、签名缓存、按缩放折叠展开和单击顺序、同族配色、编组、两种保存方式对新建分组的处理；本机有北宋数据时只用县文件还原出 大宋帝国 → 24 路 → 316 州 → 1,287 县），全部通过 |
| 画面 | `render` 命令离屏出图检查过 1111 年地图和北宋数据的各级缩放 |
| macOS 运行 | 1.3.0 由 Sonnet 子代理在后台窗口做过冒烟测试（见下） |
| 没实机验证的 | 真正从 GitHub 下载安装新版本；Windows 上的一切 |
| Windows | `scripts/publish.sh win` 在 macOS 上交叉打包，**没在 Windows 真机上验证过** |

测试数据：`/Users/air/Downloads/Geojson/data/processed/1102_song`（北宋，字段是 feature_id / parent_id / name_zh / admin_type / realm，分成 admin、settlements、physical 几个文件；另有 0742_tang）。用户截图用的是 `/Users/air/Downloads/中国1111年地图.geojson`（带 parentId：路、道、周边政权是根，下级是点和画成线的州界）。

- `dotnet run --project tests/LogicTests -- detect 文件...`：把几个文件当一个文档识别层级并打印结果。
- `dotnet run --project tests/LogicTests -- render out.png 文件... [--detect] [--zoom 4.5,6] [--center 经度,纬度] [--tiles] [--nolod]`：用程序自己的 `MapCanvas` 离屏出图（`SetViewForTest` / `RenderForTest`，自动边界同步算完），检查画面比开界面截图省得多。
- 界面检查交给 Sonnet 子代理（用户要求，省 token）：给它具体步骤，只用 computer-use 的后台工具，报告通过 / 失败即可。

## 本机环境

- .NET 10 SDK 装在 `~/.dotnet`，不在 PATH 上：

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
```

- 打包：`scripts/publish.sh [mac|win]`，产物在 `dist/`。
- GitHub：用户账号 Bing2000me，`gh` 已登录。仓库里的 git 提交身份是本地配置（WANG Sifan + GitHub noreply 邮箱），没有改全局配置。

## 注意事项

- `Program.cs` 里每个平台的注册放在单独的 `NoInlining` 方法里。按 RID 发布时只带当前平台的程序集，写在同一个方法里会在启动时找不到其他平台的程序集而崩溃。
- 根目录的 `Directory.Build.targets` 让应用和测试项目编译、发布时都不复制 NuGet 原生包里的 `.pdb`（`SkiaSharp.NativeAssets.Win32` 的三个 Windows 版 `libSkiaSharp.pdb`，每个 80 多 MB，发布的 Windows 包本来就不带）。不指定平台的编译输出因此从 422M 降到 178M 左右。
- `GeoDocument` 的所有修改都要放在 `Edit(...)` 里，撤销是整份快照（几何对象按不可变值共享引用；没变的节点复用上一次的状态对象），恢复时按 id 就地更新节点对象。历史步数按文档大小自动收缩。
- MewUI 的 `ToggleButton` 没有 `Click` 事件，工具按钮和标签按钮用 `Ui/ClickToggle`。
- 复制 / 剪切 / 粘贴 / 全选走 MewUI 的 `StandardCommands`，在窗口级注册处理器：输入框有焦点时由输入框自己处理（复制文字），焦点在地图、图层树时复制要素。不要把 ⌘C 等直接映射到窗口的 `InputMap`，会抢在应用级映射之前，把输入框的复制截走。也不要把单个字母键映射进 `InputMap`：输入框不在 KeyDown 里吃字母键，会漏到窗口的映射上。
- 剪贴板内容是 GeoJSON 文本，`FeatureCollection` 上带 `geojsonEditor` 字段（坐标系、来源文档实例 id、复制时的最上层 id）。同一文档原样粘贴放在原要素后面；跨文档按当前选择；坐标系不同自动纠偏；空文档直接采用来源坐标系。
- 分级简化：`Map/Lod.cs` 预先算每个顶点的 DP 显著度，`ProjectedShape.PathsAt(level)` 按缩放级别筛选并缓存。容差为该级别下 0.25 DIP。标注位置（最大内切圆）、分块包围盒都按需计算。
- 大数据时 `MapCanvas` 把面和线画到离屏图层缓存里（`DrawFeatureLayerCached`），拖动顶点时不用缓存。文档改动靠 `_docStamp`、配色相关的变化靠 `_styleStamp` 让缓存失效，新增影响面和线绘制的状态时记得加进去。
- 顶点手柄只在要素屏幕尺寸够大、顶点不太密、视野内不超过 2500 个时显示，否则单击要能穿透去逐级选择。
- 示例数据 `samples/示例-行政区层级.geojson` 已修成严格的覆盖面（层级检查 0 处问题），改动示例后用测试程序确认。
- 高德瓦片带注记的只有 256px 版本（`scl=2` 的高清瓦片没有注记）。CARTO 现在要 API key，已去掉。
- MewUI 内置控件的文字在 `Ui/Localization.cs` 里统一换成中文。
- MewUI 的 `TextBlock` 换行：一段中文里有空格时会优先在空格处断行，数字前后带空格的长句换行会很难看，需要换行的长段落尽量不要夹带空格。
- MewUI 的 `CheckBox`、`Button` 文字里的下划线是快捷键标记，会被吃掉（“parent_id”显示成“parentid”），这类文字里不要写下划线。
- 点标记：`Map/PointClusters.cs` 是聚合索引，`MapCanvas.Points.cs` 画点和簇并记下画出来的标记，命中测试只认画出来的（收进簇里的点点不中）。选中的点不参与聚合，总是单独画在最上层。文档一改（`_docStamp`）就重建索引，两万个点以上在后台建。
- 顶点编辑状态是 `Editor.VertexEditTarget`，选择变了、换工具、要素被删或隐藏时自动退出。只是选中时不显示手柄。
- 自动识别层级：`Geo/HierarchyDetector.cs` 只读节点、可在后台线程跑；`HierarchyDetection.Rebuild` 把还没进文档的节点组织成树，`GeoDocument.Restructure` 在文档里整体改上级（一步撤销）。
- 保存方式：`GeoDocument.WritesHierarchy` 为 false（原文件没有 parentId）时按原有字段写（`GeoJsonIO` 的 hierarchy: false），节点上的 `SourceInfo` 记着原来有哪些字段、名称取自哪个字段。`SourceInfo` 在撤销快照里，删除再撤销不会丢。
- 按层级缩放显示（`Map/RegionIndex.cs`、`Map/MapCanvas.Regions.cs`）：区域 = 自身是面或下级里有面的可见节点；线、点挂到最近的区域上。文档一改（`_docStamp`）就重建区域树。每帧从根往下走：展开尺度（下级区域的典型边长；只有点、线的区域取自身边长 / 5.5）在屏幕上超过阈值（`ExpandPx`，随“展开细节”变化）就展开，渐变区间内下级的颜色从上级过渡、边界和名称逐渐显现。填充画在一个 `SaveLayer` 半透明图层里（图层内不透明），边界深的先画、浅的压上面。命中测试按显示级排序（`RegionChainAt`）。切割没有选择时只在显示着的那一级里找对象（`CutScope`）。
- 自动边界：没有自身几何的区域由下级拼出（`DerivedShapes`，后台由深到浅计算，按内容签名缓存）。`GeometryOps.Dissolve` 先用 `CoverageUnion`，结果里下级之间的细缝形成的小圈、尖刺用 `CleanRings` 线性剪掉——**不要**改回“检查 `IsValid` 再修复 / 重算并集”，北宋县数据上会从 1.7 秒变成十几秒。面积对不上（有重叠）才退回 `OverlayNGRobust`。县数据里的河道是真实的缺口，会显示成伸进区域的边界，这是数据本身的样子。
- 识别层级新建的分组：`HierarchyDetection.CreatedGroups`（`UsedGroups()` 是实际用到的），打开 / 导入时 `Rebuild` 把它们放进树里，文档里重新识别时 `Editor.ApplyHierarchy` 先加进文档再 `Restructure`。“保持原有字段”保存时不写这些没有几何、没有 `SourceInfo` 的分组。
- 自动配色是“同族同色”：下级区域取上级颜色的深浅变化（`MapStyle.Tint`），区域里的点和线取上级颜色加深。`MapStyle.ColorOf`（图层面板用）和画布的 `FillAutoColors` 缓存必须一致。
- 标注顺序：先画图钉和簇 → 放区域名称（避开图钉、簇）→ 画小圆点（和区域名称重叠的不画）→ 放地名。区域名称大的按主轴方向逐字拉开（`TrySpreadLabel`，陡的从上往下读）。
- 点的显示方式 `PointDisplay`：逐级显示（默认，簇的代表点；所属区域折叠时取更粗一级的代表点；藏起来的点多时整屏画成小圆点）、聚合计数（原来的数字圆）、全部。
- 自动更新：`App/UpdateService.cs`。右上角常驻“检查更新”按钮（用户要求，和一般软件一样，要看得出有没有在运行）：`UpdateService.Status`（正在检查 / 已是最新 / 新版本 / 下载中 / 待重启 / 失败）变化时按钮跟着变，手动检查、启动时的自动检查、下载都会更新它。替换在程序退出后做（`Program.Main` 在 `Run` 返回后和 `ProcessExit` 里各调一次 `ApplyPending`），仓库名写在 `UpdateService.Repository`。发布时附件名必须是 `GeoJsonEditor-<版本>-win-x64.zip` 和 `GeoJsonEditor-<版本>-macos-arm64.zip`，否则程序找不到安装包；tag 用 `v<版本>`。
