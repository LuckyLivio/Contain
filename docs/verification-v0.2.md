# v0.2 verification record

Date: 2026-09-29. Scope: safe fixture and documented Windows APIs; no real application installer or user startup/service mutation was used.

## Local Windows, ordinary token

Host build: 10.0.26200.0, x64. Rust stable/MSVC. `doctor --json` confirmed a queryable process identity and successful read-only inventories (360 services, 214 scheduled tasks, 22 Run entries on this host at test time). Counts are environment-specific; no inventory contents are committed.

ETW start returned Access Denied. A manual UUID-named `logman` probe and the implemented backend agreed. Contain did not request elevation. The fixture fallback completed with 3 tracked processes, 10 file state changes, 3 registry value changes and 17 timeline records. File/registry state ownership remained Unknown; the independently launched process was absent from the installer family. Dry-run hashes/registry data were unchanged, and fixture cleanup removed its marked root and matching key.

## Remote Windows, actual ETW

- Foundation baseline `a6ba9f1`: [successful original CI](https://github.com/LuckyLivio/Contain/actions/runs/36533091757).
- Initial v0.2 `796f25e`: [successful ETW and fallback CI](https://github.com/LuckyLivio/Contain/actions/runs/36536794866).
- Precision/migration checkpoint `e921c2c`: [successful CI with source diagnostics](https://github.com/LuckyLivio/Contain/actions/runs/36537468528).

The v0.2 runner was Windows Server 2025 build 10.0.26100, image windows-2025-vs2026. Both runs reported **9 High file source events** and **3 independent writer events remaining Unknown** for the fixture. The second run reported zero ETW losses, zero application drops and zero schema decode errors. The [unaltered sample event](examples/file-event-v2.json) is a real child-process write request with PID 4276, parent/installer PID 1720 and exact FILETIME birth evidence, extracted from that run's job log.

The captured file subset included new filesystem objects, writes and deletion of a transient file. Parent startup activity and a paired rename were not established by those logs. The parser supports rename path events but does not reconstruct both endpoints. Before/after state still detected the surviving rename result and old-path deletion.

Some later file writes had no queryable issuing thread. Consequently final cache/user-file state remained Unknown despite a valid High earlier application write. This is an intentional distinction between event attribution and final resource ownership, not evidence that every file changed by the fixture belongs exclusively to it.

## Registry result and fallback

The manifest registry provider enabled successfully but its records did not provide full hive/key names. The diagnostic run observed a requested fixture key as `\software\contain\demo\contain-demo-...` with no SID/hive base. This cannot safely be treated as the selected HKCU key. No scoped High registry event was claimed. v0.2 counts missing absolute names, discards unscopable records, warns and retains the scoped value-state snapshot as Unknown. It does not use an undocumented provider filter to force names.

## What the gates establish

`cargo fmt`, Clippy with warnings denied, unit tests, workspace build and both demo modes run in CI. Unit tests cover direct/child/grandchild identity policy, unrelated and detached writers, reused PIDs, image mismatches, missing birth, mixed writers, event loss, registry unknown completion, inventory attribution, queue overflow, full-width timestamp JSON, legacy migration, newer-schema refusal, multiple lifetimes per PID and transaction rollback. The demo validates CLI JSON against its schema on PowerShell 7 and confirms inspect/diff/history/doctor readback.

No privileged real service/task/startup changes, complete Windows version matrix, large-installer benchmark, reliable registry path reconstruction, IRP success correlation, or detached/short-lived process recovery has been verified. These are limitations, not passed tests.
