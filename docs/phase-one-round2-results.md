# 第二轮：源码交付与干净 Windows 验收准备

上次审阅基线 `c0ea857efaa8d9cdf6fd6a735cb35f87cdcab9d8`；开始时远端仍是此提交，
工作树干净，main 未前进。继续原分支，没有重写已有工作。
代码验证修订：`51172ff2c1f93c11e5d95151e55508c0a9a2e8cb`；后续提交仅补充文档和证据。

**当前结论：可继续代码审阅，不具备可合并条件。** 缺少干净 Windows A/B 实测，
仓库级 SQLite 依赖门仍失败。普通 Windows CI、编译检查和合成夹具没有替代快照验收。
没有合并、发布或手动 dispatch 远程原生测试；普通 Windows CI 本轮未获得可核验结果。

## 交付的代码

- `scripts/build-source-delivery.ps1`：从干净提交、全新构建目录执行 Rust release 构建/
  回归、WPF publish、模型回归、生产模块合成 fixture，然后冻结两份交付包。
- `scripts/package-source-desktop.py`：明确接收本次 core/WPF、构建收据、选中工具链和
  许可证路径。复用历史运行时、样例和 CONTENTS 契约；新的 BUILD 协议记录当前源码与
  两个实际 EXE 身份，外部 identity 将其与 ZIP/spec SHA-256 绑定。无历史核心回退。
- `workflow-toolchain.ps1` / `select-workflow-toolchain.ps1` 与 Rust toolchain 模块：
  选择完整 MSVC patch/SDK，核对 cl/link/rc 解析，传递环境和明确 linker/CC/CXX 给实际
  构建；拒绝 active 版本冲突。CLI 和核心使用单次独立构建目录，请求静态 CRT。
- Rust `architecture`：IsWow64Process2 的 native/process/emulated 记录与未知状态；
  不再以 GetNativeSystemInfo 或编译目标冒充探测结果。增加 `platform` 命令。
- 运行结果：可选 stdout_sha256，使指定自检同时核验退出码和实际输出哈希，stdout
  内容不进入报告；保留 PR #22 在启动前拒绝相同实际 EXE 的门禁。
- 接收机脚本：探测开发工具缺失证据、校验 A 的两份 ZIP/spec 与实际 verifier 身份，
  分别执行 CLI、桌面、自检与重新固定坏包自身哈希的缺件负例；未执行项显式 not_run。

历史 Beta.1/Beta.10 打包入口没有改动，新路径没有继承其验收。WPF 功能、陌生软件
试用和配置回退未扩展。新包运行时/许可证完整性与静态 CRT 仍需要 Windows loader 实测。

## 当前机器实际执行的检查

本机 **Linux x86_64**，Rust/Cargo 1.100.0、.NET SDK 10.0.403、PowerShell 7.5.4；
fuzz 为 nightly 1.102.0-nightly / cargo-fuzz 0.13.2。

| 检查 | 结果 | 边界 |
| --- | --- | --- |
| `cargo test -p contain-workflow -p contain-offline --locked` | 新 CLI **4 单元 + 29 集成 = 33 通过**；旧离线 **98 通过、8 忽略** | 新架构解码、错误工具链拒绝、环境继承、输出哈希、旧迁移/视图与相同 EXE 门禁；零 doctest 不算功能通过 |
| `cargo check -p contain-workflow --all-targets --locked --target x86_64-pc-windows-msvc` | 通过 | 只编译检查，**未链接、未运行** Windows EXE/API |
| Linux CLI debug/release、rustfmt、portable Clippy | 通过 | offline manifest 仍有 chrono/tracing 未使用依赖提示，没有写成“完全无警告” |
| 新来源打包契约 | **8 通过** | 合成输入/模拟 native sample 调用；不是实际本次 Windows ZIP |
| 历史 modern packager | **22 通过** | 包含旧核心固定哈希与来源约束，未放松历史规则 |
| 全部 39 个 Python 回归脚本 | 全部退出 0；unittest **620 通过、6 跳过** | 另有 Windows 目录身份 **7 项全部未实测**，没有按退出码算通过 |
| PowerShell 解析与策略 | 脚本语法通过；工具链 **6**、接收策略 **3** 合成夹具通过 | 多版本选择、错误 SDK/MSVC、退出码不被 stdout 污染、缺项证据/脱敏；没有声称实际选择过 Windows 工具链 |
| 桌面模型/投影回归 | **34 + 38 通过** | 可移植合成模型，不是原生 WPF 运行验收 |
| 新输入 fuzz smoke | **689,302 次 / 31 秒、0 crash** | 清单/包规范/路径/PE 边界短期 smoke，不证明 Windows API 或 loader 正确 |
| 新 CLI 独立 cargo deny | advisories / bans / licenses / sources 通过 | 不代表整个 workspace 依赖门通过 |

新 crate 的 Linux 条件编译覆盖率为 **77.36% 行、79.81% 函数**，含 inline 单元测试，
不含 cfg(windows) 分支，不是分支覆盖率。已查看逐文件及未覆盖区域：architecture 解码
约 96%、runner 约 86%；toolchain 约 56%、package 约 64%、main 约 47% 尚缺实际
Windows 选择/运行、I/O 故障和其他分派路径。没有忽略生产文件、降低断言或把此范围
写成 Windows workspace 覆盖门通过。[详细分母与汇总](evidence/phase-one-round2/summary.json)。

旧离线的 8 个忽略仍为 5 个 Windows checkpoint 实验和 3 个 opt-in 磁盘实验。新旧
零 doctest 单列。完整本机日志在忽略的 `target/quality/round2-local-logs`；未上传日志中
的本机路径、环境字典或完整主机资料。

## Windows 实测状态与产物获取

当前仅有 Linux 执行环境，没有可用的干净 Windows 快照。没有拿旧版 ZIP、交叉编译
检查或合成 PE stub 冒充本次 Windows 包。以下全部 **未实测**：

1. 快照 A：真实工具缺项；补齐后的实际 MSVC/SDK 选择、Rust/WPF 构建/回归及组包。
2. 本次完整桌面 ZIP 与独立 Windows CLI ZIP：**未产出**，本轮没有相应下载链接或
   声称已确认的最终 SHA。包括静态 CRT/许可收集脚本的完整 Windows 执行仍待完成。
3. 快照 B：从未安装开发工具的来源证据、原生 x64/ARM64 仿真 API 实测、同一 ZIP
   桌面启动、指定自检、坏包执行前门禁，以及独立 CLI 自身运行。

可直接执行的 [完整 A/B 命令](phase-one.md) 已提交。源码机成功执行
`build --execute --delivery-dir <new-dir>` 后，获取：

- `<new-dir>/desktop-delivery/contain-source-windows-x64.zip` 与 `package-spec.json`；
- `<new-dir>/cli-delivery/contain-workflow-windows-x64.zip` 与 `package-spec.json`；
- 两个 identity.json 及 `<new-dir>/handoff.json`，记录同一源码、EXE/ZIP/spec 身份；
- 各步骤退出码 JSON 和本地日志。失败产物保留，但不生成成功交接声明。

[当前 Linux 平台报告](evidence/phase-one-round2/platform-linux.md) 的原生 Windows 门为失败；
[源码检查](evidence/phase-one-round2/source-linux.md) 正确记录 Windows selector 不可用。
它们不是干净 Windows 缺项/恢复证据。架构未知/仿真目前只有确定性单元覆盖，不能提升为
Windows 实测通过。

## 合并阻塞

- 仓库级 `cargo deny --locked check` 仍拒绝 **RUSTSEC-2026-0459**：
  `libsqlite3-sys 0.35.0` / `rusqlite 0.37.0`，其余 bans/licenses/sources 通过。
  已核对修复 `925d713539a48c2794f88e4a6b8409f18bd53a3f`（rusqlite 0.41.1 /
  SQLite 3.54.1 与转换回归）；按本轮要求单独跟踪，没有 cherry-pick 或增加忽略。
- `quality.ps1 -Mode Check` 在 Linux 的 workspace Clippy 因旧核心 winreg 要求
  Windows 失败（exit 101）。后续全量 nextest、原生 doctest/harness、Windows
  Coverage/Dependencies/Fuzz 门未因此算通过。
- 干净 Windows A/B 链条尚缺实际记录，当前不能批准“免开发工具可运行”结论。

后续需要实际执行并审阅上述 Windows 收据，以及单独处理 SQLite 合并阻塞；无需再
扩展 WPF 功能或增加试用/回退功能才能继续这些验收。

## 开发机 Windows 原生首轮构建与本机运行补充（`fix/windows-first-run`）

在原生 Windows 11 x64 开发机（`10.0.26300`，`IsWow64Process2` 确认 `native=x86_64`、
`process=x86_64`、`emulated=false`，实际窗口 144 DPI）使用锁定工具链（Rust `1.100.0`、
.NET SDK `10.0.403`、Python `3.12.10` x64、MSVC `14.44.35207`、Windows SDK `10.0.26100.0`）
完成了产物验证修订 `143443807c634782bb64618daf3e2c344e5f94b1` 的构建、组包与本机运行验证：

- **构建与打包修复**：修复 Windows PowerShell 5.1 原生进程 `stderr` 重定向提前终止
  （`Invoke-WorkflowStep`）、Windows 依赖小写 `license-apache-2.0` 许可收集白名单、Cargo
  注册表源码包早于 1980 年时间戳的 ZIP 写入夹紧、CLI ZIP 子目录正斜杠 `/` 归一化
  （`Write-WorkflowZip`）以及合成样例只读输出探测产生 `-shm`/`-wal` 混入 `required_files`
  的问题，并补充无硬编码私有路径的本地开发桌面启动入口 `scripts/start-local-desktop.ps1`/`.cmd`。
- **冻结产物身份（`source_sha = 143443807c634782bb64618daf3e2c344e5f94b1`）**：
  - `contain-source-windows-x64.zip`：`792a4ac01e47b160713d1b1601e7600f1bf23baec7868e6bf97e09799fecd4a0`
    （`Contain.Viewer.exe`：`5c47e636bccca19886b88054e0d053aa7555fea5fbf1807f917c152533f9bf7f`，
    `contain.exe`：`352676d6feabc40dee4642c15c9c97c799b064ba2c116a4186f9d7aae464af16`，
    `package-spec.json`：`e343e79efadd950063026d300ba09ee7e9e9842974bd0bffd5ebd856b940215c`）。
  - `contain-workflow-windows-x64.zip`：`f94afc577339122668c91c5e996bf0889b8ecc04a845bc89ff27a4d3b2ecbe08`
    （`contain-workflow.exe`：`6703e86f1ffea35c34be8ea04936318610c3d9ca15764ae4c872e36c7323b5af`，
    `package-spec.json`：`057e352be1ebd2ce558ccbafdd1f407a3e12e7be2e1f2207964583d5dd40f8c4`）。
- **开发机本机验证**：独立解压的 `contain-workflow.exe verify --run` 对 CLI ZIP 与桌面 ZIP
  （含带空格工作目录）均退出 `0`；PE 导入表核验 `contain-workflow.exe` 与 `contain.exe`
  均无 `vcruntime140.dll` 动态依赖。隔离合成库 UIA 检查完成会话读取、事件浏览、关键词搜索、
  依据读取、事件 ID 复制与带空格异目录冷重启（均退出 `0`，未触碰真实个人库），包内模型导出
  本页与匹配 HTML 报告核验通过。工作区 `fmt`、`clippy -D warnings`、`nextest`（427 通过、
  7 跳过）、`llvm-cov`（77.10% 行 / 77.73% 函数，`storage.rs` 97.41% 行）通过。
- **保留边界与合并阻塞**：
  - 仓库级 SQLite 公告 **RUSTSEC-2026-0459 已由 `rusqlite 0.41.1` / SQLite `3.54.1` 修复**；
    但最新 RustSec 库对旧核心依赖 `chrono 0.4.45` 报告 `RUSTSEC-2026-0543`（unmaintained），
    未添加忽略项，仓库级 `cargo deny` 仍失败（独立 `contain-workflow` 不依赖 `chrono`）。
  - 当前会话位于非前台沙盒 Win32 桌面（`GetForegroundWindow=0`），依赖前台焦点的系统
    `#32770` 文件选择器物理点击、合成键盘 `Space` 焦点投递及 `PrintWindow` 可见像素截图未完成。
  - 本机已安装开发工具，不能替代从未安装开发工具的快照 B 干净接收机验收；新 `contain-workflow`
    流程尚未接入 WPF 图形界面。
