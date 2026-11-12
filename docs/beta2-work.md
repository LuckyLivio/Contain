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
| A：离线/原生质量及冻结ZIP验收 | 进行中：A1编排失败，保留后修正目录创建顺序 |
| A：满足全部门后合并、main CI、Prerelease和公开回下载 | 待做 |
| B：独立分支有界窗口观测/离线不变性和开销 | 待做；A完成或明确阻塞后开始 |
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

## B边界（尚未开始）

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
