# Repository structure

```
iGloo/
├── src/
│   ├── Igloo.App/             # WPF application (wizard UI, dependency injection)
│   ├── Igloo.Core/            # Plugin abstractions (IDistroPlugin, InstallerBootSpec),
│   │                          #   manifest models, service contracts
│   ├── Igloo.Preflight/       # Hardware detection (WMI), DirectInstallService
│   │                          #   (partitioning, kernel/initrd staging, UEFI
│   │                          #   registration), LinuxRemovalService
│   ├── Igloo.Iso/             # Resumable download, SHA-256 and GPG verification
│   ├── Igloo.Migration/       # User file staging to the staging volume
│   └── Igloo.UsbWriter/       # USB fallback: raw ISO write + staging partition
├── distros/
│   ├── _schema/               # distro.json JSON Schema (validated in CI)
│   ├── _template/             # Template for new distribution plugins
│   ├── _debian-family/        # Shared first-boot agent for Debian/Mint/Ubuntu
│   ├── fedora-kde/            # Anaconda / kickstart (reference implementation)
│   ├── debian/                # debian-installer + live-installer / preseed
│   ├── linuxmint-cinnamon/    # Ubiquity / preseed (casper live ISO)
│   ├── ubuntu/                # subiquity / autoinstall (in development)
│   └── .../                   # 15 "coming soon" catalog entries
├── tests/                     # Six xUnit test projects
├── docs/
│   ├── architecture.md        # System architecture
│   ├── decisions/             # Architecture Decision Records
│   ├── guide/                 # Visual step-by-step guide (in progress)
│   ├── reference/             # Reference docs (operation, safety model, building)
│   └── whitepaper/            # Technical white paper (draft)
└── .github/workflows/         # CI
```
