# Pinning distro signing keys (the trust anchor)

iGloo verifies every downloaded ISO's checksum file with GPG. The **signing key
is the trust anchor** - it must be established out-of-band (not fetched and
trusted blindly from a keyserver). Two knobs in each `distro.json` control this:

```jsonc
"iso": {
  ...
  "gpgKeyFile":        "keys/debian-cd.asc",          // bundled key (preferred)
  "gpgKeyFingerprint": "AAAA BBBB CCCC ... (40 hex)"   // pinned 160-bit fingerprint
}
```

Behaviour:

| `gpgKeyFile` | `gpgKeyFingerprint` | Result |
|---|---|---|
| set | set | **Strongest.** Uses the bundled key; the signing key must match the pinned fingerprint. No keyserver. |
| unset | set | Fetches `gpgKeyUrl`, but the key **must** match the pinned fingerprint (a forged/substituted key is rejected). |
| unset | unset | Fetches `gpgKeyUrl` and trusts it (current fallback - weakest; avoid for releases). |

A **wrong** fingerprint can never make verification accept a bad key - it only
rejects. So pinning fails closed.

**Enforcement (2026-07):** ISO acquisition is fail-closed. When a distro declares
GPG (signature URL + key source), a failed signature ABORTS the install - it never
degrades to a warning. A SHA-256 must be available (manifest pin or signed checksum
file) or acquisition aborts; if both exist they must agree. All artefact URLs must
be HTTPS. Current pins (validated live against the shipping verifiers):

| distro | pinned fingerprint | cross-checked against |
|---|---|---|
| fedora-kde / fedora-workstation (F44) | `36F612DCF27F7D1A48A835E4DBFCF71C6D9F90A6` | fedoraproject.org/security |
| debian | `DF9B9C49EAA9298432589D76DA87E80D6294BE9B` | debian.org/CD/verify |
| ubuntu | `843938DF228D22F7B3742BC0D94AA3F0EFE21092` | ubuntu.com (key URL carries the full fp) |
| linuxmint-cinnamon | `27DEB15644C6B3CF3BD7D291300F846BA25BAE09` | linuxmint.com/verify.php |

> **Mint keyserver gotcha - do not "fix" back:** Mint's `gpgKeyUrl` uses
> `keys.openpgp.org/vks/v1/by-fingerprint/…`, NOT `keyserver.ubuntu.com`. The SKS
> keyserver attaches every third-party certification ever made on the key, and one
> of those is an OpenPGP v5 signature packet that BouncyCastle 2.3.1 cannot parse
> (`UnsupportedPacketVersionException`) - key parsing dies and verification fails.
> keys.openpgp.org serves the key with self-signatures only, which parses cleanly.
> The Fedora releases also rotate signing keys: bump `gpgKeyFingerprint` together
> with the release URLs (the pin is per-release).

## How to establish the anchor (do this on a trusted machine with `gpg`)

### Debian
```bash
# 1. Fetch the Debian CD signing key
curl -fsSL "https://keyring.debian.org/pks/lookup?op=get&options=mr&search=0xDA87E80D6294BE9B" -o debian-cd.asc

# 2. Read its fingerprint and CONFIRM it against debian.org/CD/verify
gpg --show-keys --with-fingerprint debian-cd.asc
#    Cross-check the printed fingerprint with https://www.debian.org/CD/verify

# 3. Bundle + pin
mkdir -p distros/debian/keys && cp debian-cd.asc distros/debian/keys/
#    set  "gpgKeyFile": "keys/debian-cd.asc"
#    set  "gpgKeyFingerprint": "<the verified fingerprint>"
```

### Ubuntu
```bash
gpg --keyserver keyserver.ubuntu.com --recv-keys 843938DF228D22F7B3742BC0D94AA3F0EFE21092
gpg --export --armor 843938DF228D22F7B3742BC0D94AA3F0EFE21092 > distros/ubuntu/keys/ubuntu-cd.asc
gpg --show-keys --with-fingerprint distros/ubuntu/keys/ubuntu-cd.asc
#   confirm against https://ubuntu.com/tutorials/how-to-verify-ubuntu  → pin it
```

### Linux Mint
```bash
gpg --keyserver keyserver.ubuntu.com --recv-keys 27DEB15644C6B3CF3BD7D291300F846BA25BAE09
gpg --export --armor 27DEB15644C6B3CF3BD7D291300F846BA25BAE09 > distros/linuxmint-cinnamon/keys/mint.asc
gpg --show-keys --with-fingerprint distros/linuxmint-cinnamon/keys/mint.asc
#   confirm against linuxmint.com/verify.php  → pin it
```

> The key IDs above are starting points; the **fingerprint you cross-check on the
> vendor's HTTPS page is the real anchor**. For published builds, add
> `keys/**` to the distro `.csproj` `<Content>` so the key is copied next to the exe.
