# Owned installation flow

Integration review, 2026-09-12. Deployment baseline: `509be6b`.

## Existing application sequence

`IglooApp.OnStartup` loads JSON through `DistroLoader`, then plugin assemblies
through `DistroRegistry`. Distro selection combines catalog status with plugin
compatibility. `MainWindowViewModel` captures the selection, downloads/verifies
the ISO, collects migration preferences, and asks `DiskSelectionViewModel` for
a disk and allocation size. That view currently offers no exact free extent.

`FileStagingViewModel` copies migration data, generates/serializes the manifest,
then immediately renders installer configuration and packages the agent.
Strong-target consumers are explicitly refused at this point.

For dual boot, `DirectInstallViewModel` asks for the boot spec and invokes
`DirectInstallService.PrepareAsync`; it also refuses strong-target consumers.
The service measures boot assets, reuses/deletes label-matched installer volumes
or shrinks a heuristically selected lettered partition, creates FAT boot storage
and optional NTFS ISO storage, optionally precreates/reuses a Linux partition,
copies kernel/initrd/EFI files, injects config, copies the ISO and staged data,
and writes GRUB configuration. Missing injected config and required extra ISO
files currently produce warnings. Root/storage substitution happens here.

On Restart, firmware registration writes Boot#### and BootNext, may substitute
another entry targeting the same partition, updates BCD and permanent BootOrder,
and then starts Windows shutdown. The replace-disk branch uses UsbWriter instead.

Fedora consumes its staged Anaconda image and kickstart; Debian uses hd-media
and preseed; Mint uses its live ISO and preseed; Ubuntu uses its live ISO and
autoinstall. Their existing allocation/config/firmware behavior is not migrated
by this phase. Their root/gap and ESP heuristics remain separate safety findings.

## Required integration boundaries

An owned-target consumer needs allocation authorization before config rendering.
The selected snapshot, exact free extent (or explicitly identified shrink source),
and explicit ESP must survive navigation unchanged. Preparation revalidates that
snapshot and captures the actual resulting root GUID using the existing preparer.
Only then may configuration be rendered from the serialized, re-read claim.

Staging must bind the ISO container partition separately from root and ESP. If
new staging partitions are necessary, create them before the final root claim;
never invalidate the claim's full-layout snapshot afterward. Existing decrypted
Windows NTFS storage is a possible ISO container, subject to an exact partition
identity, sufficient space and verified native live-boot support. Suspended
BitLocker protection does not make encrypted NTFS readable by GRUB/live-boot.

Use mandatory initrd-delivered bootstrap/config, with independently bound run
identity and hashes. Missing data must terminate before deployment. The bootstrap
uses the existing read-only GPT collector/resolver and preserves the validated
deployment sequence. ISO-file boot still needs a runtime handoff test; source
support for `fromiso`/`findiso` is not that test.

Firmware registration for this path must target the exact authorized partition
and unique loader path, verify its writes, and use BootNext without altering
permanent BootOrder or adopting another entry. Failure must leave Windows's
normal boot path intact. Do not route this through the legacy firmware fallback.

The normal catalog remains coming-soon. An explicit experimental entry point
must not remove the legacy guards until its complete replacement path exists.

## Integration progress (2026-09-13)

`InstallationTargetAllocation` offers every suitable aligned free extent and
authorizes only an explicitly selected extent and ESP. `OwnedInstallerPreparation`
orders creation, durable manifest re-read, receipt verification and fresh layout
verification before config rendering. It rejects unsupported target requirements
and unsupported boot capability before calling partition preparation.

The disk page now uses `OwnedTargetSelectionViewModel` for plugins requiring a
claim. It selects neither disk, extent nor ESP automatically. Inspection refreshes
the layout by GPT GUID; changing disks invalidates the snapshot and late async
results cannot restore it. Existing free space is supported by this selector;
explicit shrinking is still unimplemented. This selector is connected to the
page, but claim creation and boot staging are not yet connected to navigation.
The legacy staging guard and Deepin's plugin blockers remain in effect.

Reinspection of the pinned ISO's extracted initrd found additional constraints
in `usr/lib/live/boot/9990-misc-helpers.sh`:

- `fromiso` lines 130–148 mount the container without explicit `ro`.
- `findiso` lines 251–255 mount the container and ISO with `ro,noatime`.
- `find_livefs` lines 323–338 tries `LIVE_MEDIA`, then scans other devices if
  that attempt fails. `live-media=` alone therefore does not prohibit fallback.

These are source observations from the shipped initrd, not ISO-file boot test
results. A future bootstrap must enforce exact staged-medium identity and fatal
failure independently; none of these arguments alone proves media ownership.
