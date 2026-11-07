"""Prepare or upload the exact accepted Beta 10 ZIP to an existing draft.

No build, repack, overwrite, tag creation, or release publication is performed.
The immutable Beta 9 desktop checker is loaded in a separate module instance so
all its native, source, quality and HTML gates remain part of this delivery.
"""
import argparse
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


DESKTOP = load("beta10_preserved_desktop_guards", "upload-beta9-assets.py")
PAIR = load("beta10_pair_guards", "summarize-capture-loss-pair.py")
MATRIX = load("beta10_matrix_guards", "summarize-capture-loss-matrix.py")
REPO = DESKTOP.REPO
TAG = "v0.3.2-beta.10"
VERSION = "0.3.2-beta.10"
ZIP = "contain-0.3.2-beta.10-windows-x64.zip"
BASELINE_SOURCE = "8e68ccfe63389e1533235e253eb59329f15be898"
BASELINE_ZIP_SHA = "dee47046f01c6a2c31c5bda3a3a3bcf3fc2a1603eb18b35464f68cde04ec7612"
LOSS_NAME = "capture-loss-pair-evidence"
NAMES = (*DESKTOP.NAMES, LOSS_NAME)
EXTENDED_NAME = DESKTOP.EXTENDED_NAME
require, digest = DESKTOP.require, DESKTOP.digest


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", f"repos/{REPO}/{path}"], text=True))


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def verify_extended(directory, source, run):
    """The Beta 9 extended contract plus the actual quality.ps1 Bench gate."""
    require(type(run) is int and run > 0, "Required extended-quality run identity is missing")
    names = ("baseline-benchmark.json", "candidate-benchmark.json", "benchmark-identity.json", "fuzz-identity.json")
    values = {name: read(directory / name) for name in names}
    identity, fuzz = values["benchmark-identity.json"], values["fuzz-identity.json"]
    require(identity.get("source_sha") == source and identity.get("baseline_sha") == BASELINE_SOURCE
            and type(identity.get("schema_version")) is int and identity["schema_version"] == 1
            and identity.get("accepted") is True and identity.get("same_runner") is True,
            "Extended benchmark source/baseline was not accepted")
    require(type(fuzz.get("schema_version")) is int and fuzz["schema_version"] == 1
            and fuzz.get("source_sha") == source and fuzz.get("accepted") is True
            and fuzz.get("toolchain") == "nightly-2026-11-05"
            and type(fuzz.get("seconds_per_target")) is int and fuzz["seconds_per_target"] == 30
            and fuzz.get("targets") == ["event_json", "lifetime_sequences", "completion_correlation"],
            "Required actual Windows fuzz smoke did not pass")
    definitions = []
    gate = dict(mode="Bench", completed=True, passed=True, label="bench", exit_code=0)
    allowed = {"schema_version", "measurement_kind", "etw_validation", "etw_providers_started", "sqlite_measured",
        "profile", "source_commit", "results", "compiler", "os", "os_version", "arch", "fixture_generation_timed",
        "assertions_timed", "notes", "working_tree_clean", "quality_gate"}
    for name, revision in (("baseline-benchmark.json", BASELINE_SOURCE), ("candidate-benchmark.json", source)):
        value = values[name]
        require(set(value) <= allowed, "Unreviewed benchmark host metadata must remain private")
        require(value.get("schema_version") == 1 and value.get("measurement_kind") == "synthetic_in_memory"
                and value.get("etw_validation") == "not_performed" and value.get("etw_providers_started") is False
                and value.get("sqlite_measured") is False and value.get("profile") == "release"
                and value.get("source_commit") == revision and value.get("working_tree_clean") is True
                and value.get("fixture_generation_timed") is False and value.get("assertions_timed") is True,
                "Benchmark scope/profile/source differs")
        actual_gate = value.get("quality_gate", {})
        require(set(actual_gate) == set(gate) and all(type(actual_gate[k]) is type(v) and actual_gate[k] == v
                for k, v in gate.items()), "Actual quality.ps1 Bench gate was not accepted")
        rows = value.get("results")
        require(isinstance(rows, list) and len(rows) == 4 and all(isinstance(row, dict) for row in rows)
                and [row.get("name") for row in rows] == list(DESKTOP.BENCH_CASES), "Required benchmark case definitions differ")
        cases = []
        for row in rows:
            samples = row.get("sample_ns_per_work_item")
            require(type(row.get("measured_samples")) is int and row["measured_samples"] == 7
                    and type(row.get("warmup_samples")) is int and row["warmup_samples"] == 2
                    and type(row.get("work_items_per_sample")) is int and row["work_items_per_sample"] == 20000
                    and type(row.get("input_records_per_sample")) is int and row["input_records_per_sample"] in (0, 20000)
                    and type(row.get("validated_checksum")) is int and row["validated_checksum"] > 0
                    and isinstance(samples, list) and len(samples) == 7
                    and all(type(x) in (int, float) and math.isfinite(x) and x > 0 for x in samples),
                    "Seven validated benchmark samples are required")
            cases.append({k: row[k] for k in ("name", "work_items_per_sample", "input_records_per_sample",
                "measured_samples", "warmup_samples", "validated_checksum")})
        definitions.append(cases)
    require(definitions[0] == definitions[1], "Baseline and candidate benchmark denominators or checksums differ")
    return dict(ci_run_url=f"https://github.com/{REPO}/actions/runs/{run}", source_sha=source,
        files=[dict(filename=name, sha256=digest(directory / name)) for name in names],
        benchmark_identity=identity, baseline_benchmark=values["baseline-benchmark.json"],
        candidate_benchmark=values["candidate-benchmark.json"], fuzz_identity=fuzz)


# Configure only this isolated checker instance. The historical Beta 9 files,
# constants and tests stay unchanged on disk and in their own module instances.
DESKTOP.TAG, DESKTOP.VERSION, DESKTOP.ZIP = TAG, VERSION, ZIP
DESKTOP.BASELINE_SOURCE = BASELINE_SOURCE
DESKTOP.verify_extended = verify_extended


def number(value):
    return type(value) is int and value >= 0


def true_fields(value, fields, reason):
    require(all(value.get(key) is True for key in fields), reason)


def verify_loss_capture(value, oracle, revision, *, candidate=False):
    """Preserved per-capture bounds and evidence checks, shared by both protocols."""
    true_fields(value, ("available", "all_owned_processes_exited", "cold_query_exact_session", "capture_finished", "workload_complete"),
                "Loss pair capture, cold readback or full workload is unconfirmed")
    require(value.get("capture_exit_code") == 0 and type(value.get("capture_exit_code")) is int
            and value.get("independent_exit_code") == 0 and type(value.get("independent_exit_code")) is int,
            "Loss pair collector or independent fixture did not exit normally")
    terminal = value.get("terminal", {})
    require(terminal.get("stage") == "finished" and type(terminal.get("installer_exit_code")) is int
            and terminal["installer_exit_code"] == 0, "Loss pair installer did not exit normally")
    true_fields(terminal, ("installer_exited", "capture_finished", "data_saved"), "Loss pair did not finish and save")
    true_fields(value.get("providers_active", {}), ("etw_process", "etw_file"), "Loss pair real ETW providers were unavailable")
    require(value.get("stats", {}).get("drain_timed_out") is False, "Loss pair descendant drain timed out")
    p, h, s, f = (value.get(key, {}) for key in ("pipeline", "handoff", "stream", "follow_installer"))
    require(h.get("record_capacity") == 4096 and h.get("byte_capacity") == 16777216
            and s.get("quota_bytes") == 67108864 and s.get("error_present") is False
            and f.get("candidate_record_limit") == 2048 and f.get("candidate_byte_limit") == 8388608
            and f.get("resource_limit") == 4096 and f.get("direct_children_only") is True and f.get("baseline_available") is False,
            "Loss pair memory, storage or follow evidence boundaries differ")
    for item, maximum in ((p.get("queue_high_water"), 4096), (h.get("high_water_records"), 4096),
            (h.get("high_water_bytes"), 16777216), (s.get("committed_bytes"), 67108864),
            (f.get("candidate_high_water_records"), 2048), (f.get("candidate_high_water_bytes"), 8388608)):
        require(number(item) and item <= maximum, "Loss pair exceeded an existing resource bound")
    computed = PAIR.accounting(value)
    require(value.get("accounting") == computed and all(computed.values())
            and all(item is True for item in value["accounting"].values()),
            "Loss pair accounting does not conserve records")
    require(all(number(f.get(k)) for k in ("candidates", "admitted", "excluded_other", "unresolved", "candidate_dropped"))
            and f["candidates"] == sum(f[k] for k in ("admitted", "excluded_other", "unresolved", "candidate_dropped")),
            "Loss pair follow filtering and candidate losses do not conserve records")
    background = value.get("background", {})
    require(type(background.get("files")) is int and background["files"] == 12000
            and background.get("successful_operations") == dict(write_requested=12000, rename_requested=12000, delete_requested=12000)
            and background.get("complete") is True and type(background.get("exit_code")) is int and background["exit_code"] == 0
            and type(background.get("signal_exit_code")) is int and background["signal_exit_code"] == 0
            and type(background.get("falsely_attributed_events")) is int and background["falsely_attributed_events"] == 0,
            "Loss pair background truth is incomplete or wrongly attributed")
    true_fields(background.get("window", {}), ("all_noise_within_root_lifetime", "all_noise_within_capture", "spans_parent_write", "spans_child_write"),
                "Loss pair background and target capture windows do not overlap")
    targets = value.get("targets", [])
    require(isinstance(targets, list) and [row.get("label") for row in targets] == ["parent", "child", "unrelated"], "Loss pair fixed target set differs")
    for target in targets:
        require(type(target.get("independent_successful_writes")) is int and target["independent_successful_writes"] == 1
                and target.get("content_verified") is True and number(target.get("retained_write_requests"))
                and number(target.get("promoted_write_requests"))
                and target["promoted_write_requests"] <= target["retained_write_requests"], "Loss pair independent target write truth differs")
    require(targets[2]["promoted_write_requests"] == 0, "Loss pair unrelated target was attributed to installer")
    require(oracle.get("schema_version") == 1 and oracle.get("protocol") == "contain-follow-installer-experiment-1"
            and oracle.get("source_sha") == revision and oracle.get("synthetic") is False
            and oracle.get("controlled_fixture") is True and value.get("experiment_accepted") is oracle.get("accepted"),
            "Loss pair independent oracle source or outcome differs")
    checks = oracle.get("checks", [])
    require([row.get("name") for row in checks] == PAIR.ORACLE.PLAN
            and all(row.get("status") in ("passed", "failed", "not_run") for row in checks)
            and all(type(oracle.get(key)) is int and oracle[key] == sum(row["status"] == status for row in checks)
                for key, status in (("passed", "passed"), ("failed", "failed"), ("not_run", "not_run")))
            and oracle.get("accepted") is all(row["status"] == "passed" for row in checks),
            "Loss pair independent oracle checks are incomplete")
    require(oracle.get("follow_counters") == f and all(oracle.get("storage_counters", {}).get(k) == s.get(k)
            for k in ("quota_bytes", "committed_bytes", "persisted", "failed", "quota_dropped")),
            "Loss pair oracle counters differ from the measured capture")
    if candidate:
        true_fields(value, ("accepted", "experiment_accepted"), "Loss pair candidate did not pass unchanged evidence gates")
        require(all(type(value["counters"].get(k)) is int and value["counters"][k] == 0
                    for k in ("dropped_events", "etw_events_lost", "etw_buffers_lost", "decode_errors", "context_losses"))
                and value["follow_installer"]["candidate_dropped"] == 0
                and value["stream"]["failed"] == value["stream"]["quota_dropped"] == 0,
                "Loss pair candidate retains source, candidate or raw-record losses")
        for target, rule in zip(value["targets"][:2], ("InstallerPid", "DescendantProcess")):
            require(target.get("verified_actor_and_interval") is True and target["retained_write_requests"] > 0
                    and target["promoted_write_requests"] > 0, "Loss pair target write is not retained with verified actor evidence")
            observations = [row for row in oracle.get("observations", []) if row.get("label") == target["label"]]
            require(len(observations) == 1 and observations[0].get("source") == "EtwFile"
                    and observations[0].get("confidence") == observations[0].get("actor_confidence") == "High"
                    and observations[0].get("rule") == rule and observations[0].get("operation") == "write_requested"
                    and observations[0].get("state_validated") is False and observations[0].get("current_path_verified") is False
                    and observations[0].get("event_id") in target.get("event_ids", []),
                    "Loss pair independent target event evidence differs")


def verify_loss_pair(directory, source, zip_sha, fixture_sha):
    """Read historical pair evidence only; formal releases require the matrix."""
    paths = ("public/capture-loss-pair.json", "beta9/public/follow-experiment-summary.json",
             "candidate/public/follow-experiment-summary.json")
    pair, baseline_oracle, candidate_oracle = [read(directory / name) for name in paths]
    require(type(pair.get("schema_version")) is int and pair["schema_version"] == 1
            and pair.get("protocol") == "contain-capture-loss-pair-1"
            and pair.get("measurement_kind") == "real_etw" and pair.get("order") == ["beta9", "candidate"]
            and type(pair.get("retries")) is int and pair["retries"] == 0
            and type(pair.get("maximum_native_captures")) is int and pair["maximum_native_captures"] == 2,
            "Loss pair protocol or bounded native attempt plan differs")
    true_fields(pair, ("same_runner", "comparable", "accepted", "improvement_claim"), "Loss pair acceptance or comparable improvement is missing")
    identity = pair.get("package_identity", {})
    require(identity.get("candidate") == dict(version=VERSION, source_sha=source, zip_sha256=zip_sha)
            and identity.get("baseline") == dict(version="0.3.2-beta.9", source_sha=BASELINE_SOURCE, zip_sha256=BASELINE_ZIP_SHA)
            and identity.get("fixture_source") == source and identity.get("same_fixture_sha256") == fixture_sha
            and re.fullmatch(r"[0-9a-f]{64}", fixture_sha), "Loss pair source, ZIP, fixture or immutable Beta 9 identity differs")
    verify_loss_capture(pair.get("baseline", {}), baseline_oracle, BASELINE_SOURCE)
    verify_loss_capture(pair.get("candidate", {}), candidate_oracle, source, candidate=True)
    before, after = (pair[name]["counters"]["dropped_events"] for name in ("baseline", "candidate"))
    require(number(before) and before > after and after == 0
            and pair.get("application_loss_change") == dict(before=before, after=0, reduction=before, reduction_percent=100.0),
            "Loss pair improvement is not a reproducible zero-loss candidate result")
    return dict(files=[dict(path=name, sha256=digest(directory / name)) for name in paths], summary=pair)


def verify_loss_matrix(directory, source, zip_sha, fixture_sha):
    """Require every fixed attempt, including both strict candidate captures."""
    paths = ["public/run-plan.json", "public/execution.json", "public/capture-loss-matrix.json"]
    require(all((directory / name).is_file() for name in paths),
            "Formal Beta 10 delivery requires the complete six-capture matrix; historical pairs are diagnostic only")
    plan, execution, matrix = [read(directory / name) for name in paths]
    require(all(isinstance(value, dict) for value in (plan, execution, matrix))
            and MATRIX.plan_valid(plan), "Loss matrix immutable package plan differs")
    identity = plan.get("package_identity", {})
    require(identity.get("candidate") == dict(version=VERSION, source_sha=source, zip_sha256=zip_sha)
            and identity.get("fixture_source") == source and identity.get("same_fixture_sha256") == fixture_sha
            and re.fullmatch(r"[0-9a-f]{64}", fixture_sha), "Loss matrix accepted source, ZIP or fixture differs")
    require(execution == dict(attempted=MATRIX.ORDER, not_run=[], stop_reason=None),
            "Loss matrix has missing, duplicate, retried or uncompleted predeclared attempts")
    require(matrix.get("order") == MATRIX.ORDER and isinstance(matrix.get("cases"), list)
            and [case.get("id") for case in matrix["cases"]] == MATRIX.ORDER,
            "Loss matrix attempt order or complete case set differs")
    for case in matrix["cases"]:
        name = case["id"]
        package = name.split("/")[1]
        revision = identity["baseline" if package == "beta9" else package]["source_sha"]
        require(case.get("package") == package and case.get("source_sha") == revision
                and case.get("attempted") is True and case.get("identity_replay_available") is True,
                "Loss matrix case source or actual diagnostic attempt is unconfirmed")
        oracle_path = name + "/public/follow-experiment-summary.json"
        replay_path = name + "/public/follow-identity-diagnostic.json"
        require(all((directory / path).is_file() for path in (oracle_path, replay_path)),
                "Loss matrix per-attempt oracle or identity replay is missing")
        oracle, replay = read(directory / oracle_path), read(directory / replay_path)
        require(isinstance(replay, dict) and replay.get("complete") is True,
                "Loss matrix identity replay did not read the saved session completely")
        verify_loss_capture(case.get("summary", {}), oracle, revision, candidate=package == "candidate")
        paths.extend((oracle_path, replay_path))
    computed = MATRIX.aggregate(plan, execution, matrix["cases"], digest(directory / paths[0]))
    # JSON equality distinguishes true from 1. A cached true flag or selected
    # first green candidate cannot substitute for the complete recomputation.
    encode = lambda value: json.dumps(value, sort_keys=True, allow_nan=False, separators=(",", ":"))
    require(encode(matrix) == encode(computed), "Loss matrix claims, comparisons or plan hash differ from all six attempts")
    true_fields(matrix, ("same_runner", "completed", "comparable", "identity_replays_complete", "candidates_passed",
                         "accepted", "improvement_claim"), "Both fixed candidate captures must pass unchanged evidence gates")
    return dict(files=[dict(path=name, sha256=digest(directory / name)) for name in paths], summary=matrix)


def verify_artifacts(directory, source, run, zip_sha, extended_run):
    identity, files = DESKTOP.verify_artifacts(directory, source, run, zip_sha, extended_run)
    import zipfile
    with zipfile.ZipFile(files[ZIP]) as archive:
        build = json.loads(archive.read("BUILD.json"))
    fixture_sha = build["controlled_fixture"]["sha256"]
    identity["capture_loss_matrix"] = verify_loss_matrix(directory / LOSS_NAME, source, zip_sha, fixture_sha)
    identity["capture_loss_matrix"]["ci_run_url"] = f"https://github.com/{REPO}/actions/runs/{run}"
    identity["scope"] += " Same-runner immutable Beta 9 / previous diagnostic / candidate real-ETW comparison; all six fixed attempts retained and both candidate captures passed unchanged evidence gates."
    files["CAPTURE-LOSS-MATRIX.json"] = directory / LOSS_NAME / "public/capture-loss-matrix.json"
    files["CAPTURE-LOSS-PLAN.json"] = directory / LOSS_NAME / "public/run-plan.json"
    files["CAPTURE-LOSS-EXECUTION.json"] = directory / LOSS_NAME / "public/execution.json"
    return identity, files


def accepted_remote(source, run):
    value = api(f"actions/runs/{run}")
    require(value["head_sha"] == source and value["event"] == "push" and value["conclusion"] == "success"
            and value["status"] == "completed" and value["run_attempt"] == 1
            and value["path"] == ".github/workflows/ci.yml" and value["head_repository"]["full_name"] == REPO,
            "Accepted CI identity differs")
    values = api(f"actions/runs/{run}/artifacts?per_page=100")["artifacts"]
    selected = {}
    for name in NAMES:
        matches = [item for item in values if item["name"] == name and item["expired"] is False]
        require(len(matches) == 1, "Accepted artifact missing or ambiguous")
        selected[name] = matches[0]["id"]
    return selected


def accepted_extended_remote(source, run):
    value = api(f"actions/runs/{run}")
    # This Beta 10 task explicitly authorizes the existing extended workflow's
    # manual entrypoint on the isolated fix branch. Do not rerun the same core
    # merely to manufacture a push event. All source/artifact/quality gates stay
    # identical; main() verifies merged-main ancestry for this same source.
    event_allowed = value["event"] == "push" or (
        value["event"] == "workflow_dispatch" and value.get("head_branch") == "fix/beta10-capture-loss")
    require(value["head_sha"] == source and event_allowed and value["conclusion"] == "success"
            and value["status"] == "completed" and value["run_attempt"] == 1
            and value["path"] == ".github/workflows/follow-installer-quality.yml"
            and value["head_repository"]["full_name"] == REPO, "Extended-quality CI identity differs")
    values = api(f"actions/runs/{run}/artifacts?per_page=100")["artifacts"]
    matches = [item for item in values if item["name"] == EXTENDED_NAME and item["expired"] is False]
    require(len(matches) == 1, "Extended-quality artifact missing or ambiguous")
    return matches[0]["id"]


def existing_draft(source, release_id=None):
    if release_id is None:
        matches = [item for item in api("releases?per_page=100") if item["tag_name"] == TAG]
        require(len(matches) == 1, "Expected exactly one existing Beta 10 draft")
        release_id = matches[0]["id"]
    value = api(f"releases/{release_id}")
    require(value["tag_name"] == TAG and value["target_commitish"] == source
            and value["draft"] is True and value["prerelease"] is True,
            "Only the exact existing Beta 10 draft may receive assets")
    return value


def upload_assets(output, root, source, release_id):
    """Never replace an existing asset; bind and read back each uploaded ID."""
    expected = {path.name for path in output.iterdir()}
    current = existing_draft(source, release_id)
    require(not current["assets"], "Existing draft assets must not be overwritten or reused")
    readback = root / "readback"
    readback.mkdir()
    for path in sorted(output.iterdir()):
        current = existing_draft(source, release_id)
        require(path.name not in {asset["name"] for asset in current["assets"]}, "An asset appeared before upload; refusing overwrite")
        subprocess.check_call(["gh", "release", "upload", TAG, str(path), "--repo", REPO])
        current = existing_draft(source, release_id)
        selected = [asset for asset in current["assets"] if asset["name"] == path.name]
        require(len(selected) == 1 and selected[0].get("state") == "uploaded" and selected[0].get("size") == path.stat().st_size,
                "Uploaded asset identity, state or byte count differs")
        asset_id = selected[0]["id"]
        destination = readback / path.name
        with destination.open("wb") as stream:
            subprocess.check_call(["gh", "api", f"repos/{REPO}/releases/assets/{asset_id}",
                "--header", "Accept: application/octet-stream"], stdout=stream)
        require(digest(destination) == digest(path), "Uploaded asset readback differs")
        after = existing_draft(source, release_id)
        require(any(asset["id"] == asset_id and asset["name"] == path.name for asset in after["assets"]), "Uploaded asset ID changed during readback")
    require({asset["name"] for asset in existing_draft(source, release_id)["assets"]} == expected, "Final asset set differs")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--run", required=True, type=int)
    parser.add_argument("--extended-run", required=True, type=int)
    parser.add_argument("--zip-sha256", required=True)
    parser.add_argument("--local-input", type=Path)
    parser.add_argument("--output", type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--upload", action="store_true")
    mode.add_argument("--verify-artifacts-only", action="store_true")
    args = parser.parse_args()
    require(re.fullmatch(r"[0-9a-f]{40}", args.source) and re.fullmatch(r"[0-9a-f]{64}", args.zip_sha256)
            and args.run > 0 and args.extended_run > 0, "Invalid accepted identity inputs")
    require(not args.verify_artifacts_only or args.local_input, "Local verification needs downloaded artifacts")
    with tempfile.TemporaryDirectory(prefix="contain-beta10-") as temporary:
        root = Path(temporary)
        selected = accepted_remote(args.source, args.run) if not args.verify_artifacts_only else None
        extended_id = accepted_extended_remote(args.source, args.extended_run) if not args.verify_artifacts_only else None
        directory = args.local_input or root / "input"
        if not args.local_input:
            for name in NAMES:
                subprocess.check_call(["gh", "run", "download", str(args.run), "--name", name, "--dir", str(directory / name), "--repo", REPO])
            subprocess.check_call(["gh", "run", "download", str(args.extended_run), "--name", EXTENDED_NAME, "--dir", str(directory / EXTENDED_NAME), "--repo", REPO])
        identity, files = verify_artifacts(directory, args.source, args.run, args.zip_sha256, args.extended_run)
        if selected:
            require(accepted_remote(args.source, args.run) == selected and accepted_extended_remote(args.source, args.extended_run) == extended_id,
                    "Artifact identities changed")
        output = args.output or root / "assets"
        output.mkdir(parents=True, exist_ok=False)
        for name, path in files.items():
            shutil.copyfile(path, output / name)
        (output / "BUILD-IDENTITY.json").write_text(json.dumps(identity, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        (output / "SHA256SUMS.txt").write_text("".join(digest(path) + "  " + path.name + "\n" for path in sorted(output.iterdir())), encoding="utf-8")
        if args.verify_artifacts_only:
            print("Local immutable Beta 10 artifacts and loss comparison verified; no upload.")
            return
        ref = api(f"git/ref/tags/{TAG}")["object"]
        for _ in range(8):
            if ref["type"] != "tag":
                break
            ref = api(f"git/tags/{ref['sha']}")["object"]
        require(ref["type"] == "commit" and ref["sha"] == args.source, "Immutable Beta 10 tag differs")
        compare = api(f"compare/{args.source}...main")
        require(compare["status"] in ("ahead", "identical") and compare["merge_base_commit"]["sha"] == args.source, "Accepted source is not merged into main")
        release_id = existing_draft(args.source)["id"]
        if not args.upload:
            print("Exact Beta 10 tag, draft, CI and local artifact hashes verified; no upload.")
            return
        upload_assets(output, root, args.source, release_id)
        print("All accepted Beta 10 assets uploaded and read back; release remains a draft.")


if __name__ == "__main__":
    main()
