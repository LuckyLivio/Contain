"""Export only existing numeric source/snapshot accounting; no new collection."""
import argparse
import hashlib
import json
import re
from pathlib import Path

PROVIDER_FIELDS = 'callback_records identity_queries unresolved enqueued filtered overflow disconnected decode_failed context_evictions'.split()
SNAPSHOT_COUNTS = '''enumerated queried resolved failed skipped query_ns elapsed_ns thread_enumerated thread_queried thread_resolved thread_failed thread_outside_boundary thread_query_ns thread_unqueried'''.split()
PROVIDER_TIMES = 'callback lock_wait schema_lookup property_decode identity_query path_normalization event_construction enqueue queue_delay context'.split()
ETW_SAMPLE_FIELDS = '''elapsed_ns query_elapsed_ns control_error events_lost realtime_buffers_lost log_buffers_lost allocated_buffers free_buffers buffer_size_kib min_buffers max_buffers buffers_written callback_records callback_elapsed_ns lock_wait_ns'''.split()
ETW_SESSION_FIELDS = 'requested_buffer_size_kib requested_min_buffers requested_max_buffers stop_control_elapsed_ns consumer_join_elapsed_ns'.split()
DRAIN_FIELDS = 'elapsed_ms quiet_ms pending_verified_instances lifecycle_context_lost source_records_during_wait'.split()
DRAIN_REASONS = {'quiet_and_no_pending_process_exit', 'deadline_lifecycle_context_lost',
                 'deadline_pending_process_exit', 'deadline_recent_activity'}


def number(value):
    return value if type(value) is int and value >= 0 else None


def drain_summary(value):
    if not isinstance(value, dict):
        return None
    result = {key: number(value.get(key)) for key in DRAIN_FIELDS}
    reason = value.get('stop_reason')
    result['stop_reason'] = reason if isinstance(reason, str) and reason in DRAIN_REASONS else None
    examples = value.get('pending_examples')
    result['pending_instance_hashes'] = None
    result['examples_omitted'] = None
    if isinstance(examples, list):
        hashes = []
        for row in examples[:8]:
            if not isinstance(row, dict):
                continue
            pid, birth = row.get('pid'), row.get('creation_time')
            if type(pid) is int and 0 < pid <= 0xffffffff and isinstance(birth, str) \
                    and re.fullmatch(r'[0-9]{1,20}', birth) and 0 < int(birth) <= 0xffffffffffffffff:
                hashes.append(hashlib.sha256(f'{pid}:{int(birth)}'.encode('ascii')).hexdigest())
        result['pending_instance_hashes'] = hashes
        pending = result['pending_verified_instances']
        result['examples_omitted'] = max(len(examples), pending or 0) - len(hashes)
    return result


def summarize(capture):
    if (capture.get('schema_version') != 3 or capture.get('kind') != 'inspect'
            or not isinstance(capture.get('data'), dict)):
        raise ValueError('inspect_envelope_required')
    backend = capture['data'].get('backend') or {}
    pipeline = backend.get('pipeline') or {}
    providers = pipeline.get('providers') or {}
    session = pipeline.get('etw_session')
    clean_session = None
    if isinstance(session, dict):
        clean_session = {key: number(session.get(key)) for key in ETW_SESSION_FIELDS}
        for phase in ('ready', 'before_stop', 'stopped'):
            sample = session.get(phase)
            clean_session[phase] = {key: number(sample.get(key)) for key in ETW_SAMPLE_FIELDS} if isinstance(sample, dict) else None
    snapshot = backend.get('initial_identity_snapshot')
    clean_snapshot = None
    if isinstance(snapshot, dict):
        clean_snapshot = {key: number(snapshot.get(key)) for key in SNAPSHOT_COUNTS}
        for key in ('thread_enumeration_complete', 'budget_exhausted'):
            value = snapshot.get(key)
            clean_snapshot[key] = value if type(value) is bool else None
        # Error keys are fixed labels produced by the source, never error text.
        errors = snapshot.get('query_errors')
        clean_snapshot['query_errors'] = None
        clean_snapshot['omitted_error_labels'] = None
        if isinstance(errors, dict):
            clean_snapshot['query_errors'] = {key: number(value) for key, value in errors.items()
                if re.fullmatch(r'(?:process_capacity|thread_ended_or_changed|thread_(?:snapshot|query|enumeration)_win32_[0-9]{1,10})', key)}
            clean_snapshot['omitted_error_labels'] = len(errors) - len(clean_snapshot['query_errors'])
    return dict(schema_version=1, protocol='contain-existing-source-facts-1',
        providers={key: {field: number(providers[key].get(field)) for field in PROVIDER_FIELDS}
            if isinstance(providers.get(key), dict) else None for key in ('file', 'process', 'registry')},
        provider_elapsed_ns={key: {field: number((providers[key].get('elapsed_ns') or {}).get(field)) for field in PROVIDER_TIMES}
            if isinstance(providers.get(key), dict) and isinstance(providers[key].get('elapsed_ns'), dict) else None for key in ('file', 'process', 'registry')},
        etw_session=clean_session,
        drain=drain_summary((capture['data'].get('stats') or {}).get('drain')),
        initial_identity_snapshot=clean_snapshot,
        limitations=['Existing saved fields only; absent historical fields remain null.',
            'Enumeration completion is not complete identity coverage; query failures and observation boundaries still apply.',
            'ETW samples are cumulative within one trace; do not add phases, events and buffers, or application drops.',
            'Drain describes the pre-STOP quiet/process wait, not queued-record completion; instance examples are hashed and capped at eight.',
            'No paths, arbitrary error text, or host process/thread inventory are exported.'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--capture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        result = summarize(json.loads(args.capture.read_text(encoding='utf-8-sig')))
        args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    except (OSError, ValueError, TypeError, AttributeError):
        raise SystemExit('source_capture_facts_summary_failed') from None


if __name__ == '__main__':
    main()
