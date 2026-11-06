# Beta 10 diagnostic preview — acceptance blocked

**诊断预览，不是已验收的 Beta.10。** [下载 Windows ZIP](https://github.com/LuckyLivio/Contain/actions/runs/42216843544/artifacts/13501998007)（需登录 GitHub；先解开产物外层，再解开内层 Windows ZIP）。
产物保留至 2027-02-04。源码：`3c2e06871d28acd199ba1813605afecc9074738d`。
内层 ZIP SHA-256：`41a3e4e3e8dc0b75e245eb3306ed8e5c6294d04c5fc72d4cc9110d3512ef99e7`。
CRC、全部 CONTENTS 文件校验和、SOURCE 和实际桌面安装验收已核对；独立重负载采集门槛失败。
PR 保持草稿；未合并 main、未创建正式 Beta.10 tag/prerelease。Beta.9 未改动。

Scope: existing WinForms, follow-installer capture and saved diagnostic text only.
No WPF, path library or attribution confidence changes. Beta 9's tag and assets
are immutable. Baseline source: `8e68ccfe63389e1533235e253eb59329f15be898`;
development started from main `bb65aba2badd0a7e304d0557789b0ead6855ab07`.

## What the saved counters mean

Beta 9 already saves `pipeline`, `handoff`, `stream` and `follow_installer` inside
`capture_metadata.backend_json`. No new collection or migration is necessary.
The application raw-record loss total is:

```
pipeline.queue_overflow + pipeline.enqueue_disconnected
+ handoff.overflow + handoff.oversized + handoff.disconnected + handoff.serialize_failed
+ stream.quota_dropped + stream.failed
```

`pipeline.retention_dropped` mirrors `stream.quota_dropped` and must not be added
again. `pipeline.persistence_failed` similarly mirrors `stream.failed`.
`follow_installer.candidate_dropped` contributes to context loss, not this raw
record total. `deliberately_filtered`, `unsupported_records`, `excluded_other`,
`excluded_self_io` and unresolved identity counts are separate diagnostics.
An unresolved identity is not evidence of an installer writer.

The reported private Parallels session (57,767 application records dropped,
context loss 0) has not been examined here. Its loss stage and root cause remain
unconfirmed. Zero Windows ETW loss does not imply zero application queue loss.

## Read the old session without sharing private data

Close Contain normally. With Python 3 available, use the exact session ID shown
in the old record and its local database location:

```powershell
py -3 .\scripts\session-loss-counts.py --database '<local database>' --session-id '<exact session ID>'
```

The script reads only that session and prints whitelisted counts and fixed status
labels; no event contents, paths, warnings or original error text. It never opens
a capture or migrates a database. Missing historical fields print `未提供`.
`application_loss_accounting` separates receive queue, handoff and raw persistence
loss and checks whether they equal the saved total. Follow candidates stay
separate. Do not upload the database or full logs.

The script refuses a nonempty WAL, because strict immutable read-only SQLite
would otherwise ignore uncheckpointed commits. Close the application normally;
do not delete WAL/SHM files to bypass the check.

## Candidate change and verification boundaries

A bounded synthetic regression reproduces Beta 9 discarding verified parent and
direct-child writes before classification when its 2,048-record provisional
candidate buffer is full. The candidate classifies records against retained
process/thread facts before spending provisional capacity. Unresolved records
retain the same count/byte limits and can resolve when later evidence arrives.
Pending candidates are retried when retained lifecycle facts, the verified root,
or relevant resource/request relationships change. Unchanged background pages no
longer repeatedly scan the whole unresolved backlog. Each receiver page permits
at most two bounded scans; STOP still forces both existing final passes. The
process/thread cache, provisional record/byte, resource/request, handoff, journal
and shutdown limits remain unchanged. Raw evidence still undergoes final replay;
competing writers and late conflicts remain conservative. This is not proof of a fix for the private raw-record loss.

Completed sessions with saved loss now say:
“记录已保存；采集期间丢失了部分记录。” Saved stage counters are shown individually;
missing fields say “未提供”. Unfinished sessions retain their own warning.

The Windows comparison runs the immutable Beta 9 ZIP and the frozen candidate ZIP
sequentially on one runner, with identical fixture bytes, target writes, independent
noise count, 64 MiB journal quota and existing queue limits. The existing follow
oracle, desktop gates, process exit, persistence and fresh-process exact-session
readback remain required. Failed baseline/candidate aggregates are retained.
This runner is not the user's Parallels VM; incidental OS traffic and event arrival
timing can differ. Synthetic or cross-compilation results cannot establish live
ETW improvement. The release may be published only after the exact Windows ZIP passes all desktop
and heavy-load gates and main CI is confirmed. The published
[release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.10) contains
the authoritative same-ZIP `BUILD-IDENTITY.json`, `CAPTURE-LOSS-PAIR.json`, quality
summary and SHA-256. Before that publication, downloads are diagnostic previews.

## Short Parallels retest

1. Unzip into a new directory, launch `Contain.Viewer.exe`, select the same 7-Zip
   installer in “跟随安装器” and use the same arguments as the Beta 9 attempt.
2. After installation and saving finish, open technical details. Record receive
   queue overflow/disconnect, handoff overflow, raw quota/failure, follow candidate
   loss, and the application total. A zero ETW loss counter alone is insufficient.
3. Search this session for `write_requested` and one actual installed 7-Zip file
   such as `7z.exe`. Inspect its source event and actor instance. Missing target
   writes or Unknown attribution remain failures to investigate, not High evidence.
4. Close and reopen Contain. Open the same session ID and target event. Confirm the
   record is saved, the loss breakdown is unchanged, and any loss is described as
   capture loss rather than an unfinished save. Share only the necessary counts
   and whether that target write was retained; no database or private paths needed.


## Retained attempts and acceptance boundaries

The final integrated runtime/desktop validation source is
`3c2e06871d28acd199ba1813605afecc9074738d`; delivery documentation is separate
from the immutable ZIP source. Source and ZIP identities must agree in every
accepted artifact. Final same-ZIP checks are recorded in
[Windows CI](https://github.com/LuckyLivio/Contain/actions/runs/42216843544) and
[extended quality](https://github.com/LuckyLivio/Contain/actions/runs/42216845092).
These links record their actual outcomes; existence alone is not acceptance.

Earlier results are retained rather than replaced:

| Source / Windows attempt | Receive queue loss: Beta 9 → candidate | Candidate loss: Beta 9 → candidate | Outcome |
|---|---:|---:|---|
| `5ef1689` / [pilot](https://github.com/LuckyLivio/Contain/actions/runs/42210844846) | 64,840 → 0 | 13,421 → 0 | Passed this pilot only; different runner from later samples |
| `a42cd5d` / [diagnostic preview](https://github.com/LuckyLivio/Contain/actions/runs/42211783353) | 62,538 → 8,256 | 56,375 → 5,598 | Rejected; target requests remained Unknown |
| `a0e82e6` / [second candidate](https://github.com/LuckyLivio/Contain/actions/runs/42214562681) | 64,628 → 0 | 9,602 → 0 | Heavy-load pair passed; desktop runtime observation failed |
| `3c2e068` / [final diagnostic](https://github.com/LuckyLivio/Contain/actions/runs/42216843544) | 57,656 → 711 | 56,502 → 9,249 | Desktop passed; heavy-load pair rejected, target requests remain Unknown |

In these pairs handoff loss, raw persistence failure/quota loss and measured ETW
loss were zero. Parent and child each independently performed one successful
write; each retained two source write requests. The rejected `a42cd5d` sample
kept those requests Unknown; the successful pair samples independently verified
one High source write for each actor. Unrelated target/noise attribution was zero.
Both sides completed the fixed 36,000 background calls, exited with code 0,
saved and read the exact session back in a fresh process, and conserved all layer
counters. Different total callback/received counts are included in each aggregate;
the OS event streams were not identical, even with equal controlled workload.

The `a0e82e6` desktop failure was `InvalidOperationException` during runtime
observation, before full GUI save/cold-read acceptance. Its exact exception text
was not in the public aggregate, so that attempt's cause is unconfirmed. Review
found a definite test race in separate existence/text reads of a normally closing
status window. The final harness samples that optional status in one UIA operation
and tests missing/vanished status separately from required controls. It still fails
on other exceptions and retains every UUID, source, exit, save, cold-read and
timeout assertion. This change is test-only; product UI remains the same WinForms.

Two earlier preflight failures occurred before native capture: the Beta 10
internal fuzz-lock version was initially stale
([attempt](https://github.com/LuckyLivio/Contain/actions/runs/42209104084)); then
synthetic Python SQLite test connections were not closed before Windows cleanup
([attempt](https://github.com/LuckyLivio/Contain/actions/runs/42209788009)). Both
were corrected without skipping assertions. The standalone read-only diagnostic
already closes its connection explicitly.

Linux checks and model benchmarks do not validate live ETW. The candidate retry
model retained the same limits and target outcomes: seven release samples plus
two warmups measured 89.215 ms → 15.839 ms for unchanged background pages with a
full unresolved backlog, while pages continually introducing target resources
measured 96.536 ms → 98.596 ms. That model excludes receive queues, SQLite, actual
TDH/provider decoding and real request/completion throughput. Existing Windows
Bench and three 30-second ASan fuzz targets are separate regressions. Coverage
includes test code, not production-only or branch coverage; ignored historical
storage experiments are not counted as passed ordinary tests.


## Final diagnostic result and blocker

The last same-runner comparison is comparable under the declared fixed workload,
but **not accepted**. Raw application loss fell by 56,945 records (98.7668%),
yet the receive queue still reached its unchanged 4,096-record limit and dropped
711 records. The unchanged provisional limit also reached 2,048 records and
reported 9,249 candidate/context losses. Target and direct-child write requests
remained present (two each), but neither passed verified-source attribution;
Unknown was preserved. Handoff loss, raw persistence/quota loss, measured ETW
loss and decoding errors were all zero. Both sides saved, exited normally,
completed all independent background calls and passed fresh-process exact-session
readback and all eight layer-accounting checks. Unrelated attribution was zero.

Baseline/candidate callback counts were 734,591 / 685,023; received-record counts
were 267,858 / 216,853. Maximum receive delay was 610.017 / 229.282 ms. This is
one sequential pair on its own Windows runner, not an identical event replay,
not the earlier pilot runner and not the user's Parallels machine. The successful
zero-loss samples do not replace this failed final ZIP acceptance.

[Download the full safe loss aggregate](https://github.com/LuckyLivio/Contain/actions/runs/42216843544/artifacts/13500959496).
The actual Beta.10 delivery validator was run against all downloaded artifacts
and refused them with `Loss pair acceptance or comparable improvement is missing`.
No release assets were uploaded. The attempted direct diagnostic Release upload
also returned an upload-host credential error and created no release; CI artifact
download remains available. Formal release upload tooling is prepared but gated.

The remaining blocker is residual receiver/provisional-candidate pressure under
this bounded real workload, not a database save failure or a desktop failure.
Further work needs evidence about the remaining saturated intervals before more
changes; buffers, counters and confidence thresholds were not enlarged, hidden
or relaxed. The private original session still needs only its saved count
breakdown, not a database upload, to establish whether its loss stage matches.
No claim that its root cause has been confirmed is made.

Final checks: Windows nextest 378 passed / 0 failed / 7 skipped (no retries),
all Check/Coverage/CoverageOriginal/Dependencies/Companion gates passed; GUI,
actual package and install/save/cold-read gates passed. Coverage was 76.45% lines /
77.50% functions for the workspace, 73.87% / 76.26% for original packages;
`follow.rs` 99.06% / 98.44%, `ingest.rs` 71.00% / 64.58%. These scopes include
unit-test code; receive orchestration error/unwind paths remain partly uncovered,
and this is not branch coverage or instrumented live ETW. Historical skipped
storage experiments were not changed or counted as passed; the existing companion
regression ran separately. Linux offline persistence/render checks were 94 passed /
8 ignored; actual-source model harness 74 passed; diagnostic presentation 17 passed;
release verifier tests 28 passed. Linux cross-builds do not establish Windows
runtime validation. The final Windows ASan fuzz targets (three × 30 seconds) passed.
Existing synthetic Bench medians were +1.51% JSON, +8.43% lifetime, +8.54% completion,
+1.79% registry versus Beta.9 on that runner; all samples remain in the extended
artifact, and no speedup is claimed from those tests.
