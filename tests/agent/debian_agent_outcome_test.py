"""First-boot outcome checks without running commands or touching a target disk."""

import importlib.util
import hashlib
import json
import os
from contextlib import ExitStack
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import Mock, patch


AGENT_PATH = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
spec = importlib.util.spec_from_file_location("debian_family_outcome_agent", AGENT_PATH)
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)

STEP_FUNCTIONS = (
    "set_user_password", "set_keyboard", "migrate_wifi", "wait_for_network",
    "apt_update", "install_gpu_drivers", "install_codecs", "ensure_firmware",
    "enable_os_prober", "configure_boot_menu", "setup_flathub",
    "install_suggested_packages", "ensure_migration_tools", "migrate_user_files",
    "migrate_display_layout", "migrate_wallpaper", "migrate_account_picture",
    "install_welcome_app", "stage_credential_import", "redact_seed", "redact_manifest",
    "cleanup_installer_partitions",
)


class FirstBootOutcomeTests(unittest.TestCase):
    def test_standalone_modes_require_an_explicit_supported_installed_os(self):
        for mode, action in (("--import-credentials", "_run_user_mode"),
                             ("--fix-boot-order", "_run_boot_order_mode")):
            for installed_id in (None, "fedora", "debian", "linuxmint"):
                with self.subTest(mode=mode, installed_id=installed_id), \
                        patch.object(sys, "argv", ["agent.py", mode]), \
                        patch.object(agent, "os_release", return_value=(
                            {"ID": installed_id} if installed_id else {})), \
                        patch.object(agent, action, return_value=0) as dispatch:
                    result = agent.main()
                    if installed_id in ("debian", "linuxmint"):
                        self.assertEqual(result, 0)
                        dispatch.assert_called_once()
                    else:
                        self.assertEqual(result, 2)
                        dispatch.assert_not_called()

    def test_crash_before_journal_publication_resumes_scrub_without_migration(self):
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            original = json.dumps({"schemaVersion": 1, "distroId": "debian"}).encode()
            manifest = root / "manifest.json"
            manifest.write_bytes(original)
            retry = root / "manifest.retry.json"
            retry.write_bytes(original)
            retry.chmod(0o600)
            identity = root / "seed-identity.json"
            identity.write_text(json.dumps({"version": 1, "files": [
                {"path": "migration-manifest.json", "sha256": hashlib.sha256(original).hexdigest()},
                {"path": "preseed.cfg", "sha256": "1" * 64},
            ]}))
            identity.chmod(0o600)
            stack.enter_context(patch.object(sys, "argv", [
                "agent.py", "--manifest", str(manifest), "--log-dir", temporary]))
            stack.enter_context(patch.object(agent, "configure_logging"))
            stack.enter_context(patch.object(agent, "os_release", return_value={"ID": "debian"}))
            steps = {name: stack.enter_context(patch.object(agent, name))
                     for name in STEP_FUNCTIONS}
            self.assertEqual(agent.main(), 0)
            steps["redact_seed"].assert_called_once()
            steps["redact_manifest"].assert_called_once()
            steps["cleanup_installer_partitions"].assert_called_once()
            steps["stage_credential_import"].assert_not_called()
            steps["migrate_user_files"].assert_not_called()

    def run_first_boot(self, *, failing_step=None, only=None):
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            manifest = Path(temporary) / "manifest.json"
            manifest.write_text(json.dumps({"schemaVersion": 1, "distroId": "debian"}), encoding="utf-8")
            argv = ["agent.py", "--manifest", str(manifest), "--log-dir", temporary]
            if only is not None:
                argv.extend(("--only", only))
            stack.enter_context(patch.object(sys, "argv", argv))
            stack.enter_context(patch.object(agent, "configure_logging"))
            stack.enter_context(patch.object(agent, "distro_id", return_value="debian"))
            stack.enter_context(patch.object(agent, "os_release", return_value={"ID": "debian"}))
            steps = {}
            for name in STEP_FUNCTIONS:
                action = Mock(side_effect=RuntimeError("fixture failure") if name == failing_step else None)
                steps[name] = stack.enter_context(patch.object(agent, name, action))
            result = agent.main()
            return result, steps

    def test_failed_step_is_nonzero_and_preserves_seed_and_manifest(self):
        result, steps = self.run_first_boot(failing_step="migrate_user_files")
        self.assertEqual(result, 1)
        steps["redact_manifest"].assert_not_called()
        steps["redact_seed"].assert_not_called()
        steps["cleanup_installer_partitions"].assert_not_called()
        steps["stage_credential_import"].assert_called_once()

    def test_success_reaches_final_steps(self):
        result, steps = self.run_first_boot()
        self.assertEqual(result, 0)
        steps["redact_manifest"].assert_called_once()
        steps["redact_seed"].assert_called_once()
        steps["cleanup_installer_partitions"].assert_called_once()

    def test_only_cannot_invoke_cleanup_or_succeed_with_unknown_step(self):
        for selection in ("cleanup-seed", "misspelled-step", "display-layout,cleanup-seed"):
            with self.subTest(selection=selection):
                with self.assertRaises(SystemExit) as error:
                    self.run_first_boot(only=selection)
                self.assertEqual(error.exception.code, 2)

    def test_password_command_failure_is_a_failed_step(self):
        with patch.object(agent.subprocess, "run", return_value=Mock(returncode=1, stderr="fixture")):
            with self.assertRaises(RuntimeError):
                agent.set_user_password({"user": {"preferredLinuxUsername": "user", "linuxPasswordCrypted": "$6$hash"}})

    def test_selected_package_failure_cannot_report_installed(self):
        def fail_if_required(*args, check=True, **kwargs):
            if check:
                raise RuntimeError("selected package failed")
            return Mock(returncode=1)

        native = {"suggestedPackages": [{"nativePackage": "selected-fixture", "autoInstall": True}]}
        with patch.object(agent, "apt_install", side_effect=fail_if_required):
            with self.assertRaisesRegex(RuntimeError, "selected package failed"):
                agent.install_suggested_packages(native)
        flatpak = {"suggestedPackages": [{"flatpakId": "org.example.Selected", "autoInstall": True}]}
        with patch.object(agent, "run_cmd", side_effect=fail_if_required):
            with self.assertRaisesRegex(RuntimeError, "selected package failed"):
                agent.install_suggested_packages(flatpak)

    def test_selected_codecs_and_firmware_failure_cannot_report_installed(self):
        selected = {"hardware": {"needsNonFreeCodecs": True}}
        with patch.object(agent, "apt_install", side_effect=RuntimeError("apt failed")) as install:
            with patch.object(agent, "distro_id", return_value="debian"), \
                    patch.object(agent, "is_ubuntu_like", return_value=False):
                with self.assertRaisesRegex(RuntimeError, "apt failed"):
                    agent.install_codecs(selected)
                self.assertEqual(["libavcodec-extra", "gstreamer1.0-libav",
                                  "gstreamer1.0-plugins-ugly", "gstreamer1.0-plugins-bad"],
                                 install.call_args.args[0])
                with self.assertRaisesRegex(RuntimeError, "apt failed"):
                    agent.ensure_firmware(selected)
                self.assertEqual(["firmware-linux"], install.call_args.args[0])
            with patch.object(agent, "distro_id", return_value="linuxmint"):
                with self.assertRaisesRegex(RuntimeError, "apt failed"):
                    agent.install_codecs(selected)
                self.assertEqual(["mint-meta-codecs"], install.call_args.args[0])
            self.assertEqual(3, install.call_count)
        with patch.object(agent, "distro_id", return_value="ubuntu"), \
                patch.object(agent, "is_ubuntu_like", return_value=True), \
                patch.object(agent, "run_cmd", side_effect=RuntimeError("debconf failed")), \
                patch.object(agent, "apt_install") as install:
            with self.assertRaisesRegex(RuntimeError, "debconf failed"):
                agent.install_codecs(selected)
            install.assert_not_called()
        with patch.object(agent, "apt_install") as install:
            agent.install_codecs({"hardware": {"needsNonFreeCodecs": False}})
            install.assert_not_called()

    def test_selected_wifi_write_and_reload_failure_must_retry(self):
        selected = {"wifiNetworks": [{"ssid": "iGloo-selected-fixture",
                                       "security": "wpa-psk", "psk": "synthetic-fixture-key"}]}
        with TemporaryDirectory() as temporary:
            connection_dir = Path(temporary) / "system-connections"
            local_path = lambda name: connection_dir if name == "/etc/NetworkManager/system-connections" else Path(name)
            with patch.object(agent, "Path", side_effect=local_path), \
                    patch.object(agent, "_write_wifi_profile", side_effect=OSError("disk full"), create=True), \
                    patch.object(agent, "run_cmd") as reload:
                with self.assertRaisesRegex(OSError, "disk full"):
                    agent.migrate_wifi(selected)
                reload.assert_not_called()
            with patch.object(agent, "Path", side_effect=local_path):
                def fail_required_reload(*args, check=True, **kwargs):
                    if check:
                        raise RuntimeError("NetworkManager reload failed")
                    return Mock(returncode=1)

                with patch.object(agent, "run_cmd", side_effect=fail_required_reload):
                    with self.assertRaisesRegex(RuntimeError, "reload failed"):
                        agent.migrate_wifi(selected)
            profile = connection_dir / agent._safe_filename("iGloo-selected-fixture")
            first_bytes = profile.read_bytes()
            self.assertIn(b"psk=synthetic-fixture-key", first_bytes)
            self.assertEqual(0o600, profile.stat().st_mode & 0o777)
            with patch.object(agent, "Path", side_effect=local_path), \
                    patch.object(agent, "run_cmd") as reload:
                agent.migrate_wifi(selected)
                reload.assert_called_once_with(["nmcli", "connection", "reload"], timeout=60)
            self.assertEqual(first_bytes, profile.read_bytes())

    def test_distinct_selected_wifi_names_survive_sanitized_filename_collision(self):
        selected = {"wifiNetworks": [
            {"ssid": "selected?one", "security": "wpa-psk", "psk": "fixture-key-one"},
            {"ssid": "selected/one", "security": "wpa-psk", "psk": "fixture-key-two"},
        ]}
        names = [agent._safe_filename(net["ssid"]) for net in selected["wifiNetworks"]]
        self.assertNotEqual(names[0], names[1])
        with TemporaryDirectory() as temporary:
            connection_dir = Path(temporary) / "system-connections"
            local_path = lambda name: connection_dir if name == "/etc/NetworkManager/system-connections" else Path(name)
            with patch.object(agent, "Path", side_effect=local_path), \
                    patch.object(agent, "run_cmd") as reload:
                agent.migrate_wifi(selected)
                reload.assert_called_once_with(["nmcli", "connection", "reload"], timeout=60)
            self.assertIn("psk=fixture-key-one", (connection_dir / names[0]).read_text())
            self.assertIn("psk=fixture-key-two", (connection_dir / names[1]).read_text())
            self.assertEqual(0o600, (connection_dir / names[0]).stat().st_mode & 0o777)
            self.assertEqual(0o600, (connection_dir / names[1]).stat().st_mode & 0o777)

    def test_duplicate_selected_wifi_is_rejected_before_writing(self):
        selected = {"wifiNetworks": [
            {"ssid": "duplicate", "security": "wpa-psk", "psk": "fixture-key-one"},
            {"ssid": "duplicate", "security": "wpa-psk", "psk": "fixture-key-two"},
        ]}
        with TemporaryDirectory() as temporary:
            connection_dir = Path(temporary) / "system-connections"
            local_path = lambda name: connection_dir if name == "/etc/NetworkManager/system-connections" else Path(name)
            with patch.object(agent, "Path", side_effect=local_path), \
                    patch.object(agent, "_write_wifi_profile", create=True) as write, \
                    patch.object(agent, "run_cmd") as reload:
                with self.assertRaisesRegex(ValueError, "more than once|duplicate|collision"):
                    agent.migrate_wifi(selected)
                write.assert_not_called()
                reload.assert_not_called()

    def test_wpa_without_recovered_key_stages_a_promptable_profile(self):
        selected = {"wifiNetworks": [{"ssid": "promptable-fixture", "security": "wpa-psk",
                                       "psk": None, "hidden": False}]}
        with TemporaryDirectory() as temporary:
            connection_dir = Path(temporary) / "system-connections"
            local_path = lambda name: connection_dir if name == "/etc/NetworkManager/system-connections" else Path(name)
            with patch.object(agent, "Path", side_effect=local_path), \
                    patch.object(agent, "run_cmd") as reload:
                agent.migrate_wifi(selected)
                reload.assert_called_once_with(["nmcli", "connection", "reload"], timeout=60)
            profile = connection_dir / agent._safe_filename("promptable-fixture")
            contents = profile.read_text()
            self.assertIn("key-mgmt=wpa-psk", contents)
            self.assertIn("psk-flags=1", contents)
            self.assertIn("autoconnect=false", contents)
            self.assertNotIn("\npsk=", contents)
            self.assertEqual(0o600, profile.stat().st_mode & 0o777)

    def test_wifi_keyfile_fields_reject_line_injection_before_writing(self):
        for network in (
            {"ssid": "trusted\n[connection]", "security": "open", "psk": None},
            {"ssid": "trusted", "security": "wpa-psk",
             "psk": "fixture-key\n[ipv4]"},
        ):
            with self.subTest(network=network["ssid"]), TemporaryDirectory() as temporary:
                connection_dir = Path(temporary) / "system-connections"
                local_path = lambda name: connection_dir if name == "/etc/NetworkManager/system-connections" else Path(name)
                with patch.object(agent, "Path", side_effect=local_path), \
                        patch.object(agent, "_write_wifi_profile", create=True) as write, \
                        patch.object(agent, "run_cmd") as reload:
                    with self.assertRaises(ValueError):
                        agent.migrate_wifi({"wifiNetworks": [network]})
                    write.assert_not_called()
                    reload.assert_not_called()

    def test_wifi_profile_publication_is_private_and_no_clobber(self):
        with TemporaryDirectory() as temporary:
            profile = Path(temporary) / "igloo-selected.nmconnection"
            content = b"[connection]\nid=selected\n"
            agent._write_wifi_profile(profile, content)
            self.assertEqual(content, profile.read_bytes())
            self.assertEqual(0o600, profile.stat().st_mode & 0o777)
            agent._write_wifi_profile(profile, content)  # Exact first-boot retry.
            with self.assertRaisesRegex(RuntimeError, "differs"):
                agent._write_wifi_profile(profile, content + b"changed")
            self.assertEqual(content, profile.read_bytes())

            profile.unlink()
            outside = Path(temporary) / "outside"
            outside.write_bytes(b"protected\n")
            profile.symlink_to(outside)
            with self.assertRaisesRegex(RuntimeError, "unsafe"):
                agent._write_wifi_profile(profile, content)
            self.assertEqual(b"protected\n", outside.read_bytes())

    def test_wifi_keyfile_preserves_leading_spaces_and_backslashes(self):
        contents = agent._nm_keyfile(" Cafe\\WiFi", "wpa-psk", " pass\\phrase", False)
        self.assertIn(r"id=\sCafe\\WiFi", contents)
        self.assertIn(r"ssid=\sCafe\\WiFi", contents)
        self.assertIn(r"psk=\spass\\phrase", contents)

    @unittest.skipIf(os.name == "nt", "GLib keyfile parsing requires POSIX")
    def test_wifi_keyfile_roundtrips_selected_strings_through_glib(self):
        try:
            from gi.repository import GLib
        except ImportError:
            self.skipTest("GLib Python binding is unavailable")
        ssid = r"  Lab\Guest"
        psk = r" Secret\Pass"
        content = agent._nm_keyfile(ssid, "wpa-psk", psk, False)
        keyfile = GLib.KeyFile()
        self.assertTrue(keyfile.load_from_data(content, len(content),
                                               GLib.KeyFileFlags.NONE))
        self.assertEqual(ssid, keyfile.get_string("connection", "id"))
        self.assertEqual(ssid, keyfile.get_string("wifi", "ssid"))
        self.assertEqual(psk, keyfile.get_string("wifi-security", "psk"))

    def test_selected_package_identifiers_are_validated_before_any_install(self):
        valid = {"suggestedPackages": [
            {"flatpakId": "com.google.Chrome", "autoInstall": True},
            {"flatpakId": "app.zen_browser.zen", "autoInstall": True},
            {"nativePackage": "tree", "autoInstall": True},
        ]}
        with patch.object(agent, "run_cmd") as flatpak, patch.object(agent, "apt_install") as apt:
            agent.install_suggested_packages(valid)
        flatpak.assert_called_once()
        apt.assert_called_once_with(["tree"], timeout=600)

        unsafe = (
            {"flatpakId": "--installation=other"},
            {"flatpakId": "org.example..App"},
            {"nativePackage": "--installroot=/tmp/other"},
            {"nativePackage": "../outside"},
            {},
        )
        for identifier in unsafe:
            with self.subTest(identifier=identifier):
                selected = {"suggestedPackages": [
                    {"nativePackage": "tree", "autoInstall": True},
                    {**identifier, "autoInstall": True},
                ]}
                with patch.object(agent, "run_cmd") as flatpak, \
                        patch.object(agent, "apt_install") as apt:
                    with self.assertRaises(ValueError):
                        agent.install_suggested_packages(selected)
                flatpak.assert_not_called()
                apt.assert_not_called()

    def test_missing_selected_source_is_not_reported_as_a_copy(self):
        with TemporaryDirectory() as temporary:
            source = Path(temporary) / "missing-source"
            destination = Path(temporary) / "destination"
            with self.assertRaises(RuntimeError):
                agent._copy_tree(source, destination)

    def test_selected_tree_copies_offline_with_private_permissions(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            (source / "nested").mkdir(parents=True)
            (source / "nested" / "probe.txt").write_text("staged contents\n")
            # A FAT32 OEMDRV mount synthesizes execute bits for regular files.
            (source / "nested" / "probe.txt").chmod(0o777)
            selected_mtime = 1_700_000_000_123_456_789
            os.utime(source / "nested" / "probe.txt",
                     ns=(selected_mtime, selected_mtime))
            (source / "Thumbs.db").write_text("excluded\n")
            (source / "link.txt").symlink_to(source / "nested" / "probe.txt")
            destination = root / "destination"
            with patch.object(agent, "apt_install") as install:
                agent._copy_tree(source, destination)
            install.assert_not_called()
            copied = destination / "nested" / "probe.txt"
            self.assertEqual("staged contents\n", copied.read_text())
            self.assertEqual(0o600, copied.stat().st_mode & 0o777)
            self.assertEqual(selected_mtime, copied.stat().st_mtime_ns)
            self.assertEqual(0o700, (destination / "nested").stat().st_mode & 0o777)
            self.assertFalse((destination / "Thumbs.db").exists())
            self.assertFalse((destination / "link.txt").exists())

    def test_retry_replaces_public_file_only_after_private_copy(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "staged" / "Documents"
            source.mkdir(parents=True)
            selected = source / "history.txt"
            selected.write_bytes(b"new private content")
            destination = root / "home" / "Documents"
            destination.mkdir(parents=True)
            target = destination / "history.txt"
            target.write_bytes(b"old content")
            target.chmod(0o777)

            observations = []
            real_copy = agent.shutil.copyfileobj

            def observe_copy(reader, writer, *args, **kwargs):
                observations.append((target.read_bytes(),
                                     os.fstat(writer.fileno()).st_mode & 0o777))
                return real_copy(reader, writer, *args, **kwargs)

            with patch.object(agent.shutil, "copyfileobj", side_effect=observe_copy):
                agent._copy_tree(source, destination)

            self.assertEqual([before for before, _ in observations], [b"old content"])
            self.assertEqual(target.read_bytes(), b"new private content")
            if os.name == "posix":
                self.assertEqual([mode for _, mode in observations], [0o600])
                self.assertEqual(target.stat().st_mode & 0o777, 0o600)

    @unittest.skipUnless(os.name == "posix", "special-file fixture needs POSIX")
    def test_special_source_file_is_rejected_before_a_private_copy(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            source.mkdir()
            os.mkfifo(source / "unexpected-pipe")
            destination = root / "destination"
            with self.assertRaisesRegex(RuntimeError, "special file"):
                agent._copy_tree(source, destination)
            self.assertEqual(list(destination.iterdir()), [])

    def test_copy_rejects_preexisting_target_link(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            source.mkdir()
            (source / "probe.txt").write_text("selected\n")
            destination = root / "destination"
            destination.mkdir()
            outside = root / "outside.txt"
            outside.write_text("keep\n")
            (destination / "probe.txt").symlink_to(outside)
            with self.assertRaisesRegex(RuntimeError, "unsafe file"):
                agent._copy_tree(source, destination)
            self.assertEqual("keep\n", outside.read_text())

    def test_staged_migration_needs_no_ntfs_or_rsync_package(self):
        with TemporaryDirectory() as temporary:
            staged = Path(temporary) / "files"
            (staged / "Documents").mkdir(parents=True)
            with patch.object(agent, "_STAGED_FILES_DIR", staged):
                with patch.object(agent, "apt_install") as install:
                    agent.ensure_migration_tools({"files": {"folders": [{"name": "Documents"}]}})
            install.assert_not_called()

    def test_selected_files_need_the_windows_home(self):
        real_is_dir = Path.is_dir
        with patch.object(agent.Path, "is_dir", lambda path: str(path) == "/home/igloo-outcome-fixture" or real_is_dir(path)):
            with patch.object(agent, "_find_windows_home", return_value=None):
                with self.assertRaisesRegex(RuntimeError, "source home was not found"):
                    agent.migrate_user_files({
                        "user": {"preferredLinuxUsername": "igloo-outcome-fixture", "windowsUsername": "WindowsUser"},
                        "files": {"folders": [{"name": "Documents", "sourceRelativePath": "Documents"}]},
                    })

    def test_older_included_folders_manifest_still_copies_selected_folder(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home_root = root / "home"
            (home_root / "linuxuser").mkdir(parents=True)
            windows_home = root / "WindowsUser"
            (windows_home / "Documents").mkdir(parents=True)
            with ExitStack() as stack:
                stack.enter_context(patch.object(agent, "Path", lambda value: home_root if value == "/home" else Path(value)))
                stack.enter_context(patch.object(agent, "_find_windows_home", return_value=windows_home))
                copy = stack.enter_context(patch.object(agent, "_copy_tree"))
                for name in ("ensure_matching_firefox", "relocate_gecko_profiles", "normalise_gecko_profiles", "run_cmd"):
                    stack.enter_context(patch.object(agent, name))
                agent.migrate_user_files({
                    "user": {"preferredLinuxUsername": "linuxuser", "windowsUsername": "WindowsUser"},
                    "files": {"includedFolders": ["Documents"]},
                })
                copy.assert_called_once_with(windows_home / "Documents", home_root / "linuxuser" / "Documents")

    def test_staged_folder_wins_without_probing_an_ntfs_volume(self):
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            home_root = root / "home"
            (home_root / "linuxuser").mkdir(parents=True)
            staged = root / "seed" / "files"
            (staged / "Documents").mkdir(parents=True)
            stack.enter_context(patch.object(agent, "Path", lambda value: home_root if value == "/home" else Path(value)))
            stack.enter_context(patch.object(agent, "_STAGED_FILES_DIR", staged))
            ntfs = stack.enter_context(patch.object(agent, "_find_windows_home"))
            copy = stack.enter_context(patch.object(agent, "_copy_tree"))
            for name in ("ensure_matching_firefox", "relocate_gecko_profiles", "normalise_gecko_profiles"):
                stack.enter_context(patch.object(agent, name))
            commands = stack.enter_context(patch.object(agent, "run_cmd"))
            agent.migrate_user_files({
                "user": {"preferredLinuxUsername": "linuxuser", "windowsUsername": "WindowsUser"},
                "files": {"folders": [{"name": "Documents", "sourceRelativePath": "Other/Documents"}]},
            })
            ntfs.assert_not_called()
            copy.assert_called_once_with(staged / "Documents", home_root / "linuxuser" / "Documents")
            commands.assert_called_once_with(
                ["chown", "-R", "linuxuser:linuxuser", str(home_root / "linuxuser")])

    def test_missing_staged_folder_does_not_fall_back_to_ntfs(self):
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            home_root = root / "home"
            (home_root / "linuxuser").mkdir(parents=True)
            staged = root / "seed" / "files"
            staged.mkdir(parents=True)
            stack.enter_context(patch.object(agent, "Path", lambda value: home_root if value == "/home" else Path(value)))
            stack.enter_context(patch.object(agent, "_STAGED_FILES_DIR", staged))
            ntfs = stack.enter_context(patch.object(agent, "_find_windows_home"))
            with self.assertRaisesRegex(RuntimeError, "Selected folder source"):
                agent.migrate_user_files({
                    "user": {"preferredLinuxUsername": "linuxuser", "windowsUsername": "WindowsUser"},
                    "files": {"folders": [{"name": "Documents", "sourceRelativePath": "Documents"}]},
                })
            ntfs.assert_not_called()

    def test_staged_browser_profile_wins_without_ntfs_probe(self):
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            home_root = root / "home"
            (home_root / "linuxuser").mkdir(parents=True)
            seed = root / "seed"
            staged = seed / "browser-profiles" / "AppData" / "Firefox"
            staged.mkdir(parents=True)
            stack.enter_context(patch.object(agent, "Path", lambda value: home_root if value == "/home" else Path(value)))
            stack.enter_context(patch.object(agent, "_STAGED_SEED_DIR", seed))
            ntfs = stack.enter_context(patch.object(agent, "_find_windows_home"))
            copy = stack.enter_context(patch.object(agent, "_copy_tree"))
            for name in ("ensure_matching_firefox", "relocate_gecko_profiles", "normalise_gecko_profiles", "run_cmd"):
                stack.enter_context(patch.object(agent, name))
            agent.migrate_user_files({
                "user": {"preferredLinuxUsername": "linuxuser", "windowsUsername": "WindowsUser"},
                "browsers": [{"name": "Mozilla Firefox", "engine": "gecko",
                              "sourceRelativePath": "AppData\\Firefox",
                              "profileStagingPath": "browser-profiles/AppData/Firefox",
                              "destRelativePath": ".mozilla/firefox"}],
            })
            ntfs.assert_not_called()
            copy.assert_called_once_with(staged, home_root / "linuxuser" / ".mozilla/firefox")

    def test_staged_browser_path_must_match_selected_source(self):
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            home_root = root / "home"
            (home_root / "linuxuser").mkdir(parents=True)
            seed = root / "seed"
            (seed / "browser-profiles" / "Other").mkdir(parents=True)
            stack.enter_context(patch.object(agent, "Path", lambda value: home_root if value == "/home" else Path(value)))
            stack.enter_context(patch.object(agent, "_STAGED_SEED_DIR", seed))
            ntfs = stack.enter_context(patch.object(agent, "_find_windows_home"))
            with self.assertRaisesRegex(RuntimeError, "does not match"):
                agent.migrate_user_files({
                    "user": {"preferredLinuxUsername": "linuxuser", "windowsUsername": "WindowsUser"},
                    "browsers": [{"name": "Mozilla Firefox", "engine": "gecko",
                                  "sourceRelativePath": "AppData/Firefox",
                                  "profileStagingPath": "browser-profiles/Other",
                                  "destRelativePath": ".mozilla/firefox"}],
                })
            ntfs.assert_not_called()

    def test_browser_profile_missing_from_seed_is_read_from_the_windows_staging_snapshot(self):
        # The Debian family no longer carries user data on the seed. A staged
        # browser profile is read from the FileStagingService generation on the
        # Windows volume (manifest files.stagingPath), never from a guessed path.
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            home_root = root / "home"
            (home_root / "linuxuser").mkdir(parents=True)
            seed = root / "seed"
            seed.mkdir()
            win_home = root / "ntfs" / "Users" / "WindowsUser"
            generation = win_home / "AppData" / "Local" / "Igloo" / "staging" / "debian" / "abc123"
            snapshot = generation / "browser-profiles" / "AppData" / "Firefox"
            snapshot.mkdir(parents=True)
            (generation / "migration-manifest.json").write_text("{}", encoding="utf-8")
            (win_home / "AppData" / "Firefox").mkdir(parents=True)  # live profile, must NOT win
            stack.enter_context(patch.object(agent, "Path", lambda value: home_root if value == "/home" else Path(value)))
            stack.enter_context(patch.object(agent, "_STAGED_SEED_DIR", seed))
            ntfs = stack.enter_context(patch.object(agent, "_find_windows_home", return_value=win_home))
            copy = stack.enter_context(patch.object(agent, "_copy_tree"))
            for name in ("ensure_matching_firefox", "relocate_gecko_profiles", "normalise_gecko_profiles", "run_cmd"):
                stack.enter_context(patch.object(agent, name))
            agent.migrate_user_files({
                "user": {"preferredLinuxUsername": "linuxuser", "windowsUsername": "WindowsUser"},
                "files": {"stagingPath": "C:\\Users\\WindowsUser\\AppData\\Local\\Igloo\\staging\\debian\\abc123"},
                "browsers": [{"name": "Mozilla Firefox", "engine": "gecko",
                              "sourceRelativePath": "AppData/Firefox",
                              "profileStagingPath": "browser-profiles/AppData/Firefox",
                              "destRelativePath": ".mozilla/firefox"}],
            })
            ntfs.assert_called_once_with("WindowsUser")
            copy.assert_called_once_with(snapshot.resolve(), home_root / "linuxuser" / ".mozilla/firefox")

    def test_browser_profile_without_any_snapshot_falls_back_to_the_live_windows_profile(self):
        # Alpha path: nothing staged on the seed, no staging generation on NTFS
        # (or a foreign one) - the live profile under the Windows home is used.
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            home_root = root / "home"
            (home_root / "linuxuser").mkdir(parents=True)
            seed = root / "seed"
            seed.mkdir()
            win_home = root / "ntfs" / "Users" / "WindowsUser"
            live = win_home / "AppData" / "Firefox"
            live.mkdir(parents=True)
            stack.enter_context(patch.object(agent, "Path", lambda value: home_root if value == "/home" else Path(value)))
            stack.enter_context(patch.object(agent, "_STAGED_SEED_DIR", seed))
            stack.enter_context(patch.object(agent, "_find_windows_home", return_value=win_home))
            copy = stack.enter_context(patch.object(agent, "_copy_tree"))
            for name in ("ensure_matching_firefox", "relocate_gecko_profiles", "normalise_gecko_profiles", "run_cmd"):
                stack.enter_context(patch.object(agent, name))
            agent.migrate_user_files({
                "user": {"preferredLinuxUsername": "linuxuser", "windowsUsername": "WindowsUser"},
                "files": {"stagingPath": "C:\\Users\\OtherUser\\AppData\\Local\\Igloo\\staging\\debian\\zzz"},
                "browsers": [{"name": "Mozilla Firefox", "engine": "gecko",
                              "sourceRelativePath": "AppData/Firefox",
                              "profileStagingPath": "browser-profiles/AppData/Firefox",
                              "destRelativePath": ".mozilla/firefox"}],
            })
            copy.assert_called_once_with(live.resolve(), home_root / "linuxuser" / ".mozilla/firefox")

    def test_selected_folder_missing_from_seed_is_read_from_the_windows_staging_snapshot(self):
        with TemporaryDirectory() as temporary, ExitStack() as stack:
            root = Path(temporary)
            home_root = root / "home"
            (home_root / "linuxuser").mkdir(parents=True)
            seed = root / "seed"
            seed.mkdir()
            win_home = root / "ntfs" / "Users" / "WindowsUser"
            generation = win_home / "AppData" / "Local" / "Igloo" / "staging" / "debian" / "abc123"
            (generation / "files" / "Documents").mkdir(parents=True)
            (generation / "migration-manifest.json").write_text("{}", encoding="utf-8")
            (win_home / "Documents").mkdir(parents=True)
            stack.enter_context(patch.object(agent, "Path", lambda value: home_root if value == "/home" else Path(value)))
            stack.enter_context(patch.object(agent, "_STAGED_SEED_DIR", seed))
            stack.enter_context(patch.object(agent, "_STAGED_FILES_DIR", seed / "files"))
            stack.enter_context(patch.object(agent, "_find_windows_home", return_value=win_home))
            copy = stack.enter_context(patch.object(agent, "_copy_tree"))
            for name in ("ensure_matching_firefox", "relocate_gecko_profiles", "normalise_gecko_profiles", "run_cmd"):
                stack.enter_context(patch.object(agent, name))
            agent.migrate_user_files({
                "user": {"preferredLinuxUsername": "linuxuser", "windowsUsername": "WindowsUser"},
                "files": {"stagingPath": "C:\\Users\\WindowsUser\\AppData\\Local\\Igloo\\staging\\debian\\abc123",
                          "folders": [{"name": "Documents", "sourceRelativePath": "Documents"}]},
            })
            copy.assert_called_once_with((generation / "files" / "Documents").resolve(),
                                         home_root / "linuxuser" / "Documents")

    def test_manifest_paths_cannot_escape_the_selected_root(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            for path in ("../escape", "/absolute", "C:/drive", "a\\b"):
                with self.subTest(path=path), self.assertRaises(RuntimeError):
                    agent._checked_child(root, path)

    def test_cleanup_only_unmounts_and_never_deletes_without_exact_identity(self):
        with patch.object(agent.Path, "is_mount", return_value=True), \
                patch.object(agent, "run_cmd") as run:
            agent.cleanup_installer_partitions({})
            run.assert_called_once_with(["umount", "/run/igloo-seed"])

    def test_selected_credentials_cannot_disappear_silently(self):
        with self.assertRaisesRegex(RuntimeError, "no target user"):
            agent.stage_credential_import({"browsers": [{"credentialsBlob": "fixture"}]})

    def test_redaction_write_failure_is_reported(self):
        with TemporaryDirectory() as temporary:
            manifest = Path(temporary) / "manifest.json"
            manifest.write_text(json.dumps({"wifiNetworks": [{"psk": "fixture"}]}), encoding="utf-8")
            with patch.object(agent, "_atomic_bytes", side_effect=OSError("fixture write failure")):
                with self.assertRaises(OSError):
                    agent.redact_manifest({}, manifest)


if __name__ == "__main__":
    unittest.main()
