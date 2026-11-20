# Reading Time Manager

Reading Time Manager 是一个 Windows 桌面工具，用来打开 Kindle 阅读统计插件生成的 `reading-time.tsv`，查看每日阅读时长、书籍统计和累计趋势。仅浏览，不修改原始记录。

数据来源：[Kindle 阅读时长统计插件](https://github.com/L-1116/kindle-reading-records-enhanced)。

## 使用

解压 Windows x64 ZIP，双击顶层的 `ReadingTimeManager.exe`。`app/` 内是程序依赖，不需要打开；请将它和 EXE 一起移动。无需安装 .NET 或 Python。升级时解压到新目录，不要覆盖旧版的平铺目录。

从应用的「打开 TSV…」选取 Kindle 的 `reading-time/reading-time.tsv`，或把单个 `.tsv` 拖到窗口。应用会尝试恢复上次成功打开的路径，也可以用「重新读取」刷新数据。只用只读权限打开记录；记忆路径保存在 Windows 当前用户注册表 `HKCU\Software\ReadingTimeManager`，不在 Kindle 数据目录。

- **每日时长**：灰度月历、按日分书明细，以及月份概览、排名和趋势。
- **阅读书籍**：书名或 ID 搜索、范围和排序、单书指标及月历。默认显示有正时长的全部书籍；可启用 Kindle 的满 5 分钟筛选。
- **累计时长**：按周、年、全部历史查看时长、阅读日、书数和趋势，支持最近 8 周详情。

导入使用当前 Kindle Standard 源码与历史版本的 `date / book_id / seconds / title` 表头，秒数按日和书聚合。同名不同 ID 不在书架或单书统计中合并；月份排名采用 Kindle 的按显示书名汇总口径。未来日期保留在日历及相关详情，但不计入截至今天的累计统计和书籍列表。封面、阅读进度和小时明细并不在这个 TSV 中，客户端不会猜测。损坏文件会报告原因，且不会导入部分数据。

## 开发

`Core/` 保存 TSV 读取与统计，`App/` 是 WPF UI，`Tests/` 使用合成记录，不依赖 Kindle 插件检出。开发需要 .NET 10 SDK。`App/Assets` 已包含从插件 Standard `launcher-icon.png` 裁切缩放的图标，正常构建无需 Python。重建图标时使用 Python + Pillow，并运行 `python convert_icon.py <原版 launcher-icon.png 路径>`。

```powershell
dotnet run --project Tests -c Release
$out = 'dist/Reading-Time-Manager-0.1.2-win-x64'
dotnet publish App -c Release -r win-x64 --self-contained true -p:DebugType=None -o $out
if ($LASTEXITCODE) { throw 'Publish failed' }
& ./Tests/Test-PublishedManifest.ps1 -ExePath "$out/ReadingTimeManager.exe"
Compress-Archive -Path $out -DestinationPath "$out.zip" -Force
```

测试不会打开前台窗口。可用 `-- <输出目录>` 生成离屏 PNG；也可用 `-- <输出目录> <awk.exe 路径> <原版 reading-insights-cache.awk 路径>`，用同一份合成 TSV 与 Kindle 原版聚合对照。
