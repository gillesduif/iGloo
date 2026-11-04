"""Exercise Fedora first-boot retry behavior without touching a host installation."""

import argparse
import importlib.util
import json
import os
import sqlite3
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main, mock, skipIf


AGENT_PATH = Path(__file__).resolve().parents[2] / "distros/fedora-kde/agent/agent.py"
SPEC = importlib.util.spec_from_file_location("fedora_first_boot_agent", AGENT_PATH)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)

STEPS = (
    "enable_rpmfusion", "install_codecs", "install_gpu_drivers",
    "configure_boot_menu", "setup_flathub", "install_suggested_packages",
    "ensure_kernel_modules", "migrate_wifi", "migrate_display_layout",
    "migrate_wallpaper", "migrate_account_picture", "migrate_browser_profiles",
    "install_welcome_app", "stage_credential_import", "export_logs_to_seed",
    "cleanup_installer_partitions", "redact_manifest",
)


class FedoraFirstBootRecoveryTest(TestCase):
    def test_display_mode_requires_an_advertised_resolution(self):
        with mock.patch.object(AGENT.Path, "glob", return_value=iter(())):
            self.assertFalse(AGENT._mode_is_supported("DP-1", 3840, 2160))

        with TemporaryDirectory() as temporary:
            modes = Path(temporary) / "modes"
            modes.write_text("3840x2160\n1920x1080\n", encoding="ascii")
            with mock.patch.object(AGENT.Path, "glob", side_effect=lambda *_: iter((modes,))):
                self.assertTrue(AGENT._mode_is_supported("DP-1", 3840, 2160))
                self.assertFalse(AGENT._mode_is_supported("DP-1", 2560, 1440))
            with mock.patch.object(AGENT.Path, "glob", return_value=iter((modes,))), \
                    mock.patch.object(AGENT.Path, "read_text", side_effect=OSError):
                self.assertFalse(AGENT._mode_is_supported("DP-1", 3840, 2160))

    def test_duplicate_monitor_identity_does_not_choose_a_panel_to_rotate(self):
        displays = [{"pnp_id": "ABC123", "connector": connector,
                     "vendor": "ABC", "product": "Display", "serial": "same"}
                    for connector in ("DP-1", "DP-2")]
        selected = self._selected_display()
        with mock.patch.object(AGENT, "_connected_outputs", return_value=displays), \
             mock.patch.object(AGENT, "_mode_is_supported") as mode_supported, \
             mock.patch.object(AGENT, "run_cmd") as run_command:
            AGENT.migrate_display_layout(selected)
        mode_supported.assert_not_called()
        run_command.assert_not_called()

    def test_remaining_monitor_starts_at_zero_after_ambiguous_panel_is_skipped(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            for directory in ("home/lab", "opt/igloo", "etc/xdg/autostart"):
                (root / directory).mkdir(parents=True)
            selected = {"user": {"preferredLinuxUsername": "lab"}, "displays": [
                {"pnpId": "DUP123", "positionX": 0, "widthPx": 1920,
                 "heightPx": 1080, "isPrimary": True},
                {"pnpId": "ABC123", "positionX": 1920, "widthPx": 1920,
                 "heightPx": 1080},
            ]}
            displays = [
                {"pnp_id": "DUP123", "connector": connector,
                 "vendor": "DUP", "product": "Same", "serial": "same"}
                for connector in ("DP-1", "DP-2")
            ] + [{"pnp_id": "ABC123", "connector": "HDMI-A-1",
                  "vendor": "ABC", "product": "Other", "serial": "unique"}]
            with mock.patch.object(AGENT, "Path", side_effect=self.sandbox_media_paths(root)), \
                 mock.patch.object(AGENT, "_connected_outputs", return_value=displays), \
                 mock.patch.object(AGENT, "_mode_is_supported", return_value=True), \
                 mock.patch.object(AGENT, "run_cmd"):
                AGENT.migrate_display_layout(selected)
            staged = json.loads((root / "opt/igloo/display-layout.json").read_text())
            self.assertEqual(["ABC123"], [monitor["pnpId"] for monitor in staged])
            self.assertEqual(0, staged[0]["x"])
            self.assertEqual(0, staged[0]["y"])

    @staticmethod
    def sandbox_media_paths(root):
        def resolve(path):
            raw = str(path)
            for prefix, target in (("/home", "home"),
                                   ("/opt/igloo", "opt/igloo"),
                                   ("/etc/xdg/autostart", "etc/xdg/autostart"),
                                   ("/var/lib/gdm3", "gdm3"),
                                   ("/var/lib/gdm", "gdm"),
                                   ("/var/lib/lightdm", "lightdm"),
                                   ("/var/lib/AccountsService", "accounts")):
                if raw == prefix or raw.startswith(prefix + "/"):
                    return root / target / raw[len(prefix):].lstrip("/")
            return Path(path)
        return resolve

    @staticmethod
    def failed_checked_chown(argv, **kwargs):
        if argv[0] == "chown" and kwargs.get("check", True):
            raise OSError("ownership transfer failed")
        return mock.Mock(returncode=0)

    def run_agent(self, *, fail_step=None, installed_os="fedora"):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest_path = root / "manifest.json"
            manifest_path.write_text(json.dumps({
                "schemaVersion": 1,
                "distroId": "fedora-kde",
                "wifiNetworks": [{"ssid": "lab", "psk": "retained-for-retry"}],
            }), encoding="utf-8")
            args = argparse.Namespace(import_credentials=False, fix_boot_order=False,
                                      manifest=manifest_path, log_dir=root)
            calls = []
            patches = [
                mock.patch.object(AGENT, "parse_args", return_value=args),
                mock.patch.object(AGENT, "configure_logging"),
                mock.patch.object(AGENT, "logger"),
                mock.patch.object(AGENT, "_verify_bound_seed_before_firstboot"),
                mock.patch.object(AGENT.platform, "freedesktop_os_release",
                                  return_value=({"ID": installed_os} if installed_os else {})),
            ]
            for step in STEPS:
                def run_step(_manifest, name=step):
                    calls.append(name)
                    if name == fail_step:
                        raise RuntimeError("injected installer step failure")
                patches.append(mock.patch.object(AGENT, step, side_effect=run_step))
            with mock.patch.object(AGENT.Path, "write_text", autospec=True) as write_text:
                # Keep the actual temporary manifest write above; the agent's
                # failure marker is an absolute target-root path.
                for patch in patches:
                    patch.start()
                try:
                    status = AGENT.main()
                finally:
                    for patch in reversed(patches):
                        patch.stop()
            return status, calls, json.loads(manifest_path.read_text(encoding="utf-8"))

    def test_failed_migration_retains_credentials_and_seed_for_retry(self):
        status, calls, manifest = self.run_agent(fail_step="migrate_wifi")
        self.assertEqual(status, 1)
        self.assertIn("export_logs_to_seed", calls)
        self.assertNotIn("redact_manifest", calls)
        self.assertNotIn("cleanup_installer_partitions", calls)
        self.assertEqual(manifest["wifiNetworks"][0]["psk"], "retained-for-retry")

    def test_wrong_installed_os_stops_before_all_migration_steps(self):
        status, calls, manifest = self.run_agent(installed_os="debian")
        self.assertEqual(status, 4)
        self.assertEqual(calls, [])
        self.assertEqual(manifest["wifiNetworks"][0]["psk"], "retained-for-retry")

    def test_missing_installed_os_stops_before_all_migration_steps(self):
        status, calls, _ = self.run_agent(installed_os=None)
        self.assertEqual(status, 4)
        self.assertEqual(calls, [])

    def test_standalone_modes_refuse_non_fedora_before_user_or_efi_write(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            for mode in ("import_credentials", "fix_boot_order"):
                for installed_os in ("debian", None):
                    with self.subTest(mode=mode, installed_os=installed_os), \
                            mock.patch.object(AGENT, "parse_args", return_value=argparse.Namespace(
                                import_credentials=mode == "import_credentials",
                                fix_boot_order=mode == "fix_boot_order")), \
                            mock.patch.object(AGENT.platform, "freedesktop_os_release",
                                              return_value=({"ID": installed_os}
                                                            if installed_os else {})), \
                            mock.patch.object(AGENT.Path, "home", return_value=home), \
                            mock.patch.object(AGENT, "run_user_credential_import") as import_login, \
                            mock.patch.object(AGENT, "igloo_boot") as boot, \
                            mock.patch.object(AGENT, "run_cmd") as command:
                        self.assertEqual(4, AGENT.main())
                        import_login.assert_not_called()
                        boot.put_self_first_in_boot_order.assert_not_called()
                        command.assert_not_called()
                        self.assertFalse((home / ".local").exists())

    def test_complete_migration_reaches_redaction_and_cleanup(self):
        status, calls, _ = self.run_agent()
        self.assertEqual(status, 0)
        self.assertIn("redact_manifest", calls)
        self.assertIn("cleanup_installer_partitions", calls)

    @skipIf(os.name == "nt", "NetworkManager keyfile permissions require POSIX")
    def test_wifi_sanitized_name_collision_keeps_both_private_profiles(self):
        with TemporaryDirectory() as temporary:
            sysconn = Path(temporary) / "system-connections"
            manifest = {"wifiNetworks": [
                {"ssid": "Cafe/A", "security": "wpa-psk", "psk": "password-one"},
                {"ssid": "Cafe?A", "security": "wpa-psk", "psk": "password-two"},
            ]}
            with self._wifi_paths(sysconn), mock.patch.object(AGENT, "run_cmd") as run_command:
                AGENT.migrate_wifi(manifest)
            files = list(sysconn.glob("*.nmconnection"))
            self.assertEqual(len(files), 2)
            self.assertEqual({f.read_text().split("id=", 1)[1].splitlines()[0]
                              for f in files}, {"Cafe/A", "Cafe?A"})
            self.assertTrue(all(f.stat().st_mode & 0o777 == 0o600 for f in files))
            run_command.assert_called_once_with(
                ["nmcli", "connection", "reload"], timeout=60)

    @skipIf(os.name == "nt", "NetworkManager keyfile permissions require POSIX")
    def test_wifi_without_recovered_psk_is_promptable_and_not_autoconnected(self):
        with TemporaryDirectory() as temporary:
            sysconn = Path(temporary) / "system-connections"
            manifest = {"wifiNetworks": [{"ssid": "SavedWifi", "security": "wpa-psk",
                                          "psk": None}]}
            with self._wifi_paths(sysconn), mock.patch.object(AGENT, "run_cmd"):
                AGENT.migrate_wifi(manifest)
            content = next(sysconn.glob("*.nmconnection")).read_text()
            self.assertIn("autoconnect=false\n", content)
            self.assertIn("security=802-11-wireless-security\n", content)
            self.assertIn("key-mgmt=wpa-psk\npsk-flags=1\n", content)
            self.assertNotIn("\npsk=", content)

    @skipIf(os.name == "nt", "GLib keyfile parsing requires POSIX")
    def test_wifi_keyfile_preserves_backslashes_and_leading_spaces(self):
        try:
            from gi.repository import GLib
        except ImportError:
            self.skipTest("GLib Python binding is unavailable")
        ssid = r"  Lab\Guest"
        psk = r" Secret\Pass"
        content = AGENT._nm_keyfile(ssid, "wpa-psk", psk, False)
        keyfile = GLib.KeyFile()
        self.assertTrue(keyfile.load_from_data(content, len(content),
                                              GLib.KeyFileFlags.NONE))
        self.assertEqual(keyfile.get_string("connection", "id"), ssid)
        self.assertEqual(keyfile.get_string("wifi", "ssid"), ssid)
        self.assertEqual(keyfile.get_string("wifi-security", "psk"), psk)

    @skipIf(os.name == "nt", "NetworkManager keyfile permissions require POSIX")
    def test_wifi_reload_failure_preserves_private_profile_for_exact_retry(self):
        with TemporaryDirectory() as temporary:
            sysconn = Path(temporary) / "system-connections"
            manifest = {"wifiNetworks": [{"ssid": "RetryWifi", "security": "wpa-psk",
                                          "psk": "retry-password"}]}
            with self._wifi_paths(sysconn), mock.patch.object(
                    AGENT, "run_cmd", side_effect=RuntimeError("reload failed")):
                with self.assertRaisesRegex(RuntimeError, "reload failed"):
                    AGENT.migrate_wifi(manifest)
            profile = next(sysconn.glob("*.nmconnection"))
            before = profile.read_bytes()
            self.assertEqual(profile.stat().st_mode & 0o777, 0o600)
            with self._wifi_paths(sysconn), mock.patch.object(AGENT, "run_cmd"):
                AGENT.migrate_wifi(manifest)
            self.assertEqual(profile.read_bytes(), before)

    @skipIf(os.name == "nt", "NetworkManager keyfile permissions require POSIX")
    def test_wifi_invalid_selected_profile_fails_before_any_write(self):
        with TemporaryDirectory() as temporary:
            sysconn = Path(temporary) / "system-connections"
            selected = {"ssid": "valid", "security": "wpa-psk", "psk": "password-one"}
            for invalid in ({"ssid": "bad\n[wifi-security]", "security": "open"},
                            {"ssid": "bad", "security": "wpa-psk",
                             "psk": "password\nkey-mgmt=none"},
                            selected):
                with self.subTest(invalid=invalid), self._wifi_paths(sysconn), \
                     mock.patch.object(AGENT, "run_cmd") as run_command:
                    with self.assertRaises(ValueError):
                        AGENT.migrate_wifi({"wifiNetworks": [selected, invalid]})
                    self.assertFalse(sysconn.exists())
                    run_command.assert_not_called()

    @skipIf(os.name == "nt", "NetworkManager keyfile permissions require POSIX")
    def test_wifi_publish_failure_leaves_no_profile_or_secret_temporary(self):
        with TemporaryDirectory() as temporary:
            sysconn = Path(temporary) / "system-connections"
            def fail_publish(source, destination):
                self.assertEqual(Path(source).stat().st_mode & 0o777, 0o600)
                self.assertIn(b"password-one", Path(source).read_bytes())
                raise OSError("publish denied")
            with self._wifi_paths(sysconn), mock.patch.object(AGENT.os, "link",
                    side_effect=fail_publish), mock.patch.object(AGENT, "run_cmd") as run_command:
                with self.assertRaisesRegex(OSError, "publish denied"):
                    AGENT.migrate_wifi({"wifiNetworks": [
                        {"ssid": "valid", "security": "wpa-psk", "psk": "password-one"}]})
            self.assertEqual(list(sysconn.iterdir()), [])
            run_command.assert_not_called()

    @staticmethod
    def _wifi_paths(sysconn):
        return mock.patch.object(
            AGENT, "Path",
            side_effect=lambda path: sysconn if path ==
                "/etc/NetworkManager/system-connections" else Path(path))

    def test_selected_package_identifiers_fail_before_root_command(self):
        invalid = (
            {"flatpakId": "--installation=other"},
            {"flatpakId": "org.example..App"},
            {"nativePackage": "--installroot=/tmp/other"},
            {"nativePackage": "../outside"},
            {},
        )
        with mock.patch.object(AGENT, "run_cmd") as run_command:
            for selected in invalid:
                with self.subTest(selected=selected):
                    manifest = {"suggestedPackages": [{"autoInstall": True,
                                                       **selected}]}
                    with self.assertRaises(ValueError):
                        AGENT.install_suggested_packages(manifest)
            run_command.assert_not_called()

    def test_catalog_chrome_flatpak_keeps_exact_install_argv(self):
        manifest = {"suggestedPackages": [{"autoInstall": True,
                                           "flatpakId": "com.google.Chrome",
                                           "linuxAppName": "Google Chrome"}]}
        with mock.patch.object(AGENT, "run_cmd") as run_command:
            AGENT.install_suggested_packages(manifest)
        run_command.assert_called_once_with(
            ["flatpak", "install", "-y", "--noninteractive", "flathub",
             "com.google.Chrome"], timeout=600)

    def test_selected_chrome_installs_when_companion_suggestion_is_missing(self):
        manifest = {"browsers": [{"name": "Google Chrome", "engine": "chromium"}],
                    "suggestedPackages": [{"autoInstall": True,
                                           "nativePackage": "tree"}]}
        with mock.patch.object(AGENT, "run_cmd") as run_command:
            AGENT.install_suggested_packages(manifest)
        self.assertEqual(run_command.call_args_list, [
            mock.call(["flatpak", "install", "-y", "--noninteractive",
                       "flathub", "com.google.Chrome"], timeout=600),
            mock.call(["dnf", "-y", "install", "tree"], timeout=300),
        ])

    def test_selected_browser_package_is_not_installed_twice(self):
        manifest = {"browsers": [{"name": "Google Chrome"}],
                    "suggestedPackages": [{"autoInstall": True,
                                           "flatpakId": "com.google.Chrome"}]}
        with mock.patch.object(AGENT, "run_cmd") as run_command:
            AGENT.install_suggested_packages(manifest)
        run_command.assert_called_once_with(
            ["flatpak", "install", "-y", "--noninteractive", "flathub",
             "com.google.Chrome"], timeout=600)

    def test_invalid_browser_selection_stops_before_package_manager(self):
        manifest = {"browsers": ["Google Chrome"],
                    "suggestedPackages": [{"autoInstall": True,
                                           "nativePackage": "tree"}]}
        with mock.patch.object(AGENT, "run_cmd") as run_command:
            with self.assertRaises(ValueError):
                AGENT.install_suggested_packages(manifest)
        run_command.assert_not_called()

    def test_dnf5_version_prefix_selects_supported_codec_group_commands(self):
        version = mock.Mock(returncode=0,
                            stdout="dnf5 version 5.4.2.1\nlibdnf5 version 5.4.2.1\n")
        with mock.patch.object(AGENT.subprocess, "run", return_value=version), \
             mock.patch.object(AGENT, "run_cmd") as run_command:
            AGENT.install_codecs({"hardware": {"needsNonFreeCodecs": True}})
        self.assertEqual(run_command.call_args_list, [
            mock.call(["dnf", "-y", "install", "@multimedia",
                       "--exclude=PackageKit-gstreamer-plugin"], timeout=600),
            mock.call(["dnf", "-y", "install", "@sound-and-video"], timeout=300),
        ])

    def test_unknown_dnf_version_keeps_codec_step_retryable(self):
        version = mock.Mock(returncode=0, stdout="unexpected version\n")
        with mock.patch.object(AGENT.subprocess, "run", return_value=version), \
             mock.patch.object(AGENT, "run_cmd") as run_command:
            with self.assertRaisesRegex(RuntimeError, "unknown format"):
                AGENT.install_codecs({"hardware": {"needsNonFreeCodecs": True}})
        run_command.assert_not_called()

    def test_missing_installed_kernel_query_keeps_first_boot_retryable(self):
        empty = mock.Mock(stdout="", returncode=0)
        with mock.patch.object(AGENT, "run_cmd", return_value=empty) as run_command:
            with self.assertRaisesRegex(RuntimeError, "No installed Fedora kernel-core"):
                AGENT.ensure_kernel_modules({})
        run_command.assert_called_once_with(["rpm", "-q", "kernel-core"])

    @staticmethod
    def _selected_display():
        return {"displays": [{"pnpId": "ABC123", "widthPx": 1920,
                              "heightPx": 1080, "refreshHz": 60,
                              "rotationDegrees": 0, "isPrimary": True}],
                "user": {"preferredLinuxUsername": "lab"}}

    @staticmethod
    def _connected_display():
        return [{"pnp_id": "ABC123", "connector": "DP-1", "vendor": "ABC",
                 "product": "Display", "serial": "1"}]

    def test_selected_display_missing_user_home_requires_retry(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            with mock.patch.object(AGENT, "Path", side_effect=self.sandbox_media_paths(root)), \
                 mock.patch.object(AGENT, "_connected_outputs",
                                   return_value=self._connected_display()), \
                 mock.patch.object(AGENT, "_mode_is_supported", return_value=True):
                with self.assertRaisesRegex(RuntimeError, "no installed user home"):
                    AGENT.migrate_display_layout(self._selected_display())

    def test_selected_display_ownership_failure_requires_retry(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "home/lab").mkdir(parents=True)
            with mock.patch.object(AGENT, "Path", side_effect=self.sandbox_media_paths(root)), \
                 mock.patch.object(AGENT, "_connected_outputs",
                                   return_value=self._connected_display()), \
                 mock.patch.object(AGENT, "_mode_is_supported", return_value=True), \
                 mock.patch.object(AGENT, "run_cmd",
                                   side_effect=self.failed_checked_chown):
                with self.assertRaisesRegex(OSError, "ownership transfer failed"):
                    AGENT.migrate_display_layout(self._selected_display())

    @skipIf(os.name == "nt", "POSIX symlinks are required")
    def test_selected_display_rejects_redirected_user_config(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home/lab"
            home.mkdir(parents=True)
            redirected = root / "redirected"
            redirected.mkdir()
            (home / ".config").symlink_to(redirected, target_is_directory=True)
            with mock.patch.object(AGENT, "Path", side_effect=self.sandbox_media_paths(root)), \
                 mock.patch.object(AGENT, "_connected_outputs",
                                   return_value=self._connected_display()), \
                 mock.patch.object(AGENT, "_mode_is_supported", return_value=True), \
                 mock.patch.object(AGENT, "run_cmd") as run_command:
                with self.assertRaisesRegex(RuntimeError, "destination is redirected"):
                    AGENT.migrate_display_layout(self._selected_display())
            self.assertEqual(list(redirected.iterdir()), [])
            run_command.assert_not_called()

    def test_selected_kde_display_staging_failure_requires_retry(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "home/lab").mkdir(parents=True)
            # The selected user layout can be written, but the installed KDE
            # agent directory is unavailable. This must not produce .done.
            with mock.patch.object(AGENT, "Path", side_effect=self.sandbox_media_paths(root)), \
                 mock.patch.object(AGENT, "_connected_outputs",
                                   return_value=self._connected_display()), \
                 mock.patch.object(AGENT, "_mode_is_supported", return_value=True), \
                 mock.patch.object(AGENT, "run_cmd"):
                with self.assertRaises(FileNotFoundError):
                    AGENT.migrate_display_layout(self._selected_display())

    def test_unbound_seed_cleanup_does_not_issue_partition_or_efi_writes(self):
        with mock.patch.object(AGENT, "run_cmd") as run_cmd, \
             mock.patch.object(AGENT, "logger") as logger:
            AGENT.cleanup_installer_partitions({"distroId": "fedora-kde"})
        run_cmd.assert_not_called()
        self.assertIn("copied migration files and logs", logger.warning.call_args.args[0])
        self.assertIn("Keep it private", logger.warning.call_args.args[0])

    def test_nvidia_secure_boot_fails_before_driver_install_or_nouveau_blacklist(self):
        with mock.patch.object(AGENT, "run_cmd") as run_cmd, \
             mock.patch.object(AGENT, "ensure_nvidia_kernel_cmdline") as blacklist:
            with self.assertRaisesRegex(RuntimeError, "module signing"):
                AGENT.install_gpu_drivers({"hardware": {
                    "gpuVendor": "nvidia", "secureBootEnabled": True,
                }})
        run_cmd.assert_not_called()
        blacklist.assert_not_called()

    def test_unstageable_browser_credentials_fail_before_redaction(self):
        with self.assertRaisesRegex(RuntimeError, "without a Linux user"):
            AGENT.stage_credential_import({
                "browsers": [{"name": "Google Chrome", "credentialsBlob": "envelope"}],
                "user": {},
            })
        with self.assertRaisesRegex(RuntimeError, "username is unsafe"):
            AGENT.stage_credential_import({
                "browsers": [{"name": "Google Chrome", "credentialsBlob": "envelope"}],
                "user": {"preferredLinuxUsername": "../root"},
            })

    @skipIf(os.name == "nt", "POSIX ownership and umask are required")
    def test_credential_autostart_is_readable_after_restrictive_first_boot_umask(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "home" / "lab").mkdir(parents=True)
            (root / "opt" / "igloo").mkdir(parents=True)
            (root / "etc" / "xdg" / "autostart").mkdir(parents=True)
            account = mock.Mock(pw_dir=str(root / "home" / "lab"), pw_uid=os.getuid(),
                                pw_gid=os.getgid())
            with mock.patch.object(AGENT, "Path",
                                   side_effect=self.sandbox_media_paths(root)), \
                 mock.patch("pwd.getpwnam", return_value=account), \
                 mock.patch.object(AGENT.os, "chown"):
                previous_umask = os.umask(0o077)
                try:
                    AGENT.stage_credential_import({
                        "browsers": [{"name": "Google Chrome",
                                      "credentialsBlob": "synthetic-encrypted-envelope"}],
                        "user": {"preferredLinuxUsername": "lab"},
                    })
                finally:
                    os.umask(previous_umask)
            desktop = root / "etc/xdg/autostart/igloo-credential-import.desktop"
            hook = root / "opt/igloo/credential-import.sh"
            store = root / "home/lab/.local/share/igloo/credentials.json"
            self.assertEqual(desktop.stat().st_mode & 0o777, 0o644)
            self.assertEqual(hook.stat().st_mode & 0o777, 0o755)
            self.assertEqual(store.stat().st_mode & 0o777, 0o600)

    def test_credential_store_refuses_symlinked_parent_and_store(self):
        if os.name != "posix":
            self.skipTest("POSIX symlink creation is required")
        for relative in (".local", ".local/share", ".local/share/igloo",
                         AGENT._CRED_STORE_REL):
            with self.subTest(relative=relative), TemporaryDirectory() as temporary:
                root = Path(temporary)
                home = root / "home"
                outside = root / "outside"
                home.mkdir()
                outside.mkdir()
                path = home / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                target = outside / "redirected"
                is_directory = relative != AGENT._CRED_STORE_REL
                if is_directory:
                    target.mkdir()
                else:
                    target.write_text("unchanged", encoding="utf-8")
                path.symlink_to(target, target_is_directory=is_directory)
                with self.assertRaisesRegex(RuntimeError, "redirected|unsafe"):
                    AGENT._prepare_credential_store(home)
                if not is_directory:
                    self.assertEqual(target.read_text(encoding="utf-8"), "unchanged")

    def test_uninitialized_login_database_is_never_fabricated_or_modified(self):
        with TemporaryDirectory() as temporary:
            db = Path(temporary) / "Login Data"
            with self.assertRaisesRegex(RuntimeError, "Browser-created Login Data"):
                AGENT._import_into_login_data(db, [{
                    "url": "https://example.invalid/login",
                    "username": "fixture", "password": "synthetic-password",
                }])
            self.assertFalse(db.exists())
            connection = sqlite3.connect(db)
            try:
                connection.execute(
                    "CREATE TABLE logins (id INTEGER PRIMARY KEY, "
                    "origin_url TEXT, username_value TEXT, "
                    "unavailable_field TEXT NOT NULL)")
                connection.commit()
            finally:
                connection.close()
            original = db.read_bytes()
            with self.assertRaisesRegex(RuntimeError, "Browser-created Login Data"):
                AGENT._import_into_login_data(db, [{
                    "url": "https://example.invalid/login",
                    "username": "fixture", "password": "synthetic-password",
                }])
            self.assertEqual(original, db.read_bytes())

    def test_browser_created_chrome155_schema_accepts_v10_login(self):
        schema = (Path(__file__).resolve().parents[2] / "tools/chromium-crypto"
                  / "chrome155_login_data_schema.sql")
        with TemporaryDirectory() as temporary:
            db = Path(temporary) / "Login Data"
            with sqlite3.connect(db) as connection:
                connection.executescript(schema.read_text(encoding="utf-8"))
            login = {"url": "https://fedora-fixture.invalid/login",
                     "username": "fixture", "password": "synthetic-password"}
            self.assertTrue(AGENT._chromium_login_db_ready(db))
            self.assertEqual(1, AGENT._import_into_login_data(db, [login]))
            self.assertEqual(0, AGENT._import_into_login_data(db, [login]))
            with sqlite3.connect(db) as connection:
                self.assertEqual("43", connection.execute(
                    "SELECT value FROM meta WHERE key='version'").fetchone()[0])
                self.assertEqual((login["url"], login["username"]), connection.execute(
                    "SELECT origin_url, username_value FROM logins").fetchone())
                self.assertTrue(connection.execute(
                    "SELECT password_value FROM logins").fetchone()[0].startswith(b"v10"))

    def test_partial_login_database_failure_retries_only_failed_envelope(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            chrome = {"name": "Google Chrome", "blob": "encrypted-chrome"}
            brave = {"name": "Brave", "blob": "encrypted-brave"}
            store.write_text(json.dumps({"attempts": 0,
                                         "browsers": [chrome, brave]}),
                             encoding="utf-8")
            store.chmod(0o600)
            payload = {"logins": [{"url": "https://example.invalid/login",
                                   "username": "fixture",
                                   "password": "synthetic-password"}],
                       "cookies": []}
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, "lab")), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_decrypt_envelope", return_value=payload), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_chromium_login_db_ready", return_value=True), \
                 mock.patch.object(AGENT, "_import_into_login_data",
                                   side_effect=[1, OSError("injected database failure"), 1]) \
                         as import_login:
                self.assertEqual(AGENT.run_user_credential_import(), 0)
                retained = json.loads(store.read_text(encoding="utf-8"))
                self.assertEqual(retained["browsers"], [brave])
                self.assertEqual(retained["attempts"], 0)
                if os.name == "posix":
                    self.assertEqual(store.stat().st_mode & 0o777, 0o600)
                self.assertEqual(AGENT.run_user_credential_import(), 0)
                self.assertFalse(store.exists())
                self.assertEqual(import_login.call_count, 3)

    def test_absent_browser_never_consumes_selected_credential_envelope(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            entries = [{"name": "Google Chrome", "blob": "encrypted-chrome"},
                       {"name": "Unmapped Browser", "blob": "encrypted-other"}]
            store.write_text(json.dumps({"attempts": 0, "browsers": entries}),
                             encoding="utf-8")
            store.chmod(0o600)
            payload = {"logins": [{"url": "https://example.invalid/login",
                                  "username": "fixture", "password": "synthetic-password"}],
                       "cookies": []}
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, "lab")) as ask_password, \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_decrypt_envelope", return_value=payload), \
                 mock.patch.object(AGENT, "_chromium_config_homes", return_value=[]), \
                 mock.patch.object(AGENT, "_import_into_login_data") as import_login:
                self.assertEqual(0, AGENT.run_user_credential_import())
            retained = json.loads(store.read_text(encoding="utf-8"))
            self.assertEqual(entries, retained["browsers"])
            self.assertEqual(0, retained["attempts"])
            import_login.assert_not_called()
            ask_password.assert_not_called()
            if os.name == "posix":
                self.assertEqual(0o600, store.stat().st_mode & 0o777)

    def test_installed_browser_without_native_login_data_waits_without_prompt(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            original = json.dumps({"attempts": 0, "browsers": [
                {"name": "Google Chrome", "blob": "encrypted-chrome"}]})
            store.write_text(original, encoding="utf-8")
            store.chmod(0o600)
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".var/app/com.google.Chrome/config"]), \
                 mock.patch.object(AGENT, "_ask_password") as ask_password:
                self.assertEqual(0, AGENT.run_user_credential_import())
            ask_password.assert_not_called()
            self.assertEqual(original, store.read_text(encoding="utf-8"))
            self.assertFalse((home / ".var/app/com.google.Chrome/config/google-chrome"
                              / "Default/Login Data").exists())

    def test_cookie_only_profile_can_import_before_login_database_exists(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            store.write_text(json.dumps({"attempts": 0, "browsers": [
                {"name": "Google Chrome", "blob": "encrypted-chrome"}]}),
                encoding="utf-8")
            profile = home / ".config/google-chrome/Default"
            (profile / "Network").mkdir(parents=True)
            (profile / "Network/Cookies").write_bytes(b"copied cookie database")
            payload = {"logins": [], "cookies": [{"host": ".example.invalid",
                                                 "name": "fixture", "path": "/",
                                                 "value": "synthetic"}]}
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, "lab")) \
                         as ask_password, \
                 mock.patch.object(AGENT, "_decrypt_envelope", return_value=payload), \
                 mock.patch.object(AGENT, "_reencrypt_cookies", return_value=1) \
                         as import_cookies, \
                 mock.patch.object(AGENT, "_import_into_login_data") as import_login:
                self.assertEqual(0, AGENT.run_user_credential_import())
            ask_password.assert_called_once()
            import_cookies.assert_called_once_with(profile, payload["cookies"])
            import_login.assert_not_called()
            self.assertFalse(store.exists())
            self.assertFalse((profile / "Login Data").exists())

    def test_absent_browser_places_portable_profile_in_native_fallback(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            staged = root / "chromium/Google Chrome"
            staged.mkdir(parents=True)
            (staged / "Bookmarks").write_text("selected", encoding="utf-8")
            home = root / "home"
            home.mkdir()
            with mock.patch.object(AGENT, "_CHROMIUM_STAGE_DIR", root / "chromium"), \
                 mock.patch.object(AGENT, "_chromium_config_homes", return_value=[]):
                AGENT.place_staged_chromium_profiles(home)
            placed = home / ".config/google-chrome/Default/Bookmarks"
            self.assertEqual("selected", placed.read_text(encoding="utf-8"))
            self.assertFalse(staged.exists())
            if os.name == "posix":
                self.assertEqual(0o600, placed.stat().st_mode & 0o777)

    def test_chromium_roots_require_an_observed_install(self):
        with TemporaryDirectory() as temporary, \
             mock.patch.object(AGENT.shutil, "which", return_value=None):
            self.assertEqual([], AGENT._chromium_config_homes(
                "com.google.Chrome", ("google-chrome",), Path(temporary)))

    def test_unavailable_password_dialog_preserves_envelope_and_attempt_budget(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            original = json.dumps({"attempts": 1, "browsers": [
                {"name": "Google Chrome", "blob": "encrypted-chrome"}]})
            store.write_text(original, encoding="utf-8")
            store.chmod(0o600)
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_chromium_login_db_ready", return_value=True), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, None)):
                self.assertEqual(AGENT.run_user_credential_import(), 0)
            self.assertEqual(store.read_text(encoding="utf-8"), original)
            if os.name == "posix":
                self.assertEqual(store.stat().st_mode & 0o777, 0o600)

    def test_selected_wallpaper_missing_from_install_fails_for_retry(self):
        with mock.patch.object(AGENT.Path, "is_file", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "Selected wallpaper is missing"):
                AGENT.migrate_wallpaper({
                    "wallpaper": {"fileName": "igloo-wallpaper.jpg"},
                })

    def test_selected_avatar_missing_from_install_fails_for_retry(self):
        with mock.patch.object(AGENT.Path, "is_file", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "Selected account picture is missing"):
                AGENT.migrate_account_picture({
                    "accountPicture": {"fileName": "igloo-avatar.jpg"},
                })

    def test_selected_media_rejects_path_traversal(self):
        with self.assertRaisesRegex(RuntimeError, "unsafe staged file name"):
            AGENT._selected_media_source("../../etc/shadow", "wallpaper")

    def test_selected_wallpaper_ownership_failure_requires_retry(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            image = root / "image.jpg"
            image.write_bytes(b"selected image")
            (root / "home" / "lab").mkdir(parents=True)
            with mock.patch.object(AGENT, "_selected_media_source", return_value=image), \
                 mock.patch.object(AGENT, "Path",
                                   side_effect=self.sandbox_media_paths(root)), \
                 mock.patch.object(AGENT, "logger"), \
                 mock.patch.object(AGENT, "run_cmd",
                                   side_effect=self.failed_checked_chown):
                with self.assertRaisesRegex(OSError, "ownership transfer failed"):
                    AGENT.migrate_wallpaper({
                        "wallpaper": {"fileName": "igloo-wallpaper.jpg"},
                        "user": {"preferredLinuxUsername": "lab"},
                    })

    def test_selected_wallpaper_hook_targets_only_the_selected_image(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            image = root / "source.png"
            image.write_bytes(b"selected image")
            (root / "home" / "lab" / "Pictures").mkdir(parents=True)
            (root / "home" / "lab" / "Pictures" / "wallpaper.jpg").write_bytes(
                b"unselected image")
            (root / "opt" / "igloo").mkdir(parents=True)
            (root / "etc" / "xdg" / "autostart").mkdir(parents=True)
            with mock.patch.object(AGENT, "_selected_media_source", return_value=image), \
                 mock.patch.object(AGENT, "Path",
                                   side_effect=self.sandbox_media_paths(root)), \
                 mock.patch.object(AGENT, "logger"), \
                 mock.patch.object(AGENT, "run_cmd"):
                AGENT.migrate_wallpaper({
                    "wallpaper": {"fileName": "igloo-wallpaper.png"},
                    "user": {"preferredLinuxUsername": "lab"},
                })
            hook = (root / "opt" / "igloo" / "wallpaper-apply.sh").read_text()
            self.assertIn('WALLPAPER="$HOME/Pictures/wallpaper.png"', hook)
            self.assertIn('plasma-apply-wallpaperimage "$WALLPAPER"', hook)
            self.assertNotIn('wallpaper.*', hook)

    def test_selected_avatar_ownership_failure_requires_retry(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            image = root / "image.jpg"
            image.write_bytes(b"selected avatar")
            (root / "home" / "lab").mkdir(parents=True)
            with mock.patch.object(AGENT, "_selected_media_source", return_value=image), \
                 mock.patch.object(AGENT, "Path",
                                   side_effect=self.sandbox_media_paths(root)), \
                 mock.patch.object(AGENT, "logger"), \
                 mock.patch.object(AGENT, "run_cmd",
                                   side_effect=self.failed_checked_chown):
                with self.assertRaisesRegex(OSError, "ownership transfer failed"):
                    AGENT.migrate_account_picture({
                        "accountPicture": {"fileName": "igloo-avatar.jpg"},
                        "user": {"preferredLinuxUsername": "lab"},
                    })

    def test_failed_chromium_placement_retains_staged_profile_for_retry(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            stage = root / "chromium"
            selected = stage / "Google Chrome"
            selected.mkdir(parents=True)
            (selected / "Bookmarks").write_text("selected", encoding="utf-8")
            with mock.patch.object(AGENT, "_CHROMIUM_STAGE_DIR", stage), \
                 mock.patch.object(AGENT, "_place_chromium_files",
                                   side_effect=OSError("destination unavailable")):
                with self.assertRaisesRegex(OSError, "destination unavailable"):
                    AGENT.place_staged_chromium_profiles(root / "home")
            self.assertTrue((selected / "Bookmarks").is_file())

    def test_matching_seed_and_installed_manifest_are_both_redacted(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            installed, seed = root / "installed.json", root / "seed.json"
            manifest = {"user": {"linuxPasswordCrypted": "$6$lab-hash"},
                        "wifiNetworks": [{"psk": "secret"}],
                        "browsers": [{"credentialsBlob": "envelope"}]}
            original = json.dumps(manifest).encode("utf-8")
            installed.write_bytes(original)
            seed.write_bytes(original)
            AGENT._redact_matching_manifest_pair(installed, seed, manifest)
            self.assertEqual(installed.read_bytes(), seed.read_bytes())
            redacted = json.loads(installed.read_bytes())
            self.assertIsNone(redacted["user"]["linuxPasswordCrypted"])
            self.assertIsNone(redacted["wifiNetworks"][0]["psk"])
            self.assertIsNone(redacted["browsers"][0]["credentialsBlob"])

    def test_changed_seed_preserves_installed_retry_input(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            installed, seed = root / "installed.json", root / "seed.json"
            manifest = {"wifiNetworks": [{"psk": "retry-secret"}]}
            original = json.dumps(manifest).encode("utf-8")
            installed.write_bytes(original)
            seed.write_bytes(b"different input")
            with self.assertRaisesRegex(RuntimeError, "byte-match"):
                AGENT._redact_matching_manifest_pair(installed, seed, manifest)
            self.assertEqual(installed.read_bytes(), original)

    def test_seed_write_failure_preserves_installed_retry_input(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            installed, seed = root / "installed.json", root / "seed.json"
            manifest = {"wifiNetworks": [{"psk": "retry-secret"}]}
            original = json.dumps(manifest).encode("utf-8")
            installed.write_bytes(original)
            seed.write_bytes(original)
            with mock.patch.object(AGENT.os, "replace", side_effect=OSError("write denied")):
                with self.assertRaisesRegex(OSError, "write denied"):
                    AGENT._redact_matching_manifest_pair(installed, seed, manifest)
            self.assertEqual(installed.read_bytes(), original)

    def test_installed_write_failure_after_seed_redaction_keeps_retry_input(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            installed, seed = root / "installed.json", root / "seed.json"
            manifest = {"wifiNetworks": [{"psk": "retry-secret"}]}
            original = json.dumps(manifest).encode("utf-8")
            installed.write_bytes(original)
            seed.write_bytes(original)
            replace = AGENT._replace_bound_file_with_readback

            def fail_installed(path, expected, replacement, *, private):
                if private:
                    raise OSError("installed write denied")
                replace(path, expected, replacement, private=private)

            with mock.patch.object(AGENT, "_replace_bound_file_with_readback",
                                   side_effect=fail_installed):
                with self.assertRaisesRegex(OSError, "installed write denied"):
                    AGENT._redact_matching_manifest_pair(installed, seed, manifest)
            self.assertEqual(installed.read_bytes(), original)
            self.assertEqual(seed.read_bytes(), original)


if __name__ == "__main__":
    main()
