# Beta 10：冻结 ZIP 的完整桌面启动诊断（D5）

[下载冻结 Windows x64 ZIP](https://github.com/LuckyLivio/Contain/actions/runs/42227112910/artifacts/13506927140) · [下载本次运行计划、来源与结果](https://github.com/LuckyLivio/Contain/actions/runs/42249868473/artifacts/13520640899) · [下载完整桌面诊断证据](https://github.com/LuckyLivio/Contain/actions/runs/42249868473/artifacts/13520930735)。GitHub Actions 下载需登录；ZIP 仍为诊断预览，未发布正式 Beta.10。

**本轮没有复现原来的 ReadyAsync 超时，也没有确认可修复的启动缺陷。完整验收在后续安装结果观察阶段失败，因此 PR #15 继续保持草稿，未合并、未发布。** 没有修改产品二进制或采集代码，没有延长等待、放宽断言或追加重跑。

## 包和执行身份

- 产品源码：`b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4`。
- 原 ZIP：`contain-0.3.2-beta.10-windows-x64.zip`，77,889,599 字节。
- SHA-256：`a7ef3faed3c42aada77fe5619b732fe092b8787f89311047105ff6082b132656`。
- 本次测试程序／工作流源码：`ad8c6b5894a1964d972d08d23d2d8159a3acb8b4`。
- 完整 Windows 运行：[42249868473，失败，未重试](https://github.com/LuckyLivio/Contain/actions/runs/42249868473)。预声明安排见 [D5 计划](beta10-reliability-validation-plan.md#d5-full-desktop-startup-diagnosis-declared-before-execution)。

运行前校验原 ZIP 的全部 476 个清单成员，运行后再次校验 ZIP、全部解压成员且没有新增文件。测试程序单独发布，并加载原 ZIP 内字节相同的 Viewer DLL；原产品、采集代码、独立夹具和原有验收谓词均冻结。包内 `diagnostic_unverified` 和首次失败的 `VALIDATION.json` 保留，未重写。

## 本次启动实际发生了什么

下列时间相对每次启动诊断的起点；原生窗口每 100 ms 采样，因此是**首次观察时间**，不是窗口创建时间。

| 启动 | 进程启动调用返回 | 首次观察到窗口 | 找到 NewInstall | 观察到启用 | Ready 完成 |
|---|---:|---:|---:|---:|---:|
| 首次空记录库 | 9 ms | 338 ms | 366 ms | 393 ms | 394 ms |
| 已有合成记录首次打开 | 1 ms | 329 ms | 403 ms | 432 ms | 433 ms |
| 移动原包后退出重开 | 9 ms | 415 ms | 552 ms | 576 ms | 577 ms |

完整流程中 10 次 `ReadyAsync()` 全部到达 ready，完成时间 394–577 ms；这些就绪等待期间未观察到进程提前退出，没有 UIA 调用达到截止时间。已完成的单次 UIA 调用最大 81 ms。首次空库的桌面根节点、进程窗口查找、控件查找最大调用分别为 38、23、32 ms。就绪时保留的原生窗口样本可见、启用且未报告 hung。

诊断最多保留 64 次启动和 7 类固定 UIA 调用聚合，原生采样每 100 ms、周期检查点每秒最多一次，不增加 UIA 请求。记录工作线程排队／开始、探测与调用序号、声明截止时间及超时观察时间；超时先冻结状态，迟到线程不能覆盖。没有窗口文字、路径、UIA 树或异常消息。原 40 秒发现、40 秒启用、20 秒 UIA 探测、10 分钟测试程序期限，以及断言和清理都保留。

`packaged-ready-diagnostics.json` 的 SHA-256 为 `624b268a23288deed44e73dcf3146a0e0a7612ab594142b0db45636ae01c5565`。本次没有截止时间调用，相关字段为 null；不能从这些成功样本倒推出原 C1 超时属于产品还是测试程序。

## 完整验收卡在哪里

| 检查组 | 通过 | 失败 | 未运行 |
|---|---:|---:|---:|
| 原有桌面／CLI／合成记录 | 22 | 0 | 0 |
| 个人记录及合成记录冷启动 | 8 | 0 | 0 |
| 概览、搜索、搜索导出 | 27 | 0 | 0 |
| 进度、首次配置、布局 | 17 | 0 | 0 |
| 实际 GUI 跟随安装 | 3 | 1 | 2 |
| 合计 | 77 | 1 | 2 |

实际 GUI 已选择原包夹具、触发开始，观察到 `capturing`、`finalizing`。`gui_runtime_stages_and_close_guard` 在 `observe_one_recording` 开始约 6,429 ms 后抛出 `RequiredProductControlMissingException`。

依据已保存阶段、截图摘要及未改动代码路径，**推断**失败发生于循环开头的 `DescriptionAsync("SessionTitle")`：精确会话 ID 尚未取得；一次性关闭保护和运行截图已经完成；可选的 InstallStatus 采样不会抛出该必需控件异常。没有保留这次异常的调用栈，因此这不是直接记录的控件 ID，也不能判定控件未被 UIA 找到的原因。

后续准确会话自动打开、实际来源事件解释／导出及该安装会话退出重开的门槛未完成。摘要中的 `data_saved=false`、`installer_started=false`、`real_etw_executed=false` 等来自尚未完成的最终读回状态，表示**未确认**；不能据此宣称安装器未启动、ETW 未发生或实际保存失败。前面的合成记录冷启动成功也不能替代实际安装会话检查。

测试程序在 182,950 ms 后退出 1，未触及十分钟上限；只读进程核对剩余 Viewer/core/未知均为 0。没有取得实际安装器和采集进程的正常退出码，不能将“已无剩余进程”写成“安装保存均通过”。完整原有验证器中的实际安装及首次安装流程两项仍为 false。

## 保留的结论和边界

原 [C1 完整验收失败](https://github.com/LuckyLivio/Contain/actions/runs/42227112910)、[D4 子集及六轮采集对照](https://github.com/LuckyLivio/Contain/actions/runs/42231290678)、本次 D5 全部保留。D4 的同 ZIP 两次零接收／候选丢失及父子正确关联仍有效，见[采集结果报告](beta10-capture-reliability-result.md)，本轮没有重复采集矩阵。

原启动超时仍缺可以归责到产品或测试层的失败现场；本次观察到了就绪的全部阶段，而非复现失败。新增安装结果观察失败也尚未证明属于哪一层。本轮只诊断启动，不根据推测改产品，不扩大到桌面改版。PR 保持草稿，原 Beta.9 标签和附件保留；正式 Beta.10 发布门槛未满足。

Linux 验证：17 项原保存状态展示检查、8 项真实诊断记录器的合成状态检查通过；Windows 目标测试程序 Release 构建 0 警告／错误。新增检查覆盖迟到线程不能覆盖超时、清理前检查点、启动异常消息脱敏及 64 次上限，不冒充 Windows/UIA 验证。该测试提交的 [push CI](https://github.com/LuckyLivio/Contain/actions/runs/42249868329) 与 [PR CI](https://github.com/LuckyLivio/Contain/actions/runs/42249877481) 均通过。

用户原 Parallels 会话的采集丢失分项仍未取得，其根因结论不变；本轮没有访问或索取用户数据库。
