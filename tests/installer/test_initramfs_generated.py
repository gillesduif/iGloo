"""Synthetic index/query rejection tests; not retained-helper evidence."""
from dataclasses import replace
from pathlib import Path
import struct
import stat
import hashlib
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'distros/debian/native'))
from initramfs_generated import index_entries, intel_blocks, font_cache_header, verify_microcode, verify_fonts, verify_library_cache
from initramfs_archive import Member
from isolation_policy import Launch, Profile
from package_broker import PackageBroker


class GeneratedIndexTests(unittest.TestCase):
    def test_observer_rejects_wrong_kernel_and_secret_paths_before_generated_acceptance(self):
        import initramfs_candidate as candidate
        def sha(data): return hashlib.sha256(data).hexdigest().upper()
        kernel = b'CONFIG_BLK_DEV_INITRD=y\nCONFIG_RD_GZIP=y\n'
        manifest={'Entries':[
            {'Path':'/usr/bin/ntfs-3g','Type':'File','Uid':0,'Gid':0,'Mode':0o4755,'Sha256':'A'*64},
            {'Path':'/usr/share/initramfs-tools/init','Sha256':sha(b'fixture-init')},
            {'Path':'/boot/config-'+candidate.RELEASE,'Sha256':sha(kernel)},
            {'Path':'/usr/lib/modules/'+candidate.RELEASE+'/modules.dep','Sha256':sha(b'')}]}
        plan={'Policy':candidate.POLICY,'KernelRelease':candidate.RELEASE,'RootUuid':'fixture'}
        def member(path,data):return Member(path,stat.S_IFREG|0o600,0,0,1,(),data)
        for path,content in (('etc/shadow',b'synthetic-prohibited-fixture'),
                             ('unexpected-key',b'-----BEGIN PRIVATE KEY-----\nsynthetic-fixture')):
            members={path:member(path,content),'init':member('init',b'fixture-init'),
                'conf/initramfs.conf':member('conf/initramfs.conf',candidate.CONFIGURATION),
                'conf/conf.d/root':member('conf/conf.d/root',b'ROOT=UUID=fixture\n')}
            with patch.object(candidate,'read_image',return_value=members):
                with self.assertRaisesRegex(ValueError,'Initramfs(ProhibitedIdentityPath|SecretMaterial)'):
                    candidate.observe_image(b'fixture',manifest,plan,kernel,b'')
                with self.assertRaisesRegex(ValueError,'InitramfsObserverPolicy'):
                    candidate.observe_image(b'fixture',manifest,{**plan,'KernelRelease':'wrong'},kernel,b'')

    def test_library_cache_rejects_truncation_unknown_format_and_appended_material(self):
        path='etc/ld.so.cache'
        for data in (b'', b'glibc-ld.so.cache1.1', b'X'*100, b'glibc-ld.so.cache1.1'+b'\0'*80):
            with self.assertRaises(ValueError):
                verify_library_cache({path:Member(path,stat.S_IFREG|0o644,0,0,1,(),data)},b'','fixture')

    def test_microcode_requires_exact_source_and_complete_selection(self):
        block = bytearray(2048)
        struct.pack_into('<I', block, 32, 2048)
        block = bytes(block)
        source = {'usr/lib/firmware/intel-ucode/test': {'Type': 'File'}}
        path = 'kernel/x86/microcode/GenuineIntel.bin'
        padding = 'kernel/x86/microcode/.enuineIntel.align.0123456789abc'
        members = {path: Member(path, stat.S_IFREG|0o644, 0, 0, 1, (), block),
            padding: Member(padding, stat.S_IFDIR|0o755, 0, 0, 1, (), b'')}
        listing = b'  001/001: sig 0x00000001, pf_mask 0x01, 2025-01-01, rev 0x0001, size 2048\n'
        verify_microcode(members, source, lambda _: block, 'GenuineIntel', listing, listing)
        for changed in (block[:-1], block+b'garbage'):
            with self.assertRaises(ValueError): intel_blocks(changed)
        for bad in (replace(members[path], data=block[:-1]+b'x'), replace(members[path], data=block+block)):
            with self.assertRaises(ValueError):
                verify_microcode({**members,path:bad}, source, lambda _: block, 'GenuineIntel', listing, listing)
        with self.assertRaises(ValueError):
            verify_microcode(members, source, lambda _: block, 'GenuineIntel', listing.replace(b'0x0001,',b'0x0002,'), listing)

    def test_font_cache_scope_size_and_timestamp_are_not_candidate_authority(self):
        name = b'/usr/share/fonts\0'
        data = bytearray(64+len(name))
        struct.pack_into('<IIqq', data, 0, 0xfc02fc04, 9, len(data), 64)
        data[64:] = name
        self.assertEqual(('/usr/share/fonts',0,0), font_cache_header(bytes(data)))
        for bad in (bytes(data[:-1]), bytes(data)+b'secret', bytes(data).replace(b'/usr/share/fonts', b'/etc/ssl/private')):
            with self.assertRaises(ValueError):font_cache_header(bad)
        with self.assertRaises(ValueError):verify_fonts({}, {}, {})

    def test_payload_rejects_substitution_unknown_generated_data_and_changed_link(self):
        from initramfs_candidate import verify_payload
        sha = hashlib.sha256(b'public').hexdigest().upper()
        manifest = {'Entries':[{'Path':'/etc/example','Type':'File','Uid':0,'Gid':0,'Mode':0o644,'Target':None,'Sha256':sha},
            {'Path':'/etc/link','Type':'SymbolicLink','Uid':0,'Gid':0,'Mode':0o777,'Target':'example','Sha256':None}]}
        item=Member('etc/example',stat.S_IFREG|0o644,0,0,1,(),b'public')
        plan = {'RootUuid':'fixture'}
        verify_payload({'etc/example':item},manifest,plan,None)
        for bad in (replace(item,data=b'changed'),replace(item,mode=stat.S_IFREG|0o666),replace(item,path='etc/unknown')):
            with self.assertRaises(ValueError):verify_payload({bad.path:bad},manifest,plan,None)
        bad=Member('etc/link',stat.S_IFLNK|0o777,0,0,1,(),b'/etc/other')
        with self.assertRaises(ValueError):verify_payload({'etc/link':bad,'etc/other':item},manifest,plan,None)

    def test_index_is_bounded_and_all_bytes_accounted(self):
        data = struct.pack('>III', 0xB007F457, 0x20001, 0xc000000c) + b'ext4\0' + struct.pack('>II', 1, 0) + b'kernel/fs/ext4/ext4.ko:\0'
        self.assertEqual({'ext4': ['kernel/fs/ext4/ext4.ko:']}, index_entries(data))
        for bad in (data[:-1], data + b'undisclosed', b'bad'+data[3:], data[:8]+struct.pack('>I', 0xd000000c)+data[12:]):
            with self.assertRaises(ValueError): index_entries(bad)

    def test_font_queries_have_no_general_output_or_mutation_path(self):
        command = Launch('d638b5ba-bcef-4da0-88fb-f6a815446343', 'A'*64, 'InspectArtifacts', Profile.OBSERVER,
            '/usr/bin/fc-cat', 'B'*64, ('--verbose', '/run/igloo-source/'+'a'*32+'-le64.cache-9'), view='CoreConfiguration')
        PackageBroker._validate_font_query(command)
        for bad in (replace(command, executable='/usr/bin/cat'), replace(command, profile=Profile.CONFIGURATION),
                    replace(command, arguments=('--verbose', '/etc/shadow')), replace(command, view=None),
                    replace(command, executable='/usr/bin/fc-match', arguments=('--format','%{file}','arbitrary')),
                    replace(command, executable='/usr/bin/fc-query', arguments=('--format','%{=unparse}','/etc/secret.ttf'))):
            with self.assertRaises(ValueError): PackageBroker._validate_font_query(bad)


if __name__ == '__main__': unittest.main()
