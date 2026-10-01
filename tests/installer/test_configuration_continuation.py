"""Small fixtures: never evidence of native configuration or target readiness."""
import copy
import json
import fcntl
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'distros/debian/native'))
import block_session
import session_configuration as config
from isolation_policy import Launch, Profile


class ConfigurationBoundaryTests(unittest.TestCase):
    def plan(self):
        return {'SchemaVersion': 1, 'TransformationPolicy': config.TRANSFORMATION_POLICY,
                'Provenance': {'Scope': 'CoreConfiguration'}, 'Hostname': 'igloo-lab-config',
                'Username': 'iglootest', 'UserId': 1000, 'Locale': 'en_US.UTF-8', 'Timezone': 'Etc/UTC', 'Keyboard': 'us'}

    def test_profile_rejects_arbitrary_settings_and_import_scope(self):
        config.profile(self.plan())
        for key, value in [('Hostname', '../../outside'), ('Username', 'root'), ('UserId', 0), ('Timezone', '../../etc/shadow'),
                           ('SchemaVersion', 0), ('TransformationPolicy', None), ('Provenance', {'Scope': 'ConfiguredRootImport'})]:
            p = self.plan(); p[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): config.profile(p)

    def test_smoke_and_import_cannot_dispatch_configuration(self):
        for key in ('StorageSmoke', 'LabImport'):
            session = SimpleNamespace(failed=False, closed=False, acquired=True, teardown=False, channel=Mock(), declaration={key: {}})
            with self.assertRaisesRegex(ValueError, 'ConfigurationScopeRequired'):
                block_session.MountSession.perform(session, 'ConfigureCore')
            self.assertTrue(session.failed)

    def test_configuration_cannot_retag_or_mix_provenance(self):
        p = {'Provider': 'IsolatedFileBackedLab', 'Version': 3, 'Scope': 'CoreConfiguration', 'GenerationId': 'original'}
        declaration = {'GenerationId': 'original', 'LabConfiguration': p, 'ConfigurationPlan': {'Provenance': p}}
        self.assertTrue(block_session.root_only(declaration))
        for key, value in [('StorageSmoke', {}), ('LabImport', {}), ('ImportPlan', {}), ('GenerationId', 'new')]:
            d = copy.deepcopy(declaration); d[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): block_session.root_only(d)

    def test_file_scope_does_not_include_boot_machineid_or_packages(self):
        defaults = json.loads(Path(__file__).with_name('configuration_manifest_fixture.json').read_bytes())['PublicFileBytes']['/etc/default/useradd']
        outputs = config.files(self.plan(), {'Fstab': 'fixture', 'AptSources': 'fixture'}, "dc_other_hostnames=''\ndc_mailname_in_oh='false'\n", defaults)
        self.assertEqual(14, len(outputs))
        self.assertNotIn('/etc/default/locale', outputs)
        self.assertEqual(('Utf8File', 'LANG=en_US.UTF-8\n', 0o644), outputs['/etc/locale.conf'])
        self.assertFalse(any(p.startswith(('/boot/', '/var/lib/dpkg/', '/usr/')) or p == '/etc/machine-id' for p in outputs))

    def test_fresh_credentials_are_sealed_single_inputs_without_default(self):
        first, reference = config.credential_input('iglootest'); second, another = config.credential_input('iglootest')
        try:
            self.assertNotEqual(reference, another)
            self.assertEqual(15, fcntl.fcntl(first, fcntl.F_GET_SEALS) & 15)
            self.assertTrue(os.pread(first, 20, 0).startswith(b'iglootest:$6$'))
            with self.assertRaises(OSError): os.write(first, b'x')
        finally: os.close(first); os.close(second)

    def test_identity_debconf_is_closed_to_reviewed_helper(self):
        import uuid
        valid = Launch(str(uuid.uuid4()), 'A'*64, 'ConfigureIdentity', Profile.CONFIGURATION,
                       '/usr/bin/debconf-communicate', 'B'*64, ('exim4-config',), 60, 'IdentityDebconf', 'igloo-lab-config')
        valid.validate()
        from dataclasses import replace
        for fields in ({'executable': '/usr/bin/sh'}, {'arguments': ('other-package',)}, {'hostname': 'bad;command'}, {'profile': Profile.PACKAGE}):
            with self.assertRaises(ValueError): replace(valid, **fields).validate()

    def test_absent_boot_efi_uses_empty_root_scaffold_without_writing_boot(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory); target = base/'root'; payload = base/'payload'
            target.mkdir(); payload.mkdir(); (target/'mnt').mkdir(); (target/'boot').mkdir()
            resources = config.configuration_resources(SimpleNamespace(connected=str(target)), SimpleNamespace(connected=str(payload)))
            try:
                self.assertEqual(str(target/'mnt'), resources['esp'].path)
                self.assertFalse((target/'boot/efi').exists())
                self.assertEqual(resources['root'].mount, resources['esp'].mount)
            finally:
                for resource in resources.values(): resource.close()
            (target/'mnt/unexpected').write_bytes(b'x')
            with self.assertRaisesRegex(ValueError, 'ConfigurationEspScaffoldingChanged'):
                config.configuration_resources(SimpleNamespace(connected=str(target)), SimpleNamespace(connected=str(payload)))

    def test_configuration_view_is_bound_and_cannot_change_package_profile(self):
        import uuid
        from dataclasses import replace
        launch = Launch(str(uuid.uuid4()), 'A'*64, 'ConfigureIdentity', Profile.CONFIGURATION,
            '/usr/bin/true', 'B'*64, (), 60, 'None', 'igloo-lab-config', 'CoreConfiguration')
        launch.validate()
        self.assertNotEqual(launch.public_identity(), replace(launch, view=None).public_identity())
        for values in ({'view': 'arbitrary'}, {'profile': Profile.PACKAGE}, {'hostname': 'host;command'}):
            with self.assertRaises(ValueError): replace(launch, **values).validate()

    def test_existing_account_uid_or_sudo_members_rejected_before_creation(self):
        baseline = {'/etc/passwd': 'root:x:0:0:root:/root:/bin/bash\n',
            '/etc/group': 'root:x:0:\nsudo:x:27:\n', '/etc/shadow': 'root:!:20000:0:99999:7:::\n'}
        config.account_before(baseline, self.plan())
        for key, value in [('/etc/passwd', 'other:x:1000:1000::/home/other:/bin/bash\n'),
                           ('/etc/group', 'other:x:1000:\n')]:
            changed = {**baseline, key: baseline[key] + value}
            with self.assertRaisesRegex(ValueError, 'TargetAccountAlreadyExists'): config.account_before(changed, self.plan())
        with self.assertRaisesRegex(ValueError, 'UnexpectedSudoMembers'):
            config.account_before({**baseline, '/etc/group': 'root:x:0:\nsudo:x:27:unexpected\n'}, self.plan())

    def test_unexpected_helper_paths_fail_before_complement_can_be_redefined(self):
        defaults = json.loads(Path(__file__).with_name('configuration_manifest_fixture.json').read_bytes())['PublicFileBytes']['/etc/default/useradd']
        for path in ('/etc/undeclared', '/usr/bin/backdoor', '/var/lib/dpkg/updates/0001'):
            with patch.object(config, 'entries', return_value={path: None}), patch.object(config.root, 'verify_tree') as verifier:
                with self.assertRaisesRegex(ValueError, 'UndeclaredConfigurationPath'):
                    config.verify_delta(None, {'Entries': []}, self.plan(), {'AptSources': '', 'Fstab': ''}, 'Files',
                        {'Exim': "dc_other_hostnames=''\ndc_mailname_in_oh='false'\n", 'UserDefaults': defaults}, None)
                verifier.assert_not_called()

    def test_failed_or_substituted_independent_observer_never_returns_success(self):
        import json
        session = SimpleNamespace(declaration={'ConfigurationPlan': {'OperationId': 'operation'}, 'PlanSha256': 'A'*64, 'EntryPath': '/protected/entry'},
            runtime=SimpleNamespace(python='/protected/python'))
        view = SimpleNamespace(fd=10, expected=(1, 2, 3))
        for result in (SimpleNamespace(returncode=1, stdout=b''), SimpleNamespace(returncode=0, stdout=json.dumps({
                'OperationId': 'other', 'PlanSha256': 'A'*64, 'RootWitness': [1,2,3]}).encode())):
            with patch.object(config.subprocess, 'run', return_value=result), self.assertRaises(ValueError):
                config.independent(session, view, 11, 'Baseline', {}, {}, None)


if __name__ == '__main__': unittest.main()
