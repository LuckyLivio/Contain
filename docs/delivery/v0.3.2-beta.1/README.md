# v0.3.2-beta.1 交付身份

`BUILD-IDENTITY.json` 和 `SHA256SUMS.txt` 是已审交付文件的原字节副本；
Windows ZIP 不进入仓库，不重新构建或重新压缩。

- 源码及不可变标签目标：`85c522839523d259c09c80a4ce4e8c78ea11472d`。
- Windows 验证：[CI 41834806392](https://github.com/LuckyLivio/Contain/actions/runs/41834806392)。
- 原产物：`contain-windows-x64-viewer`，artifact ID `13328075262`。
- ZIP SHA-256：`4b5c533b3e45e65fcd579ca45475c92e6e027144d0c73ba3ee6a97bf8a46192f`。

手动工作流 `publish-beta-assets.yml` 仅上传这套已验证资产，使用标准
Actions `GITHUB_TOKEN`；它校验标签、成功的原 CI、ZIP 与原生验证门禁，
只接受现有的草稿 Prerelease，并拒绝替换已有资产。上传后仍保留草稿，
由独立审查核对下载内容后发布。不会重跑构建、安装器或 ETW。

默认实际 DPI 为 96；150%/200% 属于 Windows 控件模拟缩放，不等于实际
系统高 DPI。Windows 11/Parallels 的 7-Zip 结果仅为用户截图反馈。
