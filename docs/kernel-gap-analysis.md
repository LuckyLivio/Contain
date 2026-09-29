# User-mode evidence gaps and future backend choices

v0.3 uses documented ETW, process/file handles and read-only inventories. It does
not implement a kernel driver or alter permissions.

| Gap | Current behavior | Research path |
| --- | --- | --- |
| Missing registry absolute base/open metadata | Context only from verified bases; unresolved object + independent actor confidence | Separate classic KCB rundown/system-logger experiment; explicit privilege matrix |
| Missing file names, object reuse or operation ends | Raw request retained; no invented completion or full rename chain | Broader manifest versions, bounded ordered object-context replay |
| Broker/service-manager action on behalf of an app | No ancestry-based automatic ownership | Explicit broker protocol evidence and domain-specific correlation |
| Event loss or consumer overflow | Visible counts, Incomplete quality, conservative promotion | Backpressure-independent ingestion and lower-cost decode |
| Quiet-period expiry before future helper starts | Later action is outside session | Explicitly longer drain or a separately designed observation mode |

A registry callback driver or filesystem minifilter might provide stronger
pre/post-operation context, but would add signing, installation, OS compatibility,
crash and security responsibilities. It would still need a definition of actor
ownership and a measured loss model. No driver is justified merely to produce more
High labels. First validate classic user-mode alternatives across real environments.
