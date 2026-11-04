# Windows Beta 2: real task progress and failure feedback

Version **0.3.2-beta.2**, frozen source
`b79622901355e53964752bf21993094d76e782f4`.
[Release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.2) ·
[Windows x64 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.2/contain-0.3.2-beta.2-windows-x64.zip) ·
[SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.2/SHA256SUMS.txt) ·
[PR #4](https://github.com/LuckyLivio/Contain/pull/4).
The four earlier milestones remain closed. This iteration covers task progress,
failure feedback and confirmed related defects only.

## Actual behavior

- The existing schema-3 status stream now exposes real database setup, baseline
  inventory, provider readiness, baseline file enumeration/read/hash, installer
  observation, descendant drain, final file/registry reads, source/queue shutdown,
  evidence replay and normalization, state association, final inventory, SQLite
  saving, exact-session readback and checkpoint finalization.
- Task, phase and step duration use independent monotonic measurement. Completed
  entry/event/resource counts have explicit units; unavailable measurements remain
  unavailable. A step entry is a real checkpoint; an unchanged count or UI repaint
  does not advance the last actual-progress instant. A single file hash has no
  sub-file byte measurement. Unknown totals have no percentage or ETA.
- Chinese status separates installer exit, source stop, committed evidence,
  completed session and coverage. A still-running installer may need input in its
  own window; Contain cannot detect whether it is waiting for input. Local elapsed
  estimates, status receipt age and actual work age are distinct. Delayed delivery
  cannot make displayed elapsed time move backward; silence does not establish a hang.
- Concise failures open scrollable technical details and a local copyable task
  summary. Exact session IDs bind progress and reopening. Saved incomplete evidence
  remains available. Repeated finalizing updates now validate; stale/foreign rows
  cannot replace the bound task, and callbacks after form disposal are guarded.
- The collector uses a bounded asynchronous 64-row status writer, coalesces
  measurements and retains production milestones. Delivered sequence remains
  `1, 2, 3…`, preserving the existing v3 contract. Both frontend pipes drain
  asynchronously, and UI presentation uses one pending slot. Observer shutdown
  waits at most five seconds after capture; missing terminal delivery stays unconfirmed.
- A real 144-DPI test exposed a wrapped event-grid header leaving no readable row.
  Scrollable content minimum now follows actual header/action/font dimensions.
  Portable runtime is pinned to beta.1's .NET 8.0.33; packaging rejects an older
  or mixed patch. No global SDK upgrade was needed.

Evidence clocks, attribution, SQLite schema/migrations, capture/drain timeouts,
selected scope and High/Unknown/Degraded meanings retain their semantics. Active
window-close behavior stays conservative; there is no installer-tree kill button.

## Windows verification

| Actual execution | Source / artifact | Passed | Conditions |
| --- | --- | --- | --- |
| Final Windows CI package | `b796229`, CI 41868963053 | Viewer 22, layout 4, progress/failure 8, GUI recording 6; zero failed/not run | Windows Server 2025 AMD64 10.0.26100, DeviceDpi 96 |
| Downloaded final package on this Windows machine | Same CI UI DLL, CLI and runtime; same-source local test harness | Viewer 22, layout 4, progress/failure 8; exit 0 | Windows 11 x64 10.0.26300, DeviceDpi 144; read-only/synthetic checks, no new installer capture |
| User-elevated local controlled GUI recording | Earlier `52b6e70` candidate, before final wire-sequence correction | Viewer 22, layout 4, progress/failure 8, GUI recording 6; exit 0 | Windows 11 x64, DeviceDpi 144, one controlled capture |

The final package's [push CI](https://github.com/LuckyLivio/Contain/actions/runs/41868963053)
and [PR CI](https://github.com/LuckyLivio/Contain/actions/runs/41868984580) both passed.
The final GUI capture saved exact session `265d1327-4b0e-4781-aefe-3e399ac1d4f8`,
installer exit 0 and recording state finished, with **219 real source events**.
The selected High EtwFile `write_requested` event
`8c454c97-29c4-4fa9-abb7-2f7297a07ea9` was verified against the independent fixture
oracle and explained/exported by the production viewer. High is actor association,
not final ownership or deletion permission. Capture quality remains **Degraded**.
Measured event/buffer loss, application drops, context losses, decode errors and
snapshot/notification gaps were zero; drain did not time out. Received 385,
retained timeline 316 and source 219 are different denominators, not an accuracy ratio.

Native picker navigation/cancel, duplicate Start, responsive local timer, conservative
closing, exact-session auto-open among older/same-name records, saved failed-session
reopening and report export passed. Synthetic child processes exercised slow
finalization, missing/late/duplicate/foreign messages, missing metrics, structured
failure, nonzero/premature exit, pipe draining, details/copy and disposal guards.
The local earlier live capture saved 232 source events with independent truth and
zero reported losses/gaps; its original database, full report and screenshots remain local.

150% and 200% conditions use **simulated control/font scaling**, separately from
actual reported DPI. Cross-monitor DPI transitions, Windows 10, Parallels, ARM64,
third-party installers and formal scale/32-trial acceptance were not added or certified.

All **50** existing quality gates passed with unchanged 66.5% line / 70% function
floors. nextest passed **338**, skipped seven existing executions and used zero
retries; Companion separately passed its one required ignored regression. The
remaining six storage-performance executions represent three experiments in two
source imports. Zero doctests are not feature validation. Workspace line/function
coverage is **75.706705% / 77.124601%**; original-package coverage is
**72.832207% / 75.649060%**. These include inline tests and shared aliases;
branch coverage and production-only coverage were not measured. Full native
capture/worker-error paths remain partly outside instrumented unit coverage.
Existing three-target 30-second fuzz smoke ran locally at `5949569`; seven-sample
in-memory benchmarks ran at `db9c297`. These earlier checks make no final ETW
throughput claim.

## Retained failures and unresolved incident

The first local native run at `7379195` failed the 144-DPI event-row assertion;
its evidence remains retained, followed by the concrete layout fix and successful
native checks. A Windows UTF-8 report reader assumption was fixed after the actual
GBK decoding failure. [CI 41864370237](https://github.com/LuckyLivio/Contain/actions/runs/41864370237)
then exposed two new output-isolation test assertions comparing an 8.3 temporary
alias with its long spelling. The actual output destination was correct. Fixture
path canonicalization plus a real 8.3 regression fixed this without weakening
assertions. Later source-qualified checks passed. Earlier queued preflights, cancellation requests and intermediate results are
retained separately from final successful captures.

The original whole-Program-Files / Parallels finalizing incident remains
**unconfirmed and unresolved**, tracked in [Issue #5](https://github.com/LuckyLivio/Contain/issues/5).
No original database or full error log was supplied. New operation/count diagnostics
identify the actual work path; they do not establish that permissions, size,
Parallels, earlier ID/name collision or pipe handling caused the report. Program
Files is not blocked or silently narrowed, and partial saving is not forced complete.

## Immutable package

The self-contained Windows x64 ZIP is **77,178,508 bytes**, SHA-256
`58cad9ffb8de6c5e125898a79084560cc0fce6c17a39607e25dad80d49960cfa`.
All 477 members, 476 content-manifest entries and all 373 tracked files inside
SOURCE.zip were verified against the frozen Git source. The downloaded production
UI assembly and actual packaged CLI were additionally exercised on local Windows 11.
The package uses Release WinForms / .NET 8.0.33 and MSVC debug CLI/fixture,
SDK 10.0.403 and Rust 1.99.1. It is unsigned and carries no release-performance claim.
[Reviewed build identity and validation](delivery/v0.3.2-beta.2/BUILD-IDENTITY.json)
records the profiles, hashes, exact scopes and local versus CI evidence.

The tag preserves the tested source; the normal PR merge adds current download and
reviewed delivery documentation. The published ZIP is the verified CI ZIP, without
rebuilding or repacking after verification. Beta.1 tag/assets and historical records
remain preserved. Original user databases, WAL/SHM, logs and local paths are not uploaded.
