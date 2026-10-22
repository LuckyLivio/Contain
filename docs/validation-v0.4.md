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
paged storage lookup), retain all source references, and preserve an uncompleted
delete as `DeleteRequested`. File records arriving before the decoder's watermark
are rejected before object/IRP context can be reused. This increments decode
ambiguity, clears unsafe context, and suppresses session promotion. It is deliberately
conservative across objects; real ETW testing must quantify resulting coverage loss.

State validation now persists only selected event IDs in one post-stop transaction,
including registry values, updating typed and JSON stores together. Tests compare
full/history/page/explain events and inject rollback, foreign-session, unfinished
capture and late-loss cases. A same-path unselected event is never marked validated.

The audit also found a fundamental sampling gap: final snapshots run **after**
ETW stop and database finalization. An unrelated writer can change the resource
in that interval. The capture path now checks the monotonic trace/snapshot boundary
and keeps these final-state ownership claims **Unknown**, even with zero reported
loss and an earlier High source event. Source-event actor attribution is preserved;
the snapshot does not prove who wrote its final bytes. This intentionally reduces
state attribution coverage and does not change the frozen syscall oracle.

Validation at this stage: formatting, Clippy with `-D warnings`, locked release
build and workspace tests pass: **92 core + 2 fixture tests**, with three opt-in
disk experiments excluded from the ordinary suite. Original assertions and
acceptance thresholds are unchanged. Twelve identity/attribution/completion
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
also passed (30 storage tests; three explicitly opt-in experiments ignored by the
ordinary suite, with both new benchmarks separately executed).

```powershell
$env:CONTAIN_STORAGE_BENCHMARK_OUTPUT = (Join-Path $PWD 'target/v04/readback.json')
cargo test -p contain-core --release --locked storage::perf::state_evidence_readback_scaling -- --ignored --exact --nocapture
$env:CONTAIN_STORAGE_BENCHMARK_OUTPUT = (Join-Path $PWD 'target/v04/history.json')
cargo test -p contain-core --release --locked storage::perf::history_database_readback_scaling -- --ignored --exact --nocapture
```

For the first before arm, add only `storage/perf.rs` and its test module declaration
to baseline `7c5a5b2`. For the index before arm, keep the map fix but omit the two
new indexes in `storage/migration.rs`. Run only the named ignored test in each
command; do not change its workload, assertions, or run two disk experiments
concurrently. Full raw stdout is retained locally in `target/v04/raw/storage`.
