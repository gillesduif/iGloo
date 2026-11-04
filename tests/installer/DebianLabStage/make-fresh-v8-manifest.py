"""Create the disposable Debian v8 manifest after its fresh GPT identities exist."""

import base64
import crypt
import hashlib
import json
import os
from pathlib import Path

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
PRIOR = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
LOGIN_PASSWORD = 'igloolab123'  # Synthetic account and credential key in this lab only.


def main():
    if os.geteuid() != 0 or LAB.resolve() != LAB or not LAB.is_dir():
        raise RuntimeError('Run as root against the exact disposable v8 lab')
    identities = json.loads((LAB / 'base-identities.json').read_text())
    manifest_path = LAB / 'manifest.json'
    stage = LAB / 'provider-stage-v8'
    if manifest_path.exists() or stage.exists():
        raise RuntimeError('Fresh v8 manifest or provider stage already exists')
    prior = json.loads((PRIOR / 'manifest.json').read_text())
    if (prior['distroId'] != 'debian' or prior['hardware']['installMode'] != 'dual-boot'
            or identities['selectedDiskGuid'] == prior['hardware']['targetDiskGuid']
            or len(prior['browsers']) != 2):
        raise RuntimeError('Baseline or new selected GPT identity is unexpected')

    prior['hardware']['targetDiskGuid'] = identities['selectedDiskGuid']
    prior['hardware']['targetDiskBytes'] = 30 * 1024**3
    prior['hardware']['linuxPartitionSizeGb'] = 20
    # Match ManifestGeneratorService's real Community default; v7 explicitly
    # disabled codecs and therefore did not exercise this first-boot step.
    prior['hardware']['needsNonFreeCodecs'] = True
    prior['user']['linuxPasswordCrypted'] = crypt.crypt(
        LOGIN_PASSWORD, crypt.mksalt(crypt.METHOD_SHA512))
    if crypt.crypt(LOGIN_PASSWORD, prior['user']['linuxPasswordCrypted']) != prior['user']['linuxPasswordCrypted']:
        raise RuntimeError('Synthetic Linux login hash does not match the envelope key')
    prior['wifiNetworks'] = [
        {'ssid': 'iGloo-v8-fixture', 'security': 'wpa-psk',
         'psk': 'synthetic-v8-wifi-secret', 'hidden': True},
        {'ssid': 'iGloo-v8-promptable', 'security': 'wpa-psk',
         'psk': None, 'hidden': False},
    ]
    prior['suggestedPackages'] = [
        {'windowsAppName': 'Google Chrome', 'linuxAppName': 'Google Chrome',
         'flatpakId': 'com.google.Chrome', 'autoInstall': True},
        {'windowsAppName': 'Tree Fixture', 'linuxAppName': 'tree',
         'nativePackage': 'tree', 'autoInstall': True},
    ]
    prior['wallpaper'] = {'fileName': 'igloo-wallpaper.PNG'}
    prior['accountPicture'] = {'fileName': 'igloo-avatar.jpg'}

    chrome = next(browser for browser in prior['browsers'] if browser['name'] == 'Google Chrome')
    salt, nonce = os.urandom(16), os.urandom(12)
    key = hashlib.pbkdf2_hmac('sha256', LOGIN_PASSWORD.encode(), salt, 600_000, 32)
    credential = {'browser': 'Google Chrome', 'logins': [{
        'url': 'https://example.invalid/igloo-v8-login',
        'username': 'synthetic-igloo-user-v8',
        'password': 'synthetic-migrated-v8-secret',
    }], 'cookies': []}
    body = json.dumps(credential, separators=(',', ':')).encode()
    chrome['credentialsBlob'] = base64.b64encode(
        b'IGCRD001' + salt + nonce + AESGCM(key).encrypt(nonce, body, None)).decode()
    chrome['includesPasswords'] = True

    output = json.dumps(prior, indent=2) + '\n'
    manifest_path.write_text(output)
    manifest_path.chmod(0o600)
    stage.mkdir(mode=0o700)
    print(json.dumps({'manifest': str(manifest_path),
                      'manifestSha256': hashlib.sha256(output.encode()).hexdigest(),
                      'selectedDiskGuid': identities['selectedDiskGuid'],
                      'needsNonFreeCodecs': True,
                      'selectedPackages': ['com.google.Chrome', 'tree'],
                      'selectedMedia': ['igloo-wallpaper.PNG', 'igloo-avatar.jpg'],
                      'credentialEnvelopeGenerated': True}, indent=2))


if __name__ == '__main__':
    main()
