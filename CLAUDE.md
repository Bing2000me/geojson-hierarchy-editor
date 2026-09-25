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

## 硬性约束

- UI 框架：MewUI 0.21.1（NuGet `Aprillz.MewUI` + `Aprillz.MewUI.Skia.All`）。
  例外：`Aprillz.MewUI.Platform.Win32` 用 `vendor/packages` 里的本地包 0.21.1.1，是 v0.21.1 源码加上游修复提交 ce00cdc（问题 #253）重新编译的，程序集版本仍是 0.21.1.0，元数据与官方包只差 `HandleDestroy` 这一个方法。生成方法见 `scripts/build-mewui-patch.sh`，本地源由根目录的 `nuget.config` 提供。上游发布包含该修复的版本后，删掉 `vendor/`、`nuget.config` 和 csproj 里那一行单独引用，所有 MewUI 包一起升级。
- 地图画布：Skia（`SkiaCanvasView` 的子类 `Map/MapCanvas`）。
- 几何运算：NetTopologySuite 2.6.0（含 `Coverage` 命名空间）。

## 当前状态（2026-09-25，1.2.0）

| 内容 | 状态 |
|---|---|
| 编译 | 通过，无警告 |
| 核心逻辑 | `dotnet run --project tests/LogicTests`：原有各项，加上点聚合索引的不变量、自动识别层级（示例数据按属性 / 按空间都还原出原层级；本机有北宋数据时跑一遍，路 24 / 州 330 / 县 1,287）、两种保存方式、更新包的版本解析和文件替换，全部通过 |
| macOS 运行 | 用后台窗口看过：打开北宋合并数据时的识别对话框和预览、点聚合和单击展开、选中不显示顶点、双击进入顶点编辑 |
| 没实机验证的 | 真正从 GitHub 下载安装新版本（要等下一个版本发布后才能走完整流程；文件替换逻辑由测试程序覆盖）；Windows 上的一切 |
| Windows | `scripts/publish.sh win` 在 macOS 上交叉打包，**没在 Windows 真机上验证过** |

测试数据：`/Users/air/Downloads/Geojson/data/processed/1102_song`（北宋，字段是 feature_id / parent_id / name_zh / admin_type，分成 admin、settlements、physical 几个文件；另有 0742_tang）。`dotnet run --project tests/LogicTests -- detect 文件...` 把几个文件当一个文档识别层级并打印结果。

## 本机环境

- .NET 10 SDK 装在 `~/.dotnet`，不在 PATH 上：

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
```

- 打包：`scripts/publish.sh [mac|win]`，产物在 `dist/`。
- GitHub：用户账号 Bing2000me，`gh` 已登录。仓库里的 git 提交身份是本地配置（WANG Sifan + GitHub noreply 邮箱），没有改全局配置。

## 注意事项

- `Program.cs` 里每个平台的注册放在单独的 `NoInlining` 方法里。按 RID 发布时只带当前平台的程序集，写在同一个方法里会在启动时找不到其他平台的程序集而崩溃。
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
- 自动更新：`App/UpdateService.cs`。替换在程序退出后做（`Program.Main` 在 `Run` 返回后和 `ProcessExit` 里各调一次 `ApplyPending`），仓库名写在 `UpdateService.Repository`。发布时附件名必须是 `GeoJsonEditor-<版本>-win-x64.zip` 和 `GeoJsonEditor-<版本>-macos-arm64.zip`，否则程序找不到安装包；tag 用 `v<版本>`。
