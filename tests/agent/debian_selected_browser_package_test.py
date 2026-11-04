"""Selected browser migration must install the browser that will read the data."""

import importlib.util
import json
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import Mock, patch


AGENT_PATH = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
spec = importlib.util.spec_from_file_location("selected_browser_agent", AGENT_PATH)
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class SelectedBrowserPackageTests(unittest.TestCase):
    def test_selected_chrome_is_installed_even_without_a_companion_suggestion(self):
        manifest = {"browsers": [{"name": "Google Chrome", "engine": "chromium"}],
                    "suggestedPackages": [{"nativePackage": "tree", "autoInstall": True}]}
        with patch.object(agent, "run_cmd", return_value=Mock(returncode=0)) as flatpak, \
                patch.object(agent, "apt_install", return_value=Mock(returncode=0)) as apt:
            agent.install_suggested_packages(manifest)
        flatpak.assert_called_once_with(
            ["flatpak", "install", "-y", "--noninteractive", "flathub", "com.google.Chrome"],
            timeout=900)
        apt.assert_called_once_with(["tree"], timeout=600)

    def test_selected_browser_suggestion_is_not_installed_twice(self):
        manifest = {"browsers": [{"name": "Google Chrome"}],
                    "suggestedPackages": [{"flatpakId": "com.google.Chrome", "autoInstall": True}]}
        with patch.object(agent, "run_cmd", return_value=Mock(returncode=0)) as flatpak:
            agent.install_suggested_packages(manifest)
        self.assertEqual(flatpak.call_args.args[0].count("com.google.Chrome"), 1)

    def test_only_selected_known_browsers_are_added(self):
        manifest = {"browsers": [{"name": "Mozilla Firefox"},
                                 {"name": "Zen Browser"},
                                 {"name": "unknown-browser"}],
                    "suggestedPackages": [{"flatpakId": "com.brave.Browser",
                                           "autoInstall": False}]}
        with patch.object(agent, "run_cmd", return_value=Mock(returncode=0)) as flatpak:
            agent.install_suggested_packages(manifest)
        self.assertEqual(flatpak.call_args.args[0][-1], "app.zen_browser.zen")
        self.assertNotIn("com.brave.Browser", flatpak.call_args.args[0])

    def test_browser_install_failure_keeps_first_boot_retryable(self):
        manifest = {"browsers": [{"name": "Google Chrome"}]}
        with patch.object(agent, "run_cmd", side_effect=RuntimeError("install failed")):
            with self.assertRaisesRegex(RuntimeError, "install failed"):
                agent.install_suggested_packages(manifest)

    def test_failed_browser_install_retains_first_boot_retry_inputs(self):
        with TemporaryDirectory() as temporary:
            original = {"schemaVersion": 1, "distroId": "linuxmint-cinnamon",
                        "browsers": [{"name": "Google Chrome"}]}
            manifest = Path(temporary) / "manifest.json"
            manifest.write_text(json.dumps(original), encoding="utf-8")
            other_steps = (
                "set_user_password", "set_keyboard", "migrate_wifi",
                "wait_for_network", "apt_update", "install_gpu_drivers",
                "install_codecs", "ensure_firmware", "enable_os_prober",
                "configure_boot_menu", "setup_flathub", "ensure_migration_tools",
                "migrate_user_files", "migrate_display_layout", "migrate_wallpaper",
                "migrate_account_picture", "install_welcome_app",
                "stage_credential_import", "redact_seed", "redact_manifest",
                "cleanup_installer_partitions",
            )
            from contextlib import ExitStack
            with ExitStack() as stack:
                steps = {name: stack.enter_context(patch.object(agent, name))
                         for name in other_steps}
                stack.enter_context(patch.object(sys, "argv", [
                    "agent.py", "--manifest", str(manifest),
                    "--log-dir", temporary]))
                stack.enter_context(patch.object(agent, "configure_logging"))
                stack.enter_context(patch.object(
                    agent, "os_release", return_value={"ID": "linuxmint"}))
                stack.enter_context(patch.object(
                    agent, "_validate_seed_retry_state", return_value=False))
                stack.enter_context(patch.object(
                    agent, "run_cmd", side_effect=RuntimeError("install failed")))
                self.assertEqual(agent.main(), 1)
            self.assertEqual(json.loads(manifest.read_text()), original)
            steps["redact_seed"].assert_not_called()
            steps["redact_manifest"].assert_not_called()
            steps["cleanup_installer_partitions"].assert_not_called()

    def test_malformed_browser_selection_cannot_invoke_package_manager(self):
        with patch.object(agent, "run_cmd") as flatpak, \
                patch.object(agent, "apt_install") as apt:
            with self.assertRaises(ValueError):
                agent.install_suggested_packages({"browsers": ["Google Chrome"],
                                                  "suggestedPackages": [
                                                      {"nativePackage": "tree",
                                                       "autoInstall": True}]})
        flatpak.assert_not_called()
        apt.assert_not_called()


if __name__ == "__main__":
    unittest.main()
