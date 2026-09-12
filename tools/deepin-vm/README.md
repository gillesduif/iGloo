# Deepin live-environment probe

This investigation tool boots the verified Deepin 25.2.0 ISO in QEMU/KVM with
**no writable disk and no network**. It starts a console directly, never the
stock installer. It records shipped package versions, deployment help, mounts,
block devices and the result of iGloo's read-only target collector.

Linux dependencies: Python 3.11+, QEMU x86, xorriso, and access to `/dev/kvm`.
The VM uses 4 GiB RAM and direct file I/O to avoid retaining a second ISO-sized
host cache. Supply a new output directory on a filesystem supporting direct I/O:

```sh
python3 tools/deepin-vm/probe.py \
  --iso /absolute/path/deepin-desktop-community-25.2.0-amd64.iso \
  --output /absolute/path/new-probe-directory
```

The complete ISO SHA256 and size are checked before boot. The output retains
`console.log`, `probe.log`, extraction diagnostics, and the extracted kernel and
initrd. The QEMU child is stopped on completion or failure. Nothing attaches a
host physical disk. Guest uploads go only into the live system's `/run` tmpfs.

This direct-kernel probe does **not** validate UEFI, GRUB, Windows preservation,
target creation, installation, OOBE or migration. A successful boot is not a
successful install. `IGLOO_COLLECTOR_EXIT` records whether live collection passed;
the probe intentionally retains failures as evidence rather than hiding them.

## Disposable immutable-layer experiment

```sh
python3 tools/deepin-vm/immutable_probe.py \
  --iso /absolute/path/deepin-desktop-community-25.2.0-amd64.iso \
  --output /absolute/path/new-immutable-experiment
```

This separate experiment creates a **new 192 GiB sparse regular file** containing
synthetic GPT entries, including a 64 GiB root. It never accepts an existing
target image. Only that newly created image is attached writable to the VM.
The guest resolves the synthetic claim and refuses an existing root signature,
then formats that fixture root and tests Deepin's repository extraction,
checkouts and immutable mount helper. The ESP is not mounted or formatted.

The image and logs are retained for inspection, including on failure. Actual
space use grows during extraction. The synthetic receipt tests Linux resolution;
it is not evidence of Windows-side authorization capture. Synthetic Windows-type
entries contain no Windows installation. This is **not an end-to-end installer**
and is not packaged as a Deepin installation resource.

The opt-in `--deploy` flag additionally experiments with target `/var` setup,
kernel/initrd staging, first-boot service selection, and the shipped immutable
deployment command inside the target. It still does not install an EFI loader or
mount the ESP. A successful extraction/mount result does not establish that this
additional phase succeeds; inspect `experiment.log` and `preservation.json`.

On WSL with limited host commit space, run the experiment in a temporary systemd
scope so ISO verification and QEMU share a bounded memory budget:

```sh
sudo systemd-run --scope -p MemoryMax=5G -p MemorySwapMax=1G \
  python3 tools/deepin-vm/immutable_probe.py \
  --iso /absolute/linux/filesystem/path/deepin-desktop-community-25.2.0-amd64.iso \
  --output /absolute/path/new-immutable-experiment
```

The scope disappears when the command exits; it does not change global WSL
settings. Place the ISO and output on the Linux filesystem for this experiment.
