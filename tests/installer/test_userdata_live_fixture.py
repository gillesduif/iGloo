"""Small file/codec checks for the live fixture adapter; no QEMU launch."""
import base64
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from canonical_lab_runtime import checkpoint
from canonical_userdata_completion import digest
from canonical_userdata_live_fixture import (accept_fixture, backing_bindings_match,
                                             codec_files, overlay_info_matches,
                                             stable_closed_sha, finish_fixture_writer,
                                             CODEC, SOURCE)
from canonical_userdata_acquisition import process_start_time
from canonical_storage_lab import sha


class LiveFixtureInputTests(unittest.TestCase):
    def test_completed_capture_can_record_host_disposition_without_guest_shutdown(self):
        if os.geteuid() != 0 or not hasattr(os,'pidfd_open'):
            self.skipTest('Linux root pidfd fixture unavailable')
        with tempfile.TemporaryDirectory(prefix='live-shutdown-',dir='/var/lib/igloo') as temp:
            root=Path(temp); witness=root/'witness'; witness.mkdir(mode=0o700)
            run=root/'run'; run.mkdir(mode=0o700)
            process=subprocess.Popen(['/usr/bin/sleep','60'])
            class IgnoredPoweroff:
                def __init__(self): self.sent=[]
                def sendall(self,value): self.sent.append(value)
                def settimeout(self,value): self.timeout=value
                def recv(self,_): raise socket.timeout()
            channel=IgnoredPoweroff()
            try:
                with self.assertRaises(subprocess.TimeoutExpired):
                    process.wait(timeout=.1)
                checkpoint(witness,'process.json',{'StartTime':process_start_time(process.pid)})
                with patch('canonical_userdata_live_fixture.inspect_process'), \
                     patch('canonical_userdata_live_fixture.closed_backing_readback',
                           return_value=({'Sha256':'A'*64},{'scratch.raw':{'Length':1}})):
                    _,_,normal=finish_fixture_writer(run,witness,process,channel,
                                                     wait_seconds=.2)
                self.assertFalse(normal)
                self.assertEqual([b'poweroff\n'],channel.sent)
                self.assertIsNotNone(process.poll())
                result=json.loads((witness/'writer-disposition.json').read_bytes())
                self.assertTrue(result['WriterExclusionVerified'])
                self.assertTrue(result['HostSigtermViaPidfd'])
                self.assertFalse(result['NormalGuestShutdownVerified'])
                self.assertFalse(result['CanonicalCloseClaimed'])
                self.assertEqual(b'',(witness/'shutdown-console.raw').read_bytes())
            finally:
                if process.poll() is None: process.terminate();process.wait(timeout=5)

    def test_completed_capture_keeps_console_open_for_normal_exit(self):
        if os.geteuid() != 0 or not hasattr(os,'pidfd_open'):
            self.skipTest('Linux root pidfd fixture unavailable')
        with tempfile.TemporaryDirectory(prefix='live-normal-shutdown-',dir='/var/lib/igloo') as temp:
            root=Path(temp); witness=root/'witness'; witness.mkdir(mode=0o700)
            run=root/'run'; run.mkdir(mode=0o700)
            process=subprocess.Popen(['/bin/sh','-c','read line; exit 0'],stdin=subprocess.PIPE)
            class ControlledPoweroff:
                def sendall(self,value):
                    process.stdin.write(value);process.stdin.flush()
                def settimeout(self,value): self.timeout=value
                def recv(self,_): raise socket.timeout()
            try:
                checkpoint(witness,'process.json',{'StartTime':process_start_time(process.pid)})
                with patch('canonical_userdata_live_fixture.inspect_process'), \
                     patch('canonical_userdata_live_fixture.closed_backing_readback',
                           return_value=({'Sha256':'A'*64},{'scratch.raw':{'Length':1}})):
                    _,_,normal=finish_fixture_writer(run,witness,process,
                                                     ControlledPoweroff(),wait_seconds=2)
                self.assertTrue(normal)
                result=json.loads((witness/'writer-disposition.json').read_bytes())
                self.assertTrue(result['NormalGuestShutdownVerified'])
                self.assertFalse(result['HostSigtermViaPidfd'])
            finally:
                process.stdin.close()
                if process.poll() is None: process.terminate();process.wait(timeout=5)

    def test_overlay_format_capacity_and_backing_relationship(self):
        source={'Path':'/fixture/runtime.raw','Length':4096}
        info={'format':'qcow2','virtual-size':4096,
              'backing-filename':'/fixture/runtime.raw','backing-filename-format':'raw'}
        self.assertTrue(overlay_info_matches(info,source))
        for key,value in (('format','raw'),('virtual-size',8192),
                          ('backing-filename','/other/runtime.raw'),
                          ('backing-filename-format','qcow2')):
            changed=dict(info);changed[key]=value
            self.assertFalse(overlay_info_matches(changed,source))

    def test_post_shutdown_file_change_during_hash_rejected(self):
        with tempfile.TemporaryDirectory(prefix='closed-backing-',dir='/var/lib/igloo') as temp:
            path=Path(temp)/'scratch.raw';path.write_bytes(b'original')
            self.assertEqual(sha(path),stable_closed_sha(path))
            def changed(value):
                value.write_bytes(b'mutated')
                return sha(value)
            with patch('canonical_userdata_live_fixture.sha',side_effect=changed):
                with self.assertRaisesRegex(ValueError,'FixtureBackingChangedDuringHash'):
                    stable_closed_sha(path)

    def test_writable_overlay_growth_preserves_backing_identity(self):
        run = Path('/fixture-run')
        launch = {'Source': {'Device': 1, 'Inode': 2, 'Length': 100},
                  'Disks': {'runtime.qcow2': {'Device': 1, 'Inode': 3, 'Length': 10},
                            'scratch.raw': {'Device': 1, 'Inode': 4, 'Length': 64}}}
        found = {str(SOURCE/'runtime.raw'): (1, 2, 100),
                 str(run/'runtime.qcow2'): (1, 3, 20),
                 str(run/'scratch.raw'): (1, 4, 64),
                 str(run/'tools.iso'): (1, 5, 30)}
        self.assertFalse(backing_bindings_match(run, launch, found))
        self.assertTrue(backing_bindings_match(run, launch, found,
                                              overlay_may_grow=True))
        for path, changed in ((str(run/'runtime.qcow2'), (1, 6, 20)),
                              (str(run/'runtime.qcow2'), (1, 3, 9)),
                              (str(run/'runtime.qcow2'), (1, 3, 10+1024**3+1)),
                              (str(run/'scratch.raw'), (1, 4, 65)),
                              (str(SOURCE/'runtime.raw'), (1, 2, 101))):
            with self.subTest(path=path, changed=changed):
                altered = dict(found); altered[path] = changed
                self.assertFalse(backing_bindings_match(run, launch, altered,
                                                        overlay_may_grow=True))

    def setUp(self):
        if not CODEC.is_dir(): self.skipTest('retained codec input unavailable')
        self.temp = tempfile.TemporaryDirectory(prefix='live-acquisition-codec-', dir='/var/lib/igloo')
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.run = self.base/'4ad2e9ab0112495eabc40de9c1b59994'
        self.run.mkdir()
        self.witness = self.base/'witness'
        self.witness.mkdir()
        raw, observed = codec_files()
        self.files = raw
        checkpoint(self.witness, 'codec-inputs.json', {
            'Scope': 'HistoricalCodecFixtureOnlyV1',
            'SourceRunId': str(__import__('uuid').UUID(CODEC.name)),
            'Files': {name: digest(value) for name, value in raw.items()},
            'TerminalResultSha256': observed['EffectRecords'][-1]['Sha256'],
            'TerminalCloseSha256': observed['SessionRecords'][-1]['Sha256']})

    def packet(self):
        return json.dumps({'Version': 1, 'Scope': 'ControlledGuestFixtureCaptureV1',
            'RunId': '4ad2e9ab-0112-495e-abc4-0de9c1b59994',
            'CodecSourceRunId': str(__import__('uuid').UUID(CODEC.name)),
            'Challenge': 'A'*64,
            'Files': [{'Path': name, 'Length': len(data), 'Sha256': digest(data),
                       'Bytes': base64.b64encode(data).decode('ascii')}
                      for name, data in sorted(self.files.items())]},
            sort_keys=True, separators=(',', ':')).encode()

    def test_exact_codec_capture_and_terminal_reopen(self):
        raw, observed = accept_fixture(self.packet(), 'A'*64, self.run, self.witness)
        self.assertEqual(len(self.files), len(list(raw.rglob('*')))-len([p for p in raw.rglob('*') if p.is_dir()]))
        self.assertEqual('AppliedAndVerified', observed['Teardown'])
        with self.assertRaisesRegex(ValueError, 'FixtureCaptureReplay'):
            accept_fixture(self.packet(), 'A'*64, self.run, self.witness)

    def test_changed_name_byte_or_challenge_rejected(self):
        packet = json.loads(self.packet())
        for label, change in (
            ('name', lambda v: v['Files'][0].update(Path='../../escape')),
            ('byte', lambda v: v['Files'][0].update(Bytes='')),
            ('challenge', lambda v: v.update(Challenge='B'*64)),
            ('duplicate', lambda v: v['Files'].append(dict(v['Files'][0])))):
            with self.subTest(label=label):
                altered = json.loads(self.packet())
                change(altered)
                with self.assertRaises(ValueError):
                    accept_fixture(json.dumps(altered).encode(), 'A'*64, self.run, self.witness)
                self.assertFalse((self.witness/'pending').exists())
