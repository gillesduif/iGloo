# Adding a distribution to Igloo

This directory holds every Linux distribution Igloo can install. Each distro is a self-contained folder. To add a new distro, you copy `_template/`, fill it in, and open a pull request. **No changes to Igloo's core code are required.** That's the point of the architecture.

## Folder layout

```
distros/your-distro-id/
├── distro.json                              # Required. Metadata.
├── Igloo.Distro.YourDistro.csproj           # Required. Plugin assembly.
├── YourDistroPlugin.cs                      # Required. Implements IDistroPlugin.
├── installer/                               # Required. Installer driver config template.
│   └── (kickstart / preseed / Calamares config / etc.)
├── agent/                                   # Required. First-boot agent.
│   ├── first-boot.sh
│   └── agent.py
├── logo/                                    # Required. Distro logo + attribution.
│   ├── your-distro-logo.png
│   └── NOTICE
├── screenshots/                             # Required. PNG screenshots shown in the catalog.
│   └── *.png
└── README.md                                # Required. Distro-specific notes.
```

## The six pieces

### 1. `distro.json`

Metadata. Identifier, display name, description, logo, ISO download URL, SHA256, hardware tags, screenshots. Validated against `_schema/distro.schema.json` in CI. Required fields: `id`, `displayName`, `description`, `logo`, `iso`.

The optional `status` field controls availability:

- `"available"` (the default) — the distro has a working `IDistroPlugin` in this directory and can actually be installed. Everything in "The six pieces" below is required.
- `"coming-soon"` — a catalog-only entry: `distro.json` + `logo/` are enough; the plugin, installer config and agent may be omitted. The distro appears in the picker with a "coming soon" badge but cannot be selected for install. Use this to stake out an entry while the plugin is being written. **Never mark a distro `available` without a plugin that passes the install matrix.**

The `id` field **must** match the folder name. The folder `distros/fedora-kde/` requires `"id": "fedora-kde"`.

### 2. The plugin class (`YourDistroPlugin.cs`)

A single C# class implementing `IDistroPlugin` from `Igloo.Core.Abstractions`. Three methods:

- `CheckCompatibility(PreflightReport)` — return distro-specific findings. If the user's machine has an NVIDIA GPU and your distro doesn't ship NVIDIA drivers by default, surface an Info finding here. If something about the machine makes your distro literally not installable, return a Blocker.
- `RenderInstallerConfigAsync(MigrationManifest)` — produce the installer driver config (kickstart / preseed / Calamares JSON / etc.) ready to write to OEMDRV. You own this entirely; Igloo just takes the bytes.
- `GetAgentPayloadAsync()` — return the first-boot agent files (typically a `first-boot.sh` plus `agent.py`).

See `fedora-kde/FedoraKdePlugin.cs` for the reference implementation.

### 3. The installer driver config (`installer/`)

The unattended-install config for your distro's installer:

- **Anaconda** (Fedora, RHEL, AlmaLinux, Rocky, CentOS Stream): kickstart file. Documented at [pykickstart](https://pykickstart.readthedocs.io/).
- **Ubiquity** (Ubuntu, Mint, Pop!_OS, Zorin, elementary): preseed file. Documented in the [Debian installation guide, appendix B](https://www.debian.org/releases/stable/amd64/apb).
- **Calamares** (openSUSE, EndeavourOS, KaOS, Manjaro, Mauna, ...): Calamares JSON config. Documented at [calamares.io](https://calamares.io/).
- **AutoYaST** (openSUSE Leap, SUSE Linux Enterprise): AutoYaST XML.
- **Subiquity** (Ubuntu Server, newer Ubuntu): cloud-init `autoinstall` YAML.

Use placeholders like `{{LOCALE}}`, `{{LINUX_USERNAME}}`, etc. — the same ones used by `fedora-kde/kickstart/ks.cfg.template`. The plugin's `RenderInstallerConfigAsync` method does the substitution.

### 4. The first-boot agent (`agent/`)

Runs once on the freshly-installed system. Reads `/var/lib/igloo/manifest.json`, applies it. Convention is `first-boot.sh` (the systemd unit's entry point) calling `agent.py` (the migration logic). Python because it's already on every modern Linux install and JSON parsing in bash is misery.

The agent is responsible for:
- Migrating user files from the staging path declared in the manifest
- Importing browser profiles
- Enabling distro-specific extra repos (RPM Fusion for Fedora, Multiverse for Ubuntu, Packman for openSUSE, etc.)
- Installing codec packages if `hardware.needsNonFreeCodecs` is true
- Installing GPU drivers if `hardware.gpuVendor` requires it on your distro
- Installing Flatpak/native packages marked `autoInstall`
- Dropping a welcome desktop entry

The agent is idempotent and must not fatally block boot. If something fails, log it and continue — the welcome app surfaces failures to the user later.

### 5. The logo (`logo/`)

The `logo` field in `distro.json` points to a PNG inside your distro's directory
(convention: `logo/your-distro-logo.png`). It is the distro's cover in Igloo's
3D picker, so quality matters:

- **PNG only.** Igloo is a WPF app and does not render SVG at runtime — rasterize
  your distro's official vector logo yourself. **At least 1024px** on the longest
  edge: the image is texture-mapped onto a 3D plane and viewed at an angle, so
  lower resolutions visibly blur.
- **Transparent background preferred.** Igloo composes the logo onto its own
  dark cover tile; a baked-in background ruins the look.
- **Attribution is mandatory.** Ship a `logo/NOTICE` file with: the source URL
  the asset was obtained from, and a trademark line naming the trademark holder
  (e.g. "Fedora and the Fedora logo are trademarks of Red Hat, Inc."). Use the
  distro's official logo unmodified, per that project's trademark guidelines.
  See `fedora-kde/logo/NOTICE` for the reference example.

If a manifest has no usable logo, Igloo falls back to a generated placeholder
tile (the distro's initial on a colored background) — functional, but not what
you want representing your distro.

### 6. Screenshots

At minimum: one screenshot of the default desktop after install. Ideally also: the app launcher / start menu, the file manager, and one piece of distro-character (a settings panel, software centre, theme picker — whatever's distinctive).

PNG, 1920×1080 or 1280×800, under 500 KB each. License the screenshots permissively (CC-BY or CC0) so we can show them in marketing.

## Quality bar for accepted distros

- **The ISO must be officially published** by the distro project. We do not accept third-party respins.
- **The signing key for the ISO must be verifiable** against a published source (the distro's website, a Linux foundation key, etc.).
- **The distro must be actively maintained**, with releases in the past 12 months and a way to report security issues.
- **The plugin must successfully install in a clean VM** end-to-end, with at least: UEFI + Secure Boot off, UEFI + Secure Boot on (if your distro supports it), Legacy BIOS. CI runs these tests.
- **A real human must commit to maintaining the plugin.** If you contribute a distro, you're agreeing to respond to user-reported issues against it. If the maintainer goes silent for 6 months, the distro is removed from the catalog until someone else picks it up.

## License

Distro plugin code must be GPL-3.0-or-later (matching Igloo's license).
Screenshots may be CC-BY or CC0.
`distro.json` data is project metadata, no copyright.

## I just want to suggest a distro without writing code

Open an issue with the `distro-request` label. Describe the distro, link to its ISO and signing key, explain who it's for. Someone (possibly you, possibly someone else) will pick it up.
