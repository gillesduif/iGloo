"""Synthetic complete chains and tampering; never a native result."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from canonical_initramfs_readback import verify, digest

class InitramfsReadbackTests(unittest.TestCase):
    def run_fixture(self, change=None):
        with tempfile.TemporaryDirectory() as temp:
            base=Path(temp); evidence=base/'evidence'; evidence.mkdir(); journals=base/'journals'
            plan={'OperationId':'operation','Provenance':{'GenerationId':'generation'},'ConfigurationSha256':'A'*64,
                  'ConfigurationCloseSha256':'B'*64,'ConfiguredEntriesSha256':'C'*64,
                  'Candidate':{'Output':'/boot/initrd.img-6.12.107+deb13-amd64'}}
            pre={'Phase':'InitramfsPreflight','Plan':plan,'PlanSha256':'D'*64,'SessionId':'session'}
            (evidence/'initramfs.stdout').write_text(json.dumps(pre)+'\n')
            image={'Length':17,'Sha256':'E'*64}; effects=[]; sessions=[]
            def add(chain, value):
                chain.append({'SchemaVersion':4,'GenerationId':'generation','SessionId':'session','OperationId':'operation',
                    'PlanSha256':'D'*64,'Provenance':plan['Provenance'],'ConfigurationSha256':'A'*64,
                    'ConfigurationCloseSha256':'B'*64,**value})
            for step in ('Baseline','Generation','Candidate','Publication','Verify'):
                pub={'Destination':plan['Candidate']['Output'],'Policy':'CreateNewInitramfsImageV1','Uid':0,'Gid':0,'Mode':384,'Candidate':image}
                add(effects,{'Step':step,'Outcome':'IntentDurable','Bindings':[],'ProtectedStateSha256':'F'*64,
                    'Evidence':{'Publication':pub} if step=='Publication' else {}})
                obs={'OperationId':'operation','PlanSha256':'D'*64,'Step':step,'ConfiguredEntriesSha256':'C'*64,
                     'PackageStateSha256':'F'*64,'Image':image,'CompleteImageQualification':True,
                     'ImageSemantics':{'ImageSha256':image['Sha256'],'ImageLength':image['Length']}}
                add(effects,{'Step':step,'Outcome':'AppliedAndVerified','Bindings':[],'ProtectedStateSha256':'F'*64,
                    'Evidence':{'ObserverEvidence':obs,'HelperEvidence':{'State':'Exited','ExitCode':0},'Publication':image}})
            for action,path,before,after in [('UnmountPayload','/payload',[{'Path':'/root'},{'Path':'/payload'}],[{'Path':'/root'}]),
                                            ('UnmountRoot','/root',[{'Path':'/root'}],[])]:
                add(sessions,{'Action':action,'State':'AppliedAndVerified','Evidence':{'Path':path,'Before':{'Mounts':before},'After':{'Mounts':after}}})
            add(sessions,{'Action':'Close','State':'AppliedAndVerified'})
            effects=copy.deepcopy(effects); sessions=copy.deepcopy(sessions)
            if change: change(effects,sessions)
            last=None
            for store,chain in [('effects',effects),('session',sessions)]:
                dest=journals/store/'generation';dest.mkdir(parents=True);(dest/'plan.sha256').write_text('D'*64); previous=None
                for i,value in enumerate(chain):
                    value.update(Sequence=i,PreviousSha256=previous)
                    if 'ObserverEvidence' in value.get('Evidence',{}):
                        value['Evidence']['ObserverEvidenceSha256']=digest(json.dumps(value['Evidence']['ObserverEvidence'],sort_keys=True,separators=(',',':')).encode())
                    if value.get('Action')=='Close': value['InitramfsResultReference']=last
                    raw=json.dumps(value,separators=(',',':')).encode();previous=digest(raw);name=f'{i:08d}-{previous}.json';(dest/name).write_bytes(raw)
                if store=='effects':last=name
            return verify(evidence,journals)

    def test_complete_synthetic_chain_reopens(self): self.assertEqual('AppliedAndVerified',self.run_fixture()['Teardown'])
    def test_rehashed_contradictory_chains_are_rejected(self):
        changes=[lambda e,s:e[3]['Evidence']['HelperEvidence'].update(ExitCode=True),
                 lambda e,s:e[5]['Evidence']['ObserverEvidence'].update(CompleteImageQualification=1),
                 lambda e,s:e[7]['Evidence']['ObserverEvidence']['Image'].update(Sha256='0'*64),
                 lambda e,s:e[6]['Evidence']['Publication'].update(Mode=420),
                 lambda e,s:e[9]['Evidence']['ObserverEvidence'].update(PackageStateSha256='0'*64),
                 lambda e,s:e.pop(),lambda e,s:s[0]['Evidence']['After']['Mounts'].append({'Path':'/payload'}),
                 lambda e,s:s[-1].update(State='OutcomeUnknown')]
        for change in changes:
            with self.subTest(change=change),self.assertRaises((ValueError,KeyError)):self.run_fixture(change)

if __name__=='__main__':unittest.main()
