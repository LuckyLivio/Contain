# Local ETW clock interface patch

Base: crates.io `ferrisetw` 1.2.0, upstream commit
`48336cca02e7eddb3a1180a005155f43f05e91f0` (registry `.cargo_vcs_info.json`).
The original license is retained in `License.md`.

This local copy exposes the actual `OpenTraceW` output header and an explicit
raw-timestamp consumer option. The default consumer mode remains the upstream
FILETIME-converted mode. Raw timestamps must never be passed to FILETIME-based
application code without an explicitly documented separate projection.

The patch also provides an existing-session real-time subscription, checked
EVENT_KEY extraction (exactly one nonzero 8-byte key), complete event descriptor
getters, and removes callback contexts when OpenTrace fails before a consumer is
installed. The original callback timestamp is never adjusted. Native tests use
synthetic records and a nonexistent ETL file; successful live ETW collection is
not implied by those tests.

Callback contexts use monotonically assigned opaque tokens. Callback dispatch
clones its owning reference under the same lock used to unregister a consumer;
it never dereferences trace-owned memory after checking a separate validity set.
This closes a dispatch/CloseTrace lifetime race and prevents an old buffered
callback from attaching to a new consumer that reuses an allocation address.
Closing retains already acquired callbacks until they finish, while later calls
with retired tokens are ignored. Token exhaustion fails closed instead of wrapping.
The two registration maps contain only live consumers. Tests cover concurrent
dispatch/close, address reuse, exhaustion, the native thunk and failed OpenTrace.
Live timing and callback overhead remain subject to the separate ETW diagnostic.

Official interface contracts:
- https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew
- https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-trace_logfile_header
- https://learn.microsoft.com/en-us/windows/win32/etw/wnode-header
