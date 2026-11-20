"""Assemble explicit current-build inputs. Historical Beta packagers remain unchanged."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

REPO = Path(__file__).resolve().parents[1]
def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), REPO / 'scripts' / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module
MODERN = load('package-modern-desktop')
LEGACY = load('package-viewer')
require, sha, write = MODERN.require, MODERN.sha, MODERN.write
SOURCE_VERSION = '0.5.0-beta.2'
WORKFLOW_CLI_VERSION = SOURCE_VERSION

def freeze(args):
    inputs = [args.viewer, args.core, args.workflow_cli, args.fixtures, args.rust_licenses, args.toolchain]
    out = args.output.resolve()
    require(not out.exists() and not args.output.is_symlink(), 'new output directory required')
    require(all(out != p.resolve() and out not in p.resolve().parents and p.resolve() not in out.parents for p in inputs), 'inputs/outputs overlap')
    source = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip()
    require(source == args.source and len(source) == 40, 'source identity differs')
    require(not subprocess.check_output(['git', 'status', '--porcelain'], cwd=REPO), 'source worktree must be clean including untracked files')
    require(subprocess.check_output(['dotnet','--version'],cwd=REPO,text=True).strip()==MODERN.SDK,'SDK differs')
    tools=json.loads(args.toolchain.read_text(encoding='utf-8-sig'))
    require(tools.get('status')=='passed' and tools.get('msvc','').startswith('14.44.') and tools.get('sdk')=='10.0.26100.0','selected toolchain missing or conflicting')
    # Project-owned receipt binds inputs to the clean checkout and isolated build directory.
    receipt=json.loads(args.receipt.read_text(encoding='utf-8-sig'))
    require(receipt['source_sha']==source and receipt['core_sha256']==sha(args.core)
            and receipt.get('workflow_cli_sha256')==sha(args.workflow_cli)
            and receipt['entry_sha256']==sha(args.viewer/'Contain.Viewer.exe') and receipt['toolchain']==tools,'build receipt input mismatch')
    require(receipt.get('regressions')=='passed','source regressions not passed')
    frameworks=MODERN.runtime_contract(args.viewer,expected_version=SOURCE_VERSION)
    files=MODERN.tree_files(args.viewer)
    MODERN.member_policy(files, lambda name:files[name].read_bytes()[:16], published=True)
    core=LEGACY.safe_file(args.core)
    workflow_cli=LEGACY.safe_file(args.workflow_cli)
    require(core.read_bytes()[:2]==b'MZ' and workflow_cli.read_bytes()[:2]==b'MZ' and files['Contain.Viewer.exe'].read_bytes()[:2]==b'MZ','Windows EXEs required')
    version=subprocess.check_output([str(core),'--version'],cwd=REPO)
    require(version.decode().strip()=='contain 0.3.2-beta.10','core protocol version differs')
    workflow_version=subprocess.check_output([str(workflow_cli),'--version'],cwd=REPO)
    require(workflow_version.decode().strip()=='contain-workflow '+WORKFLOW_CLI_VERSION,'workflow CLI version differs')
    LEGACY.validate_sample(core,args.fixtures)
    licenses=MODERN.tree_files(args.rust_licenses)
    require(licenses and all(Path(n).suffix.lower() in {'.txt','.html','.json','.md',''} or Path(n).name.upper().startswith(('LICENSE','COPYING','NOTICE','UNLICENSE')) for n in licenses),'unreviewed Rust notice member')
    out.mkdir(parents=True)
    package=out/'desktop'
    shutil.copytree(args.viewer,package)
    shutil.copyfile(core,package/'contain.exe')
    shutil.copyfile(workflow_cli,package/'contain-workflow.exe')
    (package/'workflow').mkdir()
    for script_name in ['select-workflow-toolchain.ps1','workflow-toolchain.ps1']:
        shutil.copyfile(LEGACY.safe_file(REPO/'scripts'/script_name),package/'workflow'/script_name)
    (package/'samples').mkdir()
    shutil.copyfile(args.fixtures/'viewer-demo.db',package/'samples/viewer-demo.db')
    (package/'samples/README.txt').write_text('SYNTHETIC production-store fixture; not an installation record.\n',encoding='utf-8')
    (package/'licenses').mkdir()
    for name in MODERN.LICENSE_FILES:shutil.copyfile(LEGACY.safe_file(REPO/'licenses'/name),package/'licenses'/name)
    runtime_licenses=LEGACY.copy_runtime_licenses(frameworks,package,args.nuget)
    shutil.copytree(args.rust_licenses,package/'licenses/rust')
    for name in ['LICENSE','THIRD-PARTY-NOTICES.md']:shutil.copyfile(REPO/name,package/name)
    (package/'START-HERE.txt').write_text(f'Contain {SOURCE_VERSION} · Windows x64\nExtract completely and run Contain.Viewer.exe.\nIncludes bundled contain-workflow.exe and reviewed toolchain probes for project environment checks.\nBUILD.json identifies this checkout, not the historically accepted core.\nNative acceptance is separate and not implied by building.\n',encoding='utf-8')
    file_rows=[MODERN.identity(p,package) for p in sorted(package.rglob('*')) if p.is_file()]
    build=dict(schema_version=1,protocol='contain-source-desktop-package-1',source_sha=source,
               version=SOURCE_VERSION,target='win-x64',core_source_sha=source,reused_core_binary=False,
               core_version='0.3.2-beta.10',core_sha256=sha(core),
               workflow_cli_version=WORKFLOW_CLI_VERSION,workflow_cli_sha256=sha(workflow_cli),
               entry_sha256=sha(package/'Contain.Viewer.exe'),
               sdk=MODERN.SDK,runtime=MODERN.RUNTIME,self_contained=True,core_profile='release',
               crt_static_requested=True,toolchain=tools,regressions='passed',runtime_licenses=runtime_licenses,
               native_validation='not_run; separate receiver evidence required',files=file_rows)
    write(package/'BUILD.json',build)
    write(package/'CONTENTS.json',dict(schema_version=1,source_sha=source,files=[MODERN.identity(p,package) for p in sorted(package.rglob('*')) if p.is_file()]))
    required_files=sorted(MODERN.tree_files(package))
    archive=out/'contain-source-windows-x64.zip'
    with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6,strict_timestamps=False) as z:
        for p in sorted(package.rglob('*')):
            if p.is_file():z.write(p,p.relative_to(package).as_posix())
    with zipfile.ZipFile(archive) as z: MODERN.verify_manifest(z)
    probe_dir=out/'query-probe'
    (probe_dir/'samples').mkdir(parents=True)
    shutil.copyfile(package/'samples/viewer-demo.db',probe_dir/'samples/viewer-demo.db')
    query_args=['--db','samples/viewer-demo.db','--read-only','list','--json']
    try:
        # Pin the actual specified test output without leaving SQLite WAL/SHM sidecars in the staged package.
        query=subprocess.check_output([str((package/'contain.exe').resolve()),*query_args],cwd=probe_dir)
    finally:
        shutil.rmtree(probe_dir,ignore_errors=True)
    spec=dict(schema_version=1,target='windows-x64',sha256=sha(archive),entry='Contain.Viewer.exe',comparison_entry='contain.exe',
              required_files=required_files,launch=dict(args=[],timeout_ms=20000,expected_exit=None,window_title='Contain · '+SOURCE_VERSION),
              tests=[dict(name='Core exact version',program='contain.exe',args=['--version'],expected_exit=0,timeout_ms=10000,stdout_sha256=hashlib.sha256(version).hexdigest()),
                     dict(name='Bundled workflow CLI exact version',program='contain-workflow.exe',args=['--version'],expected_exit=0,timeout_ms=10000,stdout_sha256=hashlib.sha256(workflow_version).hexdigest()),
                     dict(name='Synthetic sample read-only list',program='contain.exe',args=query_args,expected_exit=0,timeout_ms=20000,stdout_sha256=hashlib.sha256(query).hexdigest())])
    write(out/'package-spec.json',spec)
    write(out/'identity.json',dict(schema_version=1,source_sha=source,core_sha256=build['core_sha256'],workflow_cli_sha256=build['workflow_cli_sha256'],entry_sha256=build['entry_sha256'],
                                  zip_sha256=sha(archive),spec_sha256=sha(out/'package-spec.json'),toolchain=tools,native_acceptance='not_run'))
    return build

def main():
    p=argparse.ArgumentParser(description=__doc__)
    for name in ['viewer','core','workflow-cli','fixtures','rust-licenses','toolchain','receipt','nuget','output']:p.add_argument('--'+name,type=Path,required=True)
    p.add_argument('--source',required=True)
    freeze(p.parse_args())
if __name__=='__main__':main()
