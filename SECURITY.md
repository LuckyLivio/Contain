# Security policy

Contain observes local system state and may eventually help remove application remnants. Treat incorrect attribution and unsafe deletion as security issues.

## Supported version

Security fixes target the latest published `main` branch during v0.2 development.

## Reporting

Use GitHub's private vulnerability reporting for this repository when available. If it is unavailable, contact the maintainer privately through the repository owner's public GitHub profile. Do not post exploit details, personal paths, registry data, databases, or credentials in a public issue. We will acknowledge a report and coordinate a fix and disclosure before publishing details.

## Current safety boundary

- No automatic elevation, kernel driver, or background service. ETW only uses the caller's existing rights; denied access degrades to snapshots.
- No destructive `remove` implementation. `--dry-run` is read only.
- File and registry observations remain `Unknown` without verified writer process-instance evidence. High is a session attribution claim, never proof of exclusive ownership or permission to delete.
- Watch roots are canonicalized. The scanner does not follow links; cleanup planning never deletes.
- The demo fixture only touches its own direct child under `%TEMP%` and a matching HKCU test key.
- Service/task/Run inventories are read-only. ETW receives system-wide provider activity and discards out-of-scope paths; database and JSON exports remain local. Paths, commands, registry values and debug diagnostics can be sensitive.
- Loss, decode failures, ambiguous process lifetime, absent registry hive paths and mixed writers must be visible. Missing data must not become a positive ownership claim.

Please report any path traversal, junction/reparse handling, privilege escalation, unintended deletion, data leak, or misleading attribution.
