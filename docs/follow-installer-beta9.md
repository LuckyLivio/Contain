# Beta 9: follow an installer without guessing its destination

Choose an EXE, keep **跟随安装器记录**, and click **开始记录**. Install normally;
the installer chooses its destination. After recording, the exact saved session
opens in **事件与依据**. Search a filename, select its original event and inspect
the explanation or copy its ID. Search and report export keep their existing
session and pagination guarantees.

This mode follows the launched process instance and verified direct children.
It requires available ETW sources and existing permissions. The recorded operation
does not establish a before/after file change, a cached name's current location,
or final ownership. Unknown writers stay Unknown; excluded/unresolved activity is
not proof no system write occurred. Services, detached grandchildren, MSI and
broader capture are not added. There is no directory or whole-disk scan.

**指定文件夹观察** retains an explicitly chosen existing folder, directory
notifications, and before/after comparison. Snapshot mode remains explicit in
that mode. Follow mode never silently falls back to a snapshot. Names and the
personal record library remain automatic defaults; advanced choices are shown
near the primary action when changed.

The WinForms page groups the filename, expandable/copyable path, mode and Start
together. Advanced settings and diagnostic fields are shown on demand. Short
status text identifies waiting, failure or completion; details and copied task
summaries retain the same state meaning. Keyboard access, long paths and scrolling
remain native controls rather than a framework migration.

## Evidence and delivery

The [frozen technical experiment](follow-installer-experiment.md) first verified
two independently known source writes outside the package directory, one by the
root and one by a direct child creating a new directory. A third independent
writer was excluded from the session, not misattributed. Exact IDs, process births,
unresolved observations, limits and fresh-process explanations are retained there.

The [Beta 9 release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.9)
contains the exact accepted Windows x64 ZIP, build identity, quality summary,
checksums and actual before/after screenshots. The product acceptance uses the
packaged EXE and native picker, no observation-directory or advanced interaction,
an independently controlled installer target, exact saved session, target write,
copy/explain identity and cold restart. Manual configuration, personal records,
search, report export and failure guidance retain their required regressions.
The accepted ZIP is uploaded without rebuilding or repacking and every asset is
downloaded again to verify SHA-256.

Before/after comparison uses 740 x 620 windows. Actual Windows version and DPI are
recorded in the identity; simulated 150%/200% layout stress is reported separately.
The optional official 7-Zip trial uses a disposable Windows runner and default
installation location; its version, hash, prior installation, silent arguments
and outcome are reported separately from controlled-fixture evidence. It is not
a general installer/Windows compatibility matrix or a manual GUI 7-Zip trial.

No third-party UI code, icons, fonts or new runtime were adopted. Read-only Fluent
references and existing ferrisetw/WinForms choices are documented in the experiment
record. Official 7-Zip is an external trial input only, not a component shipped in
Contain. [Two-minute demonstration outline](beta9-demo.md).

The optional external trial pins official [7-Zip 26.05](https://github.com/ip7z/7zip/releases/tag/26.05),
source `b88c6bc09339de32f7d18bbab900686b244f42ba`, installer
`7z2605-x64.exe` SHA-256
`fd99e86f57d8f429ef5ed045bf98315af962e2309b0f1ba87a147a44c71647d0`.
Its [license](https://github.com/ip7z/7zip/blob/b88c6bc09339de32f7d18bbab900686b244f42ba/DOC/License.txt)
uses LGPL 2.1-or-later with specified BSD/UnRAR terms. The installer is downloaded
as a trial input only; no 7-Zip code or binary is copied into the Contain package.

Candidate memory is bounded to 2,048 records / 8 MiB and 4,096 resources; raw
storage is capped at 64 MiB and descendant quiet wait at 10 seconds. Limits and
losses remain explicit and preserve the existing conservative attribution rules.
The existing [finalizing issue #5](https://github.com/LuckyLivio/Contain/issues/5)
is not declared solved for every workload. ARM64, Parallels, real monitor DPI
transitions and broad third-party installer matrices remain unverified.

## Retained product acceptance attempts

The first product candidate `bd78e28d5c52c8f52ee96f49d4e13321646c015f`,
[CI 42187315034](https://github.com/LuckyLivio/Contain/actions/runs/42187315034),
failed a configuration regression before any installer or native capture started.
The follow-plus-snapshot conflict correctly refused startup, but subsequent focus
on the unchecked manual-mode RadioButton caused WinForms AutoCheck to change the
selected mode. The strict no-silent-switch assertion caught this real UI bug.
Four configuration checks passed, one failed; layout and five dependent native
checks did not run. Other viewer, personal-record, overview, search, export and
progress checks passed, as did the 54 quality gates. The diagnostic artifact is
not a release candidate. The fix focuses the already checked mode and keeps the
original assertions, split into individual diagnostic conditions.

The second candidate `b2c2b793e15506d4bf9cda8617f70ffbacf91815`,
[CI 42189250179](https://github.com/LuckyLivio/Contain/actions/runs/42189250179),
passed those six strict state conditions but stopped at the test's Task-reference
receipt. Synchronous prelaunch validation can return the cached completed Task,
so reference identity did not prove whether Start executed. The corrected test
checks the newly visible, specific validation error and status instead. This
attempt also started zero installers/captures. Cold-restart test helpers were
aligned with the new automatic write search: they explicitly clear it and wait
for the ordinary first page before using full-timeline offsets; exact event and
explanation identity checks remain unchanged.
