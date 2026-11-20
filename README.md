# Contain

让 Windows 项目换一台电脑也能构建，让发出去的包在别人电脑上也能运行，并能核实具体哪些检查通过。

## 下载 Windows 版

当前公开版是 **v0.4.0-beta.1**。下载 Windows x64 便携 ZIP，完整解压后运行 `Contain.Viewer.exe`，即可记录安装行为、浏览记录并导出报告。

[下载 Windows x64 便携 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.4.0-beta.1/contain-0.4.0-beta.1-windows-x64.zip) · [发行说明](https://github.com/LuckyLivio/Contain/releases/tag/v0.4.0-beta.1) · [SHA-256 校验文件](https://github.com/LuckyLivio/Contain/releases/download/v0.4.0-beta.1/SHA256SUMS.txt)

## 即将发布的 v0.5.0-beta.1

这一版提供当前用户安装的 Setup，支持将单项目 C# 控制台应用打成可分发的安装包。预发行候选的产品源码固定为 `aac52e52`。发布前需在 GitHub 托管 Windows x64 上用同一批候选实测 Setup 安装、版本、桌面启动、随包示例读取和卸载，并按各自的包规格核验桌面 ZIP、独立 CLI ZIP 及发布文件的 SHA-256。当前等待本轮验收收据。[候选进度与历史试装记录](docs/beta05-release.md)

干净 Windows 上的缺项恢复构建和断网接收机 A/B 验收保留在稳定版发布前完成。Windows 本地 A/B 验收：云端未收到最终收据。

## 接手源码、恢复开发环境

版本化 `contain.environment.json` 描述 Windows x64 工具链、必需变量名称、实际构建和验证命令。普通 `check` 和 `check --safe-probes` 均只运行受支持的只读探测。自定义探测会标为 `needs_confirmation`。只有在审核并信任项目的探测命令和工具链脚本后，才应使用 `check --allow-unsafe-probes` 允许执行。没有清单时，只从明确支持的项目文件提取已声明要求，缺少依据的项目列为待确认。

```powershell
# 将独立 CLI ZIP 完整解压到 C:\Bootstrap，以使用随包的 Windows 工具链选择器。
C:\Bootstrap\contain-workflow.exe check --project . --report target/workflow/check-01
cargo run -p contain-workflow --locked -- build --project . --execute --delivery-dir target/workflow/delivery-01 --report target/workflow/build-01
```

## 发包前验证

对实际 Windows x64 ZIP 固定 SHA-256、启动入口和预期结果。报告分别列出包身份、静态检查、启动检查及指定测试。Windows 上可显式运行窗口或自检检查；其他平台只做静态检查，运行项记为 **未实测**。比较不同构建时，先检查两个包内实际 EXE；字节相同则拒绝开跑。

命令、清单格式与本分支验收状态见 [第一阶段说明](docs/phase-one.md)。本分支不代表已通过干净 Windows 验收。

```powershell
cargo run -p contain-workflow --locked -- verify --zip .\contain.zip --spec .\package-spec.json --report target/workflow/package-01
```

## Windows 安装候选包与便携压缩包分工

本分支同时提供参数化安装器构建入口 `scripts/build-windows-installer.ps1`（基于 Inno Setup 6），两类交付形态职责明确：

- **单文件安装候选包（`Contain-Setup-x64.exe`）**：面向普通桌面接收者。基于已冻结校验的 `contain-source-windows-x64.zip` 原样组包，默认以当前用户最低权限安装到 `%LOCALAPPDATA%\Programs\Contain`（终端用户无需联网安装 Rust、Python、Visual Studio 或 .NET SDK），自动创建开始菜单入口（桌面快捷方式可选且默认关闭），支持在 Windows“已安装的应用”中标准卸载。安装器仅管理程序目录、快捷方式与卸载登记，不改动已有便携版目录，卸载或升级时完整保留 `%LOCALAPPDATA%\Contain` 个人记录库及程序目录外的数据库与导出报告。安装包的签名状态见对应安装器收据。
- **便携桌面包（`contain-source-windows-x64.zip`）与独立 CLI 包（`contain-workflow-windows-x64.zip`）**：面向免安装解压即用、基于 `package-spec.json` 的静态／运行态交付契约校验与快照 A/B 交接验收；其中桌面 ZIP 同时作为构建 `Contain-Setup-x64.exe` 的受控载荷输入。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-windows-installer.ps1 -Zip <path-to>\contain-source-windows-x64.zip -Spec <path-to>\package-spec.json -OutputDirectory <new-output-dir>
```

本分支第二轮已补齐本次 Rust/WPF 产物的桌面 ZIP 组装、独立 CLI 打包和 A/B 交接脚本，并记录实际选中的 MSVC/SDK。架构改用 IsWow64Process2，ARM64 仿真和未知状态单独记录。[完整命令与产物位置](docs/phase-one.md) · [历史阶段记录](docs/phase-one-round2-results.md)。

## 后续里程碑

- **陌生软件隔离试用**：后续设计隔离执行、权限和试用证据；第一阶段不提供隔离保护。
- **配置回退**：后续设计配置备份、差异和可验证恢复；第一阶段不修改系统配置或提供回滚。
- **WPF 渐进接入**：桌面包已自带本次构建的 `contain-workflow.exe`，并在界面接入“检查项目环境”（含普通单项目 C# 控制台应用首次使用只读识别与一次确认生成配置、同窗口复查对比、受控项目构建、产物目录打开，以及对已验证 Windows x64 `.NET` 自包含多文件发布生成 `<应用名>-Setup-x64.exe` 安装包）与只读“检查交付包”（静态校验 ZIP 与 `package-spec.json`）；桌面端不自动安装依赖、不修改用户 `.csproj` 或系统配置，也不在文件夹选择、预览或检查交付包时自动运行项目代码或包内程序。

## 旧版安装记录工具与历史证据

**[Beta.1 Windows x64 自包含 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.4.0-beta.1/contain-0.4.0-beta.1-windows-x64.zip)** · [SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.4.0-beta.1/SHA256SUMS.txt) · [最终验收](https://github.com/LuckyLivio/Contain/releases/download/v0.4.0-beta.1/FINAL-ACCEPTANCE.json)

完整解压后运行 `Contain.Viewer.exe`。旧版用于选择安装器、记录安装行为、查看及导出证据；不等于软件所有权或安全删除许可。

Beta.1 已有一轮 Windows 发布验收通过：[发布说明](docs/beta04-release.md)、[全部尝试](docs/beta04-validation-attempts.md)、[桌面使用说明](desktop/README.md)、[实际截图](docs/beta04-screenshots.md)。Beta.2 的候选包能运行，但指定验收没有全部通过；其他分支的结果不作为本分支验收。

[Beta.10 WinForms 回退下载](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.10/contain-0.3.2-beta.10-windows-x64.zip) · [交付历史](docs/delivery-history.md) · [旧架构](docs/architecture.md) · [只读工作流](docs/read-only-workflow.md) · [贡献规范](CONTRIBUTING.md)
