# Beta 8: folder choice and finished-task guidance

This release addresses two pieces of feedback from a successful user-run 7-Zip
recording. It does not infer installation paths, remove explicit folder selection
or expand the collection scope.

The new-record page asks **要记录哪个文件夹里的变化？** and explains the choice
before opening the folder picker: normally choose the software's intended
installation folder, not the folder containing the downloaded installer. The
example distinguishes an EXE in Downloads from software installed on drive D.
Only existing folders are supported, and another existing observation folder can
still be selected explicitly. The missing-input hint is **请选择一个已有文件夹，再开始记录。**
The picker and package quick start use the same meaning; no confirmation dialog
or automatic scope selection was added.

Task guidance now follows the existing task result before the installer's exit
state. A confirmed completed, saved result says **记录已保存，可以查看变化。**
in the window, task details and copied summary. This does not require installer
exit code zero or Complete coverage. Ongoing finalization, result readback and
the wait for the collector to exit remain distinct. A failed or unconfirmed
ending directs the user to task details and any available saved evidence.
Completion decisions, exit codes, coverage classifications and saving rules are
unchanged.

## Verification and delivery

The [Beta 8 release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.8)
provides the accepted Windows x64 self-contained ZIP, build identity, quality
summary and SHA-256 checksums. The accepted ZIP is uploaded without rebuilding or
repacking, and each asset is downloaded again and checked after upload.

Windows CI exercises the frozen production forms, actual packaged EXE and CLI,
isolated saved records and synthetic progress children. Focused regressions compare
the visible next-step text, real clipboard output and task details while the
installer is already exited but recording is still finalizing, while readback or
collector exit is pending, after confirmed completion, and after a failure that
preserves partial evidence. The ordinary and simulated 150%/200% layout conditions
check the new Chinese explanation and reachable controls. The release identity
records the actual Windows version and measured DPI separately from simulation.

This focused run does not repeat an installation, start ETW, run active Doctor or
ask the user to reinstall 7-Zip. Synthetic protocol children validate presentation
and existing completion handling; they do not supply new live-ETW evidence.
Beta 7's controlled installation evidence remains historical. Windows 10/11
manual, ARM64, Parallels, real monitor-DPI transitions and new third-party installer
matrices remain unexecuted. Existing [finalizing issue #5](https://github.com/LuckyLivio/Contain/issues/5)
is not claimed fixed by this guidance change.
