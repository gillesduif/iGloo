"""Preserve a powered-off import checkpoint before a separately authorized successor.

No format/import/recovery. Same target/journal inode continues after byte-verified
checkpoint copies; runtime is a new copy. Old chains and all failed runs are retained.
"""
import json
import os
from pathlib import Path
import stat
import subprocess

from canonical_storage_lab import sha
from canonical_lab_runtime import checkpoint
from canonical_import_readback import verify


def preserve(previous, destination):
    previous, destination = Path(previous), Path(destination)
    shut = json.loads((previous / 'shutdown-host-readback.json').read_bytes())
    def require(value):
        if not value: raise ValueError('CheckpointBindingRejected')
    require(shut['ProcessAbsent'] and shut['ConsolePowerDown'])
    pid = json.loads((previous / 'process.json').read_bytes())['Pid']
    require(not Path(f'/proc/{pid}').exists())
    result = verify(previous / 'guest-evidence/provisioning', previous / 'guest-evidence/journals')
    require(result['ImportResult']['Sha256'] == 'A18E09649CD6E97EE8257EBF46F74CD0FFBF3A19E6EB49EDEF358133B2CEDDE9' and
            result['CloseResult']['Sha256'] == 'F102D92819EC0D42C6DEA88C475583E2707C2B65214BA610B2FFC1AF6164817A')
    for name, binding in shut['RetainedBackings'].items():
        path = previous / name; info = path.lstat()
        require(stat.S_ISREG(info.st_mode) and path.resolve() == path and
            (info.st_dev, info.st_ino, info.st_size) == (binding['Device'], binding['Inode'], binding['Length']))
        for process in Path('/proc').iterdir():
            if not process.name.isdigit(): continue
            try:
                for fd in (process / 'fd').iterdir():
                    current = fd.stat()
                    require((current.st_dev, current.st_ino) != (info.st_dev, info.st_ino))
            except (FileNotFoundError, ProcessLookupError): pass
        require(sha(path) == binding['Sha256'])
    destination.mkdir(mode=0o700)
    checkpoint(destination, 'intent.json', {'Predecessor': str(previous), 'Backings': shut['RetainedBackings'],
        'ImportResult': result['ImportResult'], 'CloseResult': result['CloseResult'], 'Purpose': 'EvidencePreservationNotRollback'})
    for name, binding in shut['RetainedBackings'].items():
        source, target = previous / name, destination / name
        subprocess.run(['cp', '--sparse=always', '--reflink=auto', '--no-clobber', str(source), str(target)], check=True)
        target.chmod(0o400)
        with target.open('rb') as stream: os.fsync(stream.fileno())
        require(sha(source) == binding['Sha256'] == sha(target))
    checkpoint(destination, 'result.json', {'Backings': {n: {'Sha256': sha(destination/n), 'Length': (destination/n).stat().st_size}
        for n in shut['RetainedBackings']}, 'IntentSha256': sha(destination/'intent.json'), 'ImportResult': result['ImportResult'],
        'CloseResult': result['CloseResult'], 'Outcome': 'AppliedAndVerified', 'RollbackAuthority': False})
    return destination
