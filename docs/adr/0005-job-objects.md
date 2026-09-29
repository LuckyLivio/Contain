# ADR 0005: Observe descendants without default Job Object assignment

## Context
An installer can exit while a launcher or helper continues. A job might simplify
membership tracking, but changing process membership can affect the installation.

## Options
Assign a new job to every installer; opt-in job mode; retain ETW birth ancestry and
use a bounded drain without changing the launcher's job configuration.

## Decision
Do not assign jobs in v0.3. Retain original creation ancestry. After owned-root
exit, continue until no known live descendants remain and the quiet duration has
elapsed, or `--max-drain-ms` expires. `--settle-ms` defaults to 1500 ms and max drain
to 10000 ms. ETW uses at least a 1200 ms quiet allowance for buffered delivery.
Scoped resource activity resets quiet time; unrelated scoped activity may extend
drain but cannot extend it past the maximum. Timeout marks capture Incomplete.
The pure deadline policy and the detached-after-exit fixture test both paths.

## Consequences
The observer does not impose new resource limits or kill descendants. Capturing a
continuing helper is possible within a documented finite boundary. Polling-only
mode may miss a child whose parent exits before a sampled live chain is available.

## Limitations
Existing/nested job membership, breakaway flags, elevation brokers, packaged-app
activation and installer compatibility need separate tests before any opt-in mode.
Job completion-port notifications generally are not guaranteed delivery. Jobs do
not automatically attribute service-manager or other broker activity to an app.
Changes after max drain or a false quiet interval remain outside the session.

## Sources
- [Job Objects: nested jobs, breakaway and notification semantics](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
