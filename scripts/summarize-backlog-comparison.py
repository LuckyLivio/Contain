"""Safe bounded observation report, independent of the strict no-loss release oracle.

A loss/Unknown result remains a failed strict oracle. It can still be a valid
backlog measurement if independent work, attribution safety and accounting hold.
"""
import argparse
import importlib.util
import json
import math
from pathlib import Path


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


PAIR = module('backlog_pair', 'summarize-capture-loss-pair.py')
ORACLE = PAIR.ORACLE
ORDER = ['r1/baseline', 'r1/candidate', 'r2/candidate', 'r2/baseline']
STAGES = tuple('receiver_' + name for name in (
    'drain', 'idle_wait', 'lock_wait', 'lifecycle_ingest', 'page_split', 'follow_cache_attach',
    'follow_register', 'follow_classify', 'follow_pending_scan', 'handoff',
    'follow_pending_final', 'follow_cache_attach_stop', 'follow_page_observation'))
SNAPSHOT = 'enqueued overflow dequeued pending high_water max_delay_ns first_backlog_ns first_overflow_ns'.split()
WINDOW_FLAGS = ('pre_capacity_full', 'post_capacity_full', 'trigger_before_retained_context', 'trigger_contained_by_completed_span')
WINDOW_FIELDS = 'trigger_ns observed_at_ns detection_lag_ns queue_depth pre_records post_records earlier_observations_omitted later_observations_omitted'.split()
TOP_FIELDS = 'backlog_threshold_records pre_capacity post_capacity maximum_records_per_window receiver_observations first_backlog_ns first_overflow_ns source_high_water source_max_delay_ns'.split()
FOLLOW_FIELDS = 'pending_records pending_bytes scan_visits'.split()
DROP_ORDER = ['serialization', 'record_bytes', 'pending_records', 'pending_bytes', 'resources', 'requests']
PENDING_ORDER = ['before_bind', 'no_identity', 'unverified_actor']


def number(value):
    return value if type(value) is int and value >= 0 else None


def safe_window(raw):
    if raw is None:
        return None, None
    if not isinstance(raw, dict):
        return None, 'window_not_object'
    result = {name: number(raw.get(name)) for name in WINDOW_FIELDS}
    for name in WINDOW_FLAGS:
        result[name] = raw.get(name) if type(raw.get(name)) is bool else None
    records = raw.get('records')
    if not isinstance(records, list) or not 1 <= len(records) <= 129:
        return result, 'window_record_bound_or_missing'
    result['records'] = []
    for item in records:
        if not isinstance(item, dict) or item.get('stage') not in STAGES:
            return result, 'window_unknown_stage'
        clean = dict(stage=item['stage'], **{key: number(item.get(key)) for key in ('start_ns', 'end_ns', 'units', 'cpu_ns')})
        if clean['start_ns'] is None or clean['end_ns'] is None or clean['end_ns'] < clean['start_ns'] or clean['units'] is None:
            return result, 'window_invalid_span'
        for key in ('before', 'after'):
            row = item.get(key) or {}
            clean[key] = {name: number(row.get(name)) for name in SNAPSHOT}
            if any(name not in row for name in SNAPSHOT) or any(clean[key][name] is None for name in SNAPSHOT[:6]):
                return result, 'window_missing_queue_snapshot'
        follow = item.get('follow') or {}
        clean['follow'] = {name: number(follow.get(name)) for name in FOLLOW_FIELDS}
        if any(value is None for value in clean['follow'].values()):
            return result, 'window_missing_follow_snapshot'
        for name, size in (('drops', 6), ('pending_reason_drops', 3), ('pending_attempts', 3)):
            values = follow.get(name)
            if not isinstance(values, list) or len(values) != size or any(number(value) is None for value in values):
                return result, 'window_invalid_follow_counter_array'
            clean['follow'][name] = values.copy()
        result['records'].append(clean)
    if any(result[name] is None for name in WINDOW_FIELDS) or any(result[name] is None for name in WINDOW_FLAGS):
        return result, 'window_missing_numeric_or_boolean_field'
    if not (0 <= result['pre_records'] <= 64 and 0 <= result['post_records'] <= 64
            and result['pre_records'] + 1 + result['post_records'] == len(records)
            and result['pre_capacity_full'] == (result['pre_records'] == 64)
            and result['post_capacity_full'] == (result['post_records'] == 64)):
        return result, 'window_context_accounting_inconsistent'
    if result['trigger_ns'] > result['observed_at_ns'] or result['detection_lag_ns'] != result['observed_at_ns'] - result['trigger_ns']:
        return result, 'window_milestone_time_inconsistent'
    covered = any(record['start_ns'] <= result['trigger_ns'] <= record['end_ns'] for record in result['records'])
    before_all = result['trigger_ns'] < min(record['start_ns'] for record in result['records'])
    if result['trigger_contained_by_completed_span'] != covered or result['trigger_before_retained_context'] != before_all:
        return result, 'window_context_coverage_flags_inconsistent'
    if result['records'][result['pre_records']]['end_ns'] != result['observed_at_ns']:
        return result, 'window_trigger_sample_not_bound'
    return result, None


def safe_profile(raw, pipeline):
    result = dict(available=False, valid=False, error='profile_or_windows_unavailable')
    if not isinstance(raw, dict) or not isinstance(raw.get('backlog_windows'), dict):
        return result
    value = raw['backlog_windows']
    result = dict(available=True, valid=False, protocol=value.get('protocol') if value.get('protocol') == 'contain-receiver-backlog-window-1' else None,
        thread_cpu_enabled=raw.get('thread_cpu_enabled') is True,
        **{name: number(value.get(name)) for name in TOP_FIELDS})
    errors = []
    if result['protocol'] is None or (result['backlog_threshold_records'], result['pre_capacity'], result['post_capacity'], result['maximum_records_per_window']) != (1024, 64, 64, 129):
        errors.append('window_protocol_or_capacity_differs')
    if any(name not in value for name in TOP_FIELDS) or any(result[name] is None for name in TOP_FIELDS if name not in ('first_backlog_ns', 'first_overflow_ns')):
        errors.append('window_top_counter_unavailable')
    if not result['thread_cpu_enabled']:
        errors.append('thread_cpu_flag_unverified')
    if not result['receiver_observations']:
        errors.append('receiver_samples_absent')
    result['drop_reason_order'] = DROP_ORDER
    result['pending_reason_order'] = PENDING_ORDER
    if value.get('drop_reason_order') != DROP_ORDER or value.get('pending_reason_order') != PENDING_ORDER:
        errors.append('fixed_counter_order_differs')
    for name in ('first_backlog', 'first_overflow', 'maximum_backlog'):
        result[name], error = safe_window(value.get(name))
        if error:
            errors.append(error)
    if result['maximum_backlog'] is None:
        errors.append('maximum_window_absent')
    for name in ('first_backlog', 'first_overflow'):
        at = result[name + '_ns']
        if (at is None) != (result[name] is None) or (result[name] is not None and result[name].get('trigger_ns') != at):
            errors.append('first_milestone_window_not_bound')
    for name in ('first_backlog', 'first_overflow'):
        if result[name] is not None and result[name].get('trigger_contained_by_completed_span') is not True:
            errors.append('first_trigger_stage_context_unavailable')
    if number(pipeline.get('queue_overflow')) is None:
        errors.append('saved_overflow_unavailable')
    elif bool(pipeline['queue_overflow']) != (result['first_overflow'] is not None):
        errors.append('saved_overflow_and_window_disagree')
    if number(pipeline.get('queue_high_water')) is None:
        errors.append('saved_high_water_unavailable')
    elif pipeline['queue_high_water'] >= 1024 and result['first_backlog'] is None:
        errors.append('significant_backlog_window_absent')
    result['cpu_samples_available'] = any(record['cpu_ns'] is not None for name in ('first_backlog', 'first_overflow', 'maximum_backlog')
        for record in (result.get(name) or {}).get('records', []))
    result['errors'] = list(dict.fromkeys(errors))
    result['valid'] = not errors
    result['limitations'] = ['Completed receiver spans may overlap; do not sum nested durations.',
        'First backlog is a successful send publishing the entering-time occupancy estimate crossing 1024, not the exact actual occupancy-crossing time.',
        'Producer milestone and later receiver observation are distinct; preserve detection lag and missing pre/post context.',
        'Queue occupancy is a sampled estimate. Queue delay is record age, not a measured thread pause.',
        'Null CPU remains unavailable and zero CPU may reflect coarse Windows accounting; wall minus CPU is not a measured thread pause.',
        'Window allocation/cloning and follow checkpoints can run inside existing live locks; span-end CPU/timing can precede that recorder work, leaving self-cost in gaps or outer spans.',
        'The reviewed offline cost probe disables CPU sampling and does not measure producer callbacks or Windows live-lock contention.',
        'This is a rebuilt diagnostic, not evidence that the original Parallels problem is fixed.']
    return result


def attribution_safety(directory):
    expected = PAIR.read(directory / 'private/expected.json')
    capture = ORACLE.document(directory / 'private/inspect.stdout', 'inspect')
    truth = ORACLE.load_truth(expected['truth_directory'])
    targets = ORACLE.expected_targets(expected)
    problems = 0
    if capture.get('watch_roots') != [] or capture.get('files') != [] or ORACLE.BASELINE_WARNING not in capture.get('warnings', []):
        problems += 1
    for label, target in targets.items():
        for event in ORACLE.candidates(capture, target):
            evidence = event.get('evidence') or {}
            promoted = event.get('confidence') in ORACLE.PROMOTED or evidence.get('rule') in ('InstallerPid', 'DescendantProcess') or evidence.get('session_id') is not None
            if not promoted:
                continue
            if label == 'unrelated':
                problems += 1
                continue
            rows = [row for row in truth if row.get('role') == target['role'] and row.get('operation') == 'write_requested'
                and row.get('success') is True and ORACLE.normalized(row.get('resource', '')) == ORACLE.normalized(target['path'])]
            try:
                valid = len(rows) == 1 and evidence.get('session_id') == capture['id'] \
                    and evidence.get('rule') == ('InstallerPid' if label == 'parent' else 'DescendantProcess') \
                    and ORACLE.decimal(rows[0]['start_ticks']) <= ORACLE.decimal(event['timestamp_ticks']) <= ORACLE.decimal(rows[0]['end_ticks']) \
                    and ORACLE.actor_record(capture, event, rows[0]) is not None
            except (KeyError, TypeError, ValueError):
                valid = False
            if valid and label == 'child':
                parents = [row for row in truth if row.get('role') == 'followparent' and row.get('operation') == 'write_requested' and row.get('success') is True]
                actor = ORACLE.actor_record(capture, event, rows[0])
                valid = len(parents) == 1 and actor.get('parent_pid') == parents[0]['pid'] and actor.get('parent_creation_time') == parents[0]['creation_time']
            if not valid:
                problems += 1
    return dict(verified=True, target_false_promotions=problems)


def summarize_sample(directory):
    directory = Path(directory)
    sample = PAIR.summarize(directory)
    try:
        raw_profile = PAIR.read(directory / 'private/receiver-profile.json')
        profile = safe_profile(raw_profile, sample.get('pipeline') or {})
    except (OSError, ValueError, TypeError):
        profile = dict(available=False, valid=False, error='private_profile_unavailable')
    try:
        safety = attribution_safety(directory)
    except (OSError, ValueError, KeyError, TypeError):
        safety = dict(verified=False, target_false_promotions=None)
    reasons = []
    if sample.get('capture_exit_code') != 0 or type(sample.get('capture_exit_code')) is not int:
        reasons.append('collector_exit_abnormal_or_unverified')
    if sample.get('all_owned_processes_exited') is not True:
        reasons.append('owned_process_exit_unverified')
    if sample.get('workload_complete') is not True or sample.get('background', {}).get('files') != 12000:
        reasons.append('independent_workload_save_or_accounting_unverified')
    if safety.get('verified') is not True or safety.get('target_false_promotions') != 0 or sample.get('background', {}).get('falsely_attributed_events') != 0:
        reasons.append('attribution_safety_failed_or_unverified')
    if profile.get('valid') is not True:
        reasons.append('backlog_measurement_failed_or_unverified')
    return dict(schema_version=1, protocol='contain-receiver-backlog-sample-1', strict_oracle_accepted=sample.get('accepted') is True,
        measurement_valid=not reasons, continuation_stop_reason=reasons[0] if reasons else None, errors=reasons,
        existing_sample=sample, attribution_safety=safety, receiver_backlog=profile)


def summarize(plan, execution, cases):
    valid_plan = plan.get('protocol') == 'contain-receiver-backlog-plan-1' and plan.get('order') == ORDER \
        and plan.get('maximum_native_captures') == 4 and plan.get('retries') == 0 and plan.get('background_files') == 12000 \
        and plan.get('profile') is True and plan.get('backlog_window') is True and plan.get('thread_cpu') is True
    complete = execution.get('attempted') == ORDER and execution.get('not_run') == [] and execution.get('stop_reason') is None
    measurements = valid_plan and complete and all(cases.get(name, {}).get('measurement_valid') is True for name in ORDER)
    reproduced = any(cases.get(name, {}).get('receiver_backlog', {}).get('first_backlog') is not None
        or cases.get(name, {}).get('receiver_backlog', {}).get('first_overflow') is not None for name in ORDER)
    outcome = 'measurement_or_test_failed' if not measurements else 'reproduced_with_window' if reproduced else 'not_reproduced'
    return dict(schema_version=1, protocol='contain-receiver-backlog-result-1', measurement_kind='instrumented_real_etw',
        same_runner=True, plan_valid=valid_plan, complete=complete, measurement_valid=measurements, outcome=outcome,
        backlog_observed=reproduced, maximum_native_captures=4, retries=0, order=ORDER, cases=cases,
        build_identity=plan.get('build_identity'), product_fix_claim=False, release_accepted=False,
        limitations=['Retain strict no-loss oracle failures separately; a measurement is not release acceptance.',
            'All four syscall workloads are fixed; incidental ETW traffic and scheduling are not identical.',
            'Both sides are rebuilt instrumented sources, not original release ZIPs.',
            'No product behavior fix, original-host root cause, or stable performance claim is established.'])


BENCH_NAMES = ('event_json_parse_exact_ticks', 'lifetime_ingest_resolve_batched_512',
    'completion_correlation_batched_512', 'registry_open_get_close')


def summarize_benchmark(directory, source):
    gates = PAIR.read(Path(directory)/'gates.json')
    invocations = [row for row in gates.get('invocations', []) if row.get('mode') == 'Bench' and row.get('source_revision') == source]
    if len(invocations) != 1 or invocations[0].get('completed') is not True or invocations[0].get('passed') is not True:
        raise ValueError('existing_bench_quality_gate_not_passed')
    gate = [row for row in gates.get('gates', []) if row.get('invocation_id') == invocations[0]['invocation_id'] and row.get('label') == 'bench']
    if len(gate) != 1 or type(gate[0].get('exit_code')) is not int or gate[0]['exit_code'] != 0:
        raise ValueError('existing_bench_command_not_passed')
    raw = PAIR.read(Path(directory)/'event-pipeline.json')
    if raw.get('source_commit') != source or raw.get('profile') != 'release' or raw.get('measurement_kind') != 'synthetic_in_memory' \
        or raw.get('etw_providers_started') is not False or raw.get('sqlite_measured') is not False:
        raise ValueError('existing_bench_identity_or_scope_differs')
    rows = raw.get('results') or []
    if [row.get('name') for row in rows] != list(BENCH_NAMES):
        raise ValueError('existing_bench_named_cases_differ')
    safe = []
    for row in rows:
        item = {'name': row['name']}
        for key in ('work_items_per_sample', 'input_records_per_sample', 'measured_samples', 'warmup_samples', 'validated_checksum'):
            item[key] = number(row.get(key))
            if item[key] is None:
                raise ValueError('existing_bench_numeric_field_missing')
        if item['work_items_per_sample'] != 20000 or item['measured_samples'] != 7:
            raise ValueError('existing_bench_work_or_samples_changed')
        for key in ('median_ns_per_work_item', 'min_ns_per_work_item', 'p95_ns_per_work_item', 'median_work_items_per_second'):
            value = row.get(key)
            if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
                raise ValueError('existing_bench_time_invalid')
            item[key] = value
        values = row.get('sample_ns_per_work_item')
        if not isinstance(values, list) or len(values) != 7 or any(type(value) not in (int, float) or not math.isfinite(value) or value < 0 for value in values):
            raise ValueError('existing_bench_samples_missing')
        item['sample_ns_per_work_item'] = values.copy()
        safe.append(item)
    return dict(protocol='contain-receiver-existing-bench-1', accepted=True, source_sha=source, synthetic=True,
        measurement_kind='synthetic_in_memory', real_etw=False, sqlite_measured=False, profile='release',
        quality_gate=dict(mode='Bench', completed=True, passed=True, label='bench', exit_code=0), results=safe,
        scope='Existing quality.ps1 Bench with fixed 20000 work items and seven samples per named case; no ETW throughput, SQLite or new profile-cost campaign claim.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--sample-directory', type=Path)
    mode.add_argument('--directory', type=Path)
    mode.add_argument('--benchmark-directory', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--source')
    args = parser.parse_args()
    if args.benchmark_directory is not None:
        if args.output is None or args.source is None:
            parser.error('--benchmark-directory requires --output and --source')
        try:
            result = summarize_benchmark(args.benchmark_directory, args.source)
        except (OSError, ValueError, KeyError, TypeError):
            result = dict(protocol='contain-receiver-existing-bench-1', accepted=False, source_sha=args.source,
                synthetic=True, real_etw=False, error='existing_bench_gate_or_safe_numeric_evidence_failed')
        args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        return 0 if result['accepted'] else 1
    if args.sample_directory is not None:
        if args.output is None:
            parser.error('--sample-directory requires --output')
        result = summarize_sample(args.sample_directory)
        args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        return 0
    plan = PAIR.read(args.directory / 'public/run-plan.json')
    execution = PAIR.read(args.directory / 'public/execution.json')
    cases = {}
    for name in execution['attempted']:
        try:
            cases[name] = PAIR.read(args.directory / 'public' / (name.replace('/', '-') + '.json'))
        except (OSError, ValueError, TypeError):
            cases[name] = dict(measurement_valid=False, errors=['attempted_sample_summary_unavailable'])
    result = summarize(plan, execution, cases)
    (args.directory / 'public/result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return 0 if result['measurement_valid'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
