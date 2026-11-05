# Windows Beta 3: a stable personal record library

This iteration covers default storage, startup history and the necessary new-record
controls only. Beta 2 progress and failure feedback retain their contracts. The
whole-Program-Files finalizing incident remains unconfirmed and separately tracked
in [Issue #5](https://github.com/LuckyLivio/Contain/issues/5).

## Behavior

- Windows Known Folder LocalApplicationData supplies `Contain\Records\contain.db`.
  Startup reads existing sessions; a missing library displays an empty list and
  creates no directories or database. Explicit Start creates the library on demand.
  Moving the ZIP or changing the working directory does not change this location.
- New records default to **保存到我的记录**. Selecting an EXE suggests its filename
  as the record name until the user edits it. Observation scope and Start remain
  explicit. Custom database location is an advanced option.
- Sessions append to the existing SQLite library; same names are allowed and
  UUIDs select exact sessions. Opening an external database stays read-only and
  never changes the next new record's default. **我的记录** returns to the library.
- Physical database/WAL/SHM scope checks precede writer launch. Existing databases
  pass the production read-only compatibility path before a writer can initialize
  or migrate them. Missing-main sidecars, corrupt, unrelated, future or inaccessible
  files remain preserved. Prelaunch failures offer **更改保存位置** and details.
- Only a confirmed failed-before-installer result permits correction and retry.
  Missing or untrusted completion cannot enable a duplicate installation.

## Acceptance contract

The native harness uses a marked isolated record root and refuses an invalid root
or junction redirect; it never falls back to the real personal library. The final
candidate must pass eight personal-history checks, existing 22 viewer checks,
four layout checks, eight progress/failure checks and six GUI recording checks.

One controlled real ETW recording is driven through the packaged
`Contain.Viewer.exe`, with no database path interaction. It must exit completely,
restart from another working directory, open the same session UUID from default
history and explain the same event. Source, launcher, assembly, CLI and frozen
package-manifest identities are required by the packaging gate. Synthetic
cold-restart checks exercise the same executable separately; they do not satisfy
real ETW acceptance. Native DPI and simulated 150%/200% scaling are reported
separately. Final results and immutable package identity are added after acceptance.

No full-history search, installation-path discovery, uninstall or cleanup feature
is added. Existing evidence and High/Unknown/Degraded meanings are unchanged.
