# v0.3.1 capture pipeline verification

Date: 2026-09-29. This record distinguishes code tests, actual Windows ETW
captures, snapshot fallback, and release stress acceptance.

## Correctness and compatibility

The local Windows ordinary-token run passed `cargo fmt --all -- --check`,
`cargo clippy --workspace --all-targets -- -D warnings`, `cargo test --workspace`
(46 core tests and 1 fixture test), `cargo build --workspace --release --locked`,
seven Python scorer tests and the PowerShell scorer rejection checks. The local
release snapshot fixture also passed readback, JSON schema and dry-run invariance.
It cannot validate ETW under this token.

The added storage tests exercise committed pages before finish, byte quota and
tail balance, rollback on injected write failure, SQLite FULL via max_page_count,
an actual competing SQLite write lock, reopen after simulated interruption,
and final loss downgrade parity between full and paged reads. The interruption
test drops the writer without successful finalization. A separate actual child
process exits without running destructors or SQLite close: 256 committed records
survive and its uncommitted tail does not appear. This is not a machine power
failure test. Existing v1/v2 migration, newer-schema rejection, equal-timestamp
ordering, PID/TID lifetime ambiguity and failed-completion tests remain active.

All 169 external Cargo.lock package entries match baseline f8037e9 exactly;
canonical sorted JSON SHA-256 is
`996afd8b094de1f9a6f911cff9d4a5be707de7d52508432cb86a7f4a152abb08`.
The whole lockfile hashes differ because the workspace package version is 0.3.1.

The ETW queue tests cover nonblocking overflow and final drain over multiple pages.
Scorer tests cover one-to-one matching, unexpected High claims, no-positive null
precision, separate target/noise/failed/unsupported axes, exact PID plus birth,
overlapping syscall intervals and rejection of snapshots as raw syscall evidence.

## Actual small fixture and fallback

[Windows CI at a33ea2a](https://github.com/LuckyLivio/Contain/actions/runs/36552421260)
completed successfully, including two immediate-launch ETW fixtures and fallback.
[Machine-readable scores](examples/verification-v031-small.json) preserve each run.
These CI captures use debug; they do not supply a release speed comparison.

| Fixed group | ETW run 1 | ETW run 2 | Snapshot fallback |
| --- | --- | --- | --- |
| Target successful syscalls (17) | 17 observed, 17 correct | 17 observed, 17 correct | 0 raw observed, 0 correct |
| Independent successful noise (3) | 2 observed/unresolved, 1 unobserved | 2 observed/unresolved, 1 unobserved | 3 unobserved |
| Failed delete (1) | unobserved | unobserved | unobserved |
| Unsupported | 0 expected | 0 expected | 0 expected |
| False application attribution | 0 | 0 | 0 |
| Positive prediction precision | 1 | 1 | null |
| Application / ETW event / buffer loss | 0 / 0 / 0 | 0 / 0 / 0 | 0 / unavailable / unavailable |

The failed delete leaves the locked file present; absence of its source event is
not counted as successful observation or exclusion. Noise whose identity is
unresolved does not earn correctly-not-assigned credit. Registry path gaps retain
Degraded quality even when every target syscall has correct writer attribution.
Fallback state diffs remain useful but do not satisfy raw-operation recall.

## Measurement failures retained

The historical debug measurements remain unchanged. Both completed preliminary
release comparisons are retained in [ADR 0006](adr/0006-capture-pipeline.md) and
the examples directory; neither established the 10000-file gate. The run at
[02fe345](https://github.com/LuckyLivio/Contain/actions/runs/36551287230) failed
before any trials because a cached output directory already existed. It contains
no performance samples. Output now uses a unique per-run directory. There is no
continue-on-error around benchmarks: infrastructure, denominator, balance and
overload-protection errors remain visible; measured recall/loss gate failures are
explicit fields in the resulting data.

The [812b94a CI repeat](https://github.com/LuckyLivio/Contain/actions/runs/36553895153)
failed the required detached-writer assertion after observing 16/17 target
syscalls. It reported zero queue/ETW loss, but the FULL-sync raw writer spent
17.188 s persisting 9222 records under substantial registry-provider traffic.
Zero reported loss alone therefore does not prove complete syscall coverage.
The gate was not weakened. The later WAL policy is tested again through the same CI.

The FULL-sync [lower-scale comparison](examples/comparison-release-v031-boundary-full.json)
passed three 100-file/4-worker trials (252 successful target syscalls each) while
failing all three 1000-file trials. This is historical boundary evidence, not the
final WAL-policy acceptance result. The exact-path scorer hardening was rerun over
all 16 archived corrected-comparison/small captures and produced identical scores:
[recheck record](examples/rescore-v031-corrected.json).

[Server 2025 provider inventory](examples/providers-server2025-v031.json) records
the target runner's actual selected event metadata. With ID filtering enabled,
unsupported callbacks were zero; the filter-off 1000-file trial delivered 45919
unsupported callbacks. Both modes retained source event classes needed by the
small and burst fixtures. A single filter-off run does not establish a speed ratio.

No third-party installer was executed. These results cover the isolated synthetic
fixture on the recorded Windows environments. Defender settings were not changed.
No formal release is published.

The [first NORMAL-WAL run](https://github.com/LuckyLivio/Contain/actions/runs/36554395769)
completed all 18 paired stress trials, burst and filter-off, then failed in the
intentional-overload size sampler when SQLite removed its WAL between Test-Path
and Get-Item. [Every completed trial](examples/comparison-release-v031-wal-partial.json)
is preserved. Its 1000/10000 quality failures are real and not caused by that later
sampling error. The sampler now handles only FileNotFound/DirectoryNotFound as
an absent size sample; other errors still fail. Environment metadata is saved
before trials so a later failure cannot hide compiler/fixture/lock identities.
