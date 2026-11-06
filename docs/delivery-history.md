# Delivery history

Use the [README](../README.md) for the current Windows download and quick start. This index lists each delivery, its verified scope, and its retained evidence.

| Milestone | State | Frozen source / evidence |
| --- | --- | --- |
| Bounded read-only CLI candidate | Closed | [Delivery](read-only-workflow.md), source `2ba98b6cbb99e2cf622ad45713fd8d38d9b76c15` |
| Desktop record viewer MVP | Closed | [Historical desktop evidence](examples/windows-desktop-viewer-41122708670.json), source `4b4d38764ed417e6754b5db798488227ec147e21` |
| Graphical installation recording MVP | Closed | [Delivery and all retained failures](graphical-install-record.md), source `15f214442d69baa0ebd91f5ad12edc5661281779` |
| Phase-one Beta delivery | Closed | [Historical work and evidence provenance](stage-one-beta.md) |
| Task progress and failure feedback, Beta 2 | Closed | [Changes, verified artifact and limitations](progress-beta2.md) |
| Personal record library and cold startup history, Beta 3 | Closed | [Changes, verified artifact and limitations](personal-records-beta3.md) |
| Saved-record overview, Beta 4 | Closed | [Overview and Windows verification](record-overview-beta4.md) |
| Search events in one session, Beta 5 | Closed | [Search and pagination verification](session-event-search-beta5.md) |
| Export all matching events, Beta 6 | Released | [Export and release verification](search-export-beta6.md) |

[Architecture](architecture.md), [JSON contract](json-contract.md),
[measured performance limits](performance.md) and [formal unexecuted stabilization
protocol](v04-validation.md) remain separate references. Passing a graphical
workflow does not close the formal accuracy/scale gates or establish ownership,
safe uninstall or rollback.

The [historical branch cleanup audit](examples/desktop-install-branch-cleanup.json) records the branches at the time of the graphical installation MVP.
