# Contain v0.7.0-rc.1「配置还原点」候选版验收记录

> 状态：**Windows x64 可安装发布候选版（`feat/config-restore-points` Draft PR #32，未合并、未打 Tag、未公开发布）**。当前公开稳定版仍为 `v0.6.0`。

## 1. Beta.3 双独立进程并发复核结论

在修改代码前，基于未修改的冻结 Beta.3 构建（`7f3aab5536ca645ed8756caf8086dfff04704748`）使用两个独立 OS 进程共享同一测试数据目录与同一 `settings.json`（`A=light` `7c622019…`、`B=dark` `4248206e…`，UTF-8 含 BOM · CRLF），分别在窗口一写完 `restore_prepared`、`undo_prepared` 后通过测试钩子精确暂停并保持进程存活，再启动窗口二执行启动核对、刷新与操作尝试。**实测确认 Beta.3 无法区分「另一窗口仍在写入」与「上次操作已中断」**：

| 复核场景 | 窗口二介入前状态（P1 存活暂停） | 窗口二行为与观察 | 窗口二介入后、P1 仍存活时状态 | P1 恢复执行后最终状态 |
| --- | --- | --- | --- | --- |
| 场景 1：P1（PID `91564`）停在 `restore_prepared` | 目标为 **B**（`4248206e…`），记录 `164b501fa716.json` 为 `restore_prepared`（`consumed=false`），原副本 `.bin` 为 **B** | P2（PID `83856`）误判为 `RestoreNotApplied`（「恢复未生效」），抢先将 P1 记录改写为 `restore_aborted`（`consumed=true`），并允许执行并发恢复 | 目标已被 P2 改为 **A**（`7c622019…`）；目录下出现 2 条记录（P1 被标 aborted + P2 新建 `ac24dd490a4c.json` `restore_completed`） | P1 继续执行并再次覆盖写盘 A，把已被标 aborted 的记录重新覆盖为 `restore_completed`，磁盘留下 **2 条并发竞争的未消耗撤销记录** |
| 场景 2：P1（PID `90028`）停在 `undo_prepared` | 目标为 **A**（`7c622019…`），记录 `9b1dc2796241.json` 为 `undo_prepared`（`consumed=false`），原副本 `.bin` 为 **B** | P2（PID `92276`）误判为 `UndoNotApplied`（「撤销未生效」），直接允许进入撤销并抢先执行写回 **B** | 目标已被 P2 改为 **B**（`4248206e…`），P1 的记录 `9b1dc2796241.json` 已被 P2 抢先标为 `undo_completed`（`consumed=true`） | P1 继续执行并再次重复写回 **B**，覆盖元数据 |

## 2. RC.1 跨进程协调与过期预览修正

1. **按目标文件维度的操作系统内核独占写锁**：
   - 在 `ConfigRestorePoints\Locks\target-<hash16>.lock` 上使用 `FileStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)` 获取单写者内核句柄，锁内写入持有者 `owner_pid`、`instance_id`、`operation_kind`、`undo_id`（允许读共享，拒绝并发写与删除）。
   - 正常结束或异常退出在 `using` 块释放句柄；进程被强杀或崩溃时由 Windows 内核立即回收句柄，另一窗口无需等待超时即可无缝接管 Beta.3 启动只读核对。
   - 实测中还修正了一处真实多进程边界：窗口一完成恢复（`restore_completed`）释放锁后若保持窗口打开，窗口二不再被已完成的 `owner_pid` 误拦，仍可正常预览并撤销（以内核写锁作为唯一活跃写者权威信号，并补入单元测试回归）。
2. **非抢占式只读浏览与冲突提示**：
   - 遇到同一目标正被另一活跃窗口写入时，窗口二保持完整只读浏览能力，启动核对与界面卡片明确提示 `ActiveInAnotherWindow`（「其他窗口正在操作」），绝不抢先将对方 `restore_prepared` / `undo_prepared` 标为中断、完成或已消耗，同时阻止并发写入/撤销（返回 `BusyInAnotherWindow`）。不同目标文件的并发操作互不阻塞。
3. **过期预览确认前重读保护**：
   - 窗口一正常完成恢复或撤销后，若窗口二仍停留在旧的恢复确认页或撤销预览页，点击确认时在拿到目标锁后先从磁盘重读最新目标文件与 `Undo/<id>.json`：
     - 恢复旧预览发现磁盘已等于目标备份时返回 `RefreshedDueToExternalEdit`（「目标文件已与所选备份一致」），**不重复写盘、不生成重复撤销记录**；
     - 撤销旧预览发现记录已被消耗、已撤销或已有更新恢复时返回 `StaleAlreadyHandled`（「操作记录已更新」），**不重复写盘**并自动关闭过期预览与撤销条。

## 3. RC.1 候选构建身份

- **候选版本**：`0.7.0-rc.1`（桌面端、Setup 与包元数据统一为 `0.7.0-rc.1`，PE / 程序集版本 `0.7.0.4`；采集核心沿用 `contain 0.3.2-beta.10`）
- **最终构建提交 SHA**：`5efac4fe358d6a06f6ca9356338bbf645f5d76e5`（后续仅追加本验收文档，不改产品源码与构建输入）
- **签名状态**：`NotSigned`（未做代码签名）

| 候选产物 | 大小（字节） | SHA-256 | 说明 |
| --- | ---: | --- | --- |
| `contain-0.7.0-rc.1-windows-x64.zip` | 71,476,718 | `827c345b1ede6af01b7fbddd5c2872e3dae69583a8a5ed324391a651c0452068` | 便携解压即用桌面包（723 个载荷文件，`Contain.Viewer.exe` `FileVersion=0.7.0.4`） |
| `Contain-Setup-0.7.0-rc.1-x64.exe` | 51,553,657 | `e3625040d8a8f5e18c420076db2500df316f2f6355587dd2fd84d3b48a138811` | 当前用户最低权限安装器（Inno Setup `6.7.3`，`AppVersion=0.7.0-rc.1`，`FileVersion=0.7.0.4`） |

## 4. 验收证据（严格区分受控测试与最终候选包实测）

全部测试均使用独立测试数据目录（`CONTAIN_VIEWER_TEST_ROOT`）或隔离临时 Windows 11 来宾，未触碰宿主机个人记录库。

### 4.1 受控测试证据（钩子精确暂停与单元测试，提交 `5efac4f`）

#### (A) 模型单元测试（`Contain.Desktop.Model.Tests`：**126 / 126 通过**）

在 Beta.3 120 项基础上新增 6 项 RC.1 跨进程并发与过期预览专项回归：
- `config_rc1_dual_window_live_restore_prepared_blocks_window2_without_hijacking`
- `config_rc1_dual_window_live_undo_prepared_blocks_window2_without_hijacking`
- `config_rc1_window2_stale_restore_and_undo_previews_reread_disk_after_window1_completes`
- `config_rc1_owner_killed_releases_lock_and_window2_reconciles_cleanly_across_two_reopens`
- `config_rc1_different_target_files_operate_independently_while_lock_held`
- `config_rc1_completed_restore_by_another_live_pid_remains_undoable`

#### (B) 双独立 OS 进程受控验证（最终 RC.1 `Contain.Desktop.Model.dll`，通过命名事件精确同步）

| 受控双进程用例 | 进程 PID 与状态 | 验证结论 |
| --- | --- | --- |
| Case 1：P1 停在 `restore_prepared` 并保持存活 | P1 `93796` / P2 `97200` | P2 可只读浏览，启动核对报 `ActiveInAnotherWindow`（「其他窗口正在操作」），恢复与撤销均阻止（`BusyInAnotherWindow`）；P2 退出后磁盘仍为 **B**（`4248206e…`）、`5dd4dba371a8.json` 仍为 `restore_prepared`（`consumed=false`）、原副本 B 完好；P1 恢复运行后写完 **A**（`7c622019…`），**连续 2 次重开零写盘且状态干净** |
| Case 2：P1 停在 `undo_prepared` 并保持存活 | P1 `96428` / P2 `83392` | P2 可只读浏览，启动核对报 `ActiveInAnotherWindow`，撤销预览与直接撤销均返回 `BusyInAnotherWindow`；P2 未改写或消耗 `9349867e1f9a.json`（仍为 `undo_prepared` / `consumed=false`），磁盘仍为 **A**；P1 恢复运行后写回 **B**（`4248206e…`）并记 `undo_completed`，**连续 2 次重开零写盘且无撤销条** |
| Case 3：P1 替换目标为 A 后被真实 `Process.Kill()` 强杀 | P1 `95620` 被 OS 强杀 | P1 存活时 P2 显示 `ActiveInAnotherWindow`；OS `Process.Kill()` 终止 P1 后内核即时释放写锁，P2 点「重新核对」立即接管 Beta.3 只读核对转为 `RestoreAppliedNeedsReview`（目标 **A** 零写盘）；**连续 2 次重开核对 A 与 B 完好且零写盘**，最终撤销精确写回 **B**（`4248206e…`） |

### 4.2 最终候选包真实 Windows 11 实测证据（无测试钩子介入）

| 实测场景 | 被测最终产物 | 实测结果 |
| --- | --- | --- |
| 1. 双真实 RC.1 GUI 窗口共享目录与 `settings.json`（过期恢复/撤销预览） | `contain-0.7.0-rc.1-windows-x64.zip`（两独立 `Contain.Viewer.exe` `0.7.0.4` 进程） | **通过**：两窗口同时打开恢复确认页，窗口一确认恢复 A 后，窗口二点击旧预览「确认恢复」先重读磁盘并返回「目标文件已与所选备份一致（可能已由其他窗口完成恢复）」，目标 `LastWriteTimeUtc` 零变化、仅存 **1** 条撤销记录；随后两窗口同时打开撤销预览，窗口一确认撤销回 B 后，窗口二点击旧预览「确认撤销」返回「操作记录已更新（可能已由其他窗口撤销或完成）」，目标零重复写盘且自动收起过期撤销条 |
| 2. 冻结 Beta.3 真实还原点与「待核对」记录在 RC.1 打开 | 冻结 `0.7.0-beta.3`（`0.7.0.3`）创建 → 最终 `0.7.0-rc.1`（`0.7.0.4`）接管 | **通过**：先用冻结 Beta.3 真实创建还原点与未完成 `restore_prepared` 记录，独占锁定目标后在最终 RC.1 打开显示 **「待核对」**；在「待核对」状态下点击「查看原副本」成功展示原副本 B（含 `"theme": "dark"`），「另存原副本…」按钮可用；解除独占锁后点击「重新核对」转为 **「已核实：恢复已生效」**，经撤销预览精确写回原副本 B（`dfc83c9b…`） |
| 3. 公开 `v0.6.0` → 最终 `v0.7.0-rc.1` 断网无沙箱就地升级 | 公开 `v0.6.0` `Contain-Setup-x64.exe`（`ea991d39…`）→ 最终 `Contain-Setup-0.7.0-rc.1-x64.exe`（`e3625040…`） | **通过**：在断网、未启用 Windows Sandbox 的干净 Windows 11 来宾中先装公开 `v0.6.0`，预置合成个人安装记录库 `contain.db`（`33485c22…`）与 1 条由公开 `v0.6.0` 真实生成的隔离试用历史 `1511b3f60e77.json`（`便携 演示 & App.exe`），再就地升级至最终 RC.1 Setup；验证卸载项严格仅 **1** 条（`DisplayVersion=0.7.0-rc.1`）、`Contain.Viewer.exe` `FileVersion=0.7.0.4`、两类旧文件逐字节未损且在无沙箱 UI 中全部可读（3 条个人安装记录 + 1 条 `v0.6.0` 试用历史及详情页）；随后在升级后的 RC.1 中完整跑通 **「建立还原点 → 修改 → 恢复 → 撤销」**（A `cb17d9f5…` / B `b1ddfd78…`）并干净卸载（`exit 0`） |

## 5. 全量自动化与质量门禁（构建提交 `5efac4f`）

- `Contain.Desktop` Release 构建：**0 warnings，0 errors**。
- `Contain.Desktop.Model.Tests`：**126 / 126 通过**。
- `cargo test -p contain-workflow --locked`：**55 / 55 通过**。
- `scripts/test-package-source-desktop.py`：**10 / 10 通过**。
- `scripts/check-fuzz-lock.py`、`cargo deny --locked check` 与全库卫生扫描：**全部通过（0 命中）**。

## 6. Beta.1 / Beta.2 / Beta.3 历史基线与实拍截图保留

- **Beta.1 基线**（`ab8a1dce76ae5c7d7442544dbc316dd239205f97`）：单文件原始字节备份、同名不同目录区分与基础恢复撤销（实拍：`docs/v0.7.0-beta.1/contain-v070b1-restore-confirm.png`）。
- **Beta.2 基线**（`c87fe677820f4f9faf18b6a93910fa125b360159`）：操作跟随差异方向、轻量撤销预览、取消不写盘不消耗与外部修改/删除冲突阻断（实拍：`docs/v0.7.0-beta.2/contain-v070b2-restore-preview.png`、`docs/v0.7.0-beta.2/contain-v070b2-undo-preview.png`）。
- **Beta.3 基线**（`7f3aab5536ca645ed8756caf8086dfff04704748`）：阶段化保存顺序、启动只读核对、第三版 C 保护、查看/另存原副本 B 与「待核对」（实拍：`docs/v0.7.0-beta.3/contain-v070b3-recovery-verified.png`、`docs/v0.7.0-beta.3/contain-v070b3-recovery-version-c.png`）。
