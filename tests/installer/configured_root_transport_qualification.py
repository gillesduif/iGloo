"""Opt-in retained-artifact transport proof, never canonical ownership/import proof.

Separate invocations produce and independently verify. Exact out-of-band development
anchors below are from the retained qualification, not read from transport.json.
No artifact descriptor, semantic bytes, attestation or old evidence is modified.
"""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import sys

if not Path(__file__).with_name('configured_root.py').exists():
    sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'distros/debian/native'))
import configured_root as root
import root_transport as transport

DESCRIPTOR = '4936F2FBC669253A93CB9004D3EF4E268B995AC30479CBEF1C1E83F5B8234A20'
CONTENT = 'EDE9ADCC24597B82389CE7C73D38A02A3AB077DA53C132EE9704FEC71BC705F5'
MANIFEST = '5BE0814960F7D87B3FB3379688D60873028921D4D8F0BF504DD4C0EB6AE3B9B2'
PIN = '0029CB409F537807E91B1DF39AE2B2A0491AA4487294CDBA77674C207A022D8F'


def run(mode, source, output, pin_path):
    now = dt.datetime.now(dt.timezone.utc)
    pin_bytes = pin_path.read_bytes()
    root.require(root.digest(pin_bytes) == PIN, 'ExternalPinChanged')
    pin = json.loads(pin_bytes, object_pairs_hook=root.unique_pairs)
    parse = lambda text: dt.datetime.fromisoformat(text.replace('Z', '+00:00'))
    root.require(parse(pin['NotBeforeUtc']) <= now < parse(pin['NotAfterUtc']), 'ExpiredDevelopmentPin')
    descriptor = (source / 'descriptor.json').read_bytes()
    root.require(root.digest(descriptor) == DESCRIPTOR == pin['DescriptorSha256'], 'OriginalDescriptorChanged')
    artifact = json.loads(descriptor, object_pairs_hook=root.unique_pairs)
    root.require(artifact['Content']['Length'] == 4641457180 and artifact['Content']['Sha256'] == CONTENT and
                 artifact['Manifest']['Sha256'] == MANIFEST and parse(artifact['SupportedUntilUtc']) > now, 'OriginalArtifactChanged')
    binding = dict(BuildId=artifact['BuildId'], DerivationId=artifact['Attestation']['Neutralization']['DerivationId'],
                   DescriptorSha256=DESCRIPTOR, ManifestSha256=MANIFEST, ContentSha256=CONTENT, ContentLength=4641457180)
    manifest_bytes = (source / 'root.manifest.json').read_bytes()
    development = root.DevelopmentPin(binding['BuildId'], MANIFEST, CONTENT, parse(pin['NotAfterUtc']))
    if mode == '--produce':
        fd = os.open(source / 'root.content', os.O_RDONLY | os.O_NOFOLLOW)
        parent = os.open(output.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            root.verify_source(manifest_bytes, fd, development, now)
            value = transport.produce(fd, parent, output.name, binding)
        finally:
            os.close(fd); os.close(parent)
        # Preserve exact bytes, not reserialized objects. Physical publication is create-new.
        for name in ('descriptor.json', 'root.manifest.json'):
            with (output / name).open('xb') as target:
                target.write((source / name).read_bytes()); target.flush(); os.fsync(target.fileno())
    elif mode == '--verify':
        data = (source / 'transport.json').read_bytes()
        value = transport.parse(data)
        parent = os.open(source, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            stream = transport.open_chunks(parent, '.', data, root.digest(data), binding, root.mount_id(parent))
            try:
                manifest = root.verify_source(manifest_bytes, stream, development, now)
                root.require(len(manifest['Entries']) == 144911 and len(manifest['Packages']) == 1594, 'OriginalSemanticStateChanged')
            finally: stream.close()
        finally: os.close(parent)
    else:
        raise ValueError('Unknown qualification action')
    data = transport.canonical(value)
    result = {'VerifiedAtUtc': now.isoformat(), 'Action': mode, 'Transport': value, 'TransportManifestSha256': root.digest(data),
              'Source': str(source), 'Output': str(output), 'OriginalDescriptorSha256': DESCRIPTOR, 'ExternalPinSha256': PIN,
              'FullSemanticSourceVerified': True, 'ProductionAuthentication': 'Unsupported',
              'Tools': {name: root.digest(Path(module.__file__).read_bytes()) for name, module in [('configured_root', root), ('root_transport', transport)]}}
    with output.with_suffix('.result.json').open('x') as target:
        json.dump(result, target, sort_keys=True, indent=2); target.flush(); os.fsync(target.fileno())
    print(json.dumps(result), flush=True)


if __name__ == '__main__': run(sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3]), Path(sys.argv[4]))
