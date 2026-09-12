"""Immutable mount postconditions, using synthetic findmnt JSON only."""

import copy
import importlib.util
from pathlib import Path
import unittest
from unittest import mock


REPO = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("immutable_guest", REPO / "tools/deepin-vm/immutable_guest.py")
guest = importlib.util.module_from_spec(SPEC)
with mock.patch.dict("sys.modules", {"igloo_target": mock.Mock()}):
    SPEC.loader.exec_module(guest)
BASE, EXTENSION = "a" * 64, "b" * 64


def inventory():
    rows = []
    for path, fsroot, permission in (
            ("/target", "/", "rw"),
            ("/target/var", "/persistent/ostree/deploy/deepin/var", "rw"),
            ("/target/ostree", "/ostree", "ro"),
            ("/target/persistent/ostree", "/persistent/ostree", "ro"),
            ("/target/sysroot", "/sysroot", "rw"),
            ("/target/sysroot/ostree", "/ostree", "ro")):
        rows.append({"target": path, "fsroot": fsroot, "options": permission,
                     "fstype": "ext4", "maj:min": "253:7"})
    for name, permission in (("usr", "ro"), ("etc", "rw"), ("opt", "rw")):
        rows.append({"target": f"/target/{name}", "fstype": "overlay", "options": ",".join([
            permission,
            f"lowerdir=/target/persistent/ostree/data/{EXTENSION}.0/checkout/{name}:"
            f"/target/ostree/deploy/deepin/deploy/{BASE}.0/{name}",
            f"upperdir=/target/persistent/overlay/data/{EXTENSION}.0/{name}-upper",
            f"workdir=/target/persistent/overlay/data/{EXTENSION}.0/{name}-work"])})
    return {"filesystems": rows}


class MountTests(unittest.TestCase):
    def verify(self, table):
        guest.verify_mounts(table, "253:7", BASE, EXTENSION)

    def test_exact_mounts_and_nested_json(self):
        table = inventory()
        self.verify(table)
        root, *children = table["filesystems"]
        root["children"] = list(reversed(children))
        self.verify({"filesystems": [root]})

    def test_wrong_backing_device_or_filesystem_root_fails(self):
        for key, value in (("maj:min", "253:3"), ("fsroot", "/another-root"),
                           ("fstype", "vfat"), ("options", "ro")):
            table = inventory()
            table["filesystems"][0][key] = value
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                self.verify(table)

    def test_missing_duplicate_or_unexpected_mount_fails(self):
        for action in ("remove", "duplicate", "unexpected"):
            table = inventory()
            if action == "remove":
                table["filesystems"].pop()
            elif action == "duplicate":
                table["filesystems"].append(copy.deepcopy(table["filesystems"][0]))
            else:
                table["filesystems"].append({"target": "/target/boot/efi"})
            with self.subTest(action=action), self.assertRaises(RuntimeError):
                self.verify(table)

    def test_writable_usr_reversed_layers_and_outside_upper_fail(self):
        extension = f"/target/persistent/ostree/data/{EXTENSION}.0/checkout/usr"
        base = f"/target/ostree/deploy/deepin/deploy/{BASE}.0/usr"
        for change in (lambda text: text.replace("ro,", "rw,"),
                       lambda text: text + ",rw",
                       lambda text: text.replace(f"{extension}:{base}", f"{base}:{extension}"),
                       lambda text: text.replace("lowerdir=", "ignored-lowerdir="),
                       lambda text: text.replace("upperdir=/target/", "upperdir=/outside/"),
                       lambda text: text.replace(EXTENSION, "c" * 64)):
            table = inventory()
            row = next(row for row in table["filesystems"] if row["target"] == "/target/usr")
            row["options"] = change(row["options"])
            with self.assertRaises(RuntimeError):
                self.verify(table)


class HandoffTests(unittest.TestCase):
    CONFIG = ('[General]\nDI_INSTALL_MODE=default\n'
              'DI_APT_SOURCE_DEB="deb https://community-packages.deepin.com/beige/ crimson main commercial community"\n'
              'DI_APT_SOURCE_DEB_SRC="#deb-src https://community-packages.deepin.com/beige/ crimson main commercial community"\n')

    def test_exact_iso_overrides(self):
        guest.verify_native_handoff(self.CONFIG)

    def test_missing_or_wrong_repository_and_mode_fail(self):
        for config in ("[General]\n", self.CONFIG.replace("crimson", "eagle"),
                       self.CONFIG.replace("default", "no-first-boot"),
                       self.CONFIG.replace("community-packages.deepin.com", "example.invalid")):
            with self.subTest(config=config), self.assertRaises(RuntimeError):
                guest.verify_native_handoff(config)

    def test_malformed_or_duplicate_config_fails(self):
        for config in ("not an ini file", self.CONFIG + "DI_INSTALL_MODE=default\n"):
            with self.subTest(config=config), self.assertRaises(guest.configparser.Error):
                guest.verify_native_handoff(config)


if __name__ == "__main__":
    unittest.main()
