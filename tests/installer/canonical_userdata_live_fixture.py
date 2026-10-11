"""One-use live QEMU transport fixture for UserData acquisition.

This adapter boots only a verified runtime overlay and a scratch disk. The
ISO's historical records are codec inputs, never predecessor authority. The
resulting v1 completion is deliberately inadmissible to effectful agent stage.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import select
import shutil
import signal
import socket
import stat
import subprocess
import time
import uuid

HOST_FILES = frozenset(('canonical_userdata_live_fixture.py',
    'canonical_userdata_acquisition.py', 'canonical_userdata_completion.py',
    'canonical_userdata_readback.py', 'canonical_configuration_readback.py',
    'canonical_configuration_derivation.py', 'canonical_lab_runtime.py',
    'canonical_storage_lab.py', 'canonical_lab_capacity.py',
    'canonical_userdata_guest.py', 'canonical_storage_guest.py',
    'userdata_contract.py', 'debian_first_boot.py', 'isolation_policy.py',
    'Test-CanonicalLabHostCapacity.ps1'))

if __name__ == '__main__':
    stage = Path(__file__).absolute().parent
    tools = Path('/var/lib/igloo/userdata-live-fixture-tools/20261003')
    if stage != tools/'host-v9': raise ValueError('FixtureHostStageUnnominated')
    for path in (stage, tools, tools.parent, *tools.parents):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError('FixtureHostStageUnprotected')
    manifest = tools/'host-v9-manifest.json'
    info = manifest.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or info.st_mode & 0o022:
        raise ValueError('FixtureHostManifestUnprotected')
    frozen = json.loads(manifest.read_bytes())
    if set(frozen) != HOST_FILES or set(p.name for p in stage.iterdir()) != HOST_FILES:
        raise ValueError('FixtureHostManifestChanged')
    for name, expected in frozen.items():
        path = stage/name; info = path.lstat()
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or
                info.st_mode & 0o022 or
                hashlib.sha256(path.read_bytes()).hexdigest().upper() != expected):
            raise ValueError('FixtureHostBytesChanged')
    import sys
    sys.path.insert(0, str(stage))

from canonical_lab_runtime import checkpoint
from canonical_lab_capacity import require_backing_capacity
from canonical_configuration_derivation import closed, require
from canonical_userdata_acquisition import (MAX_PACKET, MAX_WIRE, process_start_time,
    read_controlled_channel, unique)
from canonical_userdata_completion import (digest, protected, publish_from_protected_verifier)
from canonical_userdata_readback import verify as verify_terminal
from canonical_storage_lab import sha

SOURCE = Path('/var/lib/igloo/canonical-labs/28df6134bff54f94a07e3ed442caac64')
CODEC = Path('/var/lib/igloo/initramfs-helper-labs/a1b883f4179f4846b16b9a91fb4bcf9d')
TOOLS = Path('/var/lib/igloo/userdata-live-fixture-tools/20261003')
RUNS = Path('/var/lib/igloo/userdata-live-fixtures')
NEW_WITNESSES = TOOLS/'witnesses-v4'
EXHAUSTED_V3_RUNS = frozenset(('edc2969b596a4dc4aefca346ff0b5241',
                               '0196fe1de48b4e959cca2ccb81867071'))
# Pass-2 delivered 6,176,768 of 11,254,128 Base64 bytes in 120 seconds.
# At that observed rate the body alone needs about 219 seconds. The 360-second
# fixture ceiling adds 141 seconds for variation, framing and terminal status;
# startup has its own 180-second limit. Canonical execution keeps its own limit.
FIXTURE_CAPTURE_SECONDS = 360
FIXTURE_BEGIN_SECONDS = 60
FIXTURE_INACTIVITY_SECONDS = 45
FIXTURE_TERMINAL_SECONDS = 30
OLD_RUNS = frozenset(('f909adcc3ea04d0d82930212cfad32f6',
                      '0750dff791274730a2c4da001bf7fb69'))
PREVIOUS_RUNS = frozenset(('cca7e696e6264b5cbf7c644686dc5b3d',
                           '21f57f2af111494d991144fe534f030e'))
RUNTIME_SHA = 'AEA60285A9362B01C11E9BC110F8CF30CC38F1083EB936482D6DE5787DD13519'
KERNEL_SHA = '2B2358B37674D2505350528875BB17AFAE2A36522A9E8A9417EACA65A7DA0E08'
INITRD_SHA = 'C0105164A3398CE7A1CDEA19F21B7BE938493EBC170E277B8531875E3C12200D'
DOTNET_SHA = '37674A9F73C1F531B7DCB26F569692F2C85419C0BA4FB7622D5BFC65BB0F5810'
HARNESS_SHA = 'CD5C26694095ABCCD75EC6E4D70FDB80A5B5EEE2694C9CD563A91F61BF786494'
FIXED = ('userdata-retention.json', 'userdata-derivation.json',
    'userdata-independent-host-readback.json', 'input/userdata-authorization.json',
    *(f'guest-evidence/execution/{n}' for n in ('host-binding.json', 'inventory.json',
       'userdata.stdout', 'userdata-exit.json', 'independent-handoff.json',
       'preserved-before.json', 'preserved-after.json', 'provisioning-journal-unmount.json')))


def source_preflight():
    """Current bytes, historical pin and exclusive read-only backing source."""
    require(TOOLS.is_dir() and TOOLS.stat().st_uid == 0 and TOOLS.stat().st_mode & 0o077 == 0,
            'FixtureToolStoreUnprotected')
    require(sha(TOOLS/'dotnet/dotnet') == DOTNET_SHA and
            sha(TOOLS/'harness/CanonicalImportQualification.dll') == HARNESS_SHA,
            'FixtureLinuxReaderChanged')
    for name, expected in (('runtime.raw', RUNTIME_SHA), ('kernel', KERNEL_SHA),
                           ('initrd', INITRD_SHA)):
        p = SOURCE/name
        require(p.is_file() and not p.is_symlink() and p.stat().st_nlink == 1 and
                sha(p) == expected, 'FixtureRuntimeSourceChanged')
    source = SOURCE/'runtime.raw'
    closed([source])
    require(json.loads((Path('/var/lib/igloo/canonical-labs/49ae770881134d539dea7fdcb3bf7b81') /
                        'retained-runtime-final-readback.json').read_bytes())['Sha256'] == RUNTIME_SHA,
            'FixtureRuntimePinChanged')
    return {'Path': str(source), 'Device': source.stat().st_dev,
            'Inode': source.stat().st_ino, 'Length': source.stat().st_size,
            'Sha256': RUNTIME_SHA, 'MtimeNs': source.stat().st_mtime_ns,
            'CtimeNs': source.stat().st_ctime_ns}


def codec_files():
    """Explicit old bytes are transmitted only as labelled parser fixtures."""
    fixed = {name: CODEC/name for name in FIXED}
    for prefix in ('input/initramfs-predecessor', 'guest-evidence/journals'):
        base = CODEC/prefix
        require(base.is_dir() and not base.is_symlink(), 'FixtureCodecMissing')
        for item in base.rglob('*'):
            require(not item.is_symlink(), 'FixtureCodecLink')
            if item.is_file(): fixed[item.relative_to(CODEC).as_posix()] = item
    require(len(fixed) <= 4096, 'FixtureCodecInventoryBound')
    raw = {name: path.read_bytes() for name, path in fixed.items()}
    require(sum(map(len, raw.values())) <= 16*1024*1024, 'FixtureCodecBytesBound')
    verified = verify_terminal(CODEC/'guest-evidence/execution', CODEC/'guest-evidence/journals')
    require(verified['UserData'] == verified['Teardown'] == 'AppliedAndVerified',
            'FixtureCodecTerminalChanged')
    return raw, verified


def fixture_packet(raw, run_id):
    """Use the same bounded fixture declaration for launch and full-size tests."""
    packet = {'Version': 1, 'Scope': 'ControlledGuestFixtureCaptureV1',
        'RunId': str(uuid.UUID(str(run_id))),
        'CodecSourceRunId': str(uuid.UUID(CODEC.name)),
        'Files': [{'Path': name, 'Length': len(data), 'Sha256': digest(data),
                   'Bytes': base64.b64encode(data).decode('ascii')}
                  for name, data in sorted(raw.items())]}
    result = json.dumps(packet, sort_keys=True, separators=(',', ':')).encode()
    require(len(result) <= MAX_PACKET, 'FixturePacketBound')
    return result


def prepare(run_id):
    run_id = uuid.UUID(run_id)
    # The established 74-GiB lab-runtime reserve is conservative for this
    # overlay/scratch-only fixture; it does not change the 180-GiB derivative gate.
    # powershell.exe cannot -File a WSL-private UNC path. Use the existing
    # Windows checkout script only after matching its bytes to the protected
    # closure; the unchanged validator still parses the host observation.
    windows_script = Path.cwd()/'tests/installer/Test-CanonicalLabHostCapacity.ps1'
    require(sha(windows_script) == sha(Path(__file__).parent/windows_script.name),
            'FixtureCapacityScriptChanged')
    capacity = require_backing_capacity(74 * 1024**3, script_path=windows_script)
    free = os.statvfs('/var/lib/igloo')
    require(free.f_bavail * free.f_frsize >= 74 * 1024**3, 'FixtureGuestCapacityInsufficient')
    source = source_preflight(); raw, terminal = codec_files()
    qualification_path = TOOLS/(Path(__file__).parent.name+'-qualification.json')
    protected(qualification_path)
    qualification = json.loads(qualification_path.read_bytes(), object_pairs_hook=unique)
    require(qualification.get('Scope') == 'LiveFixtureFullSizeQualificationV1' and
            qualification.get('FullSizePassed') is True and
            qualification.get('ReceiverSha256') ==
                sha(Path(__file__).parent/'canonical_userdata_acquisition.py') and
            qualification.get('GuestSha256') ==
                sha(Path(__file__).parent/'canonical_userdata_guest.py') and
            qualification.get('AdapterSha256') == sha(Path(__file__)) and
            qualification.get('AbsoluteCaptureSeconds') == FIXTURE_CAPTURE_SECONDS,
            'FixtureFullSizeQualificationChanged')
    RUNS.mkdir(mode=0o755, exist_ok=True)
    protected(RUNS, file=False)
    old = TOOLS/'witnesses'
    require({path.parent.name for path in old.glob('*/budget.json')} == OLD_RUNS,
            'FixtureHistoricalBudgetChanged')
    previous = TOOLS/'witnesses-v2'
    require({path.parent.name for path in previous.glob('*/budget.json')} == PREVIOUS_RUNS and
            json.loads((previous/'21f57f2af111494d991144fe534f030e'/
                        'writer-disposition.json').read_bytes())['WriterExclusionVerified'] is True,
            'FixturePreviousBudgetOrWriterChanged')
    exhausted = TOOLS/'witnesses-v3'
    require({path.parent.name for path in exhausted.glob('*/budget.json')} == EXHAUSTED_V3_RUNS and
            all(json.loads((exhausted/name/'writer-disposition.json').read_bytes())
                ['WriterExclusionVerified'] is True for name in EXHAUSTED_V3_RUNS),
            'FixtureExhaustedBudgetOrWriterChanged')
    budgets = list(NEW_WITNESSES.glob('*/budget.json')) if NEW_WITNESSES.is_dir() else []
    used = len(budgets)
    require(used < 2, 'FixtureLiveBudgetExhausted')
    if used == 1:
        first = budgets[0].parent
        corrective = json.loads((first/'corrective-qualification.json').read_bytes()) \
            if (first/'corrective-qualification.json').is_file() else {}
        require((first/'failure-disposition.json').is_file() and
                (first/'writer-disposition.json').is_file() and
                (first/'receive-diagnostics/observation.json').is_file() and
                json.loads((first/'writer-disposition.json').read_bytes())['WriterExclusionVerified'] is True and
                corrective.get('Scope') == 'LiveFixtureCorrectivePassV4' and
                corrective.get('FailureDiagnosticSha256') ==
                    sha(first/'receive-diagnostics/observation.json') and
                corrective.get('ReceiverSha256') ==
                    sha(Path(__file__).parent/'canonical_userdata_acquisition.py') and
                corrective.get('GuestSha256') ==
                    sha(Path(__file__).parent/'canonical_userdata_guest.py') and
                corrective.get('ReproducerPassed') is True,
                'FixtureCorrectivePassUnqualified')
    run = RUNS/run_id.hex
    witness = NEW_WITNESSES/run_id.hex
    require(not run.exists() and not witness.exists(), 'FixtureReplay')
    NEW_WITNESSES.mkdir(mode=0o700, exist_ok=True)
    witness.mkdir(mode=0o700)
    checkpoint(witness, 'capacity.json', {'Scope': 'LiveFixtureCapacityV1', 'Host': capacity,
        'GuestAvailableBytes': free.f_bavail*free.f_frsize,
        'RequiredBytes': 74*1024**3, 'FullDerivativeRequirementUnchanged': 180*1024**3})
    checkpoint(witness, 'source.json', source)
    checkpoint(witness, 'budget.json', {'Scope': 'UserDataLiveAcquisitionPassV4',
        'HistoricalPassesUsed': 6, 'EarlierNewPassesUsed': used,
        'ThisNewInvocation': used+1, 'MaximumNewPasses': 2,
        'RunId': str(run_id), 'Status': 'ConsumesPassBeforeLaunch',
        'ReceiverSha256': sha(Path(__file__).parent/'canonical_userdata_acquisition.py'),
        'GuestSha256': sha(Path(__file__).parent/'canonical_userdata_guest.py'),
        'AdapterSha256': sha(Path(__file__)),
        'HostManifestSha256': sha(TOOLS/(Path(__file__).parent.name+'-manifest.json')),
        'FullSizeQualificationSha256': sha(qualification_path),
        'DotnetSha256': DOTNET_SHA, 'HarnessSha256': HARNESS_SHA,
        'CorrectiveQualificationSha256': sha(budgets[0].parent/'corrective-qualification.json')
            if used else None})
    run.mkdir(mode=0o700)
    person = pwd.getpwnam('gillesduif')
    os.chown(run, person.pw_uid, person.pw_gid)
    overlay = run/'runtime.qcow2'
    subprocess.run(['/usr/bin/qemu-img','create','-f','qcow2','-F','raw','-b',
                    str(SOURCE/'runtime.raw'),str(overlay)],check=True,capture_output=True)
    info = json.loads(subprocess.run(['/usr/bin/qemu-img','info','--output=json',str(overlay)],
        check=True,capture_output=True).stdout)
    require(overlay_info_matches(info,source),'FixtureBackingChainChanged')
    scratch = run/'scratch.raw'
    with scratch.open('xb') as out:
        out.truncate(64*1024*1024); out.flush(); os.fsync(out.fileno())
    packet_raw = fixture_packet(raw, run_id)
    inputs = run/'iso-input'; inputs.mkdir(mode=0o700)
    (inputs/'fixture-packet.json').write_bytes(packet_raw)
    for name in ('canonical_userdata_guest.py','canonical_storage_guest.py'):
        shutil.copyfile(Path(__file__).parent/name,inputs/name)
    iso = run/'tools.iso'
    subprocess.run(['/usr/bin/xorriso','-as','mkisofs','-quiet','-R','-V',
        'IGLOO_USERDATA_FIXTURE','-o',str(iso),str(inputs)],check=True,capture_output=True)
    for item in (overlay,scratch,iso): os.chown(item,person.pw_uid,person.pw_gid)
    os.chmod(iso,0o644)
    expected = {name: digest(data) for name,data in raw.items()}
    checkpoint(witness,'codec-inputs.json',{'Scope':'HistoricalCodecFixtureOnlyV1',
        'SourceRunId':str(uuid.UUID(CODEC.name)),'PacketSha256':digest(packet_raw),'Files':expected,
        'TerminalResultSha256':terminal['EffectRecords'][-1]['Sha256'],
        'TerminalCloseSha256':terminal['SessionRecords'][-1]['Sha256']})
    args = ['/usr/bin/qemu-system-x86_64','-no-user-config','-nodefaults',
        '-machine','q35,accel=kvm','-cpu','host','-smp','2','-m','2048',
        '-display','none','-nic','none','-monitor','none','-no-reboot',
        '-sandbox','on,obsolete=deny,elevateprivileges=allow,spawn=deny,resourcecontrol=deny',
        '-runas',person.pw_name,'-serial',f'unix:{run}/console.sock,server=on,wait=off',
        '-kernel',str(SOURCE/'kernel'),'-initrd',str(SOURCE/'initrd'),
        '-append','root=PARTUUID=86480d67-661c-4a36-8648-9659bce14063 ro console=ttyS0,115200n8 noresume',
        '-drive',f'file={overlay},if=none,id=runtime,format=qcow2,cache=none',
        '-device','virtio-blk-pci,drive=runtime,serial=IGLOO-GPT-RUNTIME',
        '-drive',f'file={scratch},if=none,id=scratch,format=raw,cache=none',
        '-device','virtio-blk-pci,drive=scratch,serial=IGLOO-FIXTURE-SCRATCH',
        '-drive',f'file={iso},media=cdrom,if=ide,format=raw,readonly=on']
    checkpoint(witness,'launch.json',{'Version':1,'Scope':'ControlledUserDataFixtureLaunchV1',
        'RunId':str(run_id),'Arguments':args,'Source':source,'ToolsSha256':sha(iso),
        'Overlay': {'Format': info['format'], 'VirtualSize': info['virtual-size'],
                    'BackingFilename': info['backing-filename'],
                    'BackingFormat': info['backing-filename-format']},
        'Disks':{name:{'Device':(run/name).stat().st_dev,'Inode':(run/name).stat().st_ino,
                       'Length':(run/name).stat().st_size}
                 for name in ('runtime.qcow2','scratch.raw')}})
    return run,witness,args


def backing_bindings_match(run, launch, found, *, overlay_may_grow=False):
    expected={str(run/'runtime.qcow2'):2,str(run/'scratch.raw'):2,
              str(run/'tools.iso'):0,str(SOURCE/'runtime.raw'):0}
    if set(found)!=set(expected): return False
    source=(launch['Source']['Device'],launch['Source']['Inode'],launch['Source']['Length'])
    if found[str(SOURCE/'runtime.raw')]!=source: return False
    for name,binding in launch['Disks'].items():
        current=found[str(run/name)]
        original=(binding['Device'],binding['Inode'],binding['Length'])
        if name=='runtime.qcow2' and overlay_may_grow:
            if current[:2]!=original[:2] or not original[2]<=current[2]<=original[2]+1024**3:
                return False
        elif current!=original:
            return False
    return True


def overlay_info_matches(info, source):
    return (info.get('format')=='qcow2' and
            info.get('virtual-size')==source['Length'] and
            info.get('backing-filename')==source['Path'] and
            info.get('backing-filename-format')=='raw')


def stable_closed_sha(path):
    path=Path(path)
    require(not path.is_symlink() and path.is_file(), 'FixtureBackingTypeChanged')
    closed([path]); before=path.stat()
    require(before.st_nlink==1,'FixtureBackingAliasChanged')
    value=sha(path)
    closed([path]); after=path.stat()
    require((before.st_dev,before.st_ino,before.st_size,before.st_nlink,
             before.st_mtime_ns,before.st_ctime_ns)==
            (after.st_dev,after.st_ino,after.st_size,after.st_nlink,
             after.st_mtime_ns,after.st_ctime_ns),
            'FixtureBackingChangedDuringHash')
    return value


def closed_backing_readback(run,witness):
    """Only after process exit: stable bytes, relations and source custody."""
    source=json.loads((witness/'source.json').read_bytes())
    launch=json.loads((witness/'launch.json').read_bytes())
    source_path=SOURCE/'runtime.raw'; source_state=source_path.stat()
    require((source_state.st_dev,source_state.st_ino,source_state.st_size,
             source_state.st_mtime_ns,source_state.st_ctime_ns)==
            (source['Device'],source['Inode'],source['Length'],
             source['MtimeNs'],source['CtimeNs']) and
            stable_closed_sha(source_path)==source['Sha256'], 'FixtureSourceChanged')
    backings={}
    for name in ('runtime.qcow2','scratch.raw'):
        path=run/name;state=path.stat();original=launch['Disks'][name]
        require((state.st_dev,state.st_ino)==(original['Device'],original['Inode']) and
                (name!='scratch.raw' or state.st_size==original['Length']==64*1024*1024) and
                (name!='runtime.qcow2' or
                 original['Length']<=state.st_size<=original['Length']+1024**3),
                'FixtureRetainedBackingChanged')
        backings[name]={'Length':state.st_size,'Sha256':stable_closed_sha(path),
                        'Device':state.st_dev,'Inode':state.st_ino}
    require(stable_closed_sha(run/'tools.iso')==launch['ToolsSha256'],
            'FixtureToolsChanged')
    info=json.loads(subprocess.run(['/usr/bin/qemu-img','info','--output=json',
                                    str(run/'runtime.qcow2')],check=True,
                                   capture_output=True,timeout=15).stdout)
    require(overlay_info_matches(info,source),'FixtureBackingChainChanged')
    return source,backings


def inspect_process(run,witness, *, overlay_may_grow=False):
    protected(witness,file=False); protected(witness/'launch.json'); protected(witness/'process.json')
    launch=json.loads((witness/'launch.json').read_bytes(),object_pairs_hook=unique)
    process=json.loads((witness/'process.json').read_bytes(),object_pairs_hook=unique)
    require(launch['Scope']=='ControlledUserDataFixtureLaunchV1' and
            process['LaunchSha256']==sha(witness/'launch.json') and
            process_start_time(process['Pid'])==process['StartTime'] and
            Path(f"/proc/{process['Pid']}/cmdline").read_bytes().split(b'\0')[:-1] ==
            [x.encode() for x in launch['Arguments']] and
            launch['Arguments'][launch['Arguments'].index('-runas')+1]=='gillesduif' and
            launch['Arguments'][launch['Arguments'].index('-nic')+1]=='none',
            'FixtureLaunchChanged')
    expected={str(run/'runtime.qcow2'):2,str(run/'scratch.raw'):2,
              str(run/'tools.iso'):0,str(SOURCE/'runtime.raw'):0}
    found={}
    for fd in Path(f"/proc/{process['Pid']}/fd").iterdir():
        s=fd.stat(); path=os.readlink(fd)
        require(not stat.S_ISBLK(s.st_mode),'FixturePhysicalBlockFd')
        if not stat.S_ISREG(s.st_mode) or path==str(run/'qemu.stderr'): continue
        mode=int(next(line.split()[1] for line in
            Path(f"/proc/{process['Pid']}/fdinfo/{fd.name}").read_text().splitlines()
            if line.startswith('flags:')),8)&3
        require(path in expected and expected[path]==mode and path not in found,
                'FixtureBackingFdChanged')
        found[path]=(s.st_dev,s.st_ino,s.st_size)
    require(backing_bindings_match(run,launch,found,overlay_may_grow=overlay_may_grow) and
            sha(run/'tools.iso')==launch['ToolsSha256'],'FixtureBackingIdentityChanged')
    return process,found


def accept_fixture(packet,challenge,run,witness):
    require(0<len(packet)<=MAX_PACKET,'FixturePacketBound')
    value=json.loads(packet,object_pairs_hook=unique)
    require(set(value)=={'Version','Scope','RunId','CodecSourceRunId','Files','Challenge'} and
            value['Version']==1 and value['Scope']=='ControlledGuestFixtureCaptureV1' and
            value['RunId']==str(uuid.UUID(run.name)) and value['Challenge']==challenge and
            value['CodecSourceRunId']==str(uuid.UUID(CODEC.name)) and
            type(value['Files']) is list,'FixtureCaptureSchemaChanged')
    manifest=json.loads((witness/'codec-inputs.json').read_bytes(),object_pairs_hook=unique)
    files={}; total=0
    for item in value['Files']:
        name=item['Path']
        require(set(item)=={'Path','Length','Sha256','Bytes'} and
                isinstance(name,str) and re.fullmatch(r'[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*',name) and
                name in manifest['Files'] and name not in files,'FixtureCaptureInventoryChanged')
        data=base64.b64decode(item['Bytes'],validate=True); total+=len(data)
        require(total<=16*1024*1024 and len(data)==item['Length'] and
                digest(data)==item['Sha256']==manifest['Files'][name],
                'FixtureCaptureRawChanged')
        files[name]=data
    require(set(files)==set(manifest['Files']),'FixtureCaptureMissing')
    pending=witness/'pending'; require(not pending.exists(),'FixtureCaptureReplay')
    pending.mkdir(mode=0o700); raw=pending/'raw'; raw.mkdir(mode=0o700)
    for name,data in sorted(files.items()):
        target=raw/name; target.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
        fd=os.open(target,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
        with os.fdopen(fd,'wb') as out: out.write(data); out.flush(); os.fsync(out.fileno())
    for directory in sorted((p for p in pending.rglob('*') if p.is_dir()),
                            key=lambda p:len(p.parts),reverse=True):
        fd=os.open(directory,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    observed=verify_terminal(raw/'guest-evidence/execution',raw/'guest-evidence/journals')
    require(observed['EffectRecords'][-1]['Sha256']==manifest['TerminalResultSha256'] and
            observed['SessionRecords'][-1]['Sha256']==manifest['TerminalCloseSha256'],
            'FixtureCodecTerminalChanged')
    checkpoint(pending,'capture.json',{'Scope':'LiveFixtureCaptureV1','RunId':value['RunId'],
        'Challenge':challenge,'PacketSha256':digest(packet),'Files':manifest['Files'],
        'HistoricalCodecOnly':True})
    fd=os.open(pending/'packet.bin',os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
    with os.fdopen(fd,'wb') as out: out.write(packet); out.flush(); os.fsync(out.fileno())
    protected(pending,file=False)
    return raw,observed


def dispose_failed_fixture(run, witness, process):
    """Preserve a failed run; stop only its verified scratch-only QEMU."""
    requested = False; host_terminated = False
    handle = os.pidfd_open(process.pid)
    try:
        if process.poll() is None:
            inspect_process(run, witness, overlay_may_grow=True)
            console = run/'console.sock'
            require(stat.S_ISSOCK(console.lstat().st_mode) and not console.is_symlink(),
                    'FixtureConsoleChanged')
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                channel.settimeout(3); channel.connect(str(console))
                channel.sendall(b'poweroff\n'); requested = True
            try: process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                inspect_process(run, witness, overlay_may_grow=True)
                require(process_start_time(process.pid) ==
                        json.loads((witness/'process.json').read_bytes())['StartTime'],
                        'FixtureProcessChanged')
                signal.pidfd_send_signal(handle, signal.SIGTERM, None, 0)
                host_terminated = True
                try: process.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    raise ValueError('FixtureTargetedTerminationDidNotExit') from None
    finally: os.close(handle)
    source,backings=closed_backing_readback(run,witness)
    checkpoint(witness,'writer-disposition.json',{'Scope':'FailedLiveFixtureWriterDispositionV1',
        'NormalPoweroffRequested':requested,'NormalGuestShutdownVerified':False,
        'HostSigtermViaPidfd':host_terminated,'ProcessExited':True,
        'WriterExclusionVerified':True,'SourceUnchanged':True,
        'SourceSha256':source['Sha256'],'Backings':backings,
        'CanonicalCloseClaimed':False})


def finish_fixture_writer(run, witness, process, channel, *, wait_seconds=30):
    """Keep the sole console attached through bounded normal-shutdown observation."""
    require(wait_seconds > 0 and wait_seconds <= 30, 'FixtureShutdownBoundInvalid')
    inspect_process(run, witness, overlay_may_grow=True)
    handle = os.pidfd_open(process.pid)
    observed = bytearray()
    received = 0
    host_terminated = False
    started = time.monotonic()
    try:
        channel.sendall(b'poweroff\n')
        while process.poll() is None and time.monotonic()-started < wait_seconds:
            channel.settimeout(min(1, max(.01, wait_seconds-(time.monotonic()-started))))
            try: part = channel.recv(4096)
            except socket.timeout: continue
            if not part:
                time.sleep(.1)
                continue
            received += len(part)
            observed.extend(part[:max(0, 8192-len(observed))])
        if process.poll() is None:
            inspect_process(run, witness, overlay_may_grow=True)
            require(process_start_time(process.pid) ==
                    json.loads((witness/'process.json').read_bytes())['StartTime'],
                    'FixtureProcessChanged')
            signal.pidfd_send_signal(handle, signal.SIGTERM, None, 0)
            host_terminated = True
            try: process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                raise ValueError('FixtureTargetedTerminationDidNotExit') from None
    finally:
        os.close(handle)
    require(process.poll() is not None and
            (host_terminated or process.returncode == 0), 'FixtureProcessExitUnexpected')
    fd = os.open(witness/'shutdown-console.raw',
                 os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd,'wb') as out:
        out.write(observed); out.flush(); os.fsync(out.fileno())
    checkpoint(witness,'shutdown.json',{'Scope':'LiveFixtureShutdownObservationV1',
        'NormalPoweroffRequested':True,'NormalGuestShutdownVerified':not host_terminated,
        'HostSigtermViaPidfd':host_terminated,'ProcessExit':process.returncode,
        'ConsoleBytesObserved':received,'ConsoleBytesRetained':len(observed),
        'ConsoleObservationSha256':sha(witness/'shutdown-console.raw'),
        'WaitMilliseconds':int((time.monotonic()-started)*1000),
        'CanonicalCloseClaimed':False})
    source,backings=closed_backing_readback(run,witness)
    checkpoint(witness,'retention.json',{'Scope':'LiveFixtureBackingReadbackV1',
        'Backings':backings,'SourceUnchanged':True,'CanonicalTeardownClaimed':False})
    checkpoint(witness,'writer-disposition.json',{'Scope':'LiveFixtureDispositionV2',
        'NormalPoweroffRequested':True,'NormalGuestShutdownVerified':not host_terminated,
        'HostSigtermViaPidfd':host_terminated,'ProcessExited':True,
        'WriterExclusionVerified':True,'SourceUnchanged':True,
        'SourceSha256':source['Sha256'],'Backings':backings,
        'CanonicalCloseClaimed':False})
    return source,backings,not host_terminated


def run_once(run_id):
    run,witness,args=prepare(run_id)
    # The durable budget/intents already exist. Any launch attempt consumes one.
    with (run/'qemu.stderr').open('xb') as error:
        process=subprocess.Popen(args,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,
                                 stderr=error,start_new_session=True)
    checkpoint(witness,'process.json',{'Version':1,'Scope':'ControlledUserDataFixtureProcessV1',
        'Pid':process.pid,'StartTime':process_start_time(process.pid),
        'LaunchSha256':sha(witness/'launch.json')})
    try:
        continue_once(run, witness, process)
    except Exception as error:
        code = str(error).split(':',1)[0] if isinstance(error, ValueError) else type(error).__name__
        checkpoint(witness,'failure-disposition.json',{'Scope':'LiveFixtureFailureV4',
            'Code':code,'CaptureAccepted':(witness/'pending/capture.json').is_file(),
            'PublicationCompleted':(witness/'published').is_dir(),
            'ReaderResultCompleted':(witness/'result.json').is_file(),
            'ReceiveDiagnosticSha256':sha(witness/'receive-diagnostics/observation.json')
                if (witness/'receive-diagnostics/observation.json').is_file() else None})
        if not (witness/'writer-disposition.json').exists():
            try: dispose_failed_fixture(run,witness,process)
            except Exception as disposition_error:
                checkpoint(witness,'disposition-failure.json',{'Scope':'LiveFixtureDispositionFailureV1',
                    'Code':str(disposition_error).split(':',1)[0]
                        if isinstance(disposition_error,ValueError) else type(disposition_error).__name__,
                    'WriterExclusionEstablished':False})
        raise


def continue_once(run,witness,process):
    deadline=time.monotonic()+180
    while not (run/'console.sock').exists() and time.monotonic()<deadline: time.sleep(.2)
    process_binding,found=inspect_process(run,witness)
    challenge=os.urandom(32).hex().upper()
    with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as channel:
        channel.connect(str(run/'console.sock')); channel.settimeout(2)
        boot=b''
        while time.monotonic()<deadline:
            try: part=channel.recv(65536)
            except socket.timeout: continue
            require(part,'FixtureBootChannelClosed')
            boot=(boot+part)[-65536:]
            if b'# ' in boot[-256:]: break
        require(b'# ' in boot[-256:],'FixtureRootShellUnavailable')
        checkpoint(witness,'boot-observation.json',{'Scope':'LiveFixtureBootObservationV1',
            'ElapsedMilliseconds':int((time.monotonic()-(deadline-180))*1000),
            'ConsoleTailBytes':len(boot),'PromptObserved':True})
        packet=read_controlled_channel(channel,challenge,process_binding,
            FIXTURE_CAPTURE_SECONDS,fixture=True,
            root_created_unprivileged_uid=pwd.getpwnam('gillesduif').pw_uid,
            begin_timeout_seconds=FIXTURE_BEGIN_SECONDS,
            inactivity_timeout_seconds=FIXTURE_INACTIVITY_SECONDS,
            terminal_timeout_seconds=FIXTURE_TERMINAL_SECONDS,
            diagnostic_parent=witness,
            executed_identities={'AdapterSha256':sha(Path(__file__)),
                'GuestSha256':sha(Path(__file__).parent/'canonical_userdata_guest.py'),
                'LaunchSha256':sha(witness/'launch.json'),
                'HostManifestSha256':sha(TOOLS/(Path(__file__).parent.name+'-manifest.json'))})
        post=inspect_process(run,witness,overlay_may_grow=True)[1]
        require(all(post[path][:2]==before[:2] and
                    (path.endswith('/runtime.qcow2') or post[path]==before)
                    for path,before in found.items()),'FixtureBackingChangedDuringCapture')
        raw,observed=accept_fixture(packet,challenge,run,witness)
        source,backings,normal_shutdown=finish_fixture_writer(run,witness,process,channel)
    # A v1 parser fixture can be read by current .NET, but cannot pass the
    # effectful agent-stage path which explicitly requires live canonical v2.
    retained=json.loads((raw/'userdata-retention.json').read_bytes(),object_pairs_hook=unique)
    anchor={'Version':1,'Scope':'TrustedUserDataTerminalV1',
        'SourceRunId':retained['RunId'],
        'GenerationId':json.loads((raw/'input/userdata-authorization.json').read_bytes())['GenerationId'],
        'OperationId':observed['OperationId'],'SessionId':observed['SessionId'],
        'ResultSha256':observed['EffectRecords'][-1]['Sha256'],
        'CloseSha256':observed['SessionRecords'][-1]['Sha256'],
        'RetentionSha256':sha(raw/'userdata-retention.json'),
        'TargetSha256':retained['Backings']['target.raw']['Sha256'],
        'JournalSha256':retained['Backings']['journal.raw']['Sha256'],
        'Files':{name:sha(raw/name) for name in json.loads((witness/'codec-inputs.json').read_bytes())['Files']}}
    checkpoint(witness,'anchor.json',anchor)
    auth={'Version':1,'Scope':'OneUserDataCheckpointOfflineAgentStage',
        'AttemptId':str(uuid.uuid4()),'OperationId':str(uuid.uuid4()),
        'GenerationId':anchor['GenerationId'],'UserDataSha256':anchor['ResultSha256'],
        'CloseSha256':anchor['CloseSha256'],'RetentionSha256':anchor['RetentionSha256'],
        'TargetSha256':anchor['TargetSha256'],'JournalSha256':anchor['JournalSha256']}
    checkpoint(witness,'fixture-agent-authorization.json',auth)
    publication=publish_from_protected_verifier(raw,witness/'anchor.json',
        witness/'fixture-agent-authorization.json',witness/'published')
    runtime=TOOLS/'dotnet/dotnet'; harness=TOOLS/'harness/CanonicalImportQualification.dll'
    command=[str(runtime),str(harness),'--agent-stage-completion-readiness',
             str(witness/'published'),str(witness/'fixture-agent-authorization.json')]
    read=subprocess.run(command,capture_output=True,timeout=120,check=False)
    require(read.returncode==0 and json.loads(read.stdout)['Readiness']=='Passed',
            'FixtureLinuxReaderRejected')
    denied=subprocess.run([str(runtime),str(harness),'--agent-stage-derived-lab',
        str(witness/'published'),'missing-declaration',str(witness/'fixture-agent-authorization.json'),
        'missing-runtime','missing-journal','missing-pin'],capture_output=True,timeout=120,check=False)
    require(denied.returncode==2 and
            json.loads(denied.stdout)['Boundary']=='ProtectedPredecessor',
            'FixtureEffectGateUnexpected')
    checkpoint(witness,'result.json',{'Scope':'LiveGuestFixtureQualificationV1',
        'Outcome':'TransportAndReaderVerified','RunId':str(uuid.UUID(run.name)),
        'CaptureSha256':sha(witness/'pending/capture.json'),
        'PublicationAnchorSha256':publication['AnchorSha256'],
        'LinuxReaderCommand':command,'LinuxReaderOutput':json.loads(read.stdout),
        'EffectGate':json.loads(denied.stdout),'Backings':backings,
        'NormalGuestShutdownVerified':normal_shutdown,
        'HostSigtermViaPidfd':not normal_shutdown,
        'HistoricalCodecOnly':True,'CanonicalUserDataClaimed':False})
    print(json.dumps({'Run':str(run),'Witness':str(witness),
        'ResultSha256':sha(witness/'result.json')},sort_keys=True))


if __name__=='__main__':
    import sys
    require(len(sys.argv)==2,'FixtureRunIdRequired')
    try: run_once(sys.argv[1])
    except Exception as error:
        code = str(error).split(':',1)[0] if isinstance(error,ValueError) else type(error).__name__
        print(json.dumps({'Outcome':'Failed','Code':code},sort_keys=True),file=sys.stderr)
        raise SystemExit(1) from None
