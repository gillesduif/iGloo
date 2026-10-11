"""Small isolated-runtime integration fixture. NEVER canonical authority.

The .NET test declaration is synthetic at the ownership boundary. Real FD
leases, copier, journal, protected loader, fresh-process whole-root observers
and admission primitives execute on explicitly created ordinary fixture roots.
No block acquisition, canonical reservation, target or installation is involved.
The synthetic initramfs bytes below test preservation, not image qualification.
"""
import base64
import copy
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys

import configured_root as root
import userdata_producer as producer
import userdata_contract as contract
import userdata_admission as admission
import userdata_successor as successor
import session_userdata as adapter
from target_files import mount_id
from userdata_rehearsal import prepare
from test_userdata_paths import snapshot_manifest, package_fixture


def durable(directory, name, value):
    fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
    try:
        raw = value if isinstance(value, bytes) else contract.canonical(value)
        producer.journal.durable_create(fd, name, raw)
        assert producer.journal.read(fd, name) == raw
    finally: os.close(fd)


class FixtureAdmissionContext:
    """Test-only authority-bound wire composition, not a CanonicalContext.

    Delegates real ordinary-file validation to the fixture primitive. Its outer
    result is permanently fixture-labelled. These synthetic bindings cannot
    satisfy the real canonical supervisor/lease acquisition.
    """
    def __init__(self, context, authority):
        self.context = context; self.authority = authority; self.used = False
    def __getattr__(self, name): return getattr(self.context, name)


def run(stage, evidence):
    stage, evidence = Path(stage), Path(evidence)
    assert os.geteuid() == 0 and not Path('/sys/firmware/efi').exists()
    assert sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo']
    evidence.mkdir(mode=0o700)
    wire = stage/'wire'; raw = (wire/'plan.json').read_bytes(); authority = (wire/'declaration.json').read_bytes()
    p = contract.validate(raw); contract.declaration(authority, raw)
    hashes = {str(f): contract.digest(f.read_bytes()) for f in sorted((stage/'native').glob('*.py'))}
    durable(evidence, 'intent.json', {'Scope': 'FilesystemSuccessorFixtureOnly', 'PlanSha256': contract.digest(raw),
        'ExecutionSha256': contract.digest(contract.canonical(hashes)), 'Tools': hashes, 'CanonicalAuthority': False})
    directory = prepare(raw, json.loads((wire/'data.json').read_bytes())); target = directory/'target'
    target.chmod(0o755); (target/'boot').mkdir(mode=0o755); package_fixture(target)
    context = producer.FixtureContext(raw, directory); bound = FixtureAdmissionContext(context, authority)
    view = root.RootView(context.root.fd, *producer.identity(context.root.fd))
    descriptors = []
    try:
        original = snapshot_manifest(view)
        # The ORIGINAL fixture manifest uses the unchanged artifact schema. The
        # configured fixture changes only the home directory representation.
        original.update(BuildId=p['GenerationId'], Format=root.FORMAT, MachineNeutralPolicy=root.NEUTRAL)
        for e in original['Entries']:
            if e['Type'] != 'File': e['Length'] = 0
        original_raw = contract.canonical(original); root.validate_manifest(original_raw)
        durable(evidence, 'original-fixture-manifest.json', original_raw)
        configured_entries = copy.deepcopy(original['Entries'])
        next(e for e in configured_entries if e['Path'] == p['Home'])['Length'] = None
        configured = {'ProfileStep': 'Verify', 'MachineIdentity': 'FirstBootPending', 'ChangedPaths': [p['Home']],
            'FilesystemDeltaSha256': contract.digest(contract.canonical(configured_entries)),
            'PackageStateSha256': contract.digest(contract.canonical(original['Packages']))}
        image = b'fixture preservation sentinel, not a qualified initramfs\n'
        image_path = target/successor.IMAGE_PATH.lstrip('/'); image_path.write_bytes(image); image_path.chmod(0o600)
        init = {'Step': 'Verify', 'CompleteImageQualification': True,
            'ConfiguredEntriesSha256': configured['FilesystemDeltaSha256'], 'PackageStateSha256': configured['PackageStateSha256'],
            'Image': {'Length': len(image), 'Sha256': contract.digest(image)},
            'ImageSemantics': {'ImageLength': len(image), 'ImageSha256': contract.digest(image)}}
        baseline = successor.predecessor_manifest(view, original, configured, init)
        durable(evidence, 'baseline.json', {'FixtureOnly': True, 'EntriesSha256': contract.digest(contract.canonical(baseline['Entries']))})
        produced = producer._transfer(bound)
        durable(evidence, 'producer-result.json', produced)
        store = Path(context.store.path); records = [f.read_bytes() for f in sorted((store/p['OperationId']).glob('*.json'))]
        receipt = (store/'userdata-receipt.json').read_bytes()
        checked = contract.terminal(raw, records, receipt, authority)
        bundle = contract.canonical({'SchemaVersion': 1, 'Scope': contract.SCOPE,
            'Authority': base64.b64encode(authority).decode(), 'Transfer': base64.b64encode(raw).decode(),
            'Records': [base64.b64encode(r).decode() for r in records]})
        with_exception = False
        try: root.inspect_tree(view)
        except ValueError as error: assert str(error) == 'UnsupportedPathEncoding'; with_exception = True
        assert with_exception  # unchanged default must still reject the successor-only name
        manifest_fd = os.open(evidence/'original-fixture-manifest.json', os.O_RDONLY); descriptors.append(manifest_fd)
        template = contract.decode((wire/'request.json').read_bytes())
        template['ToolHashes'] = hashes; template['EntryPath'] = str(stage/'native/session_entry.py')
        template['ExecutionSha256'] = contract.digest(contract.canonical(hashes))
        template['UserDataPlan']['ExecutionSha256'] = template['ExecutionSha256']
        previous = template['InitramfsPredecessor']; previous['Observation'] = init
        previous['Configured']['Observation'] = configured
        previous['Configured']['Imported']['ManifestSha256'] = contract.digest(original_raw)

        def observe(step, current=context, current_view=view):
            fds = [current.source.fd, current.root.fd, current.home.fd]
            request = {'Step': step, 'Authority': base64.b64encode(authority).decode(), 'RootFd': current_view.fd,
                'RootIdentity': list(current_view.expected), 'ManifestFd': manifest_fd,
                'Producer': {'Plan': base64.b64encode(raw).decode(), 'Authority': base64.b64encode(authority).decode(),
                    'Fds': fds, 'Identities': [producer.identity(f) for f in fds], 'Accounts': current.accounts,
                    'SourceBefore': current.source_before, 'DestinationBefore': current.before}}
            response = subprocess.run([sys.executable, '-I', '-B', template['EntryPath']],
                input=contract.canonical({**template, 'UserDataObservation': request})+b'\n',
                pass_fds=[current_view.fd, manifest_fd, *fds], capture_output=True, timeout=120)
            return adapter.observer_response(response, template, step)

        transferred = observe('Transfer'); durable(evidence, 'transfer-observation.json', transferred)
        intent = admission.declaration(view.fd, bundle, receipt, authority); durable(evidence, 'admission-intent.json', intent)
        admitted = admission.publish(view.fd, bundle, receipt, authority, intent, context.verify)
        durable(evidence, 'admission-result.json', admitted)
        post = observe('Admission'); durable(evidence, 'admission-observation.json', post)
        final = observe('Verify'); durable(evidence, 'final-observation.json', final)
        assert post['FilesystemSha256'] == final['FilesystemSha256'] and final['Admission']['ResultSha256'] == checked['ResultSha256']
        for name, contents in [('plan.json', raw), ('declaration.json', authority), ('bundle.json', bundle), ('receipt.json', receipt)]:
            durable(evidence, name, contents)

        # Fresh small read-only observer subjects; never replay the producer or
        # restore over the successful fixture. Preserve every negative root.
        negatives = []
        for n, case in enumerate(('ExtraAccent','WrongContent','WrongOwner','WrongMode','Missing','ExtraAscii',
                                  'Spelling','Symlink','DirectoryInsteadOfFile','Unrelated','AdmissionBytes')):
            copy_root = directory/f'negative-{n:02d}'; shutil.copytree(target, copy_root, symlinks=True)
            for src in [target, *target.rglob('*')]:
                dst = copy_root/src.relative_to(target); s = src.lstat(); os.chown(dst, s.st_uid, s.st_gid, follow_symlinks=False)
            root_fd = os.open(copy_root, os.O_RDONLY | os.O_DIRECTORY); home_fd = os.open(copy_root/p['Home'].lstrip('/'), os.O_RDONLY | os.O_DIRECTORY)
            from types import SimpleNamespace
            test_context = SimpleNamespace(source=context.source, root=SimpleNamespace(fd=root_fd), home=SimpleNamespace(fd=home_fd),
                source_before=context.source_before, accounts=producer.account(root_fd, p))
            current_home = producer.snapshot(home_fd, True)
            test_context.before = {k:v for k,v in current_home.items() if k in context.before}
            test_view = root.RootView(root_fd, *producer.identity(root_fd))
            accented = copy_root/p['Home'].lstrip('/')/'Documents/résumé.txt'
            try:
                if case == 'ExtraAccent': (accented.parent/'supplément.txt').write_bytes(b'extra')
                if case == 'WrongContent': accented.write_bytes(b'wrong')
                if case == 'WrongOwner': os.chown(accented, 0, 0)
                if case == 'WrongMode': accented.chmod(0o644)
                if case == 'Missing': accented.unlink()
                if case == 'ExtraAscii': (accented.parent/'extra.txt').write_bytes(b'extra')
                if case == 'Spelling': accented.rename(accented.parent/'re\u0301sume\u0301.txt')
                if case == 'Symlink': accented.unlink(); accented.symlink_to('/etc/passwd')
                if case == 'DirectoryInsteadOfFile': accented.unlink(); accented.mkdir()
                if case == 'Unrelated': (copy_root/'var/lib/dpkg/status').write_bytes(b'changed')
                if case == 'AdmissionBytes': (copy_root/str(admission.INPUT_ROOT).lstrip('/')/contract.INPUT_RECEIPT).write_bytes(b'{}')
                try: observe('Verify', test_context, test_view)
                except adapter.ObserverFailure as error:
                    assert error.evidence['Step'] == 'Verify' and error.evidence['ExecutionSha256'] == template['ExecutionSha256']
                    negatives.append({'Case':case, **error.evidence})
                else: raise AssertionError('Unexpected fixture acceptance: '+case)
            finally: test_view.close(); os.close(root_fd); os.close(home_fd)
        durable(evidence, 'negative-observations.json', negatives)
        # Reobserve the untouched successful subject after negative subjects.
        assert observe('Verify')['FilesystemSha256'] == final['FilesystemSha256']
        durable(evidence, 'result.json', {'FixtureOnly': True, 'CanonicalAuthority': False, 'Qualified': True,
            'Files': produced['Files'], 'Bytes': produced['Bytes'], 'SourceUnchanged': produced['SourceUnchanged'],
            'UnrelatedDestinationUnchanged': produced['UnrelatedDestinationUnchanged'], 'ContentAndOwnershipVerified': produced['ContentAndOwnershipVerified'],
            'TransferObservation': True, 'AdmissionObservation': True, 'FinalObservation': True,
            'ExecutionSha256': template['ExecutionSha256'], 'ResultSha256': checked['ResultSha256'],
            'FilesystemSha256': final['FilesystemSha256'], 'NegativeCases':len(negatives), 'NativeTargetOrBootQualification':False})
    finally:
        for fd in descriptors: os.close(fd)
        view.close(); context.close()
