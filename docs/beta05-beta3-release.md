# Contain v0.5.0-beta.3 首次打包实测

公开 v0.5.0-beta.2 安装后的首屏是“我的记录”。从侧栏进入“检查项目环境”，再选择文件夹、确认配置、开始检查、开始构建、生成安装包，共 6 个界面步骤。Windows x64 上记录到 8 次鼠标点击、3 次键盘操作和 3 次整页滚动。统计从主窗口就绪到显示第一个 Setup。此后点击“打开所在文件夹”，资源管理器打开了该 Setup 所在的目录。[原版走查](https://github.com/LuckyLivio/Contain/actions/runs/44731452526)

走查使用公开安装包和 `HelloWpf.csproj`，入口为 `HelloWpfDesktop.exe`。生成的安装包通过安装、从开始菜单打开真实窗口、关闭及卸载。以下截图均来自同一次走查，实际窗口为 960 × 650 像素。

[首次打开](first-package-v0.5.0-beta.3/before-first-launch.png) · [选择项目](first-package-v0.5.0-beta.3/before-project-preview.png) · [生成安装包](first-package-v0.5.0-beta.3/before-package-complete.png)
