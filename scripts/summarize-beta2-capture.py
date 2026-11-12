"""Bounded, fail-closed ABBA summary using the unchanged independent oracle."""
import argparse
import importlib.util
import json
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("pair_summary", Path(__file__).with_name("summarize-capture-loss-pair.py"))
PAIR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PAIR)
ORDER = ["r1/baseline", "r1/candidate", "r2/candidate", "r2/baseline"]


def continuation_reason(sample):
    code = sample.get("capture_exit_code")
    if type(code) is not int or code != 0:
        return "prior_collector_exit_abnormal_or_unverified"
    if sample.get("all_owned_processes_exited") is not True:
        return "prior_owned_process_termination_unverified"
    if sample.get("workload_complete") is not True:
        return "prior_independent_workload_or_installer_exit_unverified"
    return None


def summarize(plan, execution, cases):
    valid_plan = plan.get("protocol") == "contain-beta2-comparison-1" and plan.get("order") == ORDER \
        and plan.get("maximum_native_captures") == 4 and plan.get("retries") == 0 \
        and plan.get("background_files") == 12000 and plan.get("profile") is False
    complete = execution.get("attempted") == ORDER and execution.get("not_run") == [] and execution.get("stop_reason") is None
    bounded = [("stream", "quota_bytes"), ("handoff", "record_capacity"), ("handoff", "byte_capacity"),
               ("follow_installer", "candidate_record_limit"), ("follow_installer", "candidate_byte_limit"),
               ("follow_installer", "resource_limit")]
    comparisons = []
    for round_name in ["r1", "r2"]:
        baseline, candidate = cases.get(round_name + "/baseline", {}), cases.get(round_name + "/candidate", {})
        comparable = baseline.get("workload_complete") is True and candidate.get("workload_complete") is True \
            and baseline.get("background", {}).get("files") == candidate.get("background", {}).get("files") == 12000 \
            and all(baseline.get(layer, {}).get(key) is not None and baseline[layer][key] == candidate.get(layer, {}).get(key)
                    for layer, key in bounded)
        comparisons.append(dict(round=round_name, comparable=comparable, candidate_accepted=candidate.get("accepted") is True,
                                source_events_lost_before=baseline.get("counters", {}).get("etw_events_lost"),
                                source_events_lost_after=candidate.get("counters", {}).get("etw_events_lost")))
    accepted = valid_plan and complete and all(row["comparable"] and row["candidate_accepted"] for row in comparisons)
    return dict(schema_version=1, protocol="contain-beta2-comparison-result-1", measurement_kind="real_etw", same_runner=True,
                plan_valid=valid_plan, complete=complete, accepted=accepted, maximum_native_captures=4, retries=0,
                order=ORDER, package_identity=plan.get("package_identity"), cases=cases, comparisons=comparisons,
                source_loss_improvement_claim=accepted and all(type(row["source_events_lost_before"]) is int
                    and type(row["source_events_lost_after"]) is int and row["source_events_lost_before"] > row["source_events_lost_after"]
                    for row in comparisons),
                limitations=["Same syscall workload, not identical incidental ETW stream.",
                             "GitHub hosted Windows runner is not the user's original Parallels environment.",
                             "Zero losses in both versions does not establish a source-loss improvement or historical root cause."])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--directory", type=Path)
    mode.add_argument("--sample-directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.sample_directory is not None:
        if args.output is None:
            parser.error("--sample-directory requires --output")
        # Preserve an oracle failure without treating an abnormal collector or
        # incomplete independent workload as safe to begin another sample.
        result = PAIR.summarize(args.sample_directory)
        result["continuation_stop_reason"] = continuation_reason(result)
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        return 0
    plan = PAIR.read(args.directory / "public/run-plan.json")
    execution = PAIR.read(args.directory / "public/execution.json")
    result = summarize(plan, execution, {name: PAIR.summarize(args.directory / name) for name in ORDER})
    (args.directory / "public/comparison.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return 0 if result["accepted"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
