"""Fail-closed first-boot identity and interrupted scrub regression checks."""

import importlib.util
import hashlib
import json
import sys
from contextlib import ExitStack
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main
from unittest.mock import Mock, patch


SOURCE = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
SPEC = importlib.util.spec_from_file_location("debian_firstboot_identity_retry_agent", SOURCE)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

STEPS = (
    "set_user_password", "set_keyboard", "migrate_wifi", "wait_for_network",
    "apt_update", "install_gpu_drivers", "install_codecs", "ensure_firmware",
    "enable_os_prober", "configure_boot_menu", "setup_flathub",
    "install_suggested_packages", "ensure_migration_tools", "migrate_user_files",
    "migrate_display_layout", "migrate_wallpaper", "migrate_account_picture",
    "install_welcome_app", "stage_credential_import", "redact_seed",
    "redact_manifest", "cleanup_installer_partitions",
)


class FirstBootIdentityRetryTests(TestCase):
    def _run(self, *, distro_id="debian", os_id="debian", journal=None,
             scrub_failure=None):
        with TemporaryDirectory(prefix="igloo-firstboot-contract-") as directory, ExitStack() as stack:
            root = Path(directory)
            manifest = root / "manifest.json"
            manifest_bytes = json.dumps({"schemaVersion": 1, "distroId": distro_id}).encode("utf-8")
            manifest.write_bytes(manifest_bytes)
            manifest.chmod(0o600)
            if journal is not None:
                retry_dir = root / "seed-retry"
                retry_dir.mkdir()
                retry_dir.chmod(0o700)
                original = root / "manifest.retry.json"
                original.write_bytes(manifest_bytes)
                original.chmod(0o600)
                identity = {
                    "version": 1,
                    "diskGuid": "8c5609a8-c252-42d7-a63c-2798b1adcd3d",
                    "partitionGuid": "ee95d6f2-7cb1-4796-b0ec-b365fe41f231",
                    "files": [
                        {"path": "migration-manifest.json",
                         "sha256": hashlib.sha256(manifest_bytes).hexdigest()},
                        {"path": "preseed.cfg",
                         "sha256": hashlib.sha256(b"fixture preseed").hexdigest()},
                    ],
                }
                identity_bytes = json.dumps(identity).encode("utf-8")
                identity_path = root / "seed-identity.json"
                identity_path.write_bytes(identity_bytes)
                identity_path.chmod(0o600)
                if journal == "valid":
                    journal = json.dumps({
                        "identitySha256": hashlib.sha256(identity_bytes).hexdigest(),
                        "redactedHashes": {
                            "migration-manifest.json": "1" * 64,
                            "preseed.cfg": "2" * 64,
                        },
                    })
                journal_path = retry_dir / "journal.json"
                journal_path.write_text(journal, encoding="utf-8")
                journal_path.chmod(0o600)

            stack.enter_context(patch.object(sys, "argv", [
                "agent.py", "--manifest", str(manifest), "--log-dir", directory,
            ]))
            stack.enter_context(patch.object(agent, "configure_logging"))
            stack.enter_context(patch.object(agent, "os_release", return_value=(
                {"ID": os_id} if os_id is not None else {})))
            steps = {}
            for name in STEPS:
                action = Mock(side_effect=(RuntimeError("scrub failed")
                                           if name == scrub_failure else None))
                steps[name] = stack.enter_context(patch.object(agent, name, action))
            result = agent.main()
            return result, steps

    def test_debian_manifest_on_mint_exits_before_side_effects(self):
        result, steps = self._run(distro_id="debian", os_id="linuxmint")
        self.assertNotEqual(result, 0)
        self.assertTrue(all(step.call_count == 0 for step in steps.values()))

    def test_missing_installed_os_id_exits_before_side_effects(self):
        result, steps = self._run(distro_id="debian", os_id=None)
        self.assertNotEqual(result, 0)
        self.assertTrue(all(step.call_count == 0 for step in steps.values()))

    def test_mint_manifest_matches_installed_mint(self):
        result, steps = self._run(distro_id="linuxmint-cinnamon", os_id="linuxmint")
        self.assertEqual(result, 0)
        steps["set_user_password"].assert_called_once()

    def test_journal_resume_does_not_repeat_migration_or_credential_staging(self):
        result, steps = self._run(journal="valid")
        self.assertEqual(result, 0)
        resumed = {"redact_seed", "redact_manifest", "cleanup_installer_partitions"}
        self.assertEqual({name for name, action in steps.items() if action.called}, resumed)
        for name in resumed:
            steps[name].assert_called_once()

    def test_journal_resume_failure_keeps_completion_closed(self):
        result, steps = self._run(journal="{ malformed journal",
                                  scrub_failure="redact_seed")
        self.assertNotEqual(result, 0)
        self.assertFalse(any(step.called for step in steps.values()))


if __name__ == "__main__":
    main()
