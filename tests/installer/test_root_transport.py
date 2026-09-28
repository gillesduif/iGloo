"""Small physical-file fixtures; not full-artifact/FAT32/canonical qualification."""
import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'distros/debian/native'))
import root_transport as t


def envelope(length=3):
    return dict(Version=1, Type='Chunked', DescriptorSha256='A'*64, ManifestSha256='B'*64,
        BuildId=str(uuid.UUID(int=1)), DerivationId=str(uuid.UUID(int=2)), ContentLength=length,
        ContentSha256=t.digest(b'abc'), ChunkSize=t.CHUNK_SIZE,
        Chunks=[dict(Index=i, Name=f'root.content.{i:04d}', Length=min(t.CHUNK_SIZE, length-i*t.CHUNK_SIZE),
                     Sha256=t.digest(b'abc')) for i in range((length+t.CHUNK_SIZE-1)//t.CHUNK_SIZE)])


class TransportTests(unittest.TestCase):
    def test_native_qualification_envelope_shared_with_dotnet(self):
        data=(Path(__file__).resolve().parents[2]/'docs/architecture/debian-root-chunk-transport-manifest.json').read_bytes()
        self.assertEqual('A093432AE281F4620959B56E35774DC0E5D01C74443DCD33F248DA723B364757',t.digest(data))
        self.assertEqual(4641457180,t.parse(data)['ContentLength'])

    def test_same_descriptor_twice_and_short_reads_rejected(self):
        with tempfile.NamedTemporaryFile() as file:
            file.write(b'abc');file.flush()
            with self.assertRaisesRegex(ValueError,'Alias'):t.LogicalStream([file.fileno()]*2,[3]*2)
            stream=t.as_stream(file.fileno())
            with patch.object(t.os,'pread',return_value=b'a'):
                with self.assertRaisesRegex(ValueError,'ShortRead'):stream.pread(3,0)

    def test_fixed_policy_five_chunks_and_64_bit_length(self):
        value = t.parse(t.canonical(envelope(4641457180)))
        self.assertEqual([1073741824]*4+[346489884], [c['Length'] for c in value['Chunks']])

    def test_strict_envelope_failures(self):
        changes = [('Version', True), ('Version', 2), ('Type', 'SingleFile'), ('ContentLength', -1),
                   ('ContentLength', 1.0), ('ContentLength', 2**63), ('ContentLength', 0),
                   ('ChunkSize', 3), ('BuildId', 'invalid'), ('Unexpected', 1)]
        for key, item in changes:
            with self.subTest(key=key, item=item):
                value = envelope(); value[key] = item
                with self.assertRaises(ValueError): t.parse(t.canonical(value))

    def test_strict_chunk_failures(self):
        for key, item in [('Index', 1), ('Index', True), ('Name', '../x'), ('Name', '/x'), ('Name', 'ROOT.CONTENT.0000'),
                          ('Length', 0), ('Length', 4), ('Length', 3.0), ('Sha256', 'a'*64), ('Unknown', 1)]:
            with self.subTest(key=key, item=item):
                value = envelope(); value['Chunks'][0][key] = item
                with self.assertRaises(ValueError): t.parse(t.canonical(value))

    def test_duplicate_unknown_missing_and_reordered(self):
        value = envelope(t.CHUNK_SIZE+1)
        for chunks in [[], value['Chunks'][:1], value['Chunks']*2, value['Chunks'][::-1], [value['Chunks'][0]]*2]:
            with self.assertRaises(ValueError): t.parse(t.canonical({**value, 'Chunks': chunks}))
        with self.assertRaises(ValueError): t.parse(t.canonical(envelope()).replace(b'"Version":1', b'"Version":1,"Version":1'))
        with self.assertRaises(ValueError): t.parse(b' ' * (t.MAX_MANIFEST+1))

    def test_equal_chunk_hashes_are_legal(self):
        self.assertEqual(2, len(t.parse(t.canonical(envelope(2*t.CHUNK_SIZE)))['Chunks']))

    def test_boundary_reads_and_mutation(self):
        with tempfile.TemporaryDirectory() as path:
            files = [open(Path(path)/str(i), 'w+b') for i in range(3)]
            try:
                for f, data in zip(files, [b'ab', b'cde', b'f']): f.write(data); f.flush()
                stream = t.LogicalStream([f.fileno() for f in files], [2,3,1])
                for offset in range(7):
                    for count in range(7): self.assertEqual(b'abcdef'[offset:offset+count], stream.pread(count, offset))
                with self.assertRaises(ValueError): stream.pread(1, 7)
                with self.assertRaises(ValueError): stream.pread(t.BUFFER+1, 0)
                files[1].seek(0); files[1].write(b'X'); files[1].flush()
                with self.assertRaisesRegex(ValueError, 'Changed'): stream.pread(1,0)
            finally:
                for f in files: f.close()

    def test_sparse_offsets_beyond_uint32(self):
        with tempfile.NamedTemporaryFile() as f:
            f.truncate(2**32+10); f.seek(2**32+2); f.write(b'XYZ'); f.flush()
            stream = t.as_stream(f.fileno())
            self.assertEqual(b'\0XYZ\0', stream.pread(5,2**32+1))

    def test_aggregate_is_not_hash_list_or_updated_chunk_hash(self):
        with tempfile.NamedTemporaryFile() as f:
            f.write(b'bad'); f.flush(); source=t.as_stream(f.fileno())
            with self.assertRaisesRegex(ValueError,'Aggregate'):
                source.verify(t.digest(b'abc'), [{'Sha256':t.digest(b'bad')}])
            with self.assertRaisesRegex(ValueError,'ChunkHash'):
                source.verify(t.digest(b'bad'), [{'Sha256':t.digest(b'abc')}])

    def test_producer_reopen_no_overwrite_and_bound_open(self):
        with tempfile.TemporaryDirectory() as path, tempfile.NamedTemporaryFile() as f:
            f.write(b'abc'); f.flush(); parent=os.open(path,os.O_DIRECTORY)
            try:
                result=t.produce(f.fileno(),parent,'new',envelope())
                data=(Path(path)/'new/transport.json').read_bytes()
                stream=t.open_chunks(parent,'new',data,t.digest(data),envelope(),t.mount_id(parent))
                try: self.assertEqual(b'abc',stream.pread(3,0))
                finally: stream.close()
                self.assertEqual(result,t.parse(data))
                with self.assertRaises(FileExistsError): t.produce(f.fileno(),parent,'new',envelope())
                with self.assertRaisesRegex(ValueError,'Binding'):
                    t.open_chunks(parent,'new',data,t.digest(data),{**envelope(),'ContentSha256':'C'*64},t.mount_id(parent))
                with self.assertRaisesRegex(ValueError,'WrongMount'):
                    t.open_chunks(parent,'new',data,t.digest(data),envelope(),0)
            finally: os.close(parent)

    def test_fsync_failure_leaves_unpublished_evidence(self):
        with tempfile.TemporaryDirectory() as path, tempfile.NamedTemporaryFile() as f:
            f.write(b'abc'); f.flush(); parent=os.open(path,os.O_DIRECTORY)
            try:
                with patch.object(t.os,'fsync',side_effect=OSError('injected')):
                    with self.assertRaises(OSError): t.produce(f.fileno(),parent,'partial',envelope())
                self.assertTrue((Path(path)/'partial/root.content.0000').exists())
                self.assertFalse((Path(path)/'partial/transport.json').exists())
            finally: os.close(parent)

    def test_capacity_accounts_for_aggregate_and_reserve(self):
        required=t.required_capacity([t.CHUNK_SIZE]*4+[346489884,30779391,672873,2000],4096,1024**2)
        self.assertGreater(required,4641457180+30779391+672873+t.RESERVE)
        self.assertGreater(required,4*1024**3)
        with self.assertRaises(ValueError): t.required_capacity([2**32],4096,0)
        with self.assertRaises(ValueError): t.required_capacity([1],1000,0)

    def test_path_and_length_substitution(self):
        with tempfile.TemporaryDirectory() as path:
            parent=os.open(path,os.O_DIRECTORY)
            try:
                (Path(path)/'root.content.0000').symlink_to('/etc/passwd')
                data=t.canonical(envelope())
                with self.assertRaises(OSError): t.open_chunks(parent,'.',data,t.digest(data),envelope(),t.mount_id(parent))
                (Path(path)/'root.content.0000').unlink(); (Path(path)/'root.content.0000').write_bytes(b'abcd')
                with self.assertRaisesRegex(ValueError,'Length'): t.open_chunks(parent,'.',data,t.digest(data),envelope(),t.mount_id(parent))
            finally: os.close(parent)


if __name__ == '__main__': unittest.main()
