"""Synthetic packaging contracts; native sample/process and Windows build remain separate."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT=Path(__file__).resolve().parents[1]
def load(filename):
    s=importlib.util.spec_from_file_location(filename.replace('-','_'),ROOT/'scripts'/filename)
    m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
P=load('package-source-desktop.py')
FIX=load('test-package-modern-desktop.py')
SOURCE='a'*40
class SourcePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.viewer=self.root/'wpf';self.viewer.mkdir()
        for n,b in FIX.published_members().items():
            if n=='Contain.Viewer.deps.json':
                b=b.decode('utf-8').replace(P.MODERN.VERSION,P.SOURCE_VERSION).encode('utf-8')
            (self.viewer/n).write_bytes(b)
        (self.viewer/'Contain.Viewer.exe').write_bytes(b'MZ-current-entry')
        self.core=self.root/'contain.exe';self.core.write_bytes(b'MZ-current-core-not-the-Beta10-core')
        self.workflow_cli=self.root/'contain-workflow.exe';self.workflow_cli.write_bytes(b'MZ-current-workflow-cli')
        fixtures=self.root/'fixtures';fixtures.mkdir();(fixtures/'viewer-demo.db').write_bytes(b'SQLite format 3\0SYNTHETIC')
        licenses=self.root/'rust-licenses';licenses.mkdir();(licenses/'LICENSE.txt').write_text('synthetic license')
        tools=dict(status='passed',msvc='14.44.9',sdk='10.0.26100.0');self.tools=self.root/'tools.json';P.write(self.tools,tools)
        self.receipt=self.root/'receipt.json';P.write(self.receipt,dict(source_sha=SOURCE,core_sha256=P.sha(self.core),workflow_cli_sha256=P.sha(self.workflow_cli),entry_sha256=P.sha(self.viewer/'Contain.Viewer.exe'),toolchain=tools,regressions='passed'))
        self.args=argparse.Namespace(viewer=self.viewer,core=self.core,workflow_cli=self.workflow_cli,fixtures=fixtures,rust_licenses=licenses,toolchain=self.tools,receipt=self.receipt,nuget=self.root/'nuget',output=self.root/'delivery',source=SOURCE)
    def process(self,command,**kwargs):
        if command[:3]==['git','rev-parse','HEAD']:return SOURCE+'\n'
        if command[:3]==['git','status','--porcelain']:return b''
        if command==['dotnet','--version']:return P.MODERN.SDK+'\n'
        if command[-1]=='--version' and 'contain-workflow' in Path(command[0]).name:return b'contain-workflow 0.6.0-beta.5\n'
        if command[-1]=='--version':return b'contain 0.3.2-beta.10\n'
        cwd=Path(kwargs['cwd'])
        (cwd/'samples/viewer-demo.db-wal').write_bytes(b'')
        (cwd/'samples/viewer-demo.db-shm').write_bytes(b'\0'*32)
        return b'{"synthetic":true}\n'
    def freeze(self):
        with patch.object(P.subprocess,'check_output',side_effect=self.process),patch.object(P.LEGACY,'validate_sample') as sample,patch.object(P.LEGACY,'copy_runtime_licenses',return_value=[]):
            result=P.freeze(self.args);sample.assert_called_once_with(self.core,self.args.fixtures);return result
    def test_candidate_stages_the_existing_desktop_zip_and_its_cli(self):
        candidate = load('assemble-release-candidate.py')
        delivery = self.root / 'A-delivery'
        self.args.output = delivery / 'desktop-delivery'
        self.freeze()
        identity = candidate.read_json(self.args.output / 'identity.json')
        assets = self.root / 'assets'
        assets.mkdir()
        # The synthetic PE fixture cannot execute. Check exactly which bytes reach the native probe gate.
        def probe(cli, version, work):
            self.assertEqual(cli.read_bytes(), self.workflow_cli.read_bytes())
            self.assertEqual(version, P.SOURCE_VERSION)
            return {'fixture': True}
        with patch.object(candidate, 'check_packaged_probes', side_effect=probe) as check:
            archive, spec, staged, _ = candidate.stage_package(
                'desktop', SOURCE, P.SOURCE_VERSION, delivery, {'desktop': identity}, assets, self.root / 'probe')
        check.assert_called_once()
        self.assertEqual(archive.name, f'contain-{P.SOURCE_VERSION}-windows-x64.zip')
        self.assertEqual(candidate.digest(archive), identity['zip_sha256'])
        self.assertEqual(candidate.digest(spec), identity['spec_sha256'])
        self.assertEqual(candidate.read_json(staged), identity)
    def test_current_inputs_bound_without_historical_core_fallback(self):
        build=self.freeze();out=self.args.output
        self.assertEqual(build['source_sha'],SOURCE);self.assertEqual(build['core_source_sha'],SOURCE)
        self.assertEqual(build['version'],'0.6.0-beta.5');self.assertEqual(build['workflow_cli_version'],'0.6.0-beta.5')
        self.assertFalse(build['reused_core_binary']);self.assertEqual(build['core_sha256'],P.sha(self.core))
        spec=json.loads((out/'package-spec.json').read_text(encoding='utf-8'));identity=json.loads((out/'identity.json').read_text(encoding='utf-8'))
        self.assertEqual(spec['launch']['window_title'],'Contain · 0.6.0-beta.5')
        self.assertEqual(spec['sha256'],P.sha(out/'contain-source-windows-x64.zip'))
        self.assertEqual(identity['spec_sha256'],P.sha(out/'package-spec.json'))
        self.assertEqual(identity['entry_sha256'],P.sha(self.viewer/'Contain.Viewer.exe'))
        self.assertEqual(identity['native_acceptance'],'not_run')
        self.assertEqual(spec['comparison_entry'],'contain.exe');self.assertTrue(all(t['stdout_sha256'] for t in spec['tests']))
        self.assertFalse(any(f.endswith(('-wal','-shm')) for f in spec['required_files']))
        self.assertFalse(any(p.name.endswith(('-wal','-shm')) for p in (out/'desktop/samples').iterdir()))
        with zipfile.ZipFile(out/'contain-source-windows-x64.zip') as z:
            P.MODERN.verify_manifest(z)
            self.assertEqual(z.read('contain.exe'),self.core.read_bytes());self.assertEqual(z.read('Contain.Viewer.exe'),(self.viewer/'Contain.Viewer.exe').read_bytes())
            self.assertEqual(z.read('contain-workflow.exe'),self.workflow_cli.read_bytes())
            self.assertIn('workflow/select-workflow-toolchain.ps1',z.namelist())
            self.assertIn('workflow/workflow-toolchain.ps1',z.namelist())
            self.assertNotIn('FINAL-ACCEPTANCE.json',z.namelist());self.assertNotIn('contain-test-installer.exe',z.namelist())
    def test_changed_core_after_receipt_rejected(self):
        self.core.write_bytes(b'MZ-wrong');self.assertRejected('receipt')
    def test_receipt_requires_same_source_and_passing_regressions(self):
        for field,value in [('source_sha','b'*40),('regressions','not_run'),('entry_sha256','0'*64)]:
            receipt=json.loads(self.receipt.read_text(encoding='utf-8'));original=receipt.copy();receipt[field]=value;P.write(self.receipt,receipt)
            self.assertRejected();P.write(self.receipt,original)
    def assertRejected(self,reason=None):
        with patch.object(P.subprocess,'check_output',side_effect=self.process),self.assertRaisesRegex(ValueError,reason or '.'):
            P.freeze(self.args)
        self.assertFalse(self.args.output.exists())
    def test_installed_but_wrong_selected_toolchain_rejected(self):
        for field,value in [('msvc','14.45.1'),('sdk','10.0.22621.0'),('status','failed')]:
            tools=json.loads(self.tools.read_text(encoding='utf-8'));original=tools.copy();tools[field]=value;P.write(self.tools,tools);self.assertRejected('toolchain');P.write(self.tools,original)
    def test_dirty_or_wrong_head_rejected(self):
        for mode in ['dirty','head']:
            def process(command,**kwargs):
                if mode=='dirty' and command[:3]==['git','status','--porcelain']:return b'?? untracked-input.cs'
                if mode=='head' and command[:3]==['git','rev-parse','HEAD']:return 'b'*40
                return self.process(command,**kwargs)
            with patch.object(P.subprocess,'check_output',side_effect=process),self.assertRaises(ValueError):P.freeze(self.args)
            self.assertFalse(self.args.output.exists())
    def test_no_overwrite_or_overlap(self):
        self.args.output.mkdir();(self.args.output/'keep').write_bytes(b'keep')
        with self.assertRaises(ValueError):P.freeze(self.args)
        self.assertEqual((self.args.output/'keep').read_bytes(),b'keep')
        self.args.output=self.viewer/'nested'
        with self.assertRaises(ValueError):P.freeze(self.args)
    def test_private_published_input_is_rejected(self):
        (self.viewer/'private.db').write_bytes(b'private');self.assertRejected()
    def test_lowercase_rust_notice_allowed_and_binary_rejected(self):
        nested=self.args.rust_licenses/'windows-sys-0.61.2';nested.mkdir()
        notice=nested/'license-apache-2.0'
        notice.write_text('Apache-2.0',encoding='utf-8')
        os.utime(notice,(123456789,123456789))
        build=self.freeze()
        self.assertTrue(any(f['path']=='licenses/rust/windows-sys-0.61.2/license-apache-2.0' for f in build['files']))
        self.args.output=self.root/'delivery-bad'
        (nested/'helper.exe').write_bytes(b'MZ')
        with patch.object(P.LEGACY,'validate_sample'),self.assertRaisesRegex(ValueError,'unreviewed Rust notice member'):
            with patch.object(P.subprocess,'check_output',side_effect=self.process):
                P.freeze(self.args)
        self.assertFalse(self.args.output.exists())
    def test_manifest_detects_member_tampering(self):
        self.freeze();path=self.args.output/'contain-source-windows-x64.zip'
        with zipfile.ZipFile(path) as z:members={n:z.read(n) for n in z.namelist()}
        members['contain.exe']=b'MZ-tampered'
        with zipfile.ZipFile(path,'w') as z:
            for name,data in members.items():z.writestr(name,data)
        with zipfile.ZipFile(path) as z,self.assertRaises(ValueError):P.MODERN.verify_manifest(z)
if __name__=='__main__':unittest.main()
