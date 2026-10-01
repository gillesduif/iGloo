"""Synthetic archive/policy fixtures, not generated-image qualification."""
import copy
from dataclasses import replace
import gzip
from pathlib import Path
import stat
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'distros/debian/native'))
import initramfs_archive as archive
import isolation_policy as policy
import package_broker as broker
from test_debian_isolation import readback


def record(name, data=b'', mode=stat.S_IFREG | 0o644, ino=1, links=1):
    name = name.encode() + b'\0'
    fields = (ino, mode, 0, 0, links, 0, len(data), 0, 0, 0, 0, len(name), 0)
    value = b'070701' + b''.join(f'{n:08x}'.encode() for n in fields) + name
    value += b'\0' * (-len(value) % 4)
    value += data
    return value + b'\0' * (-len(value) % 4)


def image(*records):
    return gzip.compress(b''.join(records) + record('TRAILER!!!'))


class InitramfsArchiveTests(unittest.TestCase):
    def test_early_and_compressed_segments_are_fully_read(self):
        early = record('kernel', mode=stat.S_IFDIR | 0o755) + record('TRAILER!!!')
        result = archive.read_image(early + b'\0' * 512 + image(record('init', b'fixture', stat.S_IFREG | 0o755)))
        self.assertEqual(b'fixture', result['init'].data)
        self.assertIn('kernel', result)

    def test_corruption_truncation_and_trailing_data_rejected(self):
        good = image(record('init', b'fixture'))
        for bad in (good[:-1], good + b'garbage', good[:12], good[:-8] + b'\0' * 8,
                    image(record('init', b'one'), record('init', b'two'))):
            with self.subTest(length=len(bad)), self.assertRaises(ValueError):
                archive.read_image(bad)

    def test_devices_setid_paths_and_escape_rejected_without_extraction(self):
        for entry in (record('../escape'), record('/absolute'), record('a//b'),
                      record('device', mode=stat.S_IFCHR | 0o600),
                      record('setid', mode=stat.S_IFREG | 0o4755),
                      record('link', b'../../outside', stat.S_IFLNK | 0o777),
                      record('link', b'link', stat.S_IFLNK | 0o777)):
            with self.assertRaises(ValueError):
                archive.read_image(image(entry))

    def test_merged_directory_links_and_complete_hardlinks(self):
        data = image(record('usr', mode=stat.S_IFDIR | 0o755),
            record('usr/bin', mode=stat.S_IFDIR | 0o755), record('bin', b'usr/bin', stat.S_IFLNK | 0o777),
            record('usr/bin/busybox', b'fixture', ino=8, links=2), record('usr/bin/sh', ino=8, links=2))
        values = archive.read_image(data)
        self.assertEqual('usr/bin/sh', archive.resolve(values, 'bin/sh'))
        self.assertEqual(b'fixture', values['usr/bin/sh'].data)
        with self.assertRaisesRegex(ValueError, 'HardlinkSet'):
            archive.read_image(image(record('incomplete', ino=8, links=2)))

    def test_only_exact_authenticated_setid_copy_is_represented_as_data(self):
        import hashlib
        payload = image(record('usr/bin/ntfs-3g', b'authenticated-fixture', stat.S_IFREG | 0o4755))
        expected = {'usr/bin/ntfs-3g': (0, 0, 0o4755, hashlib.sha256(b'authenticated-fixture').hexdigest().upper())}
        self.assertIn('usr/bin/ntfs-3g', archive.read_image(payload, copied_setid=expected))
        for changed in ({}, {'usr/bin/ntfs-3g': (0, 0, 0o4755, '0'*64)},
                        {'usr/bin/ntfs-3g': (1, 0, 0o4755, expected['usr/bin/ntfs-3g'][3])}):
            with self.assertRaises(ValueError): archive.read_image(payload, copied_setid=changed)

    def test_extra_compressed_stream_or_bad_parent_rejected(self):
        with self.assertRaises(ValueError):
            archive.read_image(image(record('one')) + image(record('two')))
        with self.assertRaises(ValueError):
            archive.read_image(image(record('a'), record('a/b')))
        with self.assertRaisesRegex(ValueError, 'AliasedDestination'):
            archive.read_image(image(record('usr', mode=stat.S_IFDIR | 0o755),
                record('usr/bin', mode=stat.S_IFDIR | 0o755), record('bin', b'usr/bin', stat.S_IFLNK | 0o777),
                record('bin/program', b'one'), record('usr/bin/program', b'two')))


class InitramfsViewTests(unittest.TestCase):
    def test_only_closed_generator_can_select_candidate_view(self):
        launch = policy.Launch('d638b5ba-bcef-4da0-88fb-f6a815446343', 'A'*64, 'GenerateInitramfs',
            policy.Profile.CONFIGURATION, '/usr/sbin/mkinitramfs', 'B'*64,
            ('-d', '/var/tmp/config', '-m', 'most', '-r', 'UUID=398f0d87-e17c-46d6-866f-24d10d747006',
             '-c', 'gzip', '-o', '/var/tmp/candidate.img', '6.12.107+deb13-amd64'), view='InitramfsCandidate')
        launch.validate()
        for changed in (replace(launch, executable='/usr/sbin/update-initramfs'),
                        replace(launch, stage='ConfigureUser'), replace(launch, profile=policy.Profile.PACKAGE),
                        replace(launch, input_kind='EncryptedPassword'), replace(launch, arguments=())):
            with self.assertRaises(ValueError): changed.validate()
        self.assertIn('chroot', policy.DENIED)
        self.assertNotIn(21, policy.CAPS[policy.Profile.CONFIGURATION])

    def test_candidate_view_requires_readonly_target_and_separate_workspace(self):
        observed, host, resources = readback()
        resources['workspace'] = resources.pop('esp')
        observed['Paths'] = broker.resource_paths(resources, 'InitramfsCandidate')
        for mount in observed['Mounts']:
            if mount['Path'] == '/': mount['Options'][0] = 'ro'
            if mount['Path'] == '/boot/efi': mount.update(Path='/var/tmp', Options=['rw', 'nosuid', 'nodev'])
        broker.verify_effective(observed, host, resources, policy.Profile.CONFIGURATION, 'InitramfsCandidate')
        for path in ('/', '/var/tmp'):
            changed = copy.deepcopy(observed)
            next(m for m in changed['Mounts'] if m['Path'] == path)['Options'][0] = 'rw' if path == '/' else 'ro'
            with self.assertRaises(ValueError):
                broker.verify_effective(changed, host, resources, policy.Profile.CONFIGURATION, 'InitramfsCandidate')
        changed = copy.deepcopy(observed)
        changed['Mounts'].append({'Id': 99, 'Path': '/boot/efi', 'Options': ['ro', 'nosuid', 'nodev'], 'Propagation': []})
        with self.assertRaises(ValueError):
            broker.verify_effective(changed, host, resources, policy.Profile.CONFIGURATION, 'InitramfsCandidate')


if __name__ == '__main__':
    unittest.main()
