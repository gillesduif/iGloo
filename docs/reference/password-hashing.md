# Password hashing for the installer configs

## Why a hash at all

The preseed, kickstart and autoinstall files land on the FAT32 seed partition, which has
no access control. Anyone holding the medium can read them. Debian's installation guide
says the same thing about its own preseed files:

> Be aware that preseeding passwords is not completely secure as everyone with access to
> the preconfiguration file will have the knowledge of these passwords. Storing hashed
> passwords is considered secure unless a weak hashing algorithm like DES or MD5 is used.

Every installer iGloo targets accepts a crypt(3) hash, so the plain text never has to
travel:

| distro | installer | directive |
| --- | --- | --- |
| Debian | debian-installer | `d-i passwd/user-password-crypted password <hash>` |
| Mint | Ubiquity | same key, shared `user-setup` component |
| Fedora | Anaconda | `user --password="<hash>" --iscrypted` |
| Ubuntu | subiquity | `identity.password: "<hash>"` |

## Why SHA-512-crypt and not yescrypt

yescrypt is the default for *newly set* passwords on Debian 11+ and Fedora 35+, which is
not the same as being the only format accepted. Fedora's own change proposal states:

> No impact, as password hashes, that have been computed using the former default
> sha512crypt, will continue to work.

Debian goes further and recommends SHA-512 outright for hashes shared between releases.

The deciding argument is verifiability. A wrong hash locks the user out of the machine
they just migrated to, with no recovery path short of a rescue disk. SHA-512-crypt has
been frozen since 2008 and ships seven published test vectors, which
`LinuxPasswordHasherTests` checks one by one. yescrypt has no comparable vector set that
can be checked from Windows, and the algorithm is far more intricate (scrypt plus
pwxform). There is also no maintained .NET implementation of it that produces the
`/etc/shadow` string form.

## Why 200,000 rounds

glibc defaults to 5000 rounds when the hash carries no `rounds=` field. That default
dates from 2007 and is widely described as far too low for current hardware, but the
sources that say so do not offer a substantiated replacement:

- DISA STIG for RHEL 8 requires `SHA_CRYPT_MIN_ROUNDS` no lower than 5000, which is a
  floor rather than a recommendation.
- Hardening projects suggest 10,000 or more, without showing their work.
- OWASP's Password Storage Cheat Sheet does not cover crypt(3) schemes at all. It wants
  Argon2id, which `/etc/shadow` cannot express.

The closest usable anchor is OWASP's figure for PBKDF2-HMAC-SHA512: 220,000 iterations.
SHA-512-crypt performs structurally comparable work, so 200,000 rounds puts the cost in
the same range as a recognised standard instead of a round number picked by feel.

Measured with CryptSharpStandard on a desktop CPU:

| rounds | time per hash |
| --- | --- |
| 5,000 (glibc default) | 6.4 ms |
| 25,000 | 12.3 ms |
| 50,000 | 23.9 ms |
| 100,000 | 44.9 ms |
| **200,000** | **87.3 ms** |
| 500,000 | 225.7 ms |

The cost is paid on the target machine at every login and every `sudo`. Budget two to
three times the figures above for a modest laptop, so roughly 200-250 ms at 200,000
rounds — barely perceptible. At 500,000 it approaches a second, which is not.

Raising the count later only affects newly generated hashes; existing accounts keep
working, because the round count travels inside the hash string.

## The browser credentials

The staged browser passwords are encrypted with AES-256-GCM under
PBKDF2-HMAC-SHA256(linuxPassword, 600,000). A hash cannot serve as key material, so
that envelope needs the plain text - and the first-boot agent runs from
`multi-user.target`, before anyone has typed it.

Ubuntu hit the identical problem with ecryptfs and closed it as a design limitation
(Launchpad #1578369): use the crypted password for the install, and run the
key-derivation step afterwards, once the user supplies the password.

iGloo does the same. `stage_credential_import()` runs as root at first boot and only
moves the envelopes to `~/.local/share/igloo/credentials.json`, owned by the user and
mode 0600, then installs an autostart entry. At the first graphical login,
`agent.py --import-credentials` runs as the user, asks once via zenity or kdialog,
decrypts, imports, and deletes the store. A cancelled or wrong prompt retries at the
next login; after three attempts the envelopes are deleted unopened.

The plain-text password therefore never reaches the manifest, and the envelope is
useless to anyone holding the seed partition.

## Sources

- <https://www.debian.org/releases/stable/amd64/apbs04.en.html>
- <https://fedoraproject.org/wiki/Changes/yescrypt_as_default_hashing_method_for_shadow>
- <https://www.akkadia.org/drepper/SHA-crypt.txt>
- <https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html>
- <https://bugs.launchpad.net/bugs/1578369>
