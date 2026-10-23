"""Reject fuzz graph drift for dependencies shared with the production lockfile."""
import sys
import tomllib
from pathlib import Path


def shared_drift(production, fuzz):
    versions = {}
    for package in production["package"]:
        key = (package["name"], package.get("source"))
        versions.setdefault(key, set()).add(package["version"])
    problems = []
    for package in fuzz["package"]:
        key = (package["name"], package.get("source"))
        if key in versions and package["version"] not in versions[key]:
            problems.append(
                f"{package['name']}: fuzz {package['version']}, "
                f"production {', '.join(sorted(versions[key]))}"
            )
    return problems


def main():
    root = Path(__file__).resolve().parent.parent
    try:
        production = tomllib.loads((root / "Cargo.lock").read_text(encoding="utf-8"))
        fuzz = tomllib.loads((root / "fuzz/Cargo.lock").read_text(encoding="utf-8"))
        problems = shared_drift(production, fuzz)
    except (OSError, ValueError, KeyError) as error:
        print(f"Cannot compare locked dependency graphs: {error}", file=sys.stderr)
        return 1
    if problems:
        print("Fuzz dependencies drift from production:\n" + "\n".join(problems), file=sys.stderr)
        return 1
    print("Shared fuzz dependencies match production; fuzz-only dependencies audited separately.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
