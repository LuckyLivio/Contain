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
