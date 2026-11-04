"""Compare CLI views on a SQLite backup; never migrate the supplied database.

All events and state rows are compared. --max-explain is an explicit diagnostic
sample; only the default exhaustive check may report complete=True.
"""
import argparse
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import subprocess
import tempfile


def verify(capture, history, diff, pages, explanations):
    issues = []
    def check(condition, message):
        if not condition:
            issues.append(message)

    events = capture["events"]
    ids = [event["id"] for event in events]
    check(len(ids) == len(set(ids)), "inspect contains duplicate event IDs")
    check(history.get("app_id") == capture["id"], "history application differs")
    check(diff.get("app_id") == capture["id"], "diff application differs")
    check(history.get("events") == events, "history differs from inspect")
    check(pages == events, "paged history differs from inspect")
    for field in ("files", "registry", "inventory", "operations", "quality"):
        check(field in diff and diff[field] == capture[field], f"diff.{field} differs from inspect")
    by_id = {event["id"]: event for event in events}
    roots = {f"process:{process['pid']}:{process['creation_time']}" for process in capture.get("processes", [])
             if process["confidence"] == "Certain" and process.get("creation_time")}
    for event_id, explanation in explanations.items():
        original = by_id.get(event_id)
        check(original is not None, f"explain refers to unknown event {event_id}")
        check(explanation.get("app_id") == capture["id"], f"explain application differs: {event_id}")
        check(explanation.get("event") == original, f"explain event differs: {event_id}")
        check(explanation.get("quality") == capture["quality"], f"explain quality differs: {event_id}")
        expected_operations = [operation for operation in capture["operations"] if event_id in operation["raw_events"]]
        check(explanation.get("operations") == expected_operations, f"explain operations differ: {event_id}")
        check(all(edge in capture["edges"] for edge in explanation.get("edges", [])), f"explain invented graph edge: {event_id}")
        if original and original["event_type"] in ("file", "registry") and original["confidence"] in ("High", "Certain"):
            evidence = original["evidence"]
            check(evidence.get("pid") is not None and evidence.get("process_creation_time") is not None,
                  f"positive source event lacks process instance: {event_id}")
            check(bool(original.get("reason")), f"positive source event lacks explanation: {event_id}")
            reached = {"event:" + event_id}
            pending = list(reached)
            while pending:
                node = pending.pop()
                for edge in explanation.get("edges", []):
                    if edge["to"] == node and edge["confidence"] in ("High", "Certain") and edge["from"] not in reached:
                        reached.add(edge["from"])
                        pending.append(edge["from"])
            check(bool(roots & reached), f"positive source event lacks chain to installer instance: {event_id}")
    complete = set(explanations) == set(ids)
    return dict(schema_version=1, compared_events=len(events), compared_file_states=len(capture["files"]),
                compared_registry_states=len(capture["registry"]), explanations_checked=len(explanations),
                explanations_expected=len(events), complete=complete, checks_pass=not issues,
                acceptance_pass=complete and not issues, issues=issues,
                scope="Agreement within each evidence domain; snapshot states need not equal source-event attribution. Equality does not establish ground truth.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--contain", type=Path, required=True)
    parser.add_argument("--database", type=Path, required=True)
    parser.add_argument("--app", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--page-size", type=int, default=256)
    parser.add_argument("--max-explain", type=int, default=None)
    args = parser.parse_args()
    if not 1 <= args.page_size <= 4096 or (args.max_explain is not None and args.max_explain < 1):
        parser.error("page-size must be 1..4096; max-explain must be positive")
    report = dict(schema_version=1, acceptance_pass=False, complete=False)
    try:
        with tempfile.TemporaryDirectory(prefix="contain-v04-views-") as temporary:
            database = Path(temporary) / "snapshot.db"
            with closing(sqlite3.connect(args.database.resolve().as_uri() + "?mode=ro", uri=True)) as source:
                with closing(sqlite3.connect(database)) as target:
                    source.backup(target)
            def command(kind, *arguments):
                result = subprocess.run([str(args.contain.resolve()), "--db", str(database), kind, *arguments, "--json"],
                                        capture_output=True, text=True, encoding="utf-8", check=True, timeout=120)
                document = json.loads(result.stdout)
                if document.get("schema_version") != 3 or document.get("kind") != kind:
                    raise ValueError(f"invalid {kind} envelope")
                return document["data"]
            capture = command("inspect", args.app)
            history = command("history", args.app)
            diff = command("diff", args.app)
            pages = []
            for offset in range(0, len(capture["events"]) + args.page_size, args.page_size):
                page = command("history", args.app, "--offset", str(offset), "--limit", str(args.page_size))
                if page.get("app_id") != capture["id"]:
                    raise ValueError("paged history application differs")
                pages.extend(page["events"])
                if len(page["events"]) < args.page_size:
                    break
            ordered = sorted(capture["events"], key=lambda event: (event["confidence"] not in ("High", "Certain"), event["id"]))
            if args.max_explain is not None:
                ordered = ordered[:args.max_explain]
            explanations = {event["id"]: command("explain", event["id"]) for event in ordered}
            report = verify(capture, history, diff, pages, explanations)
    except (OSError, ValueError, KeyError, sqlite3.Error, subprocess.SubprocessError) as error:
        report["harness_error"] = str(error)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report))
    return 0 if report.get("acceptance_pass") else 2


if __name__ == "__main__":
    raise SystemExit(main())
