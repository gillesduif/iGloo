"""Process/ordinary-file acquisition fixtures, never canonical authority."""
import base64
import contextlib
import io
import json
import multiprocessing
import os
from pathlib import Path
import pty
import shutil
import socket
import select
import struct
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

from canonical_userdata_acquisition import (accept_packet, finalize_retention,
    parse_frame, process_start_time, read_controlled_channel, CaptureFrame,
    ReceiveDiagnostics, HOST_CLOSURE)
from canonical_userdata_completion import digest, publish_from_protected_verifier, verify
from canonical_userdata_readback import verify as verify_terminal
from canonical_lab_runtime import checkpoint
from canonical_userdata_guest import emit_capture_frame
from canonical_userdata_guest import fixture_capture_packet
from canonical_userdata_live_fixture import codec_files, fixture_packet, CODEC
import canonical_agent_stage_lab
import canonical_storage_lab

OLD = Path('/var/lib/igloo/initramfs-helper-labs/a1b883f4179f4846b16b9a91fb4bcf9d')


class FragmentChannel:
    """Source-only transport fragmentation; the real peer tests are separate."""
    def __init__(self, wire, chunk, advance=0):
        self.wire, self.chunk, self.advance = wire, chunk, advance
        self.offset = 0
        self.now = 0.0
        self.command = None

    def getsockopt(self, *_):
        return struct.pack('3i', os.getpid(), os.getuid(), os.getgid())

    def sendall(self, command):
        self.command = command

    def settimeout(self, value):
        self.timeout = value

    def recv(self, _):
        if self.offset == len(self.wire):
            self.now += self.timeout
            raise socket.timeout()
        end = min(len(self.wire), self.offset+self.chunk)
        part = self.wire[self.offset:end]
        self.offset = end
        self.now += self.advance
        return part


class FullSizeFixtureTransportTests(unittest.TestCase):
    def test_actual_fixture_packet_emitter_receiver_and_diagnostics(self):
        if os.geteuid() != 0 or not CODEC.is_dir():
            self.skipTest('protected full-size fixture inputs unavailable')
        challenge = 'A'*64
        run_id = '4ad2e9ab-0112-495e-abc4-0de9c1b59994'
        raw, _ = codec_files()
        with tempfile.TemporaryDirectory(prefix='capture-fullsize-', dir='/var/lib/igloo') as temp:
            base = Path(temp)
            source = base/'fixture-packet.json'
            source.write_bytes(fixture_packet(raw, run_id))
            packet = fixture_capture_packet(source, challenge)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                emit_capture_frame(packet, challenge)
            # The same fixed literal command echo and CR/LF form seen on a
            # guest console; raw packet bytes stay unchanged.
            wire = (b'IGLOO_CAPTURE_EXIT:$?\r\n' +
                    output.getvalue().encode('ascii').replace(b'\n', b'\r\n') +
                    b'IGLOO_CAPTURE_EXIT:0\r\n')
            self.assertGreater(len(packet), 8*1024*1024)
            self.assertGreater(len(wire), 11_000_000)
            import resource
            rss_before = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
            parser = CaptureFrame(challenge)
            parse_started = time.process_time_ns()
            for index in range(len(wire)):
                parser.feed(wire[index:index+1])
            parser_cpu_ms = (time.process_time_ns()-parse_started)//1000000
            self.assertEqual(packet, parser.packet())
            measurements = []
            for label, chunk, advance in (('one-byte', 1, 0),
                                          ('coalesced', 8192, 0),
                                          ('simulated-slow', 4096, .08)):
                parent = base/label; parent.mkdir(mode=0o700)
                channel = FragmentChannel(wire, chunk, advance)
                started = time.perf_counter()
                observed = read_controlled_channel(channel, challenge,
                    {'Pid': os.getpid(), 'StartTime': process_start_time(os.getpid())},
                    360, fixture=True, diagnostic_parent=parent,
                    begin_timeout_seconds=60, inactivity_timeout_seconds=45,
                    terminal_timeout_seconds=30, clock=lambda: channel.now)
                elapsed = time.perf_counter()-started
                self.assertEqual(packet, observed)
                self.assertIn(b'--emit-fixture-capture', channel.command)
                diagnostic = parent/'receive-diagnostics'
                self.assertEqual(wire, (diagnostic/'incoming.raw').read_bytes())
                result = json.loads((diagnostic/'observation.json').read_bytes())
                self.assertEqual('CompleteFrame', result['Outcome'])
                self.assertEqual(len(wire), result['RawBytes'])
                self.assertEqual(len(wire), result['DurablePrefixBytes'])
                self.assertEqual(digest(wire), result['RawSha256'])
                self.assertEqual(4*((len(packet)+2)//3), result['AcceptedBase64Bytes'])
                self.assertLess(result['WriteCalls'], 1000)
                self.assertLess(len(result['LastChunks']), 65)
                measurements.append({'Mode': label, 'ReceiveCalls': result['ChunkCount'],
                    'DiagnosticWrites': result['WriteCalls'],
                    'DiagnosticFsyncs': result['FsyncCalls'],
                    'DiagnosticIoMilliseconds': result['DiagnosticIoMilliseconds'],
                    'ReceiverCpuMilliseconds': result['ReceiverCpuMilliseconds'],
                    'RealElapsedMilliseconds': int(elapsed*1000),
                    'SimulatedElapsedSeconds': int(channel.now)})
            rss_after = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
            self.assertLess(rss_after, 512*1024)
            print('FULL_SIZE_TRANSPORT '+json.dumps({'PacketBytes':len(packet),
                'WireBytes':len(wire),'ParserOnlyCpuMilliseconds':parser_cpu_ms,
                'PeakRssKiB':rss_after,'BaselinePeakRssKiB':rss_before,
                'Measurements':measurements},sort_keys=True),
                file=sys.stderr)

    def test_simulated_absolute_timeout_cannot_be_extended_by_data(self):
        wire = (b'IGLOO_USERDATA_CAPTURE_BEGIN '+b'A'*64+b' 2 '+
                digest(b'{}').encode()+b'\nIGLOO_DATA '+b'A'*4096+b'\n')
        channel = FragmentChannel(wire, 1, .1)
        with self.assertRaisesRegex(ValueError, 'CaptureTimeout'):
            read_controlled_channel(channel, 'A'*64,
                {'Pid':os.getpid(),'StartTime':process_start_time(os.getpid())},
                1, fixture=True, clock=lambda:channel.now,
                begin_timeout_seconds=2, inactivity_timeout_seconds=2,
                terminal_timeout_seconds=2)

    def test_simulated_begin_inactivity_and_terminal_timeouts_are_distinct(self):
        challenge = 'A'*64
        begin = (b'IGLOO_USERDATA_CAPTURE_BEGIN '+challenge.encode()+b' 2 '+
                 digest(b'{}').encode()+b'\n')
        cases = (
            ('begin', b'ordinary output without marker\n'*100,
             1, 0.1, 100, 1, 50, 5, 'CaptureBeginTimeout'),
            ('inactivity', begin+b'IGLOO_DATA '+b'A'*4096,
             1, 0.1, 100, 50, 5, 5, 'CaptureInactivityTimeout'),
            ('terminal', begin+b'IGLOO_DATA e30=\n'+
             b'IGLOO_USERDATA_CAPTURE_END '+challenge.encode()+b'\n',
             4096, 0, 100, 50, 50, 3, 'CaptureTerminalTimeout'))
        for label, wire, chunk, advance, absolute, start, idle, terminal, code in cases:
            with self.subTest(label=label):
                channel=FragmentChannel(wire,chunk,advance)
                with self.assertRaisesRegex(ValueError,code):
                    read_controlled_channel(channel,challenge,
                        {'Pid':os.getpid(),'StartTime':process_start_time(os.getpid())},
                        absolute,fixture=True,clock=lambda:channel.now,
                        begin_timeout_seconds=start,
                        inactivity_timeout_seconds=idle,
                        terminal_timeout_seconds=terminal)


def serve_console(path, packet, challenge, ready, fixture=False, drop_uid=None, echo=False):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
        server.bind(path); server.listen(1)
        if drop_uid is not None:
            os.setgid(drop_uid); os.setuid(drop_uid)
        ready.set()
        channel, _ = server.accept()
        with channel:
            command = channel.recv(1024)
            switch = b'--emit-fixture-capture ' if fixture else b'--execute-and-capture '
            if switch+challenge.encode() not in command or b'; echo IGLOO_CAPTURE_EXIT:$?' not in command:
                return
            if echo:
                channel.sendall(command.replace(b'\n', b'\r\n'))
            body = base64.b64encode(packet)
            frame = (b'IGLOO_USERDATA_CAPTURE_BEGIN '+challenge.encode()+b' '+str(len(packet)).encode()+b' '+
                     digest(packet).encode()+b'\nIGLOO_DATA '+body+b'\nIGLOO_USERDATA_CAPTURE_END '+
                     challenge.encode()+b'\nIGLOO_CAPTURE_EXIT:0\n')
            channel.sendall(frame)


def serve_transcript(path, transcript, ready, fragments=None, hold=0):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
        server.bind(path); server.listen(1); ready.set()
        channel, _ = server.accept()
        with channel:
            channel.recv(1024)
            for part in fragments or (transcript,):
                try: channel.sendall(part)
                except BrokenPipeError: return
            if hold: time.sleep(hold)


def serve_pty_emitter(path, challenge, ready):
    """Process-only bridge: real interactive shell/PTY and current emitter."""
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
        server.bind(path); server.listen(1); ready.set()
        channel, _ = server.accept()
        with channel:
            command = channel.recv(1024)
            if challenge.encode() not in command: return
            master, slave = pty.openpty()
            shell = subprocess.Popen(['/bin/sh','-i'],stdin=slave,stdout=slave,stderr=slave,
                                     start_new_session=True,close_fds=True)
            os.close(slave)
            try:
                guest_command = ("python3 -c 'from canonical_userdata_guest import "
                                 "emit_capture_frame; emit_capture_frame(b\"{}\", \""+
                                 challenge+"\")' ; echo IGLOO_CAPTURE_EXIT:$?\n")
                os.write(master, guest_command.encode())
                collected=bytearray(); deadline=time.monotonic()+10
                while time.monotonic()<deadline:
                    if not select.select([master],[],[],.25)[0]: continue
                    try: part=os.read(master,4096)
                    except OSError: break
                    if not part:break
                    channel.sendall(part)
                    collected.extend(part)
                    if b'IGLOO_CAPTURE_EXIT:0\r\n' in collected: break
            finally:
                try: os.write(master,b'exit\n')
                except OSError: pass
                os.close(master)
                try: shell.wait(timeout=2)
                except subprocess.TimeoutExpired: shell.terminate(); shell.wait(timeout=2)


class CaptureTransportTests(unittest.TestCase):
    def _frame(self, challenge='A'*64, packet=b'{}'):
        return (b'IGLOO_USERDATA_CAPTURE_BEGIN '+challenge.encode()+b' '+
                str(len(packet)).encode()+b' '+digest(packet).encode()+
                b'\r\nIGLOO_DATA '+base64.b64encode(packet)+
                b'\r\nIGLOO_USERDATA_CAPTURE_END '+challenge.encode()+
                b'\r\nIGLOO_CAPTURE_EXIT:0\r\n')

    def _read_transcript(self, transcript, *, fragments=None, timeout=1, hold=0,
                         nominated_uid=None, diagnostic_parent=None):
        with tempfile.TemporaryDirectory() as temp:
            path = str(Path(temp)/'console.sock'); ready = multiprocessing.Event()
            child = multiprocessing.Process(target=serve_transcript,
                args=(path, transcript, ready, fragments, hold))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    return read_controlled_channel(channel, 'A'*64,
                        {'Pid': child.pid, 'StartTime': process_start_time(child.pid)},
                        timeout, fixture=True,
                        root_created_unprivileged_uid=nominated_uid,
                        diagnostic_parent=diagnostic_parent)
            finally:
                if child.is_alive(): child.terminate()
                child.join(5)

    def test_line_aware_live_receive_and_parser(self):
        frame = self._frame()
        echo = b'python3 --emit-fixture-capture '+b'A'*64+b'; echo IGLOO_CAPTURE_EXIT:$?\r\n'
        transcript = echo+frame+b'ordinary trailing console output '*9+b'\r\n'
        self.assertEqual(b'{}', parse_frame(transcript, 'A'*64))
        self.assertEqual(b'{}', self._read_transcript(transcript))
        wrapped_echo = b'IGLOO_CAPTURE_EXIT:$?\r\n'+frame
        self.assertEqual(b'{}', self._read_transcript(wrapped_echo))
        self.assertEqual(b'{}', self._read_transcript(transcript,
            fragments=[transcript[i:i+1] for i in range(len(transcript))]))
        for label, bad, code in (
            ('truncated', frame[:-25], 'CaptureChannelClosed'),
            ('nonzero', frame.replace(b'EXIT:0', b'EXIT:3'), 'CaptureGuestExitNonzero'),
            ('wrong challenge', frame.replace(b'BEGIN '+b'A'*64, b'BEGIN '+b'B'*64),
             'CaptureHeaderChanged'),
            ('end before begin', b'IGLOO_USERDATA_CAPTURE_END '+b'A'*64+b'\n'+frame,
             'CaptureEndChanged'),
            ('duplicate', frame+frame, 'CaptureReplayOrDuplicate'),
            ('bare exit echo', b'IGLOO_CAPTURE_EXIT:$?\n', 'CaptureChannelClosed'),
            ('bad encoding', frame.replace(b'IGLOO_DATA e30=', b'IGLOO_DATA e31='),
             'CaptureEncodingChanged')):
            with self.subTest(label=label):
                with self.assertRaisesRegex(ValueError, code):
                    self._read_transcript(bad)
        with self.assertRaisesRegex(ValueError, 'CaptureTimeout'):
            self._read_transcript(b'', timeout=.02, hold=.2)

    def test_current_emitter_over_interactive_shell_pty_and_receive_loop(self):
        with tempfile.TemporaryDirectory(prefix='capture-pty-',dir='/var/lib/igloo') as temp:
            base=Path(temp); path=str(base/'console.sock'); challenge='A'*64
            ready=multiprocessing.Event()
            child=multiprocessing.Process(target=serve_pty_emitter,args=(path,challenge,ready))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    packet=read_controlled_channel(channel,challenge,
                        {'Pid':child.pid,'StartTime':process_start_time(child.pid)},5,
                        fixture=True,diagnostic_parent=base,
                        executed_identities={'Scope':'ProcessPtyOnly'})
                self.assertEqual(b'{}',packet)
                child.join(5);self.assertEqual(0,child.exitcode)
                observed=json.loads((base/'receive-diagnostics/observation.json').read_bytes())
                self.assertEqual('CompleteFrame',observed['Outcome'])
            finally:
                if child.is_alive():child.terminate();child.join(5)

    def test_guest_emitter_separates_shell_echo_and_bounds_data_lines(self):
        packet = b'X'*5000
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            emit_capture_frame(packet, 'A'*64)
        emitted = output.getvalue().encode().replace(b'\n', b'\r\n')
        self.assertTrue(emitted.startswith(b'\r\nIGLOO_USERDATA_CAPTURE_BEGIN '))
        self.assertTrue(all(len(line.replace(b'\r', b'')) <= 1035 for line in emitted.split(b'\n')
                            if line.startswith(b'IGLOO_DATA ')))
        transcript = b'# echoed fixed command without a newline'+emitted+b'IGLOO_CAPTURE_EXIT:0\r\n'
        self.assertEqual(packet, parse_frame(transcript, 'A'*64))
        self.assertEqual(packet, self._read_transcript(transcript))

    def test_data_state_diagnostics_are_bounded(self):
        frame = self._frame()
        for transcript, code in (
            (b'IGLOO_DATA e30=\n'+frame, 'CaptureDataBeforeBegin'),
            (frame.replace(b'IGLOO_CAPTURE_EXIT:0\r\n', b'IGLOO_DATA e30=\nIGLOO_CAPTURE_EXIT:0\r\n'),
             'CaptureDataAfterEnd'),
            (frame.replace(b'IGLOO_DATA e30=', b'IGLOO_DATA '+b'A'*4097),
             'CaptureDataLineBound')):
            with self.assertRaisesRegex(ValueError, code):
                parse_frame(transcript, 'A'*64)

    def test_protected_receive_diagnostics_retain_exact_rejection(self):
        if os.geteuid() != 0: self.skipTest('protected diagnostic store requires root')
        with tempfile.TemporaryDirectory(prefix='capture-diagnostics-', dir='/var/lib/igloo') as temp:
            base = Path(temp); path = str(base/'console.sock')
            challenge = 'A'*64; ready = multiprocessing.Event()
            bad = self._frame().replace(b'IGLOO_DATA e30=', b'IGLOO_COMMAND e30=')
            child = multiprocessing.Process(target=serve_transcript,
                args=(path, bad, ready))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    with self.assertRaisesRegex(ValueError, 'CaptureUnknownRecord'):
                        read_controlled_channel(channel, challenge,
                            {'Pid': child.pid, 'StartTime': process_start_time(child.pid)},
                            1, fixture=True, diagnostic_parent=base,
                            executed_identities={'Test': 'process-only'})
                child.join(5)
            finally:
                if child.is_alive(): child.terminate(); child.join(5)
            diagnostic = base/'receive-diagnostics'
            self.assertEqual(bad, (diagnostic/'incoming.raw').read_bytes())
            self.assertEqual(b'IGLOO_COMMAND e30=\r\n',
                             (diagnostic/'offending-line.raw').read_bytes())
            observation = json.loads((diagnostic/'observation.json').read_bytes())
            self.assertEqual('CaptureUnknownRecord', observation['Code'])
            self.assertTrue(observation['BeginSeen'])
            self.assertFalse(observation['EndSeen'])
            self.assertEqual(0, observation['DataLines'])
            self.assertFalse(observation['CompletionAuthority'])
            self.assertEqual(0o600, (diagnostic/'incoming.raw').stat().st_mode & 0o777)
            self.assertEqual(0o700, diagnostic.stat().st_mode & 0o777)

    def test_diagnostic_replay_rejected_before_command(self):
        if os.geteuid() != 0: self.skipTest('protected diagnostic store requires root')
        with tempfile.TemporaryDirectory(prefix='capture-diagnostic-replay-', dir='/var/lib/igloo') as temp:
            base = Path(temp); (base/'receive-diagnostics').mkdir()
            path = str(base/'console.sock'); ready = multiprocessing.Event()
            child = multiprocessing.Process(target=serve_transcript,
                args=(path, self._frame(), ready))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    with self.assertRaisesRegex(ValueError, 'CaptureDiagnosticsReplay'):
                        read_controlled_channel(channel, 'A'*64,
                            {'Pid': child.pid, 'StartTime': process_start_time(child.pid)},
                            1, fixture=True, diagnostic_parent=base)
            finally:
                if child.is_alive(): child.terminate(); child.join(5)

    def test_eof_and_timeout_diagnostics_are_durable_and_bounded(self):
        if os.geteuid() != 0: self.skipTest('protected diagnostic store requires root')
        for label, transcript, hold, code in (
            ('eof', self._frame()[:-8], 0, 'CaptureChannelClosed'),
            ('timeout', b'partial ordinary output', .2, 'CaptureTimeout')):
            with self.subTest(label=label):
                with tempfile.TemporaryDirectory(prefix='capture-diagnostic-'+label+'-',
                                                 dir='/var/lib/igloo') as temp:
                    base=Path(temp)
                    with self.assertRaisesRegex(ValueError,code):
                        self._read_transcript(transcript,timeout=.02 if hold else 1,
                                              hold=hold,diagnostic_parent=base)
                    diagnostic=base/'receive-diagnostics'
                    self.assertEqual(transcript,(diagnostic/'incoming.raw').read_bytes())
                    observation=json.loads((diagnostic/'observation.json').read_bytes())
                    self.assertEqual(code,observation['Code'])
                    self.assertFalse(observation['CompletionAuthority'])

    def test_retained_timeout_trace_replays_without_per_chunk_fsync(self):
        trace=Path('/var/lib/igloo/userdata-live-fixture-tools/20261003/witnesses-v3/'
                   'edc2969b596a4dc4aefca346ff0b5241/receive-diagnostics/incoming.raw')
        if os.geteuid()!=0 or not trace.is_file():
            self.skipTest('protected live timeout trace unavailable')
        raw=trace.read_bytes()
        self.assertEqual('415A0523EF5052775CD6B6DB1C3E571F487D2B6F69CC1D4443038DCEC98A07AB',
                         digest(raw))
        begin=next(line for line in raw.split(b'\n')
                   if line.startswith(b'IGLOO_USERDATA_CAPTURE_BEGIN '))
        challenge=begin.split()[1].decode('ascii')
        frame=CaptureFrame(challenge)
        with tempfile.TemporaryDirectory(prefix='capture-timeout-replay-',dir='/var/lib/igloo') as temp:
            real_fsync=os.fsync
            with patch('canonical_userdata_acquisition.os.fsync',side_effect=real_fsync) as sync:
                observation=ReceiveDiagnostics(temp,{'Scope':'RetainedTimeoutRegressionOnly'})
                for offset in range(0,len(raw),84):
                    part=raw[offset:offset+84]
                    observation.record(part)
                    frame.feed(part)
                self.assertTrue(frame.begin)
                self.assertFalse(frame.ended)
                self.assertFalse(frame.exited)
                self.assertEqual(8459,len(frame.body))
                observation.finish(frame,'Failed',ValueError('CaptureTimeout'))
                self.assertLess(sync.call_count,20)
            self.assertEqual(raw,(Path(temp)/'receive-diagnostics/incoming.raw').read_bytes())

    def test_nominated_uid_rejects_still_root_peer(self):
        if os.geteuid() != 0: self.skipTest('root peer required')
        with self.assertRaisesRegex(ValueError, 'CapturePeerChanged'):
            self._read_transcript(self._frame(), nominated_uid=1000)
        with self.assertRaisesRegex(ValueError, 'CapturePeerChanged'):
            self._read_transcript(self._frame(), nominated_uid=1001)

    def test_protected_isolated_loader_rejects_source_substitution(self):
        root = Path('/var/lib/igloo/userdata-authorizations')
        if os.geteuid() != 0 or not root.is_dir(): self.skipTest('protected lab fixture root unavailable')
        repo = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory(prefix='fixture-acquisition-', dir=root) as temp:
            base = Path(temp); stage = base/'completion-tools'; stage.mkdir(mode=0o700)
            hashes = {}
            for name in HOST_CLOSURE:
                source = repo/('distros/debian/native' if name in
                    ('userdata_contract.py', 'debian_first_boot.py', 'isolation_policy.py') else
                    'tests/installer')/name
                target = stage/name; shutil.copyfile(source, target); target.chmod(0o600)
                hashes[name] = digest(target.read_bytes())
            checkpoint(base, 'completion-tools.json', {'Version': 1,
                'Scope': 'ProtectedUserDataAcquisitionToolsV1', 'Files': hashes})
            command = ['/usr/bin/python3', '-I', '-B', str(stage/'canonical_userdata_acquisition.py'), 'nonsense']
            good = subprocess.run(command, capture_output=True, timeout=15, check=False)
            self.assertNotEqual(0, good.returncode)
            self.assertIn(b'CaptureCommandUnsupported', good.stderr)
            (stage/'canonical_userdata_readback.py').write_bytes(b'substituted')
            bad = subprocess.run(command, capture_output=True, timeout=15, check=False)
            self.assertNotEqual(0, bad.returncode)
            self.assertIn(b'CaptureToolBytesChanged', bad.stderr)

    def test_real_unix_peer_and_fixed_command(self):
        with tempfile.TemporaryDirectory() as temp:
            path = str(Path(temp)/'console.sock'); challenge = 'A'*64; ready = multiprocessing.Event()
            child = multiprocessing.Process(target=serve_console, args=(path, b'{"fixture":true}', challenge, ready))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    self.assertEqual(b'{"fixture":true}', read_controlled_channel(channel, challenge,
                        {'Pid': child.pid, 'StartTime': process_start_time(child.pid)}, 5))
                child.join(5); self.assertEqual(0, child.exitcode)
            finally:
                if child.is_alive(): child.terminate(); child.join(5)

    def test_fixture_command_uses_same_peer_and_frame_parser(self):
        with tempfile.TemporaryDirectory() as temp:
            path = str(Path(temp)/'console.sock'); challenge = 'D'*64; ready = multiprocessing.Event()
            child = multiprocessing.Process(target=serve_console,
                args=(path, b'{"fixture":true}', challenge, ready, True))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    self.assertEqual(b'{"fixture":true}', read_controlled_channel(channel, challenge,
                        {'Pid': child.pid, 'StartTime': process_start_time(child.pid)}, 5,
                        fixture=True))
                child.join(5); self.assertEqual(0, child.exitcode)
            finally:
                if child.is_alive(): child.terminate(); child.join(5)

    def test_root_created_socket_then_unprivileged_process_join(self):
        if os.geteuid() != 0: self.skipTest('root-created QEMU-style listener required')
        with tempfile.TemporaryDirectory() as temp:
            path = str(Path(temp)/'console.sock'); challenge = 'E'*64; ready = multiprocessing.Event()
            child = multiprocessing.Process(target=serve_console,
                args=(path, b'{"fixture":true}', challenge, ready, True, 1000))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    witness = {'Pid': child.pid, 'StartTime': process_start_time(child.pid)}
                    with self.assertRaisesRegex(ValueError, 'CapturePeerChanged'):
                        read_controlled_channel(channel, challenge, witness, 5, fixture=True)
                    self.assertEqual(b'{"fixture":true}', read_controlled_channel(channel,
                        challenge, witness, 5, fixture=True,
                        root_created_unprivileged_uid=1000))
                child.join(5); self.assertEqual(0, child.exitcode)
            finally:
                if child.is_alive(): child.terminate(); child.join(5)

    def test_echoed_exit_command_does_not_truncate_live_frame(self):
        with tempfile.TemporaryDirectory() as temp:
            path = str(Path(temp)/'console.sock'); challenge = 'F'*64; ready = multiprocessing.Event()
            child = multiprocessing.Process(target=serve_console,
                args=(path, b'{"fixture":true}', challenge, ready, True, None, True))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    self.assertEqual(b'{"fixture":true}', read_controlled_channel(channel,
                        challenge, {'Pid': child.pid, 'StartTime': process_start_time(child.pid)},
                        5, fixture=True))
                child.join(5); self.assertEqual(0, child.exitcode)
            finally:
                if child.is_alive(): child.terminate(); child.join(5)

    def test_wrong_live_peer_rejected_before_command(self):
        with tempfile.TemporaryDirectory() as temp:
            path = str(Path(temp)/'console.sock'); challenge = 'A'*64; ready = multiprocessing.Event()
            child = multiprocessing.Process(target=serve_console, args=(path, b'{}', challenge, ready))
            child.start()
            try:
                self.assertTrue(ready.wait(5))
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                    channel.connect(path)
                    with self.assertRaisesRegex(ValueError, 'CapturePeerChanged'):
                        read_controlled_channel(channel, challenge,
                            {'Pid': child.pid+1, 'StartTime': process_start_time(child.pid)}, 5)
            finally:
                child.join(5)
                if child.is_alive(): child.terminate(); child.join(5)

    def test_truncation_replay_unknown_and_challenge_rejected(self):
        challenge = 'A'*64; packet = b'{}'
        frame = (b'IGLOO_USERDATA_CAPTURE_BEGIN '+challenge.encode()+b' 2 '+digest(packet).encode()+
                 b'\nIGLOO_DATA e30=\nIGLOO_USERDATA_CAPTURE_END '+challenge.encode()+
                 b'\nIGLOO_CAPTURE_EXIT:0\n')
        self.assertEqual(packet, parse_frame(frame, challenge))
        for label, bad in (('truncated', frame.split(b'IGLOO_USERDATA_CAPTURE_END')[0]),
                           ('replay', frame+frame),
                           ('unknown', frame.replace(b'IGLOO_DATA ', b'IGLOO_COMMAND ')),
                           ('changed', frame.replace(b'IGLOO_DATA e30=', b'IGLOO_DATA e31='))):
            with self.subTest(label=label):
                with self.assertRaises((ValueError, base64.binascii.Error)):
                    parse_frame(bad, challenge)
        with self.assertRaises(ValueError): parse_frame(frame, 'B'*64)
        for bad in (frame.replace(b'IGLOO_CAPTURE_EXIT:0', b'IGLOO_CAPTURE_EXIT:9'),
                    frame.replace(b'IGLOO_USERDATA_CAPTURE_END '+challenge.encode(),
                                  b'IGLOO_USERDATA_CAPTURE_END '+b'B'*64),
                    b'IGLOO_USERDATA_CAPTURE_END '+challenge.encode()+b'\n'+frame):
            with self.assertRaises(ValueError): parse_frame(bad, challenge)


class RetainedFixtureJoinTests(unittest.TestCase):
    def setUp(self):
        if os.geteuid() != 0 or not OLD.exists(): self.skipTest('root-owned retained fixture unavailable')
        self.temp = tempfile.TemporaryDirectory(prefix='userdata-acquire-fixture-', dir='/var/lib/igloo')
        self.addCleanup(self.temp.cleanup); self.base = Path(self.temp.name)

    def _packet(self):
        auth = self.base/'authorization.json'
        shutil.copyfile(OLD/'input/userdata-authorization.json', auth)
        a = json.loads(auth.read_bytes()); run = self.base/a['AttemptId'].replace('-', '')
        run.mkdir()
        binding = json.loads((OLD/'guest-evidence/execution/host-binding.json').read_bytes())
        lookup = {'runtime.raw': 'IGLOO-GPT-RUNTIME', 'target.raw': 'IGLOO-LAB-TARGET',
                  'journal.raw': 'IGLOO-LAB-JOURNAL'}
        found = {str(run/name): {'Device': next(b for b in binding['ReopenedBackings'] if b['Serial'] == serial)['HostDevice'],
            'Inode': next(b for b in binding['ReopenedBackings'] if b['Serial'] == serial)['HostInode'],
            'Length': next(b for b in binding['ReopenedBackings'] if b['Serial'] == serial)['Length']}
            for name, serial in lookup.items()}
        old_launch = binding['ReopenedBackings'][0]['LaunchSha256']
        launch = {'Version': 1, 'Scope': 'ControlledUserDataLaunchV1',
                  'AuthorizationSha256': digest(auth.read_bytes()), 'RunId': a['AttemptId'],
                  'RunPath': str(run), 'LaunchSha256': old_launch, 'Arguments': [],
                  'Disks': {name: found[str(run/name)] for name in lookup}, 'ToolsSha256': '0'*64}
        checkpoint(self.base, 'completion-launch.json', launch)
        checkpoint(self.base, 'completion-process.json', {'Version': 1,
            'Scope': 'ControlledUserDataProcessV1', 'Pid': 999999999,
            'StartTime': 0, 'LaunchWitnessSha256': digest((self.base/'completion-launch.json').read_bytes())})
        for name in lookup: (run/name).write_bytes(b'small synthetic backing')
        files = {}
        fixed = {'input/userdata-authorization.json': auth,
                 'userdata-derivation.json': OLD/'userdata-derivation.json'}
        for name in ('host-binding.json', 'inventory.json', 'userdata.stdout', 'userdata-exit.json',
                     'independent-handoff.json', 'preserved-before.json', 'preserved-after.json',
                     'provisioning-journal-unmount.json'):
            fixed['guest-evidence/execution/'+name] = OLD/'guest-evidence/execution'/name
        for prefix, root in (('input/initramfs-predecessor', OLD/'input/initramfs-predecessor'),
                             ('guest-evidence/journals', OLD/'guest-evidence/journals')):
            for item in root.rglob('*'):
                if item.is_file(): fixed[prefix+'/'+item.relative_to(root).as_posix()] = item
        for name, source in fixed.items(): files[name] = source.read_bytes()
        terminal = verify_terminal(OLD/'guest-evidence/execution', OLD/'guest-evidence/journals')
        challenge = 'C'*64
        packet = json.dumps({'Version': 1, 'Scope': 'ControlledGuestUserDataCaptureV1',
            'Challenge': challenge, 'RunId': a['AttemptId'], 'GenerationId': a['GenerationId'],
            'OperationId': a['OperationId'], 'SessionId': terminal['SessionId'],
            'PlanSha256': terminal['PlanSha256'],
            'ResultSha256': terminal['EffectRecords'][-1]['Sha256'],
            'CloseSha256': terminal['SessionRecords'][-1]['Sha256'],
            'HostBindingSha256': digest(files['guest-evidence/execution/host-binding.json']),
            'Files': [{'Path': name, 'Length': len(raw), 'Sha256': digest(raw),
                       'Bytes': base64.b64encode(raw).decode()} for name, raw in sorted(files.items())]},
            sort_keys=True, separators=(',', ':')).encode()
        return run, auth, launch, found, packet, challenge, terminal

    def test_capture_finalization_publisher_reader_and_preflight(self):
        run, auth, launch, found, packet, challenge, terminal = self._packet()
        pending = self.base/'pending'
        accept_packet(packet, challenge, run, auth, pending, launch, {}, found)
        with self.assertRaisesRegex(ValueError, 'CapturePendingExists'):
            accept_packet(packet, challenge, run, auth, pending, launch, {}, found)
        self.assertFalse((pending/'anchor.json').exists())
        capture = json.loads((pending/'capture.json').read_bytes())
        self.assertEqual('PendingBackingReadback', capture['Status'])
        old_retention = json.loads((OLD/'userdata-retention.json').read_bytes())
        original_sha = __import__('canonical_userdata_acquisition').sha
        original_identity = __import__('canonical_userdata_acquisition').identity
        def fixture_sha(path):
            name = Path(path).name
            return old_retention['Backings'][name]['Sha256'] if name in ('target.raw', 'journal.raw', 'runtime.raw') else original_sha(path)
        def fixture_identity(path):
            name = Path(path).name
            if name in old_retention['Backings']:
                binding = old_retention['Backings'][name]
                return binding['Device'], binding['Inode'], binding['Length']
            return original_identity(path)
        with patch('canonical_userdata_acquisition.closed'), patch('canonical_userdata_acquisition.sha', side_effect=fixture_sha), \
             patch('canonical_userdata_acquisition.identity', side_effect=fixture_identity):
            anchor_path = finalize_retention(run, auth, pending)
        anchor = json.loads(anchor_path.read_bytes())
        self.assertEqual(2, anchor['Version'])
        self.assertEqual(terminal['SessionRecords'][-1]['Sha256'], anchor['CloseSha256'])
        agent_auth = self.base/'agent-authorization.json'
        agent_auth.write_text(json.dumps({'Version': 1, 'Scope': 'OneUserDataCheckpointOfflineAgentStage',
            'AttemptId': 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
            'OperationId': 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
            'GenerationId': anchor['GenerationId'], 'UserDataSha256': anchor['ResultSha256'],
            'CloseSha256': anchor['CloseSha256'], 'RetentionSha256': anchor['RetentionSha256'],
            'TargetSha256': anchor['TargetSha256'], 'JournalSha256': anchor['JournalSha256']}))
        published = self.base/'published'
        result = publish_from_protected_verifier(pending/'raw', anchor_path, agent_auth, published)
        self.assertEqual(anchor['ResultSha256'], result['ResultSha256'])
        self.assertEqual(result['ResultSha256'], verify(published, agent_auth, require_live=True)['ResultSha256'])
        predecessor = self.base/anchor['SourceRunId'].replace('-', '')
        # This is the small fixture run created above; its declared identity
        # is substituted solely at the expensive backing-observation boundary.
        def observed_identity(path):
            value = old_retention['Backings'][Path(path).name]
            return value['Device'], value['Inode'], value['Length']
        def observed_sha(path):
            name = Path(path).name
            return old_retention['Backings'][name]['Sha256'] if name in ('target.raw', 'journal.raw') else original_sha(path)
        with patch.object(canonical_agent_stage_lab, 'closed'), \
             patch.object(canonical_agent_stage_lab, 'identity', side_effect=observed_identity), \
             patch.object(canonical_agent_stage_lab, 'sha', side_effect=observed_sha):
            joined, _, _ = canonical_agent_stage_lab.preflight(agent_auth, predecessor, published)
            self.assertEqual(anchor['ResultSha256'], joined['UserDataSha256'])
        harness = os.environ.get('IGLOO_AGENT_HARNESS'); runtime = os.environ.get('IGLOO_AGENT_DOTNET_RUNTIME')
        if harness and runtime:
            import subprocess
            response = subprocess.run([runtime, harness, '--agent-stage-completion-readiness',
                str(published), str(agent_auth)], capture_output=True, timeout=90, check=False)
            self.assertEqual(0, response.returncode, response.stdout[:512])
            read = json.loads(response.stdout)
            self.assertEqual('Passed', read['Readiness'])
            self.assertIs(False, read['EffectAuthorization'])
        wrong = self.base/'wrong-agent-authorization.json'
        wrong.write_text(agent_auth.read_text().replace(anchor['TargetSha256'], '0'*64))
        with self.assertRaises(ValueError): verify(published, wrong, require_live=True)
        with patch.object(canonical_storage_lab, 'require_backing_capacity'):
            with self.assertRaises(ValueError):
                canonical_storage_lab.create(predecessor, self.base, self.base,
                    agent_stage_authorization=wrong, userdata_predecessor=predecessor,
                    userdata_completion=published, configuration_pin=agent_auth)
        self.assertFalse((self.base/('a'*32)).exists())
        packet_path = published/'packet.bin'; packet_bytes = packet_path.read_bytes()
        packet_path.write_bytes(packet_bytes+b' ')
        with self.assertRaisesRegex(ValueError, 'CompletionCaptureChanged'):
            verify(published, agent_auth, require_live=True)
        packet_path.write_bytes(packet_bytes)
        (published/'capture.json').write_bytes((published/'capture.json').read_bytes()+b' ')
        with self.assertRaisesRegex(ValueError, 'CompletionCaptureChanged'):
            verify(published, agent_auth, require_live=True)

    def test_backing_substitution_blocks_anchor(self):
        run, auth, launch, found, packet, challenge, _ = self._packet()
        pending = self.base/'pending'; accept_packet(packet, challenge, run, auth, pending, launch, {}, found)
        with self.assertRaises(ValueError): finalize_retention(run, auth, pending)
        self.assertFalse((pending/'anchor.json').exists())

    def test_wrong_run_unknown_record_and_guest_host_path_rejected(self):
        run, auth, launch, found, packet, challenge, _ = self._packet()
        value = json.loads(packet)
        for label, change in (
            ('wrong-run', lambda v: v.update(RunId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')),
            ('guest-path', lambda v: v['Files'].append({'Path': '../../etc/passwd',
                'Length': 0, 'Sha256': digest(b''), 'Bytes': ''})),
            ('unknown-record', lambda v: v.update(HostCommand='rm -rf /'))):
            altered = json.loads(packet)
            change(altered)
            with self.subTest(label=label):
                with self.assertRaises(ValueError):
                    accept_packet(json.dumps(altered, sort_keys=True).encode(), challenge,
                        run, auth, self.base/('pending-'+label), launch, {}, found)
                self.assertFalse((self.base/('pending-'+label)).exists())
        with self.assertRaisesRegex(ValueError, 'CaptureDuplicateField'):
            accept_packet(b'{"Version":1,'+packet[1:], challenge,
                run, auth, self.base/'pending-duplicate', launch, {}, found)

    def test_missing_canonical_close_is_incomplete_capture(self):
        run, auth, launch, found, packet, challenge, _ = self._packet()
        value = json.loads(packet)
        selected = [item for item in value['Files'] if
            item['Path'].startswith('guest-evidence/journals/session/') and
            '/00000017-' in item['Path']]
        self.assertEqual(1, len(selected))
        value['Files'].remove(selected[0])
        pending = self.base/'pending-missing-close'
        with self.assertRaises((ValueError, FileNotFoundError)):
            accept_packet(json.dumps(value, sort_keys=True).encode(), challenge,
                run, auth, pending, launch, {}, found)
        self.assertFalse((pending/'capture.json').exists())
        self.assertFalse((pending/'anchor.json').exists())

    def test_capture_result_persistence_failure_remains_partial(self):
        run, auth, launch, found, packet, challenge, _ = self._packet()
        pending = self.base/'pending-no-result'
        with patch('canonical_userdata_acquisition.checkpoint', side_effect=OSError('fixture-fsync-failure')):
            with self.assertRaises(OSError):
                accept_packet(packet, challenge, run, auth, pending, launch, {}, found)
        self.assertTrue((pending/'packet.bin').is_file())
        self.assertFalse((pending/'capture.json').exists())
        self.assertFalse((pending/'anchor.json').exists())


if __name__ == '__main__': unittest.main()
