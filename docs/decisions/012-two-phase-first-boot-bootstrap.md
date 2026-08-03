# ADR-012: Two-phase first-boot bootstrap - never do real work in installer hooks

**Status:** Accepted
**Date:** 2026-07-02

## Context

Installer late-hooks (Ubiquity `success_command`, curtin `late-commands`, d-i
`late_command`) looked like the natural place to install iGloo's first-boot
agent into `/target`. In practice those hooks run in a hostile environment:
busybox tooling without `LABEL=` resolution, no udev symlinks, chroots without
`/proc`, and - decisively on Mint - a live kernel whose module tree does not
match `/target`, making `mount -t vfat` of the seed partition **impossible** at
install time. Multiple validation runs failed invisibly there.

## Decision

Installer hooks perform **no environment-sensitive work**. They only *write*:
a self-contained `igloo-bootstrap.sh` plus a systemd oneshot unit
(`Before=display-manager.service`, guarded by `ConditionPathExists=!.done`)
into `/target`, using nothing but `echo`, `mkdir`, `chmod`, `ln`. On the
**first boot of the installed system** - running its own kernel, with udev,
vfat, and full tooling - the bootstrap mounts the seed partition, copies the
agent and manifest, and hands over to the agent before any login screen exists.

## Rationale

- The installed system is the only environment guaranteed to be self-consistent.
- `echo`/`ln` cannot fail for environmental reasons; every failure mode moves to
  a place where it is diagnosable (full OS, persistent logs).
- One pattern serves every distro family; validated end-to-end on Linux Mint.

## Consequences

- **Positive:** eliminated the entire class of "agent never installed" failures;
  both phases log to disk (`igloo-install.log`, `igloo/bootstrap.log`).
- **Negative:** first boot takes visibly longer (agent runs before the greeter);
  mitigated by messaging, and by the fact that a *working* delay beats a silent failure.
- **Rule of thumb for future distros:** if a hook needs `mount`, it is in the wrong phase.
