# Contain

Windows 安装记录与证据查看工具。选择 EXE，记录安装行为，找回记录，查看文件操作及其依据。

**当前开发版：v0.4.0-beta.1，最终 Windows 实包验收与发布待完成。** 新桌面使用
WPF + WPF UI + MVVM，接入已有 Rust 核心与 CLI。
原生预览、Linux 构建和旧版本通过结果均不代替最终新包验收。
[新桌面迁移清单](docs/beta04-design-and-migration.md) · [本轮验收尝试](docs/beta04-validation-attempts.md) · [发布说明草稿与两分钟演示](docs/beta04-release-notes-draft.md)

## 下载与使用

新桌面正式下载链接将在最终实包验收后提供。目前已发布的独立回退版是
**[Beta.10 Windows x64 自包含 ZIP（旧 WinForms）](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.10/contain-0.3.2-beta.10-windows-x64.zip)** · [Release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.10) · [校验和](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.10/SHA256SUMS.txt)。
Beta.10 及更早标签和附件保持不变，不能将此回退包当作新 WPF 下载。

新桌面的普通流程：

1. 完整解压 Windows x64 自包含 ZIP，运行 **`Contain.Viewer.exe`**，无需另装 .NET 或开发工具。
2. 进入“新建记录”，**选择 EXE → 明确点击“开始记录”**。默认跟随安装器及已验证直接子进程，名称自动建议，保存到“我的记录”；无需猜安装目录或选择数据库。选择文件本身不会启动安装器。
3. 按安装器要求操作，保持窗口打开至整理保存结束。程序按准确会话打开结果，可搜索文件操作、查看解释、复制事件 ID 或导出报告。以后从“我的记录”找回。

名称可直接修改；高级设置保留手动观察已有目录、自定义保存位置、逐行字面参数、注册表、快照模式和等待参数。
“打开外部记录库”只读查看；`samples/viewer-demo.db` 明确是合成演示数据。

## 新界面截图

最终实际截图待归档与审阅；下列是原生验收程序的输出文件名，不是效果图或已完成声明。

| 画面 | 待归档位置与数据来源 |
| --- | --- |
| 我的记录 | `docs/images/beta04/records-sample.png`；隔离合成样例 |
| 新建配置 | `docs/images/beta04/new-record-configured.png`；受控安装器，尚未点击开始 |
| 保存后的事件解释 | `docs/images/beta04/real-event-explanation.png`；真实 ETW 采集 |

每张最终截图须注明窗口尺寸、实际 DPI、版本与环境，完整验收仍须包含空态、真实任务、概览和紧凑窗口。

个人库沿用 Windows LocalApplicationData 下的 `Contain\Records\contain.db`，通常为
`%LOCALAPPDATA%\Contain\Records\contain.db`，不随启动目录变化。首次空启动不建库；
同名记录按 UUID 区分。损坏、不兼容或保存位置冲突时保留原文件，既有库只读兼容查询不迁移原文件。

UI 为 `0.4.0-beta.1`，核心保持 `0.3.2-beta.10` / [源码 b0d2510](https://github.com/LuckyLivio/Contain/tree/b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4)。
新包 `START-HERE.txt`、`BUILD.json`、`CONTENTS.json` 及独立最终验收文件分别说明使用、
产品与核心身份、文件哈希和实际验收对象；[完整版本与核心哈希](docs/beta04-release-notes-draft.md)。
构建未签名；ETW 受当前令牌权限限制。程序不自动提权、不修改安全设置，也不静默切换快照模式。

## 证据与使用边界

- 安装器退出、采集完成、数据保存及覆盖质量分别显示。非零退出不统一解释为用户取消，
  保存记录也不等于软件安装成功。未结束历史不证明进程仍在运行；缺少测量、无基线与零值分别表达。
  实际进展与界面计时分开，未知总量不显示百分比；失败可查看原文、复制摘要并重开准确会话。
- High 表示有证据支持的执行进程关联；Unknown 表示证据不足。两者都不是最终文件归属
  或安全删除许可。Degraded 表示覆盖受限，不表示已发现恶意行为。
- 跟随模式限定明确启动的 EXE 与已验证直接子进程，不扫描目录或全盘，也没有安装前基线；状态差异零项不表示没有操作。
  历史路径不表示当前路径。手动目录模式保留指定范围的前后对比，均不替用户创建软件安装目标。
  注册表配置当前为一个选定 HKCU Software 键；未请求时显示 `not_requested`。
- 事件搜索限定当前数据库和当前会话；ASCII 字母忽略大小写，中文、空格、`%`、`_` 按普通文本包含匹配。
  搜索成功后可导出全部匹配事件，与当前页码或是否选中事件无关；报告列出搜索词、准确会话和全部事件。
  读取期间内容变化、超出限制或失败时拒绝提交，不覆盖已有文件。普通浏览的“导出本页报告”
  仍包含本会话状态变化、当前事件页及所选 explain。
  报告保留本地路径与证据，分享前请自行审阅。
- 本轮不提供安全卸载、回滚、自动清理、完整软件足迹或最终所有权结论。没有会强杀
  安装器的“取消”按钮；异常退出后的未完成证据应保留并重新查看。

## 已知问题与证据来源

Beta.10 的首次空库 C1 Ready 超时、D5 结果观察控件缺失、用户 Parallels 原会话
57,767 条应用丢失的原因仍未确认，分别跟踪于 [#16](https://github.com/LuckyLivio/Contain/issues/16)、
[#17](https://github.com/LuckyLivio/Contain/issues/17)、[#18](https://github.com/LuckyLivio/Contain/issues/18)。
界面迁移不追认其根因，也不将它们写成已修复。Beta.10 的 D4 采集对照与 D6 WinForms
验收保留在[旧版发布记录](docs/beta10-release.md)，只有未改变核心的证据适用于本次复用。

用户曾在 Parallels / Windows 11 中手工试用 7-Zip，截图显示在专用目标/观察目录下
安装退出 0、107 项文件变化及可解释的 High 源事件。未取得用户数据库或报告，
不将其写成独立复现；来宾架构和实际 DPI 未确认，不能据此声称 ARM64 已验证。

另一次观察整个 Program Files 的记录停留在 finalizing / Incomplete。缺少原始数据库
和错误日志，根因尚未确认；小范围成功不证明已修复。该问题保留为 Beta 已知限制，
没有通过禁止目录或全盘性能实验掩盖它；继续跟踪于 [Issue #5](https://github.com/LuckyLivio/Contain/issues/5)。

新桌面原生运行环境为 Windows Server 2025 x64 / 10.0.26100 / 实际 96 DPI；最终包
完整验收仍待完成。未验证 Windows 10/11、Parallels、实际 144/192 DPI、多显示器切换
或任意第三方安装器矩阵。原生执行、模拟缩放、手工反馈各自标明，
[历史交付与失败证据索引](docs/delivery-history.md)保留旧结论。

## 开发与参考

[Contributing](CONTRIBUTING.md) · [Architecture](docs/architecture.md) ·
[CLI / JSON contract](docs/json-contract.md) · [Desktop](desktop/README.md) ·
[Security](SECURITY.md) · [Measured limits](docs/performance.md)

新桌面锁定 .NET SDK `10.0.403`、自包含运行时 `10.0.14`、WPF UI `4.3.0` 与
CommunityToolkit.Mvvm `8.4.2`。WPF UI 是第三方开源组件，不是 Microsoft 官方产品；
Contain 负责真实业务接入与证据呈现。见[依赖许可与版权](THIRD-PARTY-NOTICES.md)。

开发验证使用现有 `scripts/quality.ps1` 与 Windows CI。合成测试、交叉编译及跳过的
检查不计作原生 ETW 或实际系统高 DPI 验证。正式 32 次、规模化和多安装器矩阵不属于
本轮 Beta 收尾；它们不会因界面工作流通过而自动被宣布完成。

## License

Apache-2.0. See [LICENSE](LICENSE).
