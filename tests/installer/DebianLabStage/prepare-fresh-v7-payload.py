"""One-off, file-backed Debian v7 lab payload; never targets a host disk."""

from pathlib import Path
import gzip
import hashlib
import json
import shutil


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
STAGE = LAB / 'provider-stage-v7'
OLD = Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6/provider-stage-v6')
KERNEL = Path('/home/gillesduif/igloo-lab/debian-20261012/vmlinuz')
INITRD = Path('/home/gillesduif/igloo-lab/debian-20261012/initrd.gz')


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


assert sha(KERNEL) == '2b2358b37674d2505350528875bb17afae2a36522a9e8a9417eaca65a7da0e08'
assert sha(INITRD) == '5a9afef23f35af5ca68b6898f4d877a6ad434053bd2f738a4e58093736323838'
assert (STAGE / 'preseed.cfg').is_file()
assert (STAGE / 'files').exists() is False
assert (STAGE / 'browser-profiles').exists() is False

docs = STAGE / 'files/Documents'
docs.mkdir(parents=True)
(docs / 'igloo-debian-v7.txt').write_text('iGloo Debian v7 selected Documents marker\n')

firefox = STAGE / 'browser-profiles/AppData/Roaming/Mozilla/Firefox'
shutil.copytree(OLD / 'browser-profiles/AppData/Roaming/Mozilla/Firefox', firefox)
profile = firefox / 'Profiles/igloolab.default'
(profile / 'igloo-profile-marker.txt').write_text('selected Firefox profile marker: Debian v7\n')
(profile / 'prefs.js').write_text('user_pref("igloo.debian_v7_marker", true);\n')

chrome = STAGE / 'browser-profiles/AppData/Local/Google/Chrome/User Data/Default'
(chrome / 'Sessions').mkdir(parents=True)
(chrome / 'Bookmarks').write_text(json.dumps({
    'version': 1,
    'roots': {'bookmark_bar': {'type': 'folder', 'name': 'Bookmarks bar',
                              'children': [{'type': 'url', 'name': 'iGloo v7 marker',
                                            'url': 'https://example.invalid/igloo-debian-v7'}]}}
}, sort_keys=True) + '\n')
(chrome / 'Sessions/Session_123').write_bytes(b'iGloo Debian v7 selected Chromium session marker\n')


def member(name, body):
    fields = [0, 0o100644, 0, 0, 1, 0, len(body), 0, 0, 0, 0, len(name) + 1, 0]
    result = b'070701' + b''.join(f'{field:08X}'.encode() for field in fields)
    result += name.encode() + b'\0'
    result += b'\0' * (-len(result) % 4)
    result += body
    result += b'\0' * (-len(result) % 4)
    return result


names = [
    'preseed.cfg',
    'igloo-initrd/igloo-bound-free-setup.sh',
    'igloo-initrd/igloo-bound-free-choices.sh',
    'igloo-initrd/igloo-bound-free-do-option.sh',
    'igloo-initrd/igloo-verify-seed.py',
]
archive = b''.join(member(Path(name).name, (STAGE / name).read_bytes()) for name in names)
archive += member('TRAILER!!!', b'')
target = LAB / 'initrd-v7.gz'
with target.open('xb') as output, INITRD.open('rb') as source:
    shutil.copyfileobj(source, output)
    output.write(gzip.compress(archive, mtime=0))

summary = {
    'sourceKernelSha256': sha(KERNEL),
    'sourceInitrdSha256': sha(INITRD),
    'injectedInitrdSha256': sha(target),
    'preseedSha256': sha(STAGE / 'preseed.cfg'),
    'agentSha256': sha(STAGE / 'igloo-agent/agent.py'),
    'documentsSha256': sha(docs / 'igloo-debian-v7.txt'),
    'firefoxMarkerSha256': sha(profile / 'igloo-profile-marker.txt'),
    'chromeBookmarksSha256': sha(chrome / 'Bookmarks'),
    'chromeSessionSha256': sha(chrome / 'Sessions/Session_123'),
}
(LAB / 'payload-summary-v7.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
