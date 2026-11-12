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

## B5：预定负载失败，保持草稿并交付诊断包

[run 43198739683](https://github.com/LuckyLivio/Contain/actions/runs/43198739683)，
冻结源码`4161c4225e419d2baa51a3331d4efea8009d277f`，
ZIP SHA-256 `9c53e207e7c126d3c80e149b89db864d054392446f60331e333d29d9de35d709`。
四次ABBA均完整执行，无重试；第二候选接收队列溢出3,843、候选丢失7,624，
分别计数，不相加。两基线及第一候选该两项均0；四次ETW事件/缓冲丢失、交付、
原始保存及解码损失均0。父/子独立内容均成功，失败样本保守Unknown，无关误归因0。
精确会话保存冷读、独立背景36,000成功操作/次及全部自有进程退出均通过。
WPF按门跳过，未执行交互目录或新GUI完整流程。

初始快照预算未耗尽，候选两次STOP/join均约数毫秒、收尾1,500/1,501ms；
全程累计回调/锁等待不能解释瞬时接收停顿。未开启profile，不能猜测具体接收阶段。
[完整分项、交付与剩余限制](beta2-diagnostic.md)和[脱敏聚合JSON](examples/beta2/B5-diagnostic-summary.json)
保留所有失败及分母。触发用户停止条件：不合并、不打标签、不发Prerelease，不再新live运行挑绿。

### B5普通质量门的独立测试一致性缺口

[CI 43198739984](https://github.com/LuckyLivio/Contain/actions/runs/43198739984)Rust nextest
408通过/7既有跳过，Coverage/Original/Dependencies/Companion通过。
Check在`test-publish-modern-desktop.py`（27项中1失败）停止：当前native emitter新增
`clipboard_probes`，旧字段审阅fixture没有显式记录。后续29个Python门当时未执行。
修复只在“当前emitter字段审阅”构造包含该空字段的fixture，保留完整字段相等断言，
并另断言历史Beta.1发布器仍拒绝新字段；不改发布器或旧Release契约。
本地最终27项通过；首次直接加到历史fixture的本地错误也保留，未弱化门槛。

后续仅测试fixture、README及本交付记录变更，冻结ZIP保持原字节/源码身份；普通CI验证
新分支HEAD，原生B5失败不会因这项测试修正而被清除。

## 本轮独立阶段 B1：构建身份人工否决，停止本轮调查

本节指用户将体验交付A与观察调查B拆分后的**唯一一次B原生实验**；上方历史B1–B5原样保留。
调查[PR22](https://github.com/LuckyLivio/Contain/pull/22)与A交付分支独立，未将观察代码混入A。

- 冻结源码：`072829579bf7f373a0f6eef71dfd842de72a70a8`；
  [唯一dispatch运行43215661372](https://github.com/LuckyLivio/Contain/actions/runs/43215661372)，attempt 1。
- 公共观察补丁 SHA-256：`98c145d06428b7042b46ad880254222afa7b07422c9f0febcefb075e45218cd6`；
  完整书面计划 SHA-256：`f55921b1e5dfc1f3951a9d70d757cadd4df0a35a790d67c3297709b07a6c1117`。
  源码、补丁、计划、统一依赖锁及toolchain摘要已独立核对，保留输入等价与Bench的原始范围。
- Windows `10.0.26100.0`，系统/进程均X64；MSVC x64 Rust/Cargo 1.99.1，核心debug。
  两侧被编排为重建并启用相同profiling，均不是原发布ZIP；统一安全SQLite依赖。
  构建命令退出0不足以证明两种行为均被实际重建，最终字节核对已将该假设否决。
- 预定四次顺序与输入完整执行、零重试。自动emitter输出`not_reproduced`、`measurement_valid=true`。
  **人工审查将最终结论判为`measurement_or_test_failed`**：回下载的两个CLI实际逐字相同，
  都是13,059,072字节、SHA-256
  `95d9d838fec94de3c879c08d8ba88a808a92881feaa732d1ef5f5c2fc1e66d77`。

声明的基线行为源码为`f76a8618426945093eacdd5751d49d2633635e78`，候选行为基底为
`347a86ee730c4fd2d9ee775add60a6c5deddf7fa`，二者确有9个采集/模型等路径差异。
公共观察补丁及名义构建通过不足以证明下载的相同字节来自两种不同生产行为，故不能认定
有效ABBA对照或选择其中一侧作为原因解释。共享`CARGO_TARGET_DIR`的构建产物复用是疑点，
现有公开构建聚合不提供确定根因；不把这一假设写成已经证实的缓存问题。

以下仅按**执行槽位**保留同一实际二进制的观测，不归因于两种行为差异：

| 槽位（保留原计划标签） | 实际回调 / 收到 | 队列高水位 | 最大记录排队年龄 | 最大窗口前 / 后记录 |
| --- | --- | --- | --- | --- |
| r1/baseline | 684,154 / 216,005 | 343 | 49.7804ms | 0 / 64 |
| r1/candidate | 683,313 / 216,391 | 388 | 56.5408ms | 0 / 64 |
| r2/candidate | 679,689 / 216,409 | 286 | 39.7231ms | 0 / 64 |
| r2/baseline | 679,932 / 216,478 | 392 | 47.1182ms | 64 / 64 |

四槽ETW事件/缓冲、应用接收、候选、上下文、交付、原始保存/配额和解码丢失均为0，
收尾均未超时；各36,000次背景操作、父/直接子/无关目标独立真值、原严格oracle、归因负例、
8项会计守恒、保存准确会话冷读及所有自有进程退出均通过。原始失败和未解决问题不会因此清除。

四槽都没有记录到首次1,024阈值积压或首次溢出标记，因此没有可用于解释历史溢出的首次
触发因果窗口。前三槽最大窗口没有前置记录，其depth还包含历史source high-water：
触发观察行的pending前/后分别为0/0、11/0、20/0，不能将该receiver观察时刻当成历史峰值
发生时刻。第四槽有64条前置及64条后置记录；最大`receiver_idle_wait`墙钟14.2865ms，
这是命名等待阶段，不证明某线程暂停。`receiver_follow_classify`一次墙钟2.8933ms对应
CPU计数增量15.625ms，其余多数CPU值为0，体现Windows记账量化边界；不能用wall−CPU推断
暂停或把0解释为没有CPU工作，也不能相加重叠阶段。

本轮不能提出有证据的最小产品修复。具体缺口是**不同二进制与两种行为源码的可靠绑定**，
以及历史首次积压/溢出触发前后的有效receiver阶段窗口。调查按“测量或测试失效”完成，
不再新增live、重跑、dispatch、合并或发布；后续产品修复需另定范围和验收。

[人工审查白名单摘要](examples/beta2/receiver-backlog-B1-review.json)保留自动输出与人工否决的
区别，以及四槽全部分项和最大窗口阶段样本。未修改原emitter的`result.json`；完整原结果在
[原安全证据artifact](https://github.com/LuckyLivio/Contain/actions/runs/43215661372/artifacts/13942638657)。
[固定诊断二进制及公共补丁](https://github.com/LuckyLivio/Contain/actions/runs/43215661372/artifacts/13943722617)
保留供核对，明确不是A的发行包，也不提供原主机可靠性保证。

随后只对原安全产物离线重解析，新增身份门给出`measurement_or_test_failed`、
`comparison_identity_valid=false`、`baseline_candidate_binaries_identical`；不新增采集。
原artifact中`result.json`仍为原字节，SHA-256
`d28aee3aa503cdb24bb43652fff55b589af02492960f328871dff0cca12a06a6`，
其自动成功字段只作为历史emitter输出保留，不能作为最终审查结论。

调查工具随后补充每侧独立且全新的私有Cargo构建目录，并在任何live前拒绝两侧核心SHA相同；
最终汇总也增加相同二进制的否决。真实B1身份fixture的离线回归通过，原安全产物重解析
正确判为测量失效。此后的工具修正未再执行Windows构建或采集，不能反向证明原对照有效。
