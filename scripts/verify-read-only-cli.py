"""Exercise the native Windows CLI on the three production synthetic fixtures.

Generate inputs with `cargo run -p contain-offline --bin read-only-examples
--locked -- <fixtures>`. Each command opens a SQLite backup in a new process.
Full command output stays in --output. Only text matching the reviewed examples
and a fixed-field summary are copied to --public-output; never upload --output.
This is synthetic CLI integration, not live ETW or installer acceptance.
"""
import argparse
from contextlib import closing
import importlib.util
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import tempfile


CASES = ("finished", "historical", "interrupted")
BIRTH = 134350000000000001
INSTALLER = r"C:\synthetic\setup.exe"
ROOT = r"C:\synthetic\observed"
RESOURCE = ROOT + r"\recorded.tmp"
REPO = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("views", Path(__file__).with_name("v04-verify-views.py"))
VIEWS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VIEWS)

# These are fixed source literals, never values taken from a database or stderr.
SAFE_CHECK_MESSAGES = frozenset({
    "invalid JSON envelope",
    "unknown synthetic case",
    "unexpected fixture identity",
    "unexpected fixture scope",
    "unexpected fixture time",
    "unexpected fixture states",
    "unexpected fixture sources",
    "raw fixture records differ",
    "missing synthetic declaration",
    "installer instance or exact decimal birth differs",
    "unexpected fixture event IDs or ordering",
    "recording state or quality differs",
    "recorded ending differs",
    "normalized operation count differs",
    "state difference count differs",
    "reviewed example sections differ",
    "missing reviewed text section",
    "text history page differs",
    "invalid source revision",
    "unexpected fixture resource or operation",
    "event FILETIME is not exact decimal text",
    "object generations differ",
    "source evidence was promoted or erased",
    "actor, name and completion evidence dimensions differ",
    "native completion evidence differs",
    "actor evidence or exact decimal birth differs",
    "unavailable loss counter changed",
    "state difference promoted to ownership",
    "CLI text differs from reviewed synthetic example",
    "CLI inspect differs from production fixture readback",
    "list differs from reopened inspect",
    "text list differs from reopened inspect",
    "text diff lacks ownership boundary",
    "CLI views disagree; see local view report",
    "database reopen changed inspect",
    "legacy missing provenance was invented",
    "recorded name promoted to verified current path",
    "name provenance or exact observation time differs",
    "CLI command failed; see local command logs",
    "text diff promotes unknown state",
    "JSON history page differs",
    "text explanation omits event ID",
    "text explanation changes actor attribution",
    "legacy name provenance is not unavailable in text explanation",
})


def safe_failure(error):
    """Publish a check identifier without publishing arbitrary exception text."""
    if isinstance(error, ValueError) and str(error) in SAFE_CHECK_MESSAGES:
        message = str(error)
        code = re.sub(r"[^a-z0-9]+", "_", message.lower()).strip("_")
    elif isinstance(error, json.JSONDecodeError):
        code, message = "invalid_json_document", "Expected one valid JSON document."
    elif isinstance(error, sqlite3.Error):
        code, message = "database_access_failed", "Synthetic database backup or read failed."
    elif isinstance(error, subprocess.SubprocessError):
        code, message = "cli_process_failed", "CLI process could not complete."
    elif isinstance(error, OSError):
        code, message = "file_or_process_access_failed", "File or process access failed."
    elif isinstance(error, (KeyError, TypeError)):
        code, message = "unexpected_response_shape", "A required response field or type differs."
    else:
        code, message = "unclassified_validation_failure", "Validation failed; details remain local."
    return dict(failure="local_validation_failed", check_code=code, check_message=message)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def document(stdout, kind):
    """json.loads rejects progress messages, concatenated documents and suffixes."""
    result = json.loads(stdout)
    require(isinstance(result, dict) and result.get("schema_version") == 3
            and result.get("kind") == kind and "data" in result, "invalid JSON envelope")
    return result["data"]


def validate_fixture(case, capture):
    """Check fixture identity and the distinctions exercised by these scenarios.

    Identity checks alone are NOT a publication filter. Public text additionally
    has to match the reviewed repository examples exactly.
    """
    require(case in CASES, "unknown synthetic case")
    app = "synthetic-" + case
    require(capture["id"] == app and capture["name"] == "Synthetic " + case,
            "unexpected fixture identity")
    require(capture["installer"] == INSTALLER and capture["watch_roots"] == [ROOT]
            and capture["registry_key"] is None, "unexpected fixture scope")
    require(capture["started_at"] == "2026-10-01T00:00:00Z", "unexpected fixture time")
    require(capture["registry"] == [] and capture["inventory"] == [], "unexpected fixture states")
    backend = capture["backend"]
    require((backend["etw_process"], backend["etw_file"], backend["etw_registry"])
            == ("active", "active", "not_requested"), "unexpected fixture sources")
    require(backend["stream"]["persisted"] == 3, "raw fixture records differ")
    require(any("SYNTHETIC model fixture" in warning for warning in capture["warnings"]),
            "missing synthetic declaration")
    processes = capture["processes"]
    require(len(processes) == 1 and processes[0]["pid"] == 100
            and processes[0]["creation_time"] == str(BIRTH)
            and processes[0]["image"] == INSTALLER and processes[0]["confidence"] == "Certain",
            "installer instance or exact decimal birth differs")
    events = capture["events"]
    require([event["id"] for event in events] == [f"{app}-event-{i}" for i in range(3)],
            "unexpected fixture event IDs or ordering")
    for i, event in enumerate(events):
        raw = event["raw"]
        unknown = case == "interrupted" or (case == "historical" and i == 1)
        require(event["resource"] == RESOURCE and event["event_type"] == "file"
                and event["operation"] == "write_requested", "unexpected fixture resource or operation")
        require(event["timestamp_ticks"] == str(BIRTH + 10 + i), "event FILETIME is not exact decimal text")
        require(raw["file_object"] == f"synthetic-object-{i}"
                and raw["object_generation"] == f"synthetic-generation-{i}", "object generations differ")
        require(event["confidence"] == ("Unknown" if unknown else "High")
                and event["success"] is (None if unknown else True)
                and event["state_validated"] is False, "source evidence was promoted or erased")
        require(event["dimensions"] == dict(actor="Unknown" if unknown else "High",
                operation="Unknown" if unknown else "High", resource="Unknown" if case == "interrupted" else "High"),
                "actor, name and completion evidence dimensions differ")
        missing_actor = case == "historical" and i == 1
        require(raw["status"] == (None if missing_actor else 0), "native completion evidence differs")
        require(event["evidence"]["pid"] == (None if missing_actor else 100)
                and event["evidence"]["process_creation_time"] == (None if missing_actor else str(BIRTH)),
                "actor evidence or exact decimal birth differs")
        name = raw.get("file_name")
        if case == "historical" and i == 2:
            require(name is None, "legacy missing provenance was invented")
        else:
            historical = case == "historical" and i == 0
            require(name is not None and name["current_path_verified"] is False,
                    "recorded name promoted to verified current path")
            require(name["path_source"] == ("file_object_cache" if historical else "supplied_FileName")
                    and name["observed_ticks"] == str(BIRTH + (1 if historical else 10 + i))
                    and name["source_event_id"] == ("synthetic-earlier-open" if historical else event["id"]),
                    "name provenance or exact observation time differs")
    interrupted = case == "interrupted"
    require(capture["capture_state"] == ("finalizing" if interrupted else "finished")
            and capture["quality"]["level"] == ("Incomplete" if interrupted else "Degraded"),
            "recording state or quality differs")
    require(capture["finished_at"] == ("" if interrupted else "2026-10-01T00:00:02Z")
            and capture["exit_code"] == (None if interrupted else 0), "recorded ending differs")
    for key in ("etw_events_lost", "etw_buffers_lost"):
        require(backend[key] == (None if interrupted else 0), "unavailable loss counter changed")
    require(len(capture["operations"]) == (0 if interrupted else 3), "normalized operation count differs")
    require(len(capture["files"]) == (0 if interrupted else 1), "state difference count differs")
    if not interrupted:
        require(capture["files"][0]["path"] == RESOURCE
                and capture["files"][0]["confidence"] == "Unknown", "state difference promoted to ownership")


def golden_sections(case):
    text = (REPO / "docs" / "examples" / f"read-only-{case}.txt").read_text(encoding="utf-8")
    pieces = re.split(r"\n(INSPECT|DETAIL|HISTORY|EXPLAIN)\n", text)
    require(len(pieces) == 9, "reviewed example sections differ")
    return {pieces[i]: pieces[i + 1] for i in range(1, 9, 2)}


def reviewed_text(case, sections):
    expected = golden_sections(case)
    for name, text in sections.items():
        require(text.replace("\r\n", "\n") == expected[name], "CLI text differs from reviewed synthetic example")
    require(set(sections) == set(expected), "missing reviewed text section")
    return ("SYNTHETIC — actual Windows contain.exe on generated model evidence; "
            "not live ETW or an installer run.\n\n"
            + "\n".join(name + "\n" + sections[name] for name in expected))


def check_page_text(text, expected, all_ids):
    # IDs appear again as source-event references, so count the history row marker.
    found = re.findall(r"\| event=([^ ]+) \|", text)
    require(found == expected and set(found) <= set(all_ids), "text history page differs")


def run_case(case, args, local, public):
    app = "synthetic-" + case
    commands = []
    with tempfile.TemporaryDirectory(prefix="contain-read-only-cli-") as temporary:
        database = Path(temporary) / "synthetic.db"
        source_path = (args.fixtures / f"{case}.db").resolve()
        with closing(sqlite3.connect(source_path.as_uri() + "?mode=ro", uri=True)) as source:
            with closing(sqlite3.connect(database)) as target:
                source.backup(target)

        def command(label, kind, *arguments, as_json=False):
            argv = [str(args.contain.resolve()), "--db", str(database), kind, *arguments]
            if as_json:
                argv.append("--json")
            result = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", timeout=120)
            commands.append(dict(command=["contain.exe", "--db", "<synthetic-backup>", kind, *arguments]
                                 + (["--json"] if as_json else []), exit_code=result.returncode))
            (local / f"{label}.stdout.txt").write_text(result.stdout, encoding="utf-8")
            (local / f"{label}.stderr.txt").write_text(result.stderr, encoding="utf-8")
            (local / "commands.json").write_text(json.dumps(commands, indent=2) + "\n", encoding="utf-8")
            require(result.returncode == 0, "CLI command failed; see local command logs")
            if as_json:
                data = document(result.stdout, kind)
                (args.output / "json" / f"{case}-{label}.json").write_text(result.stdout, encoding="utf-8")
                return data
            return result.stdout

        capture = command("inspect", "inspect", app, as_json=True)
        validate_fixture(case, capture)
        expected = document((args.fixtures / f"{case}.json").read_text(encoding="utf-8"), "inspect")
        require(capture == expected, "CLI inspect differs from production fixture readback")
        listed = command("list", "list", as_json=True)
        require(listed == [dict(id=app, name="Synthetic " + case, started_at=capture["started_at"])],
                "list differs from reopened inspect")
        require(command("list-text", "list") == f"{'Synthetic ' + case:<28} {capture['started_at']}  {app}\n",
                "text list differs from reopened inspect")
        sections = dict(INSPECT=command("inspect-text", "inspect", app),
                        DETAIL=command("detail-text", "inspect", app, "--verbose"),
                        HISTORY=command("history-text", "history", app))
        history = command("history", "history", app, as_json=True)
        diff = command("diff", "diff", app, as_json=True)
        diff_text = command("diff-text", "diff", app)
        require("State differences are not ownership." in diff_text, "text diff lacks ownership boundary")
        if case != "interrupted":
            require(f"FILE modified {RESOURCE} [Unknown]" in diff_text, "text diff promotes unknown state")
        pages = []
        ids = [event["id"] for event in capture["events"]]
        for offset in (0, 2, 4):
            expected = capture["events"][offset:offset + 2]
            page = command(f"page-{offset}", "history", app, "--offset", str(offset), "--limit", "2", as_json=True)
            require(page["app_id"] == app and page["events"] == expected, "JSON history page differs")
            pages.extend(page["events"])
            page_text = command(f"page-{offset}-text", "history", app, "--offset", str(offset), "--limit", "2")
            check_page_text(page_text, [event["id"] for event in expected], ids)
        explanations = {}
        for i, event in enumerate(capture["events"]):
            explanations[event["id"]] = command(f"explain-{i}", "explain", event["id"], as_json=True)
            text = command(f"explain-{i}-text", "explain", event["id"])
            require(text.startswith("Event " + event["id"] + "\n"), "text explanation omits event ID")
            require('Source actor attribution: "' + event["confidence"] + '"' in text,
                    "text explanation changes actor attribution")
            if case == "historical" and i == 2:
                require("Name source: unavailable; name observation FILETIME: unavailable; source event: unavailable" in text,
                        "legacy name provenance is not unavailable in text explanation")
            if i == 0:
                sections["EXPLAIN"] = text
        agreement = VIEWS.verify(capture, history, diff, pages, explanations)
        (local / "view-agreement.json").write_text(json.dumps(agreement, indent=2) + "\n", encoding="utf-8")
        require(agreement["acceptance_pass"], "CLI views disagree; see local view report")
        require(command("inspect-reopen", "inspect", app, as_json=True) == capture, "database reopen changed inspect")
        # Equality with reviewed text is the publication boundary, including warnings.
        text = reviewed_text(case, sections)
        (public / f"{case}.txt").write_text(text, encoding="utf-8")
    return dict(case=case, passed=True, commands=len(commands), events=3, explanations=3,
                history_pages=3, reopened=True, reviewed_text_match=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--contain", type=Path, required=True)
    parser.add_argument("--fixtures", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--public-output", type=Path, required=True)
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("native Windows is required; unit tests do not establish CLI integration")
    # New directories preserve every failed attempt and keep public/private separate.
    output = args.output.resolve()
    public = args.public_output.resolve()
    if output == public or output in public.parents or public in output.parents:
        parser.error("output and public-output must be separate, non-nested directories")
    args.output.mkdir(parents=True, exist_ok=False)
    args.public_output.mkdir(parents=True, exist_ok=False)
    (args.output / "json").mkdir()
    revision = subprocess.run(["git", "rev-parse", "HEAD"], cwd=REPO, capture_output=True, text=True, check=True).stdout.strip()
    require(re.fullmatch(r"[0-9a-f]{40}", revision) is not None, "invalid source revision")
    summary = dict(schema_version=1, source_sha=revision, scope="synthetic_native_windows_cli",
                   live_etw=False, cases=[])
    for case in CASES:
        local = args.output / case
        local.mkdir()
        try:
            summary["cases"].append(run_case(case, args, local, args.public_output))
        except (OSError, ValueError, KeyError, TypeError, sqlite3.Error, subprocess.SubprocessError) as error:
            (local / "failure.txt").write_text(f"{type(error).__name__}: {error}\n", encoding="utf-8")
            # Error strings can contain private paths: never copy them to public logs.
            summary["cases"].append(dict(case=case, passed=False, **safe_failure(error)))
    summary["passed"] = sum(case["passed"] for case in summary["cases"])
    summary["failed"] = len(CASES) - summary["passed"]
    summary["ignored"] = 0
    summary["acceptance_pass"] = summary["failed"] == 0
    text = json.dumps(summary, indent=2) + "\n"
    (args.public_output / "summary.json").write_text(text, encoding="utf-8")
    (args.output / "summary.json").write_text(text, encoding="utf-8")
    print(text, end="")
    return 0 if summary["acceptance_pass"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
