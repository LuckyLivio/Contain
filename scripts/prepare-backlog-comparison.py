"""Bind a rebuilt old-behavior baseline and candidate to one reviewed observer patch.

No capture or build is started here. Baseline remains identified by its original
source plus the exact applied patch; it is never called the published Beta ZIP.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

BASELINE = 'f76a8618426945093eacdd5751d49d2633635e78'
CANDIDATE_BASE = '347a86ee730c4fd2d9ee775add60a6c5deddf7fa'
OBSERVER_FILES = (
    'crates/contain-core/src/profile.rs',
    'crates/contain-core/src/profile/backlog.rs',
    'crates/contain-core/src/windows/etw/metrics.rs',
    'crates/contain-core/src/follow.rs',
    'crates/contain-core/src/ingest_tests.rs',
    'tools/offline/src/bin/receiver-window-check.rs',
)
IDENTICAL_IMPLEMENTATIONS = OBSERVER_FILES[:3] + OBSERVER_FILES[5:]
COST_INPUTS = ('Cargo.toml', 'Cargo.lock', 'tools/offline/Cargo.toml', 'tools/offline/src/lib.rs',
    'tools/offline/src/bin/receiver-window-check.rs', 'crates/contain-core/src/profile.rs',
    'crates/contain-core/src/profile/backlog.rs', 'scripts/receiver-window-overhead.py')
OFFLINE_SUMMARY = 'docs/examples/beta2/receiver-window-offline-summary.json'
CORE_SCOPE = ('Cargo.toml', 'Cargo.lock', 'crates', 'vendor', 'examples/test-installer', 'tools/offline')


def sha(data):
    return hashlib.sha256(data).hexdigest()


def git(repo, *args, data=None):
    return subprocess.check_output(['git', '-C', str(repo), *args], input=data, stderr=subprocess.PIPE)


def require(value, reason):
    if not value:
        raise ValueError(reason)


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def validate_offline(repo, result):
    require(result.get('protocol') == 'contain-receiver-window-offline-overhead-1'
        and result.get('accepted') is True and result.get('synthetic') is True and result.get('real_etw') is False
        and result.get('native_ingest') is False and result.get('retries') == 0
        and result.get('fixed_work_and_snapshot_inputs_equal') is True, 'reviewed_offline_cost_not_accepted')
    require(set(result.get('input_sha256', {})) == set(COST_INPUTS), 'reviewed_offline_input_membership_differs')
    for name in COST_INPUTS:
        require(sha((repo / name).read_bytes()) == result['input_sha256'][name], 'reviewed_offline_input_changed:' + name)
    samples = result.get('samples') or []
    require(len(samples) == 21 and sum(row.get('warmup') is True for row in samples) == 3
        and sum(row.get('warmup') is False for row in samples) == 18
        and all(type(row.get('exit_code')) is int and row['exit_code'] == 0 for row in samples), 'reviewed_offline_samples_incomplete')


def prepare(repo, source, output, expected_patch=None):
    repo = repo.resolve(strict=True)
    require(re.fullmatch('[0-9a-f]{40}', source), 'invalid_candidate_source')
    require(git(repo, 'rev-parse', 'HEAD').decode().strip() == source, 'candidate_head_differs')
    require(not git(repo, 'status', '--porcelain', '--untracked-files=no'), 'candidate_tracked_source_dirty')
    changed = git(repo, 'diff', '--name-only', CANDIDATE_BASE, source, '--', *CORE_SCOPE).decode().splitlines()
    require(changed and set(changed) <= set(OBSERVER_FILES), 'non_observer_core_change_or_missing_patch')
    patch = git(repo, 'diff', '--binary', '--full-index', CANDIDATE_BASE, source, '--', *OBSERVER_FILES)
    require(patch, 'observer_patch_empty')
    patch_sha = sha(patch)
    require(expected_patch is None or patch_sha == expected_patch, 'reviewed_observer_patch_hash_differs')
    offline_path = repo / OFFLINE_SUMMARY
    offline = json.loads(offline_path.read_text(encoding='utf-8'))
    validate_offline(repo, offline)
    require(not output.exists(), 'retain_existing_preparation')
    (output / 'private').mkdir(parents=True)
    (output / 'public').mkdir()
    patch_path = output / 'private/common-observer.patch'
    patch_path.write_bytes(patch)
    baseline = output / 'private/baseline-source'
    git(repo, 'worktree', 'add', '--detach', str(baseline), BASELINE)
    # Three-way application preserves the old baseline's behavior changes. Any
    # conflict/unknown file fails here, before compilation or native capture.
    git(baseline, 'apply', '--3way', '--index', str(patch_path.resolve()))
    require(not git(baseline, 'ls-files', '--unmerged'), 'observer_patch_conflict')
    actual_changes = git(baseline, 'diff', '--cached', '--name-only').decode().splitlines()
    require(set(actual_changes) <= set(OBSERVER_FILES), 'baseline_changed_outside_observer')
    for name in IDENTICAL_IMPLEMENTATIONS:
        require((baseline / name).read_bytes() == (repo / name).read_bytes(), 'observer_implementation_not_identical:' + name)
    require((baseline / 'Cargo.lock').read_bytes() == (repo / 'Cargo.lock').read_bytes(), 'security_dependency_locks_differ')
    require((baseline / 'Cargo.toml').read_bytes() == (repo / 'Cargo.toml').read_bytes(), 'build_manifests_differ')
    effective = git(baseline, 'diff', '--cached', '--binary', '--full-index')
    (output / 'private/baseline-effective.patch').write_bytes(effective)
    behavior_diff = git(repo, 'diff', '--binary', '--full-index', BASELINE, CANDIDATE_BASE, '--', *CORE_SCOPE)
    (output / 'private/behavior-differences.patch').write_bytes(behavior_diff)
    sources = {}
    for label, path, base, effective_sha in (
        ('baseline', baseline, BASELINE, sha(effective)),
        ('candidate', repo, CANDIDATE_BASE, patch_sha),
    ):
        sources[label] = dict(base_source=base, frozen_candidate_source=source if label == 'candidate' else None,
            common_observer_patch_sha256=patch_sha, effective_observer_patch_sha256=effective_sha,
            cargo_lock_sha256=sha((path / 'Cargo.lock').read_bytes()),
            cargo_manifest_sha256=sha((path / 'Cargo.toml').read_bytes()),
            observer_implementation_sha256={name: sha((path / name).read_bytes()) for name in IDENTICAL_IMPLEMENTATIONS},
            rebuilt_with_profiling=True, is_original_release_zip=False)
    identity = dict(schema_version=1, protocol='contain-receiver-backlog-build-inputs-1', candidate_source=source,
        baseline_source=BASELINE, candidate_behavior_base=CANDIDATE_BASE, sources=sources,
        common_observer_patch_sha256=patch_sha, applied_observer_files=changed,
        reviewed_offline_summary_sha256=sha(offline_path.read_bytes()),
        reviewed_offline_input_sha256=offline['input_sha256'],
        offline_scope='Existing Linux fixed-work experiment; no repeated cost campaign and no Windows ETW cost guarantee',
        behavior_difference_sha256=sha(behavior_diff),
        behavior_difference_files=git(repo, 'diff', '--name-only', BASELINE, CANDIDATE_BASE, '--', *CORE_SCOPE).decode().splitlines(),
        limitations=['Both sides are rebuilt with the same observation patch and safe SQLite dependencies; neither is a released ZIP.',
            'Candidate inherits PR20 behavior differences listed separately; observations do not isolate one behavior change.',
            'Baseline git HEAD identifies pre-patch source; its effective patch and binary hashes are mandatory parts of identity.'])
    write(output / 'public/build-inputs.json', identity)
    write(output / 'private/source-paths.json', dict(baseline=str(baseline.resolve()), candidate=str(repo)))
    return identity


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path('.'))
    parser.add_argument('--source', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--observer-patch-sha256', required=True)
    args = parser.parse_args()
    prepare(args.repo, args.source, args.output.resolve(), args.observer_patch_sha256)


if __name__ == '__main__':
    main()
