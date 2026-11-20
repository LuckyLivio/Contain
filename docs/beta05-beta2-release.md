# Contain v0.5.0-beta.2

## 单项目 WPF 应用打包

在 Windows x64 上打开 Contain，选择「检查项目环境 → 选择文件夹 → 使用此配置 → 开始检查 → 开始构建 → 生成安装包」，即可把受支持的 WPF 工程做成可安装的 Windows 应用。

项目需使用 `Microsoft.NET.Sdk`，显式声明 `OutputType=WinExe`、`UseWPF=true` 以及单个 `net8.0-windows` 或更高版本的 Windows 目标框架。预览会显示识别依据与入口 EXE，支持与项目文件名不同的 `AssemblyName`。构建机需要对应 .NET SDK 和 Inno Setup 6。

## Windows 实测

- 在 GitHub 托管 Windows Server 2022 x64 上校验便携桌面 ZIP 与独立 CLI ZIP 的规格和实际启动，安装候选 Setup 并核对窗口、版本、示例读取及卸载。系统版本 `10.0.20348.0`，镜像 `20261116.370.1`。[Windows 验收](https://github.com/LuckyLivio/Contain/actions/runs/44709811837)
- 在候选 Setup 安装的 Contain 窗口中选择具有自定义程序集名的 WPF 项目，确认配置、检查环境、执行构建并生成项目 Setup。实测项目文件名为 `WpfUife10e0fdc2.csproj`，确认配置的入口为 `WpfUife10e0fdc2Desktop.exe`。安装这个 Setup 后，从开始菜单入口打开项目窗口，确认响应、正常关闭并卸载。[WPF 窗口实测](https://github.com/LuckyLivio/Contain/actions/runs/44709811837/job/136198361203)
- 将公开的 Beta.1 原位升级到本次 Beta.2，核对唯一的安装登记和程序集版本 `0.5.0.2`，升级后可从桌面打开原有默认个人记录库，随包 CLI 也能读取记录与事件。标准卸载后库文件摘要一致。[升级验收](https://github.com/LuckyLivio/Contain/actions/runs/44709811837/job/136198361456)

产品源码为 `18782fc4e8cfd91df4d60fa339b77070236f130a`，构建工作流提交为 `2eebc6b93c1dcce0dd027a5290ab35676011a1e7`，验收工作流提交为 `b4645ce3f47c747eeffcc66ad42a360c06a01279`。[产品源码](https://github.com/LuckyLivio/Contain/commit/18782fc4e8cfd91df4d60fa339b77070236f130a) · [Windows 构建](https://github.com/LuckyLivio/Contain/actions/runs/44708424263) · [发布 PR](https://github.com/LuckyLivio/Contain/pull/26)

| 文件 | SHA-256 |
| --- | --- |
| `Contain-Setup-x64.exe` | `4f0bd9aa89507f69e90ded3356e1c7531a4025a85b4ffbb7a662fb6ff58e7c6f` |
| `contain-0.5.0-beta.2-windows-x64.zip` | `48ccde60800bc1fb4d5fcc2660dcef0ceda0dee3b976667d7e28796e025df81a` |
| `contain-workflow-0.5.0-beta.2-windows-x64.zip` | `b70ffa69ee05f1f74b12c26e41619ce3c5d09e485888b3491b91e69a066a4446` |
| `SHA256SUMS.txt` | `0b2c308f631a8ea028a24c82e71b14bd8064048df2057389916550c3f0b5726a` |
| `prerelease-smoke.json` | `3c40a3276a38334a088029cc46ad3292abe6420e75b329c536890ac8f9bad14d` |
| `beta2-upgrade.json` | `2020d222a577576dde588441af0d6269584353a8c72061f26c214d47b8c94ba0` |
| `installed-wpf-workflow.json` | `408bbfe4c6656e0f19b5660b57f7083abacf805e174fbd9b5abfe82e283349ae` |

当前用户安装器文件版本为 `0.5.0.2`，Authenticode 状态为 `NotSigned`。干净无开发工具机器和完全断网试装列入稳定版前的兼容性验证。
