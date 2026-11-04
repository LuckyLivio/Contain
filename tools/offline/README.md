# Offline read-only workflow checks

This workspace package executes the production model, reliability, SQLite storage
and CLI rendering modules through Rust `#[path]` imports. It does not maintain a
second implementation or supply fake Win32 providers. Windows path spelling and
serialized handoff counters are shared production modules; the capture API keeps
its existing re-exports. The package has no installer or deletion executor.

On Linux x86_64, from the repository root with the existing Rust toolchain active:

```bash
cargo test -p contain-offline --locked --offline --target x86_64-unknown-linux-gnu
cargo clippy -p contain-offline --all-targets --locked --offline --target x86_64-unknown-linux-gnu -- -D warnings
```

The explicit host target matters when a shell defaults to Windows cross-compilation.
Use `cargo fetch --locked` first only if the locked dependencies are missing.
The root lockfile is shared; no dependency versions are upgraded for this harness.
The Windows production workspace and its existing quality gates remain required.

The suite includes the real migration, JSON precision, legacy name provenance,
full/summary/page/explain agreement, transaction rollback, interrupted-session and
SQLite child-process crash-recovery tests. A zero-doctest result is reported
separately and is not functional validation. Tests added to imported production
modules also run here, so report counts from the actual run rather than a fixed
historical number.

Five opt-in Windows checkpoint experiment tests are explicitly ignored on other
platforms because they require the quota-aware `GetDiskFreeSpaceExW` API. Their
Windows definitions remain active. Attempting that API in the offline package
returns an unsupported-platform error; it never fabricates available disk bytes
or silently disables a reserve. Default storage and crash-recovery tests still
execute. The three existing disk performance experiments remain explicitly
ignored unless individually selected. Capture-worker helpers remain compiled;
only their unused-code lint is allowed on the two imported harness modules where
the Windows capture callers are absent. Other warnings remain denied.

Synthetic workflow examples and offline benchmarks must be labelled `synthetic`.
They exercise production SQLite and rendering logic, not TDH decoding, real ETW,
Windows current-path identity, live throughput or the formal acceptance matrix.
No private database, source event trace, raw clock sidecar or host inventory belongs
in this package or its published results.
