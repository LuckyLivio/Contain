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
