#!/usr/bin/env python3
"""Read-only check of one synthetic Chromium Linux Login Data password.

Chromium's Linux OSCrypt v10 format uses PBKDF2-HMAC-SHA1("peanuts",
"saltysalt", one iteration), AES-128-CBC, an IV of sixteen spaces, and a
``v10`` prefix. OpenSSL supplies an independent AES implementation here.

Source: https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.84/components/os_crypt/async/browser/posix_key_provider.cc
Chrome 155 registers this v10 provider alongside its desktop secret provider.
See chrome/browser/browser_process_impl.cc in the same Chromium tag.
This proves the blob is decryptable by that format. Browser UI behavior still
needs a separate check against the installed browser build.
"""

from __future__ import annotations

import argparse
import hashlib
import hmac
import re
import shutil
import sqlite3
import subprocess
import sys
from pathlib import Path
from urllib.parse import urlsplit


_EXPECTED_KEY_HEX = "fd621fe5a2b402539dfa147ca9272778"


def verify(db: Path, origin: str, username: str, expected_sha256: str) -> int:
    host = urlsplit(origin).hostname or ""
    if not (host.endswith(".invalid") or host == "localhost"):
        raise ValueError("Refusing to decrypt a non-synthetic browser origin")
    if not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
        raise ValueError("Expected SHA-256 must be 64 hexadecimal characters")
    if not db.is_file():
        raise ValueError("Login Data database does not exist")

    # The verifier runs on a completed lab copy; immutable prevents SQLite
    # from creating a shared-memory sidecar next to a WAL database.
    con = sqlite3.connect(db.resolve().as_uri() + "?mode=ro&immutable=1", uri=True)
    try:
        con.execute("PRAGMA query_only=ON")
        rows = list(con.execute(
            "SELECT password_value FROM logins WHERE origin_url=? AND username_value=?",
            (origin, username)))
        version_row = con.execute(
            "SELECT value FROM meta WHERE key='version'").fetchone()
    finally:
        con.close()
    if len(rows) != 1:
        raise ValueError("Expected exactly one matching synthetic browser login")
    blob = rows[0][0]
    if not isinstance(blob, bytes) or not blob.startswith(b"v10") or len(blob) < 19:
        raise ValueError("Synthetic browser login has no valid v10 ciphertext")

    key = hashlib.pbkdf2_hmac("sha1", b"peanuts", b"saltysalt", 1, 16)
    if key.hex() != _EXPECTED_KEY_HEX:
        raise RuntimeError("PBKDF2 result differs from Chromium's published v10 key")
    openssl = shutil.which("openssl")
    if openssl is None:
        raise RuntimeError("OpenSSL is required for independent AES decryption")
    result = subprocess.run(
        [openssl, "enc", "-d", "-aes-128-cbc", "-nosalt",
         "-K", key.hex(), "-iv", (b" " * 16).hex()],
        input=blob[3:], capture_output=True, check=False)
    if result.returncode != 0:
        raise ValueError("OpenSSL could not decrypt the synthetic v10 login")
    if not hmac.compare_digest(
            hashlib.sha256(result.stdout).hexdigest(), expected_sha256.lower()):
        raise ValueError("Synthetic login decrypted to an unexpected value")

    version = version_row[0] if version_row else "unknown"
    print(f"PASS: synthetic v10 login decrypts to expected digest; Login Data schema {version}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", required=True, type=Path)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--username", required=True)
    parser.add_argument("--expected-sha256", required=True)
    args = parser.parse_args()
    try:
        return verify(args.db, args.origin, args.username, args.expected_sha256)
    except (OSError, RuntimeError, ValueError, sqlite3.Error) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
