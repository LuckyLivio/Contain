# 第一阶段：源码恢复与交付验证

此分支从 `main` / `ddafeb26a89ea7bf625421e6f958b3e8ca49c31b` 开始。已检查并保留
Beta.2 的修复分支及 PR #22 / `5c7ad35e9fe3a43035a9159d50a11fad6dec90f9`，没有合并或重写它们。
新 Rust 包 `contain-workflow` 不依赖 `contain-core`，不启动 ETW、不修改旧 CLI/v2/v3 协议和 WPF。

## 开始使用

开发机需要 `rust-toolchain.toml` 固定的 Rust **1.100.0**。Windows 自用清单还要求
.NET SDK **10.0.403**、MSVC v143 **14.44 系列**（并非精确 patch 锁）、Windows SDK
**10.0.26100.0**、x64 MSVC Rust target。`global.json` 继续禁止 .NET SDK roll-forward。
完整要求和补齐说明在根目录 `contain.environment.json`；当前项目没有额外必需的环境变量。

```powershell
cargo build -p contain-workflow --locked
$cli = '.\target\debug\contain-workflow.exe'
& $cli check --project . --report target/workflow/check-01
# 按报告 remedy 安装或修正要求，再用新目录重新检查。
& $cli build --project . --execute --report target/workflow/build-01
```

`build` 先检查环境，再执行清单中的真实 Rust workspace 构建、独立 CLI 静态 CRT
构建及打包、WPF 锁定 restore 和 win-x64 自包含 publish，随后运行 Rust 和桌面模型回归。
前序构建失败会把后续步骤标为未实测。没有 `--execute` 不执行构建。
不会自动提权、安装工具或运行远程 CI。

每份报告目录必须全新，含 `report.json` 和中文状态 `report.md`。退出码：
**0** 全部指定检查通过；**1** 有失败或输入/写入错误；**2** 无失败但有未实测或待确认。
静态检查通过而没有原生运行时退出 **2**，不能当作包验收通过。
若清单格式本身无效，CLI 返回输入错误，不伪造已执行的检查报告。

Linux 可构建、运行推断、检查、ZIP 静态验证、对照门禁及可移植测试：

```bash
cargo build -p contain-workflow --locked
cargo test -p contain-workflow -p contain-offline --locked
cargo clippy -p contain-workflow -p contain-offline --all-targets --locked -- -D warnings
cargo check -p contain-workflow --all-targets --locked --target x86_64-pc-windows-msvc
```

最后一条只检查 Windows 代码能否编译，不链接 EXE，也不证明 Windows 能运行。

## 没有清单的 Git 项目

```powershell
& $cli infer --project C:\src\project --output C:\review\environment-draft.json
```

首版仅支持**根目录** `Cargo.toml`、`rust-toolchain.toml`、`global.json` 的已声明要求。
识别 Rust 精确 channel、最低 rust-version、components/targets 和 .NET SDK version；
Rust `stable`/`nightly` 不被伪装为精确版本，最低版本也不当成已安装版本。
生成的 draft 总是保留待确认项：目标平台、原生 SDK/MSVC、本地库、所需变量、构建
features 和 .NET 项目/运行时/发布命令。其他文件格式（含任意 `.csproj` 的自动分析）
暂不支持，不猜测要求。查看并完善 draft，把已确认要求写入 tools/build/verify，
清除确实已解决的 pending，然后随项目提交为 `contain.environment.json`。

清单 `schema_version: 1`，唯一支持目标为 `windows-x64`：

- `tools`：program、version、version_args、remedy。执行有 10 秒截止的版本探测，按空白
  分割的完整 token 匹配要求，不用版本前缀。多版本枚举命令可以验证某个版本是否存在；
  Contain 的 .NET 选中版本使用 `dotnet --version`。缺失/探测失败和版本不符分开报告。
- `required_variables`：仅变量名称数组，检查存在且非空；拒绝 `NAME=value` 和未知字段。
- `build` / `verify`：name、program、字面 args 数组、expected_exit、timeout_ms（1–3600000）。
  工作目录为项目根目录，不经过隐式 shell；需要 shell 时明确指定。
- `pending`：尚未证实的要求，任何待确认项都会阻止实际构建。

清单是可执行的项目配置，使用前应审阅。工具版本探测也可能执行程序。报告保存规范化
清单哈希、可获得的 Git HEAD 和工作树是否干净；HEAD 不代表未提交内容。
报告不复制环境值、工具探测原文、命令标准输出/错误或主机清单。构建失败需根据声明
命令在本地诊断；进程输出不会被附加进可分享报告。

## 用实际 ZIP 验证交付

`examples/workflow/contain-beta1.package.json` 固定旧 Beta.1 已发布 ZIP 的准确哈希及窗口标题；
`contain.package.json` 是新包模板，其中全零 SHA 必须改为**实际冻结 ZIP** 的 SHA-256。
不要在验收后重打包、覆盖报告或把 Actions 外层 artifact ZIP 当成发行 ZIP。

```powershell
Get-FileHash .\contain.zip -Algorithm SHA256
& $cli verify --zip .\contain.zip --spec .\package-spec.json --report .\reports\static-01
# 在另一台干净 Windows x64 上，复制同一份 ZIP、spec 和独立 CLI：
& $cli verify --zip .\contain.zip --spec .\package-spec.json --run `
  --work-dir .\extracted-01 --report .\reports\native-01
```

spec 有 schema_version、target、sha256、entry、required_files、launch 和 tests；
可选 comparison_entry 指定对照对象（例如包内 contain.exe），省略时对照启动入口。
launch 只能声明一种预期：`expected_exit` 或**准确** `window_title`，附 args 和 timeout_ms。
窗口必须可见且属于实际创建的进程 PID；没有把别的同名窗口算作成功。它只验证窗口出现，
不验证业务行为。tests 为包内 EXE 的相对路径、参数、截止时间和预期退出码；如有自检命令
可直接指定。首版只断言退出码，不断言输出内容；`--version exits 0` 不证明准确版本文本。
不指定启动或测试预期则对应项标为未实测。各项测试结果独立，不把能启动等同于测试通过。

静态阶段校验 ZIP SHA-256、Windows 安全路径、大小/成员数量限制、x64 PE 头、必需文件和
包内测试 EXE。存在 `CONTENTS.json` 时复用已有 schema 1 的 source_sha、文件集合、尺寸、
SHA-256 契约。PE 头与文件存在不证明 DLL 依赖完整或 loader 成功，仍须干净 Windows 实测。
限制：ZIP/展开总量各 1 GiB、10000 项；只支持 STORE/DEFLATE；拒绝链接、目录穿越、
大小写重名和 Windows 特殊路径。不提供隔离运行保护。只对可信包显式使用 `--run`。
运行时重用已验证的内存字节，在全新 work-dir 解压；不覆盖已有目录。超时会尝试结束
自己创建的进程（Windows 连同其进程树）；包工作目录和失败证据保留，未宣称系统无副作用。

`build-workflow.ps1` 生成可复制的独立 CLI ZIP、包规范和身份文件，附实际锁定依赖及 Rust
标准库许可证，要求静态 CRT。它不包含 .NET 或开发工具。其 Windows 链接和免开发工具
运行仍待实测；构建成功不会自动把 identity.json 中 acceptance 从 not_run 改为通过。

## 拒绝相同 EXE 的不同构建对照

```powershell
& $cli compare --baseline .\baseline.zip --candidate .\candidate.zip `
  --entry contain.exe --report .\reports\identity-01
# 将同一门禁与实际运行绑定，先比较包内实际入口 EXE，再决定能否执行：
& $cli verify --zip .\candidate.zip --spec .\candidate-spec.json `
  --compare-to .\baseline.zip --run --work-dir .\candidate-run-01 --report .\reports\comparison-01
```

ZIP 不同、构建标签不同、源码不同都不能替代 EXE 字节身份。门禁记录两侧实际 EXE 哈希；
相同时返回失败，在解压和启动之前终止。不同字节仅说明身份不同，不证明行为差异或对照有效。
需要比较非启动入口（例如桌面包中的 `contain.exe`）时，在 spec 声明
`"comparison_entry": "contain.exe"`，门禁与实际运行绑定；报告明确列出被比较的入口。

可复现的合成夹具（不会运行其非可执行 PE stub）：

```powershell
python scripts/workflow-fixtures.py --output target/workflow/pr22-fixture-01
& $cli verify --zip target/workflow/pr22-fixture-01/candidate.zip `
  --spec target/workflow/pr22-fixture-01/candidate-spec.json `
  --compare-to target/workflow/pr22-fixture-01/baseline.zip --run `
  --work-dir target/workflow/pr22-must-not-run-01 --report target/workflow/pr22-report-01
# 预期 exit 1、comparison_identity=failed、launch/tests=not_run，work-dir 不存在。
```

PR #22 的历史证据：
[5c7ad35 的记录](https://github.com/LuckyLivio/Contain/blob/5c7ad35e9fe3a43035a9159d50a11fad6dec90f9/docs/beta2-validation-results.md)
及 [原始 hash fixture](https://github.com/LuckyLivio/Contain/blob/5c7ad35e9fe3a43035a9159d50a11fad6dec90f9/scripts/fixtures/receiver-backlog-identical-binaries.json)。
原记录两侧 EXE 均为 13,059,072 字节 / SHA-256
`95d9d838fec94de3c879c08d8ba88a808a92881feaa732d1ef5f5c2fc1e66d77`。
本轮获取 artifact `13943722617` 的 HTTP 请求返回 403，没有得到原始 EXE。
确定性回归因此使用相同合成 x64 PE 字节、不同候选标签构造两个不同 ZIP，验证同一拒绝条件；
不声称这是历史二进制或恢复了原 Windows 对照实验，也不把缓存复用疑点写成已证实根因。

## 干净 Windows 验收步骤（待执行）

需要两套独立的干净 Windows x64 快照，并保留原包哈希和各次报告。不要 dispatch 既有
原生 CI 来替代此流程。先在可信构建机生成独立 CLI，复制到快照 A，以便工具不存在时仍能检查。

1. **快照 A，源码恢复**：检出本分支，先运行 `check` 留下真实缺失报告；按 remedy 安装
   Rust/.NET/MSVC/Windows SDK，故意用错误 .NET/Rust 版本再检查并保留报告；修正后使用
   `scripts/workflow-windows-acceptance.ps1 -Stage Source -Cli <cli.exe> -Project <checkout> -OutputDirectory <new-dir>`。
   脚本还检查受控缺失/不匹配负例，再执行真实构建和声明回归。不要将受控模拟当成快照 A 的原始缺失记录。
2. 冻结实际要发出的 ZIP 和 spec，记录 SHA-256。将**同一份** ZIP、spec、独立 CLI 复制到快照 B。
3. **快照 B，无开发工具**：记录 Windows 版本和原生/进程架构（CLI 报告会记录，无机器名称）。
   在有交互桌面的会话运行
   `scripts/workflow-windows-acceptance.ps1 -Stage Package -Cli <cli.exe> -Zip <zip> -Spec <spec> -OutputDirectory <new-dir>`。
   该阶段不调用 Rust、MSVC、Python 或 .NET SDK。当前脚本的负例针对 Contain WPF 包：移除
   coreclr.dll，固定坏包自己的新哈希，再检查缺件失败和 launch=not_run，避免仅由旧哈希误报。
4. 独立验证新独立 CLI 包自身：以 build-workflow.ps1 产生的 package-spec.json 运行 verify；
   它包含实际 --version/--help 退出检查，不适用 WPF 专用的 coreclr.dll 负例脚本。
5. 保存能启动、指定测试失败/通过、未实测三类事实，审阅后才决定发布或合并。

## 当前验收与证据

见 [本分支验收记录](phase-one-results.md)。Beta.1 历史发布验收已通过；Beta.2 候选可启动但
指定验收未全部通过，不能覆盖失败。此分支的 Linux 检查、Windows 编译检查与历史验收分别列出。

开发质量补充命令（覆盖范围均需按结果标注）：

```bash
cargo llvm-cov -p contain-workflow --locked --json --output-path target/quality/workflow-coverage.json
cargo deny --locked check
cd crates/contain-workflow
cargo +nightly fuzz run workflow_inputs -- -max_total_time=30 -max_len=8192
```

新增 fuzz workspace/锁文件与旧 ETW fuzz workspace 分离；目标覆盖新清单/包规范验证、Windows
路径拒绝和 PE 边界，不宣称对 ZIP 第三方实现或 Windows loader 做了完整 fuzz 验证。
Windows 仓库级质量门仍按 AGENTS.md 运行 `./scripts/quality.ps1 -Mode Check`、Coverage、
Dependencies 和适用 Fuzz；Linux 新 CLI 的检查不替代这些门。
