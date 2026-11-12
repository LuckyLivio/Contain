# v0.4.0-beta.2：体验交付与独立调查

历史入口[PR20](https://github.com/LuckyLivio/Contain/pull/20)及B5失败保持原样。
本轮A从main `ddafeb26a89ea7bf625421e6f958b3e8ca49c31b`创建
`fix/beta2-ux-delivery`。不迁移PR20的follow/ingest/drain/ETW行为或配套schema。
A的发布不以B查明原因作为条件。

| 工作 | 状态 |
| --- | --- |
| 核对main、PR20、Release及未提交改动 | 通过：main/PR20未推进，工作区干净，beta.2不存在 |
| A：按依赖拆分UX及回归 | 通过：只迁桌面/测试；核心采集/schema边界零diff |
| A：必要SQLite安全升级的独立审查/提交 | 通过：独立提交925d713，离线兼容/版本回归通过 |
| A：离线/原生质量及冻结ZIP验收 | 阻塞并停止本轮A原生尝试：A4压力出现15,381条ETW来源丢失；小窗解释的完整控件可见断言也失败 |
| A：满足全部门后合并、main CI、Prerelease和公开回下载 | 阻塞：PR21保持草稿，不合并、不发布；仅交付标明失败的诊断ZIP |
| B：独立分支有界窗口观测/离线不变性和开销 | 进行中：A2具体测试阻塞已留证，另建diag分支；不混入A |
| B：冻结一次同profiling的ABBA，四次、零重试 | 待做 |
| 队列/候选容量、归因/过滤/截止或架构改造 | 本轮不做 |

## A实施前固定验收

- 导出：本页与全部匹配两个入口；记录名+UTC开始时间+短会话ID+类型+.html，
  非法/保留/空/中文/长名，缺时间不造假；冻结上下文覆盖文件名、报告、查询、完整条数/顺序。
  真实保存框默认值及HTML扩展，取消、已有目标保护、失败和临时提交规则。
- 事件与状态：混合页名及进程/写入/完成区分；准确ID选中、解释、复制；保留原始操作/JSON。
  完成/整理中/等待保存/失败、Incomplete、非零退出、缺字段与真实零值分别表达。
- 数据与使用：隔离个人库、外部库只读及历史兼容、同名准确UUID、快速切换、搜索分页；
  一次明确开始的受控GUI安装、安装器自身窗口选目录、独立父/直接子/无关真值、保存、退出重开同事件。
- 布局：既有1024×720及820×640检查、Space实际激活/焦点检查、保存框和原始窗口截图；
  实测DPI逐图记录。没有进行的实际高DPI/模拟缩放不计为通过，也不伪造新验收。
- 质量：现有Check/Coverage/CoverageOriginal/Dependencies/Companion，CLI/schema/迁移/只读/备份/WAL；
  对实际依赖变化核验公告、许可与链接SQLite。无parser/归因改动，不无关扩展fuzz矩阵。

Windows先构建并冻结一个ZIP，两个独立Windows作业下载同一个ZIP并核对其SHA/清单：
1. WPF完整验收：最多一次真实受控交互安装。
2. 采集/压力：一次既有独立夹具，12,000背景文件、36,000成功写/重命名/删操作；
   使用既有严格oracle、准确父子实例/完成、无关负例、零损失及保存/冷读门。
两项零重试；一个失败不跳过另一独立作业。质量、WPF及压力均通过才允许发布。
发生新失败先保留并分析，只有明确代码/测量修正或可说明环境变更才启动新尝试。
发布使用被验收的原ZIP，不重建换包；A包含安全依赖升级时明确核心被重建。

## B边界（独立分支）

只观察首次积压/溢出附近的固定容量单调时间窗口：队列、批次、生命周期/身份、
候选处理/拒绝原因、输出交付、锁等待。区分墙钟/CPU/排队等待，不以累计均值归因。
先离线验证开关不改变事件集合、顺序、归因、损失语义、截止和容量，并量化开销。
之后冻结同runner/工具链/架构/构建配置/相同profiling的一轮ABBA（4次、零重试）。
重建的旧行为基线如加profiling必须明确身份；优先统一必要安全依赖，保留所有混杂。
调查结果只能是：复现有有效窗口、未复现并给最大窗口、或测量失败。完成一次即停止，
不自动合并/发布，不追加产品改造。

## A安全依赖审查

main使用rusqlite0.37/libsqlite3-sys0.35的bundled SQLite。
[RUSTSEC-2026-0459](https://rustsec.org/advisories/RUSTSEC-2026-0459.html)仍影响该版本，
外部库入口会直接查询用户所选DB/WAL，故保留升级，不恢复受影响依赖。
最小升级为rusqlite0.41.1/libsqlite3-sys0.39.1（SQLite3.54.1），只同步必要的
hashlink/hashbrown/foldhash；许可MIT、MIT/Apache-2、Zlib符合现有deny，最高MSRV1.89。
显式保留native cache、backup及checked unsigned SQL转换，不启用无关WASM后端。
[SQLite3.54.1官方来源校验](https://sqlite.org/releaselog/3_54_1.html)对应bundled sqlite3.c
SHA3-256 `e2b869d3003b33c942b7dd7063604fbc53553e1b66609570df39b9b7b558ee81`。
[SQLite3.54.0](https://sqlite.org/releaselog/3_54_0.html)还改变Windows非递归mutex实现，
不能称核心完全未变或据此宣称性能改善。已有DB/WAL/备份/只读回归及A实际ZIP采集压力门
覆盖这项必要变化；不引入存储schema迁移。

## A尝试记录

| 尝试 | 源码 / 运行 | 结果与下一步依据 |
| --- | --- | --- |
| A1 | `1d69fc1df731bbe05557010aee929765eefe9f4f` / [43205873863](https://github.com/LuckyLivio/Contain/actions/runs/43205873863) | Rust构建完成；workflow预创建tools/fixtures触发生成器拒覆盖。未冻结ZIP，WPF/压力未执行。仅取消预创建，保留生成器保护；不是产品采集失败。 |
| A2 | `69820241e2f64336a87fe0867be8bc58b209fc1b` / [43206472661](https://github.com/LuckyLivio/Contain/actions/runs/43206472661) | 目录修正有效；core/fixture、模型、WPF/harness构建通过。新增聚合测试默认CP1252读取中文Program.cs失败，未冻结ZIP/未采集。改显式UTF-8，断言保留；普通CI同一失败一并修复。 |
| A3 | `f76a8618426945093eacdd5751d49d2633635e78` / [43208532124](https://github.com/LuckyLivio/Contain/actions/runs/43208532124) | 同一冻结ZIP的WPF、独立压力和最终自动汇总全部通过，实际链接SQLite为3.54.1；人工审图发现长名称在820×640小窗挤没事件列表与解释正文，既有检查只确认搜索/分页按钮可见，故不发布。自动成功结果和人工失败均保留。 |
| A4 | `3f3a6e183c339c970b18bb4b74639152ef10052a` / [43211340696](https://github.com/LuckyLivio/Contain/actions/runs/43211340696) | 根据A3实图修复长名称标题并补正文/列表/复制可见性检查，重新冻结新包。两个独立Windows作业均留下结果：压力有15,381条ETW来源丢失；WPF在小窗解释检查失败。最终汇总拒绝通过，停止本轮A原生尝试，不发布。 |

A3原ZIP为`contain-0.4.0-beta.2-windows-x64.zip`，70,103,800字节，SHA-256
`5140b1367359219ecea3c9fda373092f708dd0c40e1e821bf7f3d82dd8795381`，
已回下载核对SHA、CRC、完整清单及构建身份。原包源码和核心源码均为上述A3 SHA，
UI Release、核心debug、.NET运行时10.0.14。缺陷原图为WPF证据中的`compact-events.png`，
实测96 DPI；不是模拟缩放。A4若更改二进制或运行配置，不继承A3的原包验收结果。

A3同时把既有inspect文档的`backend.stream.sqlite_version`白名单字符串纳入负载摘要，
严格核对锁定的3.54.1，不增加core/schema字段或采集次数，落实既定实际链接版本验收。
B在A2明确测试阻塞后另建独立观测分支；A的继续验证与发布不等待B结论。

## A4最终阻塞与诊断交付

[PR21](https://github.com/LuckyLivio/Contain/pull/21)保持草稿。A4的来源丢失是独立的
必需门失败；本轮不再派发A原生验证，不用A3成功样本替换A4，不改容量、归因标准或门槛。
B继续在独立分支执行原定有界调查，未混入A，也不自动解除A的发布阻塞。

| A4压力分项 | 实际结果 |
| --- | --- |
| 固定负载与退出 | 12,000背景文件写/重命名/删共36,000次成功操作；父/直接子/无关写入的独立内容真值成功，安装器及采集器退出码0，所有自有进程退出 |
| 事件量 | 1,045,866回调，339,839收到，2,749时间线观测，23规范化源操作；四者口径不同 |
| 来源丢失 | ETW EventsLost **15,381**，缓冲区丢失0；缺少该候选的分阶段来源快照，不能定位发生时段或逐条原因 |
| 应用各层 | 接收丢失、候选/上下文丢失、交付丢失、原始保存/配额丢失、解码错误均0；队列峰285，最大排队延迟40.688ms |
| 保存与收尾 | finished、保存及准确会话冷读成功，8项会计守恒通过；`drain_timed_out=false`，覆盖Incomplete |
| 目标与负例 | 父/直接子写入请求各保留2条，因EventLoss保守保持Unknown，可靠归因门失败；无关和背景操作误归属0 |
| SQLite | 从实际保存文档读到链接版本3.54.1，版本门通过 |

这次实际观测到来源层损失，未复现B5的应用接收丢失；不能据此认定原Parallels会话的
192,055条ETW丢失有相同根因，也不能把来源计数归入应用队列或候选损失。

A4 WPF自动结果为7组通过、1组失败、6组未运行。已执行同名会话、搜索分页、解释复制、
两种导出及取消/覆盖保护、外部只读等前置检查。实图`compact-events.png`确认长标题已
单行省略、首条事件完整可见；失败图中解释正文顶部与PID/创建时间可读，但后文被视口裁剪。
测试的`VisibleAsync`要求整个控件边界落在主窗口，因而这个复合断言失败不能单独证明
会话身份错误或解释完全不可达；本次未豁免该失败，也未完成后续复制可见性检查。
`compact-explanation.png`未生成。

本包尚未执行保存记录冷启动、GUI默认配置、GUI开始/保存准确UUID、真实GUI父子真值、
真实事件解释导出及冷重开、完整源库/包不变检查这6组；不能继承A3的对应结果。
两个Windows作业各自的最终包字节校验通过，未修改受测二进制。现有截图均为96 DPI；
实际高DPI及模拟缩放没有新增通过结论。

A4普通质量检查[push 43211340578](https://github.com/LuckyLivio/Contain/actions/runs/43211340578)
与[PR 43211351411](https://github.com/LuckyLivio/Contain/actions/runs/43211351411)均首次通过：
Check/Coverage/CoverageOriginal/Dependencies/Companion及Windows只读CLI检查完整执行。
这是质量证据，不替代上述两个失败的原包验收门。原生运行系统为Windows 10.0.26100、
系统AMD64、采集进程64位；未验证仿真执行情况，不推测为任何原用户机器的环境原因。

可[下载A4诊断ZIP产物](https://github.com/LuckyLivio/Contain/actions/runs/43211340696/artifacts/13942160616)
（需登录GitHub；解开产物外层后再解开内层ZIP，产物保留至2027-02-10）。
这是可运行、**未获发布验收**的候选，不能标为可靠性修复完成版或Prerelease：

完整解压内层ZIP后运行`Contain.Viewer.exe`，可先在隔离目录用“打开外部记录库”只读
查看合成样本及导出行为；不要求用户重复安装。README继续指向已发布Beta.1，不添加尚不存在的Release入口。

- 内层文件：`contain-0.4.0-beta.2-windows-x64.zip`，70,103,890字节。
- 源码/UI/核心构建来源：`3f3a6e183c339c970b18bb4b74639152ef10052a`。
- ZIP SHA-256：`57a4d05223434c1c7bc0f8a800ad277d0cdc7b43097fb8fc8f1d720038ecd759`。
- 核心EXE SHA-256：`64ad8f7e4cdd079cb81e935f9d96b1d33ea669aff219ea27ab0efaeeb6ede4d4`。
- UI Release、核心debug、Windows x64；SDK10.0.403，自包含.NET/Windows Desktop10.0.14。
  已回下载核对SHA、CRC、完整成员清单、运行时及构建身份；未重建换包。

保留原始安全聚合与截图：
[压力证据](https://github.com/LuckyLivio/Contain/actions/runs/43211340696/artifacts/13940359388)、
[WPF证据](https://github.com/LuckyLivio/Contain/actions/runs/43211340696/artifacts/13942415738)、
[最终拒绝通过的汇总](https://github.com/LuckyLivio/Contain/actions/runs/43211340696/artifacts/13941142976)。
公开材料只含受控夹具及有界统计，没有用户个人数据库、完整原始载荷或未经审查的真实用户报告。
