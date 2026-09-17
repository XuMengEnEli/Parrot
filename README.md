# 🦜 鹦鹉 Parrot

> 一款背单词的辅助小工具
> Avalonia 跨平台（Windows / macOS），免费开源，数据全存本地，OCR 与发音降级链路离线可用。

[![.NET](https://img.shields.io/badge/.NET-10-5C2D91)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Avalonia](https://img.shields.io/badge/Avalonia-12-blue)](https://avaloniaui.net)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

## ✨ 这是什么

把摊在桌面上的**考研词汇讲义 PDF**变成可交互的学习工作台：

| 功能 | 说明 |
|---|---|
| 📖 双视图阅读 | **文本视图**：PdfPig 词框聚类重建原版式（分栏/标题/词条/正文）；**原图视图**：位图渲染，版式零损失 |
| 🔊 逐句发音 | 每个英文句子行尾挂喇叭；**扫描页也照常有**——系统 OCR 后台识别，只在原图上叠 🔊 热点，**OCR 文本不上屏**，仅供发音 |
| 📌 学习记录 | 点喇叭旁图钉把词记入当日列表（含释义快照与出处句）；「学习记录」页按日汇总、可回看可移除 |
| ⏱ 番茄钟 | 25/5 专注循环 + 近 14 天统计柱状图；电脑休眠自动暂停，不把睡过去的时间算成专注 |
| ✍️ 拼写考试 | 随机挖掉单词里 n 个字母回填（1–6 可调），对绿错红露正解给分；优先考今天 📌 记的词 |
| 🔔 复习弹窗 | 右下角"伪广告"小卡：优先抽今日学习记录，没记录退回**内嵌词典**（ECDICT）随机词；无焦点不打断输入 |
| 🔄 在线升级 | 应用内检查本仓库 GitHub Releases，发现新版只提示一行、不弹窗打扰，一键前往下载（按平台自动挑安装包） |
| 🧑‍💼 老板键 | Ctrl+Shift+H 一键收起托盘（全局热键），关窗默认驻留托盘 |

仓库**不打包任何讲义素材**（版权原因已从版本库排除）：应用打开的是你自己自备的 PDF。

发音链路带降级：句子走 Edge TTS（在线微软音色），单词走 有道 → Edge；全部失败自动退回系统本地音色（mac `say`/`afplay`、Win SAPI）——**音质下降但不断流**，且合成结果按 `sha256` 落盘缓存，离线也能重放。

## 📥 下载

到 [Releases](https://github.com/XuMengEnEli/Parrot/releases/latest) 下载对应平台安装包：

| 平台 | 文件 |
|---|---|
| Windows x64 | `Parrot-win-x64.zip` — 解压即用（自包含单文件，无需装 .NET） |
| Apple 芯片 Mac | `Parrot-mac-arm64.zip` |
| Intel Mac | `Parrot-mac-x64.zip` |

> **mac 首次运行**：包未做付费开发者签名与公证。解压后把 `Parrot.app` 拖进「应用程序」，
> 按包内《使用说明.txt》执行 `chmod +x` 与 `xattr -cr Parrot.app`，再**右键 → 打开**放行 Gatekeeper 一次。
> 之后正常双击启动。

## 🛠 从源码构建

前置：[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。Windows 与 macOS 同一条命令：

```bash
dotnet restore
dotnet build            # 零警告零错误是本项目标准
dotnet test             # 110 项单元测试
dotnet run --project src/Parrot.App
```

> mac 上 `dotnet run` 跑的是裸二进制（没有 `Info.plist`），Dock 里会是通用图标、名字显示成 `Parrot.App`——
> 这不是 bug。要看品牌图标与「鹦鹉」显示名，请用 `scripts/publish-mac.sh` 产出的 `Parrot.app`（见「打包发版」）。

整包在 mac 上 `dotnet build` 也能过：`Parrot.Ocr.Windows` 需要 windows TFM，靠 `Directory.Build.props` 里的
条件 `EnableWindowsTargeting` 交叉编译（只编译、不运行），WinRT 运行期仍只在 Windows 生效——
所以 clone 下来不会因平台缺包而红。

支持范围只有 **Windows 与 macOS**：Linux 能编译，但没有任何系统 OCR 实现被注册
（`Parrot.App` 的条件编译只有 `WINDOWS_OCR` / `MAC_OCR` 两支），扫描页链路与托盘行为也未在 Linux 验证。

无界面自检（不启动 GUI，直接验证提取与 OCR 链路，输出为文本）：

```bash
# 版式重建 + 断句
dotnet src/Parrot.App/bin/Debug/net10.0/Parrot.App.dll --preview <你的.pdf>

# 扫描页 OCR 热点（页码可选，默认第 1 页）；Windows 下 dll 路径为 bin/Debug/net10.0-windows10.0.19041.0/
dotnet src/Parrot.App/bin/Debug/net10.0/Parrot.App.dll --ocr <你的.pdf> [页码]
```

`--ocr` 会报告引擎是否可用、识别行数与耗时、正文门槛是否通过、生成的热点个数——
喇叭没出现时先看这里的输出定位。

## 🔍 扫描页 OCR

OCR 只用**操作系统自带的识别框架**，不引入原生依赖，识别全程离线、图片不出机器：

| 平台 | 引擎 | 前置条件 | 语言 |
|---|---|---|---|
| Windows | `Windows.Media.Ocr`（WinRT） | Windows 10 1903+，系统已装英文语言包的 OCR 组件 | 跟随系统已装语言 |
| macOS | Apple **Vision**（`VNRecognizeTextRequest`），经 `/usr/bin/osascript -l JavaScript` 进程桥调用 | 系统自带，无需安装；不支持删除 `osascript` 的环境（会被策略禁用时自动降级） | 中英双语 |

mac 侧走进程桥而不是 P/Invoke，是为了绕开 `objc_msgSend` 在 arm64 上返回结构体的 ABI 坑；
识别语言数组的**顺序**在实测中有影响（中文在前才能同时读出中英），已在单测里锁住。

两边都遵循同一条产品原则：**位图原图就是版式权威**，OCR 文本永远不上屏，只用来定位句尾、把 🔊 叠在原图上。

## 🗂 项目结构

```
src/
├─ Parrot.Core     # 领域逻辑：版式重建/断句/热点锚点/番茄钟状态机/升级解析（不依赖 UI）
├─ Parrot.Audio    # 发音链路：Edge TTS/有道/系统音色 + 磁盘缓存 + 降级
├─ Parrot.Data     # SQLite：词典/学习记录/番茄统计/设置（Meta KV）
├─ Parrot.Pdf      # PdfPig 文本层 + PDFtoImage 位图渲染
├─ Parrot.Ocr.Windows / Parrot.Ocr.Mac   # 平台 OCR，按宿主 × RID 条件接线
├─ Parrot.UI       # Avalonia 页面与 ViewModel（Semi 主题）
└─ Parrot.App      # 组合根：DI/托盘/老板键/弹窗/自动升级/CLI 自检
tests/Parrot.Tests # xunit，含 mac Vision 真机链路回归
tools/DevTools     # 图标程序化生成、mac .app 组装打包（无 GUI 依赖）
scripts/           # 发布与词表抓取：publish-win.ps1 / publish-mac.sh / make-mac-apps.ps1 / gen-icons.ps1 / fetch-ecdict.ps1（Win）+ fetch-ecdict.sh（mac）
```

## 🔒 数据与隐私

- 学习数据全部本地：SQLite 在 Windows `%LOCALAPPDATA%\Parrot\parrot.db`、mac `~/Library/Application Support/Parrot/parrot.db`；
  TTS 缓存在 `%LOCALAPPDATA%\Parrot\tts-cache`（mac `~/Library/Caches/Parrot/tts-cache`）；
- 从旧版（数据目录名为 `StudyEnglish`）首次启动会自动整体搬迁，词库与学习记录不丢；
- 讲义**不上传任何服务器**；OCR 完全在本机系统框架内完成；
- 联网只有两件事：TTS 云合成（Edge/有道，失败即退回本地音色）与检查更新（GitHub Releases API）；
- 词库数据源自 [ECDICT](https://github.com/skywind3000/ECDICT)（MIT）。应用内嵌 45 词 seed 保证开箱即用；
  全量 ECDICT（csv 约 65MB，导入后本地库约 115MB）自行导入：Win 用 `powershell -File scripts/fetch-ecdict.ps1`，
  mac/linux 用 `bash scripts/fetch-ecdict.sh`（也可 `... fetch-ecdict.sh <已下载的 csv>` 跳过下载）。
  两个脚本下载后都调用 `Parrot.App.dll --import-ecdict <csv>` 入库，只补空字段、不重置已有复习进度；
  学习记录读释义时会回落到词库现取，所以"先记词、后导入"的老记录也会自动补齐。

## 🚀 打包发版

```powershell
powershell -File scripts\publish-win.ps1   # → publish/Parrot-win-x64.zip
```

```bash
bash scripts/publish-mac.sh                # → publish/Parrot-mac-arm64.zip / Parrot-mac-x64.zip
```

mac 脚本会 `dotnet publish` 双架构自包含单文件，组装 `Parrot.app` 壳（`Info.plist` 的显示名/版本取自
`Directory.Build.props`），做 **ad-hoc 签名**（`codesign --sign -`）后用 `ditto` 打包成 zip。

**没有 Apple 开发者证书时的分发**：产出的是未付费签名包，用户侧按《使用说明.txt》
`chmod +x` + `xattr -cr Parrot.app` + 右键打开放行一次即可；
若你有证书，把脚本里的 `--sign -` 换成自己的身份标识，再走 `notarytool` 公证。

**安装包文件名是协议的一部分**：应用内升级按 `Parrot-{win|mac}-{x64|arm64}.zip` 挑包，
上传 Release 时请保持该命名。版本号统一在 `Directory.Build.props` 的 `<Version>`。

fork 自用请改一处：`Parrot.Core/Update/UpdateService.cs` 的 `ParrotRepo` 常量（指向你的 Releases 仓库）。

## ⚠️ 已知边界

- 扫描页发音依赖系统 OCR：印刷体识别率高，手写/艺术字/思维导图纯图形页会误读或读不出——
  纯图形页有"确有正文"门槛拦住，直接保留干净原图 + 占位卡，不叠乱码热点；
- 双栏版式按"列间隙 + 左缘错位"分栏，同一行的两格不再拼成一句；表格线缺失或栏距极窄的扫描件仍可能错行——切「原图视图」核对即可；
- OCR 文本的误读（`famlly`/`0f` 一类）只影响发音内容，不影响版面与热点位置；
- Edge TTS 在线协议在个别网络偶发 403 → 自动降级系统本地音色；
- 全局老板键在 mac 上需要「辅助功能」权限，未授权时静默降级为应用内快捷键；
- Windows 侧运行期需要真机验证：本项目在 mac 上交叉编译通过，WinRT OCR 的运行时行为未在 Windows 实测。

## 🙏 致谢

[skywind3000/ECDICT](https://github.com/skywind3000/ECDICT) · [Avalonia](https://avaloniaui.net) · [Semi.Avalonia](https://github.com/irihitech/Semi.Avalonia) · [PdfPig](https://github.com/UglyToad/PdfPig) · [PDFtoImage](https://github.com/pgrep/PDFtoImage) · [EdgeTts.Net](https://github.com/swinogre/edge-tts) · [ScottPlot](https://github.com/ScottPlot/ScottPlot) · [SharpHook](https://github.com/tom-englert/SharpHook)

## 📄 License

[MIT](LICENSE)
