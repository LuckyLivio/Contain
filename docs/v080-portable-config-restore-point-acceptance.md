# Contain v0.8.0-beta.1 / v0.8.0-beta.2 / v0.8.0-rc.1「配置还原点迁移」验收记录

## 1. Beta.1 候选包与构建身份

- **功能分支**：`feat/portable-config-restore-point`（基于已发布 `v0.7.0` 的 `main`）
- **构建提交 SHA**：`e23c24d2dbee462a2b4f8a09c84a30f0cd235eac`
- **产品版本**：`0.8.0-beta.1`（PE / AssemblyVersion：`0.8.0.1`；采集核心保持 `0.3.2-beta.10`）
- **发布状态**：本地候选包 + Draft PR（公开稳定版仍为 `v0.7.0`，本次不合并、不打标签、不公开发布）

| 候选产物 | 体积（字节） | SHA-256 |
| --- | ---: | --- |
| `Contain-Setup-0.8.0-beta.1-x64.exe` | 51,574,618 | `701f1ea49ddf9d1a62d436a3952db4e8cf2fe48704ba197e685ca3b549f83619` |
| `contain-0.8.0-beta.1-windows-x64.zip` | 71,506,401 | `d2b43e79008ce7072034b48028fbc6ba2c32de3180ec2c4049c62dab6f4d44f1` |
| `package-spec.json` | 34,413 | `08766daaf80a9ae8342e2b30f77cce5dc36a73b1a6b4a6633711e9520df1519c` |
| `identity.json` | 667 | `507f44ec7b01978be4955b6bc6b976aa0c5bea7a2c0dce8bcd1de0eb9bd58441` |
| `installer-receipt.json` | 917 | `5caf26ceb43d57aaee3dfe5d0df6309e660b9d7c5186b942ab3e9bf07141ef18` |
| `SHA256SUMS.txt`（自校验） | 476 | `9ed376ecf61d200c3254c98dfaa9754468293db1ed18937e5fb0e2492f57406d` |

> 说明：Windows 安装程序 `Contain-Setup-0.8.0-beta.1-x64.exe` 为未签名本地候选构建（`NotSigned`），默认按当前普通用户权限安装至 `%LOCALAPPDATA%\Programs\Contain`，配置还原点迁移全流程离线运行，无需管理员权限或 Windows Sandbox。

---

## 2. 迁移包格式与交互设计要点

### 2.1 单文件版本化迁移包（`.contain-config-point`）

- 顶层固定字段：`schema_version = 1`、`format = "contain-config-restore-point-package"`、`exported_at_utc`、`created_at_utc`、`display_file_name`、`note`、`byte_length`、`sha256`、`encoding_label`、`line_ending_label`、`payload_base64`。
- **源机脱敏隔离**：迁移包刻意排除 `target_path`、`target_directory`、`parent_folder_name`、`point_id` 及所有撤销记录，不携带旧电脑目录路径或主机状态。
- **文件名仅作展示**：`display_file_name` 仅用于导入校验摘要展示和文件选择器默认文件名，绝不自动决定接收机写入路径；包含路径分隔符或非法字符的包名在打开校验阶段即被拒绝。
- **完整性前置校验**：导入打开阶段即只读校验格式标识、版本号、Base64 载荷解码、字节长度和 SHA-256；任何未知格式、损坏内容、长度不符或摘要篡改均在接触本机目标文件前以清晰中文错误拦截。

### 2.2 「导出还原点…」与「另存原副本…」的明确区分

- **「导出还原点…」**（详情头部主操作区）：始终导出所选还原点**建点时保存的原始字节备份（A）**，即使源机磁盘上的文件已被改为 C 或已删除仍可导出 A；打开导出确认卡片时明确显示备份版本信息与隐私提醒（提示配置文件可能包含令牌、账号、密钥或私人路径，Contain 原样保留原始字节以保证恢复逐字节一致，且迁移包不含源机目录路径或撤销记录）。
- **「另存原副本…」**（恢复冲突 / 中断恢复核对卡片与原副本只读预览）：用于救回恢复操作覆盖前的本机原始副本（B），按钮工具提示与界面文案明确区分两者用途。

### 2.3 复用 v0.7.0 恢复、撤销、并发锁与中断核对引擎

- 接收机确认导入恢复时，在本机用户数据目录创建全新的本地还原点身份（新 `point_id`，绑定到接收机的本机目标路径），随后复用 v0.7.0 的内核级跨进程目标写锁与分阶段崩溃安全核心执行写入：
  - 目标文件存在（B）：覆盖前先保存并校验 B 副本，写入原始字节 A（保留 UTF-8 BOM 与 CRLF），恢复后支持撤销预览并逐字节撤回 B；
  - 目标文件缺失：预览阶段明确展示将新建的完整路径，确认后创建 A，撤销预览预告「确认撤销（移除重建文件）」，确认后干净删除新建文件；
  - 确认期间目标被外部修改：拦截本次写入、保留外部新改动并刷新「本机当前 → 恢复后」差异，再次确认后可正常写入并撤销回最新覆盖前版本；
  - 双窗口并发或操作中断：沿用 v0.7.0 跨进程锁阻塞与只读启动核对流程。

---

## 3. 自动化受控测试（代码基线 `e23c24d`）

| 检查项 | 结果 | 覆盖重点 |
| --- | --- | --- |
| `dotnet build Contain.Desktop / Controls.Tests / Tests -c Release` | 0 警告 / 0 错误 | 桌面端、模型层与全部测试工程 Release 编译通过 |
| `Contain.Desktop.Model.Tests` | **133 / 133 通过** | 含 7 项 `v0.8.0-beta.1` 新增迁移测试：源改 C/删除后导出 A 且不含源路径、异路径导入预览/取消/恢复/撤销、缺失目标新建与撤销移除、8 类异常包拒绝、确认期间外部修改保护、复用跨进程锁与中断恢复核对、二进制载荷与同内容拦截 |
| `scripts/test-package-source-desktop.py` | **10 / 10 通过** | `0.8.0-beta.1` 打包契约与窗口标题绑定校验 |
| `scripts/check-fuzz-lock.py` | **通过** | fuzz 与生产依赖锁一致性校验 |
| `cargo test -p contain-workflow --locked` | **55 / 55 通过** | 4 项单元测试 + 51 项集成测试 |
| `cargo deny --locked check` | **通过** | advisories / bans / licenses / sources 全绿 |

---

## 4. 最终候选包跨环境真实 GUI 验收

> 以下结果均基于最终候选产物（`contain-0.8.0-beta.1-windows-x64.zip` 与 `Contain-Setup-0.8.0-beta.1-x64.exe`，构建提交 `e23c24d`）在隔离源机与独立断网无 Windows Sandbox 接收机上通过真实桌面 GUI 端到端实测产出，完整机器回执保存在本地候选目录 `v080b1-cross-env-acceptance-receipt.json`。

### 4.1 关键配置版本与校验摘要汇总

| 对象 | 编码与换行 | 字节数 | SHA-256 |
| --- | --- | ---: | --- |
| 源机建点备份 **A**（`theme: light`） | UTF-8（含 BOM）· CRLF | 163 | `114195afa1ce1ca64b71debd5f41014da1cbc2f99f6dcd1cd154334caaf6343a` |
| 源机后续修改版本 **C**（`theme: high-contrast`） | UTF-8（含 BOM）· CRLF | 171 | `d82e48689fa3ea21328f1dd26dedce7daa1b1ebb34fcfe78338fd83e67658f25` |
| 导出的迁移包 `.contain-config-point`（v1） | UTF-8 JSON | — | `f46507bf5bbda39a3600a957fabf4fa538579004819b57bb150572df009602f5` |
| 接收机原配置 **B**（`theme: dark`） | UTF-8（含 BOM）· CRLF | 160 | `6f90a054d0de67e3280ece724b5f5bbd62633c65012b68d85c3c4f80e0ceafda` |
| 接收机确认期间外部改写 **B2**（`theme: solarized`） | UTF-8（含 BOM）· CRLF | 161 | `e99e25515f0b589ebec15572407e2efb90a4df3b3d70939bf787fcb994dc9c2e` |

### 4.2 源机端实测（公开 `v0.7.0` 建点 → 改源 C / 删源文件 → 最终 `v0.8.0-beta.1` 导出）

1. 使用已公开发布的 **`v0.7.0`**（`FileVersion=0.7.0.0`）在隔离数据目录中为中文 + UTF-8 BOM + CRLF 合成配置文件 `settings.json`（版本 **A**）与辅助文件 `secondary.ini` 创建配置还原点。
2. 关闭 `v0.7.0` 后，将源机磁盘上的 `settings.json` 修改为版本 **C**，并将 `secondary.ini` 从磁盘删除。
3. 使用最终候选 **`v0.8.0-beta.1`**（`FileVersion=0.8.0.1`）打开同一隔离数据目录：
   - 正确加载 `v0.7.0` 已有还原点，将 `settings.json` 识别为「已变化」，点击「导出还原点…」展示备份版本摘要与完整隐私提醒，导出迁移包 `migrated-settings.contain-config-point`。
   - 校验迁移包解码后原始字节 SHA-256 严格等于 **A**（`114195af…`），且迁移包 JSON 中不含任何源机目录路径、`point_id` 或撤销记录；源机磁盘 `settings.json` 保持 **C**（`d82e4868…`）。
   - 对已删除的 `secondary.ini`（状态「已缺失」）同样成功导出建点时原始字节（`0fedf7ca…`），且未重新创建已删除源文件。

### 4.3 接收机端实测（独立断网、无 Windows Sandbox 的 Windows 11 Build 26100）

- **接收机环境实测核验**：`Microsoft Windows 11 企业版 (Build 26100)`，`wsb_present = false`（无 Windows Sandbox 命令），`offline_verified = true`（`default_route_count = 0`，完全断网）。
- **正常安装与版本核验**：运行最终 `Contain-Setup-0.8.0-beta.1-x64.exe`（退出码 `0`），安装在当前用户目录，核验 `FileVersion = 0.8.0.1`、`ProductVersion = 0.8.0-beta.1+e23c24d...`、卸载注册表 `DisplayVersion = 0.8.0-beta.1`。
- **真实 GUI 逐项实测结果**：
  1. **异常迁移包拦截**：依次打开未知格式（`unknown-foreign-package`）、损坏 JSON（截断文件）与长度不符（声明 183 字节、实解 163 字节）三个异常包，界面均显示明确中文错误提示且不提供确认按钮，接收机文件 B 哈希未变动。
  2. **合法迁移包导入与 B → A 预览截图**：打开从源机导出的迁移包，校验通过并展示 `settings.json`、163 B、UTF-8（含 BOM）、CRLF、SHA-256 与备注；选择接收机异路径目标 `C:\Users\Public\ReceiverConfig\settings.json`（当前内容为 B），界面明确显示「本机当前 → 恢复后」、完整目标路径、`- "theme": "dark"` / `+ "theme": "light"` 及 `- "shortcut": "Ctrl+Alt+K"` / `+ "shortcut": "Ctrl+Shift+P"` 逐行差异与主确认按钮。150% 高 DPI 下在 `1280×860` 窗口中目标路径、操作方向、确认按钮与红绿逐行差异全部同屏可见（`all_key_elements_visible_simultaneously = true`）：

![接收机导入预览实机截图（1280x860，150% 高 DPI）](v0.8.0-beta.1/contain-v080b1-import-preview.png)

  3. **取消不写盘**：点击「取消（不写入磁盘）」后，目标文件 SHA-256 仍严格等于 **B**（`6f90a054…`），不生成本地还原点或撤销记录。
  4. **主路径迁移（B → A → 撤销 → B）**：重新预览并确认恢复后，目标文件 SHA-256 变为 **A**（`114195af…`，实测保留 UTF-8 BOM + CRLF），本地新建绑定接收机路径的还原点并出现撤销栏；点击撤销进入「当前文件 → 撤销后」预览并确认后，目标文件逐字节恢复为 **B**（`6f90a054…`）。
  5. **确认期间外部修改保护（B → B2）**：预览 B 期间在外部将目标文件改写为 **B2**（`theme: solarized`），点击确认恢复后 Contain 拦截写入、完整保住 **B2**（`e99e2551…`）、弹出外部修改提醒并自动将差异刷新为 `- "theme": "solarized"` / `+ "theme": "light"`；再次确认写入 **A**，随后撤销精确回到 **B2**（`e99e2551…`）。
  6. **目标文件缺失（新建 A → 撤销移除）**：选择本机尚不存在的完整路径 `C:\Users\Public\ReceiverNewProfile\editor\settings.json`，预览显示「本机当前（缺失） → 恢复后（将新建）」与完整新建路径；确认后成功新建文件且 SHA-256 严格等于 **A**；撤销预览按钮预告「确认撤销（移除重建文件）」，确认后文件被干净删除。
  7. **干净卸载与源机终态复核**：关闭窗口后执行卸载器（退出码 `0`），安装目录与卸载注册表项清理干净；复核源机原 `settings.json` 仍严格保持 **C**（`d82e4868…`）。

---

## 5. v0.8.0-beta.2「导出验证可用后再替换原文件」

### 5.1 冻结 Beta.1 现状基线复现（`e23c24d` 候选包，专用隔离测试目录）

使用冻结的 Beta.1 候选包 `contain-0.8.0-beta.1-windows-x64.zip`（`FileVersion=0.8.0.1`，SHA-256 `d2b43e79008ce7072034b48028fbc6ba2c32de3180ec2c4049c62dab6f4d44f1`）通过正常桌面 GUI 实测，完整复现了 Beta.1 导出先写目标后回读导致的旧包破坏与残留问题（原始机器回执保存于本地候选目录 `v080b1-baseline-repro-receipt.json`）：

| 基线场景（冻结 Beta.1） | 界面结果 | 目标文件变化（字节 / SHA-256） | 事后能否在 GUI 导入 |
| --- | --- | --- | --- |
| **场景 1a：33 MiB 覆盖已有合法小包**（先导出合法小包并确认可在导入界面打开：`校验通过（v1）· small-settings.json · 103 B`） | `导出还原点失败`：`迁移包声明的字节长度无效（34603008）。` | **覆盖前**：`691 B`，`a667ff0f86ad2bfc1e534f8389e311a63d68139bd6058e7e908608471357872e`<br>**覆盖后**：`46,137,912 B`，`8e8dcd509cde2216ed1ac429183dd0f09b97683d8c313a0d83d18911d7928f11`（**原包被覆盖破坏**） | **不能**（`can_import = false`，报错：`迁移包声明的字节长度无效（34603008）。`） |
| **场景 1b：33 MiB 导出到全新文件名** | `导出还原点失败`：`迁移包声明的字节长度无效（34603008）。` | **磁盘遗留打不开的文件**：`46,137,911 B`，`7023ecee682bb8446bd400fcddc973eb39361ffbdb96023eb8127acd994fd88b` | **不能**（`can_import = false`，报错：`迁移包声明的字节长度无效（34603008）。`） |

### 5.2 Beta.2 导出顺序修正与约束

沿用 v1 格式（`schema_version = 1`）、本地还原点 `64 MiB` 上限、迁移原始内容 `32 MiB` 上限与包体 `48 MiB` 上限：

1. **前置 32 MiB 迁移上限判断（写盘前拦截）**：校验本地备份字节长度超过 `32 MiB` 时，在不创建临时文件、不触碰目标文件的前提下直接返回失败，并在界面明确提示：**「这份还原点仍可在本机恢复，超过 32 MiB 的配置暂不支持迁移」**。
2. **目标目录临时文件流式写出**：允许导出的还原点先在目标同目录写入独立 `.tmp` 临时文件（`FileOptions.WriteThrough` + `Flush(flushToDisk: true)`），绝不提前覆盖用户原文件。
3. **正式导入器完整回读校验**：在替换目标前调用正式导入器 `InspectPortablePackage(tempPath)`，逐项核对格式标识、版本号 v1、包体体积（`<= 48 MiB`）、解码载荷长度、展示文件名与 SHA-256。
4. **验证通过后再原子替换目标 & 失败必清理临时文件**：仅当正式导入器校验全部通过后才原子替换（`File.Replace` / `File.Move`）目标文件；写入、回读校验或目标替换（如文件被占用/只读）任一步失败时，已有目标文件逐字节保留，新目标保持不存在，`finally` 保证清理本次临时文件。

### 5.3 Beta.2 候选包身份与自动化验证

- **构建提交 SHA**：`885cd4d1d5b05ab9ff6b98cf243e410791df1b17`
- **产品版本**：`0.8.0-beta.2`（PE / AssemblyVersion：`0.8.0.2`；采集核心维持 `0.3.2-beta.10`）
- **发布状态**：本地候选包 + Draft PR #33（公开稳定版仍保持 `v0.7.0`）

| Beta.2 候选产物 | 体积（字节） | SHA-256 |
| --- | ---: | --- |
| `Contain-Setup-0.8.0-beta.2-x64.exe` | 51,580,383 | `ecb6fb995c8c594039ede8aa9c192d6c45037d4a2be9d6716b1a43de3ea1032f` |
| `contain-0.8.0-beta.2-windows-x64.zip` | 71,504,547 | `2a9e6ea1b898f01e95367477ca255e16a829c0a45c23116fc6ecfa3157e40d2e` |
| `package-spec.json` | 34,413 | `edb59f50abc3950c191ca0ca9f48d25f374a2ffeffe92ab96505b2bf0730ac5e` |
| `identity.json` | 667 | `13725796e2e7e6c7df0fd9fa7b458307bd831b95885ee7e1476c6733786c3fb3` |
| `installer-receipt.json` | 917 | `7200db86060a44f2459a80d303f028ffeb4e3193c2a5ca44946bd200899ff1bf` |
| `SHA256SUMS.txt`（自校验） | 476 | `ed5732bc3832d35ded886ab2c2d9a27348f9905948b8cf891c2b6a78bfcf6f0b` |

- **自动化受控测试（`885cd4d`）**：Desktop Release 0 警告/0 错误、`Contain.Desktop.Model.Tests` **138 / 138 通过**（新增 5 项 Beta.2 专项测试：33 MiB 前置拦截保护旧包/新路径、恰好 32 MiB 导出重导回环、正常覆盖旧包后接收端恢复新包与撤销回本机 B、**临时包回读校验失败保护已有/新目标并清理临时文件**、**目标文件被独占占用及只读保护原包并清理临时文件**）、`test-package-source-desktop.py` 10 / 10、`check-fuzz-lock.py` 通过、`cargo test -p contain-workflow --locked` 55 / 55、`cargo deny --locked check` 通过。

### 5.4 Beta.2 最终候选包真实 GUI 三项结果验收

以下结果基于最终 Beta.2 候选 ZIP（隔离源机）与安装最终 `Contain-Setup-0.8.0-beta.2-x64.exe`（`FileVersion=0.8.0.2`，`DisplayVersion=0.8.0-beta.2`）的独立断网（`default_route_count = 0`）、无 Windows Sandbox（`wsb_present = false`）Windows 11 Build 26100 接收机通过正常桌面 GUI 端到端实测产出，完整机器回执保存在本地候选目录 `v080b2-candidate-acceptance-receipt.json`。

#### 结果 ①：33 MiB 导出失败、旧包不变且可恢复（三项独立结论）

1. **界面提示失败**：对 33 MiB（`34,603,008` 字节，SHA-256 `2d58f0eb0c35cb7bcf0653ea18869a86d4b1c0d00bb3783e3d4a5c832b47d9dd`）本地还原点选择原合法小包确认覆盖时，界面明确提示失败：`导出还原点失败：超过 32 MiB 迁移上限` — `这份还原点仍可在本机恢复，超过 32 MiB 的配置暂不支持迁移。当前备份大小为 33.00 MB（34,603,008 字节），未修改目标文件或写入任何临时文件。`（导出到全新文件名同样提示此说明，目标文件保持不存在，临时文件残留 `0`）。
2. **旧包字节保留**：目标旧包覆盖前为 `700 B`、SHA-256 `884646defd00b28ad410eb7938cb5602477a9b27c93825c20c0a176f9644da40`，33 MiB 导出失败后仍严格为 `700 B`、SHA-256 `884646defd00b28ad410eb7938cb5602477a9b27c93825c20c0a176f9644da40`（逐字节一致，目录临时文件残留 `0`）。
3. **旧包仍能实际恢复**：在独立断网接收机 GUI 导入该旧包，校验通过（`校验通过（v1）· small-v1.json · 125 B · UTF-8（含 BOM） · CRLF (Windows) · SHA-256：583aa9fb9e8f…`），确认恢复后目标文件 SHA-256 严格等于原内容 **V1**（`583aa9fb9e8f203a07d82c1c3769840ded22b71b77219518be41dd64ef55300c`），点击撤销后逐字节回到接收机原配置 **B**（`6fbf8fb0ac53093cdd6153488b20385b687d0a968b36d2d52831d2bff5d76e2b`）。

#### 结果 ②：原始内容恰好 32 MiB 的还原点可导出并重新导入恢复

- 合成配置恰好 `32 MiB`（`33,554,432` 字节，SHA-256 `c194f29bf0ae617dfabba47f11b3e263cabb16c6e6ca200e03fca9b1b691b0f7`），在正常 GUI 导出成功，序列化迁移包大小 **`44,739,819` 字节**（处于 `50,331,648` 字节 / 48 MiB 上限内，SHA-256 `4a820cc46e5fcbf66d702ab09b8894b71b243d72ef50899378d6a3b7602ef18c`，临时文件残留 `0`）。
- 接收机通过正常 GUI 打开并校验通过（`校验通过（v1）· exact-32mib.cfg · 32.00 MB（33,554,432 字节） · 大文件（已跳过文本解码） · SHA-256：c194f29bf0ae…`），确认恢复后目标文件恰好 `33,554,432` 字节且 SHA-256 严格等于 `c194f29b…`，点击撤销后逐字节回到接收机原配置 **B**（`6fbf8fb0…`）。

#### 结果 ③：正常覆盖旧包成功，接收端恢复新包内容且撤销回到本机原配置

- 已有旧包 V1（`91905e983ac15ec92a230edf9ce83c7fe69684f835a146cd5654da7701cc692d`）被新还原点 **V2**（`theme: nord`，内容 SHA-256 `97575f169efb5418788ecb1498515c73c8445143aa02e43b0808d39b334812f4`）正常覆盖成功，更新后迁移包 SHA-256 为 `b820e5a9a4952fd387d0e48dfb33a2cd1f35ffb2719815182fdda9fc3ba44dc4`（临时文件残留 `0`）。
- 接收机在正常 GUI 导入该覆盖后的包，预览准确显示 `- "theme": "dark"` / `+ "theme": "nord"`，确认恢复后磁盘文件 SHA-256 严格等于 **新包 V2**（`97575f16…`），点击撤销后精确回到接收机自己的原配置 **B**（`6fbf8fb0…`）。
- 接收机卸载器退出码 `0`，安装目录与卸载注册表项清理干净。

---

## 6. v0.8.0-rc.1 真实 VS Code 端到端验收、覆盖升级与 Beta.2 兼容

### 6.1 RC.1 候选包与构建身份

- **基线提交**：已验收 Beta.2 构建提交 `885cd4d1d5b05ab9ff6b98cf243e410791df1b17`
- **RC.1 构建提交 SHA**：`e34ef46097760074f81aea10b6dd3323ae3c1cb1`
- **产品版本**：`0.8.0-rc.1`（PE / AssemblyVersion：`0.8.0.3`；采集核心保持 `0.3.2-beta.10`）
- **发布状态**：本地 Windows x64 候选包 + Draft PR #33（公开稳定版仍为 `v0.7.0`，本轮不合并、不打标签、不公开发布）

| RC.1 候选产物 | 体积（字节） | SHA-256 |
| --- | ---: | --- |
| `Contain-Setup-0.8.0-rc.1-x64.exe` | 51,563,509 | `da26116bd54291e5dc428a2d41d2399447df0c3a58e3622d27d553a51ed8c2ea` |
| `contain-0.8.0-rc.1-windows-x64.zip` | 71,505,764 | `3103eb7eb7820e7ede319863b21a254acc8c90976aa8bbb264938aa3751b310b` |
| `package-spec.json` | 34,411 | `0bf1e96d9b6a91c1903a6069f6505204cb819687bdd3eefef622ea04ed9c0f68` |
| `identity.json` | 667 | `bced09bbe2bb4da1dbe5bed11ef1e949851f0aaf877b5d01881698870021d7e4` |
| `installer-receipt.json` | 991 | `94c4fdb7fc5e6eeeba7403fd4c6f59749931bdf92bbc966d53993035f70fe2e3` |
| `SHA256SUMS.txt`（自校验） | 453 | `891405962c2154bc242d858b9f9cf6a1f7e4ee12ec7496b949e30d390d877ffa` |

> 说明：
> 1. `git diff 885cd4d -- desktop/Contain.Desktop.Model/ConfigRestorePointService.cs desktop/Contain.Desktop.Model/ConfigRestorePointModels.cs` 输出为空，确认 RC.1 未触碰迁移包序列化、原子导出替换与 32/64 MiB 体积上限逻辑，直接沿用第 5 节 Beta.2 的 32 MiB / 33 MiB 覆盖保护与接收端实测回执。
> 2. 自动化门禁全绿：`Contain.Desktop.Model.Tests` **139/139**（含新增 `config_v080rc1_import_target_help_guidance_and_no_auto_guess_preserves_decoy_settings_json`）、`test-package-source-desktop.py` **10/10**、`check-fuzz-lock.py`、`cargo test -p contain-workflow --locked` **55/55**、`cargo deny --locked check`。

### 6.2 导入目标定位轻量提示（「如何找到配置文件？」）

- 在导入页「本机目标」选择器下方新增可展开/收起的轻量提示入口「**如何找到配置文件？**」：
  - 打开有效迁移包但尚未选择本机目标时自动展开轻量提示卡片，给出 VS Code 具体步骤示例（在命令面板 `Ctrl+Shift+P` 执行 `Preferences: Open User Settings (JSON)`，复制打开文件的路径，回到 Contain 粘贴或通过「选择目标…」选择），并明确说明 **Contain 不会将同名 `settings.json` 自动认作 VS Code 配置，写入位置始终由用户明确决定**；
  - 选定本机目标进入预览后自动收起为单行折叠入口，保证 150% DPI 下完整目标路径、方向标签、确认恢复按钮与逐行差异仍完整位于首屏。

![导入页轻量「如何找到配置文件？」提示与空目标框](v0.8.0-rc.1/contain-v080rc1-target-help.png)

### 6.3 真实 VS Code 端到端实测环境

- **真实编辑器版本**：**Microsoft Visual Studio Code `1.134.0`**（官方签名 Windows x64 安装版，无 `data/` 便携目录，使用真实默认用户配置 `%APPDATA%\Code\User\settings.json`）
- **源端环境**：独立 Windows 11 环境，专用普通标准用户 **`ContainSrc`**（`is_admin = false`，`C:\Users\ContainSrc`），全程不接触个人主机配置与数据
- **接收端环境**：另一独立、**完全断网**（`offline_v4_default_routes = 0`、`offline_v6_default_routes = 0`）、**无 Windows Sandbox**（`wsb_command_present = false`、`windows_sandbox_exe_present = false`）的 Windows 11 环境，专用普通标准用户 **`ContainTest`**（`is_admin = false`，`C:\Users\ContainTest`），安装本次最终 `Contain-Setup-0.8.0-rc.1-x64.exe`（`FileVersion = 0.8.0.3`，`HKCU DisplayVersion = 0.8.0-rc.1`）
- **多行验证文本**：`C:\Users\Public\SampleNotes\multiline-sample.txt`（14 行文本，光标固定在第 6 行，清晰区分 `off` / `on` / `relative` 行号显示）

### 6.4 源端实测：`v0.7.0` 建点 → 改配置 C → 覆盖升级至 RC.1 → 导出 A

1. 以标准用户 `ContainSrc` 打开真实 VS Code `1.134.0`（默认 `C:\Users\ContainSrc\AppData\Roaming\Code\User\settings.json` 含 `"editor.lineNumbers": "on"`，版本 **A**），确认配置生效。
2. 以 `ContainSrc` 安装公开稳定版 **`v0.7.0`** 安装包（`FileVersion = 0.7.0.0`），通过桌面界面为 `settings.json` 创建还原点 **A**（`point_id = 72634849c8c3`，SHA-256 `4bbfc225...`）。
3. 将源端 `settings.json` 改为 `"editor.lineNumbers": "relative"`（版本 **C**，SHA-256 `a402b0ec...`）。
4. 以 `ContainSrc` 直接运行本次最终 **`Contain-Setup-0.8.0-rc.1-x64.exe`** 完成原地覆盖升级（`0.7.0.0` → `0.8.0.3`）。
5. 启动升级后的 RC.1 桌面端：旧 `v0.7.0` 还原点正常读取，状态准确显示「已变化」（`- "editor.lineNumbers": "on"` / `+ "editor.lineNumbers": "relative"`）；点击「导出还原点…」导出至 `vscode-line-numbers-on.contain-config-point`：
   - 导出包载荷严格等于建点版本 **A**（`4bbfc225...`），不包含 `ContainSrc` / `AppData` / `Roaming` 等源机路径；
   - 导出完成后源端磁盘上的 `settings.json` 仍严格为版本 **C**（`a402b0ec...`），再次打开真实 VS Code `1.134.0` 实拍可见相对行号（`5 4 3 2 1 6 1 2 3 ...`）。

### 6.5 接收端实测：真实 VS Code 定位、导入恢复（B → A）、撤销（A → B）与干扰文件保护

1. 接收端 `ContainTest` 默认 VS Code 用户配置 `C:\Users\ContainTest\AppData\Roaming\Code\User\settings.json` 初始为版本 **B**（`"editor.lineNumbers": "off"`），同时在另一目录 `C:\Users\ContainTest\Documents\OtherApp\settings.json` 放置同名干扰文件。
2. 在真实 VS Code `1.134.0` 打开多行文本，实拍确认恢复前无行号；随后按「如何找到配置文件？」提示在 VS Code 内执行 `Ctrl+Shift+P` → `Preferences: Open User Settings (JSON)`（VS Code `1.134.0` 模态编辑器切至常规标签页后执行 `File: Copy Path of Active File`），实测剪贴板返回精确路径 `C:\Users\ContainTest\AppData\Roaming\Code\User\settings.json`。
3. 回到 Contain RC.1 桌面端点击「导入还原点…」打开源端迁移包：目标框默认为空且展示轻量定位提示；粘贴 VS Code 复制出的路径后，预览准确显示完整路径 `C:\Users\ContainTest\AppData\Roaming\Code\User\settings.json`、`本机当前 → 恢复后` 及 `- "editor.lineNumbers": "off"` / `+ "editor.lineNumbers": "on"`。
4. 点击「确认恢复」后，目标文件逐字节变为 **A**，同名干扰文件 `OtherApp\settings.json` 哈希未变；重开真实 VS Code `1.134.0`，多行文本左侧行号 `1..15` 清晰开启。
5. 回到 Contain RC.1 点击「撤销」并确认后，目标文件逐字节回到接收端原始版本 **B**，同名干扰文件仍未变；重开真实 VS Code `1.134.0`，行号再次消失。

#### 第一类证据：源端与接收端目标文件（B → A → B）及同名干扰文件 SHA-256 核对

| 角色 / 文件 | 阶段 | 体积（字节） | SHA-256 | 核对结论 |
| --- | --- | ---: | --- | --- |
| 源端 `ContainSrc\...\Code\User\settings.json` | 建点版本 **A**（`"on"`） | 210 | `4bbfc225fbc6df13d033e2eef6c9d9017baffc65131f0749c6cfcd1cedc144ad` | v0.7.0 建点成功 |
| 源端 `ContainSrc\...\Code\User\settings.json` | 改后 / 导出后 / 最终 **C**（`"relative"`） | 216 | `a402b0ec5c5cd22e5d4a32c9f6a864c4fa719e89e185a420f764e7d73b56ca2e` | 导出 A 后磁盘仍为 C |
| 导出迁移包 `vscode-line-numbers-on.contain-config-point` | RC.1 导出整包（无源机路径） | — | `3def3eef1d3d102c580f4c339ae356c2cc7976419026b0381067c4d856038c92` | 载荷 SHA-256 == **A** |
| 接收端 `ContainTest\...\Code\User\settings.json` | 恢复前 **B**（`"off"`） | 211 | `1f7cfcdb51a8a3fb1eab92fcbd1ac2e8c2afa40d7bcda4b7961870a5dc7e4837` | 接收机初始配置 |
| 接收端 `ContainTest\...\Code\User\settings.json` | 导入恢复后 **A**（`"on"`） | 210 | `4bbfc225fbc6df13d033e2eef6c9d9017baffc65131f0749c6cfcd1cedc144ad` | 严格等于源端 **A** |
| 接收端 `ContainTest\...\Code\User\settings.json` | 撤销恢复后 **B**（`"off"`） | 211 | `1f7cfcdb51a8a3fb1eab92fcbd1ac2e8c2afa40d7bcda4b7961870a5dc7e4837` | 严格回到原 **B** |
| 接收端同名干扰 `ContainTest\Documents\OtherApp\settings.json` | 初始 / 恢复后 / 撤销后 / 最终 | 87 | `c875980a49f3e5473e8958efbfc9acd994ff56ef1ee4bde7c617e7b2105e1f45` | 全程逐字节未改动 |

#### 第二类证据：Contain 导入预览与真实 VS Code `1.134.0` 实拍截图

**1. 接收端 Contain RC.1 导入预览（完整路径 + 方向差异）**

![Contain RC.1 导入预览：完整目标路径与 off -> on 差异](v0.8.0-rc.1/contain-v080rc1-import-preview.png)

**2. 接收端真实 VS Code `1.134.0` 恢复前（`editor.lineNumbers = "off"`，无行号）**

![接收端 VS Code 1.134.0 恢复前：无行号](v0.8.0-rc.1/vscode-receiver-before-off.png)

**3. 接收端真实 VS Code `1.134.0` 恢复后（`editor.lineNumbers = "on"`，行号 `1..15` 开启）**

![接收端 VS Code 1.134.0 恢复后：标准行号已开启](v0.8.0-rc.1/vscode-receiver-after-restore-on.png)

**4. 接收端真实 VS Code `1.134.0` 撤销后（`editor.lineNumbers = "off"`，恢复无行号）**

![接收端 VS Code 1.134.0 撤销后：行号再次关闭](v0.8.0-rc.1/vscode-receiver-after-undo-off.png)

**5. 源端真实 VS Code `1.134.0` 导出 A 后（仍保持版本 C `"relative"` 相对行号）**

![源端 VS Code 1.134.0 导出 A 后仍显示相对行号](v0.8.0-rc.1/vscode-source-after-export-relative.png)

### 6.6 冻结 Beta.2（`885cd4d`）导出的 v1 迁移包在 RC.1 的兼容性

- 使用冻结 Beta.2 桌面端（`FileVersion = 0.8.0.2`，提交 `885cd4d`）在隔离数据目录导出真实 v1 迁移包 `beta2-exported-v1.contain-config-point`（UTF-8 含 BOM + CRLF + 中文，载荷 `205 B`，整包 SHA-256 `c0a746e9d04c877c174a36cd0995beb33e1714d174abeb99c5f63e533e5f8589`，载荷 SHA-256 `c59bd89786d7f262147fcde907f94b4428b0f75477303862058f7c044a870e17`，无源机路径泄漏）。
- 在上述独立断网接收机安装的最终 RC.1 桌面端直接导入该 Beta.2 迁移包至 `C:\Users\ContainTest\Documents\Beta2Compat\app-config.json`：
  - 恢复前接收端目标：`107 B`，SHA-256 `7bc1e45e2911f794cd9191414574cdc35b7cc89083d9bb10322b0820b1c0db3d`；
  - RC.1 直接识别、预览并确认恢复后：目标文件严格等于 Beta.2 载荷（`c59bd89786d7f262147fcde907f94b4428b0f75477303862058f7c044a870e17`）；
  - 在 RC.1 点击撤销并确认后：目标文件严格回到恢复前版本（`7bc1e45e2911f794cd9191414574cdc35b7cc89083d9bb10322b0820b1c0db3d`），且 VS Code `settings.json` 与同名干扰文件 `OtherApp\settings.json` 均保持不变。
- 最终以标准用户 `ContainTest` 执行 `unins000.exe /VERYSILENT /NORESTART` 卸载 RC.1：退出码 `0`，程序文件清理干净；完整机器回执保存在本地候选目录 `v080rc1-vscode-acceptance-receipt.json`。
