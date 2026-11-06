"""Bounded numeric receiver profile report; never export payloads or paths."""
import argparse
import json
from pathlib import Path

STAGES = tuple('receiver_' + name for name in (
    'drain', 'idle_wait', 'lock_wait', 'lifecycle_ingest', 'page_split',
    'follow_cache_attach', 'follow_register', 'follow_classify', 'follow_pending_scan', 'handoff'))
FIELDS = ('count', 'elapsed_ns', 'max_elapsed_ns', 'units', 'enqueued', 'overflow', 'dequeued',
          'max_pending_before', 'max_pending_after', 'cpu_count', 'cpu_ns')
COUNTERS = '''receiver_pages receiver_page_records receiver_lifecycle_records follow_receive_pages follow_incoming_records follow_scan_calls follow_scan_visits follow_dirty_bind follow_dirty_cache follow_dirty_resource follow_dirty_request follow_dirty_completion follow_dirty_related_removed follow_dirty_flush follow_drop_serialization follow_drop_record_bytes follow_drop_pending_records follow_drop_pending_bytes follow_drop_resources follow_drop_requests follow_drop_pending_before_bind follow_drop_pending_no_identity follow_drop_pending_unverified_actor follow_pending_before_bind_attempts follow_pending_no_identity_attempts follow_pending_unverified_actor_attempts'''.split()


def number(v):
    return v if type(v) is int and v >= 0 else None


def summarize(profile, capture):
    backend = capture.get('data', capture).get('backend', {})
    pipeline = backend.get('pipeline') or {}
    table = profile.get('queue_stage_totals') or {}
    stages = {}
    for name in STAGES:
        row = table.get(name)
        if not isinstance(row, dict):
            stages[name] = None
            continue
        clean = {k: number(row.get(k)) for k in FIELDS}
        if not clean['cpu_count']:
            clean['cpu_ns'] = None
        stages[name] = clean
    wall = sum(row['elapsed_ns'] or 0 for row in stages.values() if row)
    cpu = sum(row['cpu_ns'] or 0 for row in stages.values() if row)
    assigned = sum(row['overflow'] or 0 for row in stages.values() if row)
    source_loss = number(pipeline.get('queue_overflow'))
    for row in stages.values():
        if row:
            row['wall_share_percent'] = 100 * row['elapsed_ns'] / wall if wall and row['elapsed_ns'] is not None else None
            row['cpu_share_percent'] = 100 * row['cpu_ns'] / cpu if cpu and row['cpu_ns'] is not None else None
    counters = profile.get('counters') or {}
    drop_names = ('serialization', 'record_bytes', 'pending_records', 'pending_bytes', 'resources', 'requests')
    drop_values = [number(counters.get('follow_drop_' + k)) for k in drop_names]
    drop_sum = sum(drop_values) if all(v is not None for v in drop_values) else None
    saved_candidate_loss = number((backend.get('follow_installer') or {}).get('candidate_dropped'))
    windows = []
    for span in (profile.get('queue_spans') or [])[:8192]:
        if not isinstance(span, dict) or span.get('stage') not in STAGES:
            continue
        before, after = span.get('before') or {}, span.get('after') or {}
        start, end = number(span.get('start_ns')), number(span.get('end_ns'))
        if start is None or end is None or end < start:
            continue
        enq = (number(after.get('enqueued')), number(before.get('enqueued')))
        lost = (number(after.get('overflow')), number(before.get('overflow')))
        if any(v is None for v in enq + lost) or enq[0] < enq[1] or lost[0] < lost[1]:
            continue
        windows.append(dict(stage=span['stage'], duration_ns=end-start, cpu_ns=number(span.get('cpu_ns')),
            enqueued=enq[0]-enq[1], overflow=lost[0]-lost[1],
            pending_before=number(before.get('pending')), pending_after=number(after.get('pending'))))
    windows.sort(key=lambda row: (row['overflow'], row['duration_ns']), reverse=True)
    return dict(schema_version=1, protocol='contain-receiver-profile-summary-1',
        measurement_kind='instrumented_real_etw_diagnostic', stage_totals_available=any(stages.values()),
        exclusive_receiver_stages=stages, counters={k: number(counters.get(k)) for k in COUNTERS},
        candidate_drop_reason_sum=drop_sum, saved_candidate_dropped=saved_candidate_loss,
        candidate_drop_accounting_matches=drop_sum == saved_candidate_loss if drop_sum is not None and saved_candidate_loss is not None else None,
        sampled_largest_loss_windows=windows[:8],
        source_queue_overflow=source_loss, assigned_exclusive_overflow=assigned,
        unassigned_overflow=source_loss-assigned if source_loss is not None and assigned <= source_loss else None,
        overlapping_or_inconsistent_overflow=source_loss is not None and assigned > source_loss,
        arrival_peak_per_100ms=number(pipeline.get('arrival_peak_per_100ms')),
        queue_high_water=number(pipeline.get('queue_high_water')),
        queue_max_delay_ns=number(pipeline.get('queue_max_delay_ns')),
        omitted_queue_spans=number(profile.get('omitted_queue_spans')),
        omitted_counter_names=number(profile.get('omitted_counter_names')),
        limitations=['Wall time includes scheduling/preemption; CPU time is separate and can be coarse.',
            'Only listed exclusive receiver stages are added; nested legacy timers are not added.',
            'Profile-on/off can change timing; this is diagnosis, not final ZIP acceptance.',
            'Full stage totals survive sample truncation; omitted samples remain explicitly reported.'])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--profile', type=Path, required=True)
    p.add_argument('--capture', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    try:
        value = summarize(json.loads(args.profile.read_text(encoding='utf-8-sig')), json.loads(args.capture.read_text(encoding='utf-8-sig')))
        args.output.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')
    except (OSError, ValueError, TypeError, AttributeError):
        raise SystemExit('receiver_profile_summary_failed') from None


if __name__ == '__main__':
    main()
