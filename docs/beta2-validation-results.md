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

## B3：本地时钟测试调度依赖，原生运行主动取消

源码`85ca4eeede9acb0a9ba087da698212ef7a226749`的完整Linux offline为104通过/8既有忽略。
同源码本地模型首次执行失败：新增终态UI测试期望恰好100ms，实际101ms。
既有`InstallProgressClock`为防止显示回退保留单调本地时间下限；测试将真实Stopwatch
调度误当成固定输入。修正为后端100ms原值不变、显示不小于后端测量、终态不标为本地续计、
Tick不再推进冻结显示；不改变生产时钟或降低产品行为要求，不在原条件下反复跑选绿。

[原生run 43196063690](https://github.com/LuckyLivio/Contain/actions/runs/43196063690)
在`Locked desktop regressions and native harness build`阶段请求取消；取消生效前Windows模型/导航已通过，
Freeze阶段被取消，未完成ZIP、窗口采集、交互安装或ABBA对照。该取消和本地失败均保留，不计为原生通过。

## B4：复制后二次读取不一致，保留失败并改进观测边界

- 源码`fd2e27c1b41284afef8c6b7246f50ec6fbf44d04`，
  [run 43196822200](https://github.com/LuckyLivio/Contain/actions/runs/43196822200)。
- 冻结ZIP `cf008d661be59a794f8114200f7894ccdcbaf3f00a105483cb175066c815473e`，验后未变。
- 3组通过、1失败、10未运行，479断言。搜索事件095选择/解释正常且“已复制”可见。
- `clipboard_exact_event`在85ms内成功，证明至少一次实际读取匹配当前事件ID；
  紧接第二次独立读取未匹配。B4没有保留第二次值、owner或sequence，不能判定原因，
  不宣称产品复制出错、外部覆盖或STA读取问题已被证实。
- 原测试把两次读取当同一时点；全局剪贴板允许在两次读取间变化。
  新观测采用OpenClipboard锁内有界CF_UNICODETEXT快照，并保留sequence/owner角色及安全值分类；
  以同一快照核验准确ID，错误内容及忙碌截止仍拒绝，不承诺外部剪贴板以后永不变化。
- 未执行导出、交互安装、ETW及ABBA。后续先执行原有独立四次ETW对照，再进行完整WPF；
  次数、输入、阈值及零重试保持，只有顺序调整，以便来源诊断不再被前置界面观测阻塞。

## 扩展质量证据（固定运行，不择优重跑）

`fd2e27c`的[Windows extended 43196822202](https://github.com/LuckyLivio/Contain/actions/runs/43196822202)
通过MSVC ASan `nightly-2026-11-05`：event_json/lifetime_sequences/completion_correlation各30秒。
B5仅改原生测试观察与执行顺序，Cargo、crates、fuzz、vendor和bench源码/锁文件与fd2一致；
该扩展结果按此模块等价关系使用，明确保留原生产者SHA，不冒称在B5源码上重新执行。
最终普通Windows Check/Coverage/Dependencies仍在B5精确源码运行。

Bench为同runner纯内存阶段，4项各7×20,000 work，保留全部56样本，不测ETW或SQLite。
fd2的lifetime中位耗时517.390→332.785ns/work，历史85ca同项351.845→486.665ns/work，
方向相反。两个源码生产模块相同；没有依据宣称稳定性能提升或退化的具体原因。
这两组观测均保留，不能选较好结果作为吞吐提升证明。原始安全汇总在各run扩展artifact。
