# Bazzite DX

[Bazzite DX](https://github.com/ublue-os/bazzite-dx) is Bazzite (Universal Blue's gaming
image) with developer tooling added: VS Code, Docker and Podman, distrobox, libvirt and
Homebrew. Like every Bazzite image it is image-based: `/usr` is read-only and an update
replaces the whole system image.

## How iGloo installs it

Bazzite publishes no ISO that installs from a kickstart. Its ISO is a live session that
runs Anaconda interactively. So iGloo boots the **official Fedora Everything netinstall**,
the same one Fedora KDE uses, and the kickstart replaces the package install with:

```
ostreecontainer --url=ghcr.io/ublue-os/bazzite-dx:stable --transport=registry --no-signature-verification
```

Anaconda pulls the image from the registry and deploys it. The machine needs a network
connection during the install: the `%pre` script joins a Wi-Fi network saved on Windows,
and a wired link works without any setup.

| Machine | Image |
|---|---|
| AMD / Intel GPU | `ghcr.io/ublue-os/bazzite-dx:stable` |
| NVIDIA GPU | `ghcr.io/ublue-os/bazzite-dx-nvidia:stable` (NVIDIA open kernel driver, GTX 16 / RTX 20 and newer) |

Both references live in `distro.json` (`containerImage`, `containerImageNvidia`).

### Kickstart (`kickstart/ks.cfg.template`)

- Same disk detection, dual-boot rules and Wi-Fi loop as Fedora KDE.
- `autopart --type=plain --fstype=xfs --nohome`: plain partitions avoid volume name
  collisions with an earlier install. Bazzite's own installer uses btrfs, but autopart
  only offers btrfs as a named volume, so the root is XFS.
- `%post --nochroot` copies the agent, the manifest and the user's files. Only `/etc` and
  `/var` persist on an image-based system, so the files go to `/var/home/<user>` and the
  agent to `/var/opt/igloo` (`/opt` links there).
- It turns off passwordless autologin. Bazzite DX is built on Bazzite's "deck" images,
  whose `bazzite-autologin.service` logs the first user straight into Steam Gaming Mode
  without a password. The kickstart masks that unit and preselects Plasma at the SDDM
  login screen. Gaming Mode stays in the greeter's session list.
- A chroot `%post` runs `restorecon` on everything the nochroot `%post` wrote. Those files
  got the installer environment's SELinux labels, not the image's.
- A chroot `%post` runs `bootc switch --mutate-in-place --enforce-container-sigpolicy`,
  as Bazzite's own installer does. Every later update is then checked against the
  Universal Blue cosign signature.
- With Secure Boot on, a `%post` queues the Universal Blue key for MokManager. At the
  first reboot the user confirms it once (password `universalblue`). The preflight check
  explains this before the install starts.

### First-boot agent (`agent/`)

`agent.py` imports the Fedora KDE agent (shipped next to it as `fedora_agent.py`) and runs
its migration steps unchanged: Wi-Fi, display layout, wallpaper, account picture, browser
profiles, Chromium credential import, log export and seed cleanup. Bazzite-specific steps:

| Step | What it does |
|---|---|
| `image` | Checks the deployment follows the expected image and that updates are signature-checked. Repeats the `bootc switch` if the kickstart's did not land. |
| `secure-boot` | Logs the Secure Boot state. |
| `dx-groups` | Puts the user in `docker`, `incus-admin` and `libvirt` with `usermod`. Bazzite DX's own unit runs once per image version and only for the first wheel member. |
| `boot-menu` | Keeps this system first in the UEFI boot order. In dual-boot mode it also turns os-prober on, regenerates grub.cfg (what `ujust regenerate-grub` does) so Windows appears, and keeps the menu visible (`menu_auto_hide=1`). In replace mode grub.cfg is left as bootupd wrote it. |
| `default-apps` | Installs the Flatpaks Bazzite's own ISO would have installed: Firefox first, then Okular, Gwenview, Haruna, Flatseal, Warehouse, ProtonPlus and the rest. They come from the live ISO's Flatpak store, which a netinstall does not have. |
| `suggested-pkgs` | Installs the Flatpaks only. Native packages are logged, not layered onto the image. |

The steps it leaves out: no RPM Fusion, codecs or NVIDIA driver (the image has them), no
kernel-modules repair (the kernel comes with the image), no GRUB theme (the image owns
grub.cfg).

The unit runs after `bazzite-hardware-setup.service`, which can reboot the first boot to
apply kernel arguments.

## Testing

- Plugin: `tests/Igloo.Distro.BazziteDx.Tests` (`dotnet test`).
- Agent: `python3 tests/agent/bazzite_agent_test.py`.
- Install matrix (UEFI + Secure Boot off, UEFI + Secure Boot on): not run yet. Bazzite has
  no legacy BIOS install path.
