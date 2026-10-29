name: f-001-bazzite-dx
title: implement support for bazzite DX
domain: distros directory, if you touch anything else trace in important decisions
description: Implement support for bazzite DX in distros so we can migrate a windows machine to bazzite DX easily
status: done
created: 29-10-2026
author: naz


# Spec

Add an installable `bazzite-dx` distro. The Windows app must be able to migrate a machine to Bazzite DX unattended: preflight, installer config, first-boot agent, tests. It should match what Fedora KDE does today, adapted to an image-based (bootc) system.

# Review

After you apply the changes:

- Ensure every aspect of the distro's installation is covered
- Ensure everything is tested

## Documentation

### Important choices

- **New `distros/bazzite-dx/` entry, separate from `distros/bazzite/`.** Bazzite DX is its own image (`ghcr.io/ublue-os/bazzite-dx`). The plain Bazzite entry stays `coming-soon`. The plugin reads the image references from `distro.json` (`containerImage`, `containerImageNvidia`), so a plain Bazzite plugin could reuse it with different references.
- **Installed from the official Fedora Everything netinstall, not a Bazzite ISO.** Bazzite's ISO is a live session that starts Anaconda interactively and cannot take a kickstart. The Fedora netinstall reuses the boot pipeline that Fedora KDE already uses (OEMDRV, local stage-2). The kickstart uses `ostreecontainer --transport=registry` instead of `%packages`, so the install needs a network connection.
- **Signed updates.** The image is pulled with `--no-signature-verification`, because the installer environment has no trust policy for Universal Blue images. A `%post` then runs `bootc switch --mutate-in-place --enforce-container-sigpolicy`, as Bazzite's own installer does. The first-boot agent checks this took effect and repeats the switch if not.
- **GPU → image.** NVIDIA machines get `bazzite-dx-nvidia`, which ships NVIDIA's open kernel module (GTX 16 / RTX 20 and newer); everything else gets `bazzite-dx`. No driver is installed at first boot.
- **Plain XFS partitions** (`autopart --type=plain --fstype=xfs`). Bazzite's installer uses btrfs, but autopart only offers btrfs as a named volume. A named volume can collide with an earlier install and stop the unattended install, which is the same trade-off Fedora KDE made against LVM. `ksvalidator` rejects plain btrfs.
- **Secure Boot.** When Windows booted with Secure Boot on, the kickstart queues the Universal Blue key with `mokutil` (password `universalblue`, Bazzite's documented value). The user confirms it once in MokManager at the first reboot, and a preflight Warning explains this beforehand. With Secure Boot off nothing is queued, so first boot stays unattended. Legacy BIOS is a preflight Blocker.
- **No passwordless autologin.** Bazzite DX is built on the "deck" images. Their `bazzite-autologin.service` logs the first user into Steam Gaming Mode without a password. A machine migrated from a password-protected Windows account keeps asking for the password, so the kickstart masks that unit and preselects Plasma at the SDDM login screen.
- **Bazzite's default apps are installed at first boot.** Bazzite's ISO copies its default Flatpaks (Firefox, Okular, Gwenview, ...) from the live session. A netinstall has no such store, so without this step the system would have no browser. The agent installs the same KDE list from Flathub, Firefox first, before the browser profiles are migrated.
- **SELinux relabel.** A chroot `%post` runs `restorecon` on what the nochroot `%post` wrote (home, agent, units, SDDM state), as Bazzite's installer does for `/etc/selinux`.
- **GRUB is only regenerated in dual-boot mode.** In replace mode the bootupd-managed grub.cfg is left untouched.
- **Developer groups are added directly** with `usermod`, after copying the group line from `/usr/lib/group` into `/etc/group`. Bazzite DX's own `bazzite-dx-groups` runs once per image version and only for the first wheel member.
- **The agent reuses the Fedora KDE agent.** It is shipped as `fedora_agent.py` (csproj link, with a source-tree fallback because the app loads the DLL from `distros/bazzite-dx/`). Its data-migration steps run unchanged. Bazzite replaces package installs (Flatpak only), GPU drivers and the kernel-modules repair (the image covers them), the GRUB menu rework (the image owns grub.cfg; the agent enables os-prober and regenerates grub.cfg as `ujust regenerate-grub` does), and adds the developer groups and the signed-image check.
- **Hostname capped at 19 characters.** `bazzite-hardware-setup` renames the host to `bazzite` when `hostname | wc -m` exceeds 20, because long names break distrobox. `wc -m` counts the newline.
- **First-boot unit ordered `After=bazzite-hardware-setup.service`.** That service can reboot the first boot to apply kernel arguments, which would cut the agent off mid-run.
- **Escaping.** The Windows username, the disk model and the full name are escaped before going into the root `%post` script and `--gecos`. Windows usernames may contain `$` and backticks. The Fedora KDE template has the same exposure.
- **Outside `distros/`:**
  - `src/Igloo.App/ViewModels/DistroRecommender.cs` gets a `bazzite-dx` traits row, so the quiz can recommend it (gaming 2, work 2, tinker 2).
  - `Igloo.sln` gets the plugin and its test project.
  - `tests/Igloo.Distro.BazziteDx.Tests/` and `tests/agent/bazzite_agent_test.py` are new.
  - `distros/fedora-kde/agent/agent.py` gets a `ROOT_MOUNT` module constant (default `/`, so Fedora is unchanged). Its seed cleanup resolves the root disk from `/sysroot` on Bazzite, because `/` is a composefs overlay there.

### Guidance from the user

- No commits for now.

### Leftovers

- **Install matrix not run.** Bare-metal or VM installs with UEFI + Secure Boot off and UEFI + Secure Boot on have not been run. The README's quality bar requires them before release. Points to watch on the first real install:
  - whether the Fedora netinstall Anaconda accepts `ostreecontainer` from a kickstart on OEMDRV;
  - whether `bootc switch` works in the chroot `%post`;
  - whether `mokutil` is reachable from `%post`;
  - whether `grub2-mkconfig` on the bootupd-managed grub.cfg keeps the image's boot entries.
- **Unsigned pull window.** During the install the image is pulled over TLS with `--no-signature-verification`; signatures are enforced from the first update on. Hardening option: write a `policy.json` plus a sigstore `registries.d` entry with Universal Blue's `cosign.pub` into the installer environment in `%pre`, then drop the flag. Not done, because it is untested whether the netinstall's `ostreecontainer` honours a containers policy.
- **UEFI boot entry.** It is not confirmed that the Fedora netinstall's bootupd path creates a firmware boot entry. If it does not, the machine reboots into Windows after the install. Check on the first real install.
- **`EFI/fedora` is not wiped before install**, unlike Bazzite's own installer, on purpose: in dual-boot it could belong to another Fedora install.
- **No screenshots** in `distros/bazzite-dx/screenshots/` (the README marks them required; Fedora KDE references some it does not ship either).
- **GNOME variants** (`bazzite-dx-gnome`, `bazzite-dx-nvidia-gnome`) are not offered. The display-layout applier the agent uses is the KDE one.
- **Pre-Turing NVIDIA cards** (GTX 10-series and older) get the NVIDIA image, whose open module does not drive them. Preflight only informs. Picking the non-NVIDIA image from `GpuDeviceId` would fix it.
- **Sibling Fedora install.** Bazzite's bootloader also installs to `EFI/fedora` on the ESP. In dual-boot next to an existing Fedora install, the two may fight over that directory. Not handled.
- **Fedora KDE escaping.** `fedora-kde/kickstart/ks.cfg.template` still pastes `WINDOWS_USERNAME` and `TARGET_DISK_MODEL` into bash unescaped. That is out of this feature's domain and is left for a separate fix.
- **Unrelated test failures on macOS/Linux.** `tests/agent/grub_root_uuid_test.py` fails on macOS (5 checks), and `DistroManifestTests.Logo_path_combines_source_directory_and_relative_logo` fails on Linux, because it uses a Windows path. Both fail on a clean checkout too; CI runs on Windows.
- The WPF app and its tests were not built here (macOS, Windows-only target); CI will cover them.

# Automated validation

- Validate the implementation by running the tests
