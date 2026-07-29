# ADR-011: Chromium credential migration (browser Phase 2)

**Status:** Accepted
**Date:** 2026-07-29

## Context

Phase 1 migrates Gecko browser profiles (Firefox, Zen, Waterfox) by copying
the profile root, because Gecko profiles are fully OS-portable including
saved passwords. Chromium browsers (Chrome, Edge, Brave, Vivaldi, Opera) were
recorded in the manifest with empty paths and `includesPasswords: false`:
their saved passwords are encrypted with a master key that is itself
protected by DPAPI, bound to the Windows user account, so copying the profile
to Linux yields unusable ciphertext.

This ADR records how Phase 2 migrates Chromium credentials anyway, and the
boundaries it deliberately does not cross.

## Decision

**Decrypt on Windows, re-encrypt for transit, import on Linux.**

1. **Windows extraction** (`Igloo.Migration.Chromium`): for each selected
   Chromium browser, read `Local State` for the DPAPI-protected master key,
   unprotect it with `ProtectedData.Unprotect` (current-user scope), read the
   `logins` table from each profile's `Login Data` SQLite database, and
   decrypt `v10` password values (AES-256-GCM, 12-byte nonce, master key).
2. **Transit envelope**: the decrypted set is serialized to JSON and
   re-encrypted with AES-256-GCM under a key derived from the user's Linux
   password (PBKDF2-HMAC-SHA256, 600,000 iterations, random 16-byte salt,
   random 12-byte nonce). The envelope is stored base64-encoded in the
   manifest as `browsers[].credentialsBlob`. No plaintext credential ever
   touches the staging volume or a log.
3. **Linux import** (both first-boot agents): before the redact step (which
   still runs last), the agent derives the same key from the manifest's
   plaintext `linuxPassword`, decrypts the envelope, and inserts the rows
   into the Linux browser's `Login Data` database, with `password_value`
   encoded as Chromium's Linux `v10` form (PBKDF2-HMAC-SHA1("peanuts",
   "saltysalt", 1) -> AES-128-CBC, IV of 16 spaces). The redact step nulls
   `credentialsBlob` alongside `linuxPassword` and the Wi-Fi PSKs.

### Boundary: App-Bound Encryption is out of scope

Chrome 127+ and current Edge/Brave encrypt the master key with App-Bound
Encryption (ABE) and emit `v20` password values. Defeating ABE requires
executing code from the browser's own installation directory through its
internal COM elevation service. That is the technique credential-stealing
malware uses; it trips antivirus heuristics, it violates the spirit of the
browser's protection, and it is an arms race against the browser vendors.
iGloo does not do it. When ABE is detected (or `v20` values are found), the
browser is skipped, the manifest keeps `includesPasswords: false`, the log
says why, and first boot proceeds normally.

### Boundary: no new runtime dependencies on Linux

The Debian offline path must be able to import credentials with no network.
The agents therefore carry a small, self-contained pure-Python AES
implementation (encrypt direction only, CBC and GCM). It is guarded by
embedded known-answer tests (FIPS-197 block vectors, NIST SP 800-38A CBC
vector, McGrew-Viega GCM vector) that run before first use; on any mismatch
the step disables itself and logs an error rather than risking malformed
output on real credentials. The identical section is kept in both agents,
like the existing Wi-Fi section.

## Rationale

- **Extraction must happen on Windows.** DPAPI master keys live in the
  Windows registry hives and unlock only in the user's logon session; no
  Linux-side decryption is possible. The wizard already runs as the migrating
  user (elevated, but same session), so current-user DPAPI works.
- **The manifest is the right transport.** Every pipeline (Fedora kickstart,
  Debian-family bootstrap) already delivers the manifest to
  `/var/lib/igloo/manifest.json` and already redacts secrets from it. Riding
  that path adds no new staging surface, and the existing redact step extends
  to the new field.
- **The transit posture is stronger than the existing one.** The manifest
  already carries the plaintext Linux password and Wi-Fi PSKs on the FAT32
  seed during the install window; the credentials blob is encrypted under a
  key derived from that password, so it is never the weakest secret on the
  volume.
- **Import before redact, redact last.** The ordering already used for
  `chpasswd` and Wi-Fi applies unchanged: consume the plaintext, then erase
  it.
- **Fail-soft everywhere.** A locked `Login Data` (browser running), an ABE
  browser, an unknown browser, or a failed self-test skips that browser with
  a log line. First boot is never blocked by credential migration.

## Consequences

- **Positive:** Chrome, Edge, Brave, Vivaldi and Opera passwords migrate for
  the large installed base of pre-ABE browsers and for configurations where
  ABE is disabled; the feature degrades to an explicit log line otherwise.
- **Negative:** `Microsoft.Data.Sqlite` and
  `System.Security.Cryptography.ProtectedData` (both MIT) are new
  dependencies; recorded in THIRD-PARTY-NOTICES. Reading `Login Data` and
  calling DPAPI is, by signature, what infostealers do: this code is openly
  documented here and in SECURITY.md, runs only on explicit user selection in
  the wizard, and never transmits anything off the machine. Antivirus false
  positives are a known risk until the binary is signed (M16).
- **Neutral:** Chromium cookie and session-store migration remains out of
  scope (cookies are also encrypted; sessions are less portable). The Linux
  `Login Data` schema is created in the classic form Chromium upgrades on
  first launch; the upgrade path is validated in VM testing per
  CONTRIBUTING.md rule 4.
