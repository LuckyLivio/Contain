# Beta 7: a simpler new installation record

Click **新建安装记录**, choose an EXE in the native picker, choose an existing
folder to observe, then click **开始记录**. Selecting or cancelling a picker never
starts the installer. Cancelling the first picker preserves the selected history
record. An existing task continues to use the task-status entry.

The ordinary configuration page shows the installer and observation folder. The
EXE filename suggests the record name; the default destination is **我的记录**.
Full paths can be viewed and copied, and both selections can be changed. A missing
or invalid observation folder has an actionable message. Contain does not infer
the observation scope from the installer, a previous session or Program Files;
it does not create or select the software's installation destination.

Record name, custom database, registry selection, literal arguments, waiting
parameters, explicit snapshot mode and optional **检查环境** are in the initially
collapsed advanced settings. Changed settings remain summarized near Start when
the panel is collapsed. Environment diagnostics are optional. Actual startup still
validates permissions, required sources, the selected directory and database
conflicts before launching the installer. An unavailable source never silently
selects snapshot mode. Existing progress, session identity, storage, result views,
search and reports retain their established behavior.

## Acceptance and delivery

The [Beta 7 release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.7)
provides the accepted Windows x64 self-contained ZIP, checksum file, build identity
and two actual native screenshots: the ordinary configured page and the missing
observation-folder state. Build identity records the exact source, CI run,
measured Windows version/DPI, native checks, frozen hashes and remaining limits.
The accepted ZIP is uploaded without rebuilding or repacking.

Native acceptance operates the actual packaged EXE and native EXE/folder pickers.
One controlled installation uses the ordinary two-input flow, without opening
advanced settings, choosing a database or invoking Doctor. Its persisted scope
must exactly equal the independently selected folder, and its returned session
must be the one automatically opened. A guarded, opt-in environment value lets
the existing **test fixture only** run without installer arguments; product code
does not derive an installation target from the watch folder.

Offline and native checks cover cancellation, absent/invalidated folders,
database conflicts, explicit advanced choices, prelaunch refusal, duplicate Start,
keyboard access and small-window layout. Previous viewer/search/export gates
remain required. Layout checks precede the bounded installation so layout work
does not repeatedly start capture. Synthetic permission and progress fixtures are
reported separately from the one actual controlled capture.

Actual DPI is measured separately from simulated 150%/200% layout stress. This
delivery does not establish new Windows 10/11 manual, ARM64, Parallels, real
monitor-DPI-transition, multi-installer or scale matrices. Automatic installation
path discovery, nonexistent observation directories and expanded capture scope
remain outside this version. The existing [finalizing issue #5](https://github.com/LuckyLivio/Contain/issues/5)
is unchanged; successful bounded recording does not establish ownership or safe
deletion.
