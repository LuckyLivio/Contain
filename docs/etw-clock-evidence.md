# ETW clock evidence closeout

This task starts from `0824a64fd297e7954eeef76ed9802dbbbb6bd73b` on
`fix/windows-read-only-validation`, with no later remote commits or local changes.
The task branch is `fix/etw-clock-evidence`. Version remains **0.3.1**.

## Existing failure

[CI 41031889635](https://github.com/LuckyLivio/Contain/actions/runs/41031889635)
failed both ETW fixture runs. The unchanged frozen scorer reproduces each original
score: 17 target calls, 14 observed/correctly attributed, three unmatched positive
events, zero matched-row incorrect attribution. The
[six-event audit](examples/etw-clock-six-event-audit.json) pins the input hashes and
records each event ID, provider evidence, actor instance, available thread/name
facts, candidate interval, delivered header and exact failed comparison.

| Run | Original event ID | Category | After candidate end (100 ns ticks) |
|---|---|---|---:|
| First | `13560504-64c0-43e2-83e8-0b473e9efa80` | Registry set value | 9 |
| First | `15ade15f-29a7-48ad-9739-c926eeb5133b` | Registry set value | 11 |
| First | `5e7abef8-7db3-4f1b-a224-4fc1d4c0885a` | Registry set value | 13 |
| Repeat | `058120c9-26f8-4776-b6d1-59d53e08c4d5` | Registry set value | 1 |
| Repeat | `99ab1979-d046-4ce0-ad0f-8e9d682ead16` | Registry set value | 8 |
| Repeat | `68be7909-eb5e-4f9f-a693-10ed0061f68b` | File write request | 2 |

These are same-actor/resource/category **candidates**, not proven matching calls.
All six old events lack original QPC and independent invocation QPC bounds. The
10 MHz trace frequency and calibration samples cannot reconstruct that missing
evidence. The old result remains **D: insufficient evidence**. A registry decoder
provider constant is not a retained original header GUID; the file's recorded
cached name is not a verified current path. Earlier write-stack observations are
not evidence about these six events.

## One fixed diagnostic before any verification pair

The initial CI change schedules **one** invocation of the existing 21-operation
fixture through `scripts/ci-etw-clock.ps1`. It retains the existing Check,
Coverage, CoverageOriginal, Dependencies, Companion, synthetic CLI and snapshot
fallback steps. No 32-run acceptance, scale matrix or third-party installer runs.
There are no automatic retries. The two subsequent ETW verification attempts are
not scheduled until this evidence identifies a minimal repair.

The wrapper uses new absolute sibling directories under `target`, outside the
fixture's temporary watch/database locations:

```powershell
./scripts/ci-etw-clock.ps1 -OutputDirectory ([IO.Path]::GetFullPath('target/etw-clock-diagnostic')) -PublicOutputDirectory ([IO.Path]::GetFullPath('target/etw-clock-public'))
```

It sets `CONTAIN_FIXTURE_CLOCK=1` and points `CONTAIN_ETW_RAW_CLOCK_PATH` at the
private directory's new `raw-clock.json`, then restores both variables. The
existing companion must become ready before installer launch. `-KeepArtifacts`
retains the fixture, independent truth journals and database for offline analysis
within the same runner. No external session is stopped or elevation requested.

The existing clock analyzer preserves a general provider-emission requirement
as unmet, and currently returns 2 even when clock observations are available.
The wrapper preserves that exit and the original score; a `finally` analysis
separately reports whether the required clock evidence was actually produced.
It does not change `validation_readiness` to ready or swallow the failed demo.
The diagnostic CI step stays failed when the original demo fails.

The analysis reuses the existing bounded read-only raw-stream export, exact
event-key/descriptor companion validation, and layered verifier's same-event
fact comparison. Final actor evidence is joined only by the same event ID after
raw fact consistency checks. Independent truth never supplies production actor
identity. The write-specific layered API protocol is not used as a substitute
for the standard fixture's registry and file observations.

## Evidence and publication boundary

Private evidence includes both invocation QPC endpoints, delivered FILETIME and
same-session original ETW QPC, actual headers/frequency, exact identity links,
raw/final views, original score, clock analysis, source/build/binary identity,
script byte hashes before and after, and loss/quota/drain state. Comparison uses
integer QPC with no offset or tolerance. Initial consumer header loss counters
are not final consumer statistics; unavailable values remain unavailable.

The wrapper attempts offline analysis even after capture, view or scoring failure.
Missing files or incomplete clocks produce a specific unavailable result, never
an empty passing observation. Original errors and full logs stay private.

Only the new analyzer's fixed-field summary is uploaded as `etw-clock-evidence`.
It retains fixture event IDs, exact numeric bounds, same-event source links,
reviewed fixture identities, replaced resource paths and integrity/hash evidence.
Complete companion binaries, databases, host/boot identifiers, unrelated process
identities and raw logs are not uploaded. The old broad fixture exporter is used
only for the unchanged snapshot fallback, whose clock diagnostic is disabled.

The same-input old score and original-clock observations are separate outputs.
Diagnostic completeness is not attribution acceptance, and non-reproduction is
not a historical fix. A later protocol correction requires its own reviewed
commit and regressions for wrong actor/resource/operation, ambiguity, true
out-of-interval records and missing clocks before changing CI's decision.

Classifications remain explicit: **A** conversion/unit/precision/serialization or
comparison defect; **B** native event timing differs from the independent call
interval's semantics; **C** actual actor/resource/operation attribution defect;
**D** insufficient evidence. The exact source SHA and each observed result are
recorded by the CI artifact, including failures and unresolved items.

## First diagnostic and the bounded export repair

The single diagnostic ran at `cbc7a02b281e36e8d1a39394ab68c171abc981d7` in
[CI 41045468693](https://github.com/LuckyLivio/Contain/actions/runs/41045468693).
The quality and CLI checks passed: nextest 286 passed/0 failed/7 skipped; 21 Python
scripts ran 285 tests with no failures/skips; the explicit spool and fuzz seed
regressions each passed one test. Workspace coverage was 13032/17567 lines and
1045/1387 functions; original-package coverage was 9459/13112 lines and 755/1016
functions. Both unchanged floors passed. Dependencies, 66 synthetic CLI commands,
33 schema checks and snapshot fallback also passed. Doctests ran zero tests.

The diagnostic itself failed. The capture, truth, original score and companion
manifest existed and were hashed; source/script/binary identity checks succeeded.
However, the reused `export_raw` helper rejects any database sidecar. The standard
demo had retained SQLite WAL/SHM files, so the new entrypoint stopped with
`database_sidecars_present` before joining raw/final records or retaining the
already generated clock report. Its empty score/clock fields mean **not analyzed**,
not zero observations. This was an evidence-export integration error, not proof
of a timestamp-conversion, provider-timing or attribution defect. The time
question remains D; file existence and hashes do not establish valid QPC data.

The repair stays in the new analyzer: use the same SQLite read-only `backup()`
approach already used by the view checker, creating a new private snapshot that
includes committed WAL data. Then apply the existing strict raw export to that
closed snapshot. Never delete source sidecars or use immutable mode on the live
WAL source. Verify source DB/WAL byte stability and retain snapshot/export hashes.
Unfinished/wrong sessions, existing destinations, changing inputs or backup failure
remain rejected. The product storage and frozen scorer are unchanged.
SQLite may initialize an empty WAL/SHM when opening a WAL-mode database for
reading. The analyzer records that transition explicitly; an existing nonempty
WAL and the database must remain byte-identical throughout the pinned read and
after closing it. Only the newly owned snapshot changes journal mode.

Before this repair, the new WAL regression failed with
`database_sidecars_present`, and the partial-report regression failed because
the final-view scope was absent. Both original local failures are retained under
`target/quality/etw-clock-*-regression-before.log`. Regressions cover a committed
row present only in WAL, a residual WAL without a writer, a WAL-mode database
without sidecars, unfinished/wrong sessions, changing input and existing output.

Independently readable original scores, truth and clock reports are now retained
before raw export. An existing clock report must match the original input hashes
and is explicitly a final-view observation; it cannot substitute for the missing
same-event raw/final comparison. Failures keep the complete-evidence flag false.

The next CI schedules exactly two independent verification steps, with new
`target/etw-clock-verification-1` and `-2` private directories and separate public
summaries. Their manifest phase/index identifies the fixed pair. They verify
this definite export-chain repair and clock acquisition; they do not presume
that the historical time failure has been repaired. Both results are retained,
including failed original scores. No additional diagnostic or retry loop is added.

## Fixed pair result: clock acquisition works; adjudication remains blocked

The two planned attempts ran once each at
`7a035e6b261051f60eb50da6a88e8a8fdd2485da`, branch
`fix/etw-clock-evidence`, in
[CI 41050244512](https://github.com/LuckyLivio/Contain/actions/runs/41050244512).
The complete CI failed: both ETW steps failed, with demo exit 1 and analyzer exit 2.
No retry or additional capture followed. The earlier failed diagnostic
41045468693 and the six historical failures remain unchanged.

| Observed result | Attempt 1 | Attempt 2 |
| --- | ---: | ---: |
| Independent fixture rows with actual QPC boundaries | 21/21 | 21/21 |
| Primary records with exact key + descriptor original-QPC links | 33/33 | 33/33 |
| Primary missing links / supported sources missing QPC | 0 / 0 | 0 / 0 |
| Primary and companion frequency (Hz) | 10000000 | 10000000 |
| Same machine / boot provenance verified | true | true |
| Companion stored records | 52265 | 7829 |
| Companion binary bytes | 3344960 | 501056 |
| Source DB/WAL byte stability through snapshot and close | true | true |
| Original scorer target success: observed and correct | 17/17 | 17/17 |
| Original scorer additional target positives / wrong attribution | 0 / 0 | 0 / 0 |
| Original scorer total observed / expected | 19/21 | 19/21 |
| Original scorer Unknown | 4 | 4 |
| Bound existing final-view clock report: unique candidates / no interval source | 19 / 2 | 19 / 2 |
| Complete raw/final evidence analysis | rejected | rejected |

The WAL repair succeeded in both attempts: source hashes, snapshot hashes and raw
export hashes were retained, and the original score recomputed exactly. Both
clocks were actually enabled and produced data. The real headers record primary
mode `268435712`, companion mode `268439808`, and clock type flag `1`. All 27
capture/provider/drop/drain checks passed. Queue, quota and writer-drop counters
were zero; both writers finished and drained without timeout. The companion's
final consumer-loss counters remain **unavailable** (the report records false),
and its manifest duplicate-key counter remains null. These are not zero-loss
claims for an unobserved counter; exact per-primary links were independently
checked. No offsets, tolerance, inverse conversion or reconstructed old QPC were
used.

The actual blocker is `supported_raw_event_without_unique_final_view`, followed
by `source_original_qpc_or_actor_missing`. With zero missing QPC, the latter must
not be described as failure to acquire the clock. The new analyzer currently
requires every supported raw record to have a final view, and any violation
blocks all interval comparisons. Its `clock_unavailable` label therefore also
means that the join was rejected; it does not erase the measured QPC values.

That blanket join requirement is not a general production contract:
`crates/contain-core/src/capture.rs` explicitly keeps unresolved non-High registry
records in committed raw evidence while omitting them from final `events`.
However, this run's public projection did not retain the missing-final event IDs
or their individual selection reasons. The 33-to-31/32 private/public event-count
difference is publication filtering, **not** the number missing final views.
The current artifacts cannot establish which specific records took that branch
or whether another defect was involved. No raw record was deleted or promoted
from fixture truth to make the check pass.

Both original scores have zero additional positives, so the six historical time
failures were not reproduced. The separately hash-bound final-view clock reports
each contain 19 unique interval candidates, whose measured QPC and FILETIME both
lie in the corresponding intervals, and two rows without interval sources. These
are final-view observations, not successful raw/final adjudication or proof of
call causality. The old scorer also retains one unobserved noise operation and
one unobserved failed operation per attempt. The public raw projection retains
8 and 9 actor-Unknown native Writes respectively. Each attempt also retains four
High records without an independent truth candidate: three controls excluded by
the existing scorer and one transient registry diagnostic record. They are not
newly matched application calls. The two missing-interval truth rows are the
unrelated `set_value` (row 2 in each attempt) and the failed delete (rows 13 and 9).
Clock instrumentation was enabled
for these attempts; their passing old scores cannot retroactively clear the
unclocked historical runs. **D remains the conclusion**: no current evidence
selects A (conversion/comparison), B (provider timing), or C (attribution) as the
cause of those historical failures. No scoring-protocol change was justified.

The demo also stops at the existing v04 clock readiness check. That check keeps
the general provider emission contract unverified and returns 2, independently
of whether QPC acquisition worked. Assertions after that call, including the
later ETW smoke and original-score assertions, were **not executed**. The stored
score values above are actual computed results, not claims that those assertions
ran. Neither readiness nor the failed ETW step was downgraded to a warning.

The minimum next action is confined to the analyzer: add a regression using the
existing production raw/final selection contract, retain each raw-only record's
selection status/reason before a global rejection, and distinguish a legitimate
raw-only Unknown from duplicate IDs or conflicting facts. Preserve all raw facts;
do not manufacture a final actor or infer one from truth. The current public
artifacts are not a replayable database and cannot recover omitted membership
details. Resolving those exact records would require retained private evidence
or a separately planned future capture. This round does neither and starts no
queue/cache/WPR/storage redesign.

### Checks and retained artifacts

All following results belong to code SHA `7a035e6`, on Windows Server 2025,
runner image `windows-2025-vs2026` version `20261021.278.1`. Tools: Rust/Cargo
1.99.1, nextest 0.9.149, llvm-cov 0.9.1, deny 0.20.2, Python 3.12.10,
PowerShell 7.6.7.

The unchanged CI ran `./scripts/quality.ps1 -Mode Check`, `Coverage`,
`CoverageOriginal`, `Dependencies`, and `Companion`, each successfully.
Nextest: 286 passed, 0 failed, 7 skipped, zero retries. Python: 21 scripts,
291 passed, 0 failed/errors/skipped, including the 19 clock-audit regressions.
The two PowerShell regression scripts passed; they do not emit structured
assertion counts. The fuzz seed regression and explicit companion spool
regression each passed one test (the spool invocation filtered 191 others).
Doctests executed zero tests. No new fuzz campaign or performance benchmark was
needed for the script-only repair; neither was run.

Workspace coverage: 13032/17567 lines (74.18455%), 1045/1387 functions (75.34247%).
Original packages: 9458/13112 lines (72.13240%), 755/1016 functions (74.31102%).
Both original 66.5% line / 70% function floors passed. Inline tests and shared
offline-package source instantiations remain in the unchanged denominators;
these are not production-only or independent-unique-test counts.
All 66 synthetic native CLI commands and 33 JSON schema checks passed, as did
the separate snapshot fallback. These do not replace the two failed ETW steps.

The exact capture commands were the two `ci-etw-clock.ps1 -Phase verification
-AttemptIndex 1|2` invocations in `.github/workflows/ci.yml`, each using a new
absolute `target/etw-clock-verification-1|2` private directory and separate
`target/etw-clock-public-1|2` publication directory. CI artifact
`etw-clock-evidence` contains both reviewed `summary.json` files; artifact
`windows-read-only-quality` contains exact tool/command/count/coverage evidence
and the three synthetic CLI examples. Their full traces, companion binaries,
databases, host paths and unrelated process identities were not uploaded.
The two published clock-summary SHA-256 values, in attempt order, are:

```text
bc9026b7f52a0d238c17b3fd57efdd60145330d3c0f08e9d3aebc040f49c0c62
5d43ff1a43262ad5922e77616bbeb5b1ee61d47e4f545ce360265e0c536d15aa
```

This report-only follow-up uses a skipped-CI commit to avoid exceeding the fixed
capture budget. It does not change code or replace the failed CI at `7a035e6`.
Version remains 0.3.1. Formal 32-run, scale and real-installer validation were not
started, and no stabilization or release acceptance is claimed.
