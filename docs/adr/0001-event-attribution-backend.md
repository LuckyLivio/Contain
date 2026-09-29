# ADR 0001: scoped ETW evidence with snapshot fallback

Status: accepted for v0.2, 2026-09-29. Windows x64 only in the tested build.

## Problem

v0.1 has useful state differences but lacks file/registry writer identity. Time overlap and path similarity are insufficient, especially when an unrelated process writes inside the same root. PID reuse and delayed delivery also prevent using PID alone as an application identity.

## Candidates and experiments

| Candidate | Assessment |
| --- | --- |
| Directory notifications / snapshots | Reliable surviving-state comparison within readable scopes; no writer PID; retained fallback |
| Manifest Kernel-File / Kernel-Registry ETW via TDH | Actual user-mode source with PID/thread/timestamps; permission and path/completion gaps; selected with strict evidence rules |
| Classic kernel logger | Needs global system logger flags, lifecycle/rundown and broader controller handling; deferred |
| Direct Windows ETW/TDH FFI | More control but substantially larger unsafe surface; small native wrappers only |
| Minifilter / registry callback driver | Deployment, signing, privileged service and security burden exceed this scope; deferred |
| Procmon or audit-policy changes | External dependency or machine policy changes; unsuitable default backend |

Local Windows build 10.0.26200.0 exposed the manifest providers via `logman query providers` and `Get-WinEvent -ListProvider`:

- Kernel-File: `edd08927-9cc4-4e65-b970-c2560fb5c289`.
- Kernel-Registry: `70eb4f03-c1de-4f73-a051-33d13d5413bd`.
- Kernel-Process: `22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716` (investigated; not consumed in v0.2).

A unique-session `logman create trace ... -ets` probe returned Access Denied under the ordinary local token. The implemented `doctor` returned unavailable and capture successfully continued without elevation. The GitHub Windows Server 2025 runner successfully enabled tracing and captured live file writer evidence. See the [verification record](../verification-v0.2.md).

## Chosen solution

Use `ferrisetw` 1.2.0 for a UUID-named user trace and TDH schema parsing, with `windows-sys` wrappers for read-only identity queries, NT device mapping and stop/statistics. Its [source](https://github.com/n4r1b/ferrisetw) and [API](https://docs.rs/ferrisetw/latest/ferrisetw/) provide the necessary Windows APIs, but the project describes work in progress; this is a bounded adoption, not a claim that every provider/version is stable. Cargo.lock pins the dependency set and CI exercises actual Windows events.

Keep provider decoding behind `EventSource`; deterministic policy is a separate module. Persist only scoped source records, state differences and evidence. Batch the final session into SQLite rather than writing synchronously from callbacks.

File provider keyword mask `0x1eb0` includes create/open, close, write, delete/rename path and new-file events. Event 12 maps the file object; it is not labelled a new file. Event 14 expires the map entry. Events 30/16/26/27 produce create/write/delete/rename observations. The map is bounded and cleared for object reuse/new opens; rename invalidates its mapping. Native absolute path conversion preserves scope boundaries. Unresolvable object paths are outside the supported capture coverage.

The v1 file schema's IssuingThreadId is resolved through a live thread and process query, checking both creation times against the event timestamp. A kernel worker's header PID is not substituted. Older schemas or exited threads can remain Unknown. Process instances require PID, FILETIME birth and image; process table parent IDs are checked against live parent instances. Kernel Process start/stop plus ProcessStartKey correlation is the preferred next step for short-lived processes. StartKey metadata is requested but not yet consumed.

Registry keywords `0x5300` request create/delete key and set/delete value. The parser supports absolute BaseName/RelativeName or KeyName and status, but **the tested runner supplied empty/relative names**. Guessing the hive or prefixing HKCU would be unsound. Those records are counted as `registry_path_gaps`, discarded, and scoped snapshots remain Unknown. No undocumented provider filter option is enabled to force names. The decoder remains useful on a provider configuration that supplies full names, but v0.2 does not claim live High registry attribution was verified.

## Privileges and lifecycle

[StartTrace](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracea) documents trace-control permissions; provider ACLs can further restrict access. Administrative or appropriate tracing rights may be needed. Contain never triggers UAC and provides `--no-etw`. Services/tasks/Run use read access and report denied or incomplete queries.

Only our randomly named trace is stopped. A start failure cleans that name even when the library failed after creating the session. Normal shutdown stops the producer, joins the consumer to drain remaining buffers, then closes the library trace. Drop handles error paths. No persistent ETL or background service is installed. Forced process termination may leave the session alive; `logman query -ets` can identify an exact `Contain-<UUID>` session for manual cleanup.

## Reliability and cost

[ProcessTrace](https://learn.microsoft.com/en-us/windows/win32/api/evntcons/nf-evntcons-processtrace) converts timestamps to system time without RAW_TIMESTAMP; the library uses that mode. JSON exports FILETIME as strings. [Trace properties](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties) provide loss counters, not a guarantee that every operation generated a supported event.

Bounded ETW buffers, application queue, object map and retained event list record capacity drops. Decoding errors and missing registry names are separate. State promotion requires loss statistics of zero and no relevant application/decode gaps. Even then, raw requests, final state and exclusive ownership remain different claims. Deferred system cache writes can carry no queryable writer and keep final file state Unknown despite a valid High application write earlier.

Microsoft's [file read/write schema](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite) illustrates object/key correlation; [OpEnd](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend) supplies completion for classic events. v0.2 does not implement IRP completion pairing or both rename endpoints and labels requests accordingly. Manifest templates were inspected on the actual host rather than assuming the classic schema was identical.

ETW provider traffic is system-wide before consumer scope filtering. Live identity queries in callbacks and file hashing have measurable cost; a full large-installer performance benchmark is still pending. Scope filtering, bounded storage and one transaction limit amplification but do not prove low overhead.

## Future migration

Add process/thread lifecycle caching with precise unique keys; implement supported completion/path correlations with loss-aware lifetime expiry; validate registry names through documented sources or use a separate well-scoped backend. Add independent Windows/version tests before expanding confidence rules. A minifilter can later emit the same evidence interface if user-mode evidence leaves a justified gap. No AI heuristic or deletion authority is introduced by this decision.
