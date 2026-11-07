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
| One-page new installation record, Beta 7 | Released | [First-use flow and Windows verification](simple-install-beta7.md) |
| Folder choice and finished-task guidance, Beta 8 | Released | [Focused fixes and native verification](install-guidance-beta8.md) |
| Follow the launched EXE without guessing a directory, Beta 9 | Released | [Source evidence, page and package verification](follow-installer-beta9.md) |
| Capture lifecycle handoff and saved-loss diagnostics, Beta 10 | Prerelease | [Frozen product, D4/D6 acceptance, retained failures and release policy](beta10-release.md), product `b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4` |
| Modern WPF desktop, v0.4.0-beta.1 | Prerelease | [Final package and release state](beta04-release.md), [actual screenshots](beta04-screenshots.md), product / native tests `5a6ce89867e4c8c9d428de0a80b67b421bbd9717`; [M13 run 42344602567](https://github.com/LuckyLivio/Contain/actions/runs/42344602567) |

[Architecture](architecture.md), [JSON contract](json-contract.md),
[measured performance limits](performance.md) and [formal unexecuted stabilization
protocol](v04-validation.md) remain separate references. Passing a graphical
workflow does not close the formal accuracy/scale gates or establish ownership,
safe uninstall or rollback.

The [historical branch cleanup audit](examples/desktop-install-branch-cleanup.json) records the branches at the time of the graphical installation MVP.

The v0.4.0-beta.1 M13 package passed all 14 native groups and 997 assertions,
34 model checks, 12 actual Windows control checks and 24 independent fixture
predicates. It used the real WPF EXE on Windows Server 2025 x64 / 10.0.26100 at
actual 96 DPI, completed one GUI-controlled ETW installation, saved the exact
session and cold-reopened its original event explanation. All six Viewer launches
exited normally with code 0. Receiver, candidate, handoff, raw-save, ETW and decode
loss counts were zero in this bounded scenario. No broad pressure matrix was
repeated; the unchanged Beta.10 core retains its separate evidence and identity.

The accepted original ZIP SHA-256 is
`953b243bb3dd8ea32e61dcf5fdeaf143b9e697ea4a57021fb85ffd961ef907e3`.
Normal screenshots are 1024 × 720 pixels and compact screenshots 820 × 640,
all actual 96 DPI. Synthetic records and real capture screenshots are labelled
separately. Windows 10/11, Parallels, actual 144/192 DPI and monitor switching
remain unverified. Main checks, tag/main identities, publication and public asset
readback are separate release gates, recorded by the linked release document and
`FINAL-ACCEPTANCE.json`; the product source is not substituted for a later merge SHA.

[Every modern-desktop attempt](beta04-validation-attempts.md) remains recorded.
M11's isolated keyboard-control timeout was not reproduced by M12's staged
diagnosis, whose success is not repair proof. M13's acceptance does not rewrite
that unknown cause, the prior failed attempts, or Beta.10 issues #16 / #17 / #18.
