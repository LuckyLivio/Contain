# 第一阶段验收记录

代码验收修订：`5c6e4027b2fa6afe8c5a9fe3835880b4f7252b0b`，基于
`main` / `ddafeb26a89ea7bf625421e6f958b3e8ca49c31b`。之后的证据提交只增加文档及
本目录引用的报告，不把其他分支历史结果算作本分支测试。

本轮机器为 **Linux x86_64**。没有干净 Windows，也没有派发远程原生测试；当前结果
支持代码审阅和可移植开发，**不代表 Windows 源码恢复或免开发工具交付已验收**。
工具为 Rust/Cargo 1.100.0、.NET SDK 10.0.403、PowerShell 7.5.4（仅语法解析及尝试
质量入口），fuzz 使用 nightly 1.102.0-nightly / `5f4effd57`、cargo-fuzz 0.13.2。

## 已实测

| 检查 | 实际结果 | 范围 |
| --- | --- | --- |
| `cargo test -p contain-workflow -p contain-offline --locked` | 新 CLI **27 通过、0 失败、0 忽略**；旧离线 **98 通过、0 失败、8 忽略** | 新 CLI 的环境缺失/版本不符/恢复后执行真实夹具命令、输入验证、报告脱敏、失败与未实测分离、ZIP 和对照门禁；旧生产存储/迁移/视图模块回归 |
| `cargo build -p contain-workflow --locked` 及 `--release` | 通过 | 实际可运行的 Linux CLI；没有把它当成 Windows EXE |
| `cargo check -p contain-workflow --all-targets --target x86_64-pc-windows-msvc --locked` | 通过 | Windows 源码及 Windows 专用测试可编译检查；**未链接、未执行** |
| `cargo fmt --all -- --check`、新 fuzz workspace 格式检查 | 通过 | Rust 格式 |
| `cargo clippy -p contain-workflow -p contain-offline --all-targets --locked -- -D warnings` | 通过 | 旧 offline manifest 另有 chrono/tracing 未使用依赖提示；未当成零警告记录 |
| `dotnet run --project desktop/Contain.Desktop.Model.Tests -c Release --no-restore` | **34 通过** | 合成模型检查，非 WPF 原生验收 |
| `dotnet run --project desktop/Contain.Viewer.Presentation.Tests -c Release --no-restore` | **17 + 8 + 13 = 38 通过** | 投影/诊断状态，不证明原生窗口或 ETW |
| 38 个已有 `scripts/test-*.py` | 全部退出 0；unittest **612 通过、6 跳过** | 另有 Windows 目录身份的 **7 项全部未实测**，不能据脚本退出 0 说它们通过 |
| 新 CLI / 新 fuzz workspace 分别 `cargo deny --manifest-path … --config deny.toml --locked check` | advisories / bans / licenses / sources 均通过 | 仅各自锁定依赖闭包；没有新增许可或公告忽略规则 |
| 新输入 fuzz smoke | **643,621 次 / 31 秒，0 crash** | 清单、包规范、Windows 路径、PE 边界；仅短期 smoke，不是完整 ZIP/loader 安全证明 |
| 两份新增 PowerShell 脚本 | PowerShell Parser 语法检查通过 | **未在 Windows 执行** |

旧 offline 的 8 项忽略为 5 项 Windows checkpoint 实验及 3 项显式 opt-in 磁盘性能实验。
新/旧 doctests 均为 **0**，没有当成有效功能测试。没有新增热路径性能承诺。

Linux 覆盖（cargo-llvm-cov 0.9.1）：新 CLI **77.46% 行、83.10% 函数**。
逐文件和分母见 [summary.json](evidence/phase-one/summary.json)。这是 Linux 条件编译范围，
不含 `cfg(windows)` 的窗口探测、系统架构、Windows 版本及原生执行分支，不是分支覆盖率，
也不是原 Windows workspace 的覆盖结果。已查看受影响文件：artifact 约 92.7%、manifest
约 91.9%，package 约 64% 和 main 约 56% 的剩余项主要为原生运行、解压/输入错误、CLI
其他分派等路径；未靠忽略生产文件或降低断言补足。Windows 原生和错误 I/O 分支仍待验证。

## 用实际旧 Beta.1 ZIP 验证新流程

下载的实际发行 ZIP SHA-256 为
`953b243bb3dd8ea32e61dcf5fdeaf143b9e697ea4a57021fb85ffd961ef907e3`，
与 [历史发布文档](beta04-release.md) 一致，未用重新打包文件代替原包。
入口 EXE SHA-256 为
`2d0c0af61f2fcda3bdb599e9d94cb84da143c9133f9a32922f9c6f7443fe0eb2`。

- [原包静态报告](evidence/phase-one/beta1-static.md)：ZIP 身份、入口 x64 PE 头、必需文件、
  包内自检入口和 `CONTENTS.json` 实际文件集合/尺寸/哈希检查通过。窗口与指定测试均为
  **未实测**，CLI 退出 **2**，没有报告原生验收通过。
- [故意删去 coreclr.dll 的报告](evidence/phase-one/beta1-missing-coreclr.md)：为坏包固定其
  **自身新 SHA-256**，包身份检查仍通过；必需文件和 CONTENTS 检查失败，启动/测试
  都为未实测，未创建运行目录，CLI 退出 **1**。这是成功检测静态失败的负例，
  不是一次“启动失败”的原生观测。
- [Linux 源码环境检查报告](evidence/phase-one/source-linux.md)：正确记录本机并非 Windows、
  Windows 专用工具探测不可用；不据此猜测一台实际 Windows 机器缺什么。

报告 JSON 与同名 Markdown 一并提交；不提交 ZIP、EXE、数据库、原始主机资料或环境值。

## PR #22 回归证据

[原记录与来源](phase-one.md#拒绝相同-exe-的不同构建对照)明确保留历史人工否决；
原 artifact 下载返回 403。本轮使用可重建的**合成 PE stub**，不是原始 EXE。
两个 ZIP 标签和字节不同，内含 app.exe 字节相同：

- [比较身份报告](evidence/phase-one/pr22-compare.md)：记录两个实际 ZIP 哈希及相同 EXE 哈希，拒绝不同构建对照。
- [与运行绑定的报告](evidence/phase-one/pr22-before-run.md)：comparison_identity=failed，launch 和指定测试均为 not_run；运行目录未创建。
- [夹具来源](evidence/phase-one/pr22-provenance.json)：synthetic=true、historical_exes=false、native_execution=false。

测试还覆盖：桌面入口字节不同、真正声明要比较的 core.exe 相同的情况；使用
spec.comparison_entry 指定核心后同样在开跑前拒绝，不能用 GUI 变更掩盖相同核心。

## 未通过与待实测

**仓库级依赖门失败**：`cargo deny --locked check` 拒绝 `main` 继承的
`libsqlite3-sys 0.35.0` / `rusqlite 0.37.0`，公告 `RUSTSEC-2026-0459`。
新 CLI 不依赖 SQLite，其独立依赖门通过；旧核心升级存在于其他 Beta.2 分支，未擅自
合并、重写或增加公告忽略。此项仍阻止宣称整个仓库质量门通过。

**仓库级 Check 在 Linux 失败**：实际运行 `quality.ps1 -Mode Check`，rustfmt 通过后
workspace Clippy 因旧核心 winreg 不支持 Linux 而失败（exit 101）；后续 nextest、doctests
和 Windows PowerShell harness 没有因此被计为通过。Windows 全量 Check、Coverage、
Dependencies、既有 ETW Fuzz 门仍未实测。本轮只执行新独立输入目标的 fuzz smoke。

按 [干净 Windows 步骤](phase-one.md#干净-windows-验收步骤待执行) 继续：

1. 快照 A：真正缺少/不匹配工具链的原始报告；补齐后的实际 Rust/WPF 构建及测试。
2. 独立 CLI 的 Windows 链接、静态 CRT、许可打包脚本及无开发工具启动。
3. 快照 B：同一 ZIP 的 Windows 版本、原生与进程架构、准确窗口及指定自检结果。
4. WPF 故意缺件包在 Windows 上的门禁，以及原生测试 EXE/窗口探测的实测。

Beta.1 的既有发布验收通过仍属于历史事实。Beta.2 候选能运行，但指定验收没有全部
通过，不能改写成通过。发布和合并留待评审，本分支未执行这些操作。

本机完整日志保留在忽略的 `target/quality/phase-one-local-logs` 及其他 phase-one 输出目录；
提交的 [汇总](evidence/phase-one/summary.json) 仅包含白名单聚合值、必需变量名称和公开包身份。
