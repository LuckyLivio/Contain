# v0.3.2-beta.10：采集可靠性与保存状态诊断

[GitHub Prerelease](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.10) · [直接下载 Windows x64 自包含 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.10/contain-0.3.2-beta.10-windows-x64.zip) · [SHA256SUMS](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.10/SHA256SUMS.txt) · [最终验收依据](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.10/FINAL-ACCEPTANCE.json) · [构建与交付身份](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.10/BUILD-IDENTITY.json)

完整解压直接下载的 ZIP，运行 `Contain.Viewer.exe`。不需要再拆诊断组合包，也不需要安装 Rust、Python、Visual Studio 或独立 .NET 运行库。点击“新建安装记录”，选择 EXE，沿用默认“跟随安装器记录”；安装结束后查看准确会话的文件请求、执行进程依据与解释。复制事件 ID，退出重开同一会话可再次核对。

## 修复与边界

初始化期间收到的 ETW 生命周期事实原先已保存，却没有交给实时跟随缓存。这使本可正常筛除的背景活动缺少实时身份，增加初次分类工作并挤满候选。修复将同一份现有有界缓存从初始化移交给接收器，不增大队列、不放宽身份有效期，不更改存储或退出上限。

会话已结束并从数据库读回，但采集期间发生丢失时，现有 WinForms 明确显示“记录已保存；采集期间丢失了部分记录”，并区分已有的接收、交付、原始保存等分项；历史缺字段显示“未提供”。真正未结束的会话继续使用单独提示。没有引入 WPF、路径库或 0.4 的界面改造。

丢失后抑制归因升级的规则保留。其他 Unknown 仍然存在；High 只表示有证据支持的执行进程关联，不表示最终文件所有权、安全删除许可或完整软件足迹。有限跟随范围内的 Degraded 不自动等于采集失败。

## 最终产品及证据

发布的是同一个已经验收的原 ZIP，未重编译、未重新包装：

- 产品源码：`b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4`。
- 文件：`contain-0.3.2-beta.10-windows-x64.zip`，77,889,599 字节。
- SHA-256：`a7ef3faed3c42aada77fe5619b732fe092b8787f89311047105ff6082b132656`。
- D6 原生桌面测试源码：`d1bca0b109d722ceaf0ab4ecf89c1d8e17d0b77b`；[完整运行 42325883947](https://github.com/LuckyLivio/Contain/actions/runs/42325883947)。
- 当前测试集审查基线：`6f96beb84cd2c6e9c5b3e23271e2b5c7779f5116`。D6 后的 D7 独立入口和诊断停止标志不改变完整桌面入口；本轮没有修改产品或桌面测试行为。
- 标签指向完成 PR #15 合并及 main CI 的交付提交；它与产品源码、实际 D6 测试源码并非同一 SHA。完整标签对象、目标提交、main CI 与附件哈希记录在外部构建身份和最终验收文件中。

包内 `VALIDATION.json` **仍是 C1 当时的失败结果**，包括 `diagnostic_unverified`。它没有被替换成通过。独立的 `FINAL-ACCEPTANCE.json` 记录新的发布政策、适用的成功证据、原失败文件哈希和已知风险；原始运行计划、摘要中的 `release_accepted=false` 也保留。当前发布依据与历史运行结论分别记录。

[D4 固定六次采集对照](https://github.com/LuckyLivio/Contain/actions/runs/42231290678)使用同一 runner、夹具和每次 36,000 次经独立核对的背景操作，保留两个固定顺序及全部旧版本失败：

| 版本 | 实际收到记录，两轮 | 接收丢失，两轮 | 候选上下文丢失，两轮，另计 | 父／直接子进程 |
|---|---:|---:|---:|---|
| Beta.9 | 258,021 / 216,363 | 69,520 / 34,540 | 37,269 / 38,539 | Unknown |
| 上一诊断包 3c2e068 | 215,785 / 215,742 | 5,770 / 7,617 | 6,675 / 5,812 | Unknown |
| 发布原包 b0d2510 | 216,303 / 216,413 | 0 / 0 | 0 / 0 | 每轮各一条经核验的 High |

六次交付、原始保存／配额、ETW、解码丢失及无关误归属均为零。候选父子目标都有 PID＋创建时间、正确父实例、独立成功写入和唯一成功完成依据，精确事件解释、保存、退出和准确会话冷读回通过。实际系统事件流不完全相同，不能把该结果泛化为任意机器和负载的零丢失保证。

D6 在原包上完成 **80 项完整桌面检查及 11 项原生验收谓词**，实际经过安装、准确会话保存、父子目标核验、原事件解释、正常退出和冷启动。GUI 会话为 `3e912bb4-53e6-43a0-9121-5794db734c67`，父事件 `43749dc6-356d-42da-9f68-98281dd3983e`，子事件 `36f239b7-afa8-4691-b458-498c4ff57038`；界面复制、解释、重开保持同一子事件 ID。

产品源码的 Check、Coverage、CoverageOriginal、Dependencies、Companion 均通过；nextest 392 项通过、7 项跳过。工作区行／函数覆盖率为 77.29%／78.32%，原始包为 74.92%／77.21%，包含内联测试和实例化，不能称为分支覆盖率。独立的三个 Windows ASan 目标各运行 30 秒；合成 benchmark 的全部 56 个样本保留，它不代表真实 ETW 吞吐。发布校验另要求当前合并后 main 的质量检查通过，不能仅引用旧源码的绿色结果。

## 结果页面契约审查

`SessionTitle` 是 Tabs 之外的常驻 Label。`ResetRecord()` 清空描述，`OpenRecordAsync()` 在 summary、diff、history 查询成功后才提交准确 ID；随后仍要完成跟随事件与解释。`OpenCapturedSessionAsync()` 按本次后端 ID 找到唯一记录，完整回调结束后安装窗口才隐藏自身。没有发现主标题在该路径中被隐藏、移除或重建，也没有发现必须修改的产品生命周期错误。

当前夹具开始前明确选中 `synthetic-interrupted`，这是一个非 UUID 的历史 ID。`ObserveRuntime` 会继续等待描述为空或仍为该历史记录的状态。它读到 UUID 后也不会直接通过验收：还必须匹配当前任务详情中的 `LastStatus.SessionId`，验证采集进程退出 0，然后按该准确 ID 查询保存状态和源事件；同名、最新记录或其他 UUID 都不能替代。

本轮没有证据把“控件本体短暂缺失”定义为合法过渡，因此保留 Required 查找的原始失败，不全局吞掉异常。永久缺失会失败，错误会话不能通过当前任务及读回校验，提前退出由轮询中的进程检查拒绝，非预期 UIA 异常仍会上报。没有为了覆盖一个未经确认的合法状态而新增宽容等待或模拟修复证明。

从 D6 测试源码到审查基线，`InstallVerification.cs`、`PackagedDesktop.cs` 和产品目录均无差异。此后仅允许发布工具、发布文档和 README 修改；最终校验再次核对这个范围。因此本轮复用 D6，不为获得更新的绿色编号而重跑原生安装。D7 的 100 次合成切换未复现缺失，不作为修复证明或替代完整验收。

## 发布政策与仍未确认的问题

2026-11-07 的发布政策明确接受下列历史未知事项作为 Beta 风险独立跟踪，不再要求全部历史间歇故障确认根因才能发布。这项政策**不把旧失败改为通过**，也不允许忽略新的实际失败、可复现数据损坏、错误会话、错误 High、保存不一致或持续无法启动问题。最终发布仍要求产品身份、适用采集证据、完整桌面验收和当前质量检查通过。

- [C1：首次空记录库 Ready 超时，原因未确认（#16）](https://github.com/LuckyLivio/Contain/issues/16)。旧产物不能区分发现、启用和内层 UIA 截止时间。D6 后来通过没有证明 C1 已修复。
- [D5：安装结果观察阶段必需控件缺失，原因未确认（#17）](https://github.com/LuckyLivio/Contain/issues/17)。SessionTitle 由控制流推断，D5 没有直接记录具体控件 ID。D6/D7 均未复现，不能归责产品或测试层。
- [Parallels 原会话 57,767 条丢失，尚缺原机分项（#18）](https://github.com/LuckyLivio/Contain/issues/18)。受控场景的修复和零丢失结果不能替代用户原机证据；不要求上传数据库、私人路径或完整日志。

验证平台为 GitHub Windows runner 的 Windows Server 2025、10.0.26100、AMD64；原生桌面 DPI 96。150%／200% 是布局模拟压力检查，不是实际系统 DPI 验收。包内自包含 .NET 8.0.33，Viewer 为 Release，Rust core 为 debug，构建未签名。Windows 11／Parallels、ARM64、任意安装器和无限背景负载没有独立覆盖。ETW 仍依赖当前令牌能力，程序不会自动提权。

旧报告及原结论保留：[采集对照](beta10-capture-reliability-result.md)、[D5 失败](beta10-startup-diagnosis-result.md)、[D6/D7 交接](beta10-final-desktop-diagnosis-result.md)。其“未发布／仍阻塞”描述属于当时的政策和阶段；本页及独立最终验收文件记录本次新的发布依据。Beta.9 及更早标签和附件保持不变。本轮到 Beta.10 为止。
