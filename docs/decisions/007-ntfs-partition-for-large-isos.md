# ADR-007: Oversized ISOs get a dedicated NTFS partition

**Status:** Accepted
**Date:** 2026-07-10

## Context

The no-USB pipeline stages everything on a FAT32 partition, because FAT is the
only filesystem UEFI firmware is guaranteed to boot from. Distros whose
installers consume the whole ISO (iso-scan / casper) need the ISO staged too.
Ubuntu 26.04's desktop ISO is ~6 GB — and **FAT32 cannot hold a file ≥ 4 GiB**
(hard format limit; Windows reports it as `ERROR_DISK_FULL`, which cost a
debugging round). Mint (~2.9 GB) and Debian's hd-media artifacts fit; Ubuntu was
the first to cross the wall, and ISOs only grow.

## Decision

Split the roles when (and only when) the ISO is ≥ 4 GiB: the FAT32 partition
keeps everything boot-related (kernel, initrd, GRUB, seed config, agent,
manifest); the full ISO goes on a second, NTFS-formatted partition labelled
`IGLOOISO`. casper/iso-scan locate the ISO on any mountable partition, so NTFS
is fine *after* boot — the firmware only ever touches the FAT32 one. The
threshold check is automatic per install (`fullIsoBytes >= 4 GiB` in
`DirectInstallService`); distros under the limit keep the single-partition
layout unchanged.

## Alternatives considered

- **exFAT for the staging partition:** solves file size but firmware can't boot
  it either, so the split would still exist — and installer initrds are less
  universally able to mount exFAT than NTFS.
- **Split the ISO into <4 GiB chunks:** no installer consumes chunked ISOs.
- **Netinstall-only ISOs:** avoids the wall but sacrifices the offline richness
  of desktop ISOs; kept as a fallback avenue (see `distros/ubuntu/STATUS.md`).

## Consequences

- **Positive:** ISO size can never break the pipeline again — the design is
  size-proof, not Ubuntu-specific.
- **Negative:** one more partition to create, declare (preserve!) and clean up.
- **Note:** future distros crossing 4 GiB inherit this automatically; nothing to
  configure beyond `CopyFullIsoToVolume`.
