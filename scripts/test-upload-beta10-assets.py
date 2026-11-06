"""Immutable Beta 10 delivery guards over synthetic artifacts only."""
import copy
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


UPLOAD = load("beta10_delivery", "upload-beta10-assets.py")
FIXTURE = load("beta10_desktop_fixtures", "test-upload-beta9-assets.py")
ORACLE = load("beta10_follow_plan", "verify-follow-installer-experiment.py")
BASELINE_SOURCE = "8e68ccfe63389e1533235e253eb59329f15be898"
BASELINE_ZIP_SHA256 = "dee47046f01c6a2c31c5bda3a3a3bcf3fc2a1603eb18b35464f68cde04ec7612"
LOSS_NAME = "capture-loss-pair-evidence"


def phase(baseline=False):
    loss, context = (100, 10) if baseline else (0, 0)
    targets = [dict(label=label, independent_successful_writes=1, content_verified=True,
                    retained_write_requests=2 if label != "unrelated" else 0,
                    promoted_write_requests=0 if baseline or label == "unrelated" else 1,
                    verified_actor_and_interval=None if label == "unrelated" else not baseline,
                    event_ids=[] if label == "unrelated" else [label + "-write", label + "-write-unknown"])
               for label in ("parent", "child", "unrelated")]
    return dict(experiment_accepted=not baseline, available=True, all_owned_processes_exited=True,
        terminal=dict(stage="finished", installer_exited=True, installer_exit_code=0, capture_finished=True, data_saved=True),
        cold_query_exact_session=True, capture_finished=True, capture_exit_code=0, independent_exit_code=0,
        quality_level="Incomplete" if baseline else "Degraded",
        counters=dict(events_received=200+loss, dropped_events=loss, etw_events_lost=0, etw_buffers_lost=0, decode_errors=0, context_losses=context),
        stats=dict(events_received=200+loss, events_retained=100, events_normalized=20, known_actor_events=0 if baseline else 90,
                   unknown_actor_events=100 if baseline else 10, drain_timed_out=False),
        providers_active=dict(etw_process=True, etw_file=True),
        pipeline=dict(callback_records=400+loss, deliberately_filtered=200, unsupported_records=0, decode_attempted=400+loss,
            decode_succeeded=400+loss, decode_failed=0, failed_without_output=0, enqueue_disconnected=0, enqueued=200,
            queue_overflow=loss, dequeued=200, queue_pending=0, queue_high_water=4096 if baseline else 20,
            queue_max_delay_ns=1000, context_evictions=0, retention_dropped=0, persistence_succeeded=100, persistence_failed=0),
        handoff=dict(initial_accepted=0, received=100, submitted=100, overflow=0, oversized=0, disconnected=0, serialize_failed=0,
            drained=100, pending=0, high_water_records=10, high_water_bytes=10000, record_capacity=4096, byte_capacity=16777216,
            receiver_finished=True, writer_finished=True),
        stream=dict(accepted=100, persisted=100, failed=0, quota_dropped=0, committed_bytes=10000, quota_bytes=67108864,
                    batches=2, max_batch_bytes=5000, error_present=False),
        follow_installer=dict(direct_children_only=True, baseline_available=False, candidate_record_limit=2048,
            candidate_byte_limit=8388608, resource_limit=4096, candidates=200, admitted=100-context,
            excluded_other=100, unresolved=0, excluded_self_io=0, candidate_dropped=context,
            candidate_high_water_records=2048 if baseline else 10, candidate_high_water_bytes=10000),
        targets=targets,
        background=dict(files=12000, successful_operations=dict(write_requested=12000, rename_requested=12000, delete_requested=12000),
            exit_code=0, signal_exit_code=0, observed_events=0, falsely_attributed_events=0, complete=True,
            window=dict(all_noise_within_root_lifetime=True, all_noise_within_capture=True, spans_parent_write=True, spans_child_write=True)),
        accounting={key: True for key in ("receive_queue", "receive_drain", "handoff_input", "handoff_drain", "raw_input",
                                          "raw_persistence", "application_loss", "finished_workers")},
        accepted=not baseline, workload_complete=True)


def pair(source, zip_sha, fixture_sha):
    return dict(schema_version=1, protocol="contain-capture-loss-pair-1", measurement_kind="real_etw",
        maximum_native_captures=2, retries=0, same_runner=True, order=["beta9", "candidate"],
        package_identity=dict(retries=0, background_files=12000, maximum_native_captures=2,
            baseline=dict(version="0.3.2-beta.9", source_sha=BASELINE_SOURCE, zip_sha256=BASELINE_ZIP_SHA256),
            candidate=dict(version="0.3.2-beta.10", source_sha=source, zip_sha256=zip_sha),
            fixture_source=source, same_fixture_sha256=fixture_sha,
            workload="existing fixed follow parent/direct-child/unrelated targets, plus bounded independent noise create/rename/delete",
            resource_policy="unchanged existing queue limits; evidence quota 64 MiB; settle 1500 ms; maximum descendant drain 10000 ms"),
        baseline=phase(True), candidate=phase(), comparable=True, accepted=True, improvement_claim=True,
        application_loss_change=dict(before=100, after=0, reduction=100, reduction_percent=100.0),
        limitations=["Synthetic release guard fixture only; no Windows acceptance claim."])


def oracle(source, value, baseline=False):
    failed = {"bounded_follow_admission", "actual_sources_and_loss_accounting", "parent_verified_source_write",
              "child_verified_source_write", "direct_child_parent_instance"} if baseline else set()
    skipped = {"parent_reopened_explanation", "child_reopened_explanation"} if baseline else set()
    checks = [dict(name=name, status="failed" if name in failed else "not_run" if name in skipped else "passed",
                   reason="synthetic_missing_evidence" if name in failed | skipped else None) for name in ORACLE.PLAN]
    observations = []
    if not baseline:
        for label, pid, rule in (("parent", 1, "InstallerPid"), ("child", 2, "DescendantProcess")):
            observations.append(dict(label=label, event_id=label+"-write", operation="write_requested", timestamp_ticks="150",
                pid=pid, creation_time="100" if label == "parent" else "120", rule=rule, confidence="High", actor_confidence="High",
                source="EtwFile", completion=None, state_validated=False, recorded_name_sha256="a"*64,
                current_path_verified=False, historical_name_source="file_object_cache"))
    observations.append(dict(label="unrelated", observed_event_count=0, contextual_event_count=0, event_ids=[],
        outcome="not_retained_by_follow_scope; no_claim_no_system_event", event_id=None, classification="unobserved"))
    return dict(schema_version=1, protocol="contain-follow-installer-experiment-1", source_sha=source, synthetic=False,
        controlled_fixture=True, maximum_native_captures=1, retries=0,
        session_id="11111111-1111-4111-8111-111111111111" if baseline else "22222222-2222-4222-8222-222222222222",
        checks=checks, observations=observations, ownership_claim=False, current_path_claim=False,
        installation_before_baseline=False, post_install_file_reads=False, accepted=not baseline,
        follow_counters=copy.deepcopy(value["follow_installer"]),
        storage_counters={key: value["stream"][key] for key in ("quota_bytes", "committed_bytes", "persisted", "failed", "quota_dropped")},
        candidate_evidence=[], passed=len(ORACLE.PLAN)-len(failed)-len(skipped), failed=len(failed), not_run=len(skipped),
        unresolved=[name for name in ORACLE.PLAN if name in failed | skipped])


class Beta10DeliveryTests(unittest.TestCase):
    def setUp(self):
        # Reuse the existing desktop fixture without modifying the Beta 9 tests
        # or checker module that protects the immutable historical release.
        configured = mock.patch.object(FIXTURE, "UPLOAD", UPLOAD.DESKTOP)
        configured.start(); self.addCleanup(configured.stop)
        self.legacy = FIXTURE.Beta9DeliveryTests("test_exact_artifacts_bind_actual_follow_capture_guidance_and_before_after")
        self.addCleanup(self.legacy.doCleanups)
        with contextlib.redirect_stdout(io.StringIO()):
            self.legacy.setUp()
        self.inputs, self.package = self.legacy.inputs, self.legacy.package
        self.source = self.legacy.fixture.source
        with zipfile.ZipFile(self.package) as archive:
            build = json.loads(archive.read("BUILD.json"))
        self.fixture_sha = build["controlled_fixture"]["sha256"]
        for name in ("baseline-benchmark.json", "candidate-benchmark.json"):
            path = self.legacy.extended / name
            value = json.loads(path.read_text())
            value["quality_gate"] = dict(mode="Bench", completed=True, passed=True, label="bench", exit_code=0)
            path.write_text(json.dumps(value))
        self.loss = self.inputs / LOSS_NAME
        self.loss_value = pair(self.source, UPLOAD.digest(self.package), self.fixture_sha)
        self.save_loss()

    def write(self, relative, value):
        path = self.loss / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value))

    def save_loss(self):
        self.write("public/capture-loss-pair.json", self.loss_value)
        self.write("beta9/public/follow-experiment-summary.json", oracle(BASELINE_SOURCE, self.loss_value["baseline"], True))
        self.write("candidate/public/follow-experiment-summary.json", oracle(self.source, self.loss_value["candidate"]))

    def verify_loss(self):
        return UPLOAD.verify_loss_pair(self.loss, self.source, UPLOAD.digest(self.package), self.fixture_sha)

    def verify(self):
        return UPLOAD.verify_artifacts(self.inputs, self.source, 12345, UPLOAD.digest(self.package), 12346)

    def test_exact_desktop_extended_and_loss_artifacts_are_all_bound(self):
        identity, files = self.verify()
        self.assertEqual(identity["release_tag"], "v0.3.2-beta.10")
        self.assertEqual(identity["source_sha"], self.source)
        self.assertEqual(identity["capture_loss_pair"]["summary"], self.loss_value)
        self.assertIn("CAPTURE-LOSS-PAIR.json", files)
        self.assertEqual(files[UPLOAD.ZIP].read_bytes(), self.package.read_bytes())

    def test_pilot_source_zip_or_fixture_cannot_replace_accepted_package(self):
        original = copy.deepcopy(self.loss_value)
        for key, wrong in (("source_sha", "f"*40), ("zip_sha256", "f"*64)):
            self.loss_value = copy.deepcopy(original)
            self.loss_value["package_identity"]["candidate"][key] = wrong
            self.save_loss()
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_loss()
        self.loss_value = copy.deepcopy(original)
        self.loss_value["package_identity"]["same_fixture_sha256"] = "f"*64
        self.save_loss()
        with self.assertRaises(ValueError):
            self.verify_loss()

    def test_baseline_is_exact_immutable_beta9_source_and_zip(self):
        original = copy.deepcopy(self.loss_value)
        for key, wrong in (("source_sha", "f"*40), ("zip_sha256", "f"*64), ("version", "0.3.2-beta.8")):
            self.loss_value = copy.deepcopy(original)
            self.loss_value["package_identity"]["baseline"][key] = wrong
            self.save_loss()
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_loss()

    def test_top_level_acceptance_requires_true_booleans(self):
        original = copy.deepcopy(self.loss_value)
        for key in ("same_runner", "comparable", "accepted", "improvement_claim"):
            for wrong in (False, 1):
                self.loss_value = copy.deepcopy(original); self.loss_value[key] = wrong; self.save_loss()
                with self.subTest(key=key, wrong=wrong), self.assertRaises(ValueError):
                    self.verify_loss()

    def test_residual_queue_and_candidate_loss_cannot_be_green(self):
        original = copy.deepcopy(self.loss_value)
        for layer, field in (("pipeline", "queue_overflow"), ("follow_installer", "candidate_dropped"),
                             ("handoff", "overflow"), ("stream", "quota_dropped"), ("stream", "failed"),
                             ("counters", "dropped_events"), ("counters", "context_losses")):
            self.loss_value = copy.deepcopy(original)
            self.loss_value["candidate"][layer][field] = 1; self.save_loss()
            with self.subTest(layer=layer, field=field), self.assertRaises(ValueError):
                self.verify_loss()

    def test_missing_accounting_window_and_independent_truth_cannot_pass(self):
        original = copy.deepcopy(self.loss_value)
        changes = [lambda d: d["candidate"]["accounting"].pop("receive_queue"),
                   lambda d: d["candidate"]["accounting"].update(receive_queue=1),
                   lambda d: d["baseline"]["accounting"].update(raw_persistence=False),
                   lambda d: d["candidate"]["background"]["window"].update(spans_child_write=False),
                   lambda d: d["baseline"]["background"]["window"].pop("all_noise_within_capture"),
                   lambda d: d["candidate"]["background"]["successful_operations"].update(write_requested=11999),
                   lambda d: d["baseline"].update(workload_complete=False)]
        for index, change in enumerate(changes):
            self.loss_value = copy.deepcopy(original); change(self.loss_value); self.save_loss()
            with self.subTest(change=index), self.assertRaises(ValueError):
                self.verify_loss()

    def test_target_request_retention_is_not_enough_without_exact_actor_evidence(self):
        original = copy.deepcopy(self.loss_value)
        for key, wrong in (("verified_actor_and_interval", False), ("promoted_write_requests", 0),
                           ("independent_successful_writes", 0), ("content_verified", False)):
            self.loss_value = copy.deepcopy(original)
            self.loss_value["candidate"]["targets"][0][key] = wrong; self.save_loss()
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_loss()

    def test_unrelated_write_must_never_be_attributed(self):
        original = copy.deepcopy(self.loss_value)
        for label in ("unrelated", "background"):
            self.loss_value = copy.deepcopy(original)
            if label == "unrelated":
                self.loss_value["candidate"]["targets"][2]["promoted_write_requests"] = 1
            else:
                self.loss_value["candidate"]["background"]["falsely_attributed_events"] = 1
            self.save_loss()
            with self.subTest(label=label), self.assertRaises(ValueError):
                self.verify_loss()

    def test_candidate_oracle_source_checks_and_event_identity_must_match(self):
        path = self.loss / "candidate/public/follow-experiment-summary.json"
        original = json.loads(path.read_text())
        changes = [lambda d: d.update(source_sha="f"*40), lambda d: d.update(accepted=False),
                   lambda d: d["checks"][0].update(status="failed"), lambda d: d["checks"].pop(),
                   lambda d: d["observations"][0].update(event_id="another-event"),
                   lambda d: d["observations"][0].update(confidence="Unknown"),
                   lambda d: d["observations"][1].update(rule="InstallerPid"),
                   lambda d: d["follow_counters"].update(candidate_dropped=1),
                   lambda d: d["storage_counters"].update(persisted=1)]
        for index, change in enumerate(changes):
            value = copy.deepcopy(original); change(value); path.write_text(json.dumps(value))
            with self.subTest(change=index), self.assertRaises(ValueError):
                self.verify_loss()

    def test_same_resource_limits_and_finite_load_are_required(self):
        original = copy.deepcopy(self.loss_value)
        changes = [lambda d: d["candidate"]["handoff"].update(record_capacity=8192),
                   lambda d: d["candidate"]["follow_installer"].update(candidate_record_limit=4096),
                   lambda d: d["candidate"]["stream"].update(quota_bytes=134217728),
                   lambda d: d["candidate"]["background"].update(files=6000),
                   lambda d: d.update(retries=1), lambda d: d.update(order=["candidate", "beta9"])]
        for index, change in enumerate(changes):
            self.loss_value = copy.deepcopy(original); change(self.loss_value); self.save_loss()
            with self.subTest(change=index), self.assertRaises(ValueError):
                self.verify_loss()

    def test_loss_improvement_must_be_measured_and_math_must_match(self):
        original = copy.deepcopy(self.loss_value)
        for field, wrong in (("before", 101), ("after", 1), ("reduction", 1), ("reduction_percent", 1)):
            self.loss_value = copy.deepcopy(original)
            self.loss_value["application_loss_change"][field] = wrong; self.save_loss()
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.verify_loss()

    def test_saved_exit_workers_and_providers_must_be_confirmed(self):
        original = copy.deepcopy(self.loss_value)
        changes = [lambda d: d["candidate"]["terminal"].update(data_saved=False),
                   lambda d: d["candidate"]["terminal"].update(installer_exit_code=1),
                   lambda d: d["baseline"].update(cold_query_exact_session=False),
                   lambda d: d["candidate"].update(all_owned_processes_exited=False),
                   lambda d: d["candidate"]["providers_active"].update(etw_file=False)]
        for index, change in enumerate(changes):
            self.loss_value = copy.deepcopy(original); change(self.loss_value); self.save_loss()
            with self.subTest(change=index), self.assertRaises(ValueError):
                self.verify_loss()

    def test_claimed_true_accounting_cannot_hide_inconsistent_counters(self):
        self.loss_value["candidate"]["handoff"]["submitted"] += 1
        self.save_loss()
        with self.assertRaises(ValueError):
            self.verify_loss()

    def test_existing_desktop_gate_cannot_be_omitted(self):
        self.legacy.rewrite_validation(lambda d: d["checks"].pop("native_session_event_search"))
        with self.assertRaises(ValueError):
            self.verify()

    def test_existing_quality_evidence_still_requires_passed_gates(self):
        path = self.inputs / UPLOAD.NAMES[1] / "summary.json"
        value = json.loads(path.read_text()); value["current_required_gates_passed"] = False
        path.write_text(json.dumps(value))
        with self.assertRaises(ValueError):
            self.verify()

    def test_extended_quality_requires_actual_bench_gate_and_fuzz(self):
        path = self.legacy.extended / "candidate-benchmark.json"; original = path.read_bytes()
        for field, wrong in (("completed", False), ("passed", 1), ("exit_code", 1)):
            value = json.loads(original); value["quality_gate"][field] = wrong; path.write_text(json.dumps(value))
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.verify()
        path.write_bytes(original)
        fuzz = self.legacy.extended / "fuzz-identity.json"
        value = json.loads(fuzz.read_text()); value["accepted"] = False; fuzz.write_text(json.dumps(value))
        with self.assertRaises(ValueError):
            self.verify()

    def test_extended_baseline_must_be_beta9_instead_of_earlier_beta(self):
        path = self.legacy.extended / "baseline-benchmark.json"
        value = json.loads(path.read_text()); value["source_commit"] = "a1f9876f4976c2e35b443d9634bbb06fa19f1927"
        path.write_text(json.dumps(value))
        with self.assertRaises(ValueError):
            self.verify()

    def test_accepted_remote_requires_exact_successful_first_push_and_all_artifacts(self):
        run = dict(head_sha=self.source, event="push", conclusion="success", status="completed", run_attempt=1,
                   path=".github/workflows/ci.yml", head_repository=dict(full_name=UPLOAD.REPO))
        artifacts = [dict(id=index+1, name=name, expired=False) for index, name in enumerate(UPLOAD.NAMES)]
        with mock.patch.object(UPLOAD, "api", side_effect=[run, dict(artifacts=artifacts)]):
            self.assertEqual(set(UPLOAD.accepted_remote(self.source, 12345)), set(UPLOAD.NAMES))
        for key, wrong in (("head_sha", "f"*40), ("run_attempt", 2), ("conclusion", "failure"), ("event", "pull_request")):
            invalid = dict(run); invalid[key] = wrong
            with self.subTest(key=key), mock.patch.object(UPLOAD, "api", return_value=invalid), self.assertRaises(ValueError):
                UPLOAD.accepted_remote(self.source, 12345)
        for rows in (artifacts[:-1], artifacts+artifacts[-1:], [dict(row, expired=True) for row in artifacts]):
            with mock.patch.object(UPLOAD, "api", side_effect=[run, dict(artifacts=rows)]), self.assertRaises(ValueError):
                UPLOAD.accepted_remote(self.source, 12345)

    def test_only_exact_existing_beta10_prerelease_draft_is_allowed(self):
        draft = dict(id=77, tag_name="v0.3.2-beta.10", target_commitish=self.source, draft=True, prerelease=True, assets=[])
        with mock.patch.object(UPLOAD, "api", side_effect=[[draft], draft]):
            self.assertEqual(UPLOAD.existing_draft(self.source)["id"], 77)
        for key, wrong in (("tag_name", "v0.3.2-beta.9"), ("target_commitish", "f"*40), ("draft", False), ("prerelease", False)):
            invalid = dict(draft); invalid[key] = wrong
            with self.subTest(key=key), mock.patch.object(UPLOAD, "api", return_value=invalid), self.assertRaises(ValueError):
                UPLOAD.existing_draft(self.source, 77)

    def test_extended_remote_is_separate_same_source_successful_first_push(self):
        run = dict(head_sha=self.source, event="push", conclusion="success", status="completed", run_attempt=1,
                   path=".github/workflows/follow-installer-quality.yml", head_repository=dict(full_name=UPLOAD.REPO))
        artifact = dict(id=42, name=UPLOAD.EXTENDED_NAME, expired=False)
        with mock.patch.object(UPLOAD, "api", side_effect=[run, dict(artifacts=[artifact])]):
            self.assertEqual(UPLOAD.accepted_extended_remote(self.source, 12346), 42)
        for key, wrong in (("head_sha", "f"*40), ("event", "workflow_dispatch"), ("run_attempt", 2),
                           ("path", ".github/workflows/ci.yml"), ("conclusion", "failure")):
            invalid = dict(run); invalid[key] = wrong
            with self.subTest(key=key), mock.patch.object(UPLOAD, "api", return_value=invalid), self.assertRaises(ValueError):
                UPLOAD.accepted_extended_remote(self.source, 12346)

    def test_authorized_manual_extended_run_requires_exact_source_and_branch(self):
        run = dict(head_sha=self.source, event="workflow_dispatch", head_branch="fix/beta10-capture-loss",
                   conclusion="success", status="completed", run_attempt=1,
                   path=".github/workflows/follow-installer-quality.yml", head_repository=dict(full_name=UPLOAD.REPO))
        artifact = dict(id=42, name=UPLOAD.EXTENDED_NAME, expired=False)
        with mock.patch.object(UPLOAD, "api", side_effect=[run, dict(artifacts=[artifact])]):
            self.assertEqual(UPLOAD.accepted_extended_remote(self.source, 12346), 42)
        for key, wrong in (("head_sha", "f"*40), ("head_branch", "main"), ("head_branch", "another-branch"),
                           ("event", "pull_request"), ("run_attempt", 2), ("conclusion", "failure"),
                           ("head_repository", dict(full_name="another/repository"))):
            invalid = dict(run); invalid[key] = wrong
            with self.subTest(key=key, wrong=wrong), mock.patch.object(UPLOAD, "api", return_value=invalid), self.assertRaises(ValueError):
                UPLOAD.accepted_extended_remote(self.source, 12346)


class Beta10AssetUploadTests(unittest.TestCase):
    """Mock the network boundary; these tests never call GitHub."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.output = self.root / "assets"; self.output.mkdir()
        self.package = self.output / "accepted.zip"
        self.payload = b"immutable accepted package bytes"
        self.package.write_bytes(self.payload)
        self.source = "a"*40
        self.assets = []
        self.calls = []

    def draft(self):
        return dict(id=77, tag_name=UPLOAD.TAG, target_commitish=self.source, draft=True,
                    prerelease=True, assets=copy.deepcopy(self.assets))

    def command(self, arguments, **kwargs):
        self.calls.append(list(arguments))
        if arguments[:3] == ["gh", "release", "upload"]:
            self.assets.append(dict(id=777, name=self.package.name, state="uploaded", size=len(self.payload)))
        elif arguments[:2] == ["gh", "api"]:
            kwargs["stdout"].write(self.payload)
        else:
            self.fail("Unexpected network operation")
        return 0

    def upload(self):
        return UPLOAD.upload_assets(self.output, self.root, self.source, 77)

    def test_upload_is_bound_to_exact_asset_id_and_identical_readback(self):
        with mock.patch.object(UPLOAD, "existing_draft", side_effect=lambda *_: self.draft()), \
                mock.patch.object(UPLOAD.subprocess, "check_call", side_effect=self.command):
            self.upload()
        self.assertEqual(len(self.calls), 2)
        self.assertEqual(self.calls[0], ["gh", "release", "upload", UPLOAD.TAG, str(self.package), "--repo", UPLOAD.REPO])
        self.assertEqual(self.calls[1], ["gh", "api", f"repos/{UPLOAD.REPO}/releases/assets/777",
                                        "--header", "Accept: application/octet-stream"])
        self.assertEqual((self.root / "readback/accepted.zip").read_bytes(), self.payload)
        self.assertFalse(any("--clobber" in call for call in self.calls))

    def test_existing_draft_asset_is_never_overwritten_or_reused(self):
        self.assets = [dict(id=777, name=self.package.name, state="uploaded", size=len(self.payload))]
        with mock.patch.object(UPLOAD, "existing_draft", side_effect=lambda *_: self.draft()), \
                mock.patch.object(UPLOAD.subprocess, "check_call") as command, self.assertRaises(ValueError):
            self.upload()
        command.assert_not_called()

    def test_concurrent_asset_before_upload_blocks_write(self):
        empty = self.draft()
        self.assets = [dict(id=777, name=self.package.name, state="uploaded", size=len(self.payload))]
        with mock.patch.object(UPLOAD, "existing_draft", side_effect=[empty, self.draft()]), \
                mock.patch.object(UPLOAD.subprocess, "check_call") as command, self.assertRaises(ValueError):
            self.upload()
        command.assert_not_called()

    def test_uploaded_asset_must_be_unique_complete_and_expected_size(self):
        malformed = [dict(id=777, name=self.package.name, state="new", size=len(self.payload)),
                     dict(id=777, name=self.package.name, state="uploaded", size=len(self.payload)+1)]
        for index, asset in enumerate(malformed):
            with self.subTest(index=index):
                self.assets = []
                empty = self.draft(); self.assets = [asset]
                with mock.patch.object(UPLOAD, "existing_draft", side_effect=[empty, empty, self.draft()]), \
                        mock.patch.object(UPLOAD.subprocess, "check_call") as command, self.assertRaises(ValueError):
                    self.upload()
                self.assertEqual(command.call_count, 1)
                # The production helper refuses to overwrite its local readback
                # directory too. Each independent test sample gets a new root.
                (self.root / "readback").rmdir()
        self.assets = []
        empty = self.draft()
        self.assets = [dict(id=777, name=self.package.name, state="uploaded", size=len(self.payload))]*2
        with mock.patch.object(UPLOAD, "existing_draft", side_effect=[empty, empty, self.draft()]), \
                mock.patch.object(UPLOAD.subprocess, "check_call") as command, self.assertRaises(ValueError):
            self.upload()
        self.assertEqual(command.call_count, 1)

    def test_readback_byte_mismatch_is_a_failure(self):
        def corrupted(arguments, **kwargs):
            if arguments[:2] == ["gh", "api"]:
                self.calls.append(list(arguments)); kwargs["stdout"].write(b"different bytes"); return 0
            return self.command(arguments, **kwargs)
        with mock.patch.object(UPLOAD, "existing_draft", side_effect=lambda *_: self.draft()), \
                mock.patch.object(UPLOAD.subprocess, "check_call", side_effect=corrupted), self.assertRaises(ValueError):
            self.upload()
        self.assertEqual(len(self.calls), 2)

    def test_release_becoming_published_stops_before_asset_readback(self):
        published = False
        def api(_):
            value = self.draft(); value["draft"] = not published
            return value
        def publish_after_upload(arguments, **kwargs):
            nonlocal published
            result = self.command(arguments, **kwargs)
            published = True
            return result
        with mock.patch.object(UPLOAD, "api", side_effect=api), \
                mock.patch.object(UPLOAD.subprocess, "check_call", side_effect=publish_after_upload), self.assertRaises(ValueError):
            self.upload()
        self.assertEqual(len(self.calls), 1)

    def test_asset_id_cannot_change_during_readback(self):
        def change_id(arguments, **kwargs):
            result = self.command(arguments, **kwargs)
            if arguments[:2] == ["gh", "api"]:
                self.assets[0]["id"] = 778
            return result
        with mock.patch.object(UPLOAD, "existing_draft", side_effect=lambda *_: self.draft()), \
                mock.patch.object(UPLOAD.subprocess, "check_call", side_effect=change_id), self.assertRaises(ValueError):
            self.upload()


if __name__ == "__main__":
    unittest.main()
