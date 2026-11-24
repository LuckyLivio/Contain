param(
    [Parameter(Mandatory = $true)]
    [string]$HelloContainSetup,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedHelloContainSha256,
    [Parameter(Mandatory = $true)]
    [string]$ContainSetup,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedContainSha256,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$SourceSha = '05d96546b7bb3f97540e26f1614e9c063fd40212',
    [string]$HelloContainInstallLeaf = 'HelloContain-9a1c05c8',
    [string]$HelloContainShortcutLabel = 'HelloContain (9a1c05c8)',
    [string]$ContainVersionLabel = '0.8.0-rc.1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'inspect-receiver-environment.ps1')

foreach ($pair in @(
    @{ Name = 'HelloContainSetup'; Path = $HelloContainSetup; Expected = $ExpectedHelloContainSha256 },
    @{ Name = 'ContainSetup'; Path = $ContainSetup; Expected = $ExpectedContainSha256 }
)) {
    if ([string]::IsNullOrWhiteSpace($pair.Path) -or !(Test-Path -LiteralPath $pair.Path -PathType Leaf)) {
        throw "Original installer file for $($pair.Name) was not found: '$($pair.Path)'. Existing files left untouched."
    }
    if ($pair.Expected -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Expected SHA-256 for $($pair.Name) must be a 64-character hex digest."
    }
}

$fullOut = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $fullOut) {
    throw "OutputDirectory already exists: '$fullOut'. Choose a new handoff directory so existing artifacts are preserved."
}

$normExpectedHello = $ExpectedHelloContainSha256.ToLowerInvariant()
$normExpectedContain = $ExpectedContainSha256.ToLowerInvariant()

# 1. Verify SHA-256 of both original Setup EXEs BEFORE copying
$preCopyHelloSha = (Get-FileHash -LiteralPath $HelloContainSetup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($preCopyHelloSha -ne $normExpectedHello) {
    throw "Pre-copy SHA-256 mismatch on HelloContain-Setup-x64.exe! Actual: $preCopyHelloSha ; Expected: $normExpectedHello"
}

$preCopyContainSha = (Get-FileHash -LiteralPath $ContainSetup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($preCopyContainSha -ne $normExpectedContain) {
    throw "Pre-copy SHA-256 mismatch on Contain-Setup-x64.exe! Actual: $preCopyContainSha ; Expected: $normExpectedContain"
}

# 2. Copy into clean handoff directory and verify SHA-256 AFTER copying
New-Item -ItemType Directory -Path $fullOut | Out-Null
$dstHello = Join-Path $fullOut 'HelloContain-Setup-x64.exe'
$dstContain = Join-Path $fullOut 'Contain-Setup-x64.exe'

Copy-Item -LiteralPath $HelloContainSetup -Destination $dstHello
Copy-Item -LiteralPath $ContainSetup -Destination $dstContain

$postCopyHelloSha = (Get-FileHash -LiteralPath $dstHello -Algorithm SHA256).Hash.ToLowerInvariant()
$postCopyContainSha = (Get-FileHash -LiteralPath $dstContain -Algorithm SHA256).Hash.ToLowerInvariant()

if ($postCopyHelloSha -ne $normExpectedHello -or $postCopyContainSha -ne $normExpectedContain) {
    throw "Post-copy SHA-256 verification failed! Hello=$postCopyHelloSha Contain=$postCopyContainSha"
}

$helloBytes = (Get-Item -LiteralPath $dstHello).Length
$containBytes = (Get-Item -LiteralPath $dstContain).Length

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# 3. Write SHA256SUMS.txt
$shaSumsText = @(
    "$postCopyHelloSha *HelloContain-Setup-x64.exe",
    "$postCopyContainSha *Contain-Setup-x64.exe"
) -join "`n"
[IO.File]::WriteAllText((Join-Path $fullOut 'SHA256SUMS.txt'), $shaSumsText + "`n", $utf8NoBom)

# 4. Write one-page Chinese receiver walkthrough checklist
$walkthroughTemplate = @'
# 干净 Windows x64 接收机离线安装走查单

> **测试原则**：准备未安装开发工具（无现代 `.NET` 运行时/SDK、Visual Studio、Rust、Inno Setup）的原生 64 位 Windows x64 干净虚拟机快照、沙盒或独立测试机。**每次测试只复制当前正在测试的那 1 个 Setup EXE**，复制后**先断开网络**，全程只需双击运行正在测试的那个安装包（无需源码、项目清单或任何打包工具）。

## 零、开始前核对哈希与接收机状态（1 分钟）
- 对照 `SHA256SUMS.txt` 核对文件哈希（PowerShell：`Get-FileHash .\HelloContain-Setup-x64.exe, .\Contain-Setup-x64.exe -Algorithm SHA256`）：
  - `HelloContain-Setup-x64.exe`：`__HELLO_SHA__`
  - `Contain-Setup-x64.exe`：`__CONTAIN_SHA__`
- 拷入干净环境后**断开网络**（禁用网卡、拔网线或开启飞行模式），记录：Windows 版本号、`x64` 架构、断网状态、系统自带 `.NET Framework` 版本（单独列出），并确认未预装现代 `.NET`、Visual Studio、Rust、Inno Setup。
- 两个包当前均为**未签名候选包（`NotSigned`）**：双击时若弹出 SmartScreen（如“Windows 已保护你的电脑”），请记录实际文字或截图后点 **“更多信息 → 仍要运行”**；若被系统安全策略阻止，无需改系统策略，直接如实记录停在哪一步及提示文字。

## 一、先测 `HelloContain-Setup-x64.exe`（在未安装 Contain 的干净环境）
1. **只复制并运行**：将 `HelloContain-Setup-x64.exe` 单独拷入干净机桌面或下载目录，断网状态下双击运行，按中文向导默认路径点 **“下一步 → 安装 → 完成”**（当前用户安装，无需管理员提权，桌面快捷方式默认关闭）。
2. **从开始菜单启动并核对输出**：
   - 打开 **开始菜单 → 所有应用**，点击 **`__HELLO_SHORTCUT__`** 启动；右键该菜单项选 **“更多 → 打开文件位置”**，查看快捷方式属性，确认目标指向 `%LOCALAPPDATA%\Programs\__HELLO_LEAF__\HelloContain.exe`。
   - 打开 PowerShell 执行：`& "$env:LOCALAPPDATA\Programs\__HELLO_LEAF__\HelloContain.exe"; $LASTEXITCODE`
   - **预期结果**：第一行标准输出为 **`CONTAIN_DEMO_BUILD_OK_V1`**，最后一行退出码为 **`0`**。
3. **标准卸载**：打开 **Windows 设置 → 应用 → 已安装的应用**，搜索 `HelloContain`，点击 **`__HELLO_SHORTCUT__` → 卸载**。确认应用列表条目、开始菜单入口和 `%LOCALAPPDATA%\Programs\__HELLO_LEAF__` 文件夹均已消失。

## 二、再测 `Contain-Setup-x64.exe`（恢复相同干净快照，或使用第二个干净实例）
1. **只复制并运行**：恢复到未装过软件的同一干净快照（或换另一台干净实例），只拷入 `Contain-Setup-x64.exe`，断网状态下双击运行并按默认路径完成安装（默认目录 `%LOCALAPPDATA%\Programs\Contain`）。
2. **启动桌面端并查看随包合成记录**：
   - 打开 **开始菜单 → 所有应用**，点击 **`Contain`**，确认正常显示主窗口 **`Contain · __CONTAIN_VER__`**。
   - 点击窗口左下角 **“打开外部记录库”**，选择 `%LOCALAPPDATA%\Programs\Contain\samples\viewer-demo.db`。
   - 在记录列表中点击 **`Synthetic finished`** → 点击 **“查看文件操作”** → 在左侧事件列表点击第一条 **`写入请求`（`C:\synthetic\observed\recorded.tmp`）**。
   - **预期结果**：右侧“事件依据”面板正常展示该条事件的进程关联与证据说明；随后点击窗口右上角正常关闭 `Contain`。
3. **标准卸载**：打开 **Windows 设置 → 应用 → 已安装的应用**，找到 **`Contain`** 点击 **卸载**。确认应用列表条目、开始菜单 `Contain` 入口和 `%LOCALAPPDATA%\Programs\Contain` 程序目录均已清除。

## 三、怎样把结果交回来（关闭沙盒／回滚快照前先导出！）
请把以下简表填好（连同未签名提示或报错截图）**先复制到沙盒／虚拟机快照外部保存，再关闭或回滚测试环境**：

| 项目 | 填写实际观察结果 |
| --- | --- |
| 接收机系统版本 / 架构 / 网络状态 | 例：Windows 11 24H2 (`10.0.xxxxx`) / `x64` / 已断网 |
| 预装情况（现代 `.NET` / VS / Rust / Inno Setup；自带 `.NET Framework` 单列） | 例：均未安装；系统自带 `.NET Framework 4.8.1` |
| 未签名系统提示文字或拦截位置 | 例：SmartScreen 提示“未知发布者”，点“更多信息→仍要运行”通过（附截图） |
| 1. `HelloContain-Setup-x64.exe`：开始菜单启动 / 输出 / 退出码 / 卸载 | 快捷方式目标一致 / 输出 `CONTAIN_DEMO_BUILD_OK_V1` / 退出码 `0` / 已卸载干净 |
| 2. `Contain-Setup-x64.exe`：开始菜单启动 / 合成记录事件依据 / 卸载 | 窗口正常打开 / 选中事件依据正常显示 / 正常关闭并已卸载干净 |
'@
$walkthroughText = $walkthroughTemplate.
    Replace('__HELLO_SHA__', $postCopyHelloSha).
    Replace('__CONTAIN_SHA__', $postCopyContainSha).
    Replace('__HELLO_SHORTCUT__', $HelloContainShortcutLabel).
    Replace('__HELLO_LEAF__', $HelloContainInstallLeaf).
    Replace('__CONTAIN_VER__', $ContainVersionLabel).
    Replace("`r`n", "`n").TrimEnd() + "`n"
[IO.File]::WriteAllText((Join-Path $fullOut '接收机离线验收走查单.md'), $walkthroughText, $utf8NoBom)

# 5. Probe current host environment and record handoff manifest + local probe log
$envSummary = Get-ReceiverEnvironmentSummary
[IO.File]::WriteAllText(
    (Join-Path $fullOut 'host-environment-probe.local.json'),
    ($envSummary | ConvertTo-Json -Depth 8) + "`n",
    $utf8NoBom)

$manifest = [ordered]@{
    schema_version = 1
    protocol = 'contain-receiver-setup-handoff-1'
    product_source_sha = $SourceSha
    clean_receiver_available_on_current_host = ([bool]$envSummary.is_clean_tool_free_receiver -and ($envSummary.network_state -eq 'offline')) -or [bool]$envSummary.local_clean_vm_or_sandbox_available
    setup_clean_receiver_status = '接收机待实测'
    zip_verify_run_note = 'Developer-machine contain-workflow verify --run receipts are retained separately and do not substitute for clean-receiver Setup installation.'
    packages = @(
        [ordered]@{
            order = 1
            file = 'HelloContain-Setup-x64.exe'
            bytes = [int64]$helloBytes
            pre_copy_sha256 = $preCopyHelloSha
            post_copy_sha256 = $postCopyHelloSha
            sha256_verified_before_and_after_copy = ($preCopyHelloSha -eq $postCopyHelloSha -and $postCopyHelloSha -eq $normExpectedHello)
            signed = $false
            authenticode_status = 'NotSigned'
            clean_receiver_launch_result = '接收机待实测'
            clean_receiver_uninstall_result = '接收机待实测'
        },
        [ordered]@{
            order = 2
            file = 'Contain-Setup-x64.exe'
            bytes = [int64]$containBytes
            pre_copy_sha256 = $preCopyContainSha
            post_copy_sha256 = $postCopyContainSha
            sha256_verified_before_and_after_copy = ($preCopyContainSha -eq $postCopyContainSha -and $postCopyContainSha -eq $normExpectedContain)
            signed = $false
            authenticode_status = 'NotSigned'
            clean_receiver_launch_result = '接收机待实测'
            clean_receiver_uninstall_result = '接收机待实测'
        }
    )
    handoff_files = @(
        'HelloContain-Setup-x64.exe',
        'Contain-Setup-x64.exe',
        'SHA256SUMS.txt',
        '接收机离线验收走查单.md',
        'handoff-manifest.json'
    )
}
[IO.File]::WriteAllText(
    (Join-Path $fullOut 'handoff-manifest.json'),
    ($manifest | ConvertTo-Json -Depth 8) + "`n",
    $utf8NoBom)

Write-Output ($manifest | ConvertTo-Json -Depth 8)
