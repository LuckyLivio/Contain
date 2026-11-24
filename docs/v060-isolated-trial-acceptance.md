# Contain v0.6.0 正式版候选产物与「隔离试用」验收记录

> 状态：**Windows x64 `v0.6.0` 本地待发布全套产物已验收就绪（Draft PR #31，保持草稿、未合并、未打 Tag、未发布 GitHub Release、未切换 README 下载链接）**。公开稳定版在正式发布前继续为 `v0.5.0`。

## 1. 正式版冻结构建身份与产物清单

- **版本号统一**：桌面端（`Contain.Viewer.exe` / `Contain.Viewer.dll` / `Contain.Desktop.Model.dll`）、工作流工具（`contain-workflow.exe`）、安装包（`Contain-Setup-x64.exe`）与包元数据统一冻结为 **`0.6.0`**（PE/程序集版本 `0.6.0.0`，`ProductVersion = 0.6.0`）；底层采集核心保持约定版本 **`contain 0.3.2-beta.10`**。
- **正式版源码冻结提交 SHA（所有二进制均由此干净提交构建）**：`66bdef4ecc9e0cc7857962da1ea13a93c00b20eb`（后续提交仅更新本验收文档，未改动任何代码或构建输入）。
- **工具链版本**：MSVC `14.44.35207`、Windows 11 SDK `10.0.26100.0`、.NET SDK `10.0.403`、Rust `1.100.0`、Python `3.12.10`、Inno Setup `6.7.3`。
- **签名状态**：`NotSigned`（未做代码签名）。

| 产物文件 | 大小（字节） | SHA-256 | 说明 |
| --- | ---: | --- | --- |
| `Contain-Setup-x64.exe` | 51,484,736 | `ea991d395b87385412bc75982f7fb7287d4a4f52d9d745984e6cd262293927a3` | 当前用户最低权限安装器（`AppId={8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}`，`FileVersion=0.6.0.0`，723 个载荷文件） |
| `contain-0.6.0-windows-x64.zip` | 71,395,542 | `d00b5f3474e689f0525f584a9c3d1c7feb2e00950cfdce0620630aa6508c4606` | 便携解压即用桌面包（`Contain.Viewer.exe` + `contain.exe` + `contain-workflow.exe` + 样本库，723 个必选文件） |
| `contain-workflow-0.6.0-windows-x64.zip` | 1,734,741 | `b2b496d543603b385e3e116b3b74ffd222087e024cdf875f10d89fe155deb7f7` | 独立 CLI 发布包（静态 CRT `contain-workflow.exe` + 工具链选择脚本） |
| `SHA256SUMS.txt` | 1,289 | `e25008ec19f8ca7be88ebd932f62ade75e7e1a98b3c46a241ffe87a4beac34e0` | 独立接收机移交总校验清单（14 个文件） |
| `handoff.json` | 1,960 | `483853e74ef0ab203ade1f7022a4452aa0df6ae0c33b9f5b1c89f92143441c79` | Machine A 源码构建移交清单 |
| `installer-receipt.json` | 975 | `4950242998b33db949c4600d7968a4059f91958b82fc299f916fce85dfcd2d40` | 安装器装配回执 |
| `candidate-identity.json` | 505 | `cfee21db9096c7d45c45b781002ebc206a092e179a34ea16d760b7b5b19c3d7b` | 装配身份与封装 CLI 探针回执（`desktop`/`cli` 均 `default=passed`, `safe_probes=passed`） |

## 2. 沿用 v0.5.0 风格的 A → B 干净发布链实测

1. **干净源码机 A（独立 Windows 11 沙箱 `10.0.26100`）**：
   - **Step 1（`SourceMissing`）**：`A-before/acceptance.json = passed`（`native-platform = 0`、`actual-source-before-install = 1`）。
   - **Step 2（安装工具链）**：全新安装 MinGit `2.47.1.2`、Python `3.12.10`、.NET SDK `10.0.403`、Rust `1.100.0`、VS 2022 Build Tools（MSVC `14.44.35207`）与 Windows 11 SDK `10.0.26100.0`（73 个 MSI 全部 `0x0`）。
   - **Step 3（`-Stage Source`）**：在干净提交 `66bdef4ecc9e0cc7857962da1ea13a93c00b20eb` 上完成构建、模型/Rust 回归与双包冻结，`A-restored/acceptance.json = passed`（`native-platform = 0`、`restored-source = 0`、`active-msvc-conflict = 1`、`active-sdk-conflict = 1`、`build-and-freeze = 0`，耗时 182.5 s）。
2. **正式包装配（`scripts/assemble-release-candidate.py`）**：
   - 从 Machine A 交付物与干净提交 `66bdef4ecc9e0cc7857962da1ea13a93c00b20eb` 装配桌面 ZIP、独立 CLI ZIP、Inno Setup 安装器及 `SHA256SUMS.txt`；封装 CLI 探针（`desktop` / `cli` 的 `default` 与 `safe_probes`）全部 `passed`。
3. **独立离线无开发工具接收机 B（`Networking=Disable`，无 `.NET` / `Rust` / `VS` / `Inno Setup`）**：
   - 核验 `SHA256SUMS.txt`（`e25008ec…`）后执行 `receive-release-candidate.ps1` → `workflow-windows-acceptance.ps1 -Stage Package`。
   - `B-receipt.json = passed`，`network_before = offline`、`network_after = offline`，六项门禁全部通过：`native-platform`（`0`）、`development-tools-absent`、`A-B-same-artifacts`、`independent-cli`（`0`）、`desktop-launch-and-tests`（`0`）、`missing-coreclr-rejected`（`1`，启动前拒绝缺失 `coreclr.dll`）。

## 3. 最终版 v0.6.0 二进制三条真实用户路径实测

| 路径 | 运行环境与被测产物 | 实测结果 |
| --- | --- | --- |
| **路径 A：干净离线无沙箱安装与卸载** | 离线无工具 Windows 11 沙箱（`10.0.26100`，`Networking=Disable`，未启用沙箱）+ 最终版 `Contain-Setup-x64.exe`（`ea991d39…`） | **全部通过**（`exit 0`）：静默安装 `exit 0`；卸载表 1 条 `0.6.0`；`Contain.Viewer.exe` `FileVersion=0.6.0.0`、`contain-workflow 0.6.0`、`contain 0.3.2-beta.10`；通过开始菜单 `Contain.lnk` 启动，窗口标题 `Contain · 0.6.0`；进入「隔离试用」显示「当前系统支持 Windows 沙盒，但功能尚未启用」并禁用开始按钮，选定本地 `contain.exe` 可只读查看大小/PE 架构并算出准确 SHA-256（`8b59bcec…`）；切换「检查交付包」「我的记录」「打包项目」正常；正常退出 `exit 0` 后运行 `unins000.exe`（`exit 0`），开始菜单快捷方式、安装目录与卸载项全部清零 |
| **路径 B：公开 `v0.5.0` → 最终版 `v0.6.0` 原位升级与卸载** | 离线 Windows 11 沙箱（`Networking=Disable`）：公开 `v0.5.0` Setup（`8d7df61f…`）→ 写入个人 `contain.db` → 原位安装最终版 `Contain-Setup-x64.exe`（`ea991d39…`）→ 卸载 | **全部通过**（`exit 0`）：升级后卸载表严格仅保留 **1 条** `Contain`（`DisplayVersion = 0.6.0`，无重复 `0.5.0` 项），窗口标题 `Contain · 0.6.0`；个人 `contain.db` 在 UI（3 条样本记录）与 CLI 只读模式均可正常读取，无沙箱进入「隔离试用」优雅降级；个人 `contain.db` SHA-256 在**升级前、升级后、打开界面后、卸载后**四个阶段完全逐字节一致（`140382deb77dd1f19dc8e607b055c337618ceafb2fe71b66e18ab1bf124292a7`）；卸载后应用目录与卸载项清零且个人数据完好保留 |
| **路径 C：启用沙箱宿主机解压最终版 ZIP 便携文件夹与变更重放** | 宿主机 Windows 11 + Windows Sandbox，独立 `CONTAIN_VIEWER_TEST_ROOT`，解压最终版 `contain-0.6.0-windows-x64.zip`（`d00b5f34…`） | **全部通过**：窗口标题 `Contain · 0.6.0`；① 便携文件夹基线试用（会话 `9bfed2483cca`、沙箱 `cd1dfb5c-d768-4031-87f6-39b459ab3b58`）在来宾实时 `ContainPortableDemoWindow` 核对显示 `CONTAIN-BETA4-RESOURCE-OK`（`exit 121`），结束后「试用已结束」「本次临时文件已清理」；② 等长修改 `assets\banner.txt`（文件数、总字节数与入口 EXE 哈希均不变），从「我的记录」点击「再次试用」进入确认页（不自动启动沙箱），清晰显示「与上次相比，1 个文件内容变化」并展开精确相对路径 `assets\banner.txt`（`896c0172a0… → 56eb0837d2…`）；③ 确认启动后（会话 `74a5d932986f`、沙箱 `905d2e08-8a3f-45e1-b5ed-80a1c35e8443`）在来宾实时窗口核对显示 **`CONTAIN-V060-FINAL-BANNER`**（`exit 122`）；④ 结束后保留 2 条独立历史（基线 JSON 逐字节未变，默认导出报告不含目录清单隐私泄露），最终 `0` 运行中沙箱、`0` 暂存残留 |

## 4. 前台键盘导航验证（`keyboard_navigation_invokes_once`）

在不改动任何既有断言、不用 UIA `Invoke` 代替真实按键的前提下，运行 `Contain.Desktop.Controls.Tests`（`ActionNavigationItem`）：

- **已登录解锁交互桌面（Windows 11 沙箱交互会话 `ExistingLogin`）**：**12/12 全部通过**（`accepted = true`，`normal_exit = true`，`forced_exit = false`）：
  - 目标窗口 PID：`8452`，实际前台 PID：`8452`（`foreground_is_owned_main = true`）；
  - 控件焦点：`focused_fixed_id = "NewInstall"`，`target_has_keyboard_focus = true`；
  - 真实 `Space` 按键结果：`space_dispatch_attempted = true`，`space_down = 1`，`space_up = 1`，`space_down_on_action = 1`，`space_up_on_action = 1`，`action_clicks: 2 → 3`（单次真实按键严格只触发 1 次），`control_clicks = 2`。确认产品代码无漏触发或重复触发缺陷。
- **自动化宿主机后台会话（待前台复核）**：
  - 宿主机运行录得 `expected_host_process_id = 45076`，`foreground_process_id = 0`（`foreground_request_returned = false`），`space_dispatch_attempted = false`，`space_down = 0`，因自动化宿主会话未持有系统前台窗口而无法下发真实键盘按键；已标记为**宿主机待前台复核**，可在解锁交互桌面直接执行下述命令复核：
  ```powershell
  dotnet run --project desktop/Contain.Desktop.Controls.Tests/Contain.Desktop.Controls.Tests.csproj -c Release -- --output <out-dir>
  ```

## 5. 确定性模型与自动化回归（冻结提交 `66bdef4ecc9e0cc7857962da1ea13a93c00b20eb`）

- `Contain.Desktop.Model.Tests`：**99/99 通过**
- `cargo test -p contain-workflow --locked`：**55/55 通过**
- `scripts/test-package-source-desktop.py`：**10/10 通过**
- `scripts/check-fuzz-lock.py`、`cargo deny --locked check` 与全库卫生扫描：**全部通过**

## 6. 延续的 v0.6.0-rc.1 与 Beta.2–Beta.6 专项验收基础

`v0.6.0` 正式版代码与已验收的 `v0.6.0-rc.1`（构建提交 `6c00d03ad425b96340700bfbc1f9715577be1543`，本地对照包保留未删除）之间仅包含版本号元数据统一（`0.6.0-rc.1` → `0.6.0`），以下 RC.1 及 Beta.2–Beta.6 实测专项结论完整延续：

- **RC.1 预准备中断回收**：160 MiB 文件夹复制中途强杀与确认页就绪强杀后重开均自动回收（`0` 暂存、`0` 归属、`0` 沙箱、`0` 新历史）；双窗口并行保护与暂存根下归属未明目录隔离完好；真实冻结 Beta.6 历史逐字节兼容。
- **RC.1 大于 64 MiB 目录变动复核**：73.7 MiB 目录未变零重复复制复用快照；确认页就绪后等长修改 `assets\banner.txt` 并恢复 10 天前 `LastWriteTimeUtc` 时，首次点击阻止启动并刷新差异，二次确认后在来宾窗口核对显示新文案。
- **Beta.2–Beta.6 专项实测**：沙箱能力分诊、单文件离线安装器（7-Zip）完整试用与干净来宾对照、运行中沙箱前端强杀恢复、便携文件夹多入口与重解析点拦截、试用历史搜索与隐私保护 JSON 报告、Beta.5 旧记录兼容（历史截图与示例报告留存于 `docs/v0.6.0-beta.2/` 至 `docs/v0.6.0-beta.6/`）。
