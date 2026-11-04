"""Stage bounded selected data for the disposable Debian v8 installer lab."""

import gzip
import hashlib
import json
import shutil
from pathlib import Path


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
STAGE = LAB / 'provider-stage-v8'
FIREFOX_SOURCE = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7/provider-stage-v7/browser-profiles/AppData/Roaming/Mozilla/Firefox')
MEDIA = Path('/home/gillesduif/igloo-mint-22.3-lab/mint-selected-media-v14')
KERNEL = Path('/home/gillesduif/igloo-lab/debian-20261012/vmlinuz')
INITRD = Path('/home/gillesduif/igloo-lab/debian-20261012/initrd.gz')
EXPECTED_MEDIA = {
    'igloo-wallpaper.PNG': 'f3d180c7610e4a3fce2d54ed6681d12c8d202c4b949ab5a576c601e188efcac6',
    'igloo-avatar.jpg': 'ad1e7511713f030a8a1d73f36f26d70eda212a2b43e9ea8d2ad7b4e0271c4c4a',
}
EXPECTED_AGENT = 'abbe6eca30444a4d42403fd529ee95847f310e5c06305a26b15b82788bddff29'


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def member(name, body):
    fields = [0, 0o100644, 0, 0, 1, 0, len(body), 0, 0, 0, 0, len(name) + 1, 0]
    result = b'070701' + b''.join(f'{field:08X}'.encode() for field in fields)
    result += name.encode() + b'\0'
    result += b'\0' * (-len(result) % 4)
    result += body
    result += b'\0' * (-len(result) % 4)
    return result


def main():
    if not LAB.is_dir() or not STAGE.is_dir():
        raise RuntimeError('Fresh Debian v8 lab/provider stage does not exist')
    manifest = json.loads((LAB / 'manifest.json').read_text())
    identities = json.loads((LAB / 'base-identities.json').read_text())
    if (manifest['distroId'] != 'debian' or
            manifest['hardware']['targetDiskGuid'] != identities['selectedDiskGuid'] or
            manifest['hardware']['targetDiskBytes'] != 30 * 1024**3 or
            manifest['hardware']['needsNonFreeCodecs'] is not True or
            manifest['wallpaper']['fileName'] != 'igloo-wallpaper.PNG' or
            manifest['accountPicture']['fileName'] != 'igloo-avatar.jpg' or
            (STAGE / 'files').exists() or (STAGE / 'browser-profiles').exists() or
            any((STAGE / name).exists() for name in EXPECTED_MEDIA)):
        raise RuntimeError('Fresh v8 stage or selected manifest differs from the reviewed input')
    if sha(STAGE / 'migration-manifest.json') != sha(LAB / 'manifest.json'):
        raise RuntimeError('Staged manifest differs from the selected v8 manifest')
    if sha(STAGE / 'igloo-agent/agent.py') != EXPECTED_AGENT:
        raise RuntimeError('Provider stage does not contain the selected codec and Wi-Fi fixes')
    if sha(KERNEL) != '2b2358b37674d2505350528875bb17afae2a36522a9e8a9417eaca65a7da0e08':
        raise RuntimeError('Pinned Debian installer kernel changed')
    if sha(INITRD) != '5a9afef23f35af5ca68b6898f4d877a6ad434053bd2f738a4e58093736323838':
        raise RuntimeError('Pinned Debian installer initrd changed')
    if FIREFOX_SOURCE.is_symlink() or not FIREFOX_SOURCE.is_dir():
        raise RuntimeError('Selected small Firefox fixture is unavailable')

    for name, expected in EXPECTED_MEDIA.items():
        source = MEDIA / name
        if source.is_symlink() or not source.is_file() or sha(source) != expected:
            raise RuntimeError('Synthetic selected media source changed: ' + name)
        shutil.copyfile(source, STAGE / name)

    documents = STAGE / 'files/Documents'
    documents.mkdir(parents=True)
    (documents / 'igloo-debian-v8.txt').write_text('iGloo Debian v8 selected Documents marker\n')

    firefox = STAGE / 'browser-profiles/AppData/Roaming/Mozilla/Firefox'
    shutil.copytree(FIREFOX_SOURCE, firefox)
    profile = firefox / 'Profiles/igloolab.default'
    (profile / 'igloo-profile-marker.txt').write_text('selected Firefox profile marker: Debian v8\n')
    (profile / 'prefs.js').write_text('user_pref("igloo.debian_v8_marker", true);\n')

    chrome = STAGE / 'browser-profiles/AppData/Local/Google/Chrome/User Data/Default'
    (chrome / 'Sessions').mkdir(parents=True)
    (chrome / 'Bookmarks').write_text(json.dumps({
        'version': 1,
        'roots': {'bookmark_bar': {'type': 'folder', 'name': 'Bookmarks bar',
                                   'children': [{'type': 'url', 'name': 'iGloo v8 marker',
                                                 'url': 'https://example.invalid/igloo-debian-v8'}]}}
    }, sort_keys=True) + '\n')
    (chrome / 'Sessions/Session_123').write_bytes(b'iGloo Debian v8 selected Chromium session marker\n')

    names = [
        'preseed.cfg',
        'igloo-initrd/igloo-bound-free-setup.sh',
        'igloo-initrd/igloo-bound-free-choices.sh',
        'igloo-initrd/igloo-bound-free-do-option.sh',
        'igloo-initrd/igloo-verify-seed.py',
    ]
    archive = b''.join(member(Path(name).name, (STAGE / name).read_bytes()) for name in names)
    archive += member('TRAILER!!!', b'')
    injected = LAB / 'initrd-v8.gz'
    with injected.open('xb') as output, INITRD.open('rb') as source:
        shutil.copyfileobj(source, output)
        output.write(gzip.compress(archive, mtime=0))

    summary = {
        'injectedInitrdSha256': sha(injected),
        'preseedSha256': sha(STAGE / 'preseed.cfg'),
        'agentSha256': sha(STAGE / 'igloo-agent/agent.py'),
        'documentsSha256': sha(documents / 'igloo-debian-v8.txt'),
        'firefoxMarkerSha256': sha(profile / 'igloo-profile-marker.txt'),
        'chromeBookmarksSha256': sha(chrome / 'Bookmarks'),
        'chromeSessionSha256': sha(chrome / 'Sessions/Session_123'),
        'selectedMediaSha256': {name: sha(STAGE / name) for name in EXPECTED_MEDIA},
    }
    (LAB / 'payload-summary-v8.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
