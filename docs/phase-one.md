# 第一阶段：本次源码 → 两份 Windows 交付包 → 独立接收机验证

当前代码在 `feat/source-and-package-workflows` 上继续；上轮审阅基线
`c0ea857efaa8d9cdf6fd6a735cb35f87cdcab9d8`。本轮结果见
[第二轮验收记录](phase-one-round2-results.md)，上轮 [历史记录](phase-one-results.md) 保留。

本轮补齐了组装入口和 A/B 复现脚本，但当前任务机器仍为 Linux：**没有生成或验收过
本次提交的完整 Windows 桌面 ZIP，也没有可提供下载的本次 Windows 原生包**。
以下命令在可信 Windows 构建机生成真实文件；不以旧 Beta 包或合成 ZIP 代替。

## 构建前提与实际选择

版本化 `contain.environment.json` 要求 Windows 原生 x64、Rust 1.100.0、MSVC target
`x86_64-pc-windows-msvc`、.NET SDK 10.0.403、Python 3.12.10 x64（构建机专用）、
MSVC v143 14.44 系列、Windows SDK 10.0.26100.0，以及能读取本检出的 Git。
当前没有额外必需的业务环境变量。Rust/.NET 精确版本继续由 toolchain/global.json 固定。

`windows_toolchain` 不再表示仅检查安装目录存在。选择器枚举所有 VS 2022 实例和
14.44 工具集，选择符合要求的最高 patch；已有兼容的 active patch 会保持并验证。
存在冲突的 VCToolsVersion/WindowsSDKVersion 时拒绝继续，报告 conflict 和补齐/新 shell
步骤，不静默声称选对了。

实际构建前执行 `vcvarsall.bat amd64 10.0.26100.0 -vcvars_ver=<selected-full-version>`，
再验证返回的完整 MSVC/SDK 版本及 cl/link/rc 的实际解析路径。用该环境启动 Rust、
C 编译和 CLI 打包，并设置选中工具集的绝对 Rust linker/CC/CXX 路径。
报告及包 BUILD/identity 记录完整 MSVC patch 和 SDK 版本；PATH/LIB/INCLUDE 等本地值
仅在进程间传递，不进入可分享报告。不支持 CARGO_ENCODED_RUSTFLAGS 覆盖静态 CRT 请求。

## 可信构建机：先准备可带到空白源码机的独立 CLI

先安装上述工具，将 `$sourceSha` 设为本次候选所用发布分支的完整提交 SHA，然后从干净检出构建 bootstrap CLI；**不要在 A 上先安装
工具再补写缺项报告**。以下输出路径均必须尚不存在；失败输出和旧证据保留。

```powershell
git switch --detach $sourceSha
powershell.exe -NoProfile -File scripts/build-workflow.ps1 -OutputDirectory C:\ContainRuns\bootstrap-01
```

得到 `bootstrap-01\contain-workflow-windows-x64.zip`、`package-spec.json`、`identity.json`。
脚本使用独立新构建目录、请求静态 CRT、收集实际依赖许可证；这是 bootstrap，不是
干净接收机通过证明。将完整 CLI ZIP 解压到 A 的 `C:\Bootstrap`，保留
`contain-workflow.exe` 旁的 `workflow\` 子目录，供默认环境检查选择 MSVC 和 Windows SDK。
运行该 CLI 无需预装 Rust 或 .NET SDK。
GitHub 分支是源码获取方式；本轮没有上传 Release 或伪造 Windows artifact 下载地址。

## 快照 A：从真实缺项到本次构建的桌面/CLI ZIP

从干净 Windows x64 快照开始，检出本分支。SnapshotLabel 是操作者对快照的标记，
工具不能仅凭缺项探测证明镜像洁净；应另行保留镜像/快照来源。

```powershell
$cli = 'C:\Bootstrap\contain-workflow.exe'
powershell.exe -NoProfile -File scripts/workflow-windows-acceptance.ps1 `
  -Stage SourceMissing -Cli $cli -Project C:\src\Contain `
  -SnapshotLabel A-before -OutputDirectory C:\ContainRuns\A-before
```

该阶段要求真实 missing/toolchain-failed 项，不能用输入错误或非 Windows 平台失败充当
工具缺项证据。按报告 remedy 补齐 Rust、.NET SDK、Python、VS C++ Build Tools 和 SDK。
示例 Rust 命令为 `rustup toolchain install 1.100.0 --component rustfmt --component clippy`、
`rustup target add --toolchain 1.100.0 x86_64-pc-windows-msvc`；其余使用供应商对应版本安装器。

```powershell
powershell.exe -NoProfile -File scripts/workflow-windows-acceptance.ps1 `
  -Stage Source -Cli $cli -Project C:\src\Contain `
  -SnapshotLabel A-restored -OutputDirectory C:\ContainRuns\A-restored
```

Source 阶段重新检查，然后故意设置错误 active MSVC 和 SDK，要求 selector 明确拒绝，
恢复环境后再执行真实构建。无需测试快照编排时，也可直接使用新 CLI：

```powershell
& $cli build --project C:\src\Contain --execute `
  --delivery-dir C:\ContainRuns\delivery-01 --report C:\ContainRuns\build-01
# 等价的本仓库构建入口（仍会自行选择/核对工具链）：
powershell.exe -NoProfile -File scripts/build-source-delivery.ps1 -OutputDirectory C:\ContainRuns\delivery-02
```

该入口依次执行工具链/接收策略夹具、实际 Rust workspace release 构建和回归、桌面模型
锁定 restore/回归、WPF win-x64 自包含 publish，以及生产离线模块的合成 fixture 生成。
随后传入**本次构建的明确 core/WPF 路径**给 `package-source-desktop.py`，冻结桌面 ZIP，
并构建/冻结独立 CLI。所有原始构建输出在单次全新目录中，拒绝污染源码或覆盖旧产物。
失败步骤保留本地日志和退出码，不生成成功交接收据。完整路径中不要引入私人资料。

Source 阶段输出位于 `A-restored\delivery`；直接 build 则位于指定 delivery-dir：

| 相对路径 | 用途 |
| --- | --- |
| `desktop-delivery/contain-source-windows-x64.zip` | 本次提交的完整桌面包，含本次 Rust contain.exe、WPF 自包含运行时、许可证及标注的合成样例 |
| `desktop-delivery/package-spec.json` | 固定此 ZIP SHA-256、准确窗口标题及指定测试 |
| `desktop-delivery/identity.json` | 源码 SHA、核心/入口/ZIP/spec SHA-256、选中 MSVC/SDK |
| `cli-delivery/contain-workflow-windows-x64.zip` | 无需开发工具的独立验证 CLI 包（静态 CRT 请求仍需 B 上实测） |
| `cli-delivery/package-spec.json`、`identity.json` | CLI 自身包/EXE/spec 的身份和运行预期 |
| `handoff.json` | 将上述两份 identity 与相同源码提交绑定，交给 B 核对 |
| `build-receipt.json`、`toolchain.json`、各步骤 JSON | 构建输入/选中版本/回归状态；`.local.log` 只供本地诊断，分享前审阅 |

新桌面包用 `contain-source-desktop-package-1` BUILD 协议，沿用 schema 1 文件身份和
CONTENTS 精确成员/尺寸/SHA-256 契约。`reused_core_binary=false`，core_source_sha 与
source_sha 均为本次检出。外部 identity 另绑定最终 ZIP（避免 ZIP 自包含哈希循环）。
旧 Beta.1/Beta.10 打包脚本及固定核心规则原样保留；新包不继承旧验收。

发布分支集成后，在快照 A 使用生成这份 delivery 的干净检出组装候选。将
`$sourceSha` 设为本轮确认的完整 40 位发布源码提交，安装 Inno Setup 6 后执行：

```powershell
python scripts/assemble-release-candidate.py --source-sha $sourceSha --delivery C:\ContainRuns\A-restored\delivery --output C:\ContainRuns\candidate-01
```

入口校验 A 的交接身份、双 ZIP、清单和选择器，在开发机用两份包内 CLI 各运行普通
`check` 与 `--safe-probes`，再从已核对的桌面 ZIP 构建 Setup。新目录包含版本化双 ZIP、
两份清单及身份、Setup 及其收据、A 的原始 `handoff.json`、`candidate-identity.json` 与
`SHA256SUMS.txt`。保留 B 的独立收据及新 Setup 的离线试装记录。

## 快照 B：同一份 ZIP，开发工具缺失，独立验收

对组装后的候选，将整个候选目录传至已断网、无开发工具的 B。单独保存 A 打印的
`SHA256SUMS.txt` 摘要，核对该摘要及清单中 `receive-release-candidate.ps1`
的文件哈希，再在 B 的交互桌面执行下例（`$expectedSha` 为独立传递的摘要）：

```powershell
& C:\Receive\candidate-01\receive-release-candidate.ps1 -ExpectedChecksumsSha256 $expectedSha -SnapshotLabel B-clean -OutputDirectory C:\ContainRuns\B-01
```

入口校验整套候选及其操作脚本，提取经过哈希核对的工作流 CLI，运行 Package 阶段，并在
执行前后分别检查包括虚拟网卡在内的接口状态。请在关闭 B 前导出
`C:\ContainRuns\B-01\B-receipt.json`，同时保存快照来源和断网配置。Setup 离线试装另行记录。

B 阶段只需要 Windows PowerShell 和独立 CLI，不调用 Python/Rust/MSVC 或 .NET SDK
完成包验证。开发工具缺失探测仅在发现 dotnet 主机时枚举 SDK；不会把仅安装运行时
当作安装 SDK。检查 PATH、常用安装目录、SDK 枚举、VS/SDK 注册及卸载注册，报告只含
布尔状态，不公开本机路径/产品清单。发现开发工具、探测错误或未知时不能宣称免工具验收通过。

分别保留 platform、development-tools、received-identity、CLI、desktop、broken 及总
acceptance 报告。架构用 **IsWow64Process2** 同时记录 native/process/emulated；x64 在
ARM64 仿真单独标记，不能通过原生 x64 门。探测失败 native/process 保持 unknown，不以
编译目标或 GetNativeSystemInfo 填补。当前实测仅覆盖解码夹具，原生 API 仍待 Windows 验证。

桌面窗口必须可见、标题精确且属于实际创建的进程。指定测试是核心准确版本输出、对
包内同一合成数据库的只读 list：同时检查退出码及构建机实际输出的 SHA-256，不只看
exit 0。CLI 自身也独立运行 --version/--help。stdout 内容不进入报告。
脚本再移除 coreclr.dll、为坏包重新固定它自己的 SHA，确认静态缺件失败、launch=not_run、
运行目录未创建。桌面或自检失败时，其余独立检查仍记录；无法执行的阶段显式 not_run。

## 通用 CLI 与保留的 PR #22 回归

```powershell
& $cli platform --report .\platform-01
& $cli check --project . --report .\check-01
& $cli infer --project C:\src\other --output .\draft.json
& $cli verify --zip .\candidate.zip --spec .\spec.json --run --work-dir .\unpacked-01 --report .\native-01
& $cli compare --baseline .\baseline.zip --candidate .\candidate.zip --entry contain.exe --report .\compare-01
```

无清单推断范围仍是根目录 Cargo.toml/rust-toolchain.toml/global.json；未知要求保留 pending。
`--execute` 才运行构建；`--run` 才执行包。spec.comparison_entry 可明确比较包内 core EXE，
防止只比较改变过的 GUI；同字节在解压/运行前拒绝。用
`python scripts/workflow-fixtures.py --output target/workflow/pr22-01` 可生成确定性复现，
再 verify `--compare-to …/baseline.zip --run`，预期 exit 1、运行项 not_run、无运行目录。
这是合成条件复现，非 PR #22 历史二进制；[上轮来源](phase-one-results.md#pr-22-回归证据)保留。

报告目录必须全新。退出 0=所有指定检查通过，1=失败，2=无失败但有未实测/待确认；
静态通过不等于原生验收通过。输入错误不伪造已执行报告。包执行不是隔离；只执行可信包。
完整环境值、命令输出和主机清单不进入可分享清单/报告。

质量命令：cargo test/clippy（contain-workflow 与 contain-offline）、Windows MSVC all-targets
check、新输入 fuzz smoke、覆盖报告，以及现有 quality.ps1 的适用 Check/Coverage/Dependencies。
普通 Windows CI、编译检查、合成夹具和干净 A/B 实测分别记结果，不能互相替代。
旧 SQLite 公告单独跟踪修复 `925d713539a48c2794f88e4a6b8409f18bd53a3f`；本轮不 cherry-pick，
若仓库依赖门仍失败就保留为合并阻塞。合并和发布仍留待评审。
