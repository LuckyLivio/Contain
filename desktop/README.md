# Contain 桌面记录

`0.4.0-beta.1` 的用户入口是新的 **WPF + WPF UI** 桌面，程序名仍为
`Contain.Viewer.exe`。普通流程为“选择 EXE → 明确开始 → 保存 → 查看文件操作 →
下次找回记录”，不再通过旧 WinForms 窗口完成主要操作。

发行状态、当前下载和实际截图见[仓库首页](../README.md)；新版的功能迁移、视觉
约定和展示提纲见[迁移清单](../docs/beta04-design-and-migration.md)，实际执行与
失败记录见[本轮验收记录](../docs/beta04-validation-attempts.md)。本页说明当前实现
和验收契约，**不代表最终 Windows 包已经通过验收**；准确源码、包哈希和发布依据
须由[交付说明](../docs/beta04-delivery.md)及对应运行结果确认。
[Beta.10](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.10)
作为独立回退版本保留，旧标签、附件与历史证据不变。

## 版本与运行方式

| 部分 | 固定版本 / 来源 |
| --- | --- |
| 新桌面与发行版本 | `0.4.0-beta.1`，C#、WPF/XAML、MVVM |
| SDK | `.NET SDK 10.0.403`，根目录 `global.json` 禁止自动滚动版本 |
| 自包含运行时 | `.NET / Windows Desktop 10.0.14`，Windows x64 |
| 通用界面组件 | 第三方开源 `WPF-UI 4.3.0` 与 `WPF-UI.Abstractions 4.3.0` |
| MVVM 支持 | `CommunityToolkit.Mvvm 8.4.2` |
| 采集核心 | 原 Beta.10 `contain.exe`，版本 `0.3.2-beta.10` |
| 核心源码 | `b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4` |

UI 与核心版本分别显示、分别核验，不把原核心重新标成 `0.4.0-beta.1`。新桌面仍
严格要求包内核心的准确版本和 v3 JSON 协议。没有重写 ETW、归因算法、CLI 或
SQLite schema。新版使用原核心二进制的哈希为：

```text
a45f193a84fb3d5556e0258941d9f55bddded677098959c6b1a07b1e724e66f6
```

用户完整解压发行 ZIP 后运行 `Contain.Viewer.exe`，无需安装 .NET、Rust、Visual
Studio、Python 或 PowerShell 7。不要只拷贝 EXE；程序依赖同目录的核心、运行库和
资源。发布不启用 trimming、AOT 或单文件优化。新包只有一个用户主入口，旧
WinForms 版本保留在独立 Beta.10 发行包中。

WPF UI 提供主题、导航、按钮、窗口标题栏、对话框和 Fluent 风格图标；Contain
实现记录任务、会话一致性、证据表达、查询与报告。WPF UI 不是 Microsoft 官方
产品。依赖许可证与版权见 [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md)，
NuGet 依赖由提交的 `packages.lock.json` 锁定。应用使用系统字体及中文回退，不
打包受限系统字体或商业素材。

## 普通使用流程

1. **我的记录。** 启动后读取个人记录库，显示名称、时间、记录状态和覆盖提示。
   首次没有记录时提供新建入口，不先要求选择数据库。同名记录可以区分，实际
   选择、解释和导出始终绑定准确会话 UUID，不按名称或“最新一条”代替。
2. **新建记录。** 在左侧进入新建页面，再点击“选择 EXE”。选择后显示文件名，
   可展开、复制完整路径或更换文件；记录名称自动建议，也可自行修改。选择或
   取消文件选择器均不启动安装器，必须明确点击“开始记录”。
3. **直接开始。** 默认跟随安装器，保存到“我的记录”，无需预先猜测安装目录或
   展开高级设置。开始时通过原 `InstallClient` 自然执行保存位置、协议和实际
   采集来源检查；失败在当前页说明原因，保留已有记录，不自动提权或更换模式。
4. **等待实际任务结束。** 任务区显示后端阶段、步骤、真实可用的进展测量和本地
   续计耗时。安装器可能需要用户继续操作。重复开始被阻止；任务未结束时拒绝
   关闭或切换记录库，不通过强杀安装器来结束窗口。
5. **查看并找回。** 采集进程退出、保存和结果读回确认后，打开本次准确会话，
   明确显示“记录已保存，可以查看文件操作”。之后退出重开、从不同工作目录
   启动，仍从同一个人库找回记录。失败或未完成任务保留原始诊断，并可按准确
   会话打开已经保存的证据，不把部分保存当成完整结束。

个人库沿用 Windows LocalApplicationData 下的 `Contain\Records\contain.db`
（通常为 `%LOCALAPPDATA%\Contain\Records\contain.db`），与 ZIP 位置及当前工作
目录无关。首次空启动不创建目录或数据库；明确开始记录时才按需创建。自定义
保存位置不改变下一次任务的个人库默认位置。

“打开外部记录库”是次要导航入口，始终只读；来源状态与个人库区分。包内
`samples` 是明确标记的合成样例，不是实际安装结果。打开历史或样例记录不会
启动安装器、自动恢复采集、修改记录或清理文件。

## 查看记录、搜索与导出

记录详情分为“概览”“文件操作”“状态对比”“技术详情”。概览分别说明安装器
返回结果、会话是否结束、记录是否读回和覆盖限制。跟随模式没有安装前文件
基线，直接提示未进行前后状态对比，并提供文件操作入口；不以“新增文件 0 个”
暗示没有写入。

“文件操作”保留原时间线中的来源操作、生命周期与状态观察。每页最多显示
50 条，查询由核心完成，前端不读取全部事件后再搜索。输入搜索词并明确搜索后，
查询当前数据库的当前准确会话，显示匹配总数和分页范围；“清除”恢复普通时间线。
搜索仍按核心匹配规则处理：ASCII 字母忽略大小写，中文、空格、`%`、`_` 作为
普通文本。切换会话、翻页或修改搜索时，过期结果与过期错误不能覆盖新选择。

选中一条事件后，界面显示中文说明；原始操作名、完整 JSON 与依据可展开查看。
复制事件 ID、复制完整路径与解释绑定同一条已核验事件。历史缓存路径只是当时
记录的名称，不代表已经验证的当前路径。

- **导出本页报告**用于普通浏览，包含准确会话概览、状态差异、当前事件页及
  所选事件的解释。搜索生效时，应使用全部匹配导出；清除搜索后可恢复本页报告。
- **导出全部 N 条匹配结果**仅在成功搜索后启用，导出完整匹配序列，不受当前页
  或选中行影响。开始导出时固定数据库、会话、查询、匹配数与内容指纹；保存
  对话框期间或导出期间切换页面，也不会混入另一会话或另一搜索条件。
- 导出异步执行，可以停止导出。取消、写入失败或目标已存在均不会覆盖原文件、
  提交部分报告或改变当前选择。读取期间内容变化、超过限制或快照不一致时明确
  失败，不把截断内容写成成功报告。

报告是没有脚本或网络请求的本地 HTML，保留原路径、事件及证据，分享前应自行
审阅。状态差异仍由核心提供，前端分页呈现，可在“状态对比”筛选与检查详情。
技术详情默认折叠，关键覆盖限制始终可见；完整原始错误、损失分项、来源状态与
复制诊断摘要能力保留。

## 高级配置与任务状态

新建页面的“高级设置”保留全部既有能力：手动选择已有观察目录、名称、自定义
数据库、每行一个原样参数、一个 HKCU Software 注册表键、明确选择有限快照
模式、静默等待与最长排空参数，以及额外的“检查环境”。

默认跟随模式记录明确启动的 EXE 与已验证直接子进程；不扫描目录或全盘，也不
提供安装前文件基线。手动目录模式保留指定范围的前后状态比较，但不会为用户
创建软件安装目标目录。注册表或快照配置与跟随模式的互斥、等待上限、数据库
不得位于观察范围内等规则仍由既有 `InstallRequest.Validate` 及核心检查执行。
无效数字输入不会静默采用上一次数值开始记录。

额外的“检查环境”主动探测 ETW 能力，但不启动安装器。当前预检可用不保证之后
采集成功；实际开始仍检查来源。来源不可用时解释错误，用户可修正环境，或明确
改用手动目录与有限快照模式。应用不会自动提权、静默降级或修改系统安全设置。

进度沿用 preparing / capturing / finalizing / finished / failed，以及文件枚举、
来源停止、队列排空、原始记录读回、规范化、证据关联、SQLite 保存、准确会话
读回等实际步骤。未知总量不显示虚假百分比或预计完成时间。状态接收、界面刷新、
本地计时和实际工作进展分别表达；缺少消息本身不是卡死证据。

安装器退出、采集结束、数据保存和覆盖质量是不同事实：

- 退出码 0 不表示最终安装状态已验证；非零退出码的含义由安装器定义。
- 已保存不表示覆盖完整。已确认结束且已读回的丢失会话显示“记录已保存；采集
  期间丢失了部分记录”，不继续要求等待整理；真正未结束的会话保留单独提示。
- 接收、候选、线程间交付、原始保存等损失按原口径展示，不重复相加；历史记录
  缺字段显示“未提供”。缺失值、没有基线和实际零值不混淆。
- `Unknown` 表示当前依据不足，不表示危险。`High` 表示有依据的执行进程关联，
  不是最终文件所有权或安全删除许可。损失后抑制升级的规则保持不变。

不增加自动卸载、残留删除、回滚、路径知识库、服务代理跟随或后台常驻采集。

## 只读、异步与关闭契约

新模型直接复用 `CoreClient`、`InstallClient`、`PersonalRecords`、
`OverviewPresentation`、`ReportExporter`、`SearchReportExporter` 和相邻协议类。
这些源文件仍保存在 `Contain.Viewer` 目录，由新模型项目链接编译，不把旧窗体
编译进新主程序。新 UI 没有另一套直接写数据库的实现。

所有历史查询使用 `--read-only`。SQLite 打开既有原件，单次 CLI 调用保持只读
事务；旧 schema 的兼容迁移只发生在内存备份上。缺失、不相关、损坏和更新版本
数据库明确拒绝，不重建数据库，不删除 WAL/SHM 来掩盖错误。每次查询可能看到
其他进程的后续提交，因此报告会重查会话状态，全部匹配导出还用完整内容指纹
绑定显示过的搜索快照。

核心调用使用 `ProcessStartInfo.ArgumentList`，没有 shell 拼接或参数展开。
stdout/stderr 持续读取，输出上限、查询截止时间和协议校验保留。单个 CLI 响应
上限仍为 64 MiB，普通查询默认超时 60 秒；超限或超时明确失败，不返回伪造空页。
旧格式的核心兼容路径可能在核心内部读取旧事件集，不能据此把所有事件载入 UI。

数据库查询与报告生成不阻塞 UI。模型以取消令牌及请求身份拒绝迟到结果；用户
关闭空闲窗口时先停止并等待读操作与导出清理，再退出。活动安装期间拒绝关闭，
安装桥接不杀死安装器或其进程树。测试使用有隔离标记的目录，错误隔离配置会
拒绝运行，不能退回开发者的真实个人库。

## 项目与开发检查

| 项目 | 职责 |
| --- | --- |
| `Contain.Desktop` | 新 WPF 页面、WPF UI 主题与导航、原生文件选择器；输出 `Contain.Viewer.exe` |
| `Contain.Desktop.Model` | UI 无关 ViewModel、异步会话状态、原客户端与报告实现 |
| `Contain.Desktop.Model.Tests` | 当前固定 34 项模型行为契约；可在 Linux 运行，不提供原生界面证据 |
| `Contain.Desktop.Controls.Tests` | 当前固定 12 项 Windows 导航控件契约：6 项同进程确定性检查及 6 项独立进程 UIA / 键盘 / 退出检查 |
| `Contain.Desktop.Tests` | 当前固定 14 项打包程序验收；独立进程操作真正的 WPF EXE，不调用产品 ViewModel 代替用户动作 |
| `Contain.Viewer.Presentation.Tests` | 保留共享证据展示与历史诊断契约回归 |
| `Contain.Viewer` / `Contain.Viewer.Tests` | 旧 WinForms 源码与历史行为证据；旧界面的通过记录不计入新界面验收 |

以上数字描述固定检查契约，不是本页宣称的通过数量。当前源码的执行结果、失败
与未执行项以对应报告为准，不用重复运行挑选一次绿色结果。

从仓库根目录使用锁定 SDK：

```powershell
dotnet restore desktop/Contain.Desktop.Model.Tests --locked-mode
dotnet run --project desktop/Contain.Desktop.Model.Tests -c Release --no-restore
dotnet restore desktop/Contain.Desktop -r win-x64 -p:SelfContained=true --locked-mode
dotnet publish desktop/Contain.Desktop -c Release -r win-x64 --self-contained true --no-restore -o target/modern-viewer
```

Linux 的受支持 Windows 目标构建仅验证编译，不能运行 WPF 或证明窗口显示正确。
`dotnet publish` 输出也不单独构成完整发行包：
[`package-modern-desktop.py`](../scripts/package-modern-desktop.py) 核对原 Beta.10
ZIP、提取准确核心与所需运行库、加入许可/样例/身份清单，并在原生验收前冻结包。
完整顺序见 [Modern desktop workflow](../.github/workflows/modern-desktop.yml)。

Windows 控件回归使用新的隔离输出目录：

```powershell
dotnet restore desktop/Contain.Desktop.Controls.Tests --locked-mode
dotnet run --project desktop/Contain.Desktop.Controls.Tests -c Release --no-restore -- target/modern-controls-check
dotnet restore desktop/Contain.Desktop.Tests -r win-x64 -p:SelfContained=true --locked-mode
dotnet publish desktop/Contain.Desktop.Tests -c Release -r win-x64 --self-contained true --no-restore -o target/modern-tests
```

独立实包验收的准确参数、隔离 TEMP 与私有产物规则见
[新原生测试说明](Contain.Desktop.Tests/README.md)。原生等待有明确截止时间；合法
异步过渡可暂时缺少控件或仍显示旧会话，但只有正确会话与预期状态才成功。持续
缺失、错误会话、提前退出、非预期 UIA 异常仍失败，保留固定控件标识、阶段与
进程状态。截图来自实际 Windows 窗口，并记录像素尺寸、真实 DPI 与样例/真实
数据来源；模拟缩放不冒充真实高 DPI 或 Parallels 验证。

完整验收固定包含 130 条事件 / 53 条匹配的 ID、顺序、分页、解释与导出检查，
以及默认新 GUI 启动的一次受控真实安装。父进程、直接子进程和无关写入都有
独立依据，继续使用原严格身份、成功完成、损失和资源门槛。还需核对保存的准确
会话、事件解释、正常退出及不同工作目录冷启动。仅运行不带安装夹具的预览会
把四项真实安装检查标为 `not_run`，不能作为发布通过证据。

核心二进制未变时，可复用对应 Beta.10 D4 采集证据；不能用其 WinForms D6 桌面
结果替代新 WPF 集成。仓库质量要求继续由 [AGENTS](../AGENTS.md) 和
[quality skill](../.agents/skills/contain-rust-quality/SKILL.md) 规定，依赖、发布
身份、文件哈希与源码关系另按[交付门槛](../docs/beta04-delivery.md)核验。

## 保留的历史与限制

- [Beta.10 最终发布依据](../docs/beta10-release.md)、[采集修复对照](../docs/beta10-capture-reliability-result.md)
  与[跟随安装器证据边界](../docs/follow-installer-beta9.md)仍适用于其原对象。
- [旧图形安装里程碑](../docs/graphical-install-record.md)保留源码
  `15f214442d69baa0ebd91f5ad12edc5661281779` 和真实 GUI 安装记录。
- 旧只读查看器源码 `4b4d38764ed417e6754b5db798488227ec147e21` 的
  [原生报告](../docs/examples/windows-desktop-viewer-41122708670.json)、
  [早期失败记录](../docs/examples/windows-desktop-viewer-attempts.json)与
  [实际报告样例](../docs/examples/viewer-41122708670-report.html)继续保留。
- [交付历史](../docs/delivery-history.md)集中索引旧版本，不把历史通过报告改写为
  新版界面的通过证据。

C1 首次空库 Ready 超时（[#16](https://github.com/LuckyLivio/Contain/issues/16)）、
D5 结果观察控件缺失（[#17](https://github.com/LuckyLivio/Contain/issues/17)）、用户
Parallels 原会话 57,767 条应用丢失（[#18](https://github.com/LuckyLivio/Contain/issues/18)）
的原始原因仍未确认；另有大范围 Program Files 观察的历史未完成反馈
（[#5](https://github.com/LuckyLivio/Contain/issues/5)）。新界面不追认这些历史问题
已经修复。原生 Windows runner、模拟样例、手工反馈和未覆盖环境分别记录，不
承诺完整软件足迹、最终归属、安全删除或尚未实际验证的平台。
