"""Closed successor configuration phase. Never imports, formats, installs packages or boots.

The canonical session supplies the connected root and read-only payload. Original
artifact verification stays unchanged; configured-state verification is a separate
declared delta plus semantic checks. No target helper runs outside PackageBroker.
"""
import base64
import copy
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess

import configured_root as root
import session_import
from isolation_policy import ENVIRONMENT, Launch, Profile, canonical, digest, require
from package_broker import DirectoryLease, PackageBroker, sealed_copy
from target_files import TargetFiles, beneath

STEPS = ('Baseline', 'Files', 'Debconf', 'Exim', 'Tls', 'Locale', 'User', 'Credential', 'Sudo', 'Verify')
ACCOUNT_FILES = ('/etc/passwd', '/etc/shadow', '/etc/group', '/etc/gshadow')
TRANSFORMATION_POLICY = 'debian-trixie-core-configuration-v5'
HELPERS = ('/usr/bin/debconf-communicate', '/usr/sbin/update-exim4.conf', '/usr/sbin/make-ssl-cert',
           '/usr/sbin/locale-gen', '/usr/sbin/useradd', '/usr/sbin/chpasswd', '/usr/sbin/usermod', '/usr/sbin/visudo')


def profile(plan):
    require(plan['SchemaVersion'] == 1 and plan.get('TransformationPolicy') == TRANSFORMATION_POLICY and
            plan['Provenance']['Scope'] == 'CoreConfiguration' and
            (plan['Hostname'], plan['Username'], plan['UserId'], plan['Locale'], plan['Timezone'], plan['Keyboard']) ==
            ('igloo-lab-config', 'iglootest', 1000, 'en_US.UTF-8', 'Etc/UTC', 'us'), 'UnsupportedCoreProfile')


def files(plan, parameters, exim_before, user_defaults_before):
    profile(plan); hostname = plan['Hostname']
    require(exim_before.count("dc_other_hostnames=''") == 1 and exim_before.count("dc_mailname_in_oh='false'") == 1,
            'EximBeforeStateChanged')
    # CREATE_MAIL_SPOOL is a useradd defaults key, not a login.defs -K key.
    # Bind this narrow edit to the retained input; preserve every other default.
    require(digest(user_defaults_before.encode()) ==
            '6702E1CB0D7035A0D7A2BEEDED69F69ACD8AC2B2778913B5AFE60595936448F5' and
            user_defaults_before.count('# CREATE_MAIL_SPOOL=no\n') == 1, 'UserDefaultsBeforeStateChanged')
    text = {
        '/etc/default/useradd': user_defaults_before.replace('# CREATE_MAIL_SPOOL=no\n', 'CREATE_MAIL_SPOOL=no\n'),
        '/etc/hostname': hostname + '\n',
        '/etc/hosts': '127.0.0.1 localhost\n127.0.1.1 ' + hostname + '\n::1 localhost ip6-localhost ip6-loopback\n',
        '/etc/mailname': hostname + '\n',
        '/etc/exim4/update-exim4.conf.conf': exim_before.replace("dc_other_hostnames=''", "dc_other_hostnames='" + hostname + "'")
            .replace("dc_mailname_in_oh='false'", "dc_mailname_in_oh='true'"),
        # Trixie's /etc/default/locale is an authenticated ../locale.conf link.
        # Write the canonical file and preserve that link; never overwrite it.
        '/etc/locale.gen': plan['Locale'] + ' UTF-8\n', '/etc/locale.conf': 'LANG=' + plan['Locale'] + '\n',
        '/etc/timezone': plan['Timezone'] + '\n',
        '/etc/default/keyboard': 'XKBMODEL="pc105"\nXKBLAYOUT="us"\nXKBVARIANT=""\nXKBOPTIONS=""\n',
        '/etc/network/interfaces': 'auto lo\niface lo inet loopback\n',
        '/etc/apt/sources.list.d/debian.sources': parameters['AptSources'], '/etc/fstab': parameters['Fstab']}
    return {**{p: ('Utf8File', value, 0o644) for p, value in text.items()},
        '/etc/localtime': ('SymbolicLink', '/usr/share/zoneinfo/' + plan['Timezone'], 0o777),
        '/etc/resolv.conf': ('SymbolicLink', '/run/NetworkManager/resolv.conf', 0o777)}


def entries(view):
    return {p: (s, t, a, h) for p, s, t, a, h in root.inspect_tree(view)}


def exact_file(actual, path, uid, gid, mode, length_limit=16 * 1024 * 1024):
    require(path in actual, 'ConfigurationOutputMissing')
    info, link, attrs, _ = actual[path]
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and not attrs and link is None and
            (info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode)) == (uid, gid, mode) and info.st_size <= length_limit,
            'ConfigurationOutputMetadata')


def stanza(data):
    result = {}
    for block in data.decode().strip().split('\n\n'):
        if not block: continue
        key = block.splitlines()[0]
        require(key.startswith('Name: ') and key not in result, 'DebconfDatabaseInvalid')
        result[key] = block
    return result


def rows(data, count):
    values = [r.split(':') for r in data.decode().splitlines()]
    require(all(len(r) == count for r in values) and len({r[0] for r in values}) == len(values), 'AccountDatabaseInvalid')
    return {r[0]: r for r in values}


def account_before(baseline, plan):
    accounts = rows(baseline['/etc/passwd'].encode(), 7)
    groups = rows(baseline['/etc/group'].encode(), 4)
    require(plan['Username'] not in accounts and plan['Username'] not in groups and
            not any(r[2] == str(plan['UserId']) for r in (*accounts.values(), *groups.values())), 'TargetAccountAlreadyExists')
    require('sudo' in groups and groups['sudo'][-1] == '', 'UnexpectedSudoMembers')
    shadow = rows(baseline['/etc/shadow'].encode(), 9)
    require(shadow['root'][1] in ('!', '*', '!*', '!!'), 'RootCredentialNotNeutral')


def verify_delta(view, manifest, plan, parameters, step, baseline, credential_reference):
    index = STEPS.index(step); actual = entries(view); original = {e['Path']: e for e in manifest['Entries']}
    outputs = files(plan, parameters, baseline['Exim'], baseline['UserDefaults']); allowed = set(outputs) if index >= 1 else set()
    if index >= 2: allowed |= {'/var/cache/debconf/config.dat', '/var/cache/debconf/config.dat-old'}
    if index >= 3: allowed.add('/var/lib/exim4/config.autogenerated')
    if index >= 4:
        allowed |= {'/etc/ssl/private/ssl-cert-snakeoil.key', '/etc/ssl/certs/ssl-cert-snakeoil.pem'}
        tls_links = [p for p in actual if p not in original and re.fullmatch(r'/etc/ssl/certs/[0-9a-f]{8}\.0', p)]
        require(len(tls_links) == 1 and actual[tls_links[0]][1] == 'ssl-cert-snakeoil.pem', 'TlsHashLinkInvalid')
        allowed.update(tls_links)
    if index >= 5: allowed.add('/usr/lib/locale/locale-archive')
    home = '/home/' + plan['Username']
    if index >= 6:
        allowed.update(ACCOUNT_FILES); allowed.update(p + '-' for p in ACCOUNT_FILES)
        allowed.add(home)
        allowed.update(home + e['Path'][len('/etc/skel'):] for e in manifest['Entries'] if e['Path'].startswith('/etc/skel/'))
    require(set(actual) - set(original) <= allowed and set(original) - set(actual) <= allowed, 'UndeclaredConfigurationPath')
    # Substitute only the predeclared, independently checked outputs. The original
    # artifact verifier and hardlink/xattr rules remain unchanged for the complement.
    replacement = copy.deepcopy(manifest)
    replacement['Entries'] = [e for e in replacement['Entries'] if e['Path'] not in allowed]
    for path in sorted(allowed & set(actual)):
        info, link, attrs, sha = actual[path]
        require(not attrs and info.st_nlink == 1 if not stat.S_ISDIR(info.st_mode) else not attrs,
                'GeneratedMetadataUnsupported')
        kind = 'Directory' if stat.S_ISDIR(info.st_mode) else 'SymbolicLink' if stat.S_ISLNK(info.st_mode) else 'File'
        entry = {'Path': path, 'Type': kind, 'Uid': info.st_uid, 'Gid': info.st_gid, 'Mode': stat.S_IMODE(info.st_mode),
                 'Length': info.st_size if kind == 'File' else None, 'Sha256': sha if kind == 'File' else None,
                 'Target': link if kind == 'SymbolicLink' else None, 'Xattrs': {}}
        replacement['Entries'].append(entry)
    replacement['Entries'].sort(key=lambda e: e['Path'])
    root.verify_tree(view, replacement)
    if index >= 1:
        for path, (kind, value, mode) in outputs.items():
            info, target, attrs, _ = actual[path]
            require((info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode)) == (0, 0, mode) and not attrs, 'ConfigurationFileMode')
            require(target == value if kind == 'SymbolicLink' else root.read_small(view, path) == value.encode(), 'ConfigurationFileContent')
    if index >= 2:
        old = stanza(baseline['Debconf'].encode()); new = stanza(root.read_small(view, '/var/cache/debconf/config.dat'))
        require(set(old) == set(new), 'DebconfSetChanged')
        for name in old:
            expected = old[name]
            if name in ('Name: exim4/mailname', 'Name: exim4/dc_other_hostnames'):
                expected = re.sub(r'(?m)^Value:.*$', 'Value: ' + plan['Hostname'], expected)
                if not re.search(r'(?m)^Value:', expected): expected += '\nValue: ' + plan['Hostname']
                # debconf ordering is not identity; compare the exact field multiset.
                if name.endswith('dc_other_hostnames'):
                    flags = re.search(r'(?m)^Flags: (.*)$', expected)
                    values = set(flags.group(1).split(', ')) if flags else set()
                    value = 'Flags: ' + ', '.join(sorted(values | {'mailname'}))
                    expected = re.sub(r'(?m)^Flags:.*$', value, expected) if flags else expected + '\n' + value
                    actual_flags = re.search(r'(?m)^Flags: (.*)$', new[name])
                    if actual_flags:
                        new[name] = re.sub(r'(?m)^Flags:.*$', 'Flags: ' + ', '.join(sorted(actual_flags.group(1).split(', '))), new[name])
            require(sorted(expected.splitlines()) == sorted(new[name].splitlines()), 'UnrelatedDebconfChanged')
        exact_file(actual, '/var/cache/debconf/config.dat', 0, 0, 0o644)
        if '/var/cache/debconf/config.dat-old' in actual:
            require(stanza(root.read_small(view, '/var/cache/debconf/config.dat-old')) == old, 'DebconfBackupChanged')
    if index >= 3:
        verify_exim(view, actual, plan, baseline)
    if index >= 4: verify_tls(view, actual, plan)
    if index >= 5:
        exact_file(actual, '/usr/lib/locale/locale-archive', 0, 0, 0o644, 64 * 1024 * 1024)
        require(actual['/usr/lib/locale/locale-archive'][0].st_size > 100000, 'LocaleOutputMissing')
        locale_fd = beneath(view.fd, 'usr/lib/locale/locale-archive', os.O_RDONLY)
        try:
            query = subprocess.run(['/usr/bin/localedef', '--list-archive', f'/proc/self/fd/{locale_fd}'],
                pass_fds=(locale_fd,), capture_output=True, stdin=subprocess.DEVNULL, env=ENVIRONMENT, timeout=30, check=False)
            require(query.returncode == 0 and query.stdout.splitlines() == [b'en_US.utf8'], 'GeneratedLocaleMismatch')
        finally: os.close(locale_fd)
    if index >= 6:
        verify_accounts(view, actual, plan, baseline, index, credential_reference)
        for e in manifest['Entries']:
            if not e['Path'].startswith('/etc/skel/'): continue
            path = home + e['Path'][len('/etc/skel'):]; info, target, _, sha = actual[path]
            require((info.st_uid, info.st_gid) == (1000, 1000) and stat.S_IMODE(info.st_mode) == e['Mode'] and
                    (sha == e['Sha256'] if e['Type'] == 'File' else target == e['Target'] if e['Type'] == 'SymbolicLink' else stat.S_ISDIR(info.st_mode)),
                    'HomeSkeletonChanged')
        require((actual[home][0].st_uid, actual[home][0].st_gid, stat.S_IMODE(actual[home][0].st_mode)) == (1000, 1000, 0o700), 'HomeModeChanged')
    if index >= 8:
        sudo = [line.strip() for line in root.read_small(view, '/etc/sudoers').decode().splitlines() if line.strip() and not line.lstrip().startswith('#')]
        require(any(re.fullmatch(r'%sudo\s+ALL=\(ALL:ALL\)\s+ALL', line) for line in sudo) and not any('NOPASSWD' in line for line in sudo), 'SudoAuthenticationPolicy')
    require(root.read_small(view, '/etc/machine-id') == b'' and actual['/var/lib/dbus/machine-id'][1] == '/etc/machine-id', 'FirstBootIdentityNotPending')
    require(not any(p.startswith('/etc/NetworkManager/system-connections/') or p.startswith('/var/lib/NetworkManager/') for p in actual), 'NetworkIdentityResidue')
    package = root.verify_dpkg(view, manifest)
    return {'FilesystemDeltaSha256': digest(canonical(replacement['Entries'])), 'PackageStateSha256': package,
            'ChangedPaths': sorted(p for p in allowed if p in actual), 'MachineIdentity': 'FirstBootPending', 'ProfileStep': step}


def verify_exim(view, actual, plan, baseline):
    # The retained update-exim4.conf gentmpconf explicitly chowns its temporary
    # output root:Debian-exim before rename. Bind the group to the authenticated
    # baseline account database, not to the generated file's observed owner.
    group = rows(baseline['/etc/group'].encode(), 4).get('Debian-exim')
    require(group is not None and group[2] == '104', 'EximGroupBindingChanged')
    exact_file(actual, '/var/lib/exim4/config.autogenerated', 0, int(group[2]), 0o644)
    exim = root.read_small(view, '/var/lib/exim4/config.autogenerated').decode()
    require(plan['Hostname'] in exim and 'igloo-factory' not in exim and 'DC_eximconfig_configtype=local' in exim.replace(' ', ''),
            'EximIdentityMismatch')


def verify_accounts(view, actual, plan, baseline, index, credential_reference):
    user = plan['Username']
    for path, count in zip(ACCOUNT_FILES, (7, 9, 4, 4)):
        before = rows(baseline[path].encode(), count); after = rows(root.read_small(view, path), count)
        require(user not in before and set(after) == set(before) | {user}, 'AccountSetChanged')
        for name, value in before.items():
            expected = value.copy()
            if index >= 8 and name == 'sudo' and path in ('/etc/group', '/etc/gshadow'): expected[-1] = user
            require(after[name] == expected, 'ExistingSystemAccountChanged')
        if path == '/etc/passwd': require(after[user] == [user, 'x', '1000', '1000', '', '/home/' + user, '/bin/bash'], 'UserIdentityChanged')
        elif path == '/etc/group': require(after[user] == [user, 'x', '1000', ''], 'PrimaryGroupChanged')
        elif path == '/etc/gshadow': require(after[user] == [user, '!', '', ''], 'GroupCredentialChanged')
        else:
            value = after[user][1]
            require(value == '!' if index < 7 else re.fullmatch(r'\$6\$[./A-Za-z0-9]+\$[./A-Za-z0-9]+', value) and
                    digest(value.encode()) == credential_reference, 'CredentialReadbackMismatch')
        expected_info = baseline['AccountMetadata'][path]
        exact_file(actual, path, *expected_info)
        if path + '-' in actual:
            exact_file(actual, path + '-', *expected_info)
            # Backups may contain the immediately preceding declared account state,
            # never an extra principal or changes to unrelated system identities.
            backup = rows(root.read_small(view, path + '-'), count)
            require(set(backup) in (set(before), set(after)), 'AccountBackupChanged')
            for name, value in before.items():
                require(backup[name] == value or name == 'sudo' and path in ('/etc/group', '/etc/gshadow') and backup[name] == after[name],
                        'SystemAccountBackupChanged')
            if user in backup:
                if path == '/etc/shadow':
                    require(backup[user][2:] == after[user][2:] and backup[user][1] in ('!', after[user][1]), 'UserCredentialBackupChanged')
                else: require(backup[user] == after[user], 'UserBackupChanged')


def verify_tls(view, actual, plan):
    key, cert = '/etc/ssl/private/ssl-cert-snakeoil.key', '/etc/ssl/certs/ssl-cert-snakeoil.pem'
    exact_file(actual, key, 0, 105, 0o640); exact_file(actual, cert, 0, 0, 0o644)
    def query(path, args):
        fd = beneath(view.fd, path[1:], os.O_RDONLY)
        try:
            result = subprocess.run(['/usr/bin/openssl', *args, '-in', f'/proc/self/fd/{fd}'], pass_fds=(fd,),
                stdin=subprocess.DEVNULL, capture_output=True, env=ENVIRONMENT, timeout=30, check=False)
            require(result.returncode == 0 and len(result.stdout) < 16384, 'TlsReadbackUnavailable')
            return result.stdout
        finally: os.close(fd)
    require(query(key, ['pkey', '-pubout']) == query(cert, ['x509', '-pubkey', '-noout']), 'TlsKeyCertificateMismatch')
    subject = query(cert, ['x509', '-subject', '-nameopt', 'RFC2253', '-ext', 'subjectAltName', '-noout']).decode()
    require(subject.splitlines()[0] == 'subject=CN=' + plan['Hostname'] and
            'DNS:' + plan['Hostname'] in subject and 'igloo-factory' not in subject, 'TlsSubjectMismatch')
    query(key, ['pkey', '-check', '-noout'])
    query(cert, ['x509', '-checkend', '0', '-noout'])
    link = '/etc/ssl/certs/' + query(cert, ['x509', '-hash', '-noout']).decode().strip() + '.0'
    require(link in actual and actual[link][1] == 'ssl-cert-snakeoil.pem', 'TlsHashLinkMismatch')


def observe_configuration(declaration):
    request = declaration['ConfigurationObservation']; plan = declaration['ConfigurationPlan']; profile(plan)
    data = session_import.read_bound(request['ManifestFd'], root.MAX_MANIFEST)
    require(digest(data) == declaration['ConfigurationPredecessor']['ManifestSha256'], 'ConfigurationManifestChanged')
    manifest = root.validate_manifest(data); view = root.RootView(request['RootFd'], *request['ExpectedRoot'])
    try:
        if request['Step'] == 'Baseline':
            result = {'FilesystemSha256': root.verify_tree(view, manifest), 'PackageStateSha256': root.verify_dpkg(view, manifest),
                      'NeutralStateSha256': root.verify_neutral(view, manifest)}
        else:
            result = verify_delta(view, manifest, plan, request['Parameters'], request['Step'], request['Baseline'], request.get('CredentialReference'))
        result.update(OperationId=plan['OperationId'], PlanSha256=declaration['PlanSha256'], RootWitness=request['ExpectedRoot'])
        print(canonical(result).decode())
        return 0
    finally: view.close()


def independent(session, view, manifest_fd, step, parameters, baseline, credential_reference):
    request = {**session.declaration, 'ConfigurationObservation': {'RootFd': view.fd, 'ExpectedRoot': list(view.expected),
        'ManifestFd': manifest_fd, 'Step': step, 'Parameters': parameters, 'Baseline': baseline, 'CredentialReference': credential_reference}}
    result = subprocess.run([session.runtime.python, '-I', '-B', session.declaration['EntryPath']], input=canonical(request)+b'\n',
        pass_fds=(view.fd, manifest_fd), capture_output=True, env=ENVIRONMENT, timeout=7200, check=False)
    require(result.returncode == 0 and 0 < len(result.stdout) < 65536, 'IndependentConfigurationReadbackUnavailable')
    observed = json.loads(result.stdout)
    require(observed['OperationId'] == session.declaration['ConfigurationPlan']['OperationId'] and
            observed['PlanSha256'] == session.declaration['PlanSha256'] and observed['RootWitness'] == list(view.expected), 'ConfigurationObserverSubstituted')
    return {'ObserverEvidence': observed, 'ObserverEvidenceSha256': digest(canonical(observed))}


def credential_input(username):
    # Fresh high-entropy NON-PRODUCTION lab credential. No default/password file,
    # argv, environment or receipt contains plaintext or the encrypted password.
    library = ctypes.CDLL('libcrypt.so.1')
    library.crypt_r.argtypes = (ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p); library.crypt_r.restype = ctypes.c_void_p
    random = ctypes.create_string_buffer(32)
    libc = ctypes.CDLL(None, use_errno=True)
    require(libc.getrandom(random, 32, 0) == 32, 'CredentialEntropyUnavailable')
    secret = ctypes.create_string_buffer(65)
    alphabet = b'0123456789abcdef'
    for i in range(32):
        value = ctypes.cast(random, ctypes.POINTER(ctypes.c_ubyte))[i]
        secret[2*i] = alphabet[value >> 4]
        secret[2*i+1] = alphabet[value & 15]
    ctypes.memset(random, 0, ctypes.sizeof(random))
    state = ctypes.create_string_buffer(32768)
    encoded = bytearray()
    try:
        value = library.crypt_r(secret, ('$6$' + os.urandom(8).hex() + '$').encode(), state)
        require(value, 'CredentialProducerUnavailable')
        pointer = ctypes.cast(value, ctypes.POINTER(ctypes.c_ubyte))
        for i in range(256):
            if pointer[i] == 0: break
            encoded.append(pointer[i])
        else: raise ValueError('CredentialProducerLength')
        require(encoded.startswith(b'$6$'), 'CredentialProducerAlgorithm')
        reference = digest(encoded)
        payload = bytearray(username.encode() + b':') + encoded + b'\n'
        try: fd = sealed_copy(payload, 'igloo-lab-credential')
        finally: payload[:] = b'\0' * len(payload)
        return fd, reference
    finally:
        ctypes.memset(secret, 0, ctypes.sizeof(secret)); ctypes.memset(state, 0, ctypes.sizeof(state))
        encoded[:] = b'\0' * len(encoded)


def configuration_resources(view, payload):
    resources = {}
    try:
        for name, path in [('root', view.connected), ('esp', view.connected + '/mnt'), ('payload', payload.connected)]:
            resources[name] = DirectoryLease(path)
        require(not os.listdir(resources['esp'].fd) and resources['esp'].mount == resources['root'].mount,
                'ConfigurationEspScaffoldingChanged')
        return resources
    except (OSError, ValueError):
        for resource in resources.values(): resource.close()
        raise


def before_state(entry):
    if entry is None: return {'Kind': 'Absent'}
    require(entry['Type'] in ('File', 'SymbolicLink') and not entry['Xattrs'], 'ConfigurationBeforeMetadata')
    return {'Kind': entry['Type'], 'Owner': entry['Uid'], 'Group': entry['Gid'], 'Mode': entry['Mode'],
            **({'Target': entry['Target']} if entry['Type'] == 'SymbolicLink' else
               {'Length': entry['Length'], 'Sha256': entry['Sha256']})}


def compatibility(manifest, plan, parameters, baseline):
    """One shared, non-mutating declaration review. This is not storage authority.

    Collect independent path failures; never traverse a rejected parent. Native
    preflight additionally repeats FD-relative observation against the mounted root.
    Generated objects are requirements on their producing step, not precreated inputs.
    """
    expected = {e['Path']: e for e in manifest['Entries']}
    outputs = files(plan, parameters, baseline['Exim'], baseline['UserDefaults'])
    requirements = [(p, 'Files', kind, 'File' if kind == 'Utf8File' else 'SymbolicLink', value)
                    for p, (kind, value, _) in outputs.items()]
    requirements += [('/etc/default/locale', 'Files', 'PreserveLink', 'SymbolicLink', '../locale.conf'),
                     ('/usr/share/locale/locale.alias', 'Locale', 'PreserveLink', 'SymbolicLink', '/etc/locale.alias')]
    requirements += [(p, 'Helper', 'Executable', 'File', None) for p in HELPERS]
    # These are exact authenticated prerequisites, including databases which must
    # not be symlinks. Executable dependency closure is checked by the full baseline
    # and unchanged-complement observers before every helper release.
    inputs = {
        'Debconf': ['/etc/debconf.conf', '/var/cache/debconf/config.dat', '/var/cache/debconf/templates.dat',
                    '/var/cache/debconf/passwords.dat', '/usr/share/debconf/confmodule', '/usr/share/debconf/frontend',
                    '/usr/bin/perl'],
        'Exim': ['/etc/exim4/exim4.conf.template', '/usr/sbin/exim4'],
        'Tls': ['/usr/share/ssl-cert/ssleay.cnf', '/etc/ssl/openssl.cnf', '/usr/bin/openssl', '/usr/bin/bash'],
        'Locale': ['/usr/share/i18n/locales/en_US', '/usr/share/i18n/charmaps/UTF-8.gz',
                   '/etc/locale.alias', '/usr/bin/localedef'],
        'User': [*ACCOUNT_FILES, '/etc/login.defs', '/etc/default/useradd', '/etc/.pwd.lock'],
        'Sudo': ['/etc/sudoers'],
        'Files': ['/usr/share/zoneinfo/Etc/UTC']}
    requirements += [(p, step, 'Read', 'File', None) for step, paths in inputs.items() for p in paths]
    for p, step in [('/var/lib/exim4', 'Exim'), ('/etc/ssl/private', 'Tls'), ('/etc/ssl/certs', 'Tls'),
                    ('/usr/lib/locale', 'Locale'), ('/home', 'User'), ('/etc/skel', 'User'), ('/mnt', 'View')]:
        requirements.append((p, step, 'Directory', 'Directory', None))
    for p, step in [('/var/lib/exim4/config.autogenerated', 'Exim'),
                    ('/etc/ssl/private/ssl-cert-snakeoil.key', 'Tls'), ('/etc/ssl/certs/ssl-cert-snakeoil.pem', 'Tls'),
                    ('/usr/lib/locale/locale-archive', 'Locale'), ('/home/' + plan['Username'], 'User'),
                    ('/etc/exim4/exim4.conf', 'Exim')]:
        requirements.append((p, step, 'CreateByHelper' if p != '/etc/exim4/exim4.conf' else 'MustStayAbsent', 'Absent', None))
    report = []
    generated_metadata = {
        '/var/lib/exim4/config.autogenerated': [0, 104, 0o644],
        '/etc/ssl/private/ssl-cert-snakeoil.key': [0, 105, 0o640],
        '/etc/ssl/certs/ssl-cert-snakeoil.pem': [0, 0, 0o644],
        '/usr/lib/locale/locale-archive': [0, 0, 0o644],
        '/home/' + plan['Username']: [1000, 1000, 0o700]}
    for path, step, operation, kind, value in requirements:
        entry = expected.get(path); failures = []; parents = []
        parts = path.strip('/').split('/')
        for i in range(1, len(parts)):
            parent = '/' + '/'.join(parts[:i]); e = expected.get(parent); parents.append(parent)
            if e is None or e['Type'] != 'Directory' or e['Uid'] != 0 or e['Mode'] & 0o022:
                failures.append('UnsafeOrMissingParent:' + parent); break
        actual_kind = entry['Type'] if entry else 'Absent'
        if not failures:
            if operation in ('Utf8File', 'SymbolicLink'):
                if actual_kind not in ('Absent', kind): failures.append('ConfigurationBeforeTypeChanged')
                if entry and (entry['Xattrs'] or entry['Uid'] != 0 or entry['Gid'] != 0 or entry['Mode'] != outputs[path][2]):
                    failures.append('ConfigurationBeforeMetadata')
            elif actual_kind != kind: failures.append('ConfigurationDependencyTypeChanged')
            if operation == 'PreserveLink' and entry and entry['Target'] != value: failures.append('ConfigurationPreservedLinkChanged')
            if operation == 'Executable' and entry and (not entry['Mode'] & 0o111 or entry['Mode'] & 0o6022):
                failures.append('ConfigurationHelperUnavailable')
            if path == '/mnt' and any(p.startswith('/mnt/') for p in expected): failures.append('ConfigurationAliasNotEmpty')
        report.append({'Path': path, 'Step': step, 'Operation': operation, 'Before': entry,
                       'Parents': parents, 'Destination': path,
                       'AfterType': ('Directory' if path == '/home/' + plan['Username'] else 'File') if operation == 'CreateByHelper' else kind,
                       'AfterOwnerGroupMode': generated_metadata.get(path),
                       'Target': value if kind == 'SymbolicLink' else None,
                       'Failures': failures, 'Status': 'Incompatible' if failures else 'Supported'})
    return report


def preflight_files(view, manifest, plan, parameters, baseline):
    """Same review as offline diagnostics, then fresh no-follow mounted readback."""
    report = compatibility(manifest, plan, parameters, baseline)
    require(not any(row['Failures'] for row in report), 'ConfigurationPlanIncompatible')
    failures = []
    with TargetFiles(view.fd, view.expected[0], view.expected[2]) as target:
        for row in report:
            if row['Operation'] not in ('Utf8File', 'SymbolicLink', 'PreserveLink'): continue
            try:
                require(target.observe(row['Path']) == before_state(row['Before']), 'ConfigurationBeforeStateChanged')
            except (OSError, ValueError): failures.append(row['Path'])
    require(not failures, 'ConfigurationMountedPreflightRejected')
    return report


def helper_commands(plan):
    """Closed declarations shared by canonical dispatch and native contract tests."""
    profile(plan)
    return {
        'Debconf': ('ConfigureIdentity', '/usr/bin/debconf-communicate', ('exim4-config',), 'IdentityDebconf'),
        'Exim': ('ConfigureIdentity', '/usr/sbin/update-exim4.conf', (), 'None'),
        'Tls': ('ConfigureIdentity', '/usr/sbin/make-ssl-cert', ('generate-default-snakeoil',), 'None'),
        'Locale': ('ConfigureLocale', '/usr/sbin/locale-gen', (), 'None'),
        'User': ('ConfigureUser', '/usr/sbin/useradd', ('--create-home', '--user-group', '--no-log-init', '--uid', '1000',
            '--shell', '/bin/bash', '-K', 'SUB_UID_COUNT=0', '-K', 'SUB_GID_COUNT=0', '-K', 'HOME_MODE=0700', plan['Username']), 'None'),
        'Credential': ('ConfigureUser', '/usr/sbin/chpasswd', ('--encrypted',), 'EncryptedPassword'),
        'Sudo': ('ConfigureSudo', '/usr/sbin/usermod', ('--append', '--groups', 'sudo', plan['Username']), 'None')}


def perform(session):
    require(not session.configuration_attempted, 'ConfigurationSingleUse'); session.configuration_attempted = True
    plan = session.declaration['ConfigurationPlan']; profile(plan)
    previous = session.declaration['ConfigurationPredecessor']
    connected = session_import.ConnectedImportView(session); descriptors = []; resources = {}; credential = None; active_step = None
    phase = None
    try:
        view = connected.views['Root']; payload = connected.views['Payload']
        folder = 'configured-root/' + previous['BuildId'] + '/' + previous['DerivationId']
        descriptor = beneath(payload.fd, folder + '/descriptor.json', os.O_RDONLY); descriptors.append(descriptor)
        manifest_fd = beneath(payload.fd, folder + '/root.manifest.json', os.O_RDONLY); descriptors.append(manifest_fd)
        data = session_import.read_bound(descriptor, 4*1024*1024)
        require(digest(data) == previous['DescriptorSha256'], 'ConfigurationDescriptorChanged')
        parameters = session.channel.ask('AuthenticateConfigurationSource', Descriptor=base64.b64encode(data).decode())
        parameters = {k: parameters[k] for k in ('Fstab', 'AptSources')}
        manifest_data = session_import.read_bound(manifest_fd, root.MAX_MANIFEST)
        require(digest(manifest_data) == previous['ManifestSha256'], 'ConfigurationManifestChanged')
        manifest = root.validate_manifest(manifest_data)
        account_metadata = {}
        with TargetFiles(view.fd, view.expected[0], view.expected[2]) as target:
            for path in ACCOUNT_FILES:
                info = target.observe(path); account_metadata[path] = [info['Owner'], info['Group'], info['Mode']]
        baseline = {'Exim': root.read_small(view, '/etc/exim4/update-exim4.conf.conf').decode(),
            'UserDefaults': root.read_small(view, '/etc/default/useradd').decode(),
                    'Debconf': root.read_small(view, '/var/cache/debconf/config.dat').decode(), 'AccountMetadata': account_metadata,
                    **{path: root.read_small(view, path).decode() for path in ACCOUNT_FILES}}
        # Sensitive account state is sent only over the inherited private observer pipe;
        # it is never returned or serialized into durable/public evidence.
        reference = None
        def checkpoint(step, outcome, **evidence):
            nonlocal active_step
            result = session.channel.ask('ConfigurationCheckpoint', Record={'Step': step, 'Outcome': outcome, **evidence})
            active_step = step if outcome == 'IntentDurable' else None
            return result
        phase = 'Baseline'
        checkpoint('Baseline', 'IntentDurable')
        checkpoint('Baseline', 'AppliedAndVerified', **independent(session, view, manifest_fd, 'Baseline', parameters, {}, None))
        account_before(baseline, plan)
        phase = 'ViewSetup'
        # The artifact's empty /mnt is a read-only scaffolding alias, not an ESP.
        # The broker masks /boot with private read-only tmpfs, so an absent
        # /boot/efi is never silently created in the imported filesystem.
        resources = configuration_resources(view, payload)
        preflight_files(view, manifest, plan, parameters, baseline)
        connected.verify(); session.channel.checkpoint({'State': 'IntentDurable', 'Operation': 'ConfigureCore'})
        broker = PackageBroker(session.runtime)
        tool_entries = {e['Path']: e for e in manifest['Entries']}
        commands = helper_commands(plan)
        for step in STEPS[1:]:
            phase = step
            connected.verify()
            # Recheck the full unchanged complement immediately before selecting a
            # helper, rather than trusting an executable hash without its libraries.
            if step != 'Files':
                independent(session, view, manifest_fd, STEPS[STEPS.index(step)-1], parameters, baseline, reference)
            command = None
            if step == 'Files':
                checkpoint(step, 'IntentDurable', Outputs=sorted(files(plan, parameters, baseline['Exim'], baseline['UserDefaults'])))
                connected.verify()
                with TargetFiles(view.fd, view.expected[0], view.expected[2]) as target:
                    for path, (kind, value, mode) in files(plan, parameters, baseline['Exim'], baseline['UserDefaults']).items():
                        entry = tool_entries.get(path)
                        target.apply(path, kind, value, mode, before_state(entry))
            elif step == 'Verify':
                launch = Launch(session.declaration['GenerationId'], session.declaration['PlanSha256'], 'InspectArtifacts', Profile.OBSERVER,
                    '/usr/sbin/visudo', tool_entries['/usr/sbin/visudo']['Sha256'], ('--check', '--strict', '--quiet'), 120, 'None', plan['Hostname'], 'CoreConfiguration')
                command = broker.execute(launch, resources, lambda operation, isolation: checkpoint(step, 'IntentDurable',
                    OperationSha256=operation, IsolationSha256=isolation))
                require(command['State'] == 'Exited' and command['ExitCode'] == 0, 'SudoPolicySyntaxRejected')
            else:
                stage, executable, args, input_kind = commands[step]; input_fd = None
                if step == 'Debconf':
                    input_fd = sealed_copy(('SET exim4/mailname ' + plan['Hostname'] + '\nSET exim4/dc_other_hostnames ' +
                        plan['Hostname'] + '\nFSET exim4/dc_other_hostnames mailname true\n').encode(), 'igloo-public-debconf')
                if step == 'Credential':
                    credential, reference = credential_input(plan['Username']); input_fd = credential
                launch = Launch(session.declaration['GenerationId'], session.declaration['PlanSha256'], stage, Profile.CONFIGURATION,
                    executable, tool_entries[executable]['Sha256'], args, 600, input_kind, plan['Hostname'], 'CoreConfiguration')
                def intent(operation, isolation):
                    checkpoint(step, 'IntentDurable', OperationSha256=operation, IsolationSha256=isolation,
                        ProtectedInputReference=reference if step == 'Credential' else None)
                    connected.verify()
                try: command = broker.execute(launch, resources, intent, input_fd=input_fd)
                finally:
                    if input_fd is not None: os.close(input_fd)
                    credential = None
                require(command['State'] == 'Exited' and command['ExitCode'] == 0, 'ConfigurationHelperFailed:' + step)
            connected.verify()
            # Flush only the owned root filesystem. Helper exit is not persistence.
            require(ctypes.CDLL(None, use_errno=True).syncfs(view.fd) == 0, 'ConfigurationFlushFailed')
            observed = independent(session, view, manifest_fd, step, parameters, baseline, reference)
            result = checkpoint(step, 'AppliedAndVerified', HelperEvidence=command, **observed)
        connected.verify(); session.channel.checkpoint({'State': 'AppliedAndVerified', 'ConfigurationResultReference': result['Reference']})
        return {'Reference': result['Reference'], 'Sha256': result['Sha256'], 'OperationId': plan['OperationId']}
    except (OSError, ValueError, KeyError, subprocess.TimeoutExpired):
        if active_step is not None:
            try:
                session.channel.ask('ConfigurationCheckpoint', Record={'Step': active_step, 'Outcome': 'OutcomeUnknown',
                    'Code': 'ConfigurationPostconditionOrExecutionUnavailable'})
            except (OSError, ValueError, KeyError): pass  # Retained intent still prohibits replay.
        if phase is not None:
            try: session.channel.ask('ConfigurationStopped', Phase=phase)
            except (OSError, ValueError, KeyError): pass
        raise
    finally:
        if credential is not None: os.close(credential)
        for resource in resources.values(): resource.close()
        for fd in descriptors: os.close(fd)
        connected.close()
