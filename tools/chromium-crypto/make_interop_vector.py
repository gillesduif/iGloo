"""Generate the cross-language interop test vector for the iGloo credential
envelope. The Python output below is embedded in the C# test suite; the C#
CredentialProtector must decrypt the exact blob back to the exact payload.

Envelope format v1:
    magic  8 bytes  "IGCRD001"
    salt   16 bytes (PBKDF2-HMAC-SHA256, 600_000 iterations, 32-byte key)
    nonce  12 bytes (AES-256-GCM)
    body   ciphertext || 16-byte tag
"""

import hashlib

from aes_gcm import aes_gcm_decrypt, aes_gcm_encrypt, self_test

MAGIC = b"IGCRD001"
ITERATIONS = 600_000

PASSWORD = "correct horse battery staple"
SALT = bytes.fromhex("000102030405060708090a0b0c0d0e0f")
NONCE = bytes.fromhex("101112131415161718191a1b")
PAYLOAD = (b'{"browser":"Google Chrome","logins":['
           b'{"url":"https://example.com/login","username":"alice",'
           b'"password":"s3cret!"},'
           b'{"url":"https://test.invalid","username":"bob",'
           b'"password":"p@ss w0rd"}]}')


def main() -> None:
    self_test()
    key = hashlib.pbkdf2_hmac("sha256", PASSWORD.encode("utf-8"), SALT,
                              ITERATIONS, 32)
    body = aes_gcm_encrypt(key, NONCE, PAYLOAD)
    envelope = MAGIC + SALT + NONCE + body

    # Self-check: decrypt what we just produced.
    parsed_key = hashlib.pbkdf2_hmac("sha256", PASSWORD.encode("utf-8"),
                                     envelope[8:24], ITERATIONS, 32)
    assert aes_gcm_decrypt(parsed_key, envelope[24:36], envelope[36:]) == PAYLOAD

    print(f"EnvelopeHex  = {envelope.hex()}")
    print(f"PayloadUtf8  = {PAYLOAD.decode('utf-8')}")
    print(f"EnvelopeLen  = {len(envelope)} bytes")


if __name__ == "__main__":
    main()
