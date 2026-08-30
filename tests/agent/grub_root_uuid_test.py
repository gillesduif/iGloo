#!/usr/bin/env python3
"""Ground truth for the root= argument in the generated grub.cfg.

desktop-living, 2026-08-23: after the Mint install, Fedora stopped booting and
sat forever on "Job dev-nvme1n1p6.device/start running (7min 25s / no limit)".

Every file inside Fedora named its root by UUID, which is why four rounds of
looking there found nothing. The bad line was in Mint's grub.cfg:

    linux /vmlinuz-6.19.10-300.fc44.x86_64 root=/dev/nvme1n1p6

os-prober cannot read a BLS Fedora - the cmdline lives in
/boot/loader/entries/*.conf, not in its grub.cfg - so it fell back to the kernel
name of the partition it had just mounted. That name is assigned in probe order.
By the next boot the two NVMe controllers had swapped: Fedora's root was
/dev/nvme0n1p6, and /dev/nvme1n1p6 did not exist at all - that disk holds two
NTFS partitions. systemd waited for a device that was never coming. The Debian
entry on the same menu carries root=UUID= and kept booting throughout.

These tests run the real hook script against the real line.
"""
import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import types
from pathlib import Path

MOD_PATH = Path(__file__).resolve().parents[2] / "distros/_shared/agent/igloo_boot.py"
spec = importlib.util.spec_from_file_location("igloo_boot", MOD_PATH)
ib = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = ib
spec.loader.exec_module(ib)

FEDORA_UUID = "ae34dd01-54a8-4f0d-b26d-6d0b4a081b7d"
DEBIAN_UUID = "613d4cd4-5531-4d8b-84b2-b4e0d4f80228"

# Verbatim from //boot/grub/grub.cfg on desktop-living, lines 192-231.
GRUB_CFG = f"""menuentry 'Windows 11' {{
	search --no-floppy --fs-uuid --set=root 0AA6-9588
	chainloader /EFI/Microsoft/Boot/bootmgfw.efi
}}
menuentry 'Fedora Linux 44 (Forty Four)' {{
	search --no-floppy --fs-uuid --set=root f6ff30df-e35b-4f62-97d4-c519d5c212d5
	linux /vmlinuz-6.19.10-300.fc44.x86_64 root=/dev/nvme1n1p6
	initrd /initramfs-6.19.10-300.fc44.x86_64.img
}}
menuentry 'Fedora Linux 44 (Forty Four) (rescue)' {{
		linux /vmlinuz-0-rescue-e0dbbef2b03648d3a3758ea15b5a8035 root=/dev/nvme1n1p6 ro
}}
menuentry 'Debian GNU/Linux 13' {{
	search --no-floppy --fs-uuid --set=root {DEBIAN_UUID}
	linux /boot/vmlinuz-6.12.94+deb13-amd64 root=UUID={DEBIAN_UUID} ro quiet
}}
"""

failures: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        print(f"  PASS  {name}")
    else:
        failures.append(name)
        print(f"  FAIL  {name}  {detail}")


def run_hook(tmp: Path, cfg_text: str, uuids: dict[str, str]) -> tuple[str, int]:
    """Run the generated hook over cfg_text with blkid stubbed to uuids."""
    cfg = tmp / "grub.cfg"
    cfg.write_text(cfg_text, encoding="utf-8", newline="\n")

    hook = tmp / "hook.sh"
    hook.write_text(ib._GRUB_HOOK_TEMPLATE.format(cfg=cfg.as_posix()),
                    encoding="utf-8", newline="\n")

    # blkid is not on a Windows dev box, and must not be the real one on Linux.
    stub = tmp / "bin"
    stub.mkdir()
    cases = "".join(f'        {dev}) echo {uuid} ;;\n' for dev, uuid in uuids.items())
    (stub / "blkid").write_text(
        "#!/bin/sh\n"
        "for a in \"$@\"; do dev=$a; done\n"
        "case \"$dev\" in\n"
        f"{cases}"
        "        *) exit 2 ;;\n"
        "esac\n", encoding="utf-8", newline="\n")
    # Owner-only: sh execs the stub as this user, so group/world rx buys nothing.
    os.chmod(stub / "blkid", 0o700)

    env = dict(os.environ, PATH=str(stub) + os.pathsep + os.environ.get("PATH", ""))
    res = subprocess.run(["sh", str(hook)], env=env, capture_output=True, text=True)
    return cfg.read_text(encoding="utf-8"), res.returncode


def test_the_reported_line(tmp: Path) -> None:
    print("the line that hung desktop-living")
    out, rc = run_hook(tmp, GRUB_CFG, {"/dev/nvme1n1p6": FEDORA_UUID})
    check("the hook exits clean", rc == 0, f"rc={rc}")
    check("no entry boots a kernel name any more",
          "root=/dev/" not in out, out)
    check("the Fedora entry names Fedora's root UUID",
          f"root=UUID={FEDORA_UUID}\n" in out, out)
    check("the rescue entry keeps the arguments that followed it",
          f"root=UUID={FEDORA_UUID} ro\n" in out, out)
    check("the Debian entry is untouched",
          f"root=UUID={DEBIAN_UUID} ro quiet" in out, out)
    check("nothing else in the file moved",
          out.count("menuentry") == 4 and "bootmgfw.efi" in out, out)


def test_unresolvable_device_is_left_alone(tmp: Path) -> None:
    print("the device is already gone, as it was by the time we looked")
    out, rc = run_hook(tmp, GRUB_CFG, {})
    check("the hook still exits clean", rc == 0, f"rc={rc}")
    check("no UUID is invented", "root=UUID=ae34" not in out, out)
    check("the entry is left exactly as it was",
          "root=/dev/nvme1n1p6\n" in out, out)


def test_longer_name_is_not_eaten_by_a_shorter_one(tmp: Path) -> None:
    print("/dev/sda1 must not rewrite the front of /dev/sda11")
    cfg = ("menuentry a {\n\tlinux /vmlinuz root=/dev/sda1 ro\n}\n"
           "menuentry b {\n\tlinux /vmlinuz root=/dev/sda11 ro\n}\n")
    out, rc = run_hook(tmp, cfg, {"/dev/sda1": "1111-1111", "/dev/sda11": "9999-9999"})
    check("the hook exits clean", rc == 0, f"rc={rc}")
    check("sda1 gets its own UUID", "root=UUID=1111-1111 ro" in out, out)
    check("sda11 gets its own UUID", "root=UUID=9999-9999 ro" in out, out)
    check("neither line is corrupted", "UUID=1111-11111" not in out, out)


def test_the_rewrite_runs_before_the_single_disk_guard() -> None:
    print("a second disk must not disable the fix")
    script = ib._GRUB_HOOK_TEMPLATE.format(cfg="/boot/grub/grub.cfg")
    root_at = script.index("root=/dev/")
    guard_at = script.index("lsblk -dno NAME")
    # The hint half exits early on a multi-disk machine - which is exactly the
    # machine where a kernel-name root= is dangerous.
    check("root= is rewritten first", root_at < guard_at,
          f"root at {root_at}, guard at {guard_at}")


def test_the_fix_survives_a_grub_package_upgrade(tmp: Path) -> None:
    print("update-grub outside a kernel update must not undo it")
    saved = ib._FIXUP_UNIT
    ib._FIXUP_UNIT = tmp / "igloo-grub-fixups.service"
    b = types.SimpleNamespace(
        grub_cfg=tmp / "boot" / "grub" / "grub.cfg",
        grub_hook=tmp / "hook",
        commands=[],
        logger=types.SimpleNamespace(info=lambda *a, **k: None,
                                     exception=lambda *a, **k: None))
    b.run_cmd = lambda cmd, **kw: (b.commands.append(list(cmd)),
                                   types.SimpleNamespace(returncode=0))[1]
    try:
        ib._install_fixup_unit(b)
        unit = ib._FIXUP_UNIT.read_text(encoding="utf-8")
    finally:
        ib._FIXUP_UNIT = saved

    check("the unit runs the hook", f"ExecStart={b.grub_hook.as_posix()}" in unit, unit)
    check("on every boot", "WantedBy=multi-user.target" in unit, unit)
    check("and it was enabled",
          ["systemctl", "enable", "igloo-grub-fixups.service"] in b.commands,
          str(b.commands))


if __name__ == "__main__":
    if not shutil.which("sh"):
        print("FAIL  no POSIX sh to run the hook with - this test proves nothing")
        sys.exit(1)
    for test in (test_the_reported_line, test_unresolvable_device_is_left_alone,
                 test_longer_name_is_not_eaten_by_a_shorter_one,
                 test_the_fix_survives_a_grub_package_upgrade):
        with tempfile.TemporaryDirectory(prefix="grub-root-test-") as d:
            test(Path(d))
    test_the_rewrite_runs_before_the_single_disk_guard()
    print()
    if failures:
        print(f"{len(failures)} FAILED: {', '.join(failures)}")
        sys.exit(1)
    print("all checks passed")
