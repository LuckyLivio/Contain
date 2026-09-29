# v0.3 verification record

Date: 2026-09-29. Reference implementation: `a8d58f88767bff3466f4145c66c391f7cd83ab44`.
The final main revision is additionally gated by the same CI workflow, including a
second immediate-launch ETW run and independent scorer rejection tests.

## Real CI evidence

- [Windows CI](https://github.com/LuckyLivio/Contain/actions/runs/36542590559): success.
  Formatting, Clippy, 38 Rust tests, build, live ETW and snapshot integration passed.
- [Manual 10000-file stress](https://github.com/LuckyLivio/Contain/actions/runs/36542627758):
  success as a measurement/false-attribution gate; capture itself was **Incomplete**.
- [Actual terminal summaries](demo-output.txt),
  [actual short-lived writer event](examples/file-event-v3.json),
  [ETW stress data](examples/benchmark-server2025-etw-v3.json).

## Small fixture measured results

| Metric | ETW | Explicit fallback |
| --- | ---: | ---: |
| Expected instrumented operations | 21 | 21 |
| Observed | 20 | 17 |
| Correctly attributed | 17 | 0 |
| Unknown (includes unobserved and independent noise) | 4 | 21 |
| Incorrect attribution | 0 | 0 |
| Application drops | 0 | 0 |
| ETW events lost / buffers lost | 0 / 0 | unavailable |
| Capture quality | Degraded | Degraded |

**False attribution: 0 in the measured fixture.** This is not a universal accuracy
claim. One row represents one independently instrumented syscall. A matching raw
operation requires resource, operation and exact start/end FILETIME interval.
Compatible final state can establish observation but never the writer. Correct
application attribution additionally requires matching PID plus creation time.
Unknown includes correctly excluded unrelated operations and missing observations.
The locked-file delete failed before a matching mutation event was delivered; it
stayed unobserved/Unknown and the file survived. Noise registry state was observed
without inventing a known writer.

The five installation processes include a child that exits within milliseconds
and a detached child that writes after root exit. Original ancestry and issuing
thread intervals recover their operations. Fixture completion is awaited separately
by the scorer, so missing late capture cannot shrink the expected denominator.

Registry set-value paths were recovered for installer-family operations using
verified object context. Many provider records still lacked absolute paths; the
session counted these gaps and did not promote final registry-state ownership.
Actor High/resource Unknown remains a valid explicit result.

## Gates

- Simulated PID reuse, overlapping/missing lifetime intervals and retained ancestry.
- Failed IRP completion never normalizes as a deletion; stable file identity proves
  snapshot rename/replacement without guessing actor or intermediate chain.
- Bounded quiet/live-descendant/timeout policy, producer overflow and quality handling.
- Real v1 and v2 migration to v3; reused PID persistence; atomic rollback on event
  collision; raw details/confidence/edges/operations and stable equal-time ordering.
- Safe fixture isolation, unrelated file plus registry noise, no promotion in fallback.
- JSON v3 validation for inspect/diff/history/doctor/explain, repeated history equality,
  file and registry content unchanged by `remove --dry-run`.
- Scorer rejection tests inject a wrong PID/birth and a falsely owned noise event;
  the oracle must detect both. Exact actor, Unknown and timestamp boundaries are tested.

## Experiments that exposed limitations

An earlier immediate-launch CI failed the short-lived write gate. Inspection found
the ETW dependency used asynchronous provider enablement. v0.3 now confirms provider
configuration with a bounded synchronous call. Repeated validation then exposed
a second readiness gap: file writes and process-stop records arrived while the
earlier process/primary-thread starts were absent. A private probe callback now
confirms that the realtime consumer is receiving before installer launch. This is
an acknowledgement, not a guessed delay. The strict gate remains and CI repeats
the immediate-launch scenario. Raw scoped actor lifetimes are retained so a rejected
identity can be audited. The benchmark reference above predates this extra readiness
handshake; use the scripts to measure the current revision.

The ETW stress run exceeded the raw retention cap and reported 102755 application
drops, 5912074 ETW events lost and four decode ambiguities/errors. Application
attribution was suppressed; all 25000 ground-truth operations remained Unknown,
with 5374 observed and zero incorrect. See [performance](performance.md) for measured
time, memory, database size and the limits of each metric. This demonstrates safe
loss handling, not reliable high-volume coverage.

Local Windows 11 build 26200 used a non-elevated token. Actual ETW startup returned
access denied; automatic and explicit fallback demos passed. Doctor successfully
queried 361 services, 214 scheduled tasks and 22 startup entries during verification.
These are read-only inventory probes, not fixture-created system registrations.

## Reproduce

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo build --workspace --locked
./scripts/test-score.ps1
./scripts/demo.ps1
./scripts/demo.ps1 -SnapshotOnly
./scripts/demo.ps1 -RequireEtw -OutputDirectory ./target/demo-etw
./scripts/stress.ps1 -Files 10000 -RequireEtw
```

The RequireEtw switches assert existing privileges; they never elevate. The
application remains local, without GUI, AI, driver, real cleanup or telemetry.
