"""Partial synthetic fixtures. Wire input must come from DebianUserData.Serialize."""
import base64
import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch

sys.path.insert(0,os.environ.get('IGLOO_USERDATA_TOOLS',str(Path(__file__).resolve().parents[2]/'distros/debian/native')))
import userdata_producer as u
import userdata_rehearsal as rehearsal


@unittest.skipUnless(os.environ.get('IGLOO_USERDATA_WIRE'), 'Actual .NET wire fixture required')
class WireTests(unittest.TestCase):
    def setUp(self):
        wire=Path(os.environ['IGLOO_USERDATA_WIRE'])
        self.raw=(wire/'plan.json').read_bytes();self.plan=u.validate(self.raw)
        self.data=json.loads((wire/'data.json').read_bytes())

    def test_actual_serializer_mapping(self):
        self.assertEqual('OneDrive/Documents',self.plan['Selection']['folders'][0]['sourceRelativePath'])
        for e in self.plan['Entries']:
            self.assertEqual('Documents'+e['Source'][len('OneDrive/Documents'):],e['Destination'])
        self.assertEqual(4,sum(e['Kind']=='File' for e in self.plan['Entries']))

    def test_wrong_bindings_and_unsupported_items(self):
        for change in ('generation','scope','home','uid','mapping','bytes','metadata','source','unknown','unicode-collision','case-collision','link','empty'):
            with self.subTest(change=change):
                p=copy.deepcopy(self.plan)
                if change=='generation':p['GenerationId']='00000000-0000-0000-0000-000000000000'
                if change=='scope':p['Scope']='Production'
                if change=='home':p['Home']='/root'
                if change=='uid':p['Uid']=True
                if change=='mapping':p['Entries'][-1]['Destination']='Documents/other'
                if change=='bytes':p['Selection']['totalBytes']+=1
                if change=='metadata':p['Entries'][-1]['Mode']=0o4755
                if change=='source':p['Entries'][-1]['Source']='../escape'
                if change=='unknown':p['Entries'][-1]['Source']='OneDrive/Documents/file:stream'
                if change in ('unicode-collision','case-collision'):
                    e=copy.deepcopy(next(e for e in p['Entries'] if 'résumé' in e['Source']))
                    e['Source']=e['Source'].replace('résumé','re\u0301sume\u0301' if change=='unicode-collision' else 'RÉSUMÉ')
                    e['Destination']=e['Destination'].replace('résumé','re\u0301sume\u0301' if change=='unicode-collision' else 'RÉSUMÉ')
                    p['Entries'].append(e);p['Selection']['totalBytes']+=e['Length']
                if change=='link':p['Entries'][-1]['Kind']='Link'
                if change=='empty':p['Entries']=[]
                with self.assertRaises((ValueError,KeyError,TypeError)):u.validate(u.canonical(p))


@unittest.skipUnless(os.environ.get('IGLOO_USERDATA_NATIVE_UNIT')=='1', 'Isolated fixture runtime required')
class FileTests(WireTests):
    def setUp(self):
        super().setUp()
        root=Path('/var/lib/igloo/userdata-unit-fixtures');root.mkdir(mode=0o700,parents=True,exist_ok=True)
        self.base=Path(tempfile.mkdtemp(prefix='unit-',dir=root));self.scope=patch.object(u,'BASE',self.base);self.scope.start()
        self.directory=rehearsal.prepare(self.raw,self.data)
        self.addCleanup(self.scope.stop)  # retain all small fixture evidence, including failures

    def context(self):
        c=u.FixtureContext(self.raw,self.directory);self.addCleanup(c.close);return c

    def test_actual_transfer_observer_and_reopen(self):
        c=self.context();result=u.perform(c)
        self.assertEqual(4,result['Files']);self.assertEqual(3,result['ReopenedRecords'])
        self.assertTrue(result['SourceUnchanged']);self.assertTrue(result['UnrelatedDestinationUnchanged'])
        with self.assertRaises(ValueError):u.perform(c)
        with self.assertRaises(ValueError):self.context()  # existing selected tree cannot be replayed

    def test_wrong_account(self):
        p=self.directory/'target/etc/passwd';p.write_text(p.read_text().replace(':1000:1000:',':1001:1000:'))
        with self.assertRaises(ValueError):self.context()

    def test_rehashed_intent_substitution_rejected(self):
        c=self.context();u.perform(c)
        directory=self.directory/'journal'/self.plan['OperationId']
        path=next(directory.glob('00000000-*.json'));value=json.loads(path.read_bytes())
        value['SourceIdentity'][1]+=1
        raw=u.canonical(value);replacement=directory/f'00000000-{u.digest(raw)}.json'
        replacement.write_bytes(raw);replacement.chmod(0o600);path.unlink()
        with self.assertRaises(ValueError):u.reopen({'Plan':base64.b64encode(self.raw).decode(),'StoreFd':c.store.fd,'StoreIdentity':u.identity(c.store.fd)})

    def test_changed_plan(self):
        c=self.context();c.plan['GenerationId']=str(uuid.uuid4())
        with self.assertRaises(ValueError):u.perform(c)
        self.assertFalse((self.directory/'target/home/iglootest/Documents').exists())

    def test_conflict(self):
        (self.directory/'target/home/iglootest/Documents').mkdir()
        with self.assertRaises(ValueError):self.context()

    def test_source_mutation_and_substitution(self):
        c=self.context();path=self.directory/'source/OneDrive/Documents/empty.txt';path.unlink();path.write_bytes(b'')
        with self.assertRaises(ValueError):u.perform(c)
        self.assertFalse((self.directory/'target/home/iglootest/Documents').exists())

    def test_link_escape(self):
        path=self.directory/'source/OneDrive/Documents/empty.txt';path.unlink();path.symlink_to('/etc/passwd')
        with self.assertRaises(ValueError):self.context()

    def test_home_substitution(self):
        c=self.context();home=self.directory/'target/home/iglootest';home.rename(home.with_name('old'));home.symlink_to('old')
        with self.assertRaises(ValueError):u.perform(c)

    def test_privileged_metadata_rejected(self):
        path=self.directory/'source/OneDrive/Documents/empty.txt';path.chmod(0o4600)
        with self.assertRaises(ValueError):self.context()

    def test_secret_and_xattr_rejected(self):
        path=self.directory/'source/OneDrive/Documents/empty.txt';path.write_bytes(b'-----BEGIN PRIVATE KEY-----')
        with self.assertRaises(ValueError):self.context()
        path.write_bytes(b'');os.setxattr(path,'user.fixture',b'unsupported')
        with self.assertRaises(ValueError):self.context()

    def test_intent_failure_blocks_copy(self):
        c=self.context();original=u.journal.perform
        def fail(*args,**kwargs):
            if args[3]=='append':raise OSError('injected')
            return original(*args,**kwargs)
        with patch.object(u.journal,'perform',side_effect=fail),self.assertRaises(OSError):u.perform(c)
        self.assertFalse((self.directory/'target/home/iglootest/Documents').exists())

    def test_intent_reopen_failure_blocks_copy(self):
        c=self.context();original=u.journal.perform
        def fail(*args,**kwargs):
            if args[3]=='read':raise OSError('injected')
            return original(*args,**kwargs)
        with patch.object(u.journal,'perform',side_effect=fail),self.assertRaises(OSError):u.perform(c)
        self.assertFalse((self.directory/'target/home/iglootest/Documents').exists())

    def test_short_write_keeps_unknown_without_receipt(self):
        c=self.context()
        with patch.object(u.os,'write',return_value=0),self.assertRaises(ValueError):u.perform(c)
        self.assertFalse((self.directory/'journal/userdata-receipt.json').exists())
        records=list((self.directory/'journal'/self.plan['OperationId']).glob('*.json'))
        self.assertEqual('OutcomeUnknown',json.loads(sorted(records)[-1].read_bytes())['State'])

    def test_write_io_failure(self):
        c=self.context()
        with patch.object(u.os,'write',side_effect=OSError('injected')),self.assertRaises(OSError):u.perform(c)
        self.assertFalse((self.directory/'journal/userdata-receipt.json').exists())

    def test_short_source_copy(self):
        c=self.context();original=u.beneath;pread=u.os.pread;copying=False
        def opened(fd,name,flags,mode=0):
            nonlocal copying
            result=original(fd,name,flags,mode)
            if name=='binary.bin' and flags & os.O_WRONLY:copying=True
            return result
        with patch.object(u,'beneath',side_effect=opened),patch.object(u.os,'pread',side_effect=lambda *args:b'' if copying else pread(*args)),self.assertRaises(ValueError):u.perform(c)
        self.assertFalse((self.directory/'journal/userdata-receipt.json').exists())

    def test_observer_failure_cannot_publish(self):
        c=self.context()
        with patch.object(u,'independent',side_effect=ValueError('injected')),self.assertRaises(ValueError):u.perform(c)
        self.assertFalse((self.directory/'journal/userdata-receipt.json').exists())

    def test_successful_copy_but_wrong_owner_rejected(self):
        c=self.context();original=u.independent
        def changed(context):
            os.chown(self.directory/'target/home/iglootest/Documents/empty.txt',0,0)
            return original(context)
        with patch.object(u,'independent',side_effect=changed),self.assertRaises(ValueError):u.perform(c)
        self.assertFalse((self.directory/'journal/userdata-receipt.json').exists())

    def test_result_persistence_failure(self):
        c=self.context();original=u.journal.perform
        def fail(*args,**kwargs):
            if args[3]=='append' and json.loads(args[4])['State']=='AppliedAndVerified':raise OSError('injected')
            return original(*args,**kwargs)
        with patch.object(u.journal,'perform',side_effect=fail),self.assertRaises(OSError):u.perform(c)
        self.assertFalse((self.directory/'journal/userdata-receipt.json').exists())

    def test_persistent_reservation_rejects_concurrent_context(self):
        first=self.context();second=self.context();original=u.journal.perform
        def fail(*args,**kwargs):
            if args[3]=='append':raise OSError('injected')
            return original(*args,**kwargs)
        with patch.object(u.journal,'perform',side_effect=fail),self.assertRaises(OSError):u.perform(first)
        with self.assertRaises(FileExistsError):u.perform(second)

    def test_receipt_failure_invalidates_terminal_chain(self):
        c=self.context();original=u.journal.durable_create
        def fail(fd,name,data):
            if name=='userdata-receipt.json':raise OSError('injected')
            return original(fd,name,data)
        with patch.object(u.journal,'durable_create',side_effect=fail),self.assertRaises(OSError):u.perform(c)
        with self.assertRaises(ValueError):u.reopen({'Plan':base64.b64encode(self.raw).decode(),'StoreFd':c.store.fd,'StoreIdentity':u.identity(c.store.fd)})


if __name__=='__main__':unittest.main()
