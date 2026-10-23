"""Dependency policy regressions: reject drift without banning fuzz-only tools."""
import runpy
import unittest
from pathlib import Path

shared_drift = runpy.run_path(str(Path(__file__).with_name("check-fuzz-lock.py")))["shared_drift"]


def graph(*packages):
    return {"package": [
        {"name": name, "version": version, "source": source}
        for name, version, source in packages
    ]}


class LockPolicyTests(unittest.TestCase):
    def test_matching_versions_and_fuzz_only_crates_are_allowed(self):
        self.assertEqual(shared_drift(
            graph(("parser", "1.0.0", "registry")),
            graph(("parser", "1.0.0", "registry"), ("fuzzer", "0.4.13", "registry")),
        ), [])

    def test_newer_and_older_shared_versions_are_rejected(self):
        for version in ["0.9.0", "1.1.0"]:
            self.assertEqual(len(shared_drift(
                graph(("parser", "1.0.0", "registry")),
                graph(("parser", version, "registry")),
            )), 1)

    def test_multiple_production_generations_allow_a_matching_subset(self):
        self.assertEqual(shared_drift(
            graph(("windows", "0.57.0", "registry"), ("windows", "0.61.0", "registry")),
            graph(("windows", "0.57.0", "registry")),
        ), [])

    def test_local_patched_dependency_versions_are_compared(self):
        self.assertEqual(len(shared_drift(
            graph(("decoder", "1.2.0", None)),
            graph(("decoder", "1.3.0", None)),
        )), 1)


if __name__ == "__main__":
    unittest.main()
