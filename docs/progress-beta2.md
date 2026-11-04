# Windows Beta 2: real task progress and failure feedback

This iteration is limited to installation-task progress, failure feedback and
confirmed related defects. The earlier milestones remain closed. The release
candidate is `0.3.2-beta.2`; validation and publication are pending until the
frozen package passes its declared checks.

## What is measured

The additive schema-3 install stream reports the actual backend operation,
monotonic task and operation duration, measured work units when available, and
the last confirmed work/checkpoint time. Entering a real operation is a checkpoint;
repainting the window or receiving an unchanged heartbeat is not completed work.
Unknown totals do not produce a percentage or estimated completion time. Missing
measurements remain unavailable. Evidence QPC/FILETIME and confidence are unchanged.

The desktop keeps installer exit, source-stop completion, committed data,
session completion and coverage quality separate. An installer that is still
running may need input in its own window; Contain cannot detect whether a dialog
is waiting. A silent backend does not establish that a task has hung.

Failures retain the exact session identity, last confirmed operation, original
bounded diagnostics and any already saved evidence. Task summaries can be copied
locally. Nothing is uploaded by the product. Active tasks retain the conservative
window-close behavior; no installer-tree termination or external trace stop was
added.

## Program Files report

The original Parallels / Windows 11 whole-Program-Files incident has no supplied
database or complete error log. Its cause remains unknown and is **not confirmed
fixed**. This iteration instruments the real final-snapshot, source-drain,
association, persistence and result-readback paths. It does not exclude Program
Files, reduce the selected scope, suppress errors or force completion.

## Declared verification

- Deterministic progress/sequence/failure regressions, including repeated
  finalizing updates, unavailable measurements and saved-but-unfinished records.
- Existing Windows Rust Check, both coverage denominators, dependency checks and
  companion regression with unchanged assertions and floors.
- Actual Windows forms and packaged CLI: existing-record reads, paging, explain,
  export, default-DPI geometry and separate simulated 150%/200% layout stress.
- After offline checks pass, one controlled GUI-started ETW fixture through the
  existing Windows CI plan, including exact-session auto-open and original
  independent fixture oracle. Local ordinary-token results are reported separately.

Builds and local evidence use `target/beta2`; earlier backups, databases, logs and
build directories are retained in place. A final source commit, frozen package
identity, actual results and immutable download will be recorded before delivery.
