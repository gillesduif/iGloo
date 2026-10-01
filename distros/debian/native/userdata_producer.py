"""Selected-document producer, separate from the first-boot evidence worker.

The fixture and canonical compositions use separate validated contexts. Directory
leases alone are not source authority. No discovery, overwrite or cleanup.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import unicodedata
import uuid
from target_files import beneath, mount_id
from package_broker import DirectoryLease
from isolation_policy import canonical, digest, require
import deployment_journal as journal
from debian_first_boot import decode, validate_completion_receipt
from userdata_contract import validate, relative, key, terminal, declaration, SCOPE

BASE = Path('/var/lib/igloo/userdata-fixtures')
MAX_BYTES = 32 * 1024 * 1024
FORBIDDEN = {'.ssh', '.gnupg', '.pki', 'passwd', 'shadow', 'group', 'gshadow', 'sudoers'}
FOLDERS = {'Documents', 'Downloads', 'Pictures', 'Desktop', 'Music', 'Videos'}
CONTEXT_FIELDS = ('SourceIdentity','TargetIdentity','HomeIdentity','Accounts','SourceBeforeSha256','DestinationBeforeSha256')



def identity(fd):
    s=os.fstat(fd);return [s.st_dev,s.st_ino,mount_id(fd)]


def file_info(fd):
    s=os.fstat(fd)
    require(stat.S_ISREG(s.st_mode) and s.st_nlink == 1 and s.st_mode & 0o7000 == 0 and
            not os.listxattr(fd) and s.st_size <= MAX_BYTES, 'UserDataUnsupportedFileMetadata')
    h=hashlib.sha256();offset=0;tail=b''
    while data:=os.pread(fd, min(65536, MAX_BYTES+1-offset),offset):
        offset+=len(data);require(offset<=MAX_BYTES,'UserDataReadBound');h.update(data)
        probe=tail+data
        require(not re.search(b'-----BEGIN [A-Z ]*PRIVATE KEY-----',probe), 'UserDataSecretMaterial')
        tail=probe[-128:]
    after=os.fstat(fd)
    require((s.st_dev,s.st_ino,s.st_size,s.st_mtime_ns,s.st_ctime_ns)==
            (after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns,after.st_ctime_ns) and offset==s.st_size, 'UserDataSourceChanged')
    return {'Kind':'File','Length':offset,'Sha256':h.hexdigest().upper(),'Uid':s.st_uid,'Gid':s.st_gid,'Mode':stat.S_IMODE(s.st_mode),'Identity':[s.st_dev,s.st_ino,s.st_mtime_ns,s.st_ctime_ns]}


def snapshot(fd, preserve_links=False):
    result={}; total=0
    def visit(parent,prefix):
        nonlocal total
        for name in sorted(os.listdir(parent)):
            path=relative(prefix+name);s=os.stat(name,dir_fd=parent,follow_symlinks=False)
            require(len(result)<2048,'UserDataTreeBound')
            if stat.S_ISLNK(s.st_mode) and preserve_links:
                result[path]={'Kind':'Link','Target':os.readlink(name,dir_fd=parent),'Uid':s.st_uid,'Gid':s.st_gid,'Mode':stat.S_IMODE(s.st_mode)};continue
            require(stat.S_ISDIR(s.st_mode) or stat.S_ISREG(s.st_mode),'UserDataUnsupportedObject')
            child=beneath(parent,name,os.O_RDONLY | (os.O_DIRECTORY if stat.S_ISDIR(s.st_mode) else 0))
            try:
                require((s.st_dev,s.st_ino)==(os.fstat(child).st_dev,os.fstat(child).st_ino) and not os.listxattr(child),'UserDataSubstitutionOrMetadata')
                if stat.S_ISDIR(s.st_mode):
                    require(s.st_mode & 0o7000 == 0,'UserDataDirectoryMetadata')
                    result[path]={'Kind':'Directory','Length':0,'Sha256':None,'Uid':s.st_uid,'Gid':s.st_gid,'Mode':stat.S_IMODE(s.st_mode)}
                    visit(child,path+'/')
                else:
                    result[path]=file_info(child);total+=result[path]['Length'];require(total<=2*MAX_BYTES,'UserDataAggregateBound')
            finally:os.close(child)
    visit(fd,'');return result


def account(root_fd, p):
    observed={}
    for name in ('passwd','group'):
        fd=beneath(root_fd,'etc/'+name,os.O_RDONLY)
        try:
            s=os.fstat(fd);require(stat.S_ISREG(s.st_mode) and s.st_uid==0 and s.st_mode & 0o7022==0 and s.st_size<65536 and s.st_nlink==1 and not os.listxattr(fd),'UserDataAccountFile')
            raw=os.pread(fd,65536,0);observed[name]={'Sha256':digest(raw),'Identity':[s.st_dev,s.st_ino,s.st_mtime_ns,s.st_ctime_ns]};rows=[r.split(':') for r in raw.decode().splitlines()]
            if name=='passwd':
                matches=[r for r in rows if len(r)==7 and (r[0]==p['Username'] or r[2]==str(p['Uid']))]
                require(len(matches)==1 and matches[0][0]==p['Username'] and matches[0][2:4]==[str(p['Uid']),str(p['Gid'])] and matches[0][5]==p['Home'],'UserDataAccountChanged')
            else:
                matches=[r for r in rows if len(r)==4 and (r[0]==p['Username'] or r[2]==str(p['Gid']))]
                require(len(matches)==1 and matches[0][0]==p['Username'] and matches[0][2]==str(p['Gid']),'UserDataGroupChanged')
        finally:os.close(fd)
    return observed


def expected_destination(p, before):
    expected=dict(before)
    for e in p['Entries']:
        require(e['Destination'] not in expected,'UserDataDestinationConflict')
        expected[e['Destination']]={k:e[k] for k in ('Kind','Length','Sha256')}
        expected[e['Destination']].update(Uid=p['Uid'],Gid=p['Gid'],Mode=0o700 if e['Kind']=='Directory' else 0o600)
    return expected


def context_binding(source, root, home, accounts, source_before, destination_before):
    return {'SourceIdentity':identity(source),'TargetIdentity':identity(root),'HomeIdentity':identity(home),
            'Accounts':accounts,'SourceBeforeSha256':digest(canonical(source_before)),
            'DestinationBeforeSha256':digest(canonical(destination_before))}


def observe(request):
    p=validate(base64.b64decode(request['Plan']));fds=request['Fds']
    require([identity(fd) for fd in fds]==request['Identities'],'UserDataObserverLeaseChanged')
    source,root,home=fds
    h=os.fstat(home);require((h.st_uid,h.st_gid,stat.S_IMODE(h.st_mode))==(p['Uid'],p['Gid'],0o700),'UserDataObserverHomeChanged')
    require(account(root,p)==request['Accounts'],'UserDataObserverAccountChanged')
    require(snapshot(source)==request['SourceBefore'],'UserDataSourceNotPreserved')
    actual=snapshot(home,True)
    for e in p['Entries']:
        if e['Destination'] in actual: actual[e['Destination']].pop('Identity',None)
    require(actual==expected_destination(p,request['DestinationBefore']),'UserDataIndependentDestinationMismatch')
    binding = context_binding(source,root,home,request['Accounts'],request['SourceBefore'],request['DestinationBefore'])
    if request.get('Authority') is not None:
        authority = base64.b64decode(request['Authority'], validate=True); declaration(authority, base64.b64decode(request['Plan']))
        binding['AuthoritySha256'] = digest(authority)
    return {'GenerationId':p['GenerationId'],'OperationId':p['OperationId'],'PlanSha256':digest(base64.b64decode(request['Plan'])),
            'BindingSha256':digest(canonical(binding)),
            'Files':sum(e['Kind']=='File' for e in p['Entries']),'Bytes':sum(e['Length'] for e in p['Entries']),
            'SourceUnchanged':True,'UnrelatedDestinationUnchanged':True,'ContentAndOwnershipVerified':True,
            'DestinationSha256':digest(canonical(snapshot(home,True)))}


def verify_context(self):
    require(digest(self.raw)==self.plan_hash and canonical(self.plan)==canonical(validate(self.raw)),'UserDataPlanChanged')
    for lease in self.leases:lease.verify()
    h=os.fstat(self.home.fd);require((h.st_uid,h.st_gid,stat.S_IMODE(h.st_mode))==(self.plan['Uid'],self.plan['Gid'],0o700),'UserDataHomeChanged')
    require(os.fstat(self.store.fd).st_uid==0 and stat.S_IMODE(os.fstat(self.store.fd).st_mode)==0o700,'UserDataStoreProtectionChanged')
    require(account(self.root.fd,self.plan)==self.accounts,'UserDataAccountChanged')


class FixtureContext:
    """Trusted fixture producer context; never deserialized or a production lease."""
    def __init__(self, raw, directory):
        self.plan=validate(raw);self.raw=raw;self.plan_hash=digest(raw);self.used=False;self.leases=[]
        require(Path(directory)==BASE/self.plan['OperationId'],'UserDataFixtureLocation')
        for parent in [Path(directory),*Path(directory).parents]:
            s=parent.lstat();require(stat.S_ISDIR(s.st_mode) and s.st_uid==0 and s.st_mode & 0o022==0,'UserDataFixtureProtection')
        self.directory=Path(directory)
        try:
            for name in ('source','target','target'+self.plan['Home'],'journal'):
                self.leases.append(DirectoryLease(str(self.directory/name)))
            self.source,self.root,self.home,self.store=self.leases
            require(len({tuple(x.identity) for x in self.leases})==4,'UserDataAliasedLeases')
            h=os.fstat(self.home.fd);require((h.st_uid,h.st_gid,stat.S_IMODE(h.st_mode))==(self.plan['Uid'],self.plan['Gid'],0o700),'UserDataHomeChanged')
            require(os.fstat(self.source.fd).st_uid==0 and os.fstat(self.root.fd).st_uid==0 and stat.S_IMODE(os.fstat(self.store.fd).st_mode)==0o700,'UserDataFixtureOwner')
            self.accounts=account(self.root.fd,self.plan);self.source_before=snapshot(self.source.fd);self.before=snapshot(self.home.fd,True)
            selected={name:value for name,value in self.source_before.items() if any(name==f['sourceRelativePath'] or name.startswith(f['sourceRelativePath']+'/') for f in self.plan['Selection']['folders'])}
            require({e['Source']:{k:e[k] for k in ('Kind','Length','Sha256')} for e in self.plan['Entries']}==
                    {name:{k:v[k] for k in ('Kind','Length','Sha256')} for name,v in selected.items()},'UserDataSelectedInventoryChanged')
            require(not any(key(name.split('/')[0]) in {key(f['name']) for f in self.plan['Selection']['folders']} for name in self.before),'UserDataDestinationConflict')
            require(os.statvfs(self.home.path).f_bavail*os.statvfs(self.home.path).f_frsize>=self.plan['Selection']['totalBytes']+1024*1024,'UserDataCapacity')
        except BaseException:self.close();raise
    def verify(self):
        verify_context(self)
    def close(self):
        for lease in self.leases:lease.close()


def independent(context):
    context.verify();fds=[context.source.fd,context.root.fd,context.home.fd]
    request={'Plan':base64.b64encode(context.raw).decode(),'Fds':fds,'Identities':[identity(f) for f in fds],
             'Accounts':context.accounts,'SourceBefore':context.source_before,'DestinationBefore':context.before}
    authority = getattr(context, 'authority', None)
    if authority is not None: request['Authority'] = base64.b64encode(authority).decode()
    runner = getattr(context, 'run_observer', run_observer)
    observed=runner(request,fds,'--observe');p=context.plan
    binding = context_binding(*fds,context.accounts,context.source_before,context.before)
    if authority is not None: binding['AuthoritySha256'] = digest(authority)
    require(observed['GenerationId']==p['GenerationId'] and observed['OperationId']==p['OperationId'] and observed['PlanSha256']==digest(context.raw) and
            observed['BindingSha256']==digest(canonical(binding)) and
            observed['Files']==sum(e['Kind']=='File' for e in p['Entries']) and observed['Bytes']==p['Selection']['totalBytes'] and
            all(observed[k] is True for k in ('SourceUnchanged','UnrelatedDestinationUnchanged','ContentAndOwnershipVerified')),'UserDataObservationBinding')
    return observed


def run_observer(request, fds, action):
    raw=canonical(request);require(len(raw)<=1024*1024,'UserDataObservationRequestBound')
    result=subprocess.run([sys.executable,'-I','-B',str(Path(__file__).with_name('userdata_entry.py')),action],
        input=raw,pass_fds=fds,capture_output=True,timeout=60,env={'PATH':'/usr/bin:/bin','LC_ALL':'C.UTF-8'},check=False)
    require(result.returncode==0 and 0<len(result.stdout)<16384,'UserDataIndependentObserverUnavailable')
    return decode(result.stdout)


def reopen(request):
    """Fresh-process, terminal-chain check; an earlier success followed by failure is rejected."""
    raw=base64.b64decode(request['Plan']);p=validate(raw);store=request['StoreFd']
    require(identity(store)==request['StoreIdentity'],'UserDataStoreSubstituted')
    directory=beneath(store,p['OperationId'],os.O_RDONLY|os.O_DIRECTORY)
    try:
        require(journal.read(directory,'plan.sha256')==digest(raw).encode(),'UserDataReopenedPlan')
        names=sorted(n for n in os.listdir(directory) if n!='plan.sha256')
        require(len(names)==3,'UserDataIncompleteOrFailedChain')
        records=[]
        for i,name in enumerate(names):
            value=journal.read(directory,name)
            require(name==f'{i:08d}-{digest(value)}.json','UserDataRecordIdentity')
            records.append(value)
        receipt=journal.read(store,'userdata-receipt.json')
        authority = base64.b64decode(request['Authority'], validate=True) if request.get('Authority') is not None else None
        return terminal(raw,records,receipt,authority)
    finally:os.close(directory)


def perform(context):
    require(type(context) is FixtureContext and not context.used,'UserDataFixtureCapabilityOrReplay')
    return _transfer(context)


def _transfer(context):
    # Internal shared copier; only validated composition calls this function.
    require(not context.used, 'UserDataProducerReplay'); context.used=True
    authority = getattr(context, 'authority', None)
    if authority is not None: declaration(authority, context.raw)
    runner = getattr(context, 'run_observer', run_observer)
    p=context.plan;plan_hash=digest(context.raw);op=p['OperationId'];store=context.store
    base={'SchemaVersion':2,'GenerationId':p['GenerationId'],'OperationId':op,'PlanSha256':plan_hash,'Scope':'FixtureOnlySelectedDocuments' if authority is None else SCOPE}
    if authority is not None: base['AuthoritySha256'] = digest(authority)
    def append(value):
        raw=canonical({**base,**value});ref=journal.perform(store.path,op,plan_hash,'append',raw,expected_store=identity(store.fd)).decode()
        require(journal.perform(store.path,op,plan_hash,'read',reference=ref,expected_store=identity(store.fd))==raw,'UserDataJournalReopen')
        return raw,ref
    context.verify()
    journal.perform(store.path,op,plan_hash,'reserve',expected_store=identity(store.fd))
    effects=False
    try:
        intent,intent_ref=append({'State':'IntentDurable',**context_binding(context.source.fd,context.root.fd,context.home.fd,
                context.accounts,context.source_before,context.before)})
        context.verify();require(snapshot(context.source.fd)==context.source_before and snapshot(context.home.fd,True)==context.before,'UserDataBeforeStateChanged')
        for entry in sorted(p['Entries'],key=lambda e:(e['Destination'].count('/'),e['Destination'])):
            context.verify();name=entry['Destination'];parent_name,_,leaf=name.rpartition('/')
            parent=beneath(context.home.fd,parent_name or '.',os.O_RDONLY|os.O_DIRECTORY)
            try:
                effects=True
                if entry['Kind']=='Directory':
                    os.mkdir(leaf,0o700,dir_fd=parent);fd=beneath(parent,leaf,os.O_RDONLY|os.O_DIRECTORY)
                    try:os.fchown(fd,p['Uid'],p['Gid']);os.fchmod(fd,0o700);os.fsync(fd)
                    finally:os.close(fd)
                else:
                    source=beneath(context.source.fd,entry['Source'],os.O_RDONLY)
                    try:
                        require(file_info(source)==context.source_before[entry['Source']],'UserDataSourceChanged')
                        target=beneath(parent,leaf,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
                        try:
                            offset=0;h=hashlib.sha256()
                            while offset<entry['Length']:
                                data=os.pread(source,min(65536,entry['Length']-offset),offset);require(data,'UserDataShortRead');h.update(data);offset+=len(data)
                                pending=memoryview(data)
                                while pending:
                                    count=os.write(target,pending);require(count>0,'UserDataShortWrite');pending=pending[count:]
                            require(h.hexdigest().upper()==entry['Sha256'] and file_info(source)==context.source_before[entry['Source']],'UserDataConsumedSourceChanged')
                            os.fchown(target,p['Uid'],p['Gid']);os.fchmod(target,0o600);os.fsync(target)
                        finally:os.close(target)
                    finally:os.close(source)
                os.fsync(parent)
            finally:os.close(parent)
        observed=independent(context);context.verify()
        link={'IntentReference':intent_ref,'IntentSha256':digest(intent)}
        observation,observation_ref=append({'State':'IndependentObservation','Observation':observed,**link})
        result,result_ref=append({'State':'AppliedAndVerified','Files':observed['Files'],'Bytes':observed['Bytes'],
            'ObservationSha256':digest(observation),'ObservationReference':observation_ref,'BindingSha256':observed['BindingSha256'],**link})
        receipt={'SchemaVersion':1,'GenerationId':p['GenerationId'],'Kind':'UserData','State':'AppliedAndVerified','EvidenceSha256':digest(result)}
        validate_completion_receipt(receipt,p['GenerationId'],'UserData')
        receipt_bytes=canonical(receipt)
        # Fixture output only; never the worker's installed first-boot-input store.
        journal.durable_create(store.fd,'userdata-receipt.json',receipt_bytes)
        require(journal.read(store.fd,'userdata-receipt.json')==receipt_bytes,'UserDataReceiptReopen')
        reopen_request = {'Plan':base64.b64encode(context.raw).decode(),'StoreFd':store.fd,'StoreIdentity':identity(store.fd)}
        if authority is not None: reopen_request['Authority'] = base64.b64encode(authority).decode()
        reopened=runner(reopen_request,[store.fd],'--reopen')
        require(reopened['ResultReference']==result_ref and reopened['ResultSha256']==digest(result) and reopened['ReceiptSha256']==digest(receipt_bytes),'UserDataFinalReopen')
        return {**reopened,**observed,'Scope':base['Scope']}
    except (OSError,ValueError,KeyError,TypeError,subprocess.TimeoutExpired):
        try:append({'State':'OutcomeUnknown' if effects else 'NotStarted','Code':'UserDataTransferOrObservationUnavailable','PossibleContentEffects':effects})
        except (OSError,ValueError,KeyError):pass
        raise
