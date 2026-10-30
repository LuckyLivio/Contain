"""Windows offline report preflight, using saved synthetic models and real CLI.

No installer, provider or registry operation is executed. Production RawBuffer /
Storage generates the database; package-trial's production report and collection
functions execute all seven actual packaged CLI commands. Full model databases,
paths and CLI stderr remain in --output; --public-output contains safe summaries.
"""
import argparse
import ctypes
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid


PROTOCOL = "contain-synthetic-report-preflight-1"
COMMANDS = ("inspect-json", "history-json", "explain-json", "inspect-text",
            "diff-text", "history-text", "explain-text")
SPEC = importlib.util.spec_from_file_location("trial_report", Path(__file__).with_name("package-trial.py"))
TRIAL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRIAL)


def require(condition, code):
    if not condition:
        raise ValueError(code)


def json_write(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(value, output, indent=2, allow_nan=False)
        output.write("\n")


def plain_path(path):
    text = str(path)
    if text.startswith("\\\\?\\UNC\\"):
        return "\\\\" + text[8:]
    return text[4:] if text.startswith("\\\\?\\") else text


def extended_path(path):
    value = plain_path(path)
    return "\\\\?\\UNC\\" + value[2:] if value.startswith("\\\\") else "\\\\?\\" + value


def short_path(path):
    """Request the existing OS alias. An unchanged name does not cover 8.3."""
    from ctypes import wintypes
    function = ctypes.WinDLL("kernel32", use_last_error=True).GetShortPathNameW
    function.argtypes = (wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD)
    function.restype = wintypes.DWORD
    needed = function(str(path), None, 0)
    if not needed:
        return None, "get_short_path_unavailable"
    buffer = ctypes.create_unicode_buffer(needed)
    written = function(str(path), buffer, needed)
    require(0 < written < needed, "short_path_query_failed")
    value = buffer.value
    require(Path(value).samefile(path), "short_path_identity_mismatch")
    if TRIAL.windows_spelling(value) == TRIAL.windows_spelling(str(path)):
        return None, "filesystem_did_not_supply_distinct_8dot3_alias"
    return value, None


def synthetic_identity(package, manifest, root, database):
    before = [dict(path=str(package / row["name"]), bytes=row["bytes"], sha256=row["sha256"])
              for row in manifest["binaries"]]
    # These are actual file identities, not evidence of loaded images or ETW.
    for row in before:
        require(Path(row["path"]).stat().st_size == row["bytes"]
                and TRIAL.sha(Path(row["path"])) == row["sha256"], "preflight_package_binary_changed")
    source = dict(head=manifest["source_sha"], runtime_tree_sha256=manifest["runtime_tree_sha256"])
    return dict(synthetic=True, input_kind="synthetic_report_fixture", build_mode="prebuilt",
        prebuilt_manifest_verified=True, prebuilt_manifest_unchanged=True,
        prebuilt_manifest_sha256=TRIAL.sha(package / "BUILD.json"),
        prebuilt_manifest_after_sha256=TRIAL.sha(package / "BUILD.json"),
        binary_before=before,
        binary_after=dict(unchanged=True, binaries=[dict(before=row, after=row, unchanged=True) for row in before]),
        source_before_build=source, source_after_build=source,
        fixture_root=root, database=str(database), fixture_clock=None, raw_clock=None,
        note="SYNTHETIC saved model; package files hashed, no native fixture or provider executed")


def assert_report(summary, text, capture, private, roots):
    require(summary.get("complete") is True and summary.get("scope") == "synthetic_windows_cli_report",
            "synthetic_report_incomplete_or_mislabelled")
    require(summary.get("candidate_verified") is False, "synthetic_report_claims_candidate_validation")
    commands = summary.get("commands", [])
    require([row.get("name") for row in commands] == list(COMMANDS)
            and all(row.get("exit_code") == 0 for row in commands), "all_seven_report_commands_required")
    actual = TRIAL.json_document((private / "inspect-json.stdout.txt").read_text(encoding="utf-8"), "inspect")
    require(actual == capture, "preflight_inspect_json_changed")
    event = actual["events"][0]
    require(summary.get("event_id") == event["id"] and event["confidence"] == "High",
            "preflight_historical_event_not_selected")
    require(event["raw"]["file_name"]["path_source"] == "file_object_cache"
            and event["raw"]["file_name"]["current_path_verified"] is False,
            "preflight_historical_name_promoted")
    require(actual["events"][1]["confidence"] == "Unknown"
            and all(row["confidence"] == "Unknown" for row in actual["files"]),
            "preflight_unknown_evidence_promoted")
    history = TRIAL.json_document((private / "history-json.stdout.txt").read_text(encoding="utf-8"), "history")
    explanation = TRIAL.json_document((private / "explain-json.stdout.txt").read_text(encoding="utf-8"), "explain")
    require(history["events"] == [event] and explanation["event"] == event
            and explanation["app_id"] == actual["id"], "preflight_history_explain_mismatch")
    require(event["evidence"]["process_image"] == actual["installer"], "preflight_actor_image_missing")
    require(text.startswith("SYNTHETIC") and "not ETW or an installation" in text,
            "synthetic_report_label_missing")
    sections = {label: text.split("\n" + label + "\n", 1)[1].split("\n\n", 1)[0]
                for label in ("INSPECT", "DIFF", "HISTORY", "EXPLAIN")}
    require("SyntheticReport" in sections["INSPECT"] and "finished" in sections["INSPECT"].lower(),
            "preflight_inspect_text_missing_state")
    require(all(fragment in sections["INSPECT"] for fragment in (
        "Recorded timeline: 3 observations", "2 known actors | 1 unknown actors",
        "Recorded state differences: 2 files (2 Unknown)",
        "Sources: ETW process=active | file=active | registry=not_requested",
        "Recording: finished | Quality: " + actual["quality"]["level"],
    )), "preflight_inspect_text_counts_disagree")
    # DIFF includes a leading newline, so inspect the full exported block.
    diff = text.split("\nDIFF\n", 1)[1].split("\nHISTORY\n", 1)[0]
    require("STATE DIFFERENCES WITH UNKNOWN ATTRIBUTION" in diff
            and "historical-deleted.tmp" in diff and "recorded.tmp" in diff
            and diff.count("[Unknown]") == 2, "preflight_diff_lost_alias_or_unknown")
    require(event["id"] in sections["HISTORY"] and event["id"] in sections["EXPLAIN"],
            "preflight_text_event_untraceable")
    require(all("file_object_cache" in sections[name] and "historical-deleted.tmp" in sections[name]
                for name in ("HISTORY", "EXPLAIN")), "preflight_historical_name_text_missing")
    actor_lines = [line for line in sections["EXPLAIN"].splitlines() if line.startswith("Actor:")]
    require(len(actor_lines) == 1 and "<fixture-bin>\\contain-test-installer.exe" in actor_lines[0]
            and "null" not in actor_lines[0], "preflight_actor_image_not_redacted")
    require("historical" in text and "current path" in text.lower(), "preflight_name_boundary_missing")
    for root in roots:
        require(root.casefold() not in text.casefold()
                and root.replace("\\", "\\\\").casefold() not in text.casefold(),
                "preflight_public_root_leaked")
    require(not re.search(r"contain-demo-[0-9a-f]{32}|[A-Za-z]:[\\/]|\\\\|S-1-5-\d", text, re.I),
            "preflight_public_private_identity_leaked")


def run_case(label, alias, root, package, generator, manifest, output, public):
    local = output / label
    local.mkdir()
    capture_directory = local / "capture"
    result = dict(case=label, scope="synthetic_windows_cli_report", status="failed", commands=[], failure=None)
    try:
        completed = subprocess.run([str(generator), "--report-fixture", str(capture_directory), str(root), alias,
                                    str(package / "contain-test-installer.exe")],
                                   capture_output=True, text=True, encoding="utf-8", timeout=120)
        (local / "generator.stdout.txt").write_text(completed.stdout, encoding="utf-8")
        (local / "generator.stderr.txt").write_text(completed.stderr, encoding="utf-8")
        result["generator_exit_code"] = completed.returncode
        require(completed.returncode == 0, "synthetic_generator_failed")
        capture = TRIAL.read(capture_directory / "capture.json")["data"]
        json_write(capture_directory / "run-identity.json",
                   synthetic_identity(package, manifest, alias, capture_directory / "report.db"))
        exported = local / "approved-report"
        command_output = local / "private-cli"
        result["report_exit_code"] = TRIAL.report(argparse.Namespace(package=package,
            capture_directory=capture_directory, private_output=command_output, public_output=exported,
            scenario="synthetic"))
        summary = TRIAL.read(exported / "summary.json")
        result["commands"] = summary.get("commands", [])
        result["report_failure"] = summary.get("failure")
        require(result["report_exit_code"] == 0, "synthetic_report_export_failed")
        text = (exported / "cli-report.txt").read_text(encoding="utf-8")
        assert_report(summary, text, capture, command_output,
                      [alias, str(root), *capture["watch_roots"], capture["installer"]])
        require(not (root / "historical-deleted.tmp").exists(), "historical_child_was_created")
        require((root / ".contain-demo-marker").read_bytes() == b"Contain test fixture", "fixture_marker_changed")
        # Exercise the same approved-file collection used for the actual ZIP.
        # A private copy keeps the original frozen package untouched.
        collection = local / "package-collection"
        shutil.copytree(package, collection)
        TRIAL.collect_report(collection, "synthetic", exported)
        collected = collection / "examples" / "synthetic"
        require({path.name for path in collected.iterdir()} == {"summary.json", "cli-report.txt"},
                "preflight_collection_contains_unapproved_files")
        for name in ("summary.json", "cli-report.txt"):
            require(TRIAL.sha(collected / name) == TRIAL.sha(exported / name), "preflight_collection_changed")
        TRIAL.verify_package(package)
        approved = public / label
        approved.mkdir()
        for name in ("summary.json", "cli-report.txt"):
            shutil.copyfile(collected / name, approved / name)
        result.update(status="passed", source_sha=manifest["source_sha"], event_id=summary["event_id"],
            report_sha256=summary["report_sha256"], collected_report_sha256=TRIAL.sha(collected / "cli-report.txt"),
            checks=dict(real_contain_exe=True, all_seven_commands=True, reopened_json_equal=True,
                same_event_paged_and_explained=True, historical_child_absent=True, unknown_preserved=True,
                all_four_sections=True, private_paths_redacted=True, production_collection=True))
    except (OSError, ValueError, KeyError, TypeError, IndexError, subprocess.SubprocessError) as error:
        (local / "failure.txt").write_text(repr(error), encoding="utf-8")
        result["failure"] = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "preflight_failed_private_details_retained"
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--generator", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--public-output", type=Path, required=True)
    args = parser.parse_args()
    output = TRIAL.new_directory(args.output)
    public = TRIAL.new_directory(args.public_output)
    summary = dict(schema_version=1, protocol_revision=PROTOCOL,
        scope="synthetic_windows_cli_report_preflight", synthetic=True, native_windows=os.name == "nt",
        native_capture_executed=False, candidate_verified=False, complete=False, passed=False,
        collection_verified=False, report_commands_executed=0, cases=[],
        commands_executed=0, commands_passed=0, commands_failed=0, commands_not_run=2 * len(COMMANDS),
        cases_passed=0, cases_failed=0, cases_not_covered=1,
        python_version=sys.version.split()[0], preflight_sha256=TRIAL.sha(Path(__file__)), failure=None)
    try:
        require(os.name == "nt", "windows_native_filesystem_and_cli_required")
        package, generator = args.package.resolve(), args.generator.resolve()
        manifest, _ = TRIAL.verify_package(package)
        summary.update(source_sha=manifest["source_sha"], build_manifest_sha256=TRIAL.sha(package / "BUILD.json"),
            contain_sha256=TRIAL.sha(package / "contain.exe"), generator_sha256=TRIAL.sha(generator),
            exporter_sha256=TRIAL.sha(Path(TRIAL.__file__)), exporter=TRIAL.exporter_identity(),
            target=manifest["target"], profile=manifest["profile"])
        parent = output / "synthetic fixture with long directory name"
        parent.mkdir()
        root = parent / ("contain-demo-" + uuid.uuid4().hex)
        root.mkdir()
        (root / ".contain-demo-marker").write_bytes(b"Contain test fixture")
        aliases = [("normal", plain_path(root)), ("extended", extended_path(root))]
        short, missing = short_path(root)
        summary["windows_8dot3"] = dict(status="covered" if short else "not_covered", reason=missing)
        if short:
            aliases.append(("actual-8dot3", short))
        for label, alias in aliases:
            require(Path(alias).samefile(root), "preflight_alias_identity_mismatch")
            summary["cases"].append(run_case(label, alias, root, package, generator, manifest, output, public))
        commands = [row for case in summary["cases"] for row in case["commands"]]
        summary.update(commands_executed=len(commands), commands_passed=sum(row["exit_code"] == 0 for row in commands),
            commands_failed=sum(row["exit_code"] != 0 for row in commands),
            commands_not_run=len(aliases) * len(COMMANDS) - len(commands),
            cases_passed=sum(case["status"] == "passed" for case in summary["cases"]),
            cases_failed=sum(case["status"] == "failed" for case in summary["cases"]),
            cases_not_covered=0 if short else 1)
        summary["complete"] = all(case["status"] == "passed" for case in summary["cases"]) and bool(summary["cases"])
        summary["passed"] = summary["complete"]
        summary["collection_verified"] = summary["complete"] and all(
            case.get("checks", {}).get("production_collection") is True for case in summary["cases"])
        summary["report_commands_executed"] = len(commands)
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        (output / "failure.txt").write_text(repr(error), encoding="utf-8")
        summary["failure"] = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "preflight_failed_private_details_retained"
    json_write(public / "summary.json", summary)
    print(json.dumps(dict(protocol=PROTOCOL, complete=summary["complete"],
                         commands_executed=summary["commands_executed"], failure=summary["failure"])))
    return 0 if summary["complete"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
