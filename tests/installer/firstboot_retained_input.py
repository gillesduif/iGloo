"""Read-only test bridge from protected canonical chains to the .NET input composer.

The emitted bytes are a pending lab input, never a producer receipt or effect
authorization. Invoke as root against the original protected evidence paths.
"""
import base64
import json
from pathlib import Path
import sys

from canonical_initramfs_readback import verify as verify_initramfs
from canonical_userdata_readback import verify as verify_userdata
from canonical_configuration_readback import require
from canonical_configuration_readback import verify as verify_configuration
from canonical_import_readback import verify as verify_import
from userdata_contract import decode
import debian_first_boot as worker


def b64(raw):
    return base64.b64encode(raw).decode('ascii')


def compose(initramfs_evidence, userdata_evidence, import_evidence=None, configuration_evidence=None):
    require((import_evidence is None) == (configuration_evidence is None), 'IncompleteAncestryInputs')
    if import_evidence is not None:
        imported = Path(import_evidence)
        configured = Path(configuration_evidence)
        import_result = verify_import(imported/'provisioning', imported/'journals')
        configuration_result = verify_configuration(configured/'execution', configured/'journals')
        require(import_result['SessionChainVerified'] and import_result['ImportChainVerified'] and
                import_result['ExactTeardownVerified'] and
                configuration_result['Configuration'] == 'AppliedAndVerified' and
                configuration_result['Teardown'] == 'AppliedAndVerified' and
                configuration_result['Predecessor']['ResultSha256'] == import_result['ImportResult']['Sha256'] and
                configuration_result['Predecessor']['CloseSha256'] == import_result['CloseResult']['Sha256'],
                'FirstBootImportConfigurationJoinChanged')
    initramfs = Path(initramfs_evidence)
    userdata = Path(userdata_evidence)
    previous = verify_initramfs(initramfs/'execution', initramfs/'journals')
    current = verify_userdata(userdata/'execution', userdata/'journals')
    require(previous['Initramfs'] == 'AppliedAndVerified' and previous['Teardown'] == 'AppliedAndVerified',
            'InitramfsPredecessorIncomplete')
    if import_evidence is not None:
        require(previous['Plan']['ConfigurationSha256'] == configuration_result['EffectRecords'][-1]['Sha256'] and
                previous['Plan']['ConfigurationCloseSha256'] == configuration_result['SessionRecords'][-1]['Sha256'],
                'FirstBootConfigurationInitramfsJoinChanged')
    require(current['UserData'] == 'AppliedAndVerified' and current['Teardown'] == 'AppliedAndVerified',
            'UserDataPredecessorIncomplete')
    preflight = decode((userdata/'execution/userdata.stdout').read_bytes().splitlines()[0])
    plan = preflight['Plan']
    require(plan['InitramfsSha256'] == previous['EffectRecords'][-1]['Sha256'] and
            plan['InitramfsCloseSha256'] == previous['SessionRecords'][-1]['Sha256'],
            'FirstBootPredecessorJoinChanged')
    generation = plan['Provenance']['GenerationId']
    transfer_result = decode((userdata/'journals/effects'/generation/current['EffectRecords'][3]['Reference']).read_bytes())
    result = transfer_result['Evidence']
    bundle = base64.b64decode(result['Bundle'], validate=True)
    receipt = base64.b64decode(result['Receipt'], validate=True)
    authority = base64.b64decode(decode(bundle)['Authority'], validate=True)
    worker.validate_completion_receipt(worker.decode(receipt), generation, 'UserData')
    require(current['Admission']['ReceiptSha256'] == worker.digest(receipt), 'FirstBootAdmissionChanged')
    return {'Transfer': plan['Transfer'], 'Declaration': b64(authority),
            'Receipt': b64(receipt), 'Evidence': b64(bundle),
            'InitramfsResultSha256': plan['InitramfsSha256'],
            'InitramfsCloseSha256': plan['InitramfsCloseSha256']}


if __name__ == '__main__':
    if len(sys.argv) != 5:
        raise SystemExit('usage: firstboot_retained_input.py IMPORT_EVIDENCE CONFIGURATION_EVIDENCE INITRAMFS_EVIDENCE USERDATA_EVIDENCE')
    print(json.dumps(compose(sys.argv[3], sys.argv[4], sys.argv[1], sys.argv[2]), separators=(',', ':')))
