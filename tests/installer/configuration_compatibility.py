"""Read-only diagnostic over the retained manifest/content bytes, never authority.

Uses the executor's declaration review. Individual retained inputs are read through
bounded chunk offsets and checked against the externally nominated manifest hash.
No mounts, target helpers, target writes, authorization or reservation are performed.
"""
import hashlib
import json
import os
from pathlib import Path
import posixpath
import re
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'distros/debian/native'))
import session_configuration as configuration


class RetainedInputs:
    def __init__(self, directory, expected_manifest):
        self.directory = Path(directory)
        data = (self.directory/'root.manifest.json').read_bytes()
        if configuration.digest(data) != expected_manifest: raise ValueError('ManifestBindingChanged')
        self.manifest = configuration.root.validate_manifest(data)
        self.entries = {e['Path']: e for e in self.manifest['Entries']}
        self.offsets = {}; offset = len(b'IGLOO-SEMANTIC-ROOT-V1\n')
        for e in self.manifest['Entries']:
            if e['Type'] == 'File': self.offsets[e['Path']] = offset; offset += e['Length']
        self.read = {}

    def resolve(self, path):
        # Only a manifest lookup: never follow a host path or grant a write alias.
        for _ in range(32):
            parts = path.strip('/').split('/'); changed = False
            for i in range(1, len(parts)+1):
                prefix = '/' + '/'.join(parts[:i]); entry = self.entries.get(prefix)
                if entry is None: raise ValueError('DependencyMissing:' + prefix)
                if entry['Type'] == 'SymbolicLink':
                    destination = entry['Target']
                    path = posixpath.normpath(posixpath.join(posixpath.dirname(prefix), destination, *parts[i:]))
                    changed = True; break
                if i < len(parts) and entry['Type'] != 'Directory': raise ValueError('DependencyParentType')
            if not changed: return path
        raise ValueError('DependencyLinkCycle')

    def bytes(self, path):
        path = self.resolve(path); e = self.entries[path]
        if e['Type'] != 'File' or e['Length'] > 32*1024*1024: raise ValueError('InputTypeOrBound')
        remaining = e['Length']; offset = self.offsets[path]; data = bytearray()
        while remaining:
            index, start = divmod(offset, 1073741824); length = min(remaining, 1073741824-start)
            fd = os.open(self.directory/f'root.content.{index:04d}', os.O_RDONLY | os.O_NOFOLLOW)
            try: part = os.pread(fd, length, start)
            finally: os.close(fd)
            if len(part) != length: raise ValueError('RetainedInputShortRead')
            data.extend(part); offset += length; remaining -= length
        if configuration.digest(data) != e['Sha256']: raise ValueError('RetainedInputChanged')
        self.read[path] = e['Sha256']
        return bytes(data)

    def executable_closure(self, paths):
        pending = list(paths); found = {}; unresolved = []
        while pending:
            path = self.resolve(pending.pop())
            if path in found: continue
            data = self.bytes(path); dependencies = []
            if data.startswith(b'#!'):
                dependencies.append(data.splitlines()[0][2:].decode().strip().split()[0])
            elif data.startswith(b'\x7fELF'):
                fd = os.memfd_create('igloo-readonly-elf-diagnostic')
                try:
                    os.write(fd, data)
                    result = subprocess.run(['/usr/bin/readelf', '-l', '-d', f'/proc/self/fd/{fd}'],
                        pass_fds=(fd,), capture_output=True, check=True, timeout=30,
                        env={'PATH': '/usr/bin', 'LC_ALL': 'C'})
                finally: os.close(fd)
                text = result.stdout.decode()
                dependencies.extend(re.findall(r'Requesting program interpreter: ([^\]]+)', text))
                search = []
                for value in re.findall(r'\((?:RUNPATH|RPATH)\).*\[([^\]]+)\]', text):
                    for directory in value.split(':'):
                        if directory not in ('/usr/libexec/sudo', '/usr/lib/x86_64-linux-gnu'):
                            raise ValueError('DependencySearchPathUnreviewed:' + directory)
                        search.append(directory)
                for name in re.findall(r'\(NEEDED\).*\[([^\]]+)\]', text):
                    candidates = list(dict.fromkeys([d+'/'+name for d in search] + ['/usr/lib/x86_64-linux-gnu/'+name, '/usr/lib/'+name]))
                    matched = [p for p in candidates if p in self.entries]
                    if len(matched) != 1: unresolved.append({'Parent': path, 'Library': name})
                    else: dependencies.extend(matched)
            else: raise ValueError('ExecutableFormatUnsupported:' + path)
            found[path] = {'Sha256': self.entries[path]['Sha256'], 'Dependencies': dependencies}
            pending.extend(dependencies)
        return {'Objects': found, 'Unresolved': unresolved,
                'Limit': 'ELF DT_NEEDED/interpreters only; Perl, NSS, OpenSSL modules and shell data are separately reviewed and covered by full baseline/complement checks.'}


def review(directory, expected_manifest):
    source = RetainedInputs(directory, expected_manifest)
    plan = {'SchemaVersion': 1, 'TransformationPolicy': configuration.TRANSFORMATION_POLICY,
        'Provenance': {'Scope': 'CoreConfiguration'}, 'Hostname': 'igloo-lab-config', 'Username': 'iglootest',
        'UserId': 1000, 'Locale': 'en_US.UTF-8', 'Timezone': 'Etc/UTC', 'Keyboard': 'us'}
    baseline = {'Exim': source.bytes('/etc/exim4/update-exim4.conf.conf').decode(),
                'UserDefaults': source.bytes('/etc/default/useradd').decode()}
    account = {p: source.bytes(p).decode() for p in configuration.ACCOUNT_FILES}
    configuration.account_before(account, plan)
    if configuration.rows(account['/etc/group'].encode(), 4)['ssl-cert'][2] != '105':
        raise ValueError('ReviewedTlsGroupChanged')
    if configuration.rows(account['/etc/group'].encode(), 4)['Debian-exim'][2] != '104':
        raise ValueError('ReviewedEximGroupChanged')
    parameters = {'Fstab': 'DIAGNOSTIC: generated from fresh canonical UUIDs at execution',
                  'AptSources': 'DIAGNOSTIC: supplied by the authenticated canonical plan'}
    rows = configuration.compatibility(source.manifest, plan, parameters, baseline)
    for row in rows:
        if row['Operation'] in ('Utf8File', 'Read', 'Executable') and row['Before'] and row['Before']['Type'] == 'File':
            source.bytes(row['Path'])  # Hash only; never export shadow/password database bytes.
    closure = source.executable_closure((*configuration.HELPERS, '/usr/sbin/exim4', '/usr/bin/openssl',
        '/usr/bin/localedef', '/usr/bin/hostname', '/usr/bin/getopt', '/usr/bin/sed', '/usr/bin/grep', '/usr/bin/tr',
        '/usr/bin/cat', '/usr/bin/mv', '/usr/bin/chmod', '/usr/bin/chown', '/usr/bin/mktemp', '/usr/bin/ln',
        '/usr/bin/readlink', '/usr/bin/rm', '/usr/bin/basename', '/usr/bin/dirname', '/usr/bin/id', '/usr/bin/touch',
        '/usr/bin/fold', '/usr/bin/expr', '/usr/bin/ls', '/usr/bin/gzip'))
    return source, {'ManifestSha256': expected_manifest, 'TransformationPolicy': configuration.TRANSFORMATION_POLICY,
        'Rows': rows, 'ExecutableClosure': closure, 'ReadInputHashes': source.read,
        'Packages': [p for p in source.manifest['Packages'] if p['Name'] in
                     ('passwd', 'debconf', 'perl', 'exim4-config', 'exim4-base', 'ssl-cert', 'locales', 'libc6', 'sudo', 'openssl')],
        'AccountBeforeState': 'Existing closed account validator passed; UID/GID 1000 unused; sudo empty; root locked; ssl-cert GID 105',
        'Skeleton': [e for e in source.manifest['Entries'] if e['Path'].startswith('/etc/skel/')],
        'DynamicHelperBehavior': 'Not yet verified: must execute under the broker and pass independent semantics and the exact whole-tree complement.',
        'ExecutionAuthority': False}
