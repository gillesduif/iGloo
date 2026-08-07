# Adding a distribution

The complete guide is in [`distros/README.md`](../../distros/README.md). The
procedure:

1. Copy `distros/_template/` to `distros/<distribution-id>/`.
2. Complete `distro.json`: display name, ISO URL, checksum and signature URLs,
   GPG key file with full fingerprint, hardware tags, screenshots.
3. Implement `IDistroPlugin` and declare an `InstallerBootSpec` (kernel
   command line, artifact paths, configuration delivery). The four existing
   plugins provide reference implementations for Anaconda, debian-installer,
   Ubiquity and subiquity.
4. Provide an installer configuration template (kickstart, preseed or
   autoinstall).
5. Provide or reuse a first-boot agent that applies the migration manifest.
6. Validate an unattended end-to-end installation in a VM and open a pull
   request.
