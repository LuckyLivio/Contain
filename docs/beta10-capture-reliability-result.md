# Beta.10 采集修复候选：同 ZIP 对照通过，正式发布仍阻塞

后续 [D5 完整桌面启动诊断](beta10-startup-diagnosis-result.md) 使用同一原 ZIP：空历史启动 394 ms、10 次 Ready 均通过，原超时未复现；完整验收在安装结果观察阶段缺少必需控件而失败。该新结果不替换下列原始失败，正式发布仍阻塞。

[下载 Windows x64 诊断预览 ZIP](https://github.com/LuckyLivio/Contain/actions/runs/42227112910/artifacts/13506927140)（需登录 GitHub；解开产物外层后再解开内层 ZIP；保留至 2027-02-04）。这是一份新候选，**不是正式 Beta.10 发布**。

- 文件：`contain-0.3.2-beta.10-windows-x64.zip`。
- 源码：`b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4`。
- 内层 ZIP SHA-256：`a7ef3faed3c42aada77fe5619b732fe092b8787f89311047105ff6082b132656`。
- CRC、476 项 CONTENTS 校验、BUILD 和生产 EXE／DLL 身份已核对；后续对照使用这个原 ZIP，未重建或重新打包。
- [PR #15](https://github.com/LuckyLivio/Contain/pull/15) 保持草稿，未合并、未创建 Beta.10 tag/prerelease。Beta.9 标签及附件保留；现有 WinForms 不改版，不启动 0.4。

## 先回答四个问题

**711 条在哪里丢？** 上一份交付的现有分项已将它们定位到应用接收队列；交付、原始保存、ETW 和解码丢失均为零。旧产物没有阶段计时，不能逐条恢复这 711 条发生时的接收线程工作。本轮独立失败中，D1 的 4,060/4,093、D2 的 6,272/6,326 次队列溢出发生在初次候选分类阶段；锁等待与 pending 扫描阶段均为零。其余阶段及未分配间隙仍保留，未强行归因。

**9,249 条候选为何未能保留？** 这是独立的跟随上下文丢失，不能加到上述原始记录丢失。旧包缺逐原因字段，不能将新运行分项冒充其逐条根因。未改算法的 D1/D2 证实缺实时身份的候选达到原有 2,048 条上限；D2 的 6,358 次容量拒收为文件记录 6,357、完成记录 1。初始快照枚举完成、预算未耗尽，因此没有增加预算或采集。

代码中确认的缺口：初始化期间已收到并保存的 ETW 生命周期没有进入随后创建的实时 cache，只有最终回放能读到。初始化期间新生线程又被初始快照正确排除。这会使本可正常筛除的背景写入缺少实时身份、堆满候选并增加分类工作。修复让**同一份现有有界 cache** 从初始化移交给接收器；不复制 cache，不改变快照有效期、身份冲突检查、容量、存储或退出边界。实际源码受控输入中，10,240 次无关写入的候选丢失 **8,192 → 0**，父子请求及完成保留 4/4，无关误选为零。这证明交接机制；不能断言旧 9,249 条全部有同一原因。

**两条目标写入现在能解释吗？** 可以。本次同机对照里，旧版本保留的 PID＋创建时间、线程／进程区间、直接父实例和唯一成功 completion 都足够；其最终 Unknown 来自全会话丢失后禁止升级的安全规则。修复后的正常生产路径为父、子各保留两条请求，其中各一条通过独立真值、实例与唯一完成关联检查而成为 High。其他 Unknown 保留，候选两轮 `unknown_actor_events` 仍为 11/17；没有强行升级。High 仍只表示执行进程关联，`state_validated/current_path` 没有提升为真。

**正式 Beta.10 达到发布条件了吗？没有。** 原 ZIP 完整桌面验收在默认空历史启动的 `ReadyAsync()` 内超时，未到真实 GUI 安装门。新诊断没有复现该超时，不能替换失败或解释其原因。正式发布校验器仍拒绝：`All existing desktop gates and actual packaged follow capture must be verified`。采集矩阵通过不清除这个桌面阻塞。

## 同一个原 ZIP、同一个 Windows runner 的固定对照

[D4 六次全部结果及安全身份依据](https://github.com/LuckyLivio/Contain/actions/runs/42231290678/artifacts/13509891233)。顺序在运行前声明，无重试、无挑选成功样本。每次使用同一夹具、12,000 个背景文件的写入／重命名／删除，共 **36,000 次独立验证的成功操作**；父、子及无关目标另有独立真值。机器不是用户的 Parallels，受控调用相同不等于系统 ETW 事件流相同。

| 顺序与版本 | 实际回调 | 实际收到记录 | 接收队列丢失 | 候选丢失（另计） | 父／子目标关联 |
|---|---:|---:|---:|---:|---|
| r1 Beta.9 | 734,404 | 258,021 | 69,520 | 37,269 | 父、子均 Unknown |
| r1 上一诊断包 3c2e068 | 682,899 | 215,785 | 5,770 | 6,675 | 父、子均 Unknown |
| r1 本轮原 ZIP b0d2510 | 682,619 | 216,303 | 0 | 0 | 父、子各一条 High |
| r2 本轮原 ZIP b0d2510 | 678,836 | 216,413 | 0 | 0 | 父、子各一条 High |
| r2 上一诊断包 3c2e068 | 676,997 | 215,742 | 7,617 | 5,812 | 父、子均 Unknown |
| r2 Beta.9 | 677,177 | 216,363 | 34,540 | 38,539 | 父、子均 Unknown |

两轮候选的两种丢失均降至零；相较旧诊断包分别减少接收丢失 5,770/7,617、候选丢失 6,675/5,812。六次的交付、原始保存／配额、ETW、解码丢失与无关误归属均为零；安装器和子进程正常退出，每次结束、保存、新进程精确会话 inspect 及八项守恒均通过。两候选的新进程目标 explain 精确事件检查也通过；旧包的归因及 explain oracle 失败或未运行，原结果保留。两次候选队列高水位 335/252，候选高水位 14/12。

限制保持：接收 4,096；交付 4,096 条/16 MiB；候选 2,048 条/8 MiB；资源 4,096；journal 64 MiB；settle 1,500 ms，最大排空 10,000 ms。正常范围筛除、未解析、候选拒收、原始丢失继续独立计数；完整分项、实际收发数、冷读回和退出结果见六个 public summary。跟随模式无安装前基线，覆盖质量仍为 Degraded。

可直接核对的目标（均为唯一 `status=0, success=true` 完成，不是仅凭路径或 PID）：

| 轮次／目标 | 请求事件 ID | 完成事件 ID |
|---|---|---|
| r1 `A/parent-target.txt` | `9c2c30e3-0bdb-4473-bc20-8d4628b978d8` | `25a775f9-ad2a-46b8-adfa-f2026d37886e` |
| r1 `B/child-target.txt` | `d42e2a6a-9bc9-4c4b-b76c-b2c04a504d44` | `69e85b9b-9127-47e5-ad2f-eedaa0489183` |
| r2 父 | `59cc174b-6b1b-4547-bb05-35d8459dd17d` | `9d544f15-a9d0-41a8-bbea-76b0d18d64e6` |
| r2 子 | `953c8c66-9a81-43c5-8a9b-d82f5374356d` | `e65b2aa4-e272-4052-851b-d1848d89d7c0` |

r1 父实例为 `(3016, 134384480147223502)`，子为 `(7884, 134384480148639181)`，子的父实例精确匹配前者；r2 对应为 `(5684, 134384480365192037)` 与 `(1508, 134384480366520929)`。事件均落在独立系统调用真值区间，规则分别为 InstallerPid / DescendantProcess。完整保留事实在各 `follow-identity-diagnostic.json`，真值没有被当作身份输入。

## 所有尝试与尚未解决的桌面阻塞

| 尝试 | 接收丢失 off/on | 候选丢失 off/on | 状态 |
|---|---:|---:|---|
| [D1，00a82f6](https://github.com/LuckyLivio/Contain/actions/runs/42222854581) | 2,575/4,093 | 8,137/7,418 | 原算法；失败保留，分类占主要溢出 |
| [D2，721df2d](https://github.com/LuckyLivio/Contain/actions/runs/42225513650) | 4,661/6,326 | 7,121/6,358 | 原算法；补出现有 snapshot/provider 分项，未改预算 |
| [D3，b0d2510](https://github.com/LuckyLivio/Contain/actions/runs/42227112938) | 0/0 | 0/0 | 独立诊断通过，不冒充冻结 ZIP 验收 |
| [C1 完整 ZIP](https://github.com/LuckyLivio/Contain/actions/runs/42227112910) | 未运行采集矩阵 | 未运行采集矩阵 | 桌面 `packaged_executable_default_history` TimeoutException；真实 GUI 安装与后续完整布局/事件门未执行 |
| [D4 原 ZIP](https://github.com/LuckyLivio/Contain/actions/runs/42231290678) | 见六次表 | 见六次表 | 采集矩阵通过；发布仍未验收 |

D1/D2 本地重放确认两目标身份与 completion 已保留，最终 EventLoss 安全降级；不能反推私人原会话或旧交付的每个事件。Profile-on 会扰动时序，CPU 计时有粒度限制。D2→D3 分类 wall 2.336→1.723 秒、重扫 0.115→0.00586 秒仅是跨 runner 支持性观测，不是同机吞吐声明。

C1 安全产物只能把超时缩小到控件发现 40 秒、控件启用 40 秒或内层 UIA 调用 20 秒之一，旧私有堆栈不可取得。D4 新增测试诊断保持全部断言/截止/清理不变，使用 C1 原 DLL；三个 Ready 样本 492/532/558 ms，personal 检查 8/8、退出码零，自有 viewer/core/未知遗留计数均零。**原超时未复现，根因仍未确认**，不能用这次子集通过替代完整桌面失败。

D4 测试/脚本源码为 `c45dab35b8ed175e9e928b53a108dafa0a7aa0f6`，产品/身份重放/夹具源码为冻结 b0d2510；来源和哈希分开记录。D4 顶层 `accepted=false, release_accepted=false`，即使内部采集矩阵 accepted=true 也不授权发布。正式门槛、上传器未放宽。[预声明安排](beta10-reliability-validation-plan.md) 和 [上一轮 3c2e068 历史报告](capture-loss-beta10.md) 均保留。

源码 b0d2510 的 Windows Check/Coverage/CoverageOriginal/Dependencies/Companion 通过；nextest 392 通过、7 跳过既有实验，未重试。覆盖行/函数：workspace 77.29%/78.32%，原 packages 74.92%/77.21%，follow 98.71%/98.67%，ingest 83.70%/75.00%；包含内联测试，不是分支或纯生产覆盖，capture 编排仅 20.31%/30.00%，仍有未覆盖错误路径。Linux 离线 98 通过/8 忽略，交叉 Clippy 通过。[同源码扩展检查](https://github.com/LuckyLivio/Contain/actions/runs/42227132180) 的 Windows ASan 三目标各 30 秒通过；Bench 保留全部 56 样本，JSON 中位耗时 +2.26%，不宣称其测得 ETW 提升。

## Parallels 最短复测

1. 完整解压新 ZIP，运行 `Contain.Viewer.exe`，按原参数通过“跟随安装器”运行同一 7-Zip；记下新会话 ID。
2. 结束保存后分别查看**接收队列溢出/断开、交付丢失、原始保存失败/配额、跟随候选丢失**。期望受控复测为零；不要只看 ETW 丢失。
3. 搜索 `write_requested` 与实际安装的 `7z.exe`，找一条有 PID＋创建时间、验证父子实例及唯一成功完成依据的源请求，复制其事件 ID。其他 Unknown 保留，不要求每条重复请求都 High。
4. 退出重开，同一会话 ID 下打开该事件并核对计数。已结束且已读回的丢失会话应显示“记录已保存；采集期间丢失了部分记录”；历史缺分项显示“未提供”，未结束会话有单独提示。若启动未就绪，也记录这一失败，不用重跑结果覆盖它。

用户原始 Parallels 会话的 **57,767** 次应用丢失仍缺原机已有分项，根因未确认。只读 exact-session 命令和脚本仍见[历史报告](capture-loss-beta10.md#read-the-old-session-without-sharing-private-data)；只需必要计数，不需要数据库、私人路径或完整日志。本轮证明独立复现的交接缺口与限定负载改善，不能据此断言所有 Windows/Parallels 负载均无丢失。
