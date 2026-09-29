# Capture counter definitions (v0.3.1)

All counts below are cumulative over one Contain-owned session, read after STOP,
ProcessTrace completion and application drain. Timers are monotonic elapsed
nanoseconds. Repeated statistics queries **replace**, not add to, Windows totals.
An absent `backend.pipeline` means ETW counters were unavailable/disabled.

| Field | Unit / scope |
| --- | --- |
| callback_records | provider callbacks received, before event ID selection; excludes private readiness provider |
| unsupported_records | callbacks whose IDs the decoder intentionally does not support; subset of deliberately_filtered |
| decode_attempted | selected callbacks entering schema/property/context handling |
| decode_succeeded / decode_failed | selected callback returned without / with reported decode/context error; failed callback may still emit raw request evidence |
| deliberately_filtered | no output due to unsupported ID, outside scope, context-only event or unmatched completion; excludes failed-without-output |
| failed_without_output | failed callbacks that emitted no decoded observation |
| events_received (legacy backend) | decoded observation admission attempts, not total callbacks |
| enqueued / queue_overflow / enqueue_disconnected | admission success / full queue rejection / disconnected consumer rejection, records |
| dequeued / queue_pending | records taken from queue / still queued or in enqueue reservation |
| queue_high_water | conservative occupancy high-water from successful enqueue reservation, bounded at 8192; concurrent dequeue can make this an upper estimate |
| queue_max_delay_ns / elapsed_ns.queue_delay | max / sum from enqueue timestamp to dequeue; no source-to-callback latency claim |
| context_evictions | context insertion refusals or association quota losses, context entries; **not** missed syscall count |
| retention_dropped | raw records not retained because of the byte/record-size quota |
| persistence_succeeded / persistence_failed | raw records committed / unavailable after a write failure; null when not measured (instrumented baseline f8037e9 originally emitted placeholder zero; comparison treats that as unavailable) |
| stream.accepted / persisted / failed / quota_dropped | records submitted to journal / committed / unavailable after write failure / quota-refused |
| stream.committed_bytes / quota_bytes | UTF-8 JSON payload bytes committed / allowed; not whole database bytes |
| stream.batches / max_batch_bytes | committed raw transactions / maximum attempted serialized batch size |
| etw_events_lost | Windows EventsLost, global enabled-provider **events**, null without successful stop stats |
| etw_realtime_buffers_lost | Windows RealTimeBuffersLost, **buffers**, null without stop stats |
| etw_log_buffers_lost | null / not applicable: no ETL file logger |
| etw_allocated_buffers / etw_buffer_size_kib | Windows NumberOfBuffers and BufferSize at stop; multiply with 1024 for nominal allocated buffer bytes |

Final balances tested by unit tests and the benchmark (candidate):

```
callback_records = unsupported_records + decode_attempted
decode_attempted = decode_succeeded + decode_failed
callback_records = deliberately_filtered + events_received + failed_without_output
events_received = enqueued + queue_overflow + enqueue_disconnected
enqueued = dequeued + queue_pending; queue_pending = 0 after final drain
dequeued = stream.accepted
stream.accepted = stream.persisted + stream.failed + stream.quota_dropped
```

v0.3.1 `backend.dropped_events` adds only raw record losses (enqueue rejection,
quota and persistence). `backend.context_losses` is separate, in context entries;
both independently suppress promotion. Historic v0.3 and early diagnostic revisions
mixed these units in `dropped_events`, so those fields cannot be retroactively
interpreted as raw-event accounting. `events_retained` counts
the final scoped timeline including synthesized state/process observations. Later
lifecycle scope filtering and synthesis mean it is not received minus dropped.
Unknown counters count observations, not independent fixture operations.

Timing fields distinguish before snapshot/inventory, provider readiness, root
installer lifetime, descendant/quiet drain, ETW stop and drain, after hash/snapshot,
paged lifetime/attribution/persistence, state correlation, after inventory and final
commit. Root installer time excludes initial inventory; descendant drain includes
the nested ETW stop duration. Raw-persistence time overlaps root/drain. Callback
categories can overlap (lifecycle construction includes enqueue); mutex wait is
measured separately. Never sum all of these as end-to-end elapsed time. The benchmark
measures separate external CLI start-through-exit wall time, including the final
commit, and does not include export/scoring.
