# Delivery history

The current download entry is the phase-one Beta in the [README](../README.md).
This index preserves earlier bounded deliveries and their failures; it does not
turn historical packages into current Beta recommendations.

| Milestone | State | Frozen source / evidence |
| --- | --- | --- |
| Bounded read-only CLI candidate | Closed | [Delivery](read-only-workflow.md), source `2ba98b6cbb99e2cf622ad45713fd8d38d9b76c15` |
| Desktop record viewer MVP | Closed | [Historical desktop evidence](examples/windows-desktop-viewer-41122708670.json), source `4b4d38764ed417e6754b5db798488227ec147e21` |
| Graphical installation recording MVP | Closed | [Delivery and all retained failures](graphical-install-record.md), source `15f214442d69baa0ebd91f5ad12edc5661281779` |
| Phase-one Beta delivery | Current Beta | [Current work and evidence provenance](stage-one-beta.md) |

[Architecture](architecture.md), [JSON contract](json-contract.md),
[measured performance limits](performance.md) and [formal unexecuted stabilization
protocol](v04-validation.md) remain separate references. Passing a graphical
workflow does not close the formal accuracy/scale gates or establish ownership,
safe uninstall or rollback.

Before cleanup the retained remote refs were main, feat/desktop-record-viewer and
feat/desktop-install-record. All are ancestors of the new Beta task branch. The
historical branch cleanup audit is [retained here](examples/desktop-install-branch-cleanup.json).
This round will recheck ancestry and unique work after the authorized normal PR
merge; it does not force or rewrite history.
