# Contributing

Issues and pull requests are welcome. Please first read [the architecture](docs/architecture.md), especially the attribution evidence model. Keep observed changes separate from claims of application ownership. Avoid privileged code and system cleanup in ordinary tests.

On Windows with the stable Rust toolchain and Visual Studio C++ build tools:

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo build --workspace
./scripts/demo.ps1
```

Add a focused test for behavior that changes safety, attribution, persistence, or CLI output. Update README limitations when support boundaries change. Report security issues privately as described in [SECURITY.md](SECURITY.md).
