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

## B1 质量门：依赖公告阻塞

[CI 43193260754](https://github.com/LuckyLivio/Contain/actions/runs/43193260754)
的 Windows Check、Coverage 和原包覆盖检查通过，Dependencies 拒绝
`libsqlite3-sys 0.35.0`（rusqlite 0.37.0 bundled SQLite）：
`RUSTSEC-2026-0459` / `CVE-2026-119375`，特制DB/WAL页大小不一致可读出未初始化缓存。
项目存在外部只读库入口，因此不增加公告忽略；最小升级 SQLite 修复版本，重验历史迁移、
外部只读及 WAL。生产/模糊测试锁文件保持一致。新二进制需要重新冻结与原生验收。

## B2：保存框显示规则已观测，测试类型读取仍失败

- 源码 `7dba1b6e0e9ae7af08fc33297c9c8dc280b993a9`；
  [run 43194618890](https://github.com/LuckyLivio/Contain/actions/runs/43194618890)。
- 冻结ZIP `49925ed612efc28d5393654f45c6f70674db34a07a15d82b3f875bcfd8dad6f1`，验后未变。
- 4组通过、1失败、9未运行，486断言；失败在`native_save_default/ExportMatches`。
- 双读取（UIA ValuePattern及WM_GETTEXT）均返回正确的记录名、UTC开始时间、短会话ID及类型主体；
  Windows保存框隐藏`.html`。原始截图明确显示类型标签“本地 HTML 报告”，没有`*.html`。
  测试以原生ComboBox窗口文本读取类型得到空值，并错误要求可见标签含通配符。
  这是已观测的测试假设问题；未发现产品默认名生成错误。
- 将读取所属对话框准确类型选项，并保留“无扩展名输入实际生成.html且无裸stem文件”的行为门。
  B2尚未执行该实际保存、交互安装、ETW及四次对照，不能计为这些行为通过。
- B1未保留默认文本，不能恢复其当时的读取值；B2的新证据不改写B1失败记录。

## SQLite修补候选的离线边界

最小升级rusqlite 0.41.1 / libsqlite3-sys 0.39.1，实际SQL查询链接库为SQLite 3.54.1。
保留native cache与checked unsigned SQL转换；不新增WASM后端或迁移schema。
完整Linux offline历史/只读/备份/WAL回归及Windows GNU workspace Clippy通过。
Linux交叉fuzz构建缺MinGW g++，未执行fuzz语义；最终由Windows MSVC固定ASan smoke验证。
