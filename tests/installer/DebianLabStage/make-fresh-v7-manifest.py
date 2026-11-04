"""Create only the synthetic manifest for the disposable Debian v7 installer lab."""

import base64
import crypt
import hashlib
import json
import os
from pathlib import Path

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


LAB = Path("/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7")
PRIOR = Path("/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6")
LOGIN_PASSWORD = "igloolab123"  # Synthetic account and credential key on this disposable guest only.


def main() -> None:
    if os.geteuid() != 0 or LAB.resolve() != LAB or not LAB.is_dir():
        raise RuntimeError("Run as root against the exact disposable lab")
    manifest_path = LAB / "manifest.json"
    stage = LAB / "provider-stage-v7"
    if manifest_path.exists() or stage.exists():
        raise RuntimeError("The v7 manifest or provider stage already exists")
    identities = json.loads((LAB / "base-identities.json").read_text())
    manifest = json.loads((PRIOR / "manifest.json").read_text())
    if (manifest["distroId"] != "debian"
            or manifest["hardware"]["targetDiskGuid"] == identities["selectedDiskGuid"]
            or len(manifest["browsers"]) != 2):
        raise RuntimeError("Unexpected baseline or nonunique selected target")
    manifest["hardware"]["targetDiskGuid"] = identities["selectedDiskGuid"]
    manifest["hardware"]["targetDiskBytes"] = 30 * 1024**3
    manifest["hardware"]["linuxPartitionSizeGb"] = 20
    manifest["user"]["linuxPasswordCrypted"] = crypt.crypt(
        LOGIN_PASSWORD, crypt.mksalt(crypt.METHOD_SHA512))
    manifest["wifiNetworks"] = [{"ssid": "iGloo-v7-fixture", "security": "wpa-psk",
                                  "psk": "synthetic-v7-wifi-secret", "hidden": True}]
    manifest["suggestedPackages"] = [
        {"windowsAppName": "Google Chrome", "linuxAppName": "Google Chrome",
         "flatpakId": "com.google.Chrome", "autoInstall": True},
        {"windowsAppName": "Tree Fixture", "linuxAppName": "tree",
         "nativePackage": "tree", "autoInstall": True},
    ]

    chrome = next(browser for browser in manifest["browsers"]
                  if browser["name"] == "Google Chrome")
    salt, nonce = os.urandom(16), os.urandom(12)
    key = hashlib.pbkdf2_hmac("sha256", LOGIN_PASSWORD.encode(), salt, 600_000, 32)
    credential = {"browser": "Google Chrome", "logins": [{
        "url": "https://example.invalid/igloo-v7-login",
        "username": "synthetic-igloo-user-v7",
        "password": "synthetic-migrated-v7-secret",
    }], "cookies": []}
    body = json.dumps(credential, separators=(",", ":")).encode()
    chrome["credentialsBlob"] = base64.b64encode(
        b"IGCRD001" + salt + nonce + AESGCM(key).encrypt(nonce, body, None)
    ).decode()
    chrome["includesPasswords"] = True
    output = json.dumps(manifest, indent=2) + "\n"
    manifest_path.write_text(output)
    manifest_path.chmod(0o600)
    stage.mkdir(mode=0o700)
    print(json.dumps({"manifest": str(manifest_path),
                      "manifestSha256": hashlib.sha256(output.encode()).hexdigest(),
                      "selectedDiskGuid": identities["selectedDiskGuid"],
                      "selectedPackages": ["com.google.Chrome", "tree"],
                      "selectedBrowsers": [b["name"] for b in manifest["browsers"]]},
                     indent=2))


if __name__ == "__main__":
    main()
