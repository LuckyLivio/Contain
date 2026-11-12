"""Aggregate Stage A's independent native jobs without inheriting historical gates.

The two live captures are fixed before execution: one WPF installation and one
12000-file independent background load. This does not claim a reliability fix.
"""
import argparse
import importlib.util
import json
import os
import platform
import struct
from pathlib import Path
import re
import zipfile


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


PAIR = module("existing_capture_summary", "summarize-capture-loss-pair.py")
PACKAGE = module("ux_frozen_package", "package-beta2-desktop.py")
SQLITE_VERSION = "3.54.1"
NATIVE_CHECKS = [
    "frozen_package_identity", "empty_personal_library_and_native_picker_cancel", "saved_records_exact_identity_and_outcomes",
    "search_130_events_53_matches_exact_order", "search_copy_explain_and_export_all", "external_legacy_readonly_and_return",
    "same_name_session_switch_and_clear", "compact_visible_keyboard_actions", "saved_records_cold_restart",
    "gui_default_configuration_no_implicit_install", "gui_single_start_close_guard_and_saved_uuid",
    "real_etw_parent_child_independent_oracle", "real_event_explain_export_and_cold_restart", "source_databases_and_package_unchanged",
]


def verify_tools(directory, source, manifest_hash):
    # Share the package's regular-file, path, alias and symlink/junction rules.
    files = PACKAGE.BASE.tree_files(directory)
    PACKAGE.require("TOOLS.json" in files and PACKAGE.BASE.sha(files["TOOLS.json"]) == manifest_hash,
                    "validation tool manifest identity differs")
    manifest = read(files["TOOLS.json"])
    PACKAGE.require(manifest.get("source_sha") == source and manifest.get("synthetic_stores_only") is True,
                    "validation tool source differs")
    rows = manifest.get("files", [])
    expected = {row["path"]: row["sha256"] for row in rows}
    PACKAGE.BASE.safe_names(list(expected))
    PACKAGE.require(len(expected) == len(rows) and "TOOLS.json" not in expected
                    and set(files) == set(expected) | {"TOOLS.json"}, "validation tool membership differs")
    PACKAGE.require(all(PACKAGE.BASE.sha(files[name]) == digest for name, digest in expected.items()),
                    "validation tool file differs")


def read(path):
    if path is None:
        return {}
    try:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def captured_sqlite_version(directory):
    # Reuse saved evidence only. Never export arbitrary stream/error text.
    document = read(Path(directory) / "private/inspect.stdout")
    if not isinstance(document, dict) or document.get("schema_version") != 3 or document.get("kind") != "inspect":
        return None
    value = document.get("data")
    for key in ("backend", "stream", "sqlite_version"):
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value if isinstance(value, str) and re.fullmatch(r"[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}", value) else None


def capture(directory, source, zip_hash):
    result = PAIR.summarize(directory)
    result.update(schema_version=1, protocol="contain-ux-load-acceptance-1", source_sha=source,
                  zip_sha256=zip_hash, maximum_native_captures=1, retries=0,
                  background_files=12000, measurement_kind="real_etw", sqlite_version=captured_sqlite_version(directory),
                  validation_environment=dict(os=platform.system(), windows_version=platform.win32_ver()[1],
                      machine=platform.machine(), python=platform.python_version(), process_bits=struct.calcsize("P")*8,
                      emulation_verified=False))
    # PAIR acceptance includes the unchanged independent follow oracle, exact
    # saved-session readback, workload interval/content and all eight accounts.
    result["accepted"] = read(Path(directory) / "public/follow-experiment-summary.json").get("source_sha") == source \
        and result.get("accepted") is True and result.get("workload_complete") is True \
        and result.get("all_owned_processes_exited") is True and result.get("background", {}).get("files") == 12000
    return result


def final(needs, desktop, load, build, source, zip_hash):
    checks = {
        "linked_sqlite_version": load.get("sqlite_version") == SQLITE_VERSION,
        "all_independent_jobs_succeeded": all(needs.get(name, {}).get("result") == "success" for name in ("build", "wpf", "capture")),
        "exact_frozen_source_and_zip": re.fullmatch(r"[0-9a-f]{40}", source) is not None
            and re.fullmatch(r"[0-9a-f]{64}", zip_hash) is not None
            and build.get("source_sha") == source and build.get("version") == "0.4.0-beta.2"
            and build.get("core_source_sha") == source and build.get("reused_core_binary") is False,
        "full_native_wpf": desktop.get("accepted") is True and desktop.get("source_sha") == source
            and desktop.get("native_windows") is True and desktop.get("actual_etw") is True
            and desktop.get("cold_restart_verified") is True and desktop.get("retries") == 0
            and desktop.get("failed") == 0 and desktop.get("not_run") == 0
            and desktop.get("planned_checks") == NATIVE_CHECKS and desktop.get("passed") == len(NATIVE_CHECKS)
            and isinstance(desktop.get("checks"), list)
            and [row.get("name") if isinstance(row, dict) else None for row in desktop["checks"]] == NATIVE_CHECKS
            and all(row.get("status") == "passed" for row in desktop["checks"])
            and desktop.get("core_sha256") == build.get("core_sha256")
            and all(desktop.get(field) == next((row.get("sha256") for row in build.get("files", []) if row.get("path") == name), None)
                    for field, name in [("viewer_exe_sha256", "Contain.Viewer.exe"), ("viewer_dll_sha256", "Contain.Viewer.dll")]),
        "independent_fixed_load": load.get("accepted") is True and load.get("source_sha") == source
            and load.get("zip_sha256") == zip_hash and load.get("maximum_native_captures") == 1
            and load.get("retries") == 0 and load.get("background_files") == 12000
            and load.get("core_sha256") == build.get("core_sha256"),
    }
    return dict(schema_version=1, protocol="contain-ux-native-acceptance-1", source_sha=source, zip_sha256=zip_hash,
                accepted=all(checks.values()), checks=checks, maximum_native_captures=2, retries=0,
                jobs={name: needs.get(name, {}).get("result", "missing") for name in ("build", "wpf", "capture")},
                desktop=desktop, load=load,
                limitations=["Native acceptance still requires the separate repository quality and merged-main checks before release.",
                             "Stage A contains desktop changes and a reviewed SQLite safety update; no capture-reliability fix is claimed.",
                             "The original Parallels ETW losses and drain timeouts, B5 receive overflow, and complex installer chains remain unresolved.",
                             "DPI measurements and any simulated scaling are reported separately in native screenshots."])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["capture", "final", "tools"])
    parser.add_argument("--source", required=True)
    parser.add_argument("--zip-sha256")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--tools-sha256")
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--desktop-summary", type=Path)
    parser.add_argument("--capture-summary", type=Path)
    parser.add_argument("--zip", type=Path)
    args = parser.parse_args()
    if args.mode == "tools":
        if args.directory is None or args.tools_sha256 is None:
            parser.error("tools requires --directory and --tools-sha256")
        verify_tools(args.directory, args.source, args.tools_sha256)
        return 0
    if args.zip_sha256 is None or args.output is None:
        parser.error("capture and final require --zip-sha256 and --output")
    if args.mode == "capture":
        if args.directory is None:
            parser.error("capture requires --directory")
        result = capture(args.directory, args.source, args.zip_sha256)
        try:
            build = PACKAGE.verify(args.zip)
            result["core_sha256"] = build["core_sha256"]
            result["accepted"] = result["accepted"] and build["source_sha"] == args.source and PACKAGE.BASE.sha(args.zip) == args.zip_sha256
        except (OSError, ValueError, TypeError, KeyError, zipfile.BadZipFile):
            result["accepted"] = False
    else:
        try:
            build = PACKAGE.verify(args.zip)
            if PACKAGE.BASE.sha(args.zip) != args.zip_sha256:
                build = {}
        except (OSError, ValueError, TypeError, KeyError, zipfile.BadZipFile):
            build = {}
        try:
            needs = json.loads(os.environ.get("STAGE_A_NEEDS", "{}"))
        except ValueError:
            needs = {}
        result = final(needs, read(args.desktop_summary), read(args.capture_summary), build, args.source, args.zip_sha256)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 0 if result["accepted"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
