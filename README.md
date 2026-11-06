# Contain

Windows 安装记录与证据查看 Beta。

通过桌面选择安装器和有限观察范围，记录文件事件，保存并重新查看会话，浏览变化、
解释事件并导出本地报告。使用 WinForms、Rust 核心和现有 CLI；普通桌面使用无需安装
Rust、Visual Studio、Python、PowerShell 7 或独立 .NET 运行库。

## 下载与使用

**[直接下载 Windows x64 自包含 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.5/contain-0.3.2-beta.5-windows-x64.zip)** · [Beta Release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.5) · [SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.5/SHA256SUMS.txt)

版本 **0.3.2-beta.5**。[本版会话事件搜索、验证范围和已知限制](docs/session-event-search-beta5.md)。
发布页提供实际验收的 Windows x64 自包含 ZIP、SHA-256 和 `BUILD-IDENTITY.json`，
其中记录冻结源码、Windows / DPI、原生验收结果和构建身份。

1. 下载并完整解压 ZIP，双击 `Contain.Viewer.exe`。
2. 选择“新建安装记录”，选择 EXE 和**已有观察目录**。文件名会建议记录名称，可修改。
   默认“保存到我的记录”，无需选择数据库；高级设置可自定义保存位置。
   安装目标由安装器界面选择；安装包所在目录不等于软件安装目标或完整观察范围。
3. “检查环境”查看当前可用能力，确认实际范围后点击“开始记录”。只有这一步启动安装器。
4. 等待收尾和保存，自动打开本次准确会话。概览分别展示安装器结果、记录状态、观察范围与覆盖、主要变化。
   点击“查看变化”或“查看事件与依据”浏览当前会话；展开“技术详情”查看来源、统计和诊断。
   在“搜索本次记录”输入文件名、路径、操作或置信标签，可查找本次会话全部事件；
   结果每页最多 50 条，显示匹配总数与范围。选择结果查看同一原始事件的解释。
5. 完全退出后重新打开，从“我的记录”选择刚才的记录。更新或移动解压目录不影响历史。
   “打开记录数据库”继续只读查看外部旧库，不改变下一次新记录的默认位置。

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
  搜索时提示“清除搜索后可导出普通浏览页”，可直接清除。普通 HTML 报告仍包含本会话
  状态变化、普通浏览的当前事件页及所选 explain；完整匹配结果导出留给 Beta.6。
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
[当前交付记录](docs/session-event-search-beta5.md)中分别列出。此前四个里程碑保持关闭；
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
