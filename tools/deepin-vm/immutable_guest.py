"""Guest-only extraction/mount experiment for the disposable QEMU fixture.

Not a distro installation driver. No ESP/EFI installation or migration work.
Only immutable_probe.py supplies this program to its private VM console.
"""

import configparser
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid

import igloo_target


def run(*args):
    print("RUN", *args, flush=True)
    subprocess.run(args, check=True)


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def commit(repo, ref):
    value = output("ostree", f"--repo={repo}", "rev-parse", ref)
    if not re.fullmatch("[0-9a-f]{64}", value):
        raise RuntimeError("Unexpected OSTree commit identity")
    return value


def verify_native_handoff(text):
    """Catch missing ISO OEM overrides before producing a bootable fixture."""
    config = configparser.ConfigParser(interpolation=None, strict=True)
    config.read_string(text)
    expected = {
        "DI_INSTALL_MODE": "default",
        "DI_APT_SOURCE_DEB": "deb https://community-packages.deepin.com/beige/ crimson main commercial community",
        "DI_APT_SOURCE_DEB_SRC": "#deb-src https://community-packages.deepin.com/beige/ crimson main commercial community",
    }
    for key, value in expected.items():
        actual = config.get("General", key, fallback=None)
        if actual is not None and actual.startswith('"') and actual.endswith('"'):
            actual = actual[1:-1]
        if actual != value:
            raise RuntimeError(f"Pinned ISO handoff setting missing or changed: {key}")


def verify_mounts(table, root_device_id, base, extension):
    mounts = {}

    def visit(rows):
        if not isinstance(rows, list):
            raise RuntimeError("Malformed mount inventory")
        for row in rows:
            if not isinstance(row, dict) or not isinstance(row.get("target"), str):
                raise RuntimeError("Malformed mount entry")
            if row["target"] in mounts:
                raise RuntimeError("Stacked target mounts are not permitted")
            mounts[row["target"]] = row
            visit(row.get("children", []))

    visit(table.get("filesystems"))
    expected = {"/target": ("/", "rw"),
                "/target/var": ("/persistent/ostree/deploy/deepin/var", "rw"),
                "/target/ostree": ("/ostree", "ro"),
                "/target/persistent/ostree": ("/persistent/ostree", "ro"),
                "/target/sysroot": ("/sysroot", "rw"),
                "/target/sysroot/ostree": ("/ostree", "ro")}
    if set(mounts) != set(expected) | {"/target/usr", "/target/etc", "/target/opt"}:
        raise RuntimeError("Missing or unexpected target mounts")
    for path, (fsroot, permission) in expected.items():
        row = mounts[path]
        options = row.get("options", "").split(",")
        if (row.get("fstype") != "ext4" or row.get("maj:min") != root_device_id
                or row.get("fsroot") != fsroot or {"ro", "rw"}.intersection(options) != {permission}):
            raise RuntimeError(f"Unexpected root-backed mount: {path}")
    for name, permission in (("usr", "ro"), ("etc", "rw"), ("opt", "rw")):
        row = mounts[f"/target/{name}"]
        options = row.get("options", "").split(",")
        required = {permission,
            f"lowerdir=/target/persistent/ostree/data/{extension}.0/checkout/{name}:"
            f"/target/ostree/deploy/deepin/deploy/{base}.0/{name}",
            f"upperdir=/target/persistent/overlay/data/{extension}.0/{name}-upper",
            f"workdir=/target/persistent/overlay/data/{extension}.0/{name}-work"}
        if (row.get("fstype") != "overlay" or not required.issubset(options)
                or {"ro", "rw"}.intersection(options) != {permission}):
            raise RuntimeError(f"Unexpected immutable overlay: {name}")


def deploy_experiment(root_uuid, extension, fstab):
    # These are the root-only operations observed in the shipped OSTree hooks.
    # No whole installer hook library, parted utility, ESP or grub-install call.
    # This ISO's util-linux fsconfig remount fails on the overlay. Its own mount
    # helper explicitly selects mount(2); use that same API for target remounts.
    run("env", "LIBMOUNT_FORCE_MOUNT2=always", "mount", "-o", "remount,rw", "/target/usr", "/target/usr")
    run("env", "LIBMOUNT_FORCE_MOUNT2=always", "mount", "-o", "remount,bind,rw",
        "/target/persistent/ostree", "/target/persistent/ostree")
    seed = "/target/igloo-var-seed"
    run("unsquashfs", "-no-progress", "-d", seed,
        "/run/live/medium/live/filesystem-extra.squashfs", "var/lib")
    Path("/target/var/lib").mkdir(parents=True, exist_ok=True)
    run("rsync", "-aHAX", "--numeric-ids", f"{seed}/var/lib/", "/target/var/lib/")
    run("rsync", "-aHAX", "--numeric-ids", "--exclude=/run", "--exclude=/lock",
        "--exclude=/lib/dpkg", "/var/", "/target/var/")
    Path("/target/var/usrlocal").mkdir(parents=True, exist_ok=True)
    run("rsync", "-aHAX", "--numeric-ids", "/usr/local/", "/target/var/usrlocal/")
    for pattern in ("config-*", "initrd.img-*", "vmlinuz-*", "System.map-*"):
        sources = list(Path("/boot").glob(pattern))
        if not sources:
            raise RuntimeError(f"Missing boot resource: {pattern}")
        for source in sources:
            shutil.copy2(source, Path("/target/boot", source.name))
    Path("/target/etc/fstab").write_text(fstab)
    Path("/target/etc/hostname").write_text("igloo-deepin-vm\n")
    Path("/target/etc/default/grub").write_text(
        'GRUB_DEFAULT=0\nGRUB_TIMEOUT=5\nGRUB_DISTRIBUTOR=deepin\n'
        'GRUB_DISABLE_OS_PROBER=true\n'
        'GRUB_CMDLINE_LINUX_DEFAULT="console=ttyS0,115200 systemd.gpt_auto=0"\n')
    # Generate the shipped handoff while the verified ISO's OEM settings remain
    # available. Generating it after reboot silently loses Deepin's overrides
    # (including its package repositories) and leaves upstream UOS defaults.
    Path("/target/etc/deepin-installer").mkdir(parents=True, exist_ok=True)
    run("bash", "-e", "-c",
        "source /usr/share/deepin-installer/tools/scripts/init_environment.sh; "
        "exec deepin-installer-config init /target/etc/deepin-installer/deepin-installer.conf")
    if not Path("/target/etc/deepin-installer/deepin-installer.conf").is_file():
        raise RuntimeError("The shipped config generator did not produce its handoff")
    verify_native_handoff(Path("/target/etc/deepin-installer/deepin-installer.conf").read_text())
    run("mount", "--rbind", "/dev", "/target/dev")
    run("mount", "--make-rslave", "/target/dev")
    run("mount", "-t", "proc", "proc", "/target/proc")
    run("mount", "--rbind", "/sys", "/target/sys")
    run("mount", "--make-rslave", "/target/sys")
    run("mount", "-t", "tmpfs", "tmpfs", "/target/run")
    Path("/target/run/ostree-booted").write_bytes(b"sysroot-ro\0\0\0\0\0\0\1\0\x62\x0b\x14")
    run("chroot", "/target", "systemctl", "disable", "deepin-installer.service", "deepin-installer-extra.service")
    run("chroot", "/target", "systemctl", "enable", "deepin-installer-first-boot.service")
    run("chroot", "/target", "/usr/sbin/update-initramfs", "-k", "all", "-u")
    # The shipped deploy_persistent helper removes this legacy generator before
    # invoking immutable-ctl. Preserve it for inspection instead of deleting it.
    legacy_grub = Path("/target/etc/grub.d/15_ostree")
    if legacy_grub.exists():
        legacy_grub.rename("/target/igloo-var-seed/15_ostree.disabled")
    Path("/target/persistent/ostree/data/status.list").write_text(extension + ".0\n")
    run("chroot", "/target", "env", "LANGUAGE=en_US.UTF-8", f"GRUB_DEVICE=UUID={root_uuid}",
        "DDE_DEBUG_LEVEL=debug", "deepin-immutable-ctl", "admin", "deploy", "-v")
    run("chroot", "/target", "deepin-immutable-ctl", "admin", "status", "--json")
    print("IGLOO_IMMUTABLE_DEPLOY_EXPERIMENT_PASSED", flush=True)


def main():
    expected = sys.argv[1]
    if sys.argv[2:] not in ([], ["--deploy"]):
        raise RuntimeError("Unsupported experiment arguments")
    deploy = sys.argv[2:] == ["--deploy"]
    manifest = igloo_target.read_claim("/run/fixture-manifest.json", expected)
    layouts = igloo_target.collect_layouts(verified_optical_media={
        "f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450": 6976131072})
    resolved = igloo_target.resolve(manifest, layouts, expected)
    # Additional fixture-only fence; disk and partition selection still use GPT.
    if output("lsblk", "-dn", "-o", "SERIAL", resolved["diskDevice"]) != expected[:20]:
        raise RuntimeError("Not the disposable VM fixture")
    if Path("/proc/1/comm").read_text().strip() != "bash":
        raise RuntimeError("Expected the isolated live-console experiment")
    root = resolved["rootDevice"]
    holders = Path("/sys/class/block") / Path(root).name / "holders"
    if list(holders.iterdir()):
        raise RuntimeError("Fixture root has holders")
    if output("lsblk", "-dn", "-o", "MOUNTPOINTS", root):
        raise RuntimeError("Fixture root is mounted")
    signatures = json.loads(output("wipefs", "--no-act", "--json", root))
    if signatures.get("signatures") != []:
        raise RuntimeError("Fixture root is not empty; no rerun formatting")
    # This program runs only in a VM with a newly generated ordinary disk image.
    # It is deliberately not packaged or callable through DeepinPlugin.
    run("mkfs.ext4", "-q", "-E", "nodiscard,lazy_itable_init=0,lazy_journal_init=0", root)
    Path("/target").mkdir(exist_ok=True)
    run("mount", root, "/target")
    root_uuid = output("blkid", "-s", "UUID", "-o", "value", root)
    if str(uuid.UUID(root_uuid)) != root_uuid:
        raise RuntimeError("Invalid newly created filesystem UUID")
    esp_guid = manifest["installationTarget"]["espPartitionGuid"]
    if not isinstance(esp_guid, str) or str(uuid.UUID(esp_guid)) != esp_guid:
        raise RuntimeError("The experiment requires an explicit canonical ESP identity")
    fstab = (f"UUID={root_uuid} / ext4 defaults 0 1\n"
             f"PARTUUID={esp_guid} /boot/efi vfat noauto,ro,umask=0077 0 0\n")
    # The deployment-only fixture has a blank ESP. Record its identity without
    # mounting it; automatic GPT discovery must not choose a different ESP.
    Path("/target/boot/efi").mkdir(parents=True, exist_ok=True)
    # The shipped installer copies these into the bare root for initrd discovery,
    # separately from the /etc overlay configured after immutable mounting.
    Path("/target/etc").mkdir(exist_ok=True)
    Path("/target/etc/fstab").write_text(fstab)
    shutil.copy2("/etc/lsb-release", "/target/etc/lsb-release")
    medium = "/run/live/medium/live"
    run("unsquashfs", "-no-progress", "-d", "/target", f"{medium}/filesystem.squashfs", "ostree")
    base_repo = "/target/ostree/repo"
    base = commit(base_repo, "beige/develop/25.35/base")
    destination = f"/target/ostree/deploy/deepin/deploy/{base}.0"
    Path(destination).parent.mkdir(parents=True, exist_ok=True)
    run("ostree", f"--repo={base_repo}", "checkout", base, destination)
    run("unsquashfs", "-no-progress", "-d", "/target", f"{medium}/filesystem-extra.squashfs", "persistent/ostree")
    extension_repo = "/target/persistent/ostree/repo"
    extension = commit(extension_repo, "deb-ostree/main")
    checkout = f"/target/persistent/ostree/data/{extension}.0/checkout"
    Path(checkout).parent.mkdir(parents=True, exist_ok=True)
    run("ostree", f"--repo={extension_repo}", "checkout", "--process-passthrough-whiteouts", extension, checkout)
    parent = Path(checkout, "usr/share/deepin-immutable-ctl/state/ostree-parent").read_text().strip()
    if parent != base + ".0":
        raise RuntimeError(f"Extension parent mismatch: {parent!r} vs {base + '.0'!r}")
    Path("/target/persistent/ostree/deploy/deepin/var").mkdir(parents=True, exist_ok=True)
    os.environ["MOUNT_ROOT_HOOK_ENABLED"] = "0"
    run("/bin/sh", "-e", "/usr/bin/deepin-immutable-mount-root", "/target",
        f"--ostree=/ostree/data/{extension}.0/checkout", "--persistent=")
    run("findmnt", "-R", "/target")
    table = json.loads(output("findmnt", "-J", "-R", "/target", "-o", "TARGET,FSTYPE,OPTIONS,FSROOT,MAJ:MIN"))
    device_id = os.stat(root).st_rdev
    verify_mounts(table, f"{os.major(device_id)}:{os.minor(device_id)}", base, extension)
    if deploy:
        deploy_experiment(root_uuid, extension, fstab)
    # Inventory comparison still applies after the filesystem operation.
    after = igloo_target.collect_layouts(verified_optical_media={
        "f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450": 6976131072})
    igloo_target.resolve(manifest, after, expected)
    print("IGLOO_IMMUTABLE_MOUNT_EXPERIMENT_PASSED", flush=True)
    run("sync")


if __name__ == "__main__":
    main()
