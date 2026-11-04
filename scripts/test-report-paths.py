"""Real Windows directory-identity regression; no capture, no claimed 8.3 emulation.

The short alias comes only from GetShortPathNameW for a directory created by this
test. If this volume has no distinct 8.3 spelling, that case is explicitly skipped.
All other Windows filesystem cases remain mandatory. Paths/file IDs stay private.
"""
import argparse
import ctypes
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("trial_native_paths", Path(__file__).with_name("package-trial.py"))
TRIAL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRIAL)
SHORT_ALIAS = dict(status="not_run", old_string_comparison_equal=None, native_samefile=None)


def short_path(path):
    """Return an actual OS spelling, or None; never construct a tilde alias."""
    function = ctypes.WinDLL("kernel32", use_last_error=True).GetShortPathNameW
    function.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint32]
    function.restype = ctypes.c_uint32
    needed = function(str(path), None, 0)
    if not needed:
        return None
    buffer = ctypes.create_unicode_buffer(needed)
    size = function(str(path), buffer, needed)
    return buffer.value if 0 < size < needed else None


@unittest.skipUnless(os.name == "nt", "native Windows filesystem required; Linux is not Windows validation")
class WindowsFixturePaths(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="Contain report native synthetic ")
        self.parent = Path(self.temp.name).resolve()
        self.root = self.parent / ("contain-demo-" + "a" * 32)
        self.root.mkdir()
        self.marker = self.root / ".contain-demo-marker"
        self.marker.write_bytes(b"Contain test fixture")
        self.key = "Software\\Contain\\Demo\\" + self.root.name

    def tearDown(self):
        self.temp.cleanup()

    def check_alias(self, alias):
        self.assertTrue(Path(alias).samefile(self.root))
        scope = TRIAL.fixture_scope(str(alias), [str(self.root)], self.key)
        for spelling in (str(alias), str(self.root)):
            # This deleted/historical name does not exist. Only the root does.
            resource = spelling + "\\historical-deleted.txt"
            self.assertFalse(Path(resource).exists())
            self.assertTrue(scope.contains_file(resource))
            line = "  FILE Modified " + resource + " [Unknown]"
            self.assertTrue(scope.includes_diff(line))
            for text in (line, json.dumps(resource)):
                public = TRIAL.public_text(text, scope.replacements())
                self.assertIn("<fixture-root>", public)
                self.assertNotIn(str(self.parent).casefold(), public.casefold())
        return scope

    def test_native_canonical_and_extended_spelling(self):
        self.check_alias(str(self.root))
        self.check_alias("\\\\?\\" + str(self.root))

    def test_native_case_and_separator_spelling(self):
        self.check_alias(str(self.root).upper())
        self.check_alias(str(self.root).replace("\\", "/"))

    def test_native_actual_short_alias(self):
        alias = short_path(self.root)
        if alias is None or TRIAL.windows_spelling(alias) == TRIAL.windows_spelling(str(self.root)):
            SHORT_ALIAS.update(status="unavailable", old_string_comparison_equal=alias is not None,
                               native_samefile=None)
            self.skipTest("volume provides no distinct GetShortPathNameW spelling")
        SHORT_ALIAS.update(status="covered", old_string_comparison_equal=False,
                           native_samefile=Path(alias).samefile(self.root))
        scope = self.check_alias(alias)
        # Discovery of an observed alias also verifies the actual directory,
        # without requiring a historical child to still be present.
        canonical_only = TRIAL.fixture_scope(str(self.root), [str(self.root)], self.key)
        canonical_only.observe(alias + "\\historical-deleted.txt")
        self.assertTrue(canonical_only.contains_file(alias + "\\historical-deleted.txt"))
        self.assertEqual(scope.canonical.casefold(), str(self.root).casefold())

    def test_native_different_directory_rejected(self):
        other = self.parent / ("contain-demo-" + "b" * 32); other.mkdir()
        (other / ".contain-demo-marker").write_bytes(b"Contain test fixture")
        with self.assertRaisesRegex(ValueError, "identity_mismatch"):
            TRIAL.fixture_scope(str(self.root), [str(other)], self.key)

    def test_native_missing_directory_rejected(self):
        with self.assertRaisesRegex(ValueError, "identity_unavailable"):
            TRIAL.fixture_scope(str(self.root), [str(self.parent / "missing")], self.key)

    def test_native_wrong_guid_rejected(self):
        with self.assertRaisesRegex(ValueError, "fixture_registry"):
            TRIAL.fixture_scope(str(self.root), [str(self.root)], self.key[:-1] + "b")
        malformed = self.parent / "contain-demo-invalid-guid"; malformed.mkdir()
        (malformed / ".contain-demo-marker").write_bytes(b"Contain test fixture")
        with self.assertRaisesRegex(ValueError, "fixture_scope"):
            TRIAL.fixture_scope(str(malformed), [str(malformed)], "Software\\Contain\\Demo\\" + malformed.name)

    def test_native_invalid_marker_rejected(self):
        self.marker.write_bytes(b"not a fixture")
        with self.assertRaisesRegex(ValueError, "marker_invalid"):
            TRIAL.fixture_scope(str(self.root), [str(self.root)], self.key)
        self.marker.unlink()
        with self.assertRaises(ValueError):
            TRIAL.fixture_scope(str(self.root), [str(self.root)], self.key)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--private-log", type=Path)
    args = parser.parse_args()
    stream = io.StringIO()
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(WindowsFixturePaths)
    result = unittest.TextTestRunner(stream=stream, verbosity=2).run(suite)
    # Native failures can contain paths in assertion diagnostics. Keep CI stdout
    # fixed-shape; the private unittest stream is only available to a local run.
    summary = dict(schema_version=1, scope="synthetic_native_windows_directory_identity",
        native_windows=os.name == "nt", native=os.name == "nt", exporter=TRIAL.exporter_identity(),
        test_script_sha256=TRIAL.sha(Path(__file__)),
        passed=result.wasSuccessful() and os.name == "nt",
        tests_run=result.testsRun, passed_count=result.testsRun - len(result.failures) - len(result.errors) - len(result.skipped),
        failed_count=len(result.failures) + len(result.errors), skipped_count=len(result.skipped),
        short_alias=SHORT_ALIAS,
        failures=[test.id().rsplit(".", 1)[-1] for test, _ in result.failures + result.errors],
        skips=[dict(test=test.id().rsplit(".", 1)[-1], reason=reason) for test, reason in result.skipped])
    if args.summary:
        TRIAL.write(args.summary, summary)
    if args.private_log:
        with args.private_log.open("x", encoding="utf-8") as output:
            output.write(stream.getvalue())
    print(json.dumps(summary))
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
