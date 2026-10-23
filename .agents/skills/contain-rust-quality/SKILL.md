---
name: contain-rust-quality
description: Run and interpret Contain's Windows Rust quality gates after changes to capture, attribution, parsing, persistence, CLI contracts, dependencies, or quality tooling.
---

# Contain Rust quality

Start in the repository root. Inspect `git status`, `rustup show`, installed
components and `cargo install --list`. If Cargo is absent from PATH, inspect
`$env:USERPROFILE/.cargo/bin` before installing Rust. The quality script adds that
directory for its process and restores PATH afterward. Setup is explicit, adds
that existing directory once to user PATH for future terminals, and installs only
missing tools: `./scripts/quality.ps1 -Mode Setup`.

## Select checks by changed behavior

1. Read root `AGENTS.md`, then run `./scripts/quality.ps1 -Mode Check` after core
   changes. It fails immediately on any gate failure; nextest never retries a
   failure. Doctests run separately because nextest does not run them. The three
   explicitly ignored tests are disk-backed storage performance experiments;
   report their omission and run them when investigating readback/storage costs.
2. Run `-Mode Coverage` for core behavior changes. Read both the whole-workspace
   report and affected source regions in `target/quality/coverage-html`. Do not
   interpret instrumented unit tests as live Windows provider validation. Use
   the existing local floors (66.5% lines, 70% functions); raise them when sustained
   coverage improves. Stable coverage includes inline test code; do not call it
   production-only or branch coverage. Never
   lower assertions, ignore failures, or hide production files to meet it.
3. Run `-Mode Dependencies` for manifest/lockfile changes. Read `deny.toml` and
   inspect the local ferrisetw patch and duplicate Windows crate generations.
   Do not add blanket advisory/license exceptions. `libfuzzer-sys@0.4.13` has a
   scoped NCSA allowance for its additional permissive license. Keep the main and fuzz
   lockfiles distinct and committed.
4. For parser/lifetime/correlation changes, read `fuzz/README.md`, then run
   `-Mode Fuzz -FuzzSeconds 30`. Nightly and native MSVC ASan are separate from
   stable production builds. Use the real contain-core modules; no platform
   stubs or replacement algorithms. Keep crash inputs, minimize them and add
   deterministic regressions. A short campaign is a smoke test, not proof.
5. For performance changes read `docs/quality-benchmarks.md`, then run
   `-Mode Bench`. Synthetic events/s measures only named in-memory stages.
   Real ETW measurements use the existing release fixture and independent
   ground-truth scorer, require the actual provider and retain all losses.

## Regression selection

- Identity/lifetime: PID and TID reuse, start/stop delivery permutations, gaps,
  snapshot lower bounds, contradictory image/parent evidence.
- Correlation: duplicate completion even with equal status, reversed delivery,
  exact request generations, unresolved success, all raw references retained.
- Attribution: competing successful mutations and actor instances, equal event
  times, late source loss, Unknown preserved without adequate proof.
- Snapshot: unreadable baseline/final regions, path-component boundaries,
  hard-link identity ambiguity, post-stop reads and overwrite coverage.
- Persistence/output: historical migrations, atomic rollback/recovery, paged vs
  full views, stable CLI commands and v2/v3 JSON schema. Use existing scorer
  tests and disposable fixture scripts for end-to-end contract validation.

## Report evidence

Keep logs under `target/quality`. Record branch/revision, working-tree state,
Windows build, toolchain/tool versions, exact commands, pass/fail/ignored counts,
coverage scope, every measured sample and benchmark unit/denominator. Distinguish
`synthetic`, `snapshot_fallback`, and `real_etw` results. If the token cannot start
ETW, report that attempt unavailable; do not erase prior user-run real ETW results.
Fallback success never changes the result. Keep machine inventories and full logs
local; review aggregates against `CONTRIBUTING.md` before publication. Record the
exact integrated revision for new checks, separately from historical branch results.
Do not publish a version as part of a quality check. These are local gates; this
integration does not add or dispatch remote quality workflows.
