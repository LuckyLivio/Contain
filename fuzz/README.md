# Contain policy fuzzing

This independent workspace links the **actual Windows `contain-core` crate** and
the same vendored ferrisetw patch. It does not substitute fake Windows APIs or
copied state machines. The production workspace, CLI, database and JSON contracts
are unchanged. The separate lockfile pins fuzz-only dependencies; include it in
dependency review separately from the main workspace.

Shared dependencies must match production versions: `python scripts/check-fuzz-lock.py`
enforces this in the local dependency and fuzz checks. When intentionally updating the
production graph, first preserve the old fuzz lock, seed `fuzz/Cargo.lock` from
`Cargo.lock`, then run `cargo metadata --manifest-path fuzz/Cargo.toml --offline`
once without `--locked` to add the fuzz-only dependencies. Inspect the diff and
restore locked checks. Do not use `cargo update` to refresh the entire fuzz graph.
This workflow needs Python 3.11+ (`tomllib`) as provided by current Windows CI.

| Target | Input and bound | Semantic oracle |
| --- | --- | --- |
| `event_json` | Arbitrary JSON, at most 64 KiB; generated exact `u64` ticks on every input | Accepted events roundtrip without losing any retained field; FILETIME string output and early numeric input preserve all 64 bits. Invalid JSON/ticks may be rejected. |
| `lifetime_sequences` | At most 256 eight-byte instructions | Independent interval model checks unique PID+birth membership, overlap refusal, contradictory image refusal, stop-before-start retention, earliest duplicate stop, snapshot lower boundary, and later start widening. Both `resolve` and registry actor resolution are checked at event/birth boundaries. |
| `completion_correlation` | One operation byte plus at most 32 completion bytes | Every matching raw reference survives; multiple completions remain ambiguous even when all agree; unrelated completions do not join; failure never becomes deletion; request-first and reversed input agree. |

For lifetime instructions: byte 0 modulo 4 selects start/stop/snapshot/query;
byte 1 modulo 4 selects PID 1–4; bytes 2–3 are little-endian birth ticks;
bytes 4–5 are event/observation/query ticks; byte 6 modulo 3 selects an image;
byte 7 is reserved. These are normalized model events, **not raw ETW records**.
The independent interval oracle deliberately excludes ancestry attachment,
thread ownership, quota exhaustion and native process-handle lookup; cover those
with the Windows regression suite and real ETW fixture.

For completion inputs: byte 0 modulo 3 selects write/delete/rename; subsequent
bytes modulo 3 encode unknown/success/failure; bit 2 sets an unrelated request.
Tracked IDs are unique so duplicated completions retain distinct raw records.
Reviewed seeds cover exact `u64::MAX`, invalid negative ticks, PID reuse, delayed
stops, conflicting completions and repeated identical successes.

## Native Windows prerequisites

The [official Windows setup guide](https://rust-fuzz.github.io/book/cargo-fuzz/windows/setup.html)
documents MSVC AddressSanitizer support. Some cargo-fuzz README versions still
say Unix only; use the actual installed version's build/run result to establish
support. Required: x64 MSVC tools, C++ AddressSanitizer, a nightly Rust toolchain,
and cargo-fuzz. Stable remains the project's default. First inspect; install only
what is missing:

```powershell
$env:Path = "$env:USERPROFILE\.cargo\bin;$env:Path"
rustup toolchain list
cargo fuzz --version
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsInstallation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.ASAN -property installationPath
if (-not $vsInstallation) { throw 'MSVC x64 tools and C++ AddressSanitizer are required' }
& (Join-Path $vsInstallation 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64
# Only if absent: rustup toolchain install nightly --profile minimal
# Only if absent: cargo install cargo-fuzz --locked
```

The developer shell must make the x64 `cl.exe`, linker and
`clang_rt.asan_dynamic-x86_64.dll` available. ASan compiler/runtime mismatches are
tooling failures; do not disable sanitizers to report a passing ASan campaign.
Do not run Linux targets against the full Windows crate in WSL. A future portable
policy crate should be an explicit production refactor with equivalent Windows
regressions, rather than a parallel test-only implementation.

## Build, smoke campaign and reproduce

From the repository root, in the developer shell:

```powershell
cargo +nightly metadata --manifest-path fuzz/Cargo.toml --locked --format-version 1 > $null
if ($LASTEXITCODE -ne 0) { throw 'Locked fuzz metadata failed' }
$fuzzLockHash = (Get-FileHash fuzz/Cargo.lock).Hash
cargo +nightly fuzz build
if ($LASTEXITCODE -ne 0) { throw 'Fuzz build failed' }
if ((Get-FileHash fuzz/Cargo.lock).Hash -ne $fuzzLockHash) { throw 'Fuzz lock changed' }
cargo +nightly fuzz run event_json -- -max_total_time=30 -max_len=65536 -timeout=5 -rss_limit_mb=2048
cargo +nightly fuzz run lifetime_sequences -- -max_total_time=30 -max_len=2048 -timeout=5 -rss_limit_mb=2048
cargo +nightly fuzz run completion_correlation -- -max_total_time=30 -max_len=33 -timeout=5 -rss_limit_mb=2048
# Build-independent oracle replay, also usable on stable:
cargo test --manifest-path fuzz/Cargo.toml --lib --locked
# Use the emitted artifact path verbatim after a failure:
cargo +nightly fuzz run lifetime_sequences fuzz/artifacts/lifetime_sequences/<artifact>
cargo +nightly fuzz tmin lifetime_sequences fuzz/artifacts/lifetime_sequences/<artifact>
```

Use `cargo fmt --manifest-path fuzz/Cargo.toml --all -- --check` and
`cargo clippy --manifest-path fuzz/Cargo.toml --lib --locked -- -D warnings`
for this workspace too. Longer local campaigns can replace 30 seconds with a
bounded agreed budget. Preserve failing inputs, minimize them, add a deterministic
production regression, fix the implementation, then rerun the exact artifact and
campaign. Do not weaken the oracle or delete an unexplained failure.

Record toolchain, cargo-fuzz version, target, command, sanitizer, duration, run
count, outcome and corpus/artifact paths. A deterministic seed replay is not a
coverage-guided campaign. A bounded campaign is not an exhaustive proof.
Fuzzing these modules does not establish kernel delivery, TDH schema correctness,
trace continuity, Windows driver behavior, installed-resource ownership or live
ETW throughput. No fuzz run qualifies as real ETW validation.
