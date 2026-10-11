import copy
import unittest

from checkpoint_readonly_inspection import MountRejected, require_mount


PATH = '/run/igloo-inspection/root'
IMAGE = '/var/lib/igloo/config-checkpoints/import-daa72a8f-20260929/target.raw'
EXPECTED = dict(path=PATH, image=IMAGE, image_identity=(2096, 77587, 42949672960, 123),
                namespace=2229, offset=9933160448, size=21474836480)


def valid():
    selected = {'Id': 618, 'Parent': 596, 'Device': '7:0', 'Root': '/', 'Path': PATH,
                'Options': ['ro', 'nosuid', 'nodev', 'noexec', 'relatime'], 'Propagation': [],
                'FileSystem': 'ext4', 'SuperOptions': ['ro', 'norecovery']}
    return {'Namespace': 2229, 'ParentNamespace': 1111, 'Mounts': [selected],
            'MountSource': '/dev/loop0', 'MountId': 618, 'StatDevice': 1792,
            'ImageIdentity': list(EXPECTED['image_identity']),
            'Loop': {'Source': '/dev/loop0', 'BackingFile': IMAGE, 'Device': '7:0',
                     'SourceDevice': '7:0', 'StatDevice': 1792, 'ReadOnly': True,
                     'Offset': EXPECTED['offset'], 'SizeLimit': EXPECTED['size']}}


class CheckpointMountObservationTests(unittest.TestCase):
    def test_kernel_norecovery_is_the_supported_noload_observation(self):
        result = require_mount(valid(), **EXPECTED)
        self.assertIsNone(result['FailedPredicate'])
        self.assertTrue(all(result['Predicates'].values()))

    def test_each_independent_boundary_rejects_with_its_own_predicate(self):
        cases = [
            ('Namespace', lambda o: o.update(Namespace=1111)),
            ('Namespace', lambda o: o.update(ParentNamespace=2229)),
            ('ReadOnlyMount', lambda o: o['Mounts'][0]['Options'].remove('ro')),
            ('ReadOnlyMount', lambda o: o['Mounts'][0]['Options'].append('rw')),
            ('NoJournalReplay', lambda o: o['Mounts'][0]['SuperOptions'].remove('norecovery')),
            ('Ext4MountRoot', lambda o: o['Mounts'][0].update(FileSystem='tmpfs')),
            ('Ext4MountRoot', lambda o: o['Mounts'][0].update(Root='/subtree')),
            ('MountDeviceAndDescriptor', lambda o: o['Mounts'][0].update(Device='7:1')),
            ('MountDeviceAndDescriptor', lambda o: o.update(MountId=619)),
            ('LoopReadOnly', lambda o: o['Loop'].update(ReadOnly=False)),
            ('LoopSourceAndExtent', lambda o: o['Loop'].update(BackingFile='/wrong.raw')),
            ('LoopSourceAndExtent', lambda o: o['Loop'].update(Offset=0)),
            ('ImageIdentity', lambda o: o.update(ImageIdentity=[2096, 77588, 42949672960, 123])),
            ('PrivatePropagation', lambda o: o['Mounts'][0].update(Propagation=['shared:1'])),
            ('NoChildOrStackedMount', lambda o: o['Mounts'].append({**o['Mounts'][0],
                'Id': 619, 'Path': PATH + '/child', 'Device': '7:1'})),
            ('OneExactMount', lambda o: o['Mounts'].append({**o['Mounts'][0], 'Id': 619})),
            ('NoDeviceAlias', lambda o: o['Mounts'].append({**o['Mounts'][0],
                'Id': 619, 'Path': '/run/alias'})),
        ]
        for code, change in cases:
            with self.subTest(code=code, change=change):
                observation = copy.deepcopy(valid()); change(observation)
                with self.assertRaises(MountRejected) as caught:
                    require_mount(observation, **EXPECTED)
                self.assertEqual(code, caught.exception.code)
                self.assertFalse(caught.exception.observation['Predicates'][code])
                self.assertEqual(code, caught.exception.observation['FailedPredicate'])


if __name__ == '__main__':
    unittest.main()
