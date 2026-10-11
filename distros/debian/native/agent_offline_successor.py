"""Read-only configured+initramfs+UserData baseline and closed agent-file delta.

The caller must supply independently reopened predecessor observations and the
exact admitted UserData bundle/receipt. Nothing here authorizes publication.
"""
import copy
import os

import configured_root as root
import userdata_successor as userdata
from agent_offline import CONFIG, LINK, UNIT, WORKER, digest, require, source
from isolation_policy import canonical
from userdata_contract import SelectedDocumentPaths, validate_admitted_bundle


def compose_predecessor(view, original, configured, initramfs, transfer, bundle, receipt, authority, observed):
    SelectedDocumentPaths(transfer)
    admitted = validate_admitted_bundle(bundle, receipt, authority)
    require(observed['Step'] == 'Verify' and observed['ConfiguredAndInitramfsPreserved'] is True and
            observed['MachineIdentity'] == 'FirstBootPending' and
            observed['PackageStateSha256'] == configured['PackageStateSha256'] and
            all(observed['Admission'][key] == admitted[key] for key in
                ('ResultSha256', 'ReceiptSha256', 'EvidenceSha256', 'AuthoritySha256')),
            'AgentUserDataPredecessorChanged')
    imported = userdata.compose_predecessor(view, original, configured, initramfs, transfer=transfer)
    expected, _ = userdata.expected_delta(imported, transfer, (bundle, receipt, authority))
    require(digest(canonical(expected['Entries'])) == observed['FilesystemSha256'],
            'AgentUserDataPredecessorDeltaChanged')
    return expected


def predecessor(view, original, configured, initramfs, transfer, bundle, receipt, authority, observed):
    expected = compose_predecessor(view, original, configured, initramfs, transfer,
                                   bundle, receipt, authority, observed)
    root.verify_tree(view, expected, selected_documents=SelectedDocumentPaths(transfer))
    require(root.verify_dpkg(view, original) == observed['PackageStateSha256'],
            'AgentUserDataPredecessorPackagesChanged')
    return expected


def expected_delta(baseline, packet):
    plan, worker, unit = source(packet, scope='LabCanonicalOfflineAgentStageV1')
    expected = copy.deepcopy(baseline)
    entries = {item['Path']: item for item in expected['Entries']}
    require(len(entries) == len(expected['Entries']) and
            entries['/usr/lib']['Type'] == entries['/etc/systemd/system']['Type'] == 'Directory' and
            all(path not in entries for path in ('/usr/lib/igloo', WORKER, UNIT, CONFIG, LINK)),
            'AgentCanonicalBeforeStateChanged')
    entries['/usr/lib/igloo'] = userdata.directory_entry('/usr/lib/igloo')
    entries[WORKER] = userdata.file_entry(WORKER, len(worker), digest(worker))
    entries[UNIT] = userdata.file_entry(UNIT, len(unit), digest(unit))
    expected['Entries'] = sorted(entries.values(), key=lambda e: e['Path'])
    return expected, plan, worker, unit


def verify_delta(view, baseline, packet, original, transfer, package_sha256):
    expected, plan, worker, unit = expected_delta(baseline, packet)
    root.verify_tree(view, expected, selected_documents=SelectedDocumentPaths(transfer))
    require(root.verify_dpkg(view, original) == package_sha256,
            'AgentCanonicalPackageStateChanged')
    require(all(path not in {e['Path'] for e in expected['Entries']} for path in (CONFIG, LINK)),
            'AgentPrematureActivation')
    require(root.read_small(view, WORKER, len(worker) + 1) == worker and
            root.read_small(view, UNIT, len(unit) + 1) == unit, 'AgentInstalledBytesChanged')
    return {'FilesystemSha256': digest(canonical(expected['Entries'])),
            'PackageStateSha256': package_sha256,
            'AddedPaths': ['/etc/systemd/system/igloo-deployment.service', '/usr/lib/igloo', WORKER],
            'ConfigurationAbsent': True, 'EnableLinkAbsent': True,
            'UserDataAndConfiguredInitramfsPreserved': True,
            'GenerationId': plan['GenerationId'], 'OperationId': plan['OperationId'],
            'TargetRootId': plan['TargetRootId'], 'Files': plan['Files']}
