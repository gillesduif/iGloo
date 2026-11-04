"""Disposable v6 first-login credential probe; never mutates the v6 baseline."""

import base64
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import time


BASE = Path("/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6")
LAB = BASE / "credential-first-login-v1"
SOURCE_DISK = BASE / "selected.raw"
SOURCE_VARS = BASE / "ovmf-vars-installer-v6.fd"
DISK = LAB / "selected.raw"
VARS = LAB / "ovmf-vars.fd"
ROOT = LAB / "root"
REPO = Path(__file__).resolve().parents[3]
AGENT = REPO / "distros/_debian-family/agent/agent.py"
LINUX_USER = "igloolab"
LOGIN_PASSWORD = "igloolab123"  # Synthetic password, only on the cloned disk.
MIGRATED_PASSWORD = "synthetic-migrated-secret"
GIB = 1024 ** 3


def run(*args, **kwargs):
    return subprocess.run(list(args), check=True, **kwargs)


def output(*args):
    return subprocess.check_output(list(args), text=True).strip()


def require_idle():
    if subprocess.run(["pgrep", "-af", "[q]emu-system-x86_64"],
                      capture_output=True).returncode != 1:
        raise RuntimeError("A QEMU guest is already running")
    if output("losetup", "-a"):
        raise RuntimeError("Loop devices are active; coordinate the lab slot first")


def exact_lab_paths():
    if BASE.resolve() != Path(
            "/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6"):
        raise RuntimeError("Unexpected Debian baseline path")
    if LAB.resolve() != BASE.resolve() / "credential-first-login-v1":
        raise RuntimeError("Unexpected clone path")
    for path in (SOURCE_DISK, SOURCE_VARS, AGENT):
        if not path.is_file() or path.is_symlink():
            raise RuntimeError(f"Missing or redirected input: {path}")


def envelope():
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM

    payload = {
        "browser": "Google Chrome",
        "logins": [{"url": "https://example.invalid/igloo-v6-login",
                    "username": "synthetic-igloo-user",
                    "password": MIGRATED_PASSWORD}],
        "cookies": [],
    }
    salt, nonce = os.urandom(16), os.urandom(12)
    key = hashlib.pbkdf2_hmac("sha256", LOGIN_PASSWORD.encode(), salt, 600_000, 32)
    body = json.dumps(payload, separators=(",", ":")).encode()
    return base64.b64encode(b"IGCRD001" + salt + nonce +
                            AESGCM(key).encrypt(nonce, body, None)).decode()


def prepare():
    if os.geteuid() != 0:
        raise RuntimeError("Run prepare as the WSL root user")
    require_idle()
    exact_lab_paths()
    if LAB.exists():
        raise RuntimeError("Clone lab already exists; inspect it instead of overwriting")
    source = SOURCE_DISK.stat()
    allocated = source.st_blocks * 512
    if source.st_size != 30 * GIB or allocated > 18 * GIB:
        raise RuntimeError("The retained v6 disk exceeds this probe's bounded source size")
    if shutil.disk_usage(BASE).free < allocated + 4 * GIB:
        raise RuntimeError("Insufficient free space for a bounded sparse clone")

    LAB.mkdir(mode=0o700)
    run("cp", "--reflink=auto", "--sparse=always", str(SOURCE_DISK), str(DISK))
    run("cp", "--reflink=auto", str(SOURCE_VARS), str(VARS))
    if DISK.stat().st_size != source.st_size or DISK.samefile(SOURCE_DISK):
        raise RuntimeError("The cloned disk is incomplete or aliases the baseline")

    ROOT.mkdir(mode=0o700)
    loop = output("losetup", "--find", "--show", "--partscan", str(DISK))
    try:
        if Path(loop).resolve().as_posix() != loop or output(
                "losetup", "-n", "-O", "BACK-FILE", loop) != str(DISK):
            raise RuntimeError("Loop backing path differs from the cloned disk")
        p3, p4 = loop + "p3", loop + "p4"
        for _ in range(20):
            if Path(p3).exists() and Path(p4).exists():
                break
            time.sleep(0.25)
        if output("blkid", "-s", "PARTUUID", "-o", "value", p3).lower() != \
                "6cfd80d0-dd42-4b42-91d9-f114559685d4" or \
                output("blkid", "-s", "TYPE", "-o", "value", p4) != "ext4":
            raise RuntimeError("The clone is not the verified v6 GPT layout")
        run("mount", "-o", "rw,nodev,nosuid", p4, str(ROOT))
        try:
            installed_agent = ROOT / "opt/igloo/agent.py"
            if not installed_agent.is_file() or not (ROOT / "home" / LINUX_USER).is_dir():
                raise RuntimeError("The verified v6 installation is missing")
            run("install", "-m", "0755", str(AGENT), str(installed_agent))
            run("chpasswd", "-R", str(ROOT),
                input=f"{LINUX_USER}:{LOGIN_PASSWORD}\n", text=True)
            manifest_path = ROOT / "tmp/igloo-credential-v6-manifest.json"
            manifest_path.write_text(json.dumps({
                "user": {"preferredLinuxUsername": LINUX_USER},
                "browsers": [{"name": "Google Chrome", "credentialsBlob": envelope()}],
            }), encoding="utf-8")
            manifest_path.chmod(0o600)
            try:
                script = ("import importlib.util,json; "
                          "s=importlib.util.spec_from_file_location('igloo_agent',"
                          "'/opt/igloo/agent.py'); a=importlib.util.module_from_spec(s); "
                          "s.loader.exec_module(a); "
                          "a.stage_credential_import(json.load(open("
                          "'/tmp/igloo-credential-v6-manifest.json',encoding='utf-8')))" )
                run("chroot", str(ROOT), "/usr/bin/python3", "-c", script)
            finally:
                manifest_path.unlink(missing_ok=True)
            store = ROOT / "home" / LINUX_USER / ".local/share/igloo/credentials.json"
            hook = ROOT / "etc/xdg/autostart/igloo-credential-import.desktop"
            if not store.is_file() or store.stat().st_mode & 0o777 != 0o600 or \
                    not hook.is_file():
                raise RuntimeError("The actual first-login hook was not staged")
            (LAB / "preboot.json").write_text(json.dumps({
                "sourceLogicalBytes": source.st_size,
                "sourceAllocatedBytes": allocated,
                "cloneAllocatedBytes": DISK.stat().st_blocks * 512,
                "agentSha256": hashlib.sha256(AGENT.read_bytes()).hexdigest(),
                "credentialStoreMode": oct(store.stat().st_mode & 0o777),
                "credentialPromptPresent": (ROOT / "usr/bin/zenity").exists() or
                                           (ROOT / "usr/bin/kdialog").exists(),
                "preexistingChromeProfile": (ROOT / "home" / LINUX_USER /
                                             ".config/google-chrome/Default").is_dir(),
            }, indent=2) + "\n", encoding="utf-8")
        finally:
            run("sync")
            run("umount", str(ROOT))
    finally:
        run("losetup", "-d", loop)
    print((LAB / "preboot.json").read_text(), end="")


def launch():
    require_idle()
    exact_lab_paths()
    if not DISK.is_file() or not VARS.is_file() or not (LAB / "preboot.json").is_file():
        raise RuntimeError("Prepare the exact clone before launching")
    args = [
        "qemu-system-x86_64", "-machine", "q35,accel=kvm", "-cpu", "host",
        "-m", "6144", "-smp", "4",
        "-drive", "if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd",
        "-drive", f"if=pflash,format=raw,file={VARS}",
        "-drive", f"if=none,id=selected,format=raw,file={DISK},cache=none",
        "-device", "virtio-blk-pci,drive=selected,bootindex=1",
        "-nic", "none", "-display", "none", "-vnc", "127.0.0.1:5",
        "-monitor", f"unix:{LAB / 'qemu.monitor'},server,nowait",
        "-serial", f"file:{LAB / 'qemu.serial.log'}",
        "-no-reboot", "-pidfile", str(LAB / "qemu.pid"), "-daemonize",
    ]
    (LAB / "qemu-command.json").write_text(json.dumps(args, indent=2) + "\n")
    run(*args)
    time.sleep(1)
    pid = int((LAB / "qemu.pid").read_text())
    run("kill", "-0", str(pid))
    print(f"Cloned Debian first-login guest pid {pid}; original v6 image untouched")


def refresh_agent():
    """Refresh only the disposable clone after a focused agent source repair."""
    if os.geteuid() != 0:
        raise RuntimeError("Run refresh as the WSL root user")
    require_idle()
    exact_lab_paths()
    if not DISK.is_file() or not (LAB / "preboot.json").is_file():
        raise RuntimeError("The exact credential clone is unavailable")
    before = json.loads((LAB / "preboot.json").read_text(encoding="utf-8"))
    loop = output("losetup", "--find", "--show", "--partscan", str(DISK))
    try:
        if output("losetup", "-n", "-O", "BACK-FILE", loop) != str(DISK):
            raise RuntimeError("The loop backing path differs from the credential clone")
        p4 = loop + "p4"
        if output("blkid", "-s", "TYPE", "-o", "value", p4) != "ext4":
            raise RuntimeError("The credential clone has no expected installed root")
        run("mount", "-o", "rw,nodev,nosuid", p4, str(ROOT))
        try:
            installed = ROOT / "opt/igloo/agent.py"
            bootstrap = ROOT / "opt/igloo/igloo-bootstrap.sh"
            hook = ROOT / "etc/xdg/autostart/igloo-credential-import.desktop"
            store = ROOT / "home" / LINUX_USER / ".local/share/igloo/credentials.json"
            if not installed.is_file() or not bootstrap.is_file() or \
                    not hook.is_file() or not store.is_file() or \
                    installed.is_symlink() or bootstrap.is_symlink() or \
                    hook.is_symlink() or store.is_symlink() or \
                    hashlib.sha256(installed.read_bytes()).hexdigest() != before["agentSha256"] or \
                    store.stat().st_mode & 0o777 != 0o600:
                raise RuntimeError("The credential clone changed before agent refresh")
            bootstrap_text = bootstrap.read_text(encoding="utf-8")
            needle = "mkdir -p /opt/igloo /var/lib/igloo /var/log/igloo /run/igloo-seed\n"
            if "chmod 0755 /opt/igloo\n" not in bootstrap_text:
                if bootstrap_text.count(needle) != 1:
                    raise RuntimeError("The cloned bootstrap has an unexpected directory setup")
                bootstrap.write_text(bootstrap_text.replace(
                    needle, needle + "chmod 0755 /opt/igloo\n"), encoding="utf-8")
                bootstrap.chmod(0o755)
                bootstrap_text = bootstrap.read_text(encoding="utf-8")
            module_needle = ("chmod 0755 /opt/igloo/agent.py /opt/igloo/first-boot.sh "
                             "/opt/igloo/display-apply.py /opt/igloo/display-apply-gnome.py\n")
            if "chmod 0644 /opt/igloo/igloo_boot.py\n" not in bootstrap_text:
                if bootstrap_text.count(module_needle) != 1:
                    raise RuntimeError("The cloned bootstrap has an unexpected module setup")
                bootstrap.write_text(bootstrap_text.replace(
                    module_needle, module_needle +
                    "chmod 0644 /opt/igloo/igloo_boot.py\n"), encoding="utf-8")
                bootstrap.chmod(0o755)
            run("install", "-m", "0755", str(AGENT), str(installed))
            script = ("import importlib.util; "
                      "s=importlib.util.spec_from_file_location('igloo_agent',"
                      "'/opt/igloo/agent.py'); a=importlib.util.module_from_spec(s); "
                      "s.loader.exec_module(a); "
                      "a._write_credential_autostart("
                      "a.Path('/etc/xdg/autostart/igloo-credential-import.desktop'))")
            run("chroot", str(ROOT), "/usr/bin/python3", "-c", script)
            if hook.stat().st_mode & 0o777 != 0o644:
                raise RuntimeError("The refreshed XDG hook is not desktop-readable")
            before["agentSha256"] = hashlib.sha256(AGENT.read_bytes()).hexdigest()
            before["autostartMode"] = oct(hook.stat().st_mode & 0o777)
            before["bootstrapDirectoryModeRepair"] = True
            before["bootstrapOptionalModuleModeRepair"] = True
            (LAB / "preboot.json").write_text(json.dumps(before, indent=2) + "\n",
                                              encoding="utf-8")
        finally:
            run("sync")
            run("umount", str(ROOT))
    finally:
        run("losetup", "-d", loop)
    print((LAB / "preboot.json").read_text(), end="")


def inspect():
    if os.geteuid() != 0:
        raise RuntimeError("Run inspect as the WSL root user")
    require_idle()
    exact_lab_paths()
    if not DISK.is_file() or not (LAB / "preboot.json").is_file():
        raise RuntimeError("The exact credential clone is unavailable")
    loop = output("losetup", "--find", "--show", "--partscan", str(DISK))
    try:
        if output("losetup", "-n", "-O", "BACK-FILE", loop) != str(DISK) or \
                output("blkid", "-s", "TYPE", "-o", "value", loop + "p4") != "ext4":
            raise RuntimeError("The loop does not identify the credential clone root")
        run("mount", "-o", "ro,nodev,nosuid", loop + "p4", str(ROOT))
        try:
            paths = ["opt", "opt/igloo", "opt/igloo/agent.py",
                     "opt/igloo/credential-import.sh", "etc/xdg/autostart",
                     "etc/xdg/autostart/igloo-credential-import.desktop",
                     f"home/{LINUX_USER}/.local/share/igloo/credentials.json",
                     f"home/{LINUX_USER}/.local/state/igloo-credentials.log"]
            state = {}
            for relative in paths:
                path = ROOT / relative
                state[relative] = {"exists": path.exists(),
                                   "mode": oct(path.stat().st_mode & 0o777)
                                   if path.exists() else None,
                                   "uid": path.stat().st_uid if path.exists() else None}
            state["optIglooEntries"] = {
                p.name: {"mode": oct(p.stat().st_mode & 0o777),
                         "uid": p.stat().st_uid, "isDirectory": p.is_dir()}
                for p in sorted((ROOT / "opt/igloo").iterdir())}
            credential_store = ROOT / paths[-2]
            marker = ROOT / "home" / LINUX_USER / ".local/share/igloo/autostart-marker"
            state["autostartMarker"] = marker.read_text(encoding="utf-8") \
                if marker.is_file() else None
            hook_output = marker.parent / "credential-hook-output"
            state["hookOutput"] = hook_output.read_text(errors="replace")[-3000:] \
                if hook_output.is_file() else None
            if credential_store.is_file():
                data = json.loads(credential_store.read_text(encoding="utf-8"))
                state["credentialStore"] = {
                    "attempts": data.get("attempts"),
                    "browsers": [e.get("name") for e in data.get("browsers", [])],
                    "sha256": hashlib.sha256(credential_store.read_bytes()).hexdigest(),
                }
            log = ROOT / paths[-1]
            if log.is_file():
                state["credentialLogTail"] = log.read_text(errors="replace")[-3000:]
            login_db = ROOT / "home" / LINUX_USER / ".config/google-chrome/Default/Login Data"
            state["chromeLoginDataExists"] = login_db.is_file()
            if login_db.is_file():
                with closing(sqlite3.connect(f"file:{login_db}?mode=ro", uri=True)) as con:
                    rows = con.execute(
                        "SELECT origin_url, username_value, password_value FROM logins "
                        "WHERE origin_url = ? AND username_value = ?",
                        ("https://example.invalid/igloo-v6-login", "synthetic-igloo-user"),
                    ).fetchall()
                state["selectedLoginRows"] = [
                    {"origin": url, "username": username,
                     "passwordBlobPrefix": bytes(blob)[:3].hex(),
                     "passwordBlobSha256": hashlib.sha256(bytes(blob)).hexdigest()}
                    for url, username, blob in rows
                ]
            (LAB / "postlogin-inspection.json").write_text(json.dumps(state, indent=2) + "\n")
            print(json.dumps(state, indent=2))
        finally:
            run("umount", str(ROOT))
    finally:
        run("losetup", "-d", loop)


def instrument_autostart():
    """Add one harmless marker to the disposable hook to distinguish launch failure."""
    if os.geteuid() != 0:
        raise RuntimeError("Run instrument as the WSL root user")
    require_idle()
    exact_lab_paths()
    loop = output("losetup", "--find", "--show", "--partscan", str(DISK))
    try:
        if output("losetup", "-n", "-O", "BACK-FILE", loop) != str(DISK) or \
                output("blkid", "-s", "TYPE", "-o", "value", loop + "p4") != "ext4":
            raise RuntimeError("The loop does not identify the credential clone root")
        run("mount", "-o", "rw,nodev,nosuid", loop + "p4", str(ROOT))
        try:
            hook = ROOT / "opt/igloo/credential-import.sh"
            marker = ROOT / "home" / LINUX_USER / ".local/share/igloo/autostart-marker"
            original = hook.read_text(encoding="utf-8")
            if hook.is_symlink() or marker.exists() or \
                    not original.startswith("#!/bin/sh\n") or \
                    'STORE="$HOME/.local/share/igloo/credentials.json"' not in original or \
                    "autostart-marker" in original:
                raise RuntimeError("The credential hook is not the expected untouched clone")
            diagnostic = ('printf "entered\\n" > '
                          '"$HOME/.local/share/igloo/autostart-marker"\n')
            hook.write_text(original.replace("#!/bin/sh\n", "#!/bin/sh\n" + diagnostic, 1),
                            encoding="utf-8")
            hook.chmod(0o755)
        finally:
            run("sync")
            run("umount", str(ROOT))
    finally:
        run("losetup", "-d", loop)
    print("Disposable autostart marker added; production hook source unchanged")


def instrument_hook_boundary():
    """Log only hook path resolution and Python stderr in the cloned guest."""
    if os.geteuid() != 0:
        raise RuntimeError("Run diagnose as the WSL root user")
    require_idle()
    exact_lab_paths()
    loop = output("losetup", "--find", "--show", "--partscan", str(DISK))
    try:
        if output("losetup", "-n", "-O", "BACK-FILE", loop) != str(DISK) or \
                output("blkid", "-s", "TYPE", "-o", "value", loop + "p4") != "ext4":
            raise RuntimeError("The loop does not identify the credential clone root")
        run("mount", "-o", "rw,nodev,nosuid", loop + "p4", str(ROOT))
        try:
            hook = ROOT / "opt/igloo/credential-import.sh"
            source = hook.read_text(encoding="utf-8")
            test_line = '[ -f "$STORE" ] || exit 0\n'
            exec_line = "exec python3 /opt/igloo/agent.py --import-credentials\n"
            if hook.is_symlink() or source.count(test_line) != 1 or \
                    source.count(exec_line) != 1 or "credential-hook-output" in source:
                raise RuntimeError("The cloned hook changed before diagnosis")
            replacement = ("printf 'home=%s store=%s\\n' \"$HOME\" \"$STORE\" "
                           '> "$HOME/.local/share/igloo/autostart-marker"\n'
                           'if [ -f "$STORE" ]; then echo visible >> '
                           '"$HOME/.local/share/igloo/autostart-marker"; '
                           'else echo missing >> '
                           '"$HOME/.local/share/igloo/autostart-marker"; exit 0; fi\n')
            source = source.replace(test_line, replacement, 1).replace(
                exec_line, exec_line.rstrip("\n") +
                ' >> "$HOME/.local/share/igloo/credential-hook-output" 2>&1\n', 1)
            hook.write_text(source, encoding="utf-8")
            hook.chmod(0o755)
        finally:
            run("sync")
            run("umount", str(ROOT))
    finally:
        run("losetup", "-d", loop)
    print("Disposable hook boundary diagnostic added")


if __name__ == "__main__":
    if sys.argv[1:] == ["prepare"]:
        prepare()
    elif sys.argv[1:] == ["launch"]:
        launch()
    elif sys.argv[1:] == ["refresh"]:
        refresh_agent()
    elif sys.argv[1:] == ["inspect"]:
        inspect()
    elif sys.argv[1:] == ["instrument"]:
        instrument_autostart()
    elif sys.argv[1:] == ["diagnose"]:
        instrument_hook_boundary()
    else:
        raise SystemExit("Use prepare, refresh, launch, inspect, instrument, or diagnose")
