"""Synthetic account rows test the real observer; no target helper is executed."""
import copy
import stat
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import test_configuration_continuation as boundary
import session_configuration as config


class ConfigurationAccountTests(unittest.TestCase):
    def fixture(self, step):
        baseline = {
            '/etc/passwd': 'root:x:0:0:root:/root:/bin/bash\n',
            '/etc/shadow': 'root:!:20000:0:99999:7:::\n',
            '/etc/group': 'root:x:0:\nsudo:x:27:\n',
            '/etc/gshadow': 'root:*::\nsudo:*::\n',
            'AccountMetadata': {p: [0, 42 if p.endswith('shadow') else 0,
                0o640 if p.endswith('shadow') else 0o644] for p in config.ACCOUNT_FILES}}
        # Deliberately synthetic, never an actual credential or usable login hash.
        credential = '$6$fixture$synthetic'
        created = {
            '/etc/passwd': 'iglootest:x:1000:1000::/home/iglootest:/bin/bash\n',
            '/etc/shadow': 'iglootest:!:20000:0:99999:7:::\n',
            '/etc/group': 'iglootest:x:1000:\n', '/etc/gshadow': 'iglootest:!::\n'}
        content = {p: baseline[p] + created[p] for p in config.ACCOUNT_FILES}
        for p in config.ACCOUNT_FILES: content[p + '-'] = baseline[p]
        if step >= 7:
            content['/etc/shadow-'] = content['/etc/shadow']
            content['/etc/shadow'] = content['/etc/shadow'].replace('iglootest:!:', 'iglootest:' + credential + ':')
        if step >= 8:
            for p in ('/etc/group', '/etc/gshadow'):
                content[p + '-'] = content[p]
                content[p] = content[p].replace('sudo:x:27:\n', 'sudo:x:27:iglootest\n').replace('sudo:*::\n', 'sudo:*::iglootest\n')
        actual = {}
        for p, text in content.items():
            uid, gid, mode = baseline['AccountMetadata'][p.rstrip('-')]
            actual[p] = (SimpleNamespace(st_uid=uid, st_gid=gid, st_mode=stat.S_IFREG | mode,
                st_nlink=1, st_size=len(text)), None, {}, None)
        return baseline, content, actual, config.digest(credential.encode())

    def verify(self, step, baseline, content, actual, reference):
        with patch.object(config.root, 'read_small', side_effect=lambda _, p: content[p].encode()):
            config.verify_accounts(None, actual, boundary.ConfigurationBoundaryTests().plan(), baseline, step, reference)

    def test_user_credential_sudo_and_step_relative_backups(self):
        for step in (6, 7, 8, 9):
            with self.subTest(step=step): self.verify(step, *self.fixture(step))

    def test_account_or_backup_system_identity_changes_are_rejected(self):
        for path in ('/etc/passwd', '/etc/passwd-', '/etc/group', '/etc/group-'):
            baseline, content, actual, reference = self.fixture(8)
            content[path] = content[path].replace('root:', 'unexpected:')
            with self.subTest(path=path), self.assertRaises(ValueError):
                self.verify(8, baseline, content, actual, reference)

    def test_credential_substitution_and_root_unlock_are_rejected(self):
        baseline, content, actual, reference = self.fixture(7)
        with self.assertRaisesRegex(ValueError, 'CredentialReadbackMismatch'):
            self.verify(7, baseline, content, actual, '0' * 64)
        content['/etc/shadow'] = content['/etc/shadow'].replace('root:!:', 'root::')
        with self.assertRaisesRegex(ValueError, 'ExistingSystemAccountChanged'):
            self.verify(7, baseline, content, actual, reference)

    def test_account_database_metadata_and_sudo_membership_are_exact(self):
        baseline, content, actual, reference = self.fixture(8)
        wrong = copy.deepcopy(actual); wrong['/etc/shadow'][0].st_mode = stat.S_IFREG | 0o644
        with self.assertRaisesRegex(ValueError, 'ConfigurationOutputMetadata'):
            self.verify(8, baseline, content, wrong, reference)
        content['/etc/group'] = content['/etc/group'].replace('sudo:x:27:iglootest', 'sudo:x:27:other,iglootest')
        with self.assertRaisesRegex(ValueError, 'ExistingSystemAccountChanged'):
            self.verify(8, baseline, content, actual, reference)


if __name__ == '__main__': unittest.main()
