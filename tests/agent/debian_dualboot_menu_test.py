"""Debian dual-boot completion uses command outcomes and generated GRUB content.

All system commands return subprocess.CompletedProcess fixtures; every file is
temporary. No packages, partitions, EFI variables or boot order are changed.
"""

from contextlib import ExitStack, contextmanager
import importlib.util
import json
import logging
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


BOOT = load("debian_menu_boot_fixture", "distros/_shared/agent/igloo_boot.py")
AGENT = load("debian_menu_agent_fixture", "distros/_debian-family/agent/agent.py")
MANIFEST = {"schemaVersion": 1, "distroId": "debian",
            "hardware": {"installMode": "dual-boot"}}
# Standard os-prober EFI entry, including the feature test's inner shell block.
WINDOWS_MENU = """menuentry 'Windows Boot Manager (on /dev/sda1)' --class windows --class os $menuentry_id_option 'osprober-efi-1234-5678' {
    insmod part_gpt
    insmod fat
    if [ x$feature_platform_search_hint = xy ]; then
        search --no-floppy --fs-uuid --set=root --hint=hd0,gpt1 1234-5678
    else
        search --no-floppy --fs-uuid --set=root 1234-5678
    fi
    chainloader /EFI/Microsoft/Boot/bootmgfw.efi
}
"""
LINUX_MENU = "menuentry 'Debian GNU/Linux' {\n linux /vmlinuz root=UUID=linux\n}\n"
OTHER_STEPS = (
    "set_user_password", "set_keyboard", "migrate_wifi", "wait_for_network",
    "apt_update", "install_gpu_drivers", "install_codecs", "ensure_firmware",
    "configure_boot_menu", "setup_flathub", "install_suggested_packages",
    "ensure_migration_tools", "migrate_user_files", "migrate_display_layout",
    "migrate_wallpaper", "migrate_account_picture", "install_welcome_app",
    "stage_credential_import", "redact_seed", "redact_manifest",
    "cleanup_installer_partitions",
)


class DebianDualBootMenuTests(unittest.TestCase):
    @contextmanager
    def environment(self, *, failed=None, menu=WINDOWS_MENU, update_grub=True,
                    regeneration_results=None):
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            default = root / "grub-default"
            default.write_text("GRUB_DISABLE_OS_PROBER=false\n")
            cfg = root / "grub.cfg"
            # A stale, valid menu must not hide a failing regeneration command.
            cfg.write_text(WINDOWS_MENU)
            commands = []
            regenerations = iter(regeneration_results) if regeneration_results is not None else None

            def command(argv, **kwargs):
                commands.append(argv)
                if argv[0] not in ("apt-get", "os-prober", "update-grub", "grub-mkconfig"):
                    raise AssertionError(f"Unexpected system command: {argv}")
                code = 1 if argv[0] == failed else 0
                if argv[0] in ("update-grub", "grub-mkconfig"):
                    generated_menu = menu
                    if regenerations is not None:
                        code, generated_menu = next(regenerations)
                    if code == 0:
                        cfg.write_text(generated_menu)
                stdout = "/dev/sda1@/EFI/Microsoft/Boot/bootmgfw.efi:Windows Boot Manager:Windows:efi\n"
                return subprocess.CompletedProcess(argv, code, stdout if code == 0 else "",
                                                   "fixture command failure" if code else "")

            def local_path(value):
                return {"/etc/default/grub": default,
                        "/boot/grub/grub.cfg": cfg}.get(str(value), Path(value))

            stack.enter_context(patch.object(AGENT, "Path", side_effect=local_path))
            stack.enter_context(patch.object(AGENT, "igloo_boot", BOOT))
            stack.enter_context(patch.object(AGENT.subprocess, "run", side_effect=command))
            stack.enter_context(patch.object(AGENT.shutil, "which", side_effect=lambda name:
                "/usr/sbin/update-grub" if name == "update-grub" and update_grub else None))
            yield root, cfg, commands

    def test_failed_commands_propagate_even_when_old_windows_menu_exists(self):
        for executable in ("apt-get", "os-prober", "update-grub", "grub-mkconfig"):
            with self.subTest(executable=executable), self.environment(
                    failed=executable, update_grub=executable != "grub-mkconfig") as (_, _, commands):
                with self.assertRaisesRegex(RuntimeError, "failed"):
                    AGENT.enable_os_prober(MANIFEST)
                self.assertEqual(executable, commands[-1][0])

    def test_successful_regeneration_requires_windows_chainloader(self):
        for menu in (WINDOWS_MENU, WINDOWS_MENU.replace("Windows Boot Manager", "Windows 11")):
            with self.subTest(menu=menu), self.environment(menu=menu) as (_, _, commands):
                AGENT.enable_os_prober(MANIFEST)
                self.assertEqual(["apt-get", "os-prober", "update-grub"],
                                 [command[0] for command in commands])

    def test_missing_windows_is_not_accepted_from_titles_comments_or_other_entry(self):
        for menu in (LINUX_MENU, "# " + WINDOWS_MENU.replace("\n", "\n# "),
                     WINDOWS_MENU.replace("    chainloader", "    # chainloader"),
                     "menuentry 'Windows tools' {\n linux /vmlinuz\n}\n" +
                     WINDOWS_MENU.replace("Windows Boot Manager", "Other").replace("--class windows", "")):
            with self.subTest(menu=menu), self.environment(menu=menu):
                with self.assertRaisesRegex(RuntimeError, "no Windows EFI boot entry"):
                    AGENT.enable_os_prober(MANIFEST)

    def test_unreadable_menu_and_missing_helper_fail(self):
        with self.environment() as (_, cfg, _):
            cfg.unlink()
            with self.assertRaisesRegex(RuntimeError, "unreadable"):
                BOOT.require_windows_boot_entry(cfg)
            with patch.object(AGENT, "igloo_boot", None), self.assertRaisesRegex(RuntimeError, "unavailable"):
                AGENT.enable_os_prober(MANIFEST)

    def test_failed_dualboot_step_reaches_agent_exit_and_preserves_retry_inputs(self):
        for failed, menu in (("apt-get", WINDOWS_MENU), ("os-prober", WINDOWS_MENU),
                             ("update-grub", WINDOWS_MENU), (None, LINUX_MENU),
                             (None, WINDOWS_MENU)):
            with self.subTest(failed=failed, menu=menu), self.environment(failed=failed, menu=menu) as (root, _, _), ExitStack() as stack:
                manifest = root / "manifest.json"
                manifest.write_text(json.dumps(MANIFEST))
                before = manifest.read_bytes()
                stack.enter_context(patch.object(sys, "argv", ["agent.py", "--manifest", str(manifest),
                                                              "--log-dir", str(root)]))
                stack.enter_context(patch.object(AGENT, "configure_logging"))
                stack.enter_context(patch.object(AGENT, "os_release", return_value={"ID": "debian"}))
                steps = {name: stack.enter_context(patch.object(AGENT, name)) for name in OTHER_STEPS}
                succeeded = failed is None and menu == WINDOWS_MENU
                self.assertEqual(0 if succeeded else 1, AGENT.main())
                for name in ("redact_seed", "redact_manifest", "cleanup_installer_partitions"):
                    self.assertEqual(1 if succeeded else 0, steps[name].call_count)
                self.assertEqual(before, manifest.read_bytes())

    def test_final_menu_regeneration_failure_precedes_existing_boot_order_actions(self):
        for failed, menu in (("update-grub", WINDOWS_MENU), (None, LINUX_MENU), (None, WINDOWS_MENU)):
            with self.subTest(failed=failed, menu=menu), self.environment(failed=failed, menu=menu) as (root, cfg, _), ExitStack() as stack:
                boot = BOOT.Boot(AGENT.run_cmd, logging.getLogger("menu-fixture"), cfg,
                                 root / "themes", ["update-grub"], root / "hook")
                for name in ("_patch_os_prober_labels", "_write_dropin", "_apply_grub_fixups", "_verify"):
                    stack.enter_context(patch.object(BOOT, name))
                stack.enter_context(patch.object(BOOT, "_install_theme", return_value=False))
                stack.enter_context(patch.object(BOOT, "_patch_linux_submenu", return_value=True))
                order = stack.enter_context(patch.object(BOOT, "put_self_first_in_boot_order"))
                unit = stack.enter_context(patch.object(BOOT, "install_boot_order_unit"))
                if failed is None and menu == WINDOWS_MENU:
                    BOOT.configure_boot_menu(MANIFEST, boot, require_windows=True)
                    order.assert_called_once()
                    unit.assert_called_once()
                else:
                    with self.assertRaises(RuntimeError):
                        BOOT.configure_boot_menu(MANIFEST, boot, require_windows=True)
                    order.assert_not_called()
                    unit.assert_not_called()

    def test_second_regeneration_must_succeed_before_boot_order_actions(self):
        for second_result in ((1, WINDOWS_MENU), (0, LINUX_MENU), (0, WINDOWS_MENU)):
            with self.subTest(second_result=second_result), self.environment(
                    regeneration_results=[(0, WINDOWS_MENU), second_result]) as (root, cfg, commands), ExitStack() as stack:
                boot = BOOT.Boot(AGENT.run_cmd, logging.getLogger("menu-fixture"), cfg,
                                 root / "themes", ["update-grub"], root / "hook")
                dropin = root / "99-igloo-menu.cfg"
                dropin.write_text("# fixture theme\nGRUB_THEME=/boot/grub/themes/stylish/theme.txt\n")
                stack.enter_context(patch.object(BOOT, "_DROPIN", dropin))
                stack.enter_context(patch.object(BOOT, "_GRUB_DEFAULT", root / "grub-default"))
                for name in ("_patch_os_prober_labels", "_write_dropin", "_apply_grub_fixups", "_verify"):
                    stack.enter_context(patch.object(BOOT, name))
                stack.enter_context(patch.object(BOOT, "_install_theme", return_value=True))
                stack.enter_context(patch.object(BOOT, "_patch_linux_submenu", return_value=True))
                order = stack.enter_context(patch.object(BOOT, "put_self_first_in_boot_order"))
                unit = stack.enter_context(patch.object(BOOT, "install_boot_order_unit"))
                if second_result == (0, WINDOWS_MENU):
                    BOOT.configure_boot_menu(MANIFEST, boot, require_windows=True)
                    order.assert_called_once()
                    unit.assert_called_once()
                else:
                    with self.assertRaises(RuntimeError):
                        BOOT.configure_boot_menu(MANIFEST, boot, require_windows=True)
                    order.assert_not_called()
                    unit.assert_not_called()
                self.assertEqual([["update-grub"], ["update-grub"]], commands)

    def test_post_fixup_windows_loss_does_not_activate_boot_order(self):
        with self.environment() as (root, cfg, commands), ExitStack() as stack:
            boot = BOOT.Boot(AGENT.run_cmd, logging.getLogger("menu-fixture"), cfg,
                             root / "themes", ["update-grub"], root / "hook")
            for name in ("_patch_os_prober_labels", "_write_dropin", "_verify"):
                stack.enter_context(patch.object(BOOT, name))
            stack.enter_context(patch.object(BOOT, "_install_theme", return_value=False))
            stack.enter_context(patch.object(BOOT, "_patch_linux_submenu", return_value=True))
            fixups = stack.enter_context(patch.object(BOOT, "_apply_grub_fixups",
                side_effect=lambda _boot: cfg.write_text(LINUX_MENU)))
            order = stack.enter_context(patch.object(BOOT, "put_self_first_in_boot_order"))
            unit = stack.enter_context(patch.object(BOOT, "install_boot_order_unit"))
            with self.assertRaisesRegex(RuntimeError, "no Windows EFI boot entry"):
                BOOT.configure_boot_menu(MANIFEST, boot, require_windows=True)
            fixups.assert_called_once()
            self.assertEqual([["update-grub"]], commands)
            order.assert_not_called()
            unit.assert_not_called()

    def test_strict_policy_is_limited_to_debian_dualboot(self):
        for manifest, required in ((MANIFEST, True),
                                   ({"distroId": "debian", "hardware": {"installMode": "replace"}}, False),
                                   ({"distroId": "linuxmint-cinnamon", "hardware": {"installMode": "dual-boot"}}, False),
                                   ({}, False)):
            with self.subTest(manifest=manifest), patch.object(AGENT, "igloo_boot", BOOT), \
                    patch.object(AGENT, "_shrink_initramfs"), \
                    patch.object(BOOT, "configure_boot_menu") as configure:
                AGENT.configure_boot_menu(manifest)
                self.assertEqual(required, configure.call_args.kwargs["require_windows"])


if __name__ == "__main__":
    unittest.main()
