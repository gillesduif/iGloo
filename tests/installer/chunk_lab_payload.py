"""Explicit NEW virtual test disks only; fixture provisioning, not Windows receipts.

Host must first retain QEMU argv/backing/FD ancestry proof from chunk_lab_vm.py.
This is serial-invoked INSIDE that disposable VM, with no network or passthrough.
No retry: all output paths and GPT before-state are create-new/blank.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


def run(argv, data=None):
    result = subprocess.run(argv, input=data, capture_output=True, check=False)
    if result.returncode:
        raise OSError((argv, result.returncode, result.stderr.decode(errors='replace')))
    return result.stdout


def record(name, value):
    with (Path('/chunk-lab-evidence') / (name+'.json')).open('x') as file:
        json.dump(value, file, sort_keys=True, indent=2); file.flush(); os.fsync(file.fileno())


def main():
    assert os.getuid()==0
    Path('/chunk-lab-evidence').mkdir(exist_ok=False)
    # Only uniquely nominated brand-new virtual devices from the retained host launch.
    for device, serial, size in [('vdb','IGLOO-LAB-TARGET',40*1024**3),('vdc','IGLOO-LAB-JOURNAL',8*1024**3)]:
        assert Path('/sys/class/block/'+device+'/serial').read_text().strip()==serial
        assert int(Path('/sys/class/block/'+device+'/size').read_text())*512==size
        assert not list(Path('/sys/class/block/'+device).glob(device+'*'))
        signatures=json.loads(run(['wipefs','--no-act','--json','/dev/'+device]))
        assert signatures['signatures']==[]
        assert not any(line.split()[2]==Path('/sys/class/block/'+device+'/dev').read_text().strip() for line in Path('/proc/self/mountinfo').read_text().splitlines())
        record(device+'-before',dict(Serial=serial,Bytes=size,Signatures=signatures,Provenance='New file-backed lab virtual disk, not native Windows preparation'))
    # 256 MiB preserved test ESP, 1 GiB Linux ESP, 8 GiB payload, 20 GiB root.
    table='label: gpt\nunit: sectors\n\nstart=2048,size=524288,type=U\nsize=2097152,type=U\nsize=16777216,type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7\nsize=41943040,type=L\n'
    record('partition-intent',dict(TargetTable=table,JournalTable='GPT one Linux filesystem partition'))
    run(['sfdisk','/dev/vdb'],table.encode())
    run(['sfdisk','/dev/vdc'],b'label: gpt\nunit: sectors\n\nstart=2048,type=L\n')
    run(['udevadm','settle'])
    record('partition-readback',dict(Target=json.loads(run(['sfdisk','--json','/dev/vdb'])),Journal=json.loads(run(['sfdisk','--json','/dev/vdc']))))
    for device, kind in [('vdb1','fat'),('vdb2','fat'),('vdb3','fat'),('vdb4','ext4'),('vdc1','ext4')]:
        record(device+'-format-intent',dict(Device='/dev/'+device,Kind=kind,Before=run(['wipefs','--no-act','--json','/dev/'+device]).decode()))
        run(['mkfs.vfat','-F','32','/dev/'+device] if kind=='fat' else ['mkfs.ext4','-m','0','/dev/'+device])
        record(device+'-format-readback',dict(Blkid=run(['blkid','--probe','--output','export','/dev/'+device]).decode()))
    payload=Path('/chunk-payload');payload.mkdir()
    run(['mount','-t','vfat','-o','rw,nosuid,nodev,noexec','/dev/vdb3',str(payload)])
    try:
        source=Path('/chunks-original')
        transport=json.loads((source/'transport.json').read_bytes())
        folder=payload/'configured-root'/transport['BuildId']/transport['DerivationId']
        folder.mkdir(parents=True)
        names=['descriptor.json','root.manifest.json','transport.json']+[c['Name'] for c in transport['Chunks']]
        allocated=os.statvfs(payload)
        required=sum(((source/n).stat().st_size+allocated.f_frsize-1)//allocated.f_frsize*allocated.f_frsize for n in names)+64*1024**2
        assert allocated.f_bavail*allocated.f_frsize>=required
        record('payload-capacity-before',dict(BytesAvailable=allocated.f_bavail*allocated.f_frsize,AllocationUnit=allocated.f_frsize,RequiredWithReserve=required,Names=names))
        for name in names:
            with (source/name).open('rb') as input_file, (folder/name).open('xb') as output:
                shutil.copyfileobj(input_file,output,65536);output.flush();os.fsync(output.fileno())
        fd=os.open(folder,os.O_RDONLY|os.O_DIRECTORY)
        try:os.fsync(fd)
        finally:os.close(fd)
        record('payload-copied',dict(MountInfo=run(['findmnt','-J','-T',str(folder)]).decode(),TransportSha256=hashlib.sha256((folder/'transport.json').read_bytes()).hexdigest().upper()))
    finally:
        # Ordinary unmount failure is failure, never lazy/force success.
        run(['umount',str(payload)])
    assert not any(line.split()[4]==str(payload) for line in Path('/proc/self/mountinfo').read_text().splitlines())
    run(['mount','-t','vfat','-o','ro,nosuid,nodev,noexec','/dev/vdb3',str(payload)])
    record('payload-readonly-mount',dict(MountInfo=run(['findmnt','-J','-T',str(payload)]).decode()))
    print(str(folder),flush=True)


if __name__=='__main__': main()
