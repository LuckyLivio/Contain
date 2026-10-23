# Quality measurement boundaries

Keep synthetic in-memory policy work, snapshot fallback and real ETW capture in
separate results. Existing historical measurements remain in
[performance.md](performance.md). Retain failed attempts and identify the exact
revision, compiler, configuration and working-tree state for every new run.

## Synthetic policy benchmark

```powershell
./scripts/quality.ps1 -Mode Bench
# Explicit repeated release sample, saved separately from the default output:
cargo bench -p contain-core --bench event_pipeline --locked -- --events 200000 --samples 7 --warmups 2 --output target/quality/policy-trial-1.json
```

The dependency-free harness checks actual policy code. Its cases count different
work items: one normalized event parsed with exact 64-bit timestamp preservation;
one lifecycle source event ingested; one request/completion source record processed
(two records produce one operation); or one registry context open/get/close cycle.
Compare only the same named case. Allocation, assertions and destruction are timed;
fixture generation is outside the measured region. Reported p95 is over sample
averages, not individual event tail latency. All seven samples are retained.

These measurements exclude ETW delivery, TDH decoding, queueing, SQLite, hashing,
whole-system traffic and installer slowdown. They cannot establish live throughput.
For a claimed policy optimization, retain at least three alternating baseline /
candidate pairs with identical compiler, dependencies and workload on an idle
machine. Explain material differences rather than discarding inconvenient samples.
No synthetic result satisfies the real capture acceptance gates.

Output stays under `target/quality` by default. It includes machine metadata and
working-tree status for local reproducibility; review and redact it before any
publication, following `CONTRIBUTING.md`.

## Real ETW diagnostic and acceptance

Use the existing [small diagnostic and fixed stabilization protocol](v04-validation.md).
The small diagnostic must pass its audit before exact acceptance inputs are frozen.
The established four-worker matrix retains 100, 1,000 and 10,000 file workloads:
three baseline/candidate pairs at the smaller and larger scale, and ten pairs at
1,000 files, totaling 32 attempts. The protocol and thresholds are unchanged by
the quality tooling integration. A separate snapshot run cannot replace any attempt.

Report observed and valid attribution coverage with independent denominators,
false attribution and Unknown counts, provider state, upstream ETW loss,
application drops, persistence failures, quotas and drain completion separately.
Keep raw-clock companion accounting separate from the primary capture pipeline.
Measure diagnostic-enabled and diagnostic-disabled capture overhead separately;
do not infer an optimization from lower retained event counts or additional loss.

Report wall time, CPU and applicable I/O, sampled process memory, database / WAL
sizes, and the scope of each measure. Unavailable values remain null. Provider
permission failure describes that attempt only; retain the previously executed
user-admin diagnostics and their actual failures. Full raw-clock files, system
traces, host inventories and personal databases remain local. Publish only reviewed
aggregates, source/build identity and reproducible commands.
