# Contain

Windows 安装记录与证据查看 Beta。

通过桌面选择安装器和有限观察范围，记录文件事件，保存并重新查看会话，浏览变化、
解释事件并导出本地报告。使用 WinForms、Rust 核心和现有 CLI；普通桌面使用无需安装
Rust、Visual Studio、Python、PowerShell 7 或独立 .NET 运行库。

## 下载与使用

**[直接下载 Windows x64 自包含 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.8/contain-0.3.2-beta.8-windows-x64.zip)** · [Beta Release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.8) · [SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.8/SHA256SUMS.txt)

版本 **0.3.2-beta.8**。[本版目录说明与完成提示修正](docs/install-guidance-beta8.md)。
发布页提供实际验收的 Windows x64 自包含 ZIP、SHA-256 和 `BUILD-IDENTITY.json`，
其中记录冻结源码、Windows / DPI、原生验收结果和构建身份。

1. **选择安装器。** 完整解压 ZIP，运行 `Contain.Viewer.exe`，点击“新建安装记录”，在弹出的选择器中选择 EXE。取消时返回原来的历史记录。
2. **选择观察文件夹并开始。** 通常选软件准备安装到的已有文件夹：例如安装包在“下载”里、软件准备装到 D 盘，就选 D 盘上对应的已有文件夹。这里仅决定 Contain 观察哪里，也可明确选择其他已有文件夹；选好后点击“开始记录”。名称自动采用 EXE 文件名，默认保存到“我的记录”；需要修改名称、保存位置或记录选项时展开高级设置。安装目标仍由你在安装器里决定，Contain 不会替你创建目标或自动选择范围。
3. **查看结果。** 记录确认结束、保存并读回后，提示“记录已保存，可以查看变化。”，程序会打开本次记录。以后重开程序也能从“我的记录”找到它。在“事件与依据”里输入文件名，可搜索本次记录并把全部匹配结果导出为本地 HTML。

想先看看界面，可以通过“打开记录数据库”打开包内的 `samples/viewer-demo.db`；其中是明确标记的合成演示数据。

个人记录库位于当前用户 Windows LocalApplicationData 下的 `Contain\Records\contain.db`
（通常为 `%LOCALAPPDATA%\Contain\Records\contain.db`）。首次空启动不建库，明确开始记录时
才按需创建。名称可重复，按会话 UUID 关联；位置冲突、不可写、损坏或不兼容时保留原文件，
在启动安装器前说明原因并提供“更改保存位置”。旧数据库兼容读取不迁移原文件。

沿用 Beta.2 的实际步骤、耗时、已处理数量及最近实际进展；未知总量不显示百分比或预计时间。
失败后可查看详情、复制任务摘要并重开准确会话。界面计时、状态接收和实际工作分别报告。

概览来自当前数据库内的保存数据。退出码非零不等于记录失败；未结束历史也不代表采集进程仍在运行。
缺少测量与真实零值分别显示，状态变化数量与详情一致，不将时间线和源操作相加成修改总数。

包内 `TRY.md` 包含使用步骤及明确标记的合成演示库说明。`BUILD.json`、`CONTENTS.json`、
`VALIDATION.json` 和 `SOURCE.zip` 记录源码、配置、运行库、检查结果及哈希。
构建未签名。ETW 需要当前令牌具备相应权限；程序不自动提权、不修改安全设置。
明确选择快照模式时会显示它的实际边界，不会静默当成完整 ETW。

## 第一阶段边界

- 安装器退出、采集完成、数据保存及覆盖质量分别显示。非零退出不统一解释为用户取消，
  保存记录也不等于软件安装成功。
- High 表示有证据支持的执行进程关联；Unknown 表示证据不足。两者都不是最终文件归属
  或安全删除许可。Degraded 表示覆盖受限，不表示已发现恶意行为。
- 只观察指定有限目录及后端支持的来源，不自动扫描全盘或替用户创建软件安装目标。
  注册表配置当前为一个选定 HKCU Software 键；未请求时显示 `not_requested`。
- 事件搜索限定当前数据库和当前会话；ASCII 字母忽略大小写，中文、空格、`%`、`_` 按普通文本包含匹配。
  搜索成功后可导出全部匹配事件，与当前页码或是否选中事件无关；报告列出搜索词、准确会话和全部事件。
  读取期间内容变化、超出限制或失败时拒绝提交，不覆盖已有文件。普通浏览的“导出本页报告”
  仍包含本会话状态变化、当前事件页及所选 explain。
  报告保留本地路径与证据，分享前请自行审阅。
- 本轮不提供安全卸载、回滚、自动清理、完整软件足迹或最终所有权结论。没有会强杀
  安装器的“取消”按钮；异常退出后的未完成证据应保留并重新查看。

## 已知问题与证据来源

用户曾在 Parallels / Windows 11 中手工试用 7-Zip，截图显示在专用目标/观察目录下
安装退出 0、107 项文件变化及可解释的 High 源事件。未取得用户数据库或报告，
不将其写成独立复现；来宾架构和实际 DPI 未确认，不能据此声称 ARM64 已验证。

另一次观察整个 Program Files 的记录停留在 finalizing / Incomplete。缺少原始数据库
和错误日志，根因尚未确认；小范围成功不证明已修复。该问题保留为 Beta 已知限制，
没有通过禁止目录或全盘性能实验掩盖它；继续跟踪于 [Issue #5](https://github.com/LuckyLivio/Contain/issues/5)。

原生 Windows CI、模拟缩放压力、用户手工反馈和未验证条件在
[当前交付记录](docs/install-guidance-beta8.md)中分别列出。此前四个里程碑保持关闭；
[历史交付与失败证据索引](docs/delivery-history.md)保留原始记录，旧包不再并列为当前下载。

## 开发与参考

[Contributing](CONTRIBUTING.md) · [Architecture](docs/architecture.md) ·
[CLI / JSON contract](docs/json-contract.md) · [Desktop](desktop/README.md) ·
[Security](SECURITY.md) · [Measured limits](docs/performance.md)

开发验证使用现有 `scripts/quality.ps1` 与 Windows CI。合成测试、交叉编译及跳过的
检查不计作原生 ETW 或实际系统高 DPI 验证。正式 32 次、规模化和多安装器矩阵不属于
本轮 Beta 收尾；它们不会因界面工作流通过而自动被宣布完成。

## License

Apache-2.0. See [LICENSE](LICENSE).
