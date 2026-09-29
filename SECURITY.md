# Security policy

Contain observes local system state and may eventually help remove application remnants. Treat incorrect attribution and unsafe deletion as security issues.

## Supported version

Security fixes target the latest published `main` branch during v0.1 development.

## Reporting

Use GitHub's private vulnerability reporting for this repository when available. If it is unavailable, contact the maintainer privately through the repository owner's public GitHub profile. Do not post exploit details, personal paths, registry data, databases, or credentials in a public issue. We will acknowledge a report and coordinate a fix and disclosure before publishing details.

## Current safety boundary

- No elevated privileges, kernel driver, or background service.
- No destructive `remove` implementation. `--dry-run` is read only.
- File and registry observations have `Unknown` application ownership without writer PID evidence.
- Watch roots are canonicalized. The scanner does not follow links; cleanup planning never deletes.
- The demo fixture only touches its own direct child under `%TEMP%` and a matching HKCU test key.

Please report any path traversal, junction/reparse handling, privilege escalation, unintended deletion, data leak, or misleading attribution.
