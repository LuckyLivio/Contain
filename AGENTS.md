# Contain development

Contain records installation evidence on Windows. Preserve the distinction between
an observed mutation, a verified actor instance, state validation, and ownership.
Read `.agents/skills/contain-rust-quality/SKILL.md` when changing Rust core logic,
capture, attribution, persistence, JSON output, dependencies, or quality tooling.

## Required checks

- After core logic changes, run `./scripts/quality.ps1 -Mode Check`: rustfmt,
  Clippy with warnings denied, nextest with zero retries, doctests, and scorer /
  harness regressions. Add focused behavioral regressions appropriate to the change.
- For attribution, parsing, lifetime, correlation, or storage changes, also run
  `./scripts/quality.ps1 -Mode Coverage`; inspect affected functions as well as the
  total. Record uncovered branches and ignored tests; never reduce assertions or
  exclude changed production code to improve a percentage.
- For dependency changes, run `./scripts/quality.ps1 -Mode Dependencies` with the
  committed lockfiles. Review advisories, licenses, sources, and duplicate versions.
- For parser/state machine changes, build and smoke-run the relevant fuzz targets
  using `./scripts/quality.ps1 -Mode Fuzz`. Preserve any failure as a deterministic
  regression before fixing it. Report an unavailable prerequisite as unverified.
- For hot path changes, run `./scripts/quality.ps1 -Mode Bench` on an idle machine.
  Compare repeated release samples on the same machine/toolchain. Use the separate
  real ETW protocol in `docs/quality-benchmarks.md` for capture throughput claims.
- CLI/output/schema/storage changes additionally need the existing fixture and
  migration/view regressions. Keep command syntax, v2/v3 JSON contracts, exact
  decimal FILETIME strings, database migrations and read compatibility intact.

## Evidence and execution boundaries

PID alone does not identify a process. Use PID plus creation time and verified
event-time intervals. Reject ambiguous or conflicting identities/completions.
Late source loss must revoke state/attribution promotion consistently in every view.
Snapshots cannot manufacture a writer or validate events outside their coverage.

Synthetic tests, fuzz inputs and in-memory benchmarks do not validate TDH decoding
or live ETW. Snapshot fallback is a separate result and cannot satisfy a real ETW
gate. Real ETW acceptance requires provider startup, actual source records,
independent truth, loss/accounting checks and unchanged scorer thresholds.
Retain failed attempts. Do not elevate, change system services, delete user data,
publish releases, or dispatch remote tests without task authorization. Ordinary
tests use isolated model data or marked disposable fixtures.

Use an independent, descriptive development branch. Preserve unrelated changes.
Follow `CONTRIBUTING.md` for authorized reviewed synchronization and public-content
rules. Keep full traces, host inventories and private databases local. Report exact
source revisions, commands, versions, pass/fail/ignored counts, coverage scope,
performance denominators and remaining risks. A result from another branch is not
validation of the integrated revision. Install only missing tools; a rustup shim
on disk is not proof that its component is installed.
