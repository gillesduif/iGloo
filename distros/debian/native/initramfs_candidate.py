"""Closed candidate-image contract; publication and canonical authority are separate.

The retained 0.148.4 mkinitramfs interface separates candidate generation from
publication and managed update state. The artifact has no post-update hooks;
no managed-state marker or bootloader alias is manufactured.
"""
import re
import stat
import struct
from pathlib import Path

import configured_root as root
from initramfs_archive import MAX_IMAGE, read_image, resolve
from isolation_policy import Launch, Profile, canonical, digest, require

POLICY = 'debian-trixie-lab-initramfs-candidate-v1'
RELEASE = '6.12.107+deb13-amd64'
PACKAGE_VERSION = '6.12.107-1'
HOOKS = ('amd64_microcode', 'dmsetup', 'fsck', 'fuse', 'intel_microcode', 'keymap',
         'klibc-utils', 'kmod', 'ntfs_3g', 'plymouth', 'resume', 'thermal', 'udev', 'zz-busybox')
TOOL = '/usr/sbin/mkinitramfs'
TOOL_SHA256 = '16780CBE540FA1A9D7B1FAAD173B1FD74BDD4DE3E56E773D10462D9CE77161BF'
CONFIGURATION = b'MODULES=most\nBUSYBOX=auto\nKEYMAP=n\nCOMPRESS=gzip\nFSTYPE=ext4\nRESUME=none\nUMASK=0077\n'


def declaration(manifest, root_uuid, predecessor_sha256):
    import uuid
    require(str(uuid.UUID(root_uuid)) == root_uuid and re.fullmatch('[0-9A-F]{64}', predecessor_sha256),
            'InitramfsPlanIdentity')
    entries = {e['Path']: e for e in manifest['Entries']}
    packages = {p['Name']: p for p in manifest['Packages']}
    for name, version in (('linux-image-' + RELEASE, PACKAGE_VERSION), ('initramfs-tools-core', '0.148.4'), ('kmod', '34.2-2')):
        require(packages.get(name, {}).get('Version') == version and
                packages[name]['DpkgStatus'] == 'install ok installed', 'InitramfsPackageBindingChanged')
    require(entries[TOOL]['Sha256'] == TOOL_SHA256, 'InitramfsToolRevisionChanged')
    hooks = [e for e in manifest['Entries'] if e['Path'].startswith('/usr/share/initramfs-tools/hooks/')]
    require(tuple(e['Path'].rsplit('/', 1)[1] for e in hooks) == HOOKS and
            all(e['Type'] == 'File' and (e['Uid'], e['Gid'], e['Mode']) == (0, 0, 0o755) for e in hooks),
            'InitramfsHookSetChanged')
    require(not any(e['Path'].startswith('/etc/initramfs-tools/hooks/') for e in manifest['Entries']),
            'InitramfsLocalHookUnsupported')
    inputs = [e for e in manifest['Entries'] if e['Path'].startswith(('/usr/share/initramfs-tools/',
        '/etc/initramfs-tools/', '/usr/lib/modules/' + RELEASE + '/')) or e['Path'] in
        (TOOL, '/boot/vmlinuz-' + RELEASE, '/boot/config-' + RELEASE)]
    for name in ('modules.dep', 'modules.builtin', 'modules.builtin.modinfo', 'modules.order'):
        require(entries.get('/usr/lib/modules/' + RELEASE + '/' + name, {}).get('Type') == 'File',
                'InitramfsModuleMetadataMissing')
    require(entries.get('/boot/config-' + RELEASE, {}).get('Type') == 'File' and
            entries.get('/boot/vmlinuz-' + RELEASE, {}).get('Type') == 'File' and
            '/boot/initrd.img-' + RELEASE not in entries, 'InitramfsKernelOrOutputBeforeState')
    return {'Policy': POLICY, 'ConfigurationResultSha256': predecessor_sha256, 'KernelRelease': RELEASE,
        'KernelPackageVersion': PACKAGE_VERSION, 'RootUuid': root_uuid, 'Modules': 'most', 'Resume': 'none',
        'Compression': 'gzip', 'InputClosureSha256': digest(canonical(inputs)),
        'HookSetSha256': digest(canonical(hooks)), 'Output': '/boot/initrd.img-' + RELEASE,
        'CpuVendor': 'AuthenticAMD', 'StorageTopology': 'VirtioBlockExt4',
        'ManagedUpdateState': 'Deferred', 'BootloaderIntegration': 'Deferred'}


def verify_execution_inputs(view, manifest, plan, cpu_vendors):
    require(plan == declaration(manifest, plan['RootUuid'], plan['ConfigurationResultSha256']) and
            cpu_vendors == {plan['CpuVendor']}, 'InitramfsExecutionPlanChanged')
    entries = {e['Path']: e for e in manifest['Entries']}
    for path in ('/etc/default/intel-microcode', '/etc/default/amd64-microcode'):
        data = root.read_small(view, path)
        require(digest(data) == entries[path]['Sha256'] and all(not line.strip() or line.lstrip().startswith(b'#')
            for line in data.splitlines()), 'InitramfsMicrocodeOverrideUnsupported')


def launch(operation_id, plan_hash, plan):
    require(plan['Policy'] == POLICY and plan['KernelRelease'] == RELEASE and plan['Modules'] == 'most' and
            plan['Resume'] == 'none' and plan['Compression'] == 'gzip', 'InitramfsPolicyChanged')
    return Launch(operation_id, plan_hash, 'GenerateInitramfs', Profile.CONFIGURATION, TOOL, TOOL_SHA256,
        ('-d', '/var/tmp/config', '-m', 'most', '-r', 'UUID=' + plan['RootUuid'], '-c', 'gzip',
         '-o', '/var/tmp/candidate.img', RELEASE), 1800, view='InitramfsCandidate')


def prepare_workspace(path, view, manifest):
    """Create-new private inputs only; no target write or helper invocation."""
    path = Path(path)
    require(not path.exists(), 'InitramfsWorkspaceAlreadyExists')
    path.mkdir(mode=0o700)
    config = path / 'config'
    config.mkdir(mode=0o700)
    for name in ('conf.d', 'hooks', 'scripts'):
        (config/name).mkdir(mode=0o700)
    # Preserve supported target configuration inputs, including local boot scripts.
    # Executable local build hooks are rejected by declaration(), never dropped.
    for entry in manifest['Entries']:
        if not entry['Path'].startswith('/etc/initramfs-tools/'):
            continue
        relative = entry['Path'][len('/etc/initramfs-tools/'):]
        if relative == 'initramfs.conf':
            continue
        dest = config / relative
        if entry['Type'] == 'Directory':
            dest.mkdir(mode=0o700, exist_ok=True)
        else:
            require(entry['Type'] == 'File', 'InitramfsConfigObjectUnsupported')
            data = root.read_small(view, entry['Path'])
            require(digest(data) == entry['Sha256'], 'InitramfsConfigurationInputChanged')
            with dest.open('xb') as output:
                output.write(data)
            dest.chmod(entry['Mode'])
    with (config/'initramfs.conf').open('xb') as output:
        output.write(CONFIGURATION)
    return path


def elf_dependencies(data, *, sonames=False, search_paths=False):
    if not data.startswith(b'\x7fELF'):
        return None, ()
    require(len(data) >= 64 and data[4:6] == b'\x02\x01', 'InitramfsElfUnsupported')
    phoff = struct.unpack_from('<Q', data, 32)[0]
    size, count = struct.unpack_from('<HH', data, 54)
    if struct.unpack_from('<H', data, 16)[0] == 1:  # Authenticated relocatable kernel module.
        require(phoff == 0 and count == 0, 'InitramfsModuleProgramHeaders')
        return None, ()
    require(size == 56 and count <= 128 and phoff + size * count <= len(data), 'InitramfsElfHeader')
    segments = [struct.unpack_from('<IIQQQQQQ', data, phoff + i * size) for i in range(count)]
    interpreter = None
    dynamic = []
    def string(offset):
        end = data.find(b'\0', offset, offset + 4096)
        require(0 <= offset < end <= len(data), 'InitramfsElfString')
        return data[offset:end].decode('ascii', 'strict')
    for kind, _, offset, _, _, length, _, _ in segments:
        require(offset + length <= len(data), 'InitramfsElfSegment')
        if kind == 3:
            interpreter = string(offset)
        if kind == 2:
            require(length % 16 == 0 and length <= 65536, 'InitramfsElfDynamic')
            for i in range(offset, offset + length, 16):
                tag, value = struct.unpack_from('<QQ', data, i)
                if tag == 0: break
                dynamic.append((tag, value))
    needed = [value for tag, value in dynamic if tag in ((15, 29) if search_paths else (14,) if sonames else (1,))]
    if not needed: return interpreter, ()
    tables = [value for tag, value in dynamic if tag == 5]
    require(len(tables) == 1, 'InitramfsElfStringTable')
    mapped = [offset + tables[0] - address for kind, _, offset, address, _, length, _, _ in segments
              if kind == 1 and address <= tables[0] < address + length]
    require(len(mapped) == 1, 'InitramfsElfStringAddress')
    return interpreter, tuple(string(mapped[0] + value) for value in needed)


def observe_image(data, manifest, plan, kernel_config, module_dependencies, view=None, generated_observer=None):
    """Independent archive/data closure check. Never executes early userspace."""
    source = {e['Path'].lstrip('/'): e for e in manifest['Entries']}
    # The retained ntfs hook copies this authenticated set-ID file as archive
    # data. It is never executed by inspection; all other set-ID objects fail.
    ntfs = source.get('usr/bin/ntfs-3g', {})
    require((ntfs.get('Type'), ntfs.get('Uid'), ntfs.get('Gid'), ntfs.get('Mode')) ==
            ('File', 0, 0, 0o4755), 'InitramfsNtfsSourceMetadata')
    members = read_image(data, copied_setid={'usr/bin/ntfs-3g': (0, 0, 0o4755, ntfs['Sha256'])})
    prefix = 'usr/lib/modules/' + RELEASE + '/'
    require(plan['Policy'] == POLICY and plan['KernelRelease'] == RELEASE, 'InitramfsObserverPolicy')
    def get(path):
        name = resolve(members, path.lstrip('/'))
        require(name in members, 'InitramfsRequiredMemberMissing')
        return members[name]
    require(get('init').sha256 == source['usr/share/initramfs-tools/init']['Sha256'], 'InitramfsInitChanged')
    require(get('conf/initramfs.conf').data == CONFIGURATION and
            get('conf/conf.d/root').data == ('ROOT=UUID=' + plan['RootUuid'] + '\n').encode(), 'InitramfsRootResumePolicy')
    require('conf/conf.d/zz-resume-auto' not in members, 'InitramfsRuntimeResumeLeak')
    require(digest(kernel_config) == source['boot/config-' + RELEASE]['Sha256'], 'InitramfsKernelConfigChanged')
    require(digest(module_dependencies) == source[prefix + 'modules.dep']['Sha256'], 'InitramfsTargetDependenciesChanged')
    settings = set(kernel_config.decode().splitlines())
    require({'CONFIG_BLK_DEV_INITRD=y', 'CONFIG_RD_GZIP=y'} <= settings, 'InitramfsKernelSupportMissing')
    modules = {}
    for path, member in members.items():
        require(member.uid == 0 and member.gid == 0, 'InitramfsUnexpectedOwner')
        require(not re.search(r'(^|/)(shadow|gshadow|machine-id)(-|$)', path) and
                not path.startswith(('home/', 'root/', 'etc/ssl/private/', 'etc/NetworkManager/system-connections/')),
                'InitramfsProhibitedIdentityPath')
        if stat.S_ISREG(member.mode):
            # libgio contains a PEM parser's delimiter literals. Authenticated
            # copied ELF bytes are code, not a generated PEM credential object.
            copied_elf = member.data.startswith(b'\x7fELF') and path in source and \
                source[path]['Type'] == 'File' and member.sha256 == source[path]['Sha256']
            require(copied_elf or not re.search(br'-----BEGIN [A-Z ]*PRIVATE KEY-----|(?m:^)[^\n:]+:\$[156y]\$', member.data),
                    'InitramfsSecretMaterial')
            if path.startswith(prefix) and re.search(r'\.ko(?:\.xz|\.zst)?$', path):
                require(path in source and member.sha256 == source[path]['Sha256'], 'InitramfsModuleChanged')
                modules[path[len(prefix):]] = member
            elif '/modules/' in path:
                require(path.startswith(prefix), 'InitramfsWrongKernel')
            interpreter, dependencies = elf_dependencies(member.data)
            if interpreter: get(interpreter)
            if member.data.startswith(b'#!'):
                line = member.data.split(b'\n', 1)[0][2:].strip().split()
                require(line and line[0].startswith(b'/') and line[0] != b'/usr/bin/env', 'InitramfsScriptInterpreter')
                get(line[0].decode('ascii'))
            _, embedded_paths = elf_dependencies(member.data, search_paths=True)
            directories = ['usr/lib/x86_64-linux-gnu/', 'usr/lib/', 'usr/lib64/', 'lib/x86_64-linux-gnu/', 'lib/']
            for paths in embedded_paths:
                for directory in paths.split(':'):
                    require(directory == '/usr/lib/x86_64-linux-gnu/systemd', 'InitramfsUnsupportedLibrarySearchPath')
                    directories.append(directory.lstrip('/') + '/')
            for dependency in dependencies:
                require('/' not in dependency and any(resolve(members, directory + dependency) in members
                    for directory in directories),
                    'InitramfsLibraryClosureMissing')
    require(modules, 'InitramfsModulesMissing')
    dependency_map = {}
    for line in module_dependencies.decode().splitlines():
        name, dependencies = line.split(':', 1)
        dependency_map[name] = dependencies.split()
    for name in modules:
        require(name in dependency_map and set(dependency_map[name]) <= set(modules), 'InitramfsModuleDependencyMissing')
    for symbol, module in (('CONFIG_VIRTIO', 'virtio'), ('CONFIG_VIRTIO_PCI', 'virtio_pci'),
                           ('CONFIG_VIRTIO_BLK', 'virtio_blk'), ('CONFIG_EXT4_FS', 'ext4')):
        require(symbol + '=y' in settings or symbol + '=m' in settings and any(
            re.fullmatch(re.escape(module) + r'\.ko(?:\.xz|\.zst)?', name.rsplit('/', 1)[-1]) for name in modules),
            'InitramfsRequiredStorageDriverMissing')
    require(get('usr/sbin/modprobe') and get('usr/lib/systemd/systemd-udevd') and get('usr/sbin/fsck.ext4'),
            'InitramfsStorageUserspaceMissing')
    require(generated_observer is not None, 'InitramfsGeneratedObserverMissing')
    generated, details = generated_observer(members)
    verify_payload(members, manifest, plan, view, generated)
    return {'ImageSha256': digest(data), 'ImageLength': len(data), 'Entries': len(members),
        'KernelRelease': RELEASE, 'Modules': len(modules), 'RootUuid': plan['RootUuid'],
        'Policy': POLICY, 'GeneratedContent': details, 'TargetBooted': False}


def verify_payload(members, manifest, plan, view, generated=frozenset()):
    """Reject unaccounted data, not merely known secret filenames.

    Copy transformations are matched to authenticated source objects. Generated
    state has separate exact contracts. Unsupported generated state fails closed.
    """
    source = {e['Path'].lstrip('/'): e for e in manifest['Entries']}
    from initramfs_archive import Member
    virtual = {p: Member(p, stat.S_IFLNK if e['Type'] == 'SymbolicLink' else stat.S_IFDIR if e['Type'] == 'Directory'
        else stat.S_IFREG, e['Uid'], e['Gid'], 1, (), (e['Target'] or '').encode()) for p, e in source.items()}
    def source_entry(path):
        entry = source.get(resolve(virtual, path))
        while entry is not None and entry['Type'] == 'HardLink':
            entry = source.get(entry['Target'].lstrip('/'))
        return entry
    fixed = {'conf/initramfs.conf': CONFIGURATION, 'conf/arch.conf': b'DPKG_ARCH=amd64\n',
        'conf/conf.d/root': ('ROOT=UUID=' + plan['RootUuid'] + '\n').encode(), 'etc/fstab': b'',
        'etc/passwd': b'root:x:0:0:root:/root:/bin/sh\n', 'etc/nsswitch.conf': b'passwd: files\n'}
    for path, member in members.items():
        if stat.S_ISDIR(member.mode):
            require(stat.S_IMODE(member.mode) in (0o700, 0o755), 'InitramfsDirectoryMode')
            require(path in ('.', 'kernel', 'kernel/x86', 'kernel/x86/microcode', 'conf', 'conf/conf.d',
                'scripts', 'scripts/init-bottom', 'scripts/init-premount', 'scripts/init-top',
                'scripts/local-bottom', 'scripts/local-premount', 'scripts/panic') or path in generated or
                source.get(path, {}).get('Type') == 'Directory', 'InitramfsUnexpectedDirectory')
            continue
        if stat.S_ISLNK(member.mode):
            resolved = resolve(members, path)
            require(resolved in members or path == 'etc/mtab' and member.data == b'/proc/mounts',
                    'InitramfsDanglingLink')
            special = {'etc/mtab': b'/proc/mounts',
                'usr/share/fonts/Plymouth.ttf': b'/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',
                'usr/share/fonts/Plymouth-monospace.ttf': b'/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf'}
            require(member.data == special[path] if path in special else resolve(virtual, path) == resolved,
                    'InitramfsLinkSourceChanged')
            continue
        require(not member.mode & 0o022, 'InitramfsWritablePayload')
        if path in generated:
            continue
        if path in fixed:
            require(member.data == fixed[path] and stat.S_IMODE(member.mode) ==
                (0o644 if path == 'conf/initramfs.conf' else 0o600), 'InitramfsGeneratedConfigurationChanged')
            continue
        if path == 'conf/modules':
            entry = source['etc/initramfs-tools/modules']
            require(member.sha256 == entry['Sha256'] and stat.S_IMODE(member.mode) == entry['Mode'],
                    'InitramfsForcedModulePolicy')
            continue
        if path.startswith('scripts/') and path.endswith('/ORDER'):
            require(stat.S_IMODE(member.mode) == 0o600, 'InitramfsBootOrderMode')
            directory = path.rsplit('/', 1)[0]
            lines = member.data.decode().splitlines()
            require(len(lines) % 2 == 0 and all(lines[i + 1] == '[ -e /conf/param.conf ] && . /conf/param.conf'
                for i in range(0, len(lines), 2)), 'InitramfsBootOrderFormat')
            ordered = []
            for line in lines[::2]:
                match = re.fullmatch('/' + re.escape(directory) + r'/([A-Za-z0-9_.-]+) "\$@"', line)
                require(match is not None and directory + '/' + match[1] in members, 'InitramfsBootOrderMember')
                ordered.append(directory + '/' + match[1])
            require(len(ordered) == len(set(ordered)) and set(ordered) == {p for p, m in members.items()
                if p.rsplit('/', 1)[0] == directory and p != path and stat.S_ISREG(m.mode) and m.mode & 0o111},
                'InitramfsBootOrderIncomplete')
            for index, name in enumerate(ordered):
                dependencies = re.findall(rb'(?m:^)(?:PREREQ|PREREQS)="([A-Za-z0-9_ .-]*)"$', members[name].data)
                for declaration in dependencies:
                    for dependency in declaration.decode().split():
                        target = directory + '/' + dependency
                        require(target not in ordered or ordered.index(target) < index, 'InitramfsBootOrderDependency')
            continue
        mapped = 'usr/share/initramfs-tools/' + path if path == 'init' or path.startswith('scripts/') else path
        entry = source_entry(mapped)
        if entry is not None and entry['Type'] == 'File' and member.sha256 == entry['Sha256']:
            # The retained keyboard/plymouth hooks use cp without -p under the
            # declared 0077 umask. Limit that transformation to their inputs.
            masked = path in ('etc/default/keyboard', 'etc/fonts/conf.d/60-latin.conf', 'etc/os-release',
                'usr/share/plymouth/debian-logo.png', 'var/cache/fontconfig/CACHEDIR.TAG') or \
                path.startswith('usr/share/plymouth/themes/ceratopsian/')
            require(stat.S_IMODE(member.mode) == (entry['Mode'] & ~0o077 if masked else entry['Mode']),
                    'InitramfsCopiedModeChanged')
            continue
        if path.startswith(('usr/bin/', 'usr/sbin/')):
            klibc = source_entry('usr/lib/klibc/bin/' + path.rsplit('/', 1)[-1])
            busybox = source_entry('usr/bin/busybox')
            if any(e is not None and e['Type'] == 'File' and member.sha256 == e['Sha256'] and
                   stat.S_IMODE(member.mode) == e['Mode'] for e in (klibc, busybox)):
                continue
        # Generated module indices, microcode containers and font caches require
        # their own semantic observers; a hash from the candidate is not authority.
        raise ValueError('InitramfsUnverifiedGeneratedMember:' + path)
