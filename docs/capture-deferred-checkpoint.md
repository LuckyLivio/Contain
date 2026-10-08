# Deferred checkpoint experiment (E0 / E1)

This is an opt-in candidate on top of the commit-window diagnostic, not a default
policy change. Both arms use D3 and the same release binary, fixture, frozen scorer,
queue, raw batch size, raw evidence quota, provider configuration and drain gate.

| Setting | E0 | E1 |
| --- | --- | --- |
| Live raw journal | WAL, synchronous NORMAL | WAL, synchronous NORMAL |
| Live automatic checkpoint | 4096 pages | disabled (0 pages) |
| After producer stop, queue drain and final raw batch | existing behavior | one explicit PASSIVE checkpoint |
| Before derived indexing | 4096 pages | restored to 4096 pages |
| Final completion transaction | synchronous FULL | synchronous FULL |

`CONTAIN_CHECKPOINT_VARIANT=E0` or `E1` enables the experiment.
`CONTAIN_WAL_BUDGET_BYTES` defaults to 536870912 (512 MiB) in either arm; it is
invalid without an explicit variant. Unset both variables for existing behavior.
The experiment workflow fixes `CONTAIN_HOTPATH_VARIANT=D3`; it does not change the
default D0 selection.

## Storage boundary and failures

Both arms sample physical WAL length and available disk space at initialization
and before/after each raw batch. The 512 MiB WAL threshold is separate from the
256 MiB serialized raw evidence quota. Reaching it stops further raw retention,
records an error, preserves committed evidence, and makes the session incomplete.
Sampling permits transaction-level overshoot; it is not a hard physical file cap.
The disk guard stops raw writes when available-to-caller space is at or below
64 MiB. This reserve is a sampled check, not a reservation or a guarantee against
other writers exhausting the disk. Postprocessing and other database writers are
outside the raw-retention budget.

Stream metadata records initial and peak sampled WAL bytes, peak growth,
overshoot, sample count and minimum available disk bytes. The external harness
also samples DB/WAL files every 20 ms through process close, so its peak covers
postprocessing as well and may differ. Neither measurement is a continuous peak.

PASSIVE runs exactly once. Its busy value, total log frames, checkpointed frames,
error and duration are retained. Success requires busy=0 and equal nonnegative
frame counts. A pinned reader can prevent completion even with busy=0. There is
no retry or escalation to FULL/TRUNCATE. Failed/partial checkpoint leaves a failed,
unpromoted session, without inventing lost-event counts. Automatic checkpoint is
restored before indexing; failed restoration stops postprocessing. A borrowing
guard also restores on early capture errors and Rust unwinding. Process termination
bypasses this guard, but connection-local settings do not persist into a reopened
connection.

Raw NORMAL commits are crash-readable before finish. These process-crash checks
do not prove power-loss durability: postponing the checkpoint extends the window
of unsynced NORMAL transactions. The final completion commit still uses FULL.
See SQLite's [WAL durability discussion](https://www.sqlite.org/wal.html) and
[PASSIVE checkpoint contract](https://www.sqlite.org/c3ref/wal_checkpoint_v2.html).

## Frozen validation order

1. Formatting, Clippy, Rust storage/recovery tests, unchanged scorer rejection
   tests, diagnostic tests, and experiment-gate/artifact tests.
2. Ten E1 small attempts: five ordinary and five with registry noise, all D3.
3. Three alternating E0/E1 1000-file pairs. All three E1 attempts must satisfy the
   existing exact 2500/2500 observation/attribution gate; all six must satisfy the
   experiment configuration/storage gate. Baseline quality failures remain data.
4. Only if steps 2 and 3 pass, three alternating E0/E1 10000-file pairs, retaining
   every failed attempt and the existing 95%/zero-loss/zero-false-positive gates.

Run through the existing dispatcher:

```powershell
gh workflow run pipeline.yml --ref codex/deferred-checkpoint -f query_phase=checkpoint
```

Reliability and speed are separate outcomes. Stress capture wall time includes ETW
stop, tail flush, checkpoint, derived indexing, final FULL commit and process/DB
close; export and scoring are excluded. Small capture wall also excludes build and
scoring; its separately recorded harness wall includes them. Timing stages overlap
and must not be added. `capture_receive_wall` and `descendant_quiet_drain` end after
ETW stop/drain, before tail flush/checkpoint; `post_raw_checkpoint` records the
explicit E1 operation. This diagnostic boundary is updated in both arms.

The public artifact contains fixture-scoped evidence, frozen scores, numeric
metadata, profiles and partial-attempt records. Full machine captures stay private.
Results and a go/no-go decision will be added after the fixed experiment completes.
