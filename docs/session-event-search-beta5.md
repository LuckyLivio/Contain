# Beta 5: search this record's events

Beta 5 adds full-session event search to **事件与依据**. **搜索本次记录**
searches the selected database and exact session UUID before pagination. It
matches recorded resource names/paths, operations and confidence labels (plus
the existing event ID/type fields). ASCII letters ignore case; non-ASCII text
matches exactly. Spaces, Chinese, `%` and `_` are ordinary literal characters.

Changing the query starts at the first result page. Each page contains at most
50 rows and shows the matching total and visible range. Selection, copied event
ID and explanation refer to the same original event. Loading, no matches and
failure have distinct feedback; superseded reads cannot replace the current
list or explanation. Clearing search restores ordinary event browsing.

The core streams indexed event documents within one read snapshot and applies
one shared predicate for counting and paging; historical schemas reuse the
existing compatibility mapping. No complete modern history is sent to the
desktop. Ordering remains `(timestamp_ticks, sequence, id)`. Separate queries
can observe newer commits in an actively changing database.

During search, **清除搜索后可导出普通浏览页** explains the report boundary and
**清除搜索** is directly available. Ordinary reports still include session state
changes, the ordinary unfiltered event page and the selected explanation.
Exporting the complete matching result is deferred to Beta 6.

## Verification scope

Acceptance uses isolated saved synthetic databases, including 130 events and
53 fixed expected matches after the original first 50 events, a same-name
second session, timestamp ties, literal special characters, and older schemas.
The Windows desktop-only CI plan operates the packaged executable and production
controls with the packaged CLI. It requests no installer run or new ETW capture.
Native DPI and simulated 150%/200% scaling are separate measurements.

Linux checks use the production storage/model modules in `contain-offline` and
cross-compile the desktop. Windows quality gates and native acceptance are
required for release. Results and immutable package identity are recorded below
after the final accepted CI run.

## Limits

This searches one selected session, not other records or databases. It adds no
ownership or safe-deletion conclusion. The package remains unsigned Windows x64.
Windows 10, ARM64, Parallels, real monitor DPI transitions and third-party
installer matrices are not newly established. The existing finalizing incident
in [Issue #5](https://github.com/LuckyLivio/Contain/issues/5) remains independent.
