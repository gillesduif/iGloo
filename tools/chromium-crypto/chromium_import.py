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
# This step runs BEFORE redact-manifest, while the plaintext Linux password
# is still present, decrypts the envelope, and inserts the rows into the
# Linux browser's Login Data using Chromium's Linux "v10" encoding.
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
import sqlite3
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

# Browser display name (as the Windows wizard records it) to the Linux config
# directory relative to the user's home.
_CHROMIUM_LINUX_DIRS = {
    "Google Chrome": ".config/google-chrome",
    "Microsoft Edge": ".config/microsoft-edge",
    "Brave": ".config/BraveSoftware/Brave-Browser",
    "Vivaldi": ".config/vivaldi",
    "Opera": ".config/opera",
}

# Chromium timestamps count microseconds since 1601-01-01 UTC.
_CHROMIUM_EPOCH_OFFSET_US = 11644473600 * 1_000_000

# Classic logins schema. Newer Chromium versions upgrade older databases on
# first launch (password-store migrations only ADD columns), so writing the
# classic form is the compatible choice. Validated in VM testing per
# CONTRIBUTING.md rule 4.
_LOGINS_SCHEMA = """
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
    date_last_used INTEGER NOT NULL DEFAULT 0,
    date_password_modified INTEGER NOT NULL DEFAULT 0,
    blacklisted_by_user INTEGER NOT NULL,
    scheme INTEGER NOT NULL,
    times_used INTEGER NOT NULL DEFAULT 1,
    display_name VARCHAR,
    icon_url VARCHAR,
    federation_url VARCHAR,
    skip_zero_click INTEGER,
    generation_upload_status INTEGER,
    possible_username_pairs BLOB,
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date_synced INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS meta (
    key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY,
    value LONGVARCHAR
);
INSERT OR IGNORE INTO meta(key, value) VALUES ('version', '8');
"""


def _chromium_v10_encrypt(password: str) -> bytes:
    """Encode one password the way Linux Chromium stores it in Login Data:
    "v10" || AES-128-CBC(PKCS7), key PBKDF2-HMAC-SHA1("peanuts", "saltysalt",
    1 iteration), IV of 16 spaces. This scheme is Chromium's documented
    fallback when no desktop keyring holds the key."""
    key = hashlib.pbkdf2_hmac("sha1", b"peanuts", b"saltysalt", 1, 16)
    return b"v10" + aes_cbc_encrypt(key, b" " * 16, password.encode("utf-8"))


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
        "times_used": 1,
        "display_name": "",
        "icon_url": "",
        "federation_url": "",
        "skip_zero_click": 0,
        "generation_upload_status": 0,
        "possible_username_pairs": b"",
        "date_synced": 0,
        # Columns added by newer Chromium versions.
        "moving_blocked_for": b"",
        "sender_name": "",
        "sender_origin": "",
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


def import_chromium_credentials(manifest: dict) -> None:
    """Migrate staged Chromium credentials into the Linux browsers' Login Data.

    Runs before redact-manifest: the envelope key derives from the plaintext
    linuxPassword, which redaction then erases. Best-effort per browser; a
    failure here must never block first boot."""
    entries = [b for b in manifest.get("browsers", []) if b.get("credentialsBlob")]
    if not entries:
        return

    user = manifest.get("user", {})
    linux_user = (user.get("preferredLinuxUsername") or "").strip()
    password = user.get("linuxPassword")
    if not linux_user or not password:
        logger.info("Chromium credentials staged but no user/password in the "
                    "manifest - skipping credential import")
        return

    try:
        _aes_self_test()
    except AssertionError:
        logger.exception("AES self-test failed - Chromium credential import "
                         "is disabled for this run")
        return

    home = Path("/home") / linux_user
    if not home.is_dir():
        logger.warning("User home %s does not exist - skipping Chromium "
                       "credential import", home)
        return

    for entry in entries:
        name = entry.get("name", "")
        config_rel = _CHROMIUM_LINUX_DIRS.get(name)
        if config_rel is None:
            logger.info("No Linux profile mapping for browser %r - skipping "
                        "credential import", name)
            continue
        try:
            payload = _decrypt_envelope(entry["credentialsBlob"], password)
        except Exception:
            logger.exception("Could not decrypt the credential envelope for "
                             "%s - skipping this browser", name)
            continue

        logins = payload.get("logins", [])
        if not logins:
            continue

        config_dir = home / config_rel
        profile_dir = config_dir / "Default"
        try:
            profile_dir.mkdir(parents=True, exist_ok=True)
            inserted = _import_into_login_data(profile_dir / "Login Data", logins)
            run_cmd(["chown", "-R", f"{linux_user}:{linux_user}",
                     str(config_dir)], check=False)
            logger.info("Imported %d Chromium login(s) for %s", inserted, name)
        except Exception:
            logger.exception("Failed to import Chromium credentials for %s "
                             "(non-fatal)", name)
# === END AGENT SECTION ===================================================


# Standalone test harness below this line; NOT part of the agent section.
if __name__ == "__main__":
    import json
    import logging
    import shutil
    import tempfile

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
        con.close()

        check(rows[0][0] == "https://example.com/login", "origin_url stored")
        check(rows[0][3] == "https://example.com/", "signon_realm derived")
        check(meta and meta[0][0] == "8", "meta version written")

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

    if failures:
        raise SystemExit(f"{len(failures)} check(s) FAILED")
    print("ALL CHECKS PASSED")
