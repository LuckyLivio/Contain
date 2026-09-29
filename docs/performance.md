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

## Bounds and overload behavior

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
