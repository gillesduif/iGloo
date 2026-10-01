"""Compose configured+initramfs predecessor with the exact selected-data delta.

Read-only verification only. No producer authority, copying or receipt synthesis.
The supplied predecessor observations must come from independently reopened
successful canonical chains, including Close, before this observer is invoked.
"""
import copy

import configured_root as root
import configured_successor
from isolation_policy import canonical, digest, require
from userdata_contract import validate, sha, INPUT_EVIDENCE, INPUT_RECEIPT, validate_admitted_bundle
from debian_first_boot import INPUT_ROOT

IMAGE_PATH = '/boot/initrd.img-6.12.107+deb13-amd64'


def file_entry(path, length, content, uid=0, gid=0, mode=0o644):
    return {'Path': path, 'Type': 'File', 'Uid': uid, 'Gid': gid, 'Mode': mode,
            'Length': length, 'Sha256': content, 'Target': None, 'Xattrs': {}}


def directory_entry(path, uid=0, gid=0, mode=0o755):
    return {'Path': path, 'Type': 'Directory', 'Uid': uid, 'Gid': gid, 'Mode': mode,
            'Length': None, 'Sha256': None, 'Target': None, 'Xattrs': {}}


def compose_predecessor(view, original, configured, initramfs):
    require(initramfs['Step'] == 'Verify' and initramfs['CompleteImageQualification'] is True and
        initramfs['ConfiguredEntriesSha256'] == configured['FilesystemDeltaSha256'] and
        initramfs['PackageStateSha256'] == configured['PackageStateSha256'], 'UserDataInitramfsPredecessorRequired')
    image = initramfs['Image']
    require(type(image['Length']) is int and 0 < image['Length'] <= 536870912 and sha(image['Sha256']) and
        initramfs['ImageSemantics']['ImageSha256'] == image['Sha256'] and
        initramfs['ImageSemantics']['ImageLength'] == image['Length'], 'UserDataImagePredecessorChanged')
    baseline = configured_successor.reconstruct_manifest(view, original, configured)
    require(not any(e['Path'] == IMAGE_PATH for e in baseline['Entries']), 'UserDataImageAlreadyInConfiguredBaseline')
    baseline['Entries'].append(file_entry(IMAGE_PATH, image['Length'], image['Sha256'], mode=0o600))
    baseline['Entries'].sort(key=lambda e: e['Path'])
    return baseline


def predecessor_manifest(view, original, configured, initramfs):
    baseline = compose_predecessor(view, original, configured, initramfs)
    root.verify_tree(view, baseline)
    require(root.verify_dpkg(view, original) == configured['PackageStateSha256'], 'UserDataPredecessorPackagesChanged')
    return baseline


def expected_delta(baseline, transfer, admission=None):
    plan = validate(transfer); expected = copy.deepcopy(baseline)
    entries = {e['Path']: e for e in expected['Entries']}
    home = plan['Home']; before = entries.get(home)
    require(before == directory_entry(home, plan['Uid'], plan['Gid'], 0o700), 'UserDataBaselineHomeChanged')
    added = []
    for e in plan['Entries']:
        path = home + '/' + e['Destination']
        require(path not in entries, 'UserDataBaselineDestinationConflict')
        value = directory_entry(path, plan['Uid'], plan['Gid'], 0o700) if e['Kind'] == 'Directory' else \
            file_entry(path, e['Length'], e['Sha256'], plan['Uid'], plan['Gid'], 0o600)
        entries[path] = value; added.append(path)
    if admission is not None:
        bundle, receipt, authority = admission
        validate_admitted_bundle(bundle, receipt, authority)
        for path in ('/var', '/var/lib', '/var/lib/igloo', str(INPUT_ROOT)):
            if path in entries:
                e = entries[path]
                require(e['Type'] == 'Directory' and e['Uid'] == e['Gid'] == 0 and not e['Mode'] & 0o022 and not e['Xattrs'],
                        'UserDataAdmissionBaselineParent')
            else:
                require(path in ('/var/lib/igloo', str(INPUT_ROOT)), 'UserDataAdmissionSystemParentMissing')
                entries[path] = directory_entry(path); added.append(path)
        for name, raw in ((INPUT_EVIDENCE, bundle), (INPUT_RECEIPT, receipt)):
            path = str(INPUT_ROOT) + '/' + name
            require(path not in entries, 'UserDataAdmissionBaselineConflict')
            entries[path] = file_entry(path, len(raw), digest(raw)); added.append(path)
    expected['Entries'] = sorted(entries.values(), key=lambda e: e['Path'])
    return expected, sorted(added)


def verify_delta(view, baseline, transfer, package_sha256, admission=None):
    expected, added = expected_delta(baseline, transfer, admission)
    root.verify_tree(view, expected)
    require(root.verify_dpkg(view, baseline) == package_sha256, 'UserDataSuccessorPackageStateChanged')
    return {'FilesystemSha256': digest(canonical(expected['Entries'])), 'AddedPaths': added,
        'PackageStateSha256': package_sha256, 'ConfiguredAndInitramfsPreserved': True,
        'MachineIdentity': 'FirstBootPending'}
