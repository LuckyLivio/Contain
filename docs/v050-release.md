# Contain v0.5.0 正式发布说明与 Windows 验收记录

**发布状态**　Contain `v0.5.0` 正式版面向 Windows x64，冻结产品源码提交为 [`ddca87a1926d128821618fc9799aef09d834a6cd`](https://github.com/LuckyLivio/Contain/commit/ddca87a1926d128821618fc9799aef09d834a6cd)。本版已完成完整的 **干净源码机 A（`-Stage SourceMissing` → 补齐工具 → `-Stage Source`）→ 第二台完全断网无开发工具接收机 B（`receive-release-candidate.ps1` / `-Stage Package`）** 全链路闭环，以及独立断网沙箱中的 `Contain-Setup-x64.exe` 安装试装与公开 `v0.5.0-beta.3 → v0.5.0` 原位升级验证。

[下载 Contain v0.5.0 安装包（Windows x64）](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/Contain-Setup-x64.exe) · [便携桌面 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/contain-0.5.0-windows-x64.zip) · [独立 CLI ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/contain-workflow-0.5.0-windows-x64.zip) · [SHA256SUMS.txt](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/SHA256SUMS.txt) · [发行说明](https://github.com/LuckyLivio/Contain/releases/tag/v0.5.0)

---

## 版本、组件与构建身份

| 项目 | 固定身份 |
| --- | --- |
| 产品源码提交（`source_sha`） | `ddca87a1926d128821618fc9799aef09d834a6cd` |
| 桌面 UI / 安装器版本 | `0.5.0`（PE `FileVersion` / `AssemblyVersion` / `VersionInfoVersion = 0.5.0.0`），WPF + WPF-UI `4.3.0` / MVVM `8.4.2` |
| 固定安装身份 `AppId` | `{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}`（当前用户最低权限安装至 `%LOCALAPPDATA%\Programs\Contain`，支持从 `v0.5.0-beta.3` 原位升级） |
| 随包工作流 CLI 版本 | `contain-workflow 0.5.0`（静态 CRT，已在无开发工具接收机实测） |
| 随包采集核心版本 | `contain 0.3.2-beta.10`（本次源码 release 构建，按仓库约定与桌面 UI 分列版本） |
| 阶段 A 干净源码机构建工具链 | Rust `1.100.0`（`x86_64-pc-windows-msvc`，含 `rustfmt`/`clippy`/`rust-docs`）、`.NET SDK 10.0.403`（自包含运行时 `10.0.14`）、Python `3.12.10`、Git `2.47.1.windows.2`、MSVC `14.44.35207`、Windows SDK `10.0.26100.0` |
| 安装器编译器与签名状态 | Inno Setup `6.7.3`，`Authenticode = NotSigned`（未签名正式包，首次运行可能触发 Windows SmartScreen 提示） |

## 交付清单与校验值

以下产物均由干净源码机 A 对 `ddca87a1926d128821618fc9799aef09d834a6cd` 的实测构建直接产出，并经官方 `scripts/assemble-release-candidate.py` 原样组装与交叉核验。独立传递至接收机 B 的 `SHA256SUMS.txt` 自身 SHA-256 为 `92220a0c4a5b81aa76f781e71a87eb0729fcc75fccc78da16a6ddd6537309152`。

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| [`Contain-Setup-x64.exe`](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/Contain-Setup-x64.exe) | 51,346,821 | `8d7df61f0e5ce42a9e513088feb259394a11de105863669659790922c577a8e0` |
| [`contain-0.5.0-windows-x64.zip`](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/contain-0.5.0-windows-x64.zip) | 71,212,024 | `725c97e11a90f47c575e35186406078df72edbc179baf58856e00ea1e2ac3d51` |
| [`contain-workflow-0.5.0-windows-x64.zip`](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/contain-workflow-0.5.0-windows-x64.zip) | 1,734,590 | `d9c939cd61bd05b236b4c0a8f447f0ff6df9324223c2906a6d398d51800c94cc` |
| [`desktop-package-spec.json`](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/desktop-package-spec.json) | 34,406 | `7fb1b833143fc57a1050afd710aaa096520add6e6bb37b78e73dc163ac186c97` |
| [`cli-package-spec.json`](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/cli-package-spec.json) | 1,600 | `8a48240d508673db79722bb00d8c72bc04a506aa549bb86d6a0c3d670624a951` |
| [`handoff.json`](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/handoff.json) | 1,960 | `a1a40c777f9646d5f3a668748ac62d2a173e1849a4471089116cef787e0506ff` |
| [`SHA256SUMS.txt`](https://github.com/LuckyLivio/Contain/releases/download/v0.5.0/SHA256SUMS.txt) | 1,289 | `92220a0c4a5b81aa76f781e71a87eb0729fcc75fccc78da16a6ddd6537309152` |

桌面包内核心与入口二进制校验值（[a-handoff.json](v0.5.0/a-handoff.json)）：
- `Contain.Viewer.exe`：`d85db785a540efef1a10550919211924fc36c410b486c26ec8f75340a5b29020`
- `contain.exe`：`b5a6efb07732167b0385bb30d93a1858b3d9ab67c77a694194a880707d4e4649`
- `contain-workflow.exe`（桌面包随附）：`702c96b4d96d83e0700daacbf98d0a8809070e803cad23cee315d4a462f4f7df`
- `contain-workflow.exe`（独立 CLI 包）：`5bcc1470d815bc1b089912978dba1ef4787d00f616dbd7ed1692d129c19d7498`

---

## 全部验收项与公开收据

原始本机运行日志保留在本地，以下为提交入库的可公开复核脱敏收据：

| 验收链路 | 环境 | 实际验证内容 | 结果与收据 |
| --- | --- | --- | --- |
| **1. GitHub Actions CI** | `windows-latest`（push + PR） | `CI / rust`（`Check` / `Coverage` / `CoverageOriginal` / `Dependencies` / `Companion` / 33 份只读 CLI JSON schema 校验）与 `Modern desktop`（桌面模型回归、导航控件外部 UIA 回归、现代桌面打包契约、NuGet 漏洞扫描） | **4/4 通过**（[PR #30](https://github.com/LuckyLivio/Contain/pull/30)） |
| **2. 干净源码机 A（`-Stage SourceMissing` → 补齐工具 → `-Stage Source`）** | 原生 Windows 11 企业版 x64 沙箱（`10.0.26100.9974`，`is_wow64_process2` 原生 `x86_64`、`emulated = false`） | 安装工具前运行 `-Stage SourceMissing` 取得真实缺项报告（`rustc`/`rustup`/`dotnet`/`python`/`vswhere_missing`）；在同一实例补齐全部官方工具（`vs_exit_code = 0`，无需重启，选中 MSVC `14.44.35207` + SDK `10.0.26100.0`）；复核 `HEAD == ddca87a1` 后执行 `-Stage Source`（`native-platform`、`restored-source`、`active-msvc-conflict`、`active-sdk-conflict`、`build-and-freeze` 全部通过，耗时 1,072.3 秒，来宾 `C:` 余量 `64.11 GiB`） | **通过** · [A-before 收据](v0.5.0/a-before-acceptance.json) · [缺项明细](v0.5.0/a-before-report.json) · [A-restored 收据](v0.5.0/a-restored-acceptance.json) · [A 交接清单](v0.5.0/a-handoff.json) |
| **3. 独立断网接收机 B（`receive-release-candidate.ps1` / `-Stage Package`）** | 第二台全新、完全断网（`<Networking>Disable</Networking>`，`network_before/after = offline`）、无开发工具的 Windows 11 企业版 x64 沙箱（`10.0.26100.9974`） | 按独立传递的 `SHA256SUMS.txt` 摘要校验全套 14 份候选文件与身份一致性；解出独立 `contain-workflow.exe` 执行 `-Stage Package`：`native-platform`、`development-tools-absent`（`dotnet_sdk`/`cargo`/`msbuild`/`python`/`cl`/`rustc`/`git` 全为 `false`）、`A-B-same-artifacts`、`independent-cli`、`desktop-launch-and-tests`（窗口 `Contain · 0.5.0`、核心版本、随包 CLI 版本、合成库只读列表）以及 `missing-coreclr-rejected`（移除 `coreclr.dll` 后在静态阶段拒绝、`launch = not_run`、未创建运行目录）**6/6 全部通过** | **通过** · [B-receipt.json](v0.5.0/B-receipt.json) · [Package 总收据](v0.5.0/b-package-acceptance.json) · [无开发工具探测](v0.5.0/b-development-tools.json) · [平台探测](v0.5.0/b-platform-report.json) |
| **4. 离线干净机安装 `v0.5.0` Setup** | 第三台全新、完全断网、无开发工具的 Windows 11 企业版 x64 沙箱（`10.0.26100`） | 仅拷入 `Contain-Setup-x64.exe`（`8d7df61f...`）安装（退出码 `0`），核实唯一 HKCU 卸载条目（`0.5.0`）；从开始菜单 `Contain.lnk` 启动主窗口 `Contain · 0.5.0`，默认进入「打包项目」页且「选择项目文件夹」可用；打开随包 `samples\viewer-demo.db` 读取 `synthetic-finished` 下事件 `event:synthetic-finished-event-0` 完整依据（精确 FILETIME `134350000000000001`）；核验随包 `contain-workflow 0.5.0` 与 `contain 0.3.2-beta.10`；正常关闭（退出码 `0`）并标准卸载（退出码 `0`，卸载条目归零、快捷方式与程序文件均已清除） | **通过** · [离线试装收据](v0.5.0/offline-contain-setup-receiver.json) |
| **5. 公开 `v0.5.0-beta.3 → v0.5.0` 原位升级** | 第四台全新、完全断网、无开发工具的 Windows 11 企业版 x64 沙箱（`10.0.26100`） | 先安装公开 `v0.5.0-beta.3`（`5f78f3ae3fcb20861b3e062fec997a332ad95fa1ef99f474b8f750bd9ba4cfce`）并建立个人记录库 `%LOCALAPPDATA%\Contain\Records\contain.db`（SHA-256 `b2af4630d4c4ee395d3d7f2af3db1dc01c599bcc64200ba96643925f63eb6e2d`），在 Beta.3 界面核验可读；随后原位运行 `v0.5.0` `Contain-Setup-x64.exe`（`8d7df61f...`，退出码 `0`）：核实系统卸载列表仍**仅 1 条** `Contain`（`0.5.0`）、`Contain.Viewer.exe`/`.dll` 与 `Contain.Desktop.Model.dll` 文件与程序集版本均为 `0.5.0.0`（`0.5.0+ddca87a1...`）、升级后从开始菜单启动 `Contain · 0.5.0` 完整读取既有 3 条记录与事件 `event:synthetic-finished-event-0` 依据且数据库字节未变；最后执行标准卸载，程序目录、快捷方式与卸载登记全部清除（`install_dir_removed = true`），同时 `%LOCALAPPDATA%\Contain\Records\contain.db` 按预期完整保留且 SHA-256 保持一致 | **通过** · [原位升级收据](v0.5.0/offline-upgrade-from-beta3-receiver.json) |

### 阶段 B 首轮发现的问题与重新冻结记录

在上一候选提交 `76ee4b133d35823f589221ec2c0ba3e939cf9fe8` 上，阶段 A 构建成功，但在干净 Windows 11 PowerShell 5.1 接收机 B 执行 `missing-coreclr-rejected` 坏包构造时触发真实兼容缺陷：`scripts/workflow-windows-acceptance.ps1` 第 62 行仅加载了 `System.IO.Compression.FileSystem` 而未加载 `System.IO.Compression`，导致 Windows PowerShell 5.1 下解析 `[IO.Compression.ZipArchiveMode]::Update` 报 `Unable to find type [IO.Compression.ZipArchiveMode]`（前 5 项检查均已通过）。该首轮失败日志保留在本地；修复已提交至 [`ddca87a1926d128821618fc9799aef09d834a6cd`](https://github.com/LuckyLivio/Contain/commit/ddca87a1926d128821618fc9799aef09d834a6cd)（同时清除只读副本属性并显式使用 UTF-8 读取包规格、在 `scripts/test-workflow-acceptance.ps1` 增补回归检查），并按仓库约定重新冻结源码、在全新沙箱 A→B→Setup→升级全链路完整复测通过。

---

## 已知小问题与说明

1. **未签名安装器提示**：`Contain-Setup-x64.exe` 当前 Authenticode 状态为 `NotSigned`，首次运行可能触发 Windows SmartScreen 提示；请先对照 `SHA256SUMS.txt` 核验文件摘要。
2. **随包示例库 SQLite WAL 侧车文件**：在安装目录 `{app}\samples\` 内直接通过「打开外部记录库」打开随包 `samples\viewer-demo.db` 时，SQLite 运行期会在 `samples\` 下生成 `viewer-demo.db-shm` 与 `viewer-demo.db-wal` 两个侧车文件；Inno Setup 卸载器按不误删安装后新建文件的安全策略会保留这两个侧车文件（程序目录内其余已安装文件、开始菜单快捷方式与卸载登记均完全移除）。若仅使用 `%LOCALAPPDATA%\Contain\Records\contain.db` 个人记录库，卸载后会完整删除安装目录并原样保留个人记录库。
3. **本版保留的既有实测覆盖**：普通单项目 C# 控制台与带自定义 `AssemblyName` 的 WPF 项目首次打包、缺 Inno Setup 恢复与生成 WPF 安装包单文件断网接收验收已于前置版本完成并在本次源码回归中保持，详见 [v0.5.0-beta.3 首次打包实测与断网接收收据](beta05-beta3-release.md)。
