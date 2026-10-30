"""One small ETW clock evidence audit; not a replacement scorer or acceptance gate.

Full raw/final evidence is analyzed on the runner before publication. Only the
fixed public projection may be uploaded; the diagnostic directory stays private.
"""
import argparse
from collections import Counter
from contextlib import closing
import copy
import hashlib
import importlib.util
import json
from pathlib import Path, PureWindowsPath
import re
import sqlite3
import time


REPO = Path(__file__).resolve().parent.parent
PROTOCOL = 'contain-etw-clock-audit-1'
CLOCK_REASON_CODES = frozenset(('collector_native_provenance_changed', 'collector_native_provenance_missing',
 'collector_native_provenance_phase_bounds_invalid', 'collector_samples',
 'consumer_clock_mode_invalid', 'consumer_did_not_request_raw_timestamp',
 'consumer_loss_counters_available', 'consumer_ready', 'consumer_stopped',
 'fixture_clock_initialized', 'fixture_clock_not_an_object',
 'fixture_collector_machine_or_boot_mismatch', 'fixture_control_marker',
 'fixture_native_provenance_after_operation_boundary',
 'fixture_native_provenance_changed_within_process', 'fixture_outside_recorded_trace_qpc_window',
 'fixture_rows', 'independent_clock_observability', 'independent_clock_or_identity_missing',
 'native_provenance_identifier_invalid', 'native_provenance_measurement_unavailable_or_invalid',
 'native_provenance_qpc_invalid', 'native_provenance_qpc_regressed',
 'native_provenance_sample_missing', 'original_qpc_unavailable',
 'primary_actual_clock_type_not_qpc', 'primary_actual_consumer_mode_mismatch',
 'primary_clock_domain_unexpected', 'primary_event_key_ambiguous',
 'primary_event_key_missing_from_companion', 'raw_companion_binary_changed_during_open',
 'raw_companion_binary_changed_during_read', 'raw_companion_binary_digest_invalid',
 'raw_companion_binary_digest_size_or_file_changed',
 'raw_companion_binary_duplicate_check_not_deferred', 'raw_companion_binary_file_name_invalid',
 'raw_companion_binary_final_stats_unavailable', 'raw_companion_binary_format_invalid',
 'raw_companion_binary_manifest_changed', 'raw_companion_binary_manifest_overbudget',
 'raw_companion_binary_not_single_regular_file', 'raw_companion_binary_path_required',
 'raw_companion_binary_redirected_path', 'raw_companion_binary_size_mismatch',
 'raw_companion_binary_summary_mismatch', 'raw_companion_binary_truncated_or_overbudget',
 'raw_companion_binary_unexpected_provider', 'raw_companion_binary_writer_counters_invalid',
 'raw_companion_binary_writer_incomplete', 'raw_companion_budget_inconsistent',
 'raw_companion_clock_type_not_qpc', 'raw_companion_consumer_mode_invalid',
 'raw_companion_digest_or_size_mismatch', 'raw_companion_document_overbudget',
 'raw_companion_duplicate_key_sequence_or_invalid_qpc', 'raw_companion_exact_descriptor_mismatch',
 'raw_companion_frequency_mismatch', 'raw_companion_header_boot_time_malformed',
 'raw_companion_header_observation_contract_unsupported', 'raw_companion_header_reports_loss',
 'raw_companion_header_time_malformed', 'raw_companion_incomplete',
 'raw_companion_invalid_counters', 'raw_companion_link_unavailable',
 'raw_companion_loss_or_ambiguity', 'raw_companion_metadata_writer_incomplete',
 'raw_companion_not_committed', 'raw_companion_primary_header_mismatch',
 'raw_companion_primary_key_budget_exceeded', 'raw_companion_record_count_mismatch',
 'raw_companion_schema_unsupported', 'raw_companion_session_mismatch',
 'raw_companion_summary_mismatch', 'source_actor_unknown_events', 'source_event_count',
 'source_events_with_raw_qpc', 'source_events_with_raw_qpc_and_identity',
 'source_events_without_raw_qpc', 'source_original_clock_unavailable',
 'source_original_qpc_or_actor_missing', 'source_reasons', 'trace_calibration_diagnostic',
 'trace_calibration_status', 'trace_calibration_unavailable_or_invalid',
 'trace_clock_boundaries_missing_or_invalid', 'trace_clock_measurement_errors_or_missing',
 'trace_clock_metadata_missing', 'trace_clock_schema_unsupported',
 'trace_fixture_frequency_mismatch', 'trace_header', 'trace_header_available',
 'trace_header_unavailable', 'trace_metadata', 'trace_not_qpc', 'trace_qpc_frequency_unavailable',
 'trace_start_requested', 'trace_stop_requested'))


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


CLOCK = module('etw_existing_clock', 'v04-clock-protocol.py')
LAYERED = module('etw_existing_layers', 'verify-layered-evidence.py')
EXPORT = module('etw_existing_export', 'run-write-diagnostic.py')
SCORE = module('etw_legacy_score', 'score-fixture.py')


def obj(value):
    return value if isinstance(value, dict) else {}


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def supported(event):
    return event.get('event_type') in {'file', 'registry'} and event.get('operation') in CLOCK.SUPPORTED


def actor(event):
    try:
        return CLOCK.identity(event, True)
    except (KeyError, TypeError, ValueError):
        return None


def joined_events(capture):
    """Keep raw facts. Only a unique fact-consistent same-ID view supplies actor."""
    raw, final = capture.get('raw_events'), capture.get('events')
    issues = []
    if not isinstance(raw, list) or not all(isinstance(row, dict) for row in raw):
        return [], ['raw_events_missing']
    if not isinstance(final, list) or not all(isinstance(row, dict) for row in final):
        return [], ['final_events_missing']
    raw_ids = Counter(row.get('id') for row in raw)
    final_ids = Counter(row.get('id') for row in final)
    if any(not isinstance(key, str) or count != 1 for key, count in raw_ids.items()):
        issues.append('duplicate_or_invalid_raw_event_id')
    if any(not isinstance(key, str) or count != 1 for key, count in final_ids.items()):
        issues.append('duplicate_or_invalid_final_event_id')
    finals = {row.get('id'): row for row in final if final_ids[row.get('id')] == 1}
    result = []
    for primary in raw:
        view = finals.get(primary.get('id'))
        event = copy.deepcopy(primary)
        if view is not None:
            immutable = ('registry_object', 'registry_fields', 'thread_id', 'header_pid', 'process_key', 'parent_key')
            if (not LAYERED.fact_consistent(primary, view)
                    or any(obj(primary.get('raw')).get(key) != obj(view.get('raw')).get(key) for key in immutable)):
                issues.append('raw_final_fact_conflict')
            elif raw_ids[primary.get('id')] == 1:
                for key in ('evidence', 'dimensions', 'confidence', 'success'):
                    event[key] = copy.deepcopy(view.get(key))
        elif supported(primary):
            issues.append('supported_raw_event_without_unique_final_view')
        result.append(event)
    if any(supported(row) and row.get('id') not in raw_ids for row in final):
        issues.append('supported_final_event_without_raw_record')
    return result, sorted(set(issues))


def integrity(capture):
    checks = {}
    backend = obj(capture.get('backend'))
    for group, fields in (
        ('backend', ('dropped_events', 'context_losses', 'decode_errors', 'etw_events_lost', 'etw_buffers_lost')),
        ('stats', ('events_dropped',)),
        ('pipeline', ('queue_overflow', 'enqueue_disconnected', 'queue_pending', 'context_evictions', 'retention_dropped', 'failed_without_output')),
        ('handoff', ('overflow', 'oversized', 'disconnected', 'serialize_failed', 'pending')),
        ('stream', ('failed', 'quota_dropped')),
    ):
        source = backend if group == 'backend' else obj(capture.get(group)) if group == 'stats' else obj(backend.get(group))
        for field in fields:
            value = source.get(field)
            checks[group + '.' + field] = dict(actual=value, expected=0, satisfied=type(value) is int and value == 0)
    for key, value, expected in (
        ('capture_state', capture.get('capture_state'), 'finished'),
        ('drain_timed_out', obj(capture.get('stats')).get('drain_timed_out'), False),
        ('file_provider', backend.get('etw_file'), 'active'),
        ('process_provider', backend.get('etw_process'), 'active'),
        ('registry_provider', backend.get('etw_registry'), 'active'),
        ('receiver_finished', obj(backend.get('handoff')).get('receiver_finished'), True),
        ('writer_finished', obj(backend.get('handoff')).get('writer_finished'), True),
        ('stream_error_absent', obj(backend.get('stream')).get('error', 'missing') is None, True),
    ):
        checks[key] = dict(actual=value, expected=expected, satisfied=type(value) is type(expected) and value == expected)
    return dict(complete=all(row['satisfied'] for row in checks.values()), checks=checks)


def relation(tick, start, end):
    return 'unavailable' if None in (tick, start, end) else 'before' if tick < start else 'after' if tick > end else 'inside'


def unsigned(value):
    try:
        return CLOCK.integer(value)
    except (TypeError, ValueError):
        return None


def analyze(capture, truth, legacy_score, raw_bytes=None, raw_path=None, sid=''):
    require(isinstance(truth, list) and bool(truth), 'independent_truth_missing')
    events, issues = joined_events(capture)
    projection = dict(capture, events=events)
    try:
        clock = CLOCK.analyze(projection, truth, sid, raw_bytes, raw_path)
    except (KeyError, TypeError, ValueError, AttributeError, OSError) as error:
        clock = dict(status='unavailable', source_reasons=['clock_analysis_failed'], private_error=str(error))
    linked = {row['event_id']: unsigned(row.get('original_qpc')) for row in obj(clock.get('raw_companion')).get('event_links', [])}
    clock_rows = clock.get('truth_rows', [])
    clocks_valid = len(clock_rows) == len(truth) and all(
        row.get('independent_clock') == 'valid' and row.get('status') != 'invalid'
        and row.get('reason') not in {'trace_fixture_frequency_mismatch', 'fixture_outside_recorded_trace_qpc_window'}
        for row in clock_rows)
    checks = integrity(capture)
    available = (not issues and obj(clock.get('raw_companion')).get('status') == 'exact_original_key_links_available'
                 and clock.get('same_machine_boot_provenance_verified') is True
                 and clock.get('source_events_without_raw_qpc') == 0 and clocks_valid
                 and not [reason for reason in clock.get('source_reasons', []) if reason != 'source_original_qpc_or_actor_missing'])
    rows = []
    for event in events:
        if not supported(event):
            continue
        who = actor(event)
        tick = linked.get(event.get('id')) if available else None
        comparisons = []
        for index, call in enumerate(truth):
            try:
                expected = CLOCK.identity(call)
            except (KeyError, TypeError, ValueError):
                expected = None
            paths = {CLOCK.normalize(call.get(key), sid) for key in ('resource', 'destination')} - {''}
            independent = obj(call.get('clock'))
            start, end = unsigned(independent.get('start_qpc')), unsigned(independent.get('end_qpc'))
            wall_start, wall_end = unsigned(call.get('start_ticks')), unsigned(call.get('end_ticks'))
            wall = unsigned(event.get('timestamp_ticks'))
            same_actor = who == expected if who is not None and expected is not None else None
            same_resource = CLOCK.normalize(event.get('resource'), sid) in paths
            same_operation = event.get('operation') == call.get('operation')
            comparisons.append(dict(truth_row=index, same_actor=same_actor, same_resource=same_resource,
                same_operation=same_operation, qpc_relation=relation(tick, start, end),
                filetime_relation=relation(wall, wall_start, wall_end),
                qpc_start=start, qpc_end=end, filetime_start=wall_start, filetime_end=wall_end,
                start_margin_qpc=tick-start if tick is not None and start is not None else None,
                end_margin_qpc=end-tick if tick is not None and end is not None else None,
                filetime_after_end_100ns=wall-wall_end if wall is not None and wall_end is not None else None))
        candidate = [row['truth_row'] for row in comparisons if row['same_actor'] is True and row['same_resource'] and row['same_operation']]
        exact = [row['truth_row'] for row in comparisons if row['truth_row'] in candidate and row['qpc_relation'] == 'inside']
        rows.append(dict(event_id=event.get('id'), candidate_truth_rows=candidate, exact_qpc_truth_rows=exact,
                         unique_qpc_candidate=False, comparisons=comparisons))
    uses = Counter(index for row in rows for index in row['exact_qpc_truth_rows'])
    for row in rows:
        row['unique_qpc_candidate'] = len(row['exact_qpc_truth_rows']) == 1 and uses[row['exact_qpc_truth_rows'][0]] == 1
    recomputed = SCORE.score(capture, truth, sid)
    return dict(schema_version=1, protocol=PROTOCOL, acceptance_pass=None, tolerance_qpc=0,
        clock_evidence_complete=bool(available and checks['complete']), join_issues=issues,
        integrity=checks, clock=clock, events=events, truth=copy.deepcopy(truth), relations=rows,
        watch_roots=capture.get('watch_roots', []), registry_key=capture.get('registry_key'), private_sid=sid,
        legacy_score=copy.deepcopy(legacy_score), legacy_score_recomputed=recomputed,
        legacy_score_recomputed_equal=legacy_score == recomputed,
        definition='Integer original-QPC interval compatibility is a candidate only, not causality, explicit application calls, completion, ownership or acceptance. No truth data supplies production attribution.')


def safe_id(value):
    return value if isinstance(value, str) and re.fullmatch(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}', value) else None


def safe_hash(value):
    return value.lower() if isinstance(value, str) and re.fullmatch(r'[0-9a-fA-F]{64}', value) else None


def numeric_fields(value, keys):
    return {key: obj(value).get(key) if type(obj(value).get(key)) in (int, float, bool) or obj(value).get(key) is None else None for key in keys if key in obj(value)}


def public_summary(result):
    """Never publish raw metadata/provenance, host paths, arbitrary errors or actors."""
    truth = result.get('truth', [])
    sid = result.get('private_sid', '')
    identities, tids = set(), set()
    resources = {}
    for call in truth:
        try:
            identities.add(CLOCK.identity(call))
        except (KeyError, TypeError, ValueError):
            pass
        tid = unsigned(obj(call.get('clock')).get('thread_id'))
        if tid is not None:
            tids.add(tid)
        for key in ('resource', 'destination'):
            path = CLOCK.normalize(call.get(key), sid)
            if path and path not in resources:
                resources[path] = f'<fixture-resource-{len(resources)+1}>'
    roots = [CLOCK.normalize(root, sid).rstrip('\\') for root in result.get('watch_roots', [])]
    registry = CLOCK.normalize('HKCU\\' + result['registry_key'], sid).rstrip('\\') if result.get('registry_key') else ''

    def resource(value):
        path = CLOCK.normalize(value, sid)
        if path in resources:
            return resources[path]
        if any(path == root or path.startswith(root + '\\') for root in roots) or registry and (path == registry or path.startswith(registry + '\\')):
            resources[path] = f'<fixture-resource-{len(resources)+1}>'
            return resources[path]
        return None

    relations = {row['event_id']: row for row in result.get('relations', [])}
    links = {row['event_id']: row for row in obj(obj(result.get('clock')).get('raw_companion')).get('event_links', [])}
    published = []
    event_view = result.get('events', result.get('partial_final_view_events', []))
    view_scope = ('unavailable' if 'events' not in result and 'partial_final_view_events' not in result else
                  'final_view_only_no_raw_verification' if 'events' not in result else
                  'raw_facts_with_rejected_join' if result.get('join_issues') else 'raw_facts_with_same_id_final_actor')
    for event in event_view:
        if not supported(event) or resource(event.get('resource')) is None:
            continue
        raw = obj(event.get('raw')); clock = obj(raw.get('clock')); name = obj(raw.get('file_name'))
        descriptor = obj(clock.get('record_identity'))
        who = actor(event)
        safe_actor = who if who in identities else None
        public_descriptor = {key: descriptor.get(key) if type(descriptor.get(key)) is int else None for key in CLOCK.IDENTITY_WIDTHS}
        public_descriptor['provider'] = safe_id(descriptor.get('provider'))
        keyword = descriptor.get('keyword')
        public_descriptor['keyword'] = keyword if isinstance(keyword, str) and re.fullmatch(r'[0-9a-f]{16}', keyword) else None
        if descriptor.get('process_id') not in {pair[0] for pair in identities}:
            public_descriptor['process_id'] = None
        if descriptor.get('thread_id') not in tids:
            public_descriptor['thread_id'] = None
        row = relations.get(event.get('id'), {})
        comparison_status = ('not_run' if not row else 'evaluated' if
                             any(item.get('qpc_relation') != 'unavailable' for item in row.get('comparisons', [])) else 'clock_unavailable')
        comparisons = []
        for comparison in row.get('comparisons', []):
            # Include all truth comparisons for every published supported event,
            # not only successful candidates or the old scorer's false positives.
            copied = dict(comparison)
            for key in ('qpc_start', 'qpc_end', 'filetime_start', 'filetime_end', 'start_margin_qpc', 'end_margin_qpc', 'filetime_after_end_100ns'):
                copied[key] = str(copied[key]) if type(copied.get(key)) is int else None
            comparisons.append(copied)
        source = obj(event.get('evidence')).get('source')
        path_source = name.get('path_source')
        published.append(dict(event_id=safe_id(event.get('id')), event_type=event['event_type'], operation=event['operation'], view_scope=view_scope,
            source=source if source in {'EtwFile', 'EtwRegistry'} else None,
            resource=resource(event.get('resource')), actor=dict(pid=safe_actor[0], creation_time=str(safe_actor[1])) if safe_actor else None,
            actor_known_but_not_fixture=who is not None and safe_actor is None,
            issuing_thread_id=raw.get('thread_id') if type(raw.get('thread_id')) is int and raw['thread_id'] in tids else None,
            confidence=event.get('confidence') if event.get('confidence') in {'Certain', 'High', 'Medium', 'Low', 'Unknown'} else None,
            success=event.get('success') if type(event.get('success')) is bool else None,
            record_identity=public_descriptor,
            record_identity_sha256=hashlib.sha256(json.dumps(descriptor, sort_keys=True).encode()).hexdigest(),
            event_key=clock.get('event_key') if isinstance(clock.get('event_key'), str) and re.fullmatch(r'[0-9a-f]{16}', clock['event_key']) else None,
            delivered_header_filetime=str(unsigned(clock.get('header_timestamp'))) if unsigned(clock.get('header_timestamp')) is not None else None,
            event_filetime=str(unsigned(event.get('timestamp_ticks'))) if unsigned(event.get('timestamp_ticks')) is not None else None,
            original_qpc=str(unsigned(links.get(event.get('id'), {}).get('original_qpc'))) if unsigned(links.get(event.get('id'), {}).get('original_qpc')) is not None else None,
            name=dict(path_source=path_source if path_source in {'supplied_FileName', 'supplied_FilePath', 'file_object_cache', 'file_key_cache'} else None,
                      observed_ticks=str(unsigned(name.get('observed_ticks'))) if unsigned(name.get('observed_ticks')) is not None else None,
                      source_event_id=safe_id(name.get('source_event_id')),
                      current_path_verified=name.get('current_path_verified') if type(name.get('current_path_verified')) is bool else None),
            candidate_truth_rows=row.get('candidate_truth_rows') if row else None,
            exact_qpc_truth_rows=row.get('exact_qpc_truth_rows') if comparison_status == 'evaluated' else None,
            unique_qpc_candidate=row.get('unique_qpc_candidate') if comparison_status == 'evaluated' else None,
            comparison_status=comparison_status, comparisons=comparisons if row else None))
    legacy = obj(result.get('legacy_score'))
    score_keys = ('schema_version', 'expected', 'observed', 'correctly_attributed', 'unknown', 'incorrect_attribution',
                  'positive_raw_predictions', 'false_positive_target_events', 'supporting_duplicate_raw_events', 'excluded_control_positive_events',
                  'precision', 'dropped', 'etw_events_lost', 'etw_buffers_lost')
    score = numeric_fields(legacy, score_keys)
    score['unexpected_positive_events'] = ([safe_id(key) for key in legacy['unexpected_positive_events']]
                                          if isinstance(legacy.get('unexpected_positive_events'), list) else None)
    group_keys = ('expected', 'observed', 'unobserved', 'unsupported_out_of_scope', 'correctly_attributed', 'incorrectly_attributed',
                  'observed_but_unresolved', 'correctly_not_assigned_to_target_app', 'observation_rate', 'correct_attribution_rate')
    score['groups'] = {key: numeric_fields(obj(legacy.get('groups')).get(key), group_keys)
                       for key in ('target_success', 'noise_success', 'failed', 'unsupported')}
    diagnostic = obj(result.get('clock')); companion = obj(diagnostic.get('raw_companion')); metadata = obj(diagnostic.get('trace_metadata'))
    header_keys = ('process_trace_mode', 'reserved_flags', 'perf_frequency', 'start_time', 'end_time', 'events_lost', 'buffers_lost')

    def header(value):
        return {key: str(unsigned(obj(value).get(key))) if unsigned(obj(value).get(key)) is not None else None for key in header_keys}

    def bracket(value):
        return {key: str(unsigned(obj(value).get(key))) if unsigned(obj(value).get(key)) is not None else None
                for key in ('qpc_before', 'filetime_ticks', 'qpc_after')}

    public_truth = []
    for index, call in enumerate(truth):
        clock = obj(call.get('clock'))
        public_truth.append(dict(row=index, operation=call.get('operation') if call.get('operation') in CLOCK.SUPPORTED else 'unsupported',
            role=call.get('role') if call.get('role') in {'root', 'worker', 'short', 'detached', 'unrelated', 'noise', 'service', 'parent', 'child', 'grandchild'} else None,
            pid=call.get('pid') if type(call.get('pid')) is int else None,
            creation_time=str(unsigned(call.get('creation_time'))) if unsigned(call.get('creation_time')) is not None else None,
            thread_id=unsigned(clock.get('thread_id')), resource=resource(call.get('resource')), destination=resource(call.get('destination')),
            success=call.get('success') if type(call.get('success')) is bool else None,
            clock={key: str(unsigned(clock.get(key))) if unsigned(clock.get(key)) is not None else None
                   for key in ('start_qpc', 'end_qpc', 'qpc_frequency', 'sequence')},
            clock_before=bracket(clock.get('before')), clock_after=bracket(clock.get('after')),
            filetime={key: str(unsigned(call.get(key))) if unsigned(call.get(key)) is not None else None for key in ('start_ticks', 'end_ticks')}))
    safe_checks = {}
    for key, row in obj(obj(result.get('integrity')).get('checks')).items():
        actual = row.get('actual')
        safe_checks[key] = dict(actual=actual if type(actual) in (int, bool) or actual in (None, 'finished', 'active') else None,
                                satisfied=row.get('satisfied') is True)
    safe_reasons = list(result.get('missing_evidence', [])) + list(result.get('join_issues', [])) + list(result.get('partial_observation_issues', []))
    clock_reasons = diagnostic.get('source_reasons', []) + companion.get('reasons', [])
    provenance_reason = obj(diagnostic.get('clock_provenance')).get('reason')
    if provenance_reason is not None:
        clock_reasons.append(provenance_reason)
    safe_reasons.extend(reason if isinstance(reason, str) and reason in CLOCK_REASON_CODES else 'private_clock_detail_retained' for reason in clock_reasons)
    manifest = obj(result.get('observed_raw_manifest'))
    manifest_stats = ('received', 'stored', 'accepted', 'queued', 'queue_dropped', 'quota_dropped', 'not_persisted', 'missing_event_key',
                      'invalid_timestamp', 'capacity_dropped', 'writer_closed_dropped', 'filtered_nonprimary', 'readiness_probe_records',
                      'duplicate_event_keys', 'disk_byte_capacity', 'byte_capacity', 'bytes')
    snapshot = obj(result.get('snapshot'))
    public_snapshot = {}
    for phase in ('source_pre_open', 'source_before', 'source_after', 'source_after_close'):
        if phase in snapshot:
            public_snapshot[phase] = {kind: dict(exists=obj(obj(snapshot[phase]).get(kind)).get('exists') is True,
                bytes=unsigned(obj(obj(snapshot[phase]).get(kind)).get('bytes')),
                sha256=safe_hash(obj(obj(snapshot[phase]).get(kind)).get('sha256'))) for kind in ('database', 'wal')}
    for field in ('source_db_wal_unchanged', 'empty_wal_initialized_by_read_open'):
        if field in snapshot:
            public_snapshot[field] = snapshot[field] if type(snapshot[field]) is bool else None
    for field in ('snapshot_sha256', 'raw_export_sha256'):
        if field in snapshot:
            public_snapshot[field] = safe_hash(snapshot[field])
    partial_clock_rows = []
    if result.get('existing_clock_report_input_hashes_verified') is True:
        for row in obj(result.get('private_existing_clock_report')).get('truth_rows', []):
            partial_clock_rows.append(dict(row=unsigned(row.get('row')),
                status=row.get('status') if row.get('status') in {'unique_interval_candidate', 'ambiguous_interval_candidates',
                    'no_in_interval_source', 'unavailable', 'invalid', 'unsupported', 'interval_candidates'} else None,
                reason=row.get('reason') if row.get('reason') in CLOCK_REASON_CODES else None,
                event_ids=[safe_id(event_id) for event_id in row.get('event_ids', [])],
                **{key: str(unsigned(row.get(key))) if unsigned(row.get(key)) is not None else None
                   for key in ('start_qpc', 'end_qpc', 'qpc_frequency')}))
    return dict(schema_version=1, protocol=PROTOCOL, source_sha=result.get('source_sha'),
        phase=result.get('phase') if result.get('phase') in {'diagnostic', 'verification'} else None,
        planned_attempts=result.get('planned_attempts') if type(result.get('planned_attempts')) is int else None,
        attempt_index=result.get('attempt_index') if type(result.get('attempt_index')) is int else None, acceptance_pass=None,
        raw_join_status=result.get('raw_join_status', 'unavailable'),
        events_view_scope=view_scope, clock_observation_scope=result.get('clock_observation_scope'),
        existing_clock_report_input_hashes_verified=result.get('existing_clock_report_input_hashes_verified') is True,
        existing_final_view_clock_rows=partial_clock_rows,
        diagnostic_evidence_complete=result.get('diagnostic_evidence_complete', result.get('clock_evidence_complete', False)),
        legacy_score=score, legacy_score_recomputed_equal=result.get('legacy_score_recomputed_equal') is True,
        legacy_score_recomputation_status=result.get('legacy_score_recomputation_status', 'not_run'),
        identity=result.get('public_identity', {}), input_sha256=result.get('input_sha256', {}), missing_evidence=sorted(set(safe_reasons)),
        events=published, independent_truth=public_truth,
        supported_events_private=len(result['relations']) if 'relations' in result else None,
        supported_events_public=len(published) if 'events' in result or 'partial_final_view_events' in result else None,
        integrity=safe_checks, snapshot=public_snapshot,
        raw_manifest_observation=dict(scope='manifest_fields_only_not_event_links', present=bool(manifest),
            header=header(manifest.get('header')), stats=numeric_fields(manifest.get('stats'), manifest_stats),
            writer=numeric_fields(manifest.get('writer'), ('finished', 'drained', 'timed_out', 'cancel_requested', 'records', 'bytes', 'partial_record_bytes'))),
        clock=dict(original_qpc_links=companion.get('linked_events'), primary_events_without_link=companion.get('primary_events_without_link'),
                   source_events_without_qpc=diagnostic.get('source_events_without_raw_qpc'),
                   same_machine_boot_verified=diagnostic.get('same_machine_boot_provenance_verified') is True,
                   primary_header=header(metadata.get('trace_header')),
                   companion_header=header(obj(metadata.get('raw_companion')).get('header')),
                   qpc_frequency=str(unsigned(metadata.get('qpc_frequency'))) if unsigned(metadata.get('qpc_frequency')) is not None else None,
                   calibrations=[dict(phase=row.get('phase'), **bracket(row)) for row in metadata.get('calibrations', [])
                                 if isinstance(row, dict) and row.get('phase') in {'trace_start_requested', 'consumer_ready', 'trace_stop_requested', 'consumer_stopped'}],
                   consumer_loss_counters_available=companion.get('consumer_loss_counters_available'),
                   companion_stats=numeric_fields(obj(metadata.get('raw_companion')).get('stats'),
                       ('received', 'stored', 'accepted', 'queued', 'queue_dropped', 'quota_dropped', 'not_persisted', 'missing_event_key',
                        'invalid_timestamp', 'capacity_dropped', 'writer_closed_dropped', 'filtered_nonprimary', 'readiness_probe_records',
                        'duplicate_event_keys', 'disk_byte_capacity', 'byte_capacity', 'bytes')),
                   writer=numeric_fields(obj(metadata.get('raw_companion')).get('writer'),
                       ('finished', 'drained', 'timed_out', 'cancel_requested', 'records', 'bytes', 'partial_record_bytes', 'write_calls', 'drain_elapsed_ns')),
                   metadata_writer=numeric_fields(obj(metadata.get('raw_companion')).get('metadata_writer'),
                       ('finished', 'drained', 'timed_out', 'cancel_requested', 'bytes', 'write_calls', 'drain_elapsed_ns'))),
        definition='Fixed fixture-only projection; resource aliases preserve equality without host paths. Original clock links and exact integer intervals are observations, not causal matches or acceptance. Original scores and all additional positives remain separate. No tolerance, inverse conversion or truth-derived actor.')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def database_files(database):
    """Database/WAL bytes, not mutable shared-memory reader coordination."""
    return {name: dict(exists=path.exists(), bytes=path.stat().st_size if path.exists() else None,
                       sha256=sha(path) if path.exists() else None)
            for name, path in (('database', database), ('wal', Path(str(database) + '-wal')))}


def snapshot_export(database, exported, snapshot, destination, observation):
    """Backup the committed SQLite read snapshot, then reuse the strict exporter.

    The source is never immutable and its journal mode is never changed. SQLite
    may initialize an empty WAL/SHM for read coordination; that observation is
    kept separately from the DB/WAL hashes bracketing the pinned read snapshot.
    """
    require(not snapshot.exists() and not destination.exists(), 'snapshot_or_export_destination_exists')
    require(database.is_file(), 'source_database_missing')
    require(not any(Path(str(snapshot) + suffix).exists() for suffix in ('-wal', '-shm', '-journal')), 'snapshot_sidecar_destination_exists')
    session = read(exported)['data']['id']
    pre_open = database_files(database)
    observation.update(source_pre_open=pre_open, source_db_wal_unchanged=False,
                       source_open_mode='mode=ro', snapshot_method='SQLite_backup_of_pinned_read_transaction')
    with closing(sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True, timeout=5)) as source:
        source.execute('PRAGMA query_only=ON')
        source.execute('BEGIN')
        state = source.execute('SELECT state FROM capture_runs WHERE session_id=?', (session,)).fetchall()
        require(state == [('finished',)], 'source_session_not_uniquely_finished')
        before = database_files(database)
        empty_wal_initialized = (not pre_open['wal']['exists'] and before['wal']['exists'] and before['wal']['bytes'] == 0)
        require(pre_open['database'] == before['database'] and (pre_open['wal'] == before['wal'] or empty_wal_initialized), 'source_database_changed_before_snapshot')
        observation.update(source_before=before, empty_wal_initialized_by_read_open=empty_wal_initialized)
        # Reserve a new owned destination; never open an old database for writing.
        with snapshot.open('xb'):
            pass
        deadline = time.monotonic() + 30

        def progress(status, remaining, total):
            require(time.monotonic() <= deadline, 'snapshot_backup_timeout')

        with closing(sqlite3.connect(snapshot)) as target:
            source.backup(target, pages=256, progress=progress, sleep=0.01)
            # WAL mode is a database header property copied by backup. This only
            # changes our new snapshot, so the unchanged immutable exporter can
            # safely read it without ignoring the original source's live WAL.
            require(target.execute('PRAGMA journal_mode=DELETE').fetchone() == ('delete',), 'snapshot_journal_finalize_failed')
            require(target.execute('SELECT state FROM capture_runs WHERE session_id=?', (session,)).fetchall() == [('finished',)], 'snapshot_session_not_uniquely_finished')
        after = database_files(database)
        observation['source_after'] = after
        require(before == after, 'source_database_changed_during_snapshot')
        source.rollback()
    closed = database_files(database)
    observation['source_after_close'] = closed
    require(before == closed, 'source_database_changed_after_snapshot')
    observation.update(source_db_wal_unchanged=True, snapshot_sha256=sha(snapshot))
    EXPORT.export_raw(snapshot, exported, destination)
    observation['raw_export_sha256'] = sha(destination)
    return observation


def retain_partial(result, paths, sid):
    """Preserve independent observations before a later raw-export failure.

    This does not make a final event a raw record. The separately labelled
    existing clock report is bound to its capture/truth/manifest and script.
    """
    loaded = {}
    for name in ('capture', 'truth', 'legacy_score', 'raw_clock', 'clock_report'):
        path = paths[name]
        if not path.is_file():
            continue
        try:
            loaded[name] = read(path)
        except (OSError, ValueError, TypeError) as error:
            result['private_errors'].append(dict(stage=name, type=type(error).__name__, detail=str(error)))
            if name != 'clock_report':
                result['missing_evidence'].append(name + '_unreadable')
    capture = obj(loaded.get('capture'))
    capture = obj(capture.get('data', capture))
    truth = loaded.get('truth')
    legacy = loaded.get('legacy_score')
    if isinstance(truth, list):
        result['truth'] = truth
    if isinstance(legacy, dict):
        result['legacy_score'] = legacy
    result.update(raw_join_status='unavailable', existing_clock_report_input_hashes_verified=False,
                  clock_observation_scope='capture_metadata_only_no_event_link_audit', private_sid=sid)
    if capture:
        result.update(watch_roots=capture.get('watch_roots', []), registry_key=capture.get('registry_key'),
                      partial_final_view_events=capture.get('events', []), integrity=integrity(capture),
                      clock=dict(trace_metadata=obj(capture.get('backend')).get('clock', {})))
    if capture and isinstance(truth, list) and truth and isinstance(legacy, dict):
        try:
            recomputed = SCORE.score(capture, truth, sid)
            result.update(legacy_score_recomputed=recomputed, legacy_score_recomputed_equal=legacy == recomputed,
                          legacy_score_recomputation_status='equal' if legacy == recomputed else 'mismatch')
        except (KeyError, TypeError, ValueError) as error:
            result['private_errors'].append(dict(stage='legacy_score', type=type(error).__name__, detail=str(error)))
            result['legacy_score_recomputation_status'] = 'unavailable'
    if isinstance(loaded.get('raw_clock'), dict):
        result['observed_raw_manifest'] = loaded['raw_clock']
        result['raw_manifest_observation_scope'] = 'manifest_fields_observed_without_event_link_audit'
    report = loaded.get('clock_report')
    if isinstance(report, dict):
        expected = dict(capture=result['input_sha256'].get('capture'), truth=[result['input_sha256'].get('truth')],
                        raw_clock=result['input_sha256'].get('raw_clock'))
        recorded = obj(report.get('input_sha256'))
        script_hash = hashlib.sha256(Path(CLOCK.__file__).read_bytes().replace(b'\r\n', b'\n')).hexdigest()
        bound = (all(value is not None and value != [None] for value in expected.values())
                 and all(recorded.get(key) == value for key, value in expected.items())
                 and report.get('protocol_revision') == CLOCK.PROTOCOL_REVISION
                 and report.get('script_lf_sha256') == script_hash)
        result['existing_clock_report_input_hashes_verified'] = bound
        result['private_existing_clock_report'] = report
        if bound:
            result.update(clock=report, clock_observation_scope='existing_final_view_only')
        else:
            result['partial_observation_issues'] = ['existing_clock_report_input_binding_mismatch']


def identities(attempt, identity, directory):
    reasons = []
    revision = attempt.get('source_sha')
    if not isinstance(revision, str) or not re.fullmatch(r'[0-9a-f]{40}', revision):
        reasons.append('source_revision_missing')
        revision = None
    phase, planned, index = attempt.get('phase'), attempt.get('planned_attempts'), attempt.get('attempt_index', 1)
    if (type(planned) is not int or type(index) is not int or
            not (phase == 'diagnostic' and planned == 1 and index == 1 or phase == 'verification' and planned == 2 and index in (1, 2))):
        reasons.append('fixed_diagnostic_plan_missing')
    if (attempt.get('source_after_sha') != revision or attempt.get('source_unchanged') is not True
            or attempt.get('source_clean_before') is not True or attempt.get('source_clean_after') is not True
            or attempt.get('source_status_before') != [] or attempt.get('source_status_after') != []):
        reasons.append('source_changed_during_diagnostic')
    if attempt.get('demo_started') is not True:
        reasons.append('diagnostic_demo_not_started')
    if attempt.get('fixture_clock_requested') != '1' or attempt.get('raw_clock_requested') is not True:
        reasons.append('both_clocks_not_requested')
    if identity.get('fixture_clock') != '1' or Path(identity.get('raw_clock_path', '')).resolve() != (directory / 'raw-clock.json').resolve():
        reasons.append('actual_clock_request_mismatch')
    source_before, source_after = (obj(identity.get(key)) for key in ('source_before_build', 'source_after_build'))
    if (identity.get('source_stable_during_build') is not True or source_before.get('head') != revision or source_after.get('head') != revision
            or not safe_hash(source_before.get('runtime_tree_sha256')) or source_before.get('runtime_tree_sha256') != source_after.get('runtime_tree_sha256')
            or source_before.get('status') != [] or source_after.get('status') != [] or identity.get('build_exit_code') != 0):
        reasons.append('source_or_build_identity_unverified')
    before, after = attempt.get('scripts_before'), attempt.get('scripts_after')
    scripts = []
    if not isinstance(before, list) or not before or before != after or attempt.get('scripts_unchanged') is not True:
        reasons.append('script_identity_unverified')
    else:
        required_scripts = {'scripts/' + name for name in ('ci-etw-clock.ps1', 'demo.ps1', 'demo-provenance.ps1',
            'demo-measure.py', 'analyze-etw-clock.py', 'v04-clock-protocol.py', 'run-write-diagnostic.py',
            'verify-layered-evidence.py', 'analyze-write-diagnostic.py', 'score-fixture.py', 'score-fixture.ps1')}
        reported_paths = [row.get('path') for row in before]
        if len(set(reported_paths)) != len(reported_paths) or not required_scripts <= set(reported_paths):
            reasons.append('required_script_identity_missing_or_duplicate')
        for row in before:
            path = row.get('path', '')
            if not isinstance(path, str) or not re.fullmatch(r'scripts/[a-z0-9-]+\.(?:py|ps1|wprp)', path):
                reasons.append('script_identity_path_invalid'); continue
            digest = safe_hash(row.get('sha256'))
            if not digest or sha(REPO / path) != digest:
                reasons.append('script_identity_changed')
            scripts.append(dict(path=path, sha256=digest))
    binaries = []
    observed = obj(identity.get('binary_after'))
    rows = observed.get('binaries')
    if observed.get('unchanged') is not True or not isinstance(rows, list) or len(rows) < 2:
        reasons.append('binary_identity_unverified')
    else:
        if [row.get('before') for row in rows] != identity.get('binary_before'):
            reasons.append('binary_before_after_binding_mismatch')
        names = set()
        for row in rows:
            first, last = obj(row.get('before')), obj(row.get('after'))
            name = PureWindowsPath(first.get('path', '')).name.lower()
            names.add(name)
            digest = safe_hash(first.get('sha256'))
            if (row.get('unchanged') is not True or row.get('error') is not None or not digest or digest != safe_hash(last.get('sha256'))
                    or first.get('path') != last.get('path') or first.get('bytes') != last.get('bytes')
                    or type(first.get('bytes')) is not int or first['bytes'] <= 0):
                reasons.append('binary_identity_changed')
            binaries.append(dict(name=name if name in {'contain.exe', 'contain-test-installer.exe', 'contain-registry-noise.exe'} else None,
                                 sha256=digest, bytes=first.get('bytes') if type(first.get('bytes')) is int else None,
                                 unchanged=row.get('unchanged') is True))
        if not {'contain.exe', 'contain-test-installer.exe'} <= names:
            reasons.append('required_binary_identity_missing')
    return revision, reasons, dict(source_tree_sha256=safe_hash(source_before.get('runtime_tree_sha256')),
                                  scripts=scripts, binaries=binaries, demo_exit_code=attempt.get('demo_exit_code') if type(attempt.get('demo_exit_code')) is int else None,
                                  loaded_image_attestation=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--public-output', type=Path, required=True)
    args = parser.parse_args()
    directory, public = args.directory.resolve(), args.public_output.resolve()
    require(directory != public and directory not in public.parents and public not in directory.parents, 'private_public_directories_must_be_separate')
    require(not public.exists() or not any(public.iterdir()), 'public_output_must_be_new_or_empty')
    private_output = directory / 'clock-analysis.json'
    require(not private_output.exists(), 'analysis_already_exists_preserve_original_attempt')
    result = dict(schema_version=1, protocol=PROTOCOL, acceptance_pass=None, clock_evidence_complete=False,
                  diagnostic_evidence_complete=False, raw_join_status='unavailable', missing_evidence=[], input_sha256={}, private_errors=[])
    stage = 'input_manifest_unavailable'
    try:
        paths = {name: directory / filename for name, filename in dict(attempt='attempt.json', identity='capture/run-identity.json',
            capture='capture/capture.json', truth='capture/ground-truth.json', legacy_score='capture/reliability.json', raw_clock='raw-clock.json',
            clock_report='capture/clock-diagnostic.json').items()}
        for key, path in paths.items():
            if path.is_file():
                result['input_sha256'][key] = sha(path)
            elif key != 'clock_report':
                result['missing_evidence'].append(key + '_file_missing')
        attempt = read(paths['attempt']) if paths['attempt'].is_file() else {}
        retain_partial(result, paths, attempt.get('sid', ''))
        identity = read(paths['identity'])
        revision, identity_reasons, public_identity = identities(attempt, identity, directory)
        result.update(source_sha=revision, public_identity=public_identity, phase=attempt.get('phase'),
                      planned_attempts=attempt.get('planned_attempts'), attempt_index=attempt.get('attempt_index', 1))
        result['missing_evidence'].extend(identity_reasons)
        private_capture = directory / 'capture/private-raw.json'
        stage = 'raw_export_failed'
        result['snapshot'] = {}
        snapshot_export(Path(identity['database']), paths['capture'], directory / 'capture/private-snapshot.db', private_capture, result['snapshot'])
        result['input_sha256']['private_raw_export'] = sha(private_capture)
        result['input_sha256']['database_snapshot'] = result['snapshot']['snapshot_sha256']
        stage = 'capture_or_truth_unavailable'
        capture = read(private_capture)['data']
        truth, legacy_score = read(paths['truth']), read(paths['legacy_score'])
        raw_bytes = paths['raw_clock'].read_bytes() if paths['raw_clock'].is_file() else None
        stage = 'clock_analysis_failed'
        analysis = analyze(capture, truth, legacy_score, raw_bytes, paths['raw_clock'], attempt.get('sid', ''))
        result.update(analysis)
        result['raw_join_status'] = 'verified_same_event_id' if not analysis['join_issues'] else 'rejected'
        result['clock_observation_scope'] = 'raw_and_final_same_id' if not analysis['join_issues'] else 'raw_view_join_rejected'
        result['legacy_score_recomputation_status'] = 'equal' if analysis['legacy_score_recomputed_equal'] else 'mismatch'
        if raw_bytes is not None:
            sidecar = json.loads(raw_bytes.decode('utf-8-sig'))
            if sidecar.get('schema_version') == 2:
                binary = paths['raw_clock'].with_name(paths['raw_clock'].name + '.rows.bin')
                if binary.is_file():
                    result['input_sha256']['raw_clock_binary'] = sha(binary)
        if not result['legacy_score_recomputed_equal']:
            result['missing_evidence'].append('original_score_recomputation_mismatch')
        result['diagnostic_evidence_complete'] = result['clock_evidence_complete'] and not result['missing_evidence']
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, sqlite3.Error) as error:
        export_errors = {
            'Unexpected database sidecars; preserve them for a separate recovery audit': 'database_sidecars_present',
            'Capture database is not finished': 'database_not_finished',
            'Small-diagnostic raw export limit exceeded; no partial chain claimed': 'raw_export_budget_exceeded',
            'Database changed during read-only export': 'database_changed_during_export',
        }
        snapshot_codes = {'snapshot_or_export_destination_exists', 'source_database_missing', 'snapshot_sidecar_destination_exists',
                          'source_session_not_uniquely_finished', 'source_database_changed_before_snapshot', 'snapshot_backup_timeout',
                          'snapshot_journal_finalize_failed', 'snapshot_session_not_uniquely_finished', 'source_database_changed_during_snapshot',
                          'source_database_changed_after_snapshot'}
        reason = str(error) if str(error) in snapshot_codes else export_errors.get(str(error), stage) if stage == 'raw_export_failed' else stage
        result['missing_evidence'].append(reason)
        result['private_errors'].append(dict(type=type(error).__name__, detail=str(error)))
    with private_output.open('x', encoding='utf-8') as output:
        json.dump(result, output, indent=2, allow_nan=False); output.write('\n')
    public.mkdir(parents=True, exist_ok=True)
    published = public_summary(result)
    with (public / 'summary.json').open('x', encoding='utf-8') as output:
        json.dump(published, output, indent=2, allow_nan=False); output.write('\n')
    print(json.dumps(dict(protocol=PROTOCOL, diagnostic_evidence_complete=result['diagnostic_evidence_complete'], acceptance_pass=None,
                          missing_evidence=published['missing_evidence'])))
    return 0 if result['diagnostic_evidence_complete'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
