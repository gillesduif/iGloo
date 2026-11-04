"""A failed NVIDIA setup must not publish first-boot success or reboot."""

import importlib.util
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


SOURCE = Path(__file__).resolve().parents[2] / "distros/fedora-kde/agent/agent.py"
SPEC = importlib.util.spec_from_file_location("igloo_fedora_gpu_agent", SOURCE)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)


class FedoraGpuCompletionTests(unittest.TestCase):
    def test_module_directory_requires_an_actual_nonempty_nvidia_module(self):
        with tempfile.TemporaryDirectory(prefix="igloo-fedora-gpu-") as temporary:
            module_dir = Path(temporary) / "extra/nvidia"
            module_dir.mkdir(parents=True)

            def private_module_path(name):
                if name == "/lib/modules/6.1.0-lab":
                    return Path(temporary)
                return Path(name)

            with mock.patch.object(AGENT, "Path", side_effect=private_module_path):
                self.assertFalse(AGENT._nvidia_module_present("6.1.0-lab"))
                module_file = module_dir / "nvidia.ko.xz"
                module_file.touch()
                self.assertFalse(AGENT._nvidia_module_present("6.1.0-lab"))
                module_file.write_bytes(b"module")
                self.assertTrue(AGENT._nvidia_module_present("6.1.0-lab"))

    def test_missing_kernel_module_fails_before_blacklist_or_reboot(self):
        with tempfile.TemporaryDirectory(prefix="igloo-fedora-gpu-") as temporary:
            def private_path(name):
                if name == "/var/lib/igloo/.had-failures":
                    return Path(temporary) / "had-failures"
                return Path(name)

            with mock.patch.object(AGENT, "Path", side_effect=private_path), \
                mock.patch.object(AGENT, "run_cmd", return_value=SimpleNamespace(returncode=0)), \
                mock.patch.object(AGENT, "_secure_boot_enabled", return_value=False), \
                mock.patch.object(AGENT, "gpu_requires_open_kernel_module", return_value=False), \
                mock.patch.object(AGENT, "installed_kernel_versions", return_value=["6.1.0-lab"]), \
                mock.patch.object(AGENT, "_nvidia_module_present", return_value=False), \
                mock.patch.object(AGENT, "ensure_nvidia_kernel_cmdline") as blacklist, \
                mock.patch.object(AGENT, "_flag_driver_reboot_required") as marker, \
                mock.patch.object(AGENT, "_KERNEL_UNVERIFIED", Path(temporary) / "unverified"):
                with self.assertRaisesRegex(RuntimeError, "NVIDIA module is missing"):
                    AGENT.install_gpu_drivers({"hardware": {"gpuVendor": "nvidia"}})
                blacklist.assert_not_called()
                marker.assert_not_called()

    def test_required_open_module_selection_failure_is_fatal(self):
        requested = []

        def path_for(name):
            requested.append(name)
            target = mock.Mock()
            target.write_text.side_effect = OSError("read-only macro directory")
            return target

        with mock.patch.object(AGENT, "run_cmd", return_value=SimpleNamespace(returncode=0)), \
                mock.patch.object(AGENT, "_secure_boot_enabled", return_value=False), \
                mock.patch.object(AGENT, "gpu_requires_open_kernel_module", return_value=True), \
                mock.patch.object(AGENT, "Path", side_effect=path_for), \
                mock.patch.object(AGENT, "ensure_nvidia_kernel_cmdline") as blacklist, \
                mock.patch.object(AGENT, "_flag_driver_reboot_required") as marker:
            with self.assertRaisesRegex(OSError, "read-only macro directory"):
                AGENT.install_gpu_drivers({"hardware": {"gpuVendor": "nvidia"}})
            self.assertEqual(["/etc/rpm/macros.nvidia-kmod"], requested)
            blacklist.assert_not_called()
            marker.assert_not_called()

    def test_current_secure_boot_overrides_stale_manifest_before_driver_install(self):
        with mock.patch.object(AGENT, "_secure_boot_enabled", return_value=True), \
                mock.patch.object(AGENT, "run_cmd") as command, \
                mock.patch.object(AGENT, "ensure_nvidia_kernel_cmdline") as blacklist:
            with self.assertRaisesRegex(RuntimeError, "module signing"):
                AGENT.install_gpu_drivers({"hardware": {
                    "gpuVendor": "nvidia", "secureBootEnabled": False,
                }})
            command.assert_not_called()
            blacklist.assert_not_called()

    def test_unknown_secure_boot_state_fails_closed(self):
        with mock.patch.object(AGENT, "run_cmd", return_value=SimpleNamespace(
                returncode=0, stdout="unrecognized firmware state", stderr="")):
            with self.assertRaisesRegex(RuntimeError, "state is unknown"):
                AGENT._secure_boot_enabled()

    def test_cmdline_write_failure_prevents_driver_completion(self):
        with mock.patch.object(AGENT, "_secure_boot_enabled", return_value=False), \
                mock.patch.object(AGENT, "_merge_kernel_cmdline", return_value=False), \
                mock.patch.object(AGENT, "run_cmd") as command:
            with self.assertRaisesRegex(RuntimeError, "GRUB_CMDLINE_LINUX"):
                AGENT.ensure_nvidia_kernel_cmdline(True, ["6.1.0-lab"])
            command.assert_not_called()

    def test_grub_generation_failure_prevents_driver_completion(self):
        with mock.patch.object(AGENT, "_secure_boot_enabled", return_value=False), \
                mock.patch.object(AGENT, "_merge_kernel_cmdline", return_value=True), \
                mock.patch.object(AGENT, "run_cmd", side_effect=RuntimeError("grub failed")):
            with self.assertRaisesRegex(RuntimeError, "grub failed"):
                AGENT.ensure_nvidia_kernel_cmdline(True, ["6.1.0-lab"])


if __name__ == "__main__":
    unittest.main()
