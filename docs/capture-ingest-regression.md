# 1000-file ingest regression repair

This experiment stays on `fix/capture-ingest-regression`, project version 0.3.1.
It does not change the historical 10000-file failure or publish a performance release.

## Version and measurement boundaries

| Identity | Commit | Meaning |
| --- | --- | --- |
| Previously passing baseline | `f8037e90658cdf46aab93d397508036ebc7c22ee` | Original in-memory capture, retained as a comparator |
| Starting regression | `bef6ac17cf3c816733e661fb76dc8974f5a30f12` | Starting main; only documentation differs from the previously measured `fabfc16` |
| A, profiled regression | `f5f40ebc945b8796d238cd632667f8a1313bcb19` | Instrumentation and synthetic replay; original write policy |
| B1, direct binding | `fbaeb543d9408db589a34b285c042b96cb0584cf` | Typed scalar INSERT parameters; same indexes |
| B2, deferred indexes | `ccbf110fcec58cb82aa8c73046bbe8795e4dacb2` | Durable light staging; post-capture indexed materialization |
| B2, ordered reads | `e04b91d3f4bed4d552c5b62f53c84e0e50ff270e` | Corrects an inefficient UNION read; same capture write path |
| C, bounded derived transactions | `0d54a35c1ee64afe1b218009029545b7dbda913c` | At most four pages per derived transaction, one page in Rust at a time |

Every main comparison builds release with `--locked`, uses the same independent
fixture/scorer and four workers on one elevated Windows/NTFS runner. Trials alternate
order. Cross-workflow runner differences prevent attributing percentages across runs.
1000-file acceptance requires 2500/2500 observed and correctly attributed in all three
trials, no relevant losses, no false attribution and no queue tail. Only after that
does the workflow run the unchanged 10000/25000, >=95%, zero-loss, three-trial gate.

External wall time includes setup, capture, index materialization, all derived work,
final FULL transaction, profiling output and SQLite close. Export/scoring are outside.
Parent peak memory excludes descendants and kernel buffers. DB/WAL peaks are separate
samples, not simultaneous combined usage. NORMAL commits preserve application-crash
recovery but do not promise survival of power/OS failure for every raw batch.

## Confirmed bottleneck before optimization

The profiled A run measured these stages separately (milliseconds, trials 1/2/3).
Nested stages must not be added together as independent wall time.

| Stage | Elapsed ms, 1 / 2 / 3 |
| --- | --- |
| Queue extraction | 5.92 / 5.56 / 4.09 |
| Process polling | 531.65 / 508.94 / 483.28 |
| Live lifecycle ingest and attach | Less than 2 in each trial |
| Rust JSON serialization | 37.73 / 25.09 / 20.06 |
| Raw INSERT including indexes | 173.19 / 195.72 / 156.20 |
| Raw COMMIT including automatic checkpoint | 1466.65 / 3794.04 / 1513.40 |
| Longest interval without taking the queue | 638.82 / 1628.23 / 794.94 |
| Longest raw COMMIT | 631.14 / 1626.98 / 787.34 |
| Post-processing raw readback | 104.34 / 100.28 / 87.20 |
| Completion queries | 33.75 / 32.05 / 24.34 |
| Derived-table COMMIT | 9331.47 / 46247.38 / 6349.32 |
| Final loss downgrade and FULL transaction | 6985.50 / 12611.11 / 4267.83 |

Queue overflows were 2210/1850/5041; peak admitted attempts in fixed 100 ms windows
were 6300/5357/11134. Raw batch flush P50/P95/P99/max were respectively
4.211/170.988/633.821/633.821, 4.095/55.377/1627.146/1627.146 and
3.938/94.590/789.664/789.664 ms. These are flush execution latencies, not a claim
about total event latency; queue delay is separately recorded by the ETW receiver.

Commit-path stalls explain substantial receiver gaps; serialization and lifetime
association are much smaller. Checkpoint time has **not** been isolated from WAL
write/scheduling/storage delay. Checkpoint scheduling was consequently left unchanged.
The later post-processing bottleneck is separate from capture-period overflow.

## Changes and bounds

* `storage/stream.rs` binds session, sequence, FILETIME ticks, kind, header PID and
  related event directly. The raw JSON format is preserved.
* `raw_ingest` keeps the sequence primary key and time/page index. After trace stop,
  `prepare_raw` copies indexed pages into the historical `raw_stream`, deletes staging
  rows, and commits atomically. Historical indexes are never dropped. Failure keeps
  all previously committed raw evidence and an unfinished/failed session.
* SQLite schema 4 adds staging; v1/v2/v3 read/migration tests preserve historical data.
  Project and exported capture-envelope versions remain 0.3.1 and 3.
* Post-processing still reads 256-record keyset pages. C groups at most four pages
  in an outer transaction, without retaining four decoded pages in Rust. Per-page
  savepoints and outer rollback preserve atomicity on failure.
* No new writer thread, event source, callback ownership change, queue enlargement,
  attribution policy, fixture or scorer change was introduced.

The existing 8192-record source queue, 256-record/1 MiB raw batch, 256 KiB raw-record
limit and default 256 MiB per-session serialized-payload quota remain. Lifecycle
and context limits remain in ADR 0006. The payload quota is not a physical DB/WAL
size cap; indexes, derived data and atomic staging materialization consume additional
disk space. A bounded burst passing does not establish sustained throughput.

Runtime SQLite reports **3.50.2**, synchronous **1/NORMAL**, page size **4096**, and
automatic checkpoint threshold **4096 pages**. Final completion still switches to
FULL. No journal/synchronous OFF or concurrent SQLite writer is used. SQLite's
[WAL documentation](https://www.sqlite.org/wal.html) describes the single-writer
constraint, commit-thread automatic checkpoints and reader-pinned WAL growth;
[synchronous documentation](https://www.sqlite.org/pragma.html#pragma_synchronous)
defines the NORMAL/FULL durability distinction. The checkpoint threshold is not a
hard WAL cap. Tests exercise application exit without destructors, not power failure.

## Preserved experiments

| Data | Workflow | Result |
| --- | --- | --- |
| [A](examples/capture-regression-A.json) | [36572945116](https://github.com/LuckyLivio/Contain/actions/runs/36572945116) | Candidate 1000 gate 0/3; all nine samples retained, including one failing old-baseline sample |
| [B1](examples/capture-regression-B1.json) | [36573375899](https://github.com/LuckyLivio/Contain/actions/runs/36573375899) | Direct binding alone still lost 646 queue records; 2376/2500 observed, zero correct after loss downgrade |
| [B2](examples/capture-regression-B2.json) | [36573966806](https://github.com/LuckyLivio/Contain/actions/runs/36573966806) | 2500/2500 observed and correct in all three trials, zero relevant loss/false attribution/tail; slower end-to-end |

B2 wall times were 45.135/36.930/34.346 s; capture periods 3.055/3.124/3.355 s;
post-processing 38.228/29.975/26.983 s. Same-run old baseline wall times were
10.758/7.462/6.835 s; the regressed version took 13.575/48.801/16.190 s and failed
all three. B2 parent peaks were 24.92/26.61/25.80 MiB, final DB 90.44/90.15/90.35
MiB and sampled WAL peaks 35.34/35.50/35.59 MiB. Noise operations remained separately
scored; observed-but-unresolved noise is not credited as confirmed exclusion.

The first three workflow runs completed real ETW comparisons but failed their later
synthetic replay step because its relative diagnostic output path was resolved under
the test's crate working directory. Replay stdout measurements are preserved as
partial diagnostics, not successful test runs. The final workflow uses absolute paths.
The B2 UNION read inflated post raw readback; e04b91d switches between the atomically
exclusive tables and verifies indexed ordering without a temporary sort.

[B1 CI 36573348414](https://github.com/LuckyLivio/Contain/actions/runs/36573348414)
also failed its small fixture: two registry events were 2/3 FILETIME ticks outside
the frozen oracle intervals, and seven parent file operations had unresolved actors.
The two unmatched positive claims remain failures. No interval tolerance or exclusion
was added. Later CI results do not erase this sample.

## Profiling and recovery checks

Set `CONTAIN_PROFILE_PATH` to an absolute output path to enable new diagnostics.
With it absent, the new stage distributions and arrival/overflow tracing are disabled.
The sidecar is written once, after ETW stops. It contains no event paths or payloads:
bounded log2 histograms, up to 4096 batch count/byte/time samples and an omitted count.
Batch percentiles are exact nearest-rank values over retained samples; stage
histogram percentiles are explicitly labeled upper bounds by
`scripts/summarize-profile.py`. The first 64 overflow times plus final overflow time
are bounded diagnostics. Newer metrics use one capture clock; A/B1/B2's original
overflow clock starts at source creation and must not be mistaken for the sidecar epoch.

Tests cover partial final batches, stop with queued records, a brief actual SQLite
writer lock, quota/full-disk/write failure, index-copy interruption and rollback,
raw evidence with incomplete derived data, grouped-write rollback, abrupt process
exit, PID/TID reuse, failure non-promotion, final loss downgrade, paging/explain and
old database migration. Core tests: 50 passed and one ignored synthetic benchmark;
fixture: one passed; scorer: seven Python cases plus PowerShell rejection cases.
Local release snapshot fallback also passed. Real ETW results below come from the
authorized elevated runner, not the non-elevated local desktop.


## Final same-run comparison

[Run 36576076223](https://github.com/LuckyLivio/Contain/actions/runs/36576076223)
measured **0d54a35**, not the later reporting commits. Windows build 26100, four
logical processors, elevated token, NTFS, Rust 1.98.1, release --locked. All four
versions ran on that same runner, in forward/reverse/forward order for each scale.
The common fixture executable SHA256 was
`D99707DBCAA278854F0996BC9FB92C3D20529EB0DE41D62F9DBB16B89BFF5F63`;
the frozen scorer SHA256 was
`AA20DC9B8103C6E79ED3D96B00F6B0219E76D9771F2749A58FFEE79A56689AB7`.
The executable hash is distinct from the source-file hash. Background load and
filesystem cache state were not controlled; failures and slow samples are retained.

[Permanent measurements and complete bounded profiles](examples/capture-regression-final.json)
include all 27 real trials and all 12 synthetic replays. The
[raw artifact](https://github.com/LuckyLivio/Contain/actions/runs/36576076223/artifacts/11039319695)
includes per-trial exports, independent truth, scorer output, metadata and profiles.
Its ZIP SHA256 is `71fecc322614e8ae01584954fda0258d7f3d8fcb2311045823d1a3678eab0b87`.
GitHub currently retains it until 2026-12-28; the compact measurements are committed.

### 1000 files / 4 workers / 2500 successful target operations

C passed **all three** trials: every target operation observed and correctly
attributed, zero ETW event/buffer loss, queue/retention/persistence loss, tested false
attribution, pending queue records or drain timeouts. Counter balances hold.
The 300 noise operations in each C trial were observed but identity-unresolved;
none is counted as a confirmed correct exclusion. Overall quality remains Degraded
because this scoped collector cannot establish a complete machine footprint.

| Version / trial | Observed / correct | Queue loss | Wall s | Parent MiB | Final DB MiB | Sampled WAL peak MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline / 1 | 2500 / 2500 | 0 | 12.599 | 47.45 | 37.38 | 37.72 |
| regression / 1 | 2210 / 0 | 1602 | 27.615 | 26.16 | 62.92 | 39.06 |
| light / 1 | 1622 / 0 | 5997 | 15.177 | 25.06 | 44.67 | 32.93 |
| candidate / 1 | 2500 / 2500 | 0 | 32.705 | 27.00 | 90.12 | 35.39 |
| candidate / 2 | 2500 / 2500 | 0 | 34.855 | 25.72 | 90.21 | 35.46 |
| light / 2 | 2500 / 2500 | 0 | 55.216 | 26.27 | 90.21 | 35.50 |
| regression / 2 | 2174 / 0 | 2814 | 20.749 | 25.21 | 57.82 | 24.71 |
| baseline / 2 | 2500 / 2500 | 0 | 7.594 | 47.09 | 37.23 | 37.58 |
| baseline / 3 | 1921 / 0 | 3028 | 13.312 | 34.46 | 19.67 | 19.92 |
| regression / 3 | 2065 / 0 | 2357 | 19.595 | 26.36 | 59.72 | 37.83 |
| light / 3 | 2500 / 2500 | 0 | 55.395 | 25.74 | 90.30 | 35.73 |
| candidate / 3 | 2500 / 2500 | 0 | 35.628 | 26.25 | 90.28 | 35.52 |

All entries above had zero ETW event/buffer loss and tested false attribution. The
historically passing baseline failed trial 3 this time; this sample is preserved.
C median wall was **34.855 s**, versus baseline **12.599 s** and regression **20.749 s**
(2.77x and 1.68x respectively). Failed comparisons processed fewer records and
produced less derived evidence, so their lower times are not equal-quality speed wins.
**Intermediate functionality recovered in the candidate's three trials; speed did
not recover to the old baseline.** This is not a universal reliability guarantee.

Comparable existing phase timers give capture periods of baseline 3.170/2.797/3.644 s
and regression 3.589/3.546/3.157 s (installer_root + descendant_quiet_drain; the latter
already contains stop/drain). Their separately recorded post stages sum to
3.352/3.473/3.794 s and 17.661/10.590/10.550 s respectively, including association,
after snapshot/inventory and final persistence. These old coarse sums omit overhead
between stages; they are not the newer continuous post_wall timer.

| C phase / resource | Trial 1 | Trial 2 | Trial 3 |
| --- | ---: | ---: | ---: |
| Capture receive wall, s | 2.768 | 2.843 | 2.504 |
| Post wall, s | 28.549 | 30.690 | 31.798 |
| Raw index materialization, s | 4.658 | 4.535 | 5.168 |
| Raw readback, ms | 153.45 | 150.99 | 154.37 |
| Lifecycle resolve, ms | 2.69 | 2.54 | 2.68 |
| Completion queries, ms | 46.92 | 44.44 | 47.03 |
| Event attribution, ms | 13.03 | 13.02 | 13.00 |
| State association, ms | 13.75 | 12.02 | 11.17 |
| Derived writes excluding outer commit, s | 1.512 | 1.459 | 1.512 |
| Derived group commits, s | 15.011 | 16.791 | 17.643 |
| Summary write, s | 5.956 | 6.440 | 6.065 |
| Final quality downgrade + FULL commit, ms | 7.52 | 39.82 | 14.01 |
| Batch flush P50 / P95 / P99=max, ms | 1.754 / 20.300 / 437.166 | 2.310 / 10.940 / 535.150 | 1.685 / 13.763 / 247.214 |
| Longest interval without queue take, ms | 442.933 | 540.363 | 253.158 |
| Queue high water / capacity | 7872 / 8192 | 7541 / 8192 | 6756 / 8192 |
| Peak arrival attempts per 100 ms | 9426 | 8294 | 4997 |
| Persisted records / capture second | 5854 | 5699 | 6471 |

The last row includes quiet drain time and is not sustained saturation throughput.
Batch counts were 65/66/66, persisted records 16201/16202/16202, payload bytes
15053217/15053927/15053614; individual count/byte/time samples are in the JSON.
Queue max delays were 543.925/643.173/373.385 ms. No overflow timestamps exist for
these passing C trials. Post wall ends before diagnostic output and connection
close; their residual, plus setup, is included in external wall, not separately timed.

### Ablation and remaining hypothesis

* B1 removes repeated JSON extraction but did not eliminate commit stalls or recover
  the real 1000 gate. Same-run synthetic INSERT totals dropped from A's
  121.44/122.23/121.07 ms to 110.23/106.98/101.17 ms; total write/wall improvements
  were inconsistent.
* B2 reduced synthetic INSERT totals further to 37.79/37.93/36.92 ms. Its earlier
  three-trial ETW comparison passed, but here the light comparator failed trial 1
  with 5997 queue drops. Raw COMMIT max was only 59.79 ms while process polling max
  was 464.28 ms and no-queue-take max 469.51 ms. Storage is not the only receiver stall.
  Deferred indexes move real cost into measured post-processing and add disk work.
* C changes only post-stop derived transactions. Compared with B2's **successful**
  trials 2/3, post time fell 45.863 -> 30.690 s and 51.036 -> 31.798 s, wall fell
  55.216 -> 34.855 s and 55.395 -> 35.628 s. Synthetic post improved in every paired
  round (table below). Retain C for this measured benefit. C cannot causally explain
  different capture loss between versions sharing the same capture write path.

The next single hypothesis to verify is that synchronous process polling/global
callback work prevents sufficiently regular draining once raw INSERT cost is reduced.
The light failure and callback volumes support investigation, not a complete causal
proof. Self-I/O amplification and the division of commit latency into checkpoint,
filesystem and scheduler time remain unisolated. No fourth scheme or larger queue
was introduced. At 10000, commit stalls also remain material.

### Same-run synthetic disk replay (explanation only)

Every replay persists 16384 generated safe records / 12706288 JSON bytes to a real
SQLite file, then materializes indexes and derived data. Alternating A/B1/B2/C order
was reversed in round 2. No ETW, real system events, or capture acceptance is implied.

| Scheme / trial | Raw write s | Records/s | Payload MiB/s | Max raw commit ms | Post s | Full wall s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| A / 1 | 0.927 | 17682 | 13.08 | 705.220 | 10.066 | 11.119 |
| A / 2 | 0.492 | 33325 | 24.65 | 272.068 | 8.786 | 9.404 |
| A / 3 | 0.505 | 32470 | 24.01 | 289.003 | 8.487 | 9.146 |
| B1 / 1 | 0.953 | 17194 | 12.72 | 726.902 | 10.995 | 12.398 |
| B1 / 2 | 0.475 | 34508 | 25.52 | 263.671 | 9.593 | 10.242 |
| B1 / 3 | 0.473 | 34656 | 25.63 | 276.377 | 9.630 | 10.252 |
| B2 / 1 | 0.919 | 17835 | 13.19 | 787.198 | 13.257 | 14.292 |
| B2 / 2 | 0.704 | 23273 | 17.21 | 569.351 | 15.034 | 15.858 |
| B2 / 3 | 0.493 | 33210 | 24.56 | 362.561 | 12.183 | 12.904 |
| C / 1 | 0.882 | 18572 | 13.74 | 748.350 | 12.548 | 13.572 |
| C / 2 | 0.321 | 51090 | 37.79 | 172.328 | 12.063 | 12.595 |
| C / 3 | 0.387 | 42357 | 31.33 | 261.053 | 11.009 | 11.528 |

All replay batches committed without loss. Final DB bytes per input record (including
all derived data, not raw-only storage) were 3541.00 for A/B1 and 3541.75 for B2/C.
Sampled WAL peak bytes per input were 1035.79 versus 1035.03. These are file occupancy
ratios, not physical bytes written/write amplification; total filesystem writes were
not measured. Full per-stage P50/P95/P99 upper bounds and batch distributions are
recoverable from the committed sidecars with `scripts/summarize-profile.py`.

### Original 10000-file gate

The candidate passed 1000 first, then ran the unchanged 25000-operation gate.
**All three 10000 trials failed**; source loss correctly suppressed application
attribution. The workflow deliberately ends red at strict stress acceptance.

| C trial | Observed / 25000 | Correct | Queue loss | ETW events lost | Wall s | Capture s | Post s | Parent / DB / WAL MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 7620 (30.48%) | 0 | 80119 | 85144 | 95.037 | 7.752 | 85.961 | 45.47 / 202.74 / 92.78 |
| 2 | 7419 (29.68%) | 0 | 66271 | 134687 | 118.176 | 7.689 | 109.148 | 44.11 / 210.66 / 98.25 |
| 3 | 5878 (23.51%) | 0 | 112801 | 0 | 161.237 | 8.125 | 151.444 | 40.95 / 162.35 / 77.12 |

ETW buffer loss, retention/persistence loss, false attribution and final queue tails
were zero. Zero false positives with zero correct attribution is not a pass.
Every baseline/regression/light 10000 trial also failed; their full results remain
in the committed JSON. No original threshold or historical failure was changed.

The immediate-exit 1000-write burst passed 1000/1000, zero loss/false/tail, 10.184 s.
The filter-off diagnostic failed (1792/2500 observed, zero correct, 4921 queue drops);
provider configuration in the main comparisons was not changed. The intentional
zero-quota test reported 16215 retention drops, zero target positives and Incomplete;
it validates degradation only. Small release fixture passed 17/17 target operations,
zero false attribution/loss; noise had two unresolved observations and one missing
operation, and the deliberately failed delete was unobserved and not promoted.

## Delivery identity and CI

The measured binary is **0d54a35c1ee64afe1b218009029545b7dbda913c**.
Subsequent **87c19d5ef847897fa1116c3388b251ecd7c778c5** adds error propagation from
failed derived groups to unfinished-session quality and its regression test; it
changes no successful SQL/WAL path. Its [ordinary CI passed](https://github.com/LuckyLivio/Contain/actions/runs/36576406689).
Later report/artifact commits are not additional stress runs. The acceptance script
also now explicitly rejects drain timeouts; all candidate 1000 trials above already
had `drain_timed_out=false`, so this check does not reinterpret their result.

The branch is an intermediate repair, with slower full completion and a failed
10000 gate. Main's stability statement and release status remain unchanged.
