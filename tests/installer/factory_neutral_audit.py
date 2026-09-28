"""Independent, read-only whole-tree factory identity/private-key audit.

Evidence contains paths, classifications and hashes, never private key/password
bytes. Signed package examples are not silently whitelisted. Unknown material
requires review. This detects known encodings, not arbitrary steganographic data.
"""
import base64
import bz2
import gzip
import hashlib
import lzma
from pathlib import Path
import re
import subprocess

from factory_root_observer import digest

SYSTEM_ACCOUNTS = frozenset("root daemon bin sys sync games man lp mail news uucp proxy www-data backup list irc _apt nobody messagebus avahi Debian-exim dnsmasq speech-dispatcher systemd-network cups-pk-helper saned dhcpcd systemd-timesync tss fwupd-refresh geoclue gnome-remote-desktop usbmux polkitd colord Debian-gdm rtkit".split())
STAR_ACCOUNTS = frozenset("root daemon bin sys sync games man lp mail news uucp proxy www-data backup list irc _apt nobody".split())
LOCKED_STAR_ACCOUNTS = frozenset("messagebus systemd-network systemd-timesync fwupd-refresh gnome-remote-desktop polkitd".split())
PUBLIC_PACKAGE_KEYS = {
    "/usr/lib/x86_64-linux-gnu/libgnutls.so.30.40.3": "4B14EDDE42E7CBEEFD8A6D88D035E096F2E6412A535388BB1D073A1C196E2822",
    "/usr/share/doc/libio-socket-ssl-perl/examples/simulate_proxy.pl": "BF6FCCA47A4B4245420813689304921EBD89E630A8B2A701E6C42F5E04A73E27",
}
# Independently matched against authenticated .deb members. These are PUBLIC
# self-test/example constants, never an installed machine's TLS/SSH identity.
# Both the exact path AND whole-file hash are required. Changed bytes fail closed.
FWUPD_RESOURCE = ("/usr/share/fwupd/uefi-capsule-ux.tar.xz", "70F33EAA6216C0B3D82C62342C9374F2B6D73576A44787E8FA94159B4478D3C2")
PRIVATE_PEM = re.compile(rb"-----BEGIN ((?:RSA |DSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY)-----[\r\n]+([a-zA-Z0-9+/=\r\n]{32,})-----END \1-----")


def encoded_private_key(data):
    for match in PRIVATE_PEM.finditer(data):
        try:
            decoded = base64.b64decode(re.sub(rb"\s", b"", match[2]), validate=True)
        except ValueError:
            continue
        if len(decoded) >= 16:
            return True
    return False


def audit(root, entries, forbidden_tokens):
    root = Path(root); findings = []; checked = 0; compressed = 0; der_probes = 0; public_constants = []
    for entry in entries:
        path = entry["Path"]
        if entry["Type"] != "File":
            continue
        filename = root / path[1:]
        # Scan every byte, including binary files, without logging file contents.
        tail = b""; starts = b""; is_private = False; matched = set()
        with filename.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                if not starts: starts = block[:16]
                data = tail + block
                matched.update(token.decode("ascii") for token in forbidden_tokens if token in data)
                is_private = is_private or encoded_private_key(data)
                tail = data[-32768:]
        checked += 1
        if matched: findings.append({"Path": path, "Kind": "FactoryIdentity", "Tokens": sorted(matched)})
        if is_private:
            if PUBLIC_PACKAGE_KEYS.get(path) == entry["Sha256"]:
                public_constants.append({"Path": path, "Sha256": entry["Sha256"], "Kind": "AuthenticatedPublicPackageTestConstant"})
            else:
                findings.append({"Path": path, "Kind": "PrivatePem"})
        # Read compressed documentation/state too. Exact size bounds fail closed,
        # rather than interpreting an unavailable scan as secret absence.
        opener = gzip.open if starts.startswith(b"\x1f\x8b") else lzma.open if starts.startswith(b"\xfd7zXZ\x00") else bz2.open if starts.startswith(b"BZh") else None
        if opener is not None:
            try:
                limit = 256 * 1024 * 1024 if (path, entry["Sha256"]) == FWUPD_RESOURCE else 64 * 1024 * 1024
                count = 0; bad = False; tail = b""
                with opener(filename, "rb") as stream:
                    while block := stream.read(1024 * 1024):
                        count += len(block); data = tail + block
                        bad = bad or encoded_private_key(data) or any(t in data for t in forbidden_tokens)
                        tail = data[-32768:]
                        if count > limit: break
                if count > limit:
                    findings.append({"Path": path, "Kind": "CompressedObservationLimit"})
                elif bad:
                    findings.append({"Path": path, "Kind": "CompressedPrivateKeyOrIdentity"})
                compressed += 1
            except (OSError, EOFError, lzma.LZMAError):
                findings.append({"Path": path, "Kind": "CompressedObservationUnavailable"})
        # DER keys do not have PEM delimiters. Probe plausible bounded ASN.1
        # sequences with the independent Debian OpenSSL parser, never execute files.
        if starts.startswith(b"\x30") and 32 <= entry["Length"] <= 1024 * 1024:
            result = subprocess.run(["/usr/bin/openssl", "pkey", "-inform", "DER", "-noout"],
                                    input=filename.read_bytes(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=15, check=False)
            der_probes += 1
            if result.returncode == 0: findings.append({"Path": path, "Kind": "PrivateDer"})
        if path.endswith((".p12", ".pfx", "/key4.db", "/secring.gpg")) or "/private-keys-v1.d/" in path:
            findings.append({"Path": path, "Kind": "PrivateContainerNeedsReview"})
    passwd = [line.split(":") for line in (root / "etc/passwd").read_text().splitlines()]
    shadow = [line.split(":") for line in (root / "etc/shadow").read_text().splitlines()]
    if (set(p[0] for p in passwd) != SYSTEM_ACCOUNTS or set(p[0] for p in shadow) != SYSTEM_ACCOUNTS or
            any(len(p) != 9 or p[1] != ("*" if p[0] in STAR_ACCOUNTS else "!*" if p[0] in LOCKED_STAR_ACCOUNTS else "!") for p in shadow) or
            any(len(p) != 7 or not p[2].isdigit() or (1000 <= int(p[2]) < 65534) for p in passwd)):
        findings.append({"Path": "/etc/passwd", "Kind": "AccountPolicyMismatch"})
    backup_counts = {}
    for name in ("passwd-", "shadow-", "gshadow", "gshadow-"):
        path = root / "etc" / name
        if not path.exists(): continue
        rows = [line.split(":") for line in path.read_text().splitlines()]; backup_counts[name] = len(rows)
        if name == "shadow-":
            valid = all(len(r) == 9 and r[0] in SYSTEM_ACCOUNTS and r[1] ==
                        ("*" if r[0] in STAR_ACCOUNTS else "!*" if r[0] in LOCKED_STAR_ACCOUNTS else "!") for r in rows)
        elif name == "passwd-":
            valid = all(len(r) == 7 and r[0] in SYSTEM_ACCOUNTS and r[1] == "x" and r[2].isdigit() and
                        (int(r[2]) < 1000 or int(r[2]) == 65534) for r in rows)
        else:
            groups = {r.split(":")[0] for r in (root / "etc/group").read_text().splitlines()}
            valid = all(len(r) == 4 and r[0] in groups and r[1] in ("!", "*", "!*", "x") and
                        all(user in SYSTEM_ACCOUNTS for field in r[2:] for user in field.split(",") if user) for r in rows)
        if not valid: findings.append({"Path": "/etc/" + name, "Kind": "CredentialBackupResidue"})
    passwords = root / "var/cache/debconf/passwords.dat"
    if passwords.exists() and any(line.startswith("Value:") and line[6:].strip() for line in passwords.read_text().splitlines()):
        findings.append({"Path": "/var/cache/debconf/passwords.dat", "Kind": "DebconfSecretResidue"})
    # Exact signed package source and complete filesystem equivalence supplement
    # these known-format scans. This is not a claim to identify arbitrary secrets.
    return {"SchemaVersion": 1, "RegularFilesScanned": checked, "CompressedFilesScanned": compressed,
            "DerPrivateKeyProbes": der_probes, "Findings": findings, "AccountCount": len(passwd),
            "PublicPackageTestConstants": public_constants,
            "CredentialBackupCounts": backup_counts,
            "PasswdSha256": digest(root / "etc/passwd"), "ShadowSha256": digest(root / "etc/shadow"),
            "ApprovedSystemAccountsOnly": not any(f["Kind"] == "AccountPolicyMismatch" for f in findings),
            "Qualified": not findings}
