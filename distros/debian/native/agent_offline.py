"""Create-new offline materialization of the stable first-boot worker and unit.

This file primitive is not an installation authority. The caller owns the
validated root FD, durable intent/reopen, source pin and exclusion of writers.
Configuration and enablement are deliberately absent until receipt binding.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import uuid

from target_files import beneath, mount_id


SCOPE = 'FixtureOnlyOfflineAgentStageV1'
WORKER = '/usr/lib/igloo/debian-agent.py'
UNIT = '/etc/systemd/system/igloo-deployment.service'
CONFIG = '/etc/igloo/deployment.json'
LINK = '/etc/systemd/system/multi-user.target.wants/igloo-deployment.service'
UNIT_SHA256 = 'FA7996FC798C8541EB024324BC661B0ECE546BCE1D41251C223D9193BD88B8FA'
UNIT_REQUIRED = ('DynamicUser=yes', 'User=igloo-deployment', 'NoNewPrivileges=yes',
                 'PrivateDevices=yes', 'PrivateNetwork=yes', 'ProtectSystem=strict',
                 'ProtectHome=yes', 'CapabilityBoundingSet=', 'RestrictNamespaces=yes',
                 'SystemCallFilter=~@mount @raw-io @reboot',
                 'InaccessiblePaths=/boot /sys/firmware')


def require(value, code):
    if not value:
        raise ValueError(code)


def digest(raw):
    return hashlib.sha256(raw).hexdigest().upper()


def decode(raw):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'AgentDuplicateField')
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=unique)


def source(packet, *, scope=SCOPE):
    require(set(packet) == {'Declaration', 'Worker', 'Unit'}, 'AgentSourceShape')
    declaration = decode(base64.b64decode(packet['Declaration'], validate=True))
    worker = base64.b64decode(packet['Worker'], validate=True)
    unit = base64.b64decode(packet['Unit'], validate=True)
    # The actual embedded .NET resource must be the retained worker, never the
    # legacy agent or an arbitrary caller-supplied script.
    require(worker == Path(__file__).with_name('debian_first_boot.py').read_bytes(), 'AgentWorkerSourceChanged')
    text = unit.decode('utf-8', 'strict')
    require(digest(unit) == UNIT_SHA256 and text.startswith('[Unit]\n') and
            text.endswith('[Install]\nWantedBy=multi-user.target\n') and
            all(text.splitlines().count(line) == 1 for line in UNIT_REQUIRED) and
            'ExecStart=/usr/bin/python3 -I /usr/lib/igloo/debian-agent.py --config /etc/igloo/deployment.json' in text.splitlines(),
            'AgentUnitRestrictionChanged')
    validate(declaration, worker, unit, scope=scope)
    return declaration, worker, unit


def validate(plan, worker, unit, *, scope=SCOPE):
    require(set(plan) == {'SchemaVersion', 'Scope', 'GenerationId', 'OperationId', 'TargetRootId',
                          'ProfileVersion', 'Files', 'ConfigurationPath', 'EnableLink', 'EnableTarget',
                          'ConfigurationPending', 'EnablePending', 'StartDuringInstallation'}, 'AgentPlanShape')
    for field in ('GenerationId', 'OperationId', 'TargetRootId'):
        require(type(plan[field]) is str and str(uuid.UUID(plan[field])) == plan[field] and
                uuid.UUID(plan[field]).int != 0, 'AgentIdentityInvalid')
    require(type(plan['SchemaVersion']) is int and plan['SchemaVersion'] == 1 and
            plan['Scope'] == scope and plan['ProfileVersion'] == 'debian-additive-agent-v1' and
            plan['ConfigurationPath'] == CONFIG and plan['EnableLink'] == LINK and plan['EnableTarget'] == UNIT and
            plan['ConfigurationPending'] is True and plan['EnablePending'] is True and
            plan['StartDuringInstallation'] is False, 'AgentPendingBoundaryChanged')
    files = plan['Files']
    require(type(files) is list and len(files) == 2, 'AgentFilesChanged')
    for spec, destination, raw, source_name in zip(files, (WORKER, UNIT), (worker, unit),
                                                    ('embedded:debian-first-boot-evidence-v1', 'generated:debian-additive-agent-v1')):
        require(set(spec) == {'Source', 'Destination', 'Length', 'Sha256', 'Owner', 'Group', 'Mode'} and
                spec['Source'] == source_name and spec['Destination'] == destination and
                type(spec['Length']) is int and spec['Length'] == len(raw) and 0 < len(raw) <= 1024*1024 and
                spec['Sha256'] == digest(raw) and type(spec['Owner']) is int and type(spec['Group']) is int and
                type(spec['Mode']) is int and (spec['Owner'], spec['Group'], spec['Mode']) == (0, 0, 0o644),
                'AgentFileDeclarationChanged')


def identity(fd):
    value = os.fstat(fd)
    require(stat.S_ISDIR(value.st_mode) and value.st_uid == 0 and value.st_gid == 0 and
            stat.S_IMODE(value.st_mode) & 0o022 == 0 and not os.listxattr(fd), 'AgentParentUnprotected')
    return [value.st_dev, value.st_ino, mount_id(fd), stat.S_IMODE(value.st_mode)]


def parent(root, path):
    fd = beneath(root, path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        require(identity(fd)[0] == identity(root)[0] and identity(fd)[2] == identity(root)[2], 'AgentParentMountChanged')
        return fd
    except BaseException:
        os.close(fd)
        raise


def absent(fd, name):
    try:
        os.stat(name, dir_fd=fd, follow_symlinks=False)
    except FileNotFoundError:
        return
    raise ValueError('AgentDestinationExists')


def before(root):
    root_id = identity(root)
    parents = {}
    for path in ('usr', 'usr/lib', 'etc', 'etc/systemd', 'etc/systemd/system',
                 'etc/systemd/system/multi-user.target.wants'):
        fd = parent(root, path)
        try: parents[path] = identity(fd)
        finally: os.close(fd)
    lib = parent(root, 'usr/lib')
    try: absent(lib, 'igloo')
    finally: os.close(lib)
    systemd = parent(root, 'etc/systemd/system')
    try: absent(systemd, 'igloo-deployment.service')
    finally: os.close(systemd)
    wants = parent(root, 'etc/systemd/system/multi-user.target.wants')
    try: absent(wants, 'igloo-deployment.service')
    finally: os.close(wants)
    etc = parent(root, 'etc')
    try:
        try: config = beneath(etc, 'igloo', os.O_RDONLY | os.O_DIRECTORY)
        except FileNotFoundError: config = None
        if config is not None:
            try: identity(config); absent(config, 'deployment.json')
            finally: os.close(config)
    finally: os.close(etc)
    return {'RootIdentity': root_id, 'Parents': parents, 'WorkerAbsent': True,
            'UnitAbsent': True, 'ConfigurationAbsent': True, 'EnableLinkAbsent': True}


def declaration(root, plan, worker, unit, *, scope=SCOPE):
    validate(plan, worker, unit, scope=scope)
    return {'SchemaVersion': 1, 'Policy': 'CreateNewOfflineAgentStageV1',
            'PlanSha256': digest(json.dumps(plan, separators=(',', ':')).encode()), 'Before': before(root),
            'SourceSha256': digest(worker + unit), 'Files': plan['Files']}


def _create(parent_fd, name, raw, expected):
    absent(parent_fd, name)
    file = beneath(parent_fd, name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        pending = memoryview(raw)
        while pending:
            count = os.write(file, pending)
            require(count > 0, 'AgentShortWrite')
            pending = pending[count:]
        os.fchown(file, 0, 0)
        os.fchmod(file, 0o644)
        os.fsync(file)
    finally:
        os.close(file)
    os.fsync(parent_fd)
    require(digest(raw) == expected['Sha256'], 'AgentSourceChangedAfterWrite')


def publish(root, plan, worker, unit, intent, revalidate, *, scope=SCOPE):
    """Publish only after the caller durably appends and reopens this intent."""
    revalidate()
    require(declaration(root, plan, worker, unit, scope=scope) == intent, 'AgentIntentChanged')
    lib = parent(root, 'usr/lib')
    try:
        os.mkdir('igloo', 0o755, dir_fd=lib)
        child = beneath(lib, 'igloo', os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fchown(child, 0, 0); os.fchmod(child, 0o755); os.fsync(child); os.fsync(lib)
            revalidate()
            _create(child, 'debian-agent.py', worker, plan['Files'][0])
        finally: os.close(child)
    finally: os.close(lib)
    revalidate()
    systemd = parent(root, 'etc/systemd/system')
    try: _create(systemd, 'igloo-deployment.service', unit, plan['Files'][1])
    finally: os.close(systemd)
