"""Measure the existing demo capture process; no elevation or workload changes."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import time

SPEC = importlib.util.spec_from_file_location("demo_matrix_metrics", Path(__file__).with_name("v04-matrix.py"))
MATRIX = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MATRIX)


def measure(command, directory, database):
    if not isinstance(command, list) or not command or not all(isinstance(item, str) for item in command):
        raise ValueError("capture command must be a nonempty string array")
    report = dict(schema_version=1, raw_clock_enabled=bool(os.environ.get("CONTAIN_ETW_RAW_CLOCK_PATH")),
                  capture_exit=None, elapsed_seconds=None, sqlite_bytes=None,
                  parent_cpu_seconds=None, parent_peak_working_set_bytes=None,
                  parent_sampled_peak_private_bytes=None, parent_io=None,
                  sampled_db_peak_bytes=None, sampled_wal_peak_bytes=None, filesystem_samples=0,
                  sampling_errors=[],
                  measurement="Capture process wall through exit; excludes build/export/scoring. CPU, memory and logical I/O cover only Contain, not children or kernel ETW. Private memory and DB/WAL peaks sampled every 20ms; I/O is not physical disk traffic. Diagnostic on/off must be compared separately.")
    # Never replace an old measurement or console log, including on failed launch.
    with (directory / "measurement.json").open("x", encoding="utf-8") as output, \
            (directory / "capture.stdout.log").open("xb") as stdout, \
            (directory / "capture.stderr.log").open("xb") as stderr:
        start = time.perf_counter()
        metrics = None
        try:
            with subprocess.Popen(command, stdout=stdout, stderr=stderr,
                                  creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)) as process:
                if os.name == "nt":
                    metrics = MATRIX.ProcessMetrics(process.pid)
                while process.poll() is None:
                    if metrics:
                        metrics.sample()
                    for key, path in (("sampled_db_peak_bytes", database),
                                      ("sampled_wal_peak_bytes", Path(str(database) + "-wal"))):
                        report[key] = max(report[key] or 0, MATRIX.sampled_size(path))
                    report["filesystem_samples"] += 1
                    time.sleep(0.02)
                if metrics:
                    metrics.sample()
                    report.update(metrics.values)
                    report["sampling_errors"] = sorted(metrics.errors)
                else:
                    report["sampling_errors"] = ["Windows process counters unavailable"]
                report["capture_exit"] = process.returncode
        except OSError as error:
            report["sampling_errors"].append(type(error).__name__ + ": " + str(error))
            raise
        finally:
            report["elapsed_seconds"] = time.perf_counter() - start
            try:
                report["sqlite_bytes"] = database.stat().st_size
            except FileNotFoundError:
                report["sqlite_bytes"] = None
            except OSError as error:
                report["sqlite_bytes"] = None
                report["sampling_errors"].append("final_database_stat: " + type(error).__name__ + ": " + str(error))
            if metrics:
                metrics.close()
            json.dump(report, output, indent=2, allow_nan=False)
            output.write("\n")
    return report["capture_exit"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--command", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--database", type=Path, required=True)
    args = parser.parse_args()
    raise SystemExit(measure(json.loads(args.command.read_text(encoding="utf-8-sig")), args.output, args.database))
