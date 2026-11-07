"""Aggregate every predeclared Windows capture; never select a passing retry.

The existing per-capture independent oracle remains unchanged. Historical
versions may fail its attribution/loss checks, but every comparison still needs
complete independent workload, resource, exit and saved-session evidence.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PAIR = load("matrix_preserved_pair", "summarize-capture-loss-pair.py")
PROTOCOL = "contain-capture-loss-matrix-1"
BASELINE_SOURCE = "8e68ccfe63389e1533235e253eb59329f15be898"
BASELINE_ZIP_SHA = "dee47046f01c6a2c31c5bda3a3a3bcf3fc2a1603eb18b35464f68cde04ec7612"
PREVIOUS_SOURCE = "3c2e06871d28acd199ba1813605afecc9074738d"
PREVIOUS_ZIP_SHA = "41a3e4e3e8dc0b75e245eb3306ed8e5c6294d04c5fc72d4cc9110d3512ef99e7"
ORDER = ["r1/beta9", "r1/previous", "r1/candidate", "r2/candidate", "r2/previous", "r2/beta9"]
RESOURCE_POLICY = "unchanged existing queue limits; evidence quota 64 MiB; settle 1500 ms; maximum descendant drain 10000 ms"


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def plan_valid(plan):
    identity = plan.get("package_identity", {})
    candidate = identity.get("candidate", {})
    diagnostic = identity.get("identity_diagnostic", {})
    return (type(plan.get("schema_version")) is int and plan["schema_version"] == 1 and plan.get("protocol") == PROTOCOL
        and plan.get("order") == ORDER and type(plan.get("maximum_native_captures")) is int
        and plan["maximum_native_captures"] == 6 and type(plan.get("retries")) is int and plan["retries"] == 0
        and identity.get("baseline") == dict(version="0.3.2-beta.9", source_sha=BASELINE_SOURCE, zip_sha256=BASELINE_ZIP_SHA)
        and identity.get("previous") == dict(version="0.3.2-beta.10", source_sha=PREVIOUS_SOURCE, zip_sha256=PREVIOUS_ZIP_SHA)
        and candidate.get("version") == "0.3.2-beta.10"
        and re.fullmatch(r"[0-9a-f]{40}", str(candidate.get("source_sha"))) is not None
        and re.fullmatch(r"[0-9a-f]{64}", str(candidate.get("zip_sha256"))) is not None
        and identity.get("fixture_source") == candidate.get("source_sha")
        and re.fullmatch(r"[0-9a-f]{64}", str(identity.get("same_fixture_sha256"))) is not None
        and diagnostic.get("source_sha") == candidate.get("source_sha")
        and re.fullmatch(r"[0-9a-f]{64}", str(diagnostic.get("sha256"))) is not None
        and type(identity.get("background_files")) is int and identity["background_files"] == 12000
        and identity.get("resource_policy") == RESOURCE_POLICY)


def zero_losses(value):
    return (all(type(value.get("counters", {}).get(key)) is int and value["counters"][key] == 0
                for key in ("dropped_events", "etw_events_lost", "etw_buffers_lost", "decode_errors", "context_losses"))
        and type(value.get("follow_installer", {}).get("candidate_dropped")) is int
        and value["follow_installer"]["candidate_dropped"] == 0
        and all(type(value.get("stream", {}).get(key)) is int and value["stream"][key] == 0
                for key in ("failed", "quota_dropped")))


def capture_comparable(value):
    """Require fixed work and existing bounds even for failing old versions."""
    try:
        p, h, s, f = (value[key] for key in ("pipeline", "handoff", "stream", "follow_installer"))
        background = value["background"]
        bounds = ((p["queue_high_water"], 4096), (h["high_water_records"], 4096),
            (h["high_water_bytes"], 16777216), (s["committed_bytes"], 67108864),
            (f["candidate_high_water_records"], 2048), (f["candidate_high_water_bytes"], 8388608))
        computed = PAIR.accounting(value)
        return (value.get("available") is True and value.get("workload_complete") is True
            and value.get("all_owned_processes_exited") is True
            and h["record_capacity"] == 4096 and h["byte_capacity"] == 16777216
            and s["quota_bytes"] == 67108864 and s.get("error_present") is False
            and f["candidate_record_limit"] == 2048 and f["candidate_byte_limit"] == 8388608
            and f["resource_limit"] == 4096 and f["direct_children_only"] is True and f["baseline_available"] is False
            and all(type(number) is int and 0 <= number <= maximum for number, maximum in bounds)
            and all(type(f.get(key)) is int and f[key] >= 0
                    for key in ("candidates", "admitted", "excluded_other", "unresolved", "candidate_dropped"))
            and f["candidates"] == sum(f[key] for key in ("admitted", "excluded_other", "unresolved", "candidate_dropped"))
            and value.get("accounting") == computed and all(computed.values())
            and background.get("complete") is True and type(background.get("files")) is int and background["files"] == 12000
            and background.get("successful_operations") == dict(write_requested=12000, rename_requested=12000, delete_requested=12000)
            and background.get("falsely_attributed_events") == 0
            and all(item is True for item in background.get("window", {}).values())
            and [target.get("label") for target in value.get("targets", [])] == ["parent", "child", "unrelated"]
            and value["targets"][2].get("promoted_write_requests") == 0)
    except (KeyError, TypeError, ValueError):
        return False


def aggregate(plan, execution, cases, plan_sha256):
    """Pure aggregation also used by offline release guards and regressions."""
    attempted = execution.get("attempted", [])
    not_run = execution.get("not_run", [])
    complete = (attempted == ORDER and not_run == [] and execution.get("stop_reason") is None
                and [case.get("id") for case in cases] == ORDER
                and all(case.get("attempted") is True for case in cases))
    indexed = {case.get("id"): case.get("summary", {}) for case in cases}
    comparisons = []
    comparable = complete and plan_valid(plan) and all(capture_comparable(case.get("summary", {})) for case in cases)
    for case in cases:
        package = case.get("id", "").split("/")[-1]
        identity = plan.get("package_identity", {}).get("baseline" if package == "beta9" else package, {})
        comparable = comparable and case.get("package") == package and case.get("source_sha") == identity.get("source_sha")
    for round_name in ("r1", "r2"):
        baseline, previous, candidate = (indexed.get(round_name + "/" + name, {})
                                         for name in ("beta9", "previous", "candidate"))
        before = PAIR.compare(baseline, candidate, plan.get("package_identity", {}))
        prior = PAIR.compare(previous, candidate, plan.get("package_identity", {}))
        comparisons.append(dict(round=round_name, beta9_to_candidate=before,
                                previous_to_candidate=prior))
        comparable = comparable and before["comparable"] and prior["comparable"]
    # Replays explain failures as well as successful oracle selections. They do
    # not replace ETW, independent syscall truth or the original acceptance gate.
    replays_complete = all(case.get("identity_replay_available") is True for case in cases)
    candidates_passed = all(indexed.get(name, {}).get("accepted") is True
                            and indexed.get(name, {}).get("experiment_accepted") is True
                            and zero_losses(indexed.get(name, {}))
                            for name in ("r1/candidate", "r2/candidate"))
    accepted = comparable and replays_complete and candidates_passed
    return dict(schema_version=1, protocol=PROTOCOL, measurement_kind="real_etw", same_runner=True,
        maximum_native_captures=6, retries=0, order=ORDER, plan_sha256=plan_sha256,
        package_identity=plan.get("package_identity", {}), attempted=attempted, not_run=not_run,
        stop_reason=execution.get("stop_reason"), completed=complete, comparable=comparable,
        identity_replays_complete=replays_complete, candidates_passed=candidates_passed,
        accepted=accepted,
        improvement_claim=accepted and all(row["beta9_to_candidate"]["improvement_claim"] for row in comparisons),
        cases=cases, comparisons=comparisons,
        limitations=["Two predeclared sequential rounds on one disposable Windows runner; not the user's Parallels VM.",
            "All six attempts are retained. Both candidate captures must pass; results are not averaged into acceptance.",
            "Background syscall counts are equal; incidental OS events and ETW arrival timing can differ.",
            "Current-source offline identity replay is diagnostic; frozen-package cold explanations and real ETW remain the evidence gates.",
            "This does not identify the cause of the user's private historical session."])


def summarize(directory):
    directory = Path(directory)
    plan_path = directory / "public/run-plan.json"
    plan, execution = read(plan_path), read(directory / "public/execution.json")
    cases = []
    for name in ORDER:
        package = name.split("/")[1]
        identity = plan.get("package_identity", {}).get("baseline" if package == "beta9" else package, {})
        try:
            replay = read(directory / name / "public/follow-identity-diagnostic.json")
            replay_available = isinstance(replay, dict) and replay.get("complete") is True
        except (OSError, ValueError, TypeError):
            replay_available = False
        cases.append(dict(id=name, package=package, source_sha=identity.get("source_sha"),
            attempted=name in execution.get("attempted", []), identity_replay_available=replay_available,
            summary=PAIR.summarize(directory / name)))
    return aggregate(plan, execution, cases, hashlib.sha256(plan_path.read_bytes()).hexdigest())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", required=True, type=Path)
    args = parser.parse_args()
    result = summarize(args.directory)
    destination = args.directory / "public/capture-loss-matrix.json"
    destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return 0 if result["accepted"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
