# Windows Beta 3: a stable personal record library

Version **0.3.2-beta.3**, frozen source
`3c2d3706d05dcf0a969dfdde2cc92213c6734e83`.
[Windows x64 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.3/contain-0.3.2-beta.3-windows-x64.zip) ·
[Release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.3) ·
[SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.3/SHA256SUMS.txt) ·
[PR #6](https://github.com/LuckyLivio/Contain/pull/6).

This iteration covers default storage, startup history and the necessary new-record
controls only. Beta 2 progress and failure feedback retain their contracts. The
whole-Program-Files finalizing incident remains unconfirmed and separately tracked
in [Issue #5](https://github.com/LuckyLivio/Contain/issues/5).

## Behavior

- Windows Known Folder LocalApplicationData supplies `Contain\Records\contain.db`.
  Startup reads existing sessions; a missing library displays an empty list and
  creates no directories or database. Explicit Start creates the library on demand.
  Moving the ZIP or changing the working directory does not change this location.
- New records default to **保存到我的记录**. Selecting an EXE suggests its filename
  as the record name until the user edits it. Observation scope and Start remain
  explicit. Custom database location is an advanced option.
- Sessions append to the existing SQLite library; same names are allowed and
  UUIDs select exact sessions. Opening an external database stays read-only and
  never changes the next new record's default. **我的记录** returns to the library.
- Physical database/WAL/SHM scope checks precede writer launch. Existing databases
  pass the production read-only compatibility path before a writer can initialize
  or migrate them. Missing-main sidecars, corrupt, unrelated, future or inaccessible
  files remain preserved. Prelaunch failures offer **更改保存位置** and details.
- Only a confirmed failed-before-installer result permits correction and retry.
  Missing or untrusted completion cannot enable a duplicate installation.

## Acceptance contract

The native harness uses a marked isolated record root and refuses an invalid root
or junction redirect; it never falls back to the real personal library. The final
candidate must pass eight personal-history checks, existing 22 viewer checks,
four layout checks, eight progress/failure checks and six GUI recording checks.

One controlled real ETW recording is driven through the packaged
`Contain.Viewer.exe`, with no database path interaction. It must exit completely,
restart from another working directory, open the same session UUID from default
history and explain the same event. Source, launcher, assembly, CLI and frozen
package-manifest identities are required by the packaging gate. Synthetic
cold-restart checks exercise the same executable separately; they do not satisfy
real ETW acceptance. Native DPI and simulated 150%/200% scaling are reported
separately.

No full-history search, installation-path discovery, uninstall or cleanup feature
is added. Existing evidence and High/Unknown/Degraded meanings are unchanged.

## Actual Windows acceptance

[Final push CI 41940121546](https://github.com/LuckyLivio/Contain/actions/runs/41940121546)
and [source PR CI 41940153707](https://github.com/LuckyLivio/Contain/actions/runs/41940153707)
passed. The final package passed **48 native checks**: viewer 22, layout 4,
progress/failure 8, personal history 8 and actual GUI recording 6, with zero
failed, skipped or unexecuted checks. The native recording ran on Windows Server
2025 AMD64, build 26100, reported DeviceDpi **96**. Native picker navigation/cancel,
duplicate Start, close guards, same-name history, failed-record reopening,
paging, event explanation and local report checks passed.

The actual packaged `Contain.Viewer.exe` performed one controlled real ETW
recording without setting or choosing a database path. Its process **2800** exited
with code **0**. A distinct process **3980**, started from another working directory,
loaded default history and opened the same session and event:

- Session: `ab15f3b0-09eb-455f-88fb-e5292a1969c7`.
- Event: `46b35a7b-bb6c-47b7-81ae-3f86203e9b8d`, High EtwFile `write_requested`.

The visible explanation's production JSON matched both exact identities and the
independent fixture truth. Installer exit was 0, collection finished and data saved;
these were checked separately. There were **219 real source events**, with
**Degraded** coverage. Received 590, retained timeline 316 and source 219 are
different denominators. Measured source event/buffer loss, application drops,
context loss, decode errors and snapshot/notification gaps were zero; drain did
not time out. These results do not establish complete ownership or performance
on an entire Program Files tree.

All **50 existing quality gates** passed on this frozen source. nextest passed
**338**, skipped seven existing executions, and used zero retries; Companion
separately passed its required ignored spool regression. The other six ignored
executions represent three storage-performance experiments imported twice.
Workspace line/function coverage was **75.701602% / 77.124601%**; original-package
coverage was **72.825169% / 75.649060%**. Floors remain 66.5% / 70%. These figures
include inline Rust tests and shared aliases, and measure neither managed-code
coverage nor branch or production-only coverage. Rust production code is unchanged
in this iteration; no new parser fuzz, performance or formal 32-trial claim is made.

150% and 200% checks simulate control/font scaling. Actual monitor transitions,
Windows 10, ARM64, Parallels and third-party installer matrices remain unverified.
No test read or wrote the real personal record library. Guarded isolated data and
existing synthetic fixtures supplied history, corruption and conflict tests.

The **downloaded final CI package** additionally passed all **42 offline native
checks on Windows 11 x64 build 26300**, reported DeviceDpi **144**, using its exact
UI DLL, launcher and CLI with a same-source local harness. The actual packaged
EXE fully exited (process 34724, exit 0), restarted as process 42424 from a moved
package and different working directory, and opened/explained the same synthetic
session and event. This local run performed no ETW capture. Package bytes and all
manifest entries were checked again afterward. Local quality on the same source
also passed 50/50 gates with Rust 1.98.1: workspace line/function coverage
75.635269% / 77.060703%, original-package 72.726633% / 75.559534%, with unchanged
denominators and floors. These local measurements are distinct from the Rust
1.99.1 final CI results above.

## Retained preflight failures

Before freezing, the actual Windows UI Automation projection ignored a cell-name
setter. A dedicated accessibility getter now exposes exact UUIDs while preserving
native cell display and interaction. Native MSAA supplies the actual description
and tab header when UI Automation omits them; tests still select the real controls
and assert the visible production explanation. Earlier failed attempts are retained.

The first integrated offline candidate `68473d7` exposed an obsolete external-label
assertion and a click attempted before startup finished. The final test-only commit
updates the exact read-only label contract and waits for real control readiness,
including task result callbacks. No production success condition or assertion was
removed. Final source-qualified checks passed. The Program Files incident remains
unconfirmed in Issue #5, with no new assertion that it was repaired.

## Immutable artifact

The unmodified accepted CI ZIP is **77,218,385 bytes**, SHA-256
`8a5063d979278235554482835080358c5d56d3011caaf46de7f34bc206acefa6`.
All **477** members, **476** content-manifest entries and all **383** tracked
source files inside `SOURCE.zip` were checked against frozen Git source.
The package uses Release WinForms, self-contained .NET **8.0.33**, and the MSVC
debug CLI/fixture built with Rust **1.99.1** and SDK **10.0.403**. It is unsigned
and carries no release-performance claim.

Delivery-only documentation follows the immutable tested source in the normal
PR merge. The source tag and ZIP retain that exact revision; no rebuild or repack
follows acceptance. Beta.2 and Beta.1 tags/assets, existing local work, backups
and historical databases remain preserved.

[Reviewed build identity and exact acceptance aggregates](delivery/v0.3.2-beta.3/BUILD-IDENTITY.json).
