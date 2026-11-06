# Beta 10 capture-loss investigation

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
