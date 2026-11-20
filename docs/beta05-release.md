# v0.5.0-beta.1：普通 C# 控制台项目零配置打包与桌面工作流

**发布状态**　当前公开版为 v0.4.0-beta.1。v0.5.0-beta.1 正在整合 CLI 默认只读探测、项目选择时的后台检查及上级目录 MSBuild 配置识别。新候选须从包含全部产品修复的发布分支提交构建，并将双 ZIP、Setup、升级及离线试装收据绑定到同一批字节。Windows 本地 A/B 验收：云端未收到最终收据。[当前公开版](https://github.com/LuckyLivio/Contain/releases/tag/v0.4.0-beta.1)

本版面向在 Windows x64 上开发单项目 C# 控制台应用、希望把程序直接打包发给普通用户安装的开发者，同时也适合需要校验交付 ZIP 或查看既有安装记录的用户。双击运行 `Contain-Setup-x64.exe` 完成当前用户安装后，从开始菜单打开 `Contain`，进入「检查项目环境」选择只有 `.csproj` 和源码的项目文件夹，依次点击「使用此配置 → 开始检查 → 开始构建 → 生成安装包」，即可得到可独立分发的 `<应用名>-Setup-x64.exe`。历史冻结候选（`887f119`）已在 Windows 11 x64（`10.0.26300`）开发机、GitHub Actions `windows-latest` CI 与断网无开发工具的干净 Windows 11 x64 沙箱（`10.0.26100`）完成构建、离线安装启动、随包示例记录读取及卸载验证；完整快照 A→B（空白源码机缺项恢复构建并交接至干净接收机 `-Stage Package`）的最终收据尚未送达云端。所有候选安装包均未配置代码签名证书（`NotSigned`），首次运行可能出现 Windows SmartScreen 提示。

---

### 发布前修复进度

| 审查项 | 已推送的修复与验证 |
| --- | --- |
| CLI 环境探测 | [`07feeae`](https://github.com/LuckyLivio/Contain/commit/07feeaeec165033aeccc89d20f3494f056760e59) 将只读策略设为默认，保留桌面端显式参数。该提交的 Windows Rust 和桌面 CI 均通过。Linux 套件的两项失败在发布基线也能复现。 |
| 上级目录构建配置 | [`44fd575`](https://github.com/LuckyLivio/Contain/commit/44fd575f3d7ce6a083b2cdb6e81bb1a88c4122a0) 识别继承的 MSBuild 属性和目标，重新核对确认指纹。两种父目录样例均实际改变了子项目输出名。 |
| 项目选择响应 | [`3f564a8`](https://github.com/LuckyLivio/Contain/commit/3f564a8f2ef23783beeca1683519c73ad4b3360a) 将选择与刷新移出界面线程并丢弃过期结果。桌面模型测试 59/59 通过。 |

正式候选的产品源码 SHA 待上述改动合入发布分支后记录。下面的冻结包、哈希和安装收据属于此前的 `887f119`。快照 A 的引导 CLI 已在 Windows 上完成试构建。[引导 CLI 构建记录](https://github.com/LuckyLivio/Contain/actions/runs/44681314014)

## 本版交付范围

1. **普通单项目 C# 控制台应用一次确认接入**：
   - 选择不含 `contain.environment.json` 的根目录单项目 SDK 风格 C# 控制台应用（`<OutputType>Exe</OutputType>`，单 `<TargetFramework>net8.0` 及以上）时，执行纯只读 `.csproj` 解析，选择与预览阶段不执行项目代码、MSBuild target 或 `dotnet restore`，不写入任何文件。
   - 预览卡片明确区分 `.csproj` 声明字段（如不同于项目文件名的 `<AssemblyName>` 对应入口 `<AssemblyName>.exe`）与兼容 `.NET SDK` 主版本策略、本机已检测 SDK 及建议初始版本号（`0.1.0`），可展开查看完整发布命令与独立输出目录 `target/workflow/publish/win-x64`。
   - 点击「使用此配置」后才写入可复用的 `contain.environment.json`，全程不修改用户 `.csproj`，也不覆盖原有 `bin/` 或 `obj/` 目录；点击「取消」保持目录字节级原样；再次打开同一项目时自动复用 `contain.environment.json` 与持久安装身份 `contain.installer.json`。
   - 对多项目目录、类库、多目标框架、`WinExe`/WPF/WinForms（WPF 应用自动识别排在后续版本）或动态 MSBuild 属性拒绝猜测，给出具体原因与单一可执行下一步建议。
2. **桌面端完整工作流与保留能力**：
   - **检查项目环境**：只读安全探测白名单、同窗口重新检查“已解决”对比、受控项目构建与确认产物目录打开、基于 `build-attestation.json` 的一键 Windows x64 自包含多文件项目 Setup 生成。
   - **检查交付包**：只读核对 `.zip` 与同目录或手动指定的 `package-spec.json`，将“文件校验通过／失败”与“启动未测试”并列分开展示，绝不临时伪造预期哈希或自动运行包内程序。
   - **我的记录 / 新建记录**：完整保留既有安装记录浏览、搜索、依据解释、HTML 报告导出及历史数据库只读兼容；已安装 `0.4.0-beta.1` 的用户运行新版 `Contain-Setup-x64.exe` 可原位升级并保留个人记录库。
3. **仓库级依赖门修复**：
   - 移除 `contain-core`、`contain-cli` 与 `contain-offline` 对未维护 `chrono 0.4.45`（`RUSTSEC-2026-0543`）的依赖，采用零外部依赖的标准库 `SystemTime` 与精确格里高利历 RFC 3339 格式化（保持 `AutoSi` 0/3/6/9 位小数精度与 `+00:00` 偏移）兼容替换，`deny.toml` 严格规则保持不变，主工作区与两个 fuzz 工作区 `cargo deny --locked check` 全部通过。

---

## 历史冻结候选的版本、组件与身份

| 项目 | 固定身份 |
| --- | --- |
| 桌面 UI / 安装器版本 | `0.5.0-beta.1`（PE `VersionInfo = 0.5.0.1`），WPF + WPF UI `4.3.0` / MVVM `8.4.2` |
| 固定安装身份 `AppId` | `{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}`（当前用户最低权限安装，支持从 `0.4.0-beta.1` 原位升级） |
| 随包工作流 CLI 版本 | `contain-workflow 0.5.0-beta.1`（请求静态 CRT） |
| 随包采集核心版本 | `contain 0.3.2-beta.10`（本次源码 release 构建，协议版本与 UI 分列） |
| 构建工具链 | Rust `1.100.0`、`.NET SDK 10.0.403`（自包含运行时 `10.0.14`）、MSVC `14.44.35207`、Windows SDK `10.0.26100.0`、Inno Setup `6.7.3` |
| 候选产品源码提交（`source_sha`） | `887f119796311f475792a9d3a56a8f95159cc123` |
| 单文件安装候选包 | `Contain-Setup-x64.exe`（`51,335,691` 字节，未签名 `NotSigned`）<br>SHA-256：`9c862fb8576b34ef6e2b5a85806c793695a1d9b8e16359b0b7cfd19ba08a6c6d` |
| 便携桌面候选包 | `contain-0.5.0-beta.1-windows-x64.zip`（即 `contain-source-windows-x64.zip`）<br>SHA-256：`83d5f4b797c7cd93167050245251516662cc167b5b5aeaa90a9e1b5b764cd59d`<br>`Contain.Viewer.exe`：`8430afbafc45da943a8e2f0e6b8632706d6efaf1cd30d31e8fbaaf8fd349c242`<br>`contain-workflow.exe`：`b0738eec00114f8788e2aa2c6d595fa6f7c8c67cf2e4e2d36904b4df261917a7`<br>`contain.exe`：`2d98839d8c2e9798b1dcfa8701c69b2344fb9bf8a0aa1d97ceec202ca3e30ee5`<br>`desktop-package-spec.json`：`bff266eb22b5460032f3b2f963693d1b2f5a00ec88053b4d611e4dd158a8d3aa` |
| 独立工作流 CLI 候选包 | `contain-workflow-0.5.0-beta.1-windows-x64.zip`（即 `contain-workflow-windows-x64.zip`）<br>SHA-256：`ef297d840acb538f635a54928e3ba7ae30db441cf43659b8f482eb89770d312f`<br>`contain-workflow.exe`：`205faab65858727250551a89b05f370f7680925c36c24768aa6bdde88997f5dc`<br>`cli-package-spec.json`：`176e28c69639cbe6311c8212d72bcecab21ff19d6147e2d083b6d1b7e0882964` |

---

## 历史冻结候选的验收记录

| 环境类别 | 实际执行项 | 结果 |
| --- | --- | --- |
| **普通 Windows CI（GitHub Actions `windows-latest`）** | `CI`（`Check` / `Coverage` / `CoverageOriginal` / `Dependencies` / `Companion` / 33 份只读 CLI JSON schema 校验）与 `Modern desktop`（桌面模型 `57/57`、共享展示回归、导航控件外部 UIA/键盘 `12/12`、现代桌面打包契约 `22/22`、NuGet 漏洞扫描） | **通过**（运行器预装开发工具，不替代干净接收机验收） |
| **Windows 11 x64 开发机（`10.0.26300`）** | 1. 桌面 ZIP 与 CLI ZIP `contain-workflow verify --run`；<br>2. 先安装 `0.4.0-beta.1` 再运行本次 `Contain-Setup-x64.exe`（`0.5.0-beta.1`）原位升级，核实系统卸载列表仅保留 **1 个** `Contain` 条目（版本 `0.5.0-beta.1`）；<br>3. 从开始菜单快捷方式启动安装后的 `Contain · 0.5.0-beta.1`，通过隔离历史记录库确认 3 条旧会话在界面与 CLI 中均可正常读取；<br>4. 选择仅含 `.csproj` 与 `Program.cs` 的全新控制台项目 `HelloReleaseApp`，完成「使用此配置 → 开始检查 → 开始构建 → 生成安装包」，产出 `HelloRelease-Setup-x64.exe`（SHA-256 `4a33436ce17e309a156e6d9f12e9b91c53a5bfa228ad35d9697bd0ea4b329272`）；<br>5. 将 `HelloRelease-Setup-x64.exe` 单独复制到另一目录安装，从开始菜单目标运行 `HelloRelease.exe` 实测输出 `CONTAIN_V050_BETA1_OK`、退出码 `0`，随后标准卸载 `HelloRelease` 与 `Contain`。 | **通过** |
| **断网无开发工具干净 Windows 11 x64 沙箱（`10.0.26100`，两套独立实例）** | 1. **实例 1（无 Contain 环境）**：断网安装 `HelloRelease-Setup-x64.exe`（`4a33436c...`）与 `HelloContain-Setup-x64.exe`（`b1d7a299...`），从开始菜单快捷方式核实入口并运行输出 `CONTAIN_V050_BETA1_OK` / `CONTAIN_DEMO_BUILD_OK_V1`（退出码 `0`），标准卸载后快捷方式与安装目录全部清除；<br>2. **实例 2（全新沙箱）**：断网安装 `Contain-Setup-x64.exe`（`9c862fb8...`），核实唯一卸载登记（`0.5.0-beta.1`），从开始菜单 `Contain.lnk` 启动 `Contain · 0.5.0-beta.1`，通过「打开外部记录库」加载随包 `samples\viewer-demo.db`，浏览 `synthetic-finished` 并读取事件 `event:synthetic-finished-event-0`（`C:\synthetic\observed\recorded.tmp`）右侧完整依据，验证随包 `contain-workflow.exe --version` 正常输出后正常关闭（退出码 `0`）并调用卸载程序：卸载条目归零、开始菜单快捷方式与 `Contain.Viewer.exe` 等全部已安装文件均已移除（直接在程序目录内打开 WAL 模式的 `samples\viewer-demo.db` 时，运行期生成的两个 SQLite 临时侧车文件 `samples\viewer-demo.db-shm` 与 `samples\viewer-demo.db-wal` 按 Inno Setup 不误删安装后新建文件的默认策略留在 `samples\` 子目录中）。 | **通过**（收据已导出保存） |
| **持久干净 Windows x64 快照 A→B 链路** | 快照 A 空白持久环境安装工具前 `-Stage SourceMissing` 与同环境补齐工具后 `-Stage Source` 构建，以及在全新断网无工具沙箱对 A 新生成双 ZIP 与 `handoff.json` 执行 `-Stage Package`（含缺 `coreclr.dll` 坏包启动前拒绝） | **云端未收到最终收据** |

若纳入 CLI 默认只读探测修复，以发布分支实际包含修复的完整提交 SHA 作为新候选的 `source_sha`，在新的输出目录重建桌面及独立 CLI ZIP、两份 package spec 和由桌面 ZIP 生成的 Setup。重新记录哈希，核验两处 `contain-workflow.exe` 的探测行为和随包选择器，复验双 ZIP、Setup 升级及离线安装、启动、卸载，并按新哈希核对 A/B 交接收据。上表的离线试装记录对应表内冻结哈希。
