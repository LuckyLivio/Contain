# Contributing

Issues and pull requests are welcome. Please first read [the architecture](docs/architecture.md), especially the attribution evidence model. Keep observed changes separate from claims of application ownership. Avoid privileged code and system cleanup in ordinary tests.

On Windows with the stable Rust toolchain and Visual Studio C++ build tools:

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo build --workspace
./scripts/demo.ps1
./scripts/demo.ps1 -SnapshotOnly
```

Windows CI also runs `./scripts/demo.ps1 -RequireEtw` under its existing runner permissions. This requires a real High file event and an observed independent writer remaining Unknown. Ordinary local users can validate the denied-permission fallback without UAC. PowerShell 7 validates outputs against `docs/schema/cli-v2.schema.json` using `Test-Json`.

Service/task/startup mutations use isolated model tests. Real service lifecycle tests are manual/admin-only on a disposable Windows VM: record a baseline, create a unique test service targeting a disposable fixture binary, capture its configuration diff, then remove exactly that service and restore the VM. No such system mutation is part of the automated fixture.

Use `RUST_LOG=contain_core=info` for JSON diagnostics on stderr. Do not attach an unredacted user database to an issue. See [verification notes](docs/verification-v0.2.md) and [backend decision](docs/adr/0001-event-attribution-backend.md).

Add a focused test for behavior that changes safety, attribution, persistence, or CLI output. Update README limitations when support boundaries change. Report security issues privately as described in [SECURITY.md](SECURITY.md).
