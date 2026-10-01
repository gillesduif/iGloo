"""Read-only retained delivery -> new lab input. Never mutates an evidence backing.

The explicit retained identities are development inputs, not release authority.
Canonical import independently authenticates all bytes from the new FAT32 lease.
"""
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess

from canonical_storage_lab import sha
from canonical_lab_runtime import checkpoint

BUILD = '99695826-82bc-4077-b4cf-a22cebc706da'
DERIVATION = '6072529e-1484-4fc7-a68c-d05c970bb393'
TRANSPORT = 'A093432AE281F4620959B56E35774DC0E5D01C74443DCD33F248DA723B364757'
PIN = '0029CB409F537807E91B1DF39AE2B2A0491AA4487294CDBA77674C207A022D8F'


def prepare(image, pin, destination):
    image, pin, destination = map(Path, (image, pin, destination))
    for path in (image, pin):
        if not stat.S_ISREG(path.lstat().st_mode) or path.resolve() != path:
            raise ValueError('RetainedInputNotRegular')
    if sha(pin) != PIN or pin.stat().st_uid != 0 or pin.stat().st_mode & 0o022:
        raise ValueError('ExternalPinChangedOrUnprotected')
    destination.mkdir(mode=0o700)
    objects = destination / 'artifact'; objects.mkdir()
    # This offset is independently checked against the retained GPT, not a device selector.
    with image.open('rb') as stream:
        stream.seek(512); header = stream.read(512)
        stream.seek(1024 + 2 * 128); partition = stream.read(128)
    import struct
    if header[:8] != b'EFI PART' or struct.unpack_from('<QQ', partition, 32) != (2623488, 19400703):
        raise ValueError('RetainedPayloadGeometryChanged')
    before = image.stat()
    checkpoint(destination, 'delivery-input-intent.json', {'Image': str(image), 'Device': before.st_dev,
        'Inode': before.st_ino, 'Length': before.st_size, 'MtimeNs': before.st_mtime_ns,
        'Offset': 1343225856, 'ExternalPinSha256': PIN})
    source = f'::/configured-root/{BUILD}/{DERIVATION}/'

    def copy(name):
        with (objects / name).open('xb') as output:
            subprocess.run(['/usr/bin/mtype', '-i', str(image) + '@@1343225856', source + name],
                           stdout=output, check=True, timeout=900)
            output.flush(); os.fsync(output.fileno())
        return sha(objects / name)

    if copy('transport.json') != TRANSPORT or (objects / 'transport.json').stat().st_size != 1121:
        raise ValueError('RetainedTransportChanged')
    transport = json.loads((objects / 'transport.json').read_bytes())
    for name, expected in [('descriptor.json', transport['DescriptorSha256']), ('root.manifest.json', transport['ManifestSha256'])]:
        if copy(name) != expected: raise ValueError('RetainedMetadataChanged')
    aggregate = hashlib.sha256(); length = 0
    for chunk in transport['Chunks']:
        if chunk['Name'] != f'root.content.{chunk["Index"]:04d}' or copy(chunk['Name']) != chunk['Sha256']:
            raise ValueError('RetainedChunkChanged')
        path = objects / chunk['Name']
        if path.stat().st_size != chunk['Length']: raise ValueError('RetainedChunkLengthChanged')
        with path.open('rb') as stream:
            while data := stream.read(1024 * 1024): aggregate.update(data); length += len(data)
    if length != transport['ContentLength'] or aggregate.hexdigest().upper() != transport['ContentSha256']:
        raise ValueError('RetainedLogicalStreamChanged')
    after = image.stat()
    if (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns) != (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns):
        raise ValueError('RetainedSourceModified')
    with (destination / 'external-pin.json').open('xb') as output:
        output.write(pin.read_bytes()); output.flush(); os.fsync(output.fileno())
    checkpoint(destination, 'delivery-input-result.json', {'ContentLength': length, 'ContentSha256': aggregate.hexdigest().upper(),
        'Objects': {p.name: {'Length': p.stat().st_size, 'Sha256': sha(p)} for p in sorted(objects.iterdir())},
        'RetainedSourceUnchanged': True, 'CanonicalImport': False})
    return destination
