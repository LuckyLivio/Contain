# Contain v0.6.0-rc.1 候选版「隔离试用」验收记录

> 状态：**Windows x64 发布候选版（Draft PR #31，未合并、未打 Tag、未发布 GitHub Release）**。公开稳定版继续为 `v0.5.0`。

## 1. v0.6.0-rc.1 收尾修复的两个真实边界

1. **「还没开始试用，Contain 就意外退出」后的预准备暂存回收**：
   - **冻结 Beta.6 基线复现结论**（独立测试目录、160 MiB 便携样例副本 `168,099,929` B；探测驱动本身以退出码 `0` 正常结束，产品异常退出为仅强杀该次 `Contain.Viewer.exe`）：
     - **时刻 ①（正在复制文件夹）**：强杀 `Contain.Viewer.exe` 后留下半成品暂存目录 `e495b8e8a6b1`（2 个文件、`8,702,464` B），沙箱数 `0`，历史数 `1`；重新打开冻结 Beta.6 后残留目录未被回收。
     - **时刻 ②（差异确认页已就绪、未点开始）**：强杀 `Contain.Viewer.exe` 后留下完整暂存目录 `c35a02020455`（5 个文件、`168,099,929` B），沙箱数 `0`，历史数 `1`；重新打开冻结 Beta.6 后残留目录仍未被回收。
   - **RC.1 归属与回收机制**：
     - 在创建暂存目录**之前**原子写入可跨重启核验的归属记录 `<appDataRoot>\IsolatedTrial\PendingPrepared\<sessionId>.json`（包含 `schema_version = 1`、`session_id`、`staging_root_path`、`owner_pid` 与 `owner_start_time_utc_ticks`）。
     - 启动时按归属记录精确回收已确认退出的 Contain 进程留下的完整或半成品预准备暂存目录：**绝不枚举或整目录清空 `%TEMP%\Contain-IsolatedTrial`**；严格跳过仍活跃的另一个 Contain 窗口、已移交沙箱（`pending-session.json`）的目录以及损坏/越界记录，且绝不触碰归属未明的目录。
     - 仅在真正确认删除成功（`Cleaned` / `NotCreated`）后才移除归属记录；若目录内文件被独占占用（`Remaining`），保留归属记录并在「隔离试用」与壳层提示条展示「重试清理」，删除成功前绝不声称已清理。
     - 仅打开对比页或回收中断准备留下的暂存目录，**绝不创建任何试用历史记录**。
2. **大于 64 MiB 目录在确认页就绪后的完整变动检测（含回写原修改时间）**：
   - 移除 `IsPreparedSnapshotFreshAsync` 中大于 `64 MiB` 时跳过哈希复核的上限，改为对目录内全部实时文件执行**只读流式 SHA-256 完整复核**（小文件优先校验以便秒级拦截文本/配置/DLL 改动，支持进度回报、界面保持可操作与随时取消）。
   - 当源目录未变时，继续直接复用已准备快照，**不重复复制**。
   - 当大于 `64 MiB` 的目录在确认页打开后发生等长修改且恢复原 `LastWriteTimeUtc` 时，首次点击「开始隔离试用」会拦截启动、清理过期快照并刷新差异精确指向被修改文件；二次确认后启动刷新快照，来宾实时窗口显示新内容。

## 2. 候选构建身份

- **候选版本**：`0.6.0-rc.1`（核心采集模块保持 `0.3.2-beta.10`）
- **候选包构建提交 SHA**：`6c00d03ad425b96340700bfbc1f9715577be1543`
- **便携解压即用 ZIP**：`contain-0.6.0-rc.1-windows-x64.zip`（71,396,130 字节）
  - SHA-256：`f25e3775df5d3c0875cd7920cb4ed7398ce784ad925a8c99e2d30eb31c2fcdb8`
- **桌面安装包 Setup**：`Contain-Setup-0.6.0-rc.1-x64.exe`（51,479,255 字节，Inno Setup 6.7.3，`AppId={8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}`，当前用户最低权限安装，未签名候选包）
  - SHA-256：`ddcc3cb1da32d91b2019fe40495a3f9e7a0f1b607fcfc91710f1f08a53dbe20b`

## 3. 真实 Windows 11 x64 验收（解压最终 RC.1 ZIP + 离线沙箱原位升级）

宿主机测试全程通过 `CONTAIN_VIEWER_TEST_ROOT` 隔离在独立目录，不触碰真实个人 `contain.db`；验收驱动正常退出（`exit 0`），产品异常退出均为显式仅强杀本次 `Contain.Viewer.exe`。

| 验收场景 | 实际结果 |
| --- | --- |
| 1. 时刻 ①：正在复制 160 MiB 文件夹时强杀 RC.1 `Contain.Viewer.exe` 后重开 | 通过：强杀后留下半成品目录 `4abfde0fe9c2`（2 文件、`4,114,944` B）及 1 条 `PendingPrepared` 归属记录，沙箱数 `0`、历史数 `1`；重开 RC.1 后自动回收，暂存目录 `0`、归属记录 `0`、沙箱数 `0`、历史数仍为 `1` |
| 2. 时刻 ②：差异确认页就绪、未点开始时强杀 RC.1 `Contain.Viewer.exe` 后重开 | 通过：强杀后留下完整快照 `4e521ad3e996`（5 文件、`168,099,929` B）及 1 条归属记录；重开 RC.1 后自动回收，暂存目录 `0`、归属记录 `0`、沙箱数 `0`、历史数仍为 `1` |
| 3. 双窗口保护 + 归属未明目录隔离 + 真实 Beta.6 历史向后兼容 | 通过：窗口 A 持有活跃预准备快照 `55baaee062d0` 且临时根下存在归属未明目录 `foreign999999`；同时打开共享数据根的窗口 B 后，窗口 A 活跃快照与 `foreign999999` 均**完好无损**；正常关闭窗口 A 仅释放 `55baaee062d0`，`foreign999999` 仍未被触碰；由冻结 Beta.6 生成的真实历史记录 `a09270d67c43` 全程**逐字节未变** |
| 4. 大于 64 MiB 目录（`73,728,089` B）未变复用 + 确认期间等长修改并回写原 `LastWriteTimeUtc` | 通过：① 未变重放直接复用已准备快照 `7045d3a534e5`（零重复复制，来宾窗口核对原文案 `CONTAIN-BETA4-RESOURCE-OK`，退出码 `122`）；② 确认页就绪后对 `assets\banner.txt`（`89 B`）做等长修改并恢复精确的 10 天前 `LastWriteTimeUtc`：首次点击「开始隔离试用」**阻止启动**、清理旧快照 `cd08f8fe0470` 并刷新差异精确指向 `assets\banner.txt`（`896c0172a0… → d1a9563bbf…`）；二次确认直接复用新快照 `3907dd5008f4` 启动沙箱 `680e6b90-5d8d-4f28-8361-96171b6203d7`，来宾实时 `ContainPortableDemoWindow` 控件直接核对显示 **`CONTAIN-RC1-LARGE64M-OK!!`**（退出码 `123`）；原基线历史文件与源目录非改动文件哈希完全一致 |
| 5. 运行中沙箱前端强杀恢复回归 | 通过：沙箱运行中仅强杀 `Contain.Viewer.exe`，重开 RC.1 后在「隔离试用」识别「上次试用仍在运行」并由「结束这次试用」停止沙箱 `89535988-f7c4-495a-aeff-6ecfc4102aa1` 与清理暂存；同一会话 `267e692d5ad1` 保持严格 1 条历史记录（`ConfirmedEnded` / `Cleaned`），最终沙箱数 `0`、暂存目录 `0`、归属记录 `0` |
| 6. 干净离线 Windows 沙箱原位升级（公开 `v0.5.0` → `v0.6.0-rc.1` Setup） | 通过（来宾退出码 `0`，断网 `Networking=Disable`）：离线静默安装公开 `v0.5.0` Setup（SHA-256 `8d7df61f0e5ce42a9e513088feb259394a11de105863669659790922c577a8e0`），写入样本个人 `contain.db` 后原位静默安装 `Contain-Setup-0.6.0-rc.1-x64.exe`；核对当前用户卸载表仅存在 **1 条** Contain 记录且 `DisplayVersion = 0.6.0-rc.1`，升级前后及打开界面后 `contain.db` **SHA-256 逐字节一致**且 UI 与 CLI 均可正常读取；窗口标题显示 `Contain · 0.6.0-rc.1`，在未启用沙箱的来宾内切入「隔离试用」清晰展示不可用提示并禁用启动按钮，正常关闭无崩溃 |

## 4. 确定性模型与自动化回归测试（合成 / 模拟）

- `Contain.Desktop.Model.Tests`：**99/99 通过**（在 Beta.6 的 94 项基础上新增 5 项 RC.1 专项回归）：
  1. 预准备快照创建前落盘归属记录，已确认死亡进程（含不存在 PID 与模拟 PID 复用旧启动时间）留下的半成品与完整预准备快照重启自动回收，且创建 0 条试用历史；
  2. 回收器严格保护活跃同行窗口、`pending-session.json` 已移交沙箱目录、越界损坏归属记录与暂存根下归属未明的外部目录；
  3. 预准备暂存内文件被独占锁定时，重启保留归属记录与待清理状态、展示壳层提示条与「重试清理」且绝不误报已清理，释放锁并点击「重试清理」后彻底删除；
  4. 大于 64 MiB（66 MiB）目录等长修改 `assets\banner.txt` 并恢复 10 天前 `LastWriteTimeUtc`，首次启动拦截并刷新差异精确指向 `assets\banner.txt`，二次确认零重复复制启动新快照；
  5. 大于 64 MiB 启动前复核流式上报进度并干净响应取消。
- `cargo test -p contain-workflow --locked`（55/55）、`scripts/test-package-source-desktop.py`（10/10）、`scripts/check-fuzz-lock.py`、`cargo deny --locked check` 与全库卫生扫描全部通过。
- 注：本地非 CI 交互式控件聚焦测试 `Contain.Desktop.Controls.Tests` 的 6 项本地检查与 4 项外部 UIA 检查通过，但 `keyboard_navigation_invokes_once` 因当前会话未获系统前台焦点返回 `foreground_process_id = 0` 未通过，已如实记录。

## 5. 延续的 Beta.2 至 Beta.6 验收基础

`v0.6.0-beta.2` 至 `v0.6.0-beta.6` 已验证的沙箱能力分诊、单文件离线安装器（7-Zip）试用、便携文件夹多入口区分与重解析点拦截、试用历史与隐私保护 JSON 报告、再次试用文件夹对照与 Beta.5 旧记录兼容均在 `v0.6.0-rc.1` 完整保留（历史截图与示例报告留存于 `docs/v0.6.0-beta.2/` 至 `docs/v0.6.0-beta.6/`）。
