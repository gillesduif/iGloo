"""Read-only first-boot diagnosis of the exact disposable v7 target."""

from pathlib import Path
import json
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
DISK = LAB / 'selected.raw'
ROOT = LAB / 'mnt-installed-root-ro'
assert DISK.is_file() and DISK.stat().st_size == 30 << 30
assert not subprocess.check_output(['losetup', '-a']).strip()
assert subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode == 1
ROOT.mkdir(exist_ok=True)


def output(*args):
    return subprocess.check_output(args, text=True).strip()


loop = output('losetup', '--find', '--show', '--partscan', '--read-only', str(DISK))
try:
    assert Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() == DISK.resolve()
    for _ in range(20):
        if Path(loop + 'p4').exists() and output('lsblk', '-dnro', 'PARTUUID', loop + 'p4'):
            break
        time.sleep(.1)
    assert output('lsblk', '-dnro', 'PTUUID', loop).lower() == json.loads(
        (LAB / 'base-identities.json').read_text())['selectedDiskGuid']
    subprocess.run(['mount', '-o', 'ro,noload,nodev,nosuid', loop + 'p4', str(ROOT)], check=True)
    try:
        state = ROOT / 'var/lib/igloo'
        logdir = ROOT / 'var/log/igloo'
        result = {
            'durableMarkers': sorted(path.name for path in state.iterdir()) if state.is_dir() else [],
            'logNames': sorted(path.name for path in logdir.iterdir()) if logdir.is_dir() else [],
            'selectedDocuments': (ROOT / 'home/igloolab/Documents/igloo-debian-v7.txt').is_file(),
            'selectedFirefox': (ROOT / 'home/igloolab/.mozilla/firefox/igloolab.default/igloo-profile-marker.txt').is_file(),
            'selectedChrome': (ROOT / 'home/igloolab/.config/google-chrome/Default/Bookmarks').is_file(),
            'credentialStore': (ROOT / 'home/igloolab/.local/share/igloo/credentials.json').is_file(),
            'installedManifest': (state / 'manifest.json').is_file(),
            'nativeTreeBinary': (ROOT / 'usr/bin/tree').is_file(),
            'flatpakChromeRef': (ROOT / 'var/lib/flatpak/app/com.google.Chrome').exists(),
        }
        home = ROOT / 'home/igloolab'
        result['observedMigrationFiles'] = sorted(str(path.relative_to(home)) for path in home.rglob('*')
            if path.is_file() and (path.name.startswith('igloo-') or path.name in
                                   ('Bookmarks', 'Session_123', 'prefs.js', 'profiles.ini')))
        for name in ('bootstrap.log', 'agent.log', 'first-boot.log'):
            path = logdir / name
            if path.is_file():
                lines = path.read_text(errors='replace').splitlines()
                # Tail only process state. Never print manifest or credential content.
                result[name] = [line for line in lines[-160:] if any(
                    word in line.lower() for word in
                    ('error', 'fail', 'warning', 'starting', 'finished', 'success', 'step', 'fatal', 'traceback', 'done'))][-45:]
                if name == 'bootstrap.log':
                    result['bootstrapRebootLines'] = [line for line in lines[-45:] if
                        'reboot' in line.lower() or 'exit' in line.lower()][-12:]
        (LAB / 'firstboot-diagnostic-v7.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result, indent=2))
    finally:
        subprocess.run(['umount', str(ROOT)], check=True)
finally:
    subprocess.run(['losetup', '--detach', loop], check=True)
