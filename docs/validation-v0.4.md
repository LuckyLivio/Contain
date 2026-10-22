# v0.4 development validation

Status: **development; v0.4.0 release gates are not satisfied**. Package version
remains 0.3.1. This report separates historical elevated ETW measurements, local
fallback measurements, synthetic storage experiments, and unexecuted installer
validation. No third-party installer or real uninstaller runs on the host.

## Baseline and environment

Baseline: `codex/deferred-checkpoint`,
`7c5a5b2b534d235f4db6c472c2c34a60e8be66ee`. On 2026-10-22 the working tree had
untracked `docs/preview.md`, `scripts/build-preview.py` and `ui/`; these are
pre-existing work and are excluded from this change's commits.

Local environment: Windows 11 Pro x64 build 26300, NTFS, 24 logical processors,
33,690,267,648 bytes physical RAM; ordinary non-elevated token. Rust/Cargo 1.98.1,
MSVC target, PowerShell 7.6.5, Python 3.12.7. No CONTAIN experiment overrides were
set. Cargo was installed but absent from the terminal PATH; commands below add
the existing installation to the process PATH. This is a tooling issue, not a
Contain failure. No toolchain or dependency upgrade was performed.

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo test --workspace --locked
cargo build --workspace --release --locked
./target/v04/baseline-bin/contain.exe doctor --json
python scripts/test-score.py
python scripts/test-commit-tail.py
python scripts/test-checkpoint-gate.py
python scripts/test-hotpath-artifact.py
python scripts/test-summarize-checkpoint.py
```

Original tests: 64 core + 1 fixture passed, 1 optional storage replay ignored;
release build passed. Python suites passed 7 / 9 / 6 / 3 / 14 tests respectively.
Baseline executables were preserved before production changes in
`target/v04/baseline-bin`; raw environment, hashes, commands and output are in
`target/v04/raw/baseline`. Public reproducible numeric metadata is in
[v04-baseline.json](examples/v04-baseline.json). Machine captures remain local
because they can contain unrelated service/task/process metadata.

`doctor` and an actual marked 100-file fixture capture both attempted ETW and
received access denied (`0x80070005`). Fallback completed, with zero source events,
49 file-state differences and no promoted file attribution. One service inventory
timed out at its existing 20-second bound; that warning and trial remain in the
raw result. These facts do not reproduce the elevated ETW throughput failures.
There is no verified disposable VM/checkpoint on this host; HypervisorPresent
alone is insufficient evidence of isolation or recoverability.

## Historical failures retained

| Experiment | 100 files | 1,000 files | 10,000 files | Classification |
| --- | --- | --- | --- | --- |
| Final v0.3.1 streaming comparison | 3/3 pass | 0/3 candidate vs 3/3 original baseline | 0/3 both | Queue stalls/loss; upstream ETW loss at large scale |
| Later ingest/query repair | small fixture passes | repaired candidate 3/3 pass | still fails | Throughput coverage restored at 1,000, not proof of end-to-end speedup |
| Deferred checkpoint E0/E1 | E1 small attempts 10/10 | E1 3/3; wall median 44.862 s vs E0 25.721 s | 0/3 both | Shorter live commit tails, remaining queue/ETW losses; E1 not promoted |

Sources: [performance](performance.md), [ingest repair](capture-ingest-regression.md),
[hotpath](capture-hotpath-queries.md), [commit tails](capture-commit-tail.md),
[checkpoint experiment](capture-deferred-checkpoint.md) and their complete JSON
trial archives. Results from different runners must not be combined into a speedup.
Default hotpath remains D0, and E1 remains explicitly opt-in.

## Audit findings and validation in progress

Eight new minimal regression cases fail against the baseline: duplicated thread
start reopens an ended interval; conflicting thread start overwrites its owner;
process stop preceding start is lost; conflicting later stops extend lifetime;
file and registry operation direction conflicts promote snapshot changes;
an executable-name suffix is mistaken for an exact inventory target; and an
inventory target match is labeled as an observed writer. Red-test output is
retained at `target/v04/raw/reliability/red-tests.txt`. These are correctness
failures, distinct from queue loss, evidence gaps, and environment restrictions.

Storage audit identified a quadratic evidence readback loop; measurements must
precede replacement. The live queue (8192), raw batch (256), evidence quota,
provider masks, drain limits, and frozen attribution gates are not relaxed.

Subsequent sections will record the fixes, measured comparisons, complete
regression results, installer preflight, and remaining release gates.
