"""Read-only reconstruction of a durably observed v5 configured baseline.

The caller must reopen and authenticate the predecessor chain first. These helpers
do not confer storage ownership or authorize another configuration operation.
Secret-bearing files are hashed through the existing observer, never exported.
"""
import copy
import stat

import configured_root as root
from isolation_policy import canonical, digest, require, sha
from session_configuration import entries


def reconstruct_manifest(view, original, observation):
    require(observation.get('ProfileStep') == 'Verify' and
            observation.get('MachineIdentity') == 'FirstBootPending' and
            sha(observation.get('FilesystemDeltaSha256')) and
            sha(observation.get('PackageStateSha256')), 'ConfiguredPredecessorObservationRequired')
    paths = observation.get('ChangedPaths')
    require(type(paths) is list and paths == sorted(set(paths)) and 0 < len(paths) <= 256 and
            all(type(p) is str and p.startswith('/') for p in paths), 'ConfiguredPredecessorPathsInvalid')
    actual = entries(view)
    require(set(paths) <= set(actual), 'ConfiguredPredecessorOutputMissing')
    expected = copy.deepcopy(original)
    expected['Entries'] = [e for e in original['Entries'] if e['Path'] not in paths]
    for path in paths:
        info, target, attrs, content_hash = actual[path]
        require(not attrs and (stat.S_ISDIR(info.st_mode) or info.st_nlink == 1),
                'ConfiguredPredecessorMetadataChanged')
        require(stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode),
                'ConfiguredPredecessorObjectType')
        kind = 'Directory' if stat.S_ISDIR(info.st_mode) else 'SymbolicLink' if stat.S_ISLNK(info.st_mode) else 'File'
        expected['Entries'].append({'Path': path, 'Type': kind, 'Uid': info.st_uid, 'Gid': info.st_gid,
            'Mode': stat.S_IMODE(info.st_mode), 'Length': info.st_size if kind == 'File' else None,
            'Sha256': content_hash if kind == 'File' else None,
            'Target': target if kind == 'SymbolicLink' else None, 'Xattrs': {}})
    expected['Entries'].sort(key=lambda e: e['Path'])
    # The expected digest comes from the independently reopened predecessor, not
    # from this target. In particular, changed credentials/keys cannot be adopted.
    require(digest(canonical(expected['Entries'])) == observation['FilesystemDeltaSha256'],
            'ConfiguredPredecessorDeltaChanged')
    return expected


def configured_manifest(view, original, observation):
    expected = reconstruct_manifest(view, original, observation)
    root.verify_tree(view, expected)
    require(root.verify_dpkg(view, original) == observation['PackageStateSha256'],
            'ConfiguredPredecessorPackageStateChanged')
    return expected
