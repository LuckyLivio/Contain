# v0.4.0-beta.2 验证记录

预先安排见 [验证计划](beta2-validation-plan.md)。以下尝试均零重试，保留失败。
没有读取用户私人附件、原始数据库或完整日志；原会话事实来自本轮任务描述。

## B1：新增默认文件名原生断言失败

- 源码 `ae9d023ee72cb0db922f7f7f1225ba4d4643a559`。
- [Windows run 43193261024](https://github.com/LuckyLivio/Contain/actions/runs/43193261024)。
- 冻结 ZIP SHA-256 `9aa4136c82e699cac639b2f74bc04b328ab871940356d926dfb0e7546c461175`。
- Windows 10.0.26100 x64，实际96 DPI；WPF＋WPF UI。
- 14组：4通过、1失败、9未执行；失败前486项断言通过。
- 失败：`search_copy_explain_and_export_all` / `ExportMatches`，
  `default name has export type and explicit UTC time`。
- 实际默认文本未进入该版报告，因此不能区分默认名错误、读取时机或保存框显示规则。
  原读取使用有界 `SendMessageTimeout(WM_GETTEXT)`，并非 `GetWindowText`。
- 新建空库、准确保存记录展示、130事件/53匹配及分页、解释复制在此之前通过；
  **真实安装、真实ETW对照与冷启动等后续门未执行，不计为通过**。
- 原包与截图保留在该run的 `beta2-frozen-windows-package` / `beta2-windows-evidence`。
  这是失败候选，不是可发布验收包。

## 问题分类与证据边界

- 已复现并修复（代码及回归，原生最终验收待完成）：固定导出默认名、混合页面命名、终态运行噪声。
- 已确认代码缺口并修复：跟随模式读取/关闭及同资源非目标上下文会刷新静默时间。
  新行为只让可靠目标实例的明确文件变更请求刷新静默时间，保留原证据和有界截止。
  持续已验证直接子进程仍等待到截止，仍报告可能遗漏后续操作。
- 新增诊断，原根因仍未确认：ETW来源丢失。独立UUID会话和STOP累计值未发现重复累加。
  保留ready/pre-stop/STOP三份累计查询、EventsLost/实时/日志缓冲、实际buffer配置与STOP/join耗时；
  未改provider范围、缓冲容量或身份/完成标准，不声称192,055条来源损失已修复。
- 原机未验证：Chrome/7-Zip当时具体等待实例、生命周期完整性、ETW丢失阶段及逐条写入归因。
  Chrome可运行与启动观测不证明chrome.exe写入；7-Zip自定义路径请求不自动证明可靠归因或完成。
  不将Parallels环境本身判为原因。C1、D5、历史Parallels与M11仍开放。
