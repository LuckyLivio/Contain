# Capture hot-path query experiment

Starting branch: `fix/capture-ingest-regression`, clean local/remote
`b602628b67842dc3d403b9610496b757c3c94ea2`. Work is isolated on
`fix/capture-hotpath-queries`; main remains `bef6ac17cf3c816733e661fb76dc8974f5a30f12`.
The prior pressure-tested binary was `0d54a35c1ee64afe1b218009029545b7dbda913c`.
The subsequent changes add failed-group quality reporting, stronger drain checks,
artifact preservation and reports. They do not constitute another pressure run.

## Predeclared hypotheses and controls

H1: per-record registry callback process queries consume substantial callback time
and increase ETW loss. H2: process polling and descendant queries interrupt queue
draining and increase application queue overflow. Neither is established merely
by an earlier correlation. Preserve the previous 9357-event loss sample, queue high
water 37, 9.375 s callback / 6.121 s identity query time, and polling stalls in
[the prior report](capture-ingest-regression.md).

Keep D0 (original behavior with provider diagnostics), D1 (H1 only), D2 (H2 only)
independently runnable. Only combine D3 after the independent light trials provide
evidence. Freeze provider flags, raw queue, SQLite/WAL, batch and post-processing
transactions, quotas, original fixture/scorer and attribution thresholds.

Final small-test denominator is fixed **before running: ten attempts, five ordinary
and five with bounded registry noise**. Failed attempts are retained, never replaced.
Each noise attempt includes an independently launched pre-existing noise process,
a post-start noise process and the original short-lived target child. Each noise
process requests exactly 1000 safe registry mutations, with a 20-second deadline;
unfinished truth is a failed attempt. Original 1000-file acceptance remains three
alternating release --locked trials, four workers and 2500 successful target calls.
Run the original three-trial 10000/25000 gate only if preceding correctness,
small-test and 1000-file gates have no regression. A green CI alone proves none
of these reliability claims.

## Implementation boundaries before live trials

`CONTAIN_HOTPATH_VARIANT=D0|D1|D2|D3` selects independent paths in one binary;
the default remains D0 until evidence warrants changing it. D0 instrumentation
commit is `3802ca0`; D2 alone is also runnable from `fedc506` with the D2 setting.

D1 removes callback process API calls. The callback uses the existing bounded
LifetimeCache for registry object ownership; event identity is resolved again in
the existing post-drain lifetime pass. An initial supplemental snapshot runs after
ETW readiness, before installer launch: at most 4096 queries, checking a two-second
budget between calls. Individual OS calls cannot be interrupted; this is a query
budget, not a hard real-time deadline. Raw pages drain between queries. Queries
never hold the decoder lock. A snapshot identity is valid only at/after its own
query completed, and an ambiguous PID interval stays Unknown. Failed/skipped
queries, elapsed/query time and temporal bounds are reported, without exporting
the global process list.

The callback owns the lifetime/context caches and copies owned manifest fields.
The receiving thread remains the sole SQLite owner. No worker, borrowed ETW pointer,
new writer, enlarged raw queue or metadata channel is introduced. The additional
callback lifetime cache uses the existing 32768-process bound; registry paths keep
their 16384 bound and process-order watermarks are bounded at 32768. Unknown-owner
open/close records retain owned raw metadata instead of being mistaken for unrelated
events. This necessary evidence retention changes payload size/record counts and
must be reported separately from query timing. Historical JSON remains readable;
SQL write/index/transaction policy is unchanged.

D2 uses ETW lifecycle state for descendant drain while active; the owned installer
handle still supplies the root identity. Missing exits remain pending until the
existing maximum drain deadline. Snapshot-only/unavailable-ETW modes retain the
original polling behavior. Polling is removed, not moved to another thread. The
existing stop/consumer-join/final-queue-drain sequence is unchanged.

## Isolated light trials (before D3)

Actual binary: `1c7e98a836de0773d1fc3430714f99c8b02aa46f`,
[run 36586622200](https://github.com/LuckyLivio/Contain/actions/runs/36586622200).
All twelve planned attempts ran. D0: 4/4 target gates; D1: 3/4; D2: 4/4.
Every attempt had zero ETW event/buffer loss, queue overflow and false attribution.
D1 registry callback queries fell to zero from D0's 842–3771 per attempt;
registry callback totals were 5.28–35.93 ms versus 42.58–133.85 ms. This confirms
the callback cost mechanism, **not** that it caused or fixes the older 9357-event
loss: that loss did not recur in this isolated sample.

D2 removed both receiver polling and descendant identity queries (no worker).
D0 small attempts performed 166–171 polls, costing 0.906–1.078 s in total.
In the single diagnostic 1000-file pair, D0 had a 372.84 ms maximum poll and
1820 queue losses; D2 had no polls or queue losses and scored 2500/2500, compared
with D0's 2176 observed/0 correct after loss downgrade. This supports H2 for that
queue-overflow sample. D2 is not universally faster: total wall was 31.70 versus
15.29 s, with 16.75 versus 6.72 s in paged post-processing. The maximum no-drain
gap was still 949.06 ms in D2 versus 557.32 ms D0, consistent with remaining
storage waits. This single pair is not the three-trial acceptance.

The failed D1/noise/round 2 had 17 target operations observed, all downgraded,
with one **post-processing resource association cap** loss. The safe raw export
contains 663 events for `.fixture-go` (existing cap 512); provider cache evictions
were zero. Supplemental noise startup's 1 ms file polling amplified this control
resource. Preserve that failed run. Before the fixed final ten attempts, replace
only this supplemental startup polling with a bounded directory notification
wait, following [Microsoft's API contract](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstchangenotificationw).
The original fixture/scorer, registry mutation loop (1000 calls per process, no
inter-operation sleeps), deadlines and all product limits remain unchanged.
This harness correction is a separate variable; final noise results cannot be
credited solely to query removal. The ten final attempts have not yet run.

H1 and H2 each have independent mechanism evidence, so D3 is eligible for the
predeclared combined test. Keep D0 as default pending its results.

All isolated small results (file: observed/correct of 13; registry: observed/correct
of 4; pre/post: observed and confirmed-other of 1000 each):

| Variant | Round / environment | File | Registry | Pre / post noise | Callback identity queries / ms | Target gate |
|---|---|---|---|---|---|---|
| D0 | 1 ordinary | 13/13 | 4/4 | — | 849 / 39.363 | pass |
| D0 | 1 noise | 13/13 | 4/4 | 595 / 536 | 3771 / 108.583 | pass |
| D1 | 1 ordinary | 13/13 | 4/4 | — | 0 / 0 | pass |
| D1 | 1 noise | 13/13 | 4/4 | 1000 / 1000 | 0 / 0 | pass |
| D2 | 1 ordinary | 13/13 | 4/4 | — | 869 / 28.911 | pass |
| D2 | 1 noise | 13/13 | 4/4 | 513 / 236 | 4211 / 106.289 | pass |
| D2 | 2 ordinary | 13/13 | 4/4 | — | 2522 / 70.924 | pass |
| D2 | 2 noise | 13/13 | 4/4 | 556 / 348 | 3841 / 93.142 | pass |
| D1 | 2 ordinary | 13/13 | 4/4 | — | 0 / 0 | pass |
| D1 | 2 noise | 13/0 | 4/0 | 799 / 795 | 0 / 0 | FAIL, context cap |
| D0 | 2 ordinary | 13/13 | 4/4 | — | 842 / 37.477 | pass |
| D0 | 2 noise | 13/13 | 4/4 | 495 / 340 | 3032 / 87.622 | pass |

All twelve: original unrelated fixture 2/3 observed but unresolved, zero confirmed
exclusions; deliberately failed delete unobserved and never promoted; zero false
target attribution. Supplemental noise rows missing from the table's numerators
are unobserved, never credited as excluded. Query timings count calls, not just
successful lookups. Provider `callback` excludes separately reported `lock_wait`;
`context` and property timers nest, so do not sum them as disjoint phases.
`unresolved` in provider counters specifically means unresolved registry path;
final actor resolution is scored separately. Initial enumeration includes sysinfo
work and is timed separately from explicit native identity calls.

Full counters: [12 attempts](examples/hotpath-light.json),
[single 1000 pair](examples/hotpath-light-1000.json).
Raw safe fixture evidence, all scored truth rows and per-stage profiles:
artifact `11042541664` on the light run above (ZIP SHA256
`a5ba5f3603515a95ef5a9f535ec437077786b5b449308098404cee467455504a`).
Public exports are fixture-only projections made **after unmodified scoring**;
global machine inventory/registry/process rows are not published. The full raw
database is therefore not reproducible from that public projection.

## Candidate validation identities

The first combined series uses `bc7776577691d85e1904edcab3800273abe55f76`,
[run 36588824929](https://github.com/LuckyLivio/Contain/actions/runs/36588824929).
During this run, code review identified a separate correctness gap: callback
context ownership could precede a delayed process stop/start, and the later
lifetime pass could resolve the PID to a different birth. The next patch validates
the retained registry object generation against the final resolved process birth,
rejects conflicting associations, counts a registry path gap and retains the raw
record. It also captures the generation before close/delete invalidation.
A synthetic delayed-PID-reuse test covers rejection and the valid same-generation
case. This changes only the new H1 identity handoff, not SQL or attribution policy.

Before reading the combined series results, predeclare **one additional fixed
series** for that correctness patch: ten attempts (five ordinary/five noise), three
alternating 1000-file pairs, and 10000 only if prior gates pass. Keep the first
series' full ten-attempt denominator and every outcome separately; do not pool
successes or replace failures. No further automatic candidate search is planned.

### First combined series — failed small gate, not discarded

`bc77765` completed all ten small attempts: **0/10 passed**. There was no ETW,
queue, retention, persistence or context loss, no tail and no drain timeout.
The frozen scorer nevertheless rejected unmatched positive events in every attempt:

| Round / environment | File observed/correct / 13 | Registry observed/correct / 4 | Pre / post confirmed-other / 1000 | Unmatched positives |
|---|---|---|---|---|
| 1 ordinary | 12/12 | 0/0 | — | 5 |
| 1 noise | 12/12 | 0/0 | 2 / 3 | 5 |
| 2 ordinary | 13/13 | 3/3 | — | 1 |
| 2 noise | 12/12 | 0/0 | 1 / 2 | 5 |
| 3 ordinary | 12/12 | 0/0 | — | 5 |
| 3 noise | 12/12 | 0/0 | 0 / 0 | 5 |
| 4 ordinary | 12/12 | 0/0 | — | 5 |
| 4 noise | 12/12 | 4/4 | 129 / 43 | 1 |
| 5 ordinary | 12/12 | 0/0 | — | 5 |
| 5 noise | 12/12 | 0/0 | 1 / 1 | 5 |

Original noise in every attempt: 2/3 observed but unresolved, zero confirmed-other.
Failed delete: unobserved and never promoted. Matched-row wrong-actor count is zero,
but the **42 unmatched positives remain false positives under the frozen gate**.
Do not conflate those counts or call the series successful.

In round 1 ordinary, four registry records have matching PID/birth/path but ETW
timestamps 1.1/1.7/2.1/2.1 microseconds beyond their independent syscall interval;
the detached write is 185.9 microseconds beyond its interval. Those records remain
unmatched. This suggests an ETW/fixture timebase-boundary issue; it does not establish
its cause, invalidate the scorer, or justify changing tolerance. The later PID-context
correctness patch does not claim to fix this timing issue.

The same runner's three 1000-file pairs all passed (D0 and D3): 2500/2500,
zero relevant losses/false positives/tails/timeouts. Wall seconds D0/D3 were
23.261/32.054, 30.096/21.783, 21.888/55.723. D3 paged post-processing alone took
23.030/13.647/46.806 s; its parent memory was 25.42–25.91 MiB, final DB
89.90–90.10 MiB and sampled WAL peak 35.45–35.78 MiB. This preserves the file
intermediate capability in that series, not speed or registry stability.
10000 was skipped because the small gate failed; the workflow is red.

Full [small results](examples/hotpath-combined-1.json) and
[1000 results](examples/hotpath-combined-1-1000.json); artifact `11043910954`
on run 36588824929, ZIP SHA256
`bb22f61aed73cc6db8976d0e7fc880ac569c3131e5dbe8944770500c264da884`.
