# ADR-009: Park Ubuntu, enter closed beta with three validated distros

**Status:** Accepted
**Date:** 2026-07-13

## Context

After an extended validation campaign, Ubuntu's no-USB path had most of its
pipeline individually proven (6 GB ISO staging on NTFS, casper/toram boot,
autoinstall delivery, full-fidelity preserved partition table, disk-release
machinery) but had not completed a single end-to-end run. The remaining
obstacle is environmental churn around curtin's unconditional
`sfdisk`+`partprobe` (holders reappearing on the live-media partitions), plus a
VM graphics freeze unrelated to iGloo. Meanwhile Fedora (real hardware), Debian
and Mint (VM) were fully validated, and every week spent on Ubuntu deferred the
closed beta (M15) — the milestone that burns down the project's highest-
likelihood risk (hardware-matrix unknowns, risk register R-05).

## Decision

Ubuntu ships in the catalog as **`status: "in-development"`** — browsable, not
installable. The closed beta proceeds with Fedora KDE, Debian and Linux Mint.
All Ubuntu knowledge is preserved in a resumption dossier
([`distros/ubuntu/STATUS.md`](../../distros/ubuntu/STATUS.md)): what is proven,
the complete failure chronology with root causes, the exact open lead, and
alternative avenues (Ubuntu Server ISO route; upstream subiquity fix).

## Rationale

- **BR-01 outranks shipping pressure:** a distro whose partitioning path cannot
  yet prove "Windows survives" end-to-end does not ship, full stop.
- **Risk-driven sequencing:** M15 reduces R-05 for three distros at once;
  another Ubuntu iteration reduces one distro's remaining tail risk.
- The catalog's `status` mechanism makes parking (and later unparking) a
  one-line, no-code decision — this is exactly what it was built for.

## Consequences

- **Positive:** alpha/beta messaging stays honest (validated means validated);
  the most popular distro being absent is explained by a public engineering
  dossier, which itself builds credibility.
- **Negative:** "Ubuntu" is the first name many users look for; its absence
  costs first impressions. Mint (validated, Ubuntu-based) is the offered
  alternative.
- **Resumption trigger:** first real-hardware attempt during M15 spare cycles,
  or an upstream subiquity release that skips table writes for all-preserved
  configs — whichever comes first. Estimated resumption cost: an afternoon,
  thanks to the dossier.
