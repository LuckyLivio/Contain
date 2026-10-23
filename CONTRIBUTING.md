# Contributing

## Development synchronization

The owner authorizes ordinary reviewed fast-forward pushes for this project to
`LuckyLivio/Contain`, branch `fix/raw-clock-stabilization`. After each coherent stage:
run relevant checks, commit explicitly selected files, push that development
branch, and verify its remote SHA. Live validation need not be complete before
synchronizing development work; commit messages and reports must identify failed
or unexecuted validation. Report local HEAD, verified remote SHA, push outcome,
remaining unpublished commits, and test pass/fail/unexecuted status.

Use ordinary descriptive public branch names (for example `fix/`, `test/`,
`docs/` or `dev/`) and project-focused wording in public files and commit metadata.
No personal prefix is required. Do not add tool/vendor authorship branding.
Review every unpublished commit and historical blob, not just the final diff.
Keep complete source traces, raw clock sidecars, personal databases, DB/WAL/SHM,
credentials and private paths/host inventories local. Publish reviewed aggregate
results with explicit redaction, preserving failed samples and denominators.
Do not stage the entire working tree without file-by-file review.

Protect unrelated uncommitted work, including UI/preview and quality-tooling work.
Do not force push, rewrite published history, push directly to the default branch,
merge, tag or publish a release. Authentication failure or remote divergence is
a reported blocker, not permission to overwrite remote work. Archived local
pre-redaction refs are not publication sources. This authorization does not grant
elevation, deletion, external resources or additional remote test dispatches.

Issues and pull requests are welcome. Please first read [the architecture](docs/architecture.md), especially the attribution evidence model. Keep observed changes separate from claims of application ownership. Avoid privileged code and system cleanup in ordinary tests.

On Windows with the stable Rust toolchain and Visual Studio C++ build tools:

The local quality entrypoint is `./scripts/quality.ps1 -Mode Check`. Select
additional checks by changed behavior in `AGENTS.md` and the
[project quality instructions](.agents/skills/contain-rust-quality/SKILL.md).
Inspect existing tools before using the explicit `-Mode Setup` command, which
installs only missing prerequisites. Keep [synthetic benchmark results and real
ETW validation](docs/quality-benchmarks.md) separate. Run checks on the exact
integrated source revision; results from a different branch are historical only.

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
