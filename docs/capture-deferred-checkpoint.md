# Deferred checkpoint experiment (E0 / E1)

**Decision, 2026-10-08: keep E1 experimental.** E1 passed all ten small attempts
and all three 1000-file trials, but neither E0 nor E1 passed any 10000-file trial.
E1 shortened live commit tails and retained more target observations at 10000
files, while application queue overflow and upstream ETW event loss remained.
There is no demonstrated end-to-end speed benefit: the 1000-file wall median was
44.862 s for E1 versus 25.721 s for E0. This candidate is not promoted to a default.

This is an opt-in candidate on top of the commit-window diagnostic, not a default
policy change. Both arms use D3 and the same release binary, fixture, frozen scorer,
queue, raw batch size, raw evidence quota, provider configuration and drain gate.

| Setting | E0 | E1 |
| --- | --- | --- |
| Live raw journal | WAL, synchronous NORMAL | WAL, synchronous NORMAL |
| Live automatic checkpoint | 4096 pages | disabled (0 pages) |
| After producer stop, queue drain and final raw batch | existing behavior | one explicit PASSIVE checkpoint |
| Before derived indexing | 4096 pages | restored to 4096 pages |
| Final completion transaction | synchronous FULL | synchronous FULL |

`CONTAIN_CHECKPOINT_VARIANT=E0` or `E1` enables the experiment.
`CONTAIN_WAL_BUDGET_BYTES` defaults to 536870912 (512 MiB) in either arm; it is
invalid without an explicit variant. Unset both variables for existing behavior.
The experiment workflow fixes `CONTAIN_HOTPATH_VARIANT=D3`; it does not change the
default D0 selection.

## Storage boundary and failures

Both arms sample physical WAL length and available disk space at initialization
and before/after each raw batch. The 512 MiB WAL threshold is separate from the
256 MiB serialized raw evidence quota. Reaching it stops further raw retention,
records an error, preserves committed evidence, and makes the session incomplete.
Sampling permits transaction-level overshoot; it is not a hard physical file cap.
The disk guard stops raw writes when available-to-caller space is at or below
64 MiB. This reserve is a sampled check, not a reservation or a guarantee against
other writers exhausting the disk. Postprocessing and other database writers are
outside the raw-retention budget.

Stream metadata records initial and peak sampled WAL bytes, peak growth,
overshoot, sample count and minimum available disk bytes. The external harness
also samples DB/WAL files every 20 ms through process close, so its peak covers
postprocessing as well and may differ. Neither measurement is a continuous peak.

PASSIVE runs exactly once. Its busy value, total log frames, checkpointed frames,
error and duration are retained. Success requires busy=0 and equal nonnegative
frame counts. A pinned reader can prevent completion even with busy=0. There is
no retry or escalation to FULL/TRUNCATE. Failed/partial checkpoint leaves a failed,
unpromoted session, without inventing lost-event counts. Automatic checkpoint is
restored before indexing; failed restoration stops postprocessing. A borrowing
guard also restores on early capture errors and Rust unwinding. Process termination
bypasses this guard, but connection-local settings do not persist into a reopened
connection.

Raw NORMAL commits are crash-readable before finish. These process-crash checks
do not prove power-loss durability: postponing the checkpoint extends the window
of unsynced NORMAL transactions. The final completion commit still uses FULL.
See SQLite's [WAL durability discussion](https://www.sqlite.org/wal.html) and
[PASSIVE checkpoint contract](https://www.sqlite.org/c3ref/wal_checkpoint_v2.html).

## Frozen validation order

1. Formatting, Clippy, Rust storage/recovery tests, unchanged scorer rejection
   tests, diagnostic tests, and experiment-gate/artifact tests.
2. Ten E1 small attempts: five ordinary and five with registry noise, all D3.
3. Three alternating E0/E1 1000-file pairs. All three E1 attempts must satisfy the
   existing exact 2500/2500 observation/attribution gate; all six must satisfy the
   experiment configuration/storage gate. Baseline quality failures remain data.
4. Only if steps 2 and 3 pass, three alternating E0/E1 10000-file pairs, retaining
   every failed attempt and the existing 95%/zero-loss/zero-false-positive gates.

Run through the existing dispatcher:

```powershell
gh workflow run pipeline.yml --ref codex/deferred-checkpoint -f query_phase=checkpoint
```

Reliability and speed are separate outcomes. Stress capture wall time includes ETW
stop, tail flush, checkpoint, derived indexing, final FULL commit and process/DB
close; export and scoring are excluded. Small capture wall also excludes build and
scoring; its separately recorded harness wall includes them. Timing stages overlap
and must not be added. `capture_receive_wall` and `descendant_quiet_drain` end after
ETW stop/drain, before tail flush/checkpoint; `post_raw_checkpoint` records the
explicit E1 operation. This diagnostic boundary is updated in both arms.

The public artifact contains fixture-scoped evidence, frozen scores, numeric
metadata, profiles and partial-attempt records. Full machine captures stay private.
The measured decision and all fixed attempts are recorded below.

## Implementation validation (2026-10-08)

Candidate implementation: `b9a37352c22ee22f9bd7104f8edfa484a871ef7e`.
Local formatting and Clippy passed; 65 Rust tests passed with one optional disk
replay ignored. The new recovery tests cover live commit visibility, pinned-reader
partial PASSIVE completion, WAL threshold overshoot, early-error/unwind restoration,
and abrupt process termination before tail flush, before checkpoint, after checkpoint
and after the final FULL commit. Frozen Python scorer tests (7), PowerShell scorer
rejection cases, commit-window diagnostics (9), experiment gates (6) and safe partial
artifact tests (3) passed.

The local E1 snapshot fallback demo passed JSON/readback/dry-run checks. It reported
NORMAL=1, live autocheckpoint=0, PASSIVE 86/86 frames, restoration=4096 and no stream
error. This fallback run is not an ETW performance or attribution result.

Both [push CI](https://github.com/LuckyLivio/Contain/actions/runs/37714745845) and
[PR CI](https://github.com/LuckyLivio/Contain/actions/runs/37714777518) passed on
Windows, including two real ETW fixture runs and the fallback fixture. The fixed
[checkpoint experiment](https://github.com/LuckyLivio/Contain/actions/runs/37714766925)
uses the same candidate commit.

## Fixed experiment results

The experiment completed with a **failed quality gate**, not a build, checkpoint,
budget or artifact failure. All 22 attempts were retained. The source revision,
D3 settings, expected target denominators, manifest reconciliation and 94 source
file hashes were verified. A second audit independently unioned the overflow
counter intervals and matched the reported commit/flush counts. No profile batches
or queue spans were omitted.

Runner: elevated Windows 10.0.26100, NTFS, 4 logical processors, Rust 1.99.0,
release `--locked`. Both arms share the same source, lockfile and fixture binary;
background load and disk/cache scheduling remain uncontrolled. These are three
paired stress samples, not a general performance guarantee.

| Cohort | Original quality passes | Storage experiment checks | Complete capture wall median |
| --- | ---: | ---: | ---: |
| E1 small (5 ordinary + 5 noisy) | 10/10 | 10/10 | 12.801 s |
| E0 1000 files | 3/3 | 3/3 | 25.721 s |
| E1 1000 files | 3/3 | 3/3 | 44.862 s |
| E0 10000 files | 0/3 | 3/3 | 108.314 s |
| E1 10000 files | 0/3 | 3/3 | 173.307 s |

Every 1000-file trial observed and correctly attributed all 2500 target operations,
with zero queue overflow/ETW loss and no drain timeout. The matching pairs expose
the difference between the improved live commit tail and complete-process time:

| Pair | E0 wall | E1 wall | E0 maximum live commit | E1 maximum live commit | E1 explicit checkpoint |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 25.721 s | 23.327 s | 496.365 ms | 18.840 ms | 403.895 ms |
| 2 | 28.193 s | 61.940 s | 453.706 ms | 20.648 ms | 462.900 ms |
| 3 | 23.928 s | 44.862 s | 268.867 ms | 12.097 ms | 409.736 ms |

E1 was slower in two of three valid pairs; its wall median was 74.42% higher in
this run. Much of the variability is in post-capture derived-group commits
(E1 8.784 / 36.992 / 22.692 s). The experiment does not isolate the underlying disk
or scheduling cause of that variability.

All six 10000-file attempts failed the original observation/attribution and
zero-loss gates. The target denominator stays 25000 in every row:

| Pair / arm | Observed targets | Correct targets | App queue overflow | ETW events lost | Drain timeout | Complete wall |
| --- | ---: | ---: | ---: | ---: | --- | ---: |
| 1 / E0 | 10010 | 0 | 28016 | 385144 | yes | 162.145 s |
| 1 / E1 | 16430 | 0 | 28465 | 88137 | no | 350.701 s |
| 2 / E0 | 9973 | 0 | 53738 | 149058 | yes | 108.314 s |
| 2 / E1 | 14587 | 0 | 33511 | 114888 | no | 173.307 s |
| 3 / E0 | 9731 | 0 | 58261 | 123653 | yes | 94.104 s |
| 3 / E1 | 14157 | 0 | 13045 | 209652 | no | 142.208 s |

All six sessions reached the stored `finished` state but retained `Incomplete`
quality; pipeline completion is separate from acceptance.

E1 target observation rates were 65.72%, 58.35% and 56.63%; E0 rates were 40.04%,
39.89% and 38.92%. Correct attribution remains zero because the existing integrity
rules suppress promotion after event loss. This is not evidence that the newly
retained events have valid final attribution. Queue loss is not uniformly lower
(E1 pair 1 is slightly worse), and upstream ETW loss is worse in E1 pair 3. Those
two loss channels must stay separate.

E1 persisted 97311 / 82997 / 79003 raw rows versus E0 55486 / 55617 / 55599.
Because the large trials are incomplete and retain different workloads, their
wall times are descriptive only, not a valid speedup comparison.

## Remaining stalls and storage footprint

| 10000-file pair | E0 max live commit | E1 max live commit | E0 overflow increments inside commit | E1 inside commit | E1 inside whole flush |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 1203.057 ms | 173.057 ms | 26887 / 28016 | 7529 / 28465 | 28303 / 28465 |
| 2 | 1133.472 ms | 153.385 ms | 53728 / 53738 | 24087 / 33511 | 32165 / 33511 |
| 3 | 732.482 ms | 170.833 ms | 58239 / 58261 | 7047 / 13045 | 13045 / 13045 |

Deferral removes most of the previous live commit tail, but commit and the rest
of batch persistence still coincide with overflow. These are counter-window
measurements, not causal attribution to any particular SQLite or filesystem call.
Commit and whole-flush windows overlap and are never added together.

The common WAL/disk sampler itself has E1 maximum elapsed calls of 279.505,
111.193 and 115.317 ms at 10000 files (totals 0.901, 0.661 and 0.697 s). Sampling
includes metadata, path resolution and available-disk queries plus scheduling;
the current counters do not isolate which part stalled. This guard therefore
needs its own narrowly scoped follow-up before attributing all remaining delay
to SQLite commit. E1 post-capture wall was 339.945 / 160.155 / 131.041 s; derived
group commits alone occupied 234.225 / 99.138 / 81.077 s. Moving checkpoint timing
does not address that larger completion cost.

All 22 storage checks passed: accepted raw rows equal persisted rows, with zero
raw persistence failures, quota drops, decode errors or context evictions. All
16 E1 checkpoints completed with busy=0 and autocheckpoint restored to 4096.
The large E1 checkpoint frames were 34291/34291, 29273/29273 and 27838/27838,
taking 1.117, 4.042 and 0.778 s respectively. The ten small samples are also retained;
their slowest explicit checkpoint took 4.248 s.

| E1 10000-file pair | Raw-stage sampled WAL peak | Whole-process sampled WAL peak |
| --- | ---: | ---: |
| 1 | 134.734 MiB | 181.738 MiB |
| 2 | 115.018 MiB | 155.213 MiB |
| 3 | 109.379 MiB | 146.773 MiB |

No raw-stage 512 MiB threshold was reached. Minimum available disk space across
all attempts was 33936072704 bytes, above the 64 MiB guard. E0 large raw WAL peaks
were 16.247 / 16.357 / 16.428 MiB; whole-process peaks were 114.452 / 115.242 /
115.658 MiB. Deferral increases the storage footprint in this experiment.

## Reproduction and next decision

[Fixture-only artifact](https://github.com/LuckyLivio/Contain/actions/runs/37714766925/artifacts/11524416149):
38447885 bytes, GitHub-reported archive digest
`sha256:809a3ac535575e3131550d54f494a317fab49449e44558a9fddd6243061b0c6e`.
The compact, payload-free report is [checkpoint-final.json](examples/checkpoint-final.json).
It includes all attempts, per-source hashes, original quality gates, separate
storage checks, verified eligibility, profile stages and recomputed queue windows.

```powershell
gh run download 37714766925 -n capture-checkpoint-safe-fixture -D target/checkpoint-37714766925
python scripts/summarize-checkpoint.py target/checkpoint-37714766925 --run-url https://github.com/LuckyLivio/Contain/actions/runs/37714766925 --commit b9a37352c22ee22f9bd7104f8edfa484a871ef7e --output docs/examples/checkpoint-final.json
python scripts/test-summarize-checkpoint.py
```

The summarizer has 14 passing regression tests, including wrong commits/settings,
fixed denominators, missing/duplicate trials and conflicting manifests. It preserves
original scoring and recomputes diagnostics with the pinned existing analyzer.

The next experiment should isolate remaining consumer pauses (especially the
synchronous WAL/disk sampler and non-commit flush work), upstream ETW delivery/
decode cost, and post-capture derived-group commit cost. Keep these as separate
measurements/changes with the same gates. This run does not justify changing the
default, enlarging queues to hide loss, or claiming that checkpointing is the only
bottleneck. E1 remains an inspectable candidate in draft PR #2.
