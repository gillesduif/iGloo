"""Development copy of the Chromium credential import section for the agents.

The text between the SECTION markers is inlined verbatim into
distros/_debian-family/agent/agent.py and distros/fedora-kde/agent/agent.py
("keep in sync" convention, same as the Wi-Fi section). This copy exists so
the section can be KAT- and integration-tested standalone.
"""

# === BEGIN AGENT SECTION =================================================
# ---------------------------------------------------------------------------
# Chromium credential import (browser migration Phase 2, ADR-011)
#
# KEEP IN SYNC between distros/_debian-family/agent/agent.py and
# distros/fedora-kde/agent/agent.py (same convention as the Wi-Fi section).
#
# The Windows side decrypts the selected Chromium browsers' saved passwords
# (DPAPI unlocks only in the user's logon session) and stages them inside the
# manifest as browsers[].credentialsBlob: base64 of the envelope
#     "IGCRD001" | salt(16) | nonce(12) | AES-256-GCM(ciphertext)||tag(16)
# keyed by PBKDF2-HMAC-SHA256(linuxPassword, salt, 600_000 iterations).
#
# The key is the user's Linux password, which the first-boot agent does not
# have and must not have: putting it in the manifest would leave it in clear
# text on the FAT32 seed partition. So the work is split in two. As root at
# first boot, stage_credential_import() moves the envelopes into the user's
# own home and installs a login hook. At the user's first graphical login,
# run_user_credential_import() asks for the password once, decrypts, inserts
# the rows into the Linux browser's Login Data using Chromium's Linux "v10"
# encoding, and deletes the envelopes.
#
# Everything below is pure stdlib on purpose: the Debian offline first boot
# has no network and cannot install python3-cryptography. The embedded
# pure-Python AES is gated by self_test() against published known-answer
# vectors (FIPS-197, NIST SP 800-38A, McGrew-Viega GCM); any failure disables
# the step rather than risking malformed output on real credentials.
# Credential values are never logged.
# ---------------------------------------------------------------------------

import base64
import hashlib
import hmac as hmac_mod
import json
import os
import shutil
import sqlite3
import subprocess
import time
import urllib.parse
from pathlib import Path


def _gf_mul(a: int, b: int) -> int:
    """Multiply in GF(2^8) modulo the Rijndael polynomial x^8+x^4+x^3+x+1."""
    p = 0
    for _ in range(8):
        if b & 1:
            p ^= a
        carry = a & 0x80
        a = (a << 1) & 0xFF
        if carry:
            a ^= 0x1B
        b >>= 1
    return p


def _gf_pow(a: int, n: int) -> int:
    r = 1
    while n:
        if n & 1:
            r = _gf_mul(r, a)
        a = _gf_mul(a, a)
        n >>= 1
    return r


def _rotl8(x: int, n: int) -> int:
    return ((x << n) | (x >> (8 - n))) & 0xFF


def _build_sbox() -> list[int]:
    # S-box computed from its definition (GF(2^8) inverse + affine transform):
    # no 256-entry table that could be corrupted in transcription.
    box = []
    for x in range(256):
        inv = 0 if x == 0 else _gf_pow(x, 254)
        box.append(inv ^ _rotl8(inv, 1) ^ _rotl8(inv, 2)
                   ^ _rotl8(inv, 3) ^ _rotl8(inv, 4) ^ 0x63)
    return box


_SBOX = _build_sbox()


def _expand_key(key: bytes) -> list[list[int]]:
    """FIPS-197 key schedule. Returns Nr+1 round keys, each 16 bytes."""
    nk = len(key) // 4
    if nk not in (4, 8):
        raise ValueError("only AES-128 and AES-256 keys are supported")
    nr = nk + 6
    w = [list(key[4 * i:4 * i + 4]) for i in range(nk)]
    rcon = 1
    for i in range(nk, 4 * (nr + 1)):
        t = list(w[i - 1])
        if i % nk == 0:
            t = [_SBOX[t[1]] ^ rcon, _SBOX[t[2]], _SBOX[t[3]], _SBOX[t[0]]]
            rcon = _gf_mul(rcon, 2)
        elif nk == 8 and i % nk == 4:
            t = [_SBOX[b] for b in t]
        w.append([w[i - nk][j] ^ t[j] for j in range(4)])
    return [[b for word in w[4 * r:4 * r + 4] for b in word]
            for r in range(nr + 1)]


def _add_round_key(s: list[int], rk: list[int]) -> list[int]:
    return [s[i] ^ rk[i] for i in range(16)]


def _shift_rows(s: list[int]) -> list[int]:
    # Column-major flat state s[4*c + r]: row r rotates left by r columns.
    return [s[4 * ((c + r) % 4) + r] for c in range(4) for r in range(4)]


def _mix_columns(s: list[int]) -> list[int]:
    out = [0] * 16
    for c in range(4):
        a0, a1, a2, a3 = s[4 * c], s[4 * c + 1], s[4 * c + 2], s[4 * c + 3]
        out[4 * c] = _gf_mul(a0, 2) ^ _gf_mul(a1, 3) ^ a2 ^ a3
        out[4 * c + 1] = a0 ^ _gf_mul(a1, 2) ^ _gf_mul(a2, 3) ^ a3
        out[4 * c + 2] = a0 ^ a1 ^ _gf_mul(a2, 2) ^ _gf_mul(a3, 3)
        out[4 * c + 3] = _gf_mul(a0, 3) ^ a1 ^ a2 ^ _gf_mul(a3, 2)
    return out


def aes_encrypt_block(key: bytes, block: bytes) -> bytes:
    """Encrypt one 16-byte block with AES-128 or AES-256."""
    if len(block) != 16:
        raise ValueError("AES operates on 16-byte blocks")
    rks = _expand_key(key)
    nr = len(rks) - 1
    s = _add_round_key(list(block), rks[0])
    for rnd in range(1, nr):
        s = _mix_columns(_shift_rows([_SBOX[b] for b in s]))
        s = _add_round_key(s, rks[rnd])
    s = _shift_rows([_SBOX[b] for b in s])
    return bytes(_add_round_key(s, rks[nr]))


def aes_cbc_encrypt(key: bytes, iv: bytes, data: bytes) -> bytes:
    """AES-CBC with PKCS#7 padding (Chromium Linux "v10" password encoding)."""
    if len(iv) != 16:
        raise ValueError("CBC IV must be 16 bytes")
    pad = 16 - (len(data) % 16)
    data = data + bytes([pad]) * pad
    out = bytearray()
    prev = iv
    for off in range(0, len(data), 16):
        block = bytes(data[off + i] ^ prev[i] for i in range(16))
        prev = aes_encrypt_block(key, block)
        out += prev
    return bytes(out)


def _gcm_gf_mul(x: int, y: int) -> int:
    """Multiply in GF(2^128) with the GCM bit ordering."""
    r = 0xE1000000000000000000000000000000
    z = 0
    v = y
    for i in range(128):
        if (x >> (127 - i)) & 1:
            z ^= v
        v = (v >> 1) ^ (r if v & 1 else 0)
    return z


def _gcm_ghash(h: int, data: bytes) -> int:
    y = 0
    for off in range(0, len(data), 16):
        block = data[off:off + 16]
        y = _gcm_gf_mul(y ^ int.from_bytes(block, "big"), h)
    return y


def _inc32(ctr: bytes) -> bytes:
    head, tail = ctr[:12], int.from_bytes(ctr[12:], "big")
    return head + ((tail + 1) & 0xFFFFFFFF).to_bytes(4, "big")


def _gcm_gctr(key: bytes, icb: bytes, data: bytes) -> bytes:
    out = bytearray()
    ctr = icb
    for off in range(0, len(data), 16):
        block = data[off:off + 16]
        ks = aes_encrypt_block(key, ctr)
        out += bytes(block[i] ^ ks[i] for i in range(len(block)))
        ctr = _inc32(ctr)
    return bytes(out)


def _pad16(b: bytes) -> bytes:
    return b + b"\x00" * ((-len(b)) % 16)


def aes_gcm_decrypt(key: bytes, nonce: bytes, ct_and_tag: bytes,
                    aad: bytes = b"") -> bytes:
    """Decrypt AES-GCM; raises ValueError on tag mismatch. 96-bit nonces."""
    if len(nonce) != 12:
        raise ValueError("GCM nonce must be 12 bytes")
    if len(ct_and_tag) < 16:
        raise ValueError("GCM input shorter than the tag")
    ct, tag = ct_and_tag[:-16], ct_and_tag[-16:]
    h = int.from_bytes(aes_encrypt_block(key, b"\x00" * 16), "big")
    j0 = nonce + b"\x00\x00\x00\x01"
    lens = (len(aad) * 8).to_bytes(8, "big") + (len(ct) * 8).to_bytes(8, "big")
    s = _gcm_ghash(h, _pad16(aad) + _pad16(ct) + lens)
    expected = _gcm_gctr(key, j0, s.to_bytes(16, "big"))
    if not hmac_mod.compare_digest(expected, tag):
        raise ValueError("GCM tag mismatch (wrong key or corrupted data)")
    return _gcm_gctr(key, _inc32(j0), ct)


def _aes_self_test() -> None:
    """Known-answer tests; must pass before any real credential is touched."""
    # FIPS-197 Appendix C: AES-128 and AES-256 block vectors.
    pt = bytes.fromhex("00112233445566778899aabbccddeeff")
    assert aes_encrypt_block(
        bytes.fromhex("000102030405060708090a0b0c0d0e0f"), pt
    ).hex() == "69c4e0d86a7b0430d8cdb78070b4c55a", "AES-128 block KAT failed"
    assert aes_encrypt_block(
        bytes.fromhex("000102030405060708090a0b0c0d0e0f"
                      "101112131415161718191a1b1c1d1e1f"), pt
    ).hex() == "8ea2b7ca516745bfeafc49904b496089", "AES-256 block KAT failed"

    # NIST SP 800-38A F.2.1: CBC-AES128, first block.
    ct1 = aes_cbc_encrypt(
        bytes.fromhex("2b7e151628aed2a6abf7158809cf4f3c"),
        bytes.fromhex("000102030405060708090a0b0c0d0e0f"),
        bytes.fromhex("6bc1bee22e409f96e93d7e117393172a01"))  # 17B: 1B pad
    assert ct1[:16].hex() == "7649abac8119b246cee98e9b12e9197d", \
        "AES-CBC KAT failed"

    # McGrew & Viega GCM test case 2 (AES-128, no AAD), decrypt direction:
    # the tag must verify and the plaintext must come back exactly.
    k128 = b"\x00" * 16
    iv = b"\x00" * 12
    known_ct = bytes.fromhex("0388dace60b6a392f328c2b971b2fe78"
                             "ab6e47d42cec13bdf53a67b21257bddf")
    assert aes_gcm_decrypt(k128, iv, known_ct) == b"\x00" * 16, \
        "GCM-128 decrypt KAT failed"
    bad = bytearray(known_ct)
    bad[0] ^= 1
    try:
        aes_gcm_decrypt(k128, iv, bytes(bad))
        raise AssertionError("GCM accepted a corrupted ciphertext")
    except ValueError:
        pass

    # GCM test case 5 (AES-256, with AAD), from the GCM revised spec.
    k256 = bytes.fromhex("feffe9928665731c6d6a8f9467308308"
                         "feffe9928665731c6d6a8f9467308308")
    ct5 = bytes.fromhex(
        "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa"
        "8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662"
        "76fc6ece0f4e1768cddf8853bb2d551b")
    pt5 = bytes.fromhex(
        "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a72"
        "1c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39")
    aad5 = bytes.fromhex("feedfacedeadbeeffeedfacedeadbeefabaddad2")
    assert aes_gcm_decrypt(k256, bytes.fromhex("cafebabefacedbaddecaf888"),
                           ct5, aad5) == pt5, "GCM-256 decrypt KAT failed"


_CHROMIUM_ENVELOPE_MAGIC = b"IGCRD001"
_CHROMIUM_PBKDF2_ITERATIONS = 600_000

# Browser display name (as the Windows wizard records it) to its Flathub id, its
# config directory relative to XDG_CONFIG_HOME, and the commands a distro package
# installs. iGloo installs these as Flatpaks, but the user may already run a
# packaged build, so both forms have to be reachable.
_CHROMIUM_LINUX_DIRS = {
    "Google Chrome": ("com.google.Chrome", "google-chrome",
                      ("google-chrome", "google-chrome-stable")),
    "Microsoft Edge": ("com.microsoft.Edge", "microsoft-edge",
                       ("microsoft-edge", "microsoft-edge-stable")),
    "Brave": ("com.brave.Browser", "BraveSoftware/Brave-Browser",
              ("brave-browser", "brave")),
    "Vivaldi": ("com.vivaldi.Vivaldi", "vivaldi",
                ("vivaldi", "vivaldi-stable")),
    "Opera": ("com.opera.Opera", "opera", ("opera",)),
}


def _native_config_home(home: Path | None) -> Path:
    # XDG_CONFIG_HOME is only meaningful for the user's own session. The
    # first-boot agent runs as root and passes the home explicitly, where
    # root's environment would point at the wrong place entirely.
    if home is not None:
        return home / ".config"
    return Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config")


def _chromium_config_homes(app_id: str, binaries: tuple[str, ...],
                           home: Path | None = None) -> list[Path]:
    """Every config root this browser could read, most likely first.

    Flatpak overrides XDG_CONFIG_HOME, so a Flatpak build never sees ~/.config
    and a packaged build never sees ~/.var/app. Picking one means guessing, and
    a wrong guess writes the passwords where nothing reads them - so write to
    each root that has a matching install, and to ~/.config when neither does.
    """
    base = home or Path.home()
    roots: list[Path] = []
    if shutil.which("flatpak") and subprocess.run(
            ["flatpak", "info", app_id],
            capture_output=True, text=True, check=False).returncode == 0:
        roots.append(base / ".var" / "app" / app_id / "config")
    if any(shutil.which(b) for b in binaries):
        roots.append(_native_config_home(home))
    return roots or [_native_config_home(home)]

# Chromium timestamps count microseconds since 1601-01-01 UTC.
_CHROMIUM_EPOCH_OFFSET_US = 11644473600 * 1_000_000

# The logins table exactly as Chromium's own builder produces it at schema
# version 31, and labelled 31 - the two must agree. Labelling a modern table as
# an old version is what emptied Brave on 2026-08-21: the stored version drives
# LoginDatabase::MigrateDatabase, which then runs "ALTER TABLE logins ADD COLUMN
# possible_username_pairs" (a version 19 step) against a column that is already
# there, the migration fails, Init returns false and the caller recreates the
# file from scratch.
#
# 31 is chosen because every later step only ADDS columns (37, 39, 41, 42, 43),
# so any Brave from 31 onwards migrates this table upwards cleanly. Chromium
# creates the logins table before migrating and insecure_credentials /
# password_notes after it, so the tables we leave out are not our problem.
# See docs/reference/browser-migration.md.
_LOGINS_SCHEMA_VERSION = 31
_LOGINS_SCHEMA = f"""
CREATE TABLE IF NOT EXISTS logins (
    origin_url VARCHAR NOT NULL,
    action_url VARCHAR,
    username_element VARCHAR,
    username_value VARCHAR,
    password_element VARCHAR,
    password_value BLOB,
    submit_element VARCHAR,
    signon_realm VARCHAR NOT NULL,
    date_created INTEGER NOT NULL,
    blacklisted_by_user INTEGER NOT NULL,
    scheme INTEGER NOT NULL,
    password_type INTEGER,
    times_used INTEGER,
    form_data BLOB,
    display_name VARCHAR,
    icon_url VARCHAR,
    federation_url VARCHAR,
    skip_zero_click INTEGER,
    generation_upload_status INTEGER,
    possible_username_pairs BLOB,
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date_last_used INTEGER NOT NULL DEFAULT 0,
    moving_blocked_for BLOB,
    date_password_modified INTEGER NOT NULL DEFAULT 0,
    UNIQUE (origin_url, username_element, username_value, password_element,
            signon_realm)
);
CREATE INDEX IF NOT EXISTS logins_signon ON logins (signon_realm);
CREATE TABLE IF NOT EXISTS meta (
    key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY,
    value LONGVARCHAR
);
INSERT OR IGNORE INTO meta(key, value)
    VALUES ('version', '{_LOGINS_SCHEMA_VERSION}'),
           ('last_compatible_version', '{_LOGINS_SCHEMA_VERSION}');
"""


def _chromium_v10_encrypt_bytes(data: bytes) -> bytes:
    """Encode a value the way Linux Chromium stores it: "v10" ||
    AES-128-CBC(PKCS7), key PBKDF2-HMAC-SHA1("peanuts", "saltysalt", 1
    iteration), IV of 16 spaces. This scheme is Chromium's documented fallback
    when no desktop keyring holds the key."""
    key = hashlib.pbkdf2_hmac("sha1", b"peanuts", b"saltysalt", 1, 16)
    return b"v10" + aes_cbc_encrypt(key, b" " * 16, data)


def _chromium_v10_encrypt(password: str) -> bytes:
    return _chromium_v10_encrypt_bytes(password.encode("utf-8"))


# The cookie jar moved under Network/ in Chromium 77; both layouts still exist.
_COOKIE_DB_NAMES = ("Network/Cookies", "Cookies")


def _reencrypt_cookies(profile_dir: Path, cookies: list[dict]) -> int:
    """Rewrite a copied Cookies database so the Linux browser can read it.

    The file came off the Windows profile verbatim, so its rows are still
    encrypted under the Windows master key and its schema is a real Chromium's
    rather than one we reconstruct. Only encrypted_value changes. Rows we have
    no plaintext for are deleted: a cookie the browser cannot decrypt keeps the
    user logged out while looking like it should not.
    """
    db = next((profile_dir / n for n in _COOKIE_DB_NAMES
               if (profile_dir / n).is_file()), None)
    if db is None:
        return 0

    con = sqlite3.connect(db)
    try:
        updated = 0
        for c in cookies:
            try:
                value = base64.b64decode(c.get("value", ""))
            except (ValueError, TypeError):
                continue
            cur = con.execute(
                "UPDATE cookies SET encrypted_value = ? "
                "WHERE host_key = ? AND name = ? AND path = ?",
                (_chromium_v10_encrypt_bytes(value), c.get("host", ""),
                 c.get("name", ""), c.get("path", "")))
            updated += cur.rowcount

        # Both platforms use the "v10" prefix, so the rows we rewrote cannot be
        # told apart from the ones we did not. Name them instead.
        con.execute("CREATE TEMP TABLE igloo_keep (h TEXT, n TEXT, p TEXT)")
        con.executemany(
            "INSERT INTO igloo_keep VALUES (?, ?, ?)",
            [(c.get("host", ""), c.get("name", ""), c.get("path", "")) for c in cookies])
        con.execute(
            "DELETE FROM cookies WHERE NOT EXISTS ("
            "  SELECT 1 FROM igloo_keep k WHERE k.h = cookies.host_key"
            "    AND k.n = cookies.name AND k.p = cookies.path)")
        con.commit()
        return updated
    finally:
        con.close()


def _decrypt_envelope(blob_b64: str, password: str) -> dict:
    raw = base64.b64decode(blob_b64)
    if raw[:8] != _CHROMIUM_ENVELOPE_MAGIC:
        raise ValueError("credential envelope has a bad magic header")
    key = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"),
                              raw[8:24], _CHROMIUM_PBKDF2_ITERATIONS, 32)
    return json.loads(aes_gcm_decrypt(key, raw[24:36], raw[36:]).decode("utf-8"))


def _login_row_values(url: str, username: str, v10_blob: bytes,
                      now_us: int) -> dict:
    """Every column the importer can fill. Columns a given Chromium version
    does not have are dropped by the caller via PRAGMA table_info."""
    parsed = urllib.parse.urlsplit(url)
    signon_realm = f"{parsed.scheme}://{parsed.netloc}/"
    return {
        "origin_url": url,
        "action_url": "",
        "username_element": "",
        "username_value": username,
        "password_element": "",
        "password_value": v10_blob,
        "submit_element": "",
        "signon_realm": signon_realm,
        "date_created": now_us,
        "date_last_used": now_us,
        "date_password_modified": now_us,
        "blacklisted_by_user": 0,
        "scheme": 0,
        "password_type": 0,
        "times_used": 1,
        "form_data": b"",
        "display_name": "",
        "icon_url": "",
        "federation_url": "",
        "skip_zero_click": 0,
        "generation_upload_status": 0,
        "possible_username_pairs": b"",
        "moving_blocked_for": b"",
        # Columns added after version 31; dropped again for a database that
        # predates them, by the PRAGMA table_info filter in the caller.
        "sender_email": "",
        "sender_name": "",
        "sender_profile_image_url": "",
        "date_received": 0,
        "sharing_notification_displayed": 0,
        "keychain_identifier": "",
        "sender_app_id": "",
    }


def _import_into_login_data(db_path: Path, logins: list[dict]) -> int:
    """Insert logins into a Chromium Login Data database. Returns the number
    of rows inserted. Existing (origin_url, username_value) pairs are kept,
    so re-running the agent never duplicates entries."""
    is_new_db = not db_path.exists()
    con = sqlite3.connect(db_path)
    try:
        if is_new_db:
            con.executescript(_LOGINS_SCHEMA)

        # PRAGMA table_info: (cid, name, type, notnull, dflt_value, pk).
        table_cols = {row[1]: row for row in con.execute("PRAGMA table_info(logins)")}
        fillable_probe = _login_row_values("", "", b"", 0)
        required = [name for name, row in table_cols.items()
                    if row[3] and row[4] is None
                    and name not in fillable_probe and name != "id"]
        if required:
            logger.warning("logins table in %s has unsupported NOT NULL columns "
                           "%s - skipping credential import for this browser",
                           db_path, required)
            return 0

        existing = {(r[0], r[1]) for r in con.execute(
            "SELECT origin_url, username_value FROM logins")}

        now_us = int(time.time() * 1_000_000) + _CHROMIUM_EPOCH_OFFSET_US
        inserted = 0
        for login in logins:
            url = login.get("url", "")
            username = login.get("username", "")
            password = login.get("password", "")
            if not url or not username or not password:
                continue
            if (url, username) in existing:
                continue
            values = {k: v for k, v in
                      _login_row_values(url, username,
                                        _chromium_v10_encrypt(password), now_us)
                      .items() if k in table_cols}
            con.execute(
                f"INSERT INTO logins ({', '.join(values)}) "
                f"VALUES ({', '.join('?' * len(values))})",
                tuple(values.values()))
            existing.add((url, username))
            inserted += 1
        con.commit()
        os.chmod(db_path, 0o600)
        return inserted
    finally:
        con.close()


# Where the envelopes wait between first boot and the user's first login,
# and how many logins we keep asking before giving up and deleting them.
_CRED_STORE_REL = ".local/share/igloo/credentials.json"
_CRED_MAX_ATTEMPTS = 3


def stage_credential_import(manifest: dict) -> None:
    """Move the staged credential envelopes into the user's home and install a
    login hook that imports them.

    Runs as root at first boot, before redact-manifest. Deliberately does not
    decrypt: the envelope key is the user's Linux password, which is not in the
    manifest. See docs/reference/password-hashing.md."""
    entries = [b for b in manifest.get("browsers", []) if b.get("credentialsBlob")]
    if not entries:
        return

    linux_user = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    if not linux_user:
        logger.info("Chromium credentials staged but no user in the manifest "
                    "- skipping credential import")
        return

    home = Path("/home") / linux_user
    if not home.is_dir():
        logger.warning("User home %s does not exist - skipping Chromium "
                       "credential import", home)
        return

    try:
        store = home / _CRED_STORE_REL
        store.parent.mkdir(parents=True, exist_ok=True)
        store.write_text(json.dumps({
            "attempts": 0,
            "browsers": [{"name": e.get("name", ""), "blob": e["credentialsBlob"]}
                         for e in entries],
        }), encoding="utf-8")
        store.chmod(0o600)
        # Chown from .local down, not just the igloo directory: mkdir(parents=True) ran
        # as root, so any level it had to create is root-owned. Leaving .local that way
        # locks the user out of their own ~/.local/state - which breaks gnome-keyring,
        # xdg-user-dirs and every autostart hook, not only ours.
        run_cmd(["chown", "-R", f"{linux_user}:{linux_user}",
                 str(home / ".local")], check=False)

        hook = Path("/opt/igloo/credential-import.sh")
        hook.write_text(
            "#!/bin/sh\n"
            "# iGloo credential import - retries at each login until the\n"
            "# envelope store is consumed, then does nothing.\n"
            'STORE="$HOME/' + _CRED_STORE_REL + '"\n'
            '[ -f "$STORE" ] || exit 0\n'
            "exec python3 /opt/igloo/agent.py --import-credentials\n",
            encoding="utf-8")
        hook.chmod(0o755)

        autostart = Path("/etc/xdg/autostart/igloo-credential-import.desktop")
        autostart.write_text(
            "[Desktop Entry]\n"
            "Name=iGloo Browser Passwords\n"
            "Comment=Import the browser passwords migrated from Windows\n"
            "Exec=/opt/igloo/credential-import.sh\n"
            "Icon=dialog-password\n"
            "Terminal=false\n"
            "Type=Application\n"
            "X-GNOME-Autostart-enabled=true\n",
            encoding="utf-8")

        logger.info("Staged %d credential envelope(s) for %s and installed the "
                    "login hook", len(entries), linux_user)
    except OSError:
        logger.exception("Could not stage the credential import (non-fatal)")


def _ask_password() -> tuple[bool, str | None]:
    """Ask the desktop for the account password.

    Returns (asked, password). asked is False when neither dialog tool exists,
    which is different from the user cancelling."""
    attempts = [
        (["zenity", "--password",
          "--title=iGloo", "--timeout=300"], None),
        (["kdialog", "--password",
          "Enter your account password to import the browser passwords "
          "migrated from Windows."], None),
    ]
    for argv, _ in attempts:
        if shutil.which(argv[0]) is None:
            continue
        try:
            res = subprocess.run(argv, capture_output=True, text=True, timeout=310)
        except (OSError, subprocess.SubprocessError):
            logger.exception("%s failed while asking for the password", argv[0])
            return True, None
        if res.returncode != 0:
            return True, None
        return True, res.stdout.rstrip("\n")
    return False, None


def _run_user_mode() -> int:
    """Entry point for --import-credentials: logging the user can actually write
    to, then the import. Never returns non-zero - a failed import must not make
    the desktop report a broken autostart entry."""
    import logging

    state = Path.home() / ".local" / "state"
    try:
        state.mkdir(parents=True, exist_ok=True)
        handler: logging.Handler = logging.FileHandler(state / "igloo-credentials.log")
    except OSError:
        handler = logging.StreamHandler()
    handler.setFormatter(
        logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s"))
    root = logging.getLogger()
    root.addHandler(handler)
    root.setLevel(logging.INFO)

    try:
        return run_user_credential_import()
    except Exception:
        logger.exception("Credential import failed")
        return 0


def run_user_credential_import() -> int:
    """Import the staged Chromium credentials. Runs as the user at login."""
    store = Path.home() / _CRED_STORE_REL
    if not store.is_file():
        return 0

    try:
        data = json.loads(store.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        logger.exception("Credential store is unreadable - removing it")
        store.unlink(missing_ok=True)
        return 0

    attempts = int(data.get("attempts", 0)) + 1
    entries = data.get("browsers", [])
    if not entries or attempts > _CRED_MAX_ATTEMPTS:
        logger.info("Giving up on the credential import after %d attempt(s)",
                    attempts - 1)
        store.unlink(missing_ok=True)
        return 0

    try:
        _aes_self_test()
    except AssertionError:
        logger.exception("AES self-test failed - credential import disabled")
        store.unlink(missing_ok=True)
        return 0

    asked, password = _ask_password()
    if not asked:
        logger.warning("Neither zenity nor kdialog is installed - cannot ask "
                       "for the password, leaving the envelopes in place")
        return 0
    if not password:
        data["attempts"] = attempts
        store.write_text(json.dumps(data), encoding="utf-8")
        logger.info("Password prompt cancelled (attempt %d of %d)",
                    attempts, _CRED_MAX_ATTEMPTS)
        return 0

    imported = 0
    wrong_password = False
    for entry in entries:
        name = entry.get("name", "")
        mapping = _CHROMIUM_LINUX_DIRS.get(name)
        if mapping is None:
            logger.info("No Linux profile mapping for browser %r - skipping", name)
            continue
        app_id, config_rel, binaries = mapping
        try:
            payload = _decrypt_envelope(entry["blob"], password)
        except Exception:
            # A bad tag is indistinguishable from a corrupt envelope here, but
            # a wrong password is overwhelmingly the likelier cause.
            wrong_password = True
            logger.warning("Could not decrypt the envelope for %s", name)
            continue

        logins = payload.get("logins", [])
        cookies = payload.get("cookies", [])
        if not logins and not cookies:
            continue
        # Counted per browser, not per root: the same logins written to both a
        # Flatpak and a packaged install is one migration, not two.
        landed = 0
        for root in _chromium_config_homes(app_id, binaries):
            profile_dir = root / config_rel / "Default"
            try:
                if logins:
                    profile_dir.mkdir(parents=True, exist_ok=True)
                    landed = max(landed,
                                 _import_into_login_data(profile_dir / "Login Data", logins))
                    logger.info("Imported %s logins into %s", name, profile_dir)
            except Exception:
                logger.exception("Failed to import credentials for %s into %s "
                                 "(non-fatal)", name, root)
            try:
                # Cookies are a separate failure domain: the jar being unusable
                # must not cost the passwords that came from the same profile.
                if cookies:
                    rewritten = _reencrypt_cookies(profile_dir, cookies)
                    logger.info("Re-encrypted %d %s cookie(s) in %s",
                                rewritten, name, profile_dir)
            except Exception:
                logger.exception("Failed to re-encrypt cookies for %s in %s "
                                 "(non-fatal)", name, profile_dir)
        imported += landed

    if wrong_password and imported == 0:
        data["attempts"] = attempts
        store.write_text(json.dumps(data), encoding="utf-8")
        logger.info("Nothing decrypted (attempt %d of %d) - will ask again at "
                    "the next login", attempts, _CRED_MAX_ATTEMPTS)
        return 0

    store.unlink(missing_ok=True)
    logger.info("Imported %d browser login(s); credential store removed", imported)
    return 0


# === END AGENT SECTION ===================================================


# Standalone test harness below this line; NOT part of the agent section.
if __name__ == "__main__":
    import json
    import logging
    import shutil
    import tempfile
    import types

    logging.basicConfig(level=logging.DEBUG, format="%(levelname)s %(message)s")
    logger = logging.getLogger("chromium-import-test")

    def run_cmd(args, check=False, timeout=None):  # noqa: D103 - test shim
        import subprocess
        return subprocess.run(args, capture_output=True, text=True)

    failures = []

    def check(cond: bool, label: str) -> None:
        print(("PASS " if cond else "FAIL ") + label)
        if not cond:
            failures.append(label)

    _aes_self_test()
    print("PASS AES known-answer self-test")

    # Interop: the exact envelope the C# suite verifies against.
    from make_interop_vector import MAGIC, NONCE, PASSWORD, PAYLOAD, SALT
    import hashlib as _hl
    from aes_gcm import aes_gcm_encrypt

    key = _hl.pbkdf2_hmac("sha256", PASSWORD.encode(), SALT, 600_000, 32)
    envelope = MAGIC + SALT + NONCE + aes_gcm_encrypt(key, NONCE, PAYLOAD)
    blob = base64.b64encode(envelope).decode()

    payload = _decrypt_envelope(blob, PASSWORD)
    check(payload["browser"] == "Google Chrome", "envelope decrypts to payload")
    check(len(payload["logins"]) == 2, "payload carries both logins")

    # Wrong password must fail the tag check.
    try:
        _decrypt_envelope(blob, "wrong password")
        check(False, "wrong password rejected")
    except ValueError:
        check(True, "wrong password rejected")

    # Full import into a fresh Login Data database.
    tmp = Path(tempfile.mkdtemp(prefix="igloo-chromium-test-"))
    try:
        db = tmp / "Login Data"
        inserted = _import_into_login_data(db, payload["logins"])
        check(inserted == 2, "two rows inserted into fresh Login Data")

        con = sqlite3.connect(db)
        rows = list(con.execute(
            "SELECT origin_url, username_value, password_value, signon_realm "
            "FROM logins ORDER BY username_value"))
        meta = list(con.execute("SELECT value FROM meta WHERE key='version'"))
        columns = {r[1] for r in con.execute("PRAGMA table_info(logins)")}
        con.close()

        # Chromium's logins table at schema version 31, reconstructed from
        # InitializeBuilders in login_database.cc. If this drifts from what we
        # declare in meta, MigrateDatabase runs an ALTER TABLE that fails and
        # the caller recreates the file - which is how Brave came up empty.
        expected_v31 = {
            "origin_url", "action_url", "username_element", "username_value",
            "password_element", "password_value", "submit_element", "signon_realm",
            "date_created", "blacklisted_by_user", "scheme", "password_type",
            "times_used", "form_data", "display_name", "icon_url", "federation_url",
            "skip_zero_click", "generation_upload_status", "possible_username_pairs",
            "id", "date_last_used", "moving_blocked_for", "date_password_modified",
        }
        check(columns == expected_v31,
              "logins table matches Chromium's version 31 schema"
              + ("" if columns == expected_v31 else
                 f" (extra={sorted(columns - expected_v31)}"
                 f" missing={sorted(expected_v31 - columns)})"))
        # Dropped at version 31: present here means the version label is a lie.
        check("date_synced" not in columns, "date_synced is gone (dropped at 31)")

        check(rows[0][0] == "https://example.com/login", "origin_url stored")
        check(rows[0][3] == "https://example.com/", "signon_realm derived")
        # The stored version drives Chromium's migration, so it has to match the
        # columns actually present - see _LOGINS_SCHEMA.
        check(meta and meta[0][0] == str(_LOGINS_SCHEMA_VERSION),
              f"meta version is {_LOGINS_SCHEMA_VERSION}")

        # v10 blob is deterministic (fixed key + IV): recompute and compare.
        expect = _chromium_v10_encrypt("s3cret!")
        check(rows[0][2] == expect, "v10 encoding is the Chromium deterministic form")
        check(rows[0][2][:3] == b"v10", "v10 prefix present")

        # Idempotency: a second import inserts nothing.
        inserted2 = _import_into_login_data(db, payload["logins"])
        check(inserted2 == 0, "re-import is idempotent (no duplicates)")

        # Import into a pre-existing DB with a NEWER schema (extra columns).
        db2 = tmp / "Login Data Newer"
        con = sqlite3.connect(db2)
        con.executescript(_LOGINS_SCHEMA +
                          "ALTER TABLE logins ADD COLUMN sender_name VARCHAR;"
                          "ALTER TABLE logins ADD COLUMN date_received "
                          "INTEGER NOT NULL DEFAULT 0;")
        con.close()
        inserted3 = _import_into_login_data(db2, payload["logins"])
        check(inserted3 == 2, "newer-schema DB accepts the import")
        con = sqlite3.connect(db2)
        extra = list(con.execute(
            "SELECT sender_name, date_received FROM logins"))
        con.close()
        check(extra[0] == ("", 0), "newer columns receive safe defaults")

        # File mode is owner-only (POSIX only; chmod is a read-only toggle on
        # Windows, so this check only runs where the agent will actually run).
        if os.name == "posix":
            check((db.stat().st_mode & 0o777) == 0o600, "Login Data is 0600")
        else:
            print("SKIP Login Data is 0600 (POSIX-only check)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    # --- the login-time path: store -> prompt -> import -> store gone --------
    tmp2 = Path(tempfile.mkdtemp(prefix="igloo-userimport-"))
    real_home = Path.home
    try:
        Path.home = staticmethod(lambda: tmp2)          # type: ignore[assignment]
        store = tmp2 / _CRED_STORE_REL
        store.parent.mkdir(parents=True, exist_ok=True)
        store.write_text(json.dumps({
            "attempts": 0,
            "browsers": [{"name": "Google Chrome", "blob": blob}],
        }), encoding="utf-8")

        globals()["_ask_password"] = lambda: (True, "wrong password")
        run_user_credential_import()
        left = json.loads(store.read_text(encoding="utf-8"))
        check(store.is_file() and left["attempts"] == 1,
              "wrong password leaves the store and counts the attempt")

        globals()["_ask_password"] = lambda: (True, None)
        run_user_credential_import()
        check(json.loads(store.read_text(encoding="utf-8"))["attempts"] == 2,
              "cancelling counts as an attempt")

        globals()["_ask_password"] = lambda: (True, PASSWORD)
        run_user_credential_import()
        check(not store.exists(), "successful import removes the store")
        _, config_rel, _ = _CHROMIUM_LINUX_DIRS["Google Chrome"]
        db = tmp2 / ".config" / config_rel / "Default" / "Login Data"
        con = sqlite3.connect(db)
        rows = con.execute("SELECT COUNT(*) FROM logins").fetchone()[0]
        con.close()
        check(rows == 2, "logins landed in the user's own profile")

        #   Cookies
        # The jar is the Windows file, copied verbatim: real Chromium schema,
        # rows still encrypted under the Windows master key. Only the values
        # are rewritten, and only for cookies the envelope can account for.
        jar_dir = tmp2 / "jar" / "Network"
        jar_dir.mkdir(parents=True)
        jar = jar_dir / "Cookies"
        con = sqlite3.connect(jar)
        con.execute("CREATE TABLE cookies (host_key TEXT, name TEXT, path TEXT, "
                    "encrypted_value BLOB, value TEXT)")
        con.executemany(
            "INSERT INTO cookies VALUES (?, ?, ?, ?, '')",
            [(".example.com", "session", "/", b"v10windows-ciphertext"),
             (".example.com", "theme", "/", b"v10windows-ciphertext"),
             (".orphan.test", "stale", "/", b"v10windows-ciphertext")])
        con.commit()
        con.close()

        # A domain-hash prefix would live inside these bytes; carrying them
        # opaquely is what makes that Chromium detail none of our business.
        secret = b"\x01\x02\x03deadbeef-session-token"
        rewritten = _reencrypt_cookies(tmp2 / "jar", [
            {"host": ".example.com", "name": "session", "path": "/",
             "value": base64.b64encode(secret).decode()},
            {"host": ".example.com", "name": "theme", "path": "/",
             "value": base64.b64encode(b"dark").decode()},
        ])
        check(rewritten == 2, f"both known cookies rewritten (got {rewritten})")

        con = sqlite3.connect(jar)
        jar_rows = dict(con.execute("SELECT name, encrypted_value FROM cookies"))
        con.close()
        check("stale" not in jar_rows,
              "a cookie with no plaintext is deleted, not left undecryptable")
        check(jar_rows.keys() == {"session", "theme"}, "only the known cookies remain")
        check(jar_rows["session"] == _chromium_v10_encrypt_bytes(secret),
              "the value is re-encrypted byte-for-byte in Chromium's Linux form")
        check(jar_rows["session"].startswith(b"v10"), "v10 prefix on the cookie")

        check(_reencrypt_cookies(tmp2 / "no-such-profile", [{"host": "x"}]) == 0,
              "a profile without a cookie jar is a no-op")

        # Flatpak overrides XDG_CONFIG_HOME and a distro package does not, so the
        # four install combinations must each reach a root the browser reads.
        real_which, real_run = shutil.which, subprocess.run
        real_xdg = os.environ.pop("XDG_CONFIG_HOME", None)
        flatpak_root = tmp2 / ".var" / "app" / "com.brave.Browser" / "config"
        native_root = tmp2 / ".config"

        def stub(has_flatpak: bool, has_native: bool) -> None:
            shutil.which = lambda name: (
                "/usr/bin/flatpak" if name == "flatpak" and has_flatpak
                else "/usr/bin/" + name if name.startswith("brave") and has_native
                else None)
            subprocess.run = lambda *a, **k: types.SimpleNamespace(
                returncode=0 if has_flatpak else 1)

        def roots() -> list:
            app_id, _, binaries = _CHROMIUM_LINUX_DIRS["Brave"]
            return _chromium_config_homes(app_id, binaries)

        try:
            stub(has_flatpak=True, has_native=False)
            check(roots() == [flatpak_root], "Flatpak only -> the Flatpak root")
            stub(has_flatpak=False, has_native=True)
            check(roots() == [native_root], "package only -> ~/.config")
            stub(has_flatpak=True, has_native=True)
            check(roots() == [flatpak_root, native_root], "both installed -> both roots")
            stub(has_flatpak=False, has_native=False)
            check(roots() == [native_root], "neither installed -> ~/.config")
        finally:
            shutil.which, subprocess.run = real_which, real_run
            if real_xdg is not None:
                os.environ["XDG_CONFIG_HOME"] = real_xdg

        store.write_text(json.dumps({
            "attempts": _CRED_MAX_ATTEMPTS,
            "browsers": [{"name": "Google Chrome", "blob": blob}],
        }), encoding="utf-8")
        globals()["_ask_password"] = lambda: (True, "still wrong")
        run_user_credential_import()
        check(not store.exists(), "store is deleted once the attempts run out")
    finally:
        Path.home = real_home                            # type: ignore[assignment]
        shutil.rmtree(tmp2, ignore_errors=True)

    if failures:
        raise SystemExit(f"{len(failures)} check(s) FAILED")
    print("ALL CHECKS PASSED")
