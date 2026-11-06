# Beta 10 reliability validation plan (continuing PR 15)

Starting branch/PR HEAD is `fafac841690e1a1e26a4f090448cf3d40f57e311`.
Retain PR 15 and its history. Main is `bb65aba2badd0a7e304d0557789b0ead6855ab07`.
The previous diagnostic ZIP is source `3c2e06871d28acd199ba1813605afecc9074738d`,
SHA-256 `41a3e4e3e8dc0b75e245eb3306ed8e5c6294d04c5fc72d4cc9110d3512ef99e7`,
CI run 42216843544, artifact 13501998007. Beta 9 remains source
`8e68ccfe63389e1533235e253eb59329f15be898`, ZIP SHA-256
`dee47046f01c6a2c31c5bda3a3a3bcf3fc2a1603eb18b35464f68cde04ec7612`.

## Diagnosis before choosing a fix

Existing saved accounting identifies the application receive queue as the stage
of the 711 raw losses. The 9,249 candidate losses are separate context loss.
Existing artifacts lack receiver substage CPU/wall budgets and pre-suppression
identity/completion details, so neither the specific consumer stall nor the
original identity-chain state can be inferred from final `EventLoss` alone.

First build the existing algorithm with opt-in bounded diagnostics, without an
algorithmic fix. Run exactly two controlled captures in this predeclared order:
profile **off**, then profile **on**, using the same built binary and fixture,
12,000 noise files and 36,000 independently verified operations per capture.
No retries or replacement of failed samples. If owned-process termination is
unconfirmed, do not start the next capture. Keep the failure and mark it not run.

The profile records exclusive receiver stage wall time, own-thread CPU samples,
queue arrival/overflow deltas and bounded page-level counts. No per-event I/O or
payloads, paths or dynamic labels. Truncated samples and unavailable/coarse CPU
measurements are explicit. Profile-on can alter scheduling and is diagnostic only.
It cannot satisfy final ZIP acceptance. Classify lock wait, lifecycle work, cache
attachment, initial classification, rescans, handoff and idle/burst intervals
separately; do not add nested legacy timers or equate wall time with CPU time.

A test-only read-only tool replays the exact saved session's retained raw events
through the actual production LifetimeCache/attribution code. For each parent and
direct-child target, record one event ID, PID+creation time, parent instance,
request/completion evidence and final suppression. Truth validates the answer but
never seeds identity. Missing retained facts remain unavailable. Counterfactual
resolution under incomplete coverage is not a writer claim and never changes the
saved record or the loss suppression rule. Full traces/DBs stay local to Windows;
only bounded whitelisted numeric/identity evidence is published.

Select the minimal fix after examining these results. Prove the failing behavior
and the fix with controlled inputs before final live acceptance. Keep all queue,
byte, per-record, resource/request, journal and exit bounds, loss accounting and
Unknown/attribution rules unchanged.

### Diagnostic round D2, declared before execution

D1 (`00a82f6`, run 42222854581) retained both samples. Off/on receive loss was
2,575/4,093 and candidate loss 8,137/7,418. In the instrumented sample, 4,060 of
4,093 receive losses occurred during initial candidate classification; lock wait
and pending scans accounted for zero. All 7,418 candidate rejections hit the
2,048-record guard, with no identity available at rejection. This last category
includes completion records that rely on requests; it does not prove 7,418
missing thread identities. Both target identity chains and unique successful
completions survived; global loss correctly suppressed final promotion.

D1's public export omitted existing `pipeline.providers` and
`initial_identity_snapshot` fields. The private Windows DB is not available after
the job. D2 therefore repeats exactly one off/on pair, same load and bounds, with
the admission algorithm still unchanged. Export only these already-saved numeric
fields, plus fixed bounded new-pending/drop counts by file/completion/other.
Do not add identity collection or infer the old run's missing values. D2 is a
separate diagnostic attempt, not a replacement for D1 or acceptance. Retain both
failures. Review snapshot query budget, failures, observation boundaries and
process-provider overflow before choosing the fix.

### Candidate C1 / diagnostic round D3, declared before execution

D2 (`721df2d`, run 42225513650) retained off/on receive losses 4,661/6,326 and
candidate losses 7,121/6,358. The on case had no process-provider overflow. Its
candidate rejects were 6,357 file records and one completion, all hitting the
record guard. Snapshot enumeration completed in 167,701,000 ns with no budget
exhaustion, 1,371 resolved threads, 227 failed queries and eight threads born
after trace start. Do not extend or rebalance the snapshot budget.

Source inspection identified a distinct handoff omission: initialization drains
ETW lifecycle facts to the raw journal, before the live receiver exists. The live
cache was subsequently created empty and seeded only with API snapshots. The
already-consumed lifecycle facts were unavailable for live candidate selection,
although final replay recovered them. A thread born after trace start is correctly
excluded from the initial handle snapshot; its saved ETW start must instead reach
the live cache. D2 saved 10/17 initialization records, but lacks the rejected raw
records needed to claim this omission caused every candidate loss.

C1 moves the same bounded live lifetime cache across initialization and receiver
startup, ingesting lifecycle facts before journaling them. It does not add another
cache, queue or collector, change snapshot boundaries, or suppress loss. Existing
snapshot conflict checks remain required when snapshot evidence follows ETW facts.
Use controlled startup-order inputs to demonstrate old failure/new retention.

Controlled replay against actual source modules, same 10,240 unrelated writes:
the old snapshot-only live cache dropped 8,192 candidates and retained 2,048
pending; the moved initialization cache dropped zero, retained zero pending, and
correctly excluded all 10,240 unrelated writes. Both retained all four parent /
direct-child request-and-successful-completion records, misselected zero unrelated
writes and persisted 9/9 raw records. Final replay resolved the saved background
identity in both cases. This isolates evidence availability during live selection;
it is not real ETW throughput or a Windows acceptance claim.

Run D3's off/on pair once, with the same 36,000-operation load and bounds and an
explicit algorithm-change flag. Retain both outcomes, including failures. D3
provides stage diagnostics only. Separately run the immutable ZIP's existing
desktop gate followed by the complete six-case matrix below. These are separate
Windows runners; compare D3 with prior diagnostics only as supporting observations.
The same-runner six-case matrix is the acceptance comparison. Run same-source
Bench/Fuzz explicitly; no reruns to select a green outcome.

### C1 desktop failure / frozen-ZIP diagnostic D4, declared before execution

C1 push CI 42227112910 failed `packaged_executable_default_history` with a
`TimeoutException`, before `PackagedDesktop.ReadyAsync()` returned. Its later
independent seeded-history cold-restart case passed; that does not replace the
failed empty-history startup. The real GUI installation and six-case matrix were
not run, as planned. Preserve the rejected ZIP unchanged: source `b0d2510`,
artifact 13506927140, SHA-256
`a7ef3faed3c42aada77fe5619b732fe092b8787f89311047105ff6082b132656`.
The original safe artifact cannot distinguish the 40-second discovery wait,
40-second enabled wait and nested 20-second UIA call deadline. Its private stack
and first-launch image are unavailable. Do not infer the old timeout's cause.

D4 uses that exact original ZIP with a new test-only bounded readiness diagnostic.
Run the existing personal-records-only harness once: no ETW capture, no retry,
three actual viewer launches. Retain static phases, elapsed times, probe counts,
owned-process/window state and timeout category only. Do not upload stack traces,
window captions, UIA trees, databases or private paths. Keep every existing
assertion/deadline and the original product assembly bytes; record harness source
separately from frozen product source. Copy the original production DLL into the
new harness, build synthetic fixtures/identity replay from the frozen product
source, and verify the ZIP hash again after diagnosis.

If all owned read-only viewer/core processes are confirmed exited, run the same
frozen ZIP in the predeclared six-case matrix below, once, no retries. This supplies
the missing same-package capture comparison without relabeling the earlier desktop
failure. Stop before any capture if owned termination is unconfirmed. All six
captures remain diagnostic: D4's top-level `release_accepted` is always false.
Even a green capture matrix cannot clear the unexplained C1 desktop failure or
replace the complete final same-ZIP desktop gates. Select any further fix only
after the new readiness evidence; do not rerun C1 merely to choose a green result.
The 45-minute diagnostic job starts the at-most-30-minute matrix only when no
more than ten minutes have elapsed since plan creation, reserving time to retain
artifacts; otherwise record the matrix as not run without starting partial rounds.

## Final immutable ZIP acceptance

Freeze one candidate ZIP after the existing checks. Keep its exact source SHA and
ZIP SHA in the immutable run plan. Existing GUI startup/install/session association,
save/exit/cold restart and original event explanation gates remain required; only
start the matrix after those gates pass, to avoid leftover GUI captures.

Use one Windows runner and the exact same candidate fixture bytes for all six
captures. Fixed order (no retries):

1. r1/Beta9
2. r1/previous-3c
3. r1/candidate
4. r2/candidate
5. r2/previous-3c
6. r2/Beta9

The GUI adds one native capture, so the declared maximum is seven. Every capture
uses a new marked fixture/output directory. All previous attempts stay retained.
Each case uses 12,000 background files × write/rename/delete = 36,000 successful
truth calls; queue 4096, handoff 4096/16 MiB, candidates 2048/8 MiB, resource limit
4096, journal 64 MiB, settle 1500 ms, max drain 10000 ms and existing process
wait deadlines. Do not tune load or limits after seeing results.

Publish each case's actual callbacks/received/enqueued/dequeued, queue overflow/
disconnect/high-water/delay, candidate accounting, handoff/save loss, ETW/decode
loss, target event IDs/identity/completion/attribution, unrelated attribution,
truth/window/accounting and exit/cold-read results. Equal fixture call counts do
not mean identical system event streams. Compare each round separately.

Both candidate cases must pass the unchanged strict oracle: zero receive and
candidate loss, verified parent and direct-child writes, no unrelated attribution,
full independent workload and session save/cold readback. Both old versions may
fail their attribution oracle, but their complete workload, accounting, actual
provider startup, exit/quiescence and cold readback are required for comparability.
Any candidate failure rejects the whole matrix, even if another candidate passes.
Do not average failures into a green result or select a successful sample.

Only then make PR 15 ready, merge normally, confirm main CI and publish the
accepted original ZIP as v0.3.2-beta.10 prerelease with checksums/source/limits.
If still blocked, deliver a new diagnostic ZIP plus the new stage and identity
evidence and remaining failures. Keep the user's private Parallels root cause
separate until its saved count breakdown exists. No 0.4 work.

## Recorded D4 outcome (no release substitution)

D4 run 42231290678 used the original C1 ZIP unchanged and completed the declared
order exactly once. Both candidate captures passed the unchanged zero receive /
candidate loss, identity, completion, independent workload, unrelated attribution,
accounting and fresh-process readback gates. Beta9 receive losses were
69,520/34,540 and candidate losses 37,269/38,539; previous-3c losses were
5,770/7,617 and 6,675/5,812. All six handoff/save/ETW/decode loss counts were zero.
Personal-only checks passed 8/8; Ready samples 492/532/558 ms did not reproduce
the original C1 full-desktop timeout. Its cause remains unexplained. D4 is not
formal acceptance, release_accepted stays false, and PR 15 remains draft. See
[the current result](beta10-capture-reliability-result.md) for complete identities,
limits, download, target event IDs and retained attempts.
