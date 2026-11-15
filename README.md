# Contain

让 Windows 项目换一台电脑也能构建，让发出去的包在别人电脑上也能运行，并能核实具体哪些检查通过。

本分支实施第一阶段重构：独立 Rust CLI `contain-workflow`，不依赖旧 ETW 采集链。源码恢复和交付验收是两条独立流程；WPF 接入排在 CLI 验证之后。

## 接手源码、恢复开发环境

版本化 `contain.environment.json` 描述 Windows x64 工具链、必需变量名称、实际构建和验证命令。CLI 检查缺失项及版本差异，给出补齐步骤；补齐后显式运行项目构建。没有清单时，只从明确支持的项目文件提取已声明要求，缺少依据的项目列为待确认。

```powershell
cargo run -p contain-workflow --locked -- check --project . --report target/workflow/check-01
cargo run -p contain-workflow --locked -- build --project . --execute --delivery-dir target/workflow/delivery-01 --report target/workflow/build-01
```

## 发包前验证

对实际 Windows x64 ZIP 固定 SHA-256、启动入口和预期结果。报告分别列出包身份、静态检查、启动检查及指定测试。Windows 上可显式运行窗口或自检检查；其他平台只做静态检查，运行项记为 **未实测**。比较不同构建时，先检查两个包内实际 EXE；字节相同则拒绝开跑。

命令、清单格式与本分支验收状态见 [第一阶段说明](docs/phase-one.md)。本分支不代表已通过干净 Windows 验收。

```powershell
cargo run -p contain-workflow --locked -- verify --zip .\contain.zip --spec .\package-spec.json --report target/workflow/package-01
```

本分支第二轮已补齐本次 Rust/WPF 产物的桌面 ZIP 组装、独立 CLI 打包和 A/B 交接脚本，并记录实际选中的 MSVC/SDK。架构改用 IsWow64Process2，ARM64 仿真和未知状态单独记录。[完整命令与产物位置](docs/phase-one.md) · [本轮结果与合并阻塞](docs/phase-one-round2-results.md)。

当前只有 Linux 实测和合成夹具、Windows MSVC 编译检查；**尚未产出本次提交的完整 Windows ZIP，干净 A/B Windows 实测未完成**。新 CLI 独立依赖门通过，仓库级依赖门仍被旧核心 SQLite 公告阻塞，不能据此宣称可合并。上轮实际 Beta.1 包检查及 PR #22 门禁的[历史证据](docs/phase-one-results.md)保留。

## 后续里程碑

- **陌生软件隔离试用**：后续设计隔离执行、权限和试用证据；第一阶段不提供隔离保护。
- **配置回退**：后续设计配置备份、差异和可验证恢复；第一阶段不修改系统配置或提供回滚。
- **WPF 接入**：在独立 CLI 两条流程稳定后接入，不在本阶段替换旧界面。

## 旧版安装记录工具与历史证据

**[Beta.1 Windows x64 自包含 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.4.0-beta.1/contain-0.4.0-beta.1-windows-x64.zip)** · [SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.4.0-beta.1/SHA256SUMS.txt) · [最终验收](https://github.com/LuckyLivio/Contain/releases/download/v0.4.0-beta.1/FINAL-ACCEPTANCE.json)

完整解压后运行 `Contain.Viewer.exe`。旧版用于选择安装器、记录安装行为、查看及导出证据；不等于软件所有权或安全删除许可。

Beta.1 已有一轮 Windows 发布验收通过：[发布说明](docs/beta04-release.md)、[全部尝试](docs/beta04-validation-attempts.md)、[桌面使用说明](desktop/README.md)、[实际截图](docs/beta04-screenshots.md)。Beta.2 的候选包能运行，但指定验收没有全部通过；其他分支的结果不作为本分支验收。

[Beta.10 WinForms 回退下载](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.10/contain-0.3.2-beta.10-windows-x64.zip) · [交付历史](docs/delivery-history.md) · [旧架构](docs/architecture.md) · [只读工作流](docs/read-only-workflow.md) · [贡献规范](CONTRIBUTING.md)
