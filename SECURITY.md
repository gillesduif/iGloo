# Security Policy

iGloo shrinks partitions, writes files to the internal disk, downloads OS images,
and registers UEFI boot entries. A defect — or a malicious contribution — can cost
someone their data or their bootable machine. We take reports seriously.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Use GitHub's private reporting instead: on the repository, go to the **Security**
tab → **Report a vulnerability**. This opens a private advisory visible only to the
maintainers.

<!-- TODO(maintainer): if you prefer email, add a monitored address here and mention
     it as an alternative. Do not use a personal inbox you don't check. -->

Please include:

- iGloo version (or commit) and the Windows build you ran it on.
- The distribution and install mode (dual-boot vs. replace-disk).
- What happened, what you expected, and how to reproduce it.
- Relevant logs (see below) with anything sensitive redacted.

We aim to acknowledge a report within **7 days** and to agree on a disclosure
timeline with you. Please give us a reasonable window to ship a fix before any
public disclosure.

## What is in scope

- The install pipeline: partition shrink/creation, file staging, boot-entry
  registration, and the removal/reclaim path.
- ISO acquisition and verification (SHA-256 + pinned GPG fingerprints).
- The first-boot agents that run on the freshly installed Linux system.
- Anything that could lead to data loss, a bricked boot configuration, or code
  execution from untrusted input (a tampered ISO, a hostile mirror, etc.).

## What is out of scope

- Bugs with no security impact — please file those as normal issues.
- Vulnerabilities in the Linux distributions themselves, their installers, or
  their mirrors. Report those upstream.
- Findings that require an attacker who already has Administrator on the machine
  (iGloo requires elevation by design).

## Where the logs are

- iGloo (Windows): `%LOCALAPPDATA%\iGloo\logs\`
- First-boot agent (target Linux): `bootstrap.log` / `agent.log` on Debian-family
  installs; `first-boot.log` on the Anaconda path.

The migration manifest can contain a Wi-Fi passphrase and the Linux account
password; these are redacted after first boot. **Scrub them before attaching a
manifest to any report.**
