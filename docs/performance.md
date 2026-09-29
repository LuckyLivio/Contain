# Reproducible performance measurements

```powershell
./scripts/stress.ps1 -Files 10000 -SnapshotOnly
./scripts/stress.ps1 -Files 10000 -RequireEtw
```

The manual **Attribution stress** Actions workflow accepts 4–100000 files and
uploads its benchmark and reliability output. Normal CI uses the small fixture.
The workload runs four child processes, each writing and renaming files and
deleting half of them. 10000 files produce 25000 independently logged syscalls.
Everything stays in a marked direct TEMP child and the matching fixture HKCU key.
The script cleans only these fixtures through the guarded fixture cleanup mode.

## Local measured baseline (2026-09-29)

Windows 11 build 26200, non-admin, NTFS, Rust debug, snapshot mode. One sequential
baseline/capture pair; no repeated-trial average or statistical speed claim.
Source revision and full data: [benchmark JSON](examples/benchmark-win11-fallback-v3.json).

| Measurement | Actual result |
| --- | ---: |
| Files / worker processes | 10000 / 4 |
| Expected instrumented operations | 25000 |
| Unobserved baseline wall time | 7.084 s |
| Capture wall time through SQLite commit | 15.082 s |
| End-to-end added time / ratio | 7.998 s / 2.129× |
| Contain peak working set sampled during run | 37,662,720 B (35.92 MiB) |
| SQLite file size after close | 12,316,672 B (11.75 MiB) |
| Admitted ETW events/s | 0 (ETW disabled) |
| Retained timeline observations | 5010 |
| Expected operations observed via final state | 5000 |
| Correctly attributed / Unknown / Incorrect | 0 / 25000 / 0 |
| Application drops | 0 |

This fallback result measures state collection, not ETW throughput. Transient
operations that leave no final-state evidence are intentionally missing. Unknown
includes missed operations. False attribution was zero in this bounded workload.

## ETW stress result (2026-09-29)

[Actual manual Actions run](https://github.com/LuckyLivio/Contain/actions/runs/36542627758),
Windows Server 2025 build 26100, elevated token, NTFS, debug.
[Complete benchmark JSON](examples/benchmark-server2025-etw-v3.json).

| Measurement | Actual result |
| --- | ---: |
| Files / workers / expected syscalls | 10000 / 4 / 25000 |
| Baseline / capture wall time | 13.200 s / 43.316 s |
| Added time / ratio | 30.115 s / 3.281x |
| Contain sampled peak working set | 204,836,864 B (195.347 MiB) |
| SQLite size | 188,751,872 B (180.008 MiB) |
| Admitted source observations / rate | 202755 / 4680.87 per s |
| Application drops | 102755 |
| ETW events lost / buffers lost | 5912074 / 0 |
| Decode ambiguities/errors | 4 |
| Expected observed / correctly attributed / Unknown / Incorrect | 5374 / 0 / 25000 / 0 |
| Capture quality | **Incomplete** |

This run exceeded the 100000 raw-event retention limit and lost ETW events. It
**does not establish reliable large-installer coverage**. The passing workflow
confirms measurement, persistence, explicit loss reporting and zero incorrect
claims against the fixture; application attribution was deliberately suppressed.
The persisted timeline has 105001 entries because snapshot/synthetic observations
are added after the bounded raw-event vector. ETW's event-loss count covers enabled
provider traffic, including activity outside the watched roots; it is not the number
of missed fixture syscalls. Optimizing callback decoding and reducing provider traffic
remain necessary before claiming high-volume coverage.

## Measurement boundaries

The same instrumented fixture runs once without Contain, then once through Contain.
Wall time includes inventories, descendant waiting, quiet drain, hashing and SQLite
commit. It includes fixed capture overhead; it is not installer slowdown isolated
from setup/teardown. Ground-truth scoring and JSON export run outside the timed
region. Baseline also pays the fixture logging cost.

Memory samples read the parent process's `PeakWorkingSet64` every 20 ms. They exclude
fixture descendants and kernel ETW buffers, and may miss a process that exits before
the first sample. `admitted_events_per_second` divides decoded/admitted source
observations by full capture wall time; it is not total machine event throughput.
Database size is measured after the CLI connection closes. These are debug builds.

## Historical v0.3 bounds and overload behavior

- Producer channel: 8192 observations, nonblocking `try_send` with drop accounting.
- Session raw-event vector: 100000. Excess records are counted and discarded.
- Process lifetimes: 32768; retained thread intervals: 131072.
- File contexts, pending IRPs and registry contexts: 16384 each.
- Directory notification path set: 100000; excess/error/rescan counts are visible.
- SQLite observations, details, operations and edges commit in one transaction.
- File-state joins use a path index, avoiding a complete event scan per file.

No source-loss run can claim a complete footprint. Loss/decode/overflow or drain
timeout gives Incomplete; application promotion is suppressed when source continuity
is unverified. Raw metadata, known actor identity and the loss counters remain
available. The event cap does not bound file-snapshot hashing size or every byte of
memory; large watched trees and global lifecycle traffic still have a cost.

## v0.3.1 hardening and release protocol

The above debug results are preserved and are not optimization baselines.
[ADR 0006](adr/0006-capture-pipeline.md) freezes supported-operation denominators
and 10000-file acceptance before optimization. The
[initial instrumented release diagnostic](examples/profile-server2025-release-v031-before.json)
identified identity-query cost, channel/retention losses and expensive final writes;
its noisy in-root oracle is also diagnostic only.

The manual **Release capture comparison** workflow builds instrumented baseline
`f8037e90658cdf46aab93d397508036ebc7c22ee` and candidate using release + locked
dependencies, on one runner/compiler/token/filesystem. Both run the same fixture
and independent scorer, with truth outside watched roots and independent noise.
Three pairs alternate order at 100, 1000 and 10000 files; exit burst, intentional quota
exhaustion and a small release fixture are separate cases.

```powershell
./scripts/compare-release.ps1 -BaselineDirectory <baseline-checkout> -Files 10000 -Trials 3
# Ordinary-token fallback is measured separately, not an ETW comparison:
./scripts/compare-release.ps1 -BaselineDirectory <baseline-checkout> -Files 1000 -Trials 3 -SnapshotOnly
```

Current bounds and recovery are in the ADR. Raw evidence persists throughout
capture; it no longer stops at a 100000-entry Vec. Queue reads and association
passes are bounded pages. SQLite includes indexes, raw journal, typed evidence and
document copies; payload quota is distinct from physical DB/WAL size. Full export
and scoring are outside measured capture wall time and can be expensive themselves.

## Release experiment archive

Every completed sample remains available, including regressions and partial workflow
runs. Different rows ran on different hosted runners and must not be combined into
an optimization percentage. Comparability applies to paired trials within one run.

| Candidate / data | Workflow | Result and reason retained |
| --- | --- | --- |
| [a241758](examples/comparison-release-v031-preliminary.json) | [36549354334](https://github.com/LuckyLivio/Contain/actions/runs/36549354334) | All stress gates failed; fragmented external truth writes flooded ETW |
| [4e83759](examples/comparison-release-v031-preliminary2.json) | [36550542524](https://github.com/LuckyLivio/Contain/actions/runs/36550542524) | All stress gates failed; bounded drains still blocked on persistence |
| 02fe345 | [36551287230](https://github.com/LuckyLivio/Contain/actions/runs/36551287230) | No trial data: cached output directory collision; fixed with per-run UUID |
| [6f0f524](examples/comparison-release-v031-corrected.json) | [36551832145](https://github.com/LuckyLivio/Contain/actions/runs/36551832145) | Corrected common oracle, file ID filter; candidate regressed against a passing 1000-file baseline |
| [519bb5c](examples/comparison-release-v031-boundary-full.json) | [36553473182](https://github.com/LuckyLivio/Contain/actions/runs/36553473182) | FULL-sync candidate passed 100 files three times; 1000 failed three times |
| [9ee491f](examples/comparison-release-v031-wal-partial.json) | [36554395769](https://github.com/LuckyLivio/Contain/actions/runs/36554395769) | NORMAL WAL: all 18 paired trials completed, 100 passed, 1000/10000 failed; later overload WAL-size sampling race stopped workflow |

The external oracle now writes one serialized JSON row per WriteFile call through
one handle per process. It remains outside the watched business roots for both
binaries. Exact-path exclusion hardening was rerun over all 16 archived captures
from 36551832145; [all scores were unchanged](examples/rescore-v031-corrected.json).
No target operations or failed gate thresholds were removed.

## Final measured decision (2026-09-29)

**The 10000-file acceptance gate failed.** The streaming candidate also regressed
at 1000 files, where the baseline passed all three trials. This revision provides
bounded/crash-readable evidence and explicit degradation; it is not a successful
medium/large-installer throughput release. The largest repeatedly passing tested
multi-worker scale is 100 files, not 1000 or 10000.

[Completed workflow](https://github.com/LuckyLivio/Contain/actions/runs/36555457090),
[full machine-readable comparison](examples/comparison-release-v031-final.json),
[raw capture/truth/score artifact](https://github.com/LuckyLivio/Contain/actions/runs/36555457090/artifacts/11027707469).
Baseline `f8037e90658cdf46aab93d397508036ebc7c22ee`; candidate `fabfc168a4ef7497625d91b18e6a06295d011053`.
Same Windows Server 2025 build 26100 / NTFS / elevated 4-CPU runner,
Rust 1.98.1, release --locked, common fixture/scorer, three alternating pairs per
scale. All 169 external dependency lock entries are identical. Hashes and complete
environment metadata are in the JSON. Background load and caches are uncontrolled;
Defender is unchanged. The first baseline inventory took 5.004 s and was retained.

### Every paired trial, fixed denominators

Times include complete CLI capture and final close. Slashes list trials 1/2/3;
none are omitted. Correct attribution uses the same target-success denominator
as observation. Each stress trial additionally has 300 successful independent
noise operations. Failed and unsupported denominators are zero in these stress
cases, with null rates; the small fixture tests a failed delete separately.

| Files / target syscalls | Mode | Wall seconds, trials 1 / 2 / 3 | Median s | Observed targets, trials 1 / 2 / 3 | Correct targets, trials 1 / 2 / 3 | Gate passes |
| --- | --- | --- | ---: | --- | --- | ---: |
| 100 / 252 | baseline | 9.163 / 4.933 / 5.043 | 5.043 | 252 / 252 / 252 | 252 / 252 / 252 | 3/3 |
| 100 / 252 | candidate | 5.953 / 5.382 / 5.679 | 5.679 | 252 / 252 / 252 | 252 / 252 / 252 | 3/3 |
| 1000 / 2500 | baseline | 7.245 / 6.996 / 6.802 | 6.996 | 2500 / 2500 / 2500 | 2500 / 2500 / 2500 | 3/3 |
| 1000 / 2500 | candidate | 16.498 / 15.278 / 16.745 | 16.498 | 1938 / 1999 / 1938 | 0 / 0 / 0 | 0/3 |
| 10000 / 25000 | baseline | 13.550 / 13.839 / 14.356 | 13.839 | 7846 / 8210 / 7335 | 0 / 0 / 0 | 0/3 |
| 10000 / 25000 | candidate | 40.801 / 39.998 / 35.606 | 39.998 | 4008 / 3827 / 3027 | 0 / 0 / 0 | 0/3 |

100 files means four workers with 25 files each, including 13 deletes each:
252 target syscalls. At 10000 files the frozen denominator remains exactly 25000.
Candidate observation rates are 77.52–79.96% at 1000 and 12.108–16.032% at 10000;
correct attribution is 0% at both scales because loss suppresses promotion.
Zero false positive application claims in these runs does not make them useful.
No-positive precision is null. Independent noise is observed 300/300 in every
normal candidate stress trial, but its writer is unresolved: it receives no
correctly-not-assigned credit. Noise was started before tracing, so removing live
file identity queries trades that identity coverage for retained-lifetime-only
resolution. The data does not hide this cost.

### Layer losses and resource use

The following byte measurements are medians of the three trials, expressed in
MiB (1048576 B). Parent peak working set excludes children and ETW kernel buffers.
DB and WAL peaks are separate; do not add independently sampled peaks as simultaneous
use. There is no spool file. Admission rate divides backend admission attempts by
complete capture wall time, including rejected records; it is not useful throughput.

| Files | Mode | Parent peak MiB | DB after close MiB | WAL sampled peak MiB | Admitted records / capture s | Queue overflow, trials 1 / 2 / 3 | ETW events lost, trials 1 / 2 / 3 |
| ---: | --- | ---: | ---: | ---: | ---: | --- | --- |
| 100 | baseline | 20.97 | 6.86 | 7.03 | 626.5 | 0 / 0 / 0 | 0 / 0 / 0 |
| 100 | candidate | 19.43 | 16.21 | 16.90 | 556.8 | 0 / 0 / 0 | 0 / 0 / 0 |
| 1000 | baseline | 47.24 | 37.29 | 37.64 | 2317.1 | 0 / 0 / 0 | 0 / 0 / 0 |
| 1000 | candidate | 26.01 | 57.30 | 29.72 | 982.5 | 2933 / 2640 / 2950 | 0 / 0 / 0 |
| 10000 | baseline | 94.00 | 76.56 | 77.14 | 3207.3 | 0 / 0 / 0 | 579497 / 555726 / 607695 |
| 10000 | candidate | 42.01 | 116.41 | 42.37 | 2603.9 | 75090 / 81179 / 127708 | 187223 / 167357 / 0 |

All 21 recorded trials balance their applicable stage counters and end with zero
pending records. Candidate context loss, decoder failure, enqueue-disconnected,
raw quota loss (except intentional overload), and raw persistence failures are
zero. RealTimeBuffersLost is zero; LogBuffersLost is null because there is no ETL
file logger. These zeroes do not cancel nonzero queue/ETW event loss.
At 10000 files the candidate allocated 9 / 8 / 12 kernel buffers of 64 KiB
(0.5625 / 0.5 / 0.75 MiB), under the unchanged 4 MiB configured maximum.
It persisted 22936 / 22972 / 19135 raw records. The removed 100000 Vec ceiling was
not the limiting factor in these runs: queue rejection occurred first. Lower
memory with lower retained coverage is not a demonstrated useful-throughput gain.

### Remaining measured bottleneck

At 1000 files, raw persistence takes 1.386 / 0.987 / 1.648 s while the root workload
finishes in 0.564 / 0.623 / 0.595 s; the 8192-record queue fills. At 10000 files,
raw persistence takes 5.440 / 5.681 / 10.859 s and paged finalization takes
19.331 / 18.491 / 12.352 s. Callback identity queries are now zero in file-only
stress, but total callback elapsed at 1000 files is still about 0.66 s for both
baseline and candidate. Additional provider traffic and persistence work remain.
No unsupported claim of a net callback speedup or isolated WAL percentage is made.

The SQLite journal, typed tables, event documents and indexes duplicate evidence
for recovery/compatibility and bounded reads. Write amplification and synchronous
consumer stalls are still material. The latest tested WAL policy reduces frequent
sync barriers but does not establish the required throughput. Further work needs
a separately scoped consumer/persistence design experiment; no further tuning,
driver or broader rewrite is folded into this version.

### Burst, overload and small release fixture

* Immediate-exit burst: 1000/1000 target writes observed and correctly attributed,
  zero queue/ETW/buffer/context loss, 6019 raw records persisted, pending zero;
  9.481 s wall, 23.20 MiB parent peak, 36.10 MiB SQLite. It is a different workload
  from multi-worker write/rename/delete and cannot enlarge that acceptance scale.
* Intentional zero-byte quota: 16215 admitted/dequeued records, all 16215 quota
  rejected, zero persisted, no pending tail, zero other source-layer losses,
  High=0, quality Incomplete, precision null. Overload protection passed; its
  ordinary useful-attribution gate correctly failed.
* [Small release scorer result](examples/verification-v031-release-small.json):
  targets 17/17 observed and correct; noise 2/3 observed but unresolved, 1/3
  unobserved; failed delete 1/1 unobserved and its file remains. False attribution 0.
  Registry path gaps preserve Degraded quality. This does not establish broad
  registry or third-party installer compatibility.

Read-only summary/page APIs are ready for a bounded evidence-viewer prototype,
but medium/large capture readiness is not established. Formal GUI expansion is
not the next acceptance milestone: resolve the measured 1000-file regression and
repeat the unchanged 10000-file gate first. A GUI also needs a genuinely read-only
SQLite open path, and must display unfinished/loss/Unknown states without promotion.
