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

## 桌面端“检查项目环境”页面与自带 CLI 候选包补充（`feat/desktop-environment-check`）

在保留上轮 `143443807c634782bb64618daf3e2c344e5f94b1` 双原包不变的前提下，于代码修订
`ed5b372e348c8ddbb03584e9eeb02dc966082640` 完成了 WPF 一级页面“检查项目环境”及自带
`contain-workflow.exe` 的新桌面候选包组装与实包验证：

- **功能与安全边界**：
  - 选择文件夹仅只读识别 `contain.environment.json`、`Cargo.toml`、`rust-toolchain.toml`、
    `global.json` 的存在情况，点击“开始检查”后才调用随包 `contain-workflow.exe`（通过
    `AppContext.BaseDirectory` 定位）。
  - `contain-workflow check` 新增结构化证据字段（`outcome`/`required`/`observed`/`remedy`）、
    `--safe-probes` 首版只读探测白名单（超范围命令跳过并标为 `needs_confirmation`，不执行目标
    项目目录中的未审查脚本）及 `--infer-if-missing` 推断模式，优先解析随包
    `workflow/select-workflow-toolchain.ps1`。
  - 页面直接消费 `report.json`，优先置顶展示下一步补齐建议与缺失/版本不匹配项，默认折叠已满足项，
    明确区分“检查出缺项”“信息不足（含待确认项）”“检查程序或配置出错”与“环境已满足”四种结果，
    并在取消选择或快速切换项目时拒绝过期结果覆盖当前项目。
- **候选包冻结身份（`source_sha = ed5b372e348c8ddbb03584e9eeb02dc966082640`）**：
  - 桌面候选包 `contain-source-windows-x64.zip`：`ce89f99c65b6add8360f26e16b9968bbf310502fc172b703e57c9c4e05db1ad6`
    （`Contain.Viewer.exe`：`7e978a7ceb6f7712c9fc853f8482529ecf300ec25463dc76a31194edf75d3ed2`，
    `contain.exe`：`83f97b61eb0c1809e89b6ebe69dd82c9a0782278158a422c40f630f8a3edb44f`，
    随包 `contain-workflow.exe`：`67d7ac5bf00d84b3303dcba553373ef91faad238ebe9fab53568bf3c253c3c6b`，
    `package-spec.json`：`9ee7775e5acb15ab3165dd9c3c2123e4fe9ecd008434a68eb2437af7c35062fa`）。
  - 独立 CLI 包 `contain-workflow-windows-x64.zip`：`60a88880d93d9633eb80bf03e0a4062c75bf5fa9eef4ed962067d6fbf0771873`
    （`contain-workflow.exe`：`01f9f0b83679165d05166c8a5fd09e6ff060f3a54b4b215f1852c4f04e966f26`，
    `package-spec.json`：`fc1708644fdc9c5e7c126cba9dce37003a9a2c98deb54de31b4d10091edaa589`）。
- **实包 `Contain.Viewer.exe` 验收记录**：
  - 双包 `verify --run`（含带空格解压目录及随包 `contain-workflow.exe --version` 输出哈希核对）均退出 `0`。
  - 从带空格外部工作目录启动解压后的 `Contain.Viewer.exe`：
    1. 选择当前可信 Contain 检出：8 项检查全部通过，已满足项默认折叠且展开内容与独立 CLI 报告一致，详细报告可正常查看。
    2. 选择隔离版本不匹配样例（要求 `dotnet 10.0.999`）：准确提示“需要补齐 1 项：项目要求 .NET SDK (dotnet) 10.0.999，当前检测到 10.0.403”与补齐步骤，“重新检查”结果一致，本机已有工具全程未改动。
    3. 带空格异目录启动并快速切换至无清单 Rust 样例：随包 CLI 正常定位，页面标题、推断结果（`信息不足 · 含待确认项`）与详细报告严格绑定当前项目，未被前序慢查询覆盖；同进程切换至“我的记录”可正常打开合成样例会话并正常关闭（退出码 `0`）。
  - 桌面模型回归 **40/40** 通过，`contain-workflow` **37/37** 通过。
- **待验证与保留阻塞**：
  - 当前机器无独立的干净无开发工具 Windows x64 交互环境，对上轮原包及本轮候选包的 `-Stage Package` 免工具快照 B 验收标为**待验证**。
  - 非前台沙盒 Win32 桌面中 `PrintWindow` 返回未合成纯白表面，已丢弃无效图像，不在记录中充当真实截图。
  - 仓库级 `cargo deny` 仍保留 `chrono 0.4.45` 未维护告警（`RUSTSEC-2026-0543`）作为合并阻塞记录。

## 桌面端“重新检查”已解决项反馈与真实 SDK 恢复验收补充（`feat/environment-recheck-diff`）

在完整保留 `delivery-04` 与 `delivery-envcheck-01` 原包及开发机现有工具链的前提下，先用上轮候选包走通真实缺项与并行补装基线，再于代码修订 `81650b12fec9dc262f9cdaa56c751e9cd8c73c94` 交付同窗口重新检查恢复提示与 `.NET SDK` 指引改进：

- **功能与探测改进**：
  - 当目标项目目录存在禁用自动前滚的 `global.json` 导致 `dotnet --version` 非零退出时，`contain-workflow` 回退执行只读 `dotnet --list-sdks` 提取已安装 SDK 版本号，使报告准确输出 `version_mismatch` 与实际已装版本，而非误报“未检测到”。
  - 同一窗口、同一项目且环境声明摘要（`environment_manifest` canonical JSON SHA-256 与声明要求）未变时，“重新检查”仅在新报告确认状态变为 `passed` 后展示“已解决：…”（如 `已解决：.NET SDK (dotnet) 10.0.404`），仍缺项继续置顶；切换项目或修改项目环境要求后自动开始新的比较，不会将降低版本要求标记为已解决。
  - 重新检查过程中若检查程序或配置文件出错，页面保留上一次有效检查结果并同时标明上次有效结果时间与本次失败时间。
  - 对受支持发行段的微软正式 `.NET SDK`（8.0 / 9.0 / 10.0），显示明确版本号与官方通道下载页（如 `https://dotnet.microsoft.com/download/dotnet/10.0`）及用户自主决定是否安装的说明；对项目清单或推断生成的说明显式标注来源；对 `10.0.999` 等测试占位版本保留版本不匹配提示，明确不向用户承诺可从官方渠道下载。
- **候选包冻结身份（`source_sha = 81650b12fec9dc262f9cdaa56c751e9cd8c73c94`）**：
  - 桌面候选包 `contain-source-windows-x64.zip`：`6c6c309ab3b96e95e1d8c64ef028048949f5704af84bcfe4c1a93c7e50afa927`
    （`Contain.Viewer.exe`：`63bf75d587b9cace28bd37ceb5a5d4fc678b72123e02f00c367c6f4155269adf`，
    `contain.exe`：`ae900f174cb20f235b4ccca9b8b6139c52e45d6b2a46552a9ebbd4f94a46d167`，
    随包 `contain-workflow.exe`：`b899fe025d7d360600c28a115cf31de4f6bf4b77ba982e222b59473ee26aad4f`，
    `package-spec.json`：`6c92fa5931989f2d644c3a6ca1195c47532fa75bfe706a3220347ad804b97c83`）。
  - 独立 CLI 包 `contain-workflow-windows-x64.zip`：`d27d53cdb9a518fe75a64c4cab437d6ea3c57b86ac0f63d5665dbaf95a0f21ef`
    （`contain-workflow.exe`：`5f4f214a02b14d0754e501684e0053c219a924d84baf5bfb988174130a499696`，
    `package-spec.json`：`0a8b3833870fde042155936158dffbfc6f217eec0e0e3e49717cbc4ffcf1ee55`）。
- **最终 ZIP 解压 `Contain.Viewer.exe` 实测结果（记录结论：恢复成功）**：
  - 双包 `verify --run`（带空格工作目录）均退出 `0`；桌面模型回归 **44/44** 通过，`contain-workflow` **38/38** 通过。
  - 在独立可回滚 Windows `.NET` 测试环境中（初始仅含 `.NET SDK 10.0.403`），使用固定要求微软已正式发布的 `.NET SDK 10.0.404` 的测试项目（清单 canonical JSON SHA-256 固定为 `606929f9ecdbf32c3ecfa3b7cae019829bf2a06d7980716dd74da98d91a0ad00`，全程未修改）：
    1. 首次检查报告“需要补齐 1 项：项目要求 .NET SDK (dotnet) 10.0.404，当前检测到 10.0.403”，并显示 `.NET 10.0` 官方下载页与清单来源说明，保留首次完整报告。
    2. 在该独立测试环境并行补装微软官方 `.NET SDK 10.0.404`，同一安装位置 `dotnet --list-sdks` 独立确认同时存在 `10.0.403` 与 `10.0.404`，开发机原有系统与工具链目录均未变动。
    3. 在同一窗口点击“重新检查”，新报告确认通过（清单 SHA-256 前后一致），页面直接显示 `已解决：.NET SDK (dotnet) 10.0.404` 及前后版本对比摘要，保留复查完整报告，实测判定为**恢复成功**。
  - 同包进一步核验：切换至 `10.0.999` 样例时清空上一项目恢复记录并明确提示不承诺官方可下载；构造配置文件解析失败后点击“重新检查”，页面完整保留上次有效结果并同时标明上次有效时间与本次失败时间。

## 桌面端“环境就绪 → 预览构建步骤 → 开始构建 → 打开实际产物”补充（`feat/desktop-project-build`）

在完整保留 `delivery-envcheck-02`、`delivery-envcheck-01` 与 `delivery-04` 原包的前提下，于代码修订 `e523101d27a233494a7d76c6f211accbff282725` 在“检查项目环境”页面打通了从环境检查就绪到构建产物定位的完整链路：

- **功能与安全边界**：
  - 页面直接读取并严格校验项目根目录 `contain.environment.json` 生成步骤预览（展示项目名称、工作目录、构建与验证步骤摘要及默认收起的完整命令），全程不调用 `build` 子命令生成预览；项目切换或清单文件变化时自动刷新预览。
  - 仅在环境检查通过、存在明确有效 `contain.environment.json`、无待确认项且最近一次复查未失败时展示“开始构建”；缺依赖、仅有推断清单、含待确认项、清单变更或上次复查失败（保留的绿色结果仅供查看）时隐藏启动按钮并说明需先解决的前置问题。
  - 点击“开始构建”时二次核对当前项目与清单文件 SHA-256 仍与预览及通过检查的版本一致，随后调用随包 `contain-workflow.exe build --safe-probes --execute`，在构建前重新探测阶段继续遵守只读白名单与随包工具链选择器边界，运行期间阻止重复启动并展示实际阶段/步骤进度（不编造百分比）。
  - `contain-workflow build` 支持 `--log-dir` 记录本地步骤日志（`steps.json` 与分步标准输出/错误日志，不写入可分享 `report.json`）及可选安全相对路径 `artifact_dir`/`artifacts` 校验；仅当构建与验证全部通过且确认本次产物目录及文件真实存在时展示“打开产物文件夹”；失败时优先指出失败步骤、具体错误输出、标记后续步骤“未执行”并提供“查看构建日志”。
- **候选包冻结身份（`source_sha = e523101d27a233494a7d76c6f211accbff282725`）**：
  - 桌面候选包 `contain-source-windows-x64.zip`：`c2b1d2a199493b528adec65a8f6916feccc21bbd5ec5beae4db0b1e08555b745`
    （`Contain.Viewer.exe`：`698d192e3c7ef3c1e2096b990cf94765c2676d8567c85c8a2f1fe79ce38dd21a`，
    `contain.exe`：`6110fb0de67d5d4b302e9bb90cc2a47140ae4e170688af9c3c9e7593c0b662ea`，
    随包 `contain-workflow.exe`：`7ab67921868b921ef990bb74de057539a26ddd708e3769e0efd0f61b547fd696`，
    `package-spec.json`：`8444c18db05d60a8dd0f5c6164b716ef4165010b5d9a39f19f37b7443b801b8b`）。
  - 独立 CLI 包 `contain-workflow-windows-x64.zip`：`80d41abd8653514a61051e55ff4560228dcbac9d5bf9bda334732168d18d6ab2`
    （`contain-workflow.exe`：`2bbad5446719692f6e86a31e4048bcb8160f5626ff3e8329cdf37dde0e8f2d2b`，
    `package-spec.json`：`cf0d043a4f53059be46c415d96feb536fd8c13c5db103a7fbdb1414b83bad1a5`）。
- **最终 ZIP 解压 `Contain.Viewer.exe` 实测结果**：
  - 双包 `verify --run`（带空格工作目录）均退出 `0`；桌面模型回归 **48/48** 通过，`contain-workflow` **40/40** 通过。
  - 复用固定 `.NET SDK 10.0.404` 隔离测试环境与声明明确构建及输出 SHA-256 验证步骤的小型 `.NET` 控制台项目完成两项实包验收：
    1. **成功场景**：从尚不存在的输出目录开始，界面预览步骤与展开命令正常，点击“开始构建”生成可执行程序并通过输出摘要验证；“打开产物文件夹”准确定位到实际输出目录，独立运行生成程序得到预定输出。
    2. **失败与同界面恢复场景**：在保留上一步旧产物的隔离副本中注入确定性编译错误（`CS0029`），点击构建后明确报告构建步骤失败并展示具体编译器错误，后续验证步骤显示“未执行”，且不因旧产物存在而误报成功或显示打开产物入口；修正源码后在同一窗口点击“再次构建”恢复通过。

## 桌面端只读“检查交付包”页面与实包三场景验收补充（`feat/desktop-package-check`）

在保留 `delivery-build-01`、`delivery-envcheck-02`、`delivery-envcheck-01` 与 `delivery-04` 原包不变的前提下，于代码修订 `73ed7d02ac76b8a3f9131cfecac565c7649d2579` 在 WPF 导航栏新增“检查交付包”页面并产出新候选包：

- **功能与安全边界**：
  - 用户选择 `.zip` 交付包后，自动查找同目录 `package-spec.json` 并显示采用的清单名称；同目录无清单时提示缺少交付清单并一句话说明其用途，提供“选择校验清单”入口，绝不根据待测 ZIP 临时伪造预期哈希。
  - 从应用所在目录调用随包 `contain-workflow.exe verify` 读取结构化 `report.json`（`schema 1 / package`），选择与检查全程不传 `--run`、不解压且不启动包内程序。
  - 界面将“文件校验通过／失败”与“启动未测试”并列分开展示：通过时列出实际核对的项目（整体 SHA-256、主入口 PE x64 头、必需文件数量、包内 `CONTENTS.json` 与自检程序存在性）；失败时优先提示可处理原因（如“这份清单对应另一个 ZIP”或“缺少 `coreclr.dll`”）并提供“更换校验清单”；详细哈希与技术报告默认折叠；切换 ZIP 时丢弃前序结果，页面始终绑定当前文件。
- **候选包冻结身份（`source_sha = 73ed7d02ac76b8a3f9131cfecac565c7649d2579`）**：
  - 桌面候选包 `contain-source-windows-x64.zip`：`2a1292cd266b9f34d82f8c36866cdbbe09ee0bc20a61913477f6c0d77d18eeda`
    （`Contain.Viewer.exe`：`2ce14e4174482ce38dd4354816823afa9c7334fd22d323851830d47ea0a234f0`，
    `contain.exe`：`063d0ef2ca08a10ab859e7be0b9768f564e4bfaa8ad1b0c72a10c39b97c03385`，
    随包 `contain-workflow.exe`：`efc4dec7eacd30695fb74d834b0e71867ac5a6ba027b7ed57009b8e3a51b8199`，
    `package-spec.json`：`cc0fe4a431e231a96eb484de500f499281327132fc134008e8665189598f5e9e`）。
  - 独立 CLI 包 `contain-workflow-windows-x64.zip`：`68ba0081f89361d96e47fe3bfaed06827b5be70dac1121b5f7f3d5c217fad95a`
    （`contain-workflow.exe`：`5ff1dd8d382319d0f7feeb3b794099e8b62991f9b8e34a22e59b1507f827506e`，
    `package-spec.json`：`ec297a35bac78cfe984d9f31996092683a2ee85955eb83147b6f70b96bdbfc9a`）。
- **最终 ZIP 解压 `Contain.Viewer.exe` 实测结果**：
  - 双包 `verify --run`（带空格工作目录）均退出 `0`；桌面模型回归 **53/53** 通过，导航控件回归 **12/12** 通过，`contain-workflow` **40/40** 通过。
  - 从带空格外部目录启动最终桌面包中的 `Contain.Viewer.exe` 完成三场景与构建回归核验（全部 734 项报告检查条目与独立 CLI 报告逐条一致）：
    1. 选择保留的 `delivery-build-01` 原始桌面 ZIP：自动匹配同目录 `package-spec.json`，显示“文件校验通过”与“启动未测试”，技术详情默认折叠。
    2. 选择移除了 `coreclr.dll` 的隔离副本并继续配对原始清单：显示“文件校验失败”与“启动未测试”，明确提示压缩包身份（SHA-256）不匹配及缺少 `coreclr.dll`，`delivery-build-01` 原包哈希保持未变。
    3. 选择单独放入带空格目录的正确 ZIP：界面提示缺少 `package-spec.json` 并说明其用途，手动指定原配清单后立即完成核对并显示“文件校验通过”与“启动未测试”。
    4. 同窗口切换回“检查项目环境”执行 `.NET` 控制台样例构建回归，显示“构建通过”。
  - 当前机器为已安装开发工具的开发机，未具备独立的从未安装开发工具 Windows x64 接收机环境，干净快照 B 接收机实测仍单列为**待验证**。

## Windows 单文件安装候选包与四场景实测补充（`feat/windows-installer`）

在保留上轮 `73ed7d02ac76b8a3f9131cfecac565c7649d2579` 冻结桌面包（SHA-256 `2a1292cd266b9f34d82f8c36866cdbbe09ee0bc20a61913477f6c0d77d18eeda`）及全部历史候选包不变的前提下，新增参数化 Inno Setup 安装器构建脚本与中文向导定义，产出可直接双击分发的 `Contain-Setup-x64.exe` 安装候选包：

- **构建脚本与安装体验边界**：
  - `scripts/build-windows-installer.ps1` 接收 `-Zip`、可选 `-Spec`/`-Identity`/`-ExpectedZipSha256` 与全新 `-OutputDirectory`（仓库内无硬编码本机路径），先复算输入 ZIP 与校验资料 SHA-256，解压至全新临时目录逐文件核对 `CONTENTS.json`（724 个产品文件）并调用随包 `contain-workflow.exe verify` 做静态核验，再调用本机官方 Inno Setup `6.7.3`（`JRSoftware.InnoSetup`）输出 `Contain-Setup-x64.exe`、`.sha256`、`installer-receipt.json` 与 `installer-summary.md`。输入缺失或哈希不一致时指出具体差异并原样保留原文件。
  - `installer/contain.iss` 与 `installer/ChineseSimplified.isl` 固定 `AppId` 为 `{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}`，默认采用 `PrivilegesRequired=lowest` 安装至当前用户 `%LOCALAPPDATA%\Programs\Contain`（终端用户无需联网安装 Rust、Python、Visual Studio 或 .NET SDK），自动创建开始菜单快捷方式，桌面快捷方式默认不勾选，安装完成页提供“启动 Contain”选项。
  - 运行期保护：关闭强制自动杀进程（`CloseApplications=no`），安装、覆盖更新或卸载前若检测到 `Contain` 窗口或可执行文件正在运行，提示用户等待正在保存的记录完成后正常关闭程序再继续，静默模式下直接拒绝覆盖并保留运行中进程。
  - 数据边界：安装器仅管理程序目录、快捷方式与卸载注册表项；不改动已有便携版解压目录，不递归删除 `%LOCALAPPDATA%\Contain`，也不触碰程序目录外的数据库与导出报告。
- **安装候选包冻结身份**：
  - 安装候选包 `Contain-Setup-x64.exe`：大小 `51,192,226` 字节（约 `48.82 MiB`），SHA-256 `bb9cc0fd4edc7c7054c23e105c6e2418c81c1b2c88b28e7fc99f2999c0303c1b`。
  - 版本标识：`AppVersion = 0.4.0-beta.1`（PE `VersionInfo = 0.4.0.1`），载荷源码修订 `73ed7d02ac76b8a3f9131cfecac565c7649d2579`（来自冻结桌面包 `2a1292cd266b9f34d82f8c36866cdbbe09ee0bc20a61913477f6c0d77d18eeda`），编译器 Inno Setup `6.7.3`。
  - 代码签名状态：**未签名（`NotSigned`）**，未配置代码签名证书，Windows SmartScreen 或系统安全提示按未签名候选包预期表现。
- **最终 `Contain-Setup-x64.exe` 实测验收记录（隔离合成记录库，未触碰真实个人库）**：
  1. **首次安装**：将 `Contain-Setup-x64.exe` 单独复制到独立目录执行安装，确认默认不生成桌面快捷方式、生成开始菜单快捷方式及唯一卸载登记；程序打开状态下再次静默运行安装器返回非零退出码并保留运行中窗口；从开始菜单入口启动 `Contain`，进入“检查交付包”核验已知良好样例包，准确显示“文件校验通过”与“启动未测试”。
  2. **安装内容逐项比对**：将 `%LOCALAPPDATA%\Programs\Contain` 内全部产品文件与冻结桌面 ZIP 按相对路径和 SHA-256 逐一比对，**724 / 724 项产品文件完全一致**（完整保留随包 CLI、WPF 运行库、`workflow/`、`samples/`、`licenses/`、`LICENSE` 与 `THIRD-PARTY-NOTICES.md` 结构）；安装器自身生成的卸载文件单列为 `unins000.exe` 与 `unins000.dat`。
  3. **旧便携 ZIP 用户、跨版本升级演练与同版重装（分别记录）**：
     - 在上轮前冻结便携包（源码 `e523101d27a233494a7d76c6f211accbff282725`，尚无“检查交付包”入口）打开隔离合成会话 `synthetic-finished` 与确定性事件 `synthetic-finished-event-0`；
     - **跨版本升级演练**（注：因历史未公开发布旧 Setup EXE，使用上述冻结旧版桌面包在本地生成的仅测试用 `0.4.0-beta.0-rehearsal` 安装包演练升级到当前候选包 `0.4.0-beta.1`）：原位升级后 Windows“已安装的应用”中仅保留 **1 个** `Contain` 条目（版本更新为 `0.4.0-beta.1`），新“检查交付包”入口正常出现，“我的记录”重新打开同一会话 `synthetic-finished` 与同一事件 `synthetic-finished-event-0`，事件路径与完整依据文本逐字一致，原有便携版解压目录文件未受任何改动；
     - **同版本重装**：在 `0.4.0-beta.1` 已安装状态下再次运行 `Contain-Setup-x64.exe` 覆盖重装，应用列表中仍仅保留 **1 个** `Contain` 条目。
  4. **卸载与重装**：调用系统已安装应用登记的卸载程序（`unins000.exe`）完成卸载，确认 `%LOCALAPPDATA%\Programs\Contain`、开始菜单快捷方式及应用列表条目全部清除；隔离个人记录库 `contain.db` 与预先保存在程序目录外的 HTML 导出报告 SHA-256 均保持原样未变；重新安装 `Contain-Setup-x64.exe` 并从开始菜单启动后，仍可直接打开同一合成会话 `synthetic-finished` 与同一事件 `synthetic-finished-event-0` 且解释一致。验收结束后已执行最终卸载清场。
- **保留待验证项与合并阻塞**：
  - **干净机首次启动：待验证**（当前测试机为已装有开发工具链的原生 Windows 11 x64 开发机，不能替代从未安装开发工具的干净 Windows x64 机器首次双击安装与启动验收）。
  - 仓库级 `cargo deny` 对旧核心依赖 `chrono 0.4.45` 的未维护告警（`RUSTSEC-2026-0543`）仍按既有规则单独保留为合并阻塞，未添加忽略项。

## 桌面端一键为用户 `.NET` 项目“生成安装包”与实包验收补充（`feat/project-installer`）

在完整保留 `delivery-installer-01`、`delivery-package-01` 及原始演示样例不变的前提下，于代码修订 `05d96546b7bb3f97540e26f1614e9c063fd40212` 在现有“检查项目环境”构建结果区实现了面向 Windows x64 `.NET` 自包含多文件发布的一键“生成安装包”功能，并重新冻结新版 Contain 候选包完成全链路验收：

- **功能、安全与身份边界**：
  - `contain.environment.json`（`schema 1`）新增可选向后兼容 `installer` 字段（`app_id`、`app_name`、`version`、`publisher`、`publish_dir`、`entry`、`publish_kind`）；`contain-workflow build --execute` 在全部环境、构建、验证与产物检查通过后写出 `build-attestation.json`，精确锁定本次清单摘要与已验证发布子目录内全部文件的 SHA-256。
  - 新增 `contain-workflow package-installer` 与构建结果区“生成安装包”按钮（不新增一级导航或打包向导）：仅在清单未变且发布子目录文件与本次通过构建的 `build-attestation.json` 完全一致时在干净临时暂存目录组包，拒绝打入项目根目录配置、数据库或构建日志，并在构建失败（即使磁盘残留旧 EXE）或产物被改动时禁止打包。
  - 对普通依赖运行时的 `dotnet build` 结果保留绿色“构建通过”，同时按当前项目 `.csproj` 主动给出 `dotnet publish ... -c Release -r win-x64 --self-contained true` 指引；对入口歧义、缺少明确版本号或制作者电脑缺少 Inno Setup 6（明确说明接收者无需安装）给出最短补齐步骤；后续打包失败时保留同项目上一次已生成的可用安装包，切换项目时清空旧项目结果。
  - 项目持久安装身份保存在项目根目录 `contain.installer.json`（同一项目换目录、重编或升级沿用同一 UUID；两个同名独立项目拥有各自的 `AppId`、默认安装子目录 `%LOCALAPPDATA%\Programs\<名称>-<short_id>`、开始菜单快捷方式与卸载登记；严禁占用 Contain 自身 `AppId`/目录，也不默认写入 `LuckyLivio` 发布者）；显示名、文件名、安装路径与 `.iss` 模板参数分别独立校验与转义，支持中文与带空格目录。
- **新版 Contain 候选包冻结身份（`source_sha = 05d96546b7bb3f97540e26f1614e9c063fd40212`）**：
  - 桌面候选包 `contain-source-windows-x64.zip`：`182b48b21745874d80c5eb213d2d1190b976c45daa453bf68cf90c6eb29d38aa`
    （`Contain.Viewer.exe`：`260e25717217f4d4f218850d63d1c84dd30925bd5b4175eb288b333fbb2968f2`，
    `contain.exe`：`e8ef0a926ff7acb7a7e5fe2acccbb872df516afb59f9eaf2245c221a301fa580`，
    随包 `contain-workflow.exe`：`67803bf5bf5ae66d59a43faf6f3b3267a6b099ad8e7a1becdc802fb105c49b80`，
    `package-spec.json`：`d9df87d9b6184a526c308159bec7661733a8e6b73575bfe9f60c1603af138709`）。
  - 独立 CLI 包 `contain-workflow-windows-x64.zip`：`ab41b7748a187c4eb3d501022d6582f82c0d6274fe6696237848cc7bc6303845`
    （`contain-workflow.exe`：`3677deefe6193510d6289c719f2d001736f61cca70ff68dcee79af608086491f`，
    `package-spec.json`：`ac4c1b4a590ca9d5df55bdc108f414ac2021146e4e42812c423a997a0efb71e4`）。
  - 新版安装候选包 `Contain-Setup-x64.exe`：`51,283,068` 字节，SHA-256 `ce561df3547da80464f83470c065dafcefb02d21c038f96a3e51f1eae124a018`（未签名 `NotSigned`，Inno Setup `6.7.3`）。
- **最终桌面包 `Contain.Viewer.exe` 实测结果（结论：开发机实包通过，干净机待验证）**：
  - 双包 `verify --run` 均退出 `0`；`contain-workflow` **43/43** 通过，桌面模型回归 **55/55** 通过，导航控件回归 **12/12** 通过。
  - 运行最终桌面候选包中的 `Contain.Viewer.exe` 完成三项完整交付验收：
    1. **正常流程（A）**：选择已配置 `win-x64` 自包含发布的 `HelloContain` 演示项目 → 检查环境通过 → 构建并验证输出摘要通过 → 点击“生成安装包”产出 `HelloContain-Setup-x64.exe`（`27,727,949` 字节，SHA-256 `b1d7a299ba1b7fcaf58c1993764843ea7a4bfef6c42b01b15321a844965e2c16`，`NotSigned`）；将其单独复制到带中文与空格的独立目录执行安装（同时验证默认用户目录及自定义中文带空格安装目录），独立核实开始菜单快捷方式指向安装目录内的 `HelloContain.exe`，启动并实测标准输出为 `CONTAIN_DEMO_BUILD_OK_V1`；调用系统已登记卸载项完成卸载后确认 `Contain` 自身仍正常启动。
    2. **常见踩坑与防旧包（B）**：同一项目先运行普通依赖运行时的 `dotnet build`，页面保留“构建通过”并即时展示针对 `HelloContain.csproj` 的自包含发布命令，点击“生成安装包”拒绝打包且不冲掉绿色构建结果；按指引修正清单后在同一窗口重新检查、构建并成功产出 `HelloContain-Setup-x64.exe`；随后注入编译错误使构建失败并保留磁盘旧 `HelloContain.exe`，界面隐藏并禁止“生成安装包”，旧产物不会被包装为本次成功交付。
    3. **持久身份与同名项目隔离（C）**：同一项目重复打包及跨目录迁移均保持 `contain.installer.json` 的 `AppId` 与安装路径稳定；两个同为 `HelloContain` 的独立项目生成互不相同的 `AppId`、安装目录与开始菜单快捷方式，同机同时安装互不覆盖，卸载其一后另一项目保持完整并正常输出 `CONTAIN_DEMO_BUILD_OK_V1`。
  - **待验证环境与保留门禁**：当前仅具备已装开发工具的 Windows 11 x64 开发机，生成的 `HelloContain-Setup-x64.exe` 与新版 `Contain` 在无开发工具干净 Windows x64 机器上的离线首次启动标为**待验证**；仓库级 `cargo deny`（`RUSTSEC-2026-0543` `chrono 0.4.45`）继续按既有规则跟踪。

## 干净 Windows x64 接收机离线安装交接与状态（`feat/receiver-setup-acceptance`）

针对产品源码 `05d96546b7bb3f97540e26f1614e9c063fd40212` 已产出的两份未签名安装候选包（`HelloContain-Setup-x64.exe`：`b1d7a299ba1b7fcaf58c1993764843ea7a4bfef6c42b01b15321a844965e2c16`；`Contain-Setup-x64.exe`：`ce561df3547da80464f83470c065dafcefb02d21c038f96a3e51f1eae124a018`），在不改动开发机工具链与宿主安全策略的前提下完成只读接收环境核查与离线交接准备：

- **接收环境核查**：当前主机为在线 Windows 11 x64 开发机（系统自带 `.NET Framework 4.8.1` 单列，且已预装现代 `.NET` 运行时与 SDK、Visual Studio、Rust 及 Inno Setup 6.7.3），未启用或安装 Windows Sandbox、Hyper-V、VirtualBox、VMware、QEMU，亦无独立无工具 Windows x64 测试机可用。
- **交接准备与结论边界**：新增可复用脚本 `scripts/inspect-receiver-environment.ps1`、`scripts/prepare-receiver-setup-handoff.ps1` 与 `scripts/test-receiver-setup-handoff.ps1`，在本地独立交接目录中完成两份原始 Setup 复制前后的 SHA-256 双重核验，生成 `SHA256SUMS.txt`、一页中文离线验收走查单（按先 `HelloContain` 后 `Contain`、单包拷入断网安装、验证启动／输出／合成记录事件依据、标准卸载并在关闭环境前导出回执的顺序编写）与交接清单。开发机已有 ZIP `verify --run` 收据单独保留；两份 Setup 的干净接收机离线安装、启动与卸载均如实记为**接收机待实测**。

## 无预配置普通 C# 控制台项目一键接入与实包闭环验收补充（`feat/dotnet-project-onboarding`）

在完整保留既有候选包、离线交接材料与 `HelloContain` 演示环境的前提下，于产品源码修订 `8f1acb4d13151a20963a395d67e414291d4406d2` 在现有“检查项目环境 → 选择文件夹”流程内实现了面向从未接入 Contain 的普通单项目 C# 控制台应用的一次确认配置引导，并用重新打包的桌面候选包完成端到端实测：

- **只读识别、来源区分与安全边界**：
  - 选中已有 `contain.environment.json` 的项目时直接沿用原配置（若存在 `contain.installer.json` 同时提示复用已有 `AppId`）；选中无清单目录时由随包 `contain-workflow.exe onboard-dotnet` 执行纯只读根目录 `.csproj` 解析（选择与预览期间绝不执行项目代码、MSBuild target 或 `dotnet restore`，绝不向项目目录写入任何文件）。
  - 仅支持根目录单个、单目标框架（`<TargetFramework>net8.0`+）、SDK 风格（`Microsoft.NET.Sdk`）且明确声明 `<OutputType>Exe</OutputType>` 的 C# 控制台应用；当 `<AssemblyName>` 与 `.csproj` 文件名不同时，预期产物与安装包入口严格采用 `<AssemblyName>.exe`。
  - `contain.environment.json` 新增可选 `tools[].version_policy = "dotnet_major_or_newer"`：无 `global.json` 的普通 `net10.0` 项目声明兼容主版本要求（`version: "10.0"`）而非伪造特定 SDK 补丁号，预览界面明确区分项目文件声明字段与建议配置项（兼容 SDK 范围、本机已检测兼容 SDK 列表、建议初始版本号 `0.1.0`），并默认折叠发布命令、独立隔离输出目录 `target/workflow/publish/win-x64` 与 `.csproj` 识别依据。
  - 用户明确点击“使用此配置”后才原子写入可复用的 `contain.environment.json`，全程不修改用户 `.csproj`，也不覆盖原有 `bin/` 或 `obj/` 输出；点击“取消”时项目目录保持字节级原样；预览期间若项目文件变更则刷新只读预览并要求重新确认，若外部新建 `contain.environment.json` 则直接保留不覆盖。
  - 对多项目目录（根目录或子目录多个 `.csproj`）、类库（`Library` 或未声明 `Exe`）、多目标框架（`<TargetFrameworks>`）、`WinExe`/WPF/WinForms、非 SDK 旧式项目或含 `$(...)`/`Condition`/`<Import>`/`Directory.Build.props` 动态属性的项目拒绝猜测，直接给出原因与单一可执行下一步建议。
- **新版 Contain 候选包冻结身份（`source_sha = 8f1acb4d13151a20963a395d67e414291d4406d2`）**：
  - 桌面候选包 `contain-source-windows-x64.zip`：`2a3a59fe1ce328efcb437a0b0871acc510f12099316d755cdb2a3be0d98dc117`
    （`Contain.Viewer.exe`：`37a3c07a36cbefeb49cc51a24bbe0edf1791fd13122f6e030f43963403726239`，
    `contain.exe`：`403ff4076a9a1de5d9845d50e97e2bfaf415412b2f60be3e69e0e151fca815c2`，
    随包 `contain-workflow.exe`：`8a73fbec2ef860b3a92db1991450158af620b89c07b36d79561ca07dc2cadefc`，
    `package-spec.json`：`832c23e3d3f112ca787d7374f306596d6a7a697a1b46690b3f464c2f7dee08f6`）。
  - 独立 CLI 包 `contain-workflow-windows-x64.zip`：`c48ac85ca3a7f83c88384db41a753c3d848f7a3e0480d9ec27c374f0a042979f`
    （`contain-workflow.exe`：`38f3f58aa5a5a7ff8bc7ce7f88d2ff7e1e62dc97eaf118e0a65b70bb00f51d6f`，
    `package-spec.json`：`c7295d1e05ddaece933f6bc045673e7fb7a7eff096012863937a60940216ffc3`）。
  - 新版安装候选包 `Contain-Setup-x64.exe`：`51,327,900` 字节，SHA-256 `1c5f9f96b6c67fedbf603387d6ff23fcbd896a12ed3ed6ab55884d76f31cb3d8`（未签名 `NotSigned`，Inno Setup `6.7.3`）。
- **最终打包 `Contain.Viewer.exe` 实测验收记录**：
  - 双包 `verify --run` 均退出 `0`；`contain-workflow` **46/46** 通过，桌面模型回归 **57/57** 通过。
  - 新建初始不含任何 Contain 文件或 `global.json`、且项目文件名与程序集名不同的独立普通 C# 控制台演示项目（`DeskStarter.Project.csproj`，声明 `<AssemblyName>HelloDesk</AssemblyName>`、`<TargetFramework>net10.0</TargetFramework>`、`<OutputType>Exe</OutputType>`），运行本次打包产出的 `Contain.Viewer.exe` 完成全链路实测：
    1. 首次选择目录显示 `已识别 HelloDesk · 控制台应用` 及 `HelloDesk.exe` 入口与可展开详情，点击“取消”后逐文件哈希核验确认项目目录零写入。
    2. 再次选择该目录，依次点击“使用此配置” → “开始检查”（`环境已满足`） → “开始构建”（`构建通过`，原 `DeskStarter.Project.csproj` SHA-256 `5caa27a4f22fec9b3f82b199fe7c3c9f2b1501457d4ace54c85c7f5b1f76c481` 全程未变） → “生成安装包”，无需手写 JSON、输入命令或挑选 EXE 即产出 `HelloDesk-Setup-x64.exe`（`27,724,940` 字节，SHA-256 `569c5960fd899f9db595e4888378a18f90ec1913acc8780b2bb153eb6afcf706`，`NotSigned`）。
    3. 将 `HelloDesk-Setup-x64.exe` 单独复制到独立目录完成安装，核对开始菜单快捷方式 `HelloDesk (433e293f).lnk` 指向安装目录下的 `HelloDesk.exe`，运行该入口实测标准输出为 `CONTAIN_PLAIN_CSHARP_ONBOARD_OK_V1`、退出码 `0`，随后通过 Windows 已登记卸载程序干净卸载。
    4. 关闭并重新启动 `Contain.Viewer.exe` 再次选择该项目，确认直接复用已保存的 `contain.environment.json` 与 `contain.installer.json`（`{433e293f-98e3-4a26-8bb4-b06867d79191}`），且既有 `HelloContain` 回归检查正常通过。
    5. 分别选择多项目目录（`Alpha.csproj` + `Beta.csproj`）与类库目录（`SharedCore.csproj`，`OutputType=Library`），确认界面展示明确原因与可执行下一步指引且未写入任何文件。
  - **单列未完成项与保留阻塞**：无开发工具干净 Windows x64 接收机离线安装与运行验收继续单列为**接收机待实测**；仓库级 `cargo deny` 对旧核心依赖 `chrono 0.4.45` 的未维护告警（`RUSTSEC-2026-0543`）仍按既有规则保留跟踪。
