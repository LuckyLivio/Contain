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
Raw evidence still undergoes final replay; competing writers and late conflicts
remain conservative. This is not proof of a fix for the private raw-record loss.

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
ETW improvement. Measured results and a download entry will be recorded after the
Windows run; until then this is an unaccepted candidate.

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
