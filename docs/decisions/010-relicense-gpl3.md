# ADR-010: Relicense from GPL-2.0-only to GPL-3.0-or-later

**Status:** Accepted
**Date:** 2026-07-29
**Supersedes:** the GPL-2.0 licensing decision recorded for the initial release
(see `docs/architecture.md` ADR list; no standalone ADR file existed)

## Context

iGloo shipped under GPL-2.0-only with the reasoning "same license as the Linux
kernel". During the open-source release audit (OSS_RELEASE_CHECKLIST.md §1.1) a
concrete conflict surfaced: the distributed application bundles **Serilog,
which is Apache-2.0**. The FSF's position is that Apache-2.0's patent
termination clause is an additional restriction that GPL-2.0 does not permit,
while Apache-2.0 is explicitly compatible with GPL-3.0. The project was
therefore distributing a combination its own license did not allow.

All other dependencies are permissive (MIT / Apache-2.0) and `git log` shows a
single author across the entire history, so the relicense needs no external
consent.

## Decision

Relicense the project to **GPL-3.0-or-later**:

- `LICENSE` replaced with the official GPLv3 text downloaded from gnu.org.
- A `COPYRIGHT` file records the copyright holder and the "or later" grant.
- Every GPL-2.0 reference updated: README (badge and license section),
  CONTRIBUTING, distros/README, THIRD-PARTY-NOTICES (two mentions),
  docs/architecture.md ADR list, docs/business/risk-register.md.
- The SPDX identifier `GPL-3.0-or-later` is used verbatim and consistently.

## Rationale

- **It fixes a real licensing bug, not a preference.** GPL-2.0-only plus a
  shipped Apache-2.0 dependency is a conflict that exists today; GPLv3
  resolves it cleanly.
- **"or-later"** lets the project adopt a future GPL version without repeating
  a full relicense, and matches what most new GPL projects choose.
- **GPLv3 adds protections this project specifically benefits from:** an
  explicit patent grant (relevant for a tool that touches firmware and boot
  chains) and anti-tivoization language that keeps modified versions
  installable.
- Funding paths (NLnet, VLAIO) are compatible with GPL, so the change does not
  constrain the project's financing plans.

## Consequences

- **Positive:** the license is now internally consistent with the shipped
  dependency set; patent and tivoization protections apply; contribution
  terms (CONTRIBUTING.md, distros/README.md) are unambiguous.
- **Negative:** the "same license as the kernel" line no longer applies and
  was removed from the README; GPLv3 is incompatible with GPL-2.0-only code,
  so no GPL-2.0-only source can be copied in (none exists or is planned).
- **Neutral:** distro logos and trademarks remain excluded from the project's
  license, as documented in THIRD-PARTY-NOTICES.md.
