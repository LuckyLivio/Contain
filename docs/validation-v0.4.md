# v0.4 development validation

Status: **development; v0.4.0 release gates are not satisfied**. Package version
remains 0.3.1. This report separates historical elevated ETW measurements, local
fallback measurements, synthetic storage experiments, and unexecuted installer
validation. No third-party installer or real uninstaller runs on the host.

## Baseline and environment

Baseline: `codex/deferred-checkpoint`,
`7c5a5b2b534d235f4db6c472c2c34a60e8be66ee`. On 2026-10-22 the working tree had
untracked `docs/preview.md`, `scripts/build-preview.py` and `ui/`; these are
pre-existing work and are excluded from this change's commits.

Local environment: Windows 11 Pro x64 build 26300, NTFS, 24 logical processors,
33,690,267,648 bytes physical RAM; ordinary non-elevated token. Rust/Cargo 1.98.1,
MSVC target, PowerShell 7.6.5, Python 3.12.7. No CONTAIN experiment overrides were
set. Cargo was installed but absent from the terminal PATH; commands below add
the existing installation to the process PATH. This is a tooling issue, not a
Contain failure. No toolchain or dependency upgrade was performed.

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo test --workspace --locked
cargo build --workspace --release --locked
./target/v04/baseline-bin/contain.exe doctor --json
python scripts/test-score.py
python scripts/test-commit-tail.py
python scripts/test-checkpoint-gate.py
python scripts/test-hotpath-artifact.py
python scripts/test-summarize-checkpoint.py
```

Original tests: 64 core + 1 fixture passed, 1 optional storage replay ignored;
release build passed. Python suites passed 7 / 9 / 6 / 3 / 14 tests respectively.
Baseline executables were preserved before production changes in
`target/v04/baseline-bin`; raw environment, hashes, commands and output are in
`target/v04/raw/baseline`. Public reproducible numeric metadata is in
[v04-baseline.json](examples/v04-baseline.json). Machine captures remain local
because they can contain unrelated service/task/process metadata.

`doctor` and an actual marked 100-file fixture capture both attempted ETW and
received access denied (`0x80070005`). Fallback completed, with zero source events,
49 file-state differences and no promoted file attribution. One service inventory
timed out at its existing 20-second bound; that warning and trial remain in the
raw result. These facts do not reproduce the elevated ETW throughput failures.
There is no verified disposable VM/checkpoint on this host; HypervisorPresent
alone is insufficient evidence of isolation or recoverability.

## Historical failures retained

| Experiment | 100 files | 1,000 files | 10,000 files | Classification |
| --- | --- | --- | --- | --- |
| Final v0.3.1 streaming comparison | 3/3 pass | 0/3 candidate vs 3/3 original baseline | 0/3 both | Queue stalls/loss; upstream ETW loss at large scale |
| Later ingest/query repair | small fixture passes | repaired candidate 3/3 pass | still fails | Throughput coverage restored at 1,000, not proof of end-to-end speedup |
| Deferred checkpoint E0/E1 | E1 small attempts 10/10 | E1 3/3; wall median 44.862 s vs E0 25.721 s | 0/3 both | Shorter live commit tails, remaining queue/ETW losses; E1 not promoted |

Sources: [performance](performance.md), [ingest repair](capture-ingest-regression.md),
[hotpath](capture-hotpath-queries.md), [commit tails](capture-commit-tail.md),
[checkpoint experiment](capture-deferred-checkpoint.md) and their complete JSON
trial archives. Results from different runners must not be combined into a speedup.
Default hotpath remains D0, and E1 remains explicitly opt-in.

## Audit boundaries

The source, tests, experiment scripts and historical reports were reviewed before
selecting changes. These are the relevant production paths and their remaining
limits; a bounded queue alone is not a claim of bounded total memory.

| Path | Finding and disposition |
| --- | --- |
| ETW callback / decoder | Existing nonblocking bounded channel and explicit overflow/disconnect/decode counters retained. Added fail-closed handling of stale file context; no provider filtering or loss denominator invented. |
| Process / thread lifetimes | Birth identity and temporal intervals are required. Duplicate/conflicting starts, late stops, ancestor ambiguity and reused parent PID now have regression coverage. |
| Filesystem snapshots | Existing scans hash every readable file with BLAKE3; reparse paths are excluded and unreadable subtrees are not called deletions. The 100,000-entry quota remains. No unsafe metadata-only hash cache or narrowed scan was introduced. |
| Registry snapshots | Values of one selected HKCU Software key, not a recursive machine registry snapshot. Value bytes retain their type and lossless encoding. Enumeration size is not globally bounded. |
| SQLite | Existing WAL/NORMAL, 256-record raw batches, atomic finalization, paged raw readers, quotas and checkpoint experiments retained. Measured readback joins/indexes changed; no claim that live writer stalls are solved. |
| Evidence / views | Operation result, actor identity and final-state ownership remain different claims. Exact event-ID updates and full/paged/explain tests prevent inconsistent stored flags. |
| Synchronization / memory | Callback mutex work and process-query costs remain instrumented. Full exports, snapshot maps and registry value buffers still materialize data; the new lookup maps add temporary linear memory. |
| Recovery | Existing subprocess abrupt-exit, busy-writer, full-disk, checkpoint and rollback tests run with the new code. These establish tested process-crash behavior, not power-loss durability under NORMAL. |

The final snapshot guard intentionally leaves runtime file/registry state
validation unpromoted in the current stop-then-snapshot pipeline. The new exact-ID
registry persistence path is unit-tested infrastructure; it is **not** evidence
that real ETW registry final-state validation has passed.

## Correctness fixes

Eight new minimal regression cases fail against the baseline: duplicated thread
start reopens an ended interval; conflicting thread start overwrites its owner;
process stop preceding start is lost; conflicting later stops extend lifetime;
file and registry operation direction conflicts promote snapshot changes;
an executable-name suffix is mistaken for an exact inventory target; and an
inventory target match is labeled as an observed writer. Red-test output is
retained at `target/v04/raw/reliability/red-tests.txt`. These are correctness
failures, distinct from queue loss, evidence gaps, and environment restrictions.

Storage audit identified and measured a quadratic evidence readback loop. The
live queue (8192), raw batch (256), evidence quota,
provider masks, drain limits, and frozen attribution gates are not relaxed.

The fixes now retain pending stop boundaries, make duplicate starts idempotent,
reject conflicting identities, shrink rather than extend lifetime endpoints,
and revoke descendant claims when an ancestor becomes ambiguous. The owned
installer handle remains independent Certain evidence. File/registry composition
checks mutation direction and ordering; unrelated/mixed writers remain Unknown.
Inventory parsing requires an exact executable token, and an `inventory_target`
Medium edge replaces the incorrect `observed_actor` edge.

Further regression tests reject duplicate/conflicting completions (including the
paged storage lookup), preserve the original records in the raw journal, and preserve an uncompleted
delete as `DeleteRequested`. File records arriving before the decoder's watermark
are rejected before object/IRP context can be reused. This increments decode
ambiguity, clears unsafe context, and suppresses session promotion. It is deliberately
conservative across objects; real ETW testing must quantify resulting coverage loss.

State validation now persists only selected event IDs in one post-stop transaction,
including registry values, updating typed and JSON stores together. Tests compare
full/history/page/explain events and inject rollback, foreign-session, unfinished
capture and late-loss cases. A same-path unselected event is never marked validated.

The audit also found a fundamental sampling gap: final snapshots run **after**
ETW stop and paged evidence postprocessing. An unrelated writer can change the resource
in that interval. The capture path now checks the monotonic trace/snapshot boundary
and keeps these final-state ownership claims **Unknown**, even with zero reported
loss and an earlier High source event. Source-event actor attribution is preserved;
the snapshot does not prove who wrote its final bytes. This intentionally reduces
state attribution coverage and does not change the frozen syscall oracle.

Validation at this stage: formatting, Clippy with `-D warnings`, locked release
build and workspace tests pass at `880b450`: **93 core + 2 fixture tests**, with three opt-in
disk experiments excluded from the ordinary suite. Original assertions and
acceptance thresholds are unchanged. Thirteen identity/attribution/completion
counterexamples were first observed failing before their fixes; all pass now.
Raw logs: `target/v04/raw/reliability` and `target/v04/raw/final-*.log`.

A final targeted audit found one additional parent-PID reuse case: revalidation
could revoke a child and then reattach it to a different birth of the same parent
PID. Promotion now respects an already-recorded parent creation time, leaving a
conflict Unknown and preserving its original evidence. Its red test and 16 passing
lifetime tests are retained as `parent-birth-{red,green}.txt`. Measurements of
candidate `07e2e86` predate this extra guard and must keep that revision label.

## Measured storage optimization

`storage/evidence.rs` used a linear search for every evidence row and repeatedly
allocated registry resource names. Exact-resource lookup maps replace that join;
they preserve the existing first-match behavior and add O(N) temporary memory.
Release disk-backed fixtures contain N files **and** N registry values, with
distinct evidence per row. All three reads per API and all verification assertions
are retained. Warm-cache timings exclude fixture writes, verification and close.

| N files + N values | Summary before/after median ms | Full read before/after median ms |
| ---: | ---: | ---: |
| 100 | 0.743 / 0.370 | 0.777 / 0.460 |
| 1,000 | 37.616 / 2.707 | 37.769 / 2.656 |
| 5,000 | 904.521 / 20.587 | 901.002 / 21.456 |
| 10,000 | 3655.371 / 42.452 | 3633.803 / 42.319 |

Database sizes are identical for this isolated map change. This is approximately
86x faster summary readback at the largest synthetic scale, **not live ETW
throughput or installer speedup**.

A separate experiment keeps a 100-file/100-value target fixed and adds unrelated
sessions, each with 1,000 files, 1,000 values and 1,000 normalized operations.
Query plans confirmed missing session indexes on `system_events` and
`normalized_operations`. Additive indexes now install on both new and existing
schema-4 databases, without changing table contracts or JSON schema 3.

| History sessions | Summary before/after ms | Full before/after ms | DB before/after bytes |
| ---: | ---: | ---: | ---: |
| 0 | 0.341 / 0.403 | 0.376 / 0.441 | 286720 / 311296 |
| 10 | 3.399 / 0.383 | 6.648 / 0.443 | 14131200 / 16932864 |
| 50 | 25.524 / 0.398 | 52.303 / 0.473 | 70021120 / 83988480 |

The tradeoff is explicit: total elapsed for the 50 history writes increased
1095.353 to 1287.679 ms (+17.56%), median write increased 16.082 to 18.983 ms,
and database size increased 19.95%. Index creation on an old database also needs
one-time time and disk space. No raw-journal index or live commit policy changed.

[Every raw timing sample](examples/v04-storage.json) includes the earlier
diagnostic without write timings; it was not discarded. Tests verify exact
evidence mapping after SQL ordering and existing schema-4 index installation
without row changes. Existing recovery, rollback, full-disk and checkpoint tests
also passed. Three explicitly opt-in experiments are ignored by the ordinary
suite; both new benchmarks were separately executed.

```powershell
$env:CONTAIN_STORAGE_BENCHMARK_OUTPUT = (Join-Path $PWD 'target/v04/readback.json')
cargo test -p contain-core --release --locked storage::perf::state_evidence_readback_scaling -- --ignored --exact --nocapture
$env:CONTAIN_STORAGE_BENCHMARK_OUTPUT = (Join-Path $PWD 'target/v04/history.json')
cargo test -p contain-core --release --locked storage::perf::history_database_readback_scaling -- --ignored --exact --nocapture
```

For the first before arm, add only `storage/perf.rs` and its test module declaration
to baseline `7c5a5b2`; the corresponding after arm adds only the evidence lookup
map change, without the later indexes. For the index before arm, keep the map fix but omit the two
new indexes in `storage/migration.rs`. Run only the named ignored test in each
command; do not change its workload, assertions, or run two disk experiments
concurrently. Full raw stdout is retained locally in `target/v04/raw/storage`.

## Local paired workload regression

All **60** planned captures completed with zero harness/metric errors. These are
explicit `--snapshot-only` experiments on the non-admin host, not ETW throughput
measurements. Each arm uses the same fixture binary and independent operation
journal; the original scorer and thresholds remain unchanged.

The initial 42 attempts compare `7c5a5b2` with `07e2e86`: five file scales with
four workers and three pairs (30 attempts), 1/8-worker sensitivity at 1000/10000
(8 attempts), and 0/1000 background files at scale 1000 (4 attempts). The later
18 attempts independently compare the same baseline with final production code
`880b450` at 100/1000/10000 files, four workers, three pairs, and 100 background
files. No earlier sample is discarded or pooled with the later revision.

| Initial candidate 07e2e86, 4 workers | Baseline median wall s | Candidate median wall s |
| ---: | ---: | ---: |
| 100 | 7.821 | 8.181 |
| 500 | 8.306 | 7.985 |
| 1,000 | 8.145 | 8.233 |
| 3,000 | 9.406 | 9.531 |
| 10,000 | 12.225 | 12.861 |

| Final candidate 880b450 | Wall s before/after | Parent CPU s before/after | Peak working set MiB before/after | Closed DB MiB before/after |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 8.380 / 7.778 | 0.609 / 0.578 | 17.87 / 17.81 | 0.39 / 0.41 |
| 1,000 | 8.198 / 8.422 | 0.766 / 0.859 | 21.58 / 21.55 | 2.26 / 2.37 |
| 10,000 | 12.713 / 12.160 | 3.156 / 3.094 | 41.69 / 41.11 | 20.89 / 21.92 |

The mixed wall-time differences do not establish an end-to-end speedup. Memory
and CPU cover the Contain parent process, excluding descendants and kernel ETW
buffers. At final scale 10000, median logical read/write transfer was baseline
71.11/42.26 MiB and candidate 64.24/44.29 MiB; these are not physical disk usage.
Every per-trial resource measurement, WAL/private-memory sample peak and group
denominator is retained in [initial 42 trials](examples/v04-local-matrix.json)
and [final 18 trials](examples/v04-local-final.json).

All 60 fallback conservatism gates pass. Their target syscall recall is **0**,
successful target/noise Unknown rate is **1**, audited-positive precision is
**null**, and wrong-attribution rate is **0**. ETW loss counters/rate are **null**,
not zero. Final snapshot differences remain Unknown. These outcomes demonstrate
conservative fallback, not useful source attribution or a satisfied release gate.

Raw records remain in `target/v04/matrix-{core,workers,load,final}`, with safe full
projections under `target/v04/public-local-*`. The numeric archives reference
projection and measurement SHA-256 values and include every attempt. Reproduction
uses `scripts/v04-matrix.py` with the parameters above, then `v04-public.py` and
`v04-summarize-matrix.py`; [the protocol](v04-validation.md) defines all boundaries.

## Elevated ETW workload regression

The first paired workflow, [39811449654](https://github.com/LuckyLivio/Contain/actions/runs/39811449654),
compared `7c5a5b2` with `07e2e86` on one elevated Windows Server 2025 runner
(build 26100, four logical CPUs, Rust 1.99.1, matching locked dependencies).
File/process ETW were active; registry was not requested for this file stress
experiment. All 18 attempts completed and published complete evidence before the
unchanged gate failed. There were no harness or measurement errors.

| Files | Gate baseline/candidate | Median wall s baseline/candidate | Parent CPU s baseline/candidate | Peak working set MiB baseline/candidate |
| ---: | --- | ---: | ---: | ---: |
| 100 | 3/3 / 3/3 pass | 6.166 / 5.388 | 0.828 / 0.984 | 19.86 / 19.76 |
| 1,000 | 3/3 / 3/3 pass | 8.967 / 8.526 | 3.875 / 3.719 | 25.06 / 26.38 |
| 10,000 | **0/3 / 0/3 FAIL** | 33.156 / 28.323 | 22.094 / 19.156 | 45.60 / 45.75 |

At 100/1000 the strict target recall and observation are 100%, precision is 1,
wrong attribution is zero, and all reported loss counters are zero. All 300 noise
operations remain observed-but-unresolved; combined target/noise Unknown rates
are therefore 54.35% and 10.71%, respectively. This is scoped synthetic evidence,
not a general precision estimate.

All six 10000-file attempts reach queue capacity 8192 and fail the application
loss, ETW event loss, observation and attribution gates. Queue overflow ranges
32,483–52,449 records; ETW event loss ranges 113,481–174,176. Reported buffer loss
is zero, which does not cancel event loss. These are demonstrated collection
failures, separate from the small-CI clock issue below. Each large capture
conservatively downgrades source attribution: recall 0, Unknown 100%, precision
null, zero asserted wrong positives. The event-loss *percentage* stays null
because the counters lack a verified common denominator.

The 10000 candidate's shorter median time **is not a performance win**: median
persisted raw events fell from 74,019 to 59,822, median ETW event loss increased
from 124,448 to 165,383, and matched target observation fell from 52.388% to
39.788%. Its wall maximum was 45.147 s versus baseline 39.125 s. At 1000 the
overlapping ranges and three samples do not establish a general speedup either.
Candidate logical writes were 824.05 MiB versus 808.74 MiB; parent working set
also increased. No failed or slow sample is removed from these comparisons.

The [complete numeric trial archive](examples/v04-etw-07.json) retains CPU,
memory, logical I/O, DB/WAL, profile phases, all counters/gates, hashes and exact
build provenance. The 154 MB public projection and workflow log are preserved
locally under `target/v04/remote/39811449654`, and the workflow artifact retains
the scoped oracle rows. This public artifact is not a complete machine trace;
private hosted raw captures were deliberately not published and are unavailable
after runner teardown. Every unmatched syscall cannot be diagnosed further from
that projection alone.

The final production revision `880b450` was then tested independently in
[run 39812655516](https://github.com/LuckyLivio/Contain/actions/runs/39812655516)
with the same 18-attempt protocol. All attempts completed with complete artifacts
and zero harness/metric errors; **9/18 pass**. The completed ETW job fails its
unchanged acceptance step. These samples are kept separate from the earlier run.

| Final 880b450, files | Baseline gate | Candidate gate | Median wall s baseline/candidate |
| ---: | --- | --- | ---: |
| 100 | 3/3 pass | 3/3 pass | 5.201 / 5.557 |
| 1,000 | **2/3 FAIL** | **1/3 FAIL** | 8.692 / 8.135 |
| 10,000 | **0/3 FAIL** | **0/3 FAIL** | 42.141 / 36.413 |

At 1000, baseline trial 3 dropped 805 queue records; candidate trials 1 and 3
dropped 221 and 241. Their ETW event-loss counters were zero, so these are
application-queue losses, not proof of upstream ETW loss. Source confidence is
conservatively revoked, giving those attempts recall 0 and Unknown 100%. This
demonstrates that even the 1000-file gate is **not stable**; the previous run's
3/3 result does not override it. At 10000 all six attempts have queue overflow;
four also have nonzero ETW event loss. All six fail and downgrade to Unknown.

All 18 final attempts retain zero strict false-positive and matched-row
incorrect-attribution counts. Positive precision is null on loss-degraded runs,
not perfect. The final candidate's median 10000 persisted count is 74,787 versus
baseline 94,261. At 1000, two candidate loss downgrades also reduce final evidence
and database work. Lower wall time, write traffic or database size under these
conditions cannot establish a performance improvement. The final
[18-trial archive](examples/v04-etw-final.json) preserves every metric, gate,
profile phase and provenance; raw public evidence is in
`target/v04/remote/39812655516`. Across the two elevated matrices, **all 36 planned
attempts** remain available. Required 1000- and 10000-file release gates fail.

## Strict clock-domain validation failure

Ordinary hosted Windows CI was inconsistent: `07e2e86` push run
[39811449454](https://github.com/LuckyLivio/Contain/actions/runs/39811449454)
passed, while its PR run
[39811456338](https://github.com/LuckyLivio/Contain/actions/runs/39811456338)
failed; `880b450` push run
[39812014521](https://github.com/LuckyLivio/Contain/actions/runs/39812014521)
failed, while its PR run
[39812022615](https://github.com/LuckyLivio/Contain/actions/runs/39812022615)
passed. Both failing fixtures had zero reported queue/ETW/decode loss, and their
exhaustive CLI view checks passed. Their original strict scores remain failed.

Inspection of all seven unexpected positive records found matching PID, process
creation time, resource and operation in the independent journal, but ETW time
was 0.1–79.3 microseconds after that journal row's syscall end. The failing target
recalls were 12/17 and 15/17, with five and two unmatched positive events. The
matched-row wrong-actor counts were zero. This is a demonstrated interval-matching
failure, not proof that the unmatched records are correct or that no attribution
bug exists. [Exact ticks and original hashes](examples/v04-clock-diagnostics.json)
preserve the diagnostic without rewriting the original score.

The locked ferrisetw 1.2.0 trace uses QPC (`ClientContext=1`) but its consumer does
not request RAW_TIMESTAMP. Despite its getter name, `raw_timestamp()` therefore
receives the ProcessTrace conversion to FILETIME; the independent fixture uses
`SystemTime::now()`. Microsoft's [consumer documentation](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew)
describes this conversion. Existing artifacts lack original QPC and calibration
data, so conversion uncertainty, wall-clock adjustment and provider emission
timing cannot be separated. No offset, tolerance, assertion or acceptance score
was changed to hide these failures.

## Changed files

| Files / groups | Technical change |
| --- | --- |
| `crates/contain-core/src/{lifetime,attribution,correlation,reliability}.rs` | Conservative lifetime, completion, state-composition and inventory evidence rules; regression cases alongside implementation. |
| `crates/contain-core/src/capture.rs`, `windows/etw.rs`, `windows/etw/decode.rs` | Trace/snapshot coverage guard, exact validation integration and stale file-context rejection. |
| `crates/contain-core/src/storage.rs`, `storage/{evidence,migration,stream}.rs` | Indexed readback, additive schema-4 indexes, atomic exact-ID persistence and completion tests. |
| `storage/perf.rs`, `storage/state_validation_tests.rs` | Disk-backed measurements, schema compatibility and rollback/view/loss regressions. |
| `examples/test-installer/src/main.rs`, `scripts/v04-*`, `scripts/test-v04-*`, `scripts/demo.ps1` | Configurable bounded worker fixture, paired measurements, independent scores, publication, strict local summarization and exhaustive CLI consistency checks. |
| `.github/workflows/{ci,v04}.yml` | Regression suites and dedicated all-attempt elevated ETW gate with evidence publication before failure. |
| `README.md`, `docs/{architecture,json-contract,v04-validation,validation-v0.4}.md`, `docs/examples/v04-*.json` | Evidence semantics, reproducible protocol and complete numeric trial archives, including failures. |

## Verification, release decision and next work

Final production code `880b450` passes 95 ordinary Rust tests, formatting, Clippy
with warnings denied, and the locked release build. All 62 Python harness/scorer
tests pass, as does the existing PowerShell scorer rejection suite. The original
opt-in disk replay was additionally executed: 16,384/16,384 records persisted,
zero failed/quota-dropped records, 64 batches, 1.616 s total on this machine.
Its raw log is `target/v04/raw/final-replay-executed.log`; an earlier incorrectly
qualified test filter ran zero tests and is retained separately, not counted.
This synthetic replay does not replace a live ETW stress run.

No third-party MSI, NSIS, Inno Setup or self-extracting installer was executed.
The local token cannot activate ETW, and a disposable recoverable guest was not
established. The hosted runner provides real ETW for the bounded fixture, but no
vendor experiment or blinded independent reference ledger was established there.
Thus real-installer precision/recall/Unknown and physical-disk metrics are
**unavailable**. The [validation protocol](v04-validation.md) supplies preflight,
Windows commands, official candidate sources, independent Process Monitor ledger
requirements and an explicit list of unverifiable/out-of-scope cases.

Release remains **blocked**, package version **0.3.1**, with no v0.4.0 tag. CLI
commands, JSON schema 3 and existing database schema 4 remain compatible. Local
development commits are synchronized to `codex/deferred-checkpoint`; no merge or
release is implied by that synchronization.

Priority work toward v0.5 (or before any v0.4 release) is:

1. Establish independently recorded QPC syscall bounds, trace clock metadata and
   bracketed FILETIME calibration; validate provider timing before changing any
   production timestamp mapping or strict oracle contract.
2. Profile the remaining 10000-file live persistence/queue stalls and ETW loss;
   measure bounded consumer/write isolation against the existing D0/E1 controls.
3. Align per-resource final snapshot coverage with the live trace before allowing
   final-state promotion. Add generation-aware bounded reordering only with
   evidence that it preserves object/IRP ownership; the global watermark is
   deliberately conservative and may reduce coverage.
4. Run the vendor matrix in a verified disposable guest with a blinded independent
   oracle. Resolve service/broker ownership only with a causal link; otherwise
   retain Unknown. Bound registry/export memory and test power-failure durability
   separately from the existing process-crash recovery tests.
