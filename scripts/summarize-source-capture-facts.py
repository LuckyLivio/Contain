"""Export only existing numeric source/snapshot accounting; no new collection."""
import argparse
import json
import re
from pathlib import Path

PROVIDER_FIELDS = 'callback_records identity_queries unresolved enqueued filtered overflow disconnected decode_failed context_evictions'.split()
SNAPSHOT_COUNTS = '''enumerated queried resolved failed skipped query_ns elapsed_ns thread_enumerated thread_queried thread_resolved thread_failed thread_outside_boundary thread_query_ns thread_unqueried'''.split()


def number(value):
    return value if type(value) is int and value >= 0 else None


def summarize(capture):
    if (capture.get('schema_version') != 3 or capture.get('kind') != 'inspect'
            or not isinstance(capture.get('data'), dict)):
        raise ValueError('inspect_envelope_required')
    backend = capture['data'].get('backend') or {}
    pipeline = backend.get('pipeline') or {}
    providers = pipeline.get('providers') or {}
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
        initial_identity_snapshot=clean_snapshot,
        limitations=['Existing saved fields only; absent historical fields remain null.',
            'Enumeration completion is not complete identity coverage; query failures and observation boundaries still apply.',
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
