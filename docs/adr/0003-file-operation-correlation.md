# ADR 0003: File requests, completion and conservative rename endpoints

## Context
A write request does not prove the resulting bytes. A delete request can fail.
Paths change, object pointers and IRPs are reused, and a single rename record does
not document both source and destination on all supported schemas.

## Options
Treat each request as success; join by path/time; track scoped FileObject lifetimes
and IRP completion; infer rename from equal hashes; use stable file IDs at snapshots.

## Decision
Keep source observations and normalized operations separate. Retain event ID,
header PID, issuing TID, FileObject, FileKey, IRP, object generation and NTSTATUS.
FileObject maps establish a new generation on open/new-file and invalidate on
close/rename; FileKey is retained without assuming it equals FileObject.
The object-generation ID groups observed operations on an open context, not a
permanent physical-file identity.

Track scoped mutation IRPs until OpEnd. Duplicate pending IRPs invalidate the
mapping and count a decode ambiguity. Reversed time or completion later than 30 s
stays unresolved. Nonnegative NTSTATUS is success except pending/reparse statuses;
negative status is failure. Raw request and completion IDs remain linked. A failed
delete normalizes to `Failed`, never `Deleted`. No completion means a request label.

For persistent endpoint rename support, obtain volume serial + file index +
creation time from the same opened handle used for hashing. Reject multiple hard
links and ambiguous ID multiplicity. An identity found once before and once after
at different paths yields `Renamed`; a changed identity at the same path yields
`Replaced`. Snapshot operations remain application Unknown. Intermediate names,
backup/source chains and actor ownership are never guessed from hashes or names.

## Consequences
`file.tmp → file.new → app.dll` can retain raw rename requests and prove surviving
endpoints when a baseline identity exists. Full multi-step replacement causality
is not claimed. File-state promotion considers mutating observations only; reads
and open/close records cannot establish ownership of a changed file.

## Limitations
Lost opens, completion reuse across unobserved operations, async I/O and file-ID
reuse limit correlation. Completion confirms the request result, not durable
storage or exclusive ownership. Data lost before provider emission is invisible.
Transient files absent from both snapshots depend entirely on source events.
Hard links, unsupported filesystems and rename-out-of-scope reduce coverage.

## Sources
- [FileIo OpEnd](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend)
- [NTSTATUS semantics](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/using-ntstatus-values)
- [FileIo information](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-info)
- [File information and identity](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle)
