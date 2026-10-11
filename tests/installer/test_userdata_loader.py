"""Real verified-byte load order and .NET context; filesystem effects are doubles.

Ordinary source checkouts are not protected runtime directories. Only ownership
and write-mode observations are synthetic here; the native fixture checks them
on the staged protected tree. Hash verification, imports, exact class identity,
constructor, namespace join, mapping and dispatch remain real.
"""
import copy
import hashlib
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import test_userdata_session as tests
import session_entry


class FixtureBindingTests(unittest.TestCase):
    def test_fixture_serial_fits_virtio_transport(self):
        import ast
        source = Path(__file__).with_name('userdata_namespace_rehearsal.py').read_text()
        literals = [n.value for n in ast.walk(ast.parse(source)) if isinstance(n, ast.Constant)
                    and isinstance(n.value, str) and n.value.startswith('IGLOO-')]
        self.assertEqual(['IGLOO-UD-JOURNAL', 'IGLOO-UD-JOURNAL'], sorted(literals))
        self.assertTrue(all(len(v.encode('ascii')) <= 20 for v in literals))


@unittest.skipUnless(tests.WIRE, 'Current .NET session serializer export required')
class ProtectedLoaderTests(unittest.TestCase):
    def test_verified_loader_constructor_and_transfer_transition(self):
        case = tests.UserDataSessionTests(); case.setUp()
        declaration = copy.deepcopy(case.request)
        directory = Path(session_entry.__file__).resolve().parent
        declaration['ToolHashes'] = {str(p): hashlib.sha256(p.read_bytes()).hexdigest().upper()
                                    for p in directory.glob('*.py')}
        real_stat = os.stat
        def protected_stat(*args, **kwargs):
            values = list(real_stat(*args, **kwargs))
            values[0] &= ~0o6022; values[4] = 0
            return os.stat_result(values)
        with patch.dict(sys.modules), patch.object(session_entry.os, 'stat', side_effect=protected_stat):
            session_entry.load_verified_modules(declaration)
            loaded = sys.modules['session_userdata']
            self.assertIs(loaded.producer, sys.modules['userdata_producer'])
            self.assertIs(loaded.session_import, sys.modules['session_import'])
            with patch.object(tests, 'adapter', loaded):
                case.exercise(real_context=True)
                case.exercise('ContextImport')
                case.exercise('ContextAttribute')
            declaration['ToolHashes'][str(directory/'block_session.py')] = '0'*64
            with self.assertRaisesRegex(ValueError, 'Session tool changed'):
                session_entry.load_verified_modules(declaration)


if __name__ == '__main__': unittest.main()
