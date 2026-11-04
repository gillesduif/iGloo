"""Check the root agent commit ordering without running a migration command."""

from contextlib import ExitStack
import importlib.util
import json
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


SOURCE = (Path(__file__).resolve().parents[2]
          / 'distros' / '_debian-family' / 'agent' / 'agent.py')
SPEC = importlib.util.spec_from_file_location('igloo_debian_firstboot_order', SOURCE)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

STEPS = [
    ('set_user_password', 'password'),
    ('set_keyboard', 'keyboard'),
    ('migrate_wifi', 'wifi'),
    ('wait_for_network', 'network'),
    ('apt_update', 'apt'),
    ('install_gpu_drivers', 'gpu'),
    ('install_codecs', 'codecs'),
    ('ensure_firmware', 'firmware'),
    ('enable_os_prober', 'os-prober'),
    ('configure_boot_menu', 'boot-menu'),
    ('setup_flathub', 'flathub'),
    ('install_suggested_packages', 'packages'),
    ('ensure_migration_tools', 'tools'),
    ('migrate_user_files', 'files'),
    ('migrate_display_layout', 'display'),
    ('migrate_wallpaper', 'wallpaper'),
    ('migrate_account_picture', 'avatar'),
    ('install_welcome_app', 'welcome'),
    ('stage_credential_import', 'credentials'),
    ('redact_seed', 'redact-seed'),
    ('redact_manifest', 'redact-manifest'),
    ('cleanup_installer_partitions', 'cleanup-seed'),
]


class DebianFirstBootOrderTests(unittest.TestCase):
    def run_agent(self, failing=None):
        seen = []
        with TemporaryDirectory(prefix='igloo-firstboot-order-') as temporary, ExitStack() as stack:
            manifest = Path(temporary) / 'manifest.json'
            manifest.write_text(json.dumps({'schemaVersion': 1, 'distroId': 'debian'}))
            stack.enter_context(patch.object(agent, 'configure_logging'))
            stack.enter_context(patch.object(agent, 'distro_id', return_value='debian'))
            stack.enter_context(patch.object(agent, 'os_release', return_value={'ID': 'debian'}))
            stack.enter_context(patch.object(agent, 'logger'))
            stack.enter_context(patch.object(sys, 'argv', [str(SOURCE), '--manifest', str(manifest),
                                                         '--log-dir', temporary]))
            for function, name in STEPS:
                def invoke(*args, _name=name):
                    seen.append(_name)
                    if _name == failing:
                        raise RuntimeError('selected fixture failed')
                stack.enter_context(patch.object(agent, function, side_effect=invoke))
            result = agent.main()
        return result, seen

    def test_success_redacts_only_after_selected_migration(self):
        result, seen = self.run_agent()
        self.assertEqual(0, result)
        self.assertEqual([name for _, name in STEPS], seen)
        self.assertLess(seen.index('wifi'), seen.index('codecs'))
        self.assertLess(seen.index('files'), seen.index('redact-seed'))
        self.assertLess(seen.index('avatar'), seen.index('redact-seed'))
        self.assertLess(seen.index('credentials'), seen.index('redact-seed'))

    def test_failed_selected_codec_keeps_seed_and_manifest_for_retry(self):
        result, seen = self.run_agent(failing='codecs')
        self.assertEqual(1, result)
        self.assertIn('files', seen)
        self.assertIn('credentials', seen)
        self.assertNotIn('redact-seed', seen)
        self.assertNotIn('redact-manifest', seen)
        self.assertNotIn('cleanup-seed', seen)

    def test_failed_selected_wifi_keeps_seed_and_manifest_for_retry(self):
        result, seen = self.run_agent(failing='wifi')
        self.assertEqual(1, result)
        self.assertNotIn('redact-seed', seen)
        self.assertNotIn('redact-manifest', seen)
        self.assertNotIn('cleanup-seed', seen)


if __name__ == '__main__':
    unittest.main()
