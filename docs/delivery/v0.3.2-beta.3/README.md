# Beta 3 immutable delivery identity

Version `0.3.2-beta.3`, frozen source
`3c2d3706d05dcf0a969dfdde2cc92213c6734e83`.

[Release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.3) ·
[Windows x64 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.3/contain-0.3.2-beta.3-windows-x64.zip) ·
[SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.3/SHA256SUMS.txt).

The ZIP is the unmodified artifact accepted by
[Windows CI 41940121546](https://github.com/LuckyLivio/Contain/actions/runs/41940121546).
All 477 members, 476 content entries and 383 exact tracked source files were
verified. One actual packaged EXE GUI/ETW capture used the default library without
database-path interaction, fully exited and reopened the same session/event from
another working directory. CI reported DeviceDpi 96. The downloaded final package
also passed 42 offline native checks on local Windows 11 at DeviceDpi 144,
including a moved ZIP and exact-session/event cold restart; no additional capture.

The normal PR #6 merge adds delivery documentation after the frozen runtime
source. Tag and package retain the exact validated revision, with no rebuild or
repack. Beta.2 and Beta.1 assets remain preserved. Full local traces, databases,
WAL/SHM and private logs are excluded from publication.

[Behavior, precise results, retained failures and limits](../../personal-records-beta3.md) ·
[Build identity](BUILD-IDENTITY.json).
