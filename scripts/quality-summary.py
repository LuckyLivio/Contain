"""Publish numeric quality evidence, never raw logs, databases or host inventories.

LLVM totals retain their original denominators. Source aliases are identified for
review, not deduplicated to improve a gate. A failed gate still produces a report.
"""
import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import posixpath
import re
import xml.etree.ElementTree as ET


MODES = {"Setup", "Check", "Coverage", "CoverageOriginal", "Dependencies", "Companion", "Bench", "Fuzz"}
METRICS = ("lines", "functions", "regions", "instantiations", "branches")
AFFECTED = {
    "crates/contain-cli/src/render.rs": {"write_str", "source_scope_text", "summary_to", "inspect_to", "diff_to", "history_to", "explain_to"},
    "crates/contain-core/src/storage.rs": {"save", "load_internal", "for_event"},
    "crates/contain-core/src/storage/stream.rs": {"select_raw_only_registry"},
    "crates/contain-core/src/path.rs": {"normalize_path", "in_scope"},
    "crates/contain-core/src/storage/checkpoint.rs": {"available_disk_bytes"},
    "crates/contain-core/src/windows/clock/companion.rs": {"reserve"},
}
STATIC_ARGS = set("fmt clippy nextest run test metadata deny check llvm-cov report bench rustc -V --version --all --check --workspace --all-targets --all-features --locked --offline --profile ci --retries 0 1 66.5 70 --doc --lib --manifest-path --format-version --filter-platform x86_64-pc-windows-msvc --config --json --output-path --html --output-dir --lcov --summary-only --fail-under-lines --fail-under-functions -- -D warnings -NoProfile -File -p contain-core contain-cli contain-test-installer contain-offline --ignored --exact --nocapture".split())


def number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) and value >= 0 else None


def source_path(filename, root):
    """Return normalized and original repo-relative spellings, or None."""
    text = str(filename).replace("\\", "/")
    base = str(root).replace("\\", "/").rstrip("/")
    windows = bool(re.match(r"^[A-Za-z]:/", base))
    compare = str.casefold if windows else (lambda s: s)
    if compare(text).startswith(compare(base + "/")):
        alias = text[len(base) + 1:]
    elif not text.startswith("/") and not re.match(r"^[A-Za-z]:", text):
        alias = text
    else:
        return None
    normalized = posixpath.normpath(alias)
    if normalized == ".." or normalized.startswith("../"):
        return None
    if normalized.split("/", 1)[0] not in {"crates", "tools", "examples", "scripts", "fuzz", "target", "vendor"} and normalized not in {"Cargo.toml", "Cargo.lock", "deny.toml"}:
        return None
    return normalized, alias


def safe_argument(value, root):
    value = str(value)
    if value in STATIC_ARGS:
        return value
    path = source_path(value, root)
    if path:
        return path[0]
    if re.fullmatch(r"(?:storage|windows)::[A-Za-z0-9_:]+", value):
        return value
    return "<unpublished-argument>"


def counts(text):
    text = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", text)
    # Cargo's per-binary summaries are separate execution groups: do not claim
    # duplicate imported tests or zero-test binaries as independent coverage.
    rust = re.findall(r"test result: (?:ok|FAILED)\. (\d+) passed; (\d+) failed; (\d+) ignored; (\d+) measured; (\d+) filtered out", text)
    if rust:
        return {"runner": "cargo-test", "groups": [dict(zip(("passed", "failed", "ignored", "measured", "filtered"), map(int, row))) for row in rust]}
    summary = re.search(r"Summary\s*\[[^\]]+\]\s*(\d+) tests? run:(.*)", text)
    if summary:
        details = summary[2]
        fields = {key: int(found[1]) if (found := re.search(rf"(\d+) {label}", details)) else None
                  for key, label in (("passed", "passed"), ("failed", "failed"), ("skipped", "skipped"), ("leaky", "leaky"))}
        # A completed nextest summary omits the failed/skipped clause at zero.
        for key in ("failed", "skipped", "leaky"):
            if fields[key] is None:
                fields[key] = 0
        return {"runner": "nextest", "executed": int(summary[1]), **fields}
    python = re.search(r"Ran (\d+) tests? in ", text)
    if python:
        end = re.search(r"^(OK|FAILED)(?: \(([^\n]*)\))?\s*$", text, re.M)
        if not end:
            return {"runner": "unittest", "executed": int(python[1]), "passed": None, "failed": None, "errors": None, "skipped": None}
        fields = {key: 0 for key in ("failures", "errors", "skipped", "expected failures", "unexpected successes")}
        for key in fields:
            found = re.search(rf"(?:^|, ){key}=(\d+)(?:,|$)", end[2] or "")
            if found:
                fields[key] = int(found[1])
        executed = int(python[1])
        passed = executed - sum(fields.values())
        return {"runner": "unittest", "executed": executed, "passed": passed,
                "failed": fields["failures"], "errors": fields["errors"], "skipped": fields["skipped"],
                "expected_failures": fields["expected failures"], "unexpected_successes": fields["unexpected successes"]}
    return None


def metrics(summary):
    return {kind: {field: number(summary.get(kind, {}).get(field)) for field in ("count", "covered", "percent")}
            for kind in METRICS if kind in summary}


def coverage(path, root):
    if not path.is_file():
        return None
    report = json.loads(path.read_text(encoding="utf-8-sig"))
    units = report.get("data", [])
    result = {"export_totals": [metrics(unit.get("totals", {})) for unit in units],
              "files": [], "source_alias_groups": [], "affected_functions": [], "unpublished_external_file_entries": 0}
    aliases = defaultdict(set)
    definitions = {}
    for unit in units:
        for entry in unit.get("files", []):
            relative = source_path(entry.get("filename", ""), root)
            if not relative:
                result["unpublished_external_file_entries"] += 1
                continue
            name, alias = relative
            aliases[name].add(alias)
            result["files"].append({"source": name, "source_alias": alias, "metrics": metrics(entry.get("summary", {}))})
        for function in unit.get("functions", []):
            regions = function.get("regions", [])
            filenames = function.get("filenames", [])
            if not regions or not filenames:
                continue
            file_index = regions[0][5]
            if file_index >= len(filenames):
                continue
            relative = source_path(filenames[file_index], root)
            if not relative or relative[0] not in AFFECTED:
                continue
            name, alias = relative
            if name not in definitions:
                source = (root / name).read_text(encoding="utf-8")
                definitions[name] = [(i, found[1]) for i, line in enumerate(source.splitlines(), 1)
                                     if (found := re.search(r"\bfn\s+([A-Za-z_][A-Za-z0-9_]*)\s*[<(]", line))]
            before = [(line, symbol) for line, symbol in definitions[name] if line <= regions[0][0]]
            if not before or before[-1][1] not in AFFECTED[name]:
                continue
            code = [region for region in regions if len(region) >= 8 and region[7] == 0 and region[5] == file_index]
            raw_symbol = str(function.get("name", ""))
            result["affected_functions"].append({
                "source": name, "source_alias": alias, "enclosing_function": before[-1][1],
                "definition_line": before[-1][0], "execution_count": number(function.get("count")),
                "symbol_sha256": hashlib.sha256(raw_symbol.encode()).hexdigest(),
                "code_regions": len(code), "covered_code_regions": sum(region[4] > 0 for region in code),
                "uncovered_code_regions": [{"start": region[:2], "end": region[2:4]} for region in code if region[4] == 0],
            })
    result["source_alias_groups"] = [{"source": name, "aliases": sorted(spellings)} for name, spellings in sorted(aliases.items()) if len(spellings) > 1]
    result["scope"] = "Export totals are unchanged. Inline tests and instantiations are included; region counts are not branch coverage. Source aliases are reported without deduplicating gate denominators."
    return result


def junit(path):
    if not path.is_file():
        return None
    document = ET.parse(path).getroot()
    cases = list(document.iter("testcase"))
    failed = sum(case.find("failure") is not None for case in cases)
    errors = sum(case.find("error") is not None for case in cases)
    skipped = sum(case.find("skipped") is not None for case in cases)
    return {"testcase_entries": len(cases), "passed": len(cases) - failed - errors - skipped,
            "failed": failed, "errors": errors, "skipped_entries": skipped,
            "scope": "JUnit entries only; nextest may omit ignored tests from JUnit. Use its console summary for the selected/skipped denominator."}


def tool_versions(values):
    tools = {}
    for tool, value in values.items():
        if tool not in {"rustc", "cargo", "nextest", "llvm_cov", "llvm-cov", "deny", "python", "pwsh"}:
            continue
        line = str(value).splitlines()[0] if value else ""
        tools[tool] = line if re.fullmatch(r"[A-Za-z0-9_. -]+ \d+\.\d+[A-Za-z0-9_. ()+-]*", line) else None
    return tools


def exact_revision(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", value):
        raise ValueError("quality manifest needs an exact source revision")
    return value


def build(root, directory):
    manifest = json.loads((directory / "gates.json").read_text(encoding="utf-8-sig"))
    revision = exact_revision(manifest.get("source_revision"))
    tools = tool_versions(manifest.get("tools", {}))
    invocations = []
    invocation_ids = {}
    for row in manifest.get("invocations", []):
        if row.get("mode") not in MODES:
            raise ValueError("unknown quality mode")
        invocation_ids[row["invocation_id"]] = len(invocations) + 1
        invocations.append({"number": len(invocations) + 1, "mode": row["mode"],
                            "source_revision": exact_revision(row.get("source_revision")),
                            "completed": row.get("completed") is True,
                            "passed": row.get("passed") is True,
                            "tools": tool_versions(row.get("tools", {}))})
    gates = []
    attempts = defaultdict(int)
    for row in manifest.get("gates", []):
        label = str(row.get("label", ""))
        if not re.fullmatch(r"[a-zA-Z0-9_.-]{1,100}", label):
            raise ValueError("invalid quality gate label")
        mode = row.get("mode")
        if mode not in MODES:
            raise ValueError("unknown quality mode")
        exit_code = row.get("exit_code")
        if exit_code is not None and (not isinstance(exit_code, int) or isinstance(exit_code, bool)):
            raise ValueError("invalid gate exit status")
        log = (directory / str(row.get("log", ""))).resolve()
        if not log.is_relative_to(directory.resolve()) or log.suffix != ".log":
            raise ValueError("gate log must stay within quality output")
        attempts[(mode, label)] += 1
        invocation_number = invocation_ids.get(row.get("invocation_id"))
        invocation = invocations[invocation_number - 1] if invocation_number else None
        gates.append({"mode": mode, "label": label, "attempt": attempts[(mode, label)],
                      "invocation": invocation_number,
                      "source_revision": invocation["source_revision"] if invocation else None,
                      "program": row.get("program") if row.get("program") in {"cargo", "rustc", "python", "python3", "pwsh", "rustup"} else "<unpublished-program>",
                      "arguments": [safe_argument(arg, root) for arg in row.get("arguments", [])],
                      "exit_code": exit_code, "status": "passed" if exit_code == 0 else "unexecuted" if exit_code is None else "failed",
                      "counts": counts(log.read_text(encoding="utf-8-sig", errors="replace")) if log.is_file() else None,
                      "log_available": log.is_file()})
    required = {
        "Check": {"fmt", "clippy", "nextest", "doctests", "fuzz-fmt", "fuzz-clippy", "fuzz-seeds", "scorer-pwsh", "provenance-pwsh", "demo-checks-pwsh", "ci-clock-pwsh"} | {p.stem for p in (root / "scripts").glob("test-*.py")},
        "Coverage": {"coverage", "coverage-html", "coverage-lcov", "coverage-floor"},
        "CoverageOriginal": {"coverage-original", "coverage-original-html", "coverage-original-lcov", "coverage-original-floor"},
        "Dependencies": {"metadata", "deny", "metadata-fuzz", "fuzz-lock", "deny-fuzz"},
        "Companion": {"companion-spool"},
    }
    latest = {i["mode"]: i for i in invocations}
    current = {mode: invocation for mode, invocation in latest.items() if invocation["source_revision"] == revision}
    selected = {mode: [g for g in gates if mode in current and g["invocation"] == current[mode]["number"]] for mode in required}
    missing = {mode: sorted(labels - {g["label"] for g in selected[mode]}) for mode, labels in required.items()}
    complete = not any(missing.values()) and all(current[mode]["completed"] for mode in required)

    def produced(mode, label, reader):
        # A previous successful export must never be attributed to a later failed
        # invocation. Coverage modes archive old reports before generating; each
        # nextest gate archives its own JUnit beside its unique log.
        matching = [g for g in selected.get(mode, []) if g["label"] == label]
        if not matching:
            return None
        gate = matching[-1]
        value = reader(gate)
        if value is None:
            return None
        return {"producing_invocation": gate["invocation"], "source_revision": gate["source_revision"],
                "producing_gate_exit_code": gate["exit_code"], **value}

    def nextest_report(gate):
        index = next(i for i, item in enumerate(gates) if item is gate)
        return junit((directory / manifest["gates"][index]["log"]).parent / "junit.xml")

    return {"schema_version": 1, "source_revision": revision, "tools": tools, "invocations": invocations, "gates": gates,
            "missing_gates": missing, "all_recorded_gates_passed": bool(gates) and all(g["status"] == "passed" for g in gates),
            "required_gates_complete": complete,
            "current_required_gates_passed": complete and all(current[mode]["passed"] and all(g["status"] == "passed" for g in selected[mode]) for mode in required),
            "all_recorded_invocations_passed": all(i["completed"] and i["passed"] for i in invocations) if invocations else None,
            "nextest_junit": produced("Check", "nextest", nextest_report),
            "coverage_workspace": produced("Coverage", "coverage", lambda _: coverage(directory / "coverage.json", root)),
            "coverage_original_packages": produced("CoverageOriginal", "coverage-original", lambda _: coverage(directory / "coverage-original.json", root)),
            "coverage_floors": {"lines_percent": 66.5, "functions_percent": 70},
            "scope": "Windows quality gate evidence. Shared production tests also execute in contain-offline; counts are executions, not unique tests. Workspace and original-package coverage stay separate. No real-ETW or release acceptance is inferred. Raw logs, XML bodies, source documents, host inventory and database files are not published."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--quality", type=Path, default=Path("target/quality"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = build(args.root.resolve(), args.quality.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("Quality summary written; raw logs and databases were not copied.")


if __name__ == "__main__":
    main()
