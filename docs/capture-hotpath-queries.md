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
