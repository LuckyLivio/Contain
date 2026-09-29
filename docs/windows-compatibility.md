# Windows compatibility: observed results

No untested Windows version is marked supported by experiment. All runs use x64
debug binaries. An administrator account name does not itself prove elevation;
the benchmark records the actual token test.

| Environment | Process | File ETW | Registry | Services | Tasks |
| --- | --- | --- | --- | --- | --- |
| Local Windows 11, 10.0.26200.0, non-admin, NTFS | Owned handle and polling; short-lived actor may be missed | Access denied; automatic fallback verified | Scoped HKCU snapshot; Unknown actor | Read-only query succeeded | Read-only query succeeded |
| GitHub windows-latest, Windows Server 2025 build 26100, elevated stress token, NTFS | ETW lifecycle and polling; short-lived and detached fixture verified | Live scoped events and successful IRP completions verified | Some paths recovered through object context; remaining path gaps visible | Read-only inventory query succeeded | Read-only inventory query succeeded |

Evidence: [fixture CI](https://github.com/LuckyLivio/Contain/actions/runs/36542048919),
[local benchmark](examples/benchmark-win11-fallback-v3.json), and
[v0.3 verification](verification-v0.3.md). CI also runs explicit snapshot mode.
Services/tasks/startup entries are read-only probes; the fixture does not install
real services, tasks or startup registrations. These results are not write-event
attribution tests for those subsystems.

Not tested: Windows 10, Windows ARM64, ReFS/FAT/network shares, AppContainer/MSIX,
protected/elevated child brokers, real vendor installers, domain policy variants.
ETW permission failure is handled without requesting UAC or modifying policy.
