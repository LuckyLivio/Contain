; *** Inno Setup version 6.5.0+ Simplified Chinese messages ***

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=936
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=14

[Messages]

; *** Application titles
SetupAppTitle=安装
SetupWindowTitle=安装 - %1
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 %1

; *** Misc. common
InformationTitle=提示
ConfirmTitle=确认
ErrorTitle=错误

; *** SetupLdr messages
SetupLdrStartupMessage=本向导将安装 %1。是否继续？
LdrCannotCreateTemp=无法创建临时文件，安装已中止
LdrCannotExecTemp=无法在临时目录中执行安装文件，安装已中止
HelpTextNote=

; *** Startup error messages
LastErrorMessage=%1。%n%n错误 %2：%3
SetupFileMissing=安装目录中缺少文件 %1。请重新获取完整的安装包。
SetupFileCorrupt=安装文件已损坏。请重新获取完整的安装包。
SetupFileCorruptOrWrongVer=安装文件已损坏，或与当前安装器版本不兼容。请重新获取完整的安装包。
InvalidParameter=命令行传入了无效参数：%n%n%1
SetupAlreadyRunning=安装程序已在运行中。
WindowsVersionNotSupported=此程序不支持当前计算机运行的 Windows 版本。
WindowsServicePackRequired=此程序需要 %1 Service Pack %2 或更高版本。
NotOnThisPlatform=此程序无法在 %1 上运行。
OnlyOnThisPlatform=此程序只能在 %1 上运行。
OnlyOnTheseArchitectures=此程序只能安装在以下处理器架构的 Windows 系统上：%n%n%1
WinVersionTooLowError=此程序需要 %1 版本 %2 或更高版本。
WinVersionTooHighError=此程序无法安装在 %1 版本 %2 或更高版本上。
AdminPrivilegesRequired=安装此程序需要管理员权限。
PowerUserPrivilegesRequired=安装此程序需要管理员或高级用户组权限。
SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请先正常关闭其所有窗口（确保正在保存的记录已完成），然后点击“确定”继续，或点击“取消”退出。
UninstallAppRunningError=卸载程序检测到 %1 正在运行。%n%n请先正常关闭其所有窗口（确保正在保存的记录已完成），然后点击“确定”继续，或点击“取消”退出。

; *** Startup questions
PrivilegesRequiredOverrideTitle=选择安装模式
PrivilegesRequiredOverrideInstruction=选择安装范围
PrivilegesRequiredOverrideText1=%1 可以为所有用户安装（需要管理员权限），也可以仅为当前账户安装。
PrivilegesRequiredOverrideText2=%1 可以仅为当前账户安装，也可以为所有用户安装（需要管理员权限）。
PrivilegesRequiredOverrideAllUsers=为所有用户安装(&A)
PrivilegesRequiredOverrideAllUsersRecommended=为所有用户安装（推荐）(&A)
PrivilegesRequiredOverrideCurrentUser=仅为当前用户安装(&M)
PrivilegesRequiredOverrideCurrentUserRecommended=仅为当前用户安装（推荐）(&M)

; *** Misc. errors
ErrorCreatingDir=安装程序无法创建目录“%1”
ErrorTooManyFilesInDir=无法在目录“%1”中创建文件，因为该目录包含的文件过多

; *** Setup common messages
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。如果现在退出，程序将不会被安装。%n%n你可以稍后再次运行安装程序完成安装。%n%n确定要退出安装向导吗？
AboutSetupMenuItem=关于安装程序(&A)...
AboutSetupTitle=关于安装程序
AboutSetupMessage=%1 版本 %2%n%3%n%n%1 主页：%n%4
AboutSetupNote=
TranslatorNote=

; *** Buttons
ButtonBack=上一步(&B)
ButtonNext=下一步(&N)
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)

; *** "Select Language" dialog messages
SelectLanguageTitle=选择安装语言
SelectLanguageLabel=请选择安装过程中使用的语言。

; *** Common wizard text
ClickNext=点击“下一步”继续，或点击“取消”退出安装程序。
BeveledLabel=
BrowseDialogTitle=浏览文件夹
BrowseDialogLabel=请从下方列表中选择目标文件夹，然后点击“确定”。
NewFolderName=新建文件夹

; *** "Welcome" wizard page
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=本向导将在当前用户环境下安装 [name/ver]。%n%n安装过程不会修改你的个人记录数据库或外部导出的报告。建议继续前先正常关闭已打开的 [name] 窗口。

; *** "Password" wizard page
WizardPassword=密码
PasswordLabel1=此安装受密码保护。
PasswordLabel3=请输入密码，然后点击“下一步”继续。密码区分大小写。
PasswordEditLabel=密码(&P)：
IncorrectPassword=输入的密码不正确，请重试。

; *** "License Agreement" wizard page
WizardLicense=许可协议
LicenseLabel=继续之前请阅读以下重要信息。
LicenseLabel3=请阅读以下许可协议。继续安装前必须接受协议条款。
LicenseAccepted=我接受协议(&A)
LicenseNotAccepted=我不接受协议(&D)

; *** "Information" wizard pages
WizardInfoBefore=安装说明
InfoBeforeLabel=继续安装前请阅读以下说明。
InfoBeforeClickLabel=准备就绪后，请点击“下一步”继续。
WizardInfoAfter=安装后说明
InfoAfterLabel=继续之前请阅读以下说明。
InfoAfterClickLabel=准备就绪后，请点击“下一步”继续。

; *** "User Information" wizard page
WizardUserInfo=用户信息
UserInfoDesc=请输入你的信息。
UserInfoName=用户名(&U)：
UserInfoOrg=组织(&O)：
UserInfoSerial=序列号(&S)：
UserInfoNameRequired=必须输入名称。

; *** "Select Destination Location" wizard page
WizardSelectDir=选择安装位置
SelectDirDesc=要将 [name] 安装到哪里？
SelectDirLabel3=安装程序将把 [name] 程序文件安装到以下文件夹（个人记录库保存在独立的 LocalAppData\Contain 目录，不受卸载影响）。
SelectDirBrowseLabel=点击“下一步”继续。如需更改程序安装文件夹，请点击“浏览”。
DiskSpaceGBLabel=至少需要 [gb] GB 可用磁盘空间。
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
CannotInstallToNetworkDrive=无法安装到网络驱动器。
CannotInstallToUNCPath=无法安装到 UNC 网络路径。
InvalidPath=请输入包含盘符的完整本地路径，例如：%n%nC:\APP
InvalidDrive=所选驱动器不存在或无法访问，请重新选择。
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=安装至少需要 %1 KB 可用空间，但所选驱动器仅剩 %2 KB。%n%n仍要继续吗？
DirNameTooLong=文件夹名称或路径过长。
InvalidDirName=文件夹名称无效。
BadDirName32=文件夹名称不能包含以下任何字符：%n%n%1
DirExistsTitle=文件夹已存在
DirExists=文件夹：%n%n%1%n%n已存在。仍要安装到该文件夹吗？
DirDoesntExistTitle=文件夹不存在
DirDoesntExist=文件夹：%n%n%1%n%n不存在。是否创建该文件夹？

; *** "Select Components" wizard page
WizardSelectComponents=选择组件
SelectComponentsDesc=要安装哪些组件？
SelectComponentsLabel2=勾选要安装的组件，取消勾选不需要的组件，然后点击“下一步”继续。
FullInstallation=完整安装
CompactInstallation=精简安装
CustomInstallation=自定义安装
NoUninstallWarningTitle=组件已存在
NoUninstallWarning=安装程序检测到以下组件已安装：%n%n%1%n%n取消勾选这些组件不会将其卸载。%n%n仍要继续吗？
ComponentSize1=%1 KB
ComponentSize2=%1 MB
ComponentsDiskSpaceGBLabel=当前所选组件至少需要 [gb] GB 磁盘空间。
ComponentsDiskSpaceMBLabel=当前所选组件至少需要 [mb] MB 磁盘空间。

; *** "Select Additional Tasks" wizard page
WizardSelectTasks=选择附加任务
SelectTasksDesc=需要执行哪些附加操作？
SelectTasksLabel2=请选择安装 [name] 时需要执行的附加选项，然后点击“下一步”。

; *** "Select Start Menu Folder" wizard page
WizardSelectProgramGroup=选择开始菜单文件夹
SelectStartMenuFolderDesc=要在哪里创建程序快捷方式？
SelectStartMenuFolderLabel3=安装程序将在以下开始菜单文件夹中创建快捷方式。
SelectStartMenuFolderBrowseLabel=点击“下一步”继续。如需选择其他文件夹，请点击“浏览”。
MustEnterGroupName=必须输入文件夹名称。
GroupNameTooLong=文件夹名称或路径过长。
InvalidGroupName=文件夹名称无效。
BadGroupName=文件夹名称不能包含以下字符：%n%n%1
NoProgramGroupCheck2=不创建开始菜单文件夹(&D)

; *** "Ready to Install" wizard page
WizardReady=准备安装
ReadyLabel1=安装程序已准备好在你的电脑上安装 [name]。
ReadyLabel2a=点击“安装”开始复制文件；如需检查或修改安装选项，请点击“上一步”。
ReadyLabel2b=点击“安装”开始复制文件。
ReadyMemoUserInfo=用户信息：
ReadyMemoDir=安装位置：
ReadyMemoType=安装类型：
ReadyMemoComponents=所选组件：
ReadyMemoGroup=开始菜单文件夹：
ReadyMemoTasks=附加任务：

; *** TDownloadWizardPage wizard page and DownloadTemporaryFile
DownloadingLabel2=正在下载文件...
ButtonStopDownload=停止下载(&S)
StopDownload=确定要停止下载吗？
ErrorDownloadAborted=下载已中止
ErrorDownloadFailed=下载失败：%1 %2
ErrorDownloadSizeFailed=获取文件大小失败：%1 %2
ErrorProgress=进度无效：%1 / %2
ErrorFileSize=文件大小无效：预期 %1，实际 %2

; *** TExtractionWizardPage wizard page and ExtractArchive
ExtractingLabel=正在解压文件...
ButtonStopExtraction=停止解压(&S)
StopExtraction=确定要停止解压吗？
ErrorExtractionAborted=解压已中止
ErrorExtractionFailed=解压失败：%1

; *** Archive extraction failure details
ArchiveIncorrectPassword=密码不正确
ArchiveIsCorrupted=压缩包已损坏
ArchiveUnsupportedFormat=不支持该压缩包格式

; *** "Preparing to Install" wizard page
WizardPreparing=正在准备安装
PreparingDesc=正在准备安装 [name]。
PreviousInstallNotCompleted=前一次安装或卸载操作尚未完成，需要先重启计算机。%n%n重启后请再次运行安装程序以完成 [name] 的安装。
CannotContinue=安装无法继续，请点击“取消”退出。
ApplicationsFound=以下程序正在占用需要更新的文件。请先正常关闭它们后再继续。
ApplicationsFound2=以下程序正在占用需要更新的文件。请先正常关闭它们后再继续。
CloseApplications=自动关闭这些程序(&A)
DontCloseApplications=不自动关闭程序(&D)
ErrorCloseApplications=安装程序无法自动关闭相关程序。请先手动正常关闭占用文件的程序后再继续。
PrepareToInstallNeedsRestart=安装程序需要重启计算机。重启后请再次运行安装程序完成 [name] 的安装。%n%n现在重启吗？

; *** "Installing" wizard page
WizardInstalling=正在安装
InstallingLabel=正在安装 [name]，请稍候...

; *** "Setup Completed" wizard page
FinishedHeadingLabel=[name] 安装完成
FinishedLabelNoIcons=已在你的电脑上完成 [name] 的安装。
FinishedLabel=已在你的电脑上完成 [name] 的安装。你可以从“开始”菜单打开应用。
ClickFinish=点击“完成”退出安装向导。
FinishedRestartLabel=需要重启计算机以完成 [name] 的安装。是否现在重启？
FinishedRestartMessage=需要重启计算机以完成 [name] 的安装。%n%n是否现在重启？
ShowReadmeCheck=查看自述文件
YesRadio=是，现在重启计算机(&Y)
NoRadio=否，稍后手动重启(&N)
RunEntryExec=运行 %1
RunEntryShellExec=查看 %1

; *** "Setup Needs the Next Disk" stuff
ChangeDiskTitle=需要下一张磁盘
SelectDiskLabel2=请插入磁盘 %1 并点击“确定”。%n%n如果文件位于其他目录，请输入正确路径或点击“浏览”。
PathLabel=路径(&P)：
FileNotInDir2=在“%2”中找不到文件“%1”。请指定正确的文件夹。
SelectDirectoryLabel=请指定下一张磁盘的位置。

; *** Installation phase messages
SetupAborted=安装未完成。%n%n请解决问题后重新运行安装程序。
AbortRetryIgnoreSelectAction=选择操作
AbortRetryIgnoreRetry=重试(&T)
AbortRetryIgnoreIgnore=忽略错误并继续(&I)
AbortRetryIgnoreCancel=取消安装
RetryCancelSelectAction=选择操作
RetryCancelRetry=重试(&T)
RetryCancelCancel=取消

; *** Installation status messages
StatusClosingApplications=正在等待程序关闭...
StatusCreateDirs=正在创建目录...
StatusExtractFiles=正在复制程序文件...
StatusDownloadFiles=正在下载文件...
StatusCreateIcons=正在创建快捷方式...
StatusCreateIniEntries=正在写入配置项...
StatusCreateRegistryEntries=正在写入注册表项...
StatusRegisterFiles=正在注册组件...
StatusSavingUninstall=正在写入卸载信息...
StatusRunProgram=正在完成安装...
StatusRestartingApplications=正在重新启动程序...
StatusRollback=正在回滚更改...

; *** Misc. errors
ErrorInternal2=内部错误：%1
ErrorFunctionFailedNoCode=%1 失败
ErrorFunctionFailed=%1 失败；代码 %2
ErrorFunctionFailedWithMessage=%1 失败；代码 %2。%n%3
ErrorExecutingProgram=无法执行文件：%n%1

; *** Registry errors
ErrorRegOpenKey=打开注册表项出错：%n%1\%2
ErrorRegCreateKey=创建注册表项出错：%n%1\%2
ErrorRegWriteKey=写入注册表项出错：%n%1\%2

; *** INI errors
ErrorIniEntry=写入 INI 文件“%1”时出错。

; *** File copying errors
FileAbortRetryIgnoreSkipNotRecommended=跳过此文件（不推荐）(&S)
FileAbortRetryIgnoreIgnoreNotRecommended=忽略错误并继续（不推荐）(&I)
SourceIsCorrupted=源文件已损坏
SourceDoesntExist=源文件“%1”不存在
SourceVerificationFailed=源文件校验失败：%1
VerificationSignatureDoesntExist=签名文件“%1”不存在
VerificationSignatureInvalid=签名文件“%1”无效
VerificationKeyNotFound=签名文件“%1”使用了未知密钥
VerificationFileNameIncorrect=文件名不匹配
VerificationFileTagIncorrect=文件标记不匹配
VerificationFileSizeIncorrect=文件大小不匹配
VerificationFileHashIncorrect=文件哈希不匹配
ExistingFileReadOnly2=现有文件为只读属性，无法替换。
ExistingFileReadOnlyRetry=移除只读属性并重试(&R)
ExistingFileReadOnlyKeepExisting=保留现有文件(&K)
ErrorReadingExistingDest=读取现有文件时出错：
FileExistsSelectAction=选择操作
FileExists2=目标文件已存在。
FileExistsOverwriteExisting=覆盖现有文件(&O)
FileExistsKeepExisting=保留现有文件(&K)
FileExistsOverwriteOrKeepAll=对后续冲突执行相同操作(&D)
ExistingFileNewerSelectAction=选择操作
ExistingFileNewer2=目标位置已有更新版本的文件。
ExistingFileNewerOverwriteExisting=覆盖现有文件(&O)
ExistingFileNewerKeepExisting=保留现有文件（推荐）(&K)
ExistingFileNewerOverwriteOrKeepAll=对后续冲突执行相同操作(&D)
ErrorChangingAttr=修改现有文件属性时出错：
ErrorCreatingTemp=在目标目录创建临时文件时出错：
ErrorReadingSource=读取源文件时出错：
ErrorCopying=复制文件时出错：
ErrorDownloading=下载文件时出错：
ErrorExtracting=解压归档文件时出错：
ErrorReplacingExistingFile=替换现有文件时出错：
ErrorRestartReplace=重启替换失败：
ErrorRenamingTemp=重命名目标目录中的临时文件时出错：
ErrorRegisterServer=无法注册 DLL/OCX：%1
ErrorRegSvr32Failed=RegSvr32 失败，退出码 %1
ErrorRegisterTypeLib=无法注册类型库：%1

; *** Uninstall display name markings
UninstallDisplayNameMark=%1 (%2)
UninstallDisplayNameMarks=%1 (%2, %3)
UninstallDisplayNameMark32Bit=32 位
UninstallDisplayNameMark64Bit=64 位
UninstallDisplayNameMarkAllUsers=所有用户
UninstallDisplayNameMarkCurrentUser=当前用户

; *** Post-installation errors
ErrorOpeningReadme=打开自述文件时出错。
ErrorRestartingComputer=无法自动重启计算机，请手动重启。

; *** Uninstaller messages
UninstallNotFound=找不到文件“%1”，无法执行卸载。
UninstallOpenError=无法打开文件“%1”，无法执行卸载
UninstallUnsupportedVer=卸载日志“%1”格式不受当前卸载程序支持，无法卸载
UninstallUnknownEntry=卸载日志中包含未知记录（%1）
ConfirmUninstall=确定要卸载 %1 的程序文件吗？（你的个人记录库与外部导出的报告将保留，不会被删除。）
UninstallOnlyOnWin64=此安装只能在 64 位 Windows 上卸载。
OnlyAdminCanUninstall=此安装只能由具有管理员权限的用户卸载。
UninstallStatusLabel=正在从当前账户移除 %1 程序文件，请稍候...
UninstalledAll=%1 程序文件已从你的电脑移除（个人记录库与导出的报告已保留）。
UninstalledMost=%1 卸载已完成。%n%n部分残留项目未能自动移除，可手动清理。
UninstalledAndNeedsRestart=需要重启计算机以完成 %1 的卸载。%n%n是否现在重启？
UninstallDataCorrupted=文件“%1”已损坏，无法卸载

; *** Uninstallation phase messages
ConfirmDeleteSharedFileTitle=移除共享文件？
ConfirmDeleteSharedFile2=系统显示以下共享文件已无其他程序使用。是否一并移除？%n%n如果不确定，请选择“否”。保留该文件不会对系统造成影响。
SharedFileNameLabel=文件名：
SharedFileLocationLabel=位置：
WizardUninstalling=卸载状态
StatusUninstalling=正在卸载 %1...

; *** Shutdown block reasons
ShutdownBlockReasonInstallingApp=正在安装 %1。
ShutdownBlockReasonUninstallingApp=正在卸载 %1。

[CustomMessages]
NameAndVersion=%1 版本 %2
AdditionalIcons=附加快捷方式：
CreateDesktopIcon=创建桌面快捷方式(&D)
CreateQuickLaunchIcon=创建快速启动栏快捷方式(&Q)
ProgramOnTheWeb=%1 项目主页
UninstallProgram=卸载 %1
LaunchProgram=启动 %1
AssocFileExtension=将 %1 与 %2 文件扩展名关联(&A)
AssocingFileExtension=正在将 %1 与 %2 文件扩展名关联...
AutoStartProgramGroupDescription=启动项：
AutoStartProgram=随系统自动启动 %1
AddonHostProgramNotFound=在所选文件夹中未找到 %1。%n%n仍要继续吗？
