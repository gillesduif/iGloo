"""Small synthetic UserData fixture. No canonical storage or first-boot authority."""
import base64
import json
import os
from pathlib import Path
import sys

import userdata_producer as u


def prepare(raw, data):
    p=u.validate(raw);directory=u.BASE/p['OperationId']
    u.BASE.mkdir(mode=0o700,parents=True,exist_ok=True)
    directory.mkdir(mode=0o700)  # create-new; failed fixtures are never resumed
    for name in ('source','target','target/etc','target/home','target'+p['Home'],'journal'):
        (directory/name).mkdir(mode=0o700)
    source=directory/'source';home=directory/('target'+p['Home'])
    for e in sorted(p['Entries'],key=lambda e:(e['Source'].count('/'),e['Source'])):
        path=source/e['Source'];path.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
        if e['Kind']=='Directory':path.mkdir(exist_ok=True,mode=0o700)
        else:
            contents=base64.b64decode(data[e['Source']],validate=True)
            u.require(len(contents)==e['Length'] and u.digest(contents)==e['Sha256'],'FixtureInputMismatch')
            path.write_bytes(contents);path.chmod(0o600)
    (source/'unselected.txt').write_bytes(b'Unselected synthetic source sentinel\n')
    (source/'unselected.txt').chmod(0o600)
    (home/'unrelated.txt').write_bytes(b'Unrelated synthetic destination sentinel\n')
    (home/'unrelated.txt').chmod(0o600);os.chown(home/'unrelated.txt',p['Uid'],p['Gid'])
    os.chown(home,p['Uid'],p['Gid'])
    (directory/'target/etc/passwd').write_text(f"root:x:0:0:root:/root:/bin/sh\n{p['Username']}:x:{p['Uid']}:{p['Gid']}:Synthetic fixture:{p['Home']}:/bin/bash\n")
    (directory/'target/etc/group').write_text(f"root:x:0:\n{p['Username']}:x:{p['Gid']}:\n")
    for name in ('passwd','group'):(directory/'target/etc'/name).chmod(0o644)
    return directory


def acceptance(wire):
    raw=(wire/'plan.json').read_bytes();data=json.loads((wire/'data.json').read_bytes())
    directory=prepare(raw,data)
    fd=os.open(directory,os.O_DIRECTORY)
    try:u.journal.durable_create(fd,'fixture-plan.json',raw)
    finally:os.close(fd)
    context=u.FixtureContext(raw,directory)
    try:
        result=u.perform(context)
        # Reopen all records again, outside the writer's completed call.
        reopened=u.run_observer({'Plan':base64.b64encode(raw).decode(),'StoreFd':context.store.fd,
            'StoreIdentity':u.identity(context.store.fd)},[context.store.fd],'--reopen')
        u.require(reopened['ResultSha256']==result['ResultSha256'],'FixtureIndependentReopen')
        return directory,result
    finally:context.close()


if __name__=='__main__':
    if sys.argv[1:]!=['--fixture']:raise ValueError('OnlyExplicitSyntheticFixtureSupported')
    directory,result=acceptance(Path(__file__).parent/'wire')
    report={**result,'FixtureDirectory':str(directory),'DirectoryLeasesClosed':True,
            'CanonicalOwnership':False,'FirstBootSucceeded':False,'ProductionQualified':False}
    fd=os.open(directory,os.O_DIRECTORY)
    try:u.journal.durable_create(fd,'fixture-result.json',u.canonical(report))
    finally:os.close(fd)
    print(u.canonical(report).decode(),flush=True)
