# v0.4.0-beta.1 冻结包交付入口

`scripts/publish-modern-desktop.py` 只向已有的空 Draft Prerelease 上传经过检查的
原 ZIP 和独立验收附件。它不编译、不重新打包、不创建标签、不覆盖附件，也不把
Draft 改成已发布。Windows 原生验证尚未通过时，此脚本不能成为发布替代路径。

## 输入与来源

在正常 PR 合并、main 的 CI 和 Modern desktop 检查成功后，先核对实际最终截图、
版本、已知限制与发布说明，创建目标为准确 main SHA 的 `v0.4.0-beta.1` 标签及
**空的 Draft Prerelease**。再从 main 手动执行
`.github/workflows/publish-modern-desktop.yml`，提供：

| 输入 | 含义 |
| --- | --- |
| `accepted_run_id` | 原生完整验收通过的 Modern desktop push 运行，第一次 attempt |
| `product_source_sha` | 该运行的完整源码 SHA，也是真实验收包及原生测试的源码 |
| `zip_sha256` | 已冻结、已验收 ZIP 自身的 SHA-256；不是 Actions artifact 外层 ZIP |
| `tag_source_sha` | 现有 Beta.1 标签指向的准确、当前 main 提交 |
| `main_quality_run_id` | 该 main 的成功 CI push 运行 |
| `main_desktop_run_id` | 该 main 的成功 Modern desktop push 运行 |

工作流仅允许原仓库、main、`workflow_dispatch`、第一次 attempt。
它从 GitHub 下载两个已绑定源码/运行的验收 artifact、main 的质量和桌面 artifact，
验证 artifact 自身 digest，再核对原产品 ZIP。产品、ViewModel、控件修复、相关
测试、锁文件、采集核心和验收契约在产品源码到标签源码间必须保持一致；可增加
文档与交付工具。新增控制台测试不等于已验证普通用户 EXE。

## 保持的门槛

- 14 项固定新 WPF 原生检查全部通过；真实 Windows x64、实际 ETW、保存读回、
  正常退出、准确会话及冷启动均确认。预览运行和旧 WinForms 验收不能替代。
- 34 项基础 ViewModel 契约均通过；6 项真实 Windows 导航控件回归通过，既复现
  组件原 Selection 路径问题，也验证生产 `ActionNavigationItem` 的可访问操作。
- 130 条事件、53 条匹配的准确 ID、页序、导出序列和选中事件完全一致。
- 父、直接子进程具备身份与成功完成依据；无关写入不被归给安装器。接收、候选、
  交付、保存、ETW 和解码损失通过原严格检查，资源与排空边界不变。
- 13 张规定截图的 PNG 字节、哈希、窗口尺寸、真实 DPI、样例/真实数据标签匹配。
  实际 96 DPI 是基础。脚本检查身份，发布者仍须查看截图确认美观与可用性。
- main 的仓库质量门槛、模型和原生控件回归通过，NuGet 审计成功且没有已报告漏洞。

沿用 Beta.10 的核心二进制仅复用对应核心采集证据。脚本校验其已发布
`FINAL-ACCEPTANCE.json` 的固定哈希，并引用 D4 对照；不机械重跑全部历史矩阵，
也不把 D6 WinForms 流程视为新 WPF 的验收。

## 附件与不可变性

上传原 ZIP、包内原始 `BUILD.json` 的独立副本、独立 `FINAL-ACCEPTANCE.json`、
`SHA256SUMS.txt`、使用说明、通过的原生/模型/控件摘要、既定计划、脱敏依赖报告、
main 质量摘要和规定截图。只选择已知文件；不递归上传 artifact 中的 HTML、
数据库、WAL/SHM、完整捕获或私人日志。依赖报告去掉 runner 的绝对项目路径，
同时保留原审计文件哈希。

上传前要求 Draft 没有附件；每个附件上传后，通过 GitHub API 回下载校验哈希、
大小和资产 ID。部分上传失败后保留现场并报错，不自动覆盖或挑选已有资产继续。
成功上传仍保持 Draft。发布者核对已认证附件回读后发布 Prerelease，再从公开
下载地址回下载核对相同哈希和包内身份；Draft 的已认证回读不冒充公开下载。

`FINAL-ACCEPTANCE.json` 分别记录产品源码、实际原生测试源码、main、标签对象及
标签目标、ZIP 哈希、核心旧版本源码和复用证据。文档更新不冒充重新运行产品。
C1、D5 和用户原 Parallels 会话的未确认原因继续披露，不宣称迁移已解释历史故障。
