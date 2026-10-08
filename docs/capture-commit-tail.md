# Capture commit-tail investigation

Date: 2026-10-08. Starting revision: `abfd190`. This is a diagnostic change,
not a throughput fix or a change to the attribution acceptance criteria.

## Question and frozen controls

Does the bounded queue overflow while the receiving thread is inside a raw
SQLite commit, and how much of the measured overflow occurs in those windows?
The previous final D3 experiment removed receiver process queries, but retained
multi-second raw commit stalls and failed all three 10000-file gates.

Keep D0 as the default. Keep the D3 selector, 8192-record queue, 256-record /
1 MiB batches, 256 MiB payload quota, WAL NORMAL / 4096-page autocheckpoint,
final FULL sync, provider configuration, fixture, scorer and drain deadlines.
No new writer, checkpoint thread, SQLite hook or checkpoint scheduling policy
is introduced. Existing recovery, loss downgrade and failed-trial records remain.

## Measurement

With `CONTAIN_PROFILE_PATH` set, the existing profile now includes queue counter
samples before and after every raw batch flush and its nested commit. Each
`queue_spans` entry records stage, start/end nanoseconds relative to the existing
profile epoch, and independent enqueued/overflow/dequeued/pending counters.
Sampling uses atomic loads without a decoder lock, event payload or per-event
allocation. JSON is still written only after ETW stops. At most 8192 spans are
retained; `omitted_queue_spans` makes truncation visible.

The timestamp window brackets the counter reads as well as the operation. It
includes a small sampling overhead. Counters are **not an atomic snapshot**:
pending is approximate, and concurrent counter fields must not be combined into
an exact queue balance. A single monotonic overflow counter's delta measures
overflows between its two loads. Commit and enclosing batch deltas must be
reported separately, never added together. The original final pipeline counters
remain the acceptance source.

Historical profiles retain batch windows and only the first 64 overflow times.
Overlap of those samples cannot establish where the remaining losses happened.
The companion analyzer supports both formats and keeps missing/truncated evidence
explicit. Neither timestamp overlap nor an overflow counter delta identifies
the reason a commit was slow.

SQLite documents that its automatic checkpoint can run on the committing thread
and cause occasional slow commits, but the current measurement includes other
SQLite, filesystem and scheduling work. Checkpoint causality remains unproven.
See [SQLite WAL performance](https://www.sqlite.org/wal.html#performance_considerations).
Installing a [WAL hook](https://www.sqlite.org/c3ref/wal_hook.html) would replace
the existing automatic checkpoint hook; this investigation does not do that.

## Validation protocol

1. Run format, Clippy, workspace tests and both frozen fixture scorers.
2. Check the analyzer with controlled overlapping/nested windows, missing data,
   counter regressions and truncated samples.
3. Run the existing Windows CI and `hotpath.yml` final protocol: ten fixed small
   attempts and three alternating D0/D3 1000-file pairs. Run the original three
   10000-file pairs only when both preceding gates pass. Keep every failure.
4. Publish numeric diagnostics and the existing fixture-only exports, not global
   machine inventories. Report historical comparisons separately from new runs.

## Separate timebase uncertainty

The scorer uses integer FILETIME strings and exact inclusive bounds. The prior
one-tick failure is not explained by Python floating-point precision. ETW uses
QPC converted by ProcessTrace to FILETIME, while the fixture uses SystemTime;
process lifetime payloads also supply wall-clock timestamps. Current artifacts
do not retain enough calibration evidence to prove the cause of a mismatch.
No tolerance or attribution rule is changed by this patch.

Microsoft documents [ProcessTrace timestamp conversion](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew)
and the [ETW clock choices](https://learn.microsoft.com/en-us/windows/win32/etw/wnode-header).
Raw QPC and paired wall-clock diagnostic samples would be a separate experiment.

## Results

Results are recorded below after verification. Local non-elevated fallback does
not establish ETW stress coverage, and a successful measurement is not a passing
throughput gate.

Local format, Clippy and workspace tests passed: 58 ordinary tests, one ignored
disk replay. The real nonblocking channel test forces two Full errors from a
separate producer while the consumer is held, then checks both span counters and
final draining. Nine analyzer tests and seven frozen Python scorer tests passed,
as did the PowerShell scorer and snapshot fixture JSON/readback/dry-run checks.

[Historical D3 summary](examples/commit-tail-historical.json) preserves the three
10000-file profiles' hashes. In each trial all first 64 recorded overflow times
fall within one approximately two-second flush window. The other
106376 / 103110 / 103247 losses lack individual timestamps. No proportion of
all historical losses during COMMIT is inferred. All 22 archived final
profile/result pairs parse successfully with the new analyzer.

Run the analyzer on a matching pair (the metadata argument can be omitted when
`result.json`, `measurement.json` or `metadata.json` is beside the profile):

```powershell
python scripts/analyze-commit-tail.py profile.json result.json --max-windows 8 --output commit-tail.json
```

The first new ordinary CI ([37710777744](https://github.com/LuckyLivio/Contain/actions/runs/37710777744))
failed Clippy because the runner's newer Rust deprecated the pre-existing
`AtomicU64::fetch_update`. The follow-up uses an equivalent explicit relaxed CAS
loop compatible with the local Rust 1.98.1 and adds a concurrent counting test.
That failure happened before ETW integration; it supplies no capture result.
The concurrently dispatched diagnostic experiment remains identified by its own
`6f0e520` binary, before this counter API compatibility edit.

The analyzer also accepts an explicit `backend.pipeline: null` from snapshot
fallback. It reports absent counters and unmeasured stage statistics as null,
not zero loss; unknown input structures still fail validation. This was checked
against the actual local snapshot fixture as well as its focused test.
