"""A selected NVIDIA pass must retain its reboot request durably."""

import importlib.util
import os
from pathlib import Path
import stat
from tempfile import TemporaryDirectory
import unittest
from unittest import mock


SOURCE = (Path(__file__).resolve().parents[2] / 'distros' / '_debian-family'
          / 'agent' / 'agent.py')
SPEC = importlib.util.spec_from_file_location('igloo_debian_reboot_agent', SOURCE)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)


@unittest.skipIf(os.name == 'nt', 'The reboot receipt needs POSIX fsync semantics')
class DebianDriverRebootTests(unittest.TestCase):
    def test_payload_service_runs_done_branch_on_later_boots(self):
        unit = (SOURCE.parent / 'igloo-first-boot.service').read_text()
        self.assertIn('ExecStart=/opt/igloo/first-boot.sh', unit)
        self.assertNotIn('ConditionPathExists=!/var/lib/igloo/.done', unit)

    def test_marker_is_private_and_write_failure_propagates(self):
        with TemporaryDirectory(prefix='igloo-debian-driver-reboot-') as temporary:
            marker = Path(temporary) / '.reboot-required'
            AGENT._flag_driver_reboot_required(marker)
            self.assertEqual(b'nvidia-driver-installed\n', marker.read_bytes())
            self.assertEqual(0o600, stat.S_IMODE(marker.stat().st_mode))
            marker.unlink()
            with mock.patch.object(AGENT.os, 'replace', side_effect=OSError('write failed')):
                with self.assertRaisesRegex(OSError, 'write failed'):
                    AGENT._flag_driver_reboot_required(marker)
            self.assertFalse(marker.exists())
            self.assertEqual([], list(Path(temporary).iterdir()))

    def test_unloadable_nvidia_module_fails_before_reboot_receipt(self):
        module = mock.Mock(stdout='/lib/modules/lab/nvidia.ko\n', returncode=0)
        refusal = mock.Mock(stdout='', returncode=1)
        manifest = {'hardware': {'gpuVendor': 'nvidia'}}
        with mock.patch.object(AGENT, 'run_cmd', side_effect=[module, refusal]), \
                mock.patch.object(AGENT, 'secure_boot_enabled', return_value=True):
            self.assertFalse(AGENT._log_nvidia_module_state(manifest))
        with mock.patch.object(AGENT, 'is_ubuntu_like', return_value=False), \
                mock.patch.object(AGENT, 'install_nvidia_driver_debian', return_value=False), \
                mock.patch.object(AGENT, '_flag_driver_reboot_required') as marker:
            with self.assertRaisesRegex(RuntimeError, 'no kernel module exists'):
                AGENT.install_gpu_drivers(manifest)
            marker.assert_not_called()


if __name__ == '__main__':
    unittest.main()
