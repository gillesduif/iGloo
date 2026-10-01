"""Host-capacity protocol tests; no WSL instance or native mutation is invoked."""
import json
import sys
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import canonical_lab_capacity as capacity


class HostCapacityTests(unittest.TestCase):
    def reply(self, **changes):
        return {**{'SchemaVersion': 1, 'Scope': 'HostCapacityObservationOnly',
            'Distribution': 'Ubuntu-24.04', 'RequiredAdditionalBytes': 100,
            'AvailableBytes': 101, 'Sufficient': True, 'NativeMutationAuthorized': False}, **changes}

    def invoke(self, reply, code=0):
        with patch.object(capacity.platform, 'release', return_value='microsoft-standard-WSL2'), \
                patch.dict(capacity.os.environ, {'WSL_DISTRO_NAME': 'Ubuntu-24.04'}), \
                patch.object(capacity.subprocess, 'run', side_effect=[
                    SimpleNamespace(stdout=b'C:\\fixture\\capacity.ps1\n'),
                    SimpleNamespace(stdout=json.dumps(reply).encode(), returncode=code)]) as run:
            result = capacity.require_backing_capacity(100)
            self.assertEqual(2, run.call_count)
            self.assertNotIn('wsl.exe', str(run.call_args_list))
            return result

    def test_adequate_observation_is_capacity_only(self):
        self.assertEqual(self.reply(), self.invoke(self.reply()))

    def test_low_or_unknown_backing_space_never_becomes_available(self):
        for changes in ({'AvailableBytes': 99}, {'Sufficient': False}, {'AvailableBytes': True},
                        {'Distribution': 'other'}, {'RequiredAdditionalBytes': 1},
                        {'NativeMutationAuthorized': True}, {'SchemaVersion': 2}, {}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.invoke(self.reply(**changes), 2 if not changes else 0)

    def test_missing_wsl_identity_and_invalid_requirement_fail_without_probe(self):
        with patch.object(capacity.platform, 'release', return_value='microsoft'), \
                patch.dict(capacity.os.environ, {}, clear=True), patch.object(capacity.subprocess, 'run') as run:
            for value in (True, -1, 0, 2**63, 100):
                with self.assertRaises(ValueError): capacity.require_backing_capacity(value)
            run.assert_not_called()

    def test_non_wsl_keeps_native_filesystem_check_path(self):
        with patch.object(capacity.platform, 'release', return_value='6.12-debian'), patch.object(capacity.subprocess, 'run') as run:
            self.assertIsNone(capacity.require_backing_capacity(100))
            run.assert_not_called()

    def test_copy_and_creation_check_backing_capacity_before_mutation(self):
        # Windows has no pwd module. This test stops before any provider lookup;
        # an empty module deliberately cannot perform a native lookup or mutation.
        with patch.dict(sys.modules, {'pwd': SimpleNamespace()} if sys.platform == 'win32' else {}):
            import canonical_configuration_derivation as derivation
            import canonical_storage_lab as storage
        with patch.object(derivation, 'identity', return_value=(1, 2, 40 * 1024**3)), \
                patch.object(derivation, 'require_backing_capacity', side_effect=ValueError('capacity')), \
                patch.object(derivation.subprocess, 'run') as command:
            with self.assertRaisesRegex(ValueError, 'capacity'):
                derivation.copy_verified(Path('source'), Path('destination'), 'A' * 64)
            command.assert_not_called()
        with patch.object(storage, 'require_backing_capacity', side_effect=ValueError('capacity')), \
                patch.object(storage.Path, 'mkdir') as mkdir:
            with self.assertRaisesRegex(ValueError, 'capacity'):
                storage.create('source', 'repo', 'published')
            mkdir.assert_not_called()


if __name__ == '__main__': unittest.main()
