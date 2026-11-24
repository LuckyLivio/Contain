# Contain v0.7.0「配置还原点」Windows x64 正式发布包验收记录

> 状态：**Windows x64 `v0.7.0` 本地待发布包已完成冻结、A → B 干净发布链验证与两条最终升级实测**。分支 `feat/config-restore-points` 与 **Draft PR #32** 保持 Draft（未合并、未打 Tag、未公开发布、未切换 README 下载入口；当前公开稳定版仍为 `v0.6.0`）。

## 1. 冻结版本与发布产物身份

- **正式版本**：`0.7.0`（桌面端、`contain-workflow`、打包脚本与 Setup 统一为 `0.7.0`；PE / 程序集版本统一为稳定版 `0.7.0.0`；采集核心沿用 `contain 0.3.2-beta.10`）
- **冻结构建提交 SHA**：`aeacc2160b955965c8ea862d275ccf887092645a`（相对已验收的 RC.1 代码提交 `5efac4f` 仅包含 15 个版本元数据冻结修改，零产品逻辑变更；后续仅追加本验收文档提交）
- **代码签名状态**：`NotSigned`（未做代码签名）
- **独立校验清单自身摘要（`SHA256SUMS.txt`，覆盖全部 14 个公开交付/校验文件）**：`5289a70ae4aa4a2f365663b066eea81dc37711c0e53fdf3fd0a8481fdac42f2d`（`1,289` B）

| 公开核心产物 | 大小（字节） | SHA-256 | 说明 |
| --- | ---: | --- | --- |
| `Contain-Setup-x64.exe` | 51,547,764 | `6257a3a6ca65b4f16902bca5417bec1526fc6e10f92675e6d70325e0dd55f6fe` | 当前用户最低权限安装器（Inno Setup `6.7.3`，`AppVersion=0.7.0`，`FileVersion=0.7.0.0`，723 个载荷文件） |
| `contain-0.7.0-windows-x64.zip` | 71,476,132 | `386392ae0f37d563cf218b27909f79df15cdd477076baacbf4debd0549fa175b` | 便携免安装桌面版（`Contain.Viewer.exe` `FileVersion=0.7.0.0`，随包 `contain.exe` + `contain-workflow.exe`） |
| `contain-workflow-0.7.0-windows-x64.zip` | 1,734,776 | `d135726b58a1abe1b64a9a600075f1cca02d0e5a750e3cc0ebc520b16b6e2691` | 独立静态链接 `contain-workflow` CLI 工具（`0.7.0`） |
| `handoff.json` | 1,960 | `fe1df8aabeece542c6db8132fb8234df055bc9c1d118b712f04eb9038e20a672` | 源码机 A 冻结移交身份清单 |
| `SHA256SUMS.txt` | 1,289 | `5289a70ae4aa4a2f365663b066eea81dc37711c0e53fdf3fd0a8481fdac42f2d` | 完整发布清单（覆盖 14 个公开交付文件） |

## 2. 干净发布链（源码机 A → 组装 → 断网接收机 B）

沿用 `v0.6.0` 发布链，基于冻结提交 `aeacc2160b955965c8ea862d275ccf887092645a` 从零构建一次并完成独立接收机验收：

### 2.1 干净源码机 A（Windows 11 Build `26100`，12 GiB RAM）

| 阶段 | 命令与核对要点 | 结果 |
| --- | --- | --- |
| Step 1：`SourceMissing`（`A-before`） | 安装任何开发工具前，以静态引导 CLI `contain-workflow 0.7.0`（`49c88364…`）检查冻结源码 | **通过**（`native-platform` exit `0`，`actual-source-before-install` 按预期返回 exit `1`） |
| Step 2：安装工具链 | 复用已核验离线缓存安装 MinGit `2.47.1.2`（干净检出 `aeacc216`）、Python `3.12.10`、.NET SDK `10.0.403`（含 NuGet 缓存）、Rust `1.100.0`（含 `rust-docs` 与 Cargo 缓存）、VS 2022 Build Tools（MSVC `14.44.35207`，exit `0`）与 Windows 11 SDK `10.0.26100.0`（`OptionId.DesktopCPPx64`，48/48 子包均 `0x0`） | **通过**（`select-workflow-toolchain.ps1` 核验通过，`msvc=14.44.35207`、`sdk=10.0.26100.0`） |
| Step 3：`Source`（`A-restored`） | `restored-source`、`active-msvc-conflict`、`active-sdk-conflict`、`build-and-freeze`（含桌面 126 项模型检查、workflow 55 项测试与冻结双 ZIP + `handoff.json`，耗时 `192 s`） | **通过**（全部 5/5 检查 `passed`，退出码 `0`） |

### 2.2 本地组装与断网无开发工具接收机 B（`Networking=Disable`，6 GiB RAM）

- 以 `scripts/assemble-release-candidate.py` 校验 `handoff.json` 与双 ZIP/Spec 哈希、执行 4 项打包 CLI 探针、调用 `build-windows-installer.ps1` 构建 `Contain-Setup-x64.exe`（`FileVersion=0.7.0.0`，`NotSigned`）并生成 `SHA256SUMS.txt`（自摘要 `5289a70a…`）。
- 将 `5289a70ae4aa4a2f365663b066eea81dc37711c0e53fdf3fd0a8481fdac42f2d` 独立传入断网接收机 B 执行 `receive-release-candidate.ps1`（`B-clean`）：
  - `network_before=offline`、`network_after=offline`；
  - `development-tools-absent: passed`（`dotnet_sdk / cargo / msbuild / python / cl / rustc / git` 全部为 `false`）；
  - `native-platform: passed`、`A-B-same-artifacts: passed`、`independent-cli: passed`、`desktop-launch-and-tests: passed`；
  - `missing-coreclr-rejected: passed`（移除 `coreclr.dll` 后在启动前即返回 exit `1` 拒绝执行，未创建工作目录）。

## 3. 两条独立升级路径实测（使用同一份最终 `Contain-Setup-x64.exe` `6257a3a6…`）

两条路径均在全新、断网、未启用 Windows Sandbox 的 Windows 11 来宾中运行，全部使用合成用户数据。

### 3.1 路径 1：公开 `v0.6.0` → 最终 `v0.7.0`（含卸载后保留用户数据验证）

- **升级前基线**：安装公开 `v0.6.0` `Contain-Setup-x64.exe`（`ea991d39…`），预置合成个人安装记录库 `contain.db`（SHA-256 `33485c226626b85c9ec2664d6f678a2056d7fd1764a046a5a9a121833ea2cb0e`）与 1 条由公开 `v0.6.0` 真实生成的隔离试用历史 `d4d8693543ff.json`（SHA-256 `41d0e4aab671962f48e2aeca86cfb896babc0a486aeab38d24a0f6aeebe217a1`，`便携 演示 & App.exe`）。
- **原位升级结果**：
  - 卸载项严格仅 **1** 条（`DisplayName=Contain`，`DisplayVersion=0.7.0`），`Contain.Viewer.exe` `FileVersion=0.7.0.0`。
  - 升级后 `contain.db` 与 `d4d8693543ff.json` 的 SHA-256 与升级前**逐字节一致**；在无 Windows Sandbox 环境中启动 UI，两类旧记录全部可读（3 条个人安装记录 + 1 条 `v0.6.0` 隔离试用历史及详情页 `便携 演示 & App.exe`）。
  - 在无 Windows Sandbox 环境中完整执行 **「建立还原点 → 修改 → 恢复 → 撤销」**（A `29fb022c…` / B `29f09b7f…`，逐字节还原）。
  - 执行标准静默卸载（`unins000.exe`，exit `0`）：系统卸载项归零（`0`），同时实测确认 `%LOCALAPPDATA%\Contain\Records\contain.db`、`IsolatedTrial\History\d4d8693543ff.json` 与 `ConfigRestorePoints` 还原点元数据及备份文件**全部原样保留且 SHA-256 未变**。

### 3.2 路径 2：冻结 `v0.7.0-rc.1（0.7.0.4）` → 最终 `v0.7.0（0.7.0.0）`（含逐文件覆盖与开始菜单启动撤销）

- **升级前基线**：安装冻结 RC.1 `Contain-Setup-0.7.0-rc.1-x64.exe`（`e3625040…`，`0.7.0.4`），建立合成 `settings.json`（A `c4c83119…`）还原点，修改为 B（`f4a533b4…`）并完成一次恢复回 A，留下未消耗的可撤销记录。
- **原位安装 `v0.7.0` 后逐文件覆盖核对（验证 `ignoreversion` 使 `0.7.0.4 → 0.7.0.0` 完全覆盖，无静默跳过）**：

| 安装目录文件 | 升级前（RC.1）版本与 SHA-256 | 升级后（`v0.7.0`）版本与 SHA-256 | 最终桌面 ZIP 对应 SHA-256 | 一致 |
| --- | --- | --- | --- | --- |
| `Contain.Viewer.exe` | `0.7.0.4` · `dc316f50…` | `0.7.0.0`（`0.7.0+aeacc216…`）· `201166eb149cb08e819325aba380f2d2d1d5c2139e7a3becc11d894f3ce3c566` | `201166eb149cb08e819325aba380f2d2d1d5c2139e7a3becc11d894f3ce3c566` | **完全一致** |
| `Contain.Viewer.dll` | `0.7.0.4` · `7e9db228…` | `0.7.0.0`（`0.7.0+aeacc216…`）· `38ffdd905d84a190e4959be6d87e1d3ae46dafdbd4ca11eebdfe5943a6d6e716` | `38ffdd905d84a190e4959be6d87e1d3ae46dafdbd4ca11eebdfe5943a6d6e716` | **完全一致** |
| `Contain.Desktop.Model.dll` | `0.7.0.4` · `53aab6f9…` | `0.7.0.0`（`0.7.0+aeacc216…`）· `ae38e68a52328f0c3a2a74dab0746e60a1aa9038ff0d6e7cdbbf4d6fad01d98e` | `ae38e68a52328f0c3a2a74dab0746e60a1aa9038ff0d6e7cdbbf4d6fad01d98e` | **完全一致** |
| `contain.exe` | `40c4f60f…` | `252aff17d176787c3e01ba0a6bf56f1698d155b378a912a3c6a0062adb8d9f80` | `252aff17d176787c3e01ba0a6bf56f1698d155b378a912a3c6a0062adb8d9f80` | **完全一致** |
| `contain-workflow.exe` | `08b7e899…` | `036bcf1d6eb45c6077129a2ee02f93daa75963dc0c5c3d1f6f10183906a698bc` | `036bcf1d6eb45c6077129a2ee02f93daa75963dc0c5c3d1f6f10183906a698bc` | **完全一致** |

- **开始菜单启动与旧撤销执行**：卸载项严格仅 `1` 条（`0.7.0`）；通过开始菜单快捷方式（`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Contain.lnk`）启动 `Contain · 0.7.0`，旧 RC.1 还原点可读（`与备份一致`），点击撤销进入「当前文件 → 撤销后」预览并确认，目标文件从 A（`c4c83119…`）**逐字节还原**为 B（`f4a533b44f1c6bedc1adfbc16aaf0a35fb0dbed440dda73648d74f2da25035cb`），最后标准卸载通过（exit `0`）。

## 4. 最终便携 ZIP 双窗口短回归与 RC.1 精确强杀收据引用

- **最终便携 ZIP（`contain-0.7.0-windows-x64.zip`，`386392ae…`，`FileVersion=0.7.0.0`）双窗口实测**：
  - 启动两个独立 `Contain.Viewer.exe` 进程（PID `97684` 与 `86300`）共享测试目录与同一 `settings.json`（A `4c14fcac…` / B `a12cba6c…`）。
  - 两窗口同时停在恢复确认页，窗口一确认恢复 A 后，窗口二点击旧预览「确认恢复」先重读磁盘并提示「目标文件已与所选备份一致（可能已由其他窗口完成恢复）」，目标 `LastWriteTimeUtc` 与哈希零改动、仅保留 **1** 条撤销记录。
  - 两窗口随后同时停在撤销预览页，窗口一确认撤销回 B 后，窗口二点击旧预览「确认撤销」提示「操作记录已更新（可能已由其他窗口撤销或完成）」，目标零重复写盘、旧撤销条自动清除、总撤销记录仍为 **1** 条。
- **精确强杀与跨进程锁受控验证引用**：冻结提交 `aeacc216` 相对已验收的 RC.1 提交 `5efac4f` **未修改任何产品代码**（仅更新 15 个版本字符串），因此 Beta.3 双进程复核基线与 RC.1 受控双独立进程验证（`restore_prepared` / `undo_prepared` 存活不抢占、真实 OS `Process.Kill()` 后接管 Beta.3 只读核对与连续 2 次重开零写盘）以及 Beta.3 中断时刻 1/2/3、第三版 C 与「待核对」全部受控与实机证据均完整适用（`Contain.Desktop.Model.Tests` **126 / 126** 已随 `aeacc216` 在源码机 A 再次全量跑绿）。

## 5. 全量自动化与质量门禁（冻结提交 `aeacc216`）

- `Contain.Desktop` Release 构建：**0 warnings，0 errors**（`FileVersion=0.7.0.0`）。
- `Contain.Desktop.Model.Tests`：**126 / 126 通过**。
- `cargo test -p contain-workflow --locked`：**55 / 55 通过**。
- `scripts/test-package-source-desktop.py`：**10 / 10 通过**。
- `scripts/check-fuzz-lock.py`、`cargo deny --locked check` 与全库卫生扫描：**全部通过（0 命中）**。

## 6. 仍未验证的事项（如实说明）

1. **Authenticode 代码签名与 SmartScreen 声誉**：当前产物为 `NotSigned`（未做代码签名），未验证带证书签名后的 SmartScreen 行为。
2. **非 Windows x64 架构**：本轮仅构建并验收原生 Windows 11 x64（Build `26100`）；未在 Windows 10 旧版本或 Windows ARM64（含 x64 仿真）实机验证。
3. **非虚拟化物理机与复杂权限环境**：A/B 干净机与两条升级路径均在干净 Windows 11 Sandbox 隔离环境中完成；未覆盖受限企业组策略、杀软实时拦截写盘或跨 SMB/网络映射盘配置文件场景。
4. **公开发布回下载核对**：因本轮保持 Draft、未公开 GitHub Release，尚未执行公开 Release 匿名回下载逐项校验与 README 首屏下载入口切换（待确认公开发布后执行）。

## 7. Beta.1 / Beta.2 / Beta.3 历史基线与实拍截图保留

- **Beta.1 基线**（`ab8a1dce76ae5c7d7442544dbc316dd239205f97`）：单文件原始字节备份、同名不同目录区分与基础恢复撤销（实拍：`docs/v0.7.0-beta.1/contain-v070b1-restore-confirm.png`）。
- **Beta.2 基线**（`c87fe677820f4f9faf18b6a93910fa125b360159`）：操作跟随差异方向、轻量撤销预览、取消不写盘不消耗与外部修改/删除冲突阻断（实拍：`docs/v0.7.0-beta.2/contain-v070b2-restore-preview.png`、`docs/v0.7.0-beta.2/contain-v070b2-undo-preview.png`）。
- **Beta.3 基线**（`7f3aab5536ca645ed8756caf8086dfff04704748`）：阶段化保存顺序、启动只读核对、第三版 C 保护、查看/另存原副本 B 与「待核对」（实拍：`docs/v0.7.0-beta.3/contain-v070b3-recovery-verified.png`、`docs/v0.7.0-beta.3/contain-v070b3-recovery-version-c.png`）。
- **RC.1 基线**（`5efac4fe358d6a06f6ca9356338bbf645f5d76e5`）：双独立进程内核独占写锁、过期预览确认前重读、强杀即时接管与 `v0.6.0` / Beta.3 兼容验收。
