"""Parser tests on a labelled, byte-identical, unqualified record copy.

The original retained host run has a UID-1000 ancestor, so this test copy
cannot establish original-path protection or current canonical authority.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


class AgentStagePredecessorParserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.copy = os.environ.get('IGLOO_AGENT_PREDECESSOR_PARSER_COPY')
        cls.runtime = os.environ.get('IGLOO_AGENT_DOTNET_RUNTIME')
        cls.harness = os.environ.get('IGLOO_AGENT_HARNESS')
        if not all((cls.copy, cls.runtime, cls.harness)):
            raise unittest.SkipTest('protected diagnostic parser copy and current harness required')

    def read(self, directory):
        response = subprocess.run([self.runtime, self.harness, '--agent-stage-predecessor-readiness',
                                   str(directory)], capture_output=True, timeout=30, check=False)
        self.assertLess(len(response.stdout), 2048)
        return response.returncode, json.loads(response.stdout)

    def test_actual_persisted_bytes_reopen_but_confer_no_authority(self):
        code, result = self.read(self.copy)
        self.assertEqual(0, code)
        self.assertEqual('Passed', result['Readiness'])
        self.assertEqual('DA59655DBA6BB524BBA288F34D333A3A74E6E1E128A2DAC97C4831E26C3BFBBF',
                         result['ResultSha256'])
        self.assertFalse(result['EffectAuthorization'])

    def test_original_uid1000_host_export_rejected_before_authority(self):
        original = os.environ.get('IGLOO_AGENT_PREDECESSOR_ORIGINAL')
        if not original:
            self.skipTest('retained host export path not supplied')
        self.assertEqual(1000, Path(original).lstat().st_uid)
        code, result = self.read(original)
        self.assertEqual(2, code)
        self.assertEqual('InitramfsPredecessor', result['Boundary'])
        self.assertFalse(result['EffectAuthorization'])

    def test_persisted_chain_and_receipt_changes_rejected(self):
        for kind in ('effect', 'close', 'producer-receipt', 'duplicate-preflight'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory(prefix='igloo-agent-parser-') as parent:
                target = Path(parent)/'copy'
                shutil.copytree(self.copy, target, symlinks=True)
                journals = target/'guest-evidence/journals'
                if kind == 'effect':
                    item = sorted((journals/'effects/ff104c9d-505c-4d88-8620-19b1153bd91e').glob('00000007-*.json'))[0]
                    item.write_bytes(item.read_bytes() + b' ')
                elif kind == 'close':
                    item = sorted((journals/'session/ff104c9d-505c-4d88-8620-19b1153bd91e').glob('00000017-*.json'))[0]
                    item.write_bytes(item.read_bytes() + b' ')
                elif kind == 'producer-receipt':
                    item = journals/'effects/userdata-receipt.json'
                    item.write_bytes(item.read_bytes() + b' ')
                else:
                    item = target/'guest-evidence/execution/userdata.stdout'
                    lines = item.read_bytes().splitlines(keepends=True)
                    lines[0] = lines[0].replace(b'{', b'{"Phase":"UserDataPreflight",', 1)
                    item.write_bytes(b''.join(lines))
                code, result = self.read(target)
                self.assertEqual(2, code)
                self.assertEqual('Rejected', result['Readiness'])
                self.assertFalse(result['EffectAuthorization'])


if __name__ == '__main__':
    unittest.main()
