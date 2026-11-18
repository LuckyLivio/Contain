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
