"""One bounded native offline-agent acceptance invocation on a fresh WSL fixture.

No canonical ownership, image mounts, service manager or recovery operations.
The fixed external budget is durably consumed before calling the installer.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import uuid

from agent_offline import before, declaration, decode, digest, identity, publish, source
from agent_offline_observer import scan_tree
import deployment_journal as journal


BASE = Path('/var/lib/igloo/agent-offline-fixtures/issue241-20261003')
BUDGET = b'{"SchemaVersion":1,"Scope":"OfflineAgentStageFixtureV1","Limit":2}'


def durable(directory, name, raw):
    fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
    try:
        while raw:
            count = os.write(fd, raw)
            if count <= 0: raise OSError('AgentFixtureShortWrite')
            raw = raw[count:]
        os.fsync(fd)
    finally: os.close(fd)
    os.fsync(directory)


def private_dir(parent, name):
    try: os.mkdir(name, 0o700, dir_fd=parent)
    except FileExistsError: pass
    fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
    info = os.fstat(fd)
    if info.st_uid != 0 or info.st_gid != 0 or stat.S_IMODE(info.st_mode) != 0o700:
        os.close(fd); raise ValueError('AgentFixtureDirectoryUnprotected')
    return fd


def ensure_base():
    igloo = os.open('/var/lib/igloo', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        parent = private_dir(igloo, 'agent-offline-fixtures')
        try:
            task = private_dir(parent, BASE.name)
            try:
                try: durable(task, 'budget.json', BUDGET)
                except FileExistsError:
                    fd = os.open('budget.json', os.O_RDONLY | os.O_NOFOLLOW, dir_fd=task)
                    try:
                        value = os.read(fd, 1024)
                        if value != BUDGET: raise ValueError('AgentFixtureBudgetChanged')
                    finally: os.close(fd)
                attempts = private_dir(task, 'attempts'); os.close(attempts)
            finally: os.close(task)
        finally: os.close(parent)
    finally: os.close(igloo)


def consume(operation, packet_hash, run):
    attempts = journal.open_directory(str(BASE/'attempts'))
    try:
        names = sorted(os.listdir(attempts))
        if len(names) >= 2 or any(not re.fullmatch(r'[0-9]{8}\.json', n) for n in names):
            raise ValueError('AgentFixtureBudgetExhaustedOrChanged')
        for name in names:
            prior = decode(journal.read(attempts, name))
            if prior['OperationId'] == operation:
                raise ValueError('AgentFixtureOperationReused')
        record = {'SchemaVersion': 1, 'Scope': 'OfflineAgentStageFixtureV1', 'Sequence': len(names),
                  'OperationId': operation, 'PacketSha256': packet_hash, 'Run': str(run),
                  'State': 'ConsumedBeforeInstallerInvocation', 'RemainingAfter': 1-len(names)}
        raw = json.dumps(record, separators=(',', ':')).encode()
        durable(attempts, f'{len(names):08d}.json', raw)
        check = journal.read(attempts, f'{len(names):08d}.json')
        if check != raw: raise ValueError('AgentFixtureBudgetReopenChanged')
        return record
    finally: os.close(attempts)


def make_fixture(root):
    root.mkdir(mode=0o700)
    for path in ('usr', 'usr/lib', 'etc', 'etc/systemd', 'etc/systemd/system',
                 'etc/systemd/system/multi-user.target.wants', 'etc/igloo',
                 'home', 'home/iglootest', 'home/iglootest/Documents'):
        item = root/path
        item.mkdir(mode=0o700 if path == 'home/iglootest' else 0o755)
        if path.startswith('home/iglootest'): os.chown(item, 1000, 1000)
    sentinel = root/'home/iglootest/Documents/résumé.txt'
    sentinel.write_bytes(b'synthetic selected-document sentinel\n')
    os.chown(sentinel, 1000, 1000); sentinel.chmod(0o600)
    (root/'etc/igloo/unrelated.txt').write_bytes(b'synthetic unrelated configuration\n')


def perform(packet_path):
    ensure_base()
    packet_fd = os.open(packet_path, os.O_RDONLY | os.O_NOFOLLOW)
    try:
        info = os.fstat(packet_fd)
        if not stat.S_ISREG(info.st_mode) or info.st_size <= 0 or info.st_size > 1024*1024:
            raise ValueError('AgentFixturePacketUnsupported')
        packet_raw = os.read(packet_fd, info.st_size+1)
        if len(packet_raw) != info.st_size: raise ValueError('AgentFixturePacketChanged')
        packet_hash = digest(packet_raw)
        packet_identity = (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)
    finally: os.close(packet_fd)
    plan, worker, unit = source(decode(packet_raw))
    run = BASE/uuid.uuid4().hex
    run.mkdir(mode=0o700)
    make_fixture(run/'root')
    (run/'journal').mkdir(mode=0o700)
    run_fd = journal.open_directory(str(run))
    try: durable(run_fd, 'packet.json', packet_raw)
    finally: os.close(run_fd)
    root = os.open(run/'root', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        original = identity(root)
        snapshot = scan_tree(root)
        intent = declaration(root, plan, worker, unit)
        intent['BeforeTreeSha256'] = digest(json.dumps(snapshot, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode())
        run_fd = journal.open_directory(str(run))
        try: durable(run_fd, 'before-tree.json', json.dumps(snapshot, separators=(',', ':'), ensure_ascii=False).encode())
        finally: os.close(run_fd)
        plan_hash = digest(base64.b64decode(decode(packet_raw)['Declaration'], validate=True))
        journal.perform(str(run/'journal'), plan['OperationId'], plan_hash, 'reserve')
        record = {'SchemaVersion': 1, 'OperationId': plan['OperationId'], 'PlanSha256': plan_hash,
                  'State': 'IntentDurable', 'Intent': intent, 'PacketSha256': packet_hash}
        raw = json.dumps(record, separators=(',', ':'), ensure_ascii=False).encode()
        reference = journal.perform(str(run/'journal'), plan['OperationId'], plan_hash, 'append', raw).decode()
        if journal.perform(str(run/'journal'), plan['OperationId'], plan_hash, 'read', reference=reference) != raw:
            raise ValueError('AgentFixtureIntentReopenChanged')
        consumed = consume(plan['OperationId'], packet_hash, run)

        def revalidate():
            current = os.open(packet_path, os.O_RDONLY | os.O_NOFOLLOW)
            try:
                state = os.fstat(current)
                same = ((state.st_dev, state.st_ino, state.st_size, state.st_mtime_ns, state.st_ctime_ns) == packet_identity and
                        digest(os.read(current, state.st_size+1)) == packet_hash)
            finally: os.close(current)
            if identity(root) != original or not same or digest((run/'packet.json').read_bytes()) != packet_hash:
                raise ValueError('AgentFixtureSourceOrRootChanged')

        publish(root, plan, worker, unit, {k: v for k, v in intent.items() if k != 'BeforeTreeSha256'}, revalidate)
    finally: os.close(root)
    # A different process now observes the whole tree and exact installed bytes.
    observer = Path(__file__).resolve().parents[2]/'distros/debian/native/agent_offline_observer.py'
    result = subprocess.run([sys.executable, str(observer), str(run/'root'), str(run/'packet.json'),
                             str(run/'before-tree.json')], capture_output=True, timeout=30, check=False)
    if result.returncode != 0 or len(result.stdout) > 1024*1024:
        raise ValueError('AgentFixtureIndependentObserverFailed')
    observed = decode(result.stdout)
    proof = {k: observed[k] for k in ('WholeTreeEntriesBefore', 'WholeTreeEntriesAfter', 'WholeTreeDelta')}
    readback = {k: observed[k] for k in ('GenerationId', 'OperationId', 'TargetRootId', 'Files',
                                         'ConfigurationAbsent', 'EnableLinkAbsent', 'SourceAndUnrelatedContentUnchanged')}
    run_fd = journal.open_directory(str(run))
    try:
        durable(run_fd, 'observer.json', result.stdout)
        packet = {'Declaration': decode(packet_raw)['Declaration'],
                  'Readback': base64.b64encode(json.dumps(readback, separators=(',', ':')).encode()).decode()}
        durable(run_fd, 'dotnet-return.json', json.dumps(packet, separators=(',', ':')).encode())
    finally: os.close(run_fd)
    final = {'SchemaVersion': 1, 'OperationId': plan['OperationId'], 'PlanSha256': plan_hash,
             'State': 'StagedInputsPending', 'IntentReference': reference,
             'ObservationSha256': digest(result.stdout), 'WholeTree': proof,
             'BudgetRecord': consumed['Sequence']}
    raw = json.dumps(final, separators=(',', ':')).encode()
    reference = journal.perform(str(run/'journal'), plan['OperationId'], plan_hash, 'append', raw).decode()
    if journal.perform(str(run/'journal'), plan['OperationId'], plan_hash, 'read', reference=reference) != raw:
        raise ValueError('AgentFixtureResultReopenChanged')
    print(json.dumps({'State': 'StagedInputsPending', 'Run': str(run), 'OperationId': plan['OperationId'],
                      'ResultReference': reference, 'BudgetRemaining': consumed['RemainingAfter'],
                      'ObservedFiles': len(readback['Files']), 'WholeTreeDelta': proof['WholeTreeDelta']},
                     separators=(',', ':'), ensure_ascii=False))


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('usage: agent_offline_rehearsal.py PACKET')
    perform(sys.argv[1])
