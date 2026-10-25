"""One independent, bounded WPR observation of the frozen eight-file diagnostic.

No elevation, installation, retries, symbol downloads, cleanup, global trace stop,
workload changes or acceptance runs. An ETL file is not proof that stacks exist.
"""
import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import traceback
import uuid


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def save(path, value):
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".new")
    with temporary.open("x", encoding="utf-8") as output:
        json.dump(value, output, indent=2, allow_nan=False)
        output.write("\n")
    temporary.replace(path)


def utc():
    return datetime.now(timezone.utc).isoformat()


def module(path):
    spec = importlib.util.spec_from_file_location("frozen_write_entry", path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def verified_plan(path, expected):
    if sha(path) != expected:
        raise ValueError("Frozen wrapper plan SHA mismatch; nothing launched")
    plan = read(path)
    if plan.get("protocol") != "contain-write-stack-entry-1" or plan.get("formal_acceptance") is not False:
        raise ValueError("Unsupported wrapper plan or acceptance request")
    if plan.get("observer_seconds") != 60 or plan.get("profile_name") != "ContainWrite":
        raise ValueError("This entry requires the reviewed 60-second ContainWrite observation")
    entries = plan.get("inputs", [])
    required = {"wrapper", "wpr", "xperf", "profile", "child_plan", "child_runner", "python"}
    if {entry["name"] for entry in entries} != required or len(entries) != len(required):
        raise ValueError("Missing or duplicate frozen wrapper input")
    observed = []
    for entry in entries:
        actual = sha(entry["path"])
        if actual != entry["sha256"]:
            raise ValueError(f"Frozen input mismatch: {entry['name']}; nothing launched")
        observed.append(dict(name=entry["name"], path=entry["path"], sha256=actual))
    inputs = {entry["name"]: Path(entry["path"]) for entry in observed}
    if inputs["wrapper"].resolve() != Path(__file__).resolve():
        raise ValueError("Running wrapper is not the frozen input")
    if inputs["python"].resolve() != Path(sys.executable).resolve():
        raise ValueError("Running interpreter is not the frozen input")
    child_entry = module(inputs["child_runner"])
    child, _, child_identity = child_entry.verified_plan(inputs["child_plan"], sha(inputs["child_plan"]))
    if child.get("runtime_commit") != plan.get("runtime_commit"):
        raise ValueError("Wrapper runtime differs from frozen child")
    runner = next(entry for entry in child_identity if entry["name"] == "runner")
    if Path(runner["path"]).resolve() != inputs["child_runner"].resolve():
        raise ValueError("Frozen child runner differs from frozen child plan")
    return plan, inputs, observed, child, child_identity


def derived_child_plan(original, directory):
    # Do not modify the old plan, eight inputs, code, arguments or workload.
    value = dict(original)
    capture_parent = directory / "capture"
    capture_parent.mkdir(exist_ok=False)
    value["output_parent"] = str(capture_parent)
    path = directory / "derived-child-plan.json"
    with path.open("x", encoding="utf-8") as output:
        json.dump(value, output, indent=2, allow_nan=False)
        output.write("\n")
    differences = {key for key in set(original) | set(value) if original.get(key) != value.get(key)}
    if differences != {"output_parent"}:
        raise ValueError("Derived child plan must change output_parent only")
    return path, capture_parent


def text_log(path):
    data = path.read_bytes()
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        return data.decode("utf-16", errors="replace")
    if b"\x00" in data[:100]:
        return data.decode("utf-16-le", errors="replace")
    return data.decode("utf-8", errors="replace")


class Commands:
    def __init__(self, directory, record, checkpoint):
        self.directory, self.record, self.checkpoint = directory, record, checkpoint

    def run(self, label, command, timeout=10):
        item = dict(label=label, command=[str(value) for value in command], started_utc=utc(),
                    timeout_seconds=timeout, exit=None, timed_out=False)
        self.record["commands"].append(item)
        self.checkpoint()
        started = time.monotonic()
        stdout_path, stderr_path = (self.directory / f"{label}.{name}.log" for name in ("stdout", "stderr"))
        try:
            with stdout_path.open("xb") as stdout, stderr_path.open("xb") as stderr:
                result = subprocess.run(item["command"], stdout=stdout, stderr=stderr, timeout=timeout,
                                        creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
            item["exit"] = result.returncode
        except subprocess.TimeoutExpired:
            # This only terminates this WPR/xperf controller process. A trace
            # may remain active; never interpret a controller timeout as stop.
            item["timed_out"] = True
        except BaseException as error:
            item["error"] = f"{type(error).__name__}: {error}"
            raise
        finally:
            item["elapsed_seconds"] = time.monotonic() - started
            item["finished_utc"] = utc()
            item["outputs"] = [dict(path=str(path), bytes=path.stat().st_size, sha256=sha(path))
                               for path in (stdout_path, stderr_path) if path.exists()]
            self.checkpoint()
        return item, text_log(stdout_path) + "\n" + text_log(stderr_path)


def collector_name(instance):
    return f"WPR_initiated_{instance}_WPR System Collector"


def owned_status(item, output, instance):
    # WPR returns success even for an absent instance. The uniquely named
    # actual collector must appear in the instance-specific status output.
    exact_name = collector_name(instance).casefold()
    values = {line.split(":", 1)[-1].strip().strip('"').casefold() for line in output.splitlines()}
    return (item.get("exit") == 0 and not item.get("timed_out") and
            exact_name in values and
            "wpr is not recording" not in output.casefold())


def required_file_settings(output):
    # WPR's detailed status lists System Keywords and System Stacks as
    # individual values. Fail closed if this installation does not expose them;
    # a supported profile alone cannot establish actual active settings.
    required = {"FileIO", "FileIOInit", "Filename", "Loader", "ProcessThread", "FileCreate", "FileCleanup",
                "FileClose", "FileWrite", "FileSetInformation", "FileDelete", "FileRename", "FileOpEnd"}
    observed = {line.strip() for line in output.splitlines()}
    return sorted(required - observed)


def artifact_file(path, stopped):
    value = dict(path=str(path), bytes=None, sha256=None, observer_stop_confirmed=stopped)
    try:
        value["bytes"] = path.stat().st_size
        if stopped:
            value["sha256"] = sha(path)
    except OSError as error:
        value["read_error"] = str(error)
    return value


def read_child_state(capture_parent):
    attempts = list(capture_parent.glob("admin-*/attempt.json"))
    if len(attempts) > 1:
        raise RuntimeError("More than one child attempt appeared; no second launch is allowed")
    if not attempts:
        return None
    state = read(attempts[0])
    return dict(attempt_path=str(attempts[0]), stage=state.get("stage"), status=state.get("status"),
                capture_pid=state.get("capture_pid"), capture_exit=state.get("capture_exit"),
                child_still_running_at_entry_finish=state.get("child_still_running_at_entry_finish"),
                fixture_container=state.get("fixture_container"), truth_directory=state.get("truth_directory"))


def stop_owned(record, inputs, commands, checkpoint, label="observer-stop"):
    if not record["observer_start_succeeded"] or record["observer_stop_check_attempted"]:
        return
    record["observer_stop_check_attempted"] = True
    checkpoint()
    instance = record["instance"]
    status, output = commands.run(label + "-status", [inputs["wpr"], "-status", "profiles", "collectors",
                                                     "-details", "-instancename", instance], timeout=5)
    record["owned_status_before_stop"] = owned_status(status, output, instance)
    if not record["owned_status_before_stop"]:
        record["observer_recovery_required"] = True
        record["observer_stop_refusal"] = "Named collector not verified; no stop command issued"
        checkpoint()
        return
    collector, collector_output = commands.run(label + "-collector", [inputs["xperf"], "-loggers", collector_name(instance)], timeout=5)
    record["collector_counters_before_stop"] = ("recorded_requires_offline_review" if
        owned_status(collector, collector_output, instance)
        else "unavailable_exact_query_failed_or_returned_no_collector")
    record["observer_stop_attempted"] = True
    record["observer_stop_started_utc"] = utc()
    checkpoint()
    result, _ = commands.run(label, record["recovery_stop_command"], timeout=20)
    record["observer_stop_exit"] = result["exit"]
    record["observer_stop_controller_timed_out"] = result["timed_out"]
    record["observer_stop_command_succeeded"] = result["exit"] == 0 and not result["timed_out"]
    after, after_output = commands.run(label + "-after-status", [inputs["wpr"], "-status", "profiles", "collectors",
                                                                 "-details", "-instancename", instance], timeout=5)
    record["observer_absent_after_stop"] = (after["exit"] == 0 and not after["timed_out"] and
        "wpr is not recording" in after_output.casefold() and collector_name(instance).casefold() not in after_output.casefold())
    record["observer_stop_succeeded"] = record["observer_stop_command_succeeded"] and record["observer_absent_after_stop"]
    record["observer_recovery_required"] = not record["observer_absent_after_stop"]
    record["observer_start_to_stop_return_seconds"] = time.monotonic() - record["observer_start_monotonic"]
    checkpoint()


def launch(plan, inputs, before, child_plan, child_before):
    directory = Path(plan["output_parent"]) / ("stack-" + datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:12])
    directory.mkdir(parents=False, exist_ok=False)
    trace_temp = directory / "wpr-temp"
    trace_temp.mkdir(exist_ok=False)
    instance = "ContainWrite-" + uuid.uuid4().hex
    trace = directory / "independent-file-stacks.etl"
    record = dict(protocol=plan["protocol"], runtime_commit=plan["runtime_commit"],
                  observer_source_commit=plan.get("observer_source_commit"), formal_acceptance=False,
                  started_utc=utc(), stage="prepare", status="running", artifact_directory=str(directory),
                  input_identity_before=before, input_identity_after=None, child_input_identity_before=child_before,
                  child_input_identity_after=None, commands=[], instance=instance,
                  expected_collector=collector_name(instance), observer_start_succeeded=False,
                  observer_stop_attempted=False, observer_stop_check_attempted=False,
                  observer_stop_succeeded=False, observer_recovery_required=False,
                  observer_active_poll_limit_seconds=60, observer_control_budget_seconds=35,
                  observer_limit_semantics="stop_requested_by_60s_plus_bounded_control_calls_not_OS_stop_guarantee",
                  trace_path=str(trace), trace_stack_evidence="configured_not_verified", trace_validity=None,
                  source_loss=None, stack_count=None, symbols_verified=False, symbol_downloads=False,
                  child_pid=None, child_exit=None, child_state=None, child_still_running=None,
                  recovery_stop_command=[str(inputs["wpr"]), "-stop", str(trace), "Contain minimal file observation",
                                         "-skipPdbGen", "-instancename", instance])
    def checkpoint(stage=None):
        if stage is not None:
            record["stage"] = stage
            print(f"Stage: {stage}; private stack artifacts: {directory}", flush=True)
        save(directory / "attempt.json", record)
    checkpoint("prepare")
    commands = Commands(directory, record, checkpoint)
    process = None
    observer_start = None
    try:
        derived, capture_parent = derived_child_plan(child_plan, directory)
        record["derived_child_plan"] = dict(path=str(derived), sha256=sha(derived), changed_fields=["output_parent"])
        # A read-only inventory documents foreign loggers; it never stops them.
        inventory, _ = commands.run("loggers-before", [inputs["xperf"], "-loggers"])
        if inventory["exit"] != 0:
            raise RuntimeError("Logger inventory unavailable; no observer or child launched")
        profile, _ = commands.run("profile-details", [inputs["wpr"], "-profiledetails",
                                                      str(inputs["profile"]) + "!ContainWrite", "-filemode"])
        if profile["exit"] != 0:
            raise RuntimeError("WPR profile rejected; no observer or child launched")
        absent, output = commands.run("instance-before", [inputs["wpr"], "-status", "profiles", "collectors",
                                                           "-details", "-instancename", instance])
        if (absent["exit"] != 0 or absent["timed_out"] or "wpr is not recording" not in output.casefold()
                or collector_name(instance).casefold() in output.casefold()):
            raise RuntimeError("Unique instance precondition unavailable or already active; nothing launched")
        checkpoint("observer-start")
        observer_start = time.monotonic()
        record["observer_start_monotonic"] = observer_start
        record["observer_recovery_required"] = True
        record["observer_start_state"] = "command_pending_or_uncertain"
        checkpoint()
        started, _ = commands.run("observer-start", [inputs["wpr"], "-start", str(inputs["profile"]) + "!ContainWrite",
                                                     "-filemode", "-recordtempto", trace_temp, "-instancename", instance], timeout=10)
        record["observer_start_succeeded"] = started["exit"] == 0 and not started["timed_out"]
        record["observer_start_state"] = "command_succeeded" if record["observer_start_succeeded"] else "failed_or_uncertain"
        if not record["observer_start_succeeded"]:
            record["observer_recovery_required"] = True
            raise RuntimeError("Named observer start failed or uncertain; no fixture launched, preserve exact-instance evidence")
        active, output = commands.run("observer-active", [inputs["wpr"], "-status", "profiles", "collectors",
                                                            "-details", "-instancename", instance], timeout=5)
        record["owned_status_after_start"] = owned_status(active, output, instance)
        if not record["owned_status_after_start"]:
            raise RuntimeError("Named actual collector not verified; no fixture launched")
        record["active_file_settings_missing"] = required_file_settings(output)
        if record["active_file_settings_missing"]:
            raise RuntimeError("Actual file keywords/stacks not all present in detailed status; no fixture launched")
        record["child_command"] = [str(inputs["python"]), str(inputs["child_runner"]), "--plan", str(derived),
                                   "--plan-sha256", sha(derived)]
        checkpoint("capture")
        with (directory / "child.stdout.log").open("xb") as stdout, (directory / "child.stderr.log").open("xb") as stderr:
            process = subprocess.Popen(record["child_command"], stdout=stdout, stderr=stderr,
                                       creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
            record["child_pid"] = process.pid
            checkpoint("capture-running")
            pulse = time.monotonic()
            while process.poll() is None:
                record["child_state"] = read_child_state(capture_parent)
                elapsed = time.monotonic() - observer_start
                if record["child_state"] and record["child_state"]["capture_exit"] is not None:
                    record["observer_stop_reason"] = "capture_finished_before_offline_export_and_analysis"
                    break
                if elapsed >= plan["observer_seconds"]:
                    record["observer_stop_reason"] = "observer_time_limit_child_not_killed"
                    raise TimeoutError("Observer reached fixed 60s limit; stop only owned trace, retain running child state")
                if time.monotonic() - pulse >= 2:
                    stage = (record["child_state"] or {}).get("stage", "preparing")
                    print(f"Independent observer running: {elapsed:.1f}s; child stage: {stage}; PID {process.pid}", flush=True)
                    checkpoint()
                    pulse = time.monotonic()
                time.sleep(0.2)
            if "observer_stop_reason" not in record:
                record["observer_stop_reason"] = "child_exited"
            checkpoint("observer-stop")
            stop_owned(record, inputs, commands, checkpoint)
            if not record["observer_stop_succeeded"]:
                raise RuntimeError("Owned observer stop not confirmed; no retry, exact-instance recovery required")
            checkpoint("child-offline-analysis")
            offline_started = time.monotonic()
            pulse = offline_started
            while process.poll() is None:
                record["child_state"] = read_child_state(capture_parent)
                if time.monotonic() - offline_started >= 180:
                    raise TimeoutError("Child offline completion limit reached; preserve state, no termination or retry")
                if time.monotonic() - pulse >= 2:
                    stage = (record["child_state"] or {}).get("stage", "unknown")
                    print(f"Observer stopped; child offline stage: {stage}; PID {process.pid}", flush=True)
                    checkpoint()
                    pulse = time.monotonic()
                time.sleep(0.2)
            record["child_exit"] = process.returncode
        if process.returncode != 0:
            raise RuntimeError(f"Frozen diagnostic exited {process.returncode}; all evidence retained")
        if not trace.is_file() or trace.stat().st_size == 0:
            raise RuntimeError("No nonempty independent ETL; stacks and correlation remain unavailable")
        record["status"] = "completed_pending_stack_and_event_review"
        checkpoint("completed")
    except KeyboardInterrupt:
        record["status"], record["error"] = "interrupted", "KeyboardInterrupt; child may continue. No retry or cleanup."
    except Exception as error:
        record["status"] = "failed"
        record["error"] = f"{type(error).__name__}: {error}"
        record["traceback"] = traceback.format_exc()
    finally:
        try:
            stop_owned(record, inputs, commands, checkpoint, label="final-owned-stop")
        except BaseException as error:
            record["observer_recovery_required"] = True
            record["stop_finalization_error"] = f"{type(error).__name__}: {error}"
        if observer_start is not None:
            record["observer_entry_elapsed_seconds"] = time.monotonic() - observer_start
        if process is not None:
            record["child_exit"] = process.poll()
            record["child_still_running"] = process.returncode is None
        try:
            record["child_state"] = read_child_state(directory / "capture")
            commands.run("loggers-after", [inputs["xperf"], "-loggers"])
        except BaseException as error:
            record["final_inventory_error"] = f"{type(error).__name__}: {error}"
        if trace.is_file():
            record["trace_file"] = artifact_file(trace, record["observer_stop_succeeded"])
        record["retained_observer_files"] = [artifact_file(path, record["observer_stop_succeeded"])
            for path in trace_temp.rglob("*") if path.is_file()]
        sized_files = record["retained_observer_files"] + ([record["trace_file"]] if "trace_file" in record else [])
        record["retained_observer_size_complete"] = (record["observer_stop_succeeded"] and
            all(item["bytes"] is not None for item in sized_files))
        record["retained_observer_bytes"] = (sum(item["bytes"] for item in sized_files)
                                             if record["retained_observer_size_complete"] else None)
        record["disk_bound_scope"] = "256MB_sequential_source_ETL_only_merge_and_temp_sizes_recorded_separately"
        record["finished_utc"] = utc()
        checkpoint(record["stage"])
        print(f"Private stack artifacts: {directory}\nFormal acceptance: not executed; actual stacks require offline review.", flush=True)
        if record["observer_recovery_required"]:
            print("Observer recovery required. Use only the exact named command in attempt.json after inspecting its status; never generic stop/cancel.", flush=True)
    return directory, record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--plan-sha256", required=True)
    parser.add_argument("--preflight", action="store_true", help="Read/hash fixed inputs only; never start any collector or child")
    args = parser.parse_args()
    plan, inputs, before, child_plan, child_before = verified_plan(args.plan, args.plan_sha256)
    if args.preflight:
        print(json.dumps(dict(status="frozen_inputs_verified", runtime_commit=plan["runtime_commit"],
                              wrapper_inputs=len(before), child_inputs=len(child_before), capture_executed=False,
                              actual_stacks_verified=False, formal_acceptance=False)))
        return 0
    if os.name != "nt" or not ctypes.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("Use the existing administrator window; this entry cannot elevate")
    directory, record = launch(plan, inputs, before, child_plan, child_before)
    try:
        _, _, record["input_identity_after"], _, record["child_input_identity_after"] = verified_plan(args.plan, args.plan_sha256)
    except (OSError, ValueError) as error:
        record["post_identity_error"] = str(error)
        record["status"] = "failed"
    save(directory / "attempt.json", record)
    return 0 if record["status"] == "completed_pending_stack_and_event_review" else 1


if __name__ == "__main__":
    raise SystemExit(main())
