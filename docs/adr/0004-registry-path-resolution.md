# ADR 0004: Registry object context with explicit unresolved resources

## Context
The v0.2 manifest Kernel-Registry provider started successfully but often omitted
absolute key names. Relative names and KeyObject pointers cannot be turned into
HKCU paths merely because the user selected an HKCU snapshot scope.

## Options
Assume selected hive; maintain manifest object context; switch to classic registry
KCB events; poll all process handles; implement a kernel registry callback driver.

## Decision
Enable create/open/close plus mutations. Cache only paths derived from absolute
`BaseName`/`RelativeName`, or a verified BaseObject context in the same process
lifetime. Key contexts use `(PID, creation time, KeyObject)` and event-time guards.
Close, delete and re-open invalidate prior context. Missing opens, missing absolute
bases or reused process instances remain unresolved. Only proven absolute native
hive paths are compared against the current-user SID scope.

Keep unresolved mutation records only for verified installer-family actors and
display `<unresolved registry object>`. Store a distinct `registry_object` raw
field. Actor and resource confidence remain independent. Other unresolved system
activity is discarded after lifetime resolution. Registry snapshots always start
Unknown and cannot inherit the fixture's desired ownership from timing.

## Consequences
The cache can recover child operations from a named open even when later mutation
records omit names. The context/reuse rules have unit coverage. Whether a given
Windows build supplies enough absolute bases is a live compatibility result,
not implied by provider activation. Missing-name counters remain visible even
when some scoped resources can be recovered.

## Limitations
Manifest KeyObject is not a classic KCB rundown stream. Open metadata is sometimes
missing; live process queries can fail before object mapping is built. No guessed
hive prefixes or remote-registry scans are used. Classic KCBCreate/KCBDelete/rundown
requires another system-logger configuration and privilege/compatibility study.
Handle enumeration/NtQueryKey after the event races close/reuse and is not a safe
substitute. A future driver is outside v0.3.

## Sources
- [Registry kernel event fields](https://learn.microsoft.com/en-us/windows/win32/etw/registry-typegroup1)
- [System provider differences](https://learn.microsoft.com/en-us/windows/win32/etw/system-providers)
- [SystemTraceProvider session](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-a-systemtraceprovider-session)
- Local manifest inspection: `Get-WinEvent -ListProvider Microsoft-Windows-Kernel-Registry`.
